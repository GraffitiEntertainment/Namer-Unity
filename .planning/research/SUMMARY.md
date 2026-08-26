# Project Research Summary

**Project:** NAMER Unity plugin
**Domain:** Unity editor material-processing / texture-packing / stylization plugin (UPM package)
**Researched:** 2026-08-25
**Confidence:** HIGH (stack + architecture) / MEDIUM (features + some pitfall specifics)

## Executive Summary

NAMER is a Unity Editor tool (shipped as a UPM package) that converts a standard PBR material into a compact, bit-packed representation — two textures plus vertex colors — with an optional reference-image stylization pass. It never touches source assets, never leaves Unity, and never requires a runtime decompression stage. The differentiating math is an error-driven vertex-color decomposition (barycentric least-squares fit per triangle plus a residual texture) and a fixed packed surface format (octahedral normal in RG, AO in B, 6-bit roughness + metallic + emissive bits in A).

Experts build this kind of tool as a **layered, one-way data-flow editor pipeline**, not a monolithic importer. Source assets are read-only; intermediate data lives in GPU `RenderTexture`s; output is written only to a generated-assets directory. CPU work (asset inspection, mesh math, serialization) is cleanly separated from GPU work (all per-pixel texture ops), because per-pixel C# loops on 2K/4K textures are explicitly forbidden by the project constraints. The single most important architectural decision is to define the packed format once in a pure-C# `Core` assembly (no `UnityEngine.Object` dependencies) and mirror it in HLSL, so it is independently unit-testable headless and verified against the GPU via round-trip tests.

The key risk is **color-space and data-format correctness**, not algorithmic difficulty. The packed surface texture is *data, not color* — octahedral normals, AO, and packed bits must be linear and never sRGB-encoded, never default-compressed (BC/ETC/ASTC corrupt the bits), and never sampled through the sRGB sampler path. The second risk cluster is **GPU resource discipline**: synchronous `ReadPixels` stalls the editor, per-run `RenderTexture` allocation leaks, and Metal/DX11/Vulkan compute limits differ. Both are mitigated by explicit contracts established in Phase 1 and a single resource-owner plus `AsyncGPUReadback` from Phase 2 onward. The correct sequencing is: lock the format contract first, de-risk "compute in the editor" second, ship a minimal vertical slice third, then invest in the hard algorithmic work (vertex-color fitting, stylization).

## Key Findings

### Recommended Stack

Unity 6 LTS (6000.0/6000.3) with URP 17.x, hand-written HLSL compute shaders, and C# 9 (.NET Standard 2.1). Everything is Unity-native — no native C++ plugins, no MathNet, no cloud. Full details in `STACK.md`.

**Core technologies:**
- **Unity Editor 6000.0+ LTS** (declare `"unity": "6000.0"`) — current LTS generation; `6000.0.x` and `6000.3.x` are the two active streams.
- **URP 17.x** (`com.unity.render-pipelines.universal`) — the runtime renderer the NAMER decode shader targets; URP-first per project decision.
- **Compute Shaders (HLSL `.compute`)** — all per-pixel GPU work (normalize, octahedral encode, pack, residual, stylize); no external dependency.
- **Unity.Mathematics 1.3.2 + Burst 1.8.x + Collections 2.5.x** — SIMD/Burst-friendly CPU math for the vertex-color fit and palette extraction (the CPU hotspots). Mathematics has **no** SVD/eigen built in — hand-roll the 3×3 solver.
- **`GraphicsFormat` (not legacy `RenderTextureFormat`)** — declare precise pixel formats; compute into linear formats (`R8G8B8A8_UNorm`, `R16G16B16A16_SFloat`), never sRGB as a write target.
- **`AsyncGPUReadback`** — non-blocking GPU→CPU readback for preview and the vertex-color fit; sync `ReadPixels` only for tiny one-shot reads.
- **Unity Test Framework 1.4.x** — NUnit-based EditMode (GPU-in-editor round-trips, asset-safety) and PlayMode (runtime decode) tests, gated by asmdefs.

### Expected Features

Full analysis in `FEATURES.md`.

**Must have (table stakes — v1 launch):**
- Non-destructive output / source immutability (generated assets under `NAMERGenerated/`) — trust prerequisite.
- Source material inspection + sensible defaults for missing maps — nothing works without reading input.
- Octahedral normal encoding + surface texture packing (AO / metallic / emissive / 6-bit roughness).
- URP NAMER runtime shader (decode) — makes the output renderable at all.
- Cleaned base color texture — texture 1 of the 2-texture format.
- Before/after preview on a representative mesh + `Process with NAMER` command + editor window.

**Should have (competitive — v1.x, after the encode/decode/render loop is proven):**
- Vertex-color decomposition with barycentric least-squares fitting + residual texture — the core differentiator.
- Reconstruction-error metric + adaptive residual resolution — the actual memory win (measured, not fixed-ratio).
- Vertex splitting at seams/discontinuities + debug channel views.
- Batch processing over selections/folders.

**Defer (v2+):**
- Stylization (NAMERStyleProfile + palette + hue-aware mapping + edge-preserving smoothing) — biggest complexity chunk; defer if conversion value alone lands users.
- Dual AO model, normal detail reduction, AssetPostprocessor automation, HDRP support, Blender round-trip validation.

**Anti-features (explicitly out):** AI/semantic recognition, diffusion/cloud, auto-import-on-every-model, hard-coded art styles, runtime conversion, C++ plugins, destructive source editing, full material taxonomy, live per-pixel C#.

### Architecture Approach

A layered editor-time pipeline with **one-way data flow**: source assets read-only → GPU `RenderTexture` intermediates → output written only to `NAMERGenerated/`. Three assemblies split by compilation domain: `Core` (shared format math, pure C#, no `UnityEngine.Object`), `Runtime` (decode shader + material helper — the only code that ships), `Editor` (the entire pipeline). Each GPU stage is a stateless kernel-wrapper (resolve kernel → bind → dispatch → return output RT); each pipeline stage is a pure transform over an immutable `NAMERProcessContext`. Full details in `ARCHITECTURE.md`.

**Major components:**
1. **`Core` format library** (Octahedral, SurfacePacking, VertexColorSolver, ColorMath) — the single source of truth for the packed format; mirrored in HLSL and unit-tested headless.
2. **`NAMERProcessor` + stage classes** (SourceInspector → TextureNormalizer → OctahedralEncoder → SurfacePacker → VertexColorFitter → ResidualProcessor → Stylizer → AssetGenerator) — the sequencer owns progress + cancellation.
3. **`ComputeTexturePool`** — the single resource owner leasing/reusing `RenderTexture`s to prevent churn and leaks.
4. **`AssetGenerator`** — the *only* code that writes to `AssetDatabase`, exclusively under `NAMERGenerated/`.
5. **Runtime NAMER shader** — URP ShaderLab/HLSL decode of the packed format at render time.
6. **`NAMEREditorWindow` + `PreviewRenderer`** — UI Toolkit window with an `IMGUIContainer` `PreviewRenderUtility` viewport for before/after + debug channels.

### Critical Pitfalls

Top 5 from `PITFALLS.md` (11 total documented):

1. **sRGB vs linear mismatch in compute shaders** — compute does raw `Load()`, not sampler auto-conversion; packed data is not color. Avoid by an explicit color-space contract, in-kernel `LinearToSRGB`/`SRGBToLinear` helpers, and marking data textures linear at import/save.
2. **Blocking GPU readback stalls the editor** — `ReadPixels`/`GetPixels` on the main thread freezes at 2K/4K. Avoid with `AsyncGPUReadback` + batched `WaitAllRequests` + a `SystemInfo.supportsAsyncGPUReadback` guard.
3. **Metal vs DX11/Vulkan compute differences** — keep `[numthreads]` total ≤ 256 (≤1024), `groupshared` ≤ 16 KB, prefer `float4` RW targets; smoke-test every kernel on both backends.
4. **Precision loss (6-bit roughness / Color32 vertex colors)** — remap roughness perceptually (`sqrt`) before quantization, dither to hide banding, and define a gamma-vs-linear vertex-color contract up front.
5. **Texture compression corrupting packed data** — stamp linear + uncompressed import settings on the packed texture at `CreateAsset` time; never accept Unity's default compression/sRGB.

## Implications for Roadmap

Suggested phase structure (merges ARCHITECTURE build order with PITFALLS phase mapping and FEATURES v1/v1.x/v2 tiers):

### Phase 1: Core Format Contract + Runtime Decode
**Rationale:** Pure C#, no GPU, fastest feedback, and it locks the format every downstream stage and the runtime shader depend on. Establishes the read-only-source and color-space contracts before any GPU work.
**Delivers:** `Core` asmdef (octahedral encode/decode, surface bit-packing, LSQ solver, color math) + exhaustive CPU-reference unit tests; runtime URP NAMER decode shader with a play-mode round-trip test; source-immutability test.
**Addresses (features):** Octahedral normal encoding, surface packing spec, runtime shader — the foundation for all table stakes.
**Avoids (pitfalls):** #1 sRGB mismatch, #6 importer non-determinism, #9 untestable GPU code, #11 source mutation.

### Phase 2: GPU Texture Pipeline (Surface Packing)
**Rationale:** De-risks "compute in the editor" early and produces the packed surface + base color textures — the core conversion output.
**Delivers:** Compute dispatch harness + `ComputeTexturePool` (single resource owner); TextureNormalizer, OctahedralEncoder, SurfacePacker kernels with GPU golden tests against the Core reference; import-settings stamping helper; cross-platform compute smoke test.
**Uses (stack):** `ComputeShader.Dispatch` + `GraphicsFormat` linear targets + `AsyncGPUReadback`.
**Avoids (pitfalls):** #2 readback stall, #3 Metal/DX11, #7 compression corruption, #8 GPU leaks.

### Phase 3: Vertical Slice (Generate + Render + Preview)
**Rationale:** Turns the pipeline into a shippable, non-destructive MVP — the FEATURES v1 definition — before investing in hard algorithmic work.
**Delivers:** `AssetGenerator` writing textures/mesh/material/shader to `NAMERGenerated/`; `Tools > NAMER > Processor` editor window; before/after preview on a representative mesh.
**Addresses (features):** non-destructive generation, source inspection + defaults, editor window + command, preview — completing v1.
**Avoids (pitfalls):** #11 source mutation (hard-enforced write path), #1 (preview through the same sRGB path as save).

### Phase 4: Vertex-Color Decomposition + Residual
**Rationale:** The core differentiator and highest algorithmic risk; done only after the simple pipeline is proven so failures are isolated.
**Delivers:** `VertexColorFitter` (Burst-compiled per-triangle LSQ), `ResidualProcessor`, reconstruction-error metric + adaptive residual resolution, `MeshVertexSplitter` (seam-safe, attribute-preserving), debug channel views.
**Addresses (features):** vertex-color decomposition, error metric, adaptive resolution, vertex splitting, debug views (v1.x).
**Avoids (pitfalls):** #4 precision (gamma-vs-linear vertex-color contract), #5 UV seam bleeding, #10 vertex-split attribute corruption.

### Phase 5: Stylization
**Rationale:** Most optional and most complex; the profile-driven design keeps it cleanly separable.
**Delivers:** `NAMERStyleProfile` ScriptableObject, palette extraction, hue-aware mapping, seam-aware edge-preserving smoothing, dual AO controls.
**Addresses (features):** stylization, dual AO (v2+).
**Avoids (pitfalls):** #5 seam bleeding (UV-island gutter mask infrastructure).

### Phase 6: Hardening — Batch, Determinism, Multi-Platform
**Rationale:** Batch is a loop over the now-stable single-asset path; determinism and cross-platform validation are the last risk to close.
**Delivers:** Batch processing over selections/folders; determinism tests across import configurations; DX11 + Metal kernel validation; leak watchdog. (AssetPostprocessor, HDRP, and Blender round-trip remain deferred post-v1.)
**Addresses (features):** batch processing (v1.x), plus the determinism/re-runnability table stakes.
**Avoids (pitfalls):** #3 multi-platform, #6 determinism, #8 sustained-preview leaks.

### Phase Ordering Rationale

- **Pure-and-cheap first:** the format contract (Core + runtime decode) is testable headless and every other component depends on it — reversing this order means reworking the shader and every kernel when the format drifts.
- **De-risk GPU second:** "does compute work in-editor, on every target, without stalls/leaks" is the highest-uncertainty technical question; resolving it before the UX layer avoids building preview on an unproven foundation.
- **Vertical slice before hard math:** asset generation turns normalize→pack→generate→render into a shippable v1 before the algorithmically risky vertex-color fitting and stylization, matching the FEATURES v1/v1.x/v2 tiers.
- **Stylization last:** it depends on nothing else but nothing depends on it; keeping it last also honors the documented "defer if conversion value alone lands users" strategy.

### Research Flags

Phases likely needing deeper research during planning:
- **Phase 4 (vertex-color decomposition):** the barycentric least-squares + residual + seam-splitting algorithm is the highest algorithmic risk and has no single authoritative reference; Burst job structure and mesh-attribute preservation need validation.
- **Phase 5 (stylization):** palette extraction and edge-preserving filter specifics are sparse in Unity docs; reference-image hue mapping is niche.
- **Phase 6 (multi-platform compute):** exact Metal/DX11 threadgroup and `groupshared` limits are flagged MEDIUM in `PITFALLS.md` and must be verified against the Unity 6 compute page.
- **Phase 3 (preview):** `PreviewRenderUtility` script-reference returned 404 during research; verify the API surface during planning.

Phases with standard patterns (skip research-phase):
- **Phase 1 (Core + runtime decode):** octahedral encoding and bit-packing are well-established; pure-C# asmdef/UTF setup is fully documented.
- **Phase 2 (GPU compute pipeline):** `ComputeShader` dispatch + `AsyncGPUReadback` + `GraphicsFormat` are thoroughly documented Unity patterns.
- **Phase 3 (asset generation + UPM package):** `AssetDatabase.CreateAsset` + package.json structure are standard and well-documented.

## Confidence Assessment

| Area | Confidence | Notes |
|------|------------|-------|
| Stack | HIGH | Core stack verified against official Unity docs/Context7; exact patch versions (6000.0 vs 6000.3) are MEDIUM. |
| Features | MEDIUM | PRD-derived items are HIGH; Adobe Substance parity details timed out (LOW/MEDIUM). |
| Architecture | HIGH | Standard Unity editor patterns, verified against official docs; `PreviewRenderUtility` detail is MEDIUM (404). |
| Pitfalls | MEDIUM-HIGH | Unity behaviors stable and well-established; Metal/DX11 numeric limits derived from API specs (MEDIUM). |

**Overall confidence:** HIGH for stack/architecture and sequencing; MEDIUM for competitor parity claims and a handful of specific numeric limits.

### Gaps to Address

- **Adobe Substance feature parity** (LOW confidence, timed out) — validate only if precise competitor parity claims are needed; otherwise treat as directional.
- **Metal/DX11 exact threadgroup/`groupshared` limits** — verify against the Unity 6 compute platform page at Phase 2/6 rather than treating the MEDIUM numbers as absolute.
- **`PreviewRenderUtility` API surface** — confirm method signatures during Phase 3 planning (script-reference 404 during research).
- **Unity 6 LTS exact patch** (6000.0 vs 6000.3) — confirm the minimum `"unity"` version at package.json creation time.
- **Octahedral tangent-space assumptions** — verify it round-trips against real tangent-space normal maps (a "looks done but isn't" checklist item).
- **WebSearch tooling** returned no usable results in this environment; all findings rest on direct doc fetches plus the project PRD.

## Sources

### Primary (HIGH confidence)
- Context7 `/websites/unity3d_manual` — compute shaders, `enableRandomWrite`, `RWTexture2D`, platform support, UTF asmdef setup, `GraphicsFormat`/`IsFormatSupported`.
- Context7 `/unity-technologies/graphics` — URP 17.x = Unity 6 mapping.
- `docs.unity3d.com/Packages/com.unity.mathematics@1.3` — Mathematics 1.3.2, confirms no SVD/eigen.
- `docs.unity3d.com/Packages/com.unity.test-framework@1.4` — UTF 1.4.x.
- Unity Manual — Assembly definitions, Custom packages, UI Toolkit editor windows, Color space, Compute shaders, Texture import settings.
- Unity ScriptReference — `ComputeShader`, `Rendering.AsyncGPUReadback`, `AssetPostprocessor`.
- Unity Polybrush documentation — brush modes (vertex color painting, texture blending, mesh editing).
- NAMER Unity Plugin PRD (`NAMER_UNITY_PLUGIN_PRD.md`) and `.planning/PROJECT.md` — authoritative feature set and non-goals.

### Secondary (MEDIUM confidence)
- Wikipedia "Unity (game engine)" — 6000.0.x / 6000.3.x LTS status.
- Unity-Technologies/Graphics `package.json` (dev branch) — URP 17.6.0.
- Cigolle et al., "A Survey of Efficient Representations for Independent Unit Vectors" — octahedral encoding.
- `andydbc/unity-texture-packer` README — channel-packer tool reference.
- Metal/DX11/Vulkan threadgroup and `groupshared` numeric limits — from long-standing API specs, not re-verified against Unity 6 docs.

### Tertiary (LOW confidence — needs validation)
- Adobe Substance 3D feature specifics — help pages timed out; derived from training data.

---
*Research completed: 2026-08-25*
*Ready for roadmap: yes*

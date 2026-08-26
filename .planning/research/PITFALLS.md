# Pitfalls Research

**Domain:** Unity editor texture / GPU-compute / mesh-processing plugin (NAMER)
**Researched:** 2026-08-25
**Confidence:** MEDIUM-HIGH (Unity-specific behaviors are stable and well-established; WebSearch returned no usable results in this environment, so most claims rest on domain knowledge plus the official Unity docs listed in Sources)

## Critical Pitfalls

### Pitfall 1: sRGB vs Linear Color Space Mismatch in Compute Shaders

**What goes wrong:**
The plugin reads a source texture, runs a GPU compute pass, writes an output texture, and the result looks too bright, too dark, or washed out compared to the source. More subtly, the packed NAMER surface texture (octahedral normals, AO, roughness) is treated as if it were a color texture, so every downstream operation corrupts the data.

**Why it happens:**
Unity has two color-space contexts. In **Linear** project color space, a fragment shader sampling an `sRGB`-flagged texture gets an automatic gamma→linear conversion performed by the *sampler hardware*. A **compute shader does not sample** — it does a typed `Load()`/index fetch, which returns the **raw stored texel value with no sRGB conversion**. Developers write a compute pass assuming the same values they see in a fragment shader, and get raw values instead. Separately, the NAMER surface texture is **data, not color**: normals/AO/roughness are linear quantities and must never be flagged `sRGB`, and the packed alpha bits (metallic/emissive/roughness) are meaningless if re-interpreted through a color-space transform.

**How to avoid:**
- Establish an explicit color-space contract in Phase 1: every compute pass states whether its inputs/outputs are "linear-light color," "gamma-encoded color," or "non-color data."
- Do the sRGB↔linear conversion *explicitly* in the compute shader (or in C# when reading pixels), never rely on sampler auto-conversion for compute work. Provide `LinearToSRGB`/`SRGBToLinear` HLSL helpers.
- When reading source textures from C#, use `TextureImporter` to know each texture's `sRGBTexture` flag and convert accordingly, or set/restore a known read state.
- Mark the packed surface texture and any normal/AO/mask data as **linear / non-sRGB** at import and save time. Mark only the base/residual *color* texture as `sRGB`.
- Test the same asset in both Gamma and Linear project color spaces; a correct plugin produces identical output data (only preview presentation differs).

**Warning signs:**
- Before/after preview matches in one project color space but not another.
- Base color output looks "washed out" (linear value saved as sRGB) or "crushed" (gamma value saved as linear).
- Packed texture "normals" visually contain color-gradient banding when opened in an image viewer.

**Phase to address:**
Phase 1 (establish the contract and helper set) and Phase 2 (encoding correctness, render comparison). Phase 3 hardens the source-read path.

---

### Pitfall 2: Blocking GPU Readback (Pipeline Stall) in the Processing Loop

**What goes wrong:**
The editor window freezes, or interactive preview crawls, because every processing step calls `Texture2D.ReadPixels`/`GetPixels`/`GetRawTextureData` (or `RenderTexture.ReadPixels`) synchronously on the main thread, forcing the CPU to wait for the GPU to finish every queued dispatch and full readback.

**Why it happens:**
`ReadPixels`/`GetPixels` are synchronous: they stall the CPU (and thus the Unity Editor main thread) until the GPU pipeline drains and the data is copied back. In a batch of many compute passes, or an interactive "process on every slider tick" preview, this turns a few-millisecond GPU job into a multi-frame editor hang.

**How to avoid:**
- Use `AsyncGPUReadback.Request`/`RequestIntoNativeArray` for readback; it returns a request immediately and delivers data a few frames later without stalling either CPU or GPU. (Confirmed in Unity docs: "without any stall (GPU or CPU).")
- Batch readbacks: request all needed surfaces, then `WaitAllRequests` once, rather than N synchronous reads.
- Gate on `SystemInfo.supportsAsyncGPUReadback`; fall back to a single sync readback only where necessary and cache the result.
- Keep data on the GPU across passes (RenderTexture → RenderTexture) and only read back the *final* result the user needs to see or save.

**Warning signs:**
- Editor responsiveness drops sharply when "Process" or a preview slider is triggered.
- Profiler shows a long main-thread wait on `ReadPixels` / `Gfx.WaitForPresent`.

**Phase to address:**
Phase 3 (GPU texture pipeline) and Phase 6 (interactive preview and live controls).

---

### Pitfall 3: Compute Shader Platform Differences (Metal vs DX11 vs Vulkan)

**What goes wrong:**
A compute pass works on the developer's Windows/DX11 machine but silently fails, produces garbage, or crashes on macOS/Metal (or vice versa). Common triggers: oversized `[numthreads]`, too much `groupshared` memory, unsupported `RWTexture` formats, or relying on behavior that is undefined on one API.

**Why it happens:**
Unity compiles the same HLSL to Metal (via MSL), DX11 (HLSL), and Vulkan (via SPIR-V), and the backends differ in hard limits and undefined-behavior tolerance:
- **Total threads per group**: Metal caps the *product* of the `numthreads` dimensions at 1024 (e.g. `8×8×8` is fine, `16×16×8` is not). DX11 allows up to 1024 per dimension. A group declared `[numthreads(32,32,1)]` (1024 threads) is legal on DX11 but can exceed Metal constraints when combined with other factors; keep the *total* ≤ 1024 and ideally ≤ 256 for older mobile GPUs.
- **Threadgroup (`groupshared`) memory**: DX11 guarantees ~32 KB, Vulkan guarantees 16 KB, Metal threadgroup memory varies by device (often 16–32 KB, sometimes less on iOS). Declaring too much `groupshared` fails to compile or runs out.
- **`RWTexture` format support**: not all formats are writable on all backends; `unorm`/`snorm` typed UAV writes clamp and can behave differently. Safest cross-platform target is writing `float4` to `RWTexture2D<float4>` with an `RGBAFloat` (or `R16G16B16A16_Float` where float is heavy) backing format, then converting to the final texture format separately.
- **Undefined behavior**: uninitialized `groupshared` reads, out-of-bounds dispatch, and NaN/Inf propagation are tolerated differently per API.

**How to avoid:**
- Adopt a shared "compute dispatch" helper in Phase 3 that owns the safe constants: `kMaxThreadsPerGroup = 256`, `kMaxGroupSharedBytes = 16384`.
- Write compute shaders that run on all targets: declare modest `[numthreads(8,8,4)]`-style groups, initialize `groupshared` explicitly, guard index reads, avoid `RWTexture<unorm>`.
- Add a CI/editor smoke test that runs every compute kernel once and asserts no errors on the current platform; test on at least one DX11 and one Metal machine before shipping.
- Prefer `float4` RW targets; do format conversion in a separate, well-tested pass.

**Warning signs:**
- "This only works on my Windows box" — the canonical cross-platform compute smell.
- Compute shader compile warnings about threadgroup size or shared memory on Metal.
- Flickering/corrupted output only on one OS.

**Phase to address:**
Phase 3 (first real GPU work — establish the compatibility layer) and Phase 7 (explicit multi-platform compute testing).

---

### Pitfall 4: Precision Loss in 6-bit Roughness and Color32 Vertex Colors

**What goes wrong:**
Two distinct but related quantization failures:
1. **6-bit roughness (64 values) bands visibly** on glossy surfaces, or loses the smooth matte↔gloss transition that makes materials read correctly.
2. **Color32 vertex colors (8-bit)** show banding on smooth gradients, and/or the base color reconstructed from vertex colors has a visible color shift vs the source because of the sRGB-vs-linear encoding choice.

**Why it happens:**
- Roughness is not perceptually uniform. Quantizing *linear* roughness to 64 evenly spaced steps wastes precision in the bright/glossy range and starves the matte range — exactly where banding is most visible. A naive `round(r * 63) / 63` quantization also creates hard step edges with no dithering.
- `Color32` is 8 bits/channel. Storing *linear-light* color in 8 bits wastes precision in the dark range (where humans see banding) and over-allocates in the bright range. If the vertex color encoding (gamma vs linear) doesn't match the residual texture's encoding, the multiply `VertexColor × Residual` is wrong.

**How to avoid:**
- **Roughness**: remap to a perceptual space before quantization (e.g. `sqrt(roughness)` — the UE-style perceptual roughness) so 64 steps distribute where the eye needs them; quantize *then* store, and reconstruct `r^2` on decode. Dither the final value (2×2 Bayer or blue-noise) to hide banding. Test specifically on a glossy metal and a fine matte cloth.
- **Vertex colors**: decide the encoding contract up front. Recommendation: store vertex colors *gamma-encoded* (sRGB-like) in `Color32` for better dark-range precision, and decode to linear in the runtime shader before multiplying by the linearized residual. Document this in the shader contract so fitter and shader agree.
- Expose the quantization step as a named, testable function with a round-trip unit test: `QuantizeRoughness(linear)` → bits → `DecodeRoughness(bits)`, asserting max error bounds and no out-of-range values.
- Add an error metric for the vertex-color fit and surface it to the user (already planned as "reconstruction error") — it is also the early-warning detector for banding.

**Warning signs:**
- Visible contour/step bands in the roughness response of glossy surfaces.
- Flat-color regions of the model show subtle "posterized" bands in the final render that weren't in the source.
- Reconstruction-error debug view shows unexpectedly high error on smooth gradients (a sign of precision, not geometry, loss).

**Phase to address:**
Phase 2 (roughness packing — perceptual remap and dithering) and Phase 4 (vertex-color encoding contract). The reconstruction-error debug view lands in Phase 6.

---

### Pitfall 5: UV Seam Bleeding During Filtering / Downsampling / Residual Generation

**What goes wrong:**
Edge-preserving smoothing (bilateral/Kuwahara/guided), downsampling, and mip generation sample across UV island boundaries and pull in texels from a *different* island (or from the transparent gutter). Result: colored halos at seams, smeared colors between unrelated surfaces, and a residual texture full of seam errors.

**Why it happens:**
UV islands are separate regions of the texture atlas with no geometric adjacency. Standard bilinear filtering and spatial kernel filters assume neighboring texels are neighbors on the surface — but at an island edge, the neighbor texel belongs to a different part of the mesh. The sampler cannot know this; it just blends across the gutter.

**How to avoid:**
- Build a **UV island / gutter mask** (which texels are "inside" an island) and use it to gate every spatial filter: sample neighbors but discard/blend only those with the same island ID. This is the single most important piece of infrastructure for Phase 5.
- **Dilate/pad islands** (bleed the edge texels outward a few pixels) before any filtering or mip generation — this is what proper atlasing tools do to make bilinear and mip sampling safe.
- For the residual computation (Phase 4), sample the source texture only at UV coordinates that resolve inside the same island; never let a residual sample fall into the gutter.
- Keep the gutter transparent and ensure the final color texture's mip chain is generated from the padded atlas (or disable mipmap auto-generation and generate your own seam-aware mips).

**Warning signs:**
- Halo/glow of one surface's color along the edge of an adjacent surface.
- "Sparkle" at seams in the debug residual/error view.
- Artifacts that appear only after downsampling the residual or when mipmaps are enabled.

**Phase to address:**
Phase 3 (base texture normalization / gutter handling), Phase 4 (residual sampling), Phase 5 (edge-preserving smoothing must be seam-aware).

---

### Pitfall 6: Non-Deterministic Source Reads Due to Importer Settings

**What goes wrong:**
The same source FBX/texture produces slightly different NAMER output on different machines, or after a re-import, or after the user changes a platform override. Tests that assert "source unchanged / deterministic output" fail intermittently.

**Why it happens:**
When the plugin reads a source texture via `LoadAssetAtPath<Texture2D>` + `ReadPixels`, what it actually reads is the **imported** texture — already downscaled by `maxTextureSize`, already mipmapped, already **lossy-compressed** (DXT/BC/ETC/ASTC), and already sRGB-decoded or not, depending on the importer settings. Those settings vary per project, per build target (platform overrides), and per user. Reading a BC-compressed texture gives you the *decompressed* approximation, not the original pixels. This is the root of "works on my machine, differs in CI."

**How to avoid:**
- **Read the original source bytes directly** (`File.ReadAllBytes` of the PNG/TGA/EXR + decode) for the authoritative color/normal data, rather than the imported texture, when determinism matters. This is the only fully deterministic path.
- If reading the imported texture is acceptable (e.g., for a fast preview), capture and log the importer settings (sRGB, compression, maxSize, mipmap) so any discrepancy is attributable.
- Never mutate the source asset's `TextureImporter` to make it readable and forget to restore it — that permanently changes the source and re-imports it (see Pitfall 11).
- Keep generated output fully derived from explicit inputs; do not bake anything that depends on ambient project state (like the active Color Space) into the *data* — bake it into presentation only.

**Warning signs:**
- Output differs between two teammates with different texture import defaults.
- CI runs produce diffs with no code change.
- `ReadPixels` returns values that visibly differ from opening the PNG in an image editor.

**Phase to address:**
Phase 1 (source inspection — decide and centralize the "authoritative read" path) and Phase 7 (determinism testing across import configurations).

---

### Pitfall 7: Texture Compression Corrupting Packed Data Textures

**What goes wrong:**
The generated NAMER surface texture (octahedral normal RG, AO B, metallic/emissive/roughness bits in A) is saved, re-imported with the platform's default compression (BC1/BC3, ETC2, ASTC), and the packed data is silently destroyed — normals point wrong, AO is banded, and the roughness/metallic/emissive bits are scrambled. The base color texture compressed as sRGB also shifts colors subtly.

**Why it happens:**
Color-block compression (BC1/BC3/ETC2) assumes correlated RGB color and compresses aggressively; it is fundamentally wrong for **non-color data** (normals, masks, bit-packed channels). The alpha bits in particular are irrecoverable after BC3 DXT alpha compression. Packed data must be linear (non-sRGB) and must use a data-appropriate format (uncompressed RGBA, or a normal-map format like BC5/BC7 for the normal channels if the packing allowed it — which it does not here, since the channels are semantically mixed).

**How to avoid:**
- At save time, explicitly set the generated texture's import settings: packed surface texture → **linear (sRGB unchecked) + no color compression** (uncompressed RGBA, or a lossless/appropriate format); base color → sRGB + a chosen compression; do not accept Unity's default "Default" texture type for the packed texture.
- Prefer writing `.png` for the packed surface texture with compression disabled, or `.exr`/`.tga` for float/linear where needed — and set the import settings *immediately* after `AssetDatabase.CreateAsset` so no intermediate import with bad defaults occurs.
- Encode the fact that the texture is "linear + uncompressed" in the import pipeline itself (a helper that stamps correct `TextureImporter` settings), not as a manual step the artist must remember.
- Add a validation test that re-imports the packed texture and asserts the decoded bits round-trip exactly.

**Warning signs:**
- Roughness looks like random noise or metallic flickers on/off in the final render.
- Octahedral normals produce visible seams or inverted facets that disappear when the texture is forced to "None" compression.
- The packed texture "looks colorful" when inspected (normal map data should look bluish-gray; banding indicates sRGB treatment).

**Phase to address:**
Phase 2 (packing correctness and the import-settings stamping helper) and Phase 3 (save/import pipeline for generated assets).

---

### Pitfall 8: Editor-Time GPU Resource Leaks

**What goes wrong:**
Repeated processing (batch over many assets, or interactive preview re-running every tick) allocates `RenderTexture`s, `ComputeBuffer`s, and temporary `Texture2D`s that are never released. GPU memory climbs until the editor slows or crashes. This is invisible in a single run and only manifests over time — the classic "works once, then everything gets slow" bug.

**Why it happens:**
GPU-side resources (`RenderTexture`, `ComputeBuffer`) are not automatically freed by the C# garbage collector on a reliable schedule; `RenderTexture` also pins native GPU memory. In an editor tool that processes repeatedly, every temporary RT/ComputeBuffer that isn't explicitly released leaks. Interactive preview turns this into a per-frame leak.

**How to avoid:**
- Use `RenderTexture.GetTemporary`/`ReleaseTemporary` for short-lived intermediates, and `using`/`try-finally` (or `IDisposable` wrappers) for `ComputeBuffer` so `Release()` is always called.
- Never allocate a new RT/ComputeBuffer per process tick when one can be cached and resized.
- Centralize all GPU allocation in one resource-owner class; forbid ad-hoc `new RenderTexture(...)` / `new ComputeBuffer(...)` elsewhere.
- Add a leak watchdog in dev: assert that the count of live RTs/ComputeBuffers returns to baseline after each processing batch.

**Warning signs:**
- Editor memory usage (GPU) climbs monotonically while processing multiple assets.
- Editor gets progressively slower during a long interactive session and recovers only after restart.
- Unity logs "RenderTexture: destroying a temporary RT that was not released" warnings (sign of `ReleaseTemporary` misuse).

**Phase to address:**
Phase 3 (GPU pipeline resource ownership) and Phase 6 (interactive preview loop — the highest-frequency allocator).

---

### Pitfall 9: Untestable GPU Code / No CPU Reference

**What goes wrong:**
The encode/decode, quantization, and filtering logic lives *only* in compute shaders. There is no way to assert correctness in a headless CI run (which has no GPU), so the core math — octahedral encode/decode, bit packing, residual computation — is verified only by eyeballing a render.

**Why it happens:**
Compute shaders cannot execute in Unity's `-batchmode -nographics` headless/CI mode. Developers put the math in HLSL because "it runs on the GPU," then find they have no automated test path, and cross-platform floating-point differences make exact comparisons flaky anyway.

**How to avoid:**
- Implement a **CPU C# reference** for every encoding/quantization/fitting operation (octahedral encode/decode, roughness/AO/metallic/emissive packing, vertex-color fit, residual). The GPU kernel is a performance optimization of the same math, not the source of truth.
- Unit-test the C# reference exhaustively (round-trips, bit-level assertions, error bounds). This satisfies the PRD's validation list regardless of GPU availability.
- Add a separate **GPU golden test** that runs only when a GPU is present: dispatch the kernel, read back, compare to the C# reference within a tolerance. Mark it `[UnityTest]` + skip if `SystemInfo.supportsComputeShaders` is false.
- Compare GPU vs CPU with a tolerance (not exact) to absorb platform float differences; assert *structural* invariants exactly (e.g., the metallic bit is 0 or 1, roughness is one of 64 values).

**Warning signs:**
- "I verified it by looking at it" is the only test.
- A fix in the C# reference and the HLSL kernel drifts apart over time (duplicated logic).
- CI can't validate the core format because it's GPU-only.

**Phase to address:**
Phase 1 (test infrastructure + CPU reference skeleton) and Phase 2 (full CPU reference + GPU golden tests for encode/decode).

---

### Pitfall 10: Mesh Vertex Splitting Corrupting Source Geometry or Attributes

**What goes wrong:**
To give different vertex colors on either side of a UV seam, the plugin duplicates vertices and re-indexes triangles. The generated mesh renders wrong because splitting broke tangents, normals, multiple UV channels, blend shapes, bone weights, or sub-mesh/material boundaries — or the plugin accidentally modified the *source* mesh instead of a copy.

**Why it happens:**
Unity `Mesh` stores a single vertex buffer where position, normal, uv0/uv1, tangent, and color all share one index. You cannot assign per-corner vertex colors at a seam without duplicating the vertex. But duplicating a vertex for a color split must also copy *all* its other attributes (normal, tangent, every UV channel, bone weights/indices, color) and update every triangle that referenced it — and you must split only the mesh copy, not the source, and preserve sub-mesh boundaries (a vertex shared across two materials must not be silently merged back).

**How to avoid:**
- Work exclusively on a **deep copy** of the mesh (never `LoadAssetAtPath<Mesh>` and mutate — see Pitfall 11).
- Treat vertex splitting as a dedicated, unit-tested `MeshVertexSplitter` that: keys vertices by (position, normal, uv0, uv1, tangent, color, bone weights, submesh) — i.e., split only where a *color* discontinuity demands it, while faithfully copying every other channel.
- Preserve sub-mesh topology; do not let the splitter merge vertices that belong to different sub-meshes or different materials.
- Recompute normals/tangents only if explicitly required; otherwise copy them byte-for-byte to avoid visual smoothing differences.
- Test with a real asset that has UV seams, a second UV channel, skinning, and multiple materials; assert source mesh vertex/triangle count is unchanged and the generated mesh still binds correctly.

**Warning signs:**
- Normals flip or smooth incorrectly on the generated mesh.
- Lightmap/UV2 or skinned deformation breaks after processing.
- The source mesh's vertex count changes (it must never).

**Phase to address:**
Phase 4 (vertex color decomposition — where splitting is introduced).

---

### Pitfall 11: Modifying Source Assets (Directly or via Importer Settings)

**What goes wrong:**
The plugin writes to the source FBX/texture/material, or mutates its `TextureImporter`/`ModelImporter` settings (e.g., checking `isReadable`, toggling `sRGB`, changing compression) and leaves them changed — corrupting the artist's original and triggering a re-import. "Source assets never modified" is the project's core safety guarantee and the single most likely thing to be violated subtly.

**Why it happens:**
`AssetDatabase.LoadAssetAtPath` returns a reference to the *live imported asset*; calling `EditorUtility.SetDirty` on it and `AssetDatabase.SaveAssets` writes back to the source. Changing `TextureImporter` settings on a source texture is *also* a modification (persisted in the `.meta` file) even though the texture bytes themselves are untouched. `ReadPixels` requires `isReadable`, which tempts developers to flip the importer flag on the source.

**How to avoid:**
- Hard rule, enforced in code: **generated output is only ever written via `AssetDatabase.CreateAsset`/`AddObjectToAsset` into the `NAMERGenerated/` directory**; the source path is read-only by construction.
- Read source pixels via the original file bytes (Pitfall 6) or a temporary in-memory copy; if you must use `TextureImporter` flags on the source, snapshot and **always restore** them in a `finally`, and prefer reading a temp copy over toggling the source.
- Add an automated test that snapshots source asset contents + `.meta` + importer settings, runs a full process, and asserts byte-for-byte equality (the PRD already lists "source assets remain unchanged" as test #10).
- Never call `SetDirty`/`SaveAssets` on anything outside the generated directory.

**Warning signs:**
- Git shows changes to `Source/*.meta` files after running the plugin.
- Source textures show up as "readable" or with altered compression after processing.
- The artist reports their imported asset settings changed.

**Phase to address:**
Phase 1 (output asset handling — establish the read-only-source + generated-directory discipline and the immutability test).

---

## Technical Debt Patterns

| Shortcut | Immediate Benefit | Long-term Cost | When Acceptable |
|----------|-------------------|----------------|-----------------|
| Read imported (compressed) source texture instead of original bytes | Fastest to implement; one line | Non-deterministic output; lossy base color; cross-machine diffs | Preview-only path, never the authoritative bake |
| Synchronous `ReadPixels` everywhere | Simple, obviously correct | Editor hangs at 2K/4K; unusable interactive preview | One-shot final save; never in a per-tick preview |
| Skip the CPU reference, put math only in HLSL | Less code initially | No CI testability; GPU/CPU logic drift; unverifiable format | Never — the format *is* the product |
| Let Unity default-import the packed texture (compression/sRGB) | No import code | Silent corruption of packed data; unrecoverable without re-bake | Never for the packed surface texture |
| `new RenderTexture`/`new ComputeBuffer` inline per operation | Localized code | Editor GPU leak over batches | Only behind the single resource-owner with guaranteed release |
| Reuse the source `TextureImporter` for generated assets | Fewer settings to set | Wrong sRGB/compression on generated data | Never — always stamp explicit settings |
| Hard-code one edge-preserving filter | Fastest stylization win | Seam bleeding, no fallback, portability risk | Phase 5 spike only; replace with seam-aware filter |
| Fixed 4K→2K downsample ratio (no error metric) | Trivial logic | Detail loss where residual still needs resolution | Never — PRD mandates measured, overridable resolution |

## Integration Gotchas

| Integration | Common Mistake | Correct Approach |
|-------------|----------------|------------------|
| Unity Color Space | Assuming compute shaders auto-linearize sRGB inputs | Convert explicitly in-kernel; mark data textures linear |
| GPU readback | `ReadPixels`/`GetPixels` on the main thread | `AsyncGPUReadback` + `WaitAllRequests`; read back only the final surface |
| Metal vs DX11 | `[numthreads]` or `groupshared` too large for Metal | Total threads ≤ 256, `groupshared` ≤ 16 KB, `float4` RW targets |
| Texture import (source) | Flipping `isReadable`/`sRGB` on the source and leaving it | Read original bytes; snapshot+restore any importer mutation |
| Texture import (generated) | Default compression/sRGB on the packed texture | Stamp linear + uncompressed settings at `CreateAsset` time |
| Mesh attribute split | Splitting vertices and dropping tangent/uv1/bones | Key by all attributes; copy every channel; preserve submeshes |
| AssetDatabase save | `SetDirty` on a source asset | `CreateAsset`/`AddObjectToAsset` only into `NAMERGenerated/` |
| Script/domain reload | Losing in-flight compute state on recompile | Keep processing restartable/idempotent; persist progress to disk |
| Meta files / GUIDs | Regenerating assets creates GUID churn | Stable, deterministic output paths; overwrite via same GUID |

## Performance Traps

| Trap | Symptoms | Prevention | When It Breaks |
|------|----------|------------|----------------|
| Per-pixel C# loops (explicitly forbidden by PRD) | Multi-second hangs on 2K textures | GPU compute for all high-res ops | Immediately at 2K, catastrophic at 4K |
| Sync readback per pass | Editor freeze during multi-pass processing | AsyncGPUReadback, batch readback | Any multi-pass batch |
| Per-frame RT/ComputeBuffer allocation in preview | Editor GPU memory climbs during a session | Cache + resize; single resource owner | Long interactive sessions |
| Full-mesh barycentric solve per triangle in C# | Slow "Process" on dense meshes | Precompute per-triangle matrices; GPU or vectorized | Dense meshes (10k+ tris) |
| Repeated full re-import/compress of generated textures | Slow save + import churn | Write once, set import settings once | Batch processing |
| No mip handling on seam-aware filters | Correct at mip 0, wrong at mip 1+ | Pad islands before mip gen | Any rendered distance below mip 0 |

## Security Mistakes

*(This is an editor tool, so "security" maps to asset-integrity and resource-safety rather than network attack surface.)*

| Mistake | Risk | Prevention |
|---------|------|------------|
| Writing to source assets / their `.meta` | Irreversible damage to the artist's work; broken project | Read-only source discipline; immutability test |
| Not restoring importer settings after a read | Source silently re-imported with wrong settings | Snapshot + `finally` restore; prefer byte read |
| Overwriting an existing generated asset with a different GUID/identity | Broken material references in scenes | Stable deterministic output identity |
| Shipping editor-only code in the Runtime assembly | Package fails to build in player builds | Strict Editor/Runtime asmdef split |
| Unbounded GPU allocation from untrusted art inputs | Editor crash on pathological assets | Resource budgets + leak watchdog |

## UX Pitfalls

| Pitfall | User Impact | Better Approach |
|---------|-------------|-----------------|
| Preview does not match saved output (different color space/compression) | "Why does it look different after I save?" | Preview through the same sRGB/linear and import path as the save |
| Processing with no progress/cancellation | User thinks it hung on a 4K batch | Progress bar + cancel; async where possible |
| Unlabeled debug channels | User can't interpret "reconstruction error" vs "residual" | Clear per-channel labels + tooltips; a "final vs before" toggle |
| Too many controls surfaced at once | Overwhelmed user (this user's known scope-creep frustration) | Progressive disclosure; sensible defaults; Advanced section |
| Vague "process" that seems destructive | Fear it will overwrite source | Explicit "outputs go to NAMERGenerated/, source untouched" affordance |
| Defaults that require understanding of NAMER format | Artist must learn packing internals | Hide bit-packing details; expose only meaningful sliders |

## "Looks Done But Isn't" Checklist

- [ ] **Octahedral normal encode/decode:** Works on a unit-sphere test but not verified against real *tangent-space* normal maps — verify round-trip error and that tangent-basis assumptions match the source map.
- [ ] **Roughness 6-bit quantization:** Looks fine on matte surfaces but bands on glossy ones — verify on a glossy metal with perceptual remap + dithering.
- [ ] **GPU compute:** Works on DX11 but not Metal (or vice versa) — run every kernel on both backends.
- [ ] **sRGB handling:** Correct in Linear project color space but wrong in Gamma — test both.
- [ ] **Vertex color fit:** Works on a single-submesh mesh but breaks on multi-material / skinned / multi-UV meshes — test a representative complex asset.
- [ ] **Source immutability:** Source texture bytes unchanged, but `.meta`/importer settings were mutated — verify meta + importer settings, not just texture bytes.
- [ ] **Edge-preserving filter:** Clean on an isolated island but bleeds across nearby islands — test two-adjacent-island seam cases.
- [ ] **Packed texture import:** Looks correct before re-import but corrupt after Unity compresses it — re-import and assert round-trip.
- [ ] **Interactive preview:** Correct on first tick but leaks GPU memory over a session — run a sustained preview loop under a leak watchdog.
- [ ] **Determinism:** Matches on one machine but not another — verify output is a function of explicit inputs, not importer/project state.

## Recovery Strategies

| Pitfall | Recovery Cost | Recovery Steps |
|---------|---------------|----------------|
| sRGB/linear mismatch | LOW (before ship) / HIGH (shipped data) | Re-run normalization with corrected contract; do not hand-fix baked textures |
| Compression corruption of packed data | LOW (source preserved) | Re-bake from source with correct import settings |
| GPU resource leak | MEDIUM (annoyance) | Restart editor; fix resource owner; add watchdog |
| Vertex split corruption | LOW-MEDIUM | Re-process from source; fix `MeshVertexSplitter`; re-test complex asset |
| Non-deterministic output | MEDIUM | Switch to authoritative byte-read path; re-bake affected assets |
| Wrong vertex-color encoding (gamma vs linear) | MEDIUM | Fix shader decode + re-fit vertex colors from source |
| Source asset modification | HIGH (artist's file) | Restore from VCS; add immutability test; audit write paths |

## Pitfall-to-Phase Mapping

| Pitfall | Prevention Phase | Verification |
|---------|------------------|--------------|
| sRGB/linear mismatch (compute) | Phase 1–2 | Round-trip output identical in Gamma and Linear projects |
| Blocking GPU readback | Phase 3 | Profiler shows no `ReadPixels` stall; editor stays responsive |
| Metal vs DX11 compute differences | Phase 3 | Every kernel smoke-tested on DX11 + Metal |
| 6-bit roughness / Color32 precision | Phase 2 / Phase 4 | Quantize round-trip error bounds; reconstruction-error view |
| UV seam bleeding | Phase 3–5 | Two-island seam test shows no halo/smear |
| Importer non-determinism | Phase 1 | Deterministic output across two import configs |
| Compression corrupting packed texture | Phase 2–3 | Re-import + bit round-trip assertion |
| Editor GPU resource leaks | Phase 3 / Phase 6 | Leak watchdog: live RT/buffer count returns to baseline |
| Untestable GPU code | Phase 1–2 | C# reference unit tests pass headless; GPU golden tests pass |
| Vertex splitting corruption | Phase 4 | Source mesh unchanged; generated mesh binds/renders correctly |
| Source asset modification | Phase 1 | Immutability test: source bytes + meta + importer settings unchanged |

## Sources

- Unity Manual — Color space / Linear or Gamma workflow (confirms Unity uses an sRGB sampler to cross gamma→linear; https://docs.unity3d.com/Manual/set-project-color-space.html)
- Unity ScriptReference — `Rendering.AsyncGPUReadback` (confirms asynchronous readback "without any stall (GPU or CPU)" and the `WaitAllRequests` completion model; https://docs.unity3d.com/ScriptReference/Rendering.AsyncGPUReadback.html)
- Unity Manual — Compute shaders (platform/API support overview; https://docs.unity3d.com/Manual/class-ComputeShader.html)
- Unity Manual — Texture type: Normal map (import formatting for real-time normal mapping; https://docs.unity3d.com/Manual/texture-type-normal-map.html)
- Unity Manual — Texture import settings / sRGB checkbox (import-time color space flag)

*Note: WebSearch returned no usable results in this environment, and Unity's manual pages are terse on exact Metal/DX11 threadgroup limits. The concrete numeric limits in Pitfall 3 (total threads ≤ 1024 on Metal, threadgroup memory 16–32 KB, Vulkan 16 KB guarantee) are derived from long-standing, stable graphics-API specifications and are flagged MEDIUM confidence — verify against the current Unity 6 "compute shaders" platform page during Phase 3 implementation rather than treating them as absolute.*

---
*Pitfalls research for: NAMER Unity editor texture-processing plugin*
*Researched: 2026-08-25*

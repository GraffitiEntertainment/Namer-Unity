# Phase 2: Source Inspection + GPU Compute Pipeline - Research

**Researched:** 2026-08-26
**Domain:** Unity editor asset inspection + GPU compute texture pipeline (URP Lit / Unity Standard source materials → normalized base color + packed NAMER surface, GPU-verified against the Core CPU reference)
**Confidence:** HIGH (property tables read from the installed URP 17.0.4 source; Blender reference read from GitHub develop; DX11 limits verified against Microsoft Learn; Metal/Vulkan numeric limits MEDIUM)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions

- **D-01:** `SourceInspector` ships as a C# API in Phase 2, not UI. It accepts whatever the user selects (GameObject, prefab, FBX/model, material, or folder) and returns an inspection result. A minimal `Tools > NAMER > Inspect Selection` menu item logs the inspection report for manual validation — enough to prove INSP-01 without building Phase 3's editor window.
- **D-02:** Folder sources recurse subfolders for supported renderers/materials. Materials are deduplicated by instance ID so shared materials are inspected/processed once. Unsupported entries produce a warning entry in the result, never a failure.
- **D-03:** The inspection/processing unit is the unique material (renderer → its materials → dedupe). Multi-material meshes yield one inspection unit per unique material.
- **D-04:** Map discovery uses per-shader property-name convention tables read via `Material.GetTexture` — URP Lit first (`_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_EmissionMap`, …), Unity Standard second (`_MainTex`, `_BumpMap`, …). No filename/suffix heuristics in v1 (deferred idea).
- **D-05:** URP smoothness channel handling is a correctness requirement: read `_SmoothnessSource` and honor both source paths (metallic-map alpha vs base-map alpha), converting roughness = 1 − smoothness before packing.
- **D-06:** Unknown/custom (e.g. Shader Graph) shaders get a best-effort scan of known property names; when no maps are found, scalar properties apply, then neutral defaults — with a warning in the inspection result. The pipeline never fails on missing data (INSP-04).
- **D-07:** Neutral defaults when nothing is present: metallic 0, roughness 0.5, AO 1.0, emission black, normal (0, 0, 1).
- **D-08:** Phase 2 cleaning is conservative: color-space normalization (sRGB→linear where the source is authored sRGB) plus AO un-multiply (`albedo ÷ max(AO, ε)`) behind a strength parameter exposed by the pipeline (UI control lands in Phase 3). Full iterative/gradient-based delighting is rejected for this phase (research-heavy, albedo-damage risk) — recorded as a deferred idea.
- **D-09:** Researcher instruction: check the Blender reference implementation (`GraffitiEntertainment/BlenderNamerPlugin`, develop) texture pipeline for any existing base-color cleaning behavior and mirror it where present, in the same spirit as the Phase 1 format mirroring.
- **D-10:** Staged kernels (normalize → octahedral encode → surface pack) rather than one fused kernel, hosted in the existing `Compute/NAMERPack.compute`, plus a shared HLSL include that mirrors the Core encode math so the compute path and the runtime decode shader cannot drift from Core.
- **D-11:** Intermediate compute targets are `R16G16B16A16_SFloat`; the final packed surface output is `R8G8B8A8_UNorm` linear. Compute never writes sRGB targets (locked by project tech stack).
- **D-12:** `ComputeTexturePool` pools temporary render textures (sized to the working resolution) for reuse across dispatches instead of allocating per stage — roadmap-named component; internal design is Claude's discretion.
- **D-13:** GPU→CPU readback uses `AsyncGPUReadback` throughout the pipeline and tests; synchronous reads only for tiny one-shot cases.
- **D-14:** GPU-vs-CPU comparison semantics: expected values come from the Core reference (`NamerFormat`). The packed alpha byte must match EXACTLY (integer packing is deterministic); octahedral R/G and AO within 1/255; decoded-normal agreement via dot ≥ 1 − 1e-3 (Phase 1 tolerance convention).
- **D-15:** Backend scope for this phase: verify locally on Metal (the dev machine). Tests capability-gate on unavailable backends (skip with an explicit report entry, not silent pass). A D3D/Vulkan CI matrix is v2. Researcher must verify the flagged compute limits (threadgroup ≤ 256 threads, groupshared ≤ 16 KB) against Unity 6 docs.
- **D-16:** Phase 1 carry-in housekeeping: rename `NamerFormat.PackSurface`'s `normal` parameter to reflect that it receives a raw DirectX normal-map texel (e.g. `normalTexel`) — do this while writing the kernels that call it.

### Claude's Discretion

- Exact class/file names within the package (SourceInspector API shape, kernel names, pool internals).
- Whether GPU golden fixtures are committed as assets/JSON — planner's choice with researcher input.
- Menu-item naming details beyond `Tools > NAMER/…`.
- Dispatch sizing and synchronization mechanics (as long as D-14 semantics hold).

### Deferred Ideas (OUT OF SCOPE)

- Full delighting (iterative luminance-gradient baked-lighting removal) — potential later phase/v2 if conservative cleaning proves insufficient
- Filename/suffix-based map heuristics for unknown shaders — v2 automation territory
- Shader Graph material property auto-discovery — v2
- D3D/Vulkan/continuous-integration GPU test matrix — v2 (this phase: Metal + capability-gated skips)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| INSP-01 | User can select a GameObject, prefab, FBX/model, material, or asset folder as the NAMER processing source | §Architecture Patterns "Source selection resolution" — `Selection` / `AssetDatabase.LoadAllAssetsAtPath` / `PrefabUtility.LoadPrefabContents` / `AssetDatabase.FindAssets("t:Material"…)` |
| INSP-02 | Processor inspects URP Lit source materials and locates Base Color, Normal, AO, Metallic, Roughness/Smoothness, Emission, and Alpha maps when present | §Standard Stack "URP Lit property table" — `_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_OcclusionMap`(G channel), `_EmissionMap`, `_SmoothnessTextureChannel` |
| INSP-03 | Processor reads scalar material properties as fallbacks when separate maps are absent (Unity Standard supported where available) | §Standard Stack "Unity Standard property table" + scalar fallback names (`_Metallic`, `_Smoothness`, `_Color`, `_EmissionColor`, `_OcclusionStrength`) |
| INSP-04 | Missing maps use sensible defaults without failing the pipeline | D-07 neutral defaults + D-06 warning-not-failure; §Common Pitfalls "never fail on missing data" |
| NORM-01 | Processor produces a cleaned base color texture with unwanted baked lighting removed where feasible | §D-09 Blender mirror (AO un-multiply = albedo ÷ AO; crude normal-Z delighting rejected per D-08); §Code Examples "sRGB→linear + AO un-multiply kernel" |
| NORM-02 | Source maps are normalized (color space, orientation, scalar-vs-map unification) in a GPU compute pipeline | §Standard Stack "compute harness" + `GraphicsFormatUtility.IsSRGBFormat` + explicit `LinearToSRGB`/`SRGBToLinear`; §Common Pitfalls #1 |
| NORM-03 | Per-pixel processing of 2K/4K textures runs via compute shaders, not per-pixel C# loops | §Standard Stack "compute dispatch" (`ComputeShader.Dispatch` + `AsyncGPUReadback`) — no `GetPixels` loops |
| TEST-04 | GPU kernels are verified against the CPU reference implementation (Core assembly) via round-trip tests | §Architecture Patterns "GPU golden test" + D-14 tolerance semantics + §Code Examples "EditMode AsyncGPUReadback test" |

</phase_requirements>

## Project Constraints (from CLAUDE.md)

Directives extracted from `./CLAUDE.md` and the package's embedded stack tables. The planner must treat these with the same authority as locked decisions:

- **Tech stack is locked:** C# (editor/runtime) + HLSL compute shaders (GPU). No C++ native plugins, no Blender/Maya dependency, no cloud processing.
- **URP first.** URP 17.x is the primary target; Unity Standard is a secondary read path; HDRP deferred.
- **No per-pixel C# loops** on 2K/4K textures — GPU compute for all high-resolution per-pixel work (NORM-03).
- **Precision contract:** roughness limited to 64 values (6-bit); vertex colors Color32 (Phase 4, not this phase).
- **Asset safety:** source assets and their `.meta`/importer settings must never be modified; output only under a generated-assets directory (Phase 3 writes; this phase is in-memory only).
- **Package:** must ship as a reusable UPM package (`com.graffitientertainment.namer`), already scaffolded.
- **Use `GraphicsFormat`** (not legacy `RenderTextureFormat`); `RenderTexture.enableRandomWrite = true` for compute write targets.
- **sRGB formats are NOT writable from compute** — always compute into linear formats; sRGB-encode at the save/sample boundary (locked; see §Common Pitfalls #1).
- **No Shader Graph** for the packed-format path (bit ops are HLSL-only); hand-written ShaderLab + HLSL.
- **No MathNet.Numerics / System.Numerics** — Unity.Mathematics + hand-rolled 3×3 (Phase 4); irrelevant to Phase 2 kernels but keep Core pure.
- **No `CommandBuffer`/RenderGraph** for offscreen editor compute — direct `ComputeShader.Dispatch` + `AsyncGPUReadback`.
- **Core purity:** `Core/` holds no `UnityEngine.Object` types; `NamerFormat`/`NamerConstants` are the single source of truth. No magic numbers — constants live in `NamerConstants`.

## Summary

Phase 2 turns the Phase 1 format contract into a working editor-time GPU pipeline. It has two halves with very different risk profiles:

1. **Source inspection (INSP-01..04, CPU):** a `SourceInspector` C# API that resolves whatever the user selected (scene GameObject, prefab asset, FBX/model asset, material, or folder) into a deduplicated set of unique materials, then reads each material's PBR maps and scalar fallbacks through per-shader property-name convention tables. This is low-risk, well-understood Unity Editor API surface — the main correctness traps are (a) URP Lit's smoothness-channel indirection and (b) which channel AO/metallic/smoothness actually live in.

2. **GPU compute pipeline (NORM-01..03, TEST-04):** staged compute kernels (normalize → octahedral encode → surface pack) that produce the normalized base color and packed surface texture entirely on the GPU, with golden tests proving kernel-by-kernel equivalence to `Core/NamerFormat`. The kernels are pure per-texel point operations (no `groupshared`, no cross-thread communication), which makes the flagged cross-platform compute-limit concern largely moot — the only binding constraint is total threads per threadgroup, and `[numthreads(8,8,1)]` (64 threads) is trivially safe everywhere.

**Primary recommendation:** Build `SourceInspector` first (it de-risks the map-discovery conventions and is independently testable without a GPU), then the compute dispatch harness + `ComputeTexturePool` + three staged kernels in `Compute/NAMERPack.compute` with a shared HLSL encode include mirroring `NamerFormat` line-for-line, then GPU golden tests that gate on `SystemInfo.supportsComputeShaders`/`supportsAsyncGPUReadback` and skip-with-report on unavailable backends. The single most important research correction: **URP Lit's smoothness-source property is `_SmoothnessTextureChannel`, not `_SmoothnessSource`**, and URP Lit's AO lives in the **green** channel of `_OcclusionMap` (see §Standard Stack).

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Source selection resolution (GameObject/prefab/FBX/material/folder → materials) | Editor (CPU) | — | `Selection`/`AssetDatabase`/`PrefabUtility` are editor-only; pure CPU traversal, no pixels |
| Map/scalar discovery via property tables | Editor (CPU) | — | `Material.GetTexture`/`GetFloat`/`GetColor` are CPU reads |
| sRGB→linear base-color normalization | GPU (compute) | — | per-pixel; forbidden as C# loop (NORM-03) |
| AO un-multiply (`albedo ÷ max(AO, ε)`) | GPU (compute) | — | per-pixel; mirrors Blender `DIVIDE` node |
| Octahedral encode | GPU (compute) | Core (CPU oracle) | kernel mirrors `NamerFormat.OctahedralEncode` |
| Surface alpha-bit pack (metallic/emissive/roughness) | GPU (compute) | Core (CPU oracle) | kernel mirrors `NamerFormat.PackAlphaBits`/`PackSurface` |
| Final surface assembly (R/G/B/A channels) | GPU (compute) | Core (CPU oracle) | `R8G8B8A8_UNorm` linear write target |
| GPU→CPU readback + comparison | Editor (CPU, in tests) | GPU | `AsyncGPUReadback` + `WaitForCompletion`/`WaitUntil` |
| `ComputeTexturePool` (RT lifecycle) | Editor (CPU) | — | `RenderTexture` descriptor reuse, leak prevention |
| Golden reference (TEST-04 oracle) | Core (CPU) | — | `NamerFormat`/`NamerConstants` are the single source of truth |

## Standard Stack

No new external packages are installed this phase — the entire stack is Unity-native APIs plus the dependencies already declared in the package's `package.json` (`com.unity.mathematics` 1.3.2, `com.unity.render-pipelines.universal` 17.0.4, `com.unity.burst` 1.8.0, `com.unity.collections` 2.5.0). Unity Editor is **6000.0.82f1** (verified from `ProjectSettings/ProjectVersion.txt`), URP **17.0.4** (verified from the installed package cache).

### Core — property tables (authoritative, read from installed URP 17.0.4 source)

These are the exact property names the inspector must read via `Material.HasProperty` + `Material.GetTexture`/`GetFloat`/`GetColor`. Read from `Library/PackageCache/com.unity.render-pipelines.universal@c7abd84d7030/Shaders/Lit.shader` and `LitInput.hlsl`.

#### URP Lit (`Universal Render Pipeline/Lit`, URP 17.0.4)

| Map/Channel | Property | Notes (VERIFIED from Lit.shader + LitInput.hlsl) |
|-------------|----------|--------------------------------------------------|
| Base color texture | `_BaseMap` (2D) | `[MainTexture]`, labeled "Albedo" |
| Base color scalar | `_BaseColor` (Color) | `[MainColor]`; multiplies the map |
| Normal map | `_BumpMap` (2D) | `[Normal]`, labeled "Normal Map"; `_BumpScale` scales |
| Metallic/smoothness map | `_MetallicGlossMap` (2D) | **R = metallic, A = smoothness** (default) |
| Metallic scalar | `_Metallic` (Range) | used when `_MetallicGlossMap` absent |
| Smoothness scalar | `_Smoothness` (Range) | multiplies the map's A channel |
| **Smoothness source toggle** | **`_SmoothnessTextureChannel` (Float)** | **0 = metallic-map alpha (default), 1 = base-map alpha.** ⚠️ NOT `_SmoothnessSource`. Drives the `_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A` shader keyword |
| AO map | `_OcclusionMap` (2D) | **AO is read from the GREEN (`.g`) channel** (verified `SampleOcclusion` → `.g`) |
| AO strength | `_OcclusionStrength` (Range) | `LerpWhiteTo(occ, strength)` = lerp(1, occ, strength) |
| Emission map | `_EmissionMap` (2D) | `_EMISSION` keyword; `_EmissionColor` (HDR) is the color |
| Emission color | `_EmissionColor` (Color, HDR) | scalar fallback / multiplier |
| Height map | `_ParallaxMap` (2D) | `_Parallax` scale; deferred to v1's "where present" |
| Specular workflow | `_SpecColor` + `_SpecGlossMap` | only when `_WorkflowMode == 0` (specular). `_WorkflowMode == 1` = metallic (default) |
| Alpha/cutoff | `_Cutoff`, `_Surface` (0=opaque/1=transparent), `_AlphaClip` | `_Surface`/`_AlphaClip` are `__surface`/`__clip` hidden floats |
| **Obsolete aliases** | `_MainTex`, `_Color`, `_Glossiness`, `_GlossMapScale`, `_GlossyReflections` | `[HideInInspector]` in URP Lit — do NOT read these for URP Lit; use the real names above |

**D-05 / D-04 correction (research finding):** CONTEXT.md D-04/D-05 name the smoothness-source property `_SmoothnessSource`. The actual URP Lit property is **`_SmoothnessTextureChannel`**. The two source paths are: value `0` → smoothness in `_MetallicGlossMap.a`; value `1` → smoothness in `_BaseMap.a` (keyword `_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A`). The planner must use `_SmoothnessTextureChannel`.

#### Unity Standard (built-in, `Standard` shader) — HIGH confidence, stable since Unity 5

| Map/Channel | Property | Notes |
|-------------|----------|-------|
| Albedo texture | `_MainTex` | labeled "Albedo" |
| Albedo color | `_Color` | multiplier |
| Normal map | `_BumpMap` | `_BumpScale` scales |
| Metallic/smoothness map | `_MetallicGlossMap` | R = metallic, A = smoothness (same convention as URP) |
| Metallic scalar | `_Metallic` | |
| Smoothness scalar | `_Glossiness` | Standard names it `_Glossiness` (not `_Smoothness`); `_GlossMapScale` scales the map |
| AO map | `_OcclusionMap` | AO in GREEN channel (same convention as URP) |
| AO strength | `_OcclusionStrength` | |
| Emission | `_EmissionMap` + `_EmissionColor` | |
| Height | `_ParallaxMap` + `_Parallax` | |
| Alpha cutoff | `_Cutoff` | |

### Compute dispatch + readback (Unity-native, no third-party deps)

| API | Purpose | Key constraint |
|-----|---------|----------------|
| `ComputeShader` (`FindKernel`/`SetTexture`/`SetFloat`/`SetInts`/`Dispatch`) | Run GPU kernels | Editor-time dispatch is direct; no `CommandBuffer`/RenderGraph |
| `RenderTexture` + `enableRandomWrite = true` | Compute write target | sRGB formats not writable; use linear |
| `GraphicsFormat` + `SystemInfo.IsFormatSupported` | Declare precise formats | `R16G16B16A16_SFloat` intermediates, `R8G8B8A8_UNorm` final (D-11) |
| `AsyncGPUReadback.Request` / `RequestIntoNativeArray` | Non-blocking readback | `AsyncGPUReadbackRequest.WaitForCompletion()` for sync tests; `done`/`hasError`/`GetData<T>()` for coroutines |
| `Graphics.Blit` | Simple copy / upload into compute-readable intermediate | For source textures that are not `enableRandomWrite`-able |
| `SystemInfo.supportsComputeShaders` / `supportsAsyncGPUReadback` | Capability gates | Skip-with-report when false (D-15) |

### Source selection + asset traversal (editor APIs)

| API | Purpose |
|-----|---------|
| `Selection.objects` / `activeGameObject` / `gameObjects` | Resolve scene selection (GameObject) |
| `AssetDatabase.LoadAssetAtPath<T>(path)` | Load a material/prefab/GameObject at a path |
| `AssetDatabase.LoadAllAssetsAtPath<T>(path)` | Load all sub-assets of type T from an FBX/model asset (materials, meshes) — note "main object is not guaranteed at index 0" |
| `AssetDatabase.FindAssets("t:Material", new[]{folder})` | Recursive folder search by type; `t:Model` for FBX/model assets, `t:Prefab` for prefabs |
| `AssetDatabase.GUIDToAssetPath` | Convert FindAssets GUID → path |
| `PrefabUtility.LoadPrefabContents(path)` | Load a prefab asset's contents into a temp scene for `GetComponentsInChildren<MeshRenderer>`; must be followed by `PrefabUtility.UnloadPrefabContents` |
| `ModelImporter` | FBX/model importer metadata; material binding via sub-assets (see §Architecture Patterns) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `_SmoothnessTextureChannel` (real URP property) | `_SmoothnessSource` (as named in D-04) | The latter does not exist in URP Lit 17.0.4 — using it silently fails and falls to default |
| Staged kernels (D-10) | One fused normalize+encode+pack kernel | Fused is harder to golden-test per-stage; staging gives per-stage GPU-vs-CPU verification and reusable intermediates |
| `AsyncGPUReadback` (D-13) | Sync `ReadPixels`/`GetPixels` | Sync stalls the editor at 2K/4K; forbidden by the no-stall pitfall |
| `R16G16B16A16_SFloat` intermediates | `RGBAFloat` | Half-float is the documented HDR residual choice and half the memory of float32 |
| Direct `SetTexture` of source Texture2D | `Graphics.Blit` into an intermediate RT | Source textures are usually not `enableRandomWrite`; Blit into a linear intermediate gives compute a clean, correctly-typed input |

## Package Legitimacy Audit

This phase installs **zero new external packages**. All dependencies are Unity-managed packages already declared and resolved in the project:

| Package | Registry | Status | Disposition |
|---------|----------|--------|-------------|
| `com.unity.render-pipelines.universal` 17.0.4 | Unity Package Manager | Installed (verified in `Library/PackageCache`, `Packages/manifest.json`) | Approved — already in use |
| `com.unity.mathematics` 1.3.2 | Unity Package Manager | Declared in package `package.json` | Approved — already in use (Phase 1) |
| `com.unity.burst` 1.8.0 | Unity Package Manager | Declared | Approved — not exercised this phase |
| `com.unity.collections` 2.5.0 | Unity Package Manager | Declared | Approved — not exercised this phase |
| `com.unity.test-framework` 1.6.0 | Unity Package Manager | Installed (verified in `Packages/manifest.json`) | Approved — test runner |

No npm/PyPI/crates packages are introduced, so the slopcheck gate is N/A. Unity package names (`com.unity.*`) are resolved by Unity's own Package Manager, not a public package registry, and these specific versions are already locked in the repo (not newly chosen this phase).

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

*Note:* The installed `com.unity.test-framework` is **1.6.0** (from `Packages/manifest.json`), higher than the "1.4.x" noted in Phase 1's STACK research. No action needed — 1.6.0 is what the editor resolves, and the Phase 1 test suite already runs green on it.

## Architecture Patterns

### System Architecture Diagram

```
                       ┌────────────────────────────────────────────────────────────┐
                       │                SOURCE (READ-ONLY, never mutated)            │
                       │  scene GameObject / prefab asset / FBX/model asset /        │
                       │  material / folder                                          │
                       └──────────────┬─────────────────────────────────────────────┘
                                      │ Selection / AssetDatabase / PrefabUtility
                                      ▼
        ┌─────────────────────────────────────────────────────────────────────────┐
        │  SourceInspector (Editor, CPU)                                            │
        │   1. resolve selection → unique materials (dedupe by instance ID)         │
        │   2. per material: property-table lookup (URP Lit → Standard → best-effort)│
        │   3. emit NAMERSourceModel: per-map {Texture2D, sRGB flag, channel} +      │
        │      scalar fallbacks + warnings (never throws on missing data)           │
        └──────────────┬──────────────────────────────────────────────────────────┘
                       │ immutable model (map refs + sRGB flags + scalars)
                       ▼
   ┌───────────────────────────────────────────────────────────────────────────────┐
   │  GPU COMPUTE PIPELINE (Editor, GPU)   — one ComputeTexturePool leased RTs       │
   │                                                                                │
   │  [upload]  source Texture2D ──Graphics.Blit──▶ linear R16G16B16A16_SFloat RT    │
   │                                                                                │
   │  kernel 1: CSNormalize   ──▶ normalized base color (sRGB→linear, AO un-multiply)│
   │                               + normalized normal (raw DirectX texel)          │
   │  kernel 2: CSOctahedralEncode ──▶ octahedral RG                                │
   │  kernel 3: CSSurfacePack  ──▶ packed surface R8G8B8A8_UNorm (linear)            │
   │                                                                                │
   │  (each kernel mirrors Core/NamerFormat via shared HLSL encode include)          │
   └──────────────┬────────────────────────────────────────────────────────────────┘
                  │ AsyncGPUReadback (D-13)
                  ▼
   ┌───────────────────────────────────────────────────────────────────────────────┐
   │  VERIFICATION (TEST-04, Editor test)                                            │
   │  GPU output  vs  Core/NamerFormat CPU reference:                                │
   │    · packed alpha byte: EXACT (integer packing deterministic)                   │
   │    · octahedral R/G + AO: within 1/255                                          │
   │    · decoded-normal dot: ≥ 1 − 1e-3                                             │
   └───────────────────────────────────────────────────────────────────────────────┘
```

Outputs are in-memory `RenderTexture`s (final formats `R8G8B8A8_UNorm` packed / linear base color) — Phase 3's `AssetGenerator` consumes them later. This phase writes **no** assets.

### Recommended Project Structure

```
Packages/com.graffitientertainment.namer/
├── Core/                                  # unchanged single source of truth (D-16 tiny rename)
│   └── NamerFormat.cs                     # PackSurface(float3 normalTexel, …)  ← D-16 rename
├── Compute/
│   ├── NAMERPack.compute                  # D-10: three staged kernels + shared include
│   └── NamerEncode.hlsl                   # NEW shared include — mirrors NamerFormat encode
├── Editor/
│   ├── Pipeline/
│   │   ├── SourceInspector.cs             # NEW — D-01 C# API + Tools > NAMER > Inspect Selection
│   │   ├── NamerSourceModel.cs            # NEW — serializable inspection result (map refs + flags)
│   │   ├── NamerComputePipeline.cs        # NEW — dispatch harness (kernel wrappers)
│   │   └── ComputeTexturePool.cs          # NEW — D-12 RT lease/reuse
│   └── (existing NamerSmokeSetup.cs unchanged)
└── Tests/Editor/
    ├── SourceInspectorTests.cs            # NEW — INSP-01..04 (no GPU needed)
    └── GpuGoldenTests.cs                  # NEW — TEST-04 (capability-gated)
```

### Pattern 1: Per-shader property-convention table (D-04)

**What:** Map discovery is a data table keyed by shader name → list of `(propertyName, channel, role)`, read via `Material.HasProperty` then `GetTexture`/`GetFloat`. URP Lit first, Standard second, then a best-effort generic scan (D-06). No filename heuristics (v1).

**When to use:** The entire `SourceInspector` map-discovery path.

**Key channel facts (must be encoded in the table):**
- URP Lit `_MetallicGlossMap`: metallic = R, smoothness = A (or `_BaseMap.a` when `_SmoothnessTextureChannel == 1`).
- URP Lit `_OcclusionMap`: AO = G (green).
- Unity Standard `_OcclusionMap`: AO = G (green).
- `_BumpMap`/`_MainTex`/`_EmissionMap`: RGB (sampled as-is).

### Pattern 2: Source selection resolution (INSP-01)

**What:** A single entry point normalizes every selection kind into a deduplicated material set. The inspection unit is the unique material (D-03).

**When to use:** `SourceInspector.Inspect(UnityEngine.Object selection)`.

```csharp
// Resolve a selection object into unique materials (dedupe by instance ID — D-02/D-03).
// Source: Unity ScriptReference (AssetDatabase.LoadAllAssetsAtPath, Selection, PrefabUtility) — HIGH
static IEnumerable<Material> ResolveMaterials(Object selection)
{
    if (selection is Material mat) { yield return mat; yield break; }

    if (selection is GameObject go)                       // scene object or prefab root
    {
        foreach (var r in go.GetComponentsInChildren<Renderer>(true))
            foreach (var m in r.sharedMaterials)
                if (m != null) yield return m;
        yield break;
    }

    string path = AssetDatabase.GetAssetPath(selection);
    if (selection is DefaultAsset && AssetDatabase.IsValidFolder(path))   // folder (D-02)
    {
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { path }))
            yield return AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
        // also t:Model for FBX sub-asset materials (see below)
        yield break;
    }

    if (selection is GameObject prefabAsset)              // prefab asset (not a scene instance)
    {
        GameObject contents = PrefabUtility.LoadPrefabContents(path);
        try { /* GetComponentsInChildren<Renderer>(true).sharedMaterials */ }
        finally { PrefabUtility.UnloadPrefabContents(contents); }
        yield break;
    }

    // FBX / model asset: materials are sub-assets, not components.
    foreach (var m in AssetDatabase.LoadAllAssetsAtPath<Material>(path)) yield return m;
}
```

**FBX/model nuance (verified):** a raw model asset has **no `MeshRenderer` component** — materials are sub-assets returned by `AssetDatabase.LoadAllAssetsAtPath<Material>(fbxPath)`, and meshes by `LoadAllAssetsAtPath<Mesh>`. Renderer→material binding only exists in prefab/scene instances. For Phase 2's inspection purpose (locate all present maps/scalars), enumerating the model asset's material sub-assets is sufficient.

### Pattern 3: Staged compute kernels with a shared encode include (D-10)

**What:** Three kernels (`CSNormalize`, `CSOctahedralEncode`, `CSSurfacePack`) in `NAMERPack.compute`, each a thin per-texel function, all `#include`ing a new `NamerEncode.hlsl` that mirrors `NamerFormat` line-for-line (exactly as `NamerSurface.hlsl` already mirrors the decode side).

**When to use:** Every per-pixel GPU stage. Staging (not fusing) makes each kernel independently golden-testable against its Core counterpart.

```hlsl
// Compute/NAMERPack.compute — [numthreads(8,8,1)] (64 threads, safe on every backend)
#pragma kernel CSNormalize
#pragma kernel CSOctahedralEncode
#pragma kernel CSSurfacePack

#include "NamerEncode.hlsl"   // mirrors Core/NamerFormat + NamerConstants

RWTexture2D<float4> _BaseColorIn;   // R16G16B16A16_SFloat, linear
RWTexture2D<float4> _BaseColorOut;
RWTexture2D<float4> _NormalTexel;   // raw DirectX [0,1] texel
RWTexture2D<float4> _Octahedral;
RWTexture2D<float4> _SurfaceOut;    // R8G8B8A8_UNorm, linear
float _AoUnmultiplyStrength;
int2 _Size;

[numthreads(8, 8, 1)]
void CSSurfacePack(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) return;
    float2 oct = _Octahedral[id.xy].rg;
    float  ao  = _Octahedral[id.xy].b;    // or a dedicated AO input per stage design
    // … pack via NamerEncode.hlsl helpers, mirroring NamerFormat.PackSurface EXACTLY …
    _SurfaceOut[id.xy] = NamerPackSurface(oct, ao, metallic, emissive, roughness);
}
```

### Pattern 4: GPU golden test (TEST-04, D-14/D-15)

**What:** An EditMode `[UnityTest]` dispatches a kernel over a known input texture, reads back via `AsyncGPUReadback`, and compares to the Core CPU reference with D-14's tolerances. Gated on `SystemInfo.supportsComputeShaders` + `supportsAsyncGPUReadback`; on a missing backend it **skips with an explicit report entry** (never a silent pass).

**When to use:** Every kernel. This is the format-correctness gate, per Pitfall #9 (never rely on eyeballing a render).

```csharp
[UnityTest]
public IEnumerator SurfacePack_KernelMatchesCoreReference()
{
    if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
    {
        Assert.Ignore("[NAMER] Compute/async-readback unavailable on this backend — "
                    + "skipping GPU golden test (D-15: Metal is the verified target).");
        yield break;
    }

    // upload deterministic input (e.g. a 1x1 or small grid of known normal/AO/metallic/… texels)
    // dispatch CSSurfacePack
    var req = AsyncGPUReadback.Request(outputRT, 0, TextureFormat.RGBA32);
    yield return new WaitUntil(() => req.done);
    Assert.IsFalse(req.hasError, "AsyncGPUReadback reported an error");
    var data = req.GetData<Color32>();

    // D-14: packed alpha byte EXACT; octahedral R/G + AO within 1/255
}
```

**Synchronous alternative for tiny one-shot cases (verified):** `req.WaitForCompletion()` blocks until the request finishes (confirmed to exist on `AsyncGPUReadbackRequest`). Prefer the `[UnityTest]` + `WaitUntil(() => req.done)` form for the golden tests — it does not assume a frame completes on a hidden schedule and works in headless EditMode where the editor still pumps frames.

### Anti-Patterns to Avoid

- **Reading `_SmoothnessSource`:** does not exist in URP Lit 17.0.4 — use `_SmoothnessTextureChannel`.
- **Reading URP AO from the R channel:** URP Lit stores AO in the **G** channel of `_OcclusionMap`.
- **Per-pixel C# `GetPixels`/`SetPixels` loops:** forbidden by NORM-03; use compute.
- **Writing sRGB `RenderTexture` from compute:** not writable; compute into linear `R8G8B8A8_UNorm`.
- **Flipping `TextureImporter.isReadable`/`sRGBTexture` on a source asset to make it readable:** violates source immutability — read via `Graphics.Blit` into a temporary and record (never mutate) the source flags.
- **Assuming compute `Load()`/sampling auto-linearizes sRGB inputs:** it returns raw stored values; convert explicitly.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Per-pixel texture math (normalize, octahedral, pack) | C# `GetPixels` loops | `ComputeShader` kernels (`CSNormalize`/`CSOctahedralEncode`/`CSSurfacePack`) | 2K/4K = millions of texels; C# loops are forbidden by the PRD |
| Octahedral encode + alpha-bit pack in HLSL | Re-derive the math in the kernel | `NamerEncode.hlsl` mirroring `Core/NamerFormat` + `NamerConstants` | Single source of truth prevents bit-level drift (Pitfall #3/#9) |
| sRGB↔linear conversion math | Inline hand-written gamma | `LinearToSRGB`/`SRGBToLinear` HLSL helpers (or Core equivalent) driven by `GraphicsFormatUtility.IsSRGBFormat` | The sRGB curve has a precise spec; ad-hoc approximations shift colors |
| RenderTexture lifecycle | `new RenderTexture(...)` per stage | `ComputeTexturePool` lease/release (D-12) | Editor GPU leak over batches (Pitfall #8) |
| Blocking readback in the pipeline | Sync `ReadPixels`/`GetPixels` | `AsyncGPUReadback` + `WaitAllRequests`/`WaitForCompletion` | Editor stall at 2K/4K (Pitfall #2) |
| AO un-multiply divisor | Raw `albedo / AO` (div-by-zero) | `albedo / max(AO, ε)` with `NamerConstants.Epsilon` | Mirrors Core's epsilon-guard style; avoids NaN at AO=0 |
| Folder material enumeration | Recursive `DirectoryInfo` over the file system | `AssetDatabase.FindAssets("t:Material", new[]{folder})` | Searches the AssetDatabase (post-import state, GUID-accurate, subfolder-recursive) |

**Key insight:** The entire format contract (octahedral encode, alpha bit packing, roughness quantization) already exists once in `Core/NamerFormat.cs`. The GPU kernels must be a *mirror of that same math*, not a re-implementation — and the golden tests are the mechanism that proves the mirror is faithful.

## Common Pitfalls

### Pitfall 1: sRGB↔linear mismatch in compute (NORM-01/NORM-02 correctness)
**What goes wrong:** Base color comes out washed-out or crushed; packed surface data is corrupted.
**Why it happens:** Compute `Load()`/indexing returns **raw stored texel values with no sRGB decode** (unlike fragment-shader sampling). sRGB `RenderTexture`s cannot be written from compute at all.
**How to avoid:** Record each source texture's sRGB-ness via `GraphicsFormatUtility.IsSRGBFormat(tex.graphicsFormat)` (and/or `TextureImporter.sRGBTexture`), then convert explicitly in the kernel: sRGB→linear for base color when the source is authored sRGB; no conversion for normal/AO/metallic/roughness (non-color data). Compute into linear formats only; sRGB-encode at the final sample/save boundary.
**Warning signs:** preview matches in one project color space but not another; packed texture "looks colorful" (banding in normals).

### Pitfall 2: Wrong map channel (metallic R vs AO G vs smoothness A)
**What goes wrong:** NAMER output looks subtly wrong — AO baked from the wrong channel, smoothness read from a channel that's actually a mask.
**Why it happens:** Unity's PBR convention packs different channels per map: metallic=R, smoothness=A, AO=G. Developers read the "obvious" R channel for AO and get garbage.
**How to avoid:** Encode channel selection in the property table (not ad-hoc in the kernel). `_MetallicGlossMap`: R metallic / A smoothness; `_OcclusionMap`: **G**; smoothness source toggle is `_SmoothnessTextureChannel` (0=metallic A, 1=base A).
**Warning signs:** AO that looks like a metallic mask; smoothness that tracks the wrong map.

### Pitfall 3: Kernel↔Core drift (TEST-04 failure mode)
**What goes wrong:** The GPU kernel and the C# reference disagree on a boundary value (e.g. roughness `floor(r·63)` vs `round`, or a strict `>` threshold), producing subtly wrong packed data that passes visual review.
**Why it happens:** Two implementations of the same math without a shared artifact; a single off-by-one is invisible to the eye.
**How to avoid:** Shared `NamerEncode.hlsl` mirroring `NamerFormat` line-for-line + golden tests with D-14's exact tolerance (alpha byte EXACT, oct/AO within 1/255). Never hand-tune the kernel to "look right."
**Warning signs:** golden tests only pass at some inputs; "verified by looking at it."

### Pitfall 4: Flaky frame-dependent GPU tests
**What goes wrong:** A `[UnityTest]` that yields a fixed `WaitForSeconds` or a single `yield return null` before reading back — passes/fails nondeterministically depending on how many frames the readback took.
**Why it happens:** `AsyncGPUReadback` completes "a few frames later," which is not a fixed count.
**How to avoid:** `yield return new WaitUntil(() => req.done)` and assert `!req.hasError` before `GetData`. For tiny one-shot synchronous reads use `req.WaitForCompletion()`. Gate the whole test on `SystemInfo.supportsComputeShaders`/`supportsAsyncGPUReadback` and skip-with-report when absent.
**Warning signs:** "works when I run it alone, fails in the suite"; intermittent readback failures in CI.

### Pitfall 5: Modifying source importer settings to enable reads
**What goes wrong:** Source `.meta`/importer settings change; the artist's asset is silently re-imported or corrupted.
**Why it happens:** `ReadPixels`/`GetRawTextureData` require `isReadable`, tempting a `TextureImporter` flip on the source.
**How to avoid:** Read source pixels via `Graphics.Blit` into a temporary linear `RenderTexture` (never toggling source `isReadable`/`sRGBTexture`); record the source's sRGB flag read-only. This phase writes no assets, so `SetDirty`/`SaveAssets` on sources must never appear at all.
**Warning signs:** Git shows `*.meta` diffs; source texture appears "readable" after inspection.

### Pitfall 6: Editor GPU resource leak (ComputeTexturePool)
**What goes wrong:** Repeated inspection/processing allocates `RenderTexture`s never released; GPU memory climbs.
**Why it happens:** `RenderTexture` pins native GPU memory; GC doesn't reclaim it reliably.
**How to avoid:** `ComputeTexturePool` is the only allocator (D-12) — lease by descriptor, release in `finally`/`IDisposable`; assert live-RT count returns to baseline after a batch.

## Code Examples

Verified patterns from official/installed sources.

### sRGB detection + explicit conversion (NORM-01/NORM-02)
```csharp
// Source: GraphicsFormatUtility.IsSRGBFormat (UnityEngine.Experimental.Rendering) — VERIFIED signature
using UnityEngine.Experimental.Rendering;

bool sourceIsSrgb = GraphicsFormatUtility.IsSRGBFormat(sourceTexture.graphicsFormat);
// → pass this flag to the kernel; it drives SRGBToLinear for base color only.
```
```hlsl
// In the normalize kernel: base color is the ONLY channel that gets color-space treatment.
float3 baseLinear = _SourceIsSrgb ? SRGBToLinear(baseRaw.rgb) : baseRaw.rgb;
// normal / AO / metallic / roughness are non-color data — never converted.
```

### AO un-multiply (mirrors Blender reference + Core epsilon guard — D-08/D-09)
```hlsl
// Blender reference: albedo ÷ AO (AO from the AO map's R channel, clipped ≥ 0.1).
// Unity mirrors it with Core's epsilon guard and a strength parameter.
float ao = max(aoMap.g, NamerEpsilon);            // AO from G channel (URP convention)
float3 cleaned = baseLinear / lerp(1.0, ao, _AoUnmultiplyStrength);
```

### EditMode GPU readback test (TEST-04 — D-13/D-14/D-15)
```csharp
[UnityTest]
public IEnumerator PackKernelMatchesCore()
{
    if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
    {
        Assert.Ignore("compute/readback unavailable — skipping (D-15 target is Metal)");
        yield break;
    }
    // … upload deterministic inputs, dispatch …
    var req = AsyncGPUReadback.Request(outputRT, 0, TextureFormat.RGBA32);
    yield return new WaitUntil(() => req.done);      // NOT a fixed frame count
    Assert.IsFalse(req.hasError);
    var px = req.GetData<Color32>();
    // D-14: alpha byte exact; oct/AO within 1/255; decoded dot ≥ 1 − 1e-3
}
```

### Dispatch sizing
```csharp
// [numthreads(8,8,1)] = 64 threads/group; ceil to cover the texture.
shader.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Legacy `RenderTextureFormat` enum | `GraphicsFormat` + `GraphicsFormatUtility` | Unity 2019.3+ / mandatory in Unity 6 | Phase 2 must use `GraphicsFormat` for all RT declarations |
| `Texture2D.ReadPixels`/`GetPixels` sync readback | `AsyncGPUReadback` + `WaitAllRequests`/`WaitForCompletion` | Unity 2018.1+ | Non-blocking editor readback (D-13) |
| `CommandBuffer`/RenderGraph for offscreen work | Direct `ComputeShader.Dispatch` | Unity 6 (editor offscreen) | RenderGraph is for in-pipeline rendering, not editor processing |
| `_SmoothnessSource` (misnamed in CONTEXT) | `_SmoothnessTextureChannel` | — (always) | Property table correctness |
| Unity test-framework 1.4.x (Phase 1 STACK note) | 1.6.0 (actually installed) | Unity 6 editor resolve | No action; Phase 1 suite already green on 1.6.0 |

**Deprecated/outdated:**
- `RenderTextureFormat.DepthAuto`/`ShadowAuto` — already obsolete in Unity 6; do not use.
- `_MainTex`/`_Color`/`_Glossiness` as URP Lit property names — these are `[HideInInspector]` obsolete aliases in URP Lit 17.0.4; read `_BaseMap`/`_BaseColor`/`_Smoothness` instead (the aliases remain valid only for the built-in `Standard` shader).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Unity Standard built-in shader property names (`_MainTex`, `_MetallicGlossMap`, `_Glossiness`, `_OcclusionMap` G-channel, `_BumpMap`, `_EmissionMap`) — from training knowledge, not read from the built-in shader source this session | Standard Stack | Low — stable since Unity 5 and URP 17.0.4 confirms the identical channel conventions; but the planner should not rely on Standard-only maps without a quick `Material.HasProperty` guard, which the table design already does |
| A2 | Metal max threads/threadgroup = 1024 on Apple silicon and threadgroup memory = 32 KB — Apple's JS-rendered docs and the Metal PDF were not machine-readable this session | Common Pitfalls | Low — the dev machine is Apple silicon and the kernels use `[numthreads(8,8,1)]`=64 threads and zero groupshared, so the 1024/32KB figures are far above what's needed; even the older 512/16KB Apple-family numbers would not bind |
| A3 | Vulkan minimum-guarantee `maxComputeSharedMemorySize` = 16 KB and `maxComputeWorkGroupInvocations` = 128 — Vulkan spec returned 403 this session | Common Pitfalls | Low — Vulkan is explicitly v2 (D-15); kernels use zero groupshared |
| A4 | DX11 groupshared = 32 KB (D3D11 `TGSM` = 8192 regs × 4 B) — from training, not read from a Microsoft page this session | Common Pitfalls | Low — DX11 not exercised this phase; zero groupshared used |
| A5 | `AsyncGPUReadback` works in EditMode (non-PlayMode) tests as long as a GPU context exists | Code Examples | Medium — if EditMode readback behaves differently than PlayMode, the golden tests must move to a `[UnityTest]` PlayMode pattern or use `WaitForCompletion`; flag for the first kernel test to confirm early |

## Open Questions (RESOLVED)

1. **Should the inspector bake `_OcclusionStrength` into the NAMER AO, or take the raw AO map value?**
   - What we know: URP Lit `SampleOcclusion` returns `LerpWhiteTo(occ, _OcclusionStrength)` = lerp(1, occ, strength); the authored AO map is the `.g` channel. NAMER stores a single AO value in the surface B channel.
   - What's unclear: whether "AO" means the authored map or the strength-blended result.
   - Recommendation: take the **raw AO map value** and let the NAMER runtime `_OcclusionStrength` control the blend (consistent with the existing `NamerSurface.hlsl` which applies `_OcclusionStrength` at decode). Leave this as Claude's discretion, but document the choice in the model so Phase 3's material does not double-apply the strength.
   - **Resolution (plans 02-01/02-02):** use the **raw AO map value** (the `_OcclusionMap.g` texel) as the NAMER AO; the runtime `_OcclusionStrength` blend stays at decode in `NamerSurface.hlsl`; `OcclusionStrength` is recorded as metadata for Phase 3. The cleaning strength is a separate `AoUnmultiplyStrength` model field (default 1.0), distinct from `_OcclusionStrength`.

2. **Are GPU golden fixtures committed as assets/JSON?**
   - What we know: D-14 defines comparison semantics; fixtures are Claude's discretion with researcher input.
   - Recommendation: keep the golden fixtures **procedural** (a small deterministic input texture built in the test, compared against `NamerFormat` computed in-memory) rather than committing binary assets — avoids GUID churn and keeps the oracle in Core. If byte-exact fixtures are wanted, commit a tiny JSON of packed expected values (the Phase 1 golden-vector pattern already uses inline `[TestCase]` values, which is sufficient).
   - **Resolution (plan 02-03):** golden fixtures are **procedural** — deterministic inputs built in-test, compared against `NamerFormat` computed in-memory (Phase-1 inline golden-vector style). No committed binary/JSON assets.

3. **Does EditMode `AsyncGPUReadback` behave identically to PlayMode?**
   - What we know: `WaitForCompletion` exists; `WaitUntil(() => req.done)` is the standard async pattern. STATE.md notes the Phase 1 PlayMode smoke test was blocked by the interactive editor holding the project lock (headless `-batchmode` cannot run while the editor is open).
   - What's unclear: whether the golden tests run cleanly in EditMode vs needing PlayMode.
   - Recommendation: land `SourceInspectorTests` (no GPU) first to establish the EditMode harness, then the first GPU golden test as a spike to confirm EditMode readback — if it flakes, switch golden tests to PlayMode `[UnityTest]` and keep the CPU `SourceInspector` coverage in EditMode.
   - **Resolution (plan 02-03 contingency):** `WaitForCompletion` is the robust EditMode readback path for tiny 1x1 reads; `WaitUntil(() => req.done)` for multi-texel reads. If EditMode readback errors/flakes in `-batchmode`, switch the GPU tests to PlayMode `[UnityTest]` and keep CPU `SourceInspector` coverage in EditMode. (Assumption A5 remains the one genuinely-open item.)

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Unity Editor | All of Phase 2 | ✓ | 6000.0.82f1 (verified `ProjectVersion.txt`) | — |
| URP package | property tables + shader | ✓ | 17.0.4 (verified `Library/PackageCache`) | — |
| GPU with compute + async readback | GPU pipeline + golden tests | Assumed ✓ (dev machine is macOS/Metal) | Metal on Apple silicon | Capability-gated skip-with-report (D-15) |
| `com.unity.test-framework` | tests | ✓ | 1.6.0 (verified `Packages/manifest.json`) | — |

**Missing dependencies with no fallback:**
- None. (The only runtime-gated dependency — a compute-capable GPU — is on the dev machine and already has a defined skip-with-report fallback, which is the D-15 behavior, not a blocker.)

**Missing dependencies with fallback:**
- A D3D/Vulkan test machine — explicitly v2 (D-15); fallback is Metal-only verification this phase.

## Security Domain

*(Editor tool — "security" maps to asset-integrity and resource-safety, not a network attack surface.)*

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | — |
| V3 Session Management | no | — |
| V4 Access Control | no | — |
| V5 Input Validation | yes (selection/asset inputs) | Defensive parsing: `Material.HasProperty` before `GetTexture`/`GetFloat`; null/unsupported selections produce warnings, never exceptions (D-02/D-06) |
| V6 Cryptography | no | — |

### Known Threat Patterns for editor asset processing

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Source asset mutation via importer-flag flip (make readable, toggle sRGB) | Tampering / Info Disclosure | Read-only source discipline; `Graphics.Blit` into a temporary; record (never set) source flags; immutability test |
| GPU resource exhaustion from untrusted/pathological art inputs | DoS | `ComputeTexturePool` budgets + leak watchdog; bounded dispatch sizing |
| Unintended write outside generated-assets dir | Tampering | No `SetDirty`/`SaveAssets` in Phase 2 at all (in-memory only); Phase 3 introduces the single write path |
| Shipping editor-only code in Runtime assembly | Info Disclosure / Integrity | Strict Editor/Runtime/Core asmdef split (already established) |

## Sources

### Primary (HIGH confidence)
- Installed URP 17.0.4 source — `Library/PackageCache/com.unity.render-pipelines.universal@c7abd84d7030/Shaders/Lit.shader` + `LitInput.hlsl` — authoritative URP Lit property names, `_SmoothnessTextureChannel`, metallic-R/smoothness-A, AO-G channel, `_OcclusionStrength` lerp semantics. Read directly.
- `GraffitiEntertainment/BlenderNamerPlugin` (GitHub, `develop` branch) — `texture_operators.py`, `ao_operators.py` (`remove_ao_from_texture`), `normal_operators.py` (`remove_normal_lighting`, `invert_normal_y_channel`), `namer_core.py` (`generate_texture_preview`), `properties.py` (`guess_texture_role`). Read directly via `gh api`. D-09 finding: AO un-multiply = `albedo ÷ AO`; delighting is a crude `albedo ÷ max(normal.z, 0.1)` (not iterative); normal map uses a DirectX green flip; filename heuristics exist but are deferred in Unity.
- Microsoft Learn — `numthreads` attribute (cs_5_0 max 1024 threads/group, Z ≤ 64) — https://learn.microsoft.com/en-us/windows/win32/direct3dhlsl/sm5-attributes-numthreads
- Unity ScriptReference — `AsyncGPUReadbackRequest` (`WaitForCompletion`, `done`, `hasError`, `GetData`) — https://docs.unity3d.com/ScriptReference/Rendering.AsyncGPUReadbackRequest.html
- Unity ScriptReference — `GraphicsFormatUtility.IsSRGBFormat` (`public static bool IsSRGBFormat(GraphicsFormat)`, `UnityEngine.Experimental.Rendering`) — confirmed via 6000.0 doc URL
- Unity ScriptReference — `AssetDatabase.FindAssets` (`t:Material`/`t:Model` filters, recursive `searchInFolders`) and `LoadAllAssetsAtPath` ("main object not guaranteed at index 0")
- Repo code — `Core/NamerFormat.cs`, `Core/NamerConstants.cs`, `Shaders/NamerSurface.hlsl`, `Compute/NAMERPack.compute` (empty scaffold), `Tests/Editor/*`, `Editor/NamerSmokeSetup.cs`, `package.json`, `Packages/manifest.json`, `ProjectSettings/ProjectVersion.txt` — read directly.

### Secondary (MEDIUM confidence)
- Phase 1 research corpus — `.planning/research/STACK.md`, `ARCHITECTURE.md`, `PITFALLS.md` — authoritative architecture/pitfall context, still valid for Phase 2.
- Unity Standard built-in shader property names — training knowledge (stable since Unity 5); cross-confirmed by URP Lit's identical channel conventions read from source this session (A1).

### Tertiary (LOW confidence)
- Metal `maxTotalThreadsPerThreadgroup` (1024 Apple silicon) and `maxThreadgroupMemoryLength` (32 KB) — Apple's JS-rendered docs and Metal Feature-Set PDF were not machine-readable; figures from training (A2).
- Vulkan minimum-guarantee limits (16 KB shared memory, 128 invocations) — Vulkan spec returned 403 (A3).
- DX11 `groupshared` = 32 KB — training (A4).

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — property tables read from installed URP source; compute APIs verified via ScriptReference/Microsoft Learn; no new packages.
- Architecture: HIGH — patterns reuse the Phase 1 corpus and existing repo conventions; source-resolution API surface verified.
- Pitfalls: HIGH for the sRGB/channel/kernel-drift/editmode-readback pitfalls (directly grounded in the installed source + ScriptReference); MEDIUM for the Metal/Vulkan numeric limits (flagged in Assumptions Log).

**Research date:** 2026-08-26
**Valid until:** 2026-08-30 (URP 17.0.4 property names and Unity 6 APIs are stable; re-verify only if a Unity/URP upgrade lands mid-phase)

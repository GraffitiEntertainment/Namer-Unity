# Phase 3: Asset Generation + Editor Workflow + Preview - Research

**Researched:** 2026-08-27
**Domain:** Unity editor asset-generation (UPM package) — non-destructive texture/material write, import-settings stamping, `PreviewRenderUtility` mesh preview, IMGUI editor window, async readback, EditMode safety tests
**Confidence:** HIGH (every Unity API signature verified against the locally installed Unity `6000.0.82f1` reference assemblies + the open-source `PreviewRenderUtility.cs`; the one external reference — Blender Namer plugin — verified against its public GitHub `develop` branch)

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Default destination is `Assets/NAMERGenerated/` with one subfolder per processed selection, named after the source (`NAMERGenerated/{sourceAssetName}/`). Shared materials are written once per processing run (dedup follows Phase 2 D-03's unique-material unit), not once per renderer.
- **D-02:** Per-material outputs are `{prefix}{materialName}{suffix}_Base.png`, `{prefix}{materialName}{suffix}_Surface.png`, and `{prefix}{materialName}{suffix}.mat`. Defaults: empty prefix, suffix `_Namer`. Prefix/suffix/destination are user-configurable in the processor window.
- **D-03:** Settings persist user-scoped via `EditorPrefs` in v1 (survives sessions, zero asset noise). A shareable/team `NamerProcessorSettings` ScriptableObject is a deferred idea — do not build it now.
- **D-04:** Overwrite is limited to generated assets: only files inside the configured destination folder that carry the NAMER-generated stamp (an asset label, e.g. `NamerGenerated`, applied at write time). Nothing outside the destination folder is ever overwritten, regardless of naming; collisions with non-stamped files inside the folder abort with a clear error rather than clobber.
- **D-05:** Phase 3 generates material + textures only. No mesh generation (Phase 4), no prefab variants, no automatic re-pointing of scene renderers or prefab materials. The preview applies the NAMER material to the actual source mesh in-memory only. Auto-assign/prefab-variant conveniences are deferred ideas.
- **D-06:** Both textures are saved as PNG via `EncodeToPNG` + file write + `AssetDatabase.ImportAsset`. The normalized base color is quantized from the `R16G16B16A16_SFloat` intermediate to 8-bit on readback and imported **sRGB**. The packed surface texture is imported **linear, uncompressed, with point (nearest) filtering and no mipmaps** — bit-packed alpha cannot survive compression, mips, or interpolated sampling (research pitfall #7; PROJECT.md deferred point sampling into this phase).
- **D-07:** The generated Material uses the existing `GraffitiEntertainment.Namer/NAMER` shader and carries the metadata contract already defined by Phase 1/2: `_BaseColor` tint (not baked), `_EmissionColor` + `_EMISSION` keyword when emissive, `_OcclusionStrength`, `_Cutoff` + `_ALPHATEST_ON` when alpha-tested, and transparent blend state (`_Surface`, `_SrcBlend`/`_DstBlend`, `_ZWrite`) when the source was transparent.
- **D-08:** GPU→CPU readback for saving uses the pipeline's existing `AsyncGPUReadback` contract (Phase 2 D-13); no synchronous full-texture reads on 2K/4K outputs.
- **D-09:** Before/after preview renders the **actual selected mesh** twice side-by-side via `PreviewRenderUtility` — original source material (before) vs the NAMER material applied in-memory (after) — with a synced camera so lighting/angle comparisons are meaningful (SHDR-03 spirit). Texture-only 2D inspection may supplement but not replace the mesh preview.
- **D-10:** Interactive updates: control changes trigger a debounced re-dispatch of the compute pipeline and preview re-render (UI-06). Interactive preview NEVER writes to disk — assets are written only by the explicit Process/save action. Debounce interval, staging, and whether intermediate uploads are cached are Claude's discretion as long as no per-pixel C# loops run (NORM-03 carry-in).
- **D-11:** Phase 3 ships exactly the channels derivable from current outputs: reconstructed/normalized base color, AO, decoded normal (from octahedral), roughness, metallic, emissive. They render from the actual generated textures onto the preview mesh. Vertex-color, residual, and reconstruction-error views land with Phase 4 — no stub toggles, no permanently-disabled dead UI.
- **D-12:** Mechanism (keyword-per-channel debug variant on the NAMER shader vs a small editor-only debug shader) is planner's choice with researcher input; either way the decode math must reuse the shared HLSL include pattern so debug views cannot drift from the runtime decode.
- **D-13:** The processor window lives at `Tools > NAMER > Processor`. `Process with NAMER` is reachable from the Project-browser context menu (`Assets/Process with NAMER`) and the hierarchy context menu (`GameObject/Process with NAMER`), plus a Process button in the window — all three route into one shared entry point (validate selection → inspect → process → generate). The Phase 2 `Tools > NAMER/Inspect Selection` validation menu stays.
- **D-14:** The window exposes only controls that are functional in this phase: source selection display + inspection summary, AO un-multiply strength, destination/prefix/suffix, overwrite-generated toggle, preview + debug channel picker, Process. Phase 4/5 controls appear when their phases land — the window layout may anticipate sections, but ships no dead controls. UX polish (spacing, states, visual detail) matters — the user treats UI quality as equal to functional correctness.
- **D-15:** Source-immutability test is an EditMode test that: builds/loads a fixture with real source assets (textures, material, model), hashes every involved source file **and its .meta** (file-content hash; algorithm is discretion) before processing, runs the full Process flow end-to-end, re-hashes after, and asserts byte-identical. Metadata-only or timestamp comparisons are insufficient.
- **D-16:** Path/naming tests assert: every written asset lives under the configured destination, names honor prefix/suffix, and overwrite refuses non-stamped targets (D-04). Tests run against a **temporary destination override** created and cleaned up by the test (including `.meta`), never the user's real `NAMERGenerated/` content.
- **D-17:** "Shippable MVP package" means: `package.json` metadata complete (name, version, unity minimum, displayName, description, author), a package-level `README.md` (install + basic workflow), and the full headless EditMode + PlayMode suite green at HEAD. `Samples~` content, docs site, and Unity package-validation tooling are deferred.

### Claude's Discretion
- Exact class/file names (`AssetGenerator`, processor window, import stamper, debug shader layout).
- Window layout details (pane arrangement, section ordering) beyond D-09/D-14 constraints.
- Debounce interval and dispatch/staging mechanics (D-10).
- Hash algorithm, fixture construction, and test-file hygiene details (D-15/D-16).
- Debug-view mechanism (D-12) and PNG encode/readback plumbing details.

### Deferred Ideas (OUT OF SCOPE)
- Auto-assign generated material to scene renderers / prefab-variant generation — later convenience phase
- Shared team-settings `NamerProcessorSettings` ScriptableObject (v1 uses EditorPrefs, D-03)
- `Samples~` smoke content inside the UPM package (D-17)
- EXR/HDR base-color export path
- Batch processing over folders (BATCH-01, already v2)
- AssetPostprocessor auto-processing (AUTO-01, already v2)
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| GEN-01 | Generate NAMER material + textures under a dedicated generated-assets directory, source untouched | AssetGenerator write-only stage (§Architecture Patterns); `AssetDatabase.CreateAsset`/`File.WriteAllBytes` + `ImportAsset`; `NAMERGenerated/{source}` layout (D-01/D-02) |
| GEN-02 | Source assets never modified — enforced by test | SHA-256 hash of source files + `.meta` before/after full Process (D-15); read-only-source discipline (§Don't Hand-Roll) |
| GEN-03 | Configurable destination + prefix/suffix; overwrite limited to generated assets | `EditorPrefs` (D-03); `AssetDatabase.SetLabels` stamp + `GetLabels` gate (D-04); overwrite pattern in §Code Examples |
| GEN-04 | Generated packed textures saved with correct linear/uncompressed import settings | `TextureImporter.sRGBTexture=false`, `textureCompression=Uncompressed`, `filterMode=Point`, `mipmapEnabled=false` (§Code Examples, verified signatures) |
| UI-01 | Editor window at `Tools > NAMER > Processor` | `EditorWindow` + `[MenuItem("Tools/NAMER/Processor")]`; existing `Tools/NAMER/…` family (§Code Examples) |
| UI-02 | Explicit `Process with NAMER` command (no auto-import) | `[MenuItem("Assets/Process with NAMER")]` + `[MenuItem("GameObject/Process with NAMER")]` + window button → one shared entry point (D-13) |
| UI-03 | Window exposes processing controls (AO strength + destination/prefix/suffix + overwrite) | D-14 controls only; no Phase 4/5 dead controls |
| UI-04 | Before/after preview of the actual selected mesh | `PreviewRenderUtility` with `Render(true)` (SRP) for URP materials; single synced camera, two `DrawMesh` calls (§Code Examples, verified) |
| UI-05 | Debug channel views (base, AO, normal, roughness, metallic, emissive) | Editor-only debug shader `#include "NamerSurface.hlsl"` + `_DebugChannel` switch (D-12); §Architecture Patterns |
| UI-06 | Preview updates interactively on control change (GPU-favored) | `EditorApplication.update` debounce pump + `AsyncGPUReadback` (§Code Examples); never writes to disk (D-10) |
| TEST-03 | Automated tests for generated asset paths + source immutability | `[UnityTest]` EditMode tests; capability-gated like `GpuGoldenTests`; temp destination (`Assets/NAMER_Tests_Temp`) cleanup (§Testing) |
</phase_requirements>

## Summary

Phase 3 is a **Unity-native, zero-external-dependency** vertical slice. It turns Phase 2's in-memory `NamerComputeResult` (normalized base `R16G16B16A16_SFloat` RT + packed surface `R8G8B8A8_UNorm` RT) into persistent NAMER assets under `NAMERGenerated/{source}/`, stamps correct import settings, and wraps it in an `EditorWindow` with a `PreviewRenderUtility` before/after mesh preview and an `EditorApplication.update`-driven debounced recompute. The entire phase uses Unity Editor APIs only — no npm/pip/cargo packages — so the Package Legitimacy Gate is N/A.

The two highest-risk items are now **resolved at HIGH confidence**. First, the STATE.md blocker (`PreviewRenderUtility` docs 404) is closed by reflection against the installed `UnityEditor.dll` (Unity `6000.0.82f1`) and the open-source `PreviewRenderUtility.cs`: the exact API is `camera`, `cameraFieldOfView`, `ambientColor`, `lights` (pre-populated `Light[]`), `BeginPreview`/`EndPreview`, `AddSingleGO`, 8 `DrawMesh` overloads, and `Render(bool allowScriptableRenderPipeline = false, bool updatefov = true)`; the `Render` method toggles `Unsupported.useScriptableRenderPipeline` before `camera.Render()`, so **URP materials require `Render(true)`** (otherwise they render pink/fallback-error). Second, the Blender reference (`GraffitiEntertainment/BlenderNamerPlugin`, `develop`) was read directly: it sets **`Non-Color` (linear) color space on data maps** (normal/AO/metallic/roughness) — mirroring Unity `sRGBTexture = false` — but does **not** set an interpolation/filter mode (Blender node default is Linear). Point filtering is therefore a **Unity-side correctness requirement** (bit-packed alpha + octahedral normals corrupt under bilinear/mip sampling, PITFALLS.md #7), not a Blender-mirrored convention.

**Primary recommendation:** Implement three plans in dependency order — (03-01) a write-only `AssetGenerator` that reads back `NamerComputeResult` via `AsyncGPUReadback.RequestIntoNativeArray`, writes PNGs with `ImageConversion.EncodeToPNG` + `File.WriteAllBytes` + `ImportAsset` + `TextureImporter` stamping + `SetLabels("NamerGenerated")`, and creates the material; (03-02) an IMGUI `EditorWindow` at `Tools > NAMER > Processor` with a single `PreviewRenderUtility` (`Render(true)`) drawing before/after meshes side-by-side, an editor-only debug shader reusing `NamerSurface.hlsl`, and an `EditorApplication.update` debounce; (03-03) SHA-256 source-immutability + asset-path EditMode tests against a temp destination, plus package `package.json`/`README.md` completion.

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Source inspection | API/Backend (Editor pipeline) | — | `SourceInspector.Inspect` already resolves all 5 selection kinds into deduped `NamerMaterialInspection` units (Phase 2) |
| GPU compute (normalize/pack) | API/Backend (Editor pipeline, GPU) | — | `NamerComputePipeline.Process` + `ComputeTexturePool` (Phase 2); pool-leased RTs, `ReleaseResult` contract |
| Asset generation (write files) | Storage (AssetDatabase) | Editor | The **only** disk writer; `NAMERGenerated/` only; never mutates source paths |
| Import stamping | Storage (AssetImporter) | Editor | `TextureImporter` sets linear/uncompressed/point/no-mips on generated textures |
| Overwrite gating + labels | Storage (AssetDatabase) | Editor | `SetLabels`/`GetLabels` implement the `NamerGenerated` stamp (D-04) |
| Editor window + command surface | Client (EditorWindow + IMGUI) | — | `Tools > NAMER > Processor`, context menus, Process button → one entry point |
| Before/after mesh preview | Client (PreviewRenderUtility) | — | Single preview scene/camera, two `DrawMesh` calls, `Render(true)` for URP |
| Debug channel views | Client (editor-only debug shader) | Backend (shared HLSL decode) | `#include "NamerSurface.hlsl"` + `_DebugChannel` switch — cannot drift from runtime decode |
| Settings persistence | Client (EditorPrefs) | — | D-03: user-scoped, zero asset noise |
| Async recompute pump | Client (EditorApplication.update) | — | Debounced re-dispatch + readback completion → `Repaint()` |
| Source immutability / path tests | Test tier (EditMode) | — | SHA-256 hash + temp-destination hygiene |

## Standard Stack

All APIs are Unity Editor-native (no external packages). Versions verified against the installed Unity `6000.0.82f1` reference assemblies via reflection; nothing here is a registry package.

### Core

| API | Purpose | Key Verified Detail |
|-----|---------|---------------------|
| `UnityEditor.PreviewRenderUtility` | Before/after mesh preview (D-09) | `camera`, `cameraFieldOfView`, `ambientColor`, `lights` (pre-populated `Light[]`); `BeginPreview(Rect, GUIStyle)`, `EndPreview()`, `AddSingleGO(GameObject[, bool])`, `DrawMesh` (8 overloads), `Render(bool allowScriptableRenderPipeline = false, bool updatefov = true)`, `Cleanup()` |
| `UnityEditor.TextureImporter` | Import-settings stamping (GEN-04) | `sRGBTexture`, `textureCompression` (`TextureImporterCompression`), `filterMode` (`FilterMode`), `mipmapEnabled`, `textureType`, `maxTextureSize`, `isReadable`, `alphaIsTransparency`; `SaveAndReimport()` |
| `UnityEditor.AssetImporter.GetAtPath(string)` | Fetch importer to stamp | Returns `AssetImporter` (cast to `TextureImporter`); null until asset first imported |
| `UnityEditor.AssetDatabase` | Asset write/labels/paths | `CreateAsset`, `ImportAsset`, `SetLabels(Object, string[])`, `GetLabels(Object)`, `GenerateUniqueAssetPath`, `CreateFolder`, `IsValidFolder`, `DeleteAsset`, `Refresh`, `GUIDToAssetPath`, `AssetPathToGUID`, `SaveAssets` |
| `UnityEngine.Rendering.AsyncGPUReadback` | Non-blocking GPU→CPU readback (D-08) | `RequestIntoNativeArray<T>(out NativeArray<T>, Texture, int mipIndex, TextureFormat, callback)`; `RequestAsync(...)` → `Awaitable<AsyncGPUReadbackRequest>` (Unity 6); `WaitAllRequests()` |
| `UnityEngine.Rendering.AsyncGPUReadbackRequest` | Readback completion | `done`, `hasError`, `GetData<T>()`, `WaitForCompletion()`, `forcePlayerLoopUpdate` (get/set — key for edit-mode) |
| `UnityEngine.Texture2D` + `UnityEngine.ImageConversion` | Encode PNG from readback | `LoadRawTextureData(NativeArray<byte>)`, `Apply(bool, bool)`, `ImageConversion.EncodeToPNG()`, `EncodeToEXR()` |
| `UnityEditor.EditorWindow` + IMGUI `OnGUI` | Processor window (UI-01) | `GetWindow<T>()`, `Repaint()`, `OnEnable`/`OnDisable`; `[MenuItem]` for command surface |
| `UnityEditor.EditorApplication` | Debounced async pump (UI-06) | `update` and `delayCall` are **fields** of type `EditorApplication.CallbackFunction` (not `event`) — subscribe with `+=` |
| `UnityEditor.EditorPrefs` | Settings persistence (D-03) | `GetString`/`SetString`, `GetBool`/`SetBool` |

### Supporting

| API | Purpose | When to Use |
|-----|---------|-------------|
| `System.IO.File.WriteAllBytes` / `System.IO.File.ReadAllBytes` | Raw PNG write / source hash read | AssetGenerator save path; immutability test |
| `System.Security.Cryptography.SHA256` | File-content hashing (D-15) | Immutability test (hash source files + `.meta`) |
| `UnityEngine.Mesh.bounds` | Preview camera framing | Center/scale the mesh in the preview (discretion) |
| Existing: `NamerComputePipeline` / `SourceInspector` / `NamerSourceModel` / `ComputeTexturePool` / `NamerConstants` / `NAMER.shader` / `NamerSurface.hlsl` | Phase 2 contracts this phase builds on | The AssetGenerator honors `Process` → readback → `ReleaseResult`; the debug shader reuses the HLSL include; `NamerSmokeSetup` is the seed pattern for programmatic asset/material creation |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| IMGUI `OnGUI` window | UI Toolkit `CreateGUI()` + `IMGUIContainer` for the preview | UI Toolkit is Unity's modern recommendation, but `PreviewRenderUtility` is Rect/IMGUI-centric and the existing codebase is 100% IMGUI (`MenuItem`, no UI Toolkit). IMGUI is fastest and consistent; revisit only if window complexity grows |
| `AsyncGPUReadback.Request` + poll `done` | `RequestAsync(...)` `Awaitable` | `RequestAsync` is cleaner but editor `Awaitable` support is newer; poll/callback via `EditorApplication.update` is the established pattern and matches Phase 2's `RequestReadback` contract |
| Editor-only debug shader (D-12) | Keyword-per-channel variant on runtime `NAMER.shader` | Editor-only shader keeps the runtime shader keyword surface clean and can freely output raw channel values; either way it must `#include "NamerSurface.hlsl"` |
| `RenderTexture.active` + `ReadPixels` (sync) | `AsyncGPUReadback` | Sync stalls the editor on 2K/4K (PITFALLS #2); D-08 forbids it for the save path |

**Installation:** None — no external packages. This phase consumes Unity Editor built-in APIs plus the already-imported `com.unity.test-framework` (test asmdef already references it).

**Version verification:** No registry versions to verify. API presence and signatures were confirmed by reflecting the installed `Unity.app/Contents/Managed/UnityEditor.dll`, `UnityEngine.dll`, and `UnityEngine/UnityEngine.ImageConversionModule.dll` from `6000.0.82f1`.

## Package Legitimacy Audit

> **N/A — this phase installs zero external packages.** All APIs are Unity Editor built-ins (`UnityEditor`, `UnityEngine`, `UnityEngine.Rendering`, `UnityEngine.ImageConversion`) or the .NET BCL (`System.IO`, `System.Security.Cryptography`). The `slopcheck` gate is a no-op here. The package `com.graffitientertainment.namer`'s own dependencies (`com.unity.mathematics`, `com.unity.render-pipelines.universal`, `com.unity.burst`, `com.unity.collections`) are already declared in `package.json` and unchanged this phase.

## Project Constraints (from CLAUDE.md)

Relevant, actionable directives from the project `./CLAUDE.md` (the Unity project instructions; the user's global GNUS.AI C++ rules do not apply to this C#/HLSL Unity package):

- **Tech stack:** C# for editor/runtime code, Unity compute shaders (HLSL) for GPU image processing — no native C++ plugins.
- **Render pipeline:** URP first; Unity Standard where available; HDRP only if practical (out of scope this phase).
- **Asset safety:** source assets must never be modified; generated output lives in a separate directory (`NAMERGenerated/`).
- **Performance:** no repeated per-pixel C# loops on large textures; GPU compute for high-resolution work (already satisfied by Phase 2; Phase 3 only reads back final results).
- **Precision:** roughness limited to 64 values (6 bits); vertex colors `Color32` where sufficient (Phase 4).
- **Package:** ship as a reusable UPM package with `Runtime/` + `Editor/` split via asmdefs.
- **Conventions:** constants centralized in `NamerConstants` (no magic numbers); menu items under `Tools/NAMER/…`; hand-written ShaderLab + HLSL (not Shader Graph); `GraphicsFormat` (not legacy `RenderTextureFormat`); `AsyncGPUReadback` (not sync `ReadPixels`) for 2K/4K.
- **Testing:** asmdef-gated EditMode/PlayMode tests; headless invocation **without** `-quit` (STATE.md accumulated decision).

## Architecture Patterns

### System Architecture Diagram

```
                      ┌──────────────────────────────────────────────────────────┐
                      │                  UX LAYER (Editor asmdef)                │
                      │                                                          │
   [Assets/…] ──►  MenuItem "Assets/Process with NAMER"                          │
   [GameObject/…]─► MenuItem "GameObject/Process with NAMER"                     │
                      └──────────────┬───────────────────────────────────────────┘
                                     │  all three route into one entry point
   Tools > NAMER > Processor ──► NAMEREditorWindow  ──Process(...)──┐
        (EditorWindow, IMGUI)      │  OnGUI: controls               │
                                   │  PreviewRenderUtility (Render(true)) ◄─┐
                                   │  EditorApplication.update debounce     │
                                   └──────────────┬──────────────────────┘  │
                                                  │ validate + inspect        │ readback completion → Repaint
                                     ┌────────────▼────────────┐             │
                                     │  NamerProcessor         │             │
                                     │  (shared entry point:   │             │
                                     │   validate → inspect →  │             │
                                     │   process → generate)   │             │
                                     └───┬──────────────┬──────┘             │
                    read-only            │              │  write-only        │
                    ┌────────────────────▼──┐      ┌────▼──────────────────┐ │
                    │  SourceInspector.Inspect│     │  AssetGenerator       │ │
                    │  → NamerSourceModel     │     │  (only disk writer)   │ │
                    └────────────┬───────────┘      │  NAMERGenerated/{src} │ │
                                 │                  └────┬─────────────┬───┘ │
                    ┌────────────▼───────────┐           │             │      │
                    │ NamerComputePipeline    │     File.WriteAllBytes│  CreateAsset
                    │  Process → NamerComputeResult (pool-leased RTs)
                    │  RequestReadback (AsyncGPUReadback)             │      │
                    └────────────┬───────────┘                        │      │
                                 │   AsyncGPUReadbackRequest         │      │
                                 │   → NativeArray<byte>             │      │
                                 │   → Texture2D.LoadRawTextureData  │      │
                                 │   → ImageConversion.EncodeToPNG ──┘      │
                                 │                                            │
                                 ▼                                            │
                     NamerComputeResult → (a) save PNGs + stamp TextureImporter │
                                          (b) build Material (NAMER shader)    │
                                          (c) SetLabels("NamerGenerated")     │
                                                                               │
   Runtime NAMER.shader ◄── (generated .mat references it) ◄───────────────────┘
   NamerSurface.hlsl   ◄── (shared decode; also #include'd by editor debug shader)
```

Trace the primary use case: user selects an FBX → `Process with NAMER` → `SourceInspector.Inspect` (read-only) → `NamerComputePipeline.Process` (GPU) → `AsyncGPUReadback` → `AssetGenerator` writes PNGs + stamps importer + creates material under `NAMERGenerated/` (write-only), while the window's `PreviewRenderUtility` shows before/after in-memory. Source assets are only ever read.

### Recommended Project Structure

```
Packages/com.graffitientertainment.namer/
├── Editor/
│   ├── Generation/
│   │   └── AssetGenerator.cs            # 03-01: write-only; PNG write + TextureImporter stamp + material + labels
│   ├── UI/
│   │   ├── NamerEditorWindow.cs         # 03-02: Tools > NAMER > Processor
│   │   ├── NamerPreviewRenderer.cs      # 03-02: PreviewRenderUtility before/after + debug channel
│   │   └── NamerDebugChannelMaterial.cs # 03-02: editor-only debug material/Shader.PropertyToID cache (discretion)
│   ├── Settings/
│   │   └── NamerProcessorSettings.cs    # 03-01: EditorPrefs-backed destination/prefix/suffix/overwrite (D-03)
│   └── Pipeline/                        # (Phase 2 — unchanged)
├── Shaders/
│   ├── NAMER.shader                     # (Phase 1 — runtime decode, unchanged)
│   ├── NamerSurface.hlsl                # (Phase 1 — shared decode include, unchanged)
│   └── NamerDebugView.shader            # 03-02: editor-only debug channel shader (#include NamerSurface.hlsl)
├── Tests/Editor/
│   ├── AssetGeneratorTests.cs           # 03-03: paths, prefix/suffix, overwrite gating (D-16)
│   └── SourceImmutabilityTests.cs       # 03-03: SHA-256 source + .meta immutability (D-15)
└── package.json / README.md             # 03-03: metadata + install/workflow docs (D-17)
```

### Pattern 1: Write-only AssetGenerator as the sole disk writer

**What:** `AssetGenerator` is the only type that calls `File.WriteAllBytes`/`AssetDatabase.CreateAsset`. It takes a `NamerComputeResult` (pool-leased RTs) + a `NamerMaterialInspection`, reads back via `AsyncGPUReadback`, writes, stamps, labels, and returns written paths. Source paths are never passed to any write API.

**When to use:** End of every `Process` run; the preview path never calls it (D-10).

**Lifecycle contract it must honor (from `NamerComputePipeline`):** read back both RTs, then `ReleaseResult(result)`; a batch of N materials must not accumulate live targets (leak-watchdog contract from 02-03).

### Pattern 2: Import-then-stamp for generated textures

**What:** For a brand-new PNG there is no importer until first import, so the order is: write bytes → `ImportAsset` (creates `.meta` + importer with **defaults**) → fetch importer → stamp settings → `SaveAndReimport()`. This touches only the new generated file, never a source asset.

**When to use:** Every generated texture write (base + packed surface).

### Pattern 3: Editor-only debug shader reusing the shared HLSL include (D-12)

**What:** `NamerDebugView.shader` is a small Unlit editor shader that `#include "NamerSurface.hlsl"`, samples `_SurfaceMap` + `_BaseResidualMap`, calls `NAMER_DECODE_SURFACE`, and outputs a `_DebugChannel` selection (0 = base, 1 = AO, 2 = decoded normal, 3 = roughness, 4 = metallic, 5 = emissive). This keeps the runtime `NAMER.shader` keyword surface clean and guarantees the debug views cannot drift from runtime decode.

**When to use:** The preview's debug-channel picker (D-11). Recommended over keyword-per-channel runtime variants because it ships no extra runtime keywords and can display raw channel values directly.

### Anti-Patterns to Avoid
- **Writing to source paths / mutating source `TextureImporter`:** violates the core safety guarantee (PITFALLS #11). The AssetGenerator is write-only into `NAMERGenerated/`; source importer flags are read-only.
- **Sync `ReadPixels`/`GetPixels` on 2K/4K outputs:** editor stalls (PITFALLS #2); D-08 mandates `AsyncGPUReadback`.
- **Letting the packed surface texture default-import (compressed/sRGB/mipmapped):** silently corrupts packed bits (PITFALLS #7). Always stamp.
- **`preview.Render()` without `true`:** renders URP materials pink (fallback-error) because `Render(bool allowScriptableRenderPipeline)` defaults to `false` and the built-in-pipeline preview camera cannot run URP passes.
- **Interactive preview writing to disk:** D-10 — only the explicit Process action writes.
- **Dead Phase 4/5 controls in the window:** D-14 — ship only functional controls.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Mesh preview rendering | A custom preview scene/camera rig | `PreviewRenderUtility` (`Render(true)` for URP) | Unity's native pattern; handles hide flags, preview camera (`CameraType.Preview`), `lights`, cleanup |
| Import-settings stamping | Manually editing `.meta` YAML | `TextureImporter` + `SaveAndReimport()` | `.meta` GUID/format is internal; the importer API is the supported path |
| Unique-path collision avoidance | Manual "does this path exist" + counter loops | `AssetDatabase.GenerateUniqueAssetPath` (when needed) + the label-gated overwrite check | Handles Unity path semantics |
| Overwrite safety stamp | Naming conventions alone | `AssetDatabase.SetLabels`/`GetLabels` (`NamerGenerated`) | D-04 requires overwrite limited to stamped assets |
| GPU→CPU readback | `RenderTexture.active` + `ReadPixels` | `AsyncGPUReadback.RequestIntoNativeArray` | Non-blocking; no editor stall on 2K/4K |
| PNG encoding | Manual PNG writer | `ImageConversion.EncodeToPNG()` | Correct RGBA8 encoding, standard |
| File-content hashing | Hand-rolled checksum | `System.Security.Cryptography.SHA256` | Standard, collision-resistant enough for immutability |
| Debug decode math | Re-implementing decode in the debug shader | `#include "NamerSurface.hlsl"` + `NAMER_DECODE_SURFACE` | Single source of truth (D-12); cannot drift |

**Key insight:** every tricky part of this phase (preview rendering, import stamping, label gating, async readback, PNG encoding, hashing) has a first-party Unity or .NET BCL API. Hand-rolling any of them either corrupts packed data or breaks the source-immutability guarantee.

## Common Pitfalls

### Pitfall 1: URP materials render pink in `PreviewRenderUtility`
**What goes wrong:** The before (URP Lit) and after (NAMER) materials both render magenta/fallback-error in the preview.
**Why it happens:** `Render(bool allowScriptableRenderPipeline = false)` defaults to `false`; the preview camera (`CameraType.Preview`, `renderingPath = Forward`) renders through the built-in pipeline, which cannot run URP-shader passes. Verified against `UnityCsReference` `PreviewRenderUtility.cs`: `Render` only toggles `Unsupported.useScriptableRenderPipeline` around `camera.Render()`.
**How to avoid:** Call `preview.Render(true)` (and ensure a URP asset is active in Graphics settings — `NamerSmokeSetup.ConfigureURP` already demonstrates this, but the window should not auto-configure URP; it should detect and warn if URP is inactive). Include a preview spike task early in 03-02 to confirm URP preview output on the target platform before building the full window.
**Warning signs:** Solid magenta panes; "FallbackError" shader appearing on the preview mesh.

### Pitfall 2: Packed surface texture corrupted by default import settings
**What goes wrong:** The saved packed PNG re-imports with sRGB + compression + mips + bilinear, silently destroying the alpha bit field and octahedral normals.
**Why it happens:** `ImportAsset` on a new PNG applies project defaults (sRGB on, compression per platform, mips on, bilinear). Any of these corrupts packed data (PITFALLS #7).
**How to avoid:** Immediately stamp `sRGBTexture=false`, `textureCompression=Uncompressed`, `filterMode=Point`, `mipmapEnabled=false`, `textureType=Default` (NOT `Normal` — that would renormalize/remap channels), then `SaveAndReimport()`. Add a re-import round-trip test that re-loads the stamped texture and asserts the packed bits survive.
**Warning signs:** Metallic/roughness "noise" in the render; the packed texture "looks colorful" when inspected.

### Pitfall 3: Source `.meta` files mutated (not just texture bytes)
**What goes wrong:** The immutability test passes if it only hashes source texture bytes, but the plugin flipped a source `TextureImporter` flag (`isReadable`, `sRGB`) and left it changed.
**Why it happens:** Import-settings changes persist in the source `.meta`; the source texture bytes themselves are untouched (PITFALLS #11).
**How to avoid:** D-15 mandates hashing every source file **and its `.meta`**. Never call `SaveAndReimport()`/`SetDirty` on a source asset; only on the new generated files.

### Pitfall 4: Editor window async readback never completes in edit mode
**What goes wrong:** The debounced preview re-dispatch requests readback, but `done` stays false and the window never repaints.
**Why it happens:** In edit mode (not play mode) there is no player loop pumping `AsyncGPUReadback` by default; the request may stall.
**How to avoid:** Pump via `EditorApplication.update` (`CallbackFunction` field — subscribe with `+=`) and/or set `request.forcePlayerLoopUpdate = true` (a real property on `AsyncGPUReadbackRequest`). Use `request.GetData<byte>()`/`GetData<Color32>()` on completion, then `Repaint()`. Unsubscribe in `OnDisable`.

### Pitfall 5: Preview leaks pool targets across re-dispatches
**What goes wrong:** Each interactive control change re-runs `Process`, but the previous result's RTs are never `ReleaseResult`'d, climbing GPU memory (PITFALLS #8).
**Why it happens:** `NamerComputeResult` targets are pool-leased; the debounce loop re-processes before releasing the prior result.
**How to avoid:** Before each re-dispatch, `ReleaseResult` the previous result (and dispose any prior readback `NativeArray`s). Keep at most one live result in the window at a time. The 02-03 leak-watchdog pattern (`LiveRenderTargetCount` returns to baseline) can extend to the preview loop.

### Pitfall 6: Overwrite clobbering a non-generated asset
**What goes wrong:** A user points the destination at a folder containing their own `Something.mat`, and `Process` overwrites it.
**Why it happens:** The generator overwrote by name without checking the `NamerGenerated` stamp.
**How to avoid:** Before overwriting an existing path, `AssetDatabase.GetLabels(existing)` must contain `NamerGenerated`; otherwise abort with a clear error (D-04). Never delete/overwrite anything outside the configured destination.

## Code Examples

Verified signatures from the installed Unity `6000.0.82f1` reference assemblies and `UnityCsReference`.

### PreviewRenderUtility before/after with a single synced camera (D-09)

```csharp
// Source: UnityEditor.PreviewRenderUtility (Unity 6000.0.82f1 reflected + UnityCsReference)
private PreviewRenderUtility _preview;

private void OnEnable()
{
    _preview = new PreviewRenderUtility();
    _preview.cameraFieldOfView = 30f;
    _preview.ambientColor = new Color(0.1f, 0.1f, 0.1f, 1f);
}

private void OnDisable()
{
    _preview?.Cleanup();
    _preview = null;
}

private void DrawPreview(Rect rect, Mesh mesh, Material beforeMat, Material afterMat)
{
    _preview.BeginPreview(rect, GUIStyle.none);
    _preview.camera.transform.position = new Vector3(0f, 0f, -4f);
    _preview.camera.transform.rotation = Quaternion.identity;
    _preview.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
    _preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);

    // Two meshes, one shared camera: honest before/after lighting+angle comparison.
    _preview.DrawMesh(mesh, new Vector3(-0.7f, 0f, 0f), Quaternion.identity, beforeMat, 0);
    _preview.DrawMesh(mesh, new Vector3(+0.7f, 0f, 0f), Quaternion.identity, afterMat, 0);

    // renderFullScene=false + allowScriptableRenderPipeline=true is REQUIRED for URP shaders.
    _preview.Render(true);
    Texture result = _preview.EndPreview();
    GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
}
```

### Import stamping of the packed surface texture (GEN-04)

```csharp
// Source: TextureImporter / AssetImporter (Unity 6000.0.82f1 reflected)
using UnityEditor;
using UnityEngine;

private static void StampPackedSurfaceImporter(string pngPath)
{
    AssetDatabase.ImportAsset(pngPath);                    // create .meta + importer (defaults)
    TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(pngPath);
    importer.textureType = TextureImporterType.Default;    // NOT Normal — no channel remap
    importer.sRGBTexture = false;                          // linear data, not color
    importer.textureCompression = TextureImporterCompression.Uncompressed;
    importer.filterMode = FilterMode.Point;                // bit-packed alpha must not interpolate
    importer.mipmapEnabled = false;                        // no mip generation
    importer.wrapMode = TextureWrapMode.Repeat;
    importer.SaveAndReimport();
}
```

### Async readback → PNG save (D-08 + D-06)

```csharp
// Source: AsyncGPUReadback / Texture2D / ImageConversion (Unity 6000.0.82f1 reflected)
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

// Readback the packed surface (R8G8B8A8_UNorm) as RGBA32 bytes, non-blocking.
var request = AsyncGPUReadback.RequestIntoNativeArray(ref _surfaceBytes, rt, 0, TextureFormat.RGBA32);
request.forcePlayerLoopUpdate = true;                       // edit-mode pump (Pitfall 4)
// ... on request.done:
if (request.hasError) { /* abort */ }
NativeArray<byte> data = request.GetData<byte>();

Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, /*linear*/ true);
tex.LoadRawTextureData(data);                              // raw bytes, no color conversion
tex.Apply(false, false);
byte[] png = tex.EncodeToPNG();                            // ImageConversion extension method
File.WriteAllBytes(pngPath, png);
DestroyImmediate(tex);
```

### Overwrite-safe generated-asset write with label stamping (D-04)

```csharp
// Source: AssetDatabase (Unity 6000.0.82f1 reflected)
private const string GeneratedLabel = "NamerGenerated";

private static string EnsureWritableTarget(string path, bool overwriteGenerated)
{
    Object existing = AssetDatabase.LoadAssetAtPath<Object>(path);
    if (existing == null) { return path; }                 // fresh — safe

    string[] labels = AssetDatabase.GetLabels(existing);
    bool stamped = Array.IndexOf(labels, GeneratedLabel) >= 0;
    if (!stamped || !overwriteGenerated)
    {
        throw new InvalidOperationException(
            $"Refusing to overwrite non-generated asset '{path}'. " +
            "Only NamerGenerated-stamped assets in the destination folder can be overwritten.");
    }
    return path;
}

private static void Stamp(Object asset)
{
    AssetDatabase.SetLabels(asset, new[] { GeneratedLabel });
}
```

### Debounced recompute pump via EditorApplication.update (UI-06)

```csharp
// Source: EditorApplication (Unity 6000.0.82f1 reflected) — update/delayCall are fields.
private void OnEnable()  { EditorApplication.update += Tick; }
private void OnDisable() { EditorApplication.update -= Tick; }

private void Tick()
{
    if (_dirty && (EditorApplication.timeSinceStartup - _lastChange) > _debounceSeconds)
    {
        _dirty = false;
        RecomputeAndRequestPreview();
    }
    // ... check outstanding readback request.done; on completion: Repaint()
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `RenderTextureFormat` (legacy enum) | `GraphicsFormat` + `GraphicsFormatUtility` | Unity 2021+/Unity 6 | Already the codebase convention (STACK.md); Phase 3 writes inherit it |
| `PreviewRenderUtility.Render()` (built-in only) | `Render(bool allowScriptableRenderPipeline)` + `Unsupported.useScriptableRenderPipeline` toggle | Present in Unity 6 (`6000.0.82f1`) | Must pass `true` to preview URP materials |
| `AsyncGPUReadback.Request` (poll `done`) | Also `RequestAsync(...)` → `Awaitable<AsyncGPUReadbackRequest>` | Unity 6 | Optional; polling/callback via `EditorApplication.update` remains the established pattern |
| `RenderSingleCamera` (obsolete) | `RenderPipeline.SubmitRenderRequest` / RenderRequest API | Unity 6 | Not needed here — `PreviewRenderUtility.Render(true)` handles the offscreen preview render |

**Deprecated/outdated:**
- `PreviewRenderUtility.AddManagedGO` — not present in Unity 6 (the older name); use `AddSingleGO`.
- `RenderTextureFormat` legacy enum — do not introduce in new code (use `GraphicsFormat`).
- Sync `Texture2D.ReadPixels` on large textures — obsolete for this phase's save path (D-08).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The Blender reference's `Non-Color` data-map convention is the correct precedent for Unity `sRGBTexture=false` on the packed surface | Summary / Sources | LOW — both are "linear/non-color data" and PITFALLS #7 independently requires it; the Unity-side correctness argument holds regardless |
| A2 | `preview.lights` is pre-populated (a 2-light array usable as `lights[0]`/`lights[1]`) | Code Examples | MEDIUM — the property is get-only `Light[]` and Unity's own inspectors use `lights[0]`; if it returned an empty array the plan must add lights explicitly. Verify in the 03-02 preview spike |
| A3 | `TextureImporterCompression.Uncompressed` disables compression on the default (non-platform-overridden) setting | Import stamping | LOW — this is the documented value; platform overrides are cleared/inherited by default, but the plan's re-import round-trip test will catch any override surprise |
| A4 | `AssetDatabase.DeleteAsset(folder)` removes the folder and its `.meta` (no orphan `.meta`) | Testing | LOW — the existing `SourceInspectorTests` already rely on this exact cleanup |

## Open Questions (RESOLVED)

> **Disposition:** Both questions are resolved via **03-02 Task 1** (the preview spike) and its **blocking human-verify checkpoint (spike gate)** — the standalone `PreviewRenderUtility` + URP Lit sphere + NAMER sphere spike that gates the remaining window work. The spike makes `Render(true)` URP-correctness and the `lights` array contents empirically verifiable on this machine (Metal) before the full window is built.

1. **Does `PreviewRenderUtility.Render(true)` render URP materials correctly on the target platform (Metal, this machine)?** — **(RESOLVED via 03-02 Task 1 blocking human-verify spike gate)**
   - What we know: the API toggles `Unsupported.useScriptableRenderPipeline`; `NamerSmokeSetup` proves URP materials render in a scene; the preview camera is `CameraType.Preview` + `renderingPath = Forward`.
   - What's unclear: whether the URP forward renderer drives a preview camera cleanly on Metal (historical pink-preview reports exist).
   - Recommendation: make the first 03-02 task a **preview spike** (standalone `PreviewRenderUtility` + URP Lit sphere + NAMER sphere) that gates the rest of the window work. If it fails, fall back to a texture-only preview (D-09 permits supplementation, but the spike should be escalated as a blocker before that is accepted as a replacement).
   - Resolution: confirmed empirically by the 03-02 preview spike; if it renders pink, the checkpoint gates the fallback (escalated as a blocker, not silently accepted).

2. **`lights` array contents/behavior in a URP `Render(true)` preview** — **(RESOLVED via the same 03-02 spike; A2)**
   - What we know: get-only `Light[]`; Unity's internal inspectors set `lights[0]`/`lights[1]` rotations.
   - What's unclear: exact light count/type in Unity 6 and whether URP preview honors these lights.
   - Recommendation: covered by the same 03-02 spike (A2).
   - Resolution: the spike verifies light count/type in Unity 6 and whether URP preview honors these lights.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Unity Editor | EditMode tests + window + preview | ✓ | `6000.0.82f1` (also `2022.3.62f2` present) | Target `6000.0+`; `2022.3` is out-of-scope fallback only |
| GPU + compute + async readback | Full Process flow in tests + preview | ✓ (Metal; assumed — the 02-03 GPU golden tests are green at HEAD) | — | Tests capability-gate with `Assert.Ignore` (existing `GpuGoldenTests` pattern) |
| `com.unity.test-framework` | EditMode tests | ✓ | bundled (already referenced by test asmdef) | — |
| Blender reference repo | D-06 verification only (not a build dependency) | ✓ (public GitHub, `develop`) | — | None — verified once; no runtime/CI dependency |

**Missing dependencies with no fallback:** none identified — all phase dependencies are satisfied by the installed Unity Editor and the already-built Phase 1/2 package code.

**Note (STATE.md carry-in):** the interactive Unity Editor (PID 11637) holds the project lock and has historically blocked headless `-batchmode` PlayMode runs. The EditMode TEST-03 suite must be run with the editor closed (or via the in-editor Test Runner); the plan's test task should note this lock constraint.

## Security Domain

*Editor tool — "security" maps to asset integrity and resource safety, not network attack surface.*

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | n/a (no auth surface) |
| V3 Session Management | no | n/a |
| V4 Access Control | no | n/a |
| V5 Input Validation | yes | Validate/`abort` on: unsupported selection (Phase 2 already warns), non-stamped overwrite targets (D-04), `request.hasError` on readback, missing shader/compute asset |
| V6 Cryptography | yes (asset integrity, not confidentiality) | `SHA256` for source-immutability verification — standard BCL, no hand-rolled hashing |

### Known Threat Patterns for {Unity editor asset pipeline}

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Overwriting a non-generated asset (user data loss) | Tampering | `NamerGenerated` label gate + destination-folder confinement (D-04) |
| Mutating source `.meta`/importer settings | Tampering | Read-only source discipline; immutability test hashes files + `.meta` (D-15) |
| Unbounded GPU allocation across re-dispatches | DoS (editor) | `ReleaseResult` before each re-dispatch; leak-watchdog baseline |
| Re-importing a generated texture with defaults (data corruption) | Tampering | Immediate `TextureImporter` stamping + re-import round-trip test |
| `request.hasError` ignored (partial/garbage readback written to disk) | Integrity | Guard every readback completion; abort + surface error |

## Sources

### Primary (HIGH confidence — verified against installed Unity `6000.0.82f1` reference assemblies and open-source reference)
- `Unity.app/Contents/Managed/UnityEditor.dll` (reflected via mono `Assembly.LoadFile` + `GetMembers`) — `PreviewRenderUtility`, `TextureImporter`, `AssetImporter`, `AssetDatabase`, `EditorApplication`, `EditorWindow` exact member signatures
- `Unity.app/Contents/Managed/UnityEngine.dll` — `AsyncGPUReadback`, `AsyncGPUReadbackRequest`, `Texture2D` signatures
- `Unity.app/Contents/Managed/UnityEngine/UnityEngine.ImageConversionModule.dll` — `ImageConversion` (EncodeToPNG/EXR extension methods)
- `github.com/Unity-Technologies/UnityCsReference` `Editor/Mono/Inspector/PreviewRenderUtility.cs` — `Render(bool allowScriptableRenderPipeline)` SRP toggle, preview camera setup (`CameraType.Preview`, `renderingPath=Forward`, `Unsupported.useScriptableRenderPipeline`)
- `github.com/GraffitiEntertainment/BlenderNamerPlugin` (develop): `properties.py` (`apply_texture_to_material` sets `colorspace_settings.name='Non-Color'` for Normal/AO/Metallic/Roughness; Base→sRGB), `namer_core.py` (`create_namer_png` NAMER_PNG metadata + `pack_alpha` bit layout), `texture_operators.py` / `namer_png_panel.py` (no interpolation/filter/mip settings — negative finding)

### Secondary (MEDIUM confidence)
- Unity Discussions "Render with URP to a texture in a custom editor" + UnityCsReference — corroborate the pink-preview risk and the `Render(true)` requirement
- `docs.unity3d.com/6000.0/Documentation/Manual/urp/User-Render-Requests.html` — RenderRequest API as the modern alternative (not needed this phase)

### Tertiary (LOW confidence — flagged for validation)
- The exact `lights` array pre-population behavior (A2) — from training knowledge + Unity internal inspector usage, not a direct runtime probe. Covered by the recommended 03-02 preview spike.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — every API signature reflected from the installed Unity 6 assemblies; no external packages
- Architecture: HIGH — patterns follow the established Phase 1/2 codebase; the PreviewRenderUtility path verified against source
- Pitfalls: HIGH — drawn from the Phase 1/2 PITFALLS corpus plus the newly-verified Blender reference and PreviewRenderUtility source

**Research date:** 2026-08-27
**Valid until:** 2026-09-26 (30 days; Unity editor APIs are stable but the 03-02 preview spike should re-confirm URP-in-preview behavior on the target platform)

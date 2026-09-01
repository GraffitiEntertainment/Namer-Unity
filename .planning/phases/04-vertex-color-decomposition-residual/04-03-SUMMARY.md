---
phase: 04-vertex-color-decomposition-residual
plan: 03
subsystem: vertex-color-decomposition
tags: [csharp, unity, hlsl, exr, mesh-asset, editor-ui, integration-test, editorprefs]

# Dependency graph
requires:
  - phase: 04-vertex-color-decomposition-residual
    plan: 01
    provides: MeshVertexSplitter (NamerSplitResult), VertexColorFitter (VertexColorFitResult.ToColor32Array)
  - phase: 04-vertex-color-decomposition-residual
    plan: 02
    provides: NamerDecompPipeline.GenerateResidual (NamerDecompOutput + NamerDecompErrorStats), NamerConstants.VcFloor, NAMERDecomp.hlsl (NAMER_DECOMP_HEATMAP)
  - phase: 03-asset-generation-editor-workflow-preview
    provides: AssetGenerator (sole disk writer), NamerProcessor orchestration, NamerEditorWindow preview UI
  - phase: 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit
    provides: EditorPrefs-backed NamerProcessorSettings + NamerEditorConstants pattern
provides:
  - AssetGenerator.WriteResidualExr (RGBAHalf EXR write) + WriteMeshAsset + BuildSplitMesh
  - NamerProcessor decomposition stage gated on settings.DecompositionEnabled + scene sharedMesh swap
  - EditorPrefs DecompositionEnabled / ErrorThreshold / ResidualResolution settings + locked defaults
  - NamerEditorWindow decomposition toggle/threshold/ladder + live Statistics block + 10 debug channels
  - NamerPreviewRenderer two-mesh Render overload, NamerDebugView channels 6/7/8 (Vertex Colors / Residual / Error Heatmap)
  - NamerDecompIntegrationTests (4 headless [UnityTest] cases)
affects:
  - Phase 5 (stylization) — reconstructs over the vertex-color + residual representation
  - Phase 4 verification/UAT — the decomposition feature is now end-to-end testable

# Tech tracking
tech-stack:
  added: []
  patterns:
    - RGBAHalf residual readback -> Texture2D -> ImageConversion.EncodeToEXR (linear/uncompressed/point/no-mips importer)
    - Generated mesh serialization via AssetDatabase.CreateAsset (high-level SetVertices/SetTriangles, never low-level index-buffer)
    - EditorPrefs decomposition settings mirroring the AO-control pattern
    - In-memory preview decomposition reuse (NamerDecompPipeline kept alive, residual RT bound without readback)

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
    - Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs
    - Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader

key-decisions:
  - "The decomposition source mesh is resolved from the selection inside NamerProcessor.Process (ResolveSourceMesh) because SourceInspector never populates NamerMaterialInspection.BakeSourceMesh in production"
  - "The scene mesh swap reads MeshFilter via GetComponent<MeshFilter>() — a Component sibling of Renderer, not a Renderer pattern match — and handles SkinnedMeshRenderer (which IS a Renderer) first"
  - "The live preview keeps the pool-leased residual render target bound to the preview material (no readback), so the NamerDecompOutput stays alive until the next recompute or window disable"
  - "Auto-drop (D-13) leaves _BaseResidualMap unbound (white {} default) while still writing + swapping the split mesh, so the vertex-color reconstruction renders"

requirements-completed: [TEST-02]

# Metrics
duration: 14min
completed: 2026-08-31
---

# Phase 4 Plan 03: Vertex-Color Decomposition End-to-End Summary

**Ships vertex-color decomposition end-to-end through `Process with NAMER`: writes the seam-split mesh `.asset` + residual EXR + material, swaps the scene renderer's `sharedMesh` so vertex colors render at runtime, and exposes the opt-in toggle / threshold / resolution ladder / live stats / three debug channels — with source assets byte-identical (TEST-02).**

## Performance

- **Duration:** 14 min
- **Started:** 2026-08-31T17:00:00Z
- **Completed:** 2026-08-31
- **Tasks:** 3
- **Files created:** 1
- **Files modified:** 8

## Accomplishments

1. **`AssetGenerator` disk writes (Task 1)** — `WriteResidualExr` mirrors `WriteSurfaceTexture` but `ImageConversion.EncodeToEXR` on an RGBAHalf readback (linear/uncompressed/point/no-mips); `WriteMeshAsset` + `BuildSplitMesh` serialize the seam-split mesh via the high-level Mesh API (UInt32 index above 65535 verts, bone weights/bindposes preserved). `Generate` gains a `NamerDecompData` parameter: the split mesh is always written, the residual EXR only when `ResidualRequired`, and `WriteMaterial` binds `_BaseResidualMap` from a `baseResidualPath` (base / residual / null-unbound for the auto-drop endgame). `PreflightTargets` preflights the new `_Residual.exr` + `.asset` paths before any GPU work.

2. **`NamerProcessor` orchestration (Task 2)** — the decomposition stage runs inside the material loop, gated on `settings.DecompositionEnabled`: read back the linear base, `MeshVertexSplitter.Split` + `VertexColorFitter.Fit` + `NamerDecompPipeline.GenerateResidual`, hand `NamerDecompData` to the generator, and dispose every pipeline/output/readback in `finally`. `BindGeneratedMaterials` swaps `sharedMesh` to the generated split mesh (guarded on `sharedMesh == sourceMesh`). Three EditorPrefs-backed settings + locked defaults (OFF / 0.02 / 0).

3. **Window UI + shader + tests (Task 3)** — the decomposition toggle (default OFF), Error Threshold slider (0.0–0.10), Residual Resolution ladder (Auto/2048/1024/512/256/128), and the live Statistics block (Coverage / Avg Error / Max Error / Residual / Residual Resolution). `RecomputePreview` runs the in-memory fit + residual for preview parity (never writes disk); the debug shader renders channels 6/7/8 (Vertex Colors / Residual / Error Heatmap). Four headless `[UnityTest]` cases pass.

## Task Commits

1. **Task 1: AssetGenerator — WriteResidualExr + WriteMeshAsset + residual material bind** — `2534ba0` (feat)
2. **Task 2: NamerProcessor decomposition stage + sharedMesh swap + settings/constants** — `7452ed8` (feat)
3. **Task 3: Window UI + stats + preview parity + debug channels 6/7/8 + integration tests** — `500b8e0` (feat)

**Deviation fix commits:**
- `fd2e290` (fix) — NamerProcessor mesh-swap `GetComponent<MeshFilter>` compile fix

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs` — residual EXR + mesh write, `NamerDecompData`, `MeshPath`/`ResidualTexturePath`
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — decomposition stage, source-mesh resolution, `sharedMesh` swap
- `Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs` — three EditorPrefs decomposition settings
- `Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs` — `DefaultDecompositionEnabled`/`DefaultErrorThreshold`/`DefaultResidualResolution`
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — toggle/threshold/ladder/stats UI + decomposed preview parity
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs` — `_DebugBaseMap` + `SetDebugBaseMap`
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs` — two-mesh `Render` overload
- `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader` — COLOR semantic + channels 6/7/8 + viridis heatmap
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs` — 4 `[UnityTest]` cases

## Decisions Made

- **Source mesh resolution lives in `NamerProcessor.Process`** (`ResolveSourceMesh`), not `SourceInspector`, because `NamerMaterialInspection.BakeSourceMesh` was never populated in the production flow (only tests set it). Resolution mirrors the window's preview-mesh logic: scene renderer → prefab contents → model/FBX sub-assets; null falls back to the Phase-3 shape with a warning.
- **Mesh swap reads `GetComponent<MeshFilter>()`** rather than `renderer is MeshFilter`, since `MeshFilter` is a `Component` sibling of `Renderer` (the plan's literal example does not compile).
- **Live preview keeps the residual RT bound** (no readback) so the pool-leased `NamerDecompOutput` remains alive until the next recompute, matching the existing "assign render targets directly to preview materials" convention.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Plan's `renderer is MeshFilter` mesh-swap pattern does not compile**
- **Found during:** Task 3 first compile
- **Issue:** `MeshFilter` is a `Component` sibling of `Renderer`, not a subclass, so `renderer is MeshFilter mf` raises CS8121 ("cannot be handled by a pattern").
- **Fix:** `ResolveRendererMesh`/`SetRendererMesh` now handle `SkinnedMeshRenderer` (which IS a `Renderer`) first, then read `MeshFilter` via `renderer.GetComponent<MeshFilter>()`.
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs`
- **Verification:** Full EditMode suite compiles; 4 decomposition integration tests pass.
- **Committed in:** `fd2e290`

**2. [Rule 2 - Missing Critical] Decomposition source mesh was never resolved**
- **Found during:** Task 2 implementation
- **Issue:** `NamerMaterialInspection.BakeSourceMesh` is only assigned in tests — `SourceInspector.Inspect` never sets it. Without it, the decomposition stage would always hit the "no mesh to decompose" fallback and the feature could not work end-to-end.
- **Fix:** Added `ResolveSourceMesh` (plus `FindMeshInObject` / `FindMeshSubAsset`) to `NamerProcessor.Process`, resolved once from the selection when decomposition is enabled and assigned to each inspection's `BakeSourceMesh`.
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs`
- **Verification:** `Process_WithDecomposition_WritesMeshAndResidualAndSwapsMesh` passes (mesh written + swapped).
- **Committed in:** `7452ed8`

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical)
**Impact on plan:** Both were necessary for the feature to compile and work end-to-end. No scope creep — no new writer types, no new state machines, no architectural changes.

## Issues Encountered

- The plan's `<interfaces>` listed `NamerMaterialInspection.BakeSourceMesh` as an existing contract, but it is never populated by the production inspection path — resolved via `ResolveSourceMesh` (documented above).

## User Setup Required

None — no external service configuration required. All additions are C#/HLSL + Unity built-ins.

## Next Phase Readiness

- Decomposition is end-to-end: Process writes the full set, the scene mesh swaps, and the window previews/stats/debug channels are live.
- The 4 new integration tests plus the prior 94 EditMode tests pass headless (98/98, exit 0).
- Ready for Phase 5 stylization (which reconstructs over the vertex-color + residual representation) and Phase 4 verification/UAT.

---

*Phase: 04-vertex-color-decomposition-residual*
*Completed: 2026-08-31*

## Self-Check: PASSED

All created/modified files exist; the 4 plan commits (`2534ba0`, `7452ed8`, `500b8e0`) plus the deviation fix (`fd2e290`) are present in git history; the 4 integration tests pass headless and the full EditMode suite is 98/98 (exit 0).

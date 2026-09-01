---
phase: 04-vertex-color-decomposition-residual
verified: 2026-09-01T00:45:22Z
status: gaps_found
score: 16/17 must-haves verified (3/4 roadmap success criteria + 13/13 plan truths)
overrides_applied: 0
gaps:
  - truth: "User enables vertex-color decomposition and the output mesh's vertex colors reconstruct the low-frequency base color within reported error (ROADMAP SC1)"
    status: failed
    reason: >-
      True only for the tested subset (single-material, single-mesh, UVs in [0,1]). Three
      code-review criticals were independently confirmed in the code by this verifier and
      break the criterion for first-class real inputs, silently (Process reports success):
      (1) CR-01 multi-material/multi-mesh — one mesh is resolved for the whole selection and
      re-used for every material's fit, then the mesh-swap dictionary overwrites per-material
      entries so the last material's split mesh wins; every other slot renders
      residual_i x vc_last != base_i. (2) CR-03 tiling UVs (any triangle with UVs outside
      [0,1]) rasterize to zero coverage, which the D-13 gate reads as a perfect fit
      (MaxError=0, fully opaque) and silently drops the residual, while the CPU fitter
      clamps out-of-range UVs to edge texels — garbage vertex colors, no residual, success
      reported. (3) CR-02 the residual EXR is imported Point-filtered while the adaptive
      search and reported MaxError are measured under bilinear resampling, so the saved
      material's actual runtime error exceeds the reported error whenever ChosenResolution
      < source width (the feature's headline adaptive case).
    artifacts:
      - path: "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs"
        issue: "Lines 105/128: one ResolveSourceMesh(selection) result assigned to every inspection's BakeSourceMesh. Lines 254-263: generatedMeshBySourceMeshId[sourceMesh.GetInstanceID()] plain assignment — last material's mesh wins for a shared source mesh (CR-01)"
      - path: "Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute"
        issue: "CSRasterizeVertexColors tests texel UV in [0,1) against raw triangle UVs with no wrap handling — triangles outside [0,1] cover zero texels; uncovered texels write _ErrorStat=(0,0,0,1) (CR-03)"
      - path: "Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs"
        issue: "Lines 210-215: D-13 gate checks only MaxError <= threshold && MinAlpha opaque — no coverage-fraction guard, so zero coverage (tiling UVs) reads as a perfect fit and drops the residual (CR-03)"
      - path: "Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs"
        issue: "Lines 433-436 (and 512-515): SampleBase clamps UVs to the texture edge instead of wrapping — tiling layouts fit against clamped edge texels (CR-03)"
      - path: "Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs"
        issue: "Line 437: WriteResidualExr sets importer.filterMode = FilterMode.Point while NamerDecompPipeline evaluates resolution candidates with bilinear Resample and NamerDecompOutput documents 'hardware bilinear filtering' — saved assets render blocky and exceed the reported error at reduced resolutions (CR-02)"
    missing:
      - "Resolve the decomposition mesh per material/renderer slot (or reject decomposition with a blocking warning when >1 material maps to one mesh / selection spans multiple meshes) — SourceInspector.cs:258 makes multi-material a first-class path"
      - "Guard the D-13 gate on coverage (e.g. fitStats coverage fraction < 0.5 => keep Phase-3 shape + warning instead of running the drop gate)"
      - "Make the CPU SampleBase wrap (Repeat) instead of clamp so tiling fits match runtime sampling"
      - "Change WriteResidualExr filterMode to Bilinear (Point is only correct for the bit-packed surface texture)"
      - "Regression tests: multi-material selection, tiling-UV mesh, and a reduced-ChosenResolution residual round-trip asserting the saved importer settings"
---

# Phase 4: Vertex-Color Decomposition + Residual Verification Report

**Phase Goal:** Fit low-frequency base color into vertex colors via per-triangle barycentric least-squares with seam-safe splitting, and generate an adaptive residual texture with error reporting
**Verified:** 2026-09-01T00:45:22Z
**Status:** gaps_found
**Re-verification:** No — initial verification

**Mode note:** Phase is `mode: mvp`; the ROADMAP goal is not User Story format (`gsd-sdk query user-story.validate` -> false), same condition as Phases 1-3 (recorded there as process notes). Unlike Phase 3, the Phase-4 PLANs carry no validating User Story either, so the User Flow table below is derived from the goal's implied artist flow rather than a story's outcome clause. Optional cleanup: `/gsd mvp-phase 4`.

## Verification Basis (independently checked, not taken from SUMMARYs)

- All 17 phase source files read at the working tree (= HEAD `5887a78`, docs-only; last code commit `fd2e290`). All 15 phase-4 commits (`832d8ec`..`5887a78`) verified present in git history.
- All three 04-REVIEW.md critical findings re-derived from the code by this verifier (not trusted from the review): CR-01 at `NamerProcessor.cs:105,128,254-263` + `SourceInspector.cs:258`; CR-02 at `AssetGenerator.cs:437` vs `NamerDecompPipeline.cs` bilinear `Resample`; CR-03 at `NAMERDecomp.compute:81` (raw-UV point test), `NamerDecompPipeline.cs:210-215` (gate ignores coverage), `VertexColorFitter.cs:433-436` (clamp not wrap).
- **Test suite:** batchmode is blocked by the live editor's project lock (clone runner `/tmp/namer-gsd/run-tests-in-clone.sh` no longer exists), so this verifier did not re-execute. Evidence used: (a) `/tmp/namer-full-editmode.xml` — `total=98 passed=98 failed=0 skipped=0`, mtime Aug 31 17:14 PDT, after the final code commit `fd2e290` and before the docs-only commits — and (b) the orchestrator's independent live-editor TestRunnerApi run confirming 98/98 green. `skipped=0` proves the GPU capability gates passed, i.e. the decomposition GPU/integration tests genuinely executed on Metal. Test method counts verified by grep: 8 fit/split `[Test]`, 4 residual `[UnityTest]`, 4 integration `[UnityTest]`.

## User Flow Coverage (MVP mode, derived from the goal)

| Step | Expected | Evidence in Codebase | Status |
|------|----------|----------------------|--------|
| Artist enables "Vertex Color Decomposition" and adjusts threshold / resolution ladder | Toggle (default OFF, D-05), Error Threshold slider 0.00-0.10 (default 0.02), Auto/2048/1024/512/256/128 popup, EditorPrefs-persisted | `NamerEditorWindow.cs:836-871` (`"Vertex Color Decomposition"`, `Slider("Error Threshold", ..., 0f, 0.10f)`, `Popup("Residual Resolution", ...)`), `NamerProcessorSettings.cs:83-101`, `NamerEditorConstants.cs:47-53` | VERIFIED |
| Runs `Process with NAMER` on a textured mesh | Full set written under NAMERGenerated/{source}/: split mesh .asset + residual EXR (when required) + Base/Surface PNG + .mat; scene renderer swaps to the split mesh; source assets byte-identical | `NamerProcessor.cs:126-157` decomposition stage; `AssetGenerator.cs:165-204` writes; `NamerProcessor.cs:266-275` sharedMesh swap; `SourceImmutability_WithDecomposition` (SHA-256) | VERIFIED — but only proven for single-material, single-mesh, [0,1]-UV inputs (see Gap 1) |
| Observes reconstruction error statistics | Live Statistics block: Coverage %, Avg Error, Max Error, Residual (required/not required), Residual Resolution | `NamerEditorWindow.cs:878-915` (`DrawDecompStats`), stats from `NamerDecompPipeline` hierarchical reduce | VERIFIED |
| Outcome: "vertex colors reconstruct the low-frequency base color within reported error" | vcInterp x residual ~= base within the reported/threshold error | Algorithm and pipeline proven by tests (2/255 GPU golden vs CPU oracle; constant-color fit 1e-3); contradicted for multi-material selections (CR-01), tiling-UV meshes (CR-03), and reduced-resolution saved assets (CR-02 Point import) | **FAILED** — see Gaps Summary |

## Goal Achievement

### Observable Truths (ROADMAP Phase 4 Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | User enables vertex-color decomposition and the output mesh's vertex colors reconstruct the low-frequency base color within reported error | ✗ FAILED | True for the single-material / single-mesh / [0,1]-UV / full-or-smaller-source subset the tests cover (integration + GPU golden tests prove it there). Silently false for: multi-material or multi-mesh selections (CR-01 — wrong vertex colors rendered, success reported), tiling-UV meshes (CR-03 — garbage clamped fit + residual dropped as "perfect"), and any saved material whose residual was adaptively reduced (CR-02 — Point import makes actual runtime error exceed the reported MaxError). All three confirmed in code by this verifier. |
| 2 | Output mesh splits vertices at UV seams and discontinuities while preserving mesh attributes | ✓ VERIFIED | `MeshVertexSplitter.cs` — quantized (position, normal, tangent, uv) `WeldKey` dictionary (lines 103-137, 184-255); attribute streams re-emitted verbatim; sub-mesh triangles rewritten; boneWeights/bindposes preserved. Tests: `Split_SplitsAtUvSeam_ButWeldsSharedUvs`, `Split_PreservesAttributeStreams`, `Split_PreservesSubMeshBoundaries`, `Split_PreservesSkinningData` all pass (98/98 suite). Warning WR-02: tangent-less sources get fabricated (0,0,0,0) tangents written verbatim (`MeshVertexSplitter.cs:81-85`, `BuildSplitMesh` `SetTangents`, no `RecalculateTangents`) — degenerate TBN, broken normal mapping on such sources. |
| 3 | Residual texture captures the difference between fitted vertex-color interpolation and the source texture | ✓ VERIFIED | Multiplicative quotient `base / max(vc.rgb, _VcFloor)` in `CSResidual` (NAMERDecomp.compute:123-141), derived from the QUANTIZED Color32 (`ToFloat4(quantizedColors)` upload; `ToColor32Array` is the sole source), with coverage mask (`vc.a < 0.5` identity branch) and base-alpha preservation. `Residual_ReconstructsBase_WithinByteTolerance` proves `residual x vcInterp ~= base` within 2/255 per channel against a CPU oracle; `Residual_UncoveredTexels_AreIdentity` proves the identity branch. Caveat: the persisted EXR's Point filter mode (CR-02) degrades how the saved artifact applies this difference at runtime — tracked under truth 1. |
| 4 | Processor reports reconstruction-error statistics (coverage, average/max error, residual requirement) and adapts residual resolution with manual override | ✓ VERIFIED | `NamerDecompErrorStats` (Coverage/AvgError/MaxError/ResidualRequired/ChosenResolution) from one consistent mean-channel MAE (`CSErrorHeatmap` + hierarchical `CSReduce`); D-13 gate with alpha-opaque guard (`NamerDecompPipeline.cs:210-215`); adaptive downward-halving over `{2048,1024,512,256,128}` (`ChooseResolution`) with manual popup-index override (`manualResolution>0` -> `ResolutionLadder[idx]` clamped to source). Tests: `GenerateResidual_Gate_DistinguishesConstantVsGradient`, `GenerateResidual_AdaptiveAndManualOverride` (adaptive choice is a ladder value <= w with MaxError <= threshold; manual index 3 -> 512px, not raw index). Warnings: reported error diverges from saved-asset error (CR-02); stats are meaningless at zero coverage (CR-03 — Coverage/MaxError report 0%/0.0 as "perfect"); WR-03 non-square sources (square residual, override clamps against width only); IN-04 8-bit stats readback quantization. |

**Score:** 3/4 roadmap success criteria (+ 13/13 plan-level truths below)

### Plan-Level Must-Have Truths

| Plan | Truth | Status | Evidence |
|------|-------|--------|----------|
| 04-01 | Seam-split mesh gives each UV-seam side a distinct vertex while preserving attributes | ✓ VERIFIED | `MeshVertexSplitter.WeldKey` includes UV; seam-weld test passes |
| 04-01 | Per-vertex colors are a multi-sample barycentric LSQ fit, not simple averaging | ✓ VERIFIED | `VertexColorFitter.AccumulateJob` — kGrid=16 interior grid per triangle, symmetric 3x3 Gram + 3 RHS, `Solve3x3` Cramer solve, per-vertex combination (`VertexColorFitter.cs:355-460, 238-308`) |
| 04-01 | Fit is deterministic and runs headless (no GPU) | ✓ VERIFIED | Fixed grid + fixed float math; Burst jobs with `.Complete()`; fixed-order main-thread accumulation; `Fit_IsDeterministic` passes; zero GPU calls in the fitter |
| 04-01 | Colors quantized to Color32, A = per-vertex fit-quality [0,1] | ✓ VERIFIED | `ToColor32Array` (`round(saturate(c)*255)`, A = quality); `ToColor32Array_QuantizeRoundtrip`, `FitQuality_IsInUnitRange_AndOneForPerfectFit` pass |
| 04-02 | Residual is the quotient `base / max(vcInterp, VcFloor)` from QUANTIZED Color32 | ✓ VERIFIED | `CSResidual` + `NamerConstants.VcFloor = 1e-3f` mirrored as `NAMER_VC_FLOOR`; upload path is `fit.ToColor32Array()` -> `_Colors` |
| 04-02 | Error stats + residual-required flag from one consistent MAE metric | ✓ VERIFIED | `err = mean|rec - b|` in `CSErrorHeatmap` for both fit-only (`_FitOnly=1`, rec=vc) and full (rec=vc*residual) evaluations; both reduce through the same `CSReduce` |
| 04-02 | Adaptive resolution halves downward with manual ladder override | ✓ VERIFIED | `ChooseResolution` largest->smallest, skip `r >= w`, break on violation, fallback `w`; manual popup index resolves to pixels |
| 04-02 | All per-pixel residual/error/heatmap work in GPU compute | ✓ VERIFIED | All per-pixel work in the 4 kernels; C# loops are triangle-bounded (`SolveAndAccumulate`, error binning) and reduce-readback-bounded — sanctioned mesh-topology tier; no per-texture-pixel C# loop |
| 04-03 | Process with decomposition ON writes the full set (mesh + residual-when-required + PNGs + .mat) | ✓ VERIFIED | `AssetGenerator.Generate` decomp branch (lines 165-204): mesh ALWAYS written (D-06), EXR only when `ResidualRequired`; `Process_WithDecomposition_WritesMeshAndResidualAndSwapsMesh` passes |
| 04-03 | Material binds residual at _BaseResidualMap; renderer sharedMesh swaps so vertex colors render | ✓ VERIFIED (single-material path) | `WriteMaterial(baseResidualPath)` bind (line 552-554); `BindGeneratedMaterials` swap guarded on `sharedMesh == sourceMesh` (T-04-09); swap happens in the auto-drop case too (test-asserted). Multi-material defect CR-01 tracked under SC1. |
| 04-03 | Window exposes toggle (OFF default), threshold 0.0-0.10 (0.02), ladder, live Statistics, 10 debug channels | ✓ VERIFIED | `NamerEditorWindow.cs:24-27` (10 labels ending "Error Heatmap"), :836-886 (controls), :878-915 (`DrawDecompStats`); `NamerDebugView.shader` channels 6/7/8 with COLOR semantic + `NAMER_DECOMP_HEATMAP` |
| 04-03 | Auto-drop omits residual ("not required"); transparent/cutout always keep one | ✓ VERIFIED | Gate requires `MaxError <= threshold && MinAlpha >= 0.999` (Pitfall 5 alpha guard); `Process_ConstantColorBase_AutoDropsResidual` asserts unbound `_BaseResidualMap` + mesh still written/swapped. Caveat: zero-coverage inputs also auto-drop (CR-03) — wrong reason, tracked under SC1 |
| 04-03 | Source assets never modified — enforced by automated test | ✓ VERIFIED | `SourceImmutability_WithDecomposition` (SHA-256 over mesh/material/texture + .meta, byte-equal after Process) |

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Editor/Decompose/MeshVertexSplitter.cs` | Seam-safe attribute-preserving split | ✓ VERIFIED | 257 lines; quantized weld-key dictionary; no disk writes (`CreateAsset`/`WriteAllBytes` absent) |
| `Editor/Decompose/VertexColorFitter.cs` | Burst LSQ + Color32 quantize + quality alpha | ✓ VERIFIED | 541 lines; `[BurstCompile]` `IJobParallelFor`; `kBarycentricAreaEps`/`kInsideEps` copied exactly; `kFitQualityRef = 0.05f`; try/finally dispose discipline |
| `Compute/NAMERDecomp.hlsl` | `NAMER_VC_FLOOR` mirror + viridis ramp | ✓ VERIFIED | Include guard, `NAMER_VC_FLOOR = 1e-3`, 5 viridis anchors (0.2667/0.2314/0.1294/0.3686/0.9922) |
| `Compute/NAMERDecomp.compute` | 4 kernels: rasterize/residual/heatmap/reduce | ✓ VERIFIED | All 4 `#pragma kernel`s; `_VcFloor` + `max(vc.rgb, _VcFloor)` + `b / v`; `vc.a < 0.5` branch; no `InterlockedAdd`/`RWStructuredBuffer`; all targets linear |
| `Editor/Decompose/NamerDecompPipeline.cs` | GPU harness + adaptive search | ✓ VERIFIED | 598 lines; `ResolutionLadder` exact; D-13 gate; pool lease/release; `ComputeBuffer` uploads; `AsyncGPUReadback` stats readback; no disk writes |
| `Core/NamerConstants.cs` | `VcFloor = 1e-3f` | ✓ VERIFIED | Line 39, documented |
| `Editor/Generation/AssetGenerator.cs` | `WriteResidualExr` + `WriteMeshAsset` + residual bind | ✓ VERIFIED (content) | `EncodeToEXR` on RGBAHalf readback; high-level mesh API (`SetVertices`/`SetTriangles`, `IndexFormat.UInt32` >65535, `colors32`); `NamerDecompData`; `MeshPath`/`ResidualTexturePath`; `PreflightTargets(decomposed: true)`. Defect: `filterMode = Point` on the residual (CR-02) |
| `Editor/Pipeline/NamerProcessor.cs` | Decomposition stage + sharedMesh swap | ✓ VERIFIED (content) | Gated on `settings.DecompositionEnabled`; split->fit->GenerateResidual->Generate; dispose-in-finally; `ResolveSourceMesh` fallback with warning. Defect: selection-wide single mesh (CR-01) |
| `Editor/Settings/NamerProcessorSettings.cs` + `Editor/NamerEditorConstants.cs` | 3 EditorPrefs settings + locked defaults | ✓ VERIFIED | Keys `NamerProcessor.{DecompositionEnabled,ErrorThreshold,ResidualResolution}`; defaults `false` / `0.02f` / `0` |
| `Editor/UI/NamerEditorWindow.cs` | Toggle/threshold/ladder/stats + 10 channels + preview parity | ✓ VERIFIED | All labels present; `BeginDisabledGroup(!_decompositionEnabled)`; write-through to settings + `MarkDirty()`; `RunDecompPreview` in-memory only (no readback-to-disk); two-mesh `Render` overload used with `_previewSplitMesh` (line 720) |
| `Shaders/NamerDebugView.shader` | COLOR semantic + channels 6/7/8 | ✓ VERIFIED | `float4 color : COLOR` -> `vertexColor : TEXCOORD1`; `_DebugBaseMap`; `#include "../Compute/NAMERDecomp.hlsl"`; branches `<6.5`/`<7.5`/else with `NAMER_DECOMP_HEATMAP(err / 0.25)` |
| `Tests/Editor/VertexColorDecompTests.cs` | 8 headless tests | ✓ VERIFIED | 8 `[Test]` methods (4 split + 4 fit), real assertions |
| `Tests/Editor/ResidualPipelineTests.cs` | 4 GPU golden tests | ✓ VERIFIED | 4 `[UnityTest]` with `ComputeAvailable` gate + `Assert.Ignore`; CPU oracle `base / max(vcInterp, 1e-3f)`; 2/255 tolerances; popup-index->512px assertion |
| `Tests/Editor/NamerDecompIntegrationTests.cs` | 4 end-to-end tests | ✓ VERIFIED | 4 `[UnityTest]` through real `NamerProcessor.Process` with scene objects, written assets, `PrefsSnapshot`, SHA-256 immutability. Coverage limited to single-material/single-mesh/[0,1]-UV fixtures |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `VertexColorFitter.Fit` | `NamerSplitResult` | per-triangle barycentric accumulation into 3x3 Gram | ✓ WIRED | `Fit(split, ...)` consumes `split.Uvs` + flattened `SubMeshTriangles` |
| `VertexColorFitResult.ToColor32Array` | residual quotient + mesh `colors32` | quantized Color32 | ✓ WIRED | `NamerProcessor.cs:140` -> `GenerateResidual(split, colors, ...)` and `NamerDecompData.Colors` -> `BuildSplitMesh(...).colors32` |
| `NamerDecompPipeline.GenerateResidual` | quantized colors upload | `StructuredBuffer<float4> _Colors` | ✓ WIRED | `ToFloat4(quantizedColors)` -> `colorsBuf` -> `SetBuffer(_kernelRasterize, "_Colors", ...)` |
| `CSResidual` | `_VcFloor` | `max(vc.rgb, _VcFloor)` divisor floor | ✓ WIRED | `_compute.SetFloat("_VcFloor", NamerConstants.VcFloor)`; kernel guard |
| `CSErrorHeatmap`/`CSReduce` | one consistent MAE | `err = mean\|vcInterp*residual - base\|` | ✓ WIRED | Both evaluations use the same kernel/metric; CPU derives Coverage/AvgError from normalized reduce channels |
| `NamerProcessor` decomposition stage | `GenerateResidual` + `WriteResidualExr` | gated on `settings.DecompositionEnabled` | ✓ WIRED | Lines 126-157; OFF path passes `decomp = null` (Phase-3 shape, test-asserted) |
| `NamerProcessor.BindGeneratedMaterials` | `renderer.sharedMesh` | MeshFilter/SkinnedMeshRenderer swap | ✓ WIRED | `ResolveRendererMesh`/`SetRendererMesh` (SMR first, then `GetComponent<MeshFilter>` — the compile-correct pattern), guarded on `sharedMesh == sourceMesh`. Dictionary overwrite defect = CR-01 |
| `AssetGenerator.WriteMaterial` | `_BaseResidualMap` | base / residual / null switch | ✓ WIRED | `baseResidualPath` null -> bind skipped (white default, D-13); base when non-decomposed; residual when decomposed — all three test-asserted |
| `NamerEditorWindow.RecomputePreview` | in-memory fit + residual + stats | 300 ms debounce, never disk | ✓ WIRED | `RunDecompPreview` binds the pool RT directly; zero `AssetDatabase`/`File` calls in the preview path |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|--------------------|--------|
| Window Statistics block | `_decompStats` | `NamerDecompPipeline.GenerateResidual` -> hierarchical `CSReduce` -> `AsyncGPUReadback` | Yes (GPU-reduced scalars; test-asserted values: gate true/false, ChosenResolution 0/512) | ✓ FLOWING |
| Generated mesh vertex colors | `colors32` | `VertexColorFitter.Fit` -> `ToColor32Array` -> `BuildSplitMesh` | Yes (LSQ fit of the readback base; constant-color test recovers within 1e-3) | ✓ FLOWING |
| Residual EXR | `residualTex` | pool RT -> `ReadBackResidual` (RGBAHalf) -> `EncodeToEXR` | Yes (reconstruction invariant 2/255 vs CPU oracle) | ✓ FLOWING |
| Debug channels 6/7/8 | `input.vertexColor` / `baseResidual` / `_DebugBaseMap` | mesh COLOR stream + preview binds | Yes (shader consumes the same streams the runtime uses) | ✓ FLOWING |

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| Full EditMode suite | Unity batchmode `-runTests` | Blocked: live editor holds the project lock; clone runner absent | ? SKIP — substituted evidence: `/tmp/namer-full-editmode.xml` (98/98/0/0, mtime after final code commit `fd2e290`) + orchestrator's independent live-editor TestRunnerApi run (98/98 green) |
| Per-class results for the 16 new phase-4 tests | grep of XML + test source | 8 fit/split + 4 residual + 4 integration `[Test]`/`[UnityTest]` present with real assertions; suite XML `failed=0 skipped=0` | ✓ PASS (evidence-based) |

### Probe Execution

No probes declared in PLAN/SUMMARY; no `scripts/*/tests/probe-*.sh` exist in this project. N/A.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| VCOL-01 | 04-01 | Per-triangle multi-sample barycentric LSQ fit (not averaging) | ✓ SATISFIED | `VertexColorFitter` Gram + Cramer solve; constant-fit test 1e-3 |
| VCOL-02 | 04-01 | Split vertices at UV seams/discontinuities, preserve attributes | ✓ SATISFIED | `MeshVertexSplitter` weld key; 4 split tests. WR-02 tangent-less caveat |
| VCOL-03 | 04-02 | Residual from difference between fitted interpolation and source | ✓ SATISFIED | Quotient kernel; 2/255 reconstruction invariant vs CPU oracle. CR-02 persisted-sampling caveat |
| VCOL-04 | 04-02 | Error statistics (coverage, avg/max, residual requirement) + debug visualization | ✓ SATISFIED | `NamerDecompErrorStats` + viridis heatmap channel 8. Caveat: stats invalid (report "perfect") at zero coverage (CR-03) |
| VCOL-05 | 04-02 | Adaptive residual resolution with manual override | ✓ SATISFIED | Ladder + override, test-asserted. Caveats: CR-02 (Point import defeats the bilinear premise on saved assets), WR-03 (non-square) |
| TEST-02 | 04-01/02/03 | Automated tests cover vertex color fitting, residual reconstruction, UV seam behavior | ✓ SATISFIED | `VertexColorDecompTests` (fit + seam), `ResidualPipelineTests` (reconstruction), `NamerDecompIntegrationTests` (end-to-end) — 16 new tests in the 98/98 suite |

No orphaned requirements: ROADMAP Phase 4 lists exactly these six; all six are claimed by plans and satisfied. Informational: the Phase-4 slices of UI-03/UI-05 (decomposition controls; vertex-color/residual/error debug views) are also delivered, tracked under their Phase 3/4-5 rows in REQUIREMENTS.md.

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| NamerProcessor.cs | 105, 128, 261 | Selection-wide single source mesh + last-write-wins mesh dictionary (CR-01) | 🛑 Blocker | Wrong vertex colors, silent, for multi-material/multi-mesh selections |
| NAMERDecomp.compute / NamerDecompPipeline.cs | 81 / 210-215 | Zero-coverage reads as perfect fit; residual dropped for tiling UVs (CR-03) | 🛑 Blocker | Garbage fit + no residual, silent, for UVs outside [0,1] |
| AssetGenerator.cs | 437 | Residual EXR `FilterMode.Point` vs bilinear design assumption (CR-02) | 🛑 Blocker | Saved materials blocky; actual error exceeds reported MaxError at reduced resolutions |
| VertexColorFitter.cs | 126-129; NamerProcessor.cs 184/195 | `ArgumentException` escapes the "returns error, never throws" Process contract on empty meshes (WR-01) | ⚠️ Warning | Unhandled exception via the `Assets/Process with NAMER` context-menu path |
| MeshVertexSplitter.cs / AssetGenerator.cs / NamerEditorWindow.cs | 81-85 / 504 / 574 | Zero-tangent fallback written verbatim; no `RecalculateTangents` (WR-02) | ⚠️ Warning | Broken normal mapping on tangent-less sources |
| NamerDecompPipeline.cs | 236, 303-304, 471-476 | Square residual + manual override clamps width only (WR-03) | ⚠️ Warning | Aspect-ratio waste/distortion for non-square atlases |
| NamerEditorWindow.cs / NamerDebugView.shader | 422-453 / 129-137 | Gamma-project heatmap mixes sRGB base with linear reconstruction (WR-04) | ⚠️ Warning | Phantom error shown in the heatmap channel in Gamma projects |
| NamerEditorWindow.cs | 294-380, 455-475 | Failed preview recompute leaves stale released-RT binds (WR-05) | ⚠️ Warning | Stale/fake-null preview binds until next successful recompute |
| NamerDecompPipeline.cs / NAMERDecomp.compute | 83 / 180-216; 84/202 vs shader 137 | Reduce block factor and heatmap 0.25 bound duplicated C#/HLSL (IN-02/IN-03); unused `_Verts` upload (IN-01); 8-bit stats quantization (IN-04); RGBA32 linear readback precision (IN-05); N identical mesh assets for multi-material (IN-06); equal-weight partial blocks (IN-07) | ℹ️ Info | Drift/maintenance and minor precision concerns |

Debt-marker gate: zero `TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER` matches across all 17 phase files.

### Human Verification Required

### 1. Live-editor decomposition preview quality

**Test:** Open `Tools > NAMER > Processor` on a real textured FBX, enable "Vertex Color Decomposition", and exercise the flow: move the Error Threshold slider, switch the Residual Resolution popup, watch the Statistics block populate, and click the three new debug toolbar channels (Vertex Colors / Residual / Error Heatmap).
**Expected:** After-pane switches to the split mesh with fitted vertex colors; stats show live Coverage % / Avg / Max / Residual rows; the Error Heatmap shows a viridis (violet-low) map concentrated where the fit is poor; controls disabled when the toggle is OFF; nothing written to disk during preview tweaks.
**Why human:** Rendered-pixel correctness of the heatmap/preview parity, debounce cadence, and visual quality of the fitted reconstruction are visual/real-time properties; headless tests assert wiring, not appearance.

### 2. Real-asset spot check (after gap closure)

**Test:** After the CR-01/02/03 fixes land, Process a real multi-material FBX and a tiling-UV environment asset in the live editor and inspect the generated NAMERGenerated/ outputs in-spector (mesh vertex colors, residual EXR filter mode, material bindings).
**Expected:** Multi-material selections either produce per-slot-correct vertex colors or a blocking warning (never silent wrong colors); tiling-UV assets keep a residual or warn; the saved material visually matches the before-pane at the chosen residual resolution.
**Why human:** In-editor inspection of generated assets and visual match of before/after panes on real artist assets cannot be observed by grep; the automated regression tests use synthetic fixtures.

### Gaps Summary

The phase built everything it planned, at full depth, and the plan-level wiring is complete and test-proven: the seam-safe splitter and Burst LSQ fitter are real algorithms (not stubs), the four compute kernels implement the documented quotient/coverage/MAE/reduce contracts exactly, the processor/window/generator wiring is complete with no disk-write leaks in preview, and 16 new tests joined a 98/98-green suite. The goal fails on one of four success criteria — the one that matters most — because the reconstruction guarantee ("within reported error") only holds for the single-material, single-mesh, non-tiling inputs the tests synthesize:

1. **CR-01 (multi-material/multi-mesh):** `ResolveSourceMesh(selection)` picks one mesh for the whole selection and every material's fit runs against it; `generatedMeshBySourceMeshId` then overwrites entries so the last material's split mesh wins the renderer swap. Multi-material meshes — a first-class `SourceInspector` path — render wrong vertex colors while Process reports success. Confirmed at `NamerProcessor.cs:105,128,254-263`; `SourceInspector.cs:258`.
2. **CR-03 (tiling UVs):** The rasterizer has no UV wrap handling, so triangles outside [0,1] cover zero texels; uncovered texels feed `MaxError = 0` and `MinAlpha = 1` into the D-13 gate, which reads that as a perfect opaque fit and drops the residual — while the CPU fitter clamps out-of-range UVs to edge texels, so the written vertex colors are garbage. Silent wrong output. Confirmed at `NAMERDecomp.compute:81,160`, `NamerDecompPipeline.cs:210-215`, `VertexColorFitter.cs:433-436`.
3. **CR-02 (persisted residual filter mode):** The residual EXR is imported `FilterMode.Point` (copied from the bit-packed surface path), but the adaptive search and the reported `MaxError` are computed under bilinear resampling, and `NamerDecompOutput` documents bilinear runtime sampling. Saved materials render blocky and exceed the reported error exactly when the adaptive search succeeds (ChosenResolution < source). Confirmed at `AssetGenerator.cs:437`.

The 98/98-green suite cannot see any of these because every decomposition fixture is single-material, single-mesh, [0,1]-UV, and 64-512px (where the ladder picks full resolution). Five additional warnings (empty-mesh exception escape, zero-tangent fallback, non-square handling, Gamma-project heatmap mixing, stale preview binds) and seven info items are catalogued above from the phase review and re-confirmed where load-bearing.

**Re-verification recommendation:** fix CR-01/CR-02/CR-03 with the missing items listed in the frontmatter gaps (including regression tests for multi-material, tiling-UV, and reduced-resolution residual round-trip), then re-run verification; the remaining warnings can be scheduled separately.

---

_Verified: 2026-09-01T00:45:22Z_
_Verifier: Claude (gsd-verifier)_

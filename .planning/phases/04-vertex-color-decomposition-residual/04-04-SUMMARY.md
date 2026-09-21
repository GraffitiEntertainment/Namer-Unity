---
phase: 04-vertex-color-decomposition-residual
plan: 04
subsystem: vertex-color-decomposition
tags: [csharp, unity, integration-test, regression, gap-closure]

# Dependency graph
requires:
  - phase: 04-vertex-color-decomposition-residual
    plan: 01
    provides: MeshVertexSplitter (NamerSplitResult), VertexColorFitter (VertexColorFitResult.ToColor32Array)
  - phase: 04-vertex-color-decomposition-residual
    plan: 02
    provides: NamerDecompPipeline.GenerateResidual (NamerDecompOutput), NamerConstants.VcFloor
  - phase: 04-vertex-color-decomposition-residual
    plan: 03
    provides: AssetGenerator.WriteResidualExr (RGBAHalf EXR write), NamerProcessor decomposition stage + sharedMesh swap
  - phase: 03-asset-generation-editor-workflow-preview
    provides: SourceInspector.AddUnique material dedupe, NamerProcessor orchestration, BindGeneratedMaterials
provides:
  - NamerProcessor.CountDistinctSourceMeshes + CR-01 multi-material/multi-mesh decomposition guard (decomposeSourceMesh = null fallback)
  - AssetGenerator.WriteResidualExr FilterMode.Bilinear importer stamp (CR-02; surface texture stays FilterMode.Point per GEN-04)
  - Three new headless [UnityTest] regression cases in NamerDecompIntegrationTests
affects:
  - Phase 4 verification/UAT — CR-01/CR-02 silent-wrong-output defects closed
  - Phase 5 (stylization) — reconstructs over a correctly non-decomposed (Phase-3) shape for multi-material/multi-mesh selections

# Tech tracking
tech-stack:
  added: []
  patterns:
    - Multi-mesh counting mirrors ResolveSourceMesh's per-case resolution but collects a HashSet<int> of mesh instance IDs instead of stopping at the first mesh
    - Guard uses a ternary (decomposeSourceMesh != null ? CountDistinctSourceMeshes(selection) : 0) so PrefabUtility.LoadPrefabContents/UnloadPrefabContents is skipped when decomposition is disabled
    - Residual EXR importer stamps Bilinear while the packed surface texture stays Point (bit-packed alpha cannot survive interpolation)

key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs

key-decisions:
  - "CR-01 guard falls back by setting decomposeSourceMesh = null (never re-architecting BindGeneratedMaterials), so every material in the loop takes the existing decomp == null Phase-3 path and renderers keep their source mesh"
  - "CountDistinctSourceMeshes mirrors ResolveSourceMesh's per-case resolution but collects distinct mesh instance IDs, catching the shared-material multi-mesh case that model.Materials.Count == 1 misses"
  - "CR-02 stamps the residual EXR FilterMode.Bilinear (not Point) because it is a plain float map sampled with hardware bilinear at runtime; the packed surface texture alone keeps FilterMode.Point (GEN-04)"

requirements-completed: [VCOL-01, VCOL-05, TEST-02]

# Metrics
duration: 2min
completed: 2026-09-01
---

# Phase 4 Plan 04: Gap Closure (CR-01 + CR-02) Summary

**Closes two Phase-4 verification gaps — the CR-01 multi-material / shared-material multi-mesh decomposition guard (falls back to the non-decomposed Phase-3 shape with a warning instead of silently swapping the wrong mesh) and the CR-02 residual-EXR `FilterMode.Bilinear` import stamp — each paired with a regression test. Both changes were verified pre-existing in the 04-04 WIP snapshot rather than re-implemented.**

## Performance

- **Duration:** 2 min (verification of pre-existing implementation + state sync)
- **Completed:** 2026-09-01
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

1. **CR-01 multi-material / multi-mesh guard (Task 1)** — `NamerProcessor.Process` hoists `int distinctSourceMeshes = decomposeSourceMesh != null ? CountDistinctSourceMeshes(selection) : 0;` and guards on `decomposeSourceMesh != null && (model.Materials.Count > 1 || distinctSourceMeshes > 1)`, adding the `"Vertex-color decomposition skipped for '..."` warning and setting `decomposeSourceMesh = null` so every material takes the existing Phase-3 fallback. The private `CountDistinctSourceMeshes` helper mirrors `ResolveSourceMesh`'s per-case resolution but collects a `HashSet<int>` of mesh instance IDs (scene renderers → prefab contents → model/FBX sub-assets). Two regression tests cover the multi-material-slot case (`CreateTwoSubMeshQuadAsset` + a `CreateSceneObject(Mesh, Material[], string)` overload) and the shared-material multi-mesh case (`CreateMultiMeshSceneObject`, two distinct quad assets wearing one shared material).

2. **CR-02 residual-EXR bilinear import (Task 2)** — `AssetGenerator.WriteResidualExr` stamps `importer.filterMode = FilterMode.Bilinear;` (was `Point`) with a comment stating the residual is a plain float map sampled with hardware bilinear at runtime, so a reduced-resolution residual matches the bilinear-resampled `MaxError` the adaptive search reports. The `WriteSurfaceTexture` site keeps `FilterMode.Point` (GEN-04 — bit-packed alpha cannot survive interpolation). The regression test `Process_ReducedResolutionResidual_StampsBilinearImporter` drives a 256 px checkerboard source with `ResidualResolution = 5` (→ `ResolutionLadder[4] = 128`) and asserts the full D-02 importer contract (Bilinear / no mips / Repeat / linear / Uncompressed) plus `width == 128`.

## Task Commits

No new per-task commits were required — both tasks were already implemented and committed before this executor ran:

1. **Task 1: CR-01 guard + `CountDistinctSourceMeshes` + two regression tests** — `5ff09ac` ("wip: snapshot before 04-04 execution")
2. **Task 2: CR-02 `WriteResidualExr` bilinear change** — `dad71ec` ("test message")
3. **Task 2: CR-02 regression test** — `5ff09ac`

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — CR-01 guard + `CountDistinctSourceMeshes` (pre-existing, commit `5ff09ac`)
- `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs` — `WriteResidualExr` bilinear stamp + comment (pre-existing, commit `dad71ec`)
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs` — 3 regression cases + helpers `CreateTwoSubMeshQuadAsset` / `CreateSceneObject(Mesh, Material[], string)` / `CreateMultiMeshSceneObject` (pre-existing, commit `5ff09ac`)

## Decisions Made

- **The guard sets `decomposeSourceMesh = null` rather than re-architecting per-material mesh resolution.** This makes every material in the loop take the existing `decomp == null` Phase-3 path, so both disjuncts (multi-material and multi-mesh) produce correct non-decomposed output plus a warning instead of silent garbage — no edit touches `BindGeneratedMaterials`.
- **`CountDistinctSourceMeshes` collects distinct instance IDs, not a first-hit mesh.** This is load-bearing: a shared-material multi-mesh selection dedupes to `model.Materials.Count == 1` (via `SourceInspector.AddUnique`), so the `distinctSourceMeshes > 1` disjunct is the only thing that catches it.

## Deviations from Plan

### Pre-existing Implementation (verified, not re-implemented)

**1. [Rule - Critical-note stale] Task 2 production change was already applied, not missing**
- **Found during:** Task 2 verification
- **Issue:** The execution brief stated `AssetGenerator.cs WriteResidualExr` "still has `importer.filterMode = FilterMode.Point;` (~line 437)" and predicted the `Process_ReducedResolutionResidual_StampsBilinearImporter` test would fail. In fact the working tree (HEAD `5ff09ac`) already contained the change at line 440: `importer.filterMode = FilterMode.Bilinear;` (committed in `dad71ec`).
- **Resolution:** Verified the change line-by-line against the plan's acceptance criteria rather than re-applying it. `grep -c 'filterMode = FilterMode.Point'` returns exactly 1 (the GEN-04 `WriteSurfaceTexture` site), and `filterMode = FilterMode.Bilinear` is present inside `WriteResidualExr`. No gap existed.
- **Files verified:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs`
- **Committed in:** `dad71ec` (pre-existing)

**2. [Rule - Verified pre-existing] Task 1 CR-01 guard + regression tests fully present**
- **Found during:** Task 1 verification
- **Issue:** Task 1 (guard + `CountDistinctSourceMeshes` + three tests) was already implemented in the WIP snapshot. Verified against every acceptance criterion (helper present, guard condition exact, warning literal, both tests + all four helpers + all assertions present). No gap existed.
- **Resolution:** Recorded "Task 1 verified pre-existing (commit 5ff09ac)" — no new commit needed.
- **Files verified:** `NamerProcessor.cs`, `NamerDecompIntegrationTests.cs`
- **Committed in:** `5ff09ac` (pre-existing)

**3. [Rule 1 - Bug] CR-01 regression tests asserted the SOURCE base map, not the generated base PNG**
- **Found during:** Orchestrator live-editor EditMode run (99 PASS / 2 FAIL — both CR-01 tests)
- **Issue:** `Process_MultiMaterialSelection_FallsBackToPhase3WithWarning` and `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning` asserted `_BaseResidualMap == baseMapA/baseMapB/baseMap` (the SOURCE base map). The fallback — and the true Phase-3 path — bind the GENERATED base PNG: `AssetGenerator.Generate` (line 184) sets `baseResidualPath = decomp != null ? residualWritePath : basePath`, and `WriteMaterial` binds `LoadAssetAtPath(basePath)`. The cleaned/generated base is a different `Texture2D` asset than the source base map, so both assertions failed even though the production fallback was already byte-equivalent to a `DecompositionEnabled == false` run.
- **Fix:** Corrected both assertions to compare against `AssetDatabase.LoadAssetAtPath<Texture2D>(... .BaseTexturePath)` — the generated base PNG — exactly matching the passing `Process_WithDecompositionOff_KeepsPhase3Shape` test (lines 128-130). This is a test-reference correction, not a loosening: the assertion still verifies "base bound, not residual/nothing". No production change was required.
- **Files modified:** `Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs`

---

**Total deviations:** 3 (2 "verified pre-existing", 1 auto-fixed test-reference bug)
**Impact on plan:** CR-01/CR-02 production was already correct and committed; the only code change was correcting the two CR-01 test references to match the Phase-3 binding contract.

## Issues Encountered

- The execution brief's `<critical_preexisting_work>` note claimed Task 2's production change was missing (`FilterMode.Point` at ~line 437), but the change was already committed in `dad71ec`. The note was stale relative to HEAD `5ff09ac`.
- Commit `dad71ec` carries a non-descriptive message ("test message") for the CR-02 production change. This is noted for traceability; it was not rewritten (no history rewrite performed).
- **1Password SSH signing blocked the fix commit** — after the successful `76e0075` commit, 1Password locked and `op-ssh-sign` began failing (`failed to fill whole buffer` → `agent returned an error`). The test-reference fix is staged but uncommitted pending 1Password unlock.

## Verification

- Static `grep` gates from the plan passed for both tasks (guard literal, `FilterMode.Bilinear` in `WriteResidualExr`, exactly one `FilterMode.Point` file-wide, all three test methods + assertions present).
- **Orchestrator live-editor EditMode run: 99 PASS / 2 FAIL.** CR-02 (`Process_ReducedResolutionResidual_StampsBilinearImporter`) passed green. The 2 failures were both CR-01 tests failing only on the `_BaseResidualMap` assertion — root-caused as a test-reference bug (asserted the source base map instead of the generated base PNG) and fixed (see deviation 3). Awaiting orchestrator re-run.
- **EditMode re-run still requires the live editor** (a batchmode Unity run would exit 134 against the held project lock). The orchestrator must re-run the two corrected CR-01 cases.

## User Setup Required

None — no external service configuration required. All changes are C# + Unity built-ins.

## Next Phase Readiness

- CR-01 and CR-02 are closed in code with regression coverage. The three new integration tests join the existing suite (98 EditMode + 3 PlayMode) pending the orchestrator's live-editor run.
- Phase 04-05 (gap closure — tiling-UV repeat-wrap CPU sampling + coverage < 0.5 guard) remains the final Phase-4 gap.

---

*Phase: 04-vertex-color-decomposition-residual*
*Completed: 2026-09-01*

## Self-Check: PASSED (code) / COMMIT BLOCKED (1Password)

All three modified files exist and their changes are committed (`5ff09ac` for Task 1 + tests, `dad71ec` for Task 2 production; metadata in `76e0075`). The plan's `grep` gates all passed. The follow-up CR-01 test-reference fix is staged but its commit is blocked by a locked 1Password SSH-signing agent (`op-ssh-sign` → "agent returned an error"); it must be committed once 1Password is unlocked, then the two corrected cases re-run by the orchestrator.

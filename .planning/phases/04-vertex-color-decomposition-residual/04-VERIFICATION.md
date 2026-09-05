---
phase: 04-vertex-color-decomposition-residual
verified: 2026-09-05T10:56:11Z
status: verified
score: 17/17 must-haves verified (4/4 roadmap success criteria + 13/13 plan truths)
overrides_applied: 0
re_verification:
  previous_status: gaps_found
  previous_score: 16/17
  verified_against: "HEAD bf3791f (clean at session start; see Working-Tree Note)"
  gaps_closed:
    - "SC1 / CR-01: multi-material + shared-material multi-mesh decomposition guard with Phase-3 fallback and warning"
    - "SC1 / CR-02: residual EXR imports FilterMode.Bilinear (saved-asset error now matches the reported bilinear MaxError)"
    - "SC1 / CR-03: CPU SampleBase Repeat-wrap + zero-coverage guard before the D-13 gate + CannotDecompose Phase-3 fallback"
  gaps_remaining: []
  regressions: []
human_verification:
  - test: "Real-asset spot check (deferred from the prior round, now unblocked): in the live editor Process a real multi-material FBX, a shared-material multi-mesh selection, a tiling-UV environment asset, and one asset with an adaptively reduced residual; inspect NAMERGenerated/ outputs in the inspector"
    expected: "Multi-material / multi-mesh / tiling inputs each show the skip warning and a correct non-decomposed (Phase-3) material; the reduced-resolution case's residual EXR imports Bilinear and the after-pane visually matches at the chosen resolution"
    why_human: "In-editor inspection of generated assets and visual before/after match on real artist assets; the regression tests use synthetic fixtures"
  - test: "Live decomposition preview quality: enable Vertex Color Decomposition, move Error Threshold, switch the Residual Resolution popup, watch Coverage/Avg/Max populate, click the Vertex Colors / Residual / Error Heatmap debug channels"
    expected: "Split-mesh preview with fitted vertex colors; live stats; viridis heatmap concentrated where the fit is poor; controls disabled when OFF; no disk writes during preview"
    why_human: "Rendered-pixel correctness, debounce cadence, and visual reconstruction quality are visual/real-time properties"
---

# Phase 4: Vertex-Color Decomposition + Residual — Verification Report (Gap-Closure Re-Verification)

**Phase Goal:** Fit low-frequency base color into vertex colors via per-triangle barycentric least-squares with seam-safe splitting, and generate an adaptive residual texture with error reporting
**Verified:** 2026-09-05T10:56:11Z
**Status:** verified
**Re-verification:** Yes — after closure of the three gaps (CR-01/CR-02/CR-03) from the 2026-09-01 round

**Mode note:** Phase is `mode: mvp`; the ROADMAP goal is not User Story format (same condition as Phases 1-3 and the prior round). Verification proceeds goal-backward against the four Success Criteria.

## Verification Basis (independently checked, not taken from SUMMARYs)

- **Verified against HEAD `bf3791f`** — the exact state 04-REVIEW.md examined and the live-editor suite executed. Working tree was clean at session start (see [Working-Tree Note](#working-tree-note-uncommitted-warning-fixes-landed-mid-verification) for concurrent changes that are NOT part of this verdict).
- All three prior gaps re-derived from the current code by this verifier (below), not trusted from the SUMMARYs or the review.
- All five gap-closure regression tests read in full and confirmed substantive (real fixtures, real assertions, cleanup discipline).
- Test-run evidence: the claimed results file `Temp/gsd-editmode-gaps4.txt` (103 PASS / 0 FAIL) was **deleted** (Unity's `Temp/` is transient). Substituted corroboration: `~/Library/Logs/Unity/Editor.log` (mtime Sep 5 03:49, after the final code commits `0986374`/`4397537` at 03:37) contains execution traces of all five new tests — `Process_MultiMaterialSelection_FallsBackToPhase3WithWarning`, `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning`, `Process_ReducedResolutionResidual_StampsBilinearImporter`, `Process_TilingUvMesh_FallsBackToPhase3WithWarning`, `Fit_WrapsUvsOutsideZeroOne` — across multiple full-suite cycles, with no assertion-failure or exception traces for them. The count arithmetic is internally consistent across rounds (98 → +3 tests = 101 → +2 tests = 103). The exact 103/0 tally remains human-attested; the load-bearing facts (tests exist, are substantive, executed against final code, no failure traces) are independently confirmed.

## Gap Closure Verification (the three prior BLOCKERs)

### CR-01 — multi-material / shared-material multi-mesh guard — CLOSED

- **Guard:** `NamerProcessor.cs:117-124` — `int distinctSourceMeshes = decomposeSourceMesh != null ? CountDistinctSourceMeshes(selection) : 0;` then `if (decomposeSourceMesh != null && (model.Materials.Count > 1 || distinctSourceMeshes > 1))` adds the `"Vertex-color decomposition skipped for '...' ... material(s) to ... source mesh(es)"` warning and sets `decomposeSourceMesh = null`, so every material takes the existing `decomp == null` Phase-3 path (`NamerProcessor.cs:145-185`).
- **Counter:** `CountDistinctSourceMeshes` (`NamerProcessor.cs:541-619`) mirrors `ResolveSourceMesh`'s scene-renderer / prefab-contents / sub-asset cases, collecting a `HashSet<int>` of mesh instance IDs. The `distinctSourceMeshes > 1` disjunct is load-bearing for the shared-material multi-mesh case because `SourceInspector.AddUnique` dedupes shared materials to `model.Materials.Count == 1` (verified at `SourceInspector.cs:300-312`).
- **No wrong binding on fallback:** fallback assets carry empty `MeshPath`/`ResidualTexturePath`, the material binds the generated base PNG (`AssetGenerator.cs:184`), and `BindGeneratedMaterials` only maps a mesh swap when `MeshPath` is non-empty (`NamerProcessor.cs:285-294`).
- **Tests:** `Process_MultiMaterialSelection_FallsBackToPhase3WithWarning` (`NamerDecompIntegrationTests.cs:196-254`) and `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning` (`:257-315`) — both assert the warning, no mesh/residual assets, base-PNG bind at `_BaseResidualMap`, and no renderer mesh swap; the multi-mesh fixture additionally asserts the two mesh assets are distinct instances. Both bind against the GENERATED base PNG (the corrected 04-04 test-reference fix). Executed post-commit per Editor.log.

### CR-02 — residual EXR bilinear import — CLOSED

- `AssetGenerator.cs:440` — `importer.filterMode = FilterMode.Bilinear;` in `WriteResidualExr`, with the full D-02 contract intact (linear / uncompressed / no-mips / Repeat, `:437-443`). `WriteSurfaceTexture` keeps `FilterMode.Point` (`:388`, GEN-04) — exactly one `FilterMode.Point` site remains (the bit-packed surface texture).
- **Test:** `Process_ReducedResolutionResidual_StampsBilinearImporter` (`NamerDecompIntegrationTests.cs:318-371`) drives a 256px checkerboard with `ResidualResolution = 5` → ladder index 4 = 128px and asserts the residual exists, is 128px wide, and imports Bilinear / no-mips / Repeat / linear / uncompressed — i.e. the saved artifact now matches the bilinear-resampled `MaxError` the adaptive search reports.

### CR-03 — tiling-UV wrap + zero-coverage guard + fallback — CLOSED

- **CPU wrap:** both `SampleBase` methods Repeat-wrap out-of-range UVs (`VertexColorFitter.cs:437-438` and `:518-519`: `u - floor(u / BaseWidth) * BaseWidth`, plus `(x0 + 1) % BaseWidth` at `:442-443`/`:523-524` for the bilinear neighbor); the old edge-clamp is gone. Math verified: matches hardware `TextureWrapMode.Repeat` at `uv * W - 0.5` for in-range, tiling, and negative UVs.
- **Coverage guard BEFORE the D-13 gate:** `NamerDecompPipeline.cs:217-228` — `if (fitStats.CoverageFraction < kMinCoverageFraction)` returns `NamerDecompOutput` with `CannotDecompose = true`; the D-13 gate only runs afterwards (`:232-237`). The rasterizer writes every texel — uncovered → `(0,0,0,0)` (`NAMERDecomp.compute:74-117`, explicit fall-through write), so no stale pooled-RT data can leak into the stats. `kMinCoverageFraction = 1e-6f` (`:87`) does not trip the ~25%-coverage `Residual_UncoveredTexels_AreIdentity` fixture (test read and confirmed intact).
- **Fallback:** `NamerProcessor.cs:165-173` — `CannotDecompose` leaves `decomp` null and adds the `"UV coverage near zero (tiling/out-of-range UVs)"` warning; disposal is safe (`decompOutput.Dispose()` with null `Residual`; `ComputeTexturePool.Release` null-checks).
- **Tests:** `Fit_WrapsUvsOutsideZeroOne` (`VertexColorDecompTests.cs:176-219`, headless: [1.25,1.75]-UV triangle against a ramp; asserts fit lands in [0.15,0.85] — the old clamp would have produced the white edge texel) and `Process_TilingUvMesh_FallsBackToPhase3WithWarning` (`NamerDecompIntegrationTests.cs:374-424`: [1,2]²-UV quad; asserts warning, no mesh/residual, base-PNG bind, no swap). Both executed post-commit per Editor.log.

## Goal Achievement

### Observable Truths (ROADMAP Phase 4 Success Criteria)

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | User enables vertex-color decomposition and the output mesh's vertex colors reconstruct the low-frequency base color within reported error | ✓ VERIFIED | Algorithm/pipeline unchanged and test-proven (2/255 GPU golden vs CPU oracle; constant-color fit 1e-3). The three silent-wrong-output holes are closed with loud, fail-safe fallbacks: CR-01 (multi-material/multi-mesh guard, `NamerProcessor.cs:117-124`), CR-02 (Bilinear residual import, `AssetGenerator.cs:440` — saved-asset error now consistent with the reported bilinear MaxError), CR-03 (CPU Repeat wrap + coverage guard before the D-13 gate, `VertexColorFitter.cs:437-443`, `NamerDecompPipeline.cs:217-228`, `NamerDecompPipeline.cs` wrap + `NamerProcessor.cs:165-173`). Five regression tests cover all three. Remaining caveats WR-01/WR-03 are fail-safe-direction or rare-corner quality issues (judged below) — none produce silent wrong colors on the supported paths. |
| 2 | Output mesh splits vertices at UV seams and discontinuities while preserving mesh attributes | ✓ VERIFIED (regression check) | Unchanged from the prior round: `MeshVertexSplitter.cs` quantized (position, normal, tangent, uv) `WeldKey` dictionary (`:103-137`, `:184-255`), attribute streams re-emitted verbatim, boneWeights/bindposes preserved; 4 split tests in the suite. Known caveat (prior WR-02, unchanged): tangent-less sources get fabricated (0,0,0,0) tangents written verbatim. |
| 3 | Residual texture captures the difference between fitted vertex-color interpolation and the source texture | ✓ VERIFIED (regression check) | Unchanged: multiplicative quotient `base / max(vc.rgb, _VcFloor)` from the QUANTIZED Color32 upload (`NAMERDecomp.compute` CSResidual), coverage-mask identity branch, base-alpha preservation; `Residual_ReconstructsBase_WithinByteTolerance` (2/255 vs CPU oracle) and `Residual_UncoveredTexels_AreIdentity` intact and executing. CR-02 additionally makes the persisted EXR apply this difference as designed at reduced resolutions. |
| 4 | Processor reports reconstruction-error statistics and adapts residual resolution with manual override | ✓ VERIFIED (regression check) | Unchanged: `NamerDecompErrorStats` (Coverage/AvgError/MaxError/ResidualRequired/ChosenResolution) from one consistent MAE through the hierarchical `CSReduce`; D-13 gate with alpha guard; `ResolutionLadder {2048,1024,512,256,128}` + popup-index manual override; window Statistics block (`NamerEditorWindow.cs:902-919`) and controls (`:836-871`) intact. `GenerateResidual_AdaptiveAndManualOverride` and `GenerateResidual_Gate_DistinguishesConstantVsGradient` in the executing suite. New caveat: WR-01 quantizes the reported coverage/guard threshold to ~0.2% (judged below). |

**Score:** 4/4 roadmap success criteria (+ 13/13 plan-level truths from the prior round, regression-checked)

### Plan-Level Must-Have Truths (regression check)

All 13 plan truths from the prior round re-checked at sanity level (existence + core markers): splitter weld key, Burst Gram/Cramer fitter (`Solve3x3`, `kGrid = 16`, `[BurstCompile] IJobParallelFor`), Color32 quantize + quality alpha, quotient kernel + `NAMER_VC_FLOOR`, single-MAE metric, adaptive ladder + override, GPU-only per-pixel work, full asset-set generation, `_BaseResidualMap` binding + guarded mesh swap, window controls/stats/10 debug channels, auto-drop gate, SHA-256 source immutability. All intact; no regressions from the gap-closure edits.

### Behavioral Spot-Checks / Probe Execution

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| EditMode full suite (claimed 103 PASS / 0 FAIL) | live editor TestRunnerApi | Results file deleted; substituted Editor.log evidence | ✓ PASS (evidence-based; count human-attested) |
| Phase-4 regression tests executed against final code | Editor.log trace search | All 5 new test frames present in post-commit run cycles; no assertion/exception traces | ✓ PASS |
| Debt-marker gate | `grep -rE "TBD\|FIXME\|XXX\|HACK\|PLACEHOLDER"` over Editor/, Compute/, Tests/ | Zero matches | ✓ PASS |

No probes declared in PLAN/SUMMARY; no `scripts/*/tests/probe-*.sh` exist. N/A.

## Judgment: Do the Four New Review Warnings Block Goal Achievement?

**No. All four are quality issues that do not invalidate any success criterion.** All four were independently re-derived in the code by this verifier (not trusted from 04-REVIEW.md):

| Warning | Verified at | Why it does not block |
|---------|-------------|----------------------|
| **WR-01** — RGBA32 stats readback quantizes the coverage epsilon: the `kMinCoverageFraction = 1e-6f` guard effectively trips below ~0.5/255 ≈ 0.196% coverage, and the message then wrongly says "tiling/out-of-range UVs" | `NamerDecompPipeline.cs:435` (`TextureFormat.RGBA32`), `:458` (`c.b / 255f`), `:217` (guard), with float `R16G16B16A16_SFloat` chain at `:182-189` | **Fail-safe direction.** A small-footprint asset (e.g. 64x64 islands on a 2048 atlas ≈ 0.098%) is REFUSED decomposition and falls back to a correct Phase-3 output with a (mis-worded) warning. It never produces wrong vertex colors and never under-reports error — SC1's contract holds whenever decomposition actually produces a mesh. Availability limitation for an edge class + misleading diagnostic = quality issue, not a criterion failure. |
| **WR-02** — CR-01 fallback also emits a contradictory per-material "No mesh to decompose" warning | `NamerProcessor.cs:148-153` (fires whenever `decomposeSourceMesh == null`, including after a guard trip) | Purely diagnostic noise (N+1 warnings, one contradicting the other). Tests assert the accurate warning exists. Cosmetic. |
| **WR-03** — `CountDistinctSourceMeshes` skips `FindMeshSubAsset` in the prefab branch, so a prefab whose Mesh sub-asset differs from its renderers' meshes can pass the guard | `NamerProcessor.cs:479-483` (resolver prefers sub-asset) vs `:567-591` (counter walks only contents renderers) | Rare corner (requires an embedded Mesh sub-asset differing from the renderer meshes — ordinary prefabs reference external FBX meshes, where `FindMeshSubAsset` returns null and both methods agree). For prefab (asset) selections `BindGeneratedMaterials` early-returns (`:268-271`), so Process itself never swaps a wrong mesh; the mismatch only materializes if the user manually assigns the residual-bound material. Highest-priority of the four (it re-opens a CR-01-shaped failure mode in a corner), but not on any mainstream path. |
| **WR-04** — mid-`Generate` failure (residual readback / importer / shader-missing) leaves a partial asset set and the thrown "no files were written" message is then false | `AssetGenerator.cs:162-185` (`ReadBackResidual` at 176, after writes at 162/163/169), message at `:473` | Error-path robustness only (requires a GPU readback failure or missing importer mid-run); pre-existing ordering, widened by the delta. Does not affect any success criterion on the success path. |

**Explicit recommendation:** schedule WR-01 (small, well-understood fix: `RGBAFloat` readback) and WR-03 (mirror the resolver priority) as the priority pair; WR-02/WR-04 are cleanup. None require re-opening this phase.

## Working-Tree Note: Uncommitted Warning Fixes Landed Mid-Verification

During this verification session, **uncommitted** changes appeared in the working tree (5 files, +259/-15) implementing fixes for all four warnings: WR-01 (`RGBAFloat` readback + `forcePlayerLoopUpdate`), WR-02 (`decompGuardTripped` flag), WR-03 (sub-asset counting + new `Process_PrefabWithDistinctMeshSubAsset_FallsBackToPhase3WithWarning` test), WR-04 (residual readback hoisted before the first disk write), plus a `GenerateResidual_TinyUvFootprint_StillDecomposes_AndZeroCoverageStillFallsBack` regression test and warning-count assertions. These changes are **concurrent work, NOT part of the verified state**: they postdate the 103/0 test run, have not been through the live-editor suite, and are uncommitted. This verdict is unaffected (the warnings were judged non-blocking at HEAD), but the orchestrator must either run these through the live-editor EditMode suite and commit them as a follow-up, or discard them — they must not silently ride into the phase close.

## Requirements Coverage

| Requirement | Status | Evidence |
|-------------|--------|----------|
| VCOL-01 | ✓ SATISFIED | `VertexColorFitter` per-triangle multi-sample barycentric LSQ (Gram + `Solve3x3`); wrap fix strengthens the fit for tiling UVs; `Fit_WrapsUvsOutsideZeroOne` |
| VCOL-02 | ✓ SATISFIED | `MeshVertexSplitter` weld key; 4 split tests (unchanged) |
| VCOL-03 | ✓ SATISFIED | Quotient residual kernel; 2/255 reconstruction invariant; CR-02 makes the persisted artifact consistent at reduced resolutions |
| VCOL-04 | ✓ SATISFIED | Stats + heatmap channel intact; CR-03 guard fixes the zero-coverage "perfect fit" blind spot (caveat: WR-01 threshold precision) |
| VCOL-05 | ✓ SATISFIED | Ladder + manual override, test-asserted (128px assertion in the CR-02 test) |
| TEST-02 | ✓ SATISFIED | 16 phase-4 tests + 5 gap-closure regression tests, all executing in the live editor |

No orphaned requirements.

## Anti-Patterns Found

Zero debt markers (`TBD`/`FIXME`/`XXX`/`TODO`/`HACK`/`PLACEHOLDER`) across Editor/, Compute/, Tests/. Prior-round warnings WR-01..WR-05 (old numbering: empty-mesh exception escape, zero-tangent fallback, non-square handling, Gamma heatmap, stale preview binds) and info items remain as documented quality follow-ups; the four current-review warnings are judged above.

## Human Verification Required

1. **Real-asset spot check (deferred from the prior round — now unblocked):** in the live editor, Process a real multi-material FBX, a shared-material multi-mesh selection, a tiling-UV environment asset, and one asset with an adaptively reduced residual; inspect `NAMERGenerated/` outputs. Expected: skip warnings + correct Phase-3 materials for the first three; Bilinear residual EXR and a visually matching after-pane for the reduced case. Why human: in-editor inspection and visual match on real artist assets; the regression tests use synthetic fixtures.
2. **Live decomposition preview quality:** exercise the toggle/threshold/ladder, stats block, and the three debug channels. Expected: split-mesh preview with fitted colors, live stats, viridis heatmap, no disk writes during preview. Why human: visual/real-time properties.

## Gaps Summary

None. All three prior gaps are closed in committed code with regression coverage; no regressions detected in previously-passed truths; all four roadmap success criteria are met. The four new review warnings are quality issues that fail safe or live in rare corners and do not block goal achievement — with an in-flight uncommitted fix round already addressing them (to be tested and committed separately).

---

_Verified: 2026-09-05T10:56:11Z_
_Verifier: Claude (gsd-verifier)_

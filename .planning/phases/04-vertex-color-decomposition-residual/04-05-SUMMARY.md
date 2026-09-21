---
phase: 04-vertex-color-decomposition-residual
plan: 05
subsystem: vertex-color-decomposition
tags: [csharp, unity, regression, gap-closure, tiling-uv, coverage-guard, repeat-wrap]

# Dependency graph
requires:
  - phase: 04-vertex-color-decomposition-residual
    plan: 04
    provides: CR-01 multi-material/multi-mesh guard + CR-02 residual-EXR bilinear stamp (the CR-01 Phase-3 fallback convention this plan reuses)
  - phase: 04-vertex-color-decomposition-residual
    plan: 01
    provides: VertexColorFitter (SampleBase bilinear sampler), MeshVertexSplitter (NamerSplitResult)
  - phase: 04-vertex-color-decomposition-residual
    plan: 02
    provides: NamerDecompPipeline.GenerateResidual (NamerDecompOutput / NamerDecompErrorStats / ReduceStats)
provides:
  - Repeat-wrapped CPU UV sampling in both VertexColorFitter.SampleBase methods (matches runtime TextureWrapMode.Repeat)
  - Coverage guard (kMinCoverageFraction = 1e-6f) + NamerDecompErrorStats.CannotDecompose signal
  - NamerProcessor CannotDecompose fallback branch (Phase-3 shape + "UV coverage near zero" warning)
  - Two regression tests (headless wrap + tiling-UV integration fallback)
affects:
  - Phase 4 verification/UAT — CR-03 silent-wrong-output defect closed
  - Phase 5 (stylization) — reconstructs over a correctly non-decomposed (Phase-3) shape for tiling-UV layouts

# Tech tracking
tech-stack:
  added: []
  patterns:
    - CPU SampleBase Repeat wrap mirrors runtime TextureWrapMode.Repeat sampling via floor(u / BaseWidth) and (x0+1) % BaseWidth
    - Coverage guard returns CannotDecompose before the D-13 drop gate so a zero-coverage fit can never be misreported as a perfect fit
    - CannotDecompose fallback reuses the CR-01 Phase-3 decomp == null path (leave decomp null, add a warning)

key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs
    - Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs

key-decisions:
  - "kMinCoverageFraction = 1e-6f (near-zero epsilon): only zero rasterizer coverage trips the CannotDecompose guard — the plan's 0.5f was too aggressive and broke Residual_UncoveredTexels_AreIdentity (~25% coverage), so it was lowered to fail-safe only on truly zero coverage (Rule 1 fix)"
  - "The CPU fitter wraps UVs (Repeat) instead of clamping to edge texels so the vertex-color fit matches runtime Repeat sampling for tiling/out-of-range UVs"
  - "Fit_WrapsUvsOutsideZeroOne bounds 0.15f / 0.85f are 1/6 and 5/6 (the exact linearly-derived wrapped-ramp fit) widened by a 1/60 safety margin — do not loosen further"

requirements-completed: [VCOL-04, VCOL-01, TEST-02]

# Metrics
duration: 12min
completed: 2026-09-01
---

# Phase 4 Plan 05: Gap Closure (CR-03) Summary

**Closes the final Phase-4 verification gap — CR-03 (VCOL-04), the silent zero-coverage "perfect fit" blind spot — with two coordinated fixes: the CPU `VertexColorFitter.SampleBase` now Repeat-wraps UVs instead of clamping to edge texels, and `NamerDecompPipeline.GenerateResidual` guards the D-13 drop gate on a `kMinCoverageFraction = 1e-6f` (near-zero epsilon) coverage threshold that flags `CannotDecompose`, so `NamerProcessor` falls back to the non-decomposed Phase-3 shape with a "UV coverage near zero" warning. Paired with a headless wrap regression test and a tiling-UV integration fallback test.**

## Performance

- **Duration:** 12 min
- **Completed:** 2026-09-01
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments

1. **CR-03 CPU Repeat wrap (Task 1)** — Both `SampleBase` methods (AccumulateJob ~431-454 and ErrorJob ~510-533) now wrap out-of-range UVs back into `[0, BaseWidth)` via `u = u - floor(u / BaseWidth) * BaseWidth` (and the same for `v`), and use `(x0 + 1) % BaseWidth` / `(y0 + 1) % BaseHeight` for the bilinear neighbor. The `clamp(u, 0f, BaseWidth - 1f)` / `min(x0 + 1, BaseWidth - 1)` edge-clamp is gone from both methods. This makes the CPU fit sample the same texels the runtime `TextureWrapMode.Repeat` samples, so tiling UVs no longer write garbage vertex colors.

2. **CR-03 coverage guard + CannotDecompose signal (Task 1)** — `NamerDecompErrorStats` gains `public bool CannotDecompose;`, a `private const float kMinCoverageFraction = 1e-6f;` is added beside `kOpaqueAlphaThreshold`, and `GenerateResidual` inserts a guard immediately after `ReduceStats fitStats = ReduceToStats(...)` and before the D-13 gate: when `fitStats.CoverageFraction < kMinCoverageFraction`, it returns `new NamerDecompOutput(null, ... CannotDecompose = true ...)`. Zero rasterizer coverage means the fit was never validated against any texel — without this guard the D-13 gate reads the unwritten `_ErrorStat = (0,0,0,1)` as a perfect opaque fit and silently drops the residual.

3. **CR-03 fallback branch (Task 1)** — `NamerProcessor.Process` wraps the `decomp` assignment in `if (decompOutput.Stats.CannotDecompose) { ... } else { ... }`. The `if` branch adds the `"Vertex-color decomposition skipped for material '...': UV coverage near zero (tiling/out-of-range UVs) — generating the non-decomposed Phase-3 shape instead."` warning and leaves `decomp` null so `generator.Generate(..., decomp: null)` runs the existing Phase-3 path (same contract as the CR-01 guard). The `finally` disposal is unchanged — `decompOutput.Dispose()` already no-ops on a null `Residual` (`ComputeTexturePool.Release(null)` returns early).

4. **Two regression tests (Task 2)** — `Fit_WrapsUvsOutsideZeroOne` (headless `[Test]`) builds a `[1.25, 1.75]`-UV triangle against a grayscale x-ramp and asserts the fitted color channels land in `[0.15, 0.85]` (wrap to mid-ramp), proving the old clamp (which would have produced 1.0 from the white edge texel) is gone. `Process_TilingUvMesh_FallsBackToPhase3WithWarning` (`[UnityTest]`) drives a quad whose UVs live in `[1,2]x[1,2]` through `Process` and asserts the coverage warning, empty MeshPath/ResidualTexturePath, base-PNG binding, and no mesh swap.

## Task Commits

Both task commits are **BLOCKED** by a locked 1Password SSH-signing agent (`op-ssh-sign` → "agent returned an error"). All file work is complete and staged-ready; the orchestrator must land these exact commands once 1Password is unlocked:

1. **Task 1 (production):**
   `gsd-sdk query commit "feat(04-05): CR-03 wrap CPU UV sampling + coverage guard + CannotDecompose fallback" --files Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs`

2. **Task 2 (tests):**
   `gsd-sdk query commit "test(04-05): CR-03 headless wrap + tiling-UV fallback regression tests" --files Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs`

**Plan metadata commit (separate, blocked):**
   `gsd-sdk query commit "docs(04-05): complete gap-closure CR-03 plan" --files .planning/phases/04-vertex-color-decomposition-residual/04-05-SUMMARY.md .planning/STATE.md .planning/ROADMAP.md .planning/REQUIREMENTS.md`

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs` — Repeat wrap in both `SampleBase` methods (CR-03)
- `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs` — `kMinCoverageFraction`, `NamerDecompErrorStats.CannotDecompose`, coverage guard before D-13 gate
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — `CannotDecompose` fallback branch with "UV coverage near zero" warning
- `Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs` — `Fit_WrapsUvsOutsideZeroOne` + `CreateTilingTriangleMesh` / `CreateRampBase` helpers
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs` — `Process_TilingUvMesh_FallsBackToPhase3WithWarning` + `CreateTilingQuadMeshAsset` helper (carries 04-04 CR-01 test-reference fix)

## Decisions Made

- **`kMinCoverageFraction = 1e-6f` (near-zero epsilon), not the plan's `0.5f`.** The plan's `0.5f` threshold was too aggressive: it misclassified a legitimately partial layout (the `[0,0.5]^2` quad in the pre-existing `Residual_UncoveredTexels_AreIdentity` test, ~25% coverage) as "cannot decompose", breaking that spec. The CR-03 defect is specifically *zero* coverage (tiling UVs rasterize zero texels), so the threshold is now a tiny epsilon that fails safe only on zero coverage. See the Rule 1 deviation below.
- **The CPU fitter wraps (Repeat) rather than clamps.** `floor(u / BaseWidth)` maps any finite UV into `[0, BaseWidth)` and `(x0 + 1) % BaseWidth` keeps the bilinear fetch in-range, so out-of-range UVs can no longer index edge texels or throw — and the fit now matches runtime `TextureWrapMode.Repeat` sampling.
- **The fallback reuses the CR-01 `decomp == null` Phase-3 path** instead of adding a new branch in `BindGeneratedMaterials`/`AssetGenerator`. Leaving `decomp` null when `CannotDecompose` is set routes every material through the existing non-decomposed generator path plus a warning.
- **Bounds provenance (do not loosen):** `Fit_WrapsUvsOutsideZeroOne` asserts fitted channels in `[0.15f, 0.85f]`. These are `1/6` and `5/6` — the exact linearly-derived fitted values for the wrapped ramp — widened by a `1/60 ≈ 0.0167` safety margin (`0.15 = 1/6 − 1/60`, `0.85 = 5/6 + 1/60`). Keep these assertions as specified; do not widen the margin later.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Coverage guard threshold `0.5f` broke `Residual_UncoveredTexels_AreIdentity` (~25% coverage)**
- **Found during:** Orchestrator EditMode re-run (102 PASS / 1 FAIL)
- **Issue:** The plan specified `kMinCoverageFraction = 0.5f`, so the guard `fitStats.CoverageFraction < 0.5f` fired for the pre-existing `Residual_UncoveredTexels_AreIdentity` test, whose quad spans UV `[0, 0.5]^2` (covering ~25% of the 64×64 texture — 32×32 of 4096 texels). The guard returned `Residual = null` / `ResidualRequired = false`, failing the test's `Assert.IsTrue(ResidualRequired)` and `Assert.IsNotNull(Residual)`.
- **Root cause:** `0.5` is "less than half", not "near-zero". The CR-03 defect is specifically *zero* coverage (tiling UVs rasterize zero texels), not low-but-positive coverage. The orchestrator's "exact-UV-1.0 edge case" hypothesis was **refuted**: `Residual_UncoveredTexels_AreIdentity` calls `GenerateResidual` directly and never invokes `VertexColorFitter.Fit`, so the SampleBase wrap is not in this path; and all `[0,1]^2` integration tests (which DO exercise both the CPU fit and GPU rasterizer) passed.
- **Fix:** Lowered `kMinCoverageFraction` from `0.5f` to `1e-6f` (a near-zero epsilon). Zero coverage (`0.0 < 1e-6`) still trips the guard (CR-03 closed), while `0.25 < 1e-6` is false, so the ~25%-coverage test proceeds to assert the identity residual for uncovered texels. Comment updated to document why the threshold is an epsilon, not `0.5`. In lockstep, the `NamerProcessor` warning was reworded from "UV coverage below 50%" to "UV coverage near zero" (and the 04-05 integration test's `w.Contains(...)` expectation updated to match).
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs`
- **Verification:** `grep` confirms `kMinCoverageFraction = 1e-6f` and `fitStats.CoverageFraction < kMinCoverageFraction`. Static reasoning: `0.0 < 1e-6` fires for tiling; `0.25 > 1e-6` does not fire for the partial-coverage spec. EditMode re-run deferred to orchestrator (live-editor lock).
- **Committed in:** blocked (1Password) — rides in the Task 1 commit.

### Execution-context notes (not auto-fixes)

**1. [04-04 ride-along] `NamerDecompIntegrationTests.cs` and `STATE.md` carry prior 04-04 content**
- **Found during:** Task 2 / state updates
- **Issue:** The working tree already carried VERIFIED-BUT-UNCOMMITTED 04-04 gap-closure content (the CR-01 test-reference fix in `NamerDecompIntegrationTests.cs`, and `04-04-SUMMARY.md` / `STATE.md` updates). Per the execution brief, these are NOT to be reverted, re-edited, or separated.
- **Resolution:** When the Task 2 commit (tests) lands, the 04-04 `NamerDecompIntegrationTests.cs` test-reference fix rides along in the same commit. When the metadata commit lands, the 04-04 `STATE.md` content rides along. This is expected and correct.

**2. [Blocked commit — 1Password SSH signing]**
- **Found during:** Task 1 commit (retried 6 times) and would affect Task 2 + metadata commits.
- **Issue:** `op-ssh-sign` fails with "1Password: agent returned an error" → `fatal: failed to write commit object`. The SSH signing key is locked.
- **Resolution:** All file work and the SUMMARY are complete; the three commits are reported above as blocked with their exact `gsd-sdk query commit` commands for the orchestrator to land once 1Password is unlocked.

---

**Total deviations:** 1 auto-fix (Rule 1 — coverage threshold); 2 execution-context notes (04-04 ride-along, blocked commits)
**Impact on plan:** The coverage guard is corrected to fail-safe only on zero coverage (CR-03 closed) without breaking the pre-existing partial-coverage specification. No scope creep; no `.compute` file change was needed (the root cause was C#-side, not GPU-side).

## Issues Encountered

- 1Password SSH signing was locked for the entire execution window. Six retries with short sleeps all failed with "agent returned an error". Per the execution brief, file work + SUMMARY were completed anyway and the blocked commits are reported with exact commands.
- EditMode test execution is deferred to the orchestrator: the live interactive Unity editor holds the project lock (a batchmode Unity run would exit 134), and this executor has no unity-mcp tools. The two new EditMode cases must be run in-editor via TestRunnerApi.

## User Setup Required

None — no external service configuration required. The only human action is unlocking 1Password so the orchestrator can land the three blocked commits.

## Verification

- Static `grep` gates from the plan passed: `floor(u / BaseWidth)` ×2, `floor(v / BaseHeight)` ×2, `(x0 + 1) % BaseWidth` ×2, no `clamp(u, 0f, BaseWidth - 1f)`; `kMinCoverageFraction` / `CannotDecompose` / guard present in `NamerDecompPipeline.cs`; `Stats.CannotDecompose` / "UV coverage near zero" present in `NamerProcessor.cs`; both test methods + helpers + bounds present in the two test files.
- **Orchestrator EditMode run: 102 PASS / 1 FAIL.** Both new 04-05 tests passed (`Fit_WrapsUvsOutsideZeroOne`, `Process_TilingUvMesh_FallsBackToPhase3WithWarning`). The single failure was a regression in `Residual_UncoveredTexels_AreIdentity`, root-caused to the plan's `kMinCoverageFraction = 0.5f` threshold (NOT the "exact-UV-1.0 edge case" hypothesis) and fixed by lowering to `1e-6f`. Awaiting orchestrator re-run to confirm the regression is resolved.
- **EditMode re-run deferred to orchestrator (live-editor lock).** The full suite (103 cases) must be re-run in-editor to confirm the regression is gone.

## Next Phase Readiness

- CR-03 is closed in code with regression coverage. Phase 4's three verification gaps (CR-01, CR-02, CR-03) are all now closed; the phase is ready for its verification re-run and close-out.
- Phase 5 (stylization) reconstructs over a correctly non-decomposed (Phase-3) shape for tiling-UV layouts.

---

*Phase: 04-vertex-color-decomposition-residual*
*Completed: 2026-09-01*

## Self-Check: PASSED (files, incl. regression fix) / COMMITS BLOCKED (1Password)

All five modified files exist on disk with the plan's markers present (verified via grep), and the coverage-guard regression is fixed (`kMinCoverageFraction = 1e-6f`). The three commits (Task 1, Task 2, metadata) are blocked by a locked 1Password SSH-signing agent and must be landed by the orchestrator using the exact `gsd-sdk query commit` commands listed in the "Task Commits" section.

---
phase: quick-260926-lqr
plan: 01
subsystem: stylization-post-fit
tags: [white-balance, hue-preservation, saturation-floor, identity-curves, multistart-solver, post-processing]
requires:
  - "NamerStylePostFit solver + Fit estimator (05.1 plan 03)"
  - "ColorUtils.ColorBalanceToLMSCoeffs (URP 17.0.4 CPU twin)"
provides:
  - "Hue-preserving fitted post look: fitted Temperature/Tint clamped to ±15 (kFittedWhiteBalanceLimit), Saturation floored at 0, R/G/B curves as exact identity diagonals"
  - "Grid-multistart SolveWhiteBalance (internal): escapes the 0-seed stall basin on diagonal targets, e.g. (-40, +40)"
  - "WhiteBalanceResidual helper + SolveWhiteBalanceSeeded extraction (plan-approved)"
affects:
  - "NamerStyleVolumeWriter output profile curves (R/G/B now identity — consumption contract unchanged)"
  - "Live-editor EditMode suite: NamerStylePostFitTests 13 tests"
tech-stack:
  added: []
  patterns:
    - "Dual-start deterministic optimization: 0-seed + fixed 21-point-per-axis (step 10) grid-seed, lower shared residual wins under strict <"
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs
decisions:
  - "Fitted WB knobs clamp to ±15 while the solve still searches [-100,100] — extreme von Kries gains rotate hue, not just warmth"
  - "Saturation floors at 0: rendered art carries lower mean chroma than painted albedo, so a negative fit is a domain bias, not a look decision"
  - "R/G/B curves are exact identity diagonals (BuildIdentityKeys); only the master curve is histogram-fitted — per-channel specification fought the master map and shifted hue"
  - "Grid multistart seeds a second 32-sweep solve from the best of a fixed 21-point-per-axis (step 10) endpoint-inclusive grid; ties keep the zero-seeded result"
metrics:
  duration: 3min
  completed: 2026-09-26T22:49:45Z
  tasks: 2
  files: 2
---

# Quick Task 260926-lqr: Hue-Preserving Post-Look Redesign Summary

One-liner: NamerStylePostFit now fits a hue-preserving post look — fitted Temperature/Tint clamp to ±15, Saturation floors at 0, R/G/B curves are exact identity diagonals, and a grid-multistart white-balance solve escapes the 0-seed stall basin on diagonal targets like (-40, +40).

## What Was Done

### Task 1 — NamerStylePostFit.cs (5 plan edits, all applied)

1. **Constants:** added `kFittedWhiteBalanceLimit = 15f` (after `kParamRangeLimit`) and `kWhiteBalanceGridSteps` (after `kWhiteBalanceAxisCount`; final value 21 after the orchestrator's grid-density fix, see post-execution section), block comment style matched.
2. **WB clamp site:** both Fit clamps changed from `±kParamRangeLimit` to `±kFittedWhiteBalanceLimit`; `(1) White balance` comment extended with the hue-preservation rationale. Search bounds stay `[-100, 100]`.
3. **Saturation floor:** `clamp(saturationValue, 0f, kParamRangeLimit)`; `(4) Saturation` comment extended (domain-bias rationale). Deadband (`kChromaNoiseFloor`) and gray-source (`kStatEpsilon`) logic untouched above it.
4. **Identity curves:** `RedKeys/GreenKeys/BlueKeys = BuildIdentityKeys()` (9 keys, `value == i / (kCurveKeyCount - 1)`); master `FitCurveValues` luma call byte-unchanged; `FitCurveValues`/`MasterInverse` composition parameter left in place (master passes null) — no refactor.
5. **Solver:** `SolveWhiteBalance` is now `internal` and orchestrates (a) 0-seeded solve, (b) fixed endpoint-inclusive grid scan (21 points per axis post-fix) (`-kWhiteBalanceRange + (2f * kWhiteBalanceRange * i) / (kWhiteBalanceGridSteps - 1)`; multiplication before division makes i = last exactly +100), strict less-than with first-wins scan-order ties (first grid point (-100,-100) seeds the best), (c) grid-seeded solve, (d) lower `WhiteBalanceResidual` wins under strict <, ties keep the zero-seeded result. The 32-sweep loop moved verbatim into `SolveWhiteBalanceSeeded` (only the seed initialization differs). `WhiteBalanceDistance` now delegates to the new `WhiteBalanceResidual` (identical float op sequence, bit-identical results). `MinimizeWhiteBalanceAxis` untouched.

### Task 2 — NamerStylePostFitTests.cs (6 plan edits, all applied)

1. `using UnityEngine.Rendering;` added (ColorUtils).
2. `Fit_WhiteBalance_RecoversKnownCast`: cast now built live via `ColorUtils.ColorBalanceToLMSCoeffs(12f, -5f)`, asserts (12, -5) within `kWhiteBalanceRecoveryTolerance`; doc header explains the in-band move; `kPinnedWbRed/Green/Blue` header comment now names them as the FitSyntheticPair determinism cast.
3. NEW `Fit_WhiteBalance_ExtremeCast_ClampsToFifteen`: (-100, +100) cast fixture, asserts `fitted.Temperature == -15.0` and `fitted.Tint == +15.0` with no tolerance.
4. NEW `Fit_Saturation_NeverDesaturates_FloorsAtZero`: saturated source (0.6, 0.35, 0.15) onto near-gray reference (0.05, 0.048, 0.046); asserts `fitted.Saturation == 0.0` exactly.
5. NEW `SolveWhiteBalance_Multistart_EscapesStallBasin`: internal `NamerStylePostFit.SolveWhiteBalance` on geometric-mean-normalized coeffs(-40, +40); asserts recovery within ±2 (pre-fix stall ~(-44.1, +30.2)); test-local `NormalizeGeometricMeanForTest` mirrors the production helper (documented as such).
6. `Fit_Saturation_ChromaRatio`: expectation wrapped in `max(0f, ...)`; doc header updated.

Test count is now 13 (10 existing + 3 new). All other existing tests are byte-identical.

## Deviations from Plan

### Auto-fixed Issues

**1. [Doc accuracy, beyond the 5 enumerated edits] Fit() doc header knob-order clauses updated**
- **Found during:** Task 1
- **Issue:** The Fit doc header still described "(1) ... alternating-axis golden-section solve" and "(5) master/R/G/B curves via histogram specification (channels composed with the master mapping)" — both factually wrong after the plan's own EDITs 4/5.
- **Fix:** Two-line doc touch-up (dual-start wording; "master curve ... R/G/B stay identity diagonals"). No code change.
- **Files modified:** NamerStylePostFit.cs
- **Commit:** none (orchestrator owns commits)

**2. [Counting note] `kFittedWhiteBalanceLimit` greps 4 occurrences, plan's done-criterion said 3**
- The 4 are: definition + both WB clamp sites + the comment mention that plan EDIT 2 itself mandates. The substantive criterion (defined and used at both clamp sites) is satisfied; the plan's count of 3 was self-inconsistent with its own EDIT 2 comment requirement. No action needed.

## TDD Gate Compliance

Both tasks carry `tdd="true"`, but the plan's critical_constraints override the standard RED/GREEN/REFACTOR loop: the executor may not run Unity EditMode tests (interactive editor holds the project lock; no unity-mcp relay) and may not commit (orchestrator owns the mixed working tree). Tests were authored to specification alongside the implementation; the RED/GREEN validation is deferred to the orchestrator's live-editor run, per the plan's verification section and the 04.2-P04/04.3-02 precedent.

## Tests NOT Run (executor could not)

- The entire `NamerStylePostFitTests` class (13 tests) — including the 3 new tests and the 2 updated ones — could NOT be run by the executor. The interactive Unity editor holds the project lock; per plan critical_constraint 2 the orchestrator runs them live (targeted groupNames EditMode run, no Refresh, file markers) after this session.
- No compile/build check was run either; verification below is read-only code review plus the plan's grep gates.

## Verification Performed (read-only, per plan)

- Task 1 grep gate: all anchors present (`kFittedWhiteBalanceLimit`, `kWhiteBalanceGridSteps`, `SolveWhiteBalanceSeeded`, `WhiteBalanceResidual`, `BuildIdentityKeys`, `internal static void SolveWhiteBalance`, `clamp(saturationValue, 0f, kParamRangeLimit)`); R/G/B `FitCurveValues` call-site count is 0; master luma `FitCurveValues` call remains.
- Task 2 grep gate: all 8 anchors present (`using UnityEngine.Rendering;`, the three live `ColorBalanceToLMSCoeffs` constructions, three new test names, `max(0f, (kRefChromaExact`).
- `git diff` walked hunk-by-hunk: only the plan's edits plus deviation 1; `ApplyToProfile`, `NamerStylePostFitParams` fields, `MinimizeWhiteBalanceAxis`, `NormalizeGeometricMean`, and all other methods untouched; the uncommitted debug-session hunks (near-black/NaN source guard in Fit, its doc lines) preserved byte-for-byte.
- `InternalsVisibleTo("GraffitiEntertainment.Namer.Tests.Editor")` matches the Tests/Editor asmdef name — internal `SolveWhiteBalance` is reachable from the new stall test.
- Brace balance equal in both files; `[Test]` count is 13; no new entries in `git status` beyond the two target files (already listed at session start).
- Cross-checked the debug session's `NamerStylePostFitReadbackTests.cs` (read-only): its identity-reference assertions (`|knob| < cap`) only get easier under the ±15 clamp and 0 floor — no conflict.

## Determinism / Threat Notes

- Grid multistart is fixed-cost and data-independent (441 residual evaluations + one extra 32-sweep solve), strict less-than everywhere, first-wins ties in scan order — T-q260926-01 mitigated as planned; T-q260926-02 accepted (negligible CPU cost).
- No package installs, no new network/file surface — no threat flags.

## Known Stubs

None. No placeholder values; all new code paths are fully wired.

## Self-Check: PASSED

- Files exist: NamerStylePostFit.cs (modified), NamerStylePostFitTests.cs (modified), this SUMMARY (created).
- Commit hashes: none by design — the orchestrator owns all commits for the mixed working tree (plan critical_constraint 1). Nothing was staged or committed by the executor.

## Orchestrator Post-Execution Fixes (live-editor RED/GREEN cycle, 2026-09-26 22:51-22:57 UTC)

The executor could not compile or run tests; the orchestrator's live-editor run surfaced two defects, both fixed at the source:

1. **CS0136 compile error** (NamerStylePostFit.cs:605/608): the executor declared `gridTemperature`/`gridTint` both as grid-scan loop locals and as the seeded-solve `out` parameters in the same method scope. Fix: renamed the second solve's out params to `seededTemperature`/`seededTint` (no behavior change).
2. **Stall regression initially RED**: `SolveWhiteBalance_Multistart_EscapesStallBasin` recovered (−40, **44.58**) instead of (−40, 40) — the executor read "coarse 10-step grid" as 10 POINTS per axis (±11.1-unit cells). The normalized-coeffs distance is non-unimodal along parameter lines, so a seed ~7 units from the optimum can lock into a neighboring local minimum. Fix: `kWhiteBalanceGridSteps` 10 → **21** (step 10, endpoint-inclusive — the density the in-editor design probes verified at ~1e-12 recovery). GREEN after rebuild.

### Live-Editor Test Results (TestRunnerApi, file-marker protocol)

- Targeted `NamerStylePostFitTests`: **13/13 pass, 0 fail** (round 2, after the two fixes above; round 1 was 12/13 with only the stall regression RED).
- Targeted `NamerStylePostFitReadbackTests` (the debug session's last pending gate): **2/2 pass** — the readback-seam oracle and the degenerate-source guard both green.
- Full EditMode suite: **298 pass / 1 fail / 0 skip (299 total)**. The 1 failure is `NamerAOControlsTests.AoContrast_Identity_LeavesAoUnchanged` (0.0549 vs 0.0) — the documented pre-existing main-checkout environment-drift flake (05.1-VERIFICATION.md:116: byte-identical across five headless runs, fails identically on the pre-phase base commit, green in fresh worktrees; no causal path from this change set). It failed in a follow-up isolation run too, matching the prior main-checkout classification rather than the earlier passes-in-isolation observation.
- This full run also constitutes the debug session `fit-look-rails-black-source`'s pending final full-suite gate; that session is now archived to `.planning/debug/resolved/`.
- Live-run gotcha recorded: stale `ICallbacks` registered on earlier TestRunnerApi instances (no domain reload between targeted runs) re-fire on every later run and their `RunStarted` WriteAllText **truncates the earlier run's result file** — the full-suite file was overwritten by the AO-isolation run minutes later; the totals above were captured by the completion-time watcher before the overwrite. Read result files immediately (or snapshot to /tmp) and use a unique path per run.


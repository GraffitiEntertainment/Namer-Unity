---
phase: quick-260927-jkb
plan: 01
subsystem: stylize-post-fit
tags: [post-processing, exposure, tonemap, white-balance, fit-look]
requires:
  - "NamerStylePostMath CPU twin (TonemapLdr / Luma709 — unchanged)"
provides:
  - "Fit step (2): pre-tonemap EV solve via fixed 48-iteration bisection of Luma709(TonemapLdr(srcRawMean * exp2(ev))) against reference.MeanLuma"
  - "NamerStylePostStats raw pre-tonemap channel sums + RawMeanLinear (source-only seam)"
  - "kFittedWhiteBalanceLimit widened 15 -> 50"
affects:
  - "Fitted PostExposureEv values (systematically slightly larger than the old post-tonemap log2 ratio — removes the reported dark bias)"
  - "Fitted Temperature/Tint on warm/cool references beyond ±15 (now clamp at ±50)"
tech-stack:
  added: []
  patterns:
    - "Model-inverting fixture construction (build reference BACKWARD from a known EV so the solver's model inverts exactly)"
    - "Test-local verbatim mirror of a production numeric solve (twin-mirrored expectation instead of a magic constant)"
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs
decisions:
  - "EV solved in the PRE-tonemap domain (fixed 48-iteration bisection over the ±10 bracket) because the shader chain and URP postExposure apply EV before the ACES tonemap; the bracket endpoints are the clamp, so unreachable targets land exactly on ±10 after float collapse"
  - "Fitted WB clamp widened 15 -> 50 per user approval (solve still searches ±100; profile caps stay ±100)"
metrics:
  duration: 4min
  completed: 2026-09-27
---

# Quick Task 260927-jkb: Fit Look exposure solves pre-tonemap (bisection) — Summary

Fit Look's PostExposureEv is now solved in the pre-tonemap domain with a deterministic fixed-iteration bisection, killing the systematic slight-dark bias of the retired post-tonemap log2 ratio, and the fitted white-balance clamp widened from ±15 to ±50.

## What Was Done

### Task 1: NamerStylePostFit.cs (4 edits)

1. **Raw sums on `NamerStylePostStats`** — added `RawRedSum`/`RawGreenSum`/`RawBlueSum` (accumulated ONLY by `AccumulateSource`; the reference accumulator's raw domain equals its decoded linear domain — deliberately not accumulated) plus an `internal Color RawMeanLinear` property mirroring `MeanLinear`'s exact shape and zero-guard. Class-doc domain bullet list extended with the raw pre-tonemap source-sums line.
2. **`AccumulateSource` raw accumulation** — three `+=` statements against `pixel.r/g/b` inserted after the `IsFinitePixel` guard and BEFORE the `TonemapLdr` call (weight implicitly 1, matching the accumulator's hardcoded 1f). Statement ordering: guard → raw sums → tonemap → AccumulateLinear. `AccumulateReference`/`AccumulateLinear` untouched.
3. **Fit step (2) bisection** — new `private const int kExposureBisectionIterations = 48;` (fixed count keeps the determinism contract; float32 brackets collapse to adjacent floats well before 48). The `srcLumaMean` local, the log2-ratio clamp, and the "Accepted v1 behavior" comment are gone, replaced by: per-channel-floored `srcRawMean = max(ToFloat3(source.RawMeanLinear), kStatEpsilon)`, `refLumaMean = max(reference.MeanLuma, kStatEpsilon)` (kept as today), and a fixed-count bisection over `[-kMaxFittedPostExposureEv, +kMaxFittedPostExposureEv]` whose midpoint prediction is `NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(srcRawMean * exp2(evMid)))` with the `predicted < refLumaMean -> evLow = evMid; else evHigh = evMid` sign rule (TonemapLdr is monotone in uniform scale). Final `clamp((evLow + evHigh) * 0.5f, ±10)` — the bracket endpoints ARE the ±10 clamp, so an unreachable target lands exactly on the endpoint after float collapse (rail semantics preserved). `Fit` XML doc knob-order sentence updated (no code-pattern literal in doc text — the verify gate counts exactly one occurrence of the model expression, in the implementation).
4. **WB clamp widen** — `kFittedWhiteBalanceLimit` 15f → 50f with updated comment (hue-preservation bound chosen at 50; solve still searches [-100, 100]; URP's own slider range is ±100). Nothing else in the WB path changed; `SolveWhiteBalance` internals untouched (still referenced only by its original call site).

### Task 2: NamerStylePostFitTests.cs

- **`Fit_Exposure_SolvesPreTonemapThroughTonemap`** (replaces `Fit_UniformGain_MatchesPostTonemapMeanRatio`) — fixture built BACKWARD from `kKnownPreTonemapEv = 0.75f`: source = 64 pixels raw gray 0.8, reference = `EncodePixel(TonemapLdr(0.8 gray * exp2(0.75)))` ×64 (0.8·2^0.75 ≈ 1.35 sits deep in the ACES shoulder where compression — the defect — is strong). Asserts (1) fitted EV == 0.75 within `kEstimatorTolerance`, (2) root cause — the OLD formula's `log2(refLumaMean / srcLumaMean)` computed in-test from the exact post-tonemap means undershoots 0.75 by strictly more than `kDarkBiasMargin = 0.01f` (independent scalar estimate of the gap ≈ 0.37 — wide margin), (3) model closure — `Luma709(TonemapLdr(rawGray * exp2(fitted)))` == refLumaMean within tolerance.
- **`Fit_Exposure_SingleToneIdentity_IsExactZero`** (new) — raw gray 0.4 single-tone pair; EV == 0 within `kNeutralKnobTolerance` (only the sRGB round trip + bisection float noise remain).
- **`Fit_IdenticalPair_IsNeutral`** — ONLY the EV assertion replaced: EV now asserted equal to the new private `SolveExposureMirrorForTest` (verbatim mirror of the production bisection: `kMirrorBisectionIterations = 48`, same bracket, sign rule, float ops, per-channel floor, consuming the SAME accumulated stats) within `kEstimatorTolerance`. Doc records the first-order curvature gap (tonemap-of-mean(0.5) = 0.3673 vs mean-of-tonemap 0.3364, identity EV ≈ −0.13). The Fit call was restructured to reuse the stats locals (source/reference) for both Fit and the mirror; all other assertions (WB/contrast/saturation/curve keys) keep their tolerances.
- **`Fit_Exposure_UnreachableBrightTarget_RailsAtPlusTen`** / **`..._RailsAtMinusTen`** (new) — positive rail: dim raw 0.01 source (post-tonemap mean luma ~9.2e-4 clears the 1e-6 guard) onto sRGB white; model input caps at 0.01·2^10 = 10.24 whose tonemapped luma ~0.97 < 1.0, so the target is unreachable inside the bracket → EV == exactly `10.0`, no tolerance. Negative rail: HDR gray 30f source (TonemapLdr(30) saturate-clamps to 1.0, guard passes) onto black reference at weight 1 (WR-01 refuses only zero weight; MeanLuma 0 floors to 1e-6; Luma709(TonemapLdr(30/1024)) ≈ 7e-3 ≫ 1e-6 at ev = −10) → EV == exactly `-10.0`. Full derivations in the doc comments.
- **`Fit_WhiteBalance_ExtremeCast_ClampsToFifteen` → `..._ClampsToFifty`** — doc updated (clamp to exactly (−50, +50) after the widened bound; solve still recovers ~(−100, +100)), assertions now exact `-50.0` / `50.0`.
- `kUniformGain` constant deleted (unused after the rewrite). `Fit_IsDeterministic`, `Fit_ZeroReferenceMass_Throws`, `Fit_Contrast_SolvesLogCSpread`, `Fit_WhiteBalance_RecoversKnownCast`, `SolveWhiteBalance_Multistart_EscapesStallBasin`, `Fit_Saturation_*`, `Fit_BloomThreshold_*`, `Fit_MasterCurve_MatchesGammaPair`, `AccumulateReference_MergesWeights`, and NamerStylePostFitReadbackTests untouched (the readback non-rail assertions hold — reachable mid-range targets cannot rail under the new solve).

## Deviations from Plan

None — plan executed exactly as written. Both per-task static verify gates PASS; brace/paren balance verified; only the two listed files changed (the held o2q/qlb working-tree changes were left untouched, and neither target file carried any of their edits).

## Notes for the Orchestrator

- No commits were made (session constraint — orchestrator owns commits after live verification and user confirmation).
- No Unity recompile/test runs in this session (orchestrator-owned). Targeted live EditMode run should cover `NamerStylePostFitTests` + `NamerStylePostFitReadbackTests`, then the full-suite regression gate.
- Rail assertions are exact (`Assert.AreEqual(±10.0, ...)` with no tolerance): the bisection's float32 endgame rounds the collapsed bracket sum to exactly ±20.0f, so the result is exactly ±10.0f — the same exactness contract that the WB clamp assertions use.

## Self-Check: PASSED

- Files exist and are the only session-modified paths:
  - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs — modified (Task 1 gate PASS)
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs — modified (Task 2 gate PASS)
- Task 1 grep gate: PASS (old formula/comment/local gone; bisection constant, raw sums, RawMeanLinear, model expression ×1, WB 50f present; SolveWhiteBalance intact)
- Task 2 grep gate: PASS (ClampsToFifteen and 0.67807191 gone; ClampsToFifty, SolveExposureMirrorForTest, kKnownPreTonemapEv, ±50 assertions, Fit_IsDeterministic, twin references present)
- No new files created; forbidden stages/files untouched (NamerStylePostMath.cs, NamerPostPreview.hlsl, SolveWhiteBalance internals, ApplyToProfile, NamerStyleVolumeWriter, ApplyPostLook, profile caps, Assets/)

## Orchestrator Live Verification (2026-09-27)

- Static verifier (gsd-verifier): 6/6 checks PASSED — report in [260927-jkb-VERIFICATION.md](./260927-jkb-VERIFICATION.md); math independently reconfirmed in float64 (shoulder undershoot gap 0.3741, dim-rail cap 0.9753 < 1.0, ramp EV ≈ −0.139).
- Forced recompile via relay — `GraffitiEntertainment.Namer.Editor.dll` + `...Tests.Editor.dll` rebuilt 2026-09-27 14:35:36; symbol landing verified (`SolveExposureMirrorForTest` in the tests DLL; `RawMeanLinear` + `kExposureBisectionIterations` in the Editor DLL).
- Targeted live EditMode run (NamerStylePostFitTests + NamerStylePostFitReadbackTests): **18/18 passed** (pass=18 fail=0). The run picked up ~10 min after the domain reload — a delayed start, not a dead relay.
- Full EditMode regression gate: **314/314 passed, 0 skipped** (311 prior + 3 net-new tests).
- Commits HELD for explicit user permission; pending user visual confirmation (re-run Fit Look — old fits are stale under the qlb exclusive-tone redesign AND the new pre-tonemap EV solve).

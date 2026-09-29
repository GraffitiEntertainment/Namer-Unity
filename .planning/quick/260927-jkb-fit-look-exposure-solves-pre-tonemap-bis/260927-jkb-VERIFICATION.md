---
phase: quick-260927-jkb
verified: 2026-09-27T00:00:00Z
status: passed
score: 6/6 must-haves verified
overrides_applied: 0
verification_mode: static-only (session constraint — live Unity EditMode run is orchestrator-owned)
---

# Quick Task 260927-jkb: Fit Look exposure solves pre-tonemap (bisection) — Verification

**Task Goal:** Fit Look exposure solves pre-tonemap (bisection on TonemapLdr model, fixes systematic slight-dark bias) + widen fitted white-balance clamp from 15 to 50
**Verified:** 2026-09-27 (static session — working tree, no Unity, no git mutations)
**Status:** passed
**Re-verification:** No — initial verification

## Goal Achievement

### Observable Truths

| # | Truth | Status | Evidence |
|---|-------|--------|----------|
| 1 | Fit solves PostExposureEv in the PRE-tonemap domain via bisection of the shader-chain model; old post-tonemap log2 ratio and its "Accepted v1 behavior" acceptance are gone | ✓ VERIFIED | `NamerStylePostFit.cs` diff removes `srcLumaMean`, the `log2(refLumaMean / srcLumaMean)` clamp, and the acceptance comment; replaces with `kExposureBisectionIterations = 48` fixed loop whose prediction is `NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(srcRawMean * exp2(evMid)))` (line 352, the pattern's only occurrence — the XML doc spells no code literal, verified by count = 1). Premise confirmed in `Shaders/NamerPostPreview.hlsl`: `c *= exp2(_PostExposureEv)` precedes `saturate(AcesTonemap(...))` |
| 2 | The model closes on constructed fixtures (known-EV construction inverts) | ✓ VERIFIED | `Fit_Exposure_SolvesPreTonemapThroughTonemap` builds reference backward from `kKnownPreTonemapEv = 0.75f` with all three required assertions (recovery, strictly-greater-than-old-formula, model closure). Independent float64 simulation of the twin's transcribed constants: oldEv = 0.3759, undershoot gap 0.3741 > `kDarkBiasMargin` (0.01) with wide margin; 0.8·2^0.75 = 1.3454 (deep in shoulder, as claimed) |
| 3 | Rail semantics preserved: unreachable targets land exactly on ±10 | ✓ VERIFIED | Bracket `[-kMaxFittedPostExposureEv, +kMaxFittedPostExposureEv]` with final `clamp((evLow+evHigh)*0.5f, ±10)`; rail tests assert exact `10.0` / `-10.0` with no tolerance. Numeric check confirms both fixtures are genuinely unreachable inside the bracket: Luma709(TonemapLdr(0.01·2^10 gray)) = 0.9753 < 1.0 (positive), Luma709(TonemapLdr(30/1024)) = 6.5e-3 > 1e-6 (negative); TonemapLdr(30) = 1.0 so the source guard passes. Float32 endgame: evHigh pins at 10.0f, evLow = nextafter(10) makes the bracket sum an exact ties-to-even candidate rounding to 20.0f → midpoint exactly 10.0f |
| 4 | Fitted WB clamps to ±50 (was ±15); solve still searches ±100, internals untouched | ✓ VERIFIED | `kFittedWhiteBalanceLimit = 50f` (line 204); `kWhiteBalanceRange = 100f` unchanged; the working-tree diff contains zero hunks inside SolveWhiteBalance / SolveWhiteBalanceSeeded / MinimizeWhiteBalanceAxis / WhiteBalanceResidual; `Fit_WhiteBalance_ExtremeCast_ClampsToFifty` asserts exact −50.0 / +50.0 |
| 5 | Fit stays deterministic: fixed 48-iteration count, no RNG/threading | ✓ VERIFIED | `private const int kExposureBisectionIterations = 48;` drives a plain fixed `for` loop of pure float math; no new allocation, RNG, or threading; `Fit_IsDeterministic` untouched in the diff and still present |
| 6 | Only the two listed files change; all forbidden stages untouched | ✓ VERIFIED | `260927-jkb` markers appear in exactly `Editor/Stylize/NamerStylePostFit.cs` (5) and `Tests/Editor/NamerStylePostFitTests.cs` (3) across the entire working-tree diff. `NamerStylePostMath.cs`, `Shaders/NamerPostPreview.hlsl`, `NamerStyleProfile.cs` (caps ±100 / ±MaxFittedPostExposureEv intact at lines 330–344), `NamerStylePostFitReadbackTests.cs`, and `ApplyToProfile` are absent from `git status`. Other modified files' diffs are held o2q/qlb content only (ToneMatch routing via `NamerPostLookScale.Scale`) |

**Score:** 6/6 truths verified

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs` | Raw sums + RawMeanLinear (source-only); AccumulateSource raw accumulation; pre-tonemap bisection replacing log2 ratio; WB limit 50 | ✓ VERIFIED | All four edits present and substantive (82-line diff exactly matches plan Tasks 1a–1d); `RawMeanLinear` consumed by `Fit` step (2) — wired, not orphaned |
| `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs` | Known-EV regression with old-formula-undershoot assertion; exact single-tone identity; ±10/−10 rails; twin-mirror ramp expectation; ±50 WB clamp | ✓ VERIFIED | All five test shapes present (216-line diff); `kUniformGain` deleted; untouched tests confirmed unchanged in diff (determinism, contrast, saturation ×2, bloom, curve, WB recovery, multistart, merge) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| `AccumulateSource` | `RawRedSum/RawGreenSum/RawBlueSum` | `RawRedSum += pixel.r` after IsFinitePixel guard, before TonemapLdr | ✓ WIRED | Lines 233–240, exact ordering guard → raw sums → tonemap → AccumulateLinear; weight implicitly 1 |
| Fit step (2) | `TonemapLdr + Luma709 + exp2` | `TonemapLdr(srcRawMean * exp2(evMid))` vs `refLumaMean = max(reference.MeanLuma, kStatEpsilon)` | ✓ WIRED | Line 352–353; pattern occurs exactly once (implementation only) |
| Fit step (2) | `kExposureBisectionIterations` | fixed-count for loop over ±10 bracket | ✓ WIRED | Lines 347–361; constant = 48 |
| Test EV oracles | `NamerStylePostMath.TonemapLdr` | expected values computed from the CPU twin | ✓ WIRED | Known-EV, identity, closure, and mirror all route through the twin; `SolveExposureMirrorForTest` replicates the production bisection verbatim (same bracket, epsilon, sign rule, 48 iterations, float ops) |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|-------------------|--------|
| NamerStylePostFit.Fit step (2) | `srcRawMean` | `AccumulateSource` raw sums → `RawMeanLinear` | Yes — real per-pixel accumulation, not static | ✓ FLOWING |
| SolveExposureMirrorForTest | source/reference stats | same accumulated stats as the production Fit call in `Fit_IdenticalPair_IsNeutral` | Yes — stats locals reused for both Fit and mirror | ✓ FLOWING |

### Behavioral Spot-Checks

Static-only session (constraint). Substituted an out-of-tree float64 simulation of the twin's verbatim constants (pure computation, no Unity, no writes) to falsify/confirm the fixtures' mathematical claims:

| Claim under test | Result | Status |
|------------------|--------|--------|
| 0.8·2^0.75 sits in shoulder (≈1.35) | 1.3454 | ✓ PASS |
| Old formula undershoots 0.75 by > 0.01 (claim ~0.37) | oldEv = 0.3759, gap 0.3741 | ✓ PASS |
| Dim 0.01 source caps below white target (TonemapLdr(10.24) luma ~0.97 < 1.0) | 0.9753 | ✓ PASS |
| HDR 30 tonemaps to 1.0 (guard passes); 30/1024 luma ≫ 1e-6 at ev=−10 | 1.0000; 6.5e-3 | ✓ PASS |
| ACES asymptote 1.0165; rational fit reaches 1.0 at ~25.7 | 1.0165; 25.7 | ✓ PASS |
| Ramp identity EV distinctly negative (~ −0.13) | −0.139 | ✓ PASS |

Live Unity EditMode execution of `NamerStylePostFitTests` + `NamerStylePostFitReadbackTests`: SKIPPED (orchestrator-owned per session constraints — scheduled as the orchestrator's next gate).

### Probe Execution

Step 7c: SKIPPED — no probe scripts declared for this quick task; plan verification is the two static grep gates, both re-run here: TASK1_GATE_PASS, TASK2_GATE_PASS.

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|------------|------------|---------------------|----------|
| STYL-07 | quick-260927-jkb | Reference-fitted global grade (WB, exposure, contrast, saturation, curves) | ✓ SATISFIED (contribution) | Exposure solve now models the actual shader chain; WB clamp widened — the exposure/WB portions of the fitted grade. Phase-level completion tracked at Phase 5.1 |
| STYL-08 | quick-260927-jkb | ACES tonemap default with fitted shaping; bloom threshold auto-fit | ✓ SATISFIED (adjacent) | Task deliberately leaves tonemap/bloom stages untouched; the pre-tonemap exposure model now composes correctly with the ACES chain. No orphaned requirements found |

### Anti-Patterns Found

None. Both files clean of TBD/FIXME/XXX/TODO/HACK/PLACEHOLDER markers; no empty returns or placeholder implementations introduced; comment density and code style match the file's established conventions.

### Gaps Summary

No gaps. The working tree contains exactly the planned changes in exactly the two permitted files; the retired dark-bias formula is provably gone and its replacement's math is independently confirmed. The diff shows no edits inside SolveWhiteBalance internals, the other fit stages, ApplyToProfile, the volume writer, the shader, the CPU twin, or profile caps. Held o2q/qlb changes in other files were left untouched.

Notes for the orchestrator (not gaps):
- Live EditMode run of the two test classes remains the orchestrator's gate; the exact-tolerance rail assertions (±10.0, ±50.0) rest on float32 ties-to-even endpoint collapse, which is analytically sound and was the pre-existing exactness pattern of the WB clamp test, but only the live run is definitive.
- The ramp-identity doc comment's illustrative constants (0.3673/0.3364) differ ~0.014 from my float64 simulation (0.3813/0.3477) — documentation-only; the assertion itself compares against the verbatim twin mirror, so no test depends on those numbers.

---

_Verified: 2026-09-27_
_Verifier: Claude (gsd-verifier, static session)_

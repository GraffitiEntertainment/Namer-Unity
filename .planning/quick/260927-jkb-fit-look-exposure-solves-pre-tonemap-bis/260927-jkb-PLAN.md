---
phase: quick-260927-jkb
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs
autonomous: true
requirements: [STYL-07, STYL-08]
must_haves:
  truths:
    - "Fit Look solves PostExposureEv in the PRE-tonemap domain: EV is the gain that, applied before the ACES tonemap exactly as the shader chain does (NamerPostPreview.hlsl applies exp2(_PostExposureEv) BEFORE AcesTonemap), reproduces the reference mean luma — the systematic slight-dark bias from the post-tonemap log2 ratio is gone at the model level"
    - "The model closes on constructed fixtures: Luma709(TonemapLdr(srcRawMean * exp2(ev))) lands on reference.MeanLuma within test tolerance"
    - "Rail semantics are preserved: an unreachable target lands exactly on +10 or -10 (kMaxFittedPostExposureEv bracket endpoints, float-collapsed by the fixed 48-iteration bisection)"
    - "Fitted Temperature/Tint clamp to ±50 (was ±15); SolveWhiteBalance still searches the full ±100 and its internals are untouched"
    - "Fit stays deterministic: the bisection has a FIXED iteration count (kExposureBisectionIterations = 48), no RNG, no threading — Fit_IsDeterministic passes unchanged"
    - "Only the two listed files change; NamerStylePostMath, NamerPostPreview.hlsl, SolveWhiteBalance internals, contrast/saturation/curve/bloom stages, ApplyToProfile, NamerStyleVolumeWriter, ApplyPostLook, and the profile caps (already ±100) are untouched"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs"
      provides: "Raw (pre-tonemap) channel sums on NamerStylePostStats + RawMeanLinear; AccumulateSource accumulates raw before tonemapping; Fit step (2) replaced by fixed-iteration bisection of the pre-tonemap model; kFittedWhiteBalanceLimit widened to 50"
      contains: "kExposureBisectionIterations"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs"
      provides: "Rewritten EV solve tests (pre-tonemap known-EV construction, strictly-greater-than-old-formula root-cause assertion), single-tone exact identity, ±10 rail tests, ±50 WB clamp test; ramp identity EV asserted equal to a test-local twin mirror of the bisection (expected ~ -0.13, first-order curvature gap on the full-range ramp)"
      contains: "ClampsToFifty"
  key_links:
    - from: "NamerStylePostFit.AccumulateSource"
      to: "NamerStylePostStats.RawRedSum/RawGreenSum/RawBlueSum"
      via: "raw linear pixel accumulated per channel (weight 1) BEFORE TonemapLdr, after the IsFinitePixel guard, same statement ordering as today"
      pattern: "RawRedSum \\+= "
    - from: "NamerStylePostFit.Fit step (2)"
      to: "NamerStylePostMath.TonemapLdr + NamerStylePostMath.Luma709 + exp2"
      via: "bisection midpoint prediction Luma709(TonemapLdr(srcRawMean * exp2(evMid))) compared against refLumaMean = max(reference.MeanLuma, kStatEpsilon)"
      pattern: "TonemapLdr\\(srcRawMean \\* exp2"
    - from: "NamerStylePostFit.Fit step (2)"
      to: "kExposureBisectionIterations"
      via: "fixed-count for loop over the [-kMaxFittedPostExposureEv, +kMaxFittedPostExposureEv] bracket — fixed count keeps the class determinism contract (fixed iteration order and counts)"
      pattern: "kExposureBisectionIterations"
    - from: "NamerStylePostFitTests EV oracles"
      to: "NamerStylePostMath.TonemapLdr"
      via: "expected values computed from the CPU twin (house rule: pin the wiring, never re-implement the math)"
      pattern: "NamerStylePostMath\\.TonemapLdr"
---

<objective>
Fix the two reported Fit Look defects in `NamerStylePostFit.cs`:

1. **Slight-dark bias (root cause).** `Fit` step (2) solves `PostExposureEv = clamp(log2(refLumaMean / srcLumaMean), ±10)` where BOTH means are POST-tonemap (source is accumulated through `TonemapLdr` in `AccumulateSource`; reference is sRGB-decoded display-linear). The actual shader chain (`NamerPostPreview.hlsl`: `c *= exp2(_PostExposureEv)` at line 67, then `AcesTonemap` at line 70 — and URP `ColorAdjustments.postExposure` equivalently) applies EV BEFORE the ACES tonemap. Tonemap compresses, so a post-tonemap luma ratio r needs a LARGER pre-tonemap gain than log2(r); the current solve systematically undershoots EV → the user-visible "slightly too dark" render. The inline comment at NamerStylePostFit.cs:292-294 admits this ("Accepted v1 behavior ... planner decision 1") — that acceptance is REVOKED by this task.

2. **WB clamp too tight.** `SolveWhiteBalance` searches the full ±100 (`kWhiteBalanceRange`), but the written knobs clamp to `kFittedWhiteBalanceLimit = 15f` (line 176, applied at 289-290). Warm-reference fits rail at ±15 and leave residual mismatch. User-approved widen: **15 → 50** (strong warm/cool while keeping von Kries hue rotation bounded; URP's own slider is ±100).

USER-APPROVED DESIGN — implement exactly as specified. Do not redesign, do not simplify, do not add speculative guards.

Output: the two files above modified; no new files; no Unity recompile/test runs and no git writes (orchestrator owns both).
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@./CLAUDE.md
@Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs
@Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostMath.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs

<interfaces>
<!-- Extracted contracts the executor needs. Use directly — no codebase exploration needed. -->

From NamerStylePostFit.cs (stats struct, lines 84-147):
```csharp
public sealed class NamerStylePostStats
{
    internal double RedSum; internal double GreenSum; internal double BlueSum;
    internal double LumaSum; internal double ChromaSum; internal double TotalWeight;
    internal readonly float[] LumaHistogram = ...;   // + Red/Green/Blue/Max3Srgb histograms
    internal Color MeanLinear { get; }   // per-channel sums / TotalWeight, zero-color when TotalWeight <= 0
    internal float MeanLuma { get; }
    internal float MeanChroma { get; }
}
```

From NamerStylePostFit.cs (constants, lines 165-185):
```csharp
public const int kPostHistogramBins = 256;
private const float kStatEpsilon = 1e-6f;
private const float kFittedWhiteBalanceLimit = 15f;   // CHANGE to 50f
private const float kMaxFittedPostExposureEv = 10f;   // becomes the bisection bracket; value unchanged
```

From NamerStylePostFit.cs (seams to edit):
- `AccumulateSource(Color[] linearPixels)` lines 193-209: IsFinitePixel skip → `float3 tonemapped = NamerStylePostMath.TonemapLdr(new float3(pixel.r, pixel.g, pixel.b)); AccumulateLinear(stats, tonemapped, 1f);`
- `Fit` step (2) lines 292-297: the log2-ratio solve + the "Accepted v1 behavior" comment
- `Fit` XML doc (summary block 237-255, knob-order sentence at 242): "(2) post-exposure as the log2 post-tonemap linear mean-luma ratio"
- Helper `ToFloat3(Color)` exists (used at line 277). `SolveWhiteBalance` (~595-631): DO NOT TOUCH.

From NamerStylePostMath.cs (the CPU twin — DO NOT MODIFY):
```csharp
public static float3 TonemapLdr(float3 c)   // saturate(AcesTonemap(UnityToAces(c))); monotone in uniform scale
public static float Luma709(float3 c)       // used today in AccumulateLinear
```
Unity.Mathematics `math` is statically imported (`exp2`, `max`, `clamp` available).

From NamerPostPreview.hlsl (the modeled chain — DO NOT MODIFY; line 67 then 70):
`c *= exp2(_PostExposureEv);` BEFORE `c = saturate(AcesTonemap(unity_to_ACES(c)));`

Existing tests in NamerStylePostFitTests.cs that MUST be updated (all others keep passing):
- `Fit_UniformGain_MatchesPostTonemapMeanRatio` (lines 164-184) — pins the OLD formula `log2(1.6) = 0.67807191`
- `Fit_WhiteBalance_ExtremeCast_ClampsToFifteen` (lines 268-291) — asserts exact ±15
- `Fit_IdenticalPair_IsNeutral` (lines 124-157) — asserts ramp-pair EV == 0 within `kNeutralKnobTolerance = 1e-3`
- `kUniformGain` constant (line 43) — likely unused after the rewrite; remove if so

Tests that must keep passing UNCHANGED:
- `Fit_IsDeterministic`, `Fit_ZeroReferenceMass_Throws`, `Fit_Contrast_SolvesLogCSpread`, `Fit_WhiteBalance_RecoversKnownCast` (12/-5 stays in-band at ±50), `SolveWhiteBalance_Multistart_EscapesStallBasin`, `Fit_Saturation_*`, `Fit_BloomThreshold_*`, `Fit_MasterCurve_MatchesGammaPair`, `AccumulateReference_MergesWeights` (raw sums are source-only — reference accumulation is NOT extended)
- NamerStylePostFitReadbackTests.`Fit_DegenerateSource_RefusesLoudlyInsteadOfRailing` and `AssertFitOntoSelfDoesNotRail` (asserts only |EV| < RailCapEv — a reachable mid-range target cannot rail under the new solve)
</interfaces>

Working tree holds UNCOMMITTED changes from quick tasks 260926-o2q / 260926-qlb — layer on top, never revert. NEVER modify Assets/ user content. No new files.
</context>

<tasks>

<task type="auto">
  <name>Task 1: Pre-tonemap EV bisection + raw stats + WB clamp 50 in NamerStylePostFit.cs</name>
  <files>Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs</files>
  <action>
Four edits, all inside NamerStylePostFit.cs. Code style: XML-doc `///` comments, dense dated `//` inline comments, Allman braces, `kPascalCase` constants — match the file exactly.

(a) **Raw sums on the stats struct.** In `NamerStylePostStats` (after `TotalWeight`, before the histograms) add `internal double RawRedSum; internal double RawGreenSum; internal double RawBlueSum;` with a `///` doc: pre-tonemap raw linear channel sums, accumulated ONLY by `AccumulateSource` (the reference accumulator's raw domain equals its decoded linear domain — deliberately not accumulated, keeping the seam honest). Add an `internal Color RawMeanLinear` property mirroring `MeanLinear`'s exact shape and zero-guard (`TotalWeight <= 0` returns the zero Color), doc: mean pre-tonemap linear color; only meaningful for source distributions. Extend the struct's class-doc domain bullet list with one line for the raw pre-tonemap source sums.

(b) **AccumulateSource raw accumulation.** In `AccumulateSource` (lines 193-209), after the `IsFinitePixel` continue-guard and BEFORE the `TonemapLdr` call, accumulate the raw linear pixel into the three raw sums (weight is implicitly 1 here — the source accumulator hardcodes 1f today; write the three `+=` statements against `pixel.r/g/b`). Keep statement ordering otherwise identical (guard → raw sums → tonemap → AccumulateLinear). Do NOT touch `AccumulateReference` or `AccumulateLinear`.

(c) **Fit step (2): bisection.** New constant next to `kMaxFittedPostExposureEv` (line ~177): `private const int kExposureBisectionIterations = 48;` with a dated `//` comment: fixed count keeps the determinism contract (fixed iteration order and counts); float32 brackets collapse to adjacent floats well before 48, so the result is the fully converged float — identical run to run.

Replace lines 292-297 (the `srcLumaMean`/`refLumaMean` locals + the log2-ratio clamp; `srcLumaMean` has no other consumer — remove it) with a pre-tonemap bisection: `float3 srcRawMean = max(ToFloat3(source.RawMeanLinear), kStatEpsilon);` (per-channel floor, mirroring the existing `max(srcLms, kStatEpsilon)` pattern); `float refLumaMean = max(reference.MeanLuma, kStatEpsilon);` (kept as today); bracket `evLow = -kMaxFittedPostExposureEv`, `evHigh = +kMaxFittedPostExposureEv`; fixed `for` loop over `kExposureBisectionIterations`: midpoint `evMid = (evLow + evHigh) * 0.5f`, prediction `NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(srcRawMean * exp2(evMid)))`, update `if (predicted < refLumaMean) evLow = evMid; else evHigh = evMid;` (TonemapLdr is monotone in uniform scale — saturate(AcesTonemap(UnityToAces(c·s))) is nondecreasing in s — so the sign rule converges); after the loop `fitted.PostExposureEv = clamp((evLow + evHigh) * 0.5f, -kMaxFittedPostExposureEv, kMaxFittedPostExposureEv);` — the bracket endpoints ARE the ±10 clamp, so a target outside the bracket lands exactly on the endpoint after float collapse (rail semantics preserved), and the final clamp makes that explicit.

Replace the "Accepted v1 behavior" comment (292-294) with a dated comment describing the new model: EV is applied BEFORE the ACES tonemap in the shader chain (NamerPostPreview.hlsl applies exp2(_PostExposureEv) before AcesTonemap; URP ColorAdjustments.postExposure equivalently), so the solve finds the pre-tonemap gain whose TONEMAPPED output luma matches the reference mean luma — the old post-tonemap log2 ratio undershot EV by the tonemap compression (the reported slight-dark bias); the model evaluates tonemap on the raw MEAN, so the mean-vs-tonemap-of-mean Jensen residual is small for real (narrow-luma) distributions and accepted — the maximally wide synthetic test ramp widens it to first-order, which Task 2c handles by mirroring the twin instead of pinning a constant.

Update the `Fit` XML doc knob-order sentence (summary block 237-255, sentence at 242): "(2) post-exposure via a fixed-iteration bisection of the pre-tonemap exposure model against the reference mean luma (EV is applied before the ACES tonemap in the shader chain — NamerPostPreview.hlsl / URP postExposure equivalent)". Do NOT spell the literal expression `TonemapLdr(srcRawMean * exp2(...))` in the doc text — the Task 1 verify gate counts exactly one occurrence of that code pattern, and it belongs to the implementation only.

(d) **WB clamp widen.** Line 176: `kFittedWhiteBalanceLimit` 15f → 50f; update its `//` comment: hue-preservation bound chosen at 50 (strong warm/cool while keeping the von Kries hue rotation bounded); the solve still searches [-100, 100]; URP's own slider range is ±100. Nothing else in the WB path changes.

Do NOT touch: `SolveWhiteBalance` internals, contrast/saturation/curve/bloom stages, `NamerStylePostMath.cs`, `NamerPostPreview.hlsl`, `ApplyToProfile`, `NamerStyleVolumeWriter`, `ApplyPostLook`, profile caps.
  </action>
  <verify>
    <automated>cd /Users/Shared/SSDevelopment/Development/Namer-Unity && F=Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs && ! grep -q "log2(refLumaMean / srcLumaMean)" "$F" && ! grep -q "Accepted v1 behavior" "$F" && ! grep -q "srcLumaMean" "$F" && grep -q "private const int kExposureBisectionIterations = 48;" "$F" && grep -q "kFittedWhiteBalanceLimit = 50f" "$F" && ! grep -q "kFittedWhiteBalanceLimit = 15f" "$F" && grep -q "internal double RawRedSum;" "$F" && grep -q "RawMeanLinear" "$F" && grep -c "TonemapLdr(srcRawMean \* exp2" "$F" | grep -qx "1" && grep -q "SolveWhiteBalance" "$F" && echo PASS</automated>
  </verify>
  <done>
Old EV formula, its "Accepted v1 behavior" comment, and the srcLumaMean local are gone; the bisection constant (48, fixed), the raw sums + RawMeanLinear, the TonemapLdr(srcRawMean * exp2(...)) model evaluation, and kFittedWhiteBalanceLimit = 50f are present; SolveWhiteBalance is still referenced only by its original call site.
  </done>
</task>

<task type="auto">
  <name>Task 2: Rewrite EV solve tests + WB rail expectations in NamerStylePostFitTests.cs</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs</files>
  <action>
All fixtures are synthetic Color[] arrays; every expected value is computed from NamerStylePostMath (pin the wiring, never re-implement the math). Keep the house header docs with derivations beside the assertions.

(a) **Root-cause regression test (replaces `Fit_UniformGain_MatchesPostTonemapMeanRatio`, lines 164-184).** Rename to something like `Fit_Exposure_SolvesPreTonemapThroughTonemap` and rebuild the fixture BACKWARD from a known EV so the model inverts exactly: constant `kKnownPreTonemapEv = 0.75f`; source = 64 pixels of raw gray 0.8 (pre-tonemap HDR-ish linear, comfortably inside [0,1], deep enough that 0.8·exp2(0.75) ≈ 1.35 sits well into the tonemap shoulder where compression — the defect — is strong); reference = `EncodePixel` of the triple `TonemapLdr(new float3(0.8f, 0.8f, 0.8f) * exp2(kKnownPreTonemapEv))` repeated 64 times. For a uniform single-tone population, mean-of-tonemap equals tonemap-of-mean, so the construction is exact up to the sRGB encode/decode round trip and bisection convergence. Assert: (1) `fitted.PostExposureEv` equals `kKnownPreTonemapEv` within `kEstimatorTolerance`; (2) the root-cause assertion — compute the OLD formula's value in-test as `log2(Luma709(TonemapLdr(rawGray)) ratio side)`: oldEv = `math.log2(refLumaMean / srcLumaMean)` where refLumaMean = Luma709 of the reference triple and srcLumaMean = Luma709 of `TonemapLdr(rawGray)` (i.e., the exact post-tonemap means the old code consumed), and assert `kKnownPreTonemapEv > oldEv` with a small named margin constant (e.g. `kDarkBiasMargin = 0.01f`, doc: shoulder compression makes the old post-tonemap ratio undershoot EV by strictly more than this — the reported dark bias); (3) the model-closure assertion — `Luma709(TonemapLdr(rawGray * exp2(fitted.PostExposureEv)))` equals refLumaMean within `kEstimatorTolerance`. If `kUniformGain` (line 43) is now unused, delete it.

(b) **Exact identity test (new).** Single-tone pair: source = 64 pixels of raw gray 0.4; reference = `EncodePixel(TonemapLdr(0.4 gray))` repeated. Assert `fitted.PostExposureEv` equals 0 within `kNeutralKnobTolerance` (only the sRGB round trip + bisection float noise remain — the ramp fixture below is deliberately NOT reused here because its Jensen gap moves EV off exact zero).

(c) **Ramp identity expectation (twin-mirrored).** The full-range 256-level ramp is maximally wide, so the tonemap-of-mean vs mean-of-tonemap curvature gap is FIRST-order, not second-order: with the twin's actual constants, tonemap-of-mean(0.5) = 0.3673 vs mean-of-tonemap = 0.3364, so the identity EV under the new solve is ~ -0.128 — a distinctly negative EV that no honest widened constant pins. In `Fit_IdenticalPair_IsNeutral` (lines 124-157), replace ONLY the PostExposureEv assertion: add a private test-local mirror helper `SolveExposureMirrorForTest(NamerStylePostStats source, NamerStylePostStats reference)` that replicates the production bisection verbatim (test-local `kMirrorBisectionIterations = 48`, same bracket, same sign rule, same float ops) consuming the SAME accumulated stats — per-channel floor `max(new float3(source.RawMeanLinear.r/g/b), 1e-6f)` and `max(reference.MeanLuma, 1e-6f)` — and assert `fitted.PostExposureEv` equals the mirror result within `kEstimatorTolerance` (a verbatim mirror is float-identical; the tolerance absorbs trivial ordering drift). Doc comment on the helper: derives the identity-pair EV expectation from the CPU twin so the pin is wiring, not a magic constant (house rule). All other assertions in that test (WB/contrast/saturation/curve keys) keep their current tolerances. Every value comes from the twin — authoring needs no Unity run.

(d) **Rail tests (new).** Positive rail: source = 64 pixels of DIM raw gray 0.01 (post-tonemap mean luma ~9.2e-4 — clears the 1e-6 near-black guard, so Fit does not throw); reference = 64 raw sRGB white pixels `new Color(1f, 1f, 1f, 1f)` (decodes to exactly 1.0). Derivation for the doc comment: the fit's ACES asymptote is 1/kFitC = 1/0.983729 ~ 1.0165 > 1.0 (NamerStylePostMath.cs:120-124, URP RRTAndODTFit) and TonemapLdr's final saturate clamps the overshoot to exactly 1.0 (first reached at raw input ~25.7) — so a white target IS reachable for bright sources, and the fixture must make it unreachable INSIDE the bracket instead: the dim source caps the model input at 0.01 * 2^10 = 10.24, whose tonemapped luma ~0.97 < 1.0, so the target is unreachable within the ±10 bracket and the bisection converges to exactly +10.0. Assert `fitted.PostExposureEv` equals exactly `10.0` (no tolerance; the float-collapsed bracket endpoint — doc this). Negative rail: source = 64 pixels of HDR gray 30f (`AccumulateSource`'s documented HDR contract; TonemapLdr(30) = 1.0 (saturate-clamped) so the near-black guard passes); reference = 64 black pixels `new Color(0f, 0f, 0f, 1f)` (weight 1 each — WR-01 only refuses zero WEIGHT; MeanLuma 0 floors to kStatEpsilon via the existing max, and Luma709(TonemapLdr(30/1024)) ≈ 7e-3 ≫ 1e-6 stays above it at ev = -10, so the target is unreachable below) — assert `fitted.PostExposureEv` equals exactly `-10.0`. Assert EV only in both.

(e) **WB clamp test.** Rename `Fit_WhiteBalance_ExtremeCast_ClampsToFifteen` → `..._ClampsToFifty`; update its doc (clamp to exactly (-50, +50) = ±kFittedWhiteBalanceLimit after the widened hue-preservation bound; the solve still recovers ~(-100, +100)) and the two assertions to `-50.0` / `50.0` (still exact, no tolerance). Leave `Fit_WhiteBalance_RecoversKnownCast` (12/-5 stays in-band) and `SolveWhiteBalance_Multistart_EscapesStallBasin` untouched.

(f) **Determinism.** `Fit_IsDeterministic` needs NO change — the bisection is fixed-count float math; note that in nothing, just do not touch it.

No new .cs files; no edits to NamerStylePostFitReadbackTests (its non-rail assertions hold — reachable mid-range targets cannot rail).
  </action>
  <verify>
    <automated>cd /Users/Shared/SSDevelopment/Development/Namer-Unity && T=Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs && ! grep -q "ClampsToFifteen" "$T" && ! grep -q "0.67807191" "$T" && grep -q "ClampsToFifty" "$T" && grep -q "SolveExposureMirrorForTest" "$T" && grep -q "kKnownPreTonemapEv" "$T" && grep -q "Assert.AreEqual(-50.0" "$T" && grep -q "Assert.AreEqual(50.0" "$T" && grep -q "Fit_IsDeterministic" "$T" && grep -q "NamerStylePostMath.TonemapLdr" "$T" && echo PASS</automated>
  </verify>
  <done>
Old log2(1.6) pin and the ±15 clamp expectations are gone; the known-EV regression test (with strictly-greater-than-old-formula margin + model-closure assertions), the exact single-tone identity test, the ±10/−10 rail tests, the twin-mirrored ramp EV expectation, and the ±50 clamp test are in place; determinism/contrast/saturation/bloom/curve/WB-recovery/merge tests untouched.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| GPU readback → fit | Source pixels arrive from AsyncGPUReadback; non-finite garbage is already filtered by IsFinitePixel in both accumulators (T-05.1-01-01, unchanged) |
| fit → profile/Volume | Fitted knobs are clamped constants written to the serialized profile; profile-side sanitize re-clamps EV to ±MaxFittedPostExposureEv (NamerStyleProfile.cs:344) |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-260927jkb-01 | Tampering | Fit step (2) bisection | mitigate | Inputs already guarded: non-finite pixels filtered at accumulation, near-black/NaN source refused (existing guard), srcRawMean per-channel floored at kStatEpsilon, refLumaMean floored at kStatEpsilon; output clamped to the ±10 bracket |
| T-260927jkb-02 | DoS | bisection loop | accept | Fixed 48-iteration count, pure float math on a 3-vector — bounded, deterministic, no allocation |
| T-260927jkb-03 | Tampering | WB clamp widen 15→50 | accept | Still bounded (URP's own slider range is ±100; the profile caps stay ±100); hue-rotation bound deliberately chosen at 50 per user decision |
</threat_model>

<verification>
Static grep gates are in each task (no Unity runs, no git writes in the executor session — session constraint). Orchestrator-owned after execution: recompile, targeted live EditMode run of NamerStylePostFitTests + NamerStylePostFitReadbackTests via TestRunnerApi (file markers, not console logs), then the full-suite regression gate before any commit (user permission pending). The two files are the only changed paths.
</verification>

<success_criteria>
- Fit step (2) solves EV pre-tonemap: model Luma709(TonemapLdr(srcRawMean · 2^ev)) ≈ reference.MeanLuma, fixed 48-iteration bisection over the ±10 bracket, rail-exact endpoints
- The regression test proves old-EV < new-EV on a shoulder-compression fixture (the reported dark bias), and the model-closure assertion pins the wiring
- Identity pairs: exact EV 0 single-tone; ramp EV equals the twin-mirrored bisection expectation (~ -0.13, first-order curvature gap); all other knobs keep current tolerances
- Fitted WB clamps at exactly ±50; solve search range and internals unchanged
- Fit_IsDeterministic and the readback non-rail tests pass unchanged; no other stages/files touched
</success_criteria>

<output>
Create `.planning/quick/260927-jkb-fit-look-exposure-solves-pre-tonemap-bis/260927-jkb-SUMMARY.md` when done
</output>

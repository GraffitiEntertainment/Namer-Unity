---
phase: quick-260926-lqr
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
    - "Fitted Temperature and Tint are always within [-15, +15] (kFittedWhiteBalanceLimit), even when the solver recovers an extreme cast"
    - "Fitted Saturation is never negative — any chroma ratio below 1 floors at exactly 0"
    - "Red/Green/Blue curve keys are exact identity diagonals (9 keys, value == time); the master curve is still histogram-fitted"
    - "SolveWhiteBalance recovers a (-40, +40) geometric-mean-normalized target within ±2 (pre-fix stall at ~(-44.1, +30.2) is gone)"
    - "All tests in NamerStylePostFitTests pass in the orchestrator's live-editor EditMode run (4 new/updated tests included)"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs"
      provides: "Hue-preserving fit: ±15 WB clamp, saturation floor, identity RGB curves, grid-multistart WB solve"
      contains: "kFittedWhiteBalanceLimit"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs"
      provides: "In-band WB recovery, extreme-cast clamp, saturation floor, stall regression, floored chroma-ratio expectation"
      contains: "ColorBalanceToLMSCoeffs"
  key_links:
    - from: "NamerStylePostFit.Fit (1) white-balance clamp sites"
      to: "kFittedWhiteBalanceLimit constant"
      via: "clamp(temperature, -kFittedWhiteBalanceLimit, kFittedWhiteBalanceLimit)"
      pattern: "kFittedWhiteBalanceLimit"
    - from: "SolveWhiteBalance multistart comparison"
      to: "WhiteBalanceResidual helper"
      via: "both candidate solutions scored on the same target, lower wins (strict <)"
      pattern: "WhiteBalanceResidual"
    - from: "NamerStylePostFitTests stall regression"
      to: "internal NamerStylePostFit.SolveWhiteBalance"
      via: "InternalsVisibleTo (Editor/AssemblyInfo.cs, already present)"
      pattern: "NamerStylePostFit\\.SolveWhiteBalance"
---

<objective>
Hue-preserving post-look redesign of the reference fit in NamerStylePostFit.cs, plus matching test updates in NamerStylePostFitTests.cs.

The current fit can rotate hue two ways: (a) an extreme white-balance solve rails Temperature/Tint to ±100, and (b) the rendered-art-vs-albedo chroma bias drives Saturation negative (desaturating). The per-channel R/G/B curves were also found to fight the master curve. Additionally the 0-seeded alternating-axis WB solve stalls in a wrong basin for diagonal targets like (-40, +40).

Purpose: keep the fitted post look hue-preserving and make the WB solver globally reliable.
Output: edited NamerStylePostFit.cs (5 edit sites), edited NamerStylePostFitTests.cs (1 fixture moved, 1 expectation updated, 3 new tests).

USER-APPROVED DESIGN — implement exactly as specified below. Do not redesign, do not simplify, do not add speculative guards.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@./CLAUDE.md
@Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs
@Packages/com.graffitientertainment.namer/Editor/AssemblyInfo.cs
</context>

<critical_constraints>
1. **NEVER run `git commit`, `git add`, or any state-changing git command.** The working tree is mixed: NamerStylePostFit.cs carries UNCOMMITTED debug-session hunks (degenerate-source guard in Fit ~lines 261-269, near-black/NaN source checks) that must be preserved byte-for-byte. The ORCHESTRATOR owns all commits.
2. **Tests are run by the ORCHESTRATOR in the live Unity editor after implementation** (interactive editor holds the project lock; no unity-mcp relay in the executor session — 04.2-P04/04.3-02 precedent). Executor verification is read-only: re-read the edited code and confirm the edit checklist via grep. Do NOT promise or attempt CLI/batchmode test runs.
3. **Edit ONLY the two files in `files_modified`.** NamerStyleVolumeWriter.cs is untouched. `ApplyToProfile` is untouched. `NamerStylePostFitParams` fields are untouched (RedKeys/GreenKeys/BlueKeys remain — they now receive identity diagonals).
4. **Minimal-change discipline** (project rule): match surrounding file conventions (kCamelCase constants, Allman braces, Doxygen headers on every method). No renames, no refactors, no new classes, no reformatting of untouched lines. This is Unity C# — the C++ rules in the global profile do not apply, the minimal-change rule does.
5. **Determinism contract** (class doc on NamerStylePostFit): fixed iteration order and counts, strict comparisons, no RNG/time/threading. Every change below preserves it.
</critical_constraints>

<interfaces>
<!-- Current signatures/sites the executor edits (line numbers from the current working tree, debug hunks included). Executor should use these directly — no codebase exploration needed. -->

Constants block (lines ~168-183):
```csharp
private const float kParamRangeLimit = 100f;             // URP percent-knob clamp and percent scale
private const float kWhiteBalanceRange = 100f;           // temperature/tint search bounds [-100, 100]
private const int kWhiteBalanceSolveSweeps = 32;         // alternating-axis golden-section sweeps (fixed)
```

WB clamp site in Fit (lines ~283-285):
```csharp
SolveWhiteBalance(target, out float temperature, out float tint);
fitted.Temperature = clamp(temperature, -kParamRangeLimit, kParamRangeLimit);
fitted.Tint = clamp(tint, -kParamRangeLimit, kParamRangeLimit);
```

Saturation clamp site (line ~332):
```csharp
fitted.Saturation = clamp(saturationValue, -kParamRangeLimit, kParamRangeLimit);
```

Curve fit site (lines ~334-341):
```csharp
float[] masterValues = FitCurveValues(source.LumaHistogram, srcTotal, reference.LumaHistogram, refTotal, null);
fitted.MasterKeys = BuildKeys(masterValues);
fitted.RedKeys = BuildKeys(FitCurveValues(source.RedHistogram, srcTotal, reference.RedHistogram, refTotal, masterValues));
fitted.GreenKeys = BuildKeys(FitCurveValues(source.GreenHistogram, srcTotal, reference.GreenHistogram, refTotal, masterValues));
fitted.BlueKeys = BuildKeys(FitCurveValues(source.BlueHistogram, srcTotal, reference.BlueHistogram, refTotal, masterValues));
```

Solver (lines ~563-633):
```csharp
private static void SolveWhiteBalance(float3 target, out float temperature, out float tint) // t=0, ti=0 seeds, 32 alternating sweeps
private static float MinimizeWhiteBalanceAxis(bool solveTemperature, float t, float ti, float3 target) // golden-section over [-kWhiteBalanceRange, kWhiteBalanceRange]
private static float WhiteBalanceDistance(bool solveTemperature, float candidate, float t, float ti, float3 target) // ColorBalanceToLMSCoeffs -> NormalizeGeometricMean -> distancesq
private static float3 NormalizeGeometricMean(float3 v) // v / pow(|x*y*z|, 1/3); degenerate returns v
```

Test-file anchors: pinned WB constants kPinnedWbRed/Green/Blue (lines 26-28, still used by FitSyntheticPair), Fit_WhiteBalance_RecoversKnownCast (~238-260, currently (20,-5) via pinned constants), Fit_Saturation_ChromaRatio (~269-293). Test file imports do NOT yet include `UnityEngine.Rendering` (needed for ColorUtils).
</interfaces>

<tasks>

<task type="auto" tdd="true">
  <name>Task 1: Hue-preserving fit + grid-multistart WB solve in NamerStylePostFit.cs</name>
  <files>Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs</files>
  <behavior>
    - Fit clamps fitted Temperature/Tint to exactly ±15 (new kFittedWhiteBalanceLimit), not ±100 — search bounds stay ±100
    - Fit floors Saturation at 0: clamp(saturationValue, 0f, kParamRangeLimit); deadband and gray-source guard logic unchanged
    - RedKeys/GreenKeys/BlueKeys are exact identity diagonals (kCurveKeyCount=9 keys, value == time); MasterKeys still fitted via FitCurveValues on luma histograms with null composition
    - SolveWhiteBalance (now internal): zero-seeded solve + grid-seeded solve; the lower WhiteBalanceResidual on the same target wins (strict <; tie keeps the zero-seeded result)
    - Deterministic: fixed grid, strict comparisons, fixed sweep/probe counts
  </behavior>
  <action>
Five targeted edits. Preserve the uncommitted debug-session hunks (near-black/NaN source guard) byte-for-byte.

EDIT 1 — constants block (after `kWhiteBalanceAxisCount` or `kParamRangeLimit`, keep block ordering sensible): add two constants with Doxygen-style inline comments matching the block style:
- `private const float kFittedWhiteBalanceLimit = 15f;` — comment: hue-preserving clamp on the FITTED white-balance knobs; the solve still searches [-100, 100].
- `private const int kWhiteBalanceGridSteps = 10;` — comment: coarse multistart grid points per axis (fixed).

EDIT 2 — WB clamp site (~284-285): change both clamp bounds from `-kParamRangeLimit, kParamRangeLimit` to `-kFittedWhiteBalanceLimit, kFittedWhiteBalanceLimit`. Extend the `(1) White balance` comment with one line noting the fitted knobs clamp to ±kFittedWhiteBalanceLimit (hue preservation — extreme von Kries gains rotate hue, not just warmth).

EDIT 3 — saturation clamp (~332): `fitted.Saturation = clamp(saturationValue, 0f, kParamRangeLimit);` — floor at 0, keep upper bound. Do NOT touch the deadband (`kChromaNoiseFloor`) or gray-source (`kStatEpsilon`) logic above it. Extend the `(4) Saturation` comment: rendered art carries lower mean chroma than painted albedo, so a negative fit is a domain bias, not a look decision — the fit never desaturates.

EDIT 4 — curves (~334-341): keep the master fit line exactly as is. Replace the three Red/Green/Blue `BuildKeys(FitCurveValues(...))` assignments with three assignments from a new private helper `BuildIdentityKeys()`. Add that helper next to `BuildKeys` (~545): it builds a `float[kCurveKeyCount]` with `values[i] = i / (float)(kCurveKeyCount - 1)` and returns `BuildKeys(values)` — exact identity diagonal, 9 keys, value == time. Update the `(5) Curves` comment: master-only histogram specification; R/G/B stay identity diagonals (per-channel specification fought the master map and shifted hue); the masterValues composition parameter of FitCurveValues/MasterInverse is left in place (master path passes null) — no refactor.

EDIT 5 — solver (~556-633):
- Change `private static void SolveWhiteBalance(...)` to `internal static void SolveWhiteBalance(float3 target, out float temperature, out float tint)` (test access via existing InternalsVisibleTo in Editor/AssemblyInfo.cs).
- Extract the existing 32-sweep alternating-axis loop verbatim into `private static void SolveWhiteBalanceSeeded(float3 target, float seedTemperature, float seedTint, out float temperature, out float tint)` — identical body, only the initial `t = seedTemperature; ti = seedTint;` (was 0/0). Same signature shape, Doxygen header, fixed counts.
- Add `private static float WhiteBalanceResidual(float temperature, float tint, float3 target)`: `ColorUtils.ColorBalanceToLMSCoeffs(temperature, tint)` -> `new float3(coeffs.x, coeffs.y, coeffs.z)` -> `NormalizeGeometricMean` -> `distancesq(normalized, target)`. Identical float op sequence to the tail of WhiteBalanceDistance.
- Rewrite `SolveWhiteBalance` (internal) as the multistart orchestrator: (a) `SolveWhiteBalanceSeeded(target, 0f, 0f, out zeroT, out zeroTi)`; (b) coarse grid scan — nested fixed loops, temperature outer, tint inner, `kWhiteBalanceGridSteps` points per axis endpoint-inclusive, value `-kWhiteBalanceRange + (2f * kWhiteBalanceRange * i) / (kWhiteBalanceGridSteps - 1)` (multiplication before division keeps i = last exactly +100), score each point with `WhiteBalanceResidual`, keep the best under strict less-than (first-wins on exact ties in scan order); (c) `SolveWhiteBalanceSeeded(target, bestT, bestTi, out gridT, out gridTi)`; (d) return the candidate with lower `WhiteBalanceResidual` on the same target under strict less-than — tie returns the zero-seeded result. Update the SolveWhiteBalance doc comment to describe the dual-start scheme and why (single 0-seeded start stalls on diagonal targets, e.g. ~(-44.1, +30.2) for a (-40, +40) target).
- `WhiteBalanceDistance`: optionally reduce its body to delegate to `WhiteBalanceResidual(solveTemperature ? candidate : t, solveTemperature ? ti : candidate, target)` — same float op sequence, bit-identical results. Signature unchanged.
  </action>
  <verify>
    <automated>cd "/Users/Shared/SSDevelopment/Development/Namer-Unity" && grep -n "kFittedWhiteBalanceLimit\|kWhiteBalanceGridSteps\|SolveWhiteBalanceSeeded\|WhiteBalanceResidual\|BuildIdentityKeys\|internal static void SolveWhiteBalance\|clamp(saturationValue, 0f, kParamRangeLimit)" Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs && grep -c "FitCurveValues(source.RedHistogram\|FitCurveValues(source.GreenHistogram\|FitCurveValues(source.BlueHistogram" Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylePostFit.cs; echo "R/G/B call-site count above must be 0"</automated>
  </verify>
  <done>
kFittedWhiteBalanceLimit = 15f defined and used at both WB clamp sites (grep shows 3 occurrences); saturation clamp floors at 0f; identity-diagonal helper exists and the three per-channel FitCurveValues call sites are gone (count 0) while the master FitCurveValues call remains; SolveWhiteBalance is internal and contains the grid multistart with WhiteBalanceResidual scoring; ApplyToProfile, NamerStylePostFitParams fields, the debug-session source guards, and every other method are untouched; no git state-changing commands were run.
  </done>
</task>

<task type="auto" tdd="true">
  <name>Task 2: Test updates in NamerStylePostFitTests.cs (recovery in-band, clamp, floor, stall regression)</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs</files>
  <behavior>
    - Fit_WhiteBalance_RecoversKnownCast: fixture cast now ColorBalanceToLMSCoeffs(12, -5) built live; asserts recovery of (12, -5) within 0.5 — passes only because 12 is inside the ±15 fitted clamp
    - NEW Fit_WhiteBalance_ExtremeCast_ClampsToFifteen: (-100, +100) cast fixture -> fitted.Temperature == -15 and fitted.Tint == +15 exactly
    - NEW Fit_Saturation_NeverDesaturates_FloorsAtZero: saturated source + near-gray reference -> fitted.Saturation == 0 exactly
    - NEW SolveWhiteBalance_Multistart_EscapesStallBasin: direct internal call on normalized coeffs(-40, +40) target -> recovers (-40, +40) within ±2 (pre-fix: ~(-44.1, +30.2))
    - Fit_Saturation_ChromaRatio expectation wrapped in max(0f, ...)
  </behavior>
  <action>
Five targeted edits. Tests are NUnit `[Test]` EditMode tests in the existing style: fixture construction first, hand-derived expectation beside the assertion, Doxygen doc header per test.

EDIT 1 — imports: add `using UnityEngine.Rendering;` (ColorUtils lives there; the file does not import it yet).

EDIT 2 — Fit_WhiteBalance_RecoversKnownCast (~229-260): replace the pinned-constant cast with a live construction `Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(12f, -5f); float3 cast = new float3(coeffs.x, coeffs.y, coeffs.z);` and change the assertions to expect (12, -5) within the existing `kWhiteBalanceRecoveryTolerance`. Update the doc header: the fixture moved in-band because the fitted knobs now clamp to ±kFittedWhiteBalanceLimit (a (20, -5) fixture would assert the clamp, not the solve); the live ColorUtils construction keeps the pin-against-upstream-grading-change property. Keep the `kPinnedWbRed/Green/Blue` constants — FitSyntheticPair still uses them — but update their header comment to say they are the FitSyntheticPair determinism cast (no longer "derived below at Fit_WhiteBalance_RecoversKnownCast").

EDIT 3 — new test `Fit_WhiteBalance_ExtremeCast_ClampsToFifteen`, placed right after the recovery test: same 64-pixel gray construction (source GrayPixel(0.5f), reference = tonemapped gray cast by `ColorBalanceToLMSCoeffs(-100f, 100f)` via LmsToLinear/LinearToLms then EncodePixel). Assert `fitted.Temperature` equals -15.0 and `fitted.Tint` equals +15.0 with NO tolerance (the clamp yields exactly ±kFittedWhiteBalanceLimit as floats). Doc header: extreme cast stays hue-preserving — solver recovers ~(-100, +100), the fitted knobs clamp to exactly (-15, +15).

EDIT 4 — new test `Fit_Saturation_NeverDesaturates_FloorsAtZero`, placed after Fit_Saturation_ChromaRatio: 64 pixels of source `(0.6f, 0.35f, 0.15f, 1f)` (saturated), 64 pixels of reference `new Color(0.05f, 0.048f, 0.046f, 1f)` (near-gray; all channels decode on the exact linear segment v/12.92, chroma = (0.05-0.046)/0.05 = 0.08, so the chroma delta magnitude is far above the 1e-3 deadband and the ratio path runs). Assert `fitted.Saturation` equals 0.0 exactly. Doc header: raw ratio ~ (0.08/srcChroma - 1)*100 is strongly negative; the floor pins it at exactly 0.

EDIT 5 — new test `SolveWhiteBalance_Multistart_EscapesStallBasin`, placed after the extreme-cast test: build `Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(-40f, 40f);`, normalize geometric-mean with a small test-local static helper mirroring the production definition (`v / pow(abs(x)*abs(y)*abs(z), 1f/3f)`, degenerate guard returns v — comment that it mirrors NamerStylePostFit.NormalizeGeometricMean), then call `NamerStylePostFit.SolveWhiteBalance(target, out float temperature, out float tint)` directly (internal access via InternalsVisibleTo) and assert `temperature` within 2.0 of -40.0 and `tint` within 2.0 of +40.0. Doc header: stall regression — the pre-multistart 0-seeded alternating solve converged to ~(-44.1, +30.2) on this diagonal target; the grid-seeded second start must recover it.

EDIT 6 — Fit_Saturation_ChromaRatio expectation (~290): `float expectedSaturation = max(0f, (kRefChromaExact / srcChroma - 1f) * 100f);` and extend the doc header: expectation uses the floored max(0, ...) formula per the saturation floor.

No other test changes: Fit_IsDeterministic, Fit_IdenticalPair_IsNeutral (identical pair stays neutral — saturation 0 either way, WB neutrality unaffected since the multistart only lowers the residual), Fit_MasterCurve_MatchesGammaPair (master still fitted), and AccumulateReference_MergesWeights must remain byte-identical.
  </action>
  <verify>
    <automated>cd "/Users/Shared/SSDevelopment/Development/Namer-Unity" && grep -n "using UnityEngine.Rendering;\|ColorBalanceToLMSCoeffs(12f\|ColorBalanceToLMSCoeffs(-100f\|ColorBalanceToLMSCoeffs(-40f\|Fit_WhiteBalance_ExtremeCast_ClampsToFifteen\|Fit_Saturation_NeverDesaturates_FloorsAtZero\|SolveWhiteBalance_Multistart_EscapesStallBasin\|max(0f, (kRefChromaExact" Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylePostFitTests.cs</automated>
  </verify>
  <done>
All six edits present at the expected anchors; the four new/updated behaviors are wired (in-band recovery at (12,-5), exact ±15 clamp assertion, exact saturation-floor assertion, ±2 stall-regression assertion via internal SolveWhiteBalance); no existing test beyond the two specified was modified; file still compiles against Task 1's internal surface; no git state-changing commands were run.
  </done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| GPU readback -> fit input | already guarded (IsFinitePixel skips, near-black/NaN source throw — uncommitted hunks preserved, not altered) |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-q260926-01 | Tampering | WB multistart grid scan | mitigate | fixed grid size/endpoint formula, strict less-than, first-wins ties — deterministic, no RNG or data-dependent early exit |
| T-q260926-02 | Denial of Service | grid multistart cost | accept | 100 residual evaluations + one extra 32-sweep solve, pure CPU float math — negligible vs the existing solve |
| T-q260926-SC | Tampering | package installs | accept | no package installs in this task |
</threat_model>

<verification>
Executor (read-only, no editor access):
1. Both greps in Task 1/Task 2 verify blocks return the expected hits; R/G/B FitCurveValues call-site count is 0.
2. Re-read the final NamerStylePostFit.cs diff regions and walk this checklist: kFittedWhiteBalanceLimit at 3 sites; saturation floor 0f; BuildIdentityKeys used for R/G/B; master fit untouched; SolveWhiteBalance internal + dual-start + WhiteBalanceResidual; ApplyToProfile/fields/debug-hunks untouched.
3. Confirm with `git diff --stat` (read-only) that only the two files in files_modified gained changes attributable to this session (other pre-existing modified files in the tree are the debug session's — do not touch or stage anything).

Orchestrator (after implementation, live editor):
- Run the EditMode test class NamerStylePostFitTests (targeted groupNames run per the live-editor RunCommand pattern; no Refresh, file markers). Expect all green: 13 tests total (10 existing + 3 new), with Fit_WhiteBalance_RecoversKnownCast and Fit_Saturation_ChromaRatio updated in place.
- Orchestrator owns the git commit(s) for the mixed working tree.
</verification>

<success_criteria>
- Fitted Temperature/Tint can never exceed ±15; extreme (-100,+100) cast fits to exactly (-15, +15)
- Fitted Saturation can never go below 0; the near-gray-reference fixture reads exactly 0
- R/G/B keys are exact identity diagonals while MasterKeys still tracks the gamma-pair fixture
- Normalized coeffs(-40,+40) target recovers (-40,+40) within ±2 through the internal SolveWhiteBalance
- NamerStylePostFitTests fully green in the orchestrator's live-editor run; determinism test still element-wise identical across double-runs
- No commits made by the executor; no files beyond the two targets edited
</success_criteria>

<output>
Create `.planning/quick/260926-lqr-hue-preserving-post-look-redesign-in-nam/260926-lqr-SUMMARY.md` when done
</output>

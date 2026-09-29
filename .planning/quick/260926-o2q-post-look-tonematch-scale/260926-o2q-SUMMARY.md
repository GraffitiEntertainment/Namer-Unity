---
phase: quick-260926-o2q
plan: 01
subsystem: stylization
tags: [unity, urp, post-processing, tone-match, color-grading, editor-tooling, csharp]

# Dependency graph
requires:
  - phase: 05.1 (D-04 fitted post look, plans 02/03)
    provides: ApplyPostLook preview uniforms, NamerStyleVolumeWriter.Write, fitted post params on NamerStyleProfile
  - phase: quick-260926-hk7
    provides: ToneMatch slider field with DefaultToneMatch 0 and the NaN-sanitize precedent (T-hk7-01)
provides:
  - NamerPostLookScale shared helper (IsActive / Scale / ScaleCurve / ResolveToneMatch) — the ONE Tone Match scale
  - Tone-Match-scaled fitted post look, identical in the After-pane preview and the generated scene Volume
  - ToneMatch-aware curve-strip cache key (slider edits rebuild the strip)
  - Fixture audit: full-strength post tests now pin ToneMatch = 1 explicitly
affects: [05.1 post look, future fit-solver work, WB ±15 clamp issue, stylize compute integration]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Shared pure scale helper consumed identically by preview and Volume writers so the two cannot diverge (single sanitize point for NaN/out-of-range input)"
    - "s>=1 fast paths return inputs untouched (byte-identical at full strength); s<=0 returns exact neutral"
    - "Cache keys include every live scale input (ToneMatch appended in the AppendCurveKey R-format idiom)"

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerPostLookScale.cs
  modified:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStyleVolumeWriter.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStyleVolumeWriterTests.cs

key-decisions:
  - "Scale applied at apply/write time only through NamerPostLookScale; the profile is never written back (source of truth preserved)"
  - "Bloom threshold deliberately unscaled (separability floor); bloom intensity scales like every other fitted param"
  - "Enable ANDs NamerPostLookScale.IsActive so s=0 writes _PostEnable 0 (fully off) instead of enabled-with-neutral-params"

patterns-established:
  - "Tone Match resolve: NaN -> 0 before Clamp01, shared by every consumer (writer path pre-sanitized by ClampToCaps, preview path not)"
  - "Per-key curve scaling via struct copy preserves times/tangents/weights; only values move toward the identity diagonal"

requirements-completed: [STYL-09]

# Metrics
duration: 3 min
completed: 2026-09-26
---

# Quick Task 260926-o2q: Post Look Tone Match Scale Summary

**Fitted post look now scales by Tone Match through one shared NamerPostLookScale helper wired identically into ApplyPostLook preview uniforms, pane bloom, the curve-strip funnel, and NamerStyleVolumeWriter.Write (preview == generated Volume at every setting)**

## Performance

- **Duration:** 3 min 27 s
- **Started:** 2026-09-27T00:41:40Z
- **Completed:** 2026-09-27T00:45:07Z
- **Tasks:** 3
- **Files modified:** 5 (4 modified + 1 created)

## Accomplishments
- New `NamerPostLookScale` helper: `IsActive` (contributes anything), `Scale` (scalar lerp toward neutral 0), `ScaleCurve` (per-key lerp toward the identity diagonal, value' = Lerp(time, value, s)), `ResolveToneMatch` (NaN -> 0, then Clamp01 — T-hk7-01 precedent). s>=1 fast paths return inputs untouched (byte-identical at full strength); s<=0 returns exact neutral.
- ApplyPostLook: enable ANDs IsActive (s=0 -> _PostEnable 0 with the exact neutral table and null strip — the reported 2026-09-26 defect), temperature/tint scaled before ColorBalanceToLMSCoeffs, EV/contrast/saturation scaled before the /100f+1 URP twins.
- Pane bloom intensity scales by s (threshold unchanged); renderer's PostBloomEnabled gate (intensity > 0 && threshold > 0) puts the chain on the disabled path at s=0.
- Curve funnel: `GetOrCreatePostCurveTexture(profile, toneMatch)` / `BuildPostCurveTexture(profile, toneMatch)` hoist four ScaleCurve'd curves; `ComputePostCurveCacheKey` appends `|T:{ToneMatch:R}` so slider edits rebuild the cached strip.
- NamerStyleVolumeWriter.Write: reads toneMatch once after ClampToCaps (already sanitized), scales WB temp/tint, EV, contrast, saturation, bloom intensity, and all four curves through the same helper members. Bloom threshold and the Tonemapping ACES block untouched.
- After View tooltip now reads "...curves, bloom) scaled by Tone Match in-shader...".
- Fixture audit: both full-strength fixtures (`ApplyPostLook_FittedProfile_SetsFullTable`, `CreateFittedProfile`) pin `ToneMatch = 1f` — DefaultToneMatch is 0 since hk7; their verbatim value assertions become the s=1 byte-identical pins.
- Six new tests: preview s=0 fully-off table, preview s=0.5 exact halves, Scale endpoint unit tests (incl. NaN -> 0 and out-of-range clamp), ScaleCurve diagonal unit tests (incl. same-reference at s=1 and null -> null), Volume s=0 neutral (threshold UNSCALED at 0.82, curves on identity diagonal, ACES pinned), Volume s=0.5 exact halves.

## Task Commits

None by the executor — per plan constraint 1, the ORCHESTRATOR owns all commits for this quick task (260926-mtv / 260926-lqr precedent). Expected orchestrator commit set: the five files below plus the Unity-generated `NamerPostLookScale.cs.meta` and this SUMMARY.

## Files Created/Modified
- `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerPostLookScale.cs` (created) — the ONE shared Tone Match scale helper (internal static, four members, Doxygen headers)
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — ApplyPostLook scaled uniforms + IsActive-gated enable; ApplyPreviewPostLook bloom intensity scaled (threshold untouched, comment updated); curve funnel two-arg signature + ToneMatch cache-key entry + four ScaleCurve hoists; tooltip copy
- `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStyleVolumeWriter.cs` — Write doc + toneMatch local (after ClampToCaps) + 6 Scale sites + 4 ScaleCurve sites; threshold and ACES untouched
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs` — ToneMatch=1 fixture line + 4 new tests (ToneMatchZero / ToneMatchHalf / Scale_ToneMatchEndpoints_AreExact / ScaleCurve_LerpsKeysTowardDiagonal)
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStyleVolumeWriterTests.cs` — ToneMatch=1 fixture line in CreateFittedProfile + 2 new tests (Write_ToneMatchZero_WritesNeutralVolume / Write_ToneMatchHalf_HalvesFittedParams)

## Decisions Made
None beyond the plan - implemented exactly as the user-approved design specified.

## Verification Performed (executor, read-only)

### Grep gates (raw outcomes)
- Task 1: `class NamerPostLookScale` / `IsActive` / `static float Scale` / `ScaleCurve` / `ResolveToneMatch` all present (lines 14/21/23/32/34/59/66/88). .meta count at gate time: **0** (see Deviations note 2 for the final-state count of 1).
- Task 2: NamerPostLookScale in the window at enable (832) + temp/tint (838/839) + EV (843) + contrast (846) + saturation (849) + bloom intensity (792) + 4 ScaleCurve hoists (932-935); in the writer at 6 Scale sites (108/110/114/116/118/126) + 4 ScaleCurve sites (130-136). "scaled by Tone Match" count == 1 (tooltip, line 1482). Single-arg `GetOrCreatePostCurveTexture(profile)` count == 0. Writer `ScaleCurve` count == 4. `tonemapping.mode.value = TonemappingMode.ACES` present (line 104).
- Task 3: `ToneMatch = 1f` in both audited fixtures (preview line 102, writer line 372); all 6 new test methods present; `[Test]` counts == 11 (NamerPostPreviewTests) and == 7 (NamerStyleVolumeWriterTests).

### Read-only walkthrough checklist
- ApplyPostLook scales BEFORE ColorBalanceToLMSCoeffs and BEFORE the /100f+1 transforms — PASS
- Enable ANDs NamerPostLookScale.IsActive — PASS
- Bloom threshold arm untouched in both ApplyPreviewPostLook (`BloomThresholdLinear(profile)`) and Write (line 124) — PASS
- ComputePostCurveCacheKey carries the `|T:` R-format ToneMatch entry — PASS
- BuildTextureCurve receives ScaleCurve(...) for all four curves — PASS
- Write's Tonemapping block untouched — PASS
- No scale written back into the profile (`profile.ToneMatch =` grep across the three production files: zero matches) — PASS
- `GetOrCreatePostCurveTexture` / `BuildPostCurveTexture` have exactly one call site each (ApplyPostLook / GetOrCreatePostCurveTexture) — PASS
- `git status --porcelain`: session-attributable changes are exactly the five files_modified (plus the Unity-generated .meta, see Deviations note 2); all other entries predate this session

## Tests NOT Run (deferred to orchestrator)
Per plan constraint 2, the executor did not run Unity, compile, or execute tests. Tasks 2 and 3 are authored-but-not-run (260926-mtv TDD precedent). Orchestrator owns: recompile, targeted EditMode groups NamerPostPreviewTests + NamerStyleVolumeWriterTests (expect 11/11 and 7/7), full-suite regression (last full run 298/299 with 1 pre-existing AO drift), and the live probe (0/0 no ramp, 1 full strength, 0.5 visibly half, regenerate Volume parity).

## Deviations from Plan

None in scope or design - the five files were edited exactly as specified, no other production/test files touched, no git state-changing commands run.

Two transparency notes (not design deviations):

1. **Transient edit artifact, corrected within Task 2:** the first Edit applying Write's doc-header change briefly wrote a malformed `throw` statement (`newInvalidOperationExceptionPlaceholder`); the immediately following Edit restored the original `InvalidOperationException` throw block verbatim. The error never survived the task boundary - Task 2's verify gates and the full re-read of Write confirm the final state. No commit ever existed in that state (executor makes no commits).
2. **Unity generated the .meta ahead of the orchestrator's refresh:** at Task 1's gate the `NamerPostLookScale.cs.meta` count was 0 (executor authored none, per constraint). The live interactive Unity editor auto-refreshed during the session and generated the meta (standard `fileFormatVersion: 2` + guid content, read-only verified). It now appears untracked with count 1 at the final re-run. Per the plan's orchestrator section, the orchestrator commits it. Executor took no action on it (deleting it could churn the guid).

## Determinism Notes (exact-float fixture values)
- Every s=0.5 expectation is an exact binary half: Lerp(0, v, 0.5) == v*0.5 exactly for 20->10, -10->-5, 1.5->0.75, 12.5->6.25, -3.25->-1.625, 0.75->0.375, 8->4, -4.5->-2.25, 1->0.5 (all dyadic).
- Preview z at half: Scale(25, .5)/100 + 1 = 12.5/100 + 1 = 1.125 exactly (0.125 and 1.125 are dyadic).
- Preview s at half: -20/100 rounds to float -0.2f; 1f - 0.2f rounds to the float literal 0.8f (same arithmetic shape as the existing 0.6f assertion that passes today).
- ScaleCurve at s=0: Mathf.Lerp(time, value, 0) = time + (value-time)*0 = time exactly; s=0.5 on (0.25, 0.75) = 0.5 exactly (dyadic); s=1 returns the same reference.
- Volume curve assertions carry 0.01/0.02 tolerances for the 128-texel TextureCurve bake only; scalars are tolerance-free.

## Known Stubs

None - no placeholder values, no unwired data paths.

## Threat Flags

No new threat surface beyond the plan's register. Register dispositions implemented as planned:
- T-q260926-o2q-01 (NaN/out-of-range ToneMatch, preview path) — mitigated: ResolveToneMatch NaN->0 then Clamp01, one sanitize point shared by every consumer.
- T-q260926-o2q-02 (stale cached curve strip) — mitigated: cache key appends R-format ToneMatch (`|T:` tag).
- T-q260926-o2q-03 (preview/Volume divergence) — mitigated: both writers scale through the same NamerPostLookScale member per param; s=0/s=0.5/s=1 tests pin both to the same fixtures.
- T-q260926-o2q-04 (per-frame allocations) — accepted: s>=1 returns the same reference (zero alloc); s<1 allocates 4 small arrays per strip REBUILD only (cache-gated).

## Self-Check

- [x] `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerPostLookScale.cs` exists (created)
- [x] `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` modified (grep-verified wiring)
- [x] `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStyleVolumeWriter.cs` modified (grep-verified wiring)
- [x] `Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs` modified (11 [Test])
- [x] `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStyleVolumeWriterTests.cs` modified (7 [Test])
- [x] Commit-hash check: N/A by design — executor runs no commits; orchestrator owns them

## Self-Check: PASSED

---
*Phase: quick-260926-o2q*
*Completed: 2026-09-26*

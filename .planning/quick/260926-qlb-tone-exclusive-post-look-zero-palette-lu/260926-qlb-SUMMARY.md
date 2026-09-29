---
phase: quick-260926-qlb
plan: 01
subsystem: stylize-post-tone-composition
tags: [stylization, post-look, tone-match, preview, bake, fit]
requires:
  - "o2q: NamerPostLookScale scales the fitted post grade by ToneMatch (uncommitted, working tree)"
provides:
  - "NamerStyleStylization.Create(profile, split, postOwnsTone = false) — the one documented home of the exclusive-tone rule"
  - "ComposePreviewStylization(profile, afterView, split, forcePostOwnsTone = false) — predicate forcePostOwnsTone || (post lens && HasPostFit)"
  - "NamerProcessor bake composes with postOwnsTone: styleProfile.HasPostFit (same predicate as the Volume gate)"
affects:
  - "RunStylePostFit forced pass (fits from a tone-neutral base — params become ToneMatch-knob-independent)"
  - "Stylized + Post preview lens (fitted: tone-neutral base + post grade as only tone; unfitted: W6 degrade keeps pull)"
  - "Bake output for fitted profiles (Volume's scaled post is the only runtime tone)"
tech-stack:
  added: []
  patterns:
    - "Trailing default parameter for backward-compatible seam widening (CreateProjectionContext/GenerateResidual precedent)"
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs
decisions:
  - "Base-side predicate is HasPostFit ALONE, deliberately not NamerPostLookScale.IsActive — HasPostFit + ToneMatch = 0 reads as tone entirely untouched (pull zeroed AND post scaled to nothing); inline comment forbids 'fixing' this"
  - "The zero lands in Create's object initializer, before Active is computed — tone-only + postOwnsTone = honest zero-dispatch identity"
metrics:
  duration: ~18 min
  completed: 2026-09-26
  tasks: 2
  files: 5
---

# Quick 260926-qlb: Tone-Exclusive Post Look — Zero Palette Luma Pull Summary

Exclusive-tone rule: when the post look owns tonality, the palette luma pull is zero. One trailing `postOwnsTone` parameter on `NamerStyleStylization.Create` is the single documented home of the rule, applied at the three compose sites — the RunStylePostFit forced pass (always: solver fits the full tonal distance, params become ToneMatch-knob-independent), the Stylized + Post preview lens (iff `profile.HasPostFit`), and the NamerProcessor bake (iff `styleProfile.HasPostFit`, the same predicate as the Volume gate at NamerProcessor.cs:391 — no double tone at runtime).

## What Was Done

### Task 1: postOwnsTone seam + the three compose sites

**NamerStylizePipeline.cs (Create, ~89-143)**
- Signature widened: `Create(NamerStyleProfile profile, NamerSplitResult split, bool postOwnsTone = false)` — trailing default, existing 2-arg call sites compile unchanged.
- `ToneMatch = postOwnsTone ? 0f : Mathf.Clamp01(profile.ToneMatch)` — the zero lands in the object initializer, BEFORE Active is computed (~139-141), so a tone-only profile under post-owns-tone composes the honest zero-dispatch identity.
- `/// <summary>` extended with the full exclusive-tone rule (post grade is the ONLY tone mechanism; StyleStrength/Smoothing/HueReplaceMode/anchor filtering untouched; fitted params solve against a tone-neutral base and stay knob-independent).

**NamerEditorWindow.cs — ComposePreviewStylization (~2666-2694)**
- Trailing parameter `bool forcePostOwnsTone = false`; after the null/lens guard: `bool postOwnsTone = forcePostOwnsTone || (afterView == kAfterViewPost && profile.HasPostFit);` forwarded as Create's third argument.
- Dated inline comment states the deliberate edge: base-side predicate is HasPostFit ALONE, NOT NamerPostLookScale.IsActive (HasPostFit + ToneMatch=0 = tone entirely untouched); do not "fix".
- Summary doc updated: post lens composes the SAME stylization (W5) EXCEPT the pull is zeroed when a fit exists; unfitted = W6 degrade keeps the pull; forcePostOwnsTone = the RunStylePostFit creation pass.

**NamerEditorWindow.cs — RecomputePreview call site (~615-620)**
- Passes `forcePostOwnsTone: _forceStylizeForFit` (named argument); one dated 260926-qlb comment joining the neighboring WR-02 annotation tone. Lens ternary untouched.

**NamerProcessor.cs — bake compose (~210-227)**
- `Create(styleProfile, ..., styleProfile.HasPostFit)`; comment block extended: a fitted bake hands tonality to the generated scene Volume's scaled post grade (Volume gate further down writes under the SAME HasPostFit predicate); unfitted bakes unchanged.

### Task 2: seam + lens predicate tests in existing fixtures

**NamerStylizeKernelTests.cs — `Create_PostOwnsTone_ZeroesToneMatchOnly` (after Context_IdentitySettings_InactiveAndNoMeshFlagged)**
- Fixture idiom reused (ReferenceFingerprint + one valid anchor, try/finally DestroyImmediate). Profile: ToneMatch=0.8, StyleStrength=0.6, Smoothing=0.2, HueMode=1 (non-default, proves preservation).
- 7 asserts: postOwnsTone:true → ToneMatch==0 with StyleStrength 0.6 / Smoothing 0.2 / HueReplaceMode 1 / AnchorCount 1 (the zero touches ONLY tone); default Create → ToneMatch==0.8 (today's behavior unchanged); tone-only variant (strength 0, smoothing 0, toneMatch 0.5, anchors) + postOwnsTone:true → Active==false (zero-dispatch identity).

**NamerPostPreviewTests.cs — `ComposePreviewStylization_ToneExclusivePostLook_Rule` (after ComposePreviewStylization_PostView_ComposesStylization)**
- Fitted idiom (`PostFitFingerprint = "synthetic-fit-fingerprint"`) + extracted idiom (ReferenceFingerprint + one anchor) on BOTH profiles; ToneMatch=0.8.
- Four predicate legs, each message naming the leg: (a) fitted + kAfterViewPost → 0; (b) unfitted + kAfterViewPost → 0.8 (W6 degrade); (c) fitted + kAfterViewStylized → 0.8 (stylized-only unchanged); (d) fitted + kAfterViewStylized + forcePostOwnsTone:true → 0 (forced fit pass). Doc comment cites the rule and the accepted HasPostFit-vs-IsActive edge.

### Audit pass (Task 2 step 3 — confirmed, not re-derived)

`grep -rn "ToneMatch" Tests/Editor | grep -i postfitfingerprint` → 0 same-line hits; file-level inspection confirms the only fixtures combining a fitted profile with ToneMatch>0 (NamerPostPreviewTests ApplyPostLook/BloomThreshold/Scale tests) assert the POST layer (ApplyPostLook + NamerPostLookScale — untouched code), not the stylization pull. `Process_E2E_AttachedProfile_StylingFlowsIntoBakedAssets` uses an unfitted profile at default ToneMatch 0. No old-spec double-tone fixture exists; nothing needed updating.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Restored doc comment dropped by my own edit**
- **Found during:** Task 2, test insertion into NamerPostPreviewTests.cs
- **Issue:** My Edit's old_string included the `AfterViewPopup_HasThreeEntries_InOrder` doc comment as an anchor but the replacement omitted it, deleting the comment.
- **Fix:** Re-inserted the summary verbatim immediately after; net zero (verified lines 96-99).
- **Files modified:** NamerPostPreviewTests.cs

Otherwise: none — plan executed exactly as written.

## Session-Constraint Compliance

- NO git commit/add/restore/stash run by the executor (orchestrator owns all commits — read-only git only).
- NO Unity interaction (no recompile, no AssetDatabase, no test runs — orchestrator owns live EditMode verification). All verify steps were grep/static per the plan.
- No new .cs files (both tests went into existing fixtures; no .meta generated).
- Nothing under Assets/ touched; forbidden files untouched (NamerStylePostFit, NamerStyleVolumeWriter, NamerPostLookScale, ApplyPostLook body, compute kernels, channel panes, albedo preview plumbing, WB clamp).
- Pre-existing uncommitted o2q changes (NamerPostLookScale.cs, NamerEditorWindow.cs, NamerStyleVolumeWriter.cs, both test fixtures) left intact — qlb edits layered on top as instructed.

## Known Stubs

None — all changes are functional wiring plus tests; no placeholder values introduced.

## Threat Flags

None. No new trust-boundary surface: ToneMatch remains sanitized (ClampToCaps NaN fallback + Mathf.Clamp01; postOwnsTone only selects 0 — T-qlb-01 accept), HasPostFit is an existing unchanged getter (T-qlb-02 accept).

## Verification Results (static, per plan)

- `postOwnsTone` at the seam: NamerStylizePipeline.cs:89 (doc), :102 (parameter), :116 (ternary) — PASS
- Window sites: NamerEditorWindow.cs:2691-2693 (predicate + forward), :620 (`forcePostOwnsTone: _forceStylizeForFit`), :2680 (parameter) — PASS
- Bake: NamerProcessor.cs:224-226 (HasPostFit as 3rd arg), Volume gate :391 under the same predicate — PASS
- Call-site sweep: every production `NamerStyleStylization.Create` site is the definition, the window forward, or the HasPostFit bake; all test sites are 2-arg defaults — PASS
- Test names present: NamerStylizeKernelTests.cs:819, NamerPostPreviewTests.cs:53; assert counts 7 and 5 (>= 3 and >= 4) — PASS
- ToneMatch/PostFitFingerprint audit sweep: 0 hits — PASS (audit clean, no fixture updated)
- Changed-file set = files_modified + pre-existing o2q modifications; nothing outside scope — PASS

## Self-Check: PASSED

- Files: all 5 modified files exist on disk (verified via Read/Edit during execution; git status shows each as modified) — FOUND
- New tests: `Create_PostOwnsTone_ZeroesToneMatchOnly` (NamerStylizeKernelTests.cs:819), `ComposePreviewStylization_ToneExclusivePostLook_Rule` (NamerPostPreviewTests.cs:53) — FOUND
- Commits: N/A by session constraint (orchestrator owns commits; nothing committed by executor — verified via read-only git status showing working-tree modifications intact)

## Orchestrator Live Verification (2026-09-26/27)

- Static verifier (gsd-verifier): 7/7 checks PASSED — report in [260926-qlb-VERIFICATION.md](./260926-qlb-VERIFICATION.md).
- Forced recompile via relay — Editor + Tests DLLs rebuilt clean; new symbols verified landed.
- Targeted live EditMode run of the two touched fixtures (NamerStylizeKernelTests + NamerPostPreviewTests): **28/28 passed**.
- Full EditMode regression gate: **311/311 passed** (309 pre-existing + the 2 new tests; the two previously flaky AO tests passed this run — AO-pref isolation remains a known deferred gap).
- Commits HELD for explicit user permission (code + this docs append); no deviation found between plan, executor report, and live results.

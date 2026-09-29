---
phase: quick-260926-qlb
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs
autonomous: true
requirements: [STYL-09]
must_haves:
  truths:
    - "When the post look owns tonality, the palette luma pull is ZERO: NamerStyleStylization.Create(profile, split, postOwnsTone: true) composes ToneMatch = 0 while StyleStrength, Smoothing, HueReplaceMode, and anchors are byte-identical to the default compose"
    - "RunStylePostFit's forced stylize pass composes the base with ToneMatch = 0 regardless of lens or HasPostFit, so the solver always fits the FULL tonal distance from a tone-neutral base (fitted params become ToneMatch-knob-independent — no refit after slider edits)"
    - "The Stylized + Post preview lens composes ToneMatch = 0 when profile.HasPostFit (the post grade rides on top as the only tone mechanism); an UNFITTED profile at the same lens keeps profile.ToneMatch (W6 degrade — pull stays on, no post exists to own tone)"
    - "The Stylized-only lens (kAfterViewStylized) is completely unchanged — ToneMatch = profile.ToneMatch remains the tone mechanism there"
    - "NamerProcessor bakes the base with ToneMatch = 0 when styleProfile.HasPostFit (the generated scene Volume carries the scaled post — no double tone at runtime); unfitted bakes are unchanged"
    - "HasPostFit + ToneMatch = 0 reads as tone entirely untouched: pull zeroed AND post scaled to nothing (the base-side predicate is HasPostFit alone, deliberately NOT NamerPostLookScale.IsActive)"
    - "Existing 2-arg Create and 3-arg ComposePreviewStylization call sites compile unchanged (trailing default parameters; the trailing-param precedent of CreateProjectionContext/GenerateResidual)"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs"
      provides: "Create(profile, split, bool postOwnsTone = false) — the ONE documented home of the exclusive-tone rule; zeroes ToneMatch before Active is computed"
      contains: "postOwnsTone"
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
      provides: "ComposePreviewStylization(profile, afterView, split, bool forcePostOwnsTone = false) — in-body predicate forcePostOwnsTone || (afterView == kAfterViewPost && profile.HasPostFit); call site passes _forceStylizeForFit"
      contains: "postOwnsTone"
    - path: "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs"
      provides: "Bake compose passes styleProfile.HasPostFit as postOwnsTone"
      contains: "HasPostFit"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs"
      provides: "Create_PostOwnsTone_ZeroesToneMatchOnly — seam truth: tone zeroed, everything else preserved, tone-only + postOwnsTone = zero-dispatch identity, default path unchanged"
      contains: "Create_PostOwnsTone_ZeroesToneMatchOnly"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs"
      provides: "ComposePreviewStylization_ToneExclusivePostLook — the four predicate legs (fitted+post=0, unfitted+post=pull, stylized lens=pull, forced fit=0)"
      contains: "ComposePreviewStylization_ToneExclusivePostLook"
  key_links:
    - from: "NamerEditorWindow.ComposePreviewStylization"
      to: "NamerStyleStylization.Create"
      via: "postOwnsTone = forcePostOwnsTone || (afterView == kAfterViewPost && profile.HasPostFit), forwarded as Create's 3rd arg"
      pattern: "postOwnsTone"
    - from: "NamerEditorWindow RecomputePreview call site (~line 615)"
      to: "RunStylePostFit's forced pass"
      via: "4th argument _forceStylizeForFit — the fit renders its base tone-neutral even though the lens ternary passes kAfterViewStylized"
      pattern: "_forceStylizeForFit \\? kAfterViewStylized : _afterView"
    - from: "NamerProcessor.Process bake compose (~line 219)"
      to: "NamerStyleVolumeWriter.Write gate (~line 385, styleProfile.HasPostFit)"
      via: "the same HasPostFit predicate decides BOTH the zeroed base AND the Volume write — the Volume's scaled post is the only tone at runtime"
      pattern: "HasPostFit"
    - from: "The base-side predicate"
      to: "ApplyPostLook's enable gate (postActive && HasPostFit && NamerPostLookScale.IsActive)"
      via: "intentionally weaker — HasPostFit alone owns the base-side zeroing so ToneMatch=0 yields BOTH zero pull AND zero post (tone fully untouched); comment must state this so it is not 'fixed' later"
      pattern: "NamerPostLookScale\\.IsActive"
---

<objective>
Tone-exclusive post look (quick 260926-qlb, user-approved "Exclusive" design): when the post look owns tonality, the palette luma pull is zero. Today tonality is double-applied in Stylized + Post — the stylized base gets the palette luma pull at ToneMatch AND the fitted post grade (scaled by ToneMatch, per o2q) rides on top; worse, the fit solves against the base computed at the CURRENT ToneMatch, so fitted params absorb/compensate the active pull (fit at ToneMatch=1 rails the solver: EV −1.13, contrast 100, WB ±15 → black render; then lowering ToneMatch desyncs both layers → "tone matched but really dark").

One rule fixes all three symptoms: **post owns tone → pull = 0.** The post grade (scaled by ToneMatch via NamerPostLookScale) becomes the ONLY tone mechanism, and the fit always solves the full tonal distance from a tone-neutral base — making fitted params knob-independent.

Purpose: eliminate double-toned/dark renders on fitted profiles and make fits stable across ToneMatch edits.
Output: a `postOwnsTone` trailing parameter on `NamerStyleStylization.Create` (the one documented seam), the predicate wired at the three compose sites (fit base, +Post lens, bake), and seam/lens tests in two existing fixtures.
</objective>

<execution_context>
@~/.claude/get-shit-done/workflows/execute-plan.md
</execution_context>

<context>
@.planning/STATE.md
@./CLAUDE.md

SESSION CONSTRAINTS (from the orchestrator — binding):
- The executor MUST NOT run git commit and MUST NOT touch Unity (no recompile, no test runs). The orchestrator owns recompile, live EditMode verification, and ALL commits. All <verify> steps below are grep/static only.
- NEVER stage or modify user content: Assets/NAMERGenerated/, Assets/NAMER/, Assets/Models/, Assets/Stylized Assets/, NamerSmoke.unity, scene/volume assets.
- No new .cs files (Unity would auto-generate .meta). New test methods go in the existing fixtures listed in files_modified.
- Code style: `/// <summary>` XML-doc on seams, dense `// dated review-tag` comments, Allman braces — match the surrounding 05-03/05.1 annotations.
- Do NOT touch: NamerStylePostFit (the solver), NamerStyleVolumeWriter, NamerPostLookScale, ApplyPostLook, the compute kernels, channel panes, albedo preview plumbing, WB ±15 clamp (separate open issue).

<interfaces>
<!-- Exact current code at the seams. Executor edits THESE locations — no exploration needed. -->

NamerStylizePipeline.cs — Create signature (~line 89) and the ToneMatch field of the composed context (~line 98-104):
```csharp
public static NamerStyleStylization Create(NamerStyleProfile profile, NamerSplitResult split)
{
    if (profile == null)
    {
        throw new ArgumentNullException(nameof(profile));
    }

    profile.ClampToCaps();

    NamerStyleStylization stylization = new NamerStyleStylization
    {
        StyleStrength = Mathf.Clamp01(profile.StyleStrength),
        ToneMatch = Mathf.Clamp01(profile.ToneMatch),
        Smoothing = Mathf.Clamp01(profile.Smoothing),
        HueReplaceMode = profile.HueMode,
    };
    // ... anchors block (profile.IsExtracted && PaletteAnchors.Count > 0), split block,
    // then Active at ~139-141:
    // stylization.Active = (stylization.Smoothing > 0f && stylization.TriIsland != null)
    //     || (stylization.PaletteAvailable && stylization.StyleStrength > 0f)
    //     || (stylization.PaletteAvailable && stylization.ToneMatch > 0f);
```

NamerEditorWindow.cs — ComposePreviewStylization body (~lines 2658-2676):
```csharp
/// 05-03 W4 lens composition ... 05.1 W5: Stylized + Post composes the SAME
/// stylization (the post grade rides the preview material's uniform table...)
internal static NamerStyleStylization ComposePreviewStylization(
    NamerStyleProfile profile, int afterView, NamerSplitResult split)
{
    if (profile == null || (afterView != kAfterViewStylized && afterView != kAfterViewPost))
    {
        return null;
    }

    return NamerStyleStylization.Create(profile, split);
}
```

NamerEditorWindow.cs — RecomputePreview call site (~lines 615-616), inside the block commented "05-03 W2/W4 ... 05.1 review WR-02":
```csharp
NamerStyleStylization stylization = ComposePreviewStylization(
    styleProfile, _forceStylizeForFit ? kAfterViewStylized : _afterView, stylizeSplit);
```
Nearby state (lines 564-568): `styleProfile` resolves `_fitStyleProfileOverride != null ? _fitStyleProfileOverride : NamerProcessor.ResolveStyleProfile(_settings)`; RunStylePostFit sets `_forceStylizeForFit = true; _fitStyleProfileOverride = profile;` (~2564-2565) around one RecomputePreview() call, reset in finally.

NamerProcessor.cs — bake compose (~lines 216-221), comment block "05-03: compose the stylization context ...":
```csharp
NamerStyleStylization stylization = null;
if (styleProfile != null)
{
    stylization = NamerStyleStylization.Create(
        styleProfile, fitSplit != null ? fitSplit : stylizeSplitShared);
}
```
Volume gate (~line 385, unchanged): `if (styleProfile != null && styleProfile.HasPostFit) { NamerStyleVolumeWriter.Write(styleProfile, settings); ... }`

NamerStyleProfile.cs (~229): `public bool HasPostFit { get { return !string.IsNullOrEmpty(PostFitFingerprint); } }`

Test fixture idioms:
- NamerStylizeKernelTests.cs ~771-810 `Context_IdentitySettings_InactiveAndNoMeshFlagged` — extracted-profile idiom: `profile.ReferenceFingerprint = "tone-only"; profile.PaletteAnchors.Add(new NamerStyleAnchor { Hue = 0.5f, Saturation = 0.5f, Luma = 0.5f, Weight = 1f });` (IsExtracted gates the anchors block; ToneMatch is set in the initializer unconditionally).
- NamerPostPreviewTests.cs ~96-102 — fitted-profile idiom: `profile.PostFitFingerprint = "synthetic-fit-fingerprint"; profile.ToneMatch = 1f;` with try/finally DestroyImmediate.

AUDIT RESULT (verified in the live tree — the executor confirms, does not re-derive): NO existing test bakes or previews with a fitted profile + ToneMatch > 0 asserting the palette luma pull. Process_E2E_AttachedProfile_StylingFlowsIntoBakedAssets (NamerStylizationIntegrationTests.cs:1084) uses an UNFITTED extracted profile at default ToneMatch 0 — valid unchanged. NamerStyleVolumeWriterTests / NamerPostPreviewTests ToneMatch cases assert the post layer (writer + ApplyPostLook — untouched). Kernel tests hand-assemble stylization structs (ToneMatch field set directly), so the kernel formula tests stay valid. Nothing encodes the old double-tone spec; this change is additive to the fixtures.
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: postOwnsTone seam + the three compose sites</name>
  <files>Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs, Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs</files>
  <action>
  1. NamerStylizePipeline.cs — add a trailing parameter to Create: `bool postOwnsTone = false`. In the object initializer change the ToneMatch line to `ToneMatch = postOwnsTone ? 0f : Mathf.Clamp01(profile.ToneMatch)` (zeroing lands BEFORE Active is computed at ~139-141, so a tone-only profile under post-owns-tone composes the honest zero-dispatch identity — Active false, byte-identical null path). Extend Create's /// summary with the exclusive-tone rule (quick 260926-qlb): when the post look owns tonality — a fitted profile whose ToneMatch-scaled post grade (NamerPostLookScale) will render on top — the palette luma pull is zeroed so the post grade is the ONLY tone mechanism; StyleStrength (hue/vibrance), Smoothing, HueReplaceMode, and anchor filtering are untouched; fitted params therefore solve against a tone-neutral base and are knob-independent. Follow the trailing-default-parameter precedent of CreateProjectionContext/GenerateResidual (existing 2-arg call sites compile unchanged).
  2. NamerEditorWindow.cs — ComposePreviewStylization (~2667): add trailing parameter `bool forcePostOwnsTone = false`. After the existing null/lens guard, compute `bool postOwnsTone = forcePostOwnsTone || (afterView == kAfterViewPost && profile.HasPostFit);` and pass it as Create's third argument. Amend the /// summary: the post lens still composes the SAME stylization (W5) EXCEPT the palette luma pull is zeroed when the profile carries a fit — the fitted post grade owns tonality; an unfitted profile at the post lens keeps the pull (the W6 degrade falls back to plain Stylized NAMER — no post exists to own tone); forcePostOwnsTone is the RunStylePostFit forced pass (a fit is being CREATED — the solver fits the full tonal distance regardless of lens or existing HasPostFit state). Add a dated inline comment stating the deliberate edge semantics: the base-side predicate is HasPostFit ALONE, NOT NamerPostLookScale.IsActive — so HasPostFit + ToneMatch=0 reads as tone entirely untouched (pull zeroed AND post scaled to nothing); do not "fix" this to IsActive.
  3. NamerEditorWindow.cs — RecomputePreview call site (~615-616): pass `_forceStylizeForFit` as the 4th argument (named argument `forcePostOwnsTone: _forceStylizeForFit` for clarity). One dated comment line above the call in the 05.1 style: the fit pass composes its base tone-neutral (260926-qlb) — join the neighboring WR-02 annotation tone. Do not alter the lens ternary.
  4. NamerProcessor.cs — bake compose (~219-221): pass `styleProfile.HasPostFit` as Create's third argument. Extend the existing "05-03: compose the stylization context" comment: a fitted bake hands tonality to the generated scene Volume's scaled post grade (the Volume gate at ~385 writes under the same HasPostFit predicate), so the baked base must not also carry the palette luma pull — no double tone at runtime; unfitted profiles bake the pull in unchanged (Stylized-only path).
  </action>
  <verify>
  <automated>grep -n "postOwnsTone" Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs — expect: the Create parameter + initializer ternary, the ComposePreviewStylization parameter + predicate + Create forward, the RecomputePreview named argument; then grep -n "postOwnsTone\\|HasPostFit" Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs — expect HasPostFit as the 3rd Create argument; then grep -rn "NamerStyleStylization.Create" Packages/com.graffitientertainment.namer --include=*.cs — confirm every production call site is Create(profile, split) default, the window forward, or the HasPostFit bake (no site left unexamined)</automated>
  </verify>
  <done>postOwnsTone exists exactly once as the documented rule (Create), is forwarded by ComposePreviewStylization's predicate (forcePostOwnsTone || post-lens+HasPostFit), is driven at the window call site by _forceStylizeForFit, and gates the bake by HasPostFit; no other production code touched (NamerStylePostFit, NamerStyleVolumeWriter, NamerPostLookScale, ApplyPostLook, compute kernels untouched per session constraints); existing call sites compile via defaults</done>
</task>

<task type="auto">
  <name>Task 2: seam + lens predicate tests in existing fixtures</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs</files>
  <action>
  1. NamerStylizeKernelTests.cs — new [Test] `Create_PostOwnsTone_ZeroesToneMatchOnly` directly after Context_IdentitySettings_InactiveAndNoMeshFlagged (~line 810), reusing its fixture idiom (ReferenceFingerprint + one valid NamerStyleAnchor, try/finally DestroyImmediate). Profile: ToneMatch=0.8, StyleStrength=0.6, Smoothing=0.2. Assert: Create(profile, null, postOwnsTone: true) yields ToneMatch == 0 with StyleStrength 0.6, Smoothing 0.2, HueReplaceMode preserved, AnchorCount 1 (the zero touches ONLY tone); Create(profile, null) default yields ToneMatch == 0.8 (today's behavior unchanged); then a tone-only variant (strength 0, smoothing 0, toneMatch 0.5, anchors present) with postOwnsTone: true yields Active == false — the post-owned tone-only context is the honest zero-dispatch identity (mirror the 783-798 assertion message style, cite 260926-qlb in the doc comment).
  2. NamerPostPreviewTests.cs — new [Test] `ComposePreviewStylization_ToneExclusivePostLook_Rule` directly after ComposePreviewStylization_PostView_ComposesStylization (~line 41), fitted-profile idiom from line 100 (PostFitFingerprint = "synthetic-fit-fingerprint") plus the extracted idiom (ReferenceFingerprint + one anchor) and ToneMatch=0.8. Four legs, each with a message naming the rule leg: (a) fitted profile + kAfterViewPost → composed.ToneMatch == 0 (post owns tone); (b) fresh unfitted profile (empty PostFitFingerprint) + kAfterViewPost → ToneMatch == 0.8 (W6 degrade — pull stays on, no post owns tone); (c) fitted profile + kAfterViewStylized → ToneMatch == 0.8 (stylized-only lens unchanged); (d) fitted profile + kAfterViewStylized + forcePostOwnsTone: true → ToneMatch == 0 (the RunStylePostFit forced pass fits from a tone-neutral base regardless of lens/fit state). Doc comment: the exclusive-tone rule (260926-qlb) and the accepted edge — HasPostFit + ToneMatch=0 = tone entirely untouched, which is why the predicate does not consult NamerPostLookScale.IsActive.
  3. Audit pass (confirm, do not re-derive — see AUDIT RESULT in context): run the ToneMatch grep over Tests/Editor and confirm no fixture encodes the old double-tone spec (fitted profile + ToneMatch > 0 asserting the luma pull in baked/previewed output). If one is found that the audit missed, update it to the new spec — that is a user-decided spec change, not a workaround — and record it in the SUMMARY.
  </action>
  <verify>
  <automated>grep -n "Create_PostOwnsTone_ZeroesToneMatchOnly" Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs and grep -n "ComposePreviewStylization_ToneExclusivePostLook_Rule" Packages/com.graffitientertainment.namer/Tests/Editor/NamerPostPreviewTests.cs — both present; grep -A40 on each test name shows ≥ 3 and ≥ 4 Assert calls respectively; grep -rn "ToneMatch" Packages/com.graffitientertainment.namer/Tests/Editor --include=*.cs | grep -i "postfitfingerprint" returns only the known post-layer fixtures (audit clean, no old-spec double-tone fixture)</automated>
  </verify>
  <done>Both new tests exist in the existing fixtures (no new files, no .meta), assert the seam truth (tone-only zeroing, Active identity, default unchanged) and the four predicate legs (fitted+post=0, unfitted+post=pull, stylized=pull, forced-fit=0); audit confirms no old-spec fixtures remain; executor ran NO Unity recompile/test and NO git commands (orchestrator owns the live suite and commits)</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| profile asset → compute pipeline | user-authored NamerStyleProfile values flow into the stylize stage |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-qlb-01 | Tampering | NamerStyleProfile.ToneMatch into composed stylization | accept | No new input path: ToneMatch already sanitized by ClampToCaps (NaN → DefaultToneMatch, T-hk7-01) and Mathf.Clamp01 in Create; postOwnsTone only selects 0 — it never widens the value range |
| T-qlb-02 | Tampering | HasPostFit predicate (PostFitFingerprint string) | accept | Existing getter, unchanged; empty/null already reads false (pre-05.1 profiles take the unchanged pull-in path) |
</threat_model>

<verification>
Static only (orchestrator runs recompile + live EditMode suite afterward):
- grep confirms postOwnsTone at the seam, both window sites, and the bake site (Task 1 verify)
- grep confirms the two new tests and the audit-clean ToneMatch sweep (Task 2 verify)
- No files outside files_modified touched; user content assets untouched; no commits made by the executor
</verification>

<success_criteria>
- The exclusive-tone rule lives in exactly one documented place (Create's postOwnsTone) and is applied at all three compose sites: fit base (always), +Post lens (iff HasPostFit), bake (iff HasPostFit)
- Stylized-only lens and unfitted profiles compose byte-identically to today (default parameter, predicate legs b/c)
- The fit solves from a tone-neutral base, making fitted params independent of the ToneMatch slider (leg d) — the "tone matched but really dark" double-application defect is structurally removed
- Accepted behavior (no code change): under the +Post lens with a fit, the albedo preview pane shows the tone-neutral styled base that is actually used — correct under the new rule
</success_criteria>

<output>
Create .planning/quick/260926-qlb-tone-exclusive-post-look-zero-palette-lu/260926-qlb-SUMMARY.md when done (the orchestrator owns recompile, live EditMode verification, and ALL commits)
</output>

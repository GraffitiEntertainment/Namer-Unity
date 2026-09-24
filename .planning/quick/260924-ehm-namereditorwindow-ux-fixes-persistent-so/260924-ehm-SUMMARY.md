---
phase: quick-260924-ehm
plan: 01
status: complete
subsystem: editor-ui
tags: [namer-editor-window, source-persistence, style-profile, albedo-preview, editmode-tests]
requires:
  - "05-03 W1-W4 stylization section (profile attach, attach-state, After View lens)"
provides:
  - "Persistent editable Source pick in NamerEditorWindow (survives editor selection changes and domain reload for asset picks)"
  - "One-step Create Profile... create-and-attach inside the Stylization section"
  - "Lens-honest 2D albedo preview showing the styled SOURCE base pre-projection (shared NamerChannelView material, no new compute)"
  - "Embedded profile inspector inside the Stylization section (full P1-P14 editing UI, zero drawing-code duplication)"
affects:
  - "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
  - "Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs"
tech-stack:
  added: []
  patterns:
    - "Repaint-guarded Graphics.DrawTexture through the shared NamerChannelView material (260919-fp3) reused for the albedo preview"
    - "GUID attach mechanism (AssetPathToGUID) shared by the ObjectField handler and the Create Profile... button"
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
decisions:
  - "ObjectField change-check runs BEFORE the Create button body so a same-frame create is not clobbered by the stale newProfile != attached comparison"
  - "Button body + albedo preview extracted into private helpers (CreateAndAttachStyleProfile / DrawAlbedoPreview) — plan-sanctioned, no new public surface"
  - "Live EditMode run deferred to the orchestrator (no relay, batchmode banned); Roslyn compile-equivalence green for both assemblies against the fresh Editor dll"
  - "Task 5 (checkpoint feedback): embedded the NamerStyleProfileEditor via UnityEditor.Editor.CreateEditor instead of duplicating drawing code; the type is always fully qualified because the simple name 'Editor' resolves to the namespace segment inside GraffitiEntertainment.Namer.Editor"
  - "Task 6 (checkpoint feedback 2): the stylizer albedo preview binds SourceBaseColor ?? NormalizedBaseColor (styled pre-projection source base) via the ResolveStylizePreviewBase seam — never the post-processed _BaseResidualMap texture; Gamma projects re-encode through the extracted ReencodeForGammaPreview helper (shared with ResolvePreviewBaseMap), built only in RecomputePreview so no Blit ever runs in OnGUI"
  - "Task 7 (checkpoint feedback 3): the albedo preview's neutral state never early-returns — the lens caption + View Stylized button render always (button disabled, not hidden, when no profile); ResolveAlbedoPreviewEmptyReason differentiates the missing prerequisite (no Source vs no result yet)"
  - "Task 8 (checkpoint feedback 4): a 'Stylized preview' checkbox in the Stylization section toggles the preview lens without detaching the profile — it drives the same _afterView/_settings.AfterView state as the After View popup and the View Stylized button (all three in sync). REVERTED same round: user ruled the checkbox redundant with the After View popup (NAMER / Stylized NAMER dropdown)"
metrics:
  duration: ~65min total (Tasks 1-3 + Tasks 5-8 + Task 8 revert, spanning coordinator round-trips)
  completed: 2026-09-24 (Task 8 revert)
  tasks: 7/8 shipped (Task 4 is the blocking human-verify checkpoint; Task 8 implemented then reverted by user decision)
  commits: [ce99e81, 36d0831, 41588ff, b37140f, a162306, 6682d42, 3dc3e53, e54d938]
---

# Quick Task 260924-ehm: NamerEditorWindow UX fixes (persistent Source, Create Profile, albedo preview, embedded profile inspector) Summary

Five user-locked UX changes in `NamerEditorWindow.cs` plus behavior/caption pins in `NamerEditorWindowSmokeTests.cs`: a persistent editable Source ObjectField that no longer follows the editor selection, a one-step Create Profile… create-and-attach in the Stylization section, a lens-honest full-width 2D albedo preview drawn through the existing channel-view infrastructure (no new GPU compute), and — added from Task 4 checkpoint feedback ("close but the stylizer texture selection and sliders should be in the namer dialog") — the attached profile's full editing UI embedded inside the Stylization section, corrected by the second feedback round ("The stylizer is showing the post-processed albedo texture, which it really shouldn't") to display the styled SOURCE base (pre-projection) instead of the post-processed texture, and by the third ("the albedo didn't change and the View Stylized texture button disappeared") to keep the lens caption + View Stylized button alive in the neutral state with a differentiated missing-prerequisite reason line.

## Tasks Completed

| Task | Description | Commit | Key changes |
|------|-------------|--------|-------------|
| 1 | Persistent Source ObjectField (fix 1) | `ce99e81` | `[SerializeField] _selection`; `selectionChanged` subscription + `OnSelectionChanged` deleted; OnEnable seeds only when null; DrawSourceSection renders a busy-disabled editable ObjectField (allowSceneObjects) whose change calls `RebuildInspection()` — no early return, material-list/warnings stay reachable with null selection |
| 2 | Create Profile… button + 2D albedo preview (fixes 2+3) | `36d0831` | SaveFilePanelInProject → CreateAsset → SaveAssets → GUID attach → ping → MarkTweaking/MarkDirty; `DrawAlbedoPreview` (128px, Base channel, live `_namerMaterial` textures, Repaint-guarded DrawTexture) with locked lens-honest captions + View Stylized flip |
| 3 | EditMode test pins | `41588ff` | `SourceSelection_PersistsAcrossEditorSelectionChange` (behavioral: synthetic `Selection.activeObject` change leaves `_selection` reference-equal to the injected token) + `AlbedoPreviewCaptions_ExactLockedCopy` (four consts verbatim, em-dashes included) |
| 5 | Embedded profile inspector (checkpoint feedback follow-up) | `b37140f` | `private UnityEditor.Editor _profileEditor` (always fully qualified — the simple name `Editor` resolves to the namespace segment inside `GraffitiEntertainment.Namer.Editor`); `internal UnityEditor.Editor EnsureProfileEditor(NamerStyleProfile)` (null when detached; recreate on target change); `EnsureProfileEditor(attached)?.OnInspectorGUI()` after the attach-state line, before the albedo preview; destroyed on detach and in OnDisable; `ProfileEditor_EmbedsForAttachedProfile` test (create-for-target, cached reuse, null-when-null) |
| 6 | Stylizer preview shows the styled SOURCE base (checkpoint feedback 2) | `a162306` | `internal static Texture ResolveStylizePreviewBase(NamerComputeResult)` seam (SourceBaseColor ?? NormalizedBaseColor; null result → null); albedo preview binds that base at the `_BaseResidualMap` slot (post-processed residual/projected base no longer shown); Gamma re-encode extracted into `ReencodeForGammaPreview(source, ref cache)` shared with `ResolvePreviewBaseMap`, `_stylizePreviewBaseRt` built in RecomputePreview only and released in `ReleaseLiveResult`; `StylizePreviewBase_PrefersSourceOverProjected` test (source-wins / fallback / null-result) |
| 7 | Neutral albedo preview keeps the View Stylized button (checkpoint feedback 3) | `6682d42` | DrawAlbedoPreview restructure: `canDrawImage` false draws the box and FALLS THROUGH — caption row renders always; View Stylized wrapped in `EditorGUI.DisabledScope(!hasProfile)` (mirrors the After View popup); `ResolveAlbedoPreviewEmptyReason(hasSource, hasLiveResult)` seam renders "select a Source above to preview" vs "no preview yet — waiting for recompute" (`AlbedoPreviewEmptyCaption` retired); captions repinned + `AlbedoPreviewEmptyReason_Differentiates` test |
| 8 | Stylized preview checkbox (checkpoint feedback 4) — **reverted by `e54d938` — redundant with After View popup per user feedback** | `3dc3e53` | "Stylized preview" `EditorGUILayout.Toggle` after the attach-state line (before the embedded inspector) toggling `_afterView`/`_settings.AfterView` between `kAfterViewStylized`/`kAfterViewNamer` + MarkTweaking + MarkDirty — same persisted state as the After View popup and View Stylized button; locked label/tooltip constants (`StylizedPreviewToggleLabel`/`StylizedPreviewToggleTooltip`) pinned, growing the caption test to seven constants. USER REJECTED: "the checkbox is unnecessary, as you added the drop down for selection of NAMER and NAMER STYLIZED" — the After View popup already covers the toggle; `e54d938` restores both files byte-identically to the `6682d42` state (constants, checkbox, and test pins all removed; the View Stylized button remains the preview-side flipper) |

Task 4 (blocking human-verify checkpoint) is intentionally NOT performed — awaiting live-editor verification, which should now ALSO cover the embedded profile inspector (reference slots with thumbnails, +/– footer, Extract Style Stats, the five sliders, Statistics, Advanced foldout — all rendering inside the NAMER Processor window's Stylization section when a profile is attached) and the corrected albedo behavior: the preview shows the styled SOURCE albedo (not the residual/projected texture), the View Stylized button survives the neutral state (disabled without a profile, never hidden), and a wiped Source (domain reload) shows "select a Source above to preview" instead of a dead preview.

## Verification Results

- **Task 1 grep gates:** `selectionChanged`=0, `OnSelectionChanged`=0, `SerializeField`=1 (>=1), `typeof(UnityEngine.Object)`=1 — PASS
- **Task 2 grep gates:** `SaveFilePanelInProject`=1, `CreateAsset`=1 (>=1), `PingObject`=1, `Stylized albedo`=1, `View Stylized`=1, `SetFloat(ChannelId, 0)`=1 — PASS
- **Task 3 grep gates:** `SourceSelection_PersistsAcrossEditorSelectionChange`=1, `AlbedoPreviewCaptions_ExactLockedCopy`=1, `Assert.AreSame`=1 (>=1) — PASS
- **Task 5 grep gates:** `UnityEditor.Editor.CreateEditor`=1 in the window file (raw and code-only — the doc comment deliberately avoids the dotted form); `EnsureProfileEditor` = 2 in the window file (decl + call) + 6 in the test file = 8 combined (>= 3); `_profileEditor` destroyed in OnDisable (window OnDisable lines 297-301, verified alongside `_channelViewMaterial` cleanup) — PASS
- **Task 6 grep gates:** `ResolveStylizePreviewBase` = 4 raw / 3 code-only in the window file (>= 3: decl + RecomputePreview call + DrawAlbedoPreview call); `SourceBaseColor` = 2 raw lines in the window file (the seam return uses it twice — the selection expression itself); `ReencodeForGammaPreview` = 3 (decl + ResolvePreviewBaseMap call + RecomputePreview call); `_stylizePreviewBaseRt` released in `ReleaseLiveResult` exactly like `_previewBaseRt` (4 lines: guard/Release/DestroyImmediate/null) — PASS
- **Task 7 grep gates:** `AlbedoPreviewEmptyCaption` = 0 in BOTH files; `AlbedoPreviewNoSourceCaption` = 2 and `AlbedoPreviewNoResultCaption` = 2 in the window file (>= 2 each: const decl + seam return); `DisabledScope` present inside DrawAlbedoPreview; zero `return` statements inside DrawAlbedoPreview (no path skips the caption row); `ResolveStylizePreviewBase` seam untouched — PASS
- **Compile equivalence (04.2/04.3 precedent):** both `GraffitiEntertainment.Namer.Editor` and `GraffitiEntertainment.Namer.Tests.Editor` compile clean (exit 0, zero diagnostics) with Unity 6000.0.82f1's bundled Roslyn (`DotNetSdkRoslyn/csc.dll` via `NetCoreRuntime/dotnet`) against the csproj reference closure — the Tests assembly built against the fresh Editor dll with the real `-out:` assembly names, so the `InternalsVisibleTo` reflection seam is proven to resolve (04.3 P03 lesson). Re-run green after Tasks 5, 6, AND 7.
- **Live EditMode run: DEFERRED to the orchestrator.** No unity-mcp relay is reachable from this executor session (no CLI on PATH, no MCP tools in the subagent context) and -batchmode Unity is banned (interactive editor holds the project lock). Per the plan's own contingency and the 04.2-P04/04.3-02 precedent, the targeted `NamerEditorWindowSmokeTests` run should ride the orchestrator's live relay / post-task regression gate.
- **No OnGUI Blit introduced:** the only `Graphics.Blit` in the file remains the pre-existing `ResolvePreviewBaseMap` call (recompute path, not OnGUI).
- **Scope check:** only the two `files_modified` paths were touched; `NamerStyleProfile.cs`, `NamerStyleProfileEditor.cs`, and the NamerChannelView shader are untouched; all pre-existing dirty working-tree paths are untouched.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Ordering] ObjectField change-check ordered before the Create button body**
- **Found during:** Task 2
- **Issue:** With the button drawn between the ObjectField read and its change-check, a same-frame create sets `attached = profile` and the subsequent stale `newProfile != attached` comparison would overwrite the just-written `StyleProfileGuid` with `string.Empty`, silently detaching the profile the button just attached.
- **Fix:** The change-check now runs immediately after the ObjectField (before the button) inside the same horizontal row; only one of the two controls can change in a given event, and the button is the last writer when clicked.
- **Files modified:** NamerEditorWindow.cs
- **Commit:** `36d0831`

**2. [Plan-sanctioned extraction] Helpers + one extra constant**
- `CreateAndAttachStyleProfile(ref NamerStyleProfile attached)` and `DrawAlbedoPreview()` extracted as private methods with Doxygen headers (the plan explicitly sanctioned the first "only if inline would otherwise bloat"; the preview block followed the same readability rationale — no new public surface either way).
- Added `private const float kCreateProfileButtonWidth = 110f;` beside `kAlbedoPreviewHeight` (plan fixed the ~110px width; the named constant satisfies the project's no-magic-numbers rule).
- **Commits:** `36d0831`

### Deferred Issues

None. The live EditMode run deferral is a plan-contingency execution path, not an unresolved defect (compile equivalence is green, re-verified after Task 5).

### Scope Additions

**Task 5 (embedded profile inspector)** was added mid-checkpoint from the user's Task 4 feedback ("close but the stylizer texture selection and sliders should be in the namer dialog") — not part of the original plan's task list. Implemented exactly as directed: no drawing-code duplication (the embedded `UnityEditor.Editor` IS the `NamerStyleProfileEditor` created by `Editor.CreateEditor`), no refactor of `NamerStyleProfileEditor`, value edits ride the existing `NamerStyleProfile.Changed` → `OnProfileChanged` → debounced recompute wiring.

**Task 6 (stylizer preview source)** was added from the second checkpoint feedback ("The stylizer is showing the post-processed albedo texture, which it really shouldn't"). Diagnosis verified in `NamerComputePipeline.cs` (~266-279): the albedo preview previously sampled `_namerMaterial`'s `_BaseResidualMap`, which holds the residual RT or the projected base — the pipeline already preserved the correct texture as `NamerComputeResult.SourceBaseColor` (the styled pre-projection source base; null when no projection ran, in which case `NormalizedBaseColor` IS the styled base). Fixed via the `ResolveStylizePreviewBase` seam plus the shared gamma re-encode; all encoding stays inside `RecomputePreview` (no OnGUI Blit ever).

**Task 7 (neutral-state lens flip)** was added from the third checkpoint feedback ("the albedo didn't change and the View Stylized texture button disappeared"). Root cause verified against the live window: a mid-session domain reload (script recompile for the deferred test run) wiped the scene-object `_selection` (`instanceID: 0`), so no recompute produced `_liveResult` and the neutral branch's early return swallowed the caption row AND the button — a dead preview with no path back. Two defects fixed: the neutral branch now falls through (caption + button always render; button disabled rather than hidden without a profile), and the reason line differentiates the missing prerequisite via the `ResolveAlbedoPreviewEmptyReason` seam.

**Known limitation (accepted, Task 1):** a scene-object Source pick does not survive domain reload (Unity EditorWindow serialization cannot hold scene references — asset picks do survive). The preview now says "select a Source above to preview" instead of silently hiding the lens flip; re-picking the Source restores the live pipeline with no other state lost.

## Known Stubs

None — every new code path is fully wired to real data (live `_namerMaterial` surface map, pipeline-owned stylize source base, `AssetDatabase` create/attach, the real profile inspector).

## Auth Gates

None.

## Threat Flags

None — no security-relevant surface beyond the plan's accepted T-q-01 (SaveFilePanelInProject-sourced path) and T-q-02 (serialized UI convenience state) dispositions.

## Self-Check: PASSED

- Files exist: `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` FOUND; `Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs` FOUND; this SUMMARY FOUND.
- Commits exist on `gsd/phase-05-stylization`: `ce99e81` FOUND, `36d0831` FOUND, `41588ff` FOUND, `b37140f` FOUND, `a162306` FOUND, `6682d42` FOUND, `3dc3e53` FOUND, `e54d938` FOUND (verified via `git log`; base `7a9cfb9` is the direct parent of `ce99e81`).
- Revert verified: `git show --stat e54d938` touches exactly the window + smoke-test files; `git diff 6682d42 HEAD -- <both paths>` is empty (byte-identical); zero `StylizedPreviewToggle` occurrences remain.
- No tracked-file deletions in any task commit (`git diff-tree --diff-filter=D` empty for all eight).

---
phase: quick-260924-ehm
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
autonomous: false
requirements: [QUICK]

must_haves:
  truths:
    - "Clicking unrelated scene items or empty space no longer changes or clears the Source shown in the NAMER Processor window"
    - "The Source row is an editable ObjectField; picking a new object updates the inspection/material list immediately"
    - "A Style Profile can be created from inside the Stylization section via Create Profile… and is attached to the window in one step"
    - "With a profile attached, the Stylization section shows a full-width 2D albedo preview whose caption honestly states which lens produced it (stylized vs un-stylized), with a View Stylized button when the lens is NAMER"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
      provides: "Persistent source pick, in-window profile creation, 2D albedo preview"
      contains: "SaveFilePanelInProject"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs"
      provides: "Reflection/behavior tests pinning the selection persistence + caption copy"
      contains: "AlbedoPreviewStylizedCaption"
  key_links:
    - from: "Source ObjectField change handler"
      to: "RebuildInspection()"
      via: "direct call on change"
      pattern: "RebuildInspection\\(\\)"
    - from: "Create Profile… button"
      to: "_settings.StyleProfileGuid"
      via: "AssetPathToGUID of the created asset (same mechanism as the profile ObjectField handler)"
      pattern: "AssetPathToGUID"
    - from: "Albedo preview draw"
      to: "NamerChannelView material (_channelViewMaterial)"
      via: "EnsureChannelViewMaterial + SetTexture(_SurfaceMap/_BaseResidualMap off _namerMaterial) + SetFloat(_Channel, 0)"
      pattern: "SetFloat\\(ChannelId, 0"
---

<objective>
Three NamerEditorWindow UX fixes, all user-signed-off and locked:

1. **Persistent Source field** — stop following the global editor selection; make Source an editable ObjectField that keeps its pick.
2. **Create Profile… button** — create + attach a NamerStyleProfile asset from inside the Stylization section in one step.
3. **2D albedo preview** — an inline Base-channel view in the Stylization section while styling, reusing the existing channel-view infrastructure (no new GPU compute).

Purpose: The window currently loses its subject whenever the user clicks anything else, offers no in-place profile creation, and gives no 2D texture feedback while tuning stylization sliders.

Output: Surgical edits to `NamerEditorWindow.cs` only, plus tests extending `NamerEditorWindowSmokeTests.cs`. No new production files, no refactors, no new classes.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@./CLAUDE.md
@.planning/STATE.md

Target file: `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` (the ONLY production file to change).

<interfaces>
<!-- Exact current code at each edit site (verified 2026-09-24). Anchor on the named
     members, not line numbers. All excerpts from NamerEditorWindow.cs unless noted. -->

Field declaration (~line 111):
```csharp
private UnityEngine.Object _selection;
```

OnEnable subscription + seeding (~lines 215-221):
```csharp
EditorApplication.update += Tick;
Selection.selectionChanged += OnSelectionChanged;
NamerStyleProfile.Changed += OnProfileChanged;

_selection = ResolveSelection();
RebuildInspection();
```

OnDisable unsubscription (~lines 225-227):
```csharp
EditorApplication.update -= Tick;
Selection.selectionChanged -= OnSelectionChanged;
NamerStyleProfile.Changed -= OnProfileChanged;
```

Handler to DELETE (~lines 281-285):
```csharp
private void OnSelectionChanged()
{
    _selection = ResolveSelection();
    RebuildInspection();
}
```

DrawSourceSection block to REPLACE (the null-branch + read-only name label; keep the
foldout prologue and everything from the "No materials found" block DOWN unchanged):
```csharp
if (_selection == null)
{
    EditorGUILayout.LabelField("No source selected", EditorStyles.largeLabel);
    EditorGUILayout.LabelField(
        "Select a GameObject, prefab, model, material, or folder in the Project or Hierarchy window, then press Process with NAMER.",
        EditorStyles.label);
    EditorGUILayout.Space();
    return;
}

EditorGUILayout.LabelField(_selection.name + " (" + _selection.GetType().Name + ")", EditorStyles.label);
```
NOTE: the null-branch currently `return`s before the material list. The replacement
ObjectField must NOT return — the material-list/warnings UI below stays reachable with
a null selection (it already guards on `_model != null`).

DrawStylizationSection profile ObjectField + GUID change handler (~lines 1717-1737) —
the mechanism the Create button reuses:
```csharp
NamerStyleProfile attached = NamerProcessor.ResolveStyleProfile(_settings);
NamerStyleProfile newProfile = (NamerStyleProfile)EditorGUILayout.ObjectField(...);
if (newProfile != attached)
{
    _settings.StyleProfileGuid = newProfile != null
        ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(newProfile))
        : string.Empty;
    attached = newProfile;
    _afterPanelState.MarkTweaking();
    MarkDirty();
}
```

Channel-view draw pattern to mirror EXACTLY for the albedo preview (from
DrawChannelPanes ~lines 1256-1264; note the 260919-fp3 lesson — Repaint-guarded
Graphics.DrawTexture, NEVER Graphics.Blit in OnGUI):
```csharp
EnsureChannelViewMaterial();
_channelViewMaterial.SetTexture(SurfaceMapId, afterMaterial.GetTexture(SurfaceMapId));
_channelViewMaterial.SetTexture(BaseResidualMapId, afterMaterial.GetTexture(BaseResidualMapId));
_channelViewMaterial.SetFloat(OcclusionStrengthId, afterMaterial.GetFloat(OcclusionStrengthId));
_channelViewMaterial.SetFloat(ChannelId, i);
if (Event.current.type == EventType.Repaint)
{
    Graphics.DrawTexture(paneRect, Texture2D.whiteTexture, _channelViewMaterial);
}
```
For the albedo preview: channel index is 0 (Base — tooltip precedent at ~1267 says
channel 0 sources `_BaseResidualMap`), and the textures are read off the LIVE
`_namerMaterial` (not `_generatedMaterial`) — the stylized result only exists there.

Existing constants/UI conventions already in the file: `ChannelPaneSize`,
`ChannelPopupSize`, `kCoverageSliderStep` (k-prefix for private consts),
`ResidualNotWrittenLabel` (internal const string read via reflection by tests),
`kAfterViewNamer` / `kAfterViewStylized` (internal const int), width computation
precedent at ~1099: `Mathf.Max(EditorGUIUtility.currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth, 256f)`.

From Editor/Stylize/NamerStyleProfile.cs (~lines 244-250) — the existing create menu
the button mirrors (do NOT change this file):
```csharp
[MenuItem("Assets/Create/NAMER/Style Profile")]
private static void CreateStyleProfileAsset()
{
    ProjectWindowUtil.CreateAsset(
        ScriptableObject.CreateInstance<NamerStyleProfile>(),
        "New Namer Style Profile.asset");
}
```

Test patterns (Tests/Editor/NamerEditorWindowSmokeTests.cs): reflection on
`typeof(NamerEditorWindow)` with `BindingFlags.NonPublic | BindingFlags.Static` for
label fields (see ShaderInputToggleLabels / ChannelPaneLabels tests), EditorPrefs
snapshot/restore isolation, NUnit `[Test]`. The file already imports System.Reflection,
NUnit.Framework, UnityEditor, UnityEngine — the new tests need no new usings.
</interfaces>
</context>

<tasks>

<task type="auto" tdd="false">
  <name>Task 1: Persistent Source ObjectField (fix 1)</name>
  <files>Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs</files>
  <action>
Four surgical edits, nothing else in the file changes:

1. Add `[SerializeField]` on its own line directly above `private UnityEngine.Object _selection;` (~line 111), with a one-line comment noting asset selections survive domain reload while scene refs cannot (accepted Unity limitation — EditorWindow serialization cannot hold scene references).

2. Delete `Selection.selectionChanged += OnSelectionChanged;` from OnEnable and `Selection.selectionChanged -= OnSelectionChanged;` from OnDisable. Delete the entire `OnSelectionChanged` method. Leave the `EditorApplication.update` and `NamerStyleProfile.Changed` subscriptions untouched.

3. In OnEnable, replace `_selection = ResolveSelection();` with a seed-only-when-null guard: `if (_selection == null) { _selection = ResolveSelection(); }`. Keep the following `RebuildInspection();` call unconditional. This preserves open-with-selection convenience (fresh window seeds from the current pick) without clobbering a persisted pick after domain reload. Note: Unity's overloaded null-comparison makes a destroyed/stale serialized reference read as null, so the guard re-seeds in that case too.

4. In DrawSourceSection, replace BOTH the `if (_selection == null)` block (the "No source selected" labels + early return) AND the read-only `EditorGUILayout.LabelField(_selection.name + ...)` line with a single standard `EditorGUILayout.ObjectField(new GUIContent("Source", tooltip), _selection, typeof(UnityEngine.Object), true)` wrapped in `EditorGUI.BeginDisabledGroup(_busy)` / `EndDisabledGroup`. Tooltip copy: states the pick is explicit and does NOT follow the editor selection. On change (`newSelection != _selection` under Unity's overloaded comparison): assign `_selection = newSelection;` then call `RebuildInspection();`. Do NOT early-return on null — the material-list and warnings UI below must stay reachable (they already null-guard on `_model`). Everything from the "No materials found" block down is unchanged.
  </action>
  <verify>
    <automated>cd /Users/Shared/SSDevelopment/Development/Namer-Unity && grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'selectionChanged' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'OnSelectionChanged' ; grep -c 'SerializeField' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'typeof(UnityEngine.Object)'</automated>
    Expected: 0, 0, >=1, 1 (semicolons separate the four counts in order).
  </verify>
  <done>No selectionChanged subscription or handler remains; _selection is [SerializeField]; OnEnable seeds only when null; DrawSourceSection renders an editable ObjectField (allowSceneObjects: true, disabled while _busy) whose change handler calls RebuildInspection; material-list/warnings UI below unchanged and reachable with null selection.</done>
</task>

<task type="auto" tdd="false">
  <name>Task 2: Create Profile… button + 2D albedo preview (fixes 2 + 3)</name>
  <files>Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs</files>
  <action>
All edits inside/near `DrawStylizationSection` (and its constants). Follow file conventions: k-prefixed private consts, internal static caption strings for the test seam, Doxygen headers on new members, braces on all if statements.

A. New label constants beside `ResidualNotWrittenLabel` (~line 62), exact locked copy (em-dashes verbatim):
- `internal const string AlbedoPreviewStylizedCaption = "Stylized albedo — palette transfer applied";`
- `internal const string AlbedoPreviewUnstylizedCaption = "Un-stylized albedo — After View (Preview/Debug) is NAMER";`
- `internal const string AlbedoPreviewEmptyCaption = "select a source / attach a profile";`
- `internal const string AlbedoPreviewViewStylizedButton = "View Stylized";`
Also `private const float kAlbedoPreviewHeight = 128f;` near `ChannelPaneSize`.

B. Create Profile… button: put the existing Style Profile ObjectField and a `GUILayout.Button("Create Profile…", EditorStyles.miniButton)` on one horizontal row (BeginHorizontal/EndHorizontal; ObjectField keeps flexible width, button fixed ~110px via GUILayout.Width). Button body: call `EditorUtility.SaveFilePanelInProject("Create NAMER Style Profile", "New Namer Style Profile", "asset", message)`; empty/null return = no-op (cancelled dialog). Otherwise: `ScriptableObject.CreateInstance<NamerStyleProfile>()`, `AssetDatabase.CreateAsset(profile, path)`, `AssetDatabase.SaveAssets()`, then attach in one step exactly like the ObjectField handler — `_settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(path); attached = profile;` — then `EditorGUIUtility.PingObject(profile); _afterPanelState.MarkTweaking(); MarkDirty();` so the preview reacts. Update the local `attached` variable so the attach-state line and albedo preview render this frame. The button sits inside the existing `_busy` disabled group (satisfies "disabled while _busy" with no extra code). Consider extracting the button body into a private helper `CreateAndAttachStyleProfile(ref NamerStyleProfile attached)` only if inline would otherwise bloat DrawStylizationSection; either way no new public surface.

C. 2D albedo preview: at the end of DrawStylizationSection (after the smoothing-skipped HelpBox, before `EditorGUI.EndDisabledGroup()`), when `attached != null` draw a block headed by a bold "Albedo Preview" label:
- Neutral case — `_namerMaterial == null` or `_namerMaterial.GetTexture(SurfaceMapId) == null`: `GUI.Box` on a `GUILayoutUtility.GetRect` of row width × `kAlbedoPreviewHeight`, then `EditorGUILayout.LabelField(AlbedoPreviewEmptyCaption, EditorStyles.miniLabel)`. Row width mirrors the ~1099 precedent: `Mathf.Max(EditorGUIUtility.currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth, 256f)`.
- Live case — reuse the channel-view pattern VERBATIM from DrawChannelPanes (see interfaces block): `EnsureChannelViewMaterial()`, then set `_SurfaceMap`/`_BaseResidualMap` textures and `_OcclusionStrength` float read off the LIVE `_namerMaterial`, `SetFloat(ChannelId, 0)` (0 = Base), and on `EventType.Repaint` only, `Graphics.DrawTexture(rect, Texture2D.whiteTexture, _channelViewMaterial)`. NEVER Graphics.Blit in OnGUI (260919-fp3 black-window lesson). Sharing `_channelViewMaterial` with DrawChannelPanes is safe — both set all four properties before every draw.
- Lens-honest caption row under the texture: when `_afterView == kAfterViewStylized` show `AlbedoPreviewStylizedCaption` (miniLabel); otherwise show `AlbedoPreviewUnstylizedCaption` plus an inline `GUILayout.Button(AlbedoPreviewViewStylizedButton, EditorStyles.miniButton)` whose handler sets `_afterView = kAfterViewStylized; _settings.AfterView = kAfterViewStylized; _afterPanelState.MarkTweaking(); MarkDirty();`. Show the button only when `attached != null && _afterView != kAfterViewStylized` (it already sits inside the attached-only block).
- NO new GPU compute, no readback, no disk I/O — this is a read-only view of the last preview recompute.
  </action>
  <verify>
    <automated>cd /Users/Shared/SSDevelopment/Development/Namer-Unity && grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'SaveFilePanelInProject' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'CreateAsset' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'PingObject' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'Stylized albedo' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'View Stylized' ; grep -v '^\s*//' Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -c 'SetFloat(ChannelId, 0)'</automated>
    Expected: 1, >=1 (CreateAsset appears once in the button), 1, 1, 1, 1.
  </verify>
  <done>Create Profile… opens SaveFilePanelInProject, writes a NamerStyleProfile asset, attaches via StyleProfileGuid, pings it, and dirties the preview; cancel is a no-op; button disabled while _busy. Albedo preview renders when a profile is attached: neutral box + empty caption without live textures, else a full-width 128px Base-channel view drawn through the shared NamerChannelView material off _namerMaterial textures with the lens-honest caption and View Stylized button when the lens is NAMER. No Graphics.Blit in OnGUI; no new compute.</done>
</task>

<task type="auto" tdd="false">
  <name>Task 3: Pin the behavior + captions in EditMode tests</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs</files>
  <action>
Extend the existing test class (no new file, so no .meta concerns). Two new `[Test]` methods following the file's reflection + snapshot/restore conventions; deterministic, no sleeps, no waits:

1. `SourceSelection_PersistsAcrossEditorSelectionChange` — the behavioral pin for fix 1 (this is the meaningful assertion; "method is gone" reflection checks are trivially satisfiable and NOT sufficient): create `NamerEditorWindow` via `ScriptableObject.CreateInstance<NamerEditorWindow>()` (runs OnEnable; safe — the null-selection path is the production default and does no GPU work; Tick never fires inside a synchronous test body). Snapshot `Selection.activeObject`. Create two throwaway token objects (e.g. `ScriptableObject.CreateInstance<ScriptableObject>()` with distinct names), assign tokenA into the window's `_selection` via reflection (`GetField("_selection", BindingFlags.NonPublic | BindingFlags.Instance)`), set `Selection.activeObject = tokenB`, then assert the window's `_selection` is still reference-equal to tokenA (`Assert.AreSame`). finally: restore `Selection.activeObject`, `DestroyImmediate` the window (runs OnDisable → unsubscribe/dispose) and both tokens.

2. `AlbedoPreviewCaptions_ExactLockedCopy` — reflection on the four internal const strings (`AlbedoPreviewStylizedCaption`, `AlbedoPreviewUnstylizedCaption`, `AlbedoPreviewEmptyCaption`, `AlbedoPreviewViewStylizedButton`) with `BindingFlags.NonPublic | BindingFlags.Static`, mirroring the ResidualNotWrittenLabel pattern in NamerUIControlsTests: field exists, value equals the exact locked copy (em-dashes verbatim).

Also update the class-level doc comment to mention the two new pins. Do NOT touch existing tests.

Compile/test verification — IMPORTANT project constraint (EditMode tests cannot be run headless; batch-mode Unity is BANNED, the interactive editor holds the project lock): if the unity-mcp bridge is alive, use RunCommand to refresh assets and run ONLY this test class via TestRunnerApi with a targeted groupNames filter (live-editor EditMode run pattern; write results to a file marker, not console logs). If the bridge is dead, write the tests, run the grep gates below, and flag in the summary that the live EditMode run is deferred to the orchestrator (established 04.2/04.3 precedent). Do NOT attempt any -batchmode invocation.
  </action>
  <verify>
    <automated>cd /Users/Shared/SSDevelopment/Development/Namer-Unity && grep -c 'SourceSelection_PersistsAcrossEditorSelectionChange' Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs ; grep -c 'AlbedoPreviewCaptions_ExactLockedCopy' Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs ; grep -c 'Assert.AreSame' Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs</automated>
    Expected: 1, 1, >=1. Plus, if the unity-mcp relay is alive: targeted EditMode run of NamerEditorWindowSmokeTests green (record pass/fail counts in the summary); otherwise explicitly note the deferred live run.
  </verify>
  <done>Both tests exist and follow the file's conventions; the behavioral test proves a synthetic Selection.activeObject change does not clobber the window's _selection; the caption test pins the locked copy verbatim. Live run executed or explicitly deferred to the orchestrator per the constraint.</done>
</task>

<task type="checkpoint:human-verify" gate="blocking">
  <name>Task 4: Verify the three UX fixes in the live editor</name>
  <action>
    Present the completed Tasks 1-3 to the user for visual verification in the live Unity Editor.
    No code work happens in this task — automation is already complete and grep-verified; this
    checkpoint confirms the edits compile and behave correctly. Follow the steps in how-to-verify,
    then wait for the resume signal.
  </action>
  <what-built>Three NamerEditorWindow UX fixes: persistent editable Source ObjectField, Create Profile… one-step create-and-attach in the Stylization section, and a lens-honest 2D albedo preview while styling.</what-built>
  <how-to-verify>
    In the live Unity editor (Tools > NAMER > Processor), with a processed source loaded:
    1. Source persistence: click empty space or an unrelated scene object — the Source field keeps its pick and the material list stays. Assign a different object via the Source field — the inspection updates immediately. The field is disabled while a Process run is in flight.
    2. Create Profile…: open Stylization, click Create Profile…, save the asset — the new profile appears in the Project window (pinged), the attach-state line reads "not extracted", and no save dialog cancel produces any change.
    3. Albedo preview: with the profile attached and a preview computed, the Stylization section shows the full-width albedo image; caption reads "Un-stylized albedo — After View (Preview/Debug) is NAMER" with a View Stylized button — click it and after the ~0.3 s debounce the caption flips to "Stylized albedo — palette transfer applied" and the image re-renders through the stylized lens. With no source/preview computed, the box renders neutral with the "select a source / attach a profile" caption.
    4. No regressions: channel panes, After View popup, and Process button behave as before; the window does not go black (DrawTexture-only path).
  </how-to-verify>
  <resume-signal>Type "approved" or describe issues</resume-signal>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Editor UI → AssetDatabase | The Create Profile… button writes one new .asset inside the project, at a user-chosen path from a Unity-standard save dialog |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-q-01 | Tampering | Create Profile… → AssetDatabase.CreateAsset | accept | Path comes from EditorUtility.SaveFilePanelInProject (Unity-sanctioned in-project picker, overwrite confirmation built in); editor-only tool, no runtime surface |
| T-q-02 | Repudiation | Serialized _selection | accept | UI convenience state only; no security-relevant decisions derive from it |
</threat_model>

<verification>
- All three grep-gate suites in Tasks 1-3 pass with the stated expected counts.
- If unity-mcp relay alive: targeted EditMode run of NamerEditorWindowSmokeTests is green alongside the rest of the live suite; otherwise the deferred live run is explicitly flagged in the SUMMARY for the orchestrator.
- No file other than the two listed in files_modified is touched. NamerStyleProfile.cs, NamerStyleProfileEditor.cs, and the NamerChannelView shader are read-only references.
</verification>

<success_criteria>
- Clicking elsewhere in the editor never changes or clears the Source pick; the pick survives domain reload for asset selections (scene refs accepted-loss per sign-off).
- Source is an editable, busy-disabled ObjectField whose change rebuilds the inspection.
- A style profile can be created and attached from the Stylization section in one step; cancel is a no-op.
- The Stylization section shows the 2D albedo with the lens-honest caption and one-click View Stylized flip; no new GPU compute; no OnGUI Blit.
- Tests pin the persistence behavior and locked caption copy; live run executed or deferred-with-note per constraint.
</success_criteria>

<output>
Create `.planning/quick/260924-ehm-namereditorwindow-ux-fixes-persistent-so/260924-ehm-SUMMARY.md` when done
</output>

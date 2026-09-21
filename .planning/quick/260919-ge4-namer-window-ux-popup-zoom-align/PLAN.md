---
status: complete
quick_id: 260919-ge4
created: 2026-09-19
fixes: 260918-k8k, 260919-fp3
---

# Quick Task: NAMER Processor window UX — popup lifecycle, modifier zoom, pane alignment

## Description

Phase 04.2 UAT feedback on the channel panes/popup feature (260918-k8k +
260919-fp3). Four UX defects, all in `NamerEditorWindow.cs`:

1. Channel popups accumulate — clicking another pane opens a second popup
   instead of replacing the first (`ShowPopup()` windows never take focus, so
   `OnLostFocus` never fires).
2. Popup position drifts down the window with each open (manual
   screen-rect anchoring below the previous popup's screen position), not
   anchored to the clicked pane row.
3. Clicking outside a popup delivers the click to the widget underneath
   (selects it) instead of just closing the popup.
4. Preview scroll-wheel zoom hijacks the dialog scroll whenever the cursor is
   over the preview; plain scrolling should scroll the dialog.
5. The six texture panes don't align with the shaded-view toggle text
   (checkboxes) above them.

## Tasks

All changes in `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs`
ONLY. No shader changes, no test changes.

### Task 1 — Popup lifecycle + anchoring + click-away (`ChannelViewPopup`)

`ChannelViewPopup` (:1107-1170):

- Add `private static ChannelViewPopup _activePopup;` field.
- `Show` (:1119-1139): at entry (after the shader null check), close any
  existing popup:
  `if (_activePopup != null) { _activePopup.Close(); _activePopup = null; }`
- Replace the manual anchoring (:1136-1138)
  (`GUIUtility.GUIToScreenRect`, `popup.position = ...`, `popup.ShowPopup()`)
  with `popup.ShowAsDropDown(paneRect, new Vector2(ChannelPopupSize, ChannelPopupSize));`
  — `paneRect` is already GUI-space; ShowAsDropDown anchors below the clicked
  pane and gives framework-managed click-away dismissal (fixes accumulation,
  drift, and click-through in one call).
- Store `popup` into `_activePopup` after showing.
- Delete the `OnLostFocus` override (:1156-1160) — dead code under ShowPopup
  and counterproductive under ShowAsDropDown.
- `OnDisable` (:1162-1169): after material disposal, add
  `if (_activePopup == this) { _activePopup = null; }`.
- Update the class doc comment (:1101-1106) and the `Show` doc comment
  (:1115-1118) to describe ShowAsDropDown anchoring/click-away instead of
  ShowPopup/OnLostFocus.

Click-through (UAT item 3) is expected to be resolved by ShowAsDropDown's
framework dismissal. No extra code. If live UAT still shows the dismissing
click selecting the widget underneath, fallback (in-window overlay) requires
user sign-off first — do NOT implement it speculatively.

### Task 2 — Modifier-gated preview zoom (`HandlePreviewCameraInput`)

`HandlePreviewCameraInput` (:1248-1268): change the ScrollWheel branch
(:1262-1267) to require a modifier:

```csharp
else if (current.type == EventType.ScrollWheel
    && (current.shift || current.control || current.alt))
{
    _preview.Zoom(current.delta.y);
    current.Use();
    Repaint();
}
```

Plain ScrollWheel over the preview must NOT be consumed — no `Use()`, no
zoom — so the event falls through to the dialog's scroll view. MouseDrag
orbit unchanged.

### Task 3 — Pane column alignment (`DrawChannelPanes`)

`DrawChannelPanes` pane loop (:1030-1034): replace the fixed-size
`GUILayoutUtility.GetRect(ChannelPaneSize, ChannelPaneSize,
GUILayout.Width(ChannelPaneSize), GUILayout.Height(ChannelPaneSize))` with
slots measured the same way the shaded-view toggle row (:967-975) measures
its toggles, so each pane's left edge lands under its toggle's checkbox:

```csharp
if (i == 5)
{
    // No pane for the Vertex Color column; reserve its slot so the
    // Normal pane still lands under the Normal toggle.
    GUILayoutUtility.GetRect(new GUIContent(ShaderInputToggleLabels[5]),
        EditorStyles.toggle, GUILayout.Height(ChannelPaneSize));
}

int toggleIndex = i < 5 ? i : 6;
Rect slot = GUILayoutUtility.GetRect(
    new GUIContent(ShaderInputToggleLabels[toggleIndex]),
    EditorStyles.toggle, GUILayout.Height(ChannelPaneSize));
Rect paneRect = new Rect(slot.x, slot.y,
    Mathf.Min(ChannelPaneSize, slot.width), ChannelPaneSize);
```

Panes stay 48×48 except in narrower columns (e.g. "AO"), where the pane
clamps to the column width so it never spills into the next column. The row's
total width matches the toggle row's, so it cannot overflow. Everything after
`paneRect` acquisition (neutral `GUI.Box` branch, material setup,
Repaint-guarded `Graphics.DrawTexture`, tooltip, cursor rect, MouseDown →
`ShowChannelPopup`) is unchanged.

## Verification

Full EditMode suite in the live editor via unity-mcp RunCommand +
TestRunnerApi per memory `live-editor-editmode-test-run` (results to a Temp
file marker, NOT console logs; no AssetDatabase.Refresh; internal
`CommandScript : IRunCommand`; no System.Reflection / package namespaces).
Expect 157/157 (no test changes — labels and gates untouched). Then user
UAT: popups replace each other and anchor under the clicked pane, outside
click closes without side effects, plain scroll scrolls the dialog,
shift/ctrl/alt+scroll zooms the preview, pane row column-aligns with the
toggle row.

## Commits

Authorized by the user (proceed on the same terms as 260919-fp3). Atomic fix
commit (window file only), then docs commit (this folder + STATE.md
quick-task row). Staging discipline: never sweep unrelated working-tree
changes into commits (Assets/NAMERGenerated/*, Assets/NAMER/NamerSmoke.unity,
ProjectSettings/*, Packages/.../Tests/Editor/ResidualPipelineTests.cs,
NamerDipSwitchReproTests.cs(+meta), .planning/phases/*,
Namer-Unity.sln.DotSettings.user; untracked .planning/debug/resolved/* not
to be swept).

---
status: complete
quick_id: 260919-ge4
created: 2026-09-19
fixes: 260918-k8k, 260919-fp3
---

# Quick Task 260919-ge4: NAMER Processor window UX — popup lifecycle, modifier zoom, pane alignment Summary

**All four UAT defects fixed in `NamerEditorWindow.cs` only: channel popups now replace each other and anchor to the clicked pane via `ShowAsDropDown` (framework click-away dismissal, no accumulation, no drift, no click-through), preview scroll-wheel zoom requires shift/ctrl/alt so plain scroll scrolls the dialog, and the six channel panes column-align with the shaded-view toggle row.**

## What Changed (per task)

### Task 1 — Popup lifecycle + anchoring + click-away (`ChannelViewPopup`)

- Added `private static ChannelViewPopup _activePopup;`.
- `Show` now closes any existing popup before creating a new one (`if (_activePopup != null) { _activePopup.Close(); _activePopup = null; }`), so popups replace each other instead of accumulating.
- Replaced the manual `GUIUtility.GUIToScreenRect` + `popup.position = ...` + `popup.ShowPopup()` anchoring with `popup.ShowAsDropDown(paneRect, new Vector2(ChannelPopupSize, ChannelPopupSize))` — `paneRect` is already GUI-space, so the popup anchors below the clicked pane and the framework handles click-away dismissal (fixes accumulation, drift, and click-through in one call). Stores `_activePopup = popup;` after showing.
- Deleted the dead `OnLostFocus` override (ShowPopup windows never take focus, so it never fired; counterproductive under ShowAsDropDown).
- `OnDisable` clears `_activePopup` when it is this popup (`if (_activePopup == this) { _activePopup = null; }`), after material disposal.
- Class + `Show` doc comments updated to describe ShowAsDropDown anchoring/click-away.

### Task 2 — Modifier-gated preview zoom (`HandlePreviewCameraInput`)

- ScrollWheel branch now requires a modifier: `current.type == EventType.ScrollWheel && (current.shift || current.control || current.alt)`. Plain scroll over the preview is not consumed (no `Use()`, no zoom) and falls through to the dialog's scroll view. MouseDrag orbit unchanged.

### Task 3 — Pane column alignment (`DrawChannelPanes`)

- Replaced the fixed 48px `GetRect` with toggle-column-aligned slots measured like the shaded-view toggle row: a Vertex Color spacer `GetRect` before `i == 5` (no pane for that column, slot reserved so Normal stays under its toggle), `toggleIndex = i < 5 ? i : 6`, `GUILayoutUtility.GetRect(new GUIContent(ShaderInputToggleLabels[toggleIndex]), EditorStyles.toggle, GUILayout.Height(ChannelPaneSize))`, and `paneRect` clamped via `Mathf.Min(ChannelPaneSize, slot.width)` so narrow columns (e.g. "AO") never spill into the next. Everything after `paneRect` acquisition unchanged.

## Verification

- Full EditMode suite in the live editor (unity-mcp RunCommand + TestRunnerApi, results file `Temp/260919-ge4-results.txt`), run by the coordinator after the edits: **PASS=157 FAIL=0 SKIP=0** — matches the expected 157/157 (no test changes).
- Assembly rebuild verified clean by the coordinator: dll rebuilt with `_activePopup` present and `OnLostFocus` gone.

## Commits

- Fix commit (window file only): `135782d` — `fix(quick-ge4): popup replace/anchoring via ShowAsDropDown, modifier-gated preview zoom, toggle-column pane alignment`
- Docs commit (this folder + STATE.md row): this commit.

## Files Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — 40 insertions / 19 deletions, no other files.

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

---
status: complete
quick_id: 260919-fp3
created: 2026-09-19
fixes: 260918-k8k
---

# Quick Task: Fix black NAMER Processor window (channel-pane Graphics.Blit)

## Description

Quick task 260918-k8k introduced six texture-channel panes drawn with
`Graphics.Blit` inside `OnGUI`. That leaks `RenderTexture.active` (left pointing
at the 48x48 pane RT at OnGUI exit) and corrupts the editor window's frame
present — the entire NAMER Processor window renders black (checkboxes included),
console clean, only when an object is selected (the pane branch requires a bound
`_SurfaceMap`). Replace both blit sites with Repaint-guarded
`Graphics.DrawTexture(rect, Texture2D.whiteTexture, material)`.

## Root Cause (instrumented live, 2026-09-19)

- Probe window running the exact production pane code: `RenderTexture.active`
  `null -> TempBuffer 48x48` per event, still the pane buffer at OnGUI exit;
  window renders broken. A/B probe with the fix pattern: `active` stays `null`
  every event; all content renders (user-confirmed visually).
- `Editor.log`: `CAMetalLayer ignoring invalid setDrawableSize width=0
  height=0` at each user repro (10:41:40, 10:49:05) and during the blit probe
  run (11:14:36) — corrupted frame present collapses the drawable.
- `Shader.Find` returns the channel shader fine; no exceptions anywhere; the
  unbound-`_SurfaceMap` hypothesis is disproven (that path draws a neutral
  `GUI.Box` without blitting).

## Tasks

1. `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` —
   `DrawChannelPanes` (~:1051-1058): remove `EnsureChannelPaneRt()`,
   `Graphics.Blit(Texture2D.whiteTexture, _channelPaneRt, _channelViewMaterial, 0)`,
   and `GUI.DrawTexture(paneRect, _channelPaneRt)`; replace with
   `if (Event.current.type == EventType.Repaint) { Graphics.DrawTexture(paneRect, Texture2D.whiteTexture, _channelViewMaterial); }`.
   The shader samples `_SurfaceMap`/`_BaseResidualMap` set on
   `_channelViewMaterial`; the texture argument is only the quad filler.
2. Same file — `ChannelViewPopup.OnGUI` (~:1168-1180): same replacement; delete
   the `_rt` resize block, the `_rt` field, and its `OnDisable` RT disposal
   (keep material disposal).
3. Same file — delete the `_channelPaneRt` field, the `EnsureChannelPaneRt()`
   method, and its disposal in `OnDisable` (~:232-237).

No shader or test changes (pane labels unchanged; suite expectation 157/157).

## Verification

Full EditMode suite in the live editor via unity-mcp RunCommand + TestRunnerApi
(memory `live-editor-editmode-test-run`: results to a Temp file marker, not
console logs; no `AssetDatabase.Refresh`; `internal CommandScript : IRunCommand`;
no System.Reflection / package namespaces). Then user re-selects the object and
confirms the window renders with the pane row.

## Commits

Authorized by the user for this debug task. Atomic fix commit (window file only),
then docs commit (this folder + STATE.md quick-task row).

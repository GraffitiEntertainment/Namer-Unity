---
status: resolved
trigger: Quick task 260918-k8k UAT — the NAMER Processor window renders completely black (no checkboxes, no controls, popup never visible) whenever an object is selected; reads as "auto processing never finishes". Clean console.
created: 2026-09-19
updated: 2026-09-19
---

# Debug Session: black-window-channel-pane-blit

## Symptoms

- **Expected:** Selecting an object shows the normal NAMER Processor window (toggles, preview, foldouts) plus the new six channel panes under the shaded-view toggle row.
- **Actual:** Whole window black — every control gone, checkboxes included; popup never appears. Only with a selection (no selection renders fine). User read it as processing never finishing.
- **Errors:** None. Console clean; no IMGUI exceptions; NamerChannelView.shader imports clean.

## Evidence

- timestamp: 2026-09-19 — Editor.log grep: zero exceptions/shader errors near repros; shader import OK (log :125984). `CAMetalLayer ignoring invalid setDrawableSize width=0.000000 height=0.000000` fired at both user repro times (10:41:40, 10:49:05) AND during the instrumented blit probe run (11:14:36).
- timestamp: 2026-09-19 — Instrumented probe EditorWindow (unity-mcp RunCommand) running the exact production pane code (6× `Graphics.Blit` per OnGUI event, unguarded incl. Layout): `RenderTexture.active` enters OnGUI as `null`, exits as `TempBuffer 7311 48x48` — the pane RT binding leaks past OnGUI exit on every event. `Shader.Find` returns the channel shader (throw path dead).
- timestamp: 2026-09-19 — A/B control probe with the fix pattern (Repaint-guarded `Graphics.DrawTexture(rect, Texture2D.whiteTexture, mat)`): `RenderTexture.active` stays `null` at every event exit; all content renders. User visually confirmed the fix probe showed everything (magenta row, six panes, green row) while the blit-probe/real window were black.

## Eliminated

- hypothesis: OnGUI exception (Shader.Find null / index errors) — eliminated: zero errors in Editor.log + console; labels array verified 7 entries.
- hypothesis: `_SurfaceMap` not bound on first selection — eliminated: unbound path draws a neutral `GUI.Box` and never blits; the shared `_namerMaterial` retains its surface map across selections anyway.
- hypothesis: DrawTriangleWireframe (6a2b27a) — eliminated: behind `_showTriangles` (default off) and uses sanctioned `Handles.BeginGUI` pattern.

## Resolution

- **root_cause:** `Graphics.Blit` inside OnGUI (DrawChannelPanes + ChannelViewPopup.OnGUI, added in 260918-k8k) leaves `RenderTexture.active` pointing at the 48×48 pane RenderTexture when OnGUI returns — at GUI flush/present time the window's frame state is corrupted (macOS collapses the CAMetalLayer drawable to 0×0) and the entire window presents black. Selection-gated because the pane branch only runs when the After material has a bound `_SurfaceMap`.
- **fix:** Replace both blit sites with `if (Event.current.type == EventType.Repaint) { Graphics.DrawTexture(rect, Texture2D.whiteTexture, material); }`; delete `_channelPaneRt`/`EnsureChannelPaneRt()`/popup `_rt` + disposal (keep material disposal). Routed as quick task 260919-fp3; pattern probe-verified in the live editor before applying.
- **lesson:** Never call `Graphics.Blit` inside OnGUI — it swaps the active render target/viewport/projection and does not restore them. `Graphics.DrawTexture` guarded by `EventType.Repaint` is the OnGUI-safe way to draw a material-processed texture.

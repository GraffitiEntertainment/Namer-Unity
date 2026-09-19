---
status: complete
phase: quick-fp3-namer-black-window-channel-pane-blit
plan: 01
subsystem: editor-ui
tags: [unity, editor-window, gui, render-texture, namer, bugfix]

# Dependency graph
requires:
  - quick-k8k channel panes + popup viewer (regression source, a0f5a52)
provides:
  - NAMER Processor window that renders normally (checkboxes included) while channel panes are visible
affects:
  - 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid (residual-smear bisect depends on the channel panes being visible)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - immediate-mode textured quad in editor GUI = Repaint-guarded Graphics.DrawTexture(rect, Texture2D.whiteTexture, material); no RenderTexture, no RenderTexture.active mutation inside OnGUI
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs

key-decisions:
  - "Graphics.DrawTexture keeps the channel textures on the material: the texture argument is only the quad filler; the NamerChannelView shader samples _SurfaceMap/_BaseResidualMap"
  - "Repaint guard (Event.current.type == EventType.Repaint) replaces the Blit path; the popup rect is square so the plain 3-arg DrawTexture overload suffices (no ScaleToFit RT needed)"

requirements-completed: []

# Metrics
duration: 25min
completed: 2026-09-19
---

# Phase quick-fp3-namer-black-window-channel-pane-blit Plan 01: Fix black NAMER Processor window (channel-pane OnGUI Blit) Summary

**Both channel-pane draw sites (window pane row + ChannelViewPopup) now render through a Repaint-guarded `Graphics.DrawTexture(whiteTexture, material)` instead of `Graphics.Blit` into an RT inside `OnGUI`, eliminating the `RenderTexture.active` leak that collapsed the window's Metal drawable to 0x0 and rendered the whole Processor window black**

## Performance

- **Duration:** ~25 min
- **Started:** 2026-09-19T18:05Z (approx)
- **Completed:** 2026-09-19T18:25Z
- **Tasks:** 3 (one atomic fix commit)
- **Files modified/created:** 1

## Accomplishments

- `DrawChannelPanes`: removed `EnsureChannelPaneRt()` + `Graphics.Blit(Texture2D.whiteTexture, _channelPaneRt, _channelViewMaterial, 0)` + `GUI.DrawTexture(paneRect, _channelPaneRt)`; replaced with a Repaint-guarded `Graphics.DrawTexture(paneRect, Texture2D.whiteTexture, _channelViewMaterial)`. The shader sources `_SurfaceMap`/`_BaseResidualMap` from the material; the texture argument is only the quad filler.
- `ChannelViewPopup.OnGUI`: same replacement — deleted the `_rt` resize/recreate block and the Blit + `GUI.DrawTexture(rect, _rt, ScaleToFit)`; the popup rect is square (384x384) so the plain 3-arg overload preserves fit. Deleted the `_rt` field and its `OnDisable` RT disposal (material disposal kept).
- Removed the `_channelPaneRt` field, the `EnsureChannelPaneRt()` method, and the `_channelPaneRt` disposal block in the window's `OnDisable` (kept `_channelViewMaterial` disposal).
- Doc comments touched only where factually wrong: "each blitted through" -> "each drawn through", "no blit/tooltip/click" -> "no draw/tooltip/click", "blit material" -> "material", popup "material and render target" -> "material".
- Net: 10 insertions / 44 deletions in `NamerEditorWindow.cs`, no other files.

## Task Commits

1. **Tasks 1-3 (atomic): Replace both OnGUI Blit sites with Repaint-guarded Graphics.DrawTexture and remove the pane/popup RTs** - `70ba14a` (fix)

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` - pane draw, popup draw, RT field/method/disposal removals, doc-phrase fixes

## Decisions Made

- Kept the `Texture2D.whiteTexture` filler-quad idiom from the plan: the channel shader ignores the vertex texture inputs it doesn't sample, so no shader change was needed.
- Did not attempt a ScaleMode-preserving DrawTexture overload in the popup — the popup rect is fixed-square, so the 3-arg overload is exact.

## Deviations from Plan

None - plan executed exactly as written. (One transient tooling retry: the live-editor RunCommand script needed `using UnityEngine;` added for `ScriptableObject` — a /tmp script fix, not a repo change.)

## Issues Encountered

- This executor has no `mcp__unity-mcp__*` tools; as in quick-k8k, the unity-mcp relay (`~/.unity/relay/relay_mac_arm64.app --mcp`) was driven from Bash via a small JSON-RPC stdio client (initialize -> tools/call `Unity_RunCommand`) — the sandbox wraps scripts in `Unity.AI.Assistant.Agent.Dynamic.Extension.Editor` and does not implicitly import `UnityEngine`.
- The editor auto-imported/compiled the edited `NamerEditorWindow.cs` (dll written 11:21, "Reloading assemblies after finishing script compilation", no CS errors) before the test launch, so no explicit Refresh RunCommand was needed — verified by symbol probe: `DrawChannelPanes`/`ChannelViewPopup` present, `EnsureChannelPaneRt` gone from `GraffitiEntertainment.Namer.Editor.dll` (a failed compile writes no dll).

## Verification

- Full EditMode suite in the LIVE editor (TestRunnerApi via unity-mcp RunCommand, results file `Temp/260919-fp3-results.txt`, FINISHED-marker background watch): **SUMMARY PASS=157 FAIL=0 SKIP=0** — 157 passed / 0 failed / 0 skipped, single summary line (no duplicate-callback artifact), zero `FAILED:` lines. Matches the expected 157/0/0.
- Compile verified against the post-fix assembly (dll rebuilt and domain reloaded before the run launched).
- Plan's remaining gate (user re-selects the object and visually confirms the window renders with the pane row) is the user's visual confirmation, per the A/B probe already done during diagnosis.

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

## Self-Check: PASSED

- Modified key file present on disk: `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` (verified via `git show`/`git status`).
- Task commit verified in git log: `70ba14a`.
- Test results file read verbatim: `SUMMARY PASS=157 FAIL=0 SKIP=0`.

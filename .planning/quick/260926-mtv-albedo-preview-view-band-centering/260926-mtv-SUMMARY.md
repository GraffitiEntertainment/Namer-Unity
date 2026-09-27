---
phase: quick-260926-mtv
plan: 01
subsystem: ui
tags: [unity, editor-window, guilayout, scrollview, graphics-drawtexture, hlsl, uv-subrect, editmode-tests]

# Dependency graph
requires:
  - phase: quick-260926-lqr
    provides: the hue-preserving post look and albedo preview row this preview quad renders into
provides:
  - Albedo preview quad centered on the VISIBLE scroll band (scroll.x + viewWidth/2), never the content-width row center
  - Explicit band clipping via drawRect + _UvRect shader sub-rect window (clipped region removed from sampling, not stretched)
  - First-frame and degenerate-band draw guards (no overdraw of the horizontal scrollbar / action header)
  - Headless-testable pure geometry seam NamerEditorWindow.ComputeAlbedoPreviewGeometry
affects: [NamerEditorWindow albedo preview, NamerChannelView consumers (channel panes, channel popup), NamerEditorWindowSmokeTests]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Immediate-mode per-draw shader window: material property set immediately before Graphics.DrawTexture and reset to identity immediately after, protecting earlier-in-frame consumers of the shared material"
    - "Widget-space vs content-space rect discipline: GUILayoutUtility.GetLastRect() immediately after EndScrollView returns the scroll-view WIDGET rect; rows inside the scroll view are CONTENT-space"

key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Shaders/NamerChannelView.shader
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs

key-decisions:
  - "Derive vbar presence unconditionally (content is always taller than the view) and hbar presence from the row-width law (expand row pins at exactly viewWidth when content fits) — no second content-height capture field"
  - "Clip via uvRect computed against the UNCLIPPED fit rect with Max(1f, ...) guarded denominators, so the visible portion renders at correct aspect"

patterns-established:
  - "Per-draw property window with immediate identity reset for shared immediate-mode materials"

requirements-completed: [UI-04]

# Metrics
duration: 12min
completed: 2026-09-26T23:46:00Z
---

# Quick Task 260926-mtv: Albedo Preview View-Band Centering Summary

**Albedo preview quad now centers on the visible scroll band and clips via an identity-default `_UvRect` shader sub-rect window, replacing the content-width centering that overdraw the horizontal scrollbar and action header; plus 4 headless geometry tests against the new pure helper.**

## Performance

- **Duration:** ~12 min (started 2026-09-26T23:33:41Z)
- **Tasks:** 2/2
- **Files modified:** 3 (no files created, no .meta changes)

## What Was Done

### Task 1: View-band capture + banded draw + _UvRect window

All seven planned edits, exactly as specified (no more, no less):

1. `UvRectId` property ID added after `ChannelId` (NamerEditorWindow.cs:128).
2. Single initialized field `private Rect _scrollViewRect = new Rect(0f, 0f, 0f, 0f);` next to `_scrollPosition` (line 235-236) — the amended design; `_scrollContentHeight` was NOT added anywhere (would be dead code / CS0414).
3. OnGUI captures `_scrollViewRect = GUILayoutUtility.GetLastRect();` immediately AFTER `EditorGUILayout.EndScrollView()` (lines 1291-1293).
4. DrawAlbedoPreview's old aspect-fit/center/draw block replaced by the view-band block (lines 2262-2290): first-frame guard `_scrollViewRect.height > 0f`; `vbarW`/`hbarH` from `GUI.skin` (no literals); unconditional vbar subtract with rationale comment; `hbarShown` exact overflow detector (`rect.width > viewWidth + 0.5f`); `viewHeight` conditional hbar subtract; helper call; degenerate+Repaint-guarded draw that sets `UvRectId` immediately before `Graphics.DrawTexture` and resets it to identity `(0f, 0f, 1f, 1f)` immediately after. The four property sets above the block, the row reservation, the neutral `GUI.Box` else-branch, and the caption row are untouched.
5. New `internal static void ComputeAlbedoPreviewGeometry(Rect rowRect, float textureWidth, float textureHeight, float viewWidth, float viewHeight, Vector2 scroll, out Rect drawRect, out Rect uvRect)` with Doxygen header (lines 2333-2375): pure geometry — fit scale with `Mathf.Max(1f, ...)` guarded texture denominators, unclipped fit rect centered x on `scroll.x + (viewWidth - drawWidth) * 0.5f` and y within the row, band rect at `(scroll.x, scroll.y, viewWidth, viewHeight)`, `Rect.MinMaxRect` intersection, uvRect as the visible fraction of the unclipped fit with guarded `fitW`/`fitH` denominators, `drawRect = clipped` (inverted when fully out of band).
6. Shader Properties: `[HideInInspector] _UvRect("UV Rect", Vector) = (0,0,1,1)` after `_OcclusionStrength` with the one-line identity-default comment (NamerChannelView.shader:14-15).
7. Shader HLSL: `float4 _UvRect;` directly after `float _Channel;` outside the UnityPerMaterial CBUFFER (line 57); `output.uv = input.uv * _UvRect.zw + _UvRect.xy;` (line 78); vertex comment updated to name the identity-default sub-rect window (raw UVs when `_UvRect == (0,0,1,1)`).

### Task 2: 4 geometry tests against ComputeAlbedoPreviewGeometry

Added after `StylizePreviewBase_PrefersSourceOverProjected` and before the "-- EditorPrefs isolation" banner (test file lines 249-338), each with a Doxygen header and hand-derived expectation comments, calling the internal helper DIRECTLY (InternalsVisibleTo at Editor/AssemblyInfo.cs line 5, confirmed):

- `CentersInViewWidth_NotRowWidth` — 900px row / 488px view: `drawRect.center.x == 244` (viewWidth/2), message names 450 (row center) as the pre-fix behavior.
- `NeverWiderThanView_WideTexture` — 4096x512 texture: `drawRect.width == 488` exact, `uvRect.width == 1`.
- `BandClip_CutsQuadAndReportsUvSubRect` — `drawRect.yMin == 2200`, `uvRect.y == 0`, `uvRect.height == 300/488`, `uvRect.yMax == 300/488 < 1` (quad bottom 2500 coincides with band bottom 2500, so only the top is cut; final expectations after the orchestrator's bottom-origin v fix — see post-execution section).
- `FullyVisible_PassesIdentityUv` — `uvRect == (0, 0, 1, 1)` with NO tolerance (0/488 and 488/488 are exact floats), `drawRect.yMin == 1012`, `drawRect.height == 488` at tolerance 0.0001f.

No existing test modified; no new usings needed.

## Orchestrator Post-Execution Fixes (live verification, 2026-09-26T23:35-23:50Z)

1. **UV v-origin fix (probe-verified).** The helper computed `uvRect.y` as a fraction from the quad's GUI TOP, but the `_UvRect` shader window operates in texture space where v == 0 is the quad's BOTTOM edge. Live probe through the production path (throwaway EditorWindow, two-tone linear texture bottom=blue/top=red, `Graphics.DrawTexture` with the real NamerChannelView material, screen `ReadPixels` classification): `_UvRect = (0,0,1,0.5)` rendered the texture's BOTTOM half (solid blue, 0 red pixels; identity quad calibrated upright + y-flipped capture) — bottom-origin confirmed. A top-based offset would sample the mirrored slice whenever the quad is vertically clipped.
   - Fix: `uvRect.y = 1f - (clipped.yMax - fit.yMin) / fitH` in `ComputeAlbedoPreviewGeometry` (+ probe-fact comment).
   - `BandClip_CutsQuadAndReportsUvSubRect` expectations updated to the bottom-origin invariant: `uvRect.y == 0` (bottom uncut, exact), `uvRect.height == 300/488`, `uvRect.yMax == 300/488 < 1`. Other three tests unaffected (identity/vertical-uncut cases already yield y == 0 exactly).
   - Probe artifacts: `Temp/probe-uvrect.txt`.

2. **Repaint-gated widget capture (probe-verified; fixes user-reported invisible preview).** After visual pickup the user reported "the albedo preview is no longer visible". Replica probe of the real window structure (scroll view + 900px fixed rows + 512px expand row + capture after `EndScrollView` + action button; `Temp/probe-replica-prod.txt`) proved: `GUILayoutUtility.GetLastRect()` right after `EndScrollView()` returns the true widget rect (501×379) during **Repaint** events but a degenerate **(0,0,1,1)** during **Layout** events — and Unity fires Layout→Repaint each frame, so the original unconditional capture handed every Repaint pass the Layout pass's 1×1 rect: the `height > 0f` guard passed (1 > 0), `viewWidth = 1 − 13 = −12`, the band inverted, `drawRect` came out negative-sized, and the draw was skipped every frame (0 of 178,796 expected probe pixels drawn). The earlier minimal probe missed this because it captured and drew within a single Repaint call.
   - Fix: wrap the `_scrollViewRect` capture in `if (Event.current.type == EventType.Repaint)` (NamerEditorWindow.cs OnGUI, after `EndScrollView`). Verified in the same replica with the gate applied (`Temp/probe-replica-gated.txt`): `viewWidth=488`, `viewHeight` from the true widget rect, `drawRect 488×367 valid=True`, **magentaPx=178796** (quad draws).
   - Probe artifacts: `Temp/probe-replica-prod.txt`, `Temp/probe-replica-gated.txt` (plus two superseded runs: `probe-replica-albedo.txt`, `probe-replica-fix.txt` — the first pair had a never-set `haveData` flag so their draw path never executed; conclusions drawn only from the corrected pair).

## Live Editor Verification (orchestrator)

- Recompile: `CompilationPipeline.RequestScriptCompilation()`; dlls 16:45:08 local, newer than every source edit; `ComputeAlbedoPreviewGeometry` present in `GraffitiEntertainment.Namer.Editor.dll` (tests dll references it).
- Targeted `NamerEditorWindowSmokeTests`: **12/12 PASS** (8 existing + 4 new), `Temp/quick-260926-mtv-smoke-results.txt` (snapshotted /tmp).
- After the Repaint-gate fix: recompiled (dll 17:11:05, newer than the edit) and re-ran the targeted suite: **12/12 PASS**, `Temp/mtv-fix-smoke-results2.txt` (fixture group name is `GraffitiEntertainment.Namer.Tests.NamerEditorWindowSmokeTests` — no `.Editor` segment).
- Full EditMode suite: **301 pass / 2 fail / 0 skip** (303 total = prior 299 + 4 new), `Temp/quick-260926-mtv-full-results.txt`:
  - `NamerAOControlsTests.AoContrast_Identity_LeavesAoUnchanged` — the documented pre-existing AO drift.
  - `AssetGeneratorTests.BasePng_EncodesLinearToSrgb` — NEW vs the prior 298/299 run, environmental: fails identically in an isolated `groupNames` run (6/7, same signature); generated base carries a ×0.799 AO multiply (`linear(170)/linear(188) = 0.7993`) on the authored-white occlusion, and the user's live AO EditorPrefs are tuned (`AoContrast=1.296, AoStrength=0.769, AoUnmultiply=0.692, AoBlur=14.578`) — the exact "tuned AO settings break the D-06 byte-exact oracle" mode the test's D-07 comment warns about (its white-occlusion guard covers the synthetic-AO path only; blur/strength still bleed through — pre-existing isolation gap, orthogonal to this task; candidate follow-up quick task).

## Deviations from Plan

None executor-initiated — plan executed exactly as written. Two plan-flaged amendments were honored as instructed: (1) `_scrollContentHeight` dropped from the approved set (absent from the plan text and from the code — grep count 0); (2) TEST 3 pins `uvRect.yMax == 1` with `uvRect.height < 1` per the plan's own deviation note correcting the earlier prose.

## TDD Gate Compliance

Tasks carry `tdd="true"`; plan frontmatter type is `execute`. Per critical_constraint 2 the executor may not run Unity (interactive editor holds the project lock), so RED/GREEN validation is DEFERRED to the orchestrator's live EditMode run — authoring-only satisfies the TDD requirement for this task per the orchestrator's instructions. No `test(...)`/`feat(...)` gate commits exist in git history because the executor is forbidden from committing (constraint 1); the orchestrator owns all commits and should verify the tests fail against the pre-fix draw math only insofar as the helper seam is new code (the tests target the new helper, so they are GREEN-gate tests for this change, not regression-catching RED tests against deleted code — the old block they replace is already gone, which is the plan's design).

## Tests NOT Run (executor could not)

- `NamerEditorWindowSmokeTests` (12 tests: 8 existing + 4 new) — requires the live editor; the ORCHESTRATOR runs the targeted EditMode groupNames run per the plan's verification section. Expected 12/12 green.
- Compile check — deferred to the live editor (no CLI/batchmode Unity allowed).
- Visual probe (wide window centering, narrow-window clip without scrollbar/header overdraw, fully-scrolled-out row draws nothing) — orchestrator manual step.

## Verification Performed (executor, read-only)

**Task 1 grep gate** — all sites present:
- `UvRectId` at 3 sites: decl 128, set 2283, reset 2288 (set+reset both INSIDE the Repaint guard).
- `_scrollViewRect` at 5 sites: decl 236, OnGUI capture 1293, height guard 2267, viewWidth 2274, viewHeight 2279.
- `ComputeAlbedoPreviewGeometry` exactly twice in NamerEditorWindow.cs: call 2280, definition 2340.
- `verticalScrollbar.fixedWidth` / `horizontalScrollbar.fixedHeight` / `hbarShown` at 2269/2270/2278-2279 (line 1501 is a pre-existing unrelated popup use — untouched).
- Shader `_UvRect` at 3 functional sites: Properties 15, HLSL decl 57, ChannelVert 78 (plus comment mentions 72-73).
- Count gates: `rect.x + (rect.width - drawWidth)` == 0 in NamerEditorWindow.cs (old content-width centering gone); `output.uv = input.uv;` == 0 in the shader (old raw-UV vert line gone); `_scrollContentHeight` == 0.

**Task 2 grep gate** — 4 test names at lines 257/275/297/321; `ComputeAlbedoPreviewGeometry` count in test file == 4; `[Test]` count == 12.

**Read-only walkthrough** (re-read OnGUI 1283-1296, DrawAlbedoPreview 2253-2331, helper 2333-2375, shader regions, new tests): capture is AFTER EndScrollView; first-frame guard present; set+reset inside the Repaint guard; helper x-center is `scroll.x + (viewWidth - drawWidth) * 0.5f` (not rowRect-based); uvRect uses the UNCLIPPED fit rect with `Max(1f, ...)` denominators; shader identity default `(0,0,1,1)`; row reservation, property sets, GUI.Box else-branch, caption row, empty-reason block, DrawChannelPanes, ShowChannelPopup all untouched.

**Diff containment**: `git diff -U0` hunk map shows edits ONLY at the planned locations (window: 128, 235, 1292, 2262/2269, 2333; shader: 14, 57, 71/78; tests: 249). `git status --porcelain` filtered to the package shows exactly the three files modified; all other dirty entries in the working tree pre-existed and were not touched, staged, or committed.

## Determinism notes

All four test fixtures use exact binary floats: 488 = 61*8, fitScale values 61/128 and 61/512 are exact; the derived expectations (244, 488, 2200, 188/488, 300/488, 1012, 1) are float-exact where asserted without tolerance and within 0.0001f otherwise. The helper has no GUI state, no instance state, no randomness, no threading.

## Known Stubs

None — all data paths are wired (scroll rect captured from live GUILayout, geometry from the real stylize base dimensions, uvRect consumed by the real shader window).

## Threat Flags

None — no security-relevant surface beyond the plan's threat model. T-q260926-mtv-01 (stale _UvRect) mitigated by the set-then-immediate-reset inside the Repaint guard plus the shader's identity default; T-q260926-mtv-02 (degenerate inputs) mitigated by `Max(1f, ...)` guarded denominators and the caller's width/height > 0 draw guard.

## Self-Check: PASSED

- Files: all three modified files exist on disk (`[ -f ]` verified via git status/read-back of edited regions).
- Commits: N/A by design — constraint 1 forbids executor commits; the orchestrator owns all commits for this mixed working tree.
- Grep gates: both task gate blocks run, all counts match the plan's done criteria (gates 0, tests 12).

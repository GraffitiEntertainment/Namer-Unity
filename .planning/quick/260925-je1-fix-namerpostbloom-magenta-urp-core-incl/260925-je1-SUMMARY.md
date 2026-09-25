---
quick: 260925-je1
title: NamerPostBloom magenta fix + albedo preview 512px aspect-fit
status: complete
mode: quick
commits:
  - d951b05
  - 16292eb
verification: grep-gates-passed, diff-review-passed, relay-checks-deferred-to-orchestrator
---

# Quick 260925-je1: NamerPostBloom Magenta (URP Core.hlsl include) + Albedo Preview Layout

## What Changed

Two surgical, already-root-caused fixes in the Namer-Unity UPM package:

1. **Stylized + Post lens magenta** — `NamerPostBloom.shader`'s `HLSLINCLUDE`
   pulled core's `Common.hlsl` + `SpaceTransforms.hlsl` directly, which never
   brings in `UnityInput.hlsl`'s `UNITY_MATRIX_M` that
   `TransformObjectToHClip` (the shared vertex program of all three passes)
   needs on Metal — so every pass failed to compile and the lens rendered the
   error color. Both core includes were replaced with the single house URP
   include (`Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl`,
   the exact include `NamerPostPreview.hlsl:24` and `NAMER.shader:339` already
   use). That include chains `UnityInput.hlsl`, declaring `UNITY_MATRIX_M`,
   and also provides everything else the shader uses
   (`TEXTURE2D`/`SAMPLER`/`SAMPLE_TEXTURE2D`/`Max3`).
2. **Albedo preview layout** — `DrawAlbedoPreview` reserved its row with an
   absolute `rowWidth` wider than the scroll-view content (overflow under the
   vertical scrollbar) and stretched the image via
   `Graphics.DrawTexture`'s default scaleMode (aspect lost). The row is now
   reserved with `GUILayoutUtility.GetRect(0f, kAlbedoPreviewHeight,
   GUILayout.ExpandWidth(true), GUILayout.Height(kAlbedoPreviewHeight))`
   (expand-to-content-width — can never push under the scrollbar), the
   constant is bumped 128f -> 512f, and the image is drawn into an aspect-fit
   sub-rect (uniform `Mathf.Min` scale from the stylize base RT, centered
   horizontally — and vertically when width binds; vertical centering is a
   no-op when height binds). The neutral `GUI.Box` state keeps the FULL
   reserved rect and the caption row / View Stylized flipper are untouched.
   The `currentViewWidth` call at line 1496 (before/after pane row) was left
   alone per the plan's scope guard.

## Task 1 — Shader include swap (d951b05)

Files (1):

- `Packages/com.graffitientertainment.namer/Shaders/NamerPostBloom.shader` —
  two core includes deleted, one URP Core.hlsl include in their place. Nothing
  else in the shader changed.

Local gates (all passed):

- `grep -c "render-pipelines.universal/ShaderLibrary/Core.hlsl"` == 1
- `grep -c "render-pipelines.core"` == 0
- `git diff` shows exactly the include lines changed (1 insertion, 2 deletions)

## Task 2 — Albedo preview layout (16292eb)

Files (1):

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` —
  `kAlbedoPreviewHeight` 128f -> 512f; `rowWidth` computation deleted and the
  row reserved via `ExpandWidth(true)`; aspect-fit centered `drawRect`
  computed from `stylizeBase.width/height` and used as the
  `Graphics.DrawTexture` target (material, whiteTexture quad, and the four
  property sets unchanged); brief why-comments added at both edits.

Local gates (all passed):

- `kAlbedoPreviewHeight` shows `= 512f` at the constant (line 134) and a
  single `GetRect` usage (line 2231) — no other references
- `grep -c "rowWidth"` == 0
- Method range 2225-2305: `ExpandWidth` == 1, `currentViewWidth` == 0
- Scope guard: `currentViewWidth` still present at line 1496 (before/after
  pane row, untouched)
- `Graphics.DrawTexture(drawRect` == 1 in the method range; full diff reviewed
  — only the three planned edits

## Deferred to Orchestrator — Relay Verification

The executor session had no unity-mcp relay tools and the interactive editor
holds the project lock, so both live-editor gates are deferred. The
orchestrator should run these immediately after this return:

1. **Shader reimport console check (Task 1 gate):** via
   `mcp__unity-mcp__Unity_RunCommand`, execute
   `UnityEditor.AssetDatabase.ImportAsset("Packages/com.graffitientertainment.namer/Shaders/NamerPostBloom.shader", UnityEditor.ImportAssetOptions.ForceUpdate)`
   (no File.Delete patterns anywhere in the command), then poll
   `mcp__unity-mcp__Unity_GetConsoleLogs` a few times over ~30s (shader
   compile is asynchronous) and assert ZERO entries matching
   `Shader error in 'Hidden/NamerPostBloom'`, `UNITY_MATRIX_M`, or
   `GetObjectToWorldMatrix`.
2. **Script recompile console check (Task 2 gate):** trigger a recompile
   (editor focus, or `mcp__unity-mcp__Unity_RunCommand` with
   `UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation` —
   no Refresh, no File.Delete patterns), then
   `mcp__unity-mcp__Unity_GetConsoleLogs`: zero CS errors referencing
   `NamerEditorWindow`.

## Visual Confirmation Checklist (for the user's live editor)

Per the plan's verification section, deferred there because a live probe was
impractical in this session:

- (a) Stylized + Post lens shows the bloom approximation, not magenta.
- (b) Albedo Preview is 512 px tall, the image aspect is correct and
  horizontally centered, nothing renders under the vertical scrollbar, the
  neutral state and caption row are intact.

## Deviations from Plan

None code-wise — both tasks executed exactly as specified. Two execution
notes:

- Both commits required the established `git -c commit.gpgsign=false` retry
  (1Password agent "failed to fill whole buffer" error); nothing else was
  weakened.
- The relay gates above were executed as static/git-level verification only
  (grep gates + diff review), per the session's verification-reality
  constraints; live-editor checks are listed for the orchestrator.

## Known Stubs

None introduced.

## Threat Flags

None — no new network/auth/file-access surface; one include path swap and one
IMGUI layout change.

## Metrics

- Duration: ~3 min (2026-09-25T21:03:16Z -> 21:06:25Z)
- Tasks: 2/2 complete
- Commits: d951b05, 16292eb (one file each, atomic)

## Self-Check: PASSED

All must-have artifacts, key-link patterns, and commits verified on disk/in
git log; working tree under `Packages/` is clean (both task files fully
committed, nothing stray staged).

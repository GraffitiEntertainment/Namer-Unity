---
phase: 03-asset-generation-editor-workflow-preview
plan: 02
subsystem: editor
tags: [unity, upm, editor-window, imgui, previewrenderutility, compute-shader, debounce, context-menu]

# Dependency graph
requires:
  - phase: 02-source-inspection-gpu-compute-pipeline
    provides: SourceInspector.Inspect, NamerComputePipeline (Process/ReleaseResult), NamerSourceModel, NamerMaterialInspection
  - phase: 03-asset-generation-editor-workflow-preview
    plan: 01
    provides: NamerProcessor.Process + NamerProcessResult, NamerProcessorSettings, NamerEditorConstants (DebounceSeconds)
provides:
  - NamerPreviewRenderer (PreviewRenderUtility before/after mesh preview with synced orbit/zoom camera, Render(true) URP-correct)
  - NamerDebugView.shader + NamerDebugChannelMaterial (editor-only debug channels reusing NamerSurface.hlsl decode, D-12)
  - NamerEditorWindow (Tools > NAMER > Processor + Assets/GameObject context menus + debounced AO recompute)
affects: [03-03 (source-immutability tests, shippable package)]

# Tech tracking
tech-stack:
  added: []
  patterns: [PreviewRenderUtility Render(true) for URP editor preview, editor-only debug material factory with cached PropertyToID, EditorApplication.update debounce pump, single shared NamerProcessor.Process entry point]

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs
    - Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  modified: []

key-decisions:
  - "Preview recompute assigns NamerComputePipeline result render targets directly to in-memory NAMER/debug materials (no readback); the PreviewRenderUtility samples the RTs, and readback is reserved for the disk-write path (D-10)"
  - "Before/after preview is one combined PreviewRenderUtility render (both meshes in a single BeginPreview/EndPreview) with 'Before'/'After' captions above the halves, so both panes share one honest camera state (D-09)"
  - "Output controls bind directly to the EditorPrefs-backed NamerProcessorSettings (Destination/Prefix/Suffix/OverwriteGenerated), not a separate UI model"

patterns-established:
  - "NamerDebugChannelMaterial caches Shader.PropertyToID once and null-throws on Shader.Find, centralizing the debug-shader lookup"
  - "Debounce pump: EditorApplication.update Tick checks _dirty + NamerEditorConstants.DebounceSeconds, then ReleaseResult -> NamerComputePipeline.Process -> assign RTs -> Repaint"

requirements-completed: [UI-01, UI-02, UI-03, UI-04, UI-05, UI-06]

# Metrics
duration: 44min
completed: 2026-08-27
---

# Phase 3 Plan 2: Editor Window + Preview + Debug Views Summary

**Tools > NAMER > Processor editor window with a PreviewRenderUtility before/after mesh preview (`Render(true)` URP-correct), a 7-mode debug-channel shader reusing `NamerSurface.hlsl`, and three `Process with NAMER` command surfaces (Assets menu, GameObject menu, window button) routing to the single `NamerProcessor.Process` entry point with a 300 ms debounced AO recompute.**

## Performance

- **Duration:** 44 min (includes the preview-spike human-verification checkpoint gate)
- **Started:** 2026-08-27T22:06:36Z
- **Completed:** 2026-08-27T22:50:20Z
- **Tasks:** 2 implementation tasks + 1 human-verify checkpoint
- **Files modified:** 9 (4 source files + 5 Unity-generated `.meta` files)

## Accomplishments

- `NamerPreviewRenderer` wraps a single `PreviewRenderUtility` and calls `Render(true)` (never bare `Render()`), the fix for the Phase-3 pink-preview risk — confirmed non-pink at pixel level on this machine via the approved checkpoint.
- `NamerDebugView.shader` reuses `#include "NamerSurface.hlsl"` + `NAMER_DECODE_SURFACE` so the six debug channels (Base Color, AO, Normal, Roughness, Metallic, Emissive) cannot drift from the runtime decode (D-12).
- `NamerEditorWindow` implements the UI-SPEC layout (Source → Preview → Processing → Output → Action), state contract, and copywriting contract, with a shared orbit/zoom camera and a 7-mode debug toolbar that switches only the after pane.
- Three `Process with NAMER` surfaces (Assets menu, GameObject menu, window button) all route to `NamerProcessor.Process` — the single shared entry point (D-13).
- AO slider changes mark dirty and, after `NamerEditorConstants.DebounceSeconds` (300 ms), recompute on the GPU via `NamerComputePipeline`, releasing the prior live result first (no RT leak) and never writing to disk (D-10 / UI-06).
- No dead Phase 4/5 controls ship — the window exposes only the functional D-14 set.
- Full EditMode suite validated in a scratch clone: **46/46 passed, 0 failed** (compilation clean after the `MarkDirty` fix).

## Task Commits

1. **Task 1: NamerPreviewRenderer + NamerDebugView.shader + NamerDebugChannelMaterial** — `b45de88` (feat)
2. **Checkpoint (human-verify): URP preview renders non-pink** — no commit (approved via pixel-level evidence)
3. **Task 2: NamerEditorWindow (processor window + command surfaces + debounce pump + Process wiring)** — `19efea7` (feat)

**Plan metadata:** committed separately (docs: complete plan).

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs` — PreviewRenderUtility before/after mesh renderer with synced framed/orbit/zoom camera and `Render(true)`
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs` — editor-only debug material factory + cached PropertyToID
- `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader` — editor-only 6-channel debug shader reusing the shared decode include
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — processor window + three command surfaces + debounce pump + Process wiring

## Decisions Made

- Preview recompute assigns the pipeline's result render targets directly to in-memory NAMER/debug materials (no readback) — the preview samples RTs, and readback stays exclusive to the disk-write path.
- Before/after is a single combined render with captions, not two independent renders, so both panes share one honest camera.
- Output fields bind to the EditorPrefs-backed `NamerProcessorSettings` directly.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed missing `MarkDirty` method (compile error CS0103)**
- **Found during:** Task 2 (NamerEditorWindow — first clone-runner compile)
- **Issue:** `RebuildInspection` and the AO slider both called `MarkDirty()`, but the method was never defined, producing two `CS0103` compile errors.
- **Fix:** Added the `MarkDirty()` method (sets `_dirty = true` and records `_lastChange = EditorApplication.timeSinceStartup`).
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs`
- **Verification:** Clone-runner EditMode suite re-run green — 46/46 passed, 0 failed.
- **Committed in:** `19efea7` (part of the Task 2 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** The fix was a one-method omission required for the debounce pump; no scope change.

## Issues Encountered

- None beyond the deviation above. The preview-spike risk (RESEARCH Open Question #1, the Phase-3 blocker) was resolved at the checkpoint — the `Render(true)` URP path renders non-pink on this machine.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Ready for 03-03 (Source-immutability + asset-path tests; assemble shippable MVP package).
- The `NamerPreviewRenderer`, debug shader/material, and `NamerEditorWindow` are the presentation surface 03-03 will validate end-to-end.

## Self-Check: PASSED

All four plan source files exist on disk and both task commits (`b45de88`, `19efea7`) are present in git history; the clone-runner EditMode suite passed 46/46.

---
*Phase: 03-asset-generation-editor-workflow-preview*
*Completed: 2026-08-27*

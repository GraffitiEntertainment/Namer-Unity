---
phase: quick-n6x-remove-silent-geometry-bake-override
plan: "01"
subsystem: ui
tags: [unity, editor-window, ao, compute, ui, deletion]

requires:
  - phase: 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit
    provides: "NamerComputePipeline three-way AO gate (authored > cached bake > extraction) with HasCachedBake/RequestBake forwarders"

provides:
  - "Debounced AO-slider preview that no longer mutates BakeSourceMesh/OccluderMesh or triggers a geometry bake"
  - "Processing section with the four AO sliders and no High-res Occluder ObjectField or fallback warning"

affects: [03.1 AO preview, future explicit Bake AO action]

tech-stack:
  added: []
  patterns:
    - "Preview recompute is a straight inspection -> _pipeline.Process(inspection) with no bake priming"

key-files:
  modified:
    - "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"

key-decisions:
  - "Removed TriggerAutomaticBake and the High-res Occluder UI entirely rather than gating them, so the window never auto-bakes after a recompute and the image-space extraction preview stays authoritative"

patterns-established: []

requirements-completed: [UI-03]

metrics:
  duration: 4min
  completed: 2026-08-29
---

# Phase quick-n6x-remove-silent-geometry-bake-override: Summary

**Removed the silent geometry-bake override from NamerEditorWindow so the AO-slider preview reshapes the image-space extracted AO instead of auto-baking and caching a near-white geometry bake.**

## Performance

- **Duration:** 4 min
- **Started:** 2026-08-29T23:44:00Z
- **Completed:** 2026-08-29T23:48:47Z
- **Tasks:** 2
- **Files modified:** 1

## Accomplishments

- `RecomputePreview` is now a straight `inspection` -> `_pipeline.Process(inspection)` path with no `BakeSourceMesh`/`OccluderMesh` mutation and no post-recompute `TriggerAutomaticBake` call.
- `TriggerAutomaticBake`, `_occluderMesh`, `_occluderWarning`, and the "High-res Occluder" ObjectField + fallback warning HelpBox are fully removed from `NamerEditorWindow.cs`.
- The bake subsystem (`NamerAOPipeline.cs`, `NamerComputePipeline.cs`, `NamerProcessor.cs`, `NamerAOBakeTests.cs`) is byte-for-byte untouched — its `HasCachedBake`/`RequestBake` three-way gate remains as a tested capability reserved for a future explicit opt-in "Bake AO" action.

## Task Commits

Each task was committed atomically:

1. **Task 1: Remove the silent auto-bake override and dead cache-priming assignments** - `307b305` (fix)
2. **Task 2: Remove the High-res Occluder UI and its backing fields** - `da45cf7` (fix)

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` - Deleted the silent auto-bake trigger, dead bake-priming assignments, and the High-res Occluder UI/backing fields; the AO-slider preview now reshapes the image-space extracted AO live.

## Verification

### Static gates (run via Bash — all passed)

- Task 1 negative checks: no `TriggerAutomaticBake`, `BakeSourceMesh`, or `inspection.OccluderMesh` references remain in the window file.
- Task 1 positive check: `_pipeline.Process(inspection)` still present (line 289).
- Task 2 negative check: no `_occluderMesh`/`_occluderWarning`/`"High-res Occluder"` references remain in the window file.
- Task 2 positive checks: `HasCachedBake` still present in `NamerComputePipeline.cs`; `BakeSourceMesh`/`OccluderMesh` still present in `NamerSourceModel.cs`.
- Both commits touched only the target file (85 deletions total, zero additions); no file deletions; pre-existing unrelated dirty state left untouched.

### Live-editor EditMode test execution — DELEGATED TO THE ORCHESTRATOR

Per the plan's verification constraints, batch-mode Unity test runs are blocked by the interactive editor's project lock and were not attempted. The orchestrator runs the full EditMode suite in the live editor (`NamerAOBakeTests`, `NamerAOControlsTests`, `NamerAfterPanelStateTests`) after both tasks complete and resumes the executor if anything fails. Expected: bake/pipeline routing tests and compute remap/prefs tests keep passing; the window-level deletion is behavior-neutral to `NamerAfterPanelStateTests`.

## Decisions Made

- Deleted the auto-bake trigger and occluder UI entirely (pure deletion) rather than gating them behind a flag — the plan specified removal, and the bake subsystem stays intact for a future explicit opt-in action.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- The window's AO preview now matches what `NamerProcessor.Process` writes to disk (image-space extracted AO), satisfying the plan's three `must_haves` truths.
- A future explicit opt-in "Bake AO" action can reuse the untouched `NamerAOPipeline`/`NamerComputePipeline` bake capability.

---
*Phase: quick-n6x-remove-silent-geometry-bake-override*
*Completed: 2026-08-29*

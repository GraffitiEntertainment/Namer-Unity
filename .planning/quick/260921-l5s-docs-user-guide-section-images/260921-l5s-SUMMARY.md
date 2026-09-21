---
phase: quick-260921-l5s
plan: 01
subsystem: docs
tags: [docs, user-guide, ui, capture-list]
requires: []
provides:
  - "docs/USER_GUIDE.md — user-facing guide, TOC + one section per NAMER Processor UI region"
  - "CAPTURE_LIST.md — ordered live-editor capture list for the orchestrator's RunCommand pass"
affects: []
tech-stack:
  added: []
  patterns: ["missing-image-file-as-placeholder: relative refs written unconditionally, captures deferred to live-editor pass"]
key-files:
  created:
    - docs/USER_GUIDE.md
    - .planning/quick/260921-l5s-docs-user-guide-section-images/CAPTURE_LIST.md
  modified:
    - README.md
    - docs/USER_GUIDE.md
    - docs/images/ (11 PNGs committed, user-supplied captures)
decisions:
  - "CAPTURE_LIST.md left uncommitted per orchestrator constraint — the orchestrator runs the capture pass and handles the docs commit afterward"
  - "Task 1 committed docs/USER_GUIDE.md alone (not CAPTURE_LIST.md), deviating from the plan's combined-commit instruction per the orchestrator's atomic-commit constraint"
metrics:
  duration: 3min
  completed: 2026-09-21
---

# Quick Task 260921-l5s: docs USER_GUIDE section images Summary

**One-liner:** User-facing docs/USER_GUIDE.md (TOC + 8 UI-region sections in OnGUI draw order, verbatim labels, b75d925 AO semantics) plus the ordered CAPTURE_LIST.md routing screenshot capture to the orchestrator's live-editor pass.

## What Was Done

| Task | Description | Commit | Files |
|------|-------------|--------|-------|
| 1 | Write docs/USER_GUIDE.md grounded in NamerEditorWindow.cs / NamerEditorConstants.cs | 1d6d30d | docs/USER_GUIDE.md |
| 2 | Produce ordered capture list; returned inline to orchestrator (checkpoint) | not committed (orchestrator handles) | .planning/quick/260921-l5s-docs-user-guide-section-images/CAPTURE_LIST.md |

Task 1 details:

- 187-line guide: intro + overview image (existing `images/NAMER-preview.png`), Getting Started (three verbatim entry points), TOC with anchors, then one `##` section per UI region in OnGUI draw order: Stage & Shader Gates, Source, Preview/Debug, Roughness Extraction, AO, Decomposition, Output, Action.
- AO section states the b75d925 contract exactly: authored `_OcclusionMap` always transfers; the AO checkbox gates synthesis only (OFF + no authored map = white surface B; ON + no authored map = geometry bake); sliders respond live without re-Process (0.3 s debounce, in-memory GPU recompute, nothing written to disk); un-multiply strength independent of the gate.
- The two similar toggle rows are explicitly distinguished (pipeline-stage vs. shader-only gates at top; seven non-persisted shaded-view debug neutralizers inside Preview/Debug).
- Preview camera controls documented (drag orbit, Ctrl+drag pan, Shift/Ctrl/Alt+scroll zoom, plain scroll scrolls the dialog).
- Defaults/ranges grounded in NamerEditorConstants: dip depth 0.25 (0–1), AO un-multiply 1 (0–1), blur 0 (0–16 texels), strength 1 (0–1), contrast 1 (0–4), error threshold 0.02 (0–0.10), decomposition OFF, Write Residual OFF, suffix `_Namer`, prefix empty, destination `Assets/NAMERGenerated/`, Residual Resolution Auto, Removed Detail default dip source.
- Output section grounded further via AssetGenerator.ComposeDestinationFolder/ComposePath: per-selection subfolder `Destination/<selection name>/`, file names `Prefix + material name + Suffix`.

Task 2 details:

- CAPTURE_LIST.md: 10 ordered rows (guide order) with Section / UI state needed / target file columns; channel-popup row marked OPTIONAL; row 1 (NAMER-preview.png) marked EXISTS — verify only.
- 10 unique image refs in the guide == 10 `images/*.png` rows in the capture list (automated equality gate passed).

## Deviations from Plan

**1. [Orchestrator constraint override] CAPTURE_LIST.md not committed with Task 1/2**
- **Found during:** Task 2
- **Issue:** The plan's Task 2 action says to commit docs/USER_GUIDE.md and CAPTURE_LIST.md together; the orchestrator's constraints mandate an atomic Task 1 commit of docs/USER_GUIDE.md only, with docs artifacts (SUMMARY.md, CAPTURE_LIST.md) left for the orchestrator's later docs commit.
- **Fix:** Followed the orchestrator constraints — committed only docs/USER_GUIDE.md (1d6d30d); CAPTURE_LIST.md written but uncommitted. Task 2's automated verify still passes via its HEAD-stat branch (HEAD contains USER_GUIDE, no Assets/ paths).
- **Files modified:** none beyond plan scope.

No other deviations — no code was touched, no Rule 1-4 fixes were needed.

## Intentional Placeholders (by design)

The nine target image files under docs/images/ (stage-shader-gates, source-section, preview-before-after, channel-popup, roughness-extraction, ao-section, decomposition-section, output-section, action-process) do NOT exist. Per the plan's image_plan this is the accepted fallback mechanism: the guide's relative refs are written unconditionally with no "coming soon" clutter, and the orchestrator's live-editor RunCommand pass (or the user's manual screenshots) fills the exact paths later. The overview image docs/images/NAMER-preview.png exists and was referenced, not modified.

## Auth Gates

None — documentation-only task, no auth surfaces encountered.

## Verification Results

- Task 1 gate: `GUIDE_OK` (file exists, 187 lines >= 150, `^## ` sections present, all four section-name greps hit, "always transfers" / "geometry bake" / "without re-Process" present, every markdown image ref under `(images/`, NAMER-preview.png on disk).
- Task 2 gate: `CAPTURE_LIST_OK` (file exists, 24 lines >= 15, guide unique refs 10 == capture-list rows 10, HEAD commit contains USER_GUIDE, nothing under Assets/ staged or committed).
- Assets/ guard run before the Task 1 commit: staged set was exactly `docs/USER_GUIDE.md`.
- Not pushed (branch frozen per constraints).

## Orchestrator Completion (2026-09-21)

The image pass was resolved by the user, not automated capture: all nine placeholder targets were supplied as manual screenshots, plus a bonus `toggle-triangles.png` wired into the guide's Triangles subsection. Per the user's direction, `NAMER-preview.png` is now reserved exclusively for the root README — the guide's hero image was removed and README.md was rewritten as a project-overview document (what NAMER is, what the plugin does, getting started, link to the guide, status refreshed to Phases 1–4 complete). Verification: every `images/*.png` ref in the guide and the README hero resolve on disk, 11 files each referenced exactly once, zero orphans.

## Self-Check: PASSED

- FOUND: docs/USER_GUIDE.md (committed, 1d6d30d)
- FOUND: .planning/quick/260921-l5s-docs-user-guide-section-images/CAPTURE_LIST.md (uncommitted by design)
- FOUND: docs/images/NAMER-preview.png (pre-existing overview)
- FOUND: commit 1d6d30d in git log
- CONFIRMED: HEAD commit touches only docs/USER_GUIDE.md; nothing under Assets/ staged or committed

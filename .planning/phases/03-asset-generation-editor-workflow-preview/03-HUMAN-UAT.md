---
status: partial
phase: 03-asset-generation-editor-workflow-preview
source: [03-VERIFICATION.md]
started: 2026-08-27
updated: 2026-08-27
---

## Current Test

[awaiting human testing]

## Tests

### 1. Interactive window flow (preview + debug channels + AO recompute)

Open `Tools > NAMER > Processor`, select a textured FBX or material, drag the AO un-multiply slider, orbit/zoom the preview, and click through all 7 toolbar modes.

expected: Before pane shows the source material; after pane shows NAMER (Shaded) or the clicked debug channel immediately (CR-02); no magenta panes; preview updates after ~300 ms without any asset written to disk.
result: [pending]

### 2. End-to-end Process from the live window

Press `Process with NAMER` in the window on a real textured FBX; inspect the Project window; re-run with Overwrite generated enabled.

expected: Material + `_Base.png` + `_Surface.png` under `NAMERGenerated/{source}/` labeled `NamerGenerated`; status reads "Generated 1 material(s) under Assets/NAMERGenerated/"; source shows no modification; stamped re-run replaces cleanly.
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps

None. All 19 automated must-haves verified (5/5 roadmap success criteria + 14/14 plan truths); EditMode 53/53 and PlayMode 3/3 at HEAD with zero skips; all 8 review fixes (2 Critical + 6 Warning) confirmed in code. The two pending items above are live-editor interaction checks that headless tests cannot observe — run `/gsd:verify-work 3` to close them.

---
status: partial
phase: 02-source-inspection-gpu-compute-pipeline
source: [02-VERIFICATION.md]
started: 2026-08-27T18:43:05Z
updated: 2026-08-27T18:43:05Z
---

## Current Test

[awaiting human testing]

## Tests

### 1. Inspect Selection menu (live editor)
expected: With the interactive Unity editor open, select a material, a GameObject with a renderer, or a folder in the Project window, then run `Tools > NAMER > Inspect Selection`. The Console prints the `[NAMER]` inspection report — found maps, scalars, fallbacks, and warnings per material. All 12 automated tests call `Inspect()` directly and never exercise this menu wrapper (`SourceInspector.cs:22-34`, decision D-01's manual-validation surface).
result: [pending]

### 2. FBX/model asset selection
expected: Select an imported FBX/model asset in the Project window and run the inspection (menu or `Inspect`). Its embedded sub-asset materials are resolved and inspected via the `AddSubAssetMaterials` path (`SourceInspector.cs:178-188`). The other four selection kinds (material / scene GameObject / prefab asset / folder) are test-covered; this path needs a committed imported model fixture, which the repo does not yet have.
result: [pending]

## Summary

total: 2
passed: 0
issues: 0
pending: 2
skipped: 0
blocked: 0

## Gaps

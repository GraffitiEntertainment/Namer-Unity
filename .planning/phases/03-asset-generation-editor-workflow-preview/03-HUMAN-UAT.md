---
status: complete
phase: 03-asset-generation-editor-workflow-preview
source: [03-VERIFICATION.md]
started: 2026-08-27
updated: 2026-08-27
---

## Current Test

[testing complete]

## Tests

### 1. Interactive window flow (preview + debug channels + AO recompute)

Open `Tools > NAMER > Processor`, select a textured FBX or material, drag the AO un-multiply slider, orbit/zoom the preview, and click through all 7 toolbar modes.

expected: Before pane shows the source material; after pane shows NAMER (Shaded) or the clicked debug channel immediately (CR-02); no magenta panes; preview updates after ~300 ms without any asset written to disk.
result: issue
reported: "If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset. Also when I rotate the images, the before and after should move or be in the scene to rotate with the object. And I don't see a AO recompute button? Plus 6 signed/unsigned mismatch shader warnings from NAMERPack.compute (lines 80, 92) on metal."
severity: blocker

### 2. End-to-end Process from the live window

Press `Process with NAMER` in the window on a real textured FBX; inspect the Project window; re-run with Overwrite generated enabled.

expected: Material + `_Base.png` + `_Surface.png` under `NAMERGenerated/{source}/` labeled `NamerGenerated`; status reads "Generated 1 material(s) under Assets/NAMERGenerated/"; source shows no modification; stamped re-run replaces cleanly.
result: issue
reported: "re-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png"
severity: major

## Summary

total: 2
passed: 0
issues: 2
pending: 0
skipped: 0
blocked: 0

## Gaps

<!-- YAML format for plan-phase --gaps consumption -->
- truth: "Dragging the AO un-multiply slider recomputes the preview in ~300 ms in memory, with no asset written to disk and no error"
  status: failed
  reason: "User reported: 'If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset' — the slider recompute path hits the generator's overwrite gate instead of staying preview-only (D-10)"
  severity: blocker
  test: 1
  root_cause: ""
  artifacts: []
  missing: []
  debug_session: ""
- truth: "Orbit interaction matches the standard Unity object-preview expectation (drag rotates the object/scene; before/after panes track it)"
  status: failed
  reason: "User reported: 'when I rotate the images, the before and after should move or be in the scene to rotate with the object'"
  severity: minor
  test: 1
  root_cause: ""
  artifacts: []
  missing: []
  debug_session: ""
- truth: "The AO recompute path is discoverable in the window UI (automatic ~300 ms debounced recompute is apparent, or an explicit affordance exists)"
  status: failed
  reason: "User reported: 'I don't see a AO recompute button?'"
  severity: minor
  test: 1
  root_cause: ""
  artifacts: []
  missing: []
  debug_session: ""
- truth: "Re-running Process with Overwrite generated enabled replaces the assets the first run itself generated (NamerGenerated-stamped)"
  status: failed
  reason: "User reported: 're-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png' — same gate message as the AO-slider error, suggesting the stamp applied at generation is not detected on the next run"
  severity: major
  test: 2
  root_cause: ""
  artifacts: []
  missing: []
  debug_session: ""
- truth: "NAMERPack.compute compiles without warnings on Metal"
  status: failed
  reason: "User reported 6 shader warnings: 'Shader warning in NAMERPack: signed/unsigned mismatch, unsigned assumed' at NAMERPack.compute(80) and (92) in kernels CSNormalize, CSOctahedralEncode, CSSurfacePack (on metal)"
  severity: minor
  test: 1
  root_cause: ""
  artifacts: []
  missing: []
  debug_session: ""

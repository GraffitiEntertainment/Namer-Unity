---
status: diagnosed
phase: 03-asset-generation-editor-workflow-preview
source: [03-VERIFICATION.md]
started: 2026-08-27
updated: 2026-08-28
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
  status: diagnosed
  reason: "User reported: 'If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset' — the slider recompute path hits the generator's overwrite gate instead of staying preview-only (D-10)"
  severity: blocker
  test: 1
  root_cause: "The slider path never touches the gate — D-10 holds in code (slider → Tick debounce → RecomputePreview → NamerComputePipeline.Process only; zero AssetDatabase/File write calls on the path, verified by full call-graph trace). The error came from a separate 'Process with NAMER' button press (stack in the user's console proves it: NamerProcessor.Process ← RunProcess ← DrawActionSection) made during slider work because the debounced in-memory recompute has no visible affordance (gap 3); that press was refused by EnsureWritableTarget because the session's earlier successful run had already written Assets/NAMERGenerated/Neo-T-Pose/ and OverwriteGenerated defaults to false, with a message that mislabels stamped assets as 'non-generated' (gap 5 root cause)"
  artifacts: [".planning/debug/ao-slider-overwrite-gate.md", ".planning/debug/ao-recompute-affordance.md", ".planning/debug/rerun-overwrite-stamp.md"]
  missing: []
  debug_session: "ao-slider-overwrite-gate"
- truth: "Orbit interaction matches the standard Unity object-preview expectation (drag rotates the object/scene; before/after panes track it)"
  status: diagnosed
  reason: "User reported: 'when I rotate the images, the before and after should move or be in the scene to rotate with the object'"
  severity: minor
  test: 1
  root_cause: "Rotation-ownership mismatch in NamerPreviewRenderer: orbit state drives the CAMERA around the world-origin pivot (ApplyCamera: position = rotation * (0,0,-distance)) while both mesh instances are drawn with Quaternion.identity at fixed ±0.7 translations — a drag swings the fixed-orientation pair through the frame (objects arc across panes without rotating) instead of spinning each object in place as Unity's Inspector preview does. The mouse-event chain (hit-test → Orbit → Use → Repaint, NamerEditorWindow:418-438) is fully functional; the camera-orbit model was a deliberate-but-mismatched design choice documented in the class header"
  artifacts: [".planning/debug/preview-orbit-expectation.md"]
  missing: []
  debug_session: "preview-orbit-expectation"
- truth: "The AO recompute path is discoverable in the window UI (automatic ~300 ms debounced recompute is apparent, or an explicit affordance exists)"
  status: diagnosed
  reason: "User reported: 'I don't see a AO recompute button?'"
  severity: minor
  test: 1
  root_cause: "The 300 ms debounce works (Tick wired to EditorApplication.update; DebounceSeconds = 0.3) but the UI communicates nothing: the slider is a plain label with no tooltip, successful recompute silently clears _status, and _recomputing is set/cleared synchronously around the compute call so the disabled-Process cue never renders — the recompute is functionally happening but completely invisible, which steered the user to the disk-writing Process button (gap 1's misattribution)"
  artifacts: [".planning/debug/ao-recompute-affordance.md"]
  missing: []
  debug_session: "ao-recompute-affordance"
- truth: "Re-running Process with Overwrite generated enabled replaces the assets the first run itself generated (NamerGenerated-stamped)"
  status: diagnosed
  reason: "User reported: 're-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png' — same gate message as the AO-slider error, suggesting the stamp applied at generation is not detected on the next run"
  severity: major
  test: 2
  root_cause: "NOT a stamp-detection failure: on-disk .meta files from the user's run all carry 'labels: [NamerGenerated]' — run 1's Stamp() persisted, so stamped == true at re-run. The refusal is the OTHER arm of EnsureWritableTarget's '!stamped || !overwriteGenerated': the re-run executed with the Overwrite toggle off (EditorPrefs default false; nothing near the error names the toggle), and the gate's single conflated message then asserted the stamped asset was 'non-generated' — a false explanation the user (correctly seeing their assets stamped) reported as 'stamp not detected'. A test coverage hole let it ship: OverwriteGating_RefusesNonStampedTarget manually re-stamps the target via AssetDatabase.SetLabels, so 'first run's own stamp round-trips into a successful overwrite re-run' was never asserted"
  artifacts: [".planning/debug/rerun-overwrite-stamp.md", ".planning/debug/ao-slider-overwrite-gate.md"]
  missing: []
  debug_session: "rerun-overwrite-stamp"
- truth: "NAMERPack.compute compiles without warnings on Metal"
  status: diagnosed
  reason: "User reported 6 shader warnings: 'Shader warning in NAMERPack: signed/unsigned mismatch, unsigned assumed' at NAMERPack.compute(80) and (92) in kernels CSNormalize, CSOctahedralEncode, CSSurfacePack (on metal)"
  severity: minor
  test: 1
  root_cause: "NAMERPack.compute declares 'int2 _Size' (line 43) but each kernel's bounds guard compares its members against the unsigned 'uint3 id : SV_DispatchThreadID' components — 'if (id.x >= _Size.x || id.y >= _Size.y)' at lines 48/80/92; 2 comparison sites × 3 kernels = 6 Metal 'signed/unsigned mismatch, unsigned assumed' warnings. Behavior unaffected (C# uploads positive w/h via SetInts; all 53 tests green) — pure console noise that fires on every preview recompute, reinforcing gap 1's slider-to-error misattribution"
  artifacts: [".planning/debug/namerpack-signed-unsigned.md"]
  missing: []
  debug_session: "namerpack-signed-unsigned"

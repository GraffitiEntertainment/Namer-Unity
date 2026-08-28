---
status: diagnosed
trigger: "UAT Phase 3, Test 1, gap 2: 'when I rotate the images, the before and after should move or be in the scene to rotate with the object' - orbit does not match the standard Unity object-preview expectation"
created: 2026-08-27T00:00:00Z
updated: 2026-08-28T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED (design-semantics variant) - The mouse-event chain is sound; the mismatch is the rotation-ownership model. NamerPreviewRenderer puts ALL orbit rotation in the camera (which orbits the world-origin pivot) and draws both mesh instances with `Quaternion.identity` at fixed translations, so a drag swings the fixed-orientation pair through the frame - each pane's object translates along an arc without ever changing orientation - instead of spinning each object in place like the Unity Inspector preview the user expects.
test: Trace the window mouse path (hit-test -> Orbit -> Use -> Repaint), then the renderer transform math (ApplyCamera rotation vs DrawMesh quaternions).
expecting: If the event chain were broken, a dead link (rect mismatch / consumed event / missing repaint); if sound, the visible behavior difference must come from the camera-vs-mesh rotation split.
next_action: Root cause found - return diagnosis (find_root_cause_only). No fix applied.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: Orbit interaction matches the standard Unity object-preview expectation (drag rotates the object/scene; before/after panes track it).
actual: "when I rotate the images, the before and after should move or be in the scene to rotate with the object"
errors: None reported.
reproduction: Test 1 in UAT - open Tools > NAMER > Processor with a mesh selected, drag on the before/after preview.
started: Discovered during UAT after Phase 3 completion (severity: minor per UAT gap).

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: Drag events never reach Orbit (rect hit-test wrong, event consumed elsewhere, missing repaint)
  evidence: NamerEditorWindow.cs:418-438 - HandlePreviewCameraInput hit-tests the exact previewRect reserved at :374, handles MouseDrag by calling `_preview.Orbit(current.delta.x, current.delta.y)`, calls `current.Use()` and `Repaint()`. No earlier control on the path (LabelFields/BeginHorizontal) consumes drags inside the rect. ScrollWheel -> Zoom is wired identically.
  timestamp: 2026-08-28

- hypothesis: Orbit math is a no-op or clamped away
  evidence: NamerPreviewRenderer.cs:117-122 - Orbit accumulates yaw (unclamped) and clamps pitch to +-89; ApplyCamera (:158-169) recomputes camera position/rotation from that state every call. Any drag changes the camera pose.
  timestamp: 2026-08-28

- hypothesis: The recompute/debounce path swallows repaints after orbit
  evidence: Orbit/Zoom call Repaint() directly and ApplyCamera immediately; Render() re-renders the preview scene each repaint with the current camera. No debounce involved in orbit.
  timestamp: 2026-08-28

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-28
  checked: Who owns rotation in the preview scene (NamerPreviewRenderer.Render :54-81, ApplyCamera :158-169)
  found: Both instances are drawn as `_preview.DrawMesh(mesh, position, Quaternion.identity, material, 0)` at translations `(+/-HalfSeparation, 0, 0) - boundsCenter` (:70-75). The camera is positioned at `rotation * (0,0,-_distance)` with `rotation = Euler(pitch, yaw, 0)` (:165-168) - i.e. the camera orbits the world origin while the meshes never rotate.
  implication: A drag produces a camera fly-around: the pair swings through the frame about the shared pivot (each object traces an arc, orientation constant) rather than the objects spinning in place. The class doc (:15-18) documents this camera-orbit model as deliberate - it was a design choice that mismatches the user's expectation.

- timestamp: 2026-08-28
  checked: What the standard expectation is (Unity Inspector object/material preview)
  found: Unity's built-in previews keep the camera fixed and rotate the object about its own center; drag = object spins in place. The user's phrasing ("should ... rotate with the object") points at exactly this model.
  implication: Fix = invert rotation ownership: camera fixed on its viewing axis, yaw/pitch quaternion applied to both DrawMesh calls so each instance spins about its own (bounds-recentered) center.

- timestamp: 2026-08-28
  checked: Ripple effects of moving rotation from camera to meshes
  found: Frame (:87-111) already resets yaw/pitch and computes distance from `HalfSeparation + radius` - valid unchanged because per-instance spin preserves each instance's bounds footprint. Zoom (:128-132) only scales _distance. Lights (:65-66) are world-fixed; with a fixed camera they behave exactly like the Inspector preview's rig.
  implication: The change is contained to Render's two DrawMesh quaternions + ApplyCamera's fixed pose + doc comments; no API change to Orbit/Zoom/Frame.

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: Rotation-ownership mismatch in NamerPreviewRenderer: orbit state (_yaw/_pitch) drives the CAMERA around the world-origin pivot while both mesh instances are drawn with Quaternion.identity at fixed translations. Dragging therefore flies the camera around a static-orientation pair - the before/after objects arc across their panes without rotating - which reads as "the images don't rotate with the object". The standard expectation (Unity Inspector preview) is the inverse: camera fixed, object spinning in place about its own center. The mouse-event chain (hit-test -> Orbit -> Use -> Repaint) is fully functional; this is a deliberate-but-mismatched design choice documented in the class header, not an input defect.

fix: (for plan-phase --gaps; not applied - find_root_cause_only) In Render, apply `Quaternion.Euler(_pitch, _yaw, 0)` to both DrawMesh calls (positions stay `(+/-HalfSeparation,0,0) - boundsCenter`, so each instance spins about its own bounds-recentered origin). In ApplyCamera, fix the camera pose on its viewing axis: position `(0,0,-_distance)`, rotation identity. Update the Orbit doc and class header to describe object-spin semantics. Frame/Zoom/clamps unchanged; no public API change.

verification: Full static reads of NamerEditorWindow.cs (event path :418-438) and NamerPreviewRenderer.cs (transforms, framing, clamps). No automated pixel assertion (PreviewRenderUtility output comparison is flaky headless); final confirmation is visual re-run of UAT test 1 in the live editor.

files_changed: []

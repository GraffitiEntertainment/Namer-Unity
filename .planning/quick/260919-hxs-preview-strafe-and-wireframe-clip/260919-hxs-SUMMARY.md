---
status: complete
quick_id: 260919-hxs
created: 2026-09-19
fixes: 260919-ge4
---

# Quick Task 260919-hxs: Preview strafe (ctrl+drag pan) + wireframe clip to preview rect Summary

**Adds ctrl+drag strafe/pan to the shared preview orthographic camera (with Frame reset on re-selection) and clips the After-pane triangle wireframe overlay to the preview rect, so zoomed-in left/right extremes of the model can be inspected and the overlay never spills outside the preview area. Three files: `NamerPreviewRenderer.cs` (pan state + camera strafe), `NamerEditorWindow.cs` (ctrl+drag input split, wireframe pan-follow + per-edge clip), `NamerPreviewRendererTests.cs` (new Pan test).**

## What Changed (per task)

### Task 1 — Pan state + camera strafe (`NamerPreviewRenderer.cs`)

- New fields `private float _panX; private float _panY;` next to `_zoomScale`.
- New public API next to `Zoom`: `Pan(float worldX, float worldY)` accumulates the world-space offset, and `PanOffset => new Vector2(_panX, _panY)` exposes it (doc comments per plan).
- `ApplyCamera` camera position is now `new Vector3(_panX, _panY, -OrthoCameraDistance)` — the camera stays on its viewing axis but strafes in its view plane with `Pan` (comment updated; the camera transform moves only under Pan).
- `Frame` resets `_panX = 0f; _panY = 0f;` alongside zoom/yaw/pitch; its doc comment's reset list now includes pan.
- Class doc comment extended minimally: orbit still spins each mesh in place; ctrl+drag strafe (Pan) is the one camera-transform motion, alongside zoom-as-orthographic-size.

### Task 2 — Input: ctrl+drag pans, plain drag orbits (`NamerEditorWindow.cs`)

- `HandlePreviewCameraInput` MouseDrag branch splits on `current.control`: ctrl+drag converts the pixel delta to world units (`worldPerPixel = 2f * orthoSize / Mathf.Max(previewRect.height, 1f)`, with `orthoSize` from `OrthographicSizeForAspect(aspect, previewRect.height)`) and calls `_preview.Pan(-current.delta.x * worldPerPixel, current.delta.y * worldPerPixel)` (GUI y is down); plain drag orbits as before. `current.Use(); Repaint();` shared by both paths. ScrollWheel branch (modifier-gated zoom from 260919-ge4) unchanged.

### Task 3 — Wireframe follows pan + clips to preview rect (`NamerEditorWindow.cs`)

- `DrawTriangleWireframe` reads `Vector2 pan = _preview.PanOffset;` and passes it into all three `ProjectPreviewVertex` calls (new `Vector2 pan` parameter after `position`).
- `ProjectPreviewVertex` subtracts the camera pan before the NDC divide (`(world.x - pan.x) / halfWidthWorld`, `(world.y - pan.y) / orthoSize`) so the overlay moves with the panned view; doc comment updated.
- The three unconditional `Handles.DrawLine` calls replaced with per-edge culling — `if (previewRect.Contains(a) && previewRect.Contains(b)) { Handles.DrawLine(a, b); }` (and b-c, c-a) — a deterministic pure-math clip: interior edges shared with outside triangles still draw, only border-crossing segments vanish. No GUI.BeginGroup/GL viewport tricks (immediate-mode Handles lines are not reliably GUIClip-clipped; GL viewport state inside OnGUI is the 260919-fp3 black-window failure mode).

### Task 4 — Test (`NamerPreviewRendererTests.cs`)

- New `Pan_AccumulatesOffset_ResetsOnFrame` mirroring the neighboring Orbit/Zoom tests: two `Pan` calls accumulate to `(0.5, 3)`, then `Frame(CreateBoundsMesh(1f, 2f, 0.5f))` resets `PanOffset` to `Vector2.zero`.

## Verification

- Full EditMode suite in the live editor (unity-mcp RunCommand + TestRunnerApi, results file `Temp/260919-hxs-results.txt`), run by the coordinator after the edits: **PASS=158 FAIL=0 SKIP=0** — the expected 157 prior tests plus the new Pan test.
- Assembly rebuild verified clean by the coordinator: editor dll contains `PanOffset`, tests dll contains `Pan_AccumulatesOffset_ResetsOnFrame`.
- User UAT per plan: ctrl+drag strafes the zoomed view, plain drag still orbits, modifier+scroll still zooms, wireframe stays inside the preview rect and follows the pan, re-selection re-frames and resets pan.

## Commits

- Fix commit (three files): `e0d95e4` — `feat(quick-hxs): ctrl+drag preview strafe with Frame-reset pan, wireframe follows pan and clips to preview rect`
- Docs commit (this folder + STATE.md row): this commit.

## Files Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs` — pan fields/API, ApplyCamera strafe, Frame reset, doc comments.
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — ctrl+drag input split, wireframe pan parameter + per-edge clip.
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerPreviewRendererTests.cs` — new `Pan_AccumulatesOffset_ResetsOnFrame` (+ one doc-comment clause, below).

## Deviations from Plan

One disclosed micro-touch beyond the literal plan text: the test file's class doc comment said "zoom as orthographic size (camera transform never moves)" — factually false once `Pan` exists and directly contradicted by the new test — so it now reads "zoom as orthographic size, ctrl+drag pan as a camera view-plane strafe" (the same treatment the plan mandated for the renderer's class doc). No code impact. Otherwise none — plan executed exactly as written.

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

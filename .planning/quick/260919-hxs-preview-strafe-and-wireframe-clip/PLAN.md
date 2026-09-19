---
status: complete
quick_id: 260919-hxs
created: 2026-09-19
fixes: 260919-ge4
---

# Quick Task: Preview strafe (ctrl+drag pan) + wireframe clip to preview rect

## Description

Phase 04.2 UAT feedback on the After-pane triangle wireframe overlay and
zoomed-in inspection: the wireframe draws outside the preview rect when
zoomed, and there is no way to pan the view, so the left/right extremes of a
zoomed model cannot be inspected. Adds ctrl+drag strafe/pan to the shared
preview camera and clips the wireframe overlay to the preview rect.

## Tasks

### Task 1 — Pan state + camera strafe (`NamerPreviewRenderer.cs`)

- Fields (next to `_zoomScale`, ~:74): `private float _panX; private float _panY;`
- Public API (next to `Orbit`/`Zoom`):
  ```csharp
  /// <summary>
  /// Strafes the preview view by the given world-space offset (ctrl+drag).
  /// Moves the shared orthographic camera in its view plane; mesh draw
  /// positions and the divider anchor are unchanged.
  /// </summary>
  public void Pan(float worldX, float worldY)
  {
      _panX += worldX;
      _panY += worldY;
  }

  /// <summary>The accumulated world-space pan of the preview camera.</summary>
  public Vector2 PanOffset => new Vector2(_panX, _panY);
  ```
- `ApplyCamera` (~:352): camera position becomes
  `new Vector3(_panX, _panY, -OrthoCameraDistance);` (comment updated: the
  camera stays on its viewing axis but strafes with `Pan`).
- `Frame` (~:233-235): reset `_panX = 0f; _panY = 0f;` alongside zoom/yaw/pitch
  (doc comment notes pan resets too).
- Update the class doc comment's camera wording minimally if it states the
  camera "stays put" unconditionally (orbit still spins the mesh in place;
  pan is the one camera-transform motion, alongside zoom-as-orthographic-size).

### Task 2 — Input: ctrl+drag pans, plain drag orbits (`NamerEditorWindow.cs`)

`HandlePreviewCameraInput` (:1268-1289) — split the MouseDrag branch:

```csharp
if (current.type == EventType.MouseDrag)
{
    if (current.control)
    {
        // Ctrl+drag strafes the shared camera; pixel delta converted to world
        // units at the current orthographic size (GUI y is down).
        float aspect = previewRect.width / Mathf.Max(previewRect.height, 1f);
        float orthoSize = _preview.OrthographicSizeForAspect(aspect, previewRect.height);
        float worldPerPixel = 2f * orthoSize / Mathf.Max(previewRect.height, 1f);
        _preview.Pan(-current.delta.x * worldPerPixel, current.delta.y * worldPerPixel);
    }
    else
    {
        _preview.Orbit(current.delta.x, current.delta.y);
    }

    current.Use();
    Repaint();
}
```

ScrollWheel branch unchanged (modifier-gated zoom from 260919-ge4).

### Task 3 — Wireframe follows pan + clips to preview rect (`NamerEditorWindow.cs`)

- `DrawTriangleWireframe` (:1217-1250): read `Vector2 pan = _preview.PanOffset;`
  and pass it to `ProjectPreviewVertex` (new parameter after `position`).
- `ProjectPreviewVertex` (:1258-1266): subtract the camera pan before the NDC
  divide so the overlay moves with the panned view:
  ```csharp
  float ndcX = (world.x - pan.x) / halfWidthWorld;
  float ndcY = (world.y - pan.y) / orthoSize;
  ```
- Per-edge clip in the triangle loop — replace the three unconditional
  `Handles.DrawLine` calls with edge culling so nothing draws outside the
  preview rect:
  ```csharp
  if (previewRect.Contains(a) && previewRect.Contains(b)) { Handles.DrawLine(a, b); }
  if (previewRect.Contains(b) && previewRect.Contains(c)) { Handles.DrawLine(b, c); }
  if (previewRect.Contains(c) && previewRect.Contains(a)) { Handles.DrawLine(c, a); }
  ```
  Deterministic pure-math clip: interior edges shared with outside triangles
  still draw; only border-crossing segments vanish. (No GUI.BeginGroup/GL
  viewport tricks — immediate-mode Handles lines are not reliably
  GUIClip-clipped, and GL viewport state inside OnGUI is the 260919-fp3
  black-window failure mode.)

## Task 4 — Test (`NamerPreviewRendererTests.cs`)

New test mirroring the `Orbit_AccumulatesYawAndPitch_ClampsPitch_ResetsOnFrame`
style:

```csharp
[Test]
public void Pan_AccumulatesOffset_ResetsOnFrame()
{
    _renderer.Pan(1f, 2f);
    _renderer.Pan(-0.5f, 1f);
    Assert.AreEqual(0.5f, _renderer.PanOffset.x, 1e-4f, "pan x accumulates");
    Assert.AreEqual(3f, _renderer.PanOffset.y, 1e-4f, "pan y accumulates");

    _renderer.Frame(CreateBoundsMesh(1f, 2f, 0.5f));
    Assert.AreEqual(Vector2.zero, _renderer.PanOffset, "Frame resets pan");
}
```

(Adapt to the file's actual helper names; `CreateBoundsMesh(1f, 2f, 0.5f)` is
used by the existing tests.)

## Verification

Full EditMode suite in the live editor via unity-mcp RunCommand +
TestRunnerApi per memory `live-editor-editmode-test-run` (results to a Temp
file marker, NOT console logs; no AssetDatabase.Refresh; internal
`CommandScript : IRunCommand`; no System.Reflection / package namespaces).
Expect **158/158** (157 + the new Pan test). Then user UAT: ctrl+drag strafes
the zoomed view to left/right parts of the model, plain drag still orbits,
modifier+scroll still zooms, wireframe stays inside the preview rect and
follows the pan, re-selection re-frames and resets pan.

## Commits

Authorized by the user ("proceed", same terms as 260919-ge4). Atomic fix
commit (three files), then docs commit (this folder + STATE.md quick-task
row). Staging discipline: never sweep unrelated working-tree changes into
commits (Assets/NAMERGenerated/*, Assets/NAMER/NamerSmoke.unity,
ProjectSettings/*, Packages/.../Tests/Editor/ResidualPipelineTests.cs,
NamerDipSwitchReproTests.cs(+meta), .planning/phases/*,
Namer-Unity.sln.DotSettings.user; untracked .planning/debug/resolved/* not
to be swept).

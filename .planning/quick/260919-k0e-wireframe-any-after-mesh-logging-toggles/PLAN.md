---
status: complete
quick_id: 260919-k0e
created: 2026-09-19
fixes: 260919-irk
---

# Quick Task: Wireframe on any After mesh + exception logging + checkbox order

## Description

Three Phase 04.2 UAT fixes:

1. **Wireframe invisible.** The irk wire second pass is gated on
   `afterMesh == _previewSplitMesh` (NamerEditorWindow.cs:1009), so the wire
   silently does not draw when the After pane shows the source mesh
   (decomposition off) or the generated mesh (`PreferGenerated` after Process).
   The shader itself is proven sound (live probe: found, no errors,
   `isSupported=True`, queue 2100; `ZTest Always`/`ZWrite Off` committed), so
   the gate is the whole bug. Fix: render the wire over whatever mesh the After
   pane actually shows — the split mesh keeps its line submesh; other meshes get
   a cached standalone line-topology wire mesh built from their own vertices
   (imported/generated ASSET meshes are never mutated).
2. **Panel errors are not logged.** `RecomputePreview` (:456-460) and
   `RunProcess` (:1724-1728) catch exceptions and write only
   `"Processing failed: " + ex.Message` into the panel status — no console
   entry, no stack trace (user demanded logging twice). The live
   "RenderTexture has been destroyed" error on model selection needs its stack
   in the console to be root-caused. Fix: `Debug.LogException(ex)` in both
   catches.
3. **Checkbox order.** The five stage-gate rows in `DrawStepSwitches`
   (:836/:848/:859/:870/:882) use `EditorGUILayout.ToggleLeft` (label left,
   checkbox right-aligned). The shaded-view row (`DrawShaderInputToggle`,
   :1061-1074) uses `EditorGUILayout.Toggle` — checkbox before text — which the
   user confirmed is correct. Fix: change the five `ToggleLeft` calls to
   `Toggle`.

## Tasks

### Task 1 — Renderer: wire mesh parameter (`NamerPreviewRenderer.cs`)

- `Render(Mesh beforeMesh, Mesh afterMesh, Material before, Material after,
  Rect rect, Material wireMaterial = null, int wireSubmesh = -1)` (:147-148)
  becomes `..., Mesh wireMesh = null, Material wireMaterial = null, int
  wireSubmesh = -1)`. The before-pane `RenderPane` call (:175) passes
  `null, null, -1`; the after-pane call (:176) passes
  `wireMesh, wireMaterial, wireSubmesh`.
- `RenderPane` (:188-190) gains `Mesh wireMesh` immediately before
  `Material wireMaterial`. The wire draw (:199-205) becomes:
  ```csharp
  if (wireMesh != null && wireMaterial != null && wireSubmesh >= 0)
  {
      // Wireframe second pass: the split mesh's own line-topology submesh or a
      // standalone wire mesh built from the after mesh — same transform, unlit
      // wire material. GPU viewport clipping cuts edges per pixel at the pane
      // edge, and pan/zoom/orbit follow the camera transform for free.
      _preview.DrawMesh(wireMesh, position, meshRotation, wireMaterial, wireSubmesh);
  }
  ```
- Update the `Render` (:129-146) and `RenderPane` (:180-187) doc comments: the
  wire may be the pane mesh itself (split-mesh line submesh) OR a standalone
  cached wire mesh built from the after mesh (source/generated assets are never
  mutated).

### Task 2 — Window: wire mesh for every After-mesh state
(`NamerEditorWindow.cs`)

- Fields next to `_wireMaterial` (:100):
  ```csharp
  private Mesh _previewWireSource;
  private Mesh _previewWireMesh;
  ```
  Single-entry cache: at most one non-split after mesh needs a wire mesh at a
  time (decomp off → source mesh; decomp on + PreferGenerated → generated mesh;
  otherwise the split mesh, which uses its own submesh), so one cached pair is
  sufficient and no `Dictionary`/`using System.Collections.Generic` is needed.
- New methods next to `EnsureWireMaterial` (:1163-1177):
  ```csharp
  /// <summary>
  /// Builds (and caches) a standalone hidden line-topology mesh carrying every
  /// triangle edge of <paramref name="source"/> — the wireframe second pass for
  /// After-pane states that show the source or generated mesh (they carry no
  /// wire submesh and are never mutated). Cached per source instance so
  /// repaints do not rebuild it; rebuilt when the after mesh changes; disposed
  /// in OnDisable.
  /// </summary>
  private Mesh GetOrCreatePreviewWireMesh(Mesh source)
  {
      if (source == null)
      {
          return null;
      }

      if (_previewWireMesh != null && _previewWireSource == source)
      {
          return _previewWireMesh;
      }

      if (_previewWireMesh != null)
      {
          DestroyImmediate(_previewWireMesh);
      }

      Mesh wire = new Mesh { hideFlags = HideFlags.HideAndDontSave };
      if (source.indexFormat == UnityEngine.Rendering.IndexFormat.UInt32)
      {
          wire.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
      }

      wire.SetVertices(source.vertices);
      wire.subMeshCount = 1;
      wire.SetIndices(BuildWireEdgeIndices(source), MeshTopology.Lines, 0);
      wire.RecalculateBounds();
      _previewWireSource = source;
      _previewWireMesh = wire;
      return wire;
  }
  ```
  Plus a `Mesh` overload of the existing edge builder (C# overloads the
  `NamerSplitResult` version at :771; identical fill pattern, reading
  `mesh.GetTriangles(i)` per submesh and `(int)mesh.GetIndexCount(i)` for the
  total):
  ```csharp
  /// <summary>
  /// Line-list indices for a wire mesh built from <paramref name="mesh"/>: every
  /// triangle's three edges — (a,b), (b,c), (c,a) — across all submeshes, sharing
  /// the source vertex positions. No edge deduplication (interior shared edges
  /// overdraw the same wire color, which is invisible).
  /// </summary>
  private static int[] BuildWireEdgeIndices(Mesh mesh)
  {
      int totalTriangles = 0;
      for (int i = 0; i < mesh.subMeshCount; i++)
      {
          totalTriangles += (int)mesh.GetIndexCount(i) / 3;
      }

      int[] edges = new int[totalTriangles * 6];
      int write = 0;
      for (int i = 0; i < mesh.subMeshCount; i++)
      {
          int[] triangles = mesh.GetTriangles(i);
          for (int t = 0; t + 2 < triangles.Length; t += 3)
          {
              edges[write++] = triangles[t];
              edges[write++] = triangles[t + 1];
              edges[write++] = triangles[t + 1];
              edges[write++] = triangles[t + 2];
              edges[write++] = triangles[t + 2];
              edges[write++] = triangles[t];
          }
      }

      return edges;
  }
  ```
- Replace the wire gate (:1003-1016) — the comment AND the gate change; the
  split-mesh submesh path is kept:
  ```csharp
  // Wireframe second pass (04.2): renders over whatever mesh the After pane
  // shows — the split mesh's own line-topology submesh, or a cached standalone
  // wire mesh built from the source/generated mesh (assets are never mutated).
  Mesh wireMesh = null;
  Material wireMaterial = null;
  int wireSubmesh = -1;
  if (_showTriangles && afterMesh != null)
  {
      EnsureWireMaterial();
      wireMaterial = _wireMaterial;
      if (afterMesh == _previewSplitMesh)
      {
          wireMesh = afterMesh;
          wireSubmesh = afterMesh.subMeshCount - 1;
      }
      else
      {
          wireMesh = GetOrCreatePreviewWireMesh(afterMesh);
          wireSubmesh = 0;
      }
  }

  PreviewRenderResult previewResult = _preview.Render(_previewMesh, afterMesh, beforeMaterial, afterMaterial, previewRect, wireMesh, wireMaterial, wireSubmesh);
  ```
- `OnDisable` disposal next to the `_wireMaterial` block (:239-242):
  ```csharp
  if (_previewWireMesh != null)
  {
      DestroyImmediate(_previewWireMesh);
      _previewWireMesh = null;
      _previewWireSource = null;
  }
  ```

### Task 3 — Window: log caught exceptions (`NamerEditorWindow.cs`)

Insert `Debug.LogException(ex);` as the first statement of both catches so the
full stack lands in the console/Editor.log (the panel keeps showing only the
message):
- `RecomputePreview` catch (:456-460)
- `RunProcess` catch (:1724-1728)

```csharp
catch (Exception ex)
{
    Debug.LogException(ex);
    _status = "Processing failed: " + ex.Message;
    _statusIsError = true;
}
```

### Task 4 — Window: checkbox before text (`NamerEditorWindow.cs`)

Change the five `EditorGUILayout.ToggleLeft(` calls in `DrawStepSwitches`
(:836, :848, :859, :870, :882) to `EditorGUILayout.Toggle(` — method name
only; arguments and style unchanged (`EditorGUILayout.Toggle` draws
checkbox-then-label, matching the correct shaded-view row).

## Verification

Full EditMode suite in the live editor via unity-mcp RunCommand +
TestRunnerApi per memory `live-editor-editmode-test-run` (results to a Temp
file marker, NOT console logs; no AssetDatabase.Refresh; internal
`CommandScript : IRunCommand`; no System.Reflection / package namespaces;
`ICallbacks.TestStarted` takes `ITestAdaptor`). Expect **158/158** (no test
changes — optional parameters keep existing `Render` call sites compiling).
Compile proof: new `GetOrCreatePreviewWireMesh` symbol present in the rebuilt
editor dll. Then user UAT: wireframe visible in ALL three After states
(decomp off, split preview, generated mesh), cyan edges pixel-clipped at the
pane edge following pan/zoom/orbit; model-selection RT error prints a full
stack trace to the console; the five stage-gate checkboxes draw before their
labels.

## Commits

Authorized by the user ("proceed with all 3" after the consolidated proposal;
same terms as 260919-ge4/hxs/irk). Atomic fix commit
(`NamerPreviewRenderer.cs` + `NamerEditorWindow.cs`), then docs commit (this
folder + STATE.md quick-task row). Staging discipline: never sweep unrelated
working-tree changes into commits (Assets/NAMERGenerated/*,
Assets/NAMER/NamerSmoke.unity, ProjectSettings/*,
Packages/.../Tests/Editor/ResidualPipelineTests.cs,
NamerDipSwitchReproTests.cs(+meta), .planning/phases/*,
Namer-Unity.sln.DotSettings.user; untracked `.planning/debug/resolved/*` also
not to be swept).

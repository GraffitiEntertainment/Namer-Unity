---
status: complete
quick_id: 260919-irk
created: 2026-09-19
fixes: 260919-hxs
---

# Quick Task: Triangle wireframe as a second render pass (replace IMGUI overlay)

## Description

Phase 04.2 UAT: the After-pane triangle wireframe is a CPU-side IMGUI overlay
(`Handles.DrawLine` projected in C# by `DrawTriangleWireframe` /
`ProjectPreviewVertex`), not a render pass. Consequences: per-edge
both-endpoints containment culling drops whole edges/triangles at the pane
boundary instead of clipping per pixel; the overlay bleeds across the pane
midline into the Before pane; and up to 3N `Handles.DrawLine` calls (one
`Material.SetPass` + GL block each) exhaust the editor graphics ring buffer on
dense meshes.

Fix: render the triangle edges through the preview camera as a second pass
into the same After-pane render target — one line-topology submesh on the
preview split mesh drawn with an unlit wire material in the After pane's
`BeginPreview → DrawMesh → Render(true)` cycle. GPU viewport clipping then
clips edges per pixel, pan/zoom/orbit follow the camera for free, and the
whole overlay path (manual NDC projection, pan subtraction, containment
culling) is deleted.

Metal has no geometry shaders and Unity does not expose `SV_Barycentrics`,
so edge selection lives in a line-list index buffer (no vertex duplication,
shared vertex buffer) rather than in-shader barycentrics.

## Tasks

### Task 1 — Wire shader (`Shaders/NamerPreviewWire.shader`, NEW)

Minimal URP unlit color shader, sibling of `NamerChannelView.shader`,
mirroring the package's shader conventions:

- `Shader "GraffitiEntertainment.Namer/NamerPreviewWire"`
- Properties: `_WireColor ("Wire Color", Color) = (0, 1, 1, 1)`
- SubShader Tags: `"RenderType" = "Opaque"`,
  `"RenderPipeline" = "UniversalPipeline"`, `"Queue" = "Geometry+100"`
  (drawn after the opaque preview mesh so the X-ray look is preserved).
- Single Pass with `ZWrite Off`, `ZTest Always`, HLSL vert/frag using
  `Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl`,
  `CBUFFER_START(UnityPerMaterial) half4 _WireColor; CBUFFER_END`
  (SRP-batcher compatible), `TransformObjectToHClip` vertex transform,
  fragment returns `_WireColor`. No lighting, no texturing.

### Task 2 — Renderer second pass (`NamerPreviewRenderer.cs`)

- `Render(Mesh beforeMesh, Mesh afterMesh, Material before, Material after,
  Rect rect)` (:143) gains two optional parameters:
  `Material wireMaterial = null, int wireSubmesh = -1`. Passed through to the
  AFTER-pane `RenderPane` call only; the Before pane calls with defaults.
- `RenderPane` (:175) gains `Material wireMaterial, int wireSubmesh`
  parameters; after the existing `_preview.DrawMesh(mesh, position,
  meshRotation, material, 0);` add:
  ```csharp
  if (wireMaterial != null && wireSubmesh >= 0)
  {
      // Wireframe second pass: same mesh, same transform, line-topology
      // submesh — GPU viewport clipping cuts edges per pixel at the pane
      // edge, and pan/zoom/orbit follow the camera transform for free.
      _preview.DrawMesh(mesh, position, meshRotation, wireMaterial, wireSubmesh);
  }
  ```
- Update `Render`/`RenderPane` doc comments: wire pass description replaces
  nothing (new capability), framing/rotation/zoom semantics unchanged.

### Task 3 — Window: wire submesh, material, call site, deletion
(`NamerEditorWindow.cs`)

- `BuildPreviewSplitMesh` (:713-741): after the existing submesh loop, append
  ONE line-topology submesh carrying every triangle's three edges:
  ```csharp
  int wireSubmesh = split.SubMeshTriangles.Length;
  mesh.subMeshCount = wireSubmesh + 1;
  mesh.SetIndices(BuildWireEdgeIndices(split), MeshTopology.Lines, wireSubmesh);
  ```
  New `private static int[] BuildWireEdgeIndices(NamerSplitResult split)`:
  total triangle count across `split.SubMeshTriangles` (verify the actual
  element type — `SetTriangles` accepts it), allocate `int[totalTris * 6]`,
  fill `(a,b) (b,c) (c,a)` per triangle across all submeshes. NO edge
  deduplication (interior shared edges overdraw the same color — invisible).
- Wire material field `private Material _wireMaterial;` + shader-property id
  const for `_WireColor` (same pattern as `SurfaceMapId`). `private void
  EnsureWireMaterial()` mirroring the channel-material creation (:1080-1094):
  `Shader.Find("GraffitiEntertainment.Namer/NamerPreviewWire")`,
  `new Material(shader) { hideFlags = HideFlags.HideAndDontSave }`,
  set `_WireColor` from the existing `TriangleWireframeColor` (:78).
  Dispose in `OnDisable` next to the channel-material disposal.
- Preview call site (:954): replace with
  ```csharp
  Material wireMaterial = null;
  int wireSubmesh = -1;
  if (_showTriangles && afterMesh == _previewSplitMesh)
  {
      EnsureWireMaterial();
      wireMaterial = _wireMaterial;
      wireSubmesh = afterMesh.subMeshCount - 1;
  }
  PreviewRenderResult previewResult = _preview.Render(_previewMesh, afterMesh,
      beforeMaterial, afterMaterial, previewRect, wireMaterial, wireSubmesh);
  ```
  (Scope per the original spec: the wire renders for the in-memory split
  mesh; when the After pane falls back to the source/generated mesh the
  toggle is a no-op. Imported/generated ASSET meshes are never mutated.)
  Delete the `DrawTriangleWireframe(previewRect, afterMesh);` call (:959).
- DELETE `DrawTriangleWireframe` (:1208-1266, including its doc comment) and
  `ProjectPreviewVertex` (:1268-1282, including its doc comment). Leave the
  `Handles` using and the renderer's `PanOffset` property (pinned by the
  `Pan_AccumulatesOffset_ResetsOnFrame` test) alone.
- Update the Triangles toggle tooltip (:983-984): "Renders the split mesh's
  triangle edges as a second pass in the After preview (visual debug only)."

## Verification

Full EditMode suite in the live editor via unity-mcp RunCommand +
TestRunnerApi per memory `live-editor-editmode-test-run` (results to a Temp
file marker, NOT console logs; no AssetDatabase.Refresh; internal
`CommandScript : IRunCommand`; no System.Reflection / package namespaces).
Expect **158/158** (no test changes). Then user UAT: wireframe stays inside
the After pane half only, pixel-clipped at the pane edge with strafe/zoom
(no whole-triangle drops, no left-pane bleed), follows orbit/pan/zoom, and
dense meshes no longer flood the editor graphics ring buffer.

## Commits

Authorized by the user ("confirm" after the second-pass proposal; same terms
as 260919-ge4/hxs). Atomic fix commit (shader + renderer + window + the
shader's Unity-generated .meta), then docs commit (this folder + STATE.md
quick-task row). Staging discipline: never sweep unrelated working-tree
changes into commits (Assets/NAMERGenerated/*, Assets/NAMER/NamerSmoke.unity,
ProjectSettings/*, Packages/.../Tests/Editor/ResidualPipelineTests.cs,
NamerDipSwitchReproTests.cs(+meta), .planning/phases/*,
Namer-Unity.sln.DotSettings.user; untracked `.planning/debug/resolved/*`
not to be swept).

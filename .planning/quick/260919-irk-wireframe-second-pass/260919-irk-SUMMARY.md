---
status: complete
quick_id: 260919-irk
created: 2026-09-19
fixes: 260919-hxs
---

# Quick Task 260919-irk: Triangle wireframe as a second render pass (replace IMGUI overlay) Summary

**The After-pane triangle wireframe is now a GPU second pass instead of a CPU IMGUI overlay: `BuildPreviewSplitMesh` appends one line-topology submesh carrying every triangle's three edges (no dedup, shared vertex buffer), and the After pane's `BeginPreview → DrawMesh → Render(true)` cycle draws it with a new unlit URP wire material (`NamerPreviewWire`, `Geometry+100` / `ZWrite Off` / `ZTest Always`). GPU viewport clipping cuts edges per pixel at the pane edge (no whole-edge/triangle drops, no Before-pane bleed), pan/zoom/orbit follow the camera transform for free, and one draw call replaces up to 3N `Handles.DrawLine` calls (editor ring-buffer exhaustion gone). `DrawTriangleWireframe` + `ProjectPreviewVertex` and their manual NDC projection / pan subtraction / containment culling are deleted.**

## What Changed (per task)

### Task 1 — Wire shader (`Shaders/NamerPreviewWire.shader`, NEW)

- `Shader "GraffitiEntertainment.Namer/NamerPreviewWire"` with `_WireColor` property (default cyan `(0, 1, 1, 1)`).
- SubShader Tags: `RenderType=Opaque`, `RenderPipeline=UniversalPipeline`, `Queue=Geometry+100` (drawn after the opaque preview mesh so the X-ray look is preserved); pass tagged `LightMode=UniversalForward` mirroring `NamerChannelView` so URP's forward pass picks it up.
- Single pass: `ZWrite Off`, `ZTest Always`, minimal HLSL vert/frag on `Core.hlsl`, `CBUFFER_START(UnityPerMaterial) half4 _WireColor; CBUFFER_END` (SRP-batcher compatible), `TransformObjectToHClip`, frag returns `_WireColor`. No lighting, no texturing.
- Unity generated the `.meta` on import (committed with the shader).

### Task 2 — Renderer second pass (`NamerPreviewRenderer.cs`)

- Mesh-pair `Render` overload gains `Material wireMaterial = null, int wireSubmesh = -1`; forwarded to the AFTER-pane `RenderPane` call only (Before pane passes the `null`/`-1` no-ops). Source-compatible with the existing 5-arg test call.
- `RenderPane` gains both params; after the existing opaque `_preview.DrawMesh(...)` it adds the guarded second `_preview.DrawMesh(mesh, position, meshRotation, wireMaterial, wireSubmesh);` in the same preview cycle.
- `Render`/`RenderPane` doc comments describe the new wire capability; framing/rotation/zoom semantics unchanged.

### Task 3 — Window wiring + overlay deletion (`NamerEditorWindow.cs`)

- `BuildPreviewSplitMesh` appends ONE line-topology submesh after the triangle submesh loop: `mesh.subMeshCount = wireSubmesh + 1; mesh.SetIndices(BuildWireEdgeIndices(split), MeshTopology.Lines, wireSubmesh);`
- New `private static int[] BuildWireEdgeIndices(NamerSplitResult split)`: total triangles across `split.SubMeshTriangles` (`int[][]`, verified in `MeshVertexSplitter.cs`), `int[totalTris * 6]`, fills `(a,b) (b,c) (c,a)` per triangle across all submeshes. NO edge dedup (interior shared edges overdraw the same color — invisible).
- `private Material _wireMaterial;` field + `WireColorId` property-id const (same pattern as `SurfaceMapId`) + `EnsureWireMaterial()` mirroring the channel-material creation (`Shader.Find("GraffitiEntertainment.Namer/NamerPreviewWire")`, `HideAndDontSave`, `_WireColor` from `TriangleWireframeColor`); disposed in `OnDisable` next to `_channelViewMaterial`.
- Preview call site: wire args passed only when `_showTriangles && afterMesh == _previewSplitMesh` (split mesh carries the wire submesh; source/generated-mesh fallback leaves the toggle a no-op; imported/generated ASSET meshes never mutated). `DrawTriangleWireframe(previewRect, afterMesh);` call deleted.
- Deleted `DrawTriangleWireframe` and `ProjectPreviewVertex` with their doc comments. `NamerPreviewRenderer.PanOffset` left alone (pinned by `Pan_AccumulatesOffset_ResetsOnFrame`).
- Triangles toggle tooltip now: "Renders the split mesh's triangle edges as a second pass in the After preview (visual debug only)."

## Verification

- Full EditMode suite in the live editor (unity-mcp RunCommand + TestRunnerApi per memory `live-editor-editmode-test-run`), run by the coordinator after the edits: **158/158 PASS** — no test changes.
- Assembly rebuild verified clean by the coordinator: dll contains `BuildWireEdgeIndices`, `DrawTriangleWireframe` gone; `NamerPreviewWire.shader` imported with its Unity-generated `.meta`.
- User UAT (coordinator-confirmed): wireframe stays inside the After pane half only, pixel-clipped at the pane edge with strafe/zoom (no whole-triangle drops, no left-pane bleed), follows orbit/pan/zoom, and dense meshes no longer flood the editor graphics ring buffer.

## Commits

- Fix commit (shader + .meta + renderer + window): `f21959a` — `fix(quick-irk): triangle wireframe as second render pass through the after-pane camera`
- Docs commit (this folder + STATE.md row): this commit.

## Files Modified

- `Packages/com.graffitientertainment.namer/Shaders/NamerPreviewWire.shader` (+ Unity-generated `.shader.meta`) — NEW, 71 lines.
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs` — 24 insertions / 5 deletions.
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — 110 insertions / 61 deletions.

## Deviations from Plan

None functional — plan executed as written. Minor execution notes:

- Plan line numbers were off by ~2 lines for `DrawTriangleWireframe`/`ProjectPreviewVertex` (file had drifted); the same contiguous block was deleted.
- Before-pane `RenderPane` call passes `null, -1` explicitly (private method, required params) — the plan's "calls with defaults" equivalent under the existing null/negative guard.
- Shader pass carries `Name "NamerPreviewWire"` + `LightMode=UniversalForward` tags mirroring `NamerChannelView.shader` (plan specified conventions but not the tags); required for URP to render the pass through the preview camera.

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

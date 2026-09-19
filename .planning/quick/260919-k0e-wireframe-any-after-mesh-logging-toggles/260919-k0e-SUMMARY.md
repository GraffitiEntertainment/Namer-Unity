---
status: complete
quick_id: 260919-k0e
created: 2026-09-19
fixes: 260919-irk
---

# Quick Task 260919-k0e: Wireframe on any After mesh + exception logging + checkbox order Summary

**Three Phase 04.2 UAT fixes. (1) The After-pane wireframe now renders over whatever mesh the After pane actually shows: `NamerPreviewRenderer.Render`/`RenderPane` take an explicit `Mesh wireMesh` parameter, so the gate is no longer baked to `_previewSplitMesh` — the split mesh keeps its own line-topology submesh, and every other after mesh (source mesh with decomposition off, generated mesh after Process) gets a cached standalone hidden line-topology wire mesh built from its own vertices via `GetOrCreatePreviewWireMesh` + a `BuildWireEdgeIndices(Mesh)` overload (imported/generated ASSET meshes are never mutated; the cache is a single `_previewWireSource`/`_previewWireMesh` pair rebuilt on after-mesh change and disposed in `OnDisable`). (2) `RecomputePreview` and `RunProcess` catches now call `Debug.LogException(ex)` as their first statement, so panel failures (e.g. the live "RenderTexture has been destroyed" on model selection) land a full stack trace in the console/Editor.log while the panel keeps showing only the message. (3) The five `DrawStepSwitches` stage-gate rows changed `EditorGUILayout.ToggleLeft` → `EditorGUILayout.Toggle` (checkbox before label), matching the shaded-view row the user confirmed as correct.**

## What Changed (per task)

### Task 1 — Renderer wire-mesh parameter (`NamerPreviewRenderer.cs`)

- `Render(Mesh, Mesh, Material, Material, Rect, ...)` signature: `Material wireMaterial = null, int wireSubmesh = -1` → `Mesh wireMesh = null, Material wireMaterial = null, int wireSubmesh = -1` (source-compatible — optional params keep existing call sites compiling).
- Before-pane `RenderPane` call passes `null, null, -1`; after-pane passes `wireMesh, wireMaterial, wireSubmesh`.
- `RenderPane` gains `Mesh wireMesh` immediately before `Material wireMaterial`; the wire draw gate is now `wireMesh != null && wireMaterial != null && wireSubmesh >= 0` and draws `wireMesh` (not the pane mesh) with the updated comment (split mesh's own line submesh OR standalone wire mesh built from the after mesh).
- `Render`/`RenderPane` doc comments updated: the wire may be the pane mesh itself (split-mesh line submesh) or a standalone cached wire mesh built from the after mesh (source/generated assets never mutated).

### Task 2 — Wire mesh for every After-mesh state (`NamerEditorWindow.cs`)

- Fields next to `_wireMaterial`: `private Mesh _previewWireSource; private Mesh _previewWireMesh;` — a single-entry cache (at most one non-split after mesh needs a wire at a time; no `Dictionary` needed).
- New `GetOrCreatePreviewWireMesh(Mesh source)` next to `EnsureWireMaterial`: returns the cached wire when the source matches, otherwise `DestroyImmediate`s the stale one and builds a fresh hidden (`HideAndDontSave`) mesh — `indexFormat` upgraded to `UInt32` when the source needs it (fully qualified, matching file style), `SetVertices(source.vertices)`, one `MeshTopology.Lines` submesh from `BuildWireEdgeIndices(source)`, `RecalculateBounds`.
- New `private static int[] BuildWireEdgeIndices(Mesh mesh)` overload next to the `NamerSplitResult` version: every triangle's three edges `(a,b) (b,c) (c,a)` across all submeshes via `mesh.GetTriangles(i)` / `(int)mesh.GetIndexCount(i)`; no edge dedup (interior overdraw is invisible).
- Wire gate (~:1003): comment AND gate replaced — `if (_showTriangles && afterMesh != null)` branches split-mesh (`wireMesh = afterMesh`, last submesh) vs. anything else (`wireMesh = GetOrCreatePreviewWireMesh(afterMesh)`, `wireSubmesh = 0`); `Render` call passes `wireMesh, wireMaterial, wireSubmesh`.
- `OnDisable`: the cached wire mesh is `DestroyImmediate`d and both cache fields nulled, next to the `_wireMaterial` block.

### Task 3 — Log caught exceptions (`NamerEditorWindow.cs`)

- `Debug.LogException(ex);` inserted as the first statement of the `RecomputePreview` catch and the `RunProcess` catch (each disambiguated by its unique `finally`); the panel status text is unchanged.

### Task 4 — Checkbox before text (`NamerEditorWindow.cs`)

- The five `EditorGUILayout.ToggleLeft(` calls in `DrawStepSwitches` (VC + Residual, Roughness, AO, Metallic, Emissive) changed to `EditorGUILayout.Toggle(` — method name only, arguments untouched; 0 `ToggleLeft` remain in the file.

## Verification

- Full EditMode suite in the live editor (unity-mcp RunCommand + TestRunnerApi per memory `live-editor-editmode-test-run`), run by the coordinator after the edits: **158/158 PASS** (`Temp/260919-k0e-results.txt` file-marker flow) — no test changes.
- Clean compile proven by the coordinator: `GraffitiEntertainment.Namer.Editor.dll` rebuilt and contains the new `GetOrCreatePreviewWireMesh` symbol; the dependent tests dll rebuilt in the same pass.
- Orchestrator diff review: renderer + window changes match the plan verbatim, all four tasks, nothing else touched.

## Commits

- Fix commit (renderer + window): `c52579f` — `fix(quick-k0e): wireframe on any after mesh, log caught exceptions, checkbox-before-text toggles`
- Docs commit (this folder + STATE.md row): this commit.

## Files Modified

- `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs` — 21 insertions / 20 deletions (doc comments, signatures, wire gate).
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — 107 insertions / 10 deletions (fields, cache/builder methods, wire gate, two catches, five toggles).

## Deviations from Plan

None — plan executed exactly as written; all line anchors matched the committed state.

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

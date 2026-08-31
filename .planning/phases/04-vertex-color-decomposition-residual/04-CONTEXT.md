# Phase 4: Vertex-Color Decomposition + Residual - Context

**Gathered:** 2026-08-31
**Status:** Ready for planning

<domain>
## Phase Boundary

Fit the low-frequency base color into mesh vertex colors and capture what the fit misses as a residual texture — the algorithmic core of NAMER's "reduced texture counts/memory" promise:

1. **Per-triangle multi-sample barycentric least-squares fit** (VCOL-01 — not simple averaging) of vertex colors against the source base texture.
2. **Seam-safe vertex splitting** (VCOL-02) — the output mesh splits vertices at UV seams/discontinuities while preserving attributes.
3. **Residual texture** (VCOL-03) — the difference between fitted vertex-color interpolation and the source texture, GPU-computed.
4. **Error reporting + adaptive residual resolution** (VCOL-04/VCOL-05) — coverage/avg/max stats, downward halving search, manual ladder override, and a **no-texture endgame** when the fit alone is good enough.
5. **Opt-in wiring into Process + the editor window**, with both representations (decomposed split-mesh + residual, and non-decomposed original-mesh + Base PNG) retained so the user can switch.

The Phase 1 packed-surface format contract, runtime surface decode, and the AO work from 03.1 are untouched. Decomposition is an extension of the base-color path only.

**Out of boundary:** stylization interplay (Phase 5 — decomposition precedes stylization in the pipeline; their interaction is Phase 5's problem), bake-to-generation wiring (W1 accepted divergence stands), batch processing (v2 BATCH-01), GBuffer/deferred support.

</domain>

<decisions>
## Implementation Decisions

### Runtime recombination model
- **D-01:** Recombination math is **research-decided**, with locked constraints: Color32 vertex colors, reconstruction within the reported error, packed surface-texture contract untouched. Standing context the researcher must weigh: `NamerSurface.hlsl` **already implements multiply** — `albedo = baseResidual.rgb * _BaseColor.rgb * vertexColor.rgb` (`InitializeNamerSurfaceData`, SHDR-02/D-05, residual == base in Phase 1) — and PROJECT.md documents `BaseColor ≈ VertexColorInterpolation × ResidualColor`. Multiply is the de-facto model; choosing additive/hybrid requires changing the runtime shader and re-verifying SHDR parity.
- **D-02:** Residual on disk is **HDR EXR always** (`R16G16B16A16_SFloat`) — never quantized to PNG, even when the residual happens to be LDR-range. AssetGenerator's EXR write path (already stubbed) is the route.
- **D-03:** **Own convention, not Blender's.** `assign_vertex_colors` in the Blender reference is nearest-texel-per-loop averaging with a luminance-variance alpha heuristic — deliberately NOT mirrored. Surface-texture decode compatibility is untouched (Blender never consumes vertex-color materials; format compat applies to packed textures only).
- **D-04:** The Color32 **alpha channel carries a per-vertex fit-quality signal (0–1)** — fit confidence, debuggable in-engine. (This replaces Blender's luminance-variance alpha heuristic.)

### Opt-in flow & generated outputs
- **D-05:** Decomposition toggle defaults **OFF — explicit opt-in**. Process output stays the Phase-3 shape (original mesh + Base PNG + Surface PNG + .mat) until the user enables it.
- **D-06:** When enabled, Process writes the **full set**: seam-split vertex-colored mesh + residual EXR + normalized Base PNG + Surface PNG + .mat, under `NAMERGenerated/{source}/` with prefix/suffix naming (Phase 03 D-01/D-02). Both representations exist on disk because the user can switch between them (D-14).
- **D-07:** The generated material **binds the residual as `_BaseMap`** — mesh vertex colors × residual reconstruct base at runtime. This is the texture-memory win; the Base PNG is the switch-back/reference asset.
- **D-08:** The interactive preview shows the **decomposed reconstruction** when enabled — preview parity with generated output. (Explicitly avoids a repeat of 03.1-W1's preview/output divergence.)

### Error reporting & debug views
- **D-09:** Reconstruction-error stats surface in a **window stats block**: coverage %, avg error, max error, residual requirement (incl. "not required"), chosen residual resolution.
- **D-10:** **Vertex Colors / Residual / Error Heatmap become debug channels in the existing toolbar** — joining the Phase-03 D-11 channel set (shared debug-material pattern), not a dedicated panel.
- **D-11:** The error heatmap uses a **perceptually-uniform color ramp** (viridis-style), not grayscale.
- **D-12:** Stats update **live with the debounced preview recompute** (UI-06 / Phase-03 D-10 debounce pattern) — not only after Process.

### Adaptive residual & no-texture endgame
- **D-13:** **Auto-drop below threshold.** When the fit alone reconstructs within the error threshold, no residual texture is written or bound — the material runs on vertex colors + packed surface only (stats report "residual: not required"). The biggest memory win; fully automatic.
- **D-14:** **A user-facing on/off switch for vertex coloring** ("used or not, toggled on or off"). Because decomposed output requires the seam-split mesh while non-decomposed uses the original topology, **both representations are retained so the user can switch between them** — switching must not require reprocessing.
- **D-15:** The error threshold is **user-tunable in the window** beside the decomposition toggle, EditorPrefs-persisted via `NamerProcessorSettings` (AO-controls pattern from 03.1).
- **D-16:** Adaptive search runs **downward from source resolution** — halve (2048→1024→512→256→128) while error stays within threshold. Quality-first: never worse than today's full-res output.
- **D-17:** Manual override is a **ladder dropdown** snapping to the halving steps the adaptive search uses (no free-form pixel input).

### Claude's Discretion
- Final recombination-math call (research): multiply (standing implementation, D-01) vs additive vs hybrid — within the locked constraints.
- LSQ internals: samples per triangle, barycentric sampling pattern, per-vertex normal-equations accumulation (3×3 `AᵀA`), solver details — hand-rolled with Unity.Mathematics, Burst-compiled (no SVD library; the systems are 3×3).
- Seam-splitting specifics: split criteria beyond required UV seams (normal/color discontinuity thresholds), attribute-preservation details.
- The error-metric definition behind the threshold (perceptual ΔE-style vs mean RGB distance) — must be one consistent metric across stats, heatmap, and the adaptive search.
- The D-14 switching mechanism: separate material assets + renderer/mesh swap, material keyword, or similar — must be instant and not reprocess.
- Exact stats-block placement and channel UI layout (Phase-03 D-14: functional controls only, no dead UI).

</decisions>

<specifics>
## Specific Ideas

- User: "we are going to need a button so that this vertex coloring can be used or not, meaning, it can be toggled on or off and it might be that we need a processed mesh to switch between as a result" — the switch is a first-class requirement (D-14), not an afterthought; the two-mesh consequence is accepted.
- NAMER's core value is "reduced texture counts/memory" — the no-texture endgame (D-13) is the headline win of the phase; a pure vertex-color material is the best possible outcome for a good fit.
- The Blender reference's vertex-color code was read during discussion and **rejected as the convention** (D-03/D-04): per-loop nearest-texel averaging is exactly the "simple averaging" VCOL-01 forbids, and the luminance-variance alpha is a heuristic we replace with a fit-quality signal.

</specifics>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Blender reference (context only — NOT mirrored; OUTSIDE this repo, absolute path)
- `/Users/Shared/SSDevelopment/Development/GraffitiEntertainment/namer_plugin/namer_core.py` — `assign_vertex_colors` (lines 348–370: nearest-texel per-loop averaging) and `compute_triangle_gradient` (lines 378–396: luminance-variance alpha heuristic). Read to understand what we deliberately diverge from (D-03/D-04); do NOT port.
- `/Users/Shared/SSDevelopment/Development/GraffitiEntertainment/namer_plugin/namer_png_panel.py` — `NAMER_OT_BakeVertexColors` (lines 231–291): operator flow only.

### Product spec
- `NAMER_UNITY_PLUGIN_PRD.md` (repo root) — product intent for vertex-color decomposition, residual, and texture-reduction value.

### Planning context
- `.planning/REQUIREMENTS.md` — VCOL-01..VCOL-05, TEST-02 definitions (the phase's requirement set)
- `.planning/ROADMAP.md` §Phase 4 — goal + success criteria
- `.planning/PROJECT.md` — vertex color model line (`BaseColor ≈ VertexColorInterpolation × ResidualColor`, per-triangle multi-sample barycentric least-squares "not simple averaging"), constraints (Color32, GPU per-pixel), current state
- `.planning/phases/03-asset-generation-editor-workflow-preview/03-CONTEXT.md` — carry-ins: D-01/D-02 (output location/naming), D-03 (EditorPrefs), D-10 (debounced preview, never disk), D-11 (debug channels), D-14 (functional controls only)
- `.planning/phases/03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit/03.1-CONTEXT.md` — W1 parity lesson (why D-08 exists) and the Burst + Unity.Mathematics + Collections asmdef precedent

### Code touchpoints
- `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl` lines 76–94 — `InitializeNamerSurfaceData` already multiplies `vertexColor.rgb` into albedo (SHDR-02/D-05); the vertex-color input is plumbed, meshes just don't supply colors yet
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — Process orchestration; the D-05 opt-in gate inserts here
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — Processing section, debounced recompute, debug-channel toolbar (D-09/D-10/D-12 UI lands here)
- `Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs` — EditorPrefs key pattern for D-15
- `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs` — sole disk writer; mesh-asset + EXR-residual write paths (EXR path ready)
- `Packages/com.graffitientertainment.namer/Editor/Bake/NamerAOBaker.cs` + `NamerAOBvh.cs` — Burst + NativeArray mesh-processing precedent (structure to mirror for VertexColorFitter)
- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` + `Editor/Pipeline/ComputeTexturePool.cs` — residual generation is GPU compute (VCOL-03, no per-pixel C#)
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs` — mesh/source resolution (seam-split mesh plumbing)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `NamerAOBaker`/`NamerAOBvh`: the sanctioned Burst + Unity.Mathematics + Collections mesh-processing pattern (deterministic, headless-testable) — mirror it for the per-triangle fitter.
- `NamerProcessorSettings` + debounced `MarkDirty`/`Tick` recompute: new controls (toggle, threshold, ladder dropdown) plug in exactly like the 03.1 AO controls.
- Debug-channel toolbar + `NamerDebugChannelMaterial`: Vertex Colors / Residual / Error Heatmap join the existing channel list.
- `AssetGenerator`: sole disk writer with importer stamping; mesh-asset writing and the EXR residual path are the extension surface (D-02/D-06).
- `ComputeTexturePool` + `NamerComputePipeline`: residual generation/downsample evaluation runs as GPU compute stages.

### Established Patterns
- GPU compute for all per-pixel texture work (residual, downsampling, heatmap ramp); Burst/C# only for mesh-topology math (per-triangle fit, seam splitting).
- `AssetGenerator` is the only disk writer; preview is memory-only.
- Settings flow: `NamerProcessorSettings` field → window control → pipeline/uniform.

### Integration Points
- `NamerProcessor.Process`: decomposition stage slots between the existing compute pipeline and `AssetGenerator`, gated by the D-05 toggle.
- `InitializeNamerSurfaceData` (NamerSurface.hlsl): the runtime consumer — supplies `vertexColor` from the mesh's Color32 stream; alpha (D-04) is ignored at runtime, read only by debug tooling.
- `_BaseMap` binding in `AssetGenerator.WriteMaterial`: D-07 (residual) vs switch-back (Base PNG) selection happens here.

</code_context>

<deferred>
## Deferred Ideas

- Stylization × decomposition interplay (fit before/after stylize, palette effects on residual) — Phase 5
- GBuffer/deferred vertex-color support — tracked limitation from Phase 1
- Batch decomposition across many assets — v2 BATCH-01
- Blender-side consumption of Unity-generated vertex-color materials — explicitly out (D-03: Blender never consumes them)
- Literal Blender fixture parity for vertex colors — not applicable under the own-convention decision (packed-texture decode parity is unaffected)

</deferred>

---

*Phase: 4-vertex-color-decomposition-residual*
*Context gathered: 2026-08-31*

# Phase 3: Asset Generation + Editor Workflow + Preview - Context

**Gathered:** 2026-08-27
**Status:** Ready for planning
**Mode:** auto (recommended defaults selected; review before execution if desired)

<domain>
## Phase Boundary

Turn Phase 2's in-memory GPU pipeline output into persistent, source-compatible NAMER assets — a NAMER material + normalized base-color texture + packed surface texture (mesh only when decomposed, i.e. Phase 4) — written non-destructively under a dedicated `NAMERGenerated/` directory with correct import settings, driven from the `Tools > NAMER > Processor` editor window and the explicit `Process with NAMER` command, with interactive before/after mesh preview, debug channel views, and automated asset-path + source-immutability tests. Closes by assembling the shippable MVP UPM package. Vertex-color decomposition, residual, stylization controls, batch/automation, and HDRP are OUT — this phase is the vertical slice that makes Phases 1+2 user-shippable.

</domain>

<decisions>
## Implementation Decisions

### Generated-Output Layout & Naming (GEN-01, GEN-03)
- **D-01:** Default destination is `Assets/NAMERGenerated/` with one subfolder per processed selection, named after the source (`NAMERGenerated/{sourceAssetName}/`). Shared materials are written once per processing run (dedup follows Phase 2 D-03's unique-material unit), not once per renderer.
- **D-02:** Per-material outputs are `{prefix}{materialName}{suffix}_Base.png`, `{prefix}{materialName}{suffix}_Surface.png`, and `{prefix}{materialName}{suffix}.mat`. Defaults: empty prefix, suffix `_Namer`. Prefix/suffix/destination are user-configurable in the processor window.
- **D-03:** Settings persist user-scoped via `EditorPrefs` in v1 (survives sessions, zero asset noise). A shareable/team `NamerProcessorSettings` ScriptableObject is a deferred idea — do not build it now.
- **D-04:** Overwrite is limited to generated assets: only files inside the configured destination folder that carry the NAMER-generated stamp (an asset label, e.g. `NamerGenerated`, applied at write time). Nothing outside the destination folder is ever overwritten, regardless of naming; collisions with non-stamped files inside the folder abort with a clear error rather than clobber.

### Scene Integration Scope (GEN-01)
- **D-05:** Phase 3 generates material + textures only. No mesh generation (Phase 4), no prefab variants, no automatic re-pointing of scene renderers or prefab materials. The preview applies the NAMER material to the actual source mesh in-memory only. Auto-assign/prefab-variant conveniences are deferred ideas.

### Asset Writing & Import Stamping (GEN-04, ENCD-04 carry-in)
- **D-06:** Both textures are saved as PNG via `EncodeToPNG` + file write + `AssetDatabase.ImportAsset`. The normalized base color is quantized from the `R16G16B16A16_SFloat` intermediate to 8-bit on readback and imported **sRGB**. The packed surface texture is imported **linear, uncompressed, with point (nearest) filtering and no mipmaps** — bit-packed alpha cannot survive compression, mips, or interpolated sampling (research pitfall #7; PROJECT.md deferred point sampling into this phase). Researcher must check the Blender reference implementation's filter/mip convention for the packed texture and mirror it if it differs.
- **D-07:** The generated Material uses the existing `GraffitiEntertainment.Namer/NAMER` shader and carries the metadata contract already defined by Phase 1/2: `_BaseColor` tint (not baked — Phase 2 decision), `_EmissionColor` + `_EMISSION` keyword when emissive, `_OcclusionStrength`, `_Cutoff` + `_ALPHATEST_ON` when alpha-tested, and transparent blend state (`_Surface`, `_SrcBlend`/`_DstBlend`, `_ZWrite`) when the source was transparent.
- **D-08:** GPU→CPU readback for saving uses the pipeline's existing `AsyncGPUReadback` contract (Phase 2 D-13); no synchronous full-texture reads on 2K/4K outputs.

### Preview (UI-04, UI-06)
- **D-09:** Before/after preview renders the **actual selected mesh** twice side-by-side via `PreviewRenderUtility` — original source material (before) vs the NAMER material applied in-memory (after) — with a synced camera so lighting/angle comparisons are meaningful (SHDR-03 spirit). Texture-only 2D inspection may supplement but not replace the mesh preview. **Researcher must confirm the `PreviewRenderUtility` API surface** (STATE.md Phase 3 blocker: docs returned 404 during roadmap research).
- **D-10:** Interactive updates: control changes trigger a debounced re-dispatch of the compute pipeline and preview re-render (UI-06). Interactive preview NEVER writes to disk — assets are written only by the explicit Process/save action. Debounce interval, staging, and whether intermediate uploads are cached are Claude's discretion as long as no per-pixel C# loops run (NORM-03 carry-in).

### Debug Channel Views (UI-05)
- **D-11:** Phase 3 ships exactly the channels derivable from current outputs: reconstructed/normalized base color, AO, decoded normal (from octahedral), roughness, metallic, emissive. They render from the actual generated textures onto the preview mesh. Vertex-color, residual, and reconstruction-error views land with Phase 4 — no stub toggles, no permanently-disabled dead UI.
- **D-12:** Mechanism (keyword-per-channel debug variant on the NAMER shader vs a small editor-only debug shader) is planner's choice with researcher input; either way the decode math must reuse the shared HLSL include pattern so debug views cannot drift from the runtime decode.

### Command Surface & Window Controls (UI-01, UI-02, UI-03)
- **D-13:** The processor window lives at `Tools > NAMER > Processor`. `Process with NAMER` is reachable from the Project-browser context menu (`Assets/Process with NAMER`) and the hierarchy context menu (`GameObject/Process with NAMER`), plus a Process button in the window — all three route into one shared entry point (validate selection → inspect → process → generate). The Phase 2 `Tools > NAMER/Inspect Selection` validation menu stays.
- **D-14:** The window exposes only controls that are functional in this phase: source selection display + inspection summary, AO un-multiply strength (the Phase 2 D-08 promise), destination/prefix/suffix, overwrite-generated toggle, preview + debug channel picker, Process. Phase 4/5 controls (vertex-color decomposition, residual resolution, style strength, palette, smoothing, normal detail, roughness simplification) appear when their phases land — the window layout may anticipate sections, but ships no dead controls. UX polish (spacing, states, visual detail) matters — the user treats UI quality as equal to functional correctness.

### Testing & Safety Enforcement (TEST-03, GEN-02)
- **D-15:** Source-immutability test is an EditMode test that: builds/loads a fixture with real source assets (textures, material, model), hashes every involved source file **and its .meta** (file-content hash; algorithm is discretion) before processing, runs the full Process flow end-to-end, re-hashes after, and asserts byte-identical. Metadata-only or timestamp comparisons are insufficient.
- **D-16:** Path/naming tests assert: every written asset lives under the configured destination, names honor prefix/suffix, and overwrite refuses non-stamped targets (D-04). Tests run against a **temporary destination override** created and cleaned up by the test (including `.meta`), never the user's real `NAMERGenerated/` content.

### MVP Package Assembly (plan 03-03)
- **D-17:** "Shippable MVP package" means: `package.json` metadata complete (name, version, unity minimum, displayName, description, author), a package-level `README.md` (install + basic workflow), and the full headless EditMode + PlayMode suite green at HEAD. `Samples~` content, docs site, and Unity package-validation tooling are deferred.

### Claude's Discretion
- Exact class/file names (`AssetGenerator`, processor window, import stamper, debug shader layout).
- Window layout details (pane arrangement, section ordering) beyond D-09/D-14 constraints.
- Debounce interval and dispatch/staging mechanics (D-10).
- Hash algorithm, fixture construction, and test-file hygiene details (D-15/D-16).
- Debug-view mechanism (D-12) and PNG encode/readback plumbing details.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project definition
- `NAMER_UNITY_PLUGIN_PRD.md` — authoritative PRD: asset-safety rules, generated-directory expectations, editor workflow and preview requirements
- `.planning/PROJECT.md` — current state after Phase 2, Key Decisions (sRGB detection via `TextureImporter`; point-sampling deferral into Phase 3), constraints
- `.planning/REQUIREMENTS.md` — GEN-01..04, UI-01..06, TEST-03 are this phase's requirements
- `.planning/ROADMAP.md` §Phase 3 — goal, success criteria, plan sketch 03-01/02/03
- `.planning/STATE.md` §Blockers/Concerns — Phase 3 entry: `PreviewRenderUtility` API surface flagged (docs 404), confirm during planning

### Research (Phase 1 corpus — still authoritative)
- `.planning/research/STACK.md` — Unity 6 LTS/URP 17.x baseline, GraphicsFormat guidance, AsyncGPUReadback usage
- `.planning/research/ARCHITECTURE.md` — assembly split and one-way data flow the AssetGenerator must respect
- `.planning/research/PITFALLS.md` — compression corruption (#7), sRGB/linear contract (#1); both bite exactly this phase's save/import path

### Code to build on
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs` — entry API: `Inspect(Object)` → `NamerSourceModel`; the processor window's front door
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs` — `Process(NamerMaterialInspection)` → `NamerComputeResult` (leased RTs); `RequestReadback`; `ReleaseResult` contract the AssetGenerator must honor
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs` — the metadata contract (BaseColor tint, Emissive, OcclusionStrength, Cutoff, SurfaceType, AoUnmultiplyStrength) D-07 transfers to the generated material
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs` — RT lifecycle the preview's repeated dispatches must respect
- `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader` + `Shaders/NamerSurface.hlsl` — property/keyword surface the generated Material must populate; shared-include pattern for the debug views (D-12)
- `Packages/com.graffitientertainment.namer/Editor/NamerSmokeSetup.cs` — programmatic URP/material/asset creation patterns for editor tooling and test fixtures
- `Packages/com.graffitientertainment.namer/Tests/Editor/` — EditMode test conventions (asmdef-gated, headless-runnable) D-15/D-16 extend
- `Packages/com.graffitientertainment.namer/package.json` — metadata D-17 completes

### External reference (not in repo)
- `GraffitiEntertainment/BlenderNamerPlugin` (GitHub, develop branch) — reference implementation. Phase 3 focus: its texture save/filtering/import conventions for packed textures (D-06 point-filtering precedent).

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `SourceInspector.Inspect(Object)` already resolves all 5 selection kinds into deduped `NamerMaterialInspection` units — the processor window and `Process with NAMER` command call it directly; no new selection logic needed
- `NamerComputePipeline` produces exactly the two outputs the AssetGenerator saves, already in final formats (`R8G8B16A16_SFloat` base intermediate, `R8G8B8A8_UNorm` packed), with async readback built in
- `NamerSmokeSetup.cs` demonstrates programmatic material/asset creation under URP — the seed pattern for AssetGenerator and test fixtures
- `NAMER.shader`'s Properties block IS the generated-material metadata checklist (D-07)

### Established Patterns
- Editor assembly already references URP Runtime + Core (window code can use URP types without asmdef changes)
- Core purity + shared-HLSL-include mirroring (Phase 1/2) — debug views must follow it (D-12)
- Constants centralized in `NamerConstants`; no magic numbers (project rule)
- Menu items live under `Tools/NAMER/…` (`Inspect Selection`, `Create Smoke Scene`) — the window joins this family
- Headless test invocation without `-quit` (STATE.md accumulated decision)

### Integration Points
- `NamerComputeResult` targets are pool-leased — AssetGenerator must read back then `ReleaseResult`; preview re-dispatches must not accumulate live targets (leak-watchdog contract from 02-03)
- `NamerMaterialInspection.AoUnmultiplyStrength` (default 1.0) is the field the processor window's AO control writes (D-14)
- `NamerSurface.hlsl`'s decode is the single source of truth the debug channel views must visually match

</code_context>

<specifics>
## Specific Ideas

- Packed surface texture must never pass through compression, mip generation, or bilinear filtering — any of these corrupts the alpha bit field (PROJECT.md explicitly deferred "import stamping incl. point sampling" to this phase).
- The `_BaseColor` tint stays material metadata, never baked into the saved base texture (Phase 2 decision — generation must not regress it).
- Before/after preview panes share one camera state so the comparison is honest; original material renders with the source shader, not a re-import.
- Interactive preview writes nothing; only the explicit Process action touches disk (D-10).
- Tests must clean up their generated output including `.meta` files, and must run against a temp destination so a developer's real `NAMERGenerated/` content is never at risk (D-16).

</specifics>

<deferred>
## Deferred Ideas

- Auto-assign generated material to scene renderers / prefab-variant generation — later convenience phase
- Shared team-settings `NamerProcessorSettings` ScriptableObject (v1 uses EditorPrefs, D-03)
- `Samples~` smoke content inside the UPM package (D-17)
- EXR/HDR base-color export path
- Batch processing over folders (BATCH-01, already v2)
- AssetPostprocessor auto-processing (AUTO-01, already v2)

</deferred>

---

*Phase: 3-Asset Generation + Editor Workflow + Preview*
*Context gathered: 2026-08-27*

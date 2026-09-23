# Phase 5: Stylization - Context

**Gathered:** 2026-09-23
**Status:** Ready for planning

<domain>
## Phase Boundary

Reference-image-driven, hue-preserving stylization of the NAMER decomposition, delivered as a reusable `NAMERStyleProfile` ScriptableObject (STYL-01..06) plus the stylized-NAMER preview mode (UI-04) and the remaining UI-03 controls. Stylization = palette/style-stat mapping + island-gated edge-preserving smoothing + AO/normal/roughness styling controls, applied around the existing decompose pipeline so the **saved packed output always equals the preview**.

Out of scope: hard-coded art styles (the profile drives the look; the plugin is style-agnostic), any change to the locked packed surface format, and any modification of source assets.

</domain>

<decisions>
## Implementation Decisions

### Pipeline position
- **D-01:** Stylization runs in UV space **after the normalize/clean dispatch and BEFORE the VC fit/projection** (`NamerComputePipeline.Process` insertion point; projection is `CSProjectBase`, vertex rasterization `CSRasterizeVertexColors`, both in `NAMERDecomp.compute`). The fit reads the stylized source, so vertex colors AND the residual both come out stylized; the one-texture default (D-13, `AssetGenerator` leaves `_BaseResidualMap` white — VCs are the sole color carrier) is covered by the same single stage. The user explicitly confirmed this as "stylize the source, then fit" — the style lands in the VCs. Post-decomposition mesh-domain VC editing was rejected: edge-preserving smoothing is atlas-space only (STYL-04 islands/seams/decals), and an un-stylized residual would re-add the original detail on top.
- **D-02:** 04.2's residual-dividend semantics carry over with the referent updated: the dividend is the **styled source** albedo (styled base minus its per-triangle projection), so the residual remains the honest reconstruction error of what actually ships. Requires a doc/test note updating 04.2-CONTEXT wording; the operator itself is unchanged.

### Smoothing filter
- **D-03:** **Island-gated bilateral** with a strength slider (2–3 ping-pong iterations at strong flatten). Extends the existing coverage-gated neighborhood-loop pattern (`CSDilateResidual`, `NAMERDecomp.compute:257`). Requires a NEW **UV-island-ID buffer** (CPU union-find over UV-adjacent triangles + rasterized island ID reusing the `CSRasterizeVertexColors` dispatch shape) — a binary coverage mask alone fails when the atlas gutter is narrower than the filter radius. Kuwahara (painterly) is deferred: it imposes a hard-coded look, in tension with the style-agnostic mandate.

### Hue preservation + palette
- **D-04:** ONE style-strength slider (0 = original → 1 = full palette snap) drives a single blended GPU operator: nearest palette anchor matched **by hue**, luma/sat pulled toward the anchor's stats (hue preserved), and a strength-derived hue-replacement factor ramping to 1 at slider max — this is where STYL-03's "unless stronger replacement is chosen" lives. All 3–10 references are sampled at capped resolution into ONE merged aggregate palette/stat set stored on the profile (STYL-02 stats list). Extraction is deterministic CPU Burst (NamerAOBaker pattern; `Bake_IsDeterministic` element-wise-identical test bar), run once per profile via an explicit Extract action — never on the ~300 ms debounce.
- **D-05:** An **Advanced foldout** on the profile exposes per-reference weights and an explicit preserve/replace mode enum (user: "advanced selection triggers option 2"). The simple slider remains the default face; serialization carries both so the advanced layer can evolve without breaking profiles. Test matrix must cover the advanced path explicitly.

### AO / normal / roughness controls
- **D-06:** The profile is a **dual-channel live asset**, matching the house slider pattern (live uniform + debounced bake): sliders/gates live on the `NAMERStyleProfile`; attaching/selecting it in the Processor panel applies it to the preview on load/change; edits trigger the debounced re-Process, AND controls with existing live shader uniforms (AO strength `_OcclusionStrength`, `Shaders/NamerSurface.hlsl:85`) also respond instantly without re-Process. The bake persists final values so the saved packed asset always equals the preview.
- **D-07:** Normal-detail reduction and roughness simplification are **bake-side, before quantization** (STYL-06): roughness simplify runs after the 04.2 dip adoption and before `NamerPackAlphaBits` (`Compute/NamerEncode.hlsl:40`); normal flatten runs before octahedral encode in the surface-pack path. The packed format stores one AO channel — runtime AO blending is not representable and is out of scope.
- **D-08:** STYL-05's separate Runtime-AO / Painted-AO controls resolve to: run the AO remap on the **authored** occlusion path too (today `NamerComputePipeline.cs:143-150` uploads the authored map as raw source data with no remap), and add a **cavity floor** after contrast (`NamerAoRemap`, `Compute/NAMERAO.hlsl:28` — contrast > 1 currently saturates cavities to 0 with no floor) for the "avoid crushing cavities to black" requirement.

### Claude's Discretion
- Mid-slider blend curve shape, palette anchor count and k-means details (fixed-seed, deterministic), island-ID rasterization specifics, kernel file layout for `NAMERStylize` compute, exact gate/slider naming and foldout copy, debounce wiring details — all within existing house patterns.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Requirements + roadmap
- `.planning/REQUIREMENTS.md` §Phase 5 — STYL-01..06 acceptance criteria; §UI-03/§UI-04 notes marking stylized-preview + remaining controls as Phase 5 scope
- `.planning/ROADMAP.md` §Phase 5 — goal, mode mvp, plan skeleton (05-01 profile + extraction, 05-02 palette mapping + smoothing kernels, 05-03 AO/N/R controls + integration)

### Prior phase decisions (binding context)
- `.planning/phases/04.1-baked-response-roughness-extraction-zero-residual-one-textur/04.1-CONTEXT.md` — D-01 authored-wins gates; D-13 one-texture primary mode
- `.planning/phases/04.2-gouraud-projection-one-texture-with-roughness-transfer-resid/04.2-CONTEXT.md` — residual dividend semantics (updated by D-02 here); dip-transfer formula
- `.planning/phases/04.3-adaptive-residual-resolution-lod-texture-tiers-residual-size/04.3-CONTEXT.md` — D-16 coverage ladder, D-17 manual override

### Code anchors
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:135-171` — D-07 AO gate (authored map = raw source, un-remapped today; bake on demand; luminance fallback already retired) and the Process insertion neighborhood for D-01
- `Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute` — kernel map: `CSRasterizeVertexColors:112`, `CSResidual:170`, `CSDilateResidual:257` (neighborhood-loop + coverage-gate pattern to extend), `CSProjectBase:302`, `CSReduce:364`
- `Packages/com.graffitientertainment.namer/Compute/NamerEncode.hlsl:40-55` — `NamerPackAlphaBits` + surface-pack alpha assembly (D-07 roughness-simplify insertion, before 6-bit quantization)
- `Packages/com.graffitientertainment.namer/Compute/NAMERAO.hlsl:28` — `NamerAoRemap` (strength/contrast; D-08 cavity-floor insertion)
- `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl:26,36,85,107` — live uniforms `_OcclusionStrength` / `_DbgEnableNormal` (dual-channel instant-response path, D-06)
- `Packages/com.graffitientertainment.namer/Editor/Bake/NamerAOBaker.cs:52,298` + `Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs:49` — deterministic CPU Burst job pattern + `Bake_IsDeterministic` test bar (D-04 extraction template)
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:875-940` — Stage & Shader Gates row (gate UI idiom for D-06 profile gates)

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `CSDilateResidual` neighborhood loop + `_VcInterp.a` coverage gate — the exact kernel shape the bilateral smoothing extends (radius loop, coverage-mask rejection, ping-pong RT)
- `CSRasterizeVertexColors` dispatch — reuse its shape to rasterize the island-ID buffer (D-03)
- `NamerAOBaker` IJobParallelFor Burst job + fixed tables — the determinism template for palette/stat extraction (STYL-02)
- `NamerAOBakeTests.Bake_IsDeterministic` — the element-wise-identical test bar to replicate for extraction determinism
- Live uniforms `_OcclusionStrength`/`_DbgEnableNormal` in `NamerSurface.hlsl` — the instant-response channel for D-06

### Established Patterns
- Debounced re-Process (~300 ms) on slider change — how profile edits reach the bake; extraction stays OFF this path (explicit Extract only)
- Stage & Shader Gates row — stage gates re-Process; shader-only gates respond live (`ApplyDebugGates()`) — the profile's gates follow the same split
- Authored-wins (D-07 gate, clause 0): authored maps are source data — stylization controls shape them through remap (D-08), never replace them
- Ping-pong RT compute with pooled leases (`_pool.Lease`) — smoothing iterations use the same pooling

### Integration Points
- `NamerComputePipeline.Process`: stylize stage inserts after normalize/clean, before the projection run (D-01); profile + gates plumbed like existing stage settings
- `NamerEncode.hlsl` surface-pack: roughness-simplify + normal-flatten uniforms consumed inside the pack path pre-quantization (D-07)
- `NamerAOPipeline`/`NAMERAO.hlsl`: authored-path remap call + cavity floor (D-08)
- `NamerEditorWindow`: profile attach/select UI + Advanced foldout (D-05/D-06); stylized-NAMER preview mode (UI-04)
- `NamerStyleProfile` ScriptableObject: new asset type under the package (STYL-01), Editor creation menu + serializer for 3–10 refs, merged fingerprint, sliders, gates, advanced weights/enum

</code_context>

<specifics>
## Specific Ideas

- User's model of the profile (quoted, confirmed as D-06): "a one time 'Process' button stores information (asset); that asset has sliders and gates that update the asset, which if attached to the preview model in the panel or selected in the panel will just set the real-time parameters on load/change."
- User on the hue control (quoted, confirmed as D-05): "Probably both, like an advanced selection triggers option 2" — simple slider by default, advanced mode exposes the enum + per-ref weights.
- Hue identity example from STYL-03: dark brown → styled dark brown, green stays green — anchor matching is by hue, never by raw RGB distance.
- Smoothing must survive decals and painted hard edges: bilateral range parameter keeps large transitions while killing photographic noise (STYL-04 wording is denoise-and-preserve, not abstraction).

</specifics>

<deferred>
## Deferred Ideas

- Guided filter — rejected for this phase (no offline-bake benefit; 4–6 extra dispatches, no reusable intermediates)
- Kuwahara painterly variant — future selectable filter if artists demand a painterly look; would ride the same island-ID gate
- Bilateral + Kuwahara dropdown — doubles kernels/fixtures; revisit on user demand
- Live-uniform-only stylization — not representable in the packed format without a bake; rejected (preview would diverge from saved output)
- Global color transfer / arbitrary-size SVD (MathNet-class features) — explicitly out of scope per stack decisions

</deferred>

---

*Phase: 5-Stylization*
*Context gathered: 2026-09-23*

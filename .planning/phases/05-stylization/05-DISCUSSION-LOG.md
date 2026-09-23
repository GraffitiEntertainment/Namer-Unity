# Phase 5: Stylization - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-09-23
**Phase:** 5-Stylization
**Areas discussed:** Pipeline position, Smoothing filter look, Hue preservation + palette, AO/normal/roughness controls

---

## Pipeline position

| Option | Description | Selected |
|--------|-------------|----------|
| A — Before the fit | One unconditional stage after normalize, before fit/projection; styles all three modes incl. one-texture default | ✓ |
| B — After decomposition | Post-pass per PROJECT.md's listed order; default one-texture mode can't be styled without re-running the fit | |

**User's choice:** Initially B ("I think after decomposition, because the stylize can be applied to the Vertex Colors right?"), then A after clarification
**Notes:** Clarified that the style lands in the VCs either way — the fit paints VCs from the UV image, so stylizing the source before the fit is what makes the VCs come out stylized. Mesh-domain VC editing was rejected: no edge-preserving smoothing in atlas space (STYL-04 islands/seams/decals), and an un-stylized residual re-adds original detail. 04.2 residual-dividend referent becomes "styled source" (recorded as D-02).

## Smoothing filter look

| Option | Description | Selected |
|--------|-------------|----------|
| Island-gated bilateral | Denoise-and-preserve, look-neutral; style identity stays in palette mapping; 2–3 iterations at strong flatten | ✓ |
| Island-gated Kuwahara | Painterly/cartoon abstraction; hard-coded look tension with style-agnostic mandate | |

**User's choice:** Island-gated bilateral
**Notes:** Both options require the new UV-island-ID buffer (binary coverage mask fails when atlas gutter < filter radius). Guided filter rejected outright (no offline-bake benefit, 4–6 dispatches).

## Hue preservation + palette

| Option | Description | Selected |
|--------|-------------|----------|
| One slider + merged palette | Slider 0=original → 1=full palette snap; merged fingerprint from all refs; deterministic Burst extraction | ✓ |
| Modes + weighted refs | Preserve/Balanced/Replace enum + per-image weights + two operators | |

**User's choice:** "Probably both, like an advanced selection triggers option 2"
**Notes:** Resolved as single-slider default face + Advanced foldout exposing per-ref weights and preserve/replace enum (D-04/D-05). Hue identity: anchor match by hue, luma/sat transfer toward anchor, replacement factor ramps to 1 at slider max.

## AO/normal/roughness controls

| Option | Description | Selected |
|--------|-------------|----------|
| All-bake stage controls | Persisted sliders + stage gate; bake before quantization (STYL-06); live uniforms only as follow-on | ✓ |
| Hybrid bake + live | Bake N/R, live AO uniforms; needs second AO texture the locked format has no room for | |

**User's choice:** Initially neither as-worded — user described their mental model: "the extraction of the styling, basically is a one time 'Process' button stores information (asset?) that asset has sliders and gates that update the asset, which if attached to the preview model in the panel or selected in the panel will just set the real-time parameters on load/change". Confirmed as dual-channel.
**Notes:** Synthesized as D-06: profile SO + one-time Extract; attach/select applies on load/change; edits trigger ~300 ms debounced re-Process AND instant response where live uniforms exist (`_OcclusionStrength`); saved output always equals preview. D-08 records the authored-AO remap + cavity-floor gap fix (authored map currently uploads un-remapped at `NamerComputePipeline.cs:143-150`; contrast > 1 saturates cavities to 0).

---

## Claude's Discretion

Blend-curve shape, palette anchor count / k-means details, island-ID rasterization specifics, kernel file layout, gate/slider naming and foldout copy, debounce wiring — within existing house patterns.

## Deferred Ideas

- Guided filter — no offline-bake benefit; 4–6 extra dispatches
- Kuwahara painterly variant — future selectable filter if artists demand it
- Bilateral + Kuwahara dropdown — doubles kernels/fixtures; revisit on demand
- Live-uniform-only stylization — not representable in the packed format without a bake

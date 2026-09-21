# Phase 4: Vertex-Color Decomposition + Residual - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-08-31
**Phase:** 4-vertex-color-decomposition-residual
**Areas discussed:** Runtime recombination, Opt-in flow & outputs, Error reporting & debug views, Adaptive residual & no-texture endgame

---

## Runtime recombination

| Option | Description | Selected |
|--------|-------------|----------|
| Multiply (vColor × residual) | Direct product; matches the documented model and existing shader | |
| Additive (vColor + residual) | Sum; different residual semantics | |
| You decide via research | Research picks with locked constraints (Color32, error-bounded, packed contract untouched) | ✓ |

**User's choice:** You decide via research
**Notes:** Research is informed by the standing multiply implementation in `NamerSurface.hlsl` (`InitializeNamerSurfaceData`) and PROJECT.md's documented model — deviating to additive/hybrid requires shader changes and SHDR re-verification.

| Option | Description | Selected |
|--------|-------------|----------|
| HDR EXR always | R16G16B16A16_SFloat residual on disk, never quantized | ✓ |
| Adaptive LDR→EXR | PNG when residual is LDR-range, EXR when HDR | |
| LDR PNG always | Smaller files, loses HDR residual range | |

**User's choice:** HDR EXR always
**Notes:** AssetGenerator's EXR path (stubbed since Phase 3) is the route.

| Option | Description | Selected |
|--------|-------------|----------|
| Own convention | Unity documents its own scheme; Blender heuristic not mirrored | ✓ |
| Mirror Blender | Port `assign_vertex_colors` per-loop nearest-texel scheme | |

**User's choice:** Own convention
**Notes:** The Blender code was read during discussion: per-loop nearest-texel averaging is the "simple averaging" VCOL-01 forbids; surface-texture decode compatibility is unaffected (Blender never consumes vertex-color materials).

| Option | Description | Selected |
|--------|-------------|----------|
| Constant 1.0 | Alpha unused | |
| Fit-quality signal | Per-vertex fit confidence 0–1, debuggable in-engine | ✓ |
| Research decides | Defer to Phase-4 research | |

**User's choice:** Fit-quality signal
**Notes:** User overrode the recommendation-free option; replaces Blender's luminance-variance alpha heuristic.

---

## Opt-in flow & outputs

| Option | Description | Selected |
|--------|-------------|----------|
| Off, explicit opt-in | Process output stays Phase-3 shape until enabled | ✓ |
| On by default | Decomposition is the default Process behavior | |

**User's choice:** Off, explicit opt-in

| Option | Description | Selected |
|--------|-------------|----------|
| Full set incl. Base PNG | Split mesh + residual EXR + Base PNG + Surface PNG + .mat | ✓ |
| Replace Base PNG | Only the decomposed representation is written | |

**User's choice:** Full set incl. Base PNG
**Notes:** Both representations on disk because the user can switch between them (see Area 4 toggle).

| Option | Description | Selected |
|--------|-------------|----------|
| Bind residual | Material + mesh pair reconstruct base at runtime; the memory win | ✓ |
| Keep Base PNG bound | Texture stays bound; decomposition is informational only | |

**User's choice:** Bind residual

| Option | Description | Selected |
|--------|-------------|----------|
| Preview = decomposed | Preview parity with generated output | ✓ |
| Preview original only | Preview keeps showing the non-decomposed result | |

**User's choice:** Preview = decomposed
**Notes:** Explicitly avoids repeating 03.1-W1's preview/generated divergence.

---

## Error reporting & debug views

| Option | Description | Selected |
|--------|-------------|----------|
| Window stats block | Coverage %, avg/max error, residual requirement, chosen resolution | ✓ |
| Console only | Stats logged, not visible in-window | |
| Both | Window block + console log | |

**User's choice:** Window stats block

| Option | Description | Selected |
|--------|-------------|----------|
| Debug channels | Vertex Colors / Residual / Error Heatmap join the existing toolbar | ✓ |
| Dedicated panel | Separate inspection panel | |

**User's choice:** Debug channels

| Option | Description | Selected |
|--------|-------------|----------|
| Color ramp | Perceptually-uniform (viridis-style) mapping of error magnitude | ✓ |
| Grayscale | Plain intensity mapping | |

**User's choice:** Color ramp

| Option | Description | Selected |
|--------|-------------|----------|
| Live, debounced | Stats update with the debounced preview recompute | ✓ |
| After Process only | Stats refresh only on generation | |

**User's choice:** Live, debounced

---

## Adaptive residual & no-texture endgame

| Option | Description | Selected |
|--------|-------------|----------|
| Auto-drop below threshold | Texture-free material when the fit suffices; stats say "not required" | ✓ |
| Always write residual | Residual written even when near-zero | |
| Explicit user action only | Reported but still written/bound until the user acts | |

**User's choice:** Auto-drop below threshold
**Notes:** User added scope in free text: "we are going to need a button so that this vertex coloring can be used or not, meaning, it can be toggled on or off and it might be that we need a processed mesh to switch between as a result" — captured as D-14 (on/off switch + both representations retained).

| Option | Description | Selected |
|--------|-------------|----------|
| User-tunable in UI | Window control beside the toggle, EditorPrefs-persisted | ✓ |
| Fixed constant | Development-time constant, no knob | |

**User's choice:** User-tunable in UI

| Option | Description | Selected |
|--------|-------------|----------|
| Downward from source res | Halve while error stays within threshold; never worse than today | ✓ |
| Upward from small | Start small, double until within threshold | |
| You decide via research | Research picks with locked constraints | |

**User's choice:** Downward from source res

| Option | Description | Selected |
|--------|-------------|----------|
| Ladder dropdown | Snaps to the halving steps the search uses | ✓ |
| Exact pixel input | Any power-of-two resolution | |
| Both | Dropdown + Custom… entry | |

**User's choice:** Ladder dropdown

---

## Claude's Discretion

- Recombination math (multiply vs additive vs hybrid) — research decides under D-01's locked constraints; multiply is the standing implementation.
- LSQ internals: samples per triangle, barycentric sampling pattern, 3×3 normal-equations accumulation, hand-rolled solver (Burst + Unity.Mathematics, no SVD library).
- Seam-splitting specifics beyond required UV seams; attribute-preservation details.
- Error-metric definition (perceptual vs RGB) — one consistent metric across stats, heatmap, and adaptive search.
- D-14 switching mechanism (materials + mesh swap vs keyword) — instant, no reprocess.
- Stats-block placement and channel UI layout.

## Deferred Ideas

- Stylization × decomposition interplay — Phase 5
- Batch decomposition — v2 BATCH-01
- Blender-side consumption of vertex-color materials — explicitly out
- Literal Blender fixture parity for vertex colors — not applicable under own-convention decision

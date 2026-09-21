---
title: Anchored-inverted roughness polarity — scalar anchors, Sobel dips toward gloss
date: 2026-09-16
context: Exploration session (/gsd-explore) during Phase 04.1 UAT round 4; revises the extraction polarity premise of [[roughness-extraction-one-texture-mode]]
source: Socratic exploration; Blender facts verified against namer_core.py and Principled BSDF behavior
---

# Anchored-inverted roughness polarity

Revises the extraction design recorded in `roughness-extraction-one-texture-mode.md` (the
Phase 4.1 input): the Sobel edge-magnitude map is **no longer used directly as the roughness
level**. The authored roughness scalar anchors the map; Sobel magnitude pulls texels *down*
from that anchor toward gloss.

## The decision

- **The 6 roughness bits are the complete gloss dial.** No separate specular channel is
  needed or useful: `smoothness = 1 − roughness` at decode already covers it
  (`NamerSurface.hlsl:73-74`), and non-metal specular strength is a near-constant Fresnel
  ~4% in any renderer, actual Blender included. "Packing specular/roughness into 0..1" is
  exactly what the format already does.
- **No thresholds.** Roughness is a continuous GGX dial (0 = mirror, 1 = diffuse); ~0.4–0.5
  is only the perceptual gloss/matte line, not a semantic boundary. No `.5 split` anywhere,
  in actual Blender or in ours.
- **The authored scalar anchors the map.** Whatever the source material claimed
  (`_Roughness`, i.e. `1 − _Smoothness`) sets the quiet-region level. Matte sources stay
  matte — how actual Blender treats that material. This removes the failure where the
  map's absolute level was normalization-dependent (arbitrary) and disconnected from the
  authored claim.
- **Sobel magnitude dips toward gloss.** Edges and high-contrast features (baked specular
  rims in AI albedos) pull roughness *down* (shiny); quiet grain stays at the anchor.
  Motivating case (Neo, `_Smoothness: 0`, fit strength ≈ 0.9): quiet regions must pack
  ≈ 0.9–1.0 matte instead of the observed 0.37 gloss. At a matte anchor 1.0 is the
  ceiling, so anchoring *forces* the inverted polarity — Sobel can only subtract.
- **p90 robust scale retained** (UAT round 4). The heavy-tail problem is polarity-
  independent; under `np.max` the dip would be invisible everywhere except the few extreme
  edges.
- **Deliberate departure from `namer_core.py extract_roughness`**
  (`np.clip(edge_magnitude/np.max, 0, 1)`, direct polarity). The user identified that
  function as a testing idea for extracting roughness, not ground truth; the behavioral
  reference is how actual Blender (the product) treats an authored material. Documented
  as parity deviation #2 (deviation #1 = p90 vs np.max, UAT round 4).

## Why (reasoning chain)

1. UAT round 4: np.max normalization collapsed smooth regions to ~2–3% roughness →
   mirror gloss; fixed with p90 robust scale (smooth ≈ 0.3).
2. Fit-driven still read glossy: it adopts the Sobel texture at full searched strength
   (~0.9), landing quiet regions at lerp(1.0, 0.3, 0.9) ≈ 0.37 — below the perceptual
   gloss line. The user's liked reference look was ≈ 0.72 (estimator at slider 0.28).
3. The absolute level of `mag/scale` is arbitrary — set by normalization, not by the
   material. No renderer would treat a `_Smoothness: 0` material as 0.37-roughness.
4. Therefore the authored scalar must set the level, and Sobel can only add variation
   around it. At a matte anchor that means edges dip shiny — which is also the correct
   read of baked specular rims in AI albedos.

## Open implementation questions (for the implementing plan, not this note)

- Exact remap formula: `saturate(scalar − strength · mag/p90)` (dip-depth semantics) vs
  `lerp(scalar, 1 − mag/p90, strength)` (endpoint-lerp semantics). Dip-depth keeps the
  scalar as a true anchor for glossy sources too; endpoint-lerp ignores it at full
  strength.
- Fit-driven strength search semantics under the anchored map, and whether pack still
  adopts at full searched strength.
- All three existing Sobel test premises flip:
  `Sobel_FlatBase_YieldsNearZeroRoughness` → flat base yields ≈ scalar;
  `Sobel_SharpEdge_YieldsHighRoughness` → sharp edge yields *low* roughness (gloss dip);
  `Sobel_SparseExtremeEdges_SmoothRegionsStayRough` → smooth median ≈ scalar, edge texel
  strictly below it.
- UAT round-5 entry must record parity deviation #2 (polarity) alongside #1 (p90).

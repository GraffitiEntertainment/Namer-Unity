---
title: Plan the Gouraud-projection one-texture + roughness-transfer redesign
date: 2026-09-16
priority: high
---

# Plan the Gouraud-projection one-texture + roughness-transfer redesign

Turn `.planning/notes/sobel-led-residual-guided-roughness.md` (round-2 design) into an
executable plan:

1. **Projection kernel** — cleaned base := per-triangle Gouraud-representable projection
   of the albedo (reuses the 16×16 barycentric fit machinery, writing the fitted surface
   back), so the residual quotient is white by construction; ladder search and the honest
   threshold gamble retire.
2. **Roughness transfer kernel** — luminance-split of (albedo − projected) modulates the
   roughness map via the surviving D-08 consume site; dip depth = pure taste slider (no
   precompute); one decided polarity treatment for dark baked response.
3. **Residual on/off checkbox** — explicit override; with projection it defaults to
   near-always-passing.
4. **Estimator dropdown consolidation** — one extraction path + decomposition on/off;
   decide whether Sobel-of-base survives as an alternate dip source.
5. **UV-overlap handling** — depends on the research question filed in
   `.planning/research/questions.md`; at minimum an honest "these spots stay" statement.

## Entry point

Gap-closure plan feeding off Phase 04.1's UAT round-5 verdict once the phase closes
(`04.1-UAT.md` round 5 is still pending; this exploration is its substance). Route via
`/gsd-plan-phase --gaps`.

## Acceptance sketch

- A processed asset's residual measures white everywhere except UV-overlap texels and
  ≤8-bit quantization dust — no threshold tuning required.
- The removed detail is visibly re-expressed as gloss variation (slider-controlled), not
  lost; chroma grain loss is bounded and documented.
- One-texture outcome (no EXR) with the checkbox off-by-default override.
- Existing 04.1-07 dip-coupling tests flip again, intentionally.

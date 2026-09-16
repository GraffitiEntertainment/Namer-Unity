---
title: Plan the Sobel-led, residual-guided roughness redesign
date: 2026-09-16
priority: high
---

# Plan the Sobel-led, residual-guided roughness redesign

Turn `.planning/notes/sobel-led-residual-guided-roughness.md` into an executable plan:
sever the global removal↔dip coupling (slider owns dip, ladder owns removal), add the
residual-derived correction mask (one mask softening removal + dip locally), add the
residual on/off checkbox, and resolve the precomputed-slider-default open question
(removal-strength exposure vs. a gloss-side criterion — a dip search against the residual
is degenerate).

## Entry point

Likely a gap-closure plan feeding off Phase 04.1's UAT round-5 verdict once the phase
closes (`04.1-UAT.md` round 5 is still pending and this exploration is its substance:
fit-driven look rejected, new direction chosen). Route via `/gsd-plan-phase --gaps` or
`/gsd-quick` for the UI-only slice (checkbox) if it lands first.

## Acceptance sketch

- Fit-driven and Sobel modes produce the same slider-driven dip character; the ladder
  result no longer changes gloss depth.
- Neo: residual hotspots shrink measurably after the correction pass; one-texture outcome
  reachable without a strength-1.0 global character.
- Existing pinned tests updated intentionally (the dip-coupling tests from 04.1-07 flip
  again, by design).

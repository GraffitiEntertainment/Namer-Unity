---
title: Sobel-led, residual-guided roughness — slider owns the dip, the residual mask owns the hotspots
date: 2026-09-16
context: Exploration session (/gsd-explore) mid-Phase-04.1, triggered by the UAT round-5-early verdict that the fit-driven map (rung ~1.0 on Neo ⇒ ≈ 1−sobel) looks bad; revises the strength ownership of [[anchored-inverted-roughness-polarity]]
source: Socratic exploration; Neo residual EXR inspected on disk (Assets/NAMERGenerated/Neo-T-Pose/tripo_mat_d83278e6_Namer_Residual.exr)
---

# Sobel-led, residual-guided roughness

Verdict that motivated this: the fit-driven estimator's map — dip depth = the searched
sharp-removal rung, ~0.9–1.0 on Neo — reads as an inverted Sobel edge map and "looks like
shit" next to the Sobel-mode map tuned by the slider. Root cause is not the D-08 formula but
**strength ownership**: one `_Strength` uniform (NamerRoughnessPipeline.cs:416) drives both
`CSSharpRemoval` and `CSRoughnessRemap`, so the rung that collapses the base silently also
sets global gloss depth — and the fit has no gloss signal at all
(`EvaluateRefitMaxError` never reads the roughness texture).

## The redesign

| Concern | Owner |
|---|---|
| Residual collapse (one-texture) | Ladder search — unchanged machinery: first-passing minimal sharp-removal rung, honest `FitOnlyMaxError` gate |
| Gloss look | **The user slider** — dip depth is taste, not a search artifact; the Sobel-mode map character becomes the default everywhere |
| Local over-fire | **Residual correction mask** — `soften = 1 − blur(|residual − 1|)` derived from the already-computed residual; locally reduces removal *and* dip together (D-08 coupling preserved locally, severed globally) |
| Honest gate | Unchanged — residual is dropped only when the post-correction re-fit measures within threshold |

Supersedes the *global* half of D-08's one-scalar coupling (the formula
`saturate(scalar − strength·mag/p90)` survives; who owns `strength` changes). D-10's
full-adoption pin (`isFitDriven ? 1f`) becomes moot once the slider owns the dip — the
`_RoughnessDipApplied` pack-branch semantics need re-derivation in the implementing plan.

## Evidence

- Neo's residual is ~99% white with small, scattered, mild-gray hotspots on the figure
  surfaces (inspected via sips EXR→PNG, 2026-09-16). Dark-on-white ⇒ reconstruction
  *overshoots* the original at those texels (quotient < 1). The collapse is nearly there —
  a local touch-up, not a higher global rung, is the missing piece.
- The hotspots coincide (per user read) with over-extraction — the same edges where dip ≈ 1
  and strength-1.0 sharp-removal over-fires, because both share the rung.

## UI additions requested in the same session

1. **Residual on/off checkbox** — explicit user override of the honest gate: force the
   residual texture off (accept the visual error, ship one texture anyway) or on (keep the
   EXR even when the fit passed). The gate stays the *default*, the checkbox is the escape
   hatch.
2. **A precomputed slider default** — "precomputed for the most white residual texture".
   Open question for planning: **dip is albedo-residual-invariant** (the residual is a
   quotient on `cleanedBase × vc`; gloss never enters it), so a whitest-residual criterion
   cannot select a dip value — the search would be flat. Candidate coherent readings:
   (a) expose the ladder-picked *removal* strength as a visible slider pre-set to the
   searched value (precompute exists, just hidden today); (b) define a gloss-side criterion
   for the dip default (new statistic — needs a spec). Planning must pick one; do not
   implement a dip search against the residual.

## Open details deferred to planning

- Single correction pass (correct → re-fit VCs → re-measure) vs. iterate-until-white loop.
  Start single-pass; the re-fit/re-measure cycle already exists as ladder machinery.
- Mask threshold and blur radius; whether the mask also gates the VC refit.
- Estimator dropdown meaning after the change — degenerates to "one-texture machinery
  on/off"; renaming is a UX decision.

---
*Related: [[anchored-inverted-roughness-polarity]] (formula survives, ownership revised),
[[roughness-extraction-one-texture-mode]] (original phase input).*

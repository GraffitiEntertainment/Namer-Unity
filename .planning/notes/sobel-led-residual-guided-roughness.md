---
title: Gouraud-projection one-texture with roughness transfer (round-2 design; supersedes the round-1 sobel-led/mask design in this same note's history)
date: 2026-09-16 (round 2, same session)
context: /gsd-explore continuation during Phase 04.1; round 1 captured the ladder+mask design, round 2 replaced its core after code-verified research; revises the strength ownership of [[anchored-inverted-roughness-polarity]]
source: Socratic exploration; code findings verified by gsd-phase-researcher with file:line evidence; Neo residual EXR inspected on disk (Assets/NAMERGenerated/Neo-T-Pose/tripo_mat_d83278e6_Namer_Residual.exr)
---

# Gouraud-projection one-texture with roughness transfer

## Motivation (both rounds)

The fit-driven map (dip = the searched sharp-removal rung, ~0.9–1.0 on Neo) reads as an
inverted Sobel edge map and loses the look comparison against the slider-tuned Sobel map.
Root cause is **strength ownership**: one `_Strength` uniform
(`NamerRoughnessPipeline.cs:416`) drives both `CSSharpRemoval` and `CSRoughnessRemap`, while
the fit has no gloss signal (`EvaluateRefitMaxError` never reads the roughness texture).

## Research-verified mechanics (round 2)

- **The residual quotient is `cleanedBase / vcInterp`** in linear space with a 1e-3 vc
  floor (`Compute/NAMERDecomp.compute:130-141`); the dividend is the **cleaned base, not
  the source albedo**. The residual measures Gouraud-reconstruction of whatever base is
  handed in — it never measured source fidelity. `FitOnlyMaxError` = per-texel mean-channel
  MAE of vcInterp vs base (`NAMERDecomp.compute:166-167`).
- VCs are per-triangle barycentric LSQ on a 16×16 interior grid, vertex-aggregated by
  unweighted mean, output piecewise-linear (Gouraud) at vertex density, Color32-quantized
  (`VertexColorFitter.cs:104,265-270,389-415`). **Mid frequencies — coarser than texel
  clusters, finer than a triangle's UV footprint — are structurally unrepresentable.**
- The current smoother is a single global texel-space gaussian (radius
  `clamp(max(w,h)/32, 8, 64)`, σ = radius/3 ≈ 21 texels at 2K; `NamerRoughnessPipeline.cs:48-51`),
  not island- or triangle-aware; it under-smooths large-triangle regions and bleeds across
  UV seams. `CSRoughnessSharpRemoval`'s `saturate()` is mathematically inert (convex
  combination `(1−s)·base + s·blurred`).
- **Overlapping UVs rasterize first-covering-triangle-wins**
  (`NAMERDecomp.compute:111-113`) — on tripo/AI meshes one triangle's vcInterp lands on
  another's texels; guaranteed mismatch no smoothing can fix. Likely part of Neo's
  scattered non-white spots.
- Literature pointers [ASSUMED, unverified — search rate-limited]: bilateral filtering
  (Tomasi & Manduchi 1998); Mesh Colors (Yuksel et al. 2010) for the per-triangle
  hybrid representation gap; xatlas-style seam-aware dilation.

## The round-2 design (decided)

| Element | Design |
|---|---|
| Residual collapse | **By construction**: the cleaned base is the projection of the albedo onto the Gouraud-representable space (per-triangle) — the base says exactly what the VCs can say, so `base/vcInterp` ≡ white up to Color32 quantization. Ladder search, threshold gamble, and the round-1 correction mask all retire |
| Removed signal (albedo − projected) | Luminance component **transfers to the roughness map** (dip where baked response was bright). Chroma grain: accepted loss — a scalar gloss channel has no home for it |
| Dip depth | **Pure taste slider** (user decision, round 2) — no precompute, no search; the round-1 "precomputed for whitest residual" question dissolves (nothing left to precompute) |
| Residual checkbox | Explicit on/off override of the (now near-always-passing) gate — "drop it, I accept the quantization dust" |
| Estimator split | Largely retires: one extraction path + decomposition on/off. Whether Sobel-of-base survives as an alternate dip source is a planning call |
| D-08 formula | Survives as the consume site (`saturate(scalar − strength·mag)`); the *source signal* changes from Sobel-of-base to the transferred removed-detail luminance |

## Transfer caveats (decided constraints, details for planning)

1. **Luminance-only transfer** — the high-pass signal needs a luminance/chroma split; chroma
   cannot cross into the scalar gloss channel.
2. **Polarity by phenomenon** — bright baked speckle → dip toward gloss is physical; dark
   baked occlusion → matte is a stylization choice, not physics. The transfer function
   needs one decided sign treatment for dark response.
3. 6-bit roughness saturation headroom: heavy transfer can clip at both ends.

## Open details deferred to planning

- Exact projection form (per-triangle fitted Gouraud surface evaluated into the base —
  likely reuses the existing 16×16 fit machinery, writing the fitted surface back).
- UV-overlap texels: unaffected by projection; needs detection/repair (see research
  question) or an honest "these spots stay" statement.
- What the estimator dropdown becomes; renaming is a UX decision.
- Existing pinned tests flip intentionally again (04.1-07's dip-coupling tests).

## Provenance

Round 1 (same session, superseded core): slider-owns-dip + ladder-owns-removal +
residual-mask correction pass. Survivors into round 2: the taste slider, the checkbox, the
root-cause analysis. Superseded: the mask pass and the ladder's role in gloss — the
projection makes them unnecessary. Round 1 evidence (Neo residual ~99% white, mild
scattered dark spots = reconstruction overshoot) directly motivated asking why rung 1.0
stalled, which the research answered (frequency-band mismatch + UV overlaps).

---
*Related: [[anchored-inverted-roughness-polarity]] (formula survives, ownership revised),
[[roughness-extraction-one-texture-mode]] (original phase input).*

---
title: Baked-response roughness extraction + zero-residual one-texture mode
date: 2026-08-31
context: Exploration session (/gsd-explore) following Phase 4 verification; feeds the Phase 4.1 insert
source: Socratic exploration + gsd-phase-researcher pass on the Blender reference
---

# Baked-response roughness extraction → one-texture NAMER mode

## The scheme

Assets whose base texture has material response (gloss/shading/cavity) **baked in** — and no
dedicated roughness map (Neo is the motivating case) — inflate the Phase-4 residual with
high-frequency detail that is not albedo. Extract that baked response, store it as the
**roughness values** already carried by the packed surface format, and refit the cleaned base
into vertex colors. The residual then collapses, and D-13 (auto-drop) becomes the *primary*
path rather than the exception: **material = one RGBA8 surface PNG + a vertex-colored mesh**,
no base texture, no residual texture. Keep the residual-texture fallback for assets whose base
contains genuine albedo detail (decals, fabric weave, freckles) that survives extraction.

| Slot | Content |
|------|---------|
| Mesh vertex RGB | low-frequency base color — per-vertex gradients are fair game |
| Mesh vertex A | (as today) fit-quality/debug |
| Surface PNG R,G | octahedral normal |
| Surface PNG B | AO |
| Surface PNG A bit 7 / bit 6 | metallic / emissive flags |
| Surface PNG A bits 0–5 | 6-bit roughness — **values now extracted from baked base** |

**Format semantics do not change.** Bits 0–5 keep meaning "roughness"; only the *source* of
the values changes (derived from the base texture instead of a roughness map / scalar).
Golden-vector decode equivalence is untouched. `NamerSurface.hlsl` decode is untouched.

## Design decisions from the exploration

- **Fit-driven extraction, not just a Sobel pass.** Blender's `extract_roughness` is Sobel
  edge-magnitude on luminance — a heuristic. The Unity version should *calculate how much to
  extract* (a fit-driven strength knob, same philosophy as the adaptive resolution ladder:
  choose extraction strength to minimize post-refit residual).
- **Blur is the frequency separator.** Blurred base ≈ what vertex-color interpolation can hold
  (gradients included); sharp-minus-blur remainder is the extraction candidate.
- **Escape hatch:** an optional single RGBA shader input to offset roughness if per-texel
  6-bit extraction underfits — fallback only, not part of the one-texture happy path.
- **Honest gate:** "no residual texture" is earned per-asset by the fit, not assumed
  (D-13 extended: extraction → refit → drop residual when within threshold, else fallback).

## Blender reference findings (gsd-phase-researcher, 2026-08-31)

Read from the local reference copy at
`/Users/Shared/SSDevelopment/Development/GraffitiEntertainment/namer_plugin/namer_core.py`:

1. **The Blender NAMER format has NO residual texture.** Output = packed surface PNG
   (`create_namer_png`, RGBA8) + `_Base_Cleaned.png` + optional `_Matrix.png` + vertex colors.
   The Unity EXR residual is a Unity-only convention (Phase 04 decisions D-02/D-03), not part
   of the reference format. Dropping the residual texture moves us *toward* reference
   compatibility.
2. **Blender already extracts from the base:** `clean_base_texture` divides base by
   `ao × lighting` (clip 0.1–1.0); `extract_roughness` = Sobel edge-magnitude on luminance;
   `extract_metallic` = saturation threshold. Precedent exists; our version improves the
   estimator.
3. **Versioning = PNG tEXt metadata only** (`NAMER_PNG` JSON: format tag, channel map,
   `emissive_color`, `base_cleaned`, `extracted_textures` booleans). No header bits, no mode
   flags. Moot for this scheme (no format change), but relevant to any future format work.

Unverified: whether the local `namer_plugin` snapshot matches the develop-branch version
Phase 01 fetched.

## Dependency

Phase 4 verification left 3 open gaps that this work builds on — close them first
(`/gsd-plan-phase 4 --gaps`): CR-01 multi-material mesh pairing (invalidates multi-material
processing entirely), CR-02 residual Point-filter import, CR-03 tiling-UV coverage.

## Open questions for the phase

- Extraction estimator: Sobel-parity first (match Blender), then fit-driven strength? Or
  fit-driven from the start?
- When the fallback residual triggers on an extraction-processed asset, is the residual fit
  against the cleaned base (extraction still applied) or the original?
- Interaction with 03.1 AO extraction (both un-multiply from base — order and divisor
  composition need care).
- Neo (no roughness map, baked detail) is the acceptance case: processes to one texture,
  renders correctly, no residual file.

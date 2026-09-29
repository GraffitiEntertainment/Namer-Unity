# Intel: Requirements (PRD sources)

Extraction count: 0 PRDs in this ingest set — no REQ-{slug} entries extracted.

The single classified document is a DOC (research note), so its proposals are NOT extracted as
requirements. Requirement-candidates observed in the DOC intel (see `context.md`) for the
roadmapper's consideration, pending user resolution of the competing-variant warnings:

- Reference scene decomposition stage (post-process estimate + image decomposition + confidence
  mask over 3–10 references) — source: .planning/research/style_extraction.md
- Palette from recovered/de-graded base colors (variant of shipped STYL-02/D-04 — unresolved)
- Hue/role-aware vertex-color remapping in OKLab (variant of shipped D-01 pipeline position —
  unresolved)
- Bloom/specular/emissive/deep-shadow/fog/clipping pixel down-weighting during palette
  extraction (additive)
- Forward scene validation: render NAMER + recovered Volume, compare against reference style
  (additive; asset home and LUT representation unresolved)

Existing requirements these touch (see `.planning/REQUIREMENTS.md`): STYL-02, STYL-03,
STYL-07/08/09, STYL-10, STYL-11, STYL-12.

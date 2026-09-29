# Intel: Decisions (ADR sources)

Extraction count: 0 ADRs in this ingest set.

The single classified document (`.planning/research/style_extraction.md`) is a DOC (research
note, not locked, lowest precedence) — it contributes no extracted decisions.

Decision-shaped proposals from the DOC (roadmap restructure, palette source, vertex-path
remapping, LUT output asset) are recorded as annotated context in `context.md` and routed to
`.planning/INGEST-CONFLICTS.md` for user resolution. They must not be treated as decisions
without explicit user adoption.

Existing locked/confirmed decisions consulted during merge conflict detection (not part of this
ingest, listed for traceability):
- `.planning/phases/05-stylization/05-CONTEXT.md` — <decisions> D-01..D-08 (D-01 "stylize the
  source, then fit"; post-decomposition mesh-domain VC editing rejected; D-04 aggregate
  palette extraction from raw reference samples)
- `.planning/phases/05.1-reference-fitted-urp-post-processing-look-inserted/05.1-CONTEXT.md` —
  locked block "Locked by ROADMAP §Phase 5.1 (success criteria 1–5), not re-discussed",
  including parametric named Volume knobs with 3D-LUT/ColorLookup as plan-time fallback only
- `.planning/phases/05.2-standalone-post-look-asset-reference-fitted-urp-post-look-as/05.2-CONTEXT.md` —
  standalone NamerPostLook decisions (profile palette-only, ToneMatch retired)

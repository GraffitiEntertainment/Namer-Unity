# Intel: Constraints (SPEC sources)

Extraction count: 0 SPECs in this ingest set — no api-contract, schema, nfr, or protocol
constraint entries extracted.

The single classified document is a DOC (research note); it contains no spec-grade constraints.
Project-level constraints that bound any adoption of the doc's proposals remain those in
`.planning/PROJECT.md` (Constraints section): C# + HLSL compute only, URP first, Blender decode
equivalence, source-asset immutability, GPU compute for per-pixel work at 2K/4K, 6-bit
roughness / Color32 precision, UPM package form. Additionally, the locked 5.1 constraint
family applies to any post-look work: deterministic CPU editor-side fit, no new package
dependencies (`.planning/phases/05.1-reference-fitted-urp-post-processing-look-inserted/05.1-CONTEXT.md`).

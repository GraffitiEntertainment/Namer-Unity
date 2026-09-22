# Author review notes

## Main framing

The paper presents NAMER as **mesh-conditioned material compaction and appearance simplification**, with optional residual recovery and signed detail-to-roughness transfer. This is more precise than describing it as lossless PBR texture compression. The color-table extension is a separate prospective section rather than an implementation claim.

The paper uses the author line Kenneth Hurley / Genius Ventures. Confirm the desired affiliation and any coauthors before circulation. Add an email or identifier only as appropriate; neither has been invented in the manuscript.

## Important finding: the normal inverse has a branch restriction

The pinned implementation always chooses the larger root of its quadratic normal inverse. For a unit normal in the documented coordinate convention, that branch recovers the original input only when

```text
nx - ny + nz >= -1
```

An upper-hemisphere normal can violate this condition. The concrete example

```text
source  = (-1/sqrt(2), +1/sqrt(2), 0)
decoded = (-1/2,       +1/2,       1/sqrt(2))
```

has a 45-degree difference before byte quantization. Appendix A derives the result and the scalar script reproduces it. This is an equation-level finding, not a claimed GPU measurement. A corrected or explicitly restricted format should be tested before making a general normal-preservation claim. A conventional signed octahedral replacement would need versioned compatibility rather than an undocumented decoder change.

## Other points to preserve in claims

The current packed texture is uncompressed RGBA8, point sampled, without mipmaps. Fewer texture assets therefore does not by itself establish less resident memory than block-compressed conventional materials. The paper includes both uncompressed and BC7 byte-budget examples, with their assumptions visible.

A white residual after replacing the target with its vertex projection is an identity relative to that new target, not proof that the original color detail survived. Residual-on recovery instead uses the original normalized target and can require values above one. A zero vertex multiplier cannot be repaired by a finite multiplicative residual.

The code solves each triangle locally and averages incident solutions equally. It is not a global least-squares solver. The grid parameter 16 produces 105 interior samples per triangle, not 16.

The normalizer writes base alpha as one. General opacity preservation should be tested or implemented before it is claimed. The packed emissive field is binary, the inspected pack kernel uses a uniform emissive input, and runtime emission is a flag multiplied by a material color. Avoid claiming full source emission-map preservation.

AO factorization and shader recomposition cancel only under compatible settings. The divisor floor and decoded AO-strength multiplication can prevent exact cancellation.

The existing mesh conversion has scoped support: the examined workflow skips unsupported multi-mesh or multi-material decomposition, rejects out-of-range UVs, and does not copy blend shapes. Overlapping UVs require further attention.

## Results still to collect

The manuscript is a complete methods-and-analysis draft, but it contains no fabricated Unity benchmark or rendered comparison. For an empirical version, add real source/converted views, relighting and minification comparisons, deployed memory measurements, conversion and runtime timings, and the ablations described in Section 9.2. Record asset provenance, settings, hardware, graphics API, and the exact commit used.

For the palette extension, choose editor-baked vertex recoloring versus a shared runtime palette/weight representation, decide how RGB residuals should behave after recoloring, and distinguish palette simplification from actual brush-stroke synthesis.

## Reference and novelty review

The manuscript credits prior work on vertex interpolation, mesh-associated color, normal representations, texture compression, palette recoloring, and painterly rendering. This is a focused related-work treatment, not an exhaustive novelty or patent search. The main claim is the implemented combination and information-allocation workflow, not that its individual building blocks are unprecedented.

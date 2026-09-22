# NAMER paper revision notes

This revision reframes NAMER as a mesh-aware PBR material decomposition and reparameterization pipeline rather than mainly a vertex-color compression technique.

Major additions:

- Dedicated material-component recovery section.
- Geometry-derived AO synthesis: authored-map precedence, deterministic 64-ray hemispherical visibility, BVH tracing, 512x512 bake cap, 0.01 cage offset, 10% bounds ray distance, UV padding via jump flood, and AO blur/strength/contrast controls.
- Explicit explanation of why the earlier luminance-based AO estimator was retired from the automatic path.
- AO factorization/recomposition as a controlled separation of baked visibility from base color.
- Roughness recovery treated as a first-class component: Sobel fallback and signed projection-detail transfer.
- Current metallic/emissive semantics separated from future inference: current implementation transfers and packs source values; it does not yet infer missing values.
- New planned metallic and emissive inference section, with confidence/ambiguity caveats and links to single-image SVBRDF prior work.
- Palette/color-table work retained as a later stylization stage after material decomposition.
- Title, abstract, introduction, pipeline diagram, limitations, conclusion, traceability table, and bibliography updated to match the broader framing.

The manuscript remains careful not to claim that metallic/emissive inference is implemented at commit 9eb0685.

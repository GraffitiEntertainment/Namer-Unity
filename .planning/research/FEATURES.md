# Feature Research

**Domain:** Unity editor material-processing / stylization plugin (NAMER)
**Researched:** 2026-08-25
**Confidence:** MEDIUM

## Feature Landscape

The ecosystem NAMER enters is split across four tool families, none of which fully overlaps its value proposition:

1. **Texture/material authoring suites** (Adobe Substance 3D Painter/Sampler/Designer, ArmorPaint) — the "pro authoring" reference. Heavyweight, DCC-centric, procedural/painting-based rather than conversion-based.
2. **Texture channel packers** (unity-texture-packer, various channel-combiner assets) — single-purpose utilities that merge R/G/B/A channels but do no analysis, no geometry work, no stylization.
3. **Vertex color / mesh paint tools** (Unity Polybrush, MeshLab, Blender vertex paint) — general-purpose painting and baking, but no error-driven residual decomposition.
4. **Stylization / NPR shaders** (Flat Kit, Toony Colors Pro, Kuwahara-filter assets) — runtime rendering style, not baked-into-texture stylization.

NAMER's differentiator is the *combination*: compact bit-packed conversion + error-driven vertex-color decomposition + reference-image stylization, all as a non-destructive editor-time bake that never leaves Unity and never touches source assets. The sections below map every feature to table-stakes / differentiator / anti-feature so the roadmap can sequence the differentiators behind the table stakes.

### Table Stakes (Users Expect These)

Features every comparable tool has. Missing any of these makes NAMER feel broken even if the core tech works.

| Feature | Why Expected | Complexity | Notes |
|---------|--------------|------------|-------|
| Non-destructive output / source immutability | Any asset tool that overwrites sources is untrusted; every packer/painter writes to new assets | LOW | Generated assets under a dedicated directory (`NAMERGenerated/`); verify via tests |
| Source material inspection (read albedo, normal, metallic, roughness/smoothness, AO, emissive, alpha) | The tool must understand URP Lit + Standard inputs to convert them | MEDIUM | Must handle missing maps and scalar fallbacks; no hard taxonomy |
| Texture channel packing with source-channel selection | Channel packers exist solely for this; users expect to pick which source channel maps to which packed channel | MEDIUM | NAMER's packed format (octahedral normal RG, AO B, metallic/emissive/roughness in A) is fixed, so "selection" is partly automated but must be inspectable |
| Normal map generation (tangent-space → packed) | Every material tool emits a normal map | MEDIUM | Octahedral encode is the *format* differentiator, but "produces a usable normal" is table stakes |
| Before/after preview on a representative mesh | Users will not trust an invisible transform; even basic packers show a preview | MEDIUM | Sphere is acceptable fallback; actual mesh preferred (PRD requirement) |
| Export to a renderable material + runtime shader | The packed format is useless without a shader that decodes it | MEDIUM | URP NAMER shader is table stakes for the output to render at all |
| Batch processing over selections/folders | Production workflows have many assets; one-at-a-time is a demo | MEDIUM | Explicit-command batch (not auto-import) in v1 |
| Editor window with menu entry and clear workflow | Discoverability; "Tools > NAMER > Processor" | LOW | Basic UI scaffolding |
| Deterministic, re-runnable processing | Users need reproducible output; same input → same output | LOW-MEDIUM | No randomness in encode/pack; stylization seeded if stochastic |
| Sensible defaults for missing maps | Real FBX files are inconsistent; missing roughness/AO/metallic is common | LOW-MEDIUM | Scalar fallbacks + neutral-map defaults |

### Differentiators (Competitive Advantage)

These are where NAMER wins. They align with the Core Value: reduce texture count/memory and add optional stylization, all Unity-native. Do not cut these to ship faster — they are the product.

| Feature | Value Proposition | Complexity | Notes |
|---------|-------------------|------------|-------|
| Vertex-color decomposition with barycentric least-squares fitting + residual texture | Moves low-frequency color into vertex colors, shrinking the color texture; no mainstream Unity tool does error-driven texture→vertex-color with a residual | HIGH | Per-triangle multi-sample fit, not averaging; `BaseColor ≈ VertexColorInterpolation × ResidualColor` |
| Reconstruction-error metric + adaptive residual resolution | Measured error (not a fixed ratio) decides how much the residual texture can shrink — this is the memory win | HIGH | Expose coverage / mean / max error stats and an error heatmap |
| Reference-image-driven stylization (NAMERStyleProfile) | 3–10 reference images define art direction; palette extraction + hue-aware mapping preserves source hue identity | HIGH | Distinct from Substance's procedural/smart-material approach; profile is a reusable ScriptableObject |
| Octahedral normal + 6-bit roughness + bit-packed metallic/emissive in one RGBA | Compact runtime format: 2 textures + vertex colors vs. a full PBR set | MEDIUM | Roughness limited to 64 values is a feature (deterministic, compact), not a bug |
| Dual AO model (runtime AO vs. painted/cavity AO) | Strong stylized AO without crushing cavities; runtime AO stays in texture, painted AO blends into base color | MEDIUM | Expose both controls separately |
| Debug channel visualization (vertex color, residual, AO, normal, roughness, metallic, emissive, reconstruction error) | Lets artists verify correctness and tune; nearly unique for a conversion tool | MEDIUM | Each debug view is a shader toggle / preview mode |
| Vertex splitting at seams/discontinuities | Correct color at UV seams and hard boundaries instead of smearing | MEDIUM | Required for decomposition to not look broken on real meshes |
| Blender NAMER format compatibility (decode-equivalent) | Cross-tool workflow equivalency; Blender users can round-trip | MEDIUM | Compatibility tests against the reference plugin |

### Anti-Features (Commonly Requested, Often Problematic)

These sound valuable but create complexity, risk, or scope creep. Documented here to keep them out of v1.

| Feature | Why Requested | Why Problematic | Alternative |
|---------|---------------|-----------------|-------------|
| AI / semantic object recognition (skin, clothing, trees) | "Automatically know what material to apply" | High cost, latency, nondeterminism, model dependency; not needed for hue/palette stylization | Ordinary image processing (palette extraction, edge-preserving smoothing) |
| Diffusion-model / cloud processing | "Best-looking results" | Cloud dependency, cost, latency, nondeterminism, asset-leak concerns | Local GPU compute; revisit only if image processing proves insufficient |
| Automatic processing of every model on import (v1) | "Hands-free pipeline" | Slow/surprising imports, silent asset churn, breaks iteration | Explicit `Process with NAMER` first; optional AssetPostprocessor + label/directory opt-in later |
| Hard-coded art styles (NeoSpace, SpyWorld, etc.) | "Ship preset styles out of the box" | Ties NAMER to one art direction; limits audience | Profile-driven NAMERStyleProfile; ship example profiles as *assets*, not code |
| Runtime texture conversion | "Adapt textures at runtime" | Per-frame GPU cost, no bake-time quality guarantees | Editor-time bake only |
| C++ native plugins | "Faster pixel loops" | Portability, build complexity across 5 platforms | C# + Unity compute shaders (HLSL) |
| Destructive editing of imported source assets | "Just fix the source directly" | Irreversible, breaks version control/reimport, violates trust | Never modify sources; all output under generated-assets dir |
| Full material taxonomy / semantic classes (matte, glossy, leather, stone) | "Categorize materials automatically" | Hard to infer reliably; adds a fragile classification layer | Infer only broad properties (metallic, roughness, alpha, emissive) that the source already provides |
| HDRP-first support | "Cover the high-end" | Doubles shader surface area; risks the first milestone | URP first, Standard where available; HDRP deferred |
| Live per-pixel C# image processing | "Simpler to write in C#" | Repeated per-pixel C# on 2K/4K textures is unusably slow | GPU compute for all high-res image ops |

## Feature Dependencies

```
Source material inspection
    └──requires──> Runtime NAMER shader (decode spec must match encode spec)
    └──requires──> Normalization + missing-map defaults

Texture channel packing (surface texture)
    └──requires──> Octahedral normal encoding
    └──requires──> Normalization + missing-map defaults

Vertex-color decomposition
    └──requires──> Base color (cleaned) texture
    └──requires──> Mesh inspection (UVs, triangles, seams)
    └──produces──> Residual texture
    └──produces──> Reconstruction-error metric

Adaptive residual resolution reduction
    └──requires──> Reconstruction-error metric (from decomposition)

Stylization (palette/smoothing/AO/normal/roughness)
    └──requires──> NAMERStyleProfile + palette extraction
    └──enhances──> Vertex-color decomposition (stylize before/after decomposition — order is a design choice)
    └──conflicts──> Vertex-color decomposition IF stylization runs after residual is frozen (would invalidate error metrics)

Interactive preview (mesh + debug channels)
    └──requires──> Encoding + packing + shader (to show meaningful output)
    └──requires──> Stylization (to show styled result)

Batch processing
    └──requires──> Stable single-asset pipeline (all of the above)

AssetPostprocessor automation (post-v1)
    └──requires──> Batch processing + deterministic, re-runnable pipeline
```

### Dependency Notes

- **Texture channel packing requires octahedral encoding and normalization:** the packed surface texture's R/G are octahedral normals; you cannot pack before the source normal map is normalized and encoded.
- **Adaptive residual resolution requires the error metric:** it is meaningless to auto-shrink the residual without measured reconstruction error — the "adaptive" part is the whole point.
- **Stylization conflicts with decomposition if sequenced late:** stylization changes base color, which invalidates any already-computed residual/error. Decide the canonical order (recommended: normalize → stylize → decompose → residual) so stylization results flow into the decomposition rather than fighting it.
- **Preview requires everything upstream:** a preview window with no working encode/pack/shader pipeline shows nothing; it cannot be built first in isolation.
- **Batch requires a stable single-asset path:** batch is a loop over the single-asset processor; hardening the single path first avoids multiplying bugs across N assets.
- **AssetPostprocessor requires batch + determinism:** auto-import automation is only safe when the pipeline is re-runnable and its output stable; this is why it is explicitly deferred.

## MVP Definition

### Launch With (v1)

Minimum to validate the Core Value: "select a textured FBX, run `Process with NAMER`, get a correct NAMER material without leaving Unity or touching sources."

- [ ] Source material inspection + missing-map defaults — nothing works without reading the input
- [ ] Normalization of PBR maps — clean, consistent inputs for everything downstream
- [ ] Octahedral normal encoding — the packed format's core
- [ ] Surface texture packing (AO / metallic / emissive / 6-bit roughness) — the NAMER format
- [ ] URP NAMER runtime shader (decode) — makes the output renderable (table stakes)
- [ ] Cleaned base color texture — texture 1 of the 2-texture format
- [ ] Non-destructive asset generation to a dedicated directory — trust prerequisite
- [ ] Before/after preview on a representative mesh — users won't adopt an invisible tool
- [ ] Explicit `Process with NAMER` command + editor window — the entry point

### Add After Validation (v1.x)

Once the encode/decode/render loop is proven against real assets.

- [ ] Vertex-color decomposition + residual texture — trigger: core pipeline stable and correct on 2K/4K sets
- [ ] Reconstruction-error metric + adaptive residual resolution — trigger: decomposition producing residuals
- [ ] Vertex splitting at seams — trigger: decomposition shows smearing on real meshes
- [ ] Debug channel views — trigger: artists need to verify/tune decomposition
- [ ] Batch processing over folders — trigger: single-asset path proven

### Future Consideration (v2+)

After product-market fit is established.

- [ ] Stylization (NAMERStyleProfile + palette + hue-aware mapping + smoothing) — defer if conversion value alone lands users; it is the biggest complexity chunk
- [ ] Dual AO model (runtime vs. painted/cavity) — builds on stylization
- [ ] Normal detail reduction + roughness simplification — builds on stylization
- [ ] AssetPostprocessor automation (opt-in via labels/directories) — needs hardened batch + determinism
- [ ] HDRP Lit support — after URP is solid
- [ ] Blender NAMER round-trip compatibility validation — after format is frozen

## Feature Prioritization Matrix

| Feature | User Value | Implementation Cost | Priority |
|---------|------------|---------------------|----------|
| Non-destructive output / source immutability | HIGH | LOW | P1 |
| Source material inspection + defaults | HIGH | MEDIUM | P1 |
| Octahedral normal encoding | HIGH | MEDIUM | P1 |
| Surface texture packing | HIGH | MEDIUM | P1 |
| URP runtime NAMER shader | HIGH | MEDIUM | P1 |
| Cleaned base color texture | HIGH | MEDIUM | P1 |
| Before/after preview | HIGH | MEDIUM | P1 |
| Editor window + command | HIGH | LOW | P1 |
| Vertex-color decomposition + residual | HIGH | HIGH | P2 |
| Reconstruction error + adaptive resolution | HIGH | HIGH | P2 |
| Vertex splitting at seams | HIGH | MEDIUM | P2 |
| Debug channel views | MEDIUM | MEDIUM | P2 |
| Batch processing | MEDIUM | MEDIUM | P2 |
| Stylization (profile + palette + smoothing) | HIGH | HIGH | P3 |
| Dual AO (runtime/painted/cavity) | MEDIUM | MEDIUM | P3 |
| Normal detail reduction + roughness simplification | MEDIUM | MEDIUM | P3 |
| AssetPostprocessor automation | MEDIUM | MEDIUM | P3 |
| HDRP support | MEDIUM | HIGH | P3 |
| Blender round-trip compatibility | MEDIUM | MEDIUM | P3 |

**Priority key:**
- P1: Must have for launch
- P2: Should have, add when possible
- P3: Nice to have, future consideration

## Competitor Feature Analysis

| Feature | Substance 3D (Painter/Sampler) | Polybrush (Unity) | Unity channel packers (e.g. unity-texture-packer) | NAMER approach |
|---------|-------------------------------|-------------------|---------------------------------------------------|----------------|
| Channel packing | Export-preset templates pack channels (ORM etc.) | n/a | Manual merge R/G/B/A from source textures | Fixed compact pack (octahedral normal + AO + metallic/emissive/roughness bits) |
| Vertex color | n/a (painting is texture-based) | Paint/smooth/sculpt/scatter on vertices; no error-driven baking | n/a | Barycentric least-squares fit + residual + error metric |
| Stylization | Procedural materials, smart masks, AI material-from-photo (Sampler) | n/a | n/a | Reference-image palette + hue-aware mapping, baked into textures |
| Baking | Full bake (normal/AO/curvature/ID) from high-poly | n/a | n/a | Editor-time bake of packed format from existing textures |
| Preview | 2D/3D viewport with layers | Live mesh viewport | Texture preview | Interactive before/after on actual mesh + debug channels |
| Batch | Batch bake/export via command line | n/a | n/a | Explicit batch over selections/folders; AssetPostprocessor later |
| Non-destructive | Yes (procedural stack) | n/a | Yes (new output) | Yes (generated-assets dir, never touch sources) |
| Runtime cost | Authored textures, standard PBR | n/a | n/a | 2 textures + vertex colors, no runtime decompression stage |

## Sources

- Unity `AssetPostprocessor` documentation — confirms `OnPreprocessTexture`, `OnPostprocessTexture`, `OnPostprocessSprites`, `OnPreprocessMaterialDescription`, `OnPostprocessMaterial`, `OnAssignMaterialModel` callbacks and automated-import use cases. (HIGH confidence — official docs, fetched 2026-08-25)
  - https://docs.unity3d.com/ScriptReference/AssetPostprocessor.html
- Unity Polybrush documentation — confirms five brush modes: Sculpt, Smooth, Color, Texture, Scatter (vertex color painting, texture blending, mesh editing). (HIGH confidence — official docs, fetched 2026-08-25)
  - https://docs.unity3d.com/Packages/com.unity.polybrush@1.0/manual/modes.html
- `andydbc/unity-texture-packer` README — confirms channel-packer tools are "utility to merge different texture channels into a final texture output." (MEDIUM confidence — GitHub README fetched, limited detail)
  - https://github.com/andydbc/unity-texture-packer
- Adobe Substance 3D feature specifics (procedural authoring, smart masks, export templates, Sampler AI material-from-photo, batch baking) — derived from training data; the Adobe help pages timed out on fetch (2026-08-25). (LOW/MEDIUM confidence — flag for validation before citing as authoritative)
  - https://helpx.adobe.com/substance-3d-painter/using/export-textures.html
- NAMER Unity Plugin PRD (`NAMER_UNITY_PLUGIN_PRD.md`) and `.planning/PROJECT.md` — authoritative source for NAMER's intended feature set, non-goals, and pipeline. (HIGH confidence — project's own spec)

**Note on search tooling:** WebSearch returned empty results in this environment; findings above rely on direct documentation fetches (WebFetch) plus the project PRD. Adobe Substance details are the least-verified and should be re-validated during requirements definition if precise feature parity claims matter.

---
*Feature research for: NAMER Unity editor material-processing / stylization plugin*
*Researched: 2026-08-25*

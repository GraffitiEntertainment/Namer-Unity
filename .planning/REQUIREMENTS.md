# Requirements: NAMER Unity Plugin

**Defined:** 2026-08-25
**Core Value:** A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Material Inspection

- [x] **INSP-01**: User can select a GameObject, prefab, FBX/model, material, or asset folder as the NAMER processing source
- [x] **INSP-02**: Processor inspects URP Lit source materials and locates Base Color, Normal, AO, Metallic, Roughness/Smoothness, Emission, and Alpha maps when present
- [x] **INSP-03**: Processor reads scalar material properties as fallbacks when separate maps are absent (Unity Standard supported where available)
- [x] **INSP-04**: Missing maps use sensible defaults without failing the pipeline

### NAMER Encoding

- [x] **ENCD-01**: Source normals are converted to octahedral RG representation and round-trip within tolerance
- [x] **ENCD-02**: Processor packs the NAMER surface texture: R/G = octahedral normal, B = AO, A bit 7 = metallic, A bit 6 = emissive, A bits 0–5 = 6-bit roughness
- [x] **ENCD-03**: AO channel is preserved through the packing path
- [x] **ENCD-04**: Packed surface texture is written linear and uncompressed (no sRGB, no BC/ETC/ASTC corruption of packed bits)
- [x] **ENCD-05**: NAMER packed textures decode equivalently to the Blender NAMER reference implementation
- [x] **ENCD-06**: Emissive color is stored as material metadata rather than a texture

### Runtime Shader

- [x] **SHDR-01**: URP NAMER runtime shader decodes octahedral normals, AO, metallic, emissive flag, and 6-bit roughness from the packed texture
- [x] **SHDR-02**: Shader supports base/residual texture with vertex-color reconstruction (`BaseColor ≈ VertexColorInterpolation × ResidualColor`)
- [x] **SHDR-03**: Shader renders comparably to URP Lit on the same source material (development comparison)
- [x] **SHDR-04**: Shader supports emissive color and transparency where supported by the source

### Base Texture Normalization

- [x] **NORM-01**: Processor produces a cleaned base color texture with unwanted baked lighting removed where feasible
- [x] **NORM-02**: Source maps are normalized (color space, orientation, scalar-vs-map unification) in a GPU compute pipeline
- [x] **NORM-03**: Per-pixel processing of 2K/4K textures runs via compute shaders, not per-pixel C# loops

### Vertex Color Decomposition

- [x] **VCOL-01**: Processor fits low-frequency base color into mesh vertex colors via per-triangle multi-sample barycentric least-squares (not simple averaging)
- [x] **VCOL-02**: Generated mesh splits vertices where UV seams, hard color boundaries, or discontinuities require different colors, preserving mesh attributes
- [x] **VCOL-03**: Processor computes a residual texture from the difference between fitted interpolation and source texture
- [x] **VCOL-04**: Processor reports reconstruction-error statistics (coverage, average/max error, estimated residual requirement) with optional debug visualization
- [x] **VCOL-05**: Residual texture resolution is reduced adaptively based on measured reconstruction error, with manual override

### Stylization

- [x] **STYL-01**: User can create a reusable `NAMERStyleProfile` ScriptableObject holding 3–10 reference images and stylization parameters
- [x] **STYL-02**: Processor extracts palette/style statistics (dominant colors, hue families, saturation, luminance distribution, shadow/midtone/highlight colors, warm/cool bias, contrast, palette density) from reference images
- [x] **STYL-03**: Palette mapping preserves source hue identity (dark brown → styled dark brown, green stays green) unless stronger replacement is chosen
- [x] **STYL-04**: GPU edge-preserving smoothing (bilateral/Kuwahara/guided class) reduces photographic noise while preserving UV island boundaries, seams, decals, and large transitions
- [x] **STYL-05**: Runtime AO and Painted AO are exposed as separate controls; cavity strength and "avoid crushing cavities to black" behavior provided
- [x] **STYL-06**: Normal detail reduction and roughness simplification controls are available before quantization
- [ ] **STYL-07**: Processor fits the reference look's global grade (white balance, exposure, contrast, saturation, color curves) from the style references onto a generated URP Volume profile, applied after the baked palette mapping *(Phase 5.1)*
- [ ] **STYL-08**: Tonemapping defaults to ACES with the fit shaping parameters around it; bloom threshold auto-fitted from reference highlights, intensity exposed for artist control *(Phase 5.1)*
- [ ] **STYL-09**: Processor preview can render the stylized NAMER with the fitted post-processing look applied in-window *(Phase 5.1)*
- [x] **STYL-10**: The fitted post look lives on a standalone `NamerPostLook` ScriptableObject — Fit Look, fitted parameters, and its own reference-image list are created, fitted, and hand-tweaked in the asset's own inspector, not the NAMER Processor window *(Phase 5.2)*
- [x] **STYL-11**: Palette transfer never modifies luma — ToneMatch is retired end-to-end (no UI knob, no serialized profile field, no kernel luma-transfer path); tonality is owned exclusively by the post look *(Phase 5.2)*
- [x] **STYL-12**: Each `NamerPostLook` asset generates its own URP Volume profile under the generated-asset conventions; duplicating and tweaking an asset yields independent per-scene looks, and re-fit regenerates its Volume *(Phase 5.2)*
- [x] **STYL-13**: Reference scene decomposition: from the shared reference list, estimate the reference grade (exposure, white balance, tone curve, contrast, saturation) and classify pixels (albedo / shading / specular / emissive / bloom) with a confidence mask, recovering de-graded base colors — estimation is model-free and reuses/inverts the `NamerStylePostFit` estimator family (no second estimator family) *(Phase 5.3)*
- [x] **STYL-14**: Palette extraction runs on the recovered base colors by default with the raw-pixel mode behind a switch; de-grade and the fitted post look are inverse-consistent (the grade is applied once, never doubled); contaminated pixels (bloom, specular, emissive, deep shadow, fog, transparency) carry zero/low palette weight *(Phase 5.3)*
- [x] **STYL-15**: Merged style asset: ONE ScriptableObject owns ONE weighted reference list. The decomposition derives both blocks from that same list — the palette block (an HSL color list from the de-graded base colors) and the fitted post block (from the grade estimate) — with a single editor. Creation is **menu-only** (`Assets/Create/NAMER/…`): the NAMER Processor panel never creates the asset — it only loads/uses it via an object field. Migration merges existing `NamerStyleProfile` + `NamerPostLook` assets losing neither extracted palettes nor fitted looks; per-asset Volume generation carries over (supersedes STYL-10's two-asset split and the 05.2 two-list law) *(Phase 5.3)*
- [ ] **STYL-16**: Palette application: closest-match remap of the albedo texture against the HSL palette BEFORE the vertex-color build (upholds 05-CONTEXT D-01 "stylize the source, then fit"); hue identity (STYL-03) and no-luma-transfer (STYL-11) remain pinned; the ingest doc's post-fit OKLab VC remap is recorded as a rejected alternative *(Phase 5.3)*
- [x] **STYL-17**: Forward validation: render NAMER VCs + surface + recovered Volume and compare against the reference style; visible residual mismatch is the unlock condition for the 3D-LUT fallback (locked 5.1 decision) *(Phase 5.3)*

### Asset Generation & Safety

- [x] **GEN-01**: Processor generates NAMER material, textures, and (when decomposed) mesh under a dedicated generated-assets directory (e.g. `NAMERGenerated/`)
- [x] **GEN-02**: Source assets are never modified — enforced by test
- [x] **GEN-03**: User can configure destination folder and generated-asset prefix/suffix, with overwrite-generated option limited to generated assets
- [x] **GEN-04**: Generated packed textures are saved with correct linear/uncompressed import settings

### Editor Workflow & Preview

- [x] **UI-01**: NAMER editor window is available at `Tools > NAMER > Processor`
- [x] **UI-02**: User can trigger processing via an explicit `Process with NAMER` command (no automatic import processing in v1)
- [x] **UI-03**: Editor window exposes processing controls (style strength, smoothing, palette, AO controls, normal detail, roughness simplification, vertex color decomposition, residual resolution) — *Phase 3 delivered the AO un-multiply control; the remaining controls are Phase 4/5 scope (D-11/D-14)*
- [x] **UI-04**: User can preview the actual selected mesh before/after (original, NAMER, stylized NAMER) — *Phase 3 delivered original + NAMER preview; stylized NAMER is Phase 5 scope*
- [x] **UI-05**: Debug channel views are available (vertex color only, residual, reconstructed base, AO, normal, roughness, metallic, emissive, reconstruction error) — *Phase 3 delivered base/AO/normal/roughness/metallic/emissive; vertex-color/residual/reconstruction-error views are Phase 4 scope (D-11)*
- [x] **UI-06**: Preview updates interactively when processing controls change (GPU-favored)

### Testing

- [x] **TEST-01**: Automated tests cover octahedral encode/decode, metallic/emissive/roughness bit packing, and AO preservation
- [x] **TEST-02**: Automated tests cover vertex color fitting, residual reconstruction, and UV seam behavior
- [x] **TEST-03**: Automated tests cover generated asset paths and source-asset immutability
- [x] **TEST-04**: GPU kernels are verified against the CPU reference implementation (Core assembly) via round-trip tests

### Packaging

- [x] **PKG-01**: Plugin ships as a reusable UPM package (`com.graffitientertainment.namer`) with Runtime/, Editor/, Shaders/, Compute/, Tests/ layout and assembly definitions

## v2 Requirements

### Automation & Pipelines

- **AUTO-01**: Optional automatic processing via `AssetPostprocessor` for marked directories/labels
- **BATCH-01**: Batch processing over folder selections

### Pipeline Support

- **PIPE-01**: HDRP Lit support
- **PIPE-02**: Blender round-trip validation tooling
- **PIPE-03**: Optional automatic emissive detection utility

## Out of Scope

| Feature | Reason |
|---------|--------|
| Blender/Maya dependency | Unity becomes the primary processing environment; Blender plugin remains for Blender-centric workflows |
| AI object/semantic recognition | v1 uses ordinary image processing; AI considered later only if insufficient |
| Diffusion models | Out of v1 scope per PRD |
| Cloud processing | All processing local in-editor |
| C++ native plugins | First implementation is C#/HLSL only |
| Runtime texture conversion | NAMER processing is editor-time only |
| Destructive editing of source assets | Hard safety rule |
| Hard-coded art styles | Stylization is profile-driven; NAMER stays style-agnostic |
| Large material taxonomy / semantic classes | Behavior inferred from metallic/roughness/alpha/emission instead |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| INSP-01 | Phase 2 | Complete |
| INSP-02 | Phase 2 | Complete |
| INSP-03 | Phase 2 | Complete |
| INSP-04 | Phase 2 | Complete |
| ENCD-01 | Phase 1 | Complete |
| ENCD-02 | Phase 1 | Complete |
| ENCD-03 | Phase 1 | Complete |
| ENCD-04 | Phase 1 | Complete |
| ENCD-05 | Phase 1 | Complete |
| ENCD-06 | Phase 1 | Complete |
| SHDR-01 | Phase 1 | Complete |
| SHDR-02 | Phase 1 | Complete |
| SHDR-03 | Phase 1 | Complete |
| SHDR-04 | Phase 1 | Complete |
| NORM-01 | Phase 2 | Complete |
| NORM-02 | Phase 2 | Complete |
| NORM-03 | Phase 2 | Complete |
| VCOL-01 | Phase 4 | Complete |
| VCOL-02 | Phase 4 | Complete |
| VCOL-03 | Phase 4 | Complete |
| VCOL-04 | Phase 4 | Complete |
| VCOL-05 | Phase 4 | Complete |
| STYL-01 | Phase 5 | Complete |
| STYL-02 | Phase 5 | Complete |
| STYL-03 | Phase 5 | Complete |
| STYL-04 | Phase 5 | Complete |
| STYL-05 | Phase 5 | Complete |
| STYL-06 | Phase 5 | Complete |
| STYL-07 | Phase 5.1 | Not Started |
| STYL-08 | Phase 5.1 | Not Started |
| STYL-09 | Phase 5.1 | Not Started |
| STYL-10 | Phase 5.2 | Complete |
| STYL-11 | Phase 5.2 | Complete |
| STYL-12 | Phase 5.2 | Complete |
| STYL-13 | Phase 5.3 | Not Started |
| STYL-14 | Phase 5.3 | Not Started |
| STYL-15 | Phase 5.3 | Not Started |
| STYL-16 | Phase 5.3 | Not Started |
| STYL-17 | Phase 5.3 | Not Started |
| GEN-01 | Phase 3 | Complete |
| GEN-02 | Phase 3 | Complete |
| GEN-03 | Phase 3 | Complete |
| GEN-04 | Phase 3 | Complete |
| UI-01 | Phase 3 | Complete |
| UI-02 | Phase 3 | Complete |
| UI-03 | Phase 3 (slice) / 4-5 | In Progress — AO un-multiply control (Phase 3) + roughness-extraction estimator/strength controls (Phase 04.1) delivered; style strength/smoothing/palette/normal detail/roughness simplification remain Phase 5 scope |
| UI-04 | Phase 3 (slice) / 5 | In Progress — original+NAMER preview delivered in Phase 3 |
| UI-05 | Phase 3 (slice) / 4 | In Progress — 6 channels delivered in Phase 3 |
| UI-06 | Phase 3 | Complete |
| TEST-01 | Phase 1 | Complete |
| TEST-02 | Phase 4 | Complete |
| TEST-03 | Phase 3 | Complete |
| TEST-04 | Phase 2 | Complete |
| PKG-01 | Phase 1 | Complete |

**Coverage:**
- v1 requirements: 43 total
- Mapped to phases: 43
- Unmapped: 0 ✓

Note: the previous header stated "40 total" but the v1 sections contain 43 numbered requirements; this traceability maps all 43.

---
*Requirements defined: 2026-08-25*
*Last updated: 2026-08-25 after initial definition*

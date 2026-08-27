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

- [ ] **VCOL-01**: Processor fits low-frequency base color into mesh vertex colors via per-triangle multi-sample barycentric least-squares (not simple averaging)
- [ ] **VCOL-02**: Generated mesh splits vertices where UV seams, hard color boundaries, or discontinuities require different colors, preserving mesh attributes
- [ ] **VCOL-03**: Processor computes a residual texture from the difference between fitted interpolation and source texture
- [ ] **VCOL-04**: Processor reports reconstruction-error statistics (coverage, average/max error, estimated residual requirement) with optional debug visualization
- [ ] **VCOL-05**: Residual texture resolution is reduced adaptively based on measured reconstruction error, with manual override

### Stylization

- [ ] **STYL-01**: User can create a reusable `NAMERStyleProfile` ScriptableObject holding 3–10 reference images and stylization parameters
- [ ] **STYL-02**: Processor extracts palette/style statistics (dominant colors, hue families, saturation, luminance distribution, shadow/midtone/highlight colors, warm/cool bias, contrast, palette density) from reference images
- [ ] **STYL-03**: Palette mapping preserves source hue identity (dark brown → styled dark brown, green stays green) unless stronger replacement is chosen
- [ ] **STYL-04**: GPU edge-preserving smoothing (bilateral/Kuwahara/guided class) reduces photographic noise while preserving UV island boundaries, seams, decals, and large transitions
- [ ] **STYL-05**: Runtime AO and Painted AO are exposed as separate controls; cavity strength and "avoid crushing cavities to black" behavior provided
- [ ] **STYL-06**: Normal detail reduction and roughness simplification controls are available before quantization

### Asset Generation & Safety

- [x] **GEN-01**: Processor generates NAMER material, textures, and (when decomposed) mesh under a dedicated generated-assets directory (e.g. `NAMERGenerated/`)
- [x] **GEN-02**: Source assets are never modified — enforced by test
- [x] **GEN-03**: User can configure destination folder and generated-asset prefix/suffix, with overwrite-generated option limited to generated assets
- [x] **GEN-04**: Generated packed textures are saved with correct linear/uncompressed import settings

### Editor Workflow & Preview

- [x] **UI-01**: NAMER editor window is available at `Tools > NAMER > Processor`
- [x] **UI-02**: User can trigger processing via an explicit `Process with NAMER` command (no automatic import processing in v1)
- [x] **UI-03**: Editor window exposes processing controls (style strength, smoothing, palette, AO controls, normal detail, roughness simplification, vertex color decomposition, residual resolution)
- [x] **UI-04**: User can preview the actual selected mesh before/after (original, NAMER, stylized NAMER)
- [x] **UI-05**: Debug channel views are available (vertex color only, residual, reconstructed base, AO, normal, roughness, metallic, emissive, reconstruction error)
- [x] **UI-06**: Preview updates interactively when processing controls change (GPU-favored)

### Testing

- [x] **TEST-01**: Automated tests cover octahedral encode/decode, metallic/emissive/roughness bit packing, and AO preservation
- [ ] **TEST-02**: Automated tests cover vertex color fitting, residual reconstruction, and UV seam behavior
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
| VCOL-01 | Phase 4 | Pending |
| VCOL-02 | Phase 4 | Pending |
| VCOL-03 | Phase 4 | Pending |
| VCOL-04 | Phase 4 | Pending |
| VCOL-05 | Phase 4 | Pending |
| STYL-01 | Phase 5 | Pending |
| STYL-02 | Phase 5 | Pending |
| STYL-03 | Phase 5 | Pending |
| STYL-04 | Phase 5 | Pending |
| STYL-05 | Phase 5 | Pending |
| STYL-06 | Phase 5 | Pending |
| GEN-01 | Phase 3 | Complete |
| GEN-02 | Phase 3 | Complete |
| GEN-03 | Phase 3 | Complete |
| GEN-04 | Phase 3 | Complete |
| UI-01 | Phase 3 | Complete |
| UI-02 | Phase 3 | Complete |
| UI-03 | Phase 3 | Complete |
| UI-04 | Phase 3 | Complete |
| UI-05 | Phase 3 | Complete |
| UI-06 | Phase 3 | Complete |
| TEST-01 | Phase 1 | Complete |
| TEST-02 | Phase 4 | Pending |
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

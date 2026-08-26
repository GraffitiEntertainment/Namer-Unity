# Requirements: NAMER Unity Plugin

**Defined:** 2026-08-25
**Core Value:** A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Material Inspection

- [ ] **INSP-01**: User can select a GameObject, prefab, FBX/model, material, or asset folder as the NAMER processing source
- [ ] **INSP-02**: Processor inspects URP Lit source materials and locates Base Color, Normal, AO, Metallic, Roughness/Smoothness, Emission, and Alpha maps when present
- [ ] **INSP-03**: Processor reads scalar material properties as fallbacks when separate maps are absent (Unity Standard supported where available)
- [ ] **INSP-04**: Missing maps use sensible defaults without failing the pipeline

### NAMER Encoding

- [ ] **ENCD-01**: Source normals are converted to octahedral RG representation and round-trip within tolerance
- [ ] **ENCD-02**: Processor packs the NAMER surface texture: R/G = octahedral normal, B = AO, A bit 7 = metallic, A bit 6 = emissive, A bits 0–5 = 6-bit roughness
- [ ] **ENCD-03**: AO channel is preserved through the packing path
- [ ] **ENCD-04**: Packed surface texture is written linear and uncompressed (no sRGB, no BC/ETC/ASTC corruption of packed bits)
- [ ] **ENCD-05**: NAMER packed textures decode equivalently to the Blender NAMER reference implementation
- [ ] **ENCD-06**: Emissive color is stored as material metadata rather than a texture

### Runtime Shader

- [ ] **SHDR-01**: URP NAMER runtime shader decodes octahedral normals, AO, metallic, emissive flag, and 6-bit roughness from the packed texture
- [ ] **SHDR-02**: Shader supports base/residual texture with vertex-color reconstruction (`BaseColor ≈ VertexColorInterpolation × ResidualColor`)
- [ ] **SHDR-03**: Shader renders comparably to URP Lit on the same source material (development comparison)
- [ ] **SHDR-04**: Shader supports emissive color and transparency where supported by the source

### Base Texture Normalization

- [ ] **NORM-01**: Processor produces a cleaned base color texture with unwanted baked lighting removed where feasible
- [ ] **NORM-02**: Source maps are normalized (color space, orientation, scalar-vs-map unification) in a GPU compute pipeline
- [ ] **NORM-03**: Per-pixel processing of 2K/4K textures runs via compute shaders, not per-pixel C# loops

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

- [ ] **GEN-01**: Processor generates NAMER material, textures, and (when decomposed) mesh under a dedicated generated-assets directory (e.g. `NAMERGenerated/`)
- [ ] **GEN-02**: Source assets are never modified — enforced by test
- [ ] **GEN-03**: User can configure destination folder and generated-asset prefix/suffix, with overwrite-generated option limited to generated assets
- [ ] **GEN-04**: Generated packed textures are saved with correct linear/uncompressed import settings

### Editor Workflow & Preview

- [ ] **UI-01**: NAMER editor window is available at `Tools > NAMER > Processor`
- [ ] **UI-02**: User can trigger processing via an explicit `Process with NAMER` command (no automatic import processing in v1)
- [ ] **UI-03**: Editor window exposes processing controls (style strength, smoothing, palette, AO controls, normal detail, roughness simplification, vertex color decomposition, residual resolution)
- [ ] **UI-04**: User can preview the actual selected mesh before/after (original, NAMER, stylized NAMER)
- [ ] **UI-05**: Debug channel views are available (vertex color only, residual, reconstructed base, AO, normal, roughness, metallic, emissive, reconstruction error)
- [ ] **UI-06**: Preview updates interactively when processing controls change (GPU-favored)

### Testing

- [ ] **TEST-01**: Automated tests cover octahedral encode/decode, metallic/emissive/roughness bit packing, and AO preservation
- [ ] **TEST-02**: Automated tests cover vertex color fitting, residual reconstruction, and UV seam behavior
- [ ] **TEST-03**: Automated tests cover generated asset paths and source-asset immutability
- [ ] **TEST-04**: GPU kernels are verified against the CPU reference implementation (Core assembly) via round-trip tests

### Packaging

- [ ] **PKG-01**: Plugin ships as a reusable UPM package (`com.graffitientertainment.namer`) with Runtime/, Editor/, Shaders/, Compute/, Tests/ layout and assembly definitions

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
| (to be filled by roadmap) | | |

**Coverage:**
- v1 requirements: 40 total
- Mapped to phases: 0
- Unmapped: 40 ⚠️

---
*Requirements defined: 2026-08-25*
*Last updated: 2026-08-25 after initial definition*

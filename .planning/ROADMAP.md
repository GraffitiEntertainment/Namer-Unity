# Roadmap: NAMER Unity Plugin

## Overview

The journey moves from the format contract outward. First we lock the NAMER packed format once in a pure-C# Core assembly and prove the URP runtime shader decodes it equivalently to Blender. Then we de-risk GPU compute in the editor, producing the normalized base color and packed surface textures. A vertical slice turns that pipeline into a shippable, non-destructive MVP — `Process with NAMER` generates source-compatible assets under a dedicated directory with before/after preview. Finally we invest in the two algorithmic differentiators: vertex-color decomposition (barycentric least-squares fit + residual) and reference-image stylization.

## Phases

**Phase Numbering:**

- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

- [x] **Phase 1: Core Format Contract + Runtime Decode** - Define the packed format in pure-C# Core and decode it in the URP runtime shader (completed 2026-08-26)
- [x] **Phase 2: Source Inspection + GPU Compute Pipeline** - Read source materials and produce normalized + packed textures in GPU compute (completed 2026-08-27)
- [x] **Phase 3: Asset Generation + Editor Workflow + Preview** - Generate NAMER assets non-destructively from an editor window with preview (completed 2026-08-27)
- [x] **Phase 4: Vertex-Color Decomposition + Residual** - Fit low-frequency color into vertex colors with an adaptive residual texture (gap closure in progress; verification found 3 gaps) (completed 2026-09-01)
- [ ] **Phase 5: Stylization** - Apply reference-image-driven, hue-preserving stylization via NAMERStyleProfile

## Phase Details

### Phase 1: Core Format Contract + Runtime Decode

**Goal**: Define the NAMER packed format once in a pure-C# Core assembly and decode it correctly in the URP runtime shader, verified headless against the Blender reference
**Mode**: mvp
**Depends on**: Nothing (first phase)
**Requirements**: ENCD-01, ENCD-02, ENCD-03, ENCD-04, ENCD-05, ENCD-06, SHDR-01, SHDR-02, SHDR-03, SHDR-04, TEST-01, PKG-01
**Success Criteria** (what must be TRUE):

  1. NAMER surface texture round-trips: octahedral normal, AO, metallic, emissive, and 6-bit roughness decode back to their source values within tolerance
  2. NAMER packed textures decode equivalently to the Blender NAMER reference implementation
  3. URP runtime shader renders a NAMER material whose shading matches URP Lit on the same source material
  4. Emissive color and transparency carry through, with emissive stored as material metadata
  5. Package is a valid UPM package (`com.graffitientertainment.namer`) with Core/Runtime/Editor/Shader/Compute/Test assemblies that build and pass headless tests

**Plans**: 3 plans

Plans:
**Wave 1**

- [x] 01-03: UPM package skeleton (Runtime/Editor/Shader/Compute/Test asmdefs) + round-trip test harness

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 01-01: Core assembly — octahedral encode/decode, surface bit-packing, color math with headless unit tests

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 01-02: URP NAMER runtime decode shader with Blender-equivalence verification

### Phase 2: Source Inspection + GPU Compute Pipeline

**Goal**: Read URP Lit/Standard source materials and produce the normalized base color and packed surface textures entirely in GPU compute, verified against the CPU reference
**Mode**: mvp
**Depends on**: Phase 1
**Requirements**: INSP-01, INSP-02, INSP-03, INSP-04, NORM-01, NORM-02, NORM-03, TEST-04
**Success Criteria** (what must be TRUE):

  1. User selects any supported source (GameObject, prefab, FBX/model, material, or folder) and the processor locates all present PBR maps and scalar fallbacks
  2. Missing maps and scalar properties fall back to sensible defaults without failing the pipeline
  3. Cleaned base color texture and packed surface texture are produced via GPU compute (no per-pixel C#), with baked lighting removed where feasible
  4. GPU kernels match the CPU reference across supported compute backends (round-trip verified)

**Plans**: 3 plans

Plans:

**Wave 1**

- [x] 02-01: SourceInspector — material/map/scalar inspection with defaults

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 02-02: Compute dispatch harness + ComputeTexturePool + normalization/octahedral/packing kernels

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 02-03: GPU golden tests vs Core reference + cross-platform compute smoke test

### Phase 3: Asset Generation + Editor Workflow + Preview

**Goal**: Generate a complete, source-compatible NAMER material non-destructively under a dedicated directory, driven from the editor window with before/after preview
**Mode**: mvp
**Depends on**: Phase 2
**Requirements**: GEN-01, GEN-02, GEN-03, GEN-04, UI-01, UI-02, UI-03, UI-04, UI-05, UI-06, TEST-03
**Success Criteria** (what must be TRUE):

  1. User runs `Process with NAMER` and receives a NAMER material + textures (and mesh when decomposed) under `NAMERGenerated/`, with source assets untouched
  2. Generated packed textures carry correct linear/uncompressed import settings
  3. User can configure the destination folder and generated-asset prefix/suffix, with overwrite limited to generated assets
  4. Editor window at `Tools > NAMER > Processor` shows before/after preview with debug channel views and updates interactively when controls change
  5. Source-asset immutability is enforced by automated test

**Plans**: 3 plans

Plans:

**Wave 1**

- [x] 03-01: AssetGenerator — write material/textures/mesh to NAMERGenerated with import stamping + config

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 03-02: NAMEREditorWindow + `Process with NAMER` command + before/after preview + debug views

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 03-03: Source-immutability + asset-path tests; assemble shippable MVP package

**UI hint**: yes

### Phase 03.1: AO extraction: un-multiply baked AO from the base texture, with bake tweaks (cubemap light from high-res model, blur, etc.) (INSERTED) (completed 2026-08-31)

**Goal:** Deliver real AO to the packed surface B channel and un-multiply baked AO out of the base color via two sources — image-space extraction (fixes the always-white-AO live pain) and a high-to-low geometry bake (64-direction visibility, cage offset 0.01, 10%-bounds ray distance, 16 px seam dilation) — with AO blur/strength/contrast tweaks
**Requirements**: NORM-02, ENCD-03, UI-03 (boundary-aware IDs strengthened by this phase; no dedicated IDs — success criteria derived from CONTEXT.md D-01..D-13)
**Depends on:** Phase 3
**Plans:** 3/3 plans complete

Plans:

**Wave 1**

- [x] 03.1-01: Image-space AO extraction + un-multiply floor (0.1) + source gate (D-07/D-09) + round-trip tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 03.1-02: Geometry bake subsystem (BVH + Burst baker + JFA dilation + fallback D-06 + three-way Process gate + tests)

**Wave 3** *(blocked on Waves 1-2 completion)*

- [x] 03.1-03: AO tweak controls (blur/strength/contrast) + high-res occluder field + settings + automatic bake trigger + UI tests

### Phase 4: Vertex-Color Decomposition + Residual

**Goal**: Fit low-frequency base color into vertex colors via per-triangle barycentric least-squares with seam-safe splitting, and generate an adaptive residual texture with error reporting
**Mode**: mvp
**Depends on**: Phase 3
**Requirements**: VCOL-01, VCOL-02, VCOL-03, VCOL-04, VCOL-05, TEST-02
**Success Criteria** (what must be TRUE):

  1. User enables vertex-color decomposition and the output mesh's vertex colors reconstruct the low-frequency base color within reported error
  2. Output mesh splits vertices at UV seams and discontinuities while preserving mesh attributes
  3. Residual texture captures the difference between fitted vertex-color interpolation and the source texture
  4. Processor reports reconstruction-error statistics (coverage, average/max error, residual requirement) and adapts residual resolution with manual override

**Plans**: 5 plans (4 executed + 1 gap closure)

Plans:

**Wave 1**

- [x] 04-01: MeshVertexSplitter + VertexColorFitter (Burst per-triangle LSQ + Color32 quantize) — the CPU algorithmic core

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 04-02: Residual quotient + error stats + adaptive resolution (NAMERDecomp.compute + NamerDecompPipeline)

**Wave 3** *(blocked on Waves 1-2 completion)*

- [x] 04-03: Asset generation (EXR + mesh) + Process wiring + editor window UI + automated tests

**Wave 4** *(blocked on Waves 1-3 completion)*

- [x] 04-04: Gap closure — multi-material / shared-material multi-mesh decomposition guard + warning fallback (CR-01) + residual EXR FilterMode.Bilinear import (CR-02)

**Wave 5** *(blocked on Wave 4 completion)*

- [x] 04-05: Gap closure — tiling-UV repeat-wrap CPU sampling + coverage < 0.5 guard with CannotDecompose fallback (CR-03)

### Phase 04.1: Baked-response roughness extraction + zero-residual one-texture mode (extract gloss/shading from base into 6-bit roughness, refit vertex colors, D-13 primary) (INSERTED)

**Goal:** [Urgent work - to be planned]
**Requirements**: TBD
**Depends on:** Phase 4
**Plans:** 0 plans

Plans:
- [ ] TBD (run /gsd-plan-phase 04.1 to break down)

### Phase 5: Stylization

**Goal**: Let users apply reference-image-driven, hue-preserving stylization via a reusable NAMERStyleProfile with edge-preserving smoothing and AO/normal/roughness controls
**Mode**: mvp
**Depends on**: Phase 3
**Requirements**: STYL-01, STYL-02, STYL-03, STYL-04, STYL-05, STYL-06
**Success Criteria** (what must be TRUE):

  1. User creates a reusable `NAMERStyleProfile` ScriptableObject holding 3-10 reference images
  2. Processor extracts palette/style statistics (dominant colors, hue families, saturation, luminance, shadow/midtone/highlight, warm/cool bias, contrast, palette density) from reference images
  3. Stylized output preserves source hue identity (dark brown stays brown, green stays green) unless stronger replacement is chosen
  4. Edge-preserving smoothing reduces photographic noise without bleeding across UV island boundaries, seams, or decals
  5. User controls runtime AO vs painted AO, cavity strength, normal detail reduction, and roughness simplification

**Plans**: 3 plans

Plans:

- [ ] 05-01: NAMERStyleProfile + palette/style-stat extraction
- [ ] 05-02: Hue-aware palette mapping + edge-preserving smoothing compute kernels
- [ ] 05-03: AO/normal/roughness controls + stylization pipeline integration

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Core Format Contract + Runtime Decode | 3/3 | Complete   | 2026-08-26 |
| 2. Source Inspection + GPU Compute Pipeline | 3/3 | Complete   | 2026-08-27 |
| 3. Asset Generation + Editor Workflow + Preview | 3/3 | Complete    | 2026-08-28 |
| 03.1. AO Extraction (INSERTED) | 3/3 | Complete | 2026-08-31 |
| 4. Vertex-Color Decomposition + Residual | 5/5 | Complete   | 2026-09-01 |
| 5. Stylization | 0/3 | Not started | - |

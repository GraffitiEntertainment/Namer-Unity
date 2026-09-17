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

**Goal:** Extract material response (gloss/shading/cavity) baked into the base texture as 6-bit roughness (surface alpha bits 0-5) when no authored roughness map exists — via a Sobel Blender-parity estimator and a fit-driven strength search — then refit the sharp-removal-cleaned base into vertex colors so the Phase-4 D-13 residual auto-drop becomes the primary one-texture path (one RGBA8 surface PNG + vertex-colored mesh, no residual), with a D-06 optional roughness-offset escape hatch.
**Requirements**: ENCD-02, NORM-01, NORM-03, VCOL-03, VCOL-04, VCOL-05, UI-03, SHDR-02 (boundary-aware IDs strengthened by this phase; no dedicated IDs — success criteria derived from CONTEXT.md D-01..D-06)
**Depends on:** Phase 4
**Plans:** 7 plans (7 complete)

Plans:

- [x] 04.1-01-PLAN.md
- [x] 04.1-02-PLAN.md
- [x] 04.1-03-PLAN.md
- [x] 04.1-04-PLAN.md
- [x] 04.1-05-PLAN.md
- [x] 04.1-06-PLAN.md
- [x] 04.1-07-PLAN.md

**Wave 1**

- [x] 04.1-01: Sobel roughness extraction — NAMERRoughness.compute/hlsl + NamerRoughnessPipeline + CSNormalize/CSSurfacePack override + NamerComputePipeline D-01 gate + round-trip tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 04.1-02: Fit-driven strength search + sharp-removal cleaned base + D-05 refit wiring + estimator/strength UI + settings

**Wave 3** *(blocked on Waves 1-2 completion)*

- [x] 04.1-03: One-texture material binding + D-06 roughness-offset input + honest-gate/switch-back acceptance tests

**Wave 4** *(gap closure)*

- [x] 04.1-04: Production gap closure — scene-asset selection guard + fit-driven preview wiring + safe scene-instance bind (bindposes order + degenerate-bounds guard)

**Wave 5** *(gap closure, blocked on Wave 4)*

- [x] 04.1-05: Test-fixture gap closure — minimizer rescale + collapse-fixture threshold/gloss + persisted offset texture (13/13 suites)

**Wave 6** *(gap closure — UAT round 3, blocked on Wave 5)*

- [x] 04.1-06: Sobel + fit-search roughness redesign — CSRoughnessRemap = lerp(scalar, Sobel-normalized, fit.Strength) (high-pass detail remap retired), full-adoption pack for fit-driven, slider-truth UI + searched-strength readout (122/122 EditMode)

**Wave 7** *(gap closure — UAT round 5 revision, blocked on Wave 6)*

- [x] 04.1-07: Anchored-inverted roughness mapping — CSRoughnessRemap + Sobel-pack blend become dip-depth (saturate(scalar − strength · mag/p90)); CSRoughnessNormalize stays direct; three Sobel premises flip; D-09 tooltip drops Blender-parity; D-10 full adoption stays pinned

### Phase 4.2: Gouraud-projection one-texture with roughness transfer (residual white by construction) (INSERTED)

**Goal:** Replace the fit-driven sharp-removal search with a by-construction residual collapse — the cleaned base becomes the per-triangle Gouraud-representable projection of the albedo (residual quotient ≡ white up to quantization, no ladder search or threshold gamble), while the removed detail's luminance transfers into the 6-bit roughness map via the surviving D-08 dip consume site with a pure-taste dip-depth slider, a residual on/off override checkbox, and a consolidated estimator path; UV-overlap texels handled honestly (research-gated).
**Depends on**: Phase 4.1
**Requirements**: strengthens VCOL-03, VCOL-04, VCOL-05, UI-03, SHDR-02 (design-change follow-up from 04.1 UAT round 5; source design note `.planning/notes/sobel-led-residual-guided-roughness.md`)
**Success Criteria** (what must be TRUE):

  1. A processed asset's residual measures white everywhere except UV-overlap texels and ≤8-bit quantization dust — no threshold tuning required (D-13 auto-drop becomes near-always-passing)
  2. The removed detail (albedo − projected) is visibly re-expressed as gloss variation via the dip-depth taste slider — not lost; chroma grain loss bounded and documented
  3. One-texture outcome by default (no residual EXR) with an explicit residual on/off override checkbox
  4. Estimator dropdown consolidates to one extraction path + decomposition on/off (Sobel-of-base's survival as alternate dip source is a planning call)
  5. Existing 04.1-07 dip-coupling tests flip again, intentionally, and the full EditMode suite is green

**Plans**: 8 plans (5 + 3 gap closure from UAT round 1)

Plans:

**Wave 1**

- [x] 04.2-01: Decomposition engine — CSProjectBase projection write-back + symmetric VcFloor + NamerResidualMode (AlwaysKeep/NeverKeep) + projection identity/mode tests

- [x] 04.2-02: Removed-luminance transfer — CSRemovedLuma + CSRoughnessTransferRemap (signed Rec.601, p90-normalized dip) + ExtractTransferRoughness + polarity/headroom tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 04.2-03: The rewire + retirement — projection hook through NamerComputePipeline.Process, NamerDipSource/Write Residual/dip-depth UI, fitter/ladder/cache/frequency-separation deletion, intentional test flips

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 04.2-04: UI honesty + acceptance — end-to-end criteria tests (one-texture default, source-dividend EXR, whiteness identity, taste slider), stats relabel + chroma/overlap honesty copy, full regression + flip audit

**Wave 4** *(blocked on Wave 3 completion)*

- [x] 04.2-05: Orthographic synced side-by-side preview — ortho PreviewRenderUtility camera, synced yaw+pitch rotation (one shared quaternion, ±89° pitch clamp), orthographic-size zoom, both-objects-wide-plus-gap initial framing (UI-03/UI-04; the acceptance instrument for the transferred-roughness LOOK)

**Gap Closure** *(UAT round 1: all three parallel, independent files)*

- [x] 04.2-06: GAP-1 — soft-clip the Removed Detail transfer magnitude in CSRoughnessTransferRemap (clamp(mag, -1, 1)) so projection-error texels dip to saturate(scalar − strength) instead of gloss 0; dip-depth slider becomes monotonically live; fix the dividend/minuend comment (SHDR-02/UI-03)

- [ ] 04.2-07: GAP-2 — per-pane screen-space framing: each pane fits its own object's bounding sphere with MarginPx = 15 at neutral rotation, pane centers track zoom; supersedes the 04.2-05 pair-plus-gap framing by UAT verdict (UI-03/UI-04)

- [ ] 04.2-08: GAP-3 — close warnings: WR-01 aspect-correct residual resample (ChosenResolution = long edge), WR-02 CannotDecompose preview guard mirror, WR-03 Decomp dispose before nulling, NAMER_DECOMP_HEATMAP single-exit-point restructure for the Metal translator false positive (VCOL-04/VCOL-05/UI-03)

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
| 04.1. Roughness Extraction + One-Texture (INSERTED) | 7/7 | Complete   | 2026-09-16 |
| 5. Stylization | 0/3 | Not started | - |

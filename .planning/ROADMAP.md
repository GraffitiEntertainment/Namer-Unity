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
- [x] **Phase 5: Stylization** - Apply reference-image-driven, hue-preserving stylization via NAMERStyleProfile (completed 2026-09-24)
- [x] **Phase 5.1: Reference-fitted URP post-processing look** - Fit the reference look's global grade into a generated URP Volume profile after the baked palette mapping (INSERTED) (completed 2026-09-25)
- [x] **Phase 5.2: Standalone post-look asset** - Reference-fitted URP post look as its own ScriptableObject: Fit Look in the asset's main inspector, ToneMatch retired, own reference list, per-scene Volume profiles (INSERTED) (completed 2026-09-28)

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

### Phase 04.3: Adaptive residual resolution via percentile coverage (INSERTED)

**Goal:** Replace the max-error gate in the residual-resolution search with a percentile coverage gate — pick the lowest rung of the existing {2048..128} down/up round-trip ladder where ~99% of texels stay within the color-recreation tolerance (often 512x512). DEFERRED (not this phase): LOD-based texture tiers (residual off at distance, no-texture smallest LOD) — shelved pending the Ultimate LOD System decision; NAMER will not depend on a third-party LOD generator for now.
**Requirements**: strengthens VCOL-04, VCOL-05, UI-03 (percentile-gate follow-up from Phase 4/04.2; no dedicated IDs — success criteria derived from CONTEXT.md D-01..D-08)
**Depends on:** Phase 4
**Plans:** 3/3 plans complete

Plans:

**Wave 1**

- [x] 04.3-01-PLAN.md — Percentile coverage gate in NamerDecompPipeline (D-01/D-02/D-03/D-08, compute zero-change) + AchievedCoverage stat + DefaultCoverageTarget constant + gate tests

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 04.3-02-PLAN.md — CoverageTarget persistence (NamerProcessorSettings, D-05) + plumbing through NamerProjectionContext/CreateProjectionContext to GenerateResidual + reflection/prefs-sandbox test couplings

**Wave 3** *(blocked on Waves 1-2 completion)*

- [x] 04.3-03-PLAN.md — Coverage Target slider (0.90-1.00, default 0.99, D-05) + Error Threshold max 0.10->0.25 (D-06) + achieved-coverage readout on the Residual stats row (D-07) + persistence tests

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

**Plans**: 10 plans (5 + 5 gap closure from UAT rounds 1-3)

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

**Gap Closure** *(UAT round 1: 06/07/08 parallel, independent files; UAT round 2: 09; UAT round 3: 10)*

- [x] 04.2-06: GAP-1 — soft-clip the Removed Detail transfer magnitude in CSRoughnessTransferRemap (clamp(mag, -1, 1)) so projection-error texels dip to saturate(scalar − strength) instead of gloss 0; dip-depth slider becomes monotonically live; fix the dividend/minuend comment (SHDR-02/UI-03)

- [x] 04.2-07: GAP-2 — per-pane screen-space framing: each pane fits its own object's bounding sphere with MarginPx = 15 at neutral rotation, pane centers track zoom; supersedes the 04.2-05 pair-plus-gap framing by UAT verdict (UI-03/UI-04)

- [x] 04.2-08: GAP-3 — close warnings: WR-01 aspect-correct residual resample (ChosenResolution = long edge), WR-02 CannotDecompose preview guard mirror, WR-03 Decomp dispose before nulling, NAMER_DECOMP_HEATMAP single-exit-point restructure for the Metal translator false positive (VCOL-04/VCOL-05/UI-03)

- [x] 04.2-09: GAP-4 — divider-anchored tight-AABB framing (10 px inner margins; zoom grows each model outward from the divider, never across it) + 1 px pane outline overlay (UI-03/UI-04)

- [x] 04.2-10: GAP-5 — rotation-stable divider anchor (fixed first positioning; HorizontalHalfExtentWorld retired) + per-pane clipping (two-cycle per-pane render: each pane renders its own mesh into its own RT; the window draws only that pane's half) (UI-03/UI-04)

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

**Wave 1**

- [x] 05-01: NAMERStyleProfile + palette/style-stat extraction

**Wave 2** *(blocked on Wave 1 completion)*

- [x] 05-02: Hue-aware palette mapping + edge-preserving smoothing compute kernels

**Wave 3** *(blocked on Wave 2 completion)*

- [x] 05-03: AO/normal/roughness controls + stylization pipeline integration

### Phase 5.1: Reference-fitted URP post-processing look (INSERTED)

**Goal**: After the baked per-texel palette mapping (Phase 5 / quick 260924-hk7), fit the reference look's global grade into a generated URP Volume profile — the whole scene shares one coherent runtime look, and nothing tone-mapped is baked into the albedo
**Mode**: mvp
**Depends on**: Phase 5
**Requirements**: STYL-07, STYL-08, STYL-09
**Success Criteria** (what must be TRUE):

  1. From the style profile's references and the palette-mapped output, the editor deterministically fits URP post parameters — white balance (von Kries), post-exposure (mean-luma EV ratio), contrast (percentile spread), saturation (perceptual chroma ratio), master/RGB color curves (histogram specification) — and writes a generated Volume profile asset artists drop into a scene
  2. Tonemapping is ACES by default (URP's curve is parameter-free — the fit shapes exposure/contrast/curves around it); bloom threshold is fitted from the reference highlight percentile, intensity defaulted for artist tweaking
  3. Fitted knobs are named, tweakable Volume parameters (not an opaque LUT) — v1 parametric; the 3D-LUT/ColorLookup route is a plan-time fallback decision only if residual mismatch is visible
  4. The Processor window previews Stylized NAMER + fitted post look using the same math in-shader, so the look is approved without play mode
  5. Fit is CPU editor-side, deterministic, no new package dependencies; tests pin the fit math on synthetic source/reference pairs

**Plans**: 4 plans

Plans:

**Wave 1** *(parallel — disjoint files)*

- [x] 05.1-01-PLAN.md — CPU fit core: NamerStylePostMath (LogC/LMS/ACES port) + NamerStylePostFit estimator suite (von Kries WB solve, EV ratio, LogC contrast, chroma saturation, histogram-spec curves, bloom percentile) + golden/determinism/known-answer pins

- [x] 05.1-02-PLAN.md — NamerStyleProfile fitted-post block (D-02 source of truth + ClampToCaps) + NamerStyleVolumeWriter (generated VolumeProfile with named overrides, sub-asset wiring, GEN-01/GEN-03 gates) + asset tests

**Wave 2** *(blocked on Waves 1)*

- [x] 05.1-03-PLAN.md — Fit Look lifecycle: window exemplar seam (forced-stylized recompute + readback + fit), profile inspector P15–P20, W8 attach-state, Process companion-Volume regeneration, lifecycle pins + USER_GUIDE

**Wave 3** *(blocked on Waves 1-2)*

- [x] 05.1-04-PLAN.md — Stylized + Post preview lens: NamerPostPreview.hlsl LDR-chain twin composed in ForwardLit, W5–W8 lens wiring + post uniform table, pane-level bloom approximation, lens pins + USER_GUIDE

### Phase 05.2: Standalone post-look asset: reference-fitted URP post look as its own ScriptableObject — Fit Look moves out of the NAMER panel into the asset's main inspector (generate + tweak), ToneMatch retired (palette never transfers luma, post owns tone), post-look asset owns its own reference list, per-scene Volume profiles from duplicated/tweaked assets (INSERTED)

**Goal**: Move the reference-fitted post look (Phase 5.1) off the style profile and out of the Processor window into a standalone `NamerPostLook` ScriptableObject: fit-from-live-render and hand-tweaking happen in the asset's own inspector, the asset owns its reference list, each asset generates its own URP Volume profile (per-scene looks via duplicate + tweak), and ToneMatch is retired so palette transfer never touches luma — the post look owns tonality unconditionally

**Depends on**: Phase 5, Phase 5.1
**Requirements**: STYL-10, STYL-11, STYL-12
**Success Criteria** (what must be TRUE):

1. A `NamerPostLook` asset can be created from the asset menu; its main inspector exposes the fitted post parameters for hand-tweaking, a Fit action fed by the Processor window's current stylized render (disabled with a status hint when no render exists), its own reference-image list, and Volume regeneration
2. Fitted parameters and reference images live on the asset — `NamerStyleProfile` no longer carries the fitted-post block (5.1-era profiles have a migration path)
3. Each asset generates/refreshes its own URP Volume profile under the GEN-01/GEN-03 generated-asset conventions; re-fit regenerates, and duplicating + tweaking an asset yields independent per-scene looks
4. ToneMatch is retired end-to-end: no UI knob, no serialized field, no kernel luma-transfer path — palette transfer (StyleStrength hue/vibrance, HueReplaceMode, smoothing) is otherwise byte-identical, and the zero-dispatch identity gate semantics are preserved
5. The "Stylized + Post" After View preview reads a selected `NamerPostLook` asset's parameters
6. The fit math (`NamerStylePostFit`) is unchanged — it moves home (asset instead of profile), preserving its determinism contract and existing test pins
**Plans:** 6/6 plans complete

Plans:

**Wave 1** *(parallel — disjoint files)*

- [x] 05.2-01-PLAN.md — Asset foundation: NamerPostLook ScriptableObject + constants + PostLookGuid pref, ApplyToPostLook + asset-targeted Volume writer overloads (full-strength), asset-side tests (STYL-10/12)
- [x] 05.2-02-PLAN.md — W14 channel-pane fix: pure visible-band clip seam + headless geometry pins, de-styled reservations (UI-04)

**Wave 2** *(blocked on Wave 1)*

- [x] 05.2-03-PLAN.md — ToneMatch retirement end-to-end: pipeline/kernel/field/slider/copy, NamerPostLookScale deletion, R8 Process companion-Volume removal, test retirement (STYL-11)

**Wave 3** *(blocked on Wave 2)*

- [x] 05.2-04-PLAN.md — Window rewire: ResolvePostLook + RunStylePostFit(NamerPostLook) seam + asset-driven lens (W10), Post Look row + fallbacks + tooltip (W9-W11), R3 profile-fit-UI removal, preview-test retarget (STYL-10/UI-04)

**Wave 4** *(blocked on Wave 3)*

- [x] 05.2-05-PLAN.md — NamerPostLookEditor A1-A14 (references + Fit Look prereq chain + editable params + Volume block) + D-07 load-time migration M1-M3 + lifecycle tests + USER_GUIDE (STYL-10/12)

**Wave 5** *(blocked on Wave 4)*

- [x] 05.2-06-PLAN.md — R4 profile slim-down + W12 + ApplyToProfile/writer-profile/migration deletions + full-suite live regression + consistency grep (STYL-10/11/12)

### Phase 05.3: Reference scene decomposition + merged style asset (INSERTED)

**Goal**: Add the model-free inverse front end to the shipped stylization pipeline: from ONE shared weighted reference list, estimate the reference grade (exposure, white balance, tone curve, contrast, saturation, bloom), classify pixels and recover de-graded base colors, then derive BOTH style blocks from that same list — the HSL palette (from the de-graded base colors) and the fitted post block (from the grade estimate) — living on ONE merged style asset. Palette application remaps the albedo texture to its closest palette entry BEFORE the vertex-color build (upholds 05-CONTEXT D-01). Creation is menu-only; the NAMER Processor panel only loads/uses the asset via an object field.

**Depends on**: Phase 5, Phase 5.1, Phase 5.2
**Requirements**: STYL-13, STYL-14, STYL-15, STYL-16, STYL-17
**Success Criteria** (what must be TRUE):

1. From the shared reference list, a model-free decomposition estimates the reference grade and classifies pixels (albedo / shading / specular / emissive / bloom) with a confidence mask, recovering de-graded base colors — consuming/inverting `NamerStylePostFit` math, not introducing a second estimator family
2. Contaminated pixels (bloom, specular, emissive, deep shadow, fog, transparency) carry zero/low palette weight via the confidence mask
3. The HSL palette is derived from the de-graded base colors by default, with the raw-pixel mode behind a switch; de-grade and the fitted post look are inverse-consistent (grade applied once, never doubled)
4. ONE merged style asset owns ONE weighted reference list and ONE editor producing both blocks; creation is menu-only (`Assets/Create/NAMER/…`) and the Processor panel exposes only a load/use object field (no Create button); migration merges existing `NamerStyleProfile` + `NamerPostLook` assets losing neither extracted palettes nor fitted looks; per-asset Volume generation carries over (05.2 D-06)
5. Closest-match albedo remap runs pre-fit (before the VC build); hue identity (STYL-03) and no-luma-transfer (STYL-11) remain pinned; post-fit VC remapping is a recorded rejected alternative
6. Forward validation renders NAMER VCs + surface + recovered Volume against the reference style; visible residual mismatch is the unlock condition for the 3D-LUT fallback (locked 5.1 decision)

**Plans:** 4/6 plans executed

Plans:

**Wave 1** *(parallel — disjoint files)*

- [x] 05.3-01-PLAN.md — Inverse grade math twin: NamerStyleGradeMath (LUT-stage CPU twins + five deterministic inverses + fixture-defined BuildNeutralSourceStats) + golden/round-trip pins (STYL-13/14)

- [x] 05.3-02-PLAN.md — Merged style asset foundation: NamerStyleAsset SO (three-flag state model, clamp/fingerprints) + menu-only creation M1 + legacy menu retirement M2/R4 + Volume writer merged overload + StyleAssetGuid pref/ResolveStyleAsset + sandbox mirroring + asset tests (STYL-15)

**Wave 2** *(blocked on Wave 1; 03/04 disjoint files — the validation threshold landed in plan 02's wave-1 constants block, so plan 03 never edits NamerEditorConstants.cs; only plan 04 adds merge strings there)*

- [x] 05.3-03-PLAN.md — Decomposer: per-pixel-confidence AccumulateReference seam + NamerStyleDecomposer two-pass (classification mask, Fit-over-neutral grade, de-graded/raw palette, confidence stats) + validation metric pure static + determinism/self-consistency tests (STYL-13/14/17)

- [x] 05.3-04-PLAN.md — Merge dialog: Tools > NAMER/Merge Style Assets… + pure carry core (union first-wins, cap honesty before creation, sources untouched, never-silent) + no-silent-loss tests (STYL-15)

**Wave 3** *(blocked on Wave 2 — depends_on 05.3-02 + 05.3-04; shares NamerEditorConstants.cs with plan 04, so the ordering is structural in the plan frontmatter)*

- [x] 05.3-05-PLAN.md — Window rewire + STYL-16 data retarget: NamerStyleStylization.Create(NamerStyleAsset) pre-fit composition, W15-W18 Style Asset object field, R1-R8 retirements, RunStylePostFit retarget seam, zero-dispatch identity gate (STYL-15/16)

**Wave 4** *(blocked on Waves 2-3 — needs the decomposer + the window validation seam)*

- [x] 05.3-06-PLAN.md — Merged asset editor S1-S17: shared list + Decompose Style transaction (neutral primary, exemplar-refined named complement) + Palette Source switch + carried post/Volume blocks + Validate Look (STYL-17 LUT-unlock evidence) + prereq-seam tests + USER_GUIDE collapse (STYL-14/15/17)

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
| 04.2. Gouraud projection one-texture (INSERTED) | 10/10 | Complete | 2026-09-17 |
| 04.3. Adaptive residual coverage (INSERTED) | 3/3 | Complete | 2026-09-22 |
| 5. Stylization | 3/3 | Complete   | 2026-09-24 |
| 5.1. Reference-fitted URP Post-processing Look (INSERTED) | 4/4 | Complete   | 2026-09-25 |
| 05.2. Standalone post-look asset (INSERTED) | 6/6 | Complete | 2026-09-28 |
| 05.3. Reference decomposition + merged style asset (INSERTED) | 6/6 | Ready for Verification|  |

---
status: fix_applied
trigger: "EditMode suite failure after Phase 3 UAT fixes: ComputeSmokeTests.FullPipeline_ProducesNormalizedAndPackedTextures_OnMetal - 'normalized base R (sRGB=True) Expected: 0.43003463745117188 +/- 0.0039 But was: 0.074509803921568626' (53/54); passed 53/53 the day before on identical code"
created: 2026-08-28T00:00:00Z
updated: 2026-08-28T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED - The project's active color space flipped Gamma -> Linear during the user's live UAT editor session (`ProjectSettings/ProjectSettings.asset`: `m_ActiveColorSpace: 0 -> 1`, with Unity auto-flipping `m_LightsUseLinearIntensity` and writing `applicationIdentifier`). `NamerComputePipeline.Upload` staged every source texture with a sampled `Graphics.Blit(sRGB Texture2D -> linear RT)`: under Gamma the blit passes an sRGB-declared texture's bytes through raw (the shader's `_SourceIsSrgb` decode is the single decode -> byte 110, pass); under Linear the blit hardware-decodes the sRGB source on upload, and the shader decodes again -> byte 19 = `SRGBToLinear(SRGBToLinear(0.50196))/0.50196`, fail. The clone test runner rsyncs `ProjectSettings/`, so every post-flip run executed under Linear.
test: Three-run A/B bisection: (1) full suite under Linear fails; (2) filtered ComputeSmoke with the uint2 compute edit reverted still fails identically (exonerates the gap-4 fix); (3) filtered ComputeSmoke with a FRESH Library still fails (exonerates clone-Library state); (4) filtered ComputeSmoke with ONLY `m_ActiveColorSpace` reverted to 0 in the clone passes 1/1, exit 0 (proves causality).
expecting: Byte-level arithmetic: expected 0.43003 = `SRGBToLinear(128/255)/(128/255)` (single decode); got 19/255 = 0.0745 (double decode). Under Linear the blit must decode; the shader's conditional decode assumes raw bytes.
next_action: Fix landed after two superseded attempts (see Resolution). Final form: raw-copy material blit (NamerRawCopy.shader) whose _REENCODE_SRGB variant re-encodes the Linear-project hardware decode in-shader, bit-exact, tolerating RGB24/mips/size mismatches. Validated: full EditMode 55/55 under Linear; filtered ComputeSmoke 2/2 under reverted Gamma. Awaiting user live re-verification on real imported assets.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: ComputeSmokeTests.FullPipeline_ProducesNormalizedAndPackedTextures_OnMetal passes all 4 scenarios: normalized base = single sRGB decode of the authored 0.5 gray divided by AO; packed AO byte = raw authored value with no sRGB decode (pinned by scenario 4, `dataMapsAreSrgb: true`).
actual: Scenario 1 fails under Linear with the exact double-decode byte; passed 53/53 under Gamma the day before on identical code.
errors: "normalized base R (sRGB=True) Expected: 0.43003463745117188d +/- 0.0039... But was: 0.074509803921568626d" (ComputeSmokeTests.cs:148).
reproduction: Any EditMode run (full or filtered to ComputeSmokeTests) with `m_ActiveColorSpace: 1` in the clone; deterministic. Same runs with `m_ActiveColorSpace: 0` pass.
started: Discovered 2026-08-28 while validating the Phase 3 UAT gap fixes; the first failing run predated any of the four fix commits reaching the compute path (exonerated by A/B).

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: The gap-4 `uint2 _Size` compute edit changed numeric behavior
  evidence: Filtered ComputeSmoke run with only the compute file reverted to `int2 _Size` (sed in the clone) failed with the identical expected/got values; positive dimensions are bit-identical through SetInts.
  timestamp: 2026-08-28

- hypothesis: Stale/corrupted clone Library (incremental import or shader cache flip)
  evidence: Filtered ComputeSmoke run after renaming the clone's Library away (full fresh import) still failed identically.
  timestamp: 2026-08-28

- hypothesis: Flaky/order-dependent GPU state or cross-test RT pollution
  evidence: Three consecutive deterministic failures across full-suite and filtered runs; the filtered runs execute only ComputeSmokeTests (no other fixture runs, so no cross-fixture pollution), and the passing 53 remaining tests never wavered.
  timestamp: 2026-08-28

- hypothesis: Cross-test pollution from the new AssetGenerator round-trip test (54th test)
  evidence: The failure reproduces under `-testFilter` runs where AssetGeneratorTests never executes.
  timestamp: 2026-08-28

- hypothesis: CopyTexture can stage uploaded sources (attempt 1, commit 8e28c83)
  evidence: Green headless (54/54 Linear) but failed live on imported assets - `Graphics.CopyTexture called with mismatching mip counts (src 12 dst 1)` (2-arg form copies all mip levels). The 6-arg mip-0 variant then failed the suite on imported RGB24 sources - `mismatching data size (src (1x1 with format 7 -> 3 bytes) dst (1x1 with format 8 -> 4 bytes))` (6-arg form requires equal block size). CopyTexture cannot serve imported sources at all.
  timestamp: 2026-08-28

## Evidence

- `git diff ProjectSettings/ProjectSettings.asset`: `m_ActiveColorSpace: 0 -> 1` (uncommitted, made in the user's editor during the 2026-08-27 UAT session; `GraphicsSettings.asset` `m_LightsUseLinearIntensity: 0 -> 1` and the auto-written `applicationIdentifier` accompany it).
- `/tmp/namer-unity-testrun/results-gamma.xml`: `total="1" passed="1" failed="0"`, Unity exit 0 - only `m_ActiveColorSpace` (and the companion lights flag) reverted in the clone, code unchanged.
- Byte arithmetic: `SRGBToLinear(128/255) = 0.21585`; `/0.50196 = 0.43003` = the assertion's expected value (single decode); `SRGBToLinear(0.21585)/0.50196 = 0.0761 -> byte 19 = 0.0745` = the actual (double decode).
- `NamerComputePipeline.Upload` (pre-fix): `Graphics.Blit(source != null ? source : fallback, target)` - a sampled blit of an sRGB-declared Texture2D into a linear RT.
- `NAMERPack.compute` CSNormalize: `float3 base = (_SourceIsSrgb > 0.5) ? SRGBToLinear(baseColorIn.rgb) : baseColorIn.rgb;` - assumes `_BaseColorIn` holds RAW authored bytes.
- ComputeSmokeTests.cs:133 scenario-4 contract: "packed AO byte must be the raw authored value, no sRGB decode (sRGB data maps = True)" - the same Linear blit decode would also corrupt sRGB-declared data maps, so the fix covers inputs generally, not just base color.
- Scope note: under Linear the same double-decode affected the live editor path - `NamerProcessor.Process` on an sRGB base map would have written a visibly dark `_Base.png` (this is what the test caught; it is a real correctness bug, not a test artifact).
- Live re-verification error (user, 2026-08-28): `Graphics.CopyTexture called with mismatching mip counts (src 12 dst 1)` at `NamerComputePipeline.Upload:231` via `NamerEditorWindow.RecomputePreview` - the stack that killed attempt 1.
- Attempt 2 suite failure: 6 AssetGeneratorTests failures, all `Graphics.CopyTexture called with mismatching data size (src (1x1 with format 7 -> 3 bytes) dst (1x1 with format 8 -> 4 bytes))` - format 7 = RGB24 imported sources.

## Resolution

Fixed 2026-08-28 after two superseded attempts; final form is a raw-copy material blit.

**Root cause:** `Upload` staged sources with a plain sampled `Graphics.Blit(sRGB Texture2D -> linear RT)`. Under a Gamma project the blit passes sRGB-declared bytes through raw; under Linear the hardware decodes them on upload, and the kernels' conditional `_SourceIsSrgb` decode then double-decodes. Not a code regression - the user's editor flipped the project Gamma -> Linear during UAT; the tests were correct to fail under the new settings.

**Attempt 1 (8e28c83, superseded):** `Graphics.CopyTexture` when dimensions match. Green headless (54/54 Linear, 1/1 Gamma) but failed live re-verification: imported assets carry mip chains (12 levels) the single-mip targets lack - the 2-argument `CopyTexture` throws `mismatching mip counts (src 12 dst 1)`.

**Attempt 2 (never committed):** 6-argument mip-0 `CopyTexture`. Failed the suite - the 6-arg form requires equal block size, and imported RGB24 sources (3 B/px) are incompatible with the RGBA32 targets (6 AssetGeneratorTests failures, `mismatching data size`). `CopyTexture` cannot serve imported sources at all: no mips, no cross-block-size, no scaling.

**Final fix:** raw-copy blit through a dedicated material, `Editor/Pipeline/NamerRawCopy.shader` (`Hidden/Namer/NamerRawCopy`).
- `Upload` now always does `Graphics.Blit(upload, target, rawCopyMaterial)`. When the source is sRGB-declared (`GraphicsFormatUtility.IsSRGBFormat(upload.graphicsFormat)`) AND the project is Linear (`QualitySettings.activeColorSpace`), it enables `_REENCODE_SRGB`; otherwise the keyword is off and the pass is a plain copy (Gamma never decodes on sample, so raw bytes pass through exactly as before the color-space flip).
- The `_REENCODE_SRGB` variant applies the exact IEC 61966-2.1 linear -> sRGB encode in-shader (the inverse of the kernels' `SRGBToLinear` - deliberately not UnityCG's approximate `LinearToGammaSpace`). The hardware decode and the shader re-encode both run in float inside one pass with a single quantization at the UNorm8 write, so the source bytes arrive bit-exactly: the same guarantee `CopyTexture` gives, without its equal-dimension / equal-mip-count / equal-block-size restrictions. A sampled blit accepts every real-world source shape - RGB24 layouts, mip chains (samples LOD 0 at 1:1 scale), and non-base resolutions (resamples, the only correct behavior there anyway).
- Input RTs stay `R8G8B8A8_UNorm` (from 8e28c83; sources are 8-bit so nothing is lost), outputs/intermediates unchanged. Kernels and `_SourceIsSrgb` untouched - still the single decode authority in both color spaces.
- The material is static, `HideAndDontSave`, loaded from `RawCopyShaderPath` with a throw-if-missing mirroring the compute shader's load. UnityCG built-in style on purpose: a direct `Graphics.Blit` draw with no render-pipeline dependency.

**Regression coverage:** `Process_AcceptsMipmappedSourceTextures_CopyingMip0Raw` (mip-chained RGBA32 sources, sRGB and linear) plus the existing AssetGeneratorTests fixture (RGB24 1x1 imported sources - these are what caught attempt 2).

**Validation:** full EditMode 55/55, Unity exit 0, under the repo's (Linear) settings; filtered ComputeSmoke 2/2, exit 0, with the clone reverted to Gamma (sed, clone-only). Both color spaces green on identical code.

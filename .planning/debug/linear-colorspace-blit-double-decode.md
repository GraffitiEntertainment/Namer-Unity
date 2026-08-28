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
next_action: Fix applied - Upload now raw-copies (`Graphics.CopyTexture`) when source and target dimensions match (no sampling, no color-space conversion in either project color space), falling back to Blit only for the resolution-mismatch path (the 1x1 linear-declared fills, and rare maps whose resolution differs from the base map). The four input RTs were re-declared R8G8B8A8_UNorm (copy-compatible with RGBA32 sources; sources are 8-bit so no precision is lost). Kernels and `_SourceIsSrgb` are unchanged - they remain the single decode authority in both color spaces.

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

## Evidence

- `git diff ProjectSettings/ProjectSettings.asset`: `m_ActiveColorSpace: 0 -> 1` (uncommitted, made in the user's editor during the 2026-08-27 UAT session; `GraphicsSettings.asset` `m_LightsUseLinearIntensity: 0 -> 1` and the auto-written `applicationIdentifier` accompany it).
- `/tmp/namer-unity-testrun/results-gamma.xml`: `total="1" passed="1" failed="0"`, Unity exit 0 - only `m_ActiveColorSpace` (and the companion lights flag) reverted in the clone, code unchanged.
- Byte arithmetic: `SRGBToLinear(128/255) = 0.21585`; `/0.50196 = 0.43003` = the assertion's expected value (single decode); `SRGBToLinear(0.21585)/0.50196 = 0.0761 -> byte 19 = 0.0745` = the actual (double decode).
- `NamerComputePipeline.Upload` (pre-fix): `Graphics.Blit(source != null ? source : fallback, target)` - a sampled blit of an sRGB-declared Texture2D into a linear RT.
- `NAMERPack.compute` CSNormalize: `float3 base = (_SourceIsSrgb > 0.5) ? SRGBToLinear(baseColorIn.rgb) : baseColorIn.rgb;` - assumes `_BaseColorIn` holds RAW authored bytes.
- ComputeSmokeTests.cs:133 scenario-4 contract: "packed AO byte must be the raw authored value, no sRGB decode (sRGB data maps = True)" - the same Linear blit decode would also corrupt sRGB-declared data maps, so the fix covers inputs generally, not just base color.
- Scope note: under Linear the same double-decode affected the live editor path - `NamerProcessor.Process` on an sRGB base map would have written a visibly dark `_Base.png` (this is what the test caught; it is a real correctness bug, not a test artifact).

## Resolution

Fix applied 2026-08-28 (see git log `fix(pipeline): raw-copy texture uploads so color conversion is color-space independent`):
- `NamerComputePipeline.Upload`: `Graphics.CopyTexture` when `upload` dimensions match the target (raw texels, no sampling, no conversion in either color space); `Graphics.Blit` only on the mismatch path (1x1 linear-declared fills and rare non-base-resolution maps).
- Input RTs (`baseColorIn`, `normalTexel`, `aoIn`, `metallicGlossIn`) re-declared `R8G8B8A8_UNorm` so they are copy-compatible with RGBA32 sources; outputs/intermediates unchanged (`R16G16B16A16_SFloat` intermediates, `R8G8B8A8_UNorm` packed surface). Pool buckets unchanged for surfaceOut (identical descriptor); inputs get their own bucket.
- Kernels untouched: `_SourceIsSrgb` stays the single decode authority under both color spaces; data maps stay raw per the scenario-4 contract.
- Validation: full EditMode suite under the repo's (Linear) settings + filtered ComputeSmoke under reverted Gamma settings - both must be green before this is closed.

---
status: resolved
trigger: "AO looks wrong in Game view: dark-painted regions (clothing) get ambient-darkened as if occluded — the luminance-extraction AO map itself is wrong"
created: 2026-09-21T21:00:00Z
updated: 2026-09-22T00:45:00Z
---

## Current Focus

hypothesis: RESOLVED. Both fix cycles live-verified 2026-09-21 (see Resolution.verification): suite 186/186 green; Neo AO-OFF readback flat 255 on 100.0000% of 4,194,304 texels; Neo AO-ON readback crevice-shaped with r(luma, ao) = -0.1564 (was +0.4346) and quintile spread 0.30 → 0.04; user confirmed the visual fix and realtime slider response.
test: none remaining — human verification passed.
expecting: n/a.
next_action: archive — commit the seven authored files atomically (never Assets/NAMERGenerated/, NO push), move this session to .planning/debug/resolved/, append the knowledge-base entry, return DEBUG COMPLETE.

## Symptoms

expected: Synthetic AO (when there is no authored _OcclusionMap) should darken ambient only where geometry actually self-occludes (crevices, contact shadows); flat dark-painted regions should NOT read as occluded.
actual: Game view visibly darkens ambient across the character's dark clothing/shading — regions with no geometric occlusion. User: "the AO looks wrong". The AO debug toggle now visibly responds (round-trip fixed), which surfaced this map-quality defect.
errors: none.
reproduction: NAMER Processor on Assets/Models/Neo (tripo_mat_d83278e6 — no authored _OcclusionMap), Process, view in Game view with ambient; toggle AO off/on to isolate the contribution. Numeric: read back surface.b from the generated Surface PNG (readable copy via new Texture2D(2,2)+LoadImage; direct GetPixels32 fails on non-readable imports).
started: Visible since the first Neo processing; isolated as a distinct defect 2026-09-21 after the ao-unmultiply-roundtrip fix landed (93ca67b) and the user confirmed the toggle responds.
context: Symptoms prefilled from the completed ao-unmultiply-roundtrip investigation (resolved 2026-09-21, commit 93ca67b) plus the user's live confirmation. User's stored AoUnmultiplyStrength=0.1613 nearly neutralizes the pack-time divide, but surfaceData.occlusion (= surface.b × _OcclusionStrength=1) darkens ambient REGARDLESS of un-multiply strength — that ambient darkening is the visible defect.

Prior sessions (all resolved, do not resume): ao-unmultiply-roundtrip (round-trip contract C — re-multiply + gate rides it), ao-debug-view-no-change (BVH root-index bug — fixed; its TriggerAutomaticBake/bake-cache notes informed the unreachability finding), ao-recompute-affordance, ao-slider-overwrite-gate.

Project testing discipline: Unity Test Framework EditMode tests; run in the LIVE editor via unity-mcp RunCommand (TestRunnerApi + ICallbacks writing results to Temp/<slug>-results.txt, watch from Bash; NO AssetDatabase.Refresh before the test command; ImportAssetOptions.ForceUpdate + global::-qualified CompilationPipeline.RequestScriptCompilation to pick up external edits — note ImportAsset alone never recompiles AND the editor defers compilation until it gains focus, so ask the user to focus Unity when the dll-wait loop times out; ICallbacks member is TestFinished not TestResult; declare callback collectors as TOP-LEVEL internal sealed classes — the sandbox hoists nested ones and private nested hits CS1527; verify compile via strings Library/ScriptAssemblies/GraffitiEntertainment.Namer.Tests.Editor.dll | grep <symbol>). Never judge renders via vision models — numeric readback only.

## Eliminated

- hypothesis: The AO contribution fails to round-trip through Unity's lighting (pack divide vs ambient-only occlusion; invisible gate).
  evidence: FIXED by commit 93ca67b (contract C): _AoUnmultiplyStrength persisted on generated materials (AssetGenerator.cs:682), decode re-multiplies the GATED ao into albedo (NamerSurface.hlsl:111 gate, :137 re-multiply), ShaderLab default 0.0. User confirmed 2026-09-21 the After-pane AO toggle now visibly responds after re-Process. This session is about the MAP's content, not its consumption.
  timestamp: 2026-09-21T21:00:00Z

- hypothesis: Packed AO data corrupted / wrong channel / sRGB import.
  evidence: Surface PNG importer sRGB=False (verified live, prior session); decode ao = surface.b × _OcclusionStrength (NamerSurface.hlsl:84); surface.b carries smooth, plausible-looking structure — it is the ESTIMATOR's output behaving as designed, which is the problem.
  timestamp: 2026-09-21T21:00:00Z

- hypothesis: The geometry bake produced this map.
  evidence: Bake branch unreachable in production since 307b305 — _bakeCache is in-memory only (NamerAoPipeline), NamerProcessor.Process constructs a fresh pipeline per run, HasCachedBake always false. The prior ao-debug-view-no-change session's "bake supersedes extraction after first recompute" note described TriggerAutomaticBake seeding a cache that no longer feeds the pack path.
  timestamp: 2026-09-21T21:00:00Z

## Evidence

- timestamp: 2026-09-21T22:30:00Z
  checked: AO stage checkbox consumption — NamerProcessor.cs:133 and NamerEditorWindow.cs:384 (`inspection.AoUnmultiplyStrength = stageEnabled ? strength : 0f`), NamerEditorConstants.cs:66 (DefaultAoStageEnabled = true)
  found: The checkbox currently zeroes ONLY the un-multiply strength. The D-07 gate in NamerComputePipeline.cs:144-162 runs regardless, so surface.b still carries the synthetic (luminance) AO even with the checkbox off — ambient still darkens. The checkbox is not the "process AO?" switch its UI position implies.
  implication: The user's rethink ("the gating is what to process/extract, so that should stay; if AO is checked it should extract the AO map") targets exactly this gap: the checkbox must gate the whole stage, and the gate code decides the source.

- timestamp: 2026-09-21T21:00:00Z
  checked: NamerComputePipeline.cs:144-162 (D-07 three-way gate: authored _OcclusionMap > cached bake > luminance extraction), NamerAoPipeline.cs (Extract chain + bake cache), git 307b305
  found: Neo has no authored AO; bake cache never populated in a production Process run; extraction is the operative synthetic source.
  implication: Any fix lands in the extraction path, its gating, or a revived bake — not in pack/render consumption (already correct post-93ca67b).

- timestamp: 2026-09-21T21:00:00Z
  checked: Generated Neo Surface.png channel B via live-editor readback (readable Texture2D.LoadImage copies), pre-93ca67b output
  found: min=0.169 max=1.000 mean=0.801; 50.7% of texels <0.9; 13.3% <0.5. max(baseLum×ao)=0.717.
  implication: Half the character is flagged as occluded; depth of 0.169 = 5.9x ambient reduction in the darkest-painted regions — consistent with dark albedo driving the estimator, not crevice geometry.

- timestamp: 2026-09-21T21:00:00Z
  checked: Extraction normalization (NAMERAO.compute: CSLuminance → block-average → gaussian low-pass → AO remap; prior session ao-debug-view-no-change documents ≈ saturate(lowpass(luma)/globalLumaMean))
  found: The scale is a single GLOBAL luminance mean over the whole atlas — a texture that is uniformly dark everywhere (black clothing) has lowpass(luma) ≈ its own darkness, so AO ∝ albedo brightness regardless of local geometry.
  implication: The heuristic structurally conflates albedo with occlusion; no global constant can separate them. Options (c)/(d) change the estimator; options (a)/(b) stop using it.

- timestamp: 2026-09-21T21:00:00Z
  checked: User's live editor state after re-Process (post-93ca67b)
  found: AO debug toggle visibly responds (user-confirmed). Assets/NAMERGenerated/ churn in the working tree is the user's regenerated output — treat as user state, never stage without asking.
  implication: Consumption path validated in practice; remaining defect is purely the map content.

- timestamp: 2026-09-21T21:50:00Z
  checked: CURRENT on-disk Neo output post-93ca67b re-Process (generated files mtime 2026-09-21 13:35 vs commit 13:30:25) — sips→BMP→numpy byte forensics on Assets/NAMERGenerated/Neo-T-Pose/tripo_mat_d83278e6_Namer_Surface.png (2048², 32bpp, read-only; scratch copies in /tmp/ao-luma/)
  found: surface.b min=0.176 max=1.000 mean=0.799; 51.2% of texels <0.9; 12.8% <0.5 (pre-fix live readback: 0.169/1.000/0.801; 50.7%; 13.3%). Base atlas fully opaque (alpha=255 on 100.0% of texels — padding is not a correlation confound).
  implication: surface.b is UNCHANGED by 93ca67b within 8-bit quantization, exactly as predicted (that commit touched decode consumption, not the estimator). Working hypothesis NOT reopened. Side note: max(baseLum×ao) moved 0.717→0.514, but that product depends on Base.png, which now carries the 04.2 projected base (NamerComputePipeline.cs:206-209) and whatever settings differed between the two Process runs — not an AO-channel change.

- timestamp: 2026-09-21T21:50:00Z
  checked: per-texel albedo-conflation measurement, Base.png vs Surface.png from disk, using the estimator's own luminance definition (Rec.709 of sRGB-decoded linear channels — NAMERAO.compute:77-78, NAMERAO.hlsl:20-22)
  found: Pearson r(luma_linear, surface.b) = +0.4346 (all texels; covered identical since 100% opaque); Spearman rho = +0.3405; r on raw sRGB bytes = +0.3959. Luma-quintile mean AO on covered texels: Q1 [0.000,0.019] = 0.813, Q2 [0.019,0.032] = 0.731, Q3 [0.032,0.053] = 0.695, Q4 [0.053,0.213] = 0.769, Q5 [0.213,0.645] = 0.990.
  implication: albedo brightness alone orders the extracted AO — the brightest fifth of the atlas averages ao 0.990 (no darkening) while the darker three-fifths average 0.70-0.81 (20-30% ambient cut; 12.8% of texels below 0.5, min 0.176 = up to 5.7x ambient reduction). Pearson r is moderate only because ao is a 64px-blurred luma (fine texture decorrelates per-texel values); the estimator is structurally a rescaled blur of albedo. Decisive static fact: the extraction kernels (CSLuminance/CSAverage/CSBlurH/CSBlurV/CSAoRemap) consume NO geometric input — only _BaseColorIn and scalars — so the map cannot respond to crevice geometry by construction.

- timestamp: 2026-09-21T21:50:00Z
  checked: static re-verification of the GLOBAL-mean normalization claim (file:line)
  found: NamerAOPipeline.cs:125 ReduceToScalarMean (impl :514-540) reduces the whole-atlas luminance RT to ONE scalar (ReadBackLumaAverage :542-568 returns a single float); :143 uploads it as _LumaAverage; NAMERAO.compute:62 declares the scalar; :176 `ao = saturate(_Dst[id.xy].r / max(_LumaAverage, 1e-4));` divides every texel by that same global constant (mode split :167-177; _AoDirect=0 extraction branch is the r/_LumaAverage one; remap weights/strength/contrast at NAMERAO.hlsl:20-22 and :28-33).
  implication: CONFIRMED — one global constant normalizes the entire atlas; uniformly dark paint has lowpass(luma) ≈ its own darkness, so AO ∝ albedo brightness regardless of local geometry.

- timestamp: 2026-09-21T21:50:00Z
  checked: static re-verification of bake-branch unreachability since 307b305 (file:line), sharpened
  found: (1) NamerProcessor.cs:128 constructs a fresh NamerComputePipeline per Process → lazy fresh NamerAOPipeline (NamerComputePipeline.cs:49, :303-311) → _bakeCache (NamerAOPipeline.cs:52-53, in-memory instance field) is always empty when the gate runs. (2) RequestBake has ZERO production callers since 307b305 deleted TriggerAutomaticBake (62 lines removed from NamerEditorWindow.cs, incl. the BakeSourceMesh priming in RecomputePreview); only callers are tests (NamerAOBakeTests.cs:290, :354). (3) BakeAndUpload's only production caller is the gate itself (NamerComputePipeline.cs:154), which requires HasCachedBake true first — chicken-and-egg; the deleted trigger was the only bootstrap. (4) BakeSourceMesh IS still primed when decomposition is enabled (NamerProcessor.cs:143-146; window preview path NamerEditorWindow.cs:397), so the mesh input to a revived bake exists today. (5) The full bake stack is intact and tested: NamerAOBaker (Editor/Bake/NamerAOBaker.cs:52-57 — Burst job + BVH, kBakeResolutionCap=512, kRayCount=64 cosine-weighted rays, kCageOffset=0.01, cancelable with progress bar when shouldCancel==null), bilinear upsample + 16px jump-flood seam dilation (NAMERAO.compute:188-259; NamerAOPipeline.cs:242-317), D-11 tweak stages reused.
  implication: option (d) is rewiring, not revival — only the production trigger was deleted; the machinery, cache, tests, and mesh priming all survive.

- timestamp: 2026-09-21T21:50:00Z
  checked: test contracts touching the D-07 fallback (decision-relevant)
  found: NamerAOExtractionTests.cs pins 4 behaviors; `Extraction_ProducesNonWhiteAo_WhenNoOcclusionMap` (:34) explicitly asserts the luminance fallback yields non-white AO (its doc: "kills the live pain" of having no AO at all). The other three (round-trip :100, divisor-side floor :121, authored byte-identity :144) are source-agnostic. NamerAOBakeTests.cs covers bake/caching/cancellation machinery.
  implication: options (a)/(b-default-off) invert one pinned test's expectation — a spec change the user must own via the pending decision; (d) likely needs zero test changes; (c) changes the numbers the first test tolerates.

- timestamp: 2026-09-21T23:40:00Z
  checked: implementation of the refined contract (clause 0 + clauses 1-4), all edits re-read post-write
  found: (a) NamerSourceModel.cs:87-96 `public bool AoStageEnabled;` with the no-default convention comment; (b) NamerComputePipeline.cs:143-171 gate = authored-first (checkbox-independent) → `else if (inspection.AoStageEnabled)` BakeAndUpload (:151,:159) → `if (aoIn == null)` WhiteFill upload (:163-171); `usesExtractedAo` fully removed (grep: zero references); (c) NamerProcessor.cs:133-139 straight `AoUnmultiplyStrength = settings.AoUnmultiplyStrength` + `AoStageEnabled = settings.AoStageEnabled`; (d) NamerEditorWindow.cs:384-389 same for the preview path, tooltip rewritten (:887-891); (e) NamerAOPipeline.cs retirement docs + corrected fallback comments; (f) tests per Resolution.fix.
  implication: the albedo-conflated estimator has ZERO production call sites; the checkbox now gates synthesis only; authored data is never gated.

- timestamp: 2026-09-21T23:40:00Z
  checked: static regression screen of every NamerComputePipeline.Process call-site in Tests/Editor (grep enumeration) against the new gate
  found: authored-map fixtures (NamerAOUnmultiplyRoundTripTests, ComputeSmokeTests :105/:242/:499, NamerOneTextureTests E2E via CreateImportedWhiteOcclusion, NamerRoughnessFitTests, AssetGeneratorTests BasePng isolation) take the unchanged authored branch. Null-map fixtures with uniform bases (ComputeSmokeTests white-fill/oracle tests) are divide-identity before AND after. Null-map + non-uniform fixtures (NamerRoughnessExtractionTests Sobel/dip, NamerAOExtractionTests rewritten) assert relative/directional properties robust to the divide disappearing. Settings-E2E decomposition fixtures use flat quads whose bake ao≈1.0 → near-identity divide. NamerProjectionAcceptanceTests BuildInspection passes authored white occlusion and asserts alpha bits.
  implication: no test found that pins extraction-divided ABSOLUTE base values; residual risk is confined to threshold-sensitive E2E outcomes (residual-required flips) that only the live run can adjudicate.

- timestamp: 2026-09-21T23:40:00Z
  checked: known interaction flagged (NOT fixed — scope): BakeSourceMesh priming remains decomposition-gated (NamerProcessor.cs `if (settings.DecompositionEnabled)` block; NamerEditorWindow.cs decompWillRun ternary)
  found: AO stage ON + decomposition OFF + no authored map → no bake mesh → gate packs white (BakeAndUpload returns null). The user's workflow (decomposition on) is unaffected; extending priming to `DecompositionEnabled || AoStageEnabled` is a 2-line follow-up the user did not request (scope-creep guard) — surfaced in the checkpoint for an explicit yes/no.
  implication: if the user wants bake-backed AO with decomposition off, that is a follow-up decision, not a defect of this contract.

- timestamp: 2026-09-22T00:05:00Z
  checked: live verification of cycle 3 (coordinator-run)
  found: compiled clean; FULL EditMode suite 185/185 green (baseline 183 − retired luminance pin + 3 new tests; zero failures). User decision on the flagged interaction: EXTEND the BakeSourceMesh priming — AO stage ON + decomposition OFF + no authored map must bake, not pack white.
  implication: cycle 3's contract + implementation are live-confirmed; proceeding to cycle 4 (priming extension at both production sites + a pinning E2E) per the user's decision.

- timestamp: 2026-09-22T00:10:00Z
  checked: CR-01 guard interaction with the priming extension (NamerProcessor.cs:105/:117-126, DecompositionSkipReason :721-741; NamerEditorWindow.cs:292-293,:400-402)
  found: with the mesh resolved for AO-only runs, a multi-material/multi-mesh selection trips DecompositionSkipReason → decomposeSourceMesh nulled → BakeSourceMesh null → white fill (the same protective CR-01 outcome), but the warning text is decomposition-worded ("Vertex-color decomposition skipped..."). decompGuardTripped's only consumer (:191-203) sits inside `if (settings.DecompositionEnabled)` so no extra warnings fire when decomp is off. Window mirror: _decompGuardReason is computed unconditionally (:293) from _previewMesh (:292), so `(decompWillRun || _aoStageEnabled) && guard-empty` mirrors Process exactly.
  implication: protective behavior extends correctly to AO-only runs; rewording the warning is cosmetic and out of scope (minimal change) — noted for the user in the checkpoint.

- timestamp: 2026-09-22T00:10:00Z
  checked: pinning-test geometry requirements (Bake_FlatPlane_ProducesAoNearOne :66-91 confirms flat self-bake ≈ 1.0; ResolveOccluder null-fallback D-06 confirmed at NamerSourceModel.cs:76)
  found: a processor-E2E pin needs a SELF-OCCLUDING mesh asset (production never sets OccluderMesh; flat quads bake ≈255 = indistinguishable from white fill). Designed open-box fixture: floor y=0 z∈[-2,0] (+Y normal, uv v∈[0,0.5], v=0 AT the wall) + wall x∈[0,1] y∈[0,1] z=0 (-Z normal facing the floor, uv v∈[0.5,1]); 8 verts, non-welded shared edge, triangles {0,1,2, 0,2,3, 4,6,5, 4,7,6} (winding verified by cross products for +Y/-Z). Texel centers keep floor origins strictly z<0 (row centers ≈ -0.008 min) avoiding the coplanar-edge degeneracy; the 1-unit wall subtends ≈89° from the bottom rows → corner AO ≈ 0.5 (~128). Floor depth 2 puts the far band (rows 24-30, v∈[0.375,0.469) → z beyond -1.5) far past the wall's reach → far AO ≈ 230-255; the band stays below the v=0.5 seam (no wall-base contamination). NamerProcessorSettings setters WRITE EditorPrefs (verified :68-113) → the E2E needs a mini prefs snapshot/restore (NamerDipSwitchTests StepGateSnapshot idiom) for the 8 keys the initializer touches.
  implication: decisive E2E achievable with corner rows [0,4) mean < 220 and far-minus-corner > 30; white fill (255 flat) and any uniform darkening are both excluded by the pair.

- timestamp: 2026-09-22T00:25:00Z
  checked: cycle-4 implementation (priming extension), all edits re-read post-write; brace balance verified on all three edited files
  found: (a) NamerProcessor.cs:107 `(settings.DecompositionEnabled || settings.AoStageEnabled) ? ResolveSourceMesh(selection) : null` + :151-159 priming under the same disjunction with the CR-01 note; (b) NamerEditorWindow.cs:403-408 `primeBakeMesh = (decompWillRun || _aoStageEnabled) && string.IsNullOrEmpty(_decompGuardReason)` → `inspection.BakeSourceMesh = primeBakeMesh ? _previewMesh : null`; (c) NamerAOBakeTests.cs `Process_AoStageOn_DecompositionOff_PrimesBakeMesh` (:523, processor-E2E: open-box asset + solid-gray linear base + occlusion-free URP Lit material + scene object; settings pin decomp OFF / AO ON / identity strength-contrast-blur / metallic+emissive contributions off; asserts corner-band mean < 220 and far-minus-corner > 30 on the written Surface PNG via File.ReadAllBytes+LoadImage+GetPixels32) with fixtures CreateOpenBoxMeshAsset/CreateImportedSolidBaseMap/CreateSourceMaterial/CreateSceneObject/EnsureTempFolder/DecodeSurfacePng/BlueBandMean and the 8-key SettingsSnapshot/Capture/Restore.
  implication: AO-ON + decomp-OFF + no authored map now bakes at both production sites and the E2E pins it decisively (white fill packs exactly 255 — the un-primed defect cannot pass).

- timestamp: 2026-09-22T00:25:00Z
  checked: static regression sweep of every NamerProcessor.Process caller + window tests against the priming extension
  found: material-selection E2Es (AssetGeneratorTests, NamerReprocessTests double-process/bind-swap) — ResolveSourceMesh returns null for standalone .mat selections (documented at NamerProcessor.cs:626-631; FindMeshSubAsset on a lone .mat finds nothing) → white fill, byte-identical. Meshless GameObjects (NamerReprocessTests/NamerRendererBindingTests/SourceImmutabilityTests use MeshRenderer-only objects) → mesh null → identical. The single decomp-false meshy E2E (NamerDecompIntegrationTests :123) asserts only structural outcomes (empty MeshPath/ResidualTexturePath, _BaseResidualMap binding, renderer mesh unchanged); its flat-quad self-bake ≈0.999 shifts generated bytes ≤~1 with no assertion reading them; BindGeneratedMaterials (:402) additionally requires a non-empty MeshPath so AO-only priming never triggers mesh rebinding. Authored-occlusion E2Es (OneTexture/RoughnessFit/ProjectionAcceptance) take the unchanged authored branch. NamerAfterPanelStateTests matched only a cref (pure logic); no window test drives RecomputePreview.
  implication: no existing test's outcome changes; residual risk is confined to live-run adjudication of the new E2E's band magnitudes (estimates carry ≥90-byte margins).

- timestamp: 2026-09-22T00:40:00Z
  checked: cycle-4 live verification (coordinator-run recompile + suite + two user re-Process runs with numeric disk readbacks)
  found: suite 186/186 green (rebuilt dll verified via the PrimesBakeMesh symbol) — the priming E2E passed on first live run. Neo AO-OFF (14:45): surface.b = 255 on 100.0000% of 4,194,304 texels (mean 255.000000, zero below) — flat 1.0, Game-view darkening on dark clothing gone. Neo AO-ON (14:48): min=0.275 max=1.000 mean=0.946; <0.9: 18.0%; <0.5: 0.9%; r(luma_linear, ao) = -0.1564 (retired extractor: +0.4346); luma-quintile mean AO Q1-Q5 = 0.950/0.938/0.956/0.963/0.924 (old: 0.813/0.731/0.695/0.769/0.990 — spread 0.30 → 0.04). User confirmed the visual fix and realtime AO slider response (shader-side gates).
  implication: root cause and both fix cycles fully verified end-to-end; human verification passed; session ready to archive.

## Specialist Review

- timestamp: 2026-09-21T22:00:00Z
  specialist_dispatch: attempted per session config (specialist_hint "general" → engineering:debug) — NO MATCHING SKILL INSTALLED in this environment (user-level and project skill dirs contain GSD skills only). Proceeding directly per the dispatch table's no-match rows; verification weight instead carried by the cycle-2 numeric + static evidence above.

## Resolution

root_cause: The D-07 luminance-extraction AO map is structurally albedo-driven, not geometry-driven. With no authored _OcclusionMap and no reachable bake, NamerComputePipeline.cs:160 calls NamerAOPipeline.Extract, whose chain (CSLuminance → hierarchical block-average → gaussian low-pass, radius 64/σ≈21px at 2K → remap) normalizes every texel by ONE global scalar (_LumaAverage: NAMERAO.compute:62,:176; NamerAOPipeline.cs:125,:143) and consumes no geometric input whatsoever — so AO ≈ saturate(gaussian_blur(albedo luma)/global mean luma). Measured on the regenerated Neo output: brightest luma quintile mean ao 0.990 vs darker three-fifths 0.70-0.81 (r(luma, ao)=+0.4346; 12.8% of texels <0.5; min 0.176 → up to 5.7x ambient cut). Dark clothing therefore reads as occlusion and Game view darkens ambient across it. The geometry-bake alternative is unreachable in production since 307b305 (no RequestBake/BakeAndUpload caller outside the chicken-and-egg gate; fresh pipeline per Process).
fix: IMPLEMENTED 2026-09-21 cycle 3 (refined contract incl. clause 0) — LIVE-VERIFIED 2026-09-21 (compiled clean; full EditMode suite 185/185 green) — PLUS cycle 4 (user-decided priming extension): (1) NamerSourceModel.cs:87-96 adds `AoStageEnabled` (inline default false, RoughnessExtractStrength convention — direct Process callers/fixtures keep the identity path; shipped default-on arrives via settings). (2) NamerComputePipeline.cs:143-171 rewrites the D-07 gate: authored map FIRST and checkbox-INDEPENDENT (clause 0); stage ON + no authored → `BakeAndUpload` directly (bakes fresh on cache miss — no more HasCachedBake chicken-and-egg) with `usesBakedAo = aoIn != null`; stage OFF or null bake → WhiteFill upload so surface.b packs 1.0 and the divide is identity at any strength. Dead `usesExtractedAo` removed. (3) NamerProcessor.cs:133-143 + NamerEditorWindow.cs:384-389: the un-multiply strength passes through UNCONDITIONALLY and the stage flag is set from settings/`_aoStageEnabled`. (4) NamerEditorWindow.cs:887-891 tooltip updated to the stage-gate semantics. (5) NamerAOPipeline.cs: Extract() retained but documented RETIRED; stale comments corrected. (6) CYCLE 4: NamerProcessor.cs:102-107 resolves the source mesh when `DecompositionEnabled || AoStageEnabled` and :151-159 primes `inspection.BakeSourceMesh` under the same disjunction (CR-01 guard still nulls it for multi-material/multi-mesh selections → safe white fill); NamerEditorWindow.cs:403-408 preview mirror `primeBakeMesh = (decompWillRun || _aoStageEnabled) && guard-empty`. Tests: NamerAOExtractionTests.cs rewritten to the contract (`AoStageOff_NoOcclusionMap_PacksWhiteAo` :38, `AoStageOff_AuthoredOcclusionMap_TransfersAuthoredAo` :107); NamerAOBakeTests.cs `Process_AoStageOn_NoAuthoredMap_BakesGeometryOnDemand` (:398) + cached-routing test sets `AoStageEnabled = true` (:348) + cycle-4 `Process_AoStageOn_DecompositionOff_PrimesBakeMesh` (:523, processor-E2E with the self-occluding open-box fixture, corner band < 220 / far-minus-corner > 30) with its fixture/helper block and 8-key EditorPrefs snapshot-restore.
verification: FULLY VERIFIED LIVE 2026-09-21 (both cycles). Cycle 3: compiled clean, full EditMode suite 185/185 green. Cycle 4: rebuilt dll verified via the PrimesBakeMesh symbol, full EditMode suite 186/186 green (185 + the priming E2E). User runs on Neo (tripo_mat_d83278e6, no authored _OcclusionMap): RUN 1 — AO stage OFF, re-Process 14:45: disk readback of tripo_mat_d83278e6_Namer_Surface.png shows surface.b = 255 on 100.0000% of 4,194,304 texels (mean 255.000000, zero below) — flat 1.0; user confirmed the Game-view darkening on dark clothing is GONE. RUN 2 — AO stage ON (geometry bake), re-Process 14:48: surface.b min=0.275 max=1.000 mean=0.946; <0.9: 18.0%; <0.5: 0.9%; Pearson r(luma_linear, ao) = -0.1564 (the retired extractor measured +0.4346); luma-quintile mean AO Q1-Q5 = 0.950/0.938/0.956/0.963/0.924 vs the old 0.813/0.731/0.695/0.769/0.990 — the albedo-ordering spread collapsed 0.30 → 0.04; remaining darkening is sparse and deep = crevice-shaped. User also confirmed the AO sliders respond in realtime (shader-side gates — no re-Process needed for strength).
files_changed: [Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs, Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOExtractionTests.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs]

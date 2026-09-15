---
status: diagnosed
trigger: "Phase 04.1 UAT gaps 1+2: roughness fit strength search selects wrong value (0.5 vs 0.3), fit-driven extraction black, residual not dropped, _RoughnessOffsetMap null binding, shaded preview glossy"
created: 2026-09-14T00:00:00Z
updated: 2026-09-14T01:10:00Z
---

## Current Focus

hypothesis: CONFIRMED (5 distinct root causes — see Resolution). All five failing tests + both user visual symptoms explained; no production decode/pack bug found; two test-fixture bugs, one numerically-unachievable acceptance criterion, one dead interactive-fit wiring path, one Unity asset-serialization reality in the offset test.
test: Static trace of extraction -> strength search -> pack bits -> decode -> preview, cross-checked against /tmp/uat-041-run2.log + /tmp/uat-041-results.xml artifacts and an offline numeric simulation (/tmp/namer_sim.py) mirroring fixture/blur/fitter/rasterizer math.
expecting: n/a (diagnosis complete; goal find_root_cause_only)
next_action: Report ROOT CAUSE FOUND to caller for plan-phase --gaps. Fixes (test data, preview evaluate wiring / cache-hit path, threshold/edge-bias design decision, persistent offset fixture) are NOT applied.

## Eliminated

- hypothesis: "Strength search implemented as a global argmin scan (production bug)"
  evidence: NamerRoughnessFitter.cs:82-104 is an ascending first-passing walk exactly per plan 02 Task 1/Task 5 ('MaxError <= maxErrorThreshold', first passing, tie-break deterministic). The observed 0.5 is the correct first-passing strength for the test's data at threshold 0.02 (0.3's error 0.05 > 0.02; 0.5's 0.01 <= 0.02). Fixture/threshold mismatch, not an argmin implementation.
  timestamp: 2026-09-14T00:20Z

- hypothesis: "WR-02 fit-cache change (edf3b40) returns a stale/wrong strength in the failing tests"
  evidence: FitDriven_SelectsFirstPassingStrength is pure-CPU and never touches the cache; tests 2/3/4 use fresh pipelines (no cache hits possible on first Process of a fresh mesh id). Cache only stores Passed results; cannot inject a failing strength.
  timestamp: 2026-09-14T00:20Z

- hypothesis: "D-05 wiring missing — refit consumes the original base instead of the cleaned base"
  evidence: NamerComputePipeline.cs:184-194 repoints NormalizedBaseColor at cleanedBase; NamerProcessor.cs:204-215 refits computeResult.NormalizedBaseColor (the cleaned base). Wiring is present and correct.
  timestamp: 2026-09-14T00:30Z

- hypothesis: "Pack/decode bit mismatch (surface alpha bits) causes the test failures or the glossy preview"
  evidence: NamerEncode.hlsl NamerPackAlphaBits (bits 0-5 roughness, /255) matches NamerSurface.hlsl NAMER_DECODE_SURFACE ((uint)(a*255+0.5) & 0x3F /63) and the PRD contract (A bits 0-5). Failures 2-4 fail BEFORE packing (residual gate); debug view reuses the same decode (NamerDebugView.shader includes NamerSurface.hlsl). No divergence found.
  timestamp: 2026-09-14T00:40Z

## Evidence

- timestamp: 2026-09-14T00:05Z
  checked: Packages/com.graffitientertainment.namer/Editor/Bake/NamerRoughnessFitter.cs (full read)
  found: "Fit walks StrengthLadder {0.0,0.15,0.3,0.5,0.7,0.9,1.0} ascending, returns FIRST strength with error <= maxErrorThreshold; no-pass -> (1.0, lastError, Passed=false). Exactly matches plan 04.1-02-PLAN.md Task 1 spec and Task 5 item 1 ('MaxError <= maxErrorThreshold')."
  implication: Production strength search is CORRECT per plan. Test fixture is inconsistent: ErrorThreshold=0.02 but dict error at 0.3 is 0.05 (>0.02, NOT 'below threshold from 0.3 onward' as the plan requires) — so the walk correctly skips 0.3 and first passes at 0.5 (0.01<=0.02). Predicted visited=[0.0,0.15,0.3,0.5] (4 visits) and Strength=0.5 Passed=true — exactly the observed failure.

- timestamp: 2026-09-14T00:06Z
  checked: NamerRoughnessPipeline.cs (full read) — ExtractFitDriven runs Fit (or cache), then RunFrequencySeparation(base, w, h, fit.Strength) returning {Roughness=remap(sharpDetail), CleanedBase=sharp-removal(strength)}. Fit cache keyed (meshId, estimator, w, h, maxErrorThreshold); only Passed cached (WR-02).
  found: Fit-driven extraction machinery exists and looks coherent.
  implication: Residual-collapse failures likely in the CONSUMER of CleanedBase (NamerComputePipeline.Process / NamerProcessor), not the extraction itself.

- timestamp: 2026-09-14T00:20Z
  checked: Plan 04.1-02-PLAN.md Task 5 item 1 (line 161) vs NamerRoughnessFitTests.cs:41-60
  found: Plan requires the fixture to return MaxError "ABOVE the threshold for strengths 0.0/0.15 and BELOW it from 0.3 onward". The test's dictionary (0.3->0.05, 0.7->0.03, 0.9->0.04, 1.0->0.06) is entirely ABOVE threshold 0.02 except 0.5->0.01. Under any <=-threshold semantics 0.3 cannot pass.
  implication: Failure 1 root cause = test fixture/threshold contradiction (test bug), not production. Fix direction: local threshold in (0.05, 0.20) for the pure-CPU block (e.g. 0.1) or rescale the dictionary so errors are <= 0.02 from 0.3 onward while keeping the U-shape minimum at 0.5.

- timestamp: 2026-09-14T00:30Z
  checked: NamerComputePipeline.cs (full read), NamerProcessor.cs (full read), NamerDecompPipeline.cs (full read), VertexColorFitter.cs (full read), NAMERRoughness.compute, NAMERPack.compute, NamerEncode.hlsl
  found: All C# D-05 wiring present. D-13 gate (NamerDecompPipeline.cs:232-237): ResidualRequired=false iff fitStats.MaxError <= threshold AND MinAlpha >= 0.999. Fit's evaluate returns the same GenerateResidual().Stats.MaxError the final refit recomputes — same inputs, deterministic kernels.
  implication: ResidualRequired=true in tests 2-4 is only possible if the fit FAILED at every ladder strength (Passed=false -> strength=1.0 used, MaxError(1.0) > 0.02). If any strength had passed, the final refit at that strength would measure the same MaxError <= threshold and drop the residual.

- timestamp: 2026-09-14T00:45Z
  checked: /tmp/uat-041-run2.log (import log) + /tmp/uat-041-results.xml
  found: For OneTextureTarget AND FitDrivenDefaultTarget: SourceMat_Residual.exr imported (residual was written). Both '[NAMER] Processed' lines carry NO warning suffix — no CR-01/CR-03 guard trips, no 'No mesh' skip. XML confirms the exact 5 failures + 7 passes (GPU Sobel 3/3, smoke 2/2, HonestGate, RoughnessOffset_Unset pass).
  implication: Decomposition ran the full fit-driven path in tests 3/4 and the residual was honestly required. Also: interactive editor lockfile present — no live re-run possible; diagnosis is static + artifacts + simulation.

- timestamp: 2026-09-14T00:55Z
  checked: Offline simulation /tmp/namer_sim.py (pure Python, mirrors fixture bytes, blur radius=8 sigma=8/3 clamped taps, per-triangle barycentric LSQ fit kGrid=16 with shared-corner averaging, byte quantization, UNorm8 rasterization, max-over-texels mean-channel MAE)
  found: MaxError by strength: 0.00->0.0549, 0.15->0.0487, 0.30->0.0425, 0.50->0.0342, 0.70->0.0259, 0.90->0.0182, 1.00->0.0207 (argmax at border columns, e.g. col 2; interior max 0.0196). The error curve hugs threshold 0.02 with ZERO headroom; real-pipeline quantization (float16 RTs, RGBA32 readback the fitter consumes, UNorm8 vcInterp) puts every strength above 0.02 in the observed run.
  implication: The phase's own 64x64 BakedResponse fixture cannot reliably collapse under threshold 0.02: at low strengths the un-removed gloss (amplitude 0.05) dominates; at high strengths the clamped-edge blur bias does (MinBlurRadius floor 8 = +-12.5% of a 64px texture; border columns deviate ~0.02-0.03 from any linear vertex-color fit). Acceptance criterion numerically unachievable as specified.

- timestamp: 2026-09-14T01:00Z
  checked: NamerEditorWindow.cs RecomputePreview (:299-384) + RunDecompPreview (:519-556); grep for RequestFit/HasCachedFit across window + tests
  found: Preview calls _pipeline.Process(inspection) with NO evaluate (:322). ExtractFitDriven (NamerRoughnessPipeline.cs:325-330) early-outs to an EMPTY result whenever evaluate==null — even with a cached fit. RequestFit/HasCachedFit have ZERO callers (3A interactive path is dead code). RunDecompPreview decomposes the UNCLEANED NormalizedBaseColor.
  implication: With the DEFAULT estimator (FitDriven), the window preview NEVER extracts roughness: _liveResult.ExtractedRoughness==null -> debug channel 9 binds the shader's "black" default (user's 'fit-drive is completely black'), shaded preview packs the SCALAR inspection.Roughness (user's 'shaded model looks normal, no roughness applied'). Genuine wiring gap, exactly matches the user report.

- timestamp: 2026-09-14T01:05Z
  checked: AssetGenerator.WriteMaterial (:549-622) + NamerOneTextureTests.cs:212-276 + generated Assets/NAMERGenerated/Neo-T-Pose/tripo_mat_d83278e6_Namer.mat
  found: WriteMaterial DOES bind inspection.RoughnessOffsetMap (:577-580). But the test's offsetTex is an in-memory (non-persistent) Texture2D; AssetDatabase.CreateAsset cannot serialize a material reference to a non-persistent object — the .mat stores m_Texture: {fileID: 0} (shape verified in the generated material), so the reloaded material reads null. NAMER.shader declares the property (line 13) — property presence is not the issue.
  implication: Failure 5 = Unity asset-serialization reality vs test fixture. Production works when the user assigns a project texture asset; the test must persist its offset texture (like CreateImportedBaseMap does) or production must copy non-asset user textures into the destination (design decision).

- timestamp: 2026-09-14T01:08Z
  checked: NamerSurface.hlsl decode vs NamerEncode.hlsl pack vs NamerDebugView.shader channel 9 vs CSRoughnessNormalize (Sobel normalization)
  found: Decode matches pack bit-for-bit; debug channel 9 shows the raw extracted RT while shaded shows the packed/lerped values — same source texture. Sobel roughness = saturate(mag/globalMax) (Blender-parity global-max normalization) — on typical imagery this is near-zero almost everywhere (rough only at strong edges) -> shaded reads "totally glossy" while the grayscale debug view "looks plausible"; the two views actually AGREE (dark = glossy).
  implication: No decode bug. The Sobel glossy appearance is per-design Blender parity; if unacceptable it is a design decision (e.g. adaptive normalization). Debug channel 3 (packed roughness decode) can confirm at human-verify time.

## Resolution

root_cause: FIVE distinct causes (no single bug):
  (1) FitDriven_SelectsFirstPassingStrength — TEST BUG: fixture errors are above the 0.02 threshold at every strength except 0.5, contradicting plan 04.1-02 Task 5 item 1 ("below it from 0.3 onward"); the production first-passing walk (NamerRoughnessFitter.Fit) correctly returns 0.5. Fix the test (local threshold in (0.05,0.20), e.g. 0.1; or rescale the dictionary).
  (2) FitDriven_BakedResponse_ResidualCollapses / FitDriven_DefaultOn_.../ OneTexture_ResidualDropped — NUMERICALLY UNACHIEVABLE ACCEPTANCE: the fit honestly fails at every ladder strength for the 64x64 BakedResponse fixture (post-refit MaxError ~0.019-0.055 across strengths vs threshold 0.02, dominated by clamped-edge blur bias (radius floor 8 at 64px) + un-removed gloss), so Passed=false, strength=1.0 is used, the cleaned base still exceeds threshold, ResidualRequired stays true, and the residual EXR is written (confirmed in run log). Production logic (1A/WR-02/D-05/D-13) all behaved as designed. Needs a design decision: fixture gloss amplitude vs threshold, blur min-radius at small textures / edge mode, or excluding border texels from the error metric.
  (3) User's "fit-drive is completely black + shaded normal" — PREVIEW WIRING GAP: NamerEditorWindow.RecomputePreview calls Process without an evaluate callback; ExtractFitDriven early-outs on evaluate==null even with a cached fit, and RequestFit/HasCachedFit (the intended 3A interactive path) have no callers. The default (FitDriven) preview therefore never extracts.
  (4) RoughnessOffset_Set_BindsMaterialTexture — TEST FIXTURE vs UNITY SERIALIZATION: AssetDatabase.CreateAsset drops material references to non-persistent Texture2D (m_Texture: {fileID: 0}); the test must use a persisted texture asset (or production must copy user textures into the destination — design decision).
  (5) User's "Sobel shaded totally glossy while debug view plausible" — NO BUG FOUND in pack/decode (bit-exact match); explained by the Sobel estimator's global-max normalization yielding near-zero roughness almost everywhere (Blender-parity behavior). The debug and shaded views agree numerically; perception differs.
fix: [not applied — goal find_root_cause_only]
verification: [static trace + existing run artifacts (/tmp/uat-041-results.xml, /tmp/uat-041-run2.log) + offline simulation /tmp/namer_sim.py; live re-run blocked by open editor lockfile]
files_changed: []


## Symptoms

expected: Per PRD and phase 04.1 plans: no authored roughness map -> baked response extracted as 6-bit roughness in surface alpha bits 0-5 (bit 6 emissive, bit 7 metallic; 64 values). Strength search must select FIRST-PASSING minimal strength (pure-CPU test pins 0.3), not global error argmin (0.5). With extraction active, D-13 residual must collapse — one-texture output: one RGBA8 surface PNG + vertex-colored mesh + written-but-unbound Base PNG, NO residual EXR. A set non-null roughness-offset inspection map must bind to material's _RoughnessOffsetMap (D-06). Shaded preview must show extracted roughness; "Extracted Roughness" debug view must show grayscale extracted roughness.
actual: Headless run (Unity 6000.0.82f1 batchmode, post compile-fix): 7/12 passed. Failures: (1) FitDriven_SelectsFirstPassingStrength — selected 0.5, expected 0.3, NamerRoughnessFitTests.cs:64 — pure-CPU deterministic. (2) FitDriven_BakedResponse_ResidualCollapses — residual not collapsed, :110. (3) FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual — residual still written, :178. (4) OneTexture_ResidualDropped_LeavesBaseResidualMapUnbound — residual EXR written, NamerOneTextureTests.cs:87. (5) RoughnessOffset_Set_BindsMaterialTexture — _RoughnessOffsetMap null, NamerOneTextureTests.cs:251. User visual: "Sobel extraction seems to possibly work, but the shaded model looks totally glossy. fit-drive is completely black and the shaded model looks normal, because no roughness is applied."
errors: Five NUnit failure messages (full XML: /tmp/uat-041-results.xml; Unity log: /tmp/uat-041-run2.log — 0 error CS, 0 shader errors).
reproduction: Re-run suites headless (editor closed, no lockfile): /Applications/Unity/Hub/Editor/6000.0.82f1/Unity.app/Contents/MacOS/Unity -batchmode -projectPath /Users/Shared/SSDevelopment/Development/Namer-Unity -runTests -testPlatform EditMode -testFilter "GraffitiEntertainment.Namer.Tests.NamerRoughnessExtractionTests;GraffitiEntertainment.Namer.Tests.NamerRoughnessFitTests;GraffitiEntertainment.Namer.Tests.NamerEditorWindowSmokeTests;GraffitiEntertainment.Namer.Tests.NamerOneTextureTests" -testResults /tmp/<slug>-results.xml -logFile /tmp/<slug>-run.log. Artifacts in /tmp. WARNING: interactive editor may be open — check Temp/UnityLockfile first; if locked, diagnose statically.
started: Discovered during Phase 04.1 UAT, 2026-09-14. Wave-3 code had NO compile evidence before compile-fix 2ada508. WR-02 fix (edf3b40 "key fit cache by error threshold and skip failed fits") touched fit path — recent suspect.

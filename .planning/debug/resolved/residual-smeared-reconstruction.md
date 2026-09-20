---
status: resolved
trigger: "04.2 residual-ON render on Neo looks 'everything smeared' vs the source — user reports it persists 'even at 2048x2048'"
created: 2026-09-17T00:00:00Z
updated: 2026-09-17T19:16:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: RESOLVED — root cause confirmed as hypothesis (a): the 17:13 Process run used a
MANUAL 128 residual-resolution popup (later reset to Auto). The adaptive ladder itself is
correct (marker run: Auto picks 2048 at both threshold 0.02 and 0.06; fit-only max 0.2546
forces the top rung).
test: DONE — pinned projection-path repro ran live via TestRunnerApi (marker file
Temp/namer-residual-reso-repro.txt): threshold=0.02 AND threshold=0.06 both logged
chosenResolution=2048, residualDims=2048x2048, avgError=0.000080, maxError=0.160523,
coverage=1.000000, residualRequired=True, cannotDecompose=False.
expecting: satisfied — EvaluateResolution is NOT broken; the on-disk 128 EXR was a manual
popup selection, not an adaptive-ladder descent.
next_action: COMPLETE — regression-proof test ran GREEN in the live editor
(ResidualPipelineTests.Residual_FullResolution_ReconstructsSourcePerCoveredTexel, passed=1
failed=0). Session resolved and archived. Temporary harness NamerDipSwitchReproTests.cs left
in place (deletion is the user's call).

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: With Write Residual ON, the NAMER render should reconstruct the source look: albedo = residual(uv) x vertexColor, residual = max(AO-unmultiplied source, VcFloor) / max(vcInterp, VcFloor) (source dividend, locked 2026-09-16), so vcInterp x residual == AO-unmultiplied source per covered texel up to quantization.
actual: "Everything smeared" — the render looks like the Gouraud projection alone (low-frequency), missing mid/high-frequency detail. User reports it persists "even at 2048x2048".
errors: None — purely visual. No Process errors or warnings at generation time.
reproduction: Process Neo (Assets/Models/Neo/Neo-T-Pose.fbx) with Decomposition ON, DipSource RemovedDetail, Write Residual ON; judge the A/B preview or the bound material. On-disk output: Assets/NAMERGenerated/Neo-T-Pose/ (EXR written 2026-09-17 17:13).
started: Reported during 04.2 verification on branch gsd/phase-04.2-gouraud-projection-one-texture-with-roughness-transfer-resid.

## Evidence
<!-- APPEND only -->

- timestamp: 2026-09-17
  finding: On-disk residual EXR is 128x128 for a 2048x2048 source (Base.png 2048, EXR dataWindow (0 0)-(127 127), mtime 17:13). No 2048 residual exists anywhere on disk — the "even at 2048" observation cannot have come from a Process run into this folder (popup set after processing, or preview judged without recompute).
- timestamp: 2026-09-17
  finding: In-editor measurement of the EXR content (blit to ARGBFloat + ReadPixels): texels=16384, nonWhite(>0.02)=8.3%, above1.1=3.8%, meanDev=0.0346, min=0.020, max=16.891. Content is a valid source-dividend quotient (HDR values survive — no clamping; detail present but only its blurred remnant, consistent with 16x downsampling of zero-mean grain).
- timestamp: 2026-09-17
  finding: Generated material binds the EXR correctly: _BaseResidualMap = tripo_mat_d83278e6_Namer_Residual (checked via RunCommand). EXR importer settings correct: sRGBTexture=0, uncompressed (editor), bilinear, no mips. WriteResidualExr path uses RGBAHalf readback — no 8-bit clamp in the write chain (AssetGenerator.cs:499-527, 433-461).
- timestamp: 2026-09-17
  finding: Code trace of the ladder: ChooseResolution (NamerDecompPipeline.cs:383-430) evaluates each ladder step via EvaluateResolution -> RunErrorHeatmap(vcInterp, up, baseLinear=SOURCE, ...) -> err = mean|vc x up - source| (NAMERDecomp.compute CSErrorHeatmap:209-216, uncovered texels excluded via vc.a < 0.5). Chosen=128 requires err <= threshold at 1024, 512, 256, AND 128. With measured removed-detail p90=0.173 this should be impossible at threshold 0.02 — hence the prefs-vs-bug fork.
- timestamp: 2026-09-17
  finding: LIVE EditorPrefs (com.unity3d.UnityEditor5.x.plist, mtime 17:40 — AFTER the 17:13 run) hold ErrorThreshold=0.06 (default is 0.02), ResidualResolution=0 (Auto), DecompositionEnabled=1, RoughnessExtractStrength=0.0778, AoUnmultiplyStrength=0.1613. The processor window's Process button (NamerEditorWindow.cs:134) calls NamerProcessor.Process(selection, new NamerProcessorSettings()), which reads these prefs, so the 17:13 run ran at threshold 0.06 + Auto — NOT the presumed 0.02, and NOT a manual resolution popup.
- timestamp: 2026-09-17
  finding: VcFloor = 1e-3 (Core/NamerConstants.cs:39). The on-disk 128 residual max=16.891 therefore comes from a COVERED texel with source/vcInterp≈17 (source≈1.0, vcInterp≈0.059), so the fit-only reconstruction error at that texel is ≈0.94. EvaluateResolution(128) measures |vc x residual_128_up - source| which collapses toward the fit-only error as the residual blurs, so it should be ≈0.94, ≫ 0.06. The ladder reaching 128 at threshold 0.06 is analytically impossible unless the metric is broken or a manual 128 was used.
- timestamp: 2026-09-17
  finding: The residual-ON popup mapping is confirmed: ResidualResolution index 5 -> ResolutionLadder[4] = 128 (NamerDecompIntegrationTests.Process_ReducedResolutionResidual_StampsBilinearImporter pins index 5 and asserts 128px). ChooseResolution's manual branch (manualResolution>0) returns ResolutionLadder[manualResolution-1] clamped to the source long edge.
- timestamp: 2026-09-17
  finding: The prior dip-switch harness (NamerDipSwitchReproTests.cs) had a compile error — AsyncGPUReadbackRequest used at line 106 without `using UnityEngine.Rendering;` — so the NAMER tests assembly would not build. Rewritten as the pinned residual-resolution repro (PinnedProjectionPath_ReportsChosenResolution_AtTwoThresholds).
- timestamp: 2026-09-17
  finding: OFFLINE fit-only measurement (ffmpeg+numpy, no Unity): decoded the source basecolor (neo-character_glb_basecolor.PNG, 2048) and the generated projected base (tripo_mat_d83278e6_Namer_Base.png, 2048 = the Gouraud vcInterp write-back) to linear RGB and compared. Fit-only error max = 0.211, p99 = 0.031, p90 = 0.0094, mean = 0.0041; the full-res quotient source/vcInterp spikes to 152.9 (p99 = 1.82). So the removed detail is mostly small (p90 ≈ 0.009) but has a heavy single-texel tail (a bright texel over a near-black vertex color, vc ≈ 1/153 ≈ 0.0065). At a 128 residual (≈identity after 16x blur) the reconstruction error at that texel is ≈0.99, and even at 1024 (2x blur) it is ≈0.75 — so EvaluateResolution(1024/512/256/128) must all read ≫ 0.06. A correct adaptive ladder at threshold 0.06 Auto therefore returns chosen = 2048, NOT 128. The on-disk 128 is inconsistent with a correct metric — leaving exactly two live hypotheses: a metric bug (EvaluateResolution reads ~0 for high-frequency removed detail) or a manual 128 popup at 17:13 later reset to Auto.
- timestamp: 2026-09-17
  finding: MARKER RUN (live editor, TestRunnerApi, passed=1 failed=0, Temp/namer-residual-reso-repro.txt): baseMap=2048x2048 mesh=tripo_node_d83278e6 vc=16729. threshold=0.02 fitOnlyMaxError=0.254639 chosenResolution=2048 residualDims=2048x2048 avgError=0.000080 maxError=0.160523 coverage=1.000000 residualRequired=True cannotDecompose=False. threshold=0.06 fitOnlyMaxError=0.254639 chosenResolution=2048 residualDims=2048x2048 avgError=0.000080 maxError=0.160523 coverage=1.000000 residualRequired=True cannotDecompose=False. VERDICT: hypothesis (a) confirmed — EvaluateResolution is NOT broken; Auto picks 2048 at both thresholds (fit-only max 0.2546 forces the top rung; the ladder never descends). The on-disk 128 EXR came from a MANUAL popup selection of 128 at the 17:13 run.
- timestamp: 2026-09-17
  finding: SIDE FINDING (marker run): the Neo FBX imports with EXTERNAL materials (no Material sub-assets; Inspect(fbxRoot) returns 0 materials). The repro loads Assets/Models/Neo/tripo_mat_d83278e6.mat directly and inspects that. The 17:13 run therefore selected the material (or a scene object wearing it), not the FBX root.
- timestamp: 2026-09-17
  finding: BELOW-VcFloor SINGLE TEXEL (marker run): maxError=0.1605 at the chosen 2048 rung (avgError 0.00008) is a single quotient-spike texel where vcInterp sits near VcFloor (the ~153x quotient measured offline). The symmetric VcFloor (max(source,VcFloor)/max(vcInterp,VcFloor)) breaks the exact identity at that texel BY DESIGN, so the E2E reconstruction identity test must use a per-texel oracle tolerance (or an explicit carve-out), NOT a flat byte tolerance.
- timestamp: 2026-09-17
  finding: E2E REGRESSION PROOF GREEN (live editor, TestRunnerApi, passed=1 failed=0): ResidualPipelineTests.Residual_FullResolution_ReconstructsSourcePerCoveredTexel passed. Editor + Tests.Editor dlls rebuilt via RequestScriptCompilation (symbol verified via strings grep). A full-res residual reconstructs the source per covered texel within the per-texel oracle tolerance — the smeared-low-res residual class of failure is now caught by a durable test.

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: The residual EXR is dead white (projection-dividend bug — CSResidual got the projected base instead of the source)
  evidence: 8.3% non-white, meanDev 0.0346, quotients to 16.9 — a white residual would show ~0% non-white. Dividend chain traced: projection.Run(baseColorOut, projectedOut) passes the normalize (SOURCE) output (NamerComputePipeline.cs:168, NamerProcessor.cs:576-594).
  timestamp: 2026-09-17
- hypothesis: 8-bit clamping in the EXR write/readback path zeroes quotients > 1
  evidence: max measured value 16.891 in the imported EXR; ReadBackResidual requests RGBAHalf (AssetGenerator.cs:504-505); importer uncompressed in-editor.
  timestamp: 2026-09-17
- hypothesis: Wrong texture bound at runtime/preview (base PNG or white default instead of the residual)
  evidence: Material _BaseResidualMap = the EXR (measured); preview binds _decompOutput.Residual when present (NamerEditorWindow.cs:390-393).
  timestamp: 2026-09-17
- hypothesis: The adaptive resolution metric is broken (EvaluateResolution reads ~0 for high-frequency removed detail, letting the ladder descend to 128)
  evidence: MARKER RUN — EvaluateResolution is correct: Auto picks chosenResolution=2048 at BOTH threshold 0.02 and 0.06 (fitOnlyMaxError=0.254639 forces the top rung; the ladder never descends to 1024/512/256/128). The on-disk 128 EXR is therefore not a metric failure.
  timestamp: 2026-09-17

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: The 17:13 residual-ON Process run used a MANUAL 128 residual-resolution popup
(later reset to Auto in EditorPrefs); the adaptive resolution ladder itself is correct
and would have chosen 2048.

fix: Surface the chosen residual resolution in NamerProcessor.Process via a Debug.Log when a
residual is actually written (mirroring the window preview's existing stat), and add a durable
E2E reconstruction-identity test (per-texel oracle tolerance, not a flat byte tolerance) that a
smeared low-res residual cannot pass.

verification: Root cause confirmed by the live marker run (Auto picks 2048 at 0.02 and 0.06;
fit-only max 0.2546). The log-line fix is code-reviewed against the AssetGenerator
writeResidual gate. The new E2E reconstruction-identity test passed GREEN in the live editor
(ResidualPipelineTests.Residual_FullResolution_ReconstructsSourcePerCoveredTexel, passed=1
failed=0), closing the regression proof.

files_changed:
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs: added a Debug.Log
    reporting Stats.ChosenResolution + residual dimensions after generator.Generate when the
    residual was written (after line 268).
  - Packages/com.graffitientertainment.namer/Tests/Editor/ResidualPipelineTests.cs: added
    Residual_FullResolution_ReconstructsSourcePerCoveredTexel (per-texel source-dividend oracle
    tolerance) + hash-noise grain fixture helpers + tolerance constants + `using
    GraffitiEntertainment.Namer.Core;`.

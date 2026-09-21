---
status: resolved
trigger: "turning off AO debug the after doesn't change, but in the game view it does and the AO looks wrong"
created: 2026-09-21T00:00:00Z
updated: 2026-09-21T20:29:05Z
---

## Current Focus

hypothesis: SETTLED; fix APPLIED and VERIFIED GREEN (user decision C: shader-side re-multiply, AO debug gate RIDES the re-multiply — gate-off neutralizes BOTH the ambient occlusion term and the albedo re-multiply; gate-on reconstructs the original albedo under any lighting).
test: NamerAOUnmultiplyRoundTripTests (5 tests) + full EditMode suite — 183/183 green in the live editor (Temp/ao-unmultiply-roundtrip-results.txt).
expecting: MET — gate-on round-trip render equality through the real ForwardLit pass, gate-off dual neutralization + packed-base-unchanged, pack-divide oracle, generator persistence, legacy-neutral default 0 all pass; all 178 existing tests unregressed.
next_action: RESOLVED 2026-09-21T20:29Z — atomic commit of the authored set (fix + test + .meta + this session file), message `fix(ao): ...`, never staging Assets/NAMERGenerated/, never pushing. Note for the user: legacy generated materials (including the shipped Neo output) keep rendering exactly as before (shader default 0); re-running the processor persists `_AoUnmultiplyStrength` on the .mat, after which the After-pane AO toggle visibly responds.

reasoning_checkpoint:
  hypothesis: "The pack stage divides the saved albedo by lerp(1, max(ao, floor), strength) (NAMERPack.compute CSNormalize:61) but the runtime applies AO only as surfaceData.occlusion (ambient-only, NamerSurface.hlsl:84/134) with the _DbgEnableAO gate (:108) moving ambient alone — so the divided albedo never round-trips and the After-pane toggle is invisible. Persisting the pack-time strength on the material and re-multiplying the GATED ao into the albedo at decode inverts the divide exactly."
  confirming_evidence:
    - "NAMERPack.compute:61 `_BaseColorOut = base / lerp(1.0, max(ao, _AoUnmultiplyFloor), _AoUnmultiplyStrength)` — the divide is baked into the saved base; no shader toggle can undo it today."
    - "NamerSurface.hlsl:108 gates ao before ONLY the ambient assignment (:134); albedo (:127) = baseResidual*_BaseColor*vertexColor with no AO term — verified by reading the full shader."
    - "inspection.AoUnmultiplyStrength is set from the same value the pack used (NamerProcessor.cs:133 / NamerEditorWindow.cs:383) and WriteMaterial receives that inspection — so one SetFloat persists the exact pack-time strength (AssetGenerator.cs:678 area)."
  falsification_test: "Render the real NAMER ForwardLit pass with a divided base + strength s + gate on: if the re-multiply does not exactly invert the divide, the render will NOT match the undivided strength-0 control (and the pack-side readback will NOT equal orig/lerp(1,ao,s)). Gate off: if the gate does not ride the re-multiply, gate-off renders will still differ by strength (G1 != G6) and equal the reconstructed original (G1 == G2')."
  fix_rationale: "Re-multiplying lerp(1, gated_ao, _AoUnmultiplyStrength) into the albedo at decode is the algebraic inverse of the pack-time divide executed with the same ao texel and strength — root-cause fix of the asymmetric pack/render contract, not a symptom patch. Default 0 keeps legacy materials byte-identical (same neutral-default pattern as the _DbgEnable* gates and the 'black' {} _RoughnessOffsetMap default)."
  blind_spots: "ao < AoFloor(0.1, synthetic path only) texels: pack divided by lerp(1, floor, s) but decode multiplies by lerp(1, ao, s) — bounded under-correction in ultra-occluded texels (min observed Neo ao 0.176 > 0.1, floor never binds there); the Meta/lightmap pass albedo is NOT re-multiplied (out of the decision's scope — noted as a known limitation); half-precision decode error ~1e-5 linear (covered by byte tolerances)."

## Symptoms

expected: Turning the "AO" debug toggle (Preview/Debug section, `_dbgAoEnabled`) off should visibly neutralize the AO contribution in the full shaded After preview; and with AO on, the NAMER material should render consistent with the source material's shading.
actual: After preview shows no visible change; Game view DOES change, and the AO there "looks wrong" (washed-out).
errors: none.
reproduction: NAMER Processor on Assets/Models/Neo (tripo_mat_d83278e6 — no authored _OcclusionMap, only MainTex + BumpMap), process to NAMERGenerated output, toggle "AO" in the Preview/Debug shaded-view toggle row; compare the After pane vs Game view.
started: noticed 2026-09-21 during review of the phase-04.2 PR output (post Codex round-2 fixes).
context: Symptoms prefilled from a completed live-editor investigation (main conversation, 2026-09-21). Measured on the generated output: surface B (AO) min=0.169 max=1.000 mean=0.801, 50.7% of texels <0.9, 13.3% <0.5; `max(baseLum*ao)=0.717` over 43241 samples (consistent with base = orig/ao); mat `_OcclusionStrength=1`. Surface importer sRGB=False (correct). NOTE these stats match the FIXED geometry-bake validation numbers (mean 0.876, 10.78% <0.5 — prior session ao-debug-view-no-change) more than the extraction numbers (mean 0.755, ~24% <0.49): the current surface.b likely comes from the cached BVH bake, NOT the luminance extraction. Confirm which path is live before blaming the extractor. Either way the round-trip failure mechanism is identical: un-multiply at pack, ambient-only occlusion at render. [SUPERSEDED 13:10Z: the extraction path is live and the current Base carries effectively no un-multiply — see Evidence.]

Prior sessions (all resolved, do not resume): ao-debug-view-no-change (BVH root-index bug — fixed), ao-recompute-affordance, ao-slider-overwrite-gate.

Project testing discipline: Unity Test Framework EditMode tests; run in the LIVE editor via unity-mcp RunCommand (TestRunnerApi + ICallbacks writing results to Temp/<slug>-results.txt, watch from Bash; NO AssetDatabase.Refresh before the test command; force-refresh with ImportAssetOptions.ForceUpdate to pick up external edits; verify compile via `strings Library/ScriptAssemblies/GraffitiEntertainment.Namer.Tests.Editor.dll | grep <symbol>`). Never judge renders via vision models — numeric readback only. Update 2026-09-21 (this session's verification run): the RunCommand harness hoists nested classes to namespace level, so `private` nested callback classes hit CS1527 — declare ICallbacks collectors as top-level `internal sealed class`; the ICallbacks member is `TestFinished`, not `TestResult`; `CompilationPipeline`/`AssetDatabase` inside the wrapper need `global::` qualification (Unity.CompilationPipeline namespace collision); ImportAsset with ForceUpdate alone does NOT trigger a recompile — `RequestScriptCompilation()` is required, and the editor DEFERS compilation until it gains focus (dll rebuild observed the moment the user focused Unity). GPU test files need BOTH `using UnityEngine.Rendering;` (GraphicsDeviceType) and `using UnityEngine.Experimental.Rendering;` (AsyncGPUReadback*) — every sibling GPU test carries both.

## Eliminated

- hypothesis: The After preview render is cached and ignores the toggle.
  evidence: DrawShaderInputToggle (NamerEditorWindow.cs:1091-1104) calls ApplyDebugGates() + Repaint(); ApplyDebugGates (509-543) writes `_DbgEnableAO` onto BOTH `_namerMaterial` and `_generatedMaterial`; the pane re-renders every OnGUI via NamerPreviewRenderer.Render → RenderPane (no render caching; only pane RTs persist and are re-blitted each pass).
  timestamp: 2026-09-21T00:00:00Z

- hypothesis: The gate never reaches the shader / material property is wrong.
  evidence: NamerSurface.hlsl:108 applies `ao = lerp(1.0, ao, _DbgEnableAO)` inside InitializeNamerSurfaceData (the runtime lit path the preview uses); the property is declared in the UnityPerMaterial CBUFFER (NamerSurface.hlsl:31) and defaults 1.0 (pinned by NamerDipSwitchTests.ShaderDebugGates_DefaultNeutral). Game view visibly responds to the toggle — the value demonstrably reaches the shader.
  timestamp: 2026-09-21T00:00:00Z

- hypothesis: The packed AO data is corrupted / wrong channel / sRGB import.
  evidence: Surface PNG importer sRGB=False (verified live); surface.b carries plausible AO structure (see context stats); decode `ao = surface.b * _OcclusionStrength` (NamerSurface.hlsl:84).
  timestamp: 2026-09-21T00:00:00Z

## Evidence

- timestamp: 2026-09-21T00:00:00Z
  checked: Source material Assets/Models/Neo/tripo_mat_d83278e6.mat
  found: `_OcclusionMap: {fileID: 0}` — no authored AO. Only _MainTex + _BumpMap bound. `_OcclusionStrength: 1`.
  implication: D-07 three-way gate (NamerComputePipeline.cs:144-162) uses synthetic AO: cached geometry bake if present, else luminance extraction.

- timestamp: 2026-09-21T00:00:00Z
  checked: NamerComputePipeline.cs:144-162 (D-07 gate), :358-359 (`_AoUnmultiplyStrength`, `_AoUnmultiplyFloor = usesSyntheticAo ? AoFloor : Epsilon`), NAMERPack.compute CSNormalize:58-61 (un-multiply divide), :90/112 (AO packed via _AoIn.g into octahedral .b)
  found: Un-multiply runs at pack time for ALL paths with `base / lerp(1, max(ao, floor), strength)`; the synthetic floor (NamerConstants.AoFloor) is the only depth cap on the divide. Neo measured min surface.b = 0.169 → up to ~5.9x brightening of the saved albedo.
  implication: The un-multiply is baked into the Base PNG; no shader-side toggle can undo it exactly.

- timestamp: 2026-09-21T00:00:00Z
  checked: URP occlusion semantics (UniversalFragmentPBR) + NamerSurface.hlsl:84,108,134; NamerPreviewRenderer.cs:110-116 (ambient 0.1, two directional lights)
  found: `surfaceData.occlusion` scales baked GI/ambient and specular GI only — direct punctual lights are unaffected. Preview ambient = fixed 0.1 gray → the gate moves the preview by ~1-2%; Game view ambient is real → visible.
  implication: Both reported observations (After no-change, Game-view change + wrong look) follow from ambient-only occlusion vs a pack-time albedo divide.

- timestamp: 2026-09-21T00:00:00Z
  checked: Generated output numerics via live-editor RunCommand (readable copies via Texture2D.LoadImage)
  found: Surface B: min=0.169 max=1.000 mean=0.801, 50.7% <0.9, 13.3% <0.5. Base*AO product never exceeds 0.717 (consistent with base = orig/ao).
  implication: Substantial synthetic AO is packed; half the character is "occluded" by whatever path produced it.

- timestamp: 2026-09-21T00:00:00Z
  checked: Existing tests (NamerDipSwitchTests.cs, NamerAOBakeTests.cs, NamerRoughnessExtractionTests.cs)
  found: NamerDipSwitchTests pins gate defaults/persistence only — nothing pins the AO gate's visual/render effect or the un-multiply round-trip. Full suite 178/178 green as of the last run (2026-09-21).
  implication: A test defining the intended round-trip contract must be written before the fix (happy + unhappy path).

- timestamp: 2026-09-21T13:10:00Z
  checked: NamerAOPipeline.cs bake-cache persistence (_bakeCache, StoreBake, RequestBake, MakeKey) + repo-wide search for bake artifacts
  found: The bake cache is IN-MEMORY ONLY — `Dictionary<(int low, int occ, int w, int h), Texture2D>` (NamerAOPipeline.cs:47-48); StoreBake only writes that dictionary; nothing in Editor code writes a bake to disk (only EditorPrefs settings). There is NO bake cache artifact on disk to inspect — the operational assumption that one exists is falsified.
  implication: The "which D-07 path" question must be settled from code-path reachability, not from a disk artifact.

- timestamp: 2026-09-21T13:10:00Z
  checked: Reachability of the cached-bake branch. NamerComputePipeline.cs:151-161 gate (requires HasCachedBake true BEFORE BakeAndUpload), RequestBake call sites package-wide, git log -S RequestBake
  found: Cache entries are seeded only inside BakeAndUpload's fresh-bake branch, reachable in production ONLY via RequestBake — and RequestBake's sole production caller, `TriggerAutomaticBake` in NamerEditorWindow, was DELETED in 307b305 "fix(03.1): remove silent auto-bake trigger" (2026-08-29, ancestor of HEAD). Tests are the only remaining callers.
  implication: Since Aug 29 the bake branch is unreachable in production: the D-07 gate ALWAYS falls through to luminance extraction when no authored _OcclusionMap exists.

- timestamp: 2026-09-21T13:10:00Z
  checked: NamerProcessor.cs Process flow (pipeline construction, BakeSourceMesh assignment at :145, gate inputs) and NamerEditorWindow.cs:396 preview assignment
  found: `NamerProcessor.Process` constructs a FRESH `NamerComputePipeline()` per run (:128) — its `_aoPipeline._bakeCache` starts empty, so `HasCachedBake` is necessarily false even though `inspection.BakeSourceMesh = decomposeSourceMesh` IS set when DecompositionEnabled (prefs: 1). The window preview path keeps `_pipeline` long-lived but has no cache-seeding caller either.
  implication: The current output (written 2026-09-21 12:39 by Process with NAMER) took the EXTRACT branch. Confirms the prior-session note "bake runs on first recompute and is cached" is STALE — it described pre-307b305 behavior.

- timestamp: 2026-09-21T13:10:00Z
  checked: Unity EditorPrefs (defaults read com.Unity3D.UnityEditor5.x) for NamerProcessor.* keys
  found: AoStrength=0.5046, AoContrast=1.176, AoBlurRadius=6.99, AoUnmultiplyStrength=0.1613, DecompositionEnabled=1, OverwriteGenerated=1; AoStageEnabled key ABSENT (never written) -> default true. NamerAoRemap (NAMERAO.hlsl:28-33) is `ao = saturate(lerp(1, ao, strength))` then contrast.
  implication: The user's sliders are non-default. Strength 0.5046 pulls the extraction toward white, explaining why current stats (mean 0.801, 13.3% <0.5) sit ABOVE the prior default-settings extraction numbers (0.755, ~24% <0.49) — the "between bake and extraction" observation was a slider-settings artifact, NOT evidence of the bake path.

- timestamp: 2026-09-21T13:10:00Z
  checked: Disk forensics on the generated output (sips->BMP->numpy, per texture-forensics memory): Surface.png .b channel stats; Base.png vs SOURCE neo-character_glb_basecolor.PNG regression for the baked un-multiply strength
  found: Surface.b: min=0.176 max=1.000 mean=0.799, 12.8% <0.5, 51.2% <0.9 — matches the live measurement (0.169/1.000/0.801, 13.3%, 50.7%), confirming the live stats came from this exact file. Base-vs-source: best-fit un-multiply strength s=0.0090 (ao<0.95 subset: 0.0084); s=0 fits better than s=0.1613 (median err 0.0020 vs 0.0022) and far better than s=1 (0.0043); median |base-orig| = 0.002 (pure Gouraud-projection noise, p99 0.052). max(baseLum*ao)=0.735 reproduces the live 0.717 — but the source's own max linear luminance is ~0.76, so that product cap carries no un-multiply information.
  implication: The current Base PNG effectively does NOT carry the pack-time un-multiply (at most the weak 0.1613, which brightens <=1.15x). The Evidence claim "up to ~5.9x brightening of the saved albedo" described the default-strength (1.0) mechanism, not this output. For the CURRENT output, the Game-view "AO looks wrong" is dominated by the albedo-derived extraction AO darkening ambient in dark-painted regions, not by a baked brightening.

- timestamp: 2026-09-21T13:10:00Z
  checked: Fix-scope anchors for the decision options — NamerEditorConstants.cs:35 (DefaultAoUnmultiplyStrength=1f), Core/NamerConstants.cs:36 (AoFloor=0.1f), NAMERPack.compute:58-61 (divide), NamerSurface.hlsl:26/31 (properties), :84 (ao decode), :108 (gate), :127 (albedo = baseResidual*_BaseColor*vertexColor), AssetGenerator.cs:678 (SetFloat _OcclusionStrength), generated mat (_OcclusionStrength=1, _DbgEnableAO=1)
  found: All three candidate fixes have surgical insertion points; (c) additionally needs the pack-time strength persisted on the material to be exact.
  implication: Options ready to present; decision checkpoint returned to user.

- timestamp: 2026-09-21T14:40:00Z
  checked: Headless-with-graphics full-suite run per plan (Unity 6000.0.82f1 `-batchmode -projectPath ... -runTests -testPlatform EditMode -testResults /tmp/ao-unmultiply-roundtrip.xml -logFile /tmp/ao-unmultiply-roundtrip.log`), after snapshotting ProjectSettings.asset to /tmp/ps-before.asset
  found: FATAL — "It looks like another Unity instance is running with this project open. / Multiple Unity instances cannot open the same project." Unity exited 134 (Abort trap: 6); live editor PID 54091 holds Temp/UnityLockfile. Zero tests executed. ProjectSettings.asset restored from the snapshot (diff-verified identical); per the 134 protocol: no retry, no commit.
  implication: Verification must happen in the LIVE editor via the unity-mcp TestRunnerApi recipe (steps enumerated in Current Focus next_action); batch fallback is unavailable for this project while the editor is open.

- timestamp: 2026-09-21T14:05:00Z
  checked: Fix-scope completion sweep for contract (c) — all NAMER-material construction sites (grep _OcclusionStrength): AssetGenerator.WriteMaterial:678 (disk .mat), NamerEditorWindow RecomputePreview:449 (in-memory After-pane preview material `_namerMaterial`), NamerSmokeSetup:137 (AO=1 white — neutral under any strength); NamerComputePipeline.cs:144-168 D-07 gate re-read confirming an AUTHORED OcclusionMap is raw-copied with no remap (deterministic test fixture); NamerMaterialInspection field defaults (OcclusionStrength/AoStrength need explicit setting in direct construction).
  found: The After pane (the reported symptom) renders the IN-MEMORY `_namerMaterial` (NamerEditorWindow.cs:995-997, built at :427-449), not the written .mat — so the fix needs the SetFloat at BOTH AssetGenerator.WriteMaterial (persisted output) AND RecomputePreview (live preview), plus the ShaderLab property/CBUFFER. inspection.AoUnmultiplyStrength is set from the pack-time slider in both paths (:383 / NamerProcessor.cs:133) before pipeline.Process, so both writers can persist the exact pack value.
  implication: Fix = 4 files: NamerSurface.hlsl (CBUFFER member + gated re-multiply at the albedo), NAMER.shader (property, default 0), AssetGenerator.cs:678+1 (SetFloat), NamerEditorWindow.cs:68+1 (ID constant) and :449+1 (SetFloat). Shader-side default 0 rationale: legacy materials generated before this fix (including the shipped Neo output) never persisted the value — 0 is the neutral of lerp(1, ao, s), matching the established neutral-default pattern (_DbgEnable* = 1.0, _RoughnessOffsetMap "black" {}); NamerSmokeSetup's AO=1 material is neutral under any strength.

- timestamp: 2026-09-21T20:29:05Z
  checked: Live-editor full EditMode suite via the unity-mcp TestRunnerApi recipe (results written to Temp/ao-unmultiply-roundtrip-results.txt); fix diff re-verified in-source before running
  found: RESULT passed=183 failed=0 skipped=0 total=183 — exactly the expected 178 existing + 5 new NamerAOUnmultiplyRoundTripTests, zero failures, zero skips. In-source anchors confirmed post-compile: the gate lerp at NamerSurface.hlsl:111 precedes the re-multiply at :137; NAMER.shader:25 defaults `_AoUnmultiplyStrength` to 0.0; AssetGenerator.cs:682 persists it. The test file as authored had 5 compile errors (CS0103/CS0246): missing `using UnityEngine.Rendering;` (GraphicsDeviceType) and `using UnityEngine.Experimental.Rendering;` (AsyncGPUReadback/AsyncGPUReadbackRequest) — every sibling GPU test carries both; fixed in main context by adding those two using lines after `using UnityEngine;` (sibling ordering), no other changes to the file.
  implication: Contract C verified end-to-end through the real render path; suite green; atomic commit unblocked.

## Resolution

root_cause: Two-part. MECHANISM (verified in code, fires at default settings): the pack stage divides the base albedo by the synthetic AO (`base / lerp(1, max(ao, floor), strength)`, NAMERPack.compute CSNormalize with `_AoUnmultiplyFloor = AoFloor(0.1)` for synthetic AO), but the runtime re-applies AO only as `surfaceData.occlusion` (NamerSurface.hlsl:84,134) which scales INDIRECT/ambient light only — so the divided albedo renders brighter than source under direct light, and the `_DbgEnableAO` gate (:108) moves only the ambient term (fixed 0.1 gray in the After preview) making the toggle invisible there while visible in Game view. CURRENT-STATE CORRECTION (disk-verified 2026-09-21T13:10Z): the shipped Neo output took the luminance-EXTRACTION D-07 path (bake branch unreachable: in-memory-only cache, RequestBake has no production caller since 307b305, fresh pipeline per Process run) and its Base PNG carries effectively NO un-multiply (best-fit s=0.009; user pref 0.1613 caps brightening at 1.15x) — the current "AO looks wrong" in Game view is dominated by the albedo-derived extraction AO itself (dark-painted regions ambient-darkened; mean 0.799, 12.8% <0.5), not by a baked brightening.
fix: APPLIED 2026-09-21T14:20Z (user decision C: shader-side re-multiply, gate rides the re-multiply). (1) NamerSurface.hlsl — `half _AoUnmultiplyStrength;` added to the UnityPerMaterial CBUFFER (after `_OcclusionStrength`); the `_DbgEnableAO` gate at :~108 is applied FIRST (`ao = lerp(1.0, ao, _DbgEnableAO)`, comment updated to state both consumers ride it); before the albedo assignment at :~127 a gated re-multiply `half aoUnmultiply = lerp(1.0, ao, _AoUnmultiplyStrength);` is folded into `surfaceData.albedo = baseResidual.rgb * _BaseColor.rgb * vertexColor.rgb * aoUnmultiply;`; `surfaceData.occlusion = ao;` left as-is. (2) NAMER.shader — property `_AoUnmultiplyStrength("AO Un-multiply Strength", Range(0.0, 1.0)) = 0.0` with contract comment (default 0 = neutral of lerp(1, ao, s), legacy materials byte-identical). (3) AssetGenerator.cs WriteMaterial (~:679, beside `_OcclusionStrength`) — `material.SetFloat("_AoUnmultiplyStrength", inspection.AoUnmultiplyStrength);` persists the pack-time strength on the generated .mat. (4) NamerEditorWindow.cs — `AoUnmultiplyStrengthId` constant (~:69) + `SetFloat` in RecomputePreview (~:451) so the After-pane in-memory `_namerMaterial` matches the generated .mat. Pinning test written FIRST: Tests/Editor/NamerAOUnmultiplyRoundTripTests.cs (5 tests — legacy-neutral default 0; pack-divide oracle with authored raw-copied AO; gate-on round-trip render equality through the REAL ForwardLit pass via NamerPreviewRenderer with a no-re-multiply brightness control; gate-off neutralizes BOTH terms (strength-irrelevance + packed-base-unchanged brightness vs original); generator persistence of the strength on the .mat reloaded from disk).
verification: GREEN 2026-09-21T20:29Z — live-editor EditMode suite via unity-mcp TestRunnerApi: passed=183 failed=0 skipped=0 total=183 (Temp/ao-unmultiply-roundtrip-results.txt), exactly 178 existing + 5 new; zero skips (GPU tests executed). Test file compile-fixed in main context (two using lines, see Evidence 20:29Z entry). Committed atomically with this session file (message `fix(ao): ...`); Assets/NAMERGenerated/ dirty files left unstaged; not pushed.
files_changed: [Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl, Packages/com.graffitientertainment.namer/Shaders/NAMER.shader, Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs, Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOUnmultiplyRoundTripTests.cs (+ its .meta)]

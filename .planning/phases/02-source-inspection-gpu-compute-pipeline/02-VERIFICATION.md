---
phase: 02-source-inspection-gpu-compute-pipeline
verified: 2026-08-27T18:40:53Z
status: verified
human_verification_resolved: 2026-08-27T19:28:12Z
score: 20/20 must-haves verified (4/4 roadmap success criteria + 16/16 plan truths)
overrides_applied: 0
human_verification:
  - test: "In a live editor, select a material / GameObject / folder and run Tools > NAMER > Inspect Selection"
    expected: "Console shows the [NAMER] report: selection type, unique-material count, per-material maps (with sRGB/linear flags), scalars, roughness, and warnings"
    why_human: "The menu path (Selection.activeObject -> Inspect -> LogReport, SourceInspector.cs:22-34) is the D-01 manual-validation surface; all 12 automated tests call SourceInspector.Inspect directly and never exercise the menu wrapper or its console output"
  - test: "Select a real imported FBX/model asset and run Inspect Selection (or Inspect via script)"
    expected: "The model's sub-asset materials are found and inspected (one unit per unique material)"
    why_human: "The AddSubAssetMaterials path (SourceInspector.cs:178-188, reached via the PrefabAssetType.Model fall-through at :99-101 or the non-GameObject asset branch at :136-141) has no automated fixture — a test would require committing an imported model asset. The other four selection kinds (material, scene GameObject, prefab, folder) are test-covered"
---

# Phase 2: Source Inspection + GPU Compute Pipeline Verification Report

**Phase Goal:** Read URP Lit/Standard source materials and produce the normalized base color and packed surface textures entirely in GPU compute, verified against the CPU reference
**Verified:** 2026-08-27T18:40:53Z
**Status:** verified — 20/20 automated must-haves + 2/2 human-verification items passed via UAT (see below)

## Human Verification Resolution (2026-08-27)

Both `human_verification` items were executed and passed in the live editor by the user, recorded in `02-HUMAN-UAT.md` (status: complete, commit `76e5781`):

1. **Inspect Selection menu** — user ran `Tools > NAMER > Inspect Selection` in the live editor; the `[NAMER]` console report rendered. Result: pass.
2. **FBX/model sub-asset selection** — user inspected `Assets/Models/Neo-T-Pose.fbx`; embedded sub-asset materials resolved through `AddSubAssetMaterials`. Result: pass.
**Re-verification:** No — initial verification
**Mode note:** Phase is `mode: mvp`; the ROADMAP goal is not User Story format (same condition as Phase 1, recorded there as a process note). All three PLANs carry one identical validating User Story, used for User Flow Coverage below. Optional cleanup: `/gsd mvp-phase 2`.

## Verification Basis (independently checked, not taken from SUMMARYs)

- Every artifact file read in full at the working tree (= HEAD; `git status` shows no modified package sources).
- HLSL mirror math compared term-for-term against `Core/NamerFormat.cs` and `Core/NamerConstants.cs` (see Key Links).
- Executed test evidence re-parsed from `/tmp/namer-unity-testrun/test-results.xml` (2026-08-27T18:34:55Z, EditMode, Unity clone): **total=46 passed=46 failed=0 skipped=0**; per-case `result="Passed"` confirmed for all 4 GPU tests (`CSOctahedralEncode_KernelMatchesCore`, `CSSurfacePack_KernelMatchesCore`, `CSNormalize_KernelMatchesReference`, `FullPipeline_ProducesNormalizedAndPackedTextures_OnMetal`) and for the review-fix tests (`DataMapSrgbFlags_...`, `FolderResolution_FindsMaterialsInsidePrefabs`) — proving the run includes the final WR-04 root-cause fix content. skipped=0 confirms the D-15 capability gate passed on Metal (no silent skips).
- All 12 phase commits verified present: `cbf5eaf`, `b5a94d2`, `4a05349`, `5c31814`, `92e0837`, `3b7c53a`, `70688f8`, `d2ad4b7`, `a3b5829`, `82431b3`, `59de1f8`, `f50c1d2` (+ docs `b7b66a0`, `f49f100`).
- PlayMode regression 3/3 (phase-1 runtime tests) per orchestrator-provided evidence; static verification only was permitted here.

## User Flow Coverage (MVP mode)

User story (identical in all three PLANs): "As a technical artist using Unity, I want to select any supported source (GameObject, prefab, FBX/model, material, or folder) and have NAMER read its PBR maps and produce normalized base-color and packed surface textures entirely on the GPU, so that the output is proven equivalent to the CPU reference before asset generation."

| Step | Expected | Evidence | Status |
|------|----------|----------|--------|
| Select any supported source | All five selection kinds resolve to deduplicated unique materials | SourceInspector.cs:66-144 (Material / scene GO / prefab contents / model sub-assets / folder incl. t:Prefab recursion); dedupe by instance ID :190-202; 11 executed tests | ✓ (FBX path code-verified only — see human item 2) |
| Read PBR maps + fallbacks | URP Lit/Standard property tables, scalars, neutral defaults, warnings never failure | SourceInspector.cs:247-325 (tables), :218-227 (D-07 defaults), :329-364 (roughness/emissive derivation); NeutralDefaults + UnknownShader tests passed | ✓ |
| Produce normalized + packed textures entirely on GPU | No per-pixel C#; staged kernels; correct formats | NAMERPack.compute:45-103 (3 kernels, bounds-guarded); NamerComputePipeline.cs:198-206 (Dispatch (w+7)/8), :76-116 (lease/upload/dispatch); zero pixel loops in Editor/Pipeline (grep clean); smoke test asserts R8G8B8A8_UNorm + R16G16B16A16_SFloat — passed | ✓ |
| Outcome: proven equivalent to CPU reference | GPU-vs-CPU golden tests at D-14 tolerances, executed on Metal | GpuGoldenTests.cs:91-97 (oct 1/255 + dot ≥ 1-1e-3), :156-160 (alpha byte EXACT), :243-281 (normalize paths); ComputeSmokeTests.cs:123-150 (end-to-end oracle assertions); all 4 passed, 0 skipped | ✓ |

## Goal Achievement

### Observable Truths

Roadmap success criteria are the contract; plan truths fold in without reducing scope.

| # | Truth | Source | Status | Evidence |
|---|-------|--------|--------|----------|
| 1 | User selects any supported source (GameObject, prefab, FBX/model, material, folder) and the processor locates all present PBR maps and scalar fallbacks | SC-1 | ✓ VERIFIED | SourceInspector.cs:66-144 selection resolution; :247-299 URP Lit (`_BaseMap`/`_BumpMap`/`_MetallicGlossMap`/`_OcclusionMap`/`_EmissionMap` + `_SmoothnessTextureChannel`), :271-299 Standard (`_MainTex`/`_Glossiness`), :301-325 generic best-effort. Executed: UrpLit/Standard/Dedupe/Folder(prefab)/PrefabAsset tests all Passed. FBX sub-asset path (:178-188) code-verified, no fixture — human item 2 |
| 2 | Missing maps and scalar properties fall back to sensible defaults without failing the pipeline | SC-2 | ✓ VERIFIED | D-07 defaults SourceInspector.cs:218-227 (metallic 0, smoothness 0.5→roughness 0.5, AO 1.0 via white-fill downstream, emission black, normal neutral via NamerComputePipeline.cs:28,103); never throws on missing data (:41-60 doc + guarded reads :381-420); bare-material warning :366-374. Executed: NeutralDefaults_BareUrpLitMaterialFallsBack, UnknownShader_ReturnsWarningWithoutThrowing — Passed |
| 3 | Cleaned base color and packed surface produced via GPU compute (no per-pixel C#), baked lighting removed where feasible | SC-3 | ✓ VERIFIED | CSNormalize (NAMERPack.compute:46-75): sRGB→linear only when authored sRGB (:54), AO un-multiply `base / lerp(1, max(ao, ε), strength)` (:56, mirrors Blender DIVIDE node per 02-RESEARCH — D-08/D-09 conservative cleaning; full delighting is a documented deferred idea). No per-pixel C# anywhere in Editor/Pipeline (verified by read + grep). Executed end-to-end on Metal: FullPipeline smoke — Passed, incl. W2 normalize-vs-CPU-oracle assertions (ComputeSmokeTests.cs:135-150) |
| 4 | GPU kernels match the CPU reference across supported compute backends (round-trip verified) | SC-4 | ✓ VERIFIED | Alpha byte EXACT (GpuGoldenTests.cs:156-157), oct R/G + AO within 1/255 (:91-92, :158-160), decoded-normal dot ≥ 1-1e-3 (:96-97); smoke pins full-pipeline bytes vs NamerFormat oracle (:123-133). All 4 GPU tests Passed on Metal, 0 skipped. Other backends capability-gated with explicit skip report per D-15 (:55-59, ComputeSmokeTests.cs:27-31) — the phase's declared backend scope (D-15: Metal verified; D3D/Vulkan CI matrix is v2) |
| 5 | URP Lit maps read via real property names; AO in G; metallic R / smoothness A | 02-01 | ✓ VERIFIED | SourceInspector.cs:249-268; kernel consumers NAMERPack.compute:55 (`_AoIn[id.xy].g`), :62-66 (metallic `.r`, smoothness `.a` or base alpha per channel); golden channel-0/channel-1 tests passed (:260-281) |
| 6 | Smoothness source read from _SmoothnessTextureChannel (0 = metallic-map alpha, 1 = base-map alpha), roughness = 1 − smoothness | 02-01/D-05 | ✓ VERIFIED | SourceInspector.cs:259 read; NamerSourceModel.cs:63 Roughness = 1 − Smoothness; kernel :64-66 computes both paths before quantization; CSNormalize channel-1 golden case passed (roughness 0.2 from base alpha 0.8, GpuGoldenTests.cs:276-281) |
| 7 | Missing maps → neutral defaults, never failing (INSP-04) | 02-01 | ✓ VERIFIED | Same as SC-2; additionally the pipeline uploads neutral fills when maps are null (NamerComputePipeline.cs:28-29, 102-105) |
| 8 | Source assets never mutated during inspection | 02-01 | ✓ VERIFIED | No SetDirty/SaveAssets/importer-write anywhere in SourceInspector (read-only: HasProperty/GetTexture/GetFloat/GetColor; importer.sRGBTexture only read at :399-402); prefab contents loaded/unloaded in try/finally (:159-176); executed test SourceImmutability_InspectionDoesNotChangeTextureFlags — Passed |
| 9 | Kernels mirror Core NamerFormat line-for-line via shared NamerEncode.hlsl (D-10) | 02-02 | ✓ VERIFIED | NamerEncode.hlsl:28-32 ≡ NamerFormat.cs:20-24 (identical `max(sum, ε)` guard and `0.5 + 0.5·v/sum`); NamerEncode.hlsl:39-45 ≡ NamerFormat.cs:53-59 (strict `>` thresholds 0.5/0.1, `clamp((int)floor(r·63), 0, 63)`); constants 0x80/0x40/0x3F/63.0/255.0/1e-6 in literal sync with NamerConstants; compute includes it via relative `#include "NamerEncode.hlsl"` (NAMERPack.compute:15) |
| 10 | Roughness = 1 − smoothness before quantization; linear floor(r·63) exactly as Core | 02-02 | ✓ VERIFIED | Conversion in CSNormalize (NAMERPack.compute:66,71) → quantization only in NamerPackAlphaBits (NamerEncode.hlsl:43) — order enforced by kernel staging; golden alpha-byte-EXACT assertions passed across roughness 0/0.5/1.0 vectors |
| 11 | Only base color is sRGB-converted; normal/AO/metallic/roughness never | 02-02/Pitfall 1 | ✓ VERIFIED | NAMERPack.compute:54 (single gated SRGBToLinear on base only); header :9-10 contract; smoke scenario 2 (linear base, must NOT decode) and scenario 4 (sRGB-flagged data maps arrive raw) both passed (ComputeSmokeTests.cs:43, 48-53, 118-133) |
| 12 | AO un-multiply = albedo / lerp(1.0, max(AO, ε), strength), AO from green channel | 02-02/D-08 | ✓ VERIFIED | NAMERPack.compute:55-56 (exact formula, ε = 1e-6 from NAMER_EPSILON); strength exposed via inspection.AoUnmultiplyStrength (NamerSourceModel.cs:64, default 1.0); W2 smoke assertion uses the same CPU formula (ComputeSmokeTests.cs:146) — passed |
| 13 | Final packed R8G8B8A8_UNorm linear; intermediates R16G16B16A16_SFloat; never sRGB | 02-02/D-11 | ✓ VERIFIED | NamerComputePipeline.cs:79-80 descriptors, :218-227 (`sRGB = false`, `enableRandomWrite`); executed assertions on result formats (ComputeSmokeTests.cs:106-107) — Passed |
| 14 | Per-pixel processing via ComputeShader.Dispatch, not C# loops | 02-02/NORM-03 | ✓ VERIFIED | NamerComputePipeline.cs:198-206 (3 dispatches, (w+7)/8 ceiling); no GetPixels/SetPixels in Editor/Pipeline (grep clean; test helpers only) |
| 15 | GPU alpha byte EXACT; oct/AO within 1/255; decoded dot ≥ 1−1e-3 (D-14) | 02-03 | ✓ VERIFIED | Assertions in code at GpuGoldenTests.cs:91-97, 156-160 and executed green. Decode-dot oracle = `OctahedralDecode(OctahedralEncode(texel))` (documented deviation, GpuGoldenTests.cs:94) — for the four neutral vectors this IS the source tangent normal (0,0,1); vector D (1,0,0) is a non-tangent texel where the self-consistent oracle is the valid reading of D-14's "agreement" |
| 16 | Tests skip with an explicit report entry when compute/async-readback unavailable — never a silent pass (D-15) | 02-03 | ✓ VERIFIED | Assert.Ignore with device-type message at GpuGoldenTests.cs:57, 118, 181 and ComputeSmokeTests.cs:29-31; executed run had skipped=0 (gate passed on Metal) |
| 17 | Full pipeline run completes on Metal and live RT count returns to baseline (D-13) | 02-03 | ✓ VERIFIED | Watchdog asserts BEFORE Dispose and after each Process+ReleaseResult (ComputeSmokeTests.cs:57-58, 159-160 — the WR-02 fix; the old post-Dispose 0==0 tautology is gone); ReleaseResult clears leased targets (:154-158, asserting null after); executed Passed |
| 18 | Kernels staged (normalize → octahedral → pack) in existing NAMERPack.compute | 02-02/D-10 | ✓ VERIFIED | `#pragma kernel` x3 (NAMERPack.compute:18-20), sequential dispatch (NamerComputePipeline.cs:198-200); `NamerPackSurfaceFromOct(` called by CSSurfacePack (:102) — plan key-link pattern present |
| 19 | ComputeTexturePool is the sole RT allocator with leak watchdog | 02-02/D-12 | ✓ VERIFIED | ComputeTexturePool.cs:36-54 sole `new RenderTexture` site; LiveCount (:30); idempotent Release (:60-86); pipeline has no direct RT construction (verified by read) |
| 20 | AsyncGPUReadback used for GPU→CPU reads (D-13) | 02-02 | ✓ VERIFIED | NamerComputePipeline.RequestReadback (NamerComputePipeline.cs:150-153); tests use AsyncGPUReadback.Request + WaitForCompletion/WaitUntil (GpuGoldenTests.cs:324-325, ComputeSmokeTests.cs:112-113, 136-137) |

**Score:** 20/20 verified. No truth FAILED; 2 items need human confirmation (menu surface, FBX path) — routed to `human_verification`, neither contradicts the code evidence.

### Required Artifacts

| Artifact | Expected | Status | Details |
|----------|----------|--------|---------|
| `Editor/Pipeline/SourceInspector.cs` | Selection resolution + property tables + menu item | ✓ VERIFIED | 466 ln; all five kinds; `_SmoothnessTextureChannel` present; committed cbf5eaf/b5a94d2 + review fixes a3b5829/59de1f8 |
| `Editor/Pipeline/NamerSourceModel.cs` | Serializable inspection result | ✓ VERIFIED | 82 ln; map refs + per-map sRGB flags (WR-04) + scalars + warnings; data not log output |
| `Editor/Pipeline/NamerComputePipeline.cs` | Dispatch harness: upload → dispatch → AsyncGPUReadback | ✓ VERIFIED | 286 ln; FindKernel x3, Dispatch, ReleaseResult (WR-01 fix d2ad4b7) |
| `Editor/Pipeline/ComputeTexturePool.cs` | RT lease/release + leak watchdog | ✓ VERIFIED | 125 ln; descriptor-keyed pooling, LiveCount |
| `Compute/NAMERPack.compute` | Three staged kernels | ✓ VERIFIED | 103 ln; CSNormalize/CSOctahedralEncode/CSSurfacePack; `#pragma kernel CSSurfacePack` present; imported + executed on Metal |
| `Compute/NamerEncode.hlsl` | HLSL mirror of encode/pack + constants | ✓ VERIFIED | 68 ln; `NamerPackSurface` + `NamerPackSurfaceFromOct`; term-for-term mirror confirmed |
| `Core/NamerFormat.cs` | D-16 rename normal → normalTexel | ✓ VERIFIED | :76 `PackSurface(float3 normalTexel, ...)`; no math change (diff-adjacent body identical to Phase 1 semantics) |
| `Tests/Editor/GpuGoldenTests.cs` | Kernel golden tests vs Core | ✓ VERIFIED | 413 ln; 3 UnityTests; D-14 tolerances + D-15 gate; all Passed in executed run |
| `Tests/Editor/ComputeSmokeTests.cs` | Full-pipeline smoke + leak watchdog | ✓ VERIFIED | 197 ln; 4 scenarios (incl. WR-04 sRGB data-map pin); watchdog can fail; Passed |
| `Tests/Editor/SourceInspectorTests.cs` | INSP-01..04 coverage | ✓ VERIFIED | 443 ln; 11 tests; all Passed |
| `Tests/Editor/...Tests.Editor.asmdef` | Editor assembly reference | ✓ WIRED | references include `GraffitiEntertainment.Namer.Editor` (committed 4a05349) |
| New `.meta` files | GUID-stable Unity metas (repo convention, Phase 1 CR-02) | ⚠️ WARNING | 6 metas exist on disk but are **untracked in git**: `Editor/Pipeline.meta`, `Editor/Pipeline/NamerSourceModel.cs.meta`, `Editor/Pipeline/SourceInspector.cs.meta`, `Tests/Editor/{SourceInspectorTests,GpuGoldenTests,ComputeSmokeTests}.cs.meta`. 02-02 committed its metas (5c31814/92e0837); 02-01/02-03 did not. Low functional risk today (no assets reference these scripts by GUID yet) but violates the convention fixed as CR-02 in Phase 1 (31638ec) and destabilizes GUIDs for collaborators/Phase 3. Fix: commit the 6 files (orchestrator may bundle with this report) |

### Key Link Verification

| From | To | Via | Status | Details |
|------|----|----|--------|---------|
| NAMERPack.compute | NamerEncode.hlsl | relative #include | ✓ WIRED | `#include "NamerEncode.hlsl"` (compute:15); import + execution on Metal proves resolution |
| CSSurfacePack | NamerPackSurfaceFromOct | pack from already-encoded oct | ✓ WIRED | NAMERPack.compute:102; oct layout written by CSOctahedralEncode:86 |
| NamerComputePipeline | kernels | FindKernel + Dispatch | ✓ WIRED | :49-51, :198-206; pattern `Dispatch(kernel, (w + 7) / 8, ...)` matches plan |
| ComputeTexturePool | RenderTextureDescriptor + GraphicsFormat | lease by descriptor | ✓ WIRED | :36-54; only allocation site |
| SourceInspector.Inspect | NamerSourceModel | returns serializable result | ✓ WIRED | `NamerSourceModel Inspect(` (SourceInspector.cs:41) |
| Menu item | Selection.activeObject | [MenuItem("Tools/NAMER/Inspect Selection")] | ✓ WIRED (static) | :22-34; runtime behavior = human item 1 |
| Property table | smoothness indirection | `_SmoothnessTextureChannel` | ✓ WIRED | Inspector:259 → pipeline :181 → kernel :64; channel-0/1 golden cases executed |
| NamerSourceModel | NamerComputePipeline.Process | `Process(NamerMaterialInspection)` | ✓ WIRED | NamerComputePipeline.cs:69; consumed by smoke test end-to-end |
| GpuGoldenTests | NamerFormat (CPU oracle) | PackAlphaBits/PackSurface/OctahedralDecode | ✓ WIRED | :89, :142, :154, :156; executed |
| Tests | capability gate | supportsComputeShaders && supportsAsyncGPUReadback | ✓ WIRED | GpuGoldenTests.cs:43-44; ComputeSmokeTests.cs:27; executed (gate open on Metal) |
| readback | AsyncGPUReadback | Request + WaitForCompletion/WaitUntil | ✓ WIRED | Both patterns present and executed |
| HLSL mirror | Core NamerFormat | hand-port equivalence | ✓ WIRED (mirror) | Term-for-term: encode sum-guard + 0.5+0.5·v/sum; strict `>` thresholds; floor(r·63) clamp 0..63; /255 alpha; constants equal to NamerConstants |

### Data-Flow Trace (Level 4)

| Artifact | Data Variable | Source | Produces Real Data | Status |
|----------|--------------|--------|--------------------|--------|
| NamerComputeResult.PackedSurface | RT pixels | CSNormalize/CSOctahedralEncode/CSSurfacePack dispatches on uploaded source maps | ✓ Yes — readback non-empty, bytes match NamerFormat oracle (executed) | ✓ FLOWING |
| NamerComputeResult.NormalizedBaseColor | RT pixels | CSNormalize from base map (sRGB-aware) with AO un-multiply | ✓ Yes — W2 assertions vs CPU-computed expectation (executed) | ✓ FLOWING |
| NamerSourceModel.Materials | inspection list | SourceInspector.Inspect over real URP Lit/Standard materials | ✓ Yes — 11 tests assert real map refs/scalars (executed) | ✓ FLOWING |

No hardcoded-empty or static-only data paths found.

### Behavioral Spot-Checks

| Behavior | Command | Result | Status |
|----------|---------|--------|--------|
| EditMode suite at working tree (=HEAD sources) | Parse /tmp/namer-unity-testrun/test-results.xml | total=46 passed=46 failed=0 skipped=0 | ✓ PASS |
| GPU golden tests executed, not skipped | Per-case result extraction | All 4 → result="Passed"; skipped=0 | ✓ PASS |
| Review-fix tests in executed run | Per-case result extraction | DataMapSrgbFlags, FolderResolution_FindsMaterialsInsidePrefabs → Passed (proves run includes 59de1f8/82431b3 fix content) | ✓ PASS |
| Commit existence (12 phase commits) | git cat-file -t | all = commit | ✓ PASS |
| PlayMode phase-1 regression | orchestrator-provided run | 3/3 passed, exit 0 | ✓ PASS (evidence provided; not re-run per constraints) |
| Math re-derivation (mirror check) | hand comparison HLSL vs C# | encode/pack/alpha-bits identical term-for-term; constants in sync | ✓ PASS |

### Probe Execution

Step 7c: SKIPPED — no `scripts/*/tests/probe-*.sh` exist and none declared; not a migration/tooling phase. (Unity execution evidence above stands in for behavioral checks; re-running Unity was out of scope for this static verification.)

### Requirements Coverage

| Requirement | Source Plan | Description | Status | Evidence |
|-------------|------------|-------------|--------|----------|
| INSP-01 | 02-01 | Select GameObject/prefab/FBX/material/folder as source | ✓ SATISFIED | SourceInspector.cs:66-144; tests for material/scene GO/prefab/folder executed; FBX sub-asset path code-verified (human item 2) |
| INSP-02 | 02-01 | URP Lit map discovery (Base/Normal/AO/Metallic/Roughness-Smoothness/Emission/Alpha) | ✓ SATISFIED | :247-268; alpha via base-map alpha (smoothness channel 1, kernel :64) and SurfaceType/_Cutoff recorded — matches the research property-table interpretation of INSP-02 |
| INSP-03 | 02-01 | Scalar fallbacks; Standard supported where available | ✓ SATISFIED | ReadStandard :271-299 (`_MainTex`, `_Glossiness`, `_Mode`); Standard_RecordsMapsAndScalars Passed |
| INSP-04 | 02-01 | Missing maps → sensible defaults, no failure | ✓ SATISFIED | D-07 defaults + guarded reads + warnings; two executed tests |
| NORM-01 | 02-02 | Cleaned base color, baked lighting removed where feasible | ✓ SATISFIED | Conservative cleaning per D-08/D-09 (sRGB normalize + AO un-multiply mirroring Blender DIVIDE node); W2 executed |
| NORM-02 | 02-02 | Maps normalized (color space, scalar-vs-map unification) in GPU compute | ✓ SATISFIED | CSNormalize unifies scalar-vs-map metallic/smoothness (:58-72) and color space (:54); golden channel/scalar cases executed |
| NORM-03 | 02-02 | Per-pixel 2K/4K via compute, not C# loops | ✓ SATISFIED | Dispatch-only pipeline; no pixel loops in production code |
| TEST-04 | 02-03 | GPU kernels verified vs CPU Core via round-trip tests | ✓ SATISFIED | 3 golden + 1 smoke executed green on Metal at D-14 tolerances (alpha EXACT); capability-gated elsewhere per D-15 |

Orphaned requirements: none — REQUIREMENTS.md maps exactly INSP-01..04, NORM-01..03, TEST-04 to Phase 2 and all appear in plan frontmatter (`requirements-completed` per SUMMARY match).

### Decision-Conformance Spot Checks (D-01..D-16)

| Decision | Status | Evidence |
|----------|--------|----------|
| D-01 API + minimal Inspect Selection menu | ✓ | SourceInspector.cs:22, :41 (API returns model; menu logs report) |
| D-02 Folder recursion, dedupe, warn-not-fail | ✓ | :110-132 (t:Material + t:Prefab + t:Model), :190-202, :143 |
| D-03 Unit = unique material | ✓ | Dedupe test executed |
| D-04 Property-name tables via GetTexture; no filename heuristics | ✓ | :247-325; zero filename/suffix logic (grep clean) |
| D-05 Smoothness source both paths, roughness = 1 − smoothness | ✓ | Inspector :259; kernel :64-66; channel-0/1 golden executed |
| D-06 Best-effort scan + warning, never fail | ✓ | :301-325 + :240; UnknownShader test executed |
| D-07 Neutral defaults | ✓ | :218-227; NeutralDefaults test executed |
| D-08 Conservative cleaning w/ strength param | ✓ | Kernel :56; AoUnmultiplyStrength (Phase 3 UI noted in model doc) |
| D-09 Mirror Blender reference cleaning | ✓ | AO un-multiply mirrors reference DIVIDE node (02-RESEARCH §responsibility map); crude normal-Z delighting rejected as researched |
| D-10 Staged kernels + shared include | ✓ | 3 pragmas; NamerEncode.hlsl included; single GPU source of truth |
| D-11 Formats: R16G16B16A16_SFloat / R8G8B8A8_UNorm, never sRGB | ✓ | Descriptors + executed format assertions |
| D-12 ComputeTexturePool | ✓ | Sole allocator; LiveCount watchdog |
| D-13 AsyncGPUReadback throughout | ✓ | Pipeline + tests; leak watchdog asserts pre-Dispose (WR-02 fixed) |
| D-14 Alpha EXACT / 1-255 / dot 1−1e-3 | ✓ | Assertions in code; executed green (decode-oracle nuance noted in Truth 15) |
| D-15 Metal verified; capability-gated skips w/ report; threadgroup limits checked | ✓ | Gates present; run skipped=0; [numthreads(8,8,1)]=64 threads, no groupshared (limits verified in 02-RESEARCH) |
| D-16 normalTexel rename | ✓ | NamerFormat.cs:76 |

### Out-of-Scope Check (phase boundary held)

- **No asset writing:** grep for `WriteAllBytes|EncodeToPNG|EncodeToEXR|CreateAsset|WriteImportSettingsIfDirty|SaveAssets` in Editor/Pipeline → zero matches. Output is in-memory RTs only.
- **No editor window:** no `EditorWindow` anywhere in the package; only menu items are Phase 1's smoke scene and Phase 2's Inspect Selection (D-01-sanctioned).
- **No vertex-color decomposition:** no VertexColorFitter/splitting code; the only vertex-color references are Phase 1's runtime shader path (SHDR-02, pre-existing).
- **No stylization:** no NamerStyleProfile/palette/hue/smoothing-filter code.
- **No importer mutation:** inspection is read-only (Truth 8).

### Anti-Patterns Found

| File | Line | Pattern | Severity | Impact |
|------|------|---------|----------|--------|
| (git index) | — | 6 untracked .meta files (see Required Artifacts) | ⚠️ Warning | GUID instability for collaborators; violates the CR-02 convention; commit them |
| NAMERPack.compute / NamerComputePipeline.cs | compute:45,77,89 / cs:205 | Kernel thread-group size (8) duplicated in two files (IN-02) | ℹ️ Info | Changing numthreads without the C# side leaves pixels unwritten; add a shared const when touched |
| GpuGoldenTests.cs | 30-41 | Golden vectors contain only 2 distinct normals (IN-03) | ℹ️ Info | GPU oct encode exercised on neutral + (1,0,0) only; CPU suite covers more directions; add non-axis texels opportunistically |
| NamerComputePipeline.cs | 37-39, 237-267 | Static fill textures never destroyed (IN-01) | ℹ️ Info | Session-lifetime editor cache; acceptable |
| NamerComputePipeline.cs | 76-77 | Output resolution driven by base map only, silent rescale (IN-05) | ℹ️ Info | Documented policy; surface a warning in Phase 3 UI |
| SourceInspector.cs | 136-141 | Non-material asset with a path resolves silently to zero materials (IN-04) | ℹ️ Info | Add a "no materials found" warning when Phase 3 exposes the UI |

Debt markers: none. `grep TBD|FIXME|XXX|TODO|HACK|PLACEHOLDER` across Editor/Core/Compute/Tests/Shaders → zero matches. IN-01..IN-05 are documented review Info findings left as-is by decision (02-REVIEW fix-outcomes table), not untracked debt.

### Human Verification Required

1. **Inspect Selection menu (live editor)**
   **Test:** Select a material, a scene GameObject, and a folder; run `Tools > NAMER > Inspect Selection` each time.
   **Expected:** Console prints the `[NAMER]` report — unique-material count, per-material maps with sRGB/linear flags, scalars, Roughness, warnings (e.g. sRGB data-map advisories).
   **Why human:** The menu wrapper and its console output are the D-01 manual-validation surface; automated tests call `SourceInspector.Inspect` directly.

2. **FBX/model asset selection**
   **Test:** Select an imported FBX/model asset in Project window; run Inspect Selection.
   **Expected:** Materials embedded as sub-assets are found and inspected, one unit per unique material.
   **Why human:** The `AddSubAssetMaterials` path has no automated fixture (would require a committed imported model); the other four selection kinds are test-covered.

### Residual Risks / Gaps

- **Untracked metas (Warning):** 6 `.meta` files need committing (list above). Mechanical fix; flagged for the orchestrator's bundling commit.
- **Metal-only verification (by design, D-15):** GPU equivalence is proven on Metal only; other backends rely on the capability gate + v2 CI matrix. Documented limitation, not a gap.
- **Info findings IN-01..IN-05 left as-is:** documented in 02-REVIEW with revisit triggers (Phase 3 UI work will surface IN-04/IN-05 naturally).
- **Golden-vector diversity (IN-03):** GPU oct coverage is thin (2 distinct texels); CPU-side coverage is broader. Opportunistic improvement.
- **MVP goal format:** ROADMAP Phase 2 goal is not User Story format (process note; PLANs carry the validating story). Optional `/gsd mvp-phase 2` cleanup.

### Gaps Summary

No must-have truth FAILED and no artifact is MISSING, STUB, or unwired. All 4 roadmap success criteria and all 16 plan truths are verified against the code, with the GPU half verified by an executed 46/46 EditMode run (0 skipped, all 4 GPU tests green on Metal) whose XML I re-parsed myself — including the four review-fix commits' tests. Requirements INSP-01..04, NORM-01..03, TEST-04 are satisfied; decisions D-01..D-16 all conform; the phase boundary held (no asset writing, no window, no vertex-color, no stylization; inspection read-only).

Two editor-interactive surfaces cannot be verified statically (the Inspect Selection menu wrapper and the FBX/model sub-asset path) and are routed to human verification. One hygiene warning: 6 new `.meta` files are untracked in git and should be committed to preserve the Phase 1 CR-02 GUID-stability convention.

---

_Verified: 2026-08-27T18:40:53Z_
_Verifier: Claude (gsd-verifier)_

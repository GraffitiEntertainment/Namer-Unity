---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: executing
stopped_at: Completed 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid-09-PLAN.md
last_updated: "2026-09-17T22:00:12.379Z"
last_activity: 2026-09-17 -- Phase 4.2 planning complete
progress:
  total_phases: 8
  completed_phases: 6
  total_plans: 34
  completed_plans: 33
  percent: 75
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-08-25)

**Core value:** A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.
**Current focus:** Phase 04.2 — gouraud-projection-one-texture-with-roughness-transfer-resid

## Current Position

Phase: 04.2 (gouraud-projection-one-texture-with-roughness-transfer-resid) — EXECUTING
Plan: 5 of 5
Status: Ready to execute
Last activity: 2026-09-17 -- Phase 4.2 planning complete

Progress: [██████████] 100%

## Performance Metrics

**Velocity:**

- Total plans completed: 21
- Average duration: - min
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 02 | 3 | - | - |
| 3 | 3 | - | - |
| 03.1 | 3 | - | - |
| 04 | 5 | - | - |
| 4.1 | 7 | - | - |

**Recent Trend:**

- Last 5 plans: none
- Trend: -

*Updated after each plan completion*
| Phase 01-core-format-contract-runtime-decode P03 | 12min | 3 tasks | 11 files |
| Phase 01-core-format-contract-runtime-decode P01 | 26min | 2 tasks | 7 files |
| Phase 01-core-format-contract-runtime-decode P02 | 100min | 3 tasks | 5 files |
| Phase 02-source-inspection-gpu-compute-pipeline P01 | 10min | 2 tasks | 4 files |
| Phase 02-source-inspection-gpu-compute-pipeline P02 | 6min | 2 tasks | 8 files |
| Phase 02-source-inspection-gpu-compute-pipeline P03 | 10min | 2 tasks | 2 files |
| Phase 03-asset-generation-editor-workflow-preview P01 | 7min | 3 tasks | 10 files |
| Phase 03-asset-generation-editor-workflow-preview P02 | 44min | 2 tasks | 9 files |
| Phase 03-asset-generation-editor-workflow-preview P03 | 27min | 3 tasks | 7 files |
| Phase 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit P01 | 22min | 3 tasks | 8 files |
| Phase 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit P02 | 29min | 3 tasks | 12 files |
| Phase 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit P03 | 20min | 3 tasks | 6 files |
| Phase 04-vertex-color-decomposition-residual P04-01 | 33 | 2 tasks | 7 files |
| Phase 04-vertex-color-decomposition-residual P04-02 | 32min | 3 tasks | 9 files |
| Phase 04-vertex-color-decomposition-residual P04-03 | 14min | 3 tasks | 9 files |
| Phase 04-vertex-color-decomposition-residual P04-04 | 2min | 2 tasks | 3 files |
| Phase 04-vertex-color-decomposition-residual P04-05 | 12min | 2 tasks | 5 files |
**Per-Plan Metrics:**

| Plan | Duration | Tasks | Files |
|------|----------|-------|-------|
| Phase 04.1 P01 | 10min | 3 tasks | 7 files |
| Phase 04.1 P02 | 31 | 3 tasks | 15 files |
| Phase 04.1 P03 | 10min | 2 tasks | 7 files |
| Phase 04.1 P04 | 4min | 3 tasks | 6 files |
| Phase 04.1 P05 | 2min | 3 tasks | 2 files |
| Phase 04.1 P07 | 12min | 3 tasks | 8 files |
| Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid P01 | 9min | 3 tasks | 4 files |
| Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid P02 | 9min | 3 tasks | 4 files |
| Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid P03 | 20min | 3 tasks | 14 files |
| Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid P04 | 17min | 3 tasks | 5 files |
| Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid P05 | 6min | 2 tasks | 4 files |

## Accumulated Context

### Roadmap Evolution

- Phase 03.1 inserted after Phase 3: AO extraction: un-multiply baked AO from the base texture, with bake tweaks (cubemap light from high-res model, blur, etc.) (URGENT)
- Phase 04.1 inserted after Phase 4: Baked-response roughness extraction + zero-residual one-texture mode — extract gloss baked into base as 6-bit roughness, refit vertex colors, D-13 auto-drop becomes primary path (see notes/roughness-extraction-one-texture-mode.md) (URGENT)

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- (roadmap): Consolidated the research SUMMARY's 6-phase suggestion into 5 phases to fit coarse granularity; the empty "hardening" phase (batch/determinism/multi-platform) carried no v1 requirement and was folded into relevant phases' success criteria. Batch (BATCH-01) remains v2.
- [Phase 01]: Hand-authored the Unity project skeleton instead of -createProject and skipped the batchmode open — repo root is non-empty and a batchmode open would emit unignored Library/Temp artifacts (pre-existing .gitignore untouched)
- [Phase 01]: Unity 6 Texture2D has no GraphicsFormat constructor (GraphicsFormat lives in UnityEngine.Experimental.Rendering) — smoke test uses TextureFormat.RGBA32 + linear=true + graphicsFormat assertion
- [Phase 01]: .gitignore /packages/ (NuGet) ignores Unity Packages/ on case-insensitive macOS; force-added Packages files — downstream 01-01/01-02 additions under Packages/ also need git add -f until tracked
- [Phase 01]: Corrected the round-trip test: map unit normal -> DirectX texel -> encode -> decode, asserting dot(decoded, normal) >= 1-1e-3 (the plan's texel-direction oracle was mathematically wrong per Pitfall 3)
- [Phase 01]: Removed -quit from the batchmode test command; it quits before the async test run starts (test framework controls its own exit)
- [Phase 01]: Modernized Tests/Editor asmdef from legacy optionalUnityReferences to explicit UnityEngine.TestRunner/UnityEditor.TestRunner references
- [Phase 01]: Added com.unity.test-framework as a direct manifest dependency so UnityEditor.TestRunner loads and -runTests runs
- [Phase 01]: Used URP property-driven blend state (Blend[_SrcBlend][_DstBlend] + _Surface) instead of '#if _SURFACE_TYPE_TRANSPARENT' around Blend — ShaderLab render state cannot be keyword-gated
- [Phase 01]: Modernized the PlayMode test asmdef (legacy optionalUnityReferences -> explicit UnityEngine.TestRunner reference) to match the 01-01 Editor test asmdef fix
- [Phase 01]: Editor asmdef needs Unity.RenderPipelines.Universal.Runtime + Unity.RenderPipelines.Core.Runtime references when editor C# uses URP types (the shader HLSL does NOT need asmdef refs)
- [Phase 01]: URP OUTPUT_SH4 is a 5-param macro under LIGHTMAP_ON/APV (4-arg call fails 'too few arguments'); use OUTPUT_SH (always 2-param, SampleSHVertex legacy path) unless the shader opts into APV
- [Phase 02-source-inspection-gpu-compute-pipeline]: SourceInspector resolves FBX/model assets via non-generic AssetDatabase.LoadAllAssetsAtPath (returns Object[]) with a Material filter — LoadAllAssetsAtPath<T> does not exist in Unity 6
- [Phase 02-source-inspection-gpu-compute-pipeline]: Roughness = 1 - Smoothness; Emissive = max(EmissionColor RGB) when a map or non-black emission color is present; AoUnmultiplyStrength (cleaning, default 1.0) kept distinct from OcclusionStrength (decode-time blend metadata)
- [Phase 02-source-inspection-gpu-compute-pipeline]: _BaseColor tint stays as material metadata (not baked into the normalized texture), matching the emissive-color-as-metadata convention
- [Phase 02-source-inspection-gpu-compute-pipeline]: GPU path follows the plan's sRGB upload contract verbatim (_SourceIsSrgb from BaseMapIsSrgb); Blit linearization is verified numerically in 02-03, not assumed here
- [Phase 02-source-inspection-gpu-compute-pipeline]: 02-03 verified the sRGB upload contract numerically: Graphics.Blit from an sRGB Texture2D to a linear RenderTexture is a raw copy (no implicit sRGB->linear), so the compute shader's single SRGBToLinear in CSNormalize is the correct decode — the Blit double-decode hazard did not materialize
- [Phase 02-source-inspection-gpu-compute-pipeline]: GPU golden decode-dot reference = NamerFormat.OctahedralDecode(OctahedralEncode(texel)) so the D-14 dot >= 1-1e-3 assertion stays self-consistent for all 5 golden vectors (incl. the pathological (1,0,0) texel D, avoiding the Phase 1 Pitfall 3 texel-direction oracle)
- [Phase 02-source-inspection-gpu-compute-pipeline]: Unity 6000.0.82f1/Metal normalizes Texture2D.graphicsFormat to linear variants — sRGB-imported textures report R8G8B8A8_UNorm, so graphicsFormat-based sRGB detection never returns true (code-review WR-04 root cause). Authored-sRGB detection must read TextureImporter.sRGBTexture via AssetDatabase.GetAssetPath (runtime-created textures fall back to graphicsFormat). BaseMapIsSrgb false-negatives from the old check would have skipped sRGB decode on real user assets
- [Phase 03-asset-generation-editor-workflow-preview]: AssetGenerator readback uses NamerComputePipeline.RequestReadback (the pipeline's existing AsyncGPUReadback contract) rather than AsyncGPUReadback.Request inline — D-08
- [Phase 03-asset-generation-editor-workflow-preview]: Base color linear->sRGB uses Graphics.ConvertTexture into an sRGB Texture2D (R8G8B8A8_SRGB) — the inverse of the compute shader's SRGBToLinear, no per-pixel C# loop (NORM-03)
- [Phase 03-asset-generation-editor-workflow-preview]: Preview recompute assigns NamerComputePipeline result render targets directly to in-memory NAMER/debug materials (no readback) — readback stays exclusive to the disk-write path (D-10)
- [Phase 03-asset-generation-editor-workflow-preview]: Before/after preview is a single combined PreviewRenderUtility render (both meshes in one BeginPreview/EndPreview) with Before/After captions above the halves, so both panes share one honest camera (D-09)
- [Phase 03-asset-generation-editor-workflow-preview]: Output controls bind directly to the EditorPrefs-backed NamerProcessorSettings (Destination/Prefix/Suffix/OverwriteGenerated), not a separate UI model
- [Phase 03-asset-generation-editor-workflow-preview]: Base-PNG known-value test compares generated byte > authored source byte (proving linear->sRGB conversion ran) rather than pinning an exact sRGB byte, because the gamma-color-space project's EncodeToPNG/Blit conventions produce a value that differs from the plan's '~188' example.
- [Phase 03-asset-generation-editor-workflow-preview]: UI-03/UI-04/UI-05 recorded as phase-scoped partial delivery — the Phase-3 slice (D-14 controls, D-09 preview, six D-11 debug channels) ships, while vertex-color/residual/reconstruction-error views and Phase-4/5 stylization/palette/smoothing/normal-detail/roughness-simplification controls remain open.
- [Phase 03.1]: Image-space AO extraction auto-runs (D-07) only when _OcclusionMap is absent; the authored path stays byte-identical with the epsilon floor
- [Phase 03.1]: The SAME extracted AO is the CSNormalize divisor AND the packed B channel (D-08 round-trip); divisor floor is 0.1 extracted / 1e-6 authored, divisor-side only (D-09)
- [Phase 03.1]: Geometry bake is a deterministic CPU Burst raycast (median-split BVH + Möller–Trumbore), not GPU depth-projection — headless-testable (64 dirs, cage 0.01, 10%-bounds distance, 512 cap)
- [Phase 03.1]: RequestBake runs synchronously as an explicit one-time action off the debounce — EditorApplication.delayCall does not fire during headless EditMode tests
- [Phase 03.1]: Unity.Burst + Unity.Mathematics added to the Editor asmdef — Collections 2.6.8 no longer pulls Unity.Mathematics transitively
- [Phase 03.1 close-out]: W1 accepted — bake/occluder stay preview-scope (generated assets use extraction/authored AO); W2 accepted — synchronous first bake with cancellable progress bar; UAT 4/4 pass 2026-08-31
- [Phase 04]: Phase 04-01: MeshVertexSplitter welds by a quantized (position, normal, tangent, uv) integer key (never floating-point equality), so near-equal shared-edge attributes weld while UV seams / hard normals / tangent breaks split.
- [Phase 04]: Phase 04-01: VertexColorFitter's 'per-vertex 3x3 Gram' is a per-triangle 3x3 normal-equations solve accumulated per-vertex by incident-triangle count; the diagonal-only per-vertex approximation over-shoots constant colors, so the full 3-corner barycentric solve is load-bearing.
- [Phase 04]: Phase 04-01: the vertex-color fit is UV-space only (positions/normals/tangents are not sampled), so only Uvs plus flattened sub-mesh triangles are converted to NativeArrays.
- [Phase 04]: Phase 04-01: per-vertex reconstruction error is accumulated on the main thread from per-triangle job output, avoiding a cross-thread read-modify-write race on shared vertices.
- [Phase 04]: Phase 04-02: The residual is the multiplicative quotient base / max(vcInterp, VcFloor) derived from the QUANTIZED Color32 colors, with a coverage mask and base-alpha preservation (Pitfall 1/2/5)
- [Phase 04]: Phase 04-02: The reduce keeps every channel in [0,1] (mean/max/mean/min) — not raw sums — so it stays overflow-free at any resolution; Coverage and AvgError are derived as fraction ratios on the CPU
- [Phase 04]: Phase 04-02: Coverage needs a within-threshold count that does not fit the 4-channel reduce, so it is a second reduce over a dedicated _CoverageStat texture
- [Phase 04]: Phase 04-03: the decomposition source mesh is resolved inside NamerProcessor.Process (ResolveSourceMesh) because SourceInspector never populates NamerMaterialInspection.BakeSourceMesh in production — BakeSourceMesh was only assigned in tests; without it the decomposition stage would always fall back to the Phase-3 shape
- [Phase 04]: Phase 04-03: the scene sharedMesh swap reads MeshFilter via renderer.GetComponent<MeshFilter>() (a Component sibling of Renderer), not a 'renderer is MeshFilter' pattern match — MeshFilter is not a Renderer subclass; the plan's literal example does not compile
- [Phase 04]: Phase 04-03: the live preview keeps the pool-leased residual render target bound to the preview material (no readback) until the next recompute — Matches the Phase-3 'assign render targets directly to preview materials' convention
- [Phase 04 gap-closure]: Decision-coverage gate override — D-11/D-14 reported uncovered by the gap plans (04-04/04-05); both are body-cited in 04-02-PLAN/04-03-PLAN and implemented in shipped code (viridis ramp in NAMERDecomp.hlsl, preview toggle in NamerEditorWindow). Accepted as a frontmatter-citation gap, not a scope drop; verify-phase should re-surface if evidence of a real drop emerges.
- [Phase 04 gap-closure]: CR-01 guard (CountDistinctSourceMeshes + decomposeSourceMesh=null fallback) and CR-02 (WriteResidualExr FilterMode.Bilinear) verified pre-existing in the 04-04 WIP snapshot — the packed surface texture stays FilterMode.Point (GEN-04), only the residual EXR becomes Bilinear so the reduced-resolution asset matches the reported MaxError
- [Phase 04 gap-closure]: CR-01 fallback is byte-equivalent to DecompositionEnabled==false: AssetGenerator.Generate keys base-vs-residual binding on decomp != null (not settings.DecompositionEnabled), so _BaseResidualMap binds the GENERATED base PNG (BaseTexturePath) — the CR-01 regression tests must compare against BaseTexturePath, not the source base map
- [Phase 04]: CR-03 closed: CPU VertexColorFitter.SampleBase now Repeat-wraps UVs (floor(u / BaseWidth) + (x0+1) % BaseWidth) instead of clamping, and GenerateResidual guards the D-13 drop gate on kMinCoverageFraction = 1e-6f — zero-coverage (tiling) fits flag CannotDecompose and NamerProcessor falls back to the Phase-3 shape with a 'UV coverage near zero' warning
- [Phase 04]: kMinCoverageFraction = 1e-6f (near-zero epsilon): zero-coverage (tiling) fits flag CannotDecompose and fall back to the Phase-3 shape; legitimately partial coverage (small mesh on a large atlas) proceeds as before — that was true pre-gap and stays true
- [Phase 04.1]: Rec.601 luminance weights (not Rec.709) for Blender-parity Sobel (RESEARCH Pitfall 4)
- [Phase 04.1]: Extraction overrides the scalar roughness at PACK time in CSSurfacePack, not by re-dispatching CSNormalize (which would double-apply the AO un-multiply)
- [Phase 04.1]: RoughnessExtractStrength has no inline default (0 = off) preserving the legacy scalar path; shipped default-on 1f arrives via settings in plan 02
- [Phase ?]: NamerProcessor composes the Func<float,float> evaluate callback (owner of splitter/fitter/decomp state); NamerRoughnessPipeline owns only the GPU per-step sharp-removal (RunSharpRemoval) and the 3A fit cache
- [Phase ?]: The fit-driven strength search runs synchronously on cache miss inside NamerProcessor.Process, reusing a pre-Process split; the refit consumes NormalizedBaseColor (already the D-05 cleaned base)
- [Phase ?]: RoughnessExtractStrength/RoughnessEstimator settings + NamerEditorConstants defaults pulled into plan 02 (NamerProcessor settings copy + fit tests need them)
- [Phase ?]: NamerComputeResult.NormalizedBaseColorOwnedByRoughnessPool tracks the D-05 repoint so ReleaseResult returns the cleaned base to the roughness pool (compute pool Release would no-op and leak)
- [Phase ?]: [Phase 04.1 P03]: The one-texture _BaseResidualMap unbinding needs no code change — already delivered by the existing D-13 drop (baseResidualPath = decomp != null ? residualWritePath : basePath yields null when residualWritePath == null); no baseResidualPath edit is made, so the non-decomposed Phase-3 path keeps binding the Base PNG.
- [Phase ?]: [Phase 04.1 P03]: D-06 _RoughnessOffsetMap is additive with a neutral 'black' {} default (offset .r == 0 => byte-identical decode), applied in InitializeNamerSurfaceData AFTER NAMER_DECODE_SURFACE so the macro signature and Meta-pass call site are untouched; the offset is a direct user-assigned Texture2D (no AssetDatabase.LoadAssetAtPath).
- [Phase 04.1]: Scene guard = graceful rejection, not scene support — .unity assets are outside PRD selection scope and LoadAllAssetsAtPath cannot read scene objects; scene-instance GameObject selection (empty asset path) is unchanged
- [Phase 04.1]: Preview wiring = option 1 — RecomputePreview supplies the 3A evaluate callback (composed exactly like NamerProcessor) rather than relying on the fit cache, which is empty on first preview; EvaluateRefitMaxError widened private->internal instead of duplicating the domain helper
- [Phase 04.1]: Vanish guard = bindposes-before-boneWeights reorder (skinned-safe) + degenerate-bounds guard so a broken generated mesh is never bound; no speculative guards beyond the diagnosed static defect
- [Phase 04.1]: Minimizer fixture rescaled (not a local threshold) so the shipped ErrorThreshold=0.02 stays meaningful and plan 04.1-02 Task 5 item 1 holds exactly
- [Phase 04.1]: Collapse fixtures use fixture-local CollapseErrorThreshold=0.04 + BakedResponse gloss amplitude 0.10 (test-only); production D-13 gate / NamerRoughnessFitter / MinBlurRadius untouched
- [Phase 04.1]: Offset fixture persists the roughness-offset texture as an imported asset (mirroring CreateImportedBaseMap), matching D-06 product intent of a user-assigned project texture
- [Phase 04.1]: Anchored-inverted roughness mapping (D-08/D-09/D-10, plan 04.1-07): the authored roughness scalar anchors the extracted map and the Sobel edge magnitude (p90-scaled) dips texels toward gloss — saturate(scalar − strength · mag/p90) at both consume sites (CSRoughnessRemap fit-driven; CSSurfacePack Sobel); CSRoughnessNormalize stays direct-polarity; fit-driven pack adoption stays full-strength (isFitDriven ? 1f); estimator tooltip drops the Blender-parity claim
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: Symmetric VcFloor max(base,F)/max(vc,F) adopted (04.2 RESEARCH Pattern 2) — kills the 1.70% below-floor dark-texel exception class at sub-LSB reconstruction cost
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: NamerResidualMode.Gate is the byte-identical default; AlwaysKeep/NeverKeep force keep/drop in both directions
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: projectedOut is caller-owned (R16G16B16A16_SFloat linear, w/h), written by the pipeline and never released here
- [Phase 04.2]: Removed-luma transfer declares _RemovedLuma once as RWTexture2D (read-write, read via [] in the remap kernel) — HLSL cannot declare one resource name twice; mirrors the existing _RoughnessRaw/_BlurPing read-write pattern
- [Phase 04.2]: RemovedLumaP90 mirrors RobustSobelScale but reads |.r| and omits the trueMax ceiling — removed-luma has no precomputed true max; the kernel saturate owns the 6-bit clip at both ends
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: NamerProjectionContext.Run is invoked between normalize and pack; NormalizedBaseColor is the projected base and SourceBaseColor preserves the source for the residual-ON dividend and preview
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: CreateProjectionContext is internal; the window calls it directly and the projection-era tests mirror its Run contract with a local helper
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: DipSource/WriteResidual are fresh keys (no stale-estimator migration); WriteResidual drives AlwaysKeep/NeverKeep, retiring Gate from production call sites
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: DrawDecompStats gains a Removed-detail max error row (FitOnlyMaxError) and the residual row reads 'not written (one-texture)' vs 'written @ Npx'
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: No computed overlap-depth statistic was added — honest tooltip copy carries the measured numbers (0.07 / 87.9 / ~55%), per CONTEXT 'at most an honest statement, not repair'
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: EditMode full-suite run deferred to the orchestrator's post-wave regression gate (no unity-mcp relay; live editor holds the project lock)
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: Orthographic preview camera (fieldOfView deleted) fixed at OrthoCameraDistance; camera transform never moves for framing or zoom
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: One shared yaw+pitch quaternion (pitch clamped to +/-89 degrees) drives both DrawMesh calls — pane sync is the invariant (amended 2026-09-16)
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: Zoom is orthographic size via a clamped zoom scale; initial size = both objects wide plus PreviewGap under the rotation-invariant bounding-sphere bound
- [Phase 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid]: Added a 6th public test-observability accessor (OrthographicSize) beyond the plan's 5 listed, so the render test can assert the camera's applied size

### Pending Todos

None yet.

### Blockers/Concerns

- [Phase 2]: Metal/DX11/Vulkan compute limits (threadgroup ≤ 256, groupshared ≤ 16 KB) flagged MEDIUM in research — verify against Unity 6 docs during planning.
- [Phase 3]: `PreviewRenderUtility` API surface returned 404 during research — confirm signatures during planning.
- [Phase 4]: Vertex-color barycentric least-squares + seam-splitting is the highest algorithmic risk; no single authoritative reference.
- [Phase 5]: Palette extraction and edge-preserving filter specifics are sparse in Unity docs.
- [Phase 01]: Interactive Unity Editor (PID 11637) holds the project lock, blocking the headless PlayMode smoke test (-batchmode) — close the editor or run the PlayMode test in-editor to unblock 01-02 Task 3

### Quick Tasks Completed

| # | Description | Date | Commit | Directory |
|---|-------------|------|--------|-----------|
| 260829-isy | Bind generated NAMER materials to renderers; After panel and debug views prefer generated material/textures (flip to live on AO slider tweak) | 2026-08-29 | c4445d5 | [260829-isy-bind-generated-namer-materials-to-render](./quick/260829-isy-bind-generated-namer-materials-to-render/) |
| 260829-n6x | Remove silent geometry-bake override from NAMER window so AO slider updates reshape the extracted AO live, matching Process output | 2026-08-29 | da45cf7 | [260829-n6x-remove-silent-geometry-bake-override-fro](./quick/260829-n6x-remove-silent-geometry-bake-override-fro/) |

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-09-17T19:48:02Z
Stopped at: Completed 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid-09-PLAN.md
Resume file: None

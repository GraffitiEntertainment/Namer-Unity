---
gsd_state_version: 1.0
milestone: v1.0
milestone_name: milestone
status: planning
stopped_at: Phase 3 context gathered
last_updated: "2026-08-27T20:37:24.400Z"
last_activity: 2026-08-27
progress:
  total_phases: 5
  completed_phases: 2
  total_plans: 6
  completed_plans: 6
  percent: 40
---

# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-08-25)

**Core value:** A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.
**Current focus:** Phase 3 — asset generation + editor workflow + preview

## Current Position

Phase: 3
Plan: Not started
Status: Ready to plan
Last activity: 2026-08-27

Progress: [██████████] 100%

## Performance Metrics

**Velocity:**

- Total plans completed: 3
- Average duration: - min
- Total execution time: 0.0 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 02 | 3 | - | - |

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

## Accumulated Context

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

### Pending Todos

None yet.

### Blockers/Concerns

- [Phase 2]: Metal/DX11/Vulkan compute limits (threadgroup ≤ 256, groupshared ≤ 16 KB) flagged MEDIUM in research — verify against Unity 6 docs during planning.
- [Phase 3]: `PreviewRenderUtility` API surface returned 404 during research — confirm signatures during planning.
- [Phase 4]: Vertex-color barycentric least-squares + seam-splitting is the highest algorithmic risk; no single authoritative reference.
- [Phase 5]: Palette extraction and edge-preserving filter specifics are sparse in Unity docs.
- [Phase 01]: Interactive Unity Editor (PID 11637) holds the project lock, blocking the headless PlayMode smoke test (-batchmode) — close the editor or run the PlayMode test in-editor to unblock 01-02 Task 3

## Deferred Items

Items acknowledged and carried forward from previous milestone close:

| Category | Item | Status | Deferred At |
|----------|------|--------|-------------|
| *(none)* | | | |

## Session Continuity

Last session: 2026-08-27T20:37:24.377Z
Stopped at: Phase 3 context gathered
Resume file: .planning/phases/03-asset-generation-editor-workflow-preview/03-CONTEXT.md

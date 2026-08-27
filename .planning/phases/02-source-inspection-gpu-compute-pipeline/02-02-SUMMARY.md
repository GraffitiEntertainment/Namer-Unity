---
phase: 02-source-inspection-gpu-compute-pipeline
plan: 02
subsystem: gpu-compute
tags: [unity, hlsl, compute-shader, rendertexture, asyncgpureadback, gpu, namer]

# Dependency graph
requires:
  - phase: 01-core-format-contract-runtime-decode
    provides: Core NamerFormat + NamerConstants (encode/pack math to mirror line-for-line)
  - phase: 02-source-inspection-gpu-compute-pipeline
    provides: NamerSourceModel / NamerMaterialInspection / SourceInspector (model consumed by Process)
provides:
  - Shared HLSL mirror include (Compute/NamerEncode.hlsl) of NamerFormat encode/pack + constants
  - Three staged compute kernels in Compute/NAMERPack.compute (CSNormalize / CSOctahedralEncode / CSSurfacePack)
  - Editor/Pipeline/ComputeTexturePool.cs (sole RenderTexture allocator + leak watchdog)
  - Editor/Pipeline/NamerComputePipeline.cs (upload -> dispatch -> AsyncGPUReadback harness)
  - D-16 rename (PackSurface normal -> normalTexel) in Core/NamerFormat.cs
affects: [02-03 (GPU golden tests consume kernels + pipeline), 03-01 (asset generation consumes pipeline output)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "HLSL mirror-include pattern: NamerEncode.hlsl is the single shared GPU source of truth, mirroring Core/NamerFormat line-for-line (no re-derivation)"
    - "Staged GPU kernel pattern: CSNormalize (base/AO/roughness) -> CSOctahedralEncode (oct) -> CSSurfacePack (NamerPackSurfaceFromOct on already-encoded oct)"
    - "RenderTexture pooling by descriptor with lease/release + LiveCount leak watchdog"

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Compute/NamerEncode.hlsl
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs
  modified:
    - Packages/com.graffitientertainment.namer/Core/NamerFormat.cs
    - Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute

key-decisions:
  - "Kept _BaseColor tint as material metadata (not baked into the normalized texture), matching the emissive-color-as-metadata convention"
  - "Intermediates use R16G16B16A16_SFloat linear; final packed surface uses R8G8B8A8_UNorm linear; compute never writes sRGB (D-11)"
  - "sRGB->linear is applied only to base color, gated on inspection.BaseMapIsSrgb; normal/AO/metallic/roughness are never color-converted"

patterns-established:
  - "Mirror-Core HLSL include with strict threshold constants (0x80u/0x40u/0x3Fu/63.0/255.0/1e-6)"
  - "Per-kernel SetTexture binding (SRV for inputs, UAV for RW outputs) with (w+7)/8 dispatch sizing"

requirements-completed: [NORM-01, NORM-02, NORM-03]

# Metrics
duration: 6min
completed: 2026-08-27
---

# Phase 2 Plan 2: GPU Compute Pipeline Summary

**Shared NamerEncode.hlsl mirror include + three staged kernels (CSNormalize/CSOctahedralEncode/CSSurfacePack) + ComputeTexturePool/NamerComputePipeline dispatch harness producing normalized base color and packed surface entirely on the GPU via AsyncGPUReadback**

## Performance

- **Duration:** 6 min
- **Started:** 2026-08-27T00:37:31Z
- **Completed:** 2026-08-27T00:43:22Z
- **Tasks:** 2
- **Files modified:** 8

## Accomplishments
- `Compute/NamerEncode.hlsl` mirrors `Core/NamerFormat` encode/pack line-for-line with `NamerPackSurfaceFromOct` (post-encode helper) so kernels never re-derive format math
- `Compute/NAMERPack.compute` filled with three `[numthreads(8,8,1)]` staged kernels; metallic/smoothness MAP path wired end-to-end (`_MetallicGlossIn` / `_SmoothnessTextureChannel` / `_HasMetallicGlossMap`), roughness = 1 - smoothness computed before quantization
- `Editor/Pipeline/ComputeTexturePool.cs` is the sole RenderTexture allocator with lease/release by descriptor + `LiveCount` leak watchdog
- `Editor/Pipeline/NamerComputePipeline.cs` uploads via `Graphics.Blit`, dispatches three kernels, and reads back via `AsyncGPUReadback` — no per-pixel C# loops
- D-16 rename landed (`PackSurface(float3 normalTexel, ...)`) with no math change; EditMode suite stays green at 40/40

## Task Commits

Each task was committed atomically:

1. **Task 1: D-16 rename + NamerEncode.hlsl mirror + three staged kernels** - `5c31814` (feat)
2. **Task 2: ComputeTexturePool + NamerComputePipeline dispatch harness** - `92e0837` (feat)

**Plan metadata:** captured in the final docs commit (SUMMARY.md + STATE.md + ROADMAP.md + REQUIREMENTS.md)

## Files Created/Modified
- `Packages/com.graffitientertainment.namer/Compute/NamerEncode.hlsl` - HLSL mirror of OctahedralEncode/PackAlphaBits/PackSurface + constants
- `Packages/com.graffitientertainment.namer/Compute/NamerEncode.hlsl.meta` - GUID-stable ShaderIncludeImporter meta
- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` - three staged kernels + uniform/resources
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs` - RenderTexture lease/release + leak watchdog
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs.meta` - GUID-stable meta
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs` - upload -> dispatch -> AsyncGPUReadback harness
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs.meta` - GUID-stable meta
- `Packages/com.graffitientertainment.namer/Core/NamerFormat.cs` - D-16 parameter rename (normal -> normalTexel)

## Decisions Made
- Followed the plan's documented sRGB upload contract faithfully: `_SourceIsSrgb = inspection.BaseMapIsSrgb ? 1 : 0` with `Graphics.Blit` upload, leaving the numeric sRGB-double-decode verification to 02-03's golden tests (per plan guidance, do not assume the Blit linearization behavior)
- Kept the `_BaseColor` tint as material metadata (not baked into the normalized texture)
- Authored 2-line `.meta` files for new `.cs` files (matching the repo's existing Core/Editor convention) and a full `ShaderIncludeImporter` block for the new `.hlsl` (matching `NamerSurface.hlsl.meta`)

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Type error] Corrected an invalid 5-component float4 constructor in the CSOctahedralEncode sketch**
- **Found during:** Task 1 (three staged kernels)
- **Issue:** The plan's `<action>` sketch wrote `_Octahedral = float4(NamerOctahedralEncode(_NormalTexel.xyz), _AoIn.g, 0.0, 0.0)` — a float2 + 3 scalars = 5 components, which is not a valid HLSL `float4` constructor.
- **Fix:** Wrote `float2 oct = NamerOctahedralEncode(...); _Octahedral = float4(oct, _AoIn.g, 0.0);` (float2 + float + float = 4 components), preserving the intended layout (`.rg` = oct, `.b` = AO, `.a` = 0) that CSSurfacePack reads.
- **Files modified:** `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute`
- **Verification:** Compute shader compiled cleanly in the clone runner import (UnityShaderCompiler produced an artifact, no shader errors).
- **Committed in:** `5c31814` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (type error)
**Impact on plan:** Necessary for the compute shader to compile; no scope creep.

## Issues Encountered
- The plan's Task 2 `<verify>` references a direct `Unity -batchmode -projectPath <main>` import/compile open. Per environment constraints (interactive editor holds the main project lock), this was substituted with the validated clone runner `/tmp/namer-gsd/run-tests-in-clone.sh EditMode`, which rsyncs to a scratch clone and runs the full EditMode suite there (compile + tests in one gate). Exit 0, 40/40 passed, and the compute shader's `ComputeShaderImporter` import produced an artifact with no shader errors.

## Next Phase Readiness
- 02-03 (GPU golden tests vs Core reference + cross-platform compute smoke) can consume `NamerComputePipeline.Process` / `NamerComputeResult` / `ComputeTexturePool.LiveCount` and the three kernels directly
- The metallic/smoothness MAP path and roughness quantization are in place for the golden tests to assert byte-level equivalence
- No blockers; the sRGB upload linearization nuance is explicitly handed to 02-03 for numeric verification (per plan)

---
*Phase: 02-source-inspection-gpu-compute-pipeline*
*Completed: 2026-08-27*

## Self-Check: PASSED

- All 7 source/asset files present (NamerEncode.hlsl + meta, ComputeTexturePool.cs + meta, NamerComputePipeline.cs + meta, NAMERPack.compute)
- Task commits `5c31814` and `92e0837` present in git history
- EditMode suite green via clone runner: 40/40 passed, exit 0, compute shader imported with no shader errors

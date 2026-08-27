---
phase: 02-source-inspection-gpu-compute-pipeline
plan: 03
subsystem: testing
tags: [unity, hlsl, compute-shader, gpu, namer, editmode-tests, asyncgpureadback]

# Dependency graph
requires:
  - phase: 01-core-format-contract-runtime-decode
    provides: Core NamerFormat + NamerConstants (the CPU oracle golden tests compare against)
  - phase: 02-source-inspection-gpu-compute-pipeline
    provides: NamerComputePipeline / ComputeTexturePool / NamerEncode.hlsl / NAMERPack.compute kernels (system under test)
provides:
  - GpuGoldenTests — kernel golden tests vs Core (CSNormalize / CSOctahedralEncode / CSSurfacePack) at D-14 tolerances
  - ComputeSmokeTests — capability-gated full-pipeline smoke + RenderTexture leak watchdog (D-13/D-15)
  - Numeric verification of the sRGB upload contract (raw-copy Blit + single SRGBToLinear) for both sRGB and linear sources
affects: [03-01 (asset generation consumes the now-verified pipeline output)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Capability-gated GPU test: Assert.Ignore when !SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback (skip-with-report, D-15)"
    - "1x1 RenderTexture golden harness: RGBAHalf Texture2D -> Graphics.Blit (exact float upload, no sRGB) -> Dispatch -> AsyncGPUReadback(RGBA32).WaitForCompletion -> GetData<Color32>"
    - "GetPixel-based smoke expectation: read the actual stored 8-bit base/AO values to neutralize input quantization before the 1/255 output assertion"

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Tests/Editor/GpuGoldenTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/ComputeSmokeTests.cs
  modified: []

key-decisions:
  - "GPU golden decode-dot reference = NamerFormat.OctahedralDecode(OctahedralEncode(texel)) so the D-14 dot >= 1-1e-3 assertion stays self-consistent for all 5 golden vectors (including pathological texel D), avoiding the Phase 1 Pitfall 3 texel-direction oracle"
  - "Smoke W2 normalize expectation reads the actual stored 8-bit base/AO via GetPixel rather than the idealized float, so input quantization does not stack with the 1/255 output tolerance"
  - "No sRGB contingency applied — Graphics.Blit upload is a raw copy, so the shader's single SRGBToLinear matches the oracle; _SourceIsSrgb contract verified numerically for sRGB AND linear sources"

patterns-established:
  - "Golden-vector loop reuses BlenderGoldenVectorTests inputs so GPU and CPU oracles share test vectors"
  - "try/finally + RenderTexture.Release/DestroyImmediate cleanup, plus LiveRenderTargetCount return-to-baseline leak watchdog"

requirements-completed: [TEST-04]

# Metrics
duration: 10min
completed: 2026-08-27
---

# Phase 2 Plan 3: GPU Golden + Compute Smoke Tests Summary

**GPU golden tests prove CSNormalize/CSOctahedralEncode/CSSurfacePack mirror the Core reference (alpha byte exact, oct/AO within 1/255, decoded dot >= 1-1e-3), and a capability-gated full-pipeline smoke verifies the sRGB upload contract and the RenderTexture pool leak watchdog (D-13/D-14/D-15)**

## Performance

- **Duration:** 10 min
- **Started:** 2026-08-27T01:03:00Z
- **Completed:** 2026-08-27T01:13:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- `GpuGoldenTests` proves the three GPU kernels are a faithful mirror of `Core/NamerFormat`: `CSOctahedralEncode` oct R/G within 1/255, `CSSurfacePack` alpha byte EXACT + R/G/B within 1/255, `CSNormalize` sRGB->linear + AO un-multiply + metallic/smoothness map path within 1/255
- `ComputeSmokeTests` runs the full pipeline (upload -> normalize -> octahedral -> pack -> async readback) on Metal for scalar-only sRGB, scalar-only linear, and metallic/gloss map scenarios, with a `LiveRenderTargetCount` return-to-baseline leak watchdog
- The sRGB upload contract (`_SourceIsSrgb = BaseMapIsSrgb ? 1 : 0` + `Graphics.Blit`) was verified numerically — the Blit double-decode hazard did NOT materialize, so no contingency was needed
- Full EditMode suite green via the clone runner: 44/44 passed, exit 0 (40 pre-existing + 4 new GPU tests), confirming the compute shader compiles and dispatches correctly on Metal

## Task Commits

Each task was committed atomically:

1. **Task 1: GpuGoldenTests — kernel golden tests vs Core reference** - `3b7c53a` (test)
2. **Task 2: ComputeSmokeTests — full-pipeline smoke + leak watchdog** - `70688f8` (test)

**Plan metadata:** captured in the final docs commit (SUMMARY.md + STATE.md + ROADMAP.md + REQUIREMENTS.md)

## Files Created/Modified
- `Packages/com.graffitientertainment.namer/Tests/Editor/GpuGoldenTests.cs` - 3 `[UnityTest]` kernel golden tests (octahedral/surface-pack/normalize) with D-14 tolerance assertions and D-15 capability gate
- `Packages/com.graffitientertainment.namer/Tests/Editor/ComputeSmokeTests.cs` - `[UnityTest]` full-pipeline smoke with 3 scenarios, W2 end-to-end normalize assertion, and leak watchdog

## Decisions Made
- Decode-dot golden reference = `NamerFormat.OctahedralDecode(NamerFormat.OctahedralEncode(texel))` rather than a DirectX-inverse of the raw texel, so the D-14 `dot >= 1 - 1e-3` assertion is valid for all five golden vectors (the `(1,0,0)` texel D is a non-tangent-normal input and would otherwise fail the Phase 1 Pitfall 3 texel-direction oracle)
- Smoke W2 expectation computed from `Texture2D.GetPixel` (actual stored 8-bit value) rather than the idealized float, so the 1/255 output tolerance is not confounded by the input RGBA32 quantization
- Kept the plan's sRGB upload contract as-is after numeric verification; the `Graphics.Blit` path is a raw copy (no implicit sRGB->linear), so the compute shader's single `SRGBToLinear` is the correct decode

## Deviations from Plan

None - plan executed as written. No Rule 1-3 auto-fixes were required: both test files compiled and passed on the first run, and the sRGB double-decode contingency documented in the plan was not triggered (the raw-copy upload contract held numerically).

## Issues Encountered
- The plan's `<verify>` step references a direct `Unity -batchmode -projectPath <main>` invocation. Per environment constraints (the interactive editor holds the main project lock), this was substituted with the validated clone runner `/tmp/namer-gsd/run-tests-in-clone.sh EditMode`, which rsyncs to a scratch clone and runs the full EditMode suite (compile + test in one gate). Result: 44/44 passed, exit 0.
- The plan's smoke scenario described a "white AO map" but W2 requires verifying AO un-multiply; a uniform AO value of 0.5 was used for the normalize assertion so `cleaned = base / max(ao, eps)` is meaningfully exercised.

## Next Phase Readiness
- Phase 2 is complete: source inspection (02-01), GPU compute pipeline (02-02), and GPU-vs-CPU equivalence proof (02-03) are all committed and green
- Phase 3 (Asset Generation + Editor Workflow + Preview) can consume `NamerComputePipeline.Process` / `NamerComputeResult` (verified output formats: `R8G8B8A8_UNorm` packed surface, `R16G16B16A16_SFloat` normalized base) to write generated assets
- No blockers; TEST-04 satisfied (GPU kernels verified against Core within D-14 tolerances)

---
*Phase: 02-source-inspection-gpu-compute-pipeline*
*Completed: 2026-08-27*

## Self-Check: PASSED

- Both test files present: `GpuGoldenTests.cs`, `ComputeSmokeTests.cs`
- SUMMARY.md present: `.planning/phases/02-source-inspection-gpu-compute-pipeline/02-03-SUMMARY.md`
- Task commits `3b7c53a` and `70688f8` present in git history
- Full EditMode suite green via clone runner: 44/44 passed, exit 0 (all 4 new GPU tests ran, 0 skipped)

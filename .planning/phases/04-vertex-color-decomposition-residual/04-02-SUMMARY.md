---
phase: 04-vertex-color-decomposition-residual
plan: 02
subsystem: vertex-color-decomposition
tags: [csharp, hlsl, compute-shader, gpu, residual, vertex-color, error-metric, adaptive-resolution]

# Dependency graph
requires:
  - phase: 04-vertex-color-decomposition-residual
    plan: 01
    provides: MeshVertexSplitter (NamerSplitResult), VertexColorFitter (VertexColorFitResult.ToColor32Array)
  - phase: 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit
    provides: NamerAOPipeline compute-harness + hierarchical reduce precedent, NamerComputePipeline.RequestReadback/RawCopyMaterial
provides:
  - Compute/NAMERDecomp.compute (rasterize + quotient residual + viridis heatmap + reduce)
  - Compute/NAMERDecomp.hlsl (NAMER_VC_FLOOR + viridis NAMER_DECOMP_HEATMAP)
  - NamerConstants.VcFloor
  - NamerDecompPipeline.GenerateResidual (NamerDecompOutput + NamerDecompErrorStats)
affects:
  - 04-03 (mesh colors32 write + AssetGenerator residual EXR + debug channels)

# Tech tracking
tech-stack:
  added: []
  patterns: [GPU quotient residual, hierarchical 4-channel overflow-free reduce, adaptive downward-halving search, StructuredBuffer mesh upload]

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute
    - Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.hlsl
    - Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/ResidualPipelineTests.cs
  modified:
    - Packages/com.graffitientertainment.namer/Core/NamerConstants.cs

key-decisions:
  - "The residual is the multiplicative quotient base / max(vcInterp, VcFloor) derived from the QUANTIZED Color32 colors, with a coverage mask and base-alpha preservation (Pitfall 1/2/5)"
  - "The reduce keeps every channel in [0,1] (mean/max/mean/min) — not raw sums — so it stays overflow-free at any resolution; Coverage and AvgError are derived as fraction ratios on the CPU"
  - "Coverage needs a within-threshold count that does not fit the 4-channel reduce, so it is a second reduce over a dedicated _CoverageStat texture"

requirements-completed: [VCOL-03, VCOL-04, VCOL-05]

# Metrics
duration: 32min
completed: 2026-08-31
---

# Phase 4 Plan 02: GPU Residual Quotient + Error Stats + Adaptive Resolution Summary

**Four compute kernels (rasterize, quotient residual, viridis error heatmap, hierarchical reduce) plus a GPU harness that applies the D-13 residual-required gate and the downward-halving adaptive resolution search with a manual ladder override — the GPU side of vertex-color decomposition (VCOL-03/VCOL-04/VCOL-05).**

## Performance

- **Duration:** 32 min
- **Started:** 2026-08-31T23:03:25Z
- **Completed:** 2026-08-31
- **Tasks:** 3
- **Files created:** 8
- **Files modified:** 1

## Accomplishments

1. **`NAMERDecomp.hlsl` + `NAMERDecomp.compute`** (Task 1) — four kernels: `CSRasterizeVertexColors` (barycentric UV-space rasterize of the quantized Color32, `_VcInterp` with coverage alpha), `CSResidual` (the quotient `b / max(vc.rgb, _VcFloor)` with an uncovered-identity branch and base-alpha preservation), `CSErrorHeatmap` (one consistent mean-channel MAE mapped through the 5-anchor viridis `NAMER_DECOMP_HEATMAP`, plus the raw stat textures), and `CSReduce` (hierarchical 4-channel block reduce, overflow-free, no atomics). `NamerConstants.VcFloor = 1e-3f` mirrors `NAMER_VC_FLOOR`.

2. **`NamerDecompPipeline`** (Task 2) — the GPU harness. Uploads the split mesh + quantized colors as `ComputeBuffer`s, rasterizes, computes the quotient residual, reduces coverage/avg/max error and min base alpha via the hierarchical reduce, applies the D-13 residual-required gate (drop only when fit-only `MaxError <= threshold` AND fully opaque — Pitfall 5), and runs the downward-halving adaptive search over `{2048,1024,512,256,128}` with the manual ladder override. The residual is returned pool-leased at the chosen resolution; no disk writes.

3. **`ResidualPipelineTests`** (Task 3) — 4 GPU `[UnityTest]` cases, capability-gated: the reconstruction invariant (`residual * vcInterp ≈ base` within 2/255, vs a CPU oracle), the uncovered-texel identity residual, the D-13 gate distinguishing constant vs gradient bases, and adaptive + manual-override resolution selection.

## Verification

- Headless EditMode batchmode run: `-runTests -testPlatform EditMode -testFilter GraffitiEntertainment.Namer.Tests.ResidualPipelineTests` → **4/4 passed, exit 0** (results: `/tmp/namer-residual.xml`).
- `NAMERDecomp.compute` imports with zero shader errors (verified by the EditMode run via `ComputeShaderImporter`).
- No disk writes in this plan — the pipeline is pure in-memory (acceptance criteria met).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] CSErrorHeatmap was bound but never dispatched**
- **Found during:** Task 3 first test run
- **Issue:** `RunErrorHeatmap` only set the kernel's uniforms and textures but never called `Dispatch`, so `_ErrorStat`/`_CoverageStat` were read unwritten (all zeros). The D-13 gate saw `MinAlpha = 0` and wrongly marked every fit "residual required".
- **Fix:** `RunErrorHeatmap` now takes `w, h` and dispatches `CSErrorHeatmap` after binding.
- **Files modified:** `Editor/Decompose/NamerDecompPipeline.cs`
- **Commit:** `feacdfd`

**2. [Rule 1 - Bug] Compile errors in NamerDecompPipeline**
- **Found during:** Task 3 first compile
- **Issue:** Missing `using UnityEngine.Rendering;` (AsyncGPUReadback), a method/struct name collision (`ReduceStats`), and `Release` was `private` but called from `NamerDecompOutput.Dispose`.
- **Fix:** Added the using; renamed the method `ReduceStats` → `ReduceToStats`; made `Release` `internal`.
- **Files modified:** `Editor/Decompose/NamerDecompPipeline.cs`
- **Commit:** `feacdfd`

**3. [Rule 1 - Bug] Uncovered-texel test used a base that reconstructed within threshold**
- **Found during:** Task 3 second test run
- **Issue:** The uncovered test used base 0.5 with vertex colors 0.5, so the fit-only error (~0.002) fell within threshold and the D-13 gate dropped the residual — leaving nothing to read back.
- **Fix:** Changed the base to 0.25 so a residual is genuinely required, keeping the identity-residual + base-alpha assertions meaningful.
- **Files modified:** `Tests/Editor/ResidualPipelineTests.cs`
- **Commit:** `75f5861`

### Implementation Decisions (Claude's Discretion, within locked constraints)

- **Overflow-free reduce channels.** The plan's ".b = covered-texel count" was implemented as the *fraction* covered (a block mean in [0,1]), not a raw sum — a raw count would overflow float16 (65504) for even a 512×512 texture. AvgError = `meanErr / fractionCovered` and Coverage = `withinThresholdFraction / fractionCovered`, derived on the CPU from normalized values. This honors the plan's "copy CSAverage's overflow-free SHAPE" requirement.
- **Coverage needs a second reduce.** The within-threshold count cannot share the 4-channel reduce with mean-error/max-error/coverage/min-alpha, so a dedicated `_CoverageStat` texture (`.r` = `err <= threshold`) is reduced separately.
- **Rasterize is O(texels × triangles).** `CSRasterizeVertexColors` walks every triangle per texel exactly as the plan specifies (mirroring `NamerAOBaker.TryReconstruct`); a grid/BVH acceleration is a v2 concern for very dense meshes.
- **8-bit reduce readback.** The reduced stats are read back as RGBA32 (matching the existing `ReadBackLumaAverage` pattern); ~1/255 precision is sufficient for the threshold/gate decisions and the tests' tolerances.

## Known Stubs

None — the kernels, harness, and tests are fully functional and verified.

## Threat Flags

None — the plan's `<threat_model>` mitigations are implemented in-shader: the `_VcFloor` divisor floor (T-04-04), the barycentric area/inside epsilon guards (T-04-05), and the alpha-opaque residual-required gate (T-04-06). No new network, auth, file-access, or schema surface.

## Self-Check: PASSED

All 8 created files exist; the 4 task commits (`e222a9c`, `7911d07`, `feacdfd`, `75f5861`) are present in git history; the 4 GPU tests pass headless.

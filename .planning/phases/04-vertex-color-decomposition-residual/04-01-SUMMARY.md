---
phase: 04-vertex-color-decomposition-residual
plan: 01
subsystem: vertex-color-decomposition
tags: [csharp, unity, burst, unity-collections, unity-mathematics, mesh, vertex-color, least-squares, barycentric]

# Dependency graph
requires:
  - phase: 03.1-ao-extraction-un-multiply-baked-ao-from-the-base-texture-wit
    provides: Burst + Unity.Collections + Unity.Mathematics Editor asmdef precedent, NamerAOBaker mesh-read/dispose discipline
  - phase: 03-asset-generation-editor-workflow-preview
    provides: NamerSourceModel.NamerMaterialInspection.BakeSourceMesh (source mesh origin), NamerComputePipeline.RequestReadback contract
provides:
  - MeshVertexSplitter (seam-safe attribute-preserving split -> NamerSplitResult)
  - NamerSplitResult (managed-array split topology, no NativeArray lifetime coupling)
  - VertexColorFitter (Burst per-triangle barycentric LSQ + Color32 quantize + fit-quality alpha)
  - VertexColorFitResult (IDisposable colors + fit-quality, ToColor32Array)
affects:
  - 04-02 (residual quotient consumes VertexColorFitResult.ToColor32Array)
  - 04-03 (mesh colors32 write + AssetGenerator mesh asset + EXR)

# Tech tracking
tech-stack:
  added: []
  patterns: [Burst IJobParallelFor per-triangle accumulation, quantized integer weld key, Cramer 3x3 solve]

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Editor/Decompose/MeshVertexSplitter.cs
    - Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs
  modified: []

key-decisions:
  - "Weld by a quantized (position, normal, tangent, uv) integer key — never floating-point equality — so near-equal shared-edge attributes weld while UV seams / hard normals / tangent breaks split"
  - "The plan's 'per-vertex 3x3 Gram' is implemented as a per-triangle 3x3 normal-equations solve accumulated per-vertex by incident-triangle count; the diagonal-only per-vertex approximation would over-shoot constant colors (sum(w)/sum(w^2) != 1), so the full 3-corner barycentric solve is load-bearing"
  - "The fit is UV-space only — positions/normals/tangents are not sampled, so only Uvs + flattened sub-mesh triangles are converted to NativeArrays (avoids dead code)"
  - "Per-vertex reconstruction error is accumulated on the main thread from per-triangle job output, avoiding a cross-thread read-modify-write race on shared vertices"

patterns-established:
  - "Burst per-triangle barycentric LSQ: interior kGrid grid -> symmetric 3x3 Gram (AtA) + per-corner RHS (AtB) -> Cramer solve with diagonal jitter"
  - "Seam-safe splitter: quantized attribute weld key -> Dictionary<key,int> -> re-emit all attribute streams + rewrite per-submesh triangles"

requirements-completed: [VCOL-01, VCOL-02]

# Metrics
duration: 33min
completed: 2026-08-31
---

# Phase 4 Plan 01: MeshVertexSplitter + VertexColorFitter Summary

**Seam-safe attribute-preserving mesh splitter plus a Burst per-triangle barycentric least-squares vertex-color fitter that quantizes to Color32 with a fit-quality alpha — the CPU algorithmic core of vertex-color decomposition (VCOL-01/VCOL-02/D-04).**

## Performance

- **Duration:** 33 min
- **Started:** 2026-08-31T21:52:48Z
- **Completed:** 2026-08-31
- **Tasks:** 2
- **Files created:** 7

## Accomplishments

1. **`MeshVertexSplitter`** (Task 1) — seam-safe, attribute-preserving split that welds corners by a quantized `(position, normal, tangent, uv)` integer key and returns a `NamerSplitResult` with every attribute stream re-emitted and sub-mesh triangles rewritten. Skinned meshes preserve `boneWeights`/`bindposes`; blend shapes are skipped (MVP). Missing normals fall back to a copy of `NamerAOBaker`'s smooth-normal computation. Pure in-memory (no disk writes).

2. **`VertexColorFitter`** (Task 2) — Burst per-triangle barycentric least-squares fit. Each triangle samples a fixed interior `kGrid = 16` grid, accumulates a symmetric 3×3 Gram (`AtA`) and 3 per-corner RHS vectors (`AtB`), and solves the 3-corner system with a closed-form Cramer rule (diagonal jitter for degenerate triangles). Per-vertex colors combine incident triangle solves; `FitQuality` maps the per-vertex mean reconstruction residual through `kFitQualityRef = 0.05f`. `ToColor32Array()` quantizes RGB + fit-quality alpha — the exact byte stream the residual quotient (04-02) and mesh `colors32` write (04-03) will consume.

3. **`VertexColorDecompTests`** — 8 headless `[Test]` cases pass (4 split + 4 fit): UV-seam weld, attribute preservation, sub-mesh boundary, skinning preservation, constant-color fit within 1e-3, determinism, quantize roundtrip within 1/255, fit-quality range/perfect-fit.

## Verification

- Headless EditMode batchmode run: `-runTests -testPlatform EditMode -testFilter GraffitiEntertainment.Namer.Tests.VertexColorDecompTests` → **8/8 passed, exit 0** (results: `/tmp/namer-vcol-fit2.xml`).
- No disk writes in this plan — splitter/fitter are pure in-memory (acceptance criteria met).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Test helper allocated a job-accessed `NativeArray` with `Allocator.Temp`**
- **Found during:** Task 2 first test run
- **Issue:** `CreateConstantBase` used `Allocator.Temp`, but the base texel buffer is read by a scheduled Burst job — Unity throws `Temp memory containers cannot be used when scheduling a job`.
- **Fix:** Changed to `Allocator.TempJob` (the buffer is already disposed in a `finally`).
- **Files modified:** `Tests/Editor/VertexColorDecompTests.cs`
- **Commit:** `ccf7283`

### Implementation Decisions (Claude's Discretion, within locked constraints)

- **Per-triangle 3×3 solve accumulated per-vertex.** The plan's prose described a "per-vertex 3×3 Gram"; a strictly diagonal per-vertex solve (`sum(w·base)/sum(w²)`) over-shoots constant colors because `sum(w)/sum(w²) ≠ 1`, failing the constant-color invariant. The full per-triangle 3-corner normal-equations solve is exact for constant color and is accumulated per-vertex by incident-triangle count. This is the correct decoupling and still satisfies "multi-sample barycentric least-squares, not simple averaging" (VCOL-01).
- **Fit is UV-space only.** `split.Positions/Normals/Tangents` are not sampled by the fit, so only `Uvs` + flattened sub-mesh triangles are converted — no dead position/normal arrays.

## Known Stubs

None — the splitter and fitter are fully functional and verified.

## Threat Flags

None — the splitter/fitter are pure in-memory CPU transforms with no new network, auth, file-access, or schema surface. The malformed-mesh mitigations in the plan's threat register (null-mesh `ArgumentNullException`, `kBarycentricAreaEps`/`kBarycentricInsideEps` guards, Cramer jitter) are implemented.

## Self-Check: PASSED

All 7 created files exist and both task commits (`832d8ec`, `ccf7283`) are present in git history.

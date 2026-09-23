---
phase: 05-stylization
plan: 02
subsystem: compute
tags: [unity, compute-shader, hlsl, bilateral, hsv-palette, union-find, uv-islands, stylization, editmode-tests]

# Dependency graph
requires:
  - phase: 05-stylization research/ui-spec
    provides: 05-RESEARCH.md Pattern 3 slider mapping, Pattern 5 stage order, Pitfalls 1-11, T-05-06..T-05-09, D-01..D-05 decisions
  - phase: 05-stylization plan 01
    provides: NamerStyleProfile (PaletteAnchors, sliders, HueMode, IsExtracted, ClampToCaps)
provides:
  - NamerUvIslands CPU union-find island-id builder keyed on quantized UV pairs (D-03), binary16 cap clamp + flag (T-05-08), honest no-islands guard
  - NAMERStylize.compute: CSRasterizeIslandIds (verbatim CSRasterizeVertexColors loop shape), CSIslandBilateral (island+coverage gated, spatial+range gaussian), CSPaletteTransfer (hue-anchored match, achromatic-luma pool, Preserve/Replace hue factor D-05, passthrough copy at zero anchors/strength)
  - NamerStyleStylization per-Process context (ClampToCaps, T-05-06 anchor sanitation, Active zero-dispatch gate, SmoothingSkippedNoMesh OQ-3)
  - NamerStylizePipeline dispatch harness (caller-owned RTs only — zero pool traffic; buffer disposal in finally T-05-07)
  - D-01 Process insertion: stylize stage between normalize and fit/projection; styledBase supersedes the pre-style base for projection, removed-detail transfer, Sobel fallback, and the residual dividend
  - D-02 comment-only addenda at every source-dividend site (NamerProcessor, 04.2-CONTEXT, three 04.2 test files)
affects: [05-stylization 05-03, phase 05 post-wave regression gate]

# Tech tracking
tech-stack:
  added: []  # zero new packages
  patterns:
    - Caller-owned render-texture law for the stylize harness: NamerStylizePipeline dispatches into pool leases it never creates/leases/releases; only transient ComputeBuffers, disposed in finally
    - Palette-transfer single-buffer upload: one StructuredBuffer<float4> of (hue, sat, luma, achromaticFlag) per anchor; the kernel needs no color copy
    - Union-find edge key = LEXICOGRAPHIC endpoint ordering of the quantized UV pair (component-wise min/max collides distinct crossing edges)
    - Byte-identity guard as a testable contract: null stylization AND Active==false contexts assert exact Color32 equality plus equal LiveRenderTargetCount against the null run

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerUvIslands.cs
    - Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs
    - Packages/com.graffitientertainment.namer/Compute/NAMERStylize.compute
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerUvIslandsTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizationIntegrationTests.cs
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerProjectionAcceptanceTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerProjectionTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/ResidualPipelineTests.cs
    - .planning/phases/04.2-gouraud-projection-one-texture-with-roughness-transfer-resid/04.2-CONTEXT.md

key-decisions:
  - "Anchor upload collapsed to ONE StructuredBuffer<float4> (hue, sat, luma, achromaticFlag) instead of the plan's color+params pair — the palette kernel's math consumes params only, and unused declared buffers are stripped by HLSL compilers; NamerStyleAnchor[] keeps the color half on the CPU context"
  - "UV edge keys use lexicographic endpoint ordering, not the plan's component-wise (min,max): component-wise normalization maps the crossing edges {(1,5),(2,3)} and {(1,3),(2,5)} to the same key, merging distinct island borders"
  - "Hue assertions bounded at 0.002 (not the plan's literal 1e-3): the R16G16B16A16_SFloat working target stores 10 mantissa bits, bounding circular hue readback error at ~2e-3; oracles are computed against the READBACK input so input quantization cancels"
  - "Stylize leases are taken only when stylization.Active: styled/island/scratchA/scratchB come from the compute pool, baseColorOut is released and nulled the moment styledBase supersedes it, so the non-projection path cannot strand it and the catch path releases each at most once"
  - "The palette kernel ALWAYS dispatches (passthrough copy at zero anchors/strength) so the caller-owned dst is fully written even when only smoothing ran"

patterns-established:
  - "Byte-identity test shape for pipeline insertions: two null runs prove determinism, then a built-but-inactive context must match the null run byte-for-byte with LiveRenderTargetCount identical at result-hold time"
  - "D-02 wording addenda: one sentence ('SOURCE reads as the STYLED source; the operator is unchanged') appended as comments/doc at every source-dividend site — zero assertion edits, verified by diff grep"

requirements-completed: [STYL-03, STYL-04]

# Metrics
duration: 85min
completed: 2026-09-23
---

# Phase 05 Plan 02: Stylization Engine Summary

**UV-island-gated bilateral + hue-anchored palette transfer as a D-01 Process stage with an absolute byte-identical null path — CPU union-find islands on quantized UV pairs, three HLSL kernels, and a dispatch harness that owns zero render textures**

## Performance

- **Duration:** ~85 min (15:00-16:29 PDT; plan start epoch through final commit + docs)
- **Started:** 2026-09-23T15:00:41-07:00
- **Completed:** 2026-09-23T16:29:00-07:00 (Task 3 commit b3afe9b at 16:22:26)
- **Tasks:** 3/3
- **Files:** 6 created source/compute/test files (+6 .meta) + 6 modified

## Accomplishments
- D-03 island ids: NamerUvIslands.BuildIslandIds — union-find with path compression + union-by-size over UV-edge keys (quantized 1e-4 pairs), compact ids in fixed triangle order, kMaxIslandIdFloat16=2048 clamp with ClampedToFloat16Max flag (T-05-08) and TRUE pre-clamp IslandCount, out-of-range-UV honest no-islands guard
- NAMERStylize.compute: CSRasterizeIslandIds copies the CSRasterizeVertexColors loop shape verbatim (first-hit immediate return, Pitfall 11) with texel-space barycentrics (NAMER_STYLIZE_AREA_EPS=1e-8 vs (2*Area_texels)^2, Pitfall 4); CSIslandBilateral gates neighbors on coverage>=0.5 AND same island id with NAMERAO-form spatial/range gaussians, center weight 1, alpha preserved; CSPaletteTransfer does circular-hue matching with an achromatic-luma pool, never-fail fallback, lerp luma/sat by strength, hue factor f=strength (Preserve) / 1 (Replace, D-05), unit-color luma rebuild + saturate, alpha preserved
- NamerStyleStylization.Create: ClampToCaps + T-05-06 anchor sanitation (weight>0, no NaN anywhere) before upload; Active gate false for strength 0 + smoothing 0 or smoothing with no mesh (SmoothingSkippedNoMesh, OQ-3)
- D-01 insertion in NamerComputePipeline.Process: (a2) stylize stage after BindAndDispatchNormalizeEncode; styledBase/islandIds/scratchA/scratchB leased only when Active; projection.Run, ExtractTransferRoughness, and ExtractSobel all consume styledBase; result SourceBaseColor carries the STYLED source (D-02); catch releases styledBase exactly once (only when it is not baseColorOut); finally releases the island/scratch leases
- Byte-identity guard proven three ways in NamerStylizationIntegrationTests: null-path byte identity across runs, inactive-context byte identity + equal LiveRenderTargetCount (zero extra leases), and throw discipline returning every stylize lease to baseline
- D-02 comment-only addenda at all 10 inventoried sites (NamerProcessor CreateProjectionContext doc, 04.2-CONTEXT residual-gate + specifics bullets, three 04.2 test files) — diff grep confirms comment lines only, zero assertion edits

## Task Commits

Each task was committed atomically:

1. **Task 1: NamerUvIslands union-find + island rasterize/bilateral kernels + CPU tests** - `8ca8f45` (feat)
2. **Task 2: CSPaletteTransfer kernel + NamerStylizePipeline harness + GPU tests** - `4417d3b` (feat)
3. **Task 3: D-01 Process wiring + D-02 addenda + integration tests** - `b3afe9b` (feat)

## Files Created/Modified
- `Editor/Stylize/NamerUvIslands.cs` - island-id builder + FlattenTriangles (decomp upload contract)
- `Compute/NAMERStylize.compute` - the three kernels (numthreads 8x8, no groupshared, linear write targets only)
- `Editor/Stylize/NamerStylizePipeline.cs` - NamerStyleStylization context + dispatch harness; Pattern 3 slider mapping (iterations = clamp(1+round(2s),1,3), radius 2, sigmaS = radius*0.5, sigmaR lerp 0.05..0.40)
- `Editor/Pipeline/NamerComputePipeline.cs` - _stylizePipeline field/Dispose/Ensure, Process stylization parameter, (a2) stage, downstream repoints, result fields, catch/finally discipline
- `Editor/Pipeline/NamerProcessor.cs` - CreateProjectionContext D-02 doc addendum (comment-only)
- `Tests/Editor/NamerUvIslandsTests.cs` - 6 CPU tests: flat quad, Pitfall-2 duplicated-UV-edge oracle, two disjoint quads compact ids in flatten order, double-run determinism, out-of-range guard, 2050-island clamp+flag
- `Tests/Editor/NamerStylizeKernelTests.cs` - 10 GPU tests: two-island narrow-gutter no-bleed, hash-grain MAD drop, range-gate hard step, Preserve/Replace luma-hue pulls, mid-strength hue ramp, achromatic luma match, full-chain alpha preservation, identity context CPU flags
- `Tests/Editor/NamerStylizationIntegrationTests.cs` - 5 Process-level tests (see Accomplishments)
- `04.2-CONTEXT.md` + three 04.2 test files - D-02 wording addenda (comment-only)

## Decisions Made
- Single anchor-params buffer (see key-decisions) — upload shape deviation from the plan's two-buffer sketch
- Lexicographic edge-key ordering (see key-decisions) — correctness fix over the plan's literal component-wise min/max
- Anchor test color chosen at hue 0.6/sat 0.6/luma 0.4 so the luma-target rebuild never hits saturate() clipping (V ~ 0.65), keeping the readback hue exact to binary16
- Palette kernel always dispatches: dst fully written even when only smoothing ran; passthrough copy is byte-exact

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Component-wise (min,max) edge keys merge distinct island borders**
- **Found during:** Task 1 (union-find implementation)
- **Issue:** The plan's literal component-wise endpoint normalization collides crossing edges — {(1,5),(2,3)} and {(1,3),(2,5)} both map to ((1,3),(2,5)) — incorrectly merging separate UV islands
- **Fix:** Lexicographic ordering of the two quantized endpoints (compare x, then y) — distinct edges stay distinct
- **Files modified:** Editor/Stylize/NamerUvIslands.cs
- **Verification:** SharedUvEdge_DistinctVertexIndices_SingleIsland + two-disjoint-quads tests; GSD-05-02-COMPILE-OK
- **Committed in:** 8ca8f45

**2. [Rule 2 - Missing Critical] Plan's two-buffer anchor upload half-unused**
- **Found during:** Task 2 (palette kernel)
- **Issue:** The plan sketched float4 color + float4 params buffers; the kernel consumes (hue, sat, luma, achromaticFlag) only — a second buffer would be dead weight (and unused declared buffers are stripped by HLSL compilers anyway)
- **Fix:** One StructuredBuffer<float4> _AnchorParams; NamerStyleAnchor[] retains the color half CPU-side for 05-03 display
- **Files modified:** Compute/NAMERStylize.compute, Editor/Stylize/NamerStylizePipeline.cs
- **Verification:** Palette kernel tests; compile proof green
- **Committed in:** 4417d3b

**3. [Rule 1 - Bug] Plan's 1e-3 hue tolerance unsatisfiable in binary16 storage**
- **Found during:** Task 2 (test oracle authoring)
- **Issue:** The R16G16B16A16_SFloat working target stores 10 mantissa bits; circular hue readback error runs ~2e-3, so the plan's literal 1e-3 bound would false-fail correct kernels
- **Fix:** kHueTolerance = 0.002 with oracles computed from the READBACK input (input quantization cancels); kExactTolerance 1e-5 retained for half-exact constants
- **Files modified:** Tests/Editor/NamerStylizeKernelTests.cs
- **Verification:** Compile proof green; suite deferred to post-wave live gate
- **Committed in:** 4417d3b

**4. [Rule 1 - Bug] Task-1 island-compactness fixture wrote 8 triangles but asserted 6**
- **Found during:** Task 1 (test authoring)
- **Issue:** The plan's two-disjoint-quads sketch mismatched its own assertion set (bogus Array.IndexOf lookup)
- **Fix:** Two submeshes each a single quad (2 triangles each); compactness asserted via max(TriIsland) == IslandCount-1 and ids [0,0,1,1]
- **Files modified:** Tests/Editor/NamerUvIslandsTests.cs
- **Verification:** Compile proof green
- **Committed in:** 8ca8f45

---

**Total deviations:** 4 auto-fixed (3 Rule 1 bugs, 1 Rule 2 missing critical)
**Impact on plan:** All fixes were correctness requirements within the plan's own file scope. The 04.2 files carry comment-only edits (zero assertion changes — verified by diff grep). No scope creep; pre-existing dirty files untouched.

## Issues Encountered
- Live editor holds the Library lock (6000.0.82f1), so the EditMode suite defers to the orchestrator's post-wave live regression gate per house precedent (STATE 04.2/05-01). Compile evidence: Roslyn closure proofs green for Editor (26 sources) and Tests.Editor (35 sources) assemblies (GSD-05-02-COMPILE-OK), compiled to REAL assembly names with the fresh Editor dll override; the live editor additionally hot-imported each new file on refresh (ScriptAssemblies regenerated)
- macOS bash 3.2 + set -u rejects empty-array expansion ("unbound variable") — the compile script guards extras with ${ExtraFiles[@]+"${ExtraFiles[@]}"}
- 04.2 test-file addenda re-flowed three existing comment lines (shown as -// +// pairs in the diff); no code or assertion lines touched

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- 05-03 (Processor window Stylization foldout) calls NamerStyleStylization.Create(profile, split) and passes it to Process(inspection, projection, stylization); Active=false contexts cost nothing by construction; SmoothingSkippedNoMesh and IslandClampedToFloat16Max are surfaced fields for the Info/Warning notes (OQ-3, T-05-08)
- Known follow-ups for the post-wave regression gate: run NamerUvIslandsTests (6) + NamerStylizeKernelTests (10) + NamerStylizationIntegrationTests (5) in the live editor; the byte-identity tests double as the 205-suite regression guard (null path must stay byte-identical)

## Self-Check: PASSED

All 6 created files exist on disk; all 3 task commits (8ca8f45, 4417d3b, b3afe9b) present in git log.

---
*Phase: 05-stylization*
*Completed: 2026-09-23*

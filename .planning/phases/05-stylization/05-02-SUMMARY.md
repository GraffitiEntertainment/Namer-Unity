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

**5. [Rule 2 - Missing Critical] Unity 6 / Metal silently skips dispatches with unbound kernel properties (post-wave gate, 3 fix rounds)**
- **Found during:** Post-wave live gate round 1 (2026-09-23) — Bilateral trio failed with dst = fresh-RT zeros
- **Issue:** Unity 6 on Metal SKIPS a dispatch when ANY property a dispatched kernel references is unbound — constants ("requires a Compute ConstantBuffer at index 1 ... Skipping dispatch to avoid crashing", Editor.log) and StructuredBuffers ("Property (_AnchorParams) at kernel index (2) is not set") alike. NamerStylizePipeline.Run bound _StyleStrength/_HueReplaceMode and the _AnchorParams buffer only in the has-anchors branch, so the ALWAYS-DISPATCHED palette kernel's passthrough copy (the write that fills the caller-owned dst on the smoothing-only path) never executed. The bilateral/rasterize kernels ran correctly throughout — only the final copy was skipped, which is why the full-chain alpha test passed while the three no-anchor smoothing tests read zeros. Found in two rounds because the constant-buffer skip and the buffer skip log differently; the second round proved the rule is per-property over the whole argument table
- **Fix (rounds 1+2):** Every argument of CSPaletteTransfer is set UNCONDITIONALLY — _AnchorCount/_StyleStrength/_HueReplaceMode scalars plus the _AnchorParams StructuredBuffer on every dispatch; only the anchor DATA is conditional, and the no-anchor buffer carries one dummy record (count 0 → 1; zero-length ComputeBuffers are rejected on some backends; the kernel provably never reads it — _AnchorCount == 0 returns before the first access). Full argument-table sweep of all three kernels: rasterize and bilateral bind every property immediately before their only dispatch sites
- **Files modified:** Editor/Stylize/NamerStylizePipeline.cs
- **Verification:** Targeted NamerStylizeKernelTests 10/10; full EditMode suite 226/226 twice, identical (wave-2 gate)
- **Committed in:** 3656d07

**6. [Rule 1 - Bug] Ramp-test fixture clipped in the HSV rebuild (post-wave gate round 1)**
- **Found during:** Post-wave live gate — Palette_PreserveMidStrength_HueRampedHalfwayToAnchor off by exactly 0.00833
- **Issue:** With the (0.6, 0.6, 0.4) anchor at strength 0.5, the midpoint hue 0.84167 carries sat 0.8 / luma 0.485, demanding V = 0.485/0.425 = 1.14; saturate() clipped R and B and bent the read-back hue by exactly 1/120 (the reported 0.00833338499 to five digits). The kernel's Preserve ramp (f = strength) was correct — the pre-rebuild outHue matched the oracle exactly
- **Fix:** Anchor saturation 0.6 → 0.2 (V ≈ 0.85 < 1, no clipping; anchor stays chromatic at 0.2 ≥ the 0.15 cutoff so the hue-match path is unchanged). Assertions untouched
- **Files modified:** Tests/Editor/NamerStylizeKernelTests.cs
- **Verification:** Targeted class 10/10
- **Committed in:** 3656d07

**7. [Rule 1 - Bug] Median-preservation oracle asserted a property the filter does not guarantee (post-wave gate round 3)**
- **Found during:** Post-wave live gate — grain-strip median drift 0.00146484375 vs a 0.001 bound
- **Issue:** The observed drift is EXACTLY 3 × 2^-11 — three binary16 steps, one ulp of store-rounding per bilateral pass. Repeated averaging pulls a skewed sample's median toward its mean; the island bilateral (symmetric spatial/range weights) preserves the strip MEAN, not the median. The old assert tested fixture statistics, not filter behavior
- **Fix:** Replaced (not loosened) with mean preservation, kMeanPreservationTolerance = 0.002 (~4 binary16 steps at luma ~0.5, covering 3 intermediate stores + final store plus the second-order strip-boundary exchange); MAD now centered on the mean; variance-drop assert unchanged
- **Files modified:** Tests/Editor/NamerStylizeKernelTests.cs
- **Verification:** Targeted class 10/10; full suite 226/226 ×2 identical
- **Committed in:** 3656d07

---

**Total deviations:** 7 (4 auto-fixed during execution, 3 gate findings — all Rule 1/2, all within plan file scope)
**Impact on plan:** All fixes were correctness requirements within the plan's own file scope. The 04.2 files carry comment-only edits (zero assertion changes — verified by diff grep). No scope creep; pre-existing dirty files untouched. The Metal argument-table rule is a durable project lesson: every property referenced by a dispatched kernel must be bound on every dispatch of that kernel, regardless of branch.

## Issues Encountered
- Live editor holds the Library lock (6000.0.82f1), so the EditMode suite defers to the orchestrator's post-wave live regression gate per house precedent (STATE 04.2/05-01). Compile evidence: Roslyn closure proofs green for Editor (26 sources) and Tests.Editor (35 sources) assemblies (GSD-05-02-COMPILE-OK), compiled to REAL assembly names with the fresh Editor dll override; the live editor additionally hot-imported each new file on refresh (ScriptAssemblies regenerated). GATE RESOLVED: wave-2 live run green — targeted NamerStylizeKernelTests 10/10, full EditMode suite 226/226 pass twice with identical results (determinism bar met)
- macOS bash 3.2 + set -u rejects empty-array expansion ("unbound variable") — the compile script guards extras with ${ExtraFiles[@]+"${ExtraFiles[@]}"}
- 04.2 test-file addenda re-flowed three existing comment lines (shown as -// +// pairs in the diff); no code or assertion lines touched
- Metal shader warnings on NamerStylize ("potentially uninitialized variable" for NamerStylizeHue/NamerStylizeHsvUnit) are conservative flow-analysis notes on static helpers whose every path returns — benign, no action

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- 05-03 (Processor window Stylization foldout) calls NamerStyleStylization.Create(profile, split) and passes it to Process(inspection, projection, stylization); Active=false contexts cost nothing by construction; SmoothingSkippedNoMesh and IslandClampedToFloat16Max are surfaced fields for the Info/Warning notes (OQ-3, T-05-08)
- Known follow-ups for the post-wave regression gate: run NamerUvIslandsTests (6) + NamerStylizeKernelTests (10) + NamerStylizationIntegrationTests (5) in the live editor; the byte-identity tests double as the 205-suite regression guard (null path must stay byte-identical)

## Self-Check: PASSED

All 6 created files exist on disk; all 3 task commits (8ca8f45, 4417d3b, b3afe9b) present in git log. Post-wave gate fix commit 3656d07 present; both files it touched exist on disk.

---
*Phase: 05-stylization*
*Completed: 2026-09-23*

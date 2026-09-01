---
phase: 04-vertex-color-decomposition-residual
reviewed: 2026-09-01T00:34:54Z
depth: standard
files_reviewed: 18
files_reviewed_list:
  - Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute
  - Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.hlsl
  - Packages/com.graffitientertainment.namer/Core/NamerConstants.cs
  - Packages/com.graffitientertainment.namer/Editor/Decompose/MeshVertexSplitter.cs
  - Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs
  - Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs
  - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
  - Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs
  - Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/ResidualPipelineTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs
findings:
  critical: 3
  warning: 5
  info: 7
  total: 15
status: issues_found
---

# Phase 4: Code Review Report

**Reviewed:** 2026-09-01T00:34:54Z
**Depth:** standard
**Files Reviewed:** 18 (17 listed + `ComputeTexturePool.cs` read as a cross-file dependency of the decomp pipeline's pooling contract)
**Status:** issues_found

## Summary

The CPU/Burst fitter, the seam-safe splitter, and the GPU residual kernels are individually solid — the quotient math, the VcFloor guard, the identity branch on uncovered texels, the fit-only vs. full-residual error metric, and the pool lease/release discipline in `NamerDecompPipeline.GenerateResidual` are all correct and well matched by the golden tests. The three critical findings are all at the seams between those correct parts:

1. **CR-01** — the processor resolves ONE mesh for the entire selection and fits every material's vertex colors against it, then the bind step's mesh dictionary silently overwrites per-material entries. Any multi-material mesh or multi-mesh selection renders with the wrong vertex colors while the tool reports success.
2. **CR-02** — the residual EXR is imported Point-filtered, but the whole adaptive-resolution design (and the error metric that justifies dropping resolution) assumes bilinear runtime sampling. Saved materials go blocky exactly when the adaptive search succeeds.
3. **CR-03** — UVs outside [0,1] (tiling layouts) rasterize to zero coverage, which the D-13 gate interprets as a perfect fit and silently drops the residual, while the CPU fitter clamps instead of wrapping and fits against wrong texels.

Warnings cover an exception-type escape from the documented "returns error, never throws" contract (empty meshes), degenerate zero tangents on generated split meshes, non-square-source handling in the resolution ladder, Gamma-project color-space mixing in the new heatmap channel, and stale render-target bindings after a failed preview recompute.

## Critical Issues

### CR-01: Decomposition pairs every material with a single selection-wide mesh, and the mesh-swap dictionary overwrites per-material entries

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:105,138-152,242-264`
**Issue:** `ResolveSourceMesh(selection)` (line 105) returns the FIRST mesh found (`FindMeshSubAsset` / `FindMeshInObject` return on the first hit). That one mesh is then used inside the per-material loop (lines 138-152) to split and fit vertex colors for **every** material in `model.Materials`. Two failure modes:

- **Multi-material, single mesh** (one mesh, 2+ material slots — very common): each material `i` gets its own split-mesh `.asset` whose vertex colors are fit against material `i`'s base texture, and each material's residual is computed as `base_i / vcInterp_i`. But `BindGeneratedMaterials` (lines 254-263) keys the generated meshes by the SHARED source mesh instance ID in a plain dictionary assignment — `generatedMeshBySourceMeshId[sourceMesh.GetInstanceID()] = generatedMesh;` — so the last material's mesh wins. The renderer swaps to that mesh, and every other slot now renders `residual_i * vc_fit_last ≠ base_i`. Slot 0's reconstruction is wrong wherever the two fits differ.
- **Multi-mesh selection** (two renderers, different meshes, different materials): material B is fit against mesh M1's UV layout — garbage vertex colors for renderer 2 — and renderer 1 is swapped to B's mesh by the same dictionary overwrite.

`SourceInspector.Inspect` explicitly supports multiple unique materials per selection, so this is a first-class path, and it fails silently (Process reports success). The integration tests only cover the single-material, single-mesh case, which is why 98/98 stays green.
**Fix:** Resolve the mesh per material (per renderer/sub-mesh slot) rather than once per selection — e.g. have `CollectMaterials`/`NamerMaterialInspection` carry the owning renderer's `sharedMesh` (the `BakeSourceMesh` field already exists for exactly this shape), skip decomposition with a warning for materials whose owning mesh can't be resolved, and for the shared-mesh multi-slot case either (a) keep the mesh keyed per material and only swap when the selection has exactly one material, or (b) reject decomposition with a blocking warning when `model.Materials.Count > 1` maps to the same mesh, since one vertex-color stream cannot represent N materials' fits simultaneously.

### CR-02: Residual EXR is imported with Point filtering, contradicting the bilinear sampling the resolution search is built on

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:437`
**Issue:** `WriteResidualExr` sets `importer.filterMode = FilterMode.Point;` (copied from the packed-surface path, where Point is mandatory because bit-packed alpha cannot survive interpolation). But the residual is a plain float color map that the pipeline deliberately produces at a REDUCED resolution: `NamerDecompOutput` documents "produced at the chosen resolution (not upsampled back to source) — the runtime material samples it with hardware bilinear filtering" (`NamerDecompPipeline.cs:33-36`), and the adaptive search (`ChooseResolution` / step 7) measures error after a bilinear `Resample` up to source size — i.e. the reported `MaxError` and the D-16 halving decision are both computed under bilinear assumptions. `NamerSurface.hlsl` uses `SAMPLER(sampler_BaseResidualMap)`, so the runtime sampler comes from the import settings: Point. Consequences: saved materials render blocky whenever `ChosenResolution < sourceWidth` (the feature's headline case), the stats block understates the actual runtime error, and the live preview (which binds the pool RT, default Bilinear) does not match the saved result.
**Fix:**
```csharp
// WriteResidualExr — the residual is a plain float map sampled with hardware
// bilinear at runtime (NamerDecompOutput contract); Point is only for the
// bit-packed surface.
importer.filterMode = FilterMode.Bilinear;
```

### CR-03: UVs outside [0,1] produce zero coverage, which the D-13 gate reads as a perfect fit and silently drops the residual

**File:** `Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute:76-116`; `Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs:433-436`; `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:209-215`
**Issue:** `CSRasterizeVertexColors` tests each texel center `uv = (id + 0.5)/size ∈ [0,1)` against each triangle's raw UV coordinates — there is no wrap handling. A tiling UV layout (any triangle whose UVs live in `[1,2]`, `[0,4]`, etc. — standard for environment/architecture assets) covers no texels at all. Uncovered texels write `_ErrorStat = (0,0,0,1)` (compute line 160), so `fitStats.MaxError` is exactly 0 and `ReduceStats.MinAlpha` is the opaque base alpha — the gate at `NamerDecompPipeline.cs:210-215` concludes "fit within threshold on an opaque base" and returns `ResidualRequired = false`. Meanwhile the CPU fitter's `SampleBase` CLAMPS out-of-range UVs to the texture edge (`u = clamp(u, 0, BaseWidth - 1)`, lines 435-436) instead of wrapping, so the vertex colors written into the mesh are fit against clamped edge texels. Net result for a tiling-UV mesh: garbage vertex colors, no residual, material binds nothing at `_BaseResidualMap`, Process reports success. Note the same zero-coverage blind spot applies to any fully-unrasterizable UV set (all-degenerate UVs), not just tiling.
**Fix:** (1) Make the CPU sampler wrap (`u = uv.x * BaseWidth - 0.5f; u -= floor(u);` style, or `Repeat` via `u - BaseWidth * floor(u / BaseWidth)`) so the fit at least matches what a Repeat-wrap runtime sample sees; (2) in `GenerateResidual`, treat near-zero coverage (e.g. `fitStats.CoverageFraction < 0.5f` — the `.b` channel already carries it) as "cannot decompose": keep the Phase-3 shape and surface a warning instead of running the drop-residual gate, since a fit that covers no texels has not been validated. Optionally wrap triangle UVs into [0,1) per-texel in the rasterizer (`frac` of the barycentric point against each triangle's wrapped bounds), but the coverage guard alone prevents the silent wrong output.

## Warnings

### WR-01: Empty/no-triangle mesh throws ArgumentException through Process's documented "returns error, never throws" contract; the context-menu path crashes

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs:126-129`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:184,195`; `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:101-105`
**Issue:** A mesh with no vertices, or with vertices but no triangles (the splitter's weld map is populated only from triangle indices, so such a mesh yields `VertexCount == 0`), makes `VertexColorFitter.Fit` throw `ArgumentException("Split result has no vertices to fit.")`. `NamerProcessor.Process` catches only `InvalidOperationException` (its XML contract says errors are returned, never thrown), so the exception propagates out of `Process`. The window's `RunProcess` catches `Exception`, but the `Assets/Process with NAMER` and `GameObject/Process with NAMER` menu handlers (`ProcessSelection` → `LogResult`) do not — an artist right-clicking a degenerate/empty mesh asset gets an unhandled exception bubble instead of a logged error. (`NamerDecompPipeline.GenerateResidual` would also build `new ComputeBuffer(0, stride)` buffers for such a split — currently unreachable only because Fit throws first.)
**Fix:** Either throw `InvalidOperationException` from `Fit` for the empty-split case, or widen the two catch clauses in `NamerProcessor.Process` to `catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)`, and/or guard in `Process` before splitting: `if (decomposeSourceMesh != null && decomposeSourceMesh.vertexCount == 0) { result.Warnings.Add(...); decomposeSourceMesh = null; }`.

### WR-02: Zero-tangent fallback is written verbatim into generated/preview split meshes — normal mapping breaks on meshes whose source lacks tangents

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/MeshVertexSplitter.cs:81-85`; `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:504`; `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:574`
**Issue:** When `sourceMesh.tangents` is empty, the splitter fabricates `new Vector4[positions.Length]` — all `(0,0,0,0)`. `BuildSplitMesh` and `BuildPreviewSplitMesh` then `SetTangents` those zeros, and no `RecalculateTangents` runs anywhere. The NAMER runtime shader decodes the octahedral normal and transforms by the tangent frame; a zero tangent yields a degenerate TBN, so generated split meshes from tangent-less sources (runtime-built meshes, some FBX exports) shade with broken normals. The renderer-mesh swap makes this permanent for the scene object.
**Fix:** Track "source had no tangent stream" on `NamerSplitResult` (or emit `null` Tangents) and in that case call `outMesh.RecalculateTangents()` after `SetUVs`/`SetTriangles` instead of `SetTangents(zeros)` — in both `AssetGenerator.BuildSplitMesh` and `NamerEditorWindow.BuildPreviewSplitMesh`.

### WR-03: Non-square sources: manual override clamps only against width, and the square residual resample distorts aspect

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:236,303-304,471-476`
**Issue:** `Resample(fullResidual, chosenResolution, chosenResolution)` always produces a SQUARE residual. For a 4096x512 base, a manual pick of 2048 (`Mathf.Min(ResolutionLadder[idx], w)` — `h` is never consulted) yields a 2048x2048 residual whose V axis is upscaled 4x from 512 (pure waste), while the U axis is halved. The adaptive path is self-consistent (the error metric resamples back to w×h the same way, and UV-space sampling is preserved under the stretch), so this is a quality/memory defect rather than a broken invariant — but for portrait/landscape atlases the effective per-axis resolution differs by the aspect ratio, and the manual ladder can silently upscale one axis.
**Fix:** Scale the chosen resolution per axis: `int dstW = Mathf.Min(chosenW, w); int dstH = Mathf.Max(1, Mathf.RoundToInt(dstW * (h / (float)w)));` in `Resample` call sites (and clamp the manual override against both `w` and `h`), or clamp the manual value to `Mathf.Min(ResolutionLadder[idx], Mathf.Min(w, h))`.

### WR-04: Gamma-project decomposition preview mixes sRGB-encoded base with linear residual/vertex colors — the Error Heatmap channel shows phantom error

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:422-453`; `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader:129-137`
**Issue:** In a Gamma project, `ResolvePreviewBaseMap` deliberately returns an sRGB-ENCODED copy of the base (correct for the non-decomposed base bind, which matches the saved PNG). But when decomposition is on, the same sRGB-encoded texture is also bound to `_DebugBaseMap`, while `_BaseResidualMap` is the raw linear residual RT and `input.vertexColor` is the linear fit. The heatmap channel computes `err = |residual*vc (linear) - debugBase (sRGB-encoded)|` — a large, meaningless nonzero error across the whole model. Similarly the decomposed after-pane (`residual*vc`, linear, displayed without encode) shades darker than both the before-pane and the non-decomposed base bind.
**Fix:** In Gamma projects, either bind `_liveResult.NormalizedBaseColor` (linear) to `_DebugBaseMap` when `_decompOutput?.Residual != null`, or sRGB-encode the residual RT before binding it to the preview materials, so both operands of the reconstruction comparison live in the same space. At minimum, gate the Error Heatmap toolbar entry on `ColorSpace.Linear`.

### WR-05: A failed preview recompute leaves preview materials bound to pool-released / destroyed render targets

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:294-380,455-475`
**Issue:** `RecomputePreview` calls `ReleaseLiveResult()` (DestroyImmediate on `_previewBaseRt`, pool-release of `_liveResult`) and `ReleaseDecompPreview()` (pool-release of the residual) BEFORE the new compute. If anything after that throws (`_pipeline.Process`, `RunDecompPreview`, a readback error), the `catch` at line 369 sets the error status and `Repaint()` — but `_namerMaterial`/`_debugMaterial` still reference the released/destroyed RTs, and the after-pane draws them (stale contents for pooled RTs that a later lease may re-dispatch into; Unity fake-null behavior for the destroyed `_previewBaseRt`). The bindings are only repaired on the next successful recompute.
**Fix:** In the `catch` (or a `finally` before `Repaint`), clear the stale binds when the recompute did not complete: `_namerMaterial.SetTexture(BaseResidualMapId, null); _debugMaterialFactory.SetTextures(_debugMaterial, null, null); _debugMaterialFactory.SetDebugBaseMap(_debugMaterial, null);` — or restructure so textures are released only after their replacements exist.

## Info

### IN-01: `_Verts` StructuredBuffer is bound every call but never read by any kernel

**File:** `Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute:53`; `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:171,193`
**Issue:** The rasterize is UV-space only (documented at line 51-52), so the vertex-position upload (`ToFloat3(split.Positions)` + buffer create/set/dispose) is pure overhead per material per recompute. If it is a deliberate interface commitment, a comment saying "reserved" exists; otherwise drop it.
**Fix:** Remove `_Verts`, `vertsBuf`, and `ToFloat3`, or gate the upload behind a future consumer.

### IN-02: The reduce block factor exists as a C# constant and as bare HLSL literals — silent corruption on drift

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:83`; `Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute:180-216`
**Issue:** `ReduceDownsampleFactor = 8` must match `numthreads(8,8,1)`, the `uint2(8u,8u)` source stride, and the `for (y < 8) for (x < 8)` loop in `CSReduce`. Nothing enforces that; changing the C# side alone mis-addresses every reduce read with no error. (Same pattern as the `_Size`-driven kernels, but those pass their size as a uniform.)
**Fix:** Add a static assert-style comment pairing the constant with the kernel, or pass the block factor as a uniform and loop `for (uint y = 0; y < _Block; ++y)`.

### IN-03: Heatmap normalization bound 0.25 duplicated between C# and the debug shader

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:84,202`; `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader:137`
**Issue:** `kMaxObservedErrFloor = 0.25f` feeds `_MaxObservedErr` in the compute path; the debug shader hardcodes `err / 0.25`. They agree only because the threshold slider tops out at 0.10. Raise the slider max past 0.25 (or lower the floor) and the on-model heatmap and the stats diverge silently.
**Fix:** Bind the ramp scale from C# (`_debugMaterial.SetFloat("_HeatmapScale", Mathf.Max(threshold, 0.25f))`) or share one HLSL constant like `NAMER_VC_FLOOR` already does.

### IN-04: Stats readback byte-quantizes the reduced error/alpha channels

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:411-454`
**Issue:** `ReadBackStats` requests `TextureFormat.RGBA32` from the R16G16B16A16 reduce target, so `MaxError` and `MinAlpha` land on a 1/255 grid. At the default threshold 0.02, a true max error of 0.0198 and 0.0210 are 2 ticks apart — the D-13 gate can flip on quantization (currently in the conservative keep-residual direction), and `kOpaqueAlphaThreshold = 0.999` effectively means "alpha must be exactly 255". Acceptable, but worth a comment; if tighter gating is ever needed, read back `RGBAHalf`.
**Fix:** Document the quantization at `kOpaqueAlphaThreshold`, or read back `TextureFormat.RGBAHalf` into `half4`.

### IN-05: CPU fit consumes an 8-bit LINEAR readback of the base — dark-region fit precision loss

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:410-422`; `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:550-562`
**Issue:** `ReadBackBase`/`ReadBackBaseTexels` quantize the linear float16 base to RGBA32 before the fitter samples it. Linear 8-bit has ~4 usable levels below 0.05 sRGB, so fits on dark materials are noisier than the GPU side (which divides the float16 base by the quantized vc). Reconstruction stays near-exact because the residual divides by the same quantized vc, but `FitQuality` and the fit-only error metric absorb the noise.
**Fix:** Read back `TextureFormat.RGBAHalf` and convert to `float3` in the sampler (the fitter's `SampleBase` already works in float), or note the accepted precision limit where `baseTexels` is documented as "linear RGBA32".

### IN-06: Multi-material selections write N identical split-mesh assets (one per material name)

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:65-72,169-172`
**Issue:** The mesh path is composed from the material name inside the per-material `Generate` loop, so a 3-slot selection writes three `.asset` meshes with identical geometry (and, per CR-01, competing vertex colors) plus three EXR-adjacent preflights. Beyond the waste, this is the mechanism that makes CR-01's dictionary overwrite possible.
**Fix:** Resolve one split mesh per source MESH (see CR-01) and write it once, keyed by the mesh rather than the material.

### IN-07: Hierarchical reduce means "mean of block means" for partial edge blocks

**File:** `Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute:180-216`
**Issue:** Pass N≥2 averages the per-block means with equal weight, but edge blocks with fewer than 64 valid sources carry the same weight as full blocks. For non-multiple-of-8 sizes this biases `Coverage`/`AvgError` by up to one block's worth of weight out of ~thousands. Negligible in practice; noting because the kernel comment claims exact means.
**Fix:** None needed; optionally carry a valid-count channel and weight by it if stats ever need to be exact.

---

_Reviewed: 2026-09-01T00:34:54Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

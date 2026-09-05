---
phase: 04-vertex-color-decomposition-residual
reviewed: 2026-09-05T00:00:00Z
depth: standard
files_reviewed: 6
files_reviewed_list:
  - Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs
  - Packages/com.graffitientertainment.namer/Editor/Decompose/VertexColorFitter.cs
  - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/VertexColorDecompTests.cs
findings:
  critical: 0
  warning: 4
  info: 4
  total: 8
status: issues_found
---

# Phase 4: Code Review Report (Gap-Closure Delta Round)

**Reviewed:** 2026-09-05
**Depth:** standard
**Files Reviewed:** 6 (plus cross-file verification of `NAMERDecomp.compute`, `ComputeTexturePool.cs`, `NamerComputePipeline.cs`, `MeshVertexSplitter.cs`, `NamerProcessorSettings.cs`, `NamerSourceModel.cs`, `NamerEditorWindow.cs` resolution popup)
**Status:** issues_found
**Scope:** Phase-04 gap-closure delta closing prior CR-01/CR-02/CR-03. The prior round's full report is preserved in git history at commit 5887a78.

## Summary

All three prior Critical findings are genuinely closed and their regression tests are meaningful (details below). No new Critical defects were found. Four Warnings remain: one is a precision defect in the new CR-03 coverage guard that makes its effective threshold ~0.2% instead of the documented zero (WR-01), two are quality gaps in the CR-01 guard's diagnostics and its mesh-counting mirror (WR-02/WR-03), and one is a pre-existing but delta-widened partial-write window in `AssetGenerator.Generate` (WR-04).

### Closure verification

**CR-01 (multi-material / shared-material guard) — CLOSED.**
`NamerProcessor.cs:117-124` trips on `model.Materials.Count > 1 || distinctSourceMeshes > 1`, nulls `decomposeSourceMesh`, and every material then takes the existing `decomp == null` Phase-3 path. Verified end-to-end:
- The fallback writes no mesh asset and no residual; the material binds the generated base PNG; `BindGeneratedMaterials` leaves every renderer's `sharedMesh` untouched (`BakeSourceMesh` keying at `NamerProcessor.cs:285-294` only maps when `MeshPath` is non-empty).
- `SourceInspector.AddUnique` dedupes by material instance ID, so the shared-material/multi-mesh disjunct is real and covered by `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning`.
- Both regression tests assert the warning, the absence of mesh/residual, the base-PNG bind, and no mesh swap. Good tests.
- Residual gaps in the closure: WR-02 (the fallback also emits a contradictory "No mesh to decompose" warning per material) and WR-03 (`CountDistinctSourceMeshes` does not fully mirror `ResolveSourceMesh`).

**CR-02 (residual EXR bilinear import) — CLOSED.**
`AssetGenerator.cs:440` sets `FilterMode.Bilinear` in `WriteResidualExr` only; `WriteSurfaceTexture` still forces `FilterMode.Point` (line 388, GEN-04 intact). Linear/uncompressed/no-mip/Repeat preserved. `Process_ReducedResolutionResidual_StampsBilinearImporter` asserts the full importer state plus the 128px reduced size. Correct and consistent with the bilinear-resampled `MaxError` the adaptive search reports.

**CR-03 (repeat-wrap SampleBase + zero-coverage guard + CannotDecompose fallback) — CLOSED, with one precision defect (WR-01).**
- The wrap in both Burst jobs (`VertexColorFitter.cs:437-438`, `518-519`) is mathematically correct: `u - floor(u/W)*W` maps the half-texel-aligned coordinate into `[0, W)`, `x1 = (x0+1) % W` wraps the bilinear neighbor, matching hardware `TextureWrapMode.Repeat` sampling at `uv*W - 0.5`. Verified for in-range, tiling (`[1,2]`), and negative UVs.
- `NamerDecompPipeline.cs:217-228` places the guard before the D-13 gate, so unwritten `_ErrorStat` texels can no longer masquerade as a perfect fit. The rasterizer kernel (`NAMERDecomp.compute:74-117`) writes every texel (uncovered → `(0,0,0,0)`), so no stale pooled data leaks into the stats.
- The `CannotDecompose` branch (`NamerProcessor.cs:165-173`) leaves `decomp` null → Phase-3 shape; `NamerDecompOutput.Dispose()` with a null residual is safe (`ComputeTexturePool.Release` null-checks at `ComputeTexturePool.cs:62-65`).
- The GPU rasterizer only sees texel centers in `[0,1]`, so fully out-of-range UVs genuinely produce zero coverage; the partially-tiling case (some triangles in range) still decomposes, and the CPU wrap makes the fit valid there — coherent design.
- Defect: the guard's effective threshold is ~0.2%, not zero — see WR-01.

## Warnings

### WR-01: Coverage guard's effective threshold is ~0.2% of texels, not "near zero" — small UV footprints are falsely refused

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:217` (guard), `:87` (`kMinCoverageFraction`), `:435` (`ReadBackStats` RGBA32 readback)
**Issue:** The reduce chain carries coverage as float (`_ErrorStat` is `R16G16B16A16_SFloat`, `CSReduce` averages `.b` in float) but `ReadBackStats` reads the final 1x1 target as `TextureFormat.RGBA32`, quantizing the fraction once to 1/255 steps: `CoverageFraction = round(f * 255) / 255`. The guard `CoverageFraction < 1e-6f` is therefore equivalent to `round(f*255) == 0`, i.e. it trips for any true coverage fraction below ~0.5/255 (~0.196%), not just zero — contradicting the comment at lines 210-216 ("only zero coverage trips this guard").

Concrete failure: a mesh whose UV islands occupy a 64x64 region of a 2048x2048 base atlas covers 4096/4,194,304 = 0.098% of texels; `0.00098 * 255 = 0.25` rounds to byte 0 → `CannotDecompose` → decomposition silently skipped with a misleading "UV coverage near zero (tiling/out-of-range UVs)" warning. On a 4096 source the refusal line is ~180x180 texels. Atlas-packed small props hit this; a UV-less mesh (the splitter substitutes all-zero UVs, `MeshVertexSplitter.cs:84-88`) lands in the same bucket with the same wrong message.

**Fix:** Read the reduce result back in float so the epsilon means what it says:

```csharp
private static ReduceStats ReadBackStats(RenderTexture src, int validW, int validH)
{
    AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(src, 0, TextureFormat.RGBAFloat);
    request.forcePlayerLoopUpdate = true;
    request.WaitForCompletion();
    if (request.hasError)
    {
        throw new InvalidOperationException("NAMER decomp stats readback failed.");
    }

    NativeArray<Color> data = request.GetData<Color>();   // 4x float, no 1/255 quantization
    try
    {
        int rowStride = src.width;
        // ... replace c.r / 255f with c.r etc. — values are already [0,1] floats
```

(`RGBAFloat` readback of the UNorm8 `coverageStat` chain is also exact for its 0/1 flags, so the shared helper stays correct for both callers.)

### WR-02: CR-01 fallback emits contradictory "No mesh to decompose" warnings per material

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:148-153`
**Issue:** When the CR-01 guard trips, it adds one accurate warning ("Vertex-color decomposition skipped ... maps 2 material(s) to 1 source mesh(es)") and sets `decomposeSourceMesh = null`. The per-material block then can't distinguish "guard tripped" from "no mesh was ever resolved", so every material additionally gets `"No mesh to decompose for material 'X' — generating the Phase-3 shape instead."` For a 2-material selection the user sees 3 warnings, N+1 of which state there is no mesh while the first says there are 1+. The tests pass only because they use `Warnings.Exists(...)`.
**Fix:** Track why the mesh is null and only warn on a genuine resolution failure:

```csharp
bool guardTripped = decomposeSourceMesh != null
    && (model.Materials.Count > 1 || distinctSourceMeshes > 1);
// ... after the guard sets decomposeSourceMesh = null:
if (decomposeSourceMesh == null && !guardTripped)
{
    result.Warnings.Add("No mesh to decompose for material '...' ...");
}
```

### WR-03: `CountDistinctSourceMeshes` does not mirror `ResolveSourceMesh`'s prefab sub-asset branch — guard can pass while the resolved mesh differs from what renderers wear

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:567-591` (count) vs `:476-486` (resolution)
**Issue:** `ResolveSourceMesh` for Regular/Variant prefabs checks `FindMeshSubAsset(assetPath)` FIRST and returns it without looking at renderers; `CountDistinctSourceMeshes` skips the sub-asset check entirely and only walks prefab-contents renderers, despite its doc comment claiming to mirror "ResolveSourceMesh's per-case resolution ... the same order the single-mesh resolution uses". If a prefab file carries a `Mesh` sub-asset that differs from the mesh its renderers reference, the count sees 1 renderer mesh → guard passes → decomposition runs on the sub-asset mesh, but `BindGeneratedMaterials` keys the swap on the sub-asset mesh while the renderer wears the other mesh → no swap → the material binds a residual onto a mesh with no fitted vertex colors. That is exactly the CR-01 silent-wrong-render failure mode, re-opened through a corner case. Rare trigger, real consequence.
**Fix:** Mirror the priority in the prefab branch — if `FindMeshSubAsset(assetPath)` is non-null, count distinct mesh sub-assets via `LoadAllAssetsAtPath` instead of (or in addition to) walking contents renderers, and make the two methods share one enumerator so they cannot drift again.

### WR-04: Mid-`Generate` failure leaves a partially-written asset set, and `ReadBackResidual`'s error message is then false

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:162-185` (write ordering), `:470-474` (message)
**Issue:** `Generate` writes surface → base → mesh → residual → material sequentially. `ReadBackResidual` (line 176) runs after the surface, base, and mesh are already on disk, so a residual readback failure, a missing `TextureImporter`, or a missing NAMER shader in `WriteMaterial` leaves surface+base(+mesh)(+residual) without the material — despite T-03-02/D-04's "never a partially-written asset set" contract (preflight only covers overwrite refusals) and despite the thrown message `"GPU readback failed while reading the decomposition residual; no files were written."`, which is factually wrong at that point: three files were.
**Fix:** Move both blocking readbacks (`ReadBackResidual`, and the residual importer/shader existence checks) ahead of the first `File.WriteAllBytes`, e.g. hoist `residualTex = decomp.Stats != null && decomp.Stats.ResidualRequired && decomp.Residual != null ? ReadBackResidual(decomp.Residual) : null;` above `WriteSurfaceTexture(...)` and correct the message to name the files that were written.

## Info

### IN-01: `surfaceData` / `baseData` readback NativeArrays are never disposed

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:139-141`
**Issue:** The arrays from `surfaceRequest.GetData<byte>()` / `baseRequest.GetData<byte>()` are used and abandoned, while `ReadBackResidual` (line 476-487) correctly disposes its array in a `finally`. Inconsistent ownership pattern; per Unity's readback contract the caller disposes.
**Fix:** Wrap usage in `try/finally { surfaceData.Dispose(); baseData.Dispose(); }` mirroring `ReadBackResidual`.

### IN-02: `ReadBackStats` omits `forcePlayerLoopUpdate = true` before `WaitForCompletion`

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:435-436`
**Issue:** Every other readback site in the package (`AssetGenerator.cs:126-127`, `:467-468`, `NamerProcessor.cs:444-445`) sets the flag with the documented rationale ("pumps the request even in edit mode where there is no player loop driving async GPU reads"). This site — the hottest one, called per resolution-ladder step — does not. The live-editor suite passes, but batch/headless invocations are the stated motivation for the pattern.
**Fix:** Add `request.forcePlayerLoopUpdate = true;` before `WaitForCompletion()` (included in the WR-01 snippet).

### IN-03: `_Verts` position buffer is built, converted, and uploaded but never read by any kernel

**File:** `Packages/com.graffitientertainment.namer/Editor/Decompose/NamerDecompPipeline.cs:151, 173, 195`
**Issue:** `CSRasterizeVertexColors` is position-independent (`NAMERDecomp.compute:51-53` documents `_Verts` as "carried for the documented interface"), so `ToFloat3(split.Positions)` + `CreateBuffer` + `SetBuffer` + dispose is dead work proportional to vertex count on every decompose.
**Fix:** Delete the `_Verts` plumbing from C# and the `StructuredBuffer<float3> _Verts` declaration from the shader, or gate it behind the debug interface that will eventually consume it.

### IN-04: Preflight demands residual/mesh targets the CR-01/CR-03 fallbacks never write; stale decomposition assets linger beside a regenerated non-decomposed set

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:233-244`, `NamerProcessor.cs:100`
**Issue:** `PreflightTargets(..., settings.DecompositionEnabled)` preflights `_Residual.exr` and the mesh `.asset` whenever decomposition is enabled, but the CR-01/CR-03 fallbacks produce neither. Two consequences: (a) a stale stamped residual from a previous decomposed run plus `OverwriteGenerated == false` now hard-blocks a run that would not have touched it; (b) when preflight passes, the fallback regenerates the material as non-decomposed while the previous run's residual EXR and split mesh remain on disk next to it (inert — nothing binds them — but untracked clutter).
**Fix:** Either pass the post-guard effective flag into preflight, or have the fallback delete previously-generated residual/mesh assets for the materials it regenerates without them.

---

_Reviewed: 2026-09-05_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

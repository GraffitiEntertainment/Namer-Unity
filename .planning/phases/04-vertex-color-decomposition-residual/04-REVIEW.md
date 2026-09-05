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

## Resolution Log (post-review fix round)

_Fixed: 2026-09-05. All four Warnings verified against the code before fixing — every claim
held. Info findings IN-01..IN-04 were not in scope for this round except where the review's
own WR-01 snippet folded IN-02 in (see below)._

### WR-01 — CONFIRMED, FIXED (commit `5eed72c`)

- **Root cause:** The reduce chain carries coverage as a float fraction (`avgA`/`avgB` are
  `R16G16B16A16_SFloat`; `CSReduce` averages `.b` in float), but `ReadBackStats` read the
  final 1x1 target back as `TextureFormat.RGBA32`, quantizing the fraction once to
  `round(f*255)/255`. The `kMinCoverageFraction = 1e-6f` guard therefore fired for any true
  coverage below ~0.5/255 (~0.196%), not just zero.
- **Fix:** `NamerDecompPipeline.ReadBackStats` now requests `TextureFormat.RGBAFloat` and
  reads `GetData<Color>()` with no `/255f` scaling — values are already [0,1] floats
  (`NamerDecompPipeline.cs:433-476`, request at `:444`). `RGBAFloat` is exact for both
  callers: the float error chain keeps full precision, and the UNorm8 `coverageStat`
  chain's 0/1 flags are exactly representable. `forcePlayerLoopUpdate = true` before
  `WaitForCompletion()` was added as part of the review's suggested snippet (IN-02).
- **Test added:** `ResidualPipelineTests.GenerateResidual_TinyUvFootprint_StillDecomposes_AndZeroCoverageStillFallsBack`
  — block 1 covers a 12x12-texel island of a 512x512 base (~0.055% coverage: below the old
  ~0.196% effective cutoff, far above true zero) and asserts it decomposes
  (`CannotDecompose == false`, residual required); block 2 re-asserts true zero coverage
  (UVs in [1,2]^2) still trips the CR-03 guard. Block 1 fails on the old RGBA32 readback
  (0.00055 * 255 rounds to byte 0), so the test is a genuine regression guard. The existing
  end-to-end zero-coverage test (`Process_TilingUvMesh_FallsBackToPhase3WithWarning`) is
  unaffected.

### WR-02 — CONFIRMED, FIXED (commit `e03cf9d`)

- **Root cause:** The CR-01 guard nulled `decomposeSourceMesh` after emitting its accurate
  warning, but the per-material block could not distinguish "guard tripped" from "no mesh
  was ever resolved", so it added a contradictory `"No mesh to decompose for material 'X'"`
  warning per material (N+1 warnings for an N-material selection).
- **Fix:** A `decompGuardTripped` flag set when the guard nulls the mesh
  (`NamerProcessor.cs:118,125`); the per-material warning now fires only on a genuine
  resolution failure — `if (decomposeSourceMesh == null && !decompGuardTripped)`
  (`NamerProcessor.cs:155`). A guard trip now emits exactly ONE accurate warning.
- **Tests added:** Warning-count assertions in both CR-01 fallback tests —
  `Process_MultiMaterialSelection_FallsBackToPhase3WithWarning` (2-material selection) and
  `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning` — each now asserts exactly
  1 `"Vertex-color decomposition skipped"` warning and 0 `"No mesh to decompose"` warnings,
  via a new `CountWarnings(result, fragment)` helper. Counts are asserted by fragment
  rather than `Warnings.Count` so unrelated inspection warnings cannot make the assertion
  brittle.

### WR-03 — CONFIRMED, FIXED (commit `5e02e06`)

- **Root cause:** `ResolveSourceMesh` checks `FindMeshSubAsset(assetPath)` FIRST for
  Regular/Variant prefabs and never looks at renderers in that case, while
  `CountDistinctSourceMeshes` walked only prefab-contents renderers. A prefab whose Mesh
  sub-asset differs from its renderers' meshes counted 1, passed the guard, and
  decomposition ran on the sub-asset while `BindGeneratedMaterials` keyed the swap on what
  the renderers wear — the CR-01 silent-wrong-render failure mode reopened.
- **Fix:** The counter's prefab branch now collects the mesh sub-asset IDs
  (`LoadAllAssetsAtPath`) alongside the contents renderers' mesh IDs and returns the
  DISTINCT-union count (`NamerProcessor.cs:583-593`). Design note: the review offered
  "instead of (or in addition to)" walking contents renderers; "instead of" alone would
  still count 1 in the mismatch case (a single sub-asset) and leave the hazard open, so
  the union form is used — a sub-asset that differs from the renderers' meshes is itself
  the hazard and must trip the guard. For every normal prefab (sub-assets ARE the renderer
  meshes, or there is no sub-asset) the union equals the old count, so behavior is
  unchanged outside the corner case. Doc comment updated to state the mirror + union.
- **Test added:** `NamerDecompIntegrationTests.Process_PrefabWithDistinctMeshSubAsset_FallsBackToPhase3WithWarning`
  — builds the fixture headlessly (`PrefabUtility.SaveAsPrefabAsset` from a scene object
  wearing an external persisted quad, then `AssetDatabase.AddObjectToAsset` of a distinct
  Mesh into the prefab file, then `ImportAsset`), processes the prefab with decomposition
  ON, and asserts the guard trips (exactly 1 skip warning, 0 no-mesh warnings, no mesh /
  residual written, base PNG bound). On the old counter this test fails cleanly: the guard
  passes and a split mesh + residual are written.

### WR-04 — CONFIRMED, FIXED (commit `bd29c58`)

- **Root cause:** `Generate` wrote surface → base → mesh before `ReadBackResidual` ran, so
  a residual readback failure threw after three files were on disk while its message
  claimed "no files were written" — and left a partial asset set behind (T-03-02/D-04
  contract; preflight only covers overwrite refusals).
- **Fix:** The residual readback is hoisted to the first statement inside the existing
  `try` (`AssetGenerator.cs:160-199`): `writeResidual` (the same
  `decomp.Stats.ResidualRequired && decomp.Residual != null` condition the write used) is
  computed before the try, `ReadBackResidual` runs as the try's first statement, and
  `WriteResidualExr` consumes the already-read-back texture after the surface/base/mesh
  writes. Every fallible GPU readback now completes before the first `File.WriteAllBytes`,
  making the failure message literally true and eliminating the partial-set window for
  readback failures. Placing the readback inside the try (rather than before it) preserves
  the `finally` that `DestroyImmediate`s the already-created surface/base textures.
  Trade-off documented: a missing `TextureImporter` or a missing NAMER shader
  (`Shader.Find` in `WriteMaterial`) can still throw after earlier writes — those are
  non-readback failures that predate this finding, hoisting them would mean restructuring
  the per-asset write helpers, and the false-message defect the review named is closed.
  GEN-01 (non-destructive, dedicated directory) is untouched — the write set and order of
  on-disk artifacts on success are identical.
- **Test added:** None — not reasonably testable headlessly. Forcing a mid-`Generate`
  residual readback failure requires making `AsyncGPUReadback.Request` fail on demand;
  the readback goes through the static `NamerComputePipeline.RequestReadback` with no
  injection seam, and adding one (interface indirection around a static) is a refactor
  outside this fix round's scope. Passing a destroyed/released `RenderTexture` as
  `decomp.Residual` does not deterministically produce `hasError` — it exercises
  undefined Unity behavior, which would make the test flaky rather than decisive.
  Verified statically: the readback (`AssetGenerator.cs:171-174`) precedes the first
  `File.WriteAllBytes` call site (`WriteSurfaceTexture`, reached at `:182`).

### Post-fix round: batchmode regression corrections (2026-09-05)

The first batchmode run of the fixed suite came back 102 PASS / 3 FAIL — two regressions
introduced by the WR-02 fix plus the new WR-03 test failing. Both diagnosed at the root and
fixed; no assertions were loosened.

**WR-02 regression (Failures 1+2)** — `Process_MultiMaterialSelection_FallsBackToPhase3WithWarning`
and `Process_SharedMaterialMultiMesh_FallsBackToPhase3WithWarning` died with
`ArgumentNullException: sourceMesh` at `NamerProcessor.cs:164 → MeshVertexSplitter.Split`.
- **Root cause:** the pre-WR-02 `if (decomposeSourceMesh == null) { warn } else { split }`
  coupled the warning to the null-gate. Changing only the warning condition to
  `decomposeSourceMesh == null && !decompGuardTripped` made the guard-tripped case (mesh
  null, guard tripped) fall into the `else` and call `Split(null)`.
- **Fix (commit `1d399ae`):** the null-gate is now a separate statement —
  `if (decomposeSourceMesh != null) { split path }` (`NamerProcessor.cs:161-167`) — so a
  guard trip takes neither branch and skips the split exactly as before WR-02. Truth table:
  mesh null + genuine resolution failure → warning only; mesh null + guard tripped →
  neither; mesh non-null → split (unchanged).

**WR-03 test failure (Failure 3)** — `Process_PrefabWithDistinctMeshSubAsset_FallsBackToPhase3WithWarning`
failed its skip-warning count (Expected 1, But was 0).
- **Root cause:** fixture, not production. The first fixture attached the Mesh sub-asset
  with `AddObjectToAsset(mesh, prefabPath)` + `ImportAsset(prefabPath)` — the pattern that
  works for `.asset` containers (see `NamerReprocessTests`). A `.prefab` file is owned by
  the prefab system: the import regenerated the file from the unchanged in-memory prefab
  model and silently dropped the raw-added sub-object. With no visible sub-asset, both the
  resolver (`FindMeshSubAsset`) and the counter's sub-asset walk saw nothing; the resolver
  fell back to the contents walk (`rendererMesh`), the union counted 1, the guard passed,
  and decomposition ran normally — 0 skip warnings. Production resolver and counter agree
  on branch selection for the same input; the fixture simply never produced the intended
  input.
- **Fix (round 2 = commit `310bf6e`, superseded by round 3 = commit `9b95cf5`):** the
  fixture needed three rounds, each with hard batchmode evidence:
  - **Round 1** — `AddObjectToAsset(mesh, prefabPath)` + `ImportAsset(prefabPath)`: the
    pattern that works for `.asset` containers (see `NamerReprocessTests`) silently drops
    the sub-object on a `.prefab`, because the import regenerates the file from the
    unchanged in-memory prefab model. Result: no visible sub-asset → resolver and counter
    agreed on the contents fallback → union counted 1 → guard passed → 0 skip warnings
    (Expected 1, But was 0).
  - **Round 2 (commit `310bf6e`)** — `LoadPrefabContents` → `AddObjectToAsset(mesh,
    contentsRoot)`: fails with `[Error] AddAssetToSameFile failed because the other asset
    DistinctSubAssetPrefab is not persistent` — the contents root is an in-memory object,
    and `AddObjectToAsset` requires a persistent on-disk destination.
  - **Round 3 (working combination)** — attach to the PERSISTENT prefab object that
    `SaveAsPrefabAsset` already returned (`prefabRoot`), then flush with
    `AssetDatabase.SaveAssets()`; never call `ImportAsset`/`Refresh` on that path
    afterwards — that re-import is precisely what regenerated the sub-asset away in
    round 1, so `SaveAssets`-not-`ImportAsset` is load-bearing here. Any refresh must
    happen BEFORE the `AddObjectToAsset`.
  The precondition assert (the prefab file must expose a Mesh sub-asset whose instance ID
  differs from the renderer's mesh) is kept unchanged from round 2 — it converts both prior
  failure modes into loud, clearly-labeled failures instead of silently testing the wrong
  input. All original test assertions unchanged.

---

_Reviewed: 2026-09-05_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

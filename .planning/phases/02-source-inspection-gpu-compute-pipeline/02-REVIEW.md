---
phase: 02-source-inspection-gpu-compute-pipeline
reviewed: 2026-08-27T18:01:20Z
depth: standard
files_reviewed: 11
files_reviewed_list:
  - Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute
  - Packages/com.graffitientertainment.namer/Compute/NamerEncode.hlsl
  - Packages/com.graffitientertainment.namer/Core/NamerFormat.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/ComputeTexturePool.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/ComputeSmokeTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/GpuGoldenTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/SourceInspectorTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef
findings:
  critical: 0
  warning: 4
  info: 5
  total: 9
status: issues_found
---

# Phase 2: Code Review Report

**Reviewed:** 2026-08-27T18:01:20Z
**Depth:** standard
**Files Reviewed:** 11
**Status:** issues_found

## Summary

Static adversarial review of the Phase 2 deliverables: the three staged compute kernels, the shared HLSL encode include, the CPU oracle, the compute dispatch harness + RT pool, the source inspector and its data model, and the three test suites.

**Verified sound (no findings):**

- `NamerEncode.hlsl` mirrors `NamerFormat`/`NamerConstants` exactly: thresholds are strict `>`, roughness uses `floor(r * 63)` clamped to `[0, 63]`, alpha bits `0x80/0x40/0x3F`, epsilon `1e-6` — all eight constants are in literal sync with `Core/NamerConstants.cs`. No drift.
- Kernel `_Size` bounds guards and `(w+7)/8` dispatch ceiling are correct; the alpha byte round-trips through `R8G8B8A8_UNorm` exactly (what the golden test asserts as EXACT).
- `ComputeTexturePool.Release` is idempotent (double-release and never-leased both no-op) and `Dispose` destroys both live and pooled targets.
- `SourceInspector` is strictly read-only: no importer flag flips, no `SetTexture`/`SetDirty`, prefab contents loaded and unloaded in `try/finally`. Asset safety holds.
- Assembly boundaries are clean: the new pipeline code is Editor-only; the test asmdef gained only the `GraffitiEntertainment.Namer.Editor` reference; nothing leaked into Runtime.

**Key concerns:** the compute result contract has no release path (outputs accumulate as live RTs and are destroyed under the caller at Dispose), the D-13 leak-watchdog assertion is a tautology that cannot fail, folder inspection silently misses prefab-contained materials, and the sRGB robustness of data-map uploads is untested territory.

## Narrative Findings (AI reviewer)

### Warnings

### WR-01: `NamerComputePipeline.Process` outputs can never be returned to the pool — no public release path, ambiguous ownership

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:93-122` (also `:186-189`, `:58`)

**Issue:** `Process` leases seven render targets and returns two of them (`baseColorOut`, `surfaceOut`) to the caller inside `NamerComputeResult`, but the `finally` block releases only the six intermediates. There is no public API to return the two outputs: `Release(RenderTexture)` is private (line 186) and `_pool` is private. Consequences:

1. Every `Process` call permanently holds two full-resolution live RTs (an `R16G16B16A16_SFloat` intermediate plus the packed surface) until `Dispose`. For the batch workflow this phase builds — `SourceInspector.Inspect` on a folder resolves to N unique materials — the count grows without bound and pooled reuse is defeated for outputs (intermediates recycle; outputs never do).
2. Ownership is contradictory: the doc (lines 60-64) says "the caller owns the returned targets," yet the pool keeps them in `_live` and `Dispose()` destroys them (ComputeTexturePool.cs:92-111). A Phase 3 consumer that keeps `PackedSurface` alive across pipeline disposal holds a destroyed RenderTexture — a use-after-destroy of GPU resources.

**Fix:** Add an explicit release contract for results, e.g.:

```csharp
/// <summary>Returns a Process result's render targets to the pool. Call after readback.</summary>
public void ReleaseResult(NamerComputeResult result)
{
    if (result == null) return;
    _pool.Release(result.NormalizedBaseColor);
    _pool.Release(result.PackedSurface);
    result.NormalizedBaseColor = null;
    result.PackedSurface = null;
}
```

Then assert in the batch loop (`LiveRenderTargetCount` back to 0 between materials), and document that results are invalid after `ReleaseResult`/`Dispose`.

### WR-02: Leak-watchdog assertion is a tautology — it can never fail and masks the exact leak condition it claims to verify

**File:** `Packages/com.graffitientertainment.namer/Tests/Editor/ComputeSmokeTests.cs:47-52` (baseline at `:33-34`)

**Issue:** The watchdog does:

```csharp
finally
{
    pipeline.Dispose();
    Assert.AreEqual(baseline, pipeline.LiveRenderTargetCount,
        "pool must return to baseline after Process + Dispose (no leak, D-13)");
}
```

`baseline` is 0 (asserted at line 34), and `Dispose()` unconditionally clears `_live` (ComputeTexturePool.cs:99). The assertion therefore compares 0 == 0 after `Dispose` and passes regardless of any leak. The D-13 contract ("LiveCount returns to baseline after a batch") is never actually tested — in reality, after three scenarios the live count is 6 (two unreleasable outputs per `Process`, see WR-01), which is precisely the leak-shaped condition this test exists to catch, and it sails through. This is a missing-assertion failure in the phase's stated deliverable.

**Fix:** Assert before `Dispose`, once a result-release API exists (WR-01):

```csharp
NamerComputeResult result = pipeline.Process(inspection);
try { /* readback assertions */ }
finally { pipeline.ReleaseResult(result); }

Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must be empty between batches (D-13)");
// ... after all scenarios, before Dispose:
Assert.AreEqual(0, pipeline.LiveRenderTargetCount);
pipeline.Dispose();
```

### WR-03: Folder inspection silently misses materials used by prefab assets — asymmetric with the single-selection path

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs:124-137`

**Issue:** The folder branch searches only `t:Material` and `t:Model`. Prefab assets (`.prefab`, the most common material container in a Unity project) are not discovered: a folder of prefabs whose materials live elsewhere resolves to zero materials with no warning. This is asymmetric with `CollectMaterials`' own selection path (lines 92-112), which fully supports prefab assets via `LoadPrefabContents`. Silent-empty output for a supported input kind is an unhandled edge case in the phase's front-door API (D-01/D-02).

**Fix:**

```csharp
foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
{
    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
    if (prefab == null) continue;
    GameObject contents = PrefabUtility.LoadPrefabContents(AssetDatabase.GUIDToAssetPath(guid));
    try
    {
        foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
        {
            AddSharedMaterials(renderer, materials, seen);
        }
    }
    finally
    {
        PrefabUtility.UnloadPrefabContents(contents);
    }
}
```

(Extract the shared prefab-contents walk so the selection path and folder path use one implementation.)

### WR-04: sRGB handling of non-base data maps (AO / metallicGloss) is unverified and unrecorded — only `BaseMapIsSrgb` exists

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs:35-50`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:98-101`; `Packages/com.graffitientertainment.namer/Tests/Editor/ComputeSmokeTests.cs:58-62`

**Issue:** The kernels treat `_AoIn.g`, `_MetallicGlossIn.r/.a`, and `_NormalTexel` as raw texel data (NAMERPack.compute header, lines 9-10: "Normal / AO / metallic / roughness are never color-converted"), and the pipeline uploads them with a plain `Graphics.Blit` into linear RTs. The inspection model records an sRGB flag for the base map only. The numerically verified upload contract covers the base map; the data-map case is never exercised: every data map in the smoke and golden tests is created `linear: true` (ComputeSmokeTests.cs:59-62) or uploaded as linear `RGBAHalf` (GpuGoldenTests.cs:312). Real-world AO and metallic/gloss textures are frequently left at the TextureImporter default (`sRGBTexture: true`). Because no test pins the behavior for an sRGB-imported data map, ENCD-05 (decode equivalence with the Blender oracle) is unverified for a very common import configuration — and if the runtime Blit treats such sources differently from linear ones, the strict `> 0.5` metallic threshold and the 6-bit roughness quantization silently diverge from the CPU oracle. This is flagged as an actual hazard at the edge of the verified contract, not as a dispute of the base-map contract itself.

**Fix:** (1) Add a smoke/golden scenario that uploads an sRGB-authored AO/metallicGloss map and pins the packed bytes against the CPU expectation, locking the contract the way the base-map scenarios do. (2) Record per-map sRGB flags in `NamerMaterialInspection` (mirroring `BaseMapIsSrgb`), and have `SourceInspector.FinalizeInspection` emit a warning when `OcclusionMap`/`MetallicGlossMap`/`NormalMap` report an sRGB graphics format, since that contradicts the kernels' raw-data assumption and is worth surfacing to the artist.

### Info

### IN-01: Static fill textures are never destroyed

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:37-39, 237-245`

**Issue:** `_whiteFill`, `_neutralNormalTexture`, `_neutralMetallicGlossTexture` are lazily created with `HideAndDontSave` but never destroyed; they persist for the editor session until domain reload. Acceptable for editor tooling, but they outlive every pipeline instance.
**Fix:** Optionally move them behind a small static holder with explicit teardown, or accept and document the session-lifetime cache.

### IN-02: Dispatch math hardcodes the kernel thread-group size

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:181-184`

**Issue:** `(w + 7) / 8` duplicates the `[numthreads(8,8,1)]` size declared in `NAMERPack.compute:45/77/89`. The coupling lives as magic numbers in two files; changing the kernel group size without the C# side would silently leave pixels unwritten (the bounds guard does not cover under-dispatch).
**Fix:** `private const int ThreadGroupSize = 8;` then `(w + ThreadGroupSize - 1) / ThreadGroupSize`, with a comment pointing at the `numthreads` declaration.

### IN-03: "Five Blender golden vectors" contain only two distinct normals

**File:** `Packages/com.graffitientertainment.namer/Tests/Editor/GpuGoldenTests.cs:30-41`

**Issue:** Vectors A, B, C, E are all `(0.5, 0.5, 1.0)`; only D differs. Even if these mirror `BlenderGoldenVectorTests` inputs for parity, the GPU octahedral encode is effectively exercised on two distinct texels, so R/G variation across `[0.5, 1.0]` is barely covered. (Credit where due: vector C's `emissive = 0.1` does pin the strict `>` boundary.)
**Fix:** Add a couple of non-neutral, non-axis texels (e.g. `(0.75, 0.6, 0.9)`, `(0.4, 0.9, 0.2)`) to the GPU-side array — parity is preserved by the CPU oracle comparison, not by reusing identical inputs.

### IN-04: Non-material asset selections resolve silently to zero materials

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs:140-149`

**Issue:** Selecting any asset with a path that is neither a Material, GameObject, model, nor valid folder (e.g. a Texture2D or Mesh) runs `AddSubAssetMaterials`, finds nothing, and returns an empty model with no warning — while the path-less unsupported case at line 149 does warn. Inconsistent diagnostics for the same class of unsupported input.
**Fix:** After `AddSubAssetMaterials(path, ...)` in the fall-through branch, add a warning when zero materials were collected (e.g. "No materials found in asset 'path'").

### IN-05: Output resolution is silently driven by the base map only

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs:72-73`

**Issue:** `w`/`h` come exclusively from `BaseMap` (falling back to `DefaultResolution = 256` when absent). A material with a 2K normal/AO map but no base map silently downsamples everything to 256 via blit rescaling, and dimension mismatches between maps are resampled with no warning. Deliberate policy, but undocumented and silent.
**Fix:** Record a warning in the inspection (or pipeline) when maps have differing dimensions or when `BaseMap == null` but other maps exist, so the resolution choice is visible to the user.

---

_Reviewed: 2026-08-27T18:01:20Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

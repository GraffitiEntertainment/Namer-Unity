---
phase: 02-source-inspection-gpu-compute-pipeline
plan: 01
subsystem: editor-tooling
tags: [unity, csharp, urp, upm, material-inspection, source-inspector, serialization]

# Dependency graph
requires:
  - phase: 01-core-format-contract-runtime-decode
    provides: "pure-C# Core assembly (NamerFormat/NamerConstants), UPM package scaffold with Core/Editor/Runtime/Tests asmdefs"
provides:
  - "SourceInspector.Inspect(Object) — resolves Material/GameObject/prefab/FBX/folder into deduplicated unique materials"
  - "NamerSourceModel / NamerMaterialInspection — serializable per-material inspection result (map refs, sRGB flags, scalars, warnings)"
  - "Per-shader property tables for URP Lit and Unity Standard (smoothness-channel indirection, AO-in-G, metallic-R/smoothness-A)"
  - "Tools/NAMER/Inspect Selection menu item with a Debug.Log report"
affects: [02-02-compute-pipeline, 03-01-asset-generation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Per-shader property-convention tables read via Material.HasProperty + GetTexture/GetFloat/GetColor"
    - "Selection resolution deduped by Material.GetInstanceID() via a HashSet<int>"
    - "Non-generic AssetDatabase.LoadAllAssetsAtPath (Object[]) filtered for Material sub-assets"

key-files:
  created:
    - "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs"
    - "Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs"
    - "Packages/com.graffitientertainment.namer/Tests/Editor/SourceInspectorTests.cs"
  modified:
    - "Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef"

key-decisions:
  - "SourceInspector resolves FBX/model assets via the non-generic AssetDatabase.LoadAllAssetsAtPath (returns Object[]) filtered for Material sub-assets — LoadAllAssetsAtPath<T> does not exist in Unity 6"
  - "Roughness = 1 - Smoothness; Emissive = max(EmissionColor RGB) when a map or non-black emission color is present; AoUnmultiplyStrength (cleaning, default 1.0) kept distinct from OcclusionStrength (decode-time blend metadata)"

patterns-established:
  - "Pattern 1: guarded property reads — every Material access goes through HasProperty before GetTexture/GetFloat/GetColor"
  - "Pattern 2: selection resolution — single Inspect entry point normalizes all five selection kinds into a deduplicated material set"

requirements-completed: [INSP-01, INSP-02, INSP-03, INSP-04]

# Metrics
duration: 10min
completed: 2026-08-27
---

# Phase 02 Plan 01: SourceInspector Summary

**SourceInspector C# API resolving Material/GameObject/prefab/FBX/folder selections into deduplicated URP Lit + Unity Standard material inspections with sRGB flags, smoothness-channel indirection, and neutral defaults**

## Performance

- **Duration:** 10 min
- **Started:** 2026-08-27T00:13:51Z
- **Completed:** 2026-08-27T00:24:11Z
- **Tasks:** 2
- **Files modified:** 4 (3 created, 1 modified)

## Accomplishments

- `SourceInspector.Inspect(Object)` resolves all five selection kinds (Material, scene GameObject, prefab asset, FBX/model asset, folder) into unique materials deduplicated by instance ID
- URP Lit maps read via the real property names (`_BaseMap`/`_BumpMap`/`_MetallicGlossMap`/`_OcclusionMap`/`_EmissionMap`) with the `_SmoothnessTextureChannel` indirection and AO-in-green-channel semantics
- `NamerSourceModel`/`NamerMaterialInspection` are serializable data (not log output), recording map refs, sRGB flags, scalar fallbacks, roughness/emissive derivations, and warnings
- Missing maps/scalars fall back to neutral defaults (metallic 0, roughness 0.5, AO 1.0, emission black) without failing; unsupported shaders record warnings
- Source assets are never mutated — no `SetDirty`/`SaveAssets`/importer-flag flips (verified by grep and immutability test)
- EditMode suite green 40/40 via the clone runner (31 pre-existing + 9 new SourceInspector tests)

## Task Commits

Each task was committed atomically:

1. **Task 1: SourceInspector API + NamerSourceModel + Inspect Selection menu item** - `cbf5eaf` (feat)
2. **Task 1 fix: non-generic AssetDatabase.LoadAllAssetsAtPath** - `b5a94d2` (fix)
3. **Task 2: SourceInspectorTests + Editor asmdef reference** - `4a05349` (test)

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs` - serializable inspection result (map refs + sRGB flags + scalars + warnings)
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs` - selection resolution + per-shader property tables + Inspect Selection menu item
- `Packages/com.graffitientertainment.namer/Tests/Editor/SourceInspectorTests.cs` - INSP-01..04 EditMode coverage
- `Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef` - added Editor assembly reference

## Decisions Made

- Used the non-generic `AssetDatabase.LoadAllAssetsAtPath` (returns `Object[]`) with a `Material` filter for FBX/model sub-assets — the generic form does not exist in Unity 6.
- Recorded AO as raw `_OcclusionMap.g` semantics: `OcclusionStrength` is captured as decode-time blend metadata, distinct from `AoUnmultiplyStrength` (cleaning strength, default 1.0).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Used non-generic AssetDatabase.LoadAllAssetsAtPath instead of the generic form**
- **Found during:** Task 2 (EditMode suite compile)
- **Issue:** The plan text referenced `AssetDatabase.LoadAllAssetsAtPath<Material>(path)`, which does not exist — the API is non-generic and returns `Object[]`. The first test run failed to compile (CS0308).
- **Fix:** Added a `AddSubAssetMaterials` helper that iterates `AssetDatabase.LoadAllAssetsAtPath(path)` and filters `subAsset is Material`.
- **Files modified:** Packages/com.graffitientertainment.namer/Editor/Pipeline/SourceInspector.cs
- **Verification:** EditMode suite compiles and passes 40/40.
- **Committed in:** `b5a94d2`

**2. [Rule 1 - Bug] Restructured the dedupe test to two child GameObjects**
- **Found during:** Task 2 (EditMode suite run)
- **Issue:** The test attached two `MeshRenderer` components to one GameObject; Unity only allows one per GameObject, so `AddComponent<MeshRenderer>()` returned null and the test threw a NullReferenceException.
- **Fix:** Created a parent with two child GameObjects, each with its own `MeshRenderer`, both sharing one material.
- **Files modified:** Packages/com.graffitientertainment.namer/Tests/Editor/SourceInspectorTests.cs
- **Verification:** Dedupe test passes; full EditMode suite green 40/40.
- **Committed in:** `4a05349`

---

**Total deviations:** 2 auto-fixed (both Rule 1 - Bug)
**Impact on plan:** Both fixes correct code/tests that would otherwise not compile or run. No scope creep; no plan-objective change.

## Issues Encountered

- The plan's `<verify>` batchmode command targets the main project path, which is lock-held by the interactive editor; per environment constraints, tests ran via the validated clone runner `/tmp/namer-gsd/run-tests-in-clone.sh EditMode` (exit 0).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- `NamerSourceModel` is ready for plan 02-02 (compute dispatch harness + `ComputeTexturePool` + kernels) to consume map refs + sRGB flags + scalars.
- Map-discovery conventions (URP Lit `_SmoothnessTextureChannel`, AO-in-G, metallic-R/smoothness-A) are locked before the GPU kernels read them.
- No blockers for 02-02; the interactive editor lock remains the only test-run constraint (clone runner handles it).

---
*Phase: 02-source-inspection-gpu-compute-pipeline*
*Completed: 2026-08-27*

## Self-Check: PASSED

All created/modified files present on disk; all three commits (`cbf5eaf`, `b5a94d2`, `4a05349`) present in history; EditMode suite green 40/40 via the clone runner.

# Phase 3: Asset Generation + Editor Workflow + Preview - Pattern Map

**Mapped:** 2026-08-27
**Files analyzed:** 11 (10 new/modified + 1 implied shared entry point)
**Analogs found:** 9 / 11

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Editor/Generation/AssetGenerator.cs` | service (write-only disk writer) | file-I/O + transform | `Editor/NamerSmokeSetup.cs` (asset creation) + `Editor/Pipeline/NamerComputePipeline.cs` (readback/ReleaseResult) | role-match |
| `Editor/UI/NamerEditorWindow.cs` | controller (EditorWindow) | event-driven (IMGUI + update pump) | `Editor/Pipeline/SourceInspector.cs` (MenuItem family, static entry) | partial (no EditorWindow exists) |
| `Editor/UI/NamerPreviewRenderer.cs` | component (PreviewRenderUtility) | transform (mesh → offscreen render) | none — RESEARCH.md verified code example | no-analog |
| `Editor/UI/NamerDebugChannelMaterial.cs` | utility (debug material/property cache) | transform | `Editor/NamerSmokeSetup.cs` (`CreateNamerMaterial`) | role-match |
| `Editor/Settings/NamerProcessorSettings.cs` | config (EditorPrefs persistence) | CRUD (get/set) | `Core/NamerConstants.cs` (constants) + RESEARCH.md | partial (no EditorPrefs use yet) |
| `Shaders/NamerDebugView.shader` | shader (GPU render) | transform | `Shaders/NAMER.shader` + `Shaders/NamerSurface.hlsl` | exact (same role, reuses include) |
| `Tests/Editor/AssetGeneratorTests.cs` | test | file-I/O | `Tests/Editor/SourceInspectorTests.cs` | exact |
| `Tests/Editor/SourceImmutabilityTests.cs` | test | file-I/O (hash) | `Tests/Editor/SourceInspectorTests.cs` + `Tests/Editor/GpuGoldenTests.cs` | exact |
| `package.json` (modify) | config | n/a | self (existing `package.json`) | exact |
| `README.md` (modify) | config/docs | n/a | self (existing `README.md`) | exact |
| `Editor/NamerProcessor.cs` (implied — D-13 shared entry point, Claude's discretion) | service (orchestrator) | request-response | `Editor/Pipeline/SourceInspector.cs` (`Inspect` static entry) | role-match |

---

## Pattern Assignments

### `Editor/Generation/AssetGenerator.cs` (service, file-I/O + transform)

**Analog:** `Editor/NamerSmokeSetup.cs` (asset/material/texture creation) + `Editor/Pipeline/NamerComputePipeline.cs` (readback + result lifecycle)

**Imports + namespace pattern** (`NamerComputePipeline.cs` lines 1-7, `NamerSmokeSetup.cs` lines 1-8):
```csharp
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// [full XML doc comment describing write-only role; every public type/member carries one]
    /// </summary>
    public sealed class AssetGenerator : IDisposable
```

**Load-and-throw on missing asset** (`NamerComputePipeline.cs` lines 43-47; `NamerSmokeSetup.cs` lines 109-113):
```csharp
_compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
if (_compute == null)
{
    throw new InvalidOperationException("NAMER compute shader not found at " + ComputeShaderPath);
}
```
Apply the same shape for the NAMER shader load: `Shader.Find("GraffitiEntertainment.Namer/NAMER")` then throw `InvalidOperationException` if null (`NamerSmokeSetup.cs` lines 109-113).

**Readback contract it must honor** (`NamerComputePipeline.cs` lines 147-153):
```csharp
public static AsyncGPUReadbackRequest RequestReadback(RenderTexture source, int mip = 0, TextureFormat format = TextureFormat.RGBA32)
{
    return AsyncGPUReadback.Request(source, mip, format);
}
```
And the `ReleaseResult` lifecycle (`NamerComputePipeline.cs` lines 134-145) — AssetGenerator must read back both `NormalizedBaseColor` and `PackedSurface`, then call `ReleaseResult(result)`. `LiveRenderTargetCount` (`line 59`) is the leak watchdog the tests assert returns to baseline.

**Programmatic material + texture + asset creation** (`NamerSmokeSetup.cs` lines 107-144):
```csharp
Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
// ... null check ...
Texture2D surfaceMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
surfaceMap.name = "NamerSmoke_Surface";
surfaceMap.filterMode = FilterMode.Point;
surfaceMap.SetPixel(0, 0, new Color(...));
surfaceMap.Apply(false, false);

Material material = new Material(shader);
material.name = "NamerSmoke_Material";
material.SetTexture("_SurfaceMap", surfaceMap);
material.SetTexture("_BaseResidualMap", baseResidualMap);
material.SetColor("_BaseColor", Color.white);
material.SetColor("_EmissionColor", Color.black);
material.SetFloat("_OcclusionStrength", 1.0f);

AssetDatabase.CreateAsset(surfaceMap, SurfaceMapPath);
AssetDatabase.CreateAsset(baseResidualMap, BaseResidualMapPath);
AssetDatabase.CreateAsset(material, NamerMaterialPath);
```

**Import-then-stamp pattern (the PNG write path — reuse from test fixture, promoted to production)** (`SourceInspectorTests.cs` lines 137-149):
```csharp
Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
source.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
source.Apply();
File.WriteAllBytes(path, source.EncodeToPNG());
Destroy(source);
AssetDatabase.ImportAsset(path);
TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
importer.sRGBTexture = srgb;
importer.SaveAndReimport();
return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
```
In production the readback bytes replace `SetPixel` (use `LoadRawTextureData` + `Apply` + `EncodeToPNG` per RESEARCH.md lines 360-381), and the importer stamp per D-06 is: `textureType = TextureImporterType.Default`, `sRGBTexture = false`, `textureCompression = TextureImporterCompression.Uncompressed`, `filterMode = FilterMode.Point`, `mipmapEnabled = false` for the packed surface (RESEARCH.md lines 346-357).

**Overwrite gate + label stamp (D-04)** — RESEARCH.md lines 383-408, verbatim:
```csharp
private const string GeneratedLabel = "NamerGenerated";

private static string EnsureWritableTarget(string path, bool overwriteGenerated)
{
    Object existing = AssetDatabase.LoadAssetAtPath<Object>(path);
    if (existing == null) { return path; }
    string[] labels = AssetDatabase.GetLabels(existing);
    bool stamped = Array.IndexOf(labels, GeneratedLabel) >= 0;
    if (!stamped || !overwriteGenerated)
    {
        throw new InvalidOperationException(
            $"Refusing to overwrite non-generated asset '{path}'. " +
            "Only NamerGenerated-stamped assets in the destination folder can be overwritten.");
    }
    return path;
}
```

---

### `Editor/UI/NamerEditorWindow.cs` (controller, event-driven)

**Analog:** `Editor/Pipeline/SourceInspector.cs` (MenuItem family, static command entry) — no `EditorWindow` exists in the codebase, so the `OnGUI`/`EditorApplication.update` body comes from RESEARCH.md (verified signatures).

**MenuItem family convention** (`SourceInspector.cs` lines 22-34; `NamerSmokeSetup.cs` line 27):
```csharp
[MenuItem("Tools/NAMER/Inspect Selection")]
public static void InspectSelection()
{
    Object selection = Selection.activeObject != null ? Selection.activeObject : Selection.activeGameObject;
    if (selection == null)
    {
        Debug.Log("[NAMER] Inspect Selection: no selection.");
        return;
    }
    NamerSourceModel model = Inspect(selection);
    LogReport(selection, model);
}
```
Phase 3 adds three `[MenuItem]`s — `Tools/NAMER/Processor`, `Assets/Process with NAMER`, `GameObject/Process with NAMER` — all routing into the shared entry point. The window class uses `GetWindow<T>()` (RESEARCH.md lines 102-103).

**Logging convention — `Debug.Log("[NAMER] ...")` prefix** (`SourceInspector.cs` lines 426-453): every user-facing report is a `Debug.Log("[NAMER]   [warning] ...")` / `"[NAMER]     [Material] ..."` indented-tree line. The window's inspection summary (D-14) should follow this shape.

**Debounced recompute pump** — no codebase analog; use RESEARCH.md lines 411-426 (verified `EditorApplication.update` is a field, subscribe with `+=`, unsubscribe in `OnDisable`):
```csharp
private void OnEnable()  { EditorApplication.update += Tick; }
private void OnDisable() { EditorApplication.update -= Tick; }
private void Tick()
{
    if (_dirty && (EditorApplication.timeSinceStartup - _lastChange) > _debounceSeconds)
    {
        _dirty = false;
        RecomputeAndRequestPreview();
    }
    // ... check outstanding readback request.done; on completion: Repaint()
}
```

---

### `Editor/UI/NamerPreviewRenderer.cs` (component, transform — offscreen render)

**Analog:** none in codebase. Use RESEARCH.md lines 301-336 verbatim (reflected + UnityCsReference-verified). The single synced camera + two `DrawMesh` calls + `Render(true)` are mandatory for URP:
```csharp
_preview.BeginPreview(rect, GUIStyle.none);
_preview.camera.transform.position = new Vector3(0f, 0f, -4f);
_preview.camera.transform.rotation = Quaternion.identity;
_preview.lights[0].transform.rotation = Quaternion.Euler(50f, -30f, 0f);
_preview.lights[1].transform.rotation = Quaternion.Euler(340f, 218f, 177f);
_preview.DrawMesh(mesh, new Vector3(-0.7f, 0f, 0f), Quaternion.identity, beforeMat, 0);
_preview.DrawMesh(mesh, new Vector3(+0.7f, 0f, 0f), Quaternion.identity, afterMat, 0);
_preview.Render(true);   // true = allowScriptableRenderPipeline (URP materials, else pink)
Texture result = _preview.EndPreview();
GUI.DrawTexture(rect, result, ScaleMode.StretchToFill, false);
```
Lifecycle: `new PreviewRenderUtility()` in `OnEnable`, `_preview?.Cleanup()` in `OnDisable` (RESEARCH.md lines 305-318). Pitfall 1 (Render without `true`) and Pitfall 5 (ReleaseResult before each re-dispatch) apply.

---

### `Editor/UI/NamerDebugChannelMaterial.cs` (utility, transform)

**Analog:** `Editor/NamerSmokeSetup.CreateNamerMaterial` (`NamerSmokeSetup.cs` lines 107-144). Same shape: `Shader.Find("...")` → null-throw → `new Material(shader)` → `SetTexture/SetColor/SetFloat` → return material. The debug material caches `Shader.PropertyToID` for `_SurfaceMap`, `_BaseResidualMap`, `_DebugChannel` and feeds the preview's `DrawMesh` after-mesh (the runtime `NAMER.shader` material is the "before", the debug material is the "after" only when a debug channel is selected).

---

### `Editor/Settings/NamerProcessorSettings.cs` (config, CRUD)

**Analog:** `Core/NamerConstants.cs` for the constants convention; RESEARCH.md for `EditorPrefs` (no codebase use yet).

**Constants convention — no magic numbers** (`NamerConstants.cs` lines 9-34): centralize as `const`/`constexpr` with XML docs. Phase 3 constants (default destination `Assets/NAMERGenerated/`, default suffix `_Namer`, label `NamerGenerated`, debounce interval) belong here or in a sibling settings-constants type — the project rule is "constants centralized in `NamerConstants`; no magic numbers", so do not inline `"NamerGenerated"` in AssetGenerator without reconciling with this rule.

**EditorPrefs pattern** (RESEARCH.md lines 104): `EditorPrefs.GetString/SetString` for destination/prefix/suffix, `EditorPrefs.GetBool/SetBool` for overwrite toggle. Wrap in typed getters/setters (D-03: user-scoped, zero asset noise).

---

### `Shaders/NamerDebugView.shader` (shader, transform)

**Analog:** `Shaders/NAMER.shader` (structure) + `Shaders/NamerSurface.hlsl` (shared decode include, D-12).

**Shader header + include pattern** (`NAMER.shader` line 1, lines 109/290-292/531-532; `NamerSurface.hlsl` lines 1-2, 14-15):
```hlsl
Shader "GraffitiEntertainment.Namer/NAMER"
{
    Properties { ... }
    SubShader { ... Pass { HLSLPROGRAM ... #include "NamerSurface.hlsl" ... ENDHLSL } }
}
```
`NamerSurface.hlsl` carries the include guard `#ifndef GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED` (lines 1-2) and the URP ShaderLibrary includes (lines 14-15). The debug shader must `#include "NamerSurface.hlsl"` and call `NAMER_DECODE_SURFACE` (`NamerSurface.hlsl` lines 63-74) + `NamerOctahedralDecode` (lines 41-54) so decode cannot drift (D-12). It is a small editor-only Unlit-style shader that samples `_SurfaceMap` + `_BaseResidualMap` (TEXTURE2D/SAMPLER declarations at `NamerSurface.hlsl` lines 33-34) and switches output on a `_DebugChannel` float.

**Property surface the generated material must populate (D-07 metadata checklist)** (`NAMER.shader` Properties block lines 3-36): `_SurfaceMap`, `_BaseResidualMap`, `_BaseColor`, `_EmissionColor`, `_OcclusionStrength`, `_Cutoff`, `_ALPHATEST_ON` (+ keyword), `_EMISSION` (+ keyword), `_Surface` (+ `_SURFACE_TYPE_TRANSPARENT`), `_SrcBlend`/`_DstBlend`/`_SrcBlendAlpha`/`_DstBlendAlpha`, `_ZWrite`.

---

### `Tests/Editor/AssetGeneratorTests.cs` + `Tests/Editor/SourceImmutabilityTests.cs` (test, file-I/O)

**Analog:** `Tests/Editor/SourceInspectorTests.cs` (temp folder + fixture helpers + cleanup) and `Tests/Editor/GpuGoldenTests.cs` (capability gating).

**Imports + namespace** (`SourceInspectorTests.cs` lines 1-9):
```csharp
using System.IO;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GraffitiEntertainment.Namer.Tests
```

**Temp-destination + cleanup pattern (D-16)** (`SourceInspectorTests.cs` lines 19, 421-429):
```csharp
private const string TempFolder = "Assets/NAMER_Tests_Temp";

private static void EnsureTempFolder()
{
    if (AssetDatabase.IsValidFolder(TempFolder))
    {
        AssetDatabase.DeleteAsset(TempFolder);   // deletes folder + its .meta (assumption A4)
    }
    AssetDatabase.CreateFolder("Assets", "NAMER_Tests_Temp");
}
// ... in finally: AssetDatabase.DeleteAsset(TempFolder);
```

**Fixture helper: write PNG + import + stamp** (`SourceInspectorTests.cs` lines 137-149) — already shown under AssetGenerator; reuse for fixtures. The `Destroy(params Object[])` helper using `DestroyImmediate` (`SourceInspectorTests.cs` lines 431-440) is the standard cleanup.

**Capability gating for GPU paths (D-15/D-16 run the full Process flow)** (`GpuGoldenTests.cs` lines 43-59):
```csharp
private static bool ComputeAvailable =>
    SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

[UnityTest]
public IEnumerator CSOctahedralEncode_KernelMatchesCore()
{
    if (!ComputeAvailable)
    {
        Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU golden test (D-15: Metal is the verified target).");
        yield break;
    }
    // ...
    yield return null;
}
```

**Existing immutability precedent to extend (D-15)** (`SourceInspectorTests.cs` lines 394-417): the current test checks `isReadable` + `sRGB` flags don't change. Phase 3's `SourceImmutabilityTests` upgrades this to SHA-256 (`System.Security.Cryptography.SHA256`) of every source file **and its `.meta`** before/after the full Process flow, then asserts byte-identical (RESEARCH.md lines 110-111, 473-476 — note the editor-lock constraint on headless runs).

**Async readback in tests** (`GpuGoldenTests.cs` lines 322-336): `AsyncGPUReadback.Request(...)`, `req.WaitForCompletion()`, `Assert.IsFalse(req.hasError)`, `req.GetData<T>()` — the assert/error-guard pattern the re-import round-trip test should reuse.

---

### `package.json` (config, modify)

**Analog:** self. Current `package.json` (13 lines) already has `name`, `version`, `displayName`, `unity`, `dependencies`, `testables`. D-17 adds: `description`, `author`, and confirms `unity: "6000.0"`. Do not change the four `com.unity.*` dependencies (unchanged this phase per RESEARCH.md "Package Legitimacy Audit").

### `README.md` (docs, modify)

**Analog:** self. Existing `README.md` (18 lines) covers the packed format. D-17 adds install + basic workflow sections. Keep the existing packed-format table intact; append "Installation" and "Workflow" sections (select FBX → `Process with NAMER` → output lands in `NAMERGenerated/`).

---

## Shared Patterns

### Namespace + documentation
**Source:** every existing file. `Editor/` → `namespace GraffitiEntertainment.Namer.Editor`; `Core/` → `GraffitiEntertainment.Namer.Core`; `Tests/` → `GraffitiEntertainment.Namer.Tests`. Every public type/member carries a `/// <summary>` XML doc comment (see `SourceInspector.cs` class header lines 8-19).

### Menu item / command surface
**Source:** `Editor/Pipeline/SourceInspector.cs` lines 22-34; `Editor/NamerSmokeSetup.cs` lines 27-28.
**Apply to:** `NamerEditorWindow.cs`, `AssetGenerator.cs` (or the shared `NamerProcessor` entry). All menu items live under `Tools/NAMER/…`; the two context-menu items are new this phase (D-13).

### Readback → ReleaseResult lifecycle
**Source:** `Editor/Pipeline/NamerComputePipeline.cs` lines 134-153, 59.
**Apply to:** `AssetGenerator.cs` (save path) and `NamerPreviewRenderer.cs`/window (preview re-dispatch). Read back both RTs, then `ReleaseResult`; keep at most one live result; `LiveRenderTargetCount` returns to baseline.

### Error handling
**Source:** `NamerComputePipeline.cs` lines 43-47 (throw `InvalidOperationException` on missing asset); `SourceInspector.cs` lines 41-60 (never throw on missing data — record warnings).
**Apply to:** `AssetGenerator` throws `InvalidOperationException` on non-stamped overwrite and missing shader; the inspector/window never throws on unsupported selection, it warns (D-02/D-06).

### Constants — no magic numbers
**Source:** `Core/NamerConstants.cs` lines 9-34.
**Apply to:** `AssetGenerator.cs` (label, default destination, suffix), `NamerEditorWindow.cs` (debounce interval). Reconcile with `NamerConstants` (or a settings-constants type) per the project rule.

### asmdef scope
**Source:** `Editor/GraffitiEntertainment.Namer.Editor.asmdef` (20 lines — already references URP Runtime + Core + Runtime) and `Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef` (21 lines — references Core, Editor, UnityEngine.TestRunner, UnityEditor.TestRunner, Unity.Mathematics, `nunit.framework.dll`).
**Apply to:** all new `Editor/` and `Tests/Editor/` files — **no asmdef changes required**; new files fall under the existing Editor and Tests.Editor asmdefs. `NamerDebugView.shader` is a Shader asset (not in any C# asmdef).

---

## No Analog Found

Files with no close codebase match (planner uses RESEARCH.md verified patterns instead):

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Editor/UI/NamerPreviewRenderer.cs` | component | transform (offscreen render) | No `PreviewRenderUtility` usage exists; RESEARCH.md lines 301-336 give the reflected/verified API (Render(true), lights, DrawMesh overloads) |
| `Editor/UI/NamerEditorWindow.cs` (the `OnGUI`/update-pump body) | controller | event-driven | No `EditorWindow` exists; RESEARCH.md lines 102-103, 411-426 verified `EditorApplication.update` is a field (subscribe `+=`) |

## Metadata

**Analog search scope:** `Packages/com.graffitientertainment.namer/{Core,Editor,Shaders,Compute,Tests}`, `package.json`, `README.md`
**Files scanned:** 14 (`SourceInspector.cs`, `NamerComputePipeline.cs`, `NamerSourceModel.cs`, `ComputeTexturePool.cs`, `NamerSmokeSetup.cs`, `NAMER.shader`, `NamerSurface.hlsl`, `NamerConstants.cs`, `NamerFormat.cs` [checked via references], `SourceInspectorTests.cs`, `GpuGoldenTests.cs`, 3 asmdefs, `package.json`, `README.md`)
**Pattern extraction date:** 2026-08-27

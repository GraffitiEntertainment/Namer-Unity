# Phase 2: Source Inspection + GPU Compute Pipeline - Pattern Map

**Mapped:** 2026-08-26
**Files analyzed:** 9 (7 new, 2 modified)
**Analogs found:** 7 / 9

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Core/NamerFormat.cs` (MODIFY, D-16) | model (pure C# format reference) | transform (pure function) | itself — `Core/NamerFormat.cs` | exact (file being edited) |
| `Compute/NamerEncode.hlsl` (NEW) | utility (HLSL include) | transform (pure encode functions) | `Shaders/NamerSurface.hlsl` | exact (HLSL-mirrors-Core) |
| `Compute/NAMERPack.compute` (MODIFY) | compute shader (GPU processing) | transform (per-texel GPU point ops) | `Shaders/NamerSurface.hlsl` + empty scaffold | role-match (no prior compute in repo) |
| `Editor/Pipeline/SourceInspector.cs` (NEW) | service (editor asset inspection) | request-response (selection → model; asset traversal) | `Editor/NamerSmokeSetup.cs` | role-match |
| `Editor/Pipeline/NamerSourceModel.cs` (NEW) | model (serializable data) | transform (data holder) | Core purity conventions (`Core/NamerFormat.cs`) | partial (no serializable-data analog) |
| `Editor/Pipeline/NamerComputePipeline.cs` (NEW) | service (compute dispatch harness) | transform (GPU dispatch/readback) | none in repo | none (use RESEARCH.md) |
| `Editor/Pipeline/ComputeTexturePool.cs` (NEW) | utility (RenderTexture lifecycle) | resource management | none in repo | none (use RESEARCH.md) |
| `Tests/Editor/SourceInspectorTests.cs` (NEW) | test | request-response (NUnit EditMode) | `Tests/Editor/BlenderGoldenVectorTests.cs` | exact test conventions |
| `Tests/Editor/GpuGoldenTests.cs` (NEW) | test | request-response + async GPU readback | `Tests/Runtime/NamerRoundTripSmokeTests.cs` | partial ([UnityTest]/cleanup) |

## Pattern Assignments

### `Core/NamerFormat.cs` (MODIFY — D-16 minimal rename)

**Analog:** itself (existing file; this is a surgical parameter rename, not a new file)

**Change target** — `PackSurface` signature (line 75):
```csharp
public static float4 PackSurface(float3 normal, float ao, float metallic, float emissive, float roughness)
```
becomes (D-16):
```csharp
public static float4 PackSurface(float3 normalTexel, float ao, float metallic, float emissive, float roughness)
```

**Reference for the rename** — the existing doc language already calls this a "raw DirectX normal-map texel" (lines 14-19, `OctahedralEncode` XML):
```csharp
/// <summary>
/// Encodes a raw [0,1] DirectX normal-map texel into NAMER octahedral R/G.
/// Barycentric projection of the non-negative texel (the reference's normalize
/// and L1-divide cancel for these inputs). R/G always land in [0.5, 1.0];
/// no corner-fold is applied.
/// </summary>
public static float2 OctahedralEncode(float3 v)
```

**Body reference (unchanged — kernels mirror this exactly)** (lines 20-24, 53-59, 75-80):
```csharp
public static float2 OctahedralEncode(float3 v)
{
    float sum = max(v.x + v.y + v.z, NamerConstants.Epsilon);
    return new float2(0.5f + 0.5f * v.x / sum, 0.5f + 0.5f * v.y / sum);
}

public static byte PackAlphaBits(float metallic, float emissive, float roughness)
{
    byte metallicBit = metallic > NamerConstants.MetallicThreshold ? NamerConstants.MetallicBit : (byte)0;
    byte emissiveBit = emissive > NamerConstants.EmissiveThreshold ? NamerConstants.EmissiveBit : (byte)0;
    byte roughnessBits = (byte)clamp((int)floor(roughness * NamerConstants.RoughnessLevels), 0, NamerConstants.RoughnessMask);
    return (byte)(metallicBit | emissiveBit | roughnessBits);
}

public static float4 PackSurface(float3 normal, float ao, float metallic, float emissive, float roughness)
{
    float2 oct = OctahedralEncode(normal);
    float alpha = PackAlphaBits(metallic, emissive, roughness) / NamerConstants.AlphaByteScale;
    return new float4(oct.x, oct.y, ao, alpha);
}
```

**Impact note:** all existing callers (`Tests/Editor/*`, `Tests/Runtime/*`, `Editor/NamerSmokeSetup.cs` uses a hand-written texel, not `PackSurface`) pass the `float3` positionally, so the rename breaks nothing; the HLSL `NamerEncode.hlsl` mirrors these lines verbatim (with the renamed parameter name).

---

### `Compute/NamerEncode.hlsl` (NEW — shared encode include)

**Analog:** `Shaders/NamerSurface.hlsl` (the established HLSL-mirrors-Core include). This new include mirrors the *encode* side of `NamerFormat` exactly as `NamerSurface.hlsl` already mirrors the *decode* side.

**Include-guard pattern** (lines 1-2 and 112):
```hlsl
#ifndef GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED
...
#endif // GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED
```
Use the analogous guard `GRAFFITI_ENTERTAINMENT_NAMER_ENCODE_INCLUDED`.

**Header-comment pattern** (lines 4-12 — documents the hand-port and the linear-data contract):
```hlsl
// NAMER runtime decode + surface assembly.
//
// Hand-port of GraffitiEntertainment.Namer.Core.NamerFormat (plan 01-01) and the
// BlenderNamerPlugin reference (namer_core.py). The packed surface texture is
// LINEAR data (ENCD-04): R/G are octahedral normal coordinates, B is AO, A is the
// packed alpha byte (bit 7 = metallic, bit 6 = emissive, bits 0-5 = roughness).
```

**Mirror-Core function pattern** (lines 41-54, `NamerOctahedralDecode` mirrors `NamerFormat.OctahedralDecode` line-for-line):
```hlsl
float3 NamerOctahedralDecode(float2 oct)
{
    float2 pxy = oct * 2.0 - 1.0;
    float  pz  = 1.0 - pxy.x - pxy.y;
    float3 p   = float3(pxy.x, pxy.y, pz);
    float dotP = dot(p, p);
    float disc = max(1.0 - 2.0 * dotP, 0.0);
    float S    = (1.0 + sqrt(disc)) / max(dotP, 1e-6);
    float3 n;
    n.x = p.x * S - 1.0;
    n.y = 1.0 - p.y * S;
    n.z = p.z * S - 1.0;
    return normalize(n);
}
```
The new include must provide HLSL mirrors of `OctahedralEncode`, `PackAlphaBits`, and `PackSurface` (plus the `NamerConstants` values: `0x80`, `0x40`, `0x3F`, `0.5`, `0.1`, `63.0`, `255.0`, `1e-6`), transcribing the C# bodies above with `float3`/`float2`/`uint` types.

**Include resolution** — the include lives in `Compute/` next to `NAMERPack.compute`, so the compute file includes it relatively (`#include "NamerEncode.hlsl"`), matching the existing relative include in `Shaders/NAMER.shader` (line 109: `#include "NamerSurface.hlsl"`).

---

### `Compute/NAMERPack.compute` (MODIFY — three staged kernels)

**Analog:** `Shaders/NamerSurface.hlsl` (HLSL conventions) + the empty scaffold currently in `Compute/NAMERPack.compute` (lines 1-3):
```hlsl
// NAMERPack.compute
// GPU surface-packing kernels. Intentionally empty — kernels land in Phase 2.
```

**Kernel declaration pattern** (from RESEARCH.md §Pattern 3, D-10) — staged kernels, `[numthreads(8,8,1)]`, zero `groupshared`:
```hlsl
#pragma kernel CSNormalize
#pragma kernel CSOctahedralEncode
#pragma kernel CSSurfacePack

#include "NamerEncode.hlsl"   // mirrors Core/NamerFormat + NamerConstants

RWTexture2D<float4> _BaseColorIn;   // R16G16B16A16_SFloat, linear
RWTexture2D<float4> _BaseColorOut;
RWTexture2D<float4> _NormalTexel;   // raw DirectX [0,1] texel
RWTexture2D<float4> _Octahedral;
RWTexture2D<float4> _SurfaceOut;    // R8G8B8A8_UNorm, linear
float _AoUnmultiplyStrength;
int2 _Size;

[numthreads(8, 8, 1)]
void CSSurfacePack(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) return;
    float2 oct = _Octahedral[id.xy].rg;
    float  ao  = _Octahedral[id.xy].b;
    // pack via NamerEncode.hlsl helpers, mirroring NamerFormat.PackSurface EXACTLY
    _SurfaceOut[id.xy] = NamerPackSurface(oct, ao, metallic, emissive, roughness);
}
```

**sRGB rule (locked):** compute writes only linear formats (`R16G16B16A16_SFloat` intermediates, `R8G8B8A8_UNorm` final). sRGB→linear for base color is explicit in the normalize kernel via `_SourceIsSrgb ? SRGBToLinear(baseRaw.rgb) : baseRaw.rgb` (RESEARCH.md §Code Examples); normal/AO/metallic/roughness are never color-converted.

**Dispatch sizing (caller side)** — `shader.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1)` for `[numthreads(8,8,1)]`.

---

### `Editor/Pipeline/SourceInspector.cs` (NEW — C# API + menu item)

**Analog:** `Editor/NamerSmokeSetup.cs` (menu item, AssetDatabase, `Debug.Log`, namespace/imports).

**Imports pattern** (lines 1-6):
```csharp
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
```
`SourceInspector` needs the subset: `using UnityEditor; using UnityEngine;` (plus `PrefabUtility` via `UnityEditor`, `AssetDatabase` via `UnityEditor`, `GraphicsFormatUtility` via `UnityEngine.Experimental.Rendering`). It does **not** need `SceneManagement`/`Rendering.Universal` unless it also builds URP assets (it does not in Phase 2).

**Namespace + static-class + menu-item pattern** (lines 8, 16, 27-28):
```csharp
namespace GraffitiEntertainment.Namer.Editor
{
    public static class NamerSmokeSetup
    {
        [MenuItem("Tools/NAMER/Create Smoke Scene")]
        public static void CreateSmokeScene()
```
`SourceInspector` uses the same namespace (`GraffitiEntertainment.Namer.Editor`) and its entry menu item is `[MenuItem("Tools/NAMER/Inspect Selection")]` (D-01).

**AssetDatabase traversal pattern** (lines 49-55, `EnsureFolder`):
```csharp
private static void EnsureFolder()
{
    if (!AssetDatabase.IsValidFolder(NamerRoot))
    {
        AssetDatabase.CreateFolder("Assets", "NAMER");
    }
}
```
The inspector's folder source (D-02) replaces this with `AssetDatabase.FindAssets("t:Material", new[] { path })` + `AssetDatabase.GUIDToAssetPath` + `AssetDatabase.LoadAssetAtPath<Material>`, and FBX sources use `AssetDatabase.LoadAllAssetsAtPath<Material>(path)` (RESEARCH.md §Pattern 2).

**Log/output pattern** (line 46) — the manual-validation report (D-01) uses `Debug.Log`:
```csharp
Debug.Log("[NAMER] Smoke scene created at " + SmokeScenePath);
```

**Error-handling pattern** (lines 110-113) — explicit throw for a hard prerequisite, but the inspection path itself never throws on missing data (D-02/D-06):
```csharp
if (shader == null)
{
    throw new System.InvalidOperationException("Shader 'GraffitiEntertainment.Namer/NAMER' was not found. Ensure the NAMER shader compiled and imported.");
}
```
The `SourceInspector` must return warning entries in the result instead of throwing on unsupported/unknown shaders.

---

### `Editor/Pipeline/NamerSourceModel.cs` (NEW — serializable inspection result)

**Analog:** Core purity conventions (`Core/NamerFormat.cs` — a pure, namespace-scoped `public static class` with no `UnityEngine.Object` fields). No serializable data-holder exists in the package yet.

**Namespace convention** — `GraffitiEntertainment.Namer.Editor` (from `Editor/NamerSmokeSetup.cs` line 8). Note: if placed under `Editor/Pipeline/`, the planner may either keep the flat `GraffitiEntertainment.Namer.Editor` namespace or introduce `GraffitiEntertainment.Namer.Editor.Pipeline`; the existing convention is flat, so keep flat unless a sub-namespace is explicitly wanted.

**Shape requirements (from RESEARCH.md §Architecture Patterns):** the model is "serializable data, not log output" (CONTEXT §Integration Points). It holds per-material map refs (`Texture2D` + sRGB flag + channel), scalar fallbacks, and warnings. `Texture2D` is a `UnityEngine.Object`, so this class **must live in the Editor assembly, not Core** (Core purity rule — CONTEXT §Established Patterns).

---

### `Editor/Pipeline/NamerComputePipeline.cs` (NEW — dispatch harness)

**Analog:** none in repo — the package has zero existing `ComputeShader`/`RenderTexture`/`AsyncGPUReadback` usage (verified by grep). Planner uses RESEARCH.md §Standard Stack + §Code Examples as the reference.

**Assembly/namespace to match:** `GraffitiEntertainment.Namer.Editor` namespace, and the file is auto-included in the existing `GraffitiEntertainment.Namer.Editor.asmdef` (no new asmdef). The Editor asmdef already references `Unity.RenderPipelines.Universal.Runtime` and `Unity.RenderPipelines.Core.Runtime` (see Shared Patterns) — sufficient for compute dispatch.

**Core pattern to follow (RESEARCH.md §Code Examples, verified APIs):**
```csharp
shader.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);   // [numthreads(8,8,1)]
var req = AsyncGPUReadback.Request(outputRT, 0, TextureFormat.RGBA32);
```
Compute write targets: `RenderTexture` with `enableRandomWrite = true`, declared via `GraphicsFormat` (`R16G16B16A16_SFloat` intermediates, `R8G8B8A8_UNorm` final — never sRGB). Source textures are uploaded via `Graphics.Blit` into a linear intermediate (never mutating source `isReadable`/`sRGBTexture`).

---

### `Editor/Pipeline/ComputeTexturePool.cs` (NEW — RenderTexture lease/reuse)

**Analog:** none in repo. RESEARCH.md §Don't Hand-Roll mandates this as the sole allocator: "lease by descriptor, release in `finally`/`IDisposable`; assert live-RT count returns to baseline after a batch" (Pitfall 6). Uses `RenderTextureDescriptor` + `GraphicsFormat`, never `new RenderTexture(...)` per stage.

---

### `Tests/Editor/SourceInspectorTests.cs` (NEW — EditMode NUnit)

**Analog:** `Tests/Editor/BlenderGoldenVectorTests.cs` + `Tests/Editor/BitPackingTests.cs`.

**Imports + namespace pattern** (BlenderGoldenVectorTests.cs lines 1-5):
```csharp
using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace GraffitiEntertainment.Namer.Tests
{
    public class BlenderGoldenVectorTests
```

**Plain `[Test]` convention** (BitPackingTests.cs lines 46-51):
```csharp
[Test]
public void PackSurface_PreservesAoInBlueChannel()
{
    float4 surface = NamerFormat.PackSurface(new float3(0.5f, 0.5f, 1.0f), 0.25f, 0.0f, 0.0f, 0.5f);
    Assert.AreEqual(0.25, (double)surface.z, 1e-7);
}
```

**`[TestCase]` golden-vector convention** (BlenderGoldenVectorTests.cs lines 18-23) — reuse for property-table discovery cases if the planner wants data-driven map/scalar assertions.

**Immutability assertion to copy** — the Phase 1 suite enforces the contract that source assets are untouched; `SourceInspectorTests` must assert source `isReadable`/`sRGBTexture`/`.meta` unchanged after inspection (RESEARCH.md Pitfall 5). Use the `finally`-cleanup style from the Runtime smoke test for any temporary `UnityEngine.Object`s created in tests.

---

### `Tests/Editor/GpuGoldenTests.cs` (NEW — capability-gated `[UnityTest]`)

**Analog:** `Tests/Runtime/NamerRoundTripSmokeTests.cs` (async/`IEnumerator` + `try`/`finally` cleanup) combined with `Tests/Editor/BlenderGoldenVectorTests.cs` (golden values + D-14 tolerance).

**`[UnityTest]` + capability gate + readback pattern** (from RESEARCH.md §Pattern 4; the `try/finally` style below is from `NamerRoundTripSmokeTests.cs` lines 13-64):
```csharp
[UnityTest]
public IEnumerator SurfacePack_KernelMatchesCoreReference()
{
    if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
    {
        Assert.Ignore("[NAMER] Compute/async-readback unavailable on this backend — "
                    + "skipping GPU golden test (D-15: Metal is the verified target).");
        yield break;
    }
    // upload deterministic input, dispatch, then:
    var req = AsyncGPUReadback.Request(outputRT, 0, TextureFormat.RGBA32);
    yield return new WaitUntil(() => req.done);
    Assert.IsFalse(req.hasError, "AsyncGPUReadback reported an error");
    var data = req.GetData<Color32>();
    // D-14 tolerances applied against NamerFormat CPU reference
}
```

**`try`/`finally` cleanup for `UnityEngine.Object`s** (NamerRoundTripSmokeTests.cs lines 18-64):
```csharp
Texture2D surfaceMap = null;
Material material = null;
try { ... }
finally
{
    if (material != null) UnityEngine.Object.Destroy(material);
    if (surfaceMap != null) UnityEngine.Object.Destroy(surfaceMap);
}
yield return null;
```

**D-14 tolerance assertions** (BlenderGoldenVectorTests.cs lines 32-40 for float/golden, plus OctahedralRoundTripTests.cs line 67 for dot):
```csharp
Assert.AreEqual(expOctX, (double)oct.x, 1e-4);
Assert.AreEqual(expAlpha, (int)NamerFormat.PackAlphaBits(metallic, emissive, roughness));  // EXACT
...
Assert.GreaterOrEqual((double)dot(decoded, normal), 1.0 - 1e-3);
```
For GPU-vs-CPU, apply: packed alpha byte **exact** (`Assert.AreEqual` on the `Color32.a` byte); octahedral R/G and AO within `1/255.0`; decoded-normal dot ≥ `1.0 - 1e-3`.

---

## Shared Patterns

### Namespaces
- Core → `GraffitiEntertainment.Namer.Core` (`Core/NamerFormat.cs`, `Core/NamerConstants.cs`)
- Editor → `GraffitiEntertainment.Namer.Editor` (`Editor/NamerSmokeSetup.cs` line 8)
- Tests → `GraffitiEntertainment.Namer.Tests` (all test files)
- Runtime → rootNamespace `GraffitiEntertainment.Namer` (`Runtime/*.asmdef`)

### Assembly references (load-bearing — planner must edit the test asmdef)
`Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef` (lines 4-9) currently references:
```json
"references": [
    "UnityEngine.TestRunner",
    "UnityEditor.TestRunner",
    "GraffitiEntertainment.Namer.Core",
    "Unity.Mathematics"
],
```
It does **not** reference `GraffitiEntertainment.Namer.Editor`. Both `SourceInspectorTests.cs` and `GpuGoldenTests.cs` test classes that live in the Editor assembly, so the planner **must add `"GraffitiEntertainment.Namer.Editor"` to this references array**. Test asmdef also carries `"overrideReferences": true`, `"precompiledReferences": ["nunit.framework.dll"]`, `"defineConstraints": ["UNITY_INCLUDE_TESTS"]` (lines 13-16) — keep these intact.

Editor assembly reference list (`Editor/GraffitiEntertainment.Namer.Editor.asmdef` lines 4-9): `GraffitiEntertainment.Namer.Core`, `GraffitiEntertainment.Namer.Runtime`, `Unity.RenderPipelines.Universal.Runtime`, `Unity.RenderPipelines.Core.Runtime`; `"includePlatforms": ["Editor"]`. New Editor files need no asmdef change.

### Core purity (CONTEXT §Established Patterns)
`Core/` holds no `UnityEngine.Object` types; engine types live in Runtime/Editor. `NamerFormat`/`NamerConstants` are the single source of truth. No magic numbers — all thresholds/bits/levels come from `NamerConstants` (lines 11-33). The GPU kernels and `NamerEncode.hlsl` are a *mirror* of this math, never a re-derivation.

### Constants reference (mirror these exact values in HLSL)
`Core/NamerConstants.cs` (lines 11-33): `MetallicBit = 0x80`, `EmissiveBit = 0x40`, `RoughnessMask = 0x3F`, `MetallicThreshold = 0.5f`, `EmissiveThreshold = 0.1f`, `RoughnessLevels = 63.0f`, `AlphaByteScale = 255.0f`, `Epsilon = 1e-6f`.

### Menu item + logging
All Phase 2 manual-validation output uses `[MenuItem("Tools/NAMER/…")]` + `Debug.Log("[NAMER] …")` (from `Editor/NamerSmokeSetup.cs` lines 27-28, 46).

### Source immutability (Pitfall 5)
Never toggle source `isReadable`/`sRGBTexture`/importer settings. Read source pixels via `Graphics.Blit` into a temporary linear `RenderTexture`; record (never set) the source's sRGB flag. Phase 2 writes no assets and calls no `SetDirty`/`SaveAssets`.

## No Analog Found

Files with no close match in the codebase (planner should use RESEARCH.md patterns instead):

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Editor/Pipeline/NamerComputePipeline.cs` | service | transform (GPU dispatch/readback) | Zero existing `ComputeShader`/`RenderTexture`/`AsyncGPUReadback` usage in the package (grep-verified) |
| `Editor/Pipeline/ComputeTexturePool.cs` | utility | resource management | No `RenderTexture` pooling code exists yet |
| `Editor/Pipeline/NamerSourceModel.cs` | model | data holder | No serializable editor data class exists; follows Core purity + namespace convention only |

## Metadata

**Analog search scope:** `Packages/com.graffitientertainment.namer/` (all `.cs`, `.hlsl`, `.compute`, `.asmdef`, `package.json`); project-wide grep for `ComputeShader|RenderTexture|AsyncGPUReadback|ComputeTexturePool`.
**Files scanned:** 14 code files + 5 asmdef/package metadata.
**Pattern extraction date:** 2026-08-26

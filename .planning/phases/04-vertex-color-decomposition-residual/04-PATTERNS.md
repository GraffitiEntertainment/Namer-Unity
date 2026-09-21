# Phase 4: Vertex-Color Decomposition + Residual - Pattern Map

**Mapped:** 2026-08-31
**Files analyzed:** 14 (6 new, 8 modified)
**Analogs found:** 13 / 14

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Compute/NAMERDecomp.compute` | compute shader | GPU transform (per-pixel) | `Compute/NAMERAO.compute` | exact |
| `Compute/NAMERDecomp.hlsl` | HLSL include | GPU transform | `Compute/NAMERAO.hlsl` | exact |
| `Editor/Decompose/VertexColorFitter.cs` | service (Burst job) | transform (mesh-topology) | `Editor/Bake/NamerAOBaker.cs` | exact |
| `Editor/Decompose/MeshVertexSplitter.cs` | utility | transform (mesh-topology) | `Editor/Bake/NamerAOBaker.cs` | partial |
| `Editor/Decompose/NamerDecompPipeline.cs` | service (GPU harness) | GPU transform | `Editor/Pipeline/NamerAOPipeline.cs` | exact |
| `Tests/Editor/VertexColorDecompTests.cs` | test | n/a | `Tests/Editor/NamerAOBakeTests.cs` | exact |
| `Editor/Generation/AssetGenerator.cs` (EXTEND) | disk writer | file-I/O | self (`WriteSurfaceTexture`/`WriteBaseTexture`) | exact |
| `Editor/Settings/NamerProcessorSettings.cs` (EXTEND) | config | n/a | self (AO EditorPrefs keys) | exact |
| `Editor/UI/NamerEditorWindow.cs` (EXTEND) | controller (UI) | request-response | self (AO slider + debounce) | exact |
| `Editor/UI/NamerDebugChannelMaterial.cs` (EXTEND) | utility (debug material) | n/a | self (`SetChannel`) | exact |
| `Shaders/NamerDebugView.shader` (EXTEND) | shader | n/a | self (`Attributes` + channel branches) | exact |
| `Editor/Pipeline/NamerProcessor.cs` (EXTEND) | controller (orchestrator) | request-response | self (`BindGeneratedMaterials`) | exact |
| `Editor/NamerEditorConstants.cs` (EXTEND) | config | n/a | self (AO default constants) | exact |
| `Core/NamerConstants.cs` (EXTEND) | config | n/a | self (`AoFloor`) | exact |

---

## Pattern Assignments

### `Compute/NAMERDecomp.compute` (compute shader, GPU transform)

**Analog:** `Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute`

**Header + include convention** (NAMERAO.compute lines 1-19):
```hlsl
// NAMERAO.compute
// GPU ... kernels (Phase 03.1, plan 01).
// ...
// All write targets are LINEAR. ...

#include "NAMERAO.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

#pragma kernel CSLuminance
#pragma kernel CSAverage
#pragma kernel CSBlurH
#pragma kernel CSBlurV
#pragma kernel CSAoRemap
#pragma kernel CSJumpFloodInit
#pragma kernel CSJumpFloodStep
```
For decomp: `#include "NAMERDecomp.hlsl"`, `#pragma kernel CSRasterizeVertexColors`, `#pragma kernel CSResidual`, `#pragma kernel CSErrorHeatmap`, `#pragma kernel CSReduce`.

**RWTexture/Texture/StructuredBuffer declarations** (NAMERAO.compute lines 30-51):
```hlsl
RWTexture2D<float4> _Luma;
RWTexture2D<float4> _BlurA;
RWTexture2D<float4> _AoOut;
// ...
Texture2D<float4> _BaseColorIn;
```
For decomp the rasterize kernel reads vertex data from `StructuredBuffer<float3> _Verts`, `StructuredBuffer<float2> _Uvs`, `StructuredBuffer<int3> _Tris`, `StructuredBuffer<float4> _Colors` (RESEARCH §Code Examples lines 318-343), writes `_VcInterp` (R8G8B8A8_UNorm, alpha=coverage) and `_ResidualOut` (R16G16B16A16_SFloat).

**Uniform + size-guard convention** (NAMERAO.compute lines 54-56, 68-74):
```hlsl
uint2 _Size; // unsigned: compared against SV_DispatchThreadID (uint) in the kernel guards
// ...
[numthreads(8,8,1)]
void CSLuminance(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y)
    {
        return;
    }
    // ...
}
```
Every kernel uses `[numthreads(8,8,1)]` and the `_Size` guard. The residual kernel floor guard (`_VcFloor`) mirrors the `max(_LumaAverage, 1e-4)` divisor guard at NAMERAO.compute line 176.

**Viridis heatmap ramp** — no codebase analog; use the 5 anchors from `04-UI-SPEC.md` lines 93-101 (`#440154`, `#3B528B`, `#21918C`, `#5EC962`, `#FDE725`). Implement as a `NAMER_DECOMP_HEATMAP(x)` helper in `NAMERDecomp.hlsl` (mirror `NamerAoRemap` shape below).

---

### `Compute/NAMERDecomp.hlsl` (HLSL include)

**Analog:** `Packages/com.graffitientertainment.namer/Compute/NAMERAO.hlsl`

**Include guard + mirrored constants** (NAMERAO.hlsl lines 1-2, 12-15):
```hlsl
#ifndef GRAFFITI_ENTERTAINMENT_NAMER_AO_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_AO_INCLUDED
// ...
// Constants mirroring Core/NamerConstants.cs (D-09).
static const float NAMER_AO_FLOOR = 0.1;
```
For decomp: `#ifndef GRAFFITI_ENTERTAINMENT_NAMER_DECOMP_INCLUDED` + `static const float NAMER_VC_FLOOR = 1e-3;` mirroring a new `NamerConstants.VcFloor`. The comment "Do not edit the constants here independently of Core/NamerConstants.cs" (NAMERAO.hlsl line 10) is the convention to repeat.

**Helper-function shape** (NAMERAO.hlsl lines 28-49):
```hlsl
float NamerAoRemap(float ao, float strength, float contrast)
{
    ao = saturate(lerp(1.0, ao, strength));
    ao = saturate((ao - 0.5) * contrast + 0.5);
    return ao;
}
```

---

### `Editor/Decompose/VertexColorFitter.cs` (service, Burst, transform)

**Analog:** `Packages/com.graffitientertainment.namer/Editor/Bake/NamerAOBaker.cs`

**Imports** (NamerAOBaker.cs lines 1-8):
```csharp
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static Unity.Mathematics.math;
```
(`Unity.Jobs` + `[BurstCompile] IJobParallelFor` is the sanctioned Burst pattern; Burst/Mathematics already in `GraffitiEntertainment.Namer.Editor.asmdef` lines 9-10.)

**Result struct** (NamerAOBaker.cs lines 18-38): a `readonly struct` result (`NamerAOBakeResult`) owning a `NativeArray` that "The caller owns ... and must Dispose" — mirror as a `VertexColorFitResult` owning `NativeArray<float3> Colors` + `NativeArray<float> FitQuality` + `Width`/`Height`.

**Named constants** (NamerAOBaker.cs lines 54-65):
```csharp
public const int kBakeResolutionCap = 512;
public const float kCageOffset = 0.01f;
private const float kBarycentricAreaEps = 1e-8f;
private const float kBarycentricInsideEps = 1e-4f;
```
The fitter reuses `kBarycentricAreaEps`/`kBarycentricInsideEps` for its interior barycentric sampling (RESEARCH "Don't Hand-Roll" line 271: "Reuse the exact math in `NamerAOBaker.RayTriangleJob.TryReconstruct`"). Add `kGrid` (samples/triangle, ~16) and `kVcFloor` per RESEARCH §Pattern 1.

**Main-thread mesh read → NativeArray** (NamerAOBaker.cs lines 113-131, 264-295):
```csharp
// Unity Mesh reads happen on the main thread.
Vector3[] lv = lowMesh.vertices;
Vector3[] ln = lowMesh.normals;
Vector2[] lu = lowMesh.uv;
int[] lt = lowMesh.triangles;
// ...
lowVerts = ToFloat3(lv);
lowNormals = ToFloat3(ln);
lowUvs = ToFloat2(lu);
lowTris = ToInt3(lt);
```
`ToFloat3`/`ToFloat2`/`ToInt3` helpers (lines 264-295) are the exact conversion pattern to copy for `(verts, uvs, tris)` and the base-texel `NativeArray<Color32>`.

**try/finally dispose discipline** (NamerAOBaker.cs lines 111-224): every `NativeArray` declared as `default`, disposed in `finally` guarded by `if (x.IsCreated) x.Dispose();` — except the returned result array which is "intentionally NOT disposed" (line 223). Copy this for the fit output arrays.

**Burst job struct** (NamerAOBaker.cs lines 297-395):
```csharp
[BurstCompile]
private struct RayTriangleJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> LowVertices;
    [ReadOnly] public NativeArray<float2> LowUvs;
    [ReadOnly] public NativeArray<int3> LowTriangles;
    // ...
    [WriteOnly]
    [NativeDisableParallelForRestriction]
    public NativeArray<float> AoOut;
    public void Execute(int index) { ... }
}
```
The fitter's `FitJob : IJobParallelFor` mirrors this exactly: `[ReadOnly]` verts/uvs/tris/base-texels, `[NativeDisableParallelForRestriction]` write arrays for the per-vertex 3×3 `AᵀA` accumulation (RESEARCH §Pattern 1 lines 190-216). Note the multi-vertex write within a per-triangle job requires the same `[NativeDisableParallelForRestriction]` justification (each triangle writes only its 3 corner vertices; a deterministic single dispatch means no write collision).

**Schedule pattern** (NamerAOBaker.cs line 177):
```csharp
job.Schedule(indexCount, kInnerLoopBatchCount).Complete();
```

---

### `Editor/Decompose/MeshVertexSplitter.cs` (utility, transform) — PARTIAL

**No exact analog exists** (no mesh-topology remap/split anywhere in the package). Use two partial patterns:

1. **Mesh attribute read → NativeArray**: reuse `NamerAOBaker.ToFloat3`/`ToFloat2`/`ToInt3` and `ComputeSmoothNormals` (NamerAOBaker.cs lines 242-295) for reading `vertices`/`normals`/`tangents`/`uv`/`colors32`/`boneWeights` into splitter working buffers.
2. **Mesh asset write**: no codebase analog — implement the high-level `SetVertices`/`SetTriangles` write from RESEARCH §Pattern 3 (lines 242-254), which mirrors the "prefer high-level mesh API over `SetIndexBufferData`" anti-pattern (RESEARCH Pitfall 6, lines 310-314):
```csharp
Mesh outMesh = new Mesh { name = sanitized };
if (vertexCount > ushort.MaxValue) outMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
outMesh.SetVertices(verts);
outMesh.SetNormals(normals);
outMesh.SetTangents(tangents);
outMesh.SetUVs(0, uv0);
outMesh.colors32 = colors;                 // RGB = fitted color, A = fit-quality (D-04)
if (boneWeights != null) outMesh.boneWeights = boneWeights;
outMesh.subMeshCount = subMeshCount;
for (int i = 0; i < subMeshCount; i++) outMesh.SetTriangles(subIndices[i], i);
outMesh.bindposes = bindposes;              // skinned only
outMesh.RecalculateBounds();
```
The disk write (`AssetDatabase.CreateAsset(outMesh, meshPath)`) is deferred to `AssetGenerator` (sole disk writer, GEN-01). The splitter returns the split arrays; `AssetGenerator` owns persistence.

**Weld key convention:** weld by `(position, normal, tangent, uv)`; split further on color discontinuity (RESEARCH §Pattern 3 lines 238-239). Preserve `boneWeights`/`bindposes` (skinned) and warn+skip blend shapes (RESEARCH Open Question 2).

---

### `Editor/Decompose/NamerDecompPipeline.cs` (service, GPU harness)

**Analog:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs`

**Class skeleton + IDisposable + pool** (NamerAOPipeline.cs lines 27-45, 55-70):
```csharp
public sealed class NamerAOPipeline : IDisposable
{
    private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute";
    // ...
    private readonly ComputeTexturePool _pool = new ComputeTexturePool();
    private readonly ComputeShader _compute;
    private readonly int _kernelLuminance;
    // ...

    public NamerAOPipeline()
    {
        _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
        if (_compute == null)
        {
            throw new InvalidOperationException("NAMER AO compute shader not found at " + ComputeShaderPath);
        }
        _kernelLuminance = _compute.FindKernel("CSLuminance");
        // ...
    }
```
For decomp: `ComputeShaderPath = "Packages/.../Compute/NAMERDecomp.compute"`; kernels `CSRasterizeVertexColors`, `CSResidual`, `CSErrorHeatmap`, `CSReduce`.

**Pool Lease/Release + try/finally** (NamerAOPipeline.cs lines 100-161): lease all intermediates up-front, dispatch, return one result target, release the rest in `finally`. The residual RT is the returned pool-leased target; `ReleaseAo` (line 166-169) becomes `ReleaseResidual`.

**Descriptor + Dispatch** (NamerAOPipeline.cs lines 570-589):
```csharp
private void Dispatch(int kernel, int w, int h)
{
    _compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
}

private static RenderTextureDescriptor NewDescriptor(int width, int height, GraphicsFormat format)
{
    return new RenderTextureDescriptor(width, height, format, 0)
    {
        enableRandomWrite = true,
        sRGB = false,
        useMipMap = false,
        autoGenerateMips = false,
    };
}
```
Residual uses `GraphicsFormat.R16G16B16A16_SFloat`; vc-interp uses `GraphicsFormat.R8G8B8A8_UNorm` (linear).

**Hierarchical scalar reduction for error stats** (NamerAOPipeline.cs lines 514-568): `ReduceToScalarMean` + `ReadBackLumaAverage` is the exact shape for the coverage/avg/max error reduce (`CSReduce`). The `while (srcW > 1 || srcH > 1)` block-average loop (lines 521-540) and the one-shot `AsyncGPUReadback` + partial-region sum (lines 542-568) are copied directly.

**Adaptive search loop** (D-16): the halving decision is a small CPU loop over `{source/2, source/4, ..., 128}` re-using `NamerRawCopy.shader` `Graphics.Blit` for bilinear downsample/upsample (same raw-copy pattern as `NamerAOPipeline.BakeAndUpload` line 258 and `NamerComputePipeline.Upload` lines 284-309).

**Readback contract** — reuse `NamerComputePipeline.RequestReadback` (`NamerComputePipeline.cs` lines 189-192):
```csharp
public static AsyncGPUReadbackRequest RequestReadback(RenderTexture source, int mip = 0, TextureFormat format = TextureFormat.RGBA32)
```

---

### `Editor/Generation/AssetGenerator.cs` (EXTEND, disk writer)

**Analog:** self — `WriteSurfaceTexture`/`WriteBaseTexture`/`WriteMaterial` + `EnsureWritableTarget`/`Stamp`.

**New `WriteResidualExr`** — mirror `WriteSurfaceTexture` (AssetGenerator.cs lines 306-330) but EXR + linear:
```csharp
private static void WriteSurfaceTexture(Texture2D texture, string path, bool overwriteGenerated)
{
    EnsureWritableTarget(path, overwriteGenerated);
    byte[] png = ImageConversion.EncodeToPNG(texture);
    File.WriteAllBytes(path, png);
    AssetDatabase.ImportAsset(path);
    TextureImporter importer = AssetImporter.GetAtPath(path) as TextureImporter;
    // ... GEN-04: linear, uncompressed, point, no mips.
    importer.sRGBTexture = false;
    importer.textureCompression = TextureImporterCompression.Uncompressed;
    importer.filterMode = FilterMode.Point;
    importer.mipmapEnabled = false;
    importer.wrapMode = TextureWrapMode.Repeat;
    importer.SaveAndReimport();
    Stamp(AssetDatabase.LoadAssetAtPath<Texture2D>(path));
}
```
`WriteResidualExr` replaces `EncodeToPNG` with `ImageConversion.EncodeToEXR` on an RGBAHalf `Texture2D` (read back the residual RT as `TextureFormat.RGBAHalf`, per RESEARCH lines 269/303-304), keeps `sRGBTexture = false`, and stamps. The residual texture must be `isReadable = true` before encoding.

**New `WriteMeshAsset`** — no analog; uses the RESEARCH §Pattern 3 mesh write and `AssetDatabase.CreateAsset(outMesh, meshPath)` (the `CreateAsset` pattern already present for materials at line 417), followed by `Stamp(...)`.

**Path composition** — reuse `ComposePath` (lines 188-198) with new suffixes `"_Residual.exr"` and `".asset"` for the mesh; run `ValidateComposedPath` + `EnsureWritableTarget` on both (security: RESEARCH §Security Domain lines 439-444).

**Extend `NamerGeneratedAsset`** (lines 512-517) with `MeshPath` + `ResidualTexturePath`.

**Material bind (D-07)** — `WriteMaterial` line 378 currently binds the base PNG to `_BaseResidualMap`; add an overload/parameter so decomposed output binds the residual EXR instead (`material.SetTexture("_BaseResidualMap", residualTexture)`).

---

### `Editor/Settings/NamerProcessorSettings.cs` (EXTEND)

**Analog:** self — AO EditorPrefs keys (lines 14-21, 51-77).
```csharp
private const string AoStrengthKey = "NamerProcessor.AoStrength";
// ...
public float AoStrength
{
    get { return EditorPrefs.GetFloat(AoStrengthKey, NamerEditorConstants.DefaultAoStrength); }
    set { EditorPrefs.SetFloat(AoStrengthKey, value); }
}
```
Add three keys with the same shape:
- `NamerProcessor.DecompositionEnabled` → `EditorPrefs.GetBool(..., NamerEditorConstants.DefaultDecompositionEnabled)` (default false, D-05).
- `NamerProcessor.ErrorThreshold` → `EditorPrefs.GetFloat(..., NamerEditorConstants.DefaultErrorThreshold)` (default 0.02, D-15).
- `NamerProcessor.ResidualResolution` → `EditorPrefs.GetInt(..., NamerEditorConstants.DefaultResidualResolution)` (ladder index 0 = Auto, D-17).

---

### `Editor/UI/NamerEditorWindow.cs` (EXTEND)

**Analog:** self — AO slider + debounce (lines 587-641), `DebugChannelLabels` (lines 22-25), `MarkDirty` (lines 425-429), toolbar (lines 541-554).

**Debug channel labels** (lines 22-25) — append 3:
```csharp
private static readonly string[] DebugChannelLabels =
{
    "Shaded", "Base Color", "AO", "Normal", "Roughness", "Metallic", "Emissive",
    "Vertex Colors", "Residual", "Error Heatmap",
};
```

**AO slider + debounce pattern** (lines 587-599) — copy for the Error Threshold slider (0.0-0.10, default 0.02):
```csharp
float newAo = EditorGUILayout.Slider(
    new GUIContent("AO Un-multiply Strength", "..."),
    _aoUnmultiplyStrength, 0f, 1f);
if (!Mathf.Approximately(newAo, _aoUnmultiplyStrength))
{
    _aoUnmultiplyStrength = newAo;
    _settings.AoUnmultiplyStrength = newAo;
    _afterPanelState.MarkTweaking();
    MarkDirty();
}
```

**Toggle pattern** (from `DrawOutputSection` lines 678-682) — for the "Vertex Color Decomposition" toggle:
```csharp
bool overwrite = EditorGUILayout.Toggle("Overwrite generated", _settings.OverwriteGenerated);
if (overwrite != _settings.OverwriteGenerated)
{
    _settings.OverwriteGenerated = overwrite;
}
```
Wrap the threshold slider + resolution `Popup` in `EditorGUI.BeginDisabledGroup(!_decompositionEnabled)` (the "no dead controls" rule, UI-SPEC lines 128-129, 169). The ladder `Popup` mirrors `GUILayout.Toolbar` usage (lines 541-554).

**Disabled-group scope** (line 585): `EditorGUI.BeginDisabledGroup(inspection == null || _busy);` — the decomposition group lives inside this same disabled group.

**Stats block** — five `EditorGUILayout.LabelField` rows (Body `EditorStyles.label`, sub-header `EditorStyles.boldLabel`) per UI-SPEC §Component Inventory #15 / §Copywriting lines 225-231; hidden when toggle OFF.

**`MarkDirty()`** (lines 425-429) is the debounce trigger all three new controls call; `Tick()` (lines 238-262) drives the 300 ms `NamerEditorConstants.DebounceSeconds` gate — no new debounce machinery.

---

### `Editor/UI/NamerDebugChannelMaterial.cs` (EXTEND)

**Analog:** self — property-ID caching (lines 14-16), `SetChannel` (lines 47-55).
```csharp
private static readonly int DebugChannelId = Shader.PropertyToID("_DebugChannel");
// ...
public void SetChannel(Material material, int channel)
{
    if (material == null) { throw new ArgumentNullException(nameof(material)); }
    material.SetFloat(DebugChannelId, channel);
}
```
`SetChannel` is channel-count agnostic; no change needed beyond the shader gaining channels 6/7/8 (the window already maps `Mathf.Max(0, _debugChannel - 1)` at line 311, and channels 6/7/8 now resolve to `_DebugChannel` 5/6/7 in the shader — update the debug shader branch, not this class). If a per-channel vertex-color bind is needed, add a `SetVertexColors(Material, Mesh)` or a `_HasVertexColors` float setter following the `SetTextures` shape (lines 35-44).

---

### `Shaders/NamerDebugView.shader` (EXTEND)

**Analog:** self — `Attributes`/`Varyings` (lines 52-62) + `DebugFrag` branch (lines 88-112); COLOR semantic from `NAMER.shader` (lines 113-123).

**Add COLOR semantic to `Attributes`** (NamerDebugView.shader lines 52-56; the runtime reference is NAMER.shader lines 113-123):
```hlsl
struct Attributes
{
    float4 positionOS : POSITION;
    float2 texcoord   : TEXCOORD0;
    float4 color      : COLOR;      // NEW — carries the vertex-color stream (channel 6)
};
struct Varyings
{
    float2 uv         : TEXCOORD0;
    float4 vertexColor : TEXCOORD1; // NEW
    float4 positionCS : SV_POSITION;
};
```
`DebugVert` (lines 64-70) copies `output.vertexColor = input.color;` and keeps `output.positionCS = TransformObjectToHClip(input.positionOS.xyz);`.

**Extend the channel branch** (lines 88-112) — append channels 6/7/8 after Emissive:
```hlsl
else if (_DebugChannel < 5.5)  { channel = emissiveFlag.rrr; }      // 5: Emissive (existing)
else if (_DebugChannel < 6.5)  { channel = input.vertexColor.rgb; } // 6: Vertex Colors (white where no stream)
else if (_DebugChannel < 7.5)  { channel = baseResidual.rgb; }      // 7: Residual (== base when non-decomposed)
else                           { channel = NAMER_DECOMP_HEATMAP(err(input)); } // 8: Error Heatmap (viridis)
```
The shader must keep `#include "NamerSurface.hlsl"` and the `NAMER_DECODE_SURFACE` call (line 83) so views cannot drift from runtime decode. The heatmap ramp lives in `NAMERDecomp.hlsl` and is `#include`d here if channel 8 is rendered in-editor (mirror the NAMER.shader `#include "NamerSurface.hlsl"` at line 109).

---

### `Editor/Pipeline/NamerProcessor.cs` (EXTEND)

**Analog:** self — `Process` orchestration (lines 40-149) + `BindGeneratedMaterials` (lines 157-209).

**D-05 gate + decomposition stage** — insert between `NamerComputePipeline.Process` (line 109) and `generator.Generate` (line 112), gated on `settings.DecompositionEnabled` (RESEARCH §System Architecture Diagram lines 121-168):
```csharp
NamerComputeResult computeResult = pipeline.Process(inspection);
try
{
    NamerGeneratedAsset asset = generator.Generate(computeResult, inspection, settings, destinationFolder);
    result.GeneratedAssets.Add(asset);
}
```
The decomposition stage slots here: `VertexColorFitter` + `MeshVertexSplitter` + `NamerDecompPipeline` run between the compute result (linear base readback) and `AssetGenerator`.

**Mesh swap (Pitfall 3)** — extend `BindGeneratedMaterials` (lines 157-209). Today it swaps only `sharedMaterials` (lines 185-208). Add a `MeshFilter.sharedMesh` / `SkinnedMeshRenderer.sharedMesh` swap to the generated split mesh when decomposition is ON (and record the original mesh for D-14 switch-back). The `foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))` traversal (line 185) already reaches both mesh types; add:
```csharp
if (renderer is MeshFilter mf) { mf.sharedMesh = generatedMesh; }
else if (renderer is SkinnedMeshRenderer smr) { smr.sharedMesh = generatedMesh; }
```
**Preflight** — extend `AssetGenerator.PreflightTargets` (line 98) so the new `_Residual.exr` + `.asset` paths are also pre-flighted (`EnsureWritableTarget`) before any GPU work.

---

### `Tests/Editor/VertexColorDecompTests.cs` (test)

**Analog:** `Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs`

**Headless Burst tests are pure `[Test]`** (NamerAOBakeTests.cs lines 30-62):
```csharp
[Test]
public void Bake_IsDeterministic()
{
    // ... two bakes, element-wise equality, Dispose in finally
}
```
The fit tests run the `VertexColorFitter` headless (no GPU): deterministic two-run equality, "flat quad + constant base color → per-vertex color ≈ that color", "quantized Color32 fit → `residual × quantizedVcInterp ≈ base`" (RESEARCH Pitfall 2 invariant). Seam-split tests assert weld keys and attribute preservation.

**Mesh fixture helpers** (NamerAOBakeTests.cs lines 357-458): `CreateFlatQuad` / `CreateRoofQuad` / `CreateSubdividedRoof` build `Mesh` objects in-code (set `vertices`/`normals`/`uv`/`triangles`, `RecalculateBounds()`). Copy `CreateFlatQuad` for a quad with a UV seam to exercise `MeshVertexSplitter`.

**GPU tests are `[UnityTest]` with the capability gate** (NamerAOBakeTests.cs lines 27-28, 263-268):
```csharp
private static bool ComputeAvailable =>
    SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;
// ...
[UnityTest]
public IEnumerator Process_WithCachedBake_RoutesBakedAoIntoPackedSurface()
{
    if (!ComputeAvailable)
    {
        Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping ...");
        yield break;
    }
    // ...
}
```
Residual GPU tests use the same `[UnityTest]` + `ComputeAvailable` gate, and the readback helper `ReadBackColor32` (lines 460-474) plus `Destroy(params Object[])` (lines 476-485).

**Namespace + test asmdef**: tests live in `namespace GraffitiEntertainment.Namer.Tests` (line 10) under `Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef`; the asmdef already references the editor assembly, so the new `VertexColorFitter`/`MeshVertexSplitter`/`NamerDecompPipeline` types are visible without asmdef changes.

---

### `Editor/NamerEditorConstants.cs` (EXTEND)

**Analog:** self — AO default constants (lines 35-44).
```csharp
public const float DefaultAoStrength = 1f;
```
Add (all consumed by `NamerProcessorSettings` defaults):
```csharp
public const bool  DefaultDecompositionEnabled = false;   // D-05 opt-in, OFF by default
public const float DefaultErrorThreshold = 0.02f;         // D-15
public const int   DefaultResidualResolution = 0;         // 0 = Auto ladder (D-17)
```

---

### `Core/NamerConstants.cs` (EXTEND)

**Analog:** self — `AoFloor` (line 36).
```csharp
/// <summary>Un-multiply divisor floor for extracted/baked AO (D-09); divisor-side only — the packed B stores the raw AO.</summary>
public const float AoFloor = 0.1f;
```
Add the vertex-color divisor floor (RESEARCH Pitfall 1), mirrored in `NAMERDecomp.hlsl`:
```csharp
/// <summary>Vertex-color interpolation floor for the residual quotient (D-01); prevents divide blow-up at near-zero vcInterp.</summary>
public const float VcFloor = 1e-3f;
```
Keep the documented "mirrored manually in HLSL" convention (NamerConstants.cs lines 4-7) — edit `NAMERDecomp.hlsl` and this file together, never one without the other.

---

## Shared Patterns

### Burst + Collections mesh-processing (CPU tier)
**Source:** `Editor/Bake/NamerAOBaker.cs` lines 1-8, 87-225, 297-395
**Apply to:** `VertexColorFitter.cs`, `MeshVertexSplitter.cs`
- `using Unity.Burst; Unity.Collections; Unity.Jobs; Unity.Mathematics; static Unity.Mathematics.math;`
- Main-thread `Mesh` reads → `NativeArray<float3/float2/int3>`; `[BurstCompile] private struct ... : IJobParallelFor` with `[ReadOnly]` inputs and `[NativeDisableParallelForRestriction]` writes; `try/finally` dispose with `if (x.IsCreated) x.Dispose();`.

### GPU compute harness (GPU tier)
**Source:** `Editor/Pipeline/NamerAOPipeline.cs` lines 27-70, 100-161, 514-589
**Apply to:** `NamerDecompPipeline.cs`
- `ComputeShader` loaded by `AssetDatabase.LoadAssetAtPath<ComputeShader>(path)` with a null-throw; kernels resolved via `FindKernel`; `ComputeTexturePool` lease/release; `GraphicsFormat` descriptors (`enableRandomWrite = true, sRGB = false`); `[numthreads(8,8,1)]` dispatch `(w+7)/8`; hierarchical block-average reduction.

### Sole disk writer (file-I/O)
**Source:** `Editor/Generation/AssetGenerator.cs` lines 36-155, 306-352, 474-506
**Apply to:** `AssetGenerator.cs` new methods (mesh + EXR), and the path-validation surface reused by `NamerProcessor.PreflightTargets`
- `EnsureWritableTarget` (overwrite gate, `NamerGenerated` stamp) + `ValidateComposedPath`/`ValidateDestinationFolder` (confinement) before every write; `File.WriteAllBytes` / `AssetDatabase.CreateAsset` only here.

### EditorPrefs settings
**Source:** `Editor/Settings/NamerProcessorSettings.cs` lines 14-21, 51-77; `Editor/NamerEditorConstants.cs` lines 35-44
**Apply to:** the three new decomposition settings
- `private const string XKey = "NamerProcessor.X";` + `EditorPrefs.Get*`/`Set*` with a default from `NamerEditorConstants`.

### Debounced in-memory preview
**Source:** `Editor/UI/NamerEditorWindow.cs` lines 238-262, 425-429, 587-641
**Apply to:** the decomposition toggle / threshold / resolution controls (D-08/D-12)
- `MarkDirty()` sets `_dirty` + `_lastChange`; `Tick()` re-runs `RecomputePreview()` after `NamerEditorConstants.DebounceSeconds` (0.3 s). Never writes to disk.

### Debug channel material + shader
**Source:** `Editor/UI/NamerDebugChannelMaterial.cs` lines 14-16, 35-55; `Shaders/NamerDebugView.shader` lines 52-112
**Apply to:** channels 6/7/8 (Vertex Colors / Residual / Error Heatmap)
- Property-ID cache via `Shader.PropertyToID`, `SetChannel`/`SetTextures`, shader `Attributes` gains `COLOR` semantic, `DebugFrag` appends `_DebugChannel` branches; keep `NAMER_DECODE_SURFACE` so views cannot drift from runtime.

### HLSL-mirrored constants
**Source:** `Core/NamerConstants.cs` lines 4-7, 36; `Compute/NAMERAO.hlsl` lines 12-15
**Apply to:** `NAMER_VC_FLOOR` in `NAMERDecomp.hlsl` (mirrors `NamerConstants.VcFloor`)
- Named C# const + matching `static const float` in an HLSL include guard, documented "do not edit independently".

---

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Editor/Decompose/MeshVertexSplitter.cs` (mesh write portion) | utility | transform | No mesh-topology remap/split exists in the package. Use RESEARCH §Pattern 3 (lines 242-254) for the `Mesh` API write, and `NamerAOBaker`'s attribute-read helpers. The `AssetDatabase.CreateAsset(mesh, ...)` persistence goes in `AssetGenerator`. |
| `NAMERDecomp.compute` viridis heatmap ramp | compute | GPU transform | No color-ramp helper exists. Use the 5 anchors from `04-UI-SPEC.md` lines 93-101. |

---

## Metadata

**Analog search scope:** `Packages/com.graffitientertainment.namer/{Compute,Core,Editor,Shaders,Tests}`
**Files scanned:** 22 source + 1 UI-SPEC + 2 phase docs (CONTEXT/RESEARCH)
**Pattern extraction date:** 2026-08-31

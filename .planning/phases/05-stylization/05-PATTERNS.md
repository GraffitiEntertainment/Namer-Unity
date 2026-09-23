# Phase 5: Stylization - Pattern Map

**Mapped:** 2026-09-23
**Files analyzed:** 21 (6 new source, 9 modified source, 5 new test, 1 doc)
**Analogs found:** 19 / 21 (2 partial: profile SO + custom inspector have no in-house analog)

All paths below are relative to `Packages/com.graffitientertainment.namer/` unless noted. All line numbers were verified in-tree this session (develop branch).

## File Classification

| New/Modified File | Role | Data Flow | Closest Analog | Match Quality |
|-------------------|------|-----------|----------------|---------------|
| `Editor/Stylize/NamerStyleProfile.cs` (NEW) | model (ScriptableObject) | CRUD (serialized asset) | none in-house (see No Analog) | none — role-new |
| `Editor/Stylize/NamerStyleExtractor.cs` (NEW) | service (CPU Burst) | batch (deterministic) | `Editor/Bake/NamerAOBaker.cs` | exact |
| `Editor/Stylize/NamerStylizePipeline.cs` (NEW) | service (GPU harness) | transform (ping-pong compute) | `Editor/Pipeline/NamerAOPipeline.cs` | exact |
| `Editor/Stylize/NamerUvIslands.cs` (NEW) | utility (CPU mesh graph) | transform (union-find) | `Editor/Decompose/MeshVertexSplitter.cs` | role-match |
| `Compute/NAMERStylize.compute` (NEW) | service (compute kernels) | transform (per-texel GPU) | `Compute/NAMERDecomp.compute` | exact |
| `Editor/UI/NamerStyleProfileEditor.cs` (NEW) | component (custom inspector) | request-response (IMGUI) | none in-house (IMGUI idioms from `NamerEditorWindow.cs`) | partial |
| `Editor/Pipeline/NamerComputePipeline.cs` (MOD) | service (GPU orchestrator) | transform | itself (`Process` structure) | exact (in-place extension) |
| `Editor/Pipeline/NamerAOPipeline.cs` (MOD) | service (GPU harness) | transform | itself (`ApplyStrengthContrastDirect`) | exact (in-place extension) |
| `Editor/Pipeline/NamerProcessor.cs` (MOD) | controller (bake entry) | request-response | itself (settings→inspection plumb) | exact (in-place extension) |
| `Editor/UI/NamerEditorWindow.cs` (MOD) | component (editor window) | event-driven (debounce) | itself (foldout/gate idioms) | exact (in-place extension) |
| `Editor/Settings/NamerProcessorSettings.cs` (MOD) | config (EditorPrefs) | CRUD | itself (property pattern) | exact (in-place extension) |
| `Editor/NamerEditorConstants.cs` (MOD) | config (constants) | n/a | itself (const pattern) | exact (in-place extension) |
| `Compute/NAMERPack.compute` (MOD) | service (compute kernels) | transform | itself (`CSSurfacePack`/`CSOctahedralEncode`) | exact (in-place extension) |
| `Compute/NAMERAO.compute` (MOD) | service (compute kernels) | transform | itself (`CSAoRemap` call site) | exact (in-place extension) |
| `Compute/NAMERAO.hlsl` (MOD) | utility (HLSL include) | transform | itself (`NamerAoRemap`) | exact (in-place extension) |
| `Tests/Editor/NamerStyleProfileTests.cs` (NEW) | test | CRUD | `Tests/Editor/NamerAOBakeTests.cs` (pure `[Test]` shape) | role-match |
| `Tests/Editor/NamerStyleExtractionTests.cs` (NEW) | test | batch | `Tests/Editor/NamerAOBakeTests.cs:48-80` (`Bake_IsDeterministic`) | exact |
| `Tests/Editor/NamerUvIslandsTests.cs` (NEW) | test | batch | `Tests/Editor/NamerAOBakeTests.cs` (mesh fixtures, pure CPU) | exact |
| `Tests/Editor/NamerStylizeKernelTests.cs` (NEW) | test | streaming (GPU readback) | `Tests/Editor/ResidualPipelineTests.cs:742-796` | exact |
| `Tests/Editor/NamerStylizationIntegrationTests.cs` (NEW) | test | request-response (E2E) | `Tests/Editor/NamerAOBakeTests.cs` (E2E + EditorPrefs pinning) | role-match |
| `.planning/phases/04.2-*/04.2-CONTEXT.md` + test comments (MOD) | doc | n/a | D-02 dividend addendum inventory (RESEARCH) | n/a |

Note: `Compute/NamerEncode.hlsl` is a **read-only anchor** this phase — the D-07 uniforms land inside `NAMERPack.compute` kernel bodies *before* the `NamerPackSurfaceFromOct` / `NamerOctahedralEncode` calls; `NamerEncode.hlsl` itself needs no edit.

## Pattern Assignments

### `Editor/Stylize/NamerStyleExtractor.cs` (service, CPU Burst, deterministic batch)

**Analog:** `Editor/Bake/NamerAOBaker.cs` — copy its class skeleton verbatim (static class, `kCamelCase` public consts, fixed tables, cancellable slabs).

**Class shape + fixed tables** (`NamerAOBaker.cs:52-72`):
```csharp
public static class NamerAOBaker
{
    public const int kBakeResolutionCap = 512;
    // ...
    private const float kGoldenRatio = 0.61803398875f;

    /// <summary> Fixed 64 cosine-weighted hemisphere directions ... built once with a
    /// deterministic golden-ratio sequence (never <see cref="System.Random"/>). </summary>
    private static readonly float3[] DirectionTable = BuildDirectionTable();
```

**Imports pattern** (`NamerAOBaker.cs:1-8`):
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

**Cancellable slab loop — synchronous, off-debounce, headless-testable** (`NamerAOBaker.cs:153-204`):
```csharp
for (int rowStart = 0; rowStart < res; rowStart += kRowsPerSlab)
{
    // ... schedule + Complete() one slab ...
    float progress = (float)(rowStart + rowCount) / res;
    bool cancelled;
    if (shouldCancel != null)
    {
        cancelled = shouldCancel();               // Injectable headless path (tests): no editor UI.
    }
    else
    {
        cancelled = EditorUtility.DisplayCancelableProgressBar(   // Interactive-editor path.
            "Baking NAMER AO", "...", progress);
    }
    if (cancelled)
    {
        if (ao.IsCreated) { ao.Dispose(); }
        return new NamerAOBakeResult(default, res, res, cancelled: true);
    }
}
```

**Burst job shape** (`NamerAOBaker.cs:297-319`):
```csharp
[BurstCompile]
private struct RayTriangleJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> LowVertices;
    // ...
    [WriteOnly]
    [NativeDisableParallelForRestriction]
    public NativeArray<float> AoOut;
```

**Ownership/finally discipline** (`NamerAOBaker.cs:213-224`): every intermediate `NativeArray` disposed in `finally`; the result array is `Allocator.Persistent` and intentionally NOT disposed (returned to caller). Replicate for `NamerStyleExtractResult`.

**Fixed-sample precedent for the downsample cap** (`VertexColorFitter.cs:104`): `public const int kGrid = 16;` — "Interior barycentric samples per triangle". The extractor's per-ref downsample cap is the same named-constant fixed-grid discipline. `VertexColorFitter.cs:98-100` states the determinism contract in the doc header: "Pure CPU, deterministic (fixed grid + fixed float math) and headless".

**Determinism law (from `NamerAOBaker.cs:70-71` doc):** fixed tables + fixed float math; never `System.Random`; fixed list order for refs; index-ordered tiebreaks in k-means/histogram.

---

### `Editor/Stylize/NamerStylizePipeline.cs` (service, GPU harness, transform)

**Analog:** `Editor/Pipeline/NamerAOPipeline.cs` — copy constructor/dispatch/pool/NewDescriptor shape; ping-pong loop from its JFA pass.

**Class skeleton** (`NamerAOPipeline.cs:31-74`):
```csharp
public sealed class NamerAOPipeline : IDisposable
{
    private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute";
    // ... named kCamelCase constants (lines 35-41) ...
    private readonly ComputeTexturePool _pool = new ComputeTexturePool();
    private readonly ComputeShader _compute;
    private readonly int _kernelAoRemap;
    // ...

    public NamerAOPipeline()
    {
        _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
        if (_compute == null)
        {
            throw new InvalidOperationException("NAMER AO compute shader not found at " + ComputeShaderPath);
        }
        _kernelAoRemap = _compute.FindKernel("CSAoRemap");
    }
```

**Ping-pong src/dst alternation over pool leases** (`NamerAOPipeline.cs:277-295` — the bilateral's 2-3 iterations copy this):
```csharp
RenderTexture src = jfaA;
RenderTexture dst = jfaB;
// ...
for (; step >= 1; step >>= 1)
{
    _compute.SetInt("_Step", step);
    _compute.SetTexture(_kernelJumpFloodStep, "_Jfa", src);
    _compute.SetTexture(_kernelJumpFloodStep, "_JfaOut", dst);
    Dispatch(_kernelJumpFloodStep, w, h);

    RenderTexture tmp = src;
    src = dst;
    dst = tmp;
}
```

**Identity short-circuit (byte-identity guard, Pitfall 7)** (`NamerAOPipeline.cs:394-399`):
```csharp
private RenderTexture ApplyStrengthContrastDirect(RenderTexture aoIn, NamerMaterialInspection inspection, int w, int h)
{
    if (Mathf.Approximately(inspection.AoStrength, 1f) && Mathf.Approximately(inspection.AoContrast, 1f))
    {
        return aoIn;   // identity -> no dispatch, no lease
    }
```

**Dispatch + descriptor + release helpers** (`NamerAOPipeline.cs:580-599`):
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

**CRITICAL — pool-ownership pitfall (D-08/Pattern 7):** `ApplyStrengthContrastDirect` (lines 394-411) leases AND releases from the AO pipeline's own `_pool`. The D-08 authored-AO remap in `NamerComputePipeline.Process` holds a **compute-pool** lease (`NamerComputePipeline.cs:148`). Do NOT call `ApplyStrengthContrastDirect` cross-pool. Add a caller-owned variant on `NamerAOPipeline` that dispatches `CSAoRemap` with caller-provided src/dst and never touches `_pool`:
```csharp
// Shape only — modeled on NamerAOPipeline.cs:394-411 but with caller-owned targets:
_compute.SetFloat("_AoDirect", 1f);   // read .g (the _AoIn.g contract)
_compute.SetTexture(_kernelAoRemap, "_Dst", src);    // caller-owned; NOT released here
_compute.SetTexture(_kernelAoRemap, "_AoOut", dst);  // caller leases/releases from its own pool
```

---

### `Compute/NAMERStylize.compute` (service, compute kernels, transform)

**Analog:** `Compute/NAMERDecomp.compute` — copy file header style, kernel guard idiom, and the two kernel shapes below.

**File header + kernel pragma style** (`NAMERDecomp.compute:1-61`): comment block naming phase/plan, kernel map with per-kernel doc lines, then `#pragma kernel ...` list, then constants with in-comment rationale. Epsilons (`NAMERDecomp.compute:66-67`):
```hlsl
static const float NAMER_DECOMP_AREA_EPS = 1e-8;
static const float NAMER_DECOMP_INSIDE_EPS = 1e-4;
```

**StructuredBuffer mesh-data pattern** (`NAMERDecomp.compute:87-90`):
```hlsl
StructuredBuffer<float3> _Verts;
StructuredBuffer<float2> _Uvs;
StructuredBuffer<int3> _Tris;
StructuredBuffer<float4> _Colors;
```
(Proven on macOS Metal — the palette-anchor buffer upload follows the same `CreateBuffer` C# pattern.)

**`CSRasterizeIslandIds` — copy `CSRasterizeVertexColors` verbatim** (`NAMERDecomp.compute:111-160`). Per-texel walk of all triangles, texel-space barycentric test, first-hit wins, write coverage alpha:
```hlsl
[numthreads(8,8,1)]
void CSRasterizeVertexColors(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) { return; }

    // Texel-space barycentric test: denom = (2*Area_texels)^2 ... In [0,1] UV space
    // denom shrinks quadratically with resolution — at 2048^2 the 1e-8 gate silently
    // discarded ~92% of real triangles.
    float2 p = float2(id.xy) + 0.5;
    float2 texels = float2(_Size);
    for (uint t = 0; t < _TriCount; ++t)
    {
        // ... barycentric math (lines 132-152), area-eps + inside-eps guards ...
        float3 color = _Colors[tri.x].rgb * u + _Colors[tri.y].rgb * v + _Colors[tri.z].rgb * w;
        _VcInterp[id.xy] = float4(color, 1.0);
        return;   // first-hit wins (Pitfall 11: keep this loop shape verbatim)
    }
    _VcInterp[id.xy] = float4(0.0, 0.0, 0.0, 0.0);   // uncovered: alpha-0 coverage
}
```
Island variant: replace the color write with `_IslandOut[id.xy] = float4(_TriIsland[t], 0, 0, 1.0);` and keep the alpha coverage convention; the texel-space area epsilon is load-bearing (in-kernel comment says why).

**`CSIslandBilateral` — extend `CSDilateResidual`'s neighborhood loop** (`NAMERDecomp.compute:256-294`):
```hlsl
[numthreads(8,8,1)]
void CSDilateResidual(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) { return; }

    if (_VcInterp[id.xy].a >= 0.5)          // <- gate becomes: islandId[p] == centerId && covered[p]
    {
        _ResidualOut[id.xy] = _ResidualSrc[id.xy];
        return;
    }

    float3 sum = float3(0.0, 0.0, 0.0);
    float count = 0.0;
    int sizeX = (int)_Size.x;
    int sizeY = (int)_Size.y;
    for (int dy = -_DilateRadius; dy <= _DilateRadius; dy++)
    {
        for (int dx = -_DilateRadius; dx <= _DilateRadius; dx++)
        {
            int2 p = int2(id.xy) + int2(dx, dy);
            if (p.x < 0 || p.y < 0 || p.x >= sizeX || p.y >= sizeY) { continue; }

            if (_VcInterp[p].a >= 0.5)
            {
                sum += _ResidualSrc[p].rgb;   // <- becomes: w * src[p].rgb; wsum += w (bilateral)
                count += 1.0;
            }
        }
    }

    float4 src = _ResidualSrc[id.xy];
    _ResidualOut[id.xy] = (count > 0.0) ? float4(sum / count, src.a) : src;
}
```
Read `_Src` / write `_Dst`, never in place (same law as `CSDespike`, `NAMERDecomp.compute:207-208`). Gaussian exp weights already in-house: `NAMERAO.compute:147-153` (`exp(-(float)(y*y) / (2.0*_Sigma*_Sigma))` separable blur).

---

### `Editor/Stylize/NamerUvIslands.cs` (utility, CPU mesh graph)

**Analog:** `Editor/Decompose/MeshVertexSplitter.cs` — static CPU mesh utility, quantized keys, managed-array result, main-thread mesh reads.

**Quantized-key constants** (`MeshVertexSplitter.cs:65-68`):
```csharp
private const float kPositionQuantize = 1e-4f;
private const float kNormalQuantize = 1e-3f;
private const float kTangentQuantize = 1e-3f;
private const float kUvQuantize = 1e-4f;
```
Reuse the `kUvQuantize` concept for the UV-pair edge keys (Pitfall 2: adjacency must key on quantized UVs, never vertex indices — the splitter welds by the full 4-tuple, so position seams produce distinct indices with identical UVs).

**Static-class entry + read-only source discipline** (`MeshVertexSplitter.cs:58, 76-82`):
```csharp
public static class MeshVertexSplitter
{
    public static NamerSplitResult Split(Mesh sourceMesh)
    {
        if (sourceMesh == null) { throw new ArgumentNullException(nameof(sourceMesh)); }
        // ... source mesh only read, never mutated ...
```

**Input data contract:** consume `NamerSplitResult` (`MeshVertexSplitter.cs:20-47` — `Uvs`, `SubMeshTriangles`) exactly as `NamerDecompPipeline.GenerateResidual` does (`NamerDecompPipeline.cs:218-261`: `ToFloat2(split.Uvs)`, `FlattenTriangles(split.SubMeshTriangles)`, then the `kUvBoundsEpsilon` out-of-range guard at 227-257 — mirror that guard for island IDs). Output `int[] triIsland` as managed arrays (no NativeArray lifetime coupling — the splitter doc at lines 72-75 states why).

---

### `Editor/Stylize/NamerStyleProfile.cs` (model, ScriptableObject) — NO IN-HOUSE ANALOG

No project-authored ScriptableObject or `[CustomEditor]` exists (verified: only `ScriptableObject.CreateInstance<UniversalRendererData>` in `NamerSmokeSetup.cs:97`). Placements to copy:

- **Editor-asmdef placement** — `Editor/Stylize/` per RESEARCH (no asmdef change; Editor asmdef already references Burst/Mathematics).
- **Asset creation menu** — house menu root is `Tools/NAMER/` + `Assets/...` (`NamerEditorWindow.cs:154-166`); use `[MenuItem("Assets/Create/NAMER/Style Profile")]` + `ProjectWindowUtil`. `AssetDatabase.CreateAsset` usage precedent: `NamerSmokeSetup.cs:98-101`.
- **Defaults** — every serialized value defaults from a named `NamerEditorConstants` member (profile-constants coupling pattern below).
- **Shape:** follow RESEARCH Pattern 8 (`List<Texture2D> References` starting EMPTY, parallel weights, `[Range]` sliders, extracted `PaletteAnchors`/`Stats`/`ReferenceFingerprint` written only by the Extract action).

---

### `Editor/UI/NamerStyleProfileEditor.cs` (component, custom inspector) — PARTIAL ANALOG

No `UnityEditor.Editor` subclass exists. Copy IMGUI idioms from `NamerEditorWindow.cs`:

**Foldout idiom** (`NamerEditorWindow.cs:944-948`):
```csharp
_settings.FoldoutSource = EditorGUILayout.Foldout(_settings.FoldoutSource, "Source", true);
if (!_settings.FoldoutSource)
{
    return;
}
```

**Slider-with-write-through + debounced recompute** (`NamerEditorWindow.cs:880-890` — the gate-toggle pattern; sliders identical minus the toggle):
```csharp
bool newDecomposition = EditorGUILayout.ToggleLeft(
    new GUIContent("VC + Residual", "tooltip..."),
    _decompositionEnabled);
if (newDecomposition != _decompositionEnabled)
{
    _decompositionEnabled = newDecomposition;
    _settings.DecompositionEnabled = newDecomposition;
    _afterPanelState.MarkTweaking();
    MarkDirty();
}
```

**HelpBox + status copy idioms** (`NamerEditorWindow.cs:982, 999-1002`): `EditorGUILayout.HelpBox(text, MessageType.Warning/Info)`.
**miniButton row** for the P3 "+/–" footer: `GUILayout.Button("+", EditorStyles.miniButton)` house style.
**Undo (P3 locked):** wrap every edit in `Undo.RecordObject(profile, "...")` + `EditorUtility.SetDirty(profile)` — no in-house example; this is the UI-SPEC contract.

---

### `Editor/Pipeline/NamerComputePipeline.cs` (MOD — D-01 insertion + D-08 authored remap)

**Self-analog.** The insertion point between normalize and projection (`NamerComputePipeline.cs:175-185`):
```csharp
// (a) Normalize + octahedral encode -> _BaseColorOut / _PackInputs / _Octahedral.
BindAndDispatchNormalizeEncode(inspection, baseColorIn, normalTexel, aoIn, metallicGlossIn,
    baseColorOut, octahedral, packInputs, w, h, usesBakedAo);

// (b) 04.2 projection write-back + roughness dip, between the normalized base
//     and the surface pack (D-01 authored-map-wins gate).
if (projection != null)
{
    projectedOut = _pool.Lease(intermediate);
    projection.Run(baseColorOut, projectedOut);    // <- D-01: pass styledBase instead
}
```
Insert the stylize stage between (a) and (b): `styledBase = baseColorOut` by default; `EnsureStylizePipeline().Run(...)` (pool lease) when the profile is active. Copy the `EnsureXxxPipeline` lazy-sub-pipeline idiom (`NamerComputePipeline.cs:312-330`).

**D-08 authored-AO branch to modify** (`NamerComputePipeline.cs:143-150`):
```csharp
if (inspection.OcclusionMap != null)
{
    // D-07 gate, clause 0 (2026-09-21 contract): an authored map is
    // source data and always transfers, regardless of the AO stage
    // checkbox (the checkbox gates synthesis only).
    aoIn = _pool.Lease(unorm8);
    Upload(inspection.OcclusionMap, aoIn, WhiteFill());
}
```

**Catch-block release discipline** (`NamerComputePipeline.cs:228-248`, WR-03): `styledBase` must be released in the catch when `!= baseColorOut` — mirror the `projectedOut` release at lines 242-245.

**Trailing-optional-param precedent** for threading the profile (`NamerComputePipeline.cs:92`): `Process(NamerMaterialInspection inspection, NamerProjectionContext projection = null)` — add `NamerStyleStylization stylization = null` the same way; `NamerProjectionContext.CoverageTarget` trailing default (`NamerComputePipeline.cs:596-599`) is the second precedent.

---

### `Editor/Pipeline/NamerAOPipeline.cs` (MOD — caller-owned remap API)

**Self-analog:** `ApplyStrengthContrastDirect` (`NamerAOPipeline.cs:394-411`) — the CSAoRemap direct-mode dispatch to extend with a caller-owned variant (see NamerStylizePipeline section above for the shape and the pool pitfall).

---

### `Editor/Pipeline/NamerProcessor.cs` (MOD — profile plumb + mesh priming)

**Self-analog.** Settings→inspection plumb block (`NamerProcessor.cs:140-146`) — add the stylization plumb here:
```csharp
inspection.AoUnmultiplyStrength = settings.AoUnmultiplyStrength;
inspection.AoStageEnabled = settings.AoStageEnabled;
inspection.AoBlurRadius = settings.AoBlurRadius;
inspection.AoStrength = settings.AoStrength;
inspection.AoContrast = settings.AoContrast;
inspection.RoughnessExtractStrength = settings.RoughnessStageEnabled ? settings.RoughnessExtractStrength : 0f;
inspection.DipSource = (NamerDipSource)settings.DipSource;
```

**Mesh-priming line to extend** (`NamerProcessor.cs:107`):
```csharp
Mesh decomposeSourceMesh = (settings.DecompositionEnabled || settings.AoStageEnabled) ? ResolveSourceMesh(selection) : null;
```
Add `|| StylizationNeedsMesh` (profile attached + smoothing > 0); same for the attach at 155-158 and the window's `primeBakeMesh` (`NamerEditorWindow.cs:409`).

**CreateProjectionContext needs NO change** (`NamerProcessor.cs:610-629`): the `Run` callback consumes whatever RT `Process` passes — D-02 holds by construction. The doc comment at 583 ("generates the residual against the SOURCE base") gets the D-02 addendum.

---

### `Editor/UI/NamerEditorWindow.cs` (MOD — W1-W4)

**Self-analog.** Foldout draw order, the W1 insertion point (`NamerEditorWindow.cs:846-864`):
```csharp
DrawSourceSection();
DrawPreviewSection(inspection);
DrawRoughnessExtractionSection(inspection);
DrawAoSection(inspection);           // line 858 — insert DrawStylizationSection() AFTER this
DrawDecompositionSection(inspection); // line 859
DrawOutputSection();
```
(Draw order mirrors pipeline order per UI-SPEC W1.)

**Debounce tick** (`NamerEditorWindow.cs:345-369`) — profile `Changed` event handlers call `MarkTweaking()`/`MarkDirty()` and this existing machinery drives the 0.3 s recompute:
```csharp
if (_busy || !_dirty) { return; }
if (EditorApplication.timeSinceStartup - _lastChange < NamerEditorConstants.DebounceSeconds) { return; }
_dirty = false;
RecomputePreview();
```

**RecomputePreview plumb + primeBakeMesh** (`NamerEditorWindow.cs:391-411`) — extend exactly like NamerProcessor (profile param into `_pipeline.Process(inspection, _projectionContext, stylization)` at line 430; `primeBakeMesh` disjunct at 409).

**W4 Before/After captions area** (`NamerEditorWindow.cs:1006-1009`):
```csharp
EditorGUILayout.BeginHorizontal();
GUILayout.Label("Before", EditorStyles.largeLabel);
GUILayout.Label("After", EditorStyles.largeLabel);
EditorGUILayout.EndHorizontal();
```
The After View popup (`{"NAMER", "Stylized NAMER"}`) goes above this pair; disabled-with-hint when no profile attached (house disabled idiom: `EditorGUI.BeginDisabledGroup` at 877).

**Toggle-flip recompute precedent (UI-04):** the `DecompositionEnabled` toggle at 884-890 (`MarkTweaking(); MarkDirty();`) is the pattern for the After-View flip.

---

### `Editor/Settings/NamerProcessorSettings.cs` (MOD — 3 new keys)

**Self-analog.** Key + property pattern (`NamerProcessorSettings.cs:15, 42-46`):
```csharp
private const string DestinationKey = "NamerProcessor.Destination";
// ...
public string Destination
{
    get { return EditorPrefs.GetString(DestinationKey, NamerEditorConstants.DefaultDestination); }
    set { EditorPrefs.SetString(DestinationKey, value); }
}
```
Foldout pattern (174-186): `FoldoutSource` default `true`, all others `false` — `FoldoutStylization` defaults `false`. `StyleProfileGuid` is a string key (GUID persistence; resolve via `AssetDatabase.GUIDToAssetPath`). Clamp-on-read precedent if needed: `CoverageTarget` (139-144).

---

### `Editor/NamerEditorConstants.cs` (MOD — ~10 new constants)

**Self-analog.** Every const is `public const` with a doc comment citing the decision (`NamerEditorConstants.cs:31-32`):
```csharp
/// <summary>UI debounce interval in seconds (UI-SPEC; named constant, not a magic number).</summary>
public const float DebounceSeconds = 0.3f;
```
Add: `MinReferences = 3`, `MaxReferences = 10`, `DefaultStyleStrength = 0.5f`, `DefaultSmoothingStrength = 0f`, `DefaultCavityFloor = 0f`, `DefaultNormalDetailReduction = 0f`, `DefaultRoughnessSimplify = 0f`, `DefaultReferenceWeight = 1f`, `DefaultHueMode = 0` (UI-SPEC persistence table).

---

### `Compute/NAMERPack.compute` (MOD — D-07 uniforms)

**Self-analog.** Roughness-simplify insertion — after the dip branches converge, before the pack call (`NAMERPack.compute:127-145`):
```hlsl
if (_HasExtractedRoughness > 0.5)
{
    if (_RoughnessDipApplied > 0.5)
    {
        roughness = _RoughnessTex[id.xy].r;          // transfer full-adoption
    }
    else
    {
        roughness = saturate(roughness - _RoughnessExtractStrength * _RoughnessTex[id.xy].r);  // Sobel dip
    }
}

float emissive = _PackInputs[id.xy].z;
_SurfaceOut[id.xy] = NamerPackSurfaceFromOct(oct, ao, metallic, emissive, roughness);
```
Insert `roughness = lerp(roughness, neighborhoodMean, _RoughnessSimplify)` between the `if` block and the write (covers all three roughness paths — they converge on the local `roughness`). Neighborhood loop shape: `CSDilateResidual` (`NAMERDecomp.compute:274-290`) reading `_PackInputs.y`; `exp` weights from `NAMERAO.compute:147-153` if gaussian.

Normal-flatten insertion — inside `CSOctahedralEncode` after Z-rebuild, before the encode call (`NAMERPack.compute:106-112`):
```hlsl
float4 texel = _NormalTexel[id.xy];
texel.x *= texel.w;
float2 signedXY = texel.xy * 2.0 - 1.0;
texel.z = sqrt(1.0 - saturate(dot(signedXY, signedXY))) * 0.5 + 0.5;

float2 oct = NamerOctahedralEncode(texel.xyz);
_Octahedral[id.xy] = float4(oct, _AoIn[id.xy].g, 0.0);
```
Insert `n = normalize(lerp(n, float3(0,0,1), _NormalDetailReduction))` before `NamerOctahedralEncode`; never touch `.b` (it carries AO at line 112).

**Uniform declaration style** (`NAMERPack.compute:36-48`): `float _RoughnessExtractStrength;` with trailing comment — follow for `_RoughnessSimplify` / `_NormalDetailReduction` / `_CavityFloor`. Identity default = 0 short-circuits to byte-identical (Pitfall 7).

---

### `Compute/NAMERAO.hlsl` + `Compute/NAMERAO.compute` (MOD — D-08 cavity floor)

**Self-analog.** `NamerAoRemap` (`NAMERAO.hlsl:28-33`) gains the floor parameter:
```hlsl
float NamerAoRemap(float ao, float strength, float contrast)
{
    ao = saturate(lerp(1.0, ao, strength));
    ao = saturate((ao - 0.5) * contrast + 0.5);
    return ao;   // <- becomes: return max(ao, cavityFloor);  (0 == old function post-saturate)
}
```
Call site (`NAMERAO.compute:179`): `ao = NamerAoRemap(ao, _AoStrength, _AoContrast);` — add `_CavityFloor`. The include's header contract (lines 6-10: "single shared source of truth ... do not edit independently of Core/NamerConstants.cs") governs where any mirrored constant lives.

---

### `Tests/Editor/NamerStyleExtractionTests.cs` + `NamerUvIslandsTests.cs` + `NamerStyleProfileTests.cs` (test)

**Analog:** `Tests/Editor/NamerAOBakeTests.cs`.

**Header doc + capability gate** (`NamerAOBakeTests.cs:14-46`):
```csharp
private static bool ComputeAvailable =>
    SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;
```
Pure CPU tests are `[Test]`; only GPU-touching tests are `[UnityTest]` with the gate + `Assert.Ignore` (D-15 — see `ResidualPipelineTests.cs:745-749`).

**The determinism bar to replicate exactly** (`NamerAOBakeTests.cs:48-80`):
```csharp
NamerAOBakeResult first = NamerAOBaker.Bake(floor, roof, BakeResolution, ...);
NamerAOBakeResult second = NamerAOBaker.Bake(floor, roof, BakeResolution, ...);
try
{
    Assert.AreEqual(first.Width, second.Width);
    // ...
    for (int i = 0; i < first.Ao.Length; i++)
    {
        Assert.AreEqual(first.Ao[i], second.Ao[i],
            "AO buffer must be element-wise identical across two bakes (deterministic direction table + float math)");
    }
}
finally
{
    first.Ao.Dispose();
    second.Ao.Dispose();
}
```
Fixture discipline: hand-built meshes (`CreateFlatQuad`/`CreateRoofQuad`), `try/finally { Destroy(...) }` cleanup.

**EditorPrefs pinning for pipeline tests** (`NamerAOBakeTests.cs:34-43`): tests construct `NamerProcessorSettings` whose setters persist — pin every outcome-relevant key (`Pitfall 6`; keys are the private consts mirrored in the test).

---

### `Tests/Editor/NamerStylizeKernelTests.cs` (test, GPU)

**Analog:** `Tests/Editor/ResidualPipelineTests.cs:742-796`. The GPU kernel-test shape:
- `[UnityTest] public IEnumerator ...` + `ComputeAvailable` gate + `Assert.Ignore` (742-749)
- Named-size consts, hand-built `NamerSplitResult` + colors, RT fixture (751-758)
- **Hash-noise grain fixture, never luminance ramps** (`CreateGrainBase`, 758 — Pitfall 5 / project memory: ramps flatten to zero under the AO un-multiply; disable the AO stage or supply authored white occlusion to isolate the stylize stage)
- Per-texel numeric oracles with per-oracle tolerance, strip medians — never single texels, never screenshots (777-796; Pitfall 10 bans vision-model judging)
- `ReadBackFloat`/readback helpers with `try/finally` dispose and pool-release (722 area)

### `Tests/Editor/NamerStylizationIntegrationTests.cs` (test, E2E)

**Analog:** `NamerAOBakeTests.cs` E2E section (the AO-priming E2E: proves `NamerProcessor.Process` primes the bake mesh — mirror for stylize-stage priming + byte-identity) plus the identity-regression class in `Tests/Editor/NamerProjectionAcceptanceTests.cs` (byte-comparison without a profile — Pitfall 7's guard). `NamerAOBakeTests.cs:31-32` shows the `TempFolder = "Assets/NAMER_AO_Tests_Temp"` temp-asset pattern.

## Shared Patterns

### Compute kernel guard + dispatch (every kernel, C# and HLSL)
**Source:** `NAMERDecomp.compute:114-117` (HLSL) + `NamerAOPipeline.cs:580-583` (C#)
**Apply to:** `NAMERStylize.compute` all kernels, `NAMERPack.compute`/`NAMERAO.compute` edits
```hlsl
[numthreads(8,8,1)]
void CSXxx(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) { return; }
```
```csharp
_compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
```
`numthreads(8,8,1)` everywhere; no groupshared; unsigned `uint2 _Size` compared against `SV_DispatchThreadID`.

### Render-target leasing + descriptor law
**Source:** `NamerComputePipeline.cs:488-497`, `ComputeTexturePool.cs:36,65` (`Lease`/`Release`), pool leak watchdog `LiveRenderTargetCount` (`NamerComputePipeline.cs:75`)
**Apply to:** all new pipeline stages
```csharp
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
Never `new RenderTexture` outside the pool; never sRGB write targets (D-11); smoothing RTs are `R16G16B16A16_SFloat` linear. Release cross-pool never; caller-owned API for the D-08 remap.

### Identity-default byte-identity guard
**Source:** `NamerAOPipeline.cs:396-399` (`Mathf.Approximately` short-circuit), `NamerComputePipeline.cs` projection-null path
**Apply to:** every Phase 5 dispatch — `stylization == null` / all-identity sliders must take the zero-dispatch path so Phase 04.3 output stays bit-identical (158-test suite).

### ComputeBuffer upload
**Source:** `NamerDecompPipeline.cs:755-760`
**Apply to:** island-ID buffer + palette anchor buffer uploads
```csharp
private static ComputeBuffer CreateBuffer<T>(T[] data, int count, int stride) where T : struct
{
    ComputeBuffer buffer = new ComputeBuffer(count, stride);
    buffer.SetData(data);
    return buffer;
}
```
Pair with `DisposeBuffer` (772-778) in `finally`.

### GPU→CPU readback pumping (edit mode)
**Source:** `NamerProcessor.cs:565-577`
**Apply to:** any stylize-stage readback in tests/integration
```csharp
AsyncGPUReadbackRequest request = NamerComputePipeline.RequestReadback(source, 0, TextureFormat.RGBA32);
request.forcePlayerLoopUpdate = true;   // IN-02: async readback stalls in edit mode without the pump
request.WaitForCompletion();
```

### Debounced recompute wiring
**Source:** `NamerEditorWindow.cs:345-369` (Tick), 880-890 (toggle → `MarkDirty`)
**Apply to:** W2 profile attach/detach/swap, W4 After-View flip, profile-slider edits (via the profile `Changed` event → window `MarkTweaking()` + `MarkDirty()`). Extract NEVER rides this path (D-04 — explicit synchronous action, `RequestBake` precedent `NamerAOPipeline.cs:330-367`).

### Named constants, no magic numbers
**Source:** `NamerEditorConstants.cs:31-32`, `NamerAOBaker.cs:54-65`, `MeshVertexSplitter.cs:65-68`, `VertexColorFitter.cs:104`
**Apply to:** every threshold (hue eps, low-sat cutoff, bins, K, iterations, radius, sigmas, caps) — `kCamelCase` in HLSL/C# locals, `NamerEditorConstants` for UI defaults.

### Doxygen headers on every public interface
**Source:** every file read (`NamerAOBaker.cs:40-51`, `MeshVertexSplitter.cs:49-57`, etc.)
**Apply to:** all new classes/methods — `<summary>` citing the decision ID (D-0x/STYL-xx) is house style throughout.

## No Analog Found

| File | Role | Data Flow | Reason |
|------|------|-----------|--------|
| `Editor/Stylize/NamerStyleProfile.cs` | model (ScriptableObject) | CRUD | No project-authored ScriptableObject exists; build per RESEARCH Pattern 8 + UI-SPEC P1-P14; creation menu follows house `Assets/...` root (`NamerEditorWindow.cs:160`) |
| `Editor/UI/NamerStyleProfileEditor.cs` | component (custom inspector) | request-response | No `[CustomEditor]`/`UnityEditor.Editor` subclass in-house; IMGUI control idioms (foldout/slider/miniButton/HelpBox) copied from `NamerEditorWindow.cs`; `Undo.RecordObject` discipline is new (UI-SPEC locked) |

Both have actionable partial guidance above; the planner should treat RESEARCH Pattern 8 + the 05-UI-SPEC.md control inventory as the spec and the window idioms as the style source.

## Metadata

**Analog search scope:** `Packages/com.graffitientertainment.namer/` (Editor/, Compute/, Shaders/, Core/, Tests/Editor/), `.planning/phases/05-stylization/`
**Files scanned:** 20 source/analog files read (13 full, 7 targeted ranges) + greps for ScriptableObject/CustomEditor/MenuItem across the package
**Pattern extraction date:** 2026-09-23
**Verified-verbatim anchors:** all excerpts above copied from the working tree at mapping time; line numbers match the current develop HEAD (751ead7)

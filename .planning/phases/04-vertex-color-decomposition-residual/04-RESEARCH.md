# Phase 4: Vertex-Color Decomposition + Residual - Research

**Researched:** 2026-08-31
**Domain:** Per-vertex color fitting (barycentric least-squares), seam-safe mesh topology remapping, GPU residual/error texture generation, editor mesh/EXR asset writing
**Confidence:** HIGH (recombination math + runtime plumbing verified against source; MEDIUM on seam-splitting algorithm specifics and error-metric refinement)

## Summary

Phase 4 turns the normalized base color into (a) a low-frequency per-vertex color approximation and (b) a residual texture that captures the difference. The runtime recombination is **already multiply** — `NamerSurface.hlsl` line 94 computes `albedo = baseResidual.rgb * _BaseColor.rgb * vertexColor.rgb`, and the full vertex-color path (`Attributes.color : COLOR` → `Varyings.vertexColor` → `InitializeNamerSurfaceData`) is already plumbed in `NAMER.shader` (lines 113–123, 195–241, 243–261). No runtime shader edit is required for the multiply path.

**Primary recommendation:** Keep the multiply model (D-01). Because albedo reconstructs multiplicatively, the residual is a **multiplicative quotient `residual = base / max(vertexColorInterp, epsilon)`** — not a subtractive difference — and it degenerates to `base` when vertex colors are white (Phase-1 parity holds by construction). Fit per-vertex colors on **CPU Burst** (mirror `NamerAOBaker`) against a one-shot linear-base readback; **split** the mesh at UV seams/discontinuities into a generated `.asset`; **compute** the residual quotient + error stats in a new GPU compute file (mirror `NAMERAO.compute`). All dependencies (Burst, Unity.Mathematics, Unity.Collections, compute) are already present — **zero new packages**.

Two material facts the planner must not miss: (1) the "EXR path ready" claim in `04-CONTEXT.md` is **stale** — `AssetGenerator.cs` only writes PNG (surface + base); there is no `EncodeToEXR` call anywhere in the package, so the EXR residual writer must be built. (2) `AssetGenerator` writes materials but nothing writes/rebinds **meshes**; when decomposition is ON, Process must also swap the scene renderer's `sharedMesh` to the split mesh (or the vertex colors are never present at runtime).

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** Recombination math is **research-decided**, with locked constraints: Color32 vertex colors, reconstruction within the reported error, packed surface-texture contract untouched. Standing context the researcher must weigh: `NamerSurface.hlsl` **already implements multiply** — `albedo = baseResidual.rgb * _BaseColor.rgb * vertexColor.rgb` (`InitializeNamerSurfaceData`, SHDR-02/D-05, residual == base in Phase 1) — and PROJECT.md documents `BaseColor ≈ VertexColorInterpolation × ResidualColor`. Multiply is the de-facto model; choosing additive/hybrid requires changing the runtime shader and re-verifying SHDR parity.
- **D-02:** Residual on disk is **HDR EXR always** (`R16G16B16A16_SFloat`) — never quantized to PNG, even when the residual happens to be LDR-range. AssetGenerator's EXR write path (already stubbed) is the route.
- **D-03:** **Own convention, not Blender's.** `assign_vertex_colors` in the Blender reference is nearest-texel-per-loop averaging with a luminance-variance alpha heuristic — deliberately NOT mirrored. Surface-texture decode compatibility is untouched.
- **D-04:** The Color32 **alpha channel carries a per-vertex fit-quality signal (0–1)** — fit confidence, debuggable in-engine. (This replaces Blender's luminance-variance alpha heuristic.)
- **D-05:** Decomposition toggle defaults **OFF — explicit opt-in**. Process output stays the Phase-3 shape until the user enables it.
- **D-06:** When enabled, Process writes the **full set**: seam-split vertex-colored mesh + residual EXR + normalized Base PNG + Surface PNG + .mat, under `NAMERGenerated/{source}/`. Both representations exist on disk because the user can switch between them (D-14).
- **D-07:** The generated material **binds the residual as `_BaseMap`** — mesh vertex colors × residual reconstruct base at runtime.
- **D-08:** The interactive preview shows the **decomposed reconstruction** when enabled — preview parity with generated output.
- **D-09:** Reconstruction-error stats surface in a **window stats block**: coverage %, avg error, max error, residual requirement (incl. "not required"), chosen residual resolution.
- **D-10:** **Vertex Colors / Residual / Error Heatmap become debug channels in the existing toolbar** — joining the Phase-03 D-11 channel set (shared debug-material pattern), not a dedicated panel.
- **D-11:** The error heatmap uses a **perceptually-uniform color ramp** (viridis-style), not grayscale.
- **D-12:** Stats update **live with the debounced preview recompute** (UI-06 / Phase-03 D-10 debounce pattern) — not only after Process.
- **D-13:** **Auto-drop below threshold.** When the fit alone reconstructs within the error threshold, no residual texture is written or bound — the material runs on vertex colors + packed surface only (stats report "residual: not required").
- **D-14:** **A user-facing on/off switch for vertex coloring** ("used or not, toggled on or off"). Both representations are retained so the user can switch between them — switching must not require reprocessing.
- **D-15:** The error threshold is **user-tunable in the window** beside the decomposition toggle, EditorPrefs-persisted via `NamerProcessorSettings` (AO-controls pattern from 03.1).
- **D-16:** Adaptive search runs **downward from source resolution** — halve (2048→1024→512→256→128) while error stays within threshold. Quality-first: never worse than today's full-res output.
- **D-17:** Manual override is a **ladder dropdown** snapping to the halving steps the adaptive search uses (no free-form pixel input).

### Claude's Discretion
- Final recombination-math call (research): multiply (standing implementation, D-01) vs additive vs hybrid — within the locked constraints.
- LSQ internals: samples per triangle, barycentric sampling pattern, per-vertex normal-equations accumulation (3×3 `AᵀA`), solver details — hand-rolled with Unity.Mathematics, Burst-compiled (no SVD library; the systems are 3×3).
- Seam-splitting specifics: split criteria beyond required UV seams (normal/color discontinuity thresholds), attribute-preservation details.
- The error-metric definition behind the threshold (perceptual ΔE-style vs mean RGB distance) — must be one consistent metric across stats, heatmap, and the adaptive search.
- The D-14 switching mechanism: separate material assets + renderer/mesh swap, material keyword, or similar — must be instant and not reprocess.
- Exact stats-block placement and channel UI layout (Phase-03 D-14: functional controls only, no dead UI).

### Deferred Ideas (OUT OF SCOPE)
- Stylization × decomposition interplay (fit before/after stylize, palette effects on residual) — Phase 5
- GBuffer/deferred vertex-color support — tracked limitation from Phase 1
- Batch decomposition across many assets — v2 BATCH-01
- Blender-side consumption of Unity-generated vertex-color materials — explicitly out (D-03)
- Literal Blender fixture parity for vertex colors — not applicable under the own-convention decision
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| VCOL-01 | Per-triangle multi-sample barycentric least-squares (not simple averaging) of vertex colors against the base texture | §LSQ fit (Burst 3×3 normal equations, barycentric grid sampling); mirrors `NamerAOBaker` |
| VCOL-02 | Split vertices at UV seams / hard color boundaries / discontinuities, preserving attributes | §Seam splitting (weld-by-(pos+normal+tangent+UV) + discontinuity split; Unity Mesh attribute streams) |
| VCOL-03 | Residual texture = difference between fitted interpolation and source texture | §Residual quotient `base / vcInterp` (GPU kernel); new `NAMERDecomp.compute` |
| VCOL-04 | Reconstruction-error stats (coverage, avg/max, residual requirement) + debug viz | §Error metric (one consistent MAE) + GPU scalar reduce + viridis heatmap |
| VCOL-05 | Adaptive residual resolution + manual override | §Adaptive search (downward halving on GPU, CPU decision loop) + ladder dropdown |
| TEST-02 | Automated tests for vertex fitting, residual reconstruction, UV seam behavior | §Validation/Test patterns (headless Burst tests like `NamerAOBakeTests`, GPU golden vs CPU oracle) |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Per-vertex LSQ fit (VCOL-01) | CPU Burst (Editor) | — | Mesh-topology math (per-triangle → per-vertex normal equations) — same tier as `NamerAOBaker`; deterministic + headless-testable |
| Base-color sampling for the fit | GPU readback → CPU (Editor) | — | Base is produced on GPU (`NormalizedBaseColor`); read back once (RGBA32 linear) so the Burst fit samples it |
| Seam-safe vertex splitting (VCOL-02) | CPU/Editor (mesh topology) | — | Topology remap + attribute preservation is index/array work, not pixel work |
| Residual quotient generation (VCOL-03) | GPU compute | — | Per-pixel division `base / vcInterp` — NORM-03 mandates GPU |
| Error stats + heatmap (VCOL-04) | GPU compute (per-texel) + CPU scalar reduce | — | Same split as the AO reduction (`ReduceToScalarMean`) |
| Adaptive resolution search (VCOL-05) | GPU compute (downsample/upsample/eval) + CPU loop (decision) | — | Per-pixel eval on GPU; halving decision is a tiny CPU loop |
| Mesh + EXR asset write | Editor (`AssetGenerator`) | — | Sole disk writer (GEN-01); new mesh + EXR write paths land here |
| Runtime recombination | Runtime shader (multiply, already present) | — | `NamerSurface.hlsl` unchanged; residual vs base bound at `_BaseResidualMap` |
| Debug channels (Vertex Colors / Residual / Heatmap) | Editor debug shader + runtime pass | — | `NamerDebugView.shader` gains a `COLOR` semantic; channels 6/7/8 |

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Unity Editor | 6000.0.x LTS | Editor host | Project baseline (CLAUDE.md); Linear color space confirmed (`m_ActiveColorSpace: 1`) |
| Unity.Burst | bundled (1.8.x) | Compile the per-triangle LSQ fit + seam split | Already referenced by `Editor.asmdef`; `NamerAOBaker` precedent |
| Unity.Mathematics | 1.3.2 | `float3`/`float3x3`/`math` for the 3×3 solver + barycentric math | Already referenced; sanctioned for hand-rolled 3×3 (no SVD) |
| Unity.Collections | bundled (2.5.x) | `NativeArray` for mesh/base/texture buffers | Transitive via Unity.Burst (verified: `NamerAOBaker` uses it without an explicit asmdef ref) |
| Compute Shaders (HLSL `.compute`) | Unity 6 built-in | Vertex-color UV rasterize + residual quotient + error/heatmap kernels | New `NAMERDecomp.compute` mirrors `NAMERAO.compute` precedent |
| Unity Mesh API (`Mesh`, `IndexFormat`, `MeshData`) | Unity 6 built-in | Write the seam-split mesh asset | `AssetDatabase.CreateAsset(mesh, ".asset")` — generated, not imported |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Texture2D.EncodeToEXR` / `ImageConversion.EncodeToEXR` | Unity 6 built-in | Write the R16G16B16A16_SFloat residual (D-02) | Requires an RGBAHalf/RGBAFloat readable `Texture2D`; `EXRFlags.OutputAsFloat` for 32-bit |
| `AsyncGPUReadback` | Unity 6 built-in | Read base (fit input) + residual/error back | Existing `NamerComputePipeline.RequestReadback` contract |
| `Graphics.Blit` + `NamerRawCopy.shader` | existing | Bilinear downsample/upsample during adaptive search | Reuses the existing raw-copy pattern |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Multiply recombination (recommended) | Additive `albedo = base + residual` / hybrid | Requires editing `NamerSurface.hlsl`, re-verifying SHDR-03 parity, and breaks Phase-1 "residual == base" back-compat; rejected |
| GPU atomic accumulation of normal equations | CPU Burst fit (recommended) | Float `InterlockedAdd` portability risk across Metal/DX/Vulkan; CPU Burst matches the established "Burst for mesh-topology" pattern and is headless-testable |
| Texel-aligned sampling (walk each triangle's UV bbox) | Fixed barycentric grid sampling (recommended) | Bbox walk is variable-length per triangle (GPU divergence / CPU cost); a fixed interior barycentric grid is deterministic and well-conditioned for a low-frequency fit |
| MathNet.Numerics / Accord SVD | Hand-rolled 3×3 symmetric solve | Heavy, AOT/linker friction; CLAUDE.md explicitly sanctions hand-rolled 3×3 (systems are 3×3) |
| Separate `.mat` for the non-decomposed switch-back | Retained Base PNG + original mesh (recommended) | A second material asset doubles disk/state; the Base PNG already exists (D-06) and the window can re-bind `_BaseResidualMap` for preview switching |

**Version verification:** No new packages. Unity built-ins + the already-installed Burst/Mathematics/Collections (Editor asmdef refs verified at `Packages/com.graffitientertainment.namer/Editor/GraffitiEntertainment.Namer.Editor.asmdef`). Linear color space verified at `ProjectSettings/ProjectSettings.asset` line 50 (`m_ActiveColorSpace: 1`).

## Package Legitimacy Audit

> No new external packages are installed by this phase. All dependencies are Unity engine built-ins (`UnityEngine`, `UnityEditor`, compute/HLSL) plus `Unity.Burst`, `Unity.Mathematics`, `Unity.Collections`, which are **already installed and referenced** (verified in `Editor.asmdef`). slopcheck is therefore N/A — there are zero registry-installed packages to vet. If any implementation later introduces a package, it must pass the Package Legitimacy Gate (slopcheck + ecosystem registry verification) before inclusion.

**Packages removed due to slopcheck [SLOP] verdict:** none
**Packages flagged as suspicious [SUS]:** none

## Architecture Patterns

### System Architecture Diagram

```
                           ┌────────────────────────────────────────────────────────┐
                           │                    NamerProcessor.Process              │
                           │   (D-05 gate: decomposition toggle ON → new stage)     │
                           └────────────────────────────────────────────────────────┘
                                                          │
             ┌────────────────────────────────────────────┼──────────────────────────────────┐
             ▼                                            ▼                                  ▼
  ┌─────────────────────┐                     ┌───────────────────────┐        ┌────────────────────────┐
  │ NamerComputePipeline │                     │  MeshVertexSplitter   │        │  VertexColorFitter     │
  │ (existing)           │                     │  (CPU/Burst)          │        │  (CPU/Burst)           │
  │ → NormalizedBaseColor │                     │  split at UV seams +  │        │  read back base (RGBA32)│
  │   (R16G16B16A16_SFloat)│                     │  discontinuity       │        │  per-triangle multi-   │
  └─────────┬────────────┘                     │  → new vertex/index   │        │  sample barycentric LSQ│
            │                                   │    arrays (all attrs)│        │  → per-vertex float3   │
            │ readback (linear RGBA32)          └──────────┬───────────┘        │  → quantize Color32 +  │
            ▼                                              │                    │    alpha=fit-quality   │
  ┌──────────────────────┐                                 │                    └──────────┬─────────────┘
  │  VertexColorFitter   │◄────────────────────────────────┴───────────────────────────────┘
  │  (same stage)        │
  └──────────┬───────────┘
             │ quantized Color32 (RGB=color, A=fit-quality)
             ▼
  ┌───────────────────────────────────────────────────────────────────────────────────────────┐
  │  NEW: NAMERDecomp.compute  (GPU, per-pixel)                                                │
  │                                                                                            │
  │   1. CSRasterizeVertexColors : per triangle → UV-space barycentric interpolation of the    │
  │                                Color32 colors → _VcInterp RT (R8G8B8A8_UNorm, linear) +     │
  │                                UV coverage mask                                             │
  │   2. CSResidual             : residual = base / max(vcInterp, eps)  (alpha = base.a)      │
  │                                → R16G16B16A16_SFloat linear                                 │
  │   3. CSError / CSHeatmap    : err = mean(|vcInterp·residual − base| per ch) → viridis ramp │
  │   4. CSReduce               : coverage/avg/max scalar reduction (mirror ReduceToScalarMean)│
  └──────────┬───────────────────────────────────────────────────────────────────────────────────┘
             │ residual RT + error stats
             ▼
  ┌────────────────────────────────────────────────────────────────────────────────────────────┐
  │  AssetGenerator (sole disk writer — extended)                                              │
  │    • residual → EXR  (EncodeToEXR, RGBAHalf; importer linear/uncompressed/point/no-mips)  │
  │    • seam-split mesh → .asset  (CreateAsset; UInt32 index when >65535 verts)              │
  │    • material binds residual at _BaseResidualMap (D-07)                                    │
  │    • Base PNG + Surface PNG unchanged (switch-back + packed surface)                       │
  └──────────┬─────────────────────────────────────────────────────────────────────────────────┘
             ▼
  ┌────────────────────────────────────────────────────────────────────────────────────────────┐
  │  Runtime: NamerSurface.hlsl (UNCHANGED)  albedo = baseResidual.rgb · _BaseColor.rgb · vc   │
  │  Scene bind: swap renderer.sharedMesh → split mesh when decomposition ON (BindGeneratedMaterials) │
  └────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Recommended Project Structure (new files only)
```
Packages/com.graffitientertainment.namer/
├── Compute/NAMERDecomp.compute          # rasterize + residual + error/heatmap kernels
├── Editor/Decompose/VertexColorFitter.cs   # Burst per-triangle LSQ + 3×3 solve + Color32 quantize
├── Editor/Decompose/MeshVertexSplitter.cs  # seam/discontinuity split, attribute-preserving
├── Editor/Decompose/NamerDecompPipeline.cs # GPU harness (mirror NamerAOPipeline)
├── Editor/Generation/AssetGenerator.cs     # EXTEND: WriteResidualExr + WriteMeshAsset
├── Editor/Settings/NamerProcessorSettings.cs # EXTEND: DecompositionEnabled / ErrorThreshold / ResidualResolution
├── Editor/UI/NamerEditorWindow.cs          # EXTEND: toggle/threshold/ladder/stats + 3 channels
├── Editor/UI/NamerDebugChannelMaterial.cs  # EXTEND: COLOR semantic + channels 6/7/8
├── Shaders/NamerDebugView.shader           # EXTEND: Attributes.color + 3 debug branches
└── Tests/Editor/VertexColorDecompTests.cs  # fit/residual/UV-seam headless tests
```

### Pattern 1: Per-vertex normal-equations accumulation (3×3 AᵀA)
**What:** Each vertex accumulates a symmetric 3×3 Gram matrix `AᵀA` (6 unique scalars) and a per-channel 3-vector `Aᵀb` (float3). Per triangle, each interior barycentric sample `(w_a, w_b, w_c)` contributes `w_i w_j` to `AᵀA[i][j]` and `w_i · baseColor(sample)` to `Aᵀb[i]`. Solving `AᵀA · c = Aᵀb` per vertex gives the color. Because color channels are independent, one scalar 3×3 serves all three channels.
**When to use:** VCOL-01 — the multi-sample fit; replaces simple per-vertex averaging.
**Example (Burst shape, mirrors NamerAOBaker.RayTriangleJob):**
```csharp
[BurstCompile]
private struct FitJob : IJobParallelFor
{
    [ReadOnly] public NativeArray<float3> Verts;
    [ReadOnly] public NativeArray<float2> Uvs;
    [ReadOnly] public NativeArray<int3>   Tris;
    [ReadOnly] public NativeArray<Color32> BaseTexels;   // RGBA32 linear base readback
    public int BaseWidth, BaseHeight;
    [NativeDisableParallelForRestriction] public NativeArray<float> AtA; // vertCount * 6
    [NativeDisableParallelForRestriction] public NativeArray<float3> AtB; // vertCount

    public void Execute(int triIndex)
    {
        int3 t = Tris[triIndex];
        float2 a = Uvs[t.x], b = Uvs[t.y], c = Uvs[t.z];
        for (int i = 1; i <= kGrid; i++)
        for (int j = 1; i + j <= kGrid; j++)
        {
            float wa = (float)i / kGrid, wb = (float)j / kGrid, wc = 1f - wa - wb; // interior
            float2 uv = a * wa + b * wb + c * wc;
            float3 col = SampleBase(uv); // linear
            // accumulate into AtA/AtB for the three corner vertices (6 + 3 adds each)
        }
    }
}
```
(Full accumulation + a symmetric 3×3 closed-form/Cramer solve lives in `VertexColorFitter.cs`; the "3×3 AᵀA" in CLAUDE.md's discretion is exactly this per-vertex Gram matrix.)

### Pattern 2: GPU residual quotient + coverage mask
**What:** Rasterize vertex colors into UV space, then divide. Uncovered texels are excluded from stats via a coverage mask (alpha of the rasterize pass = 1 where a triangle covers).
**Example (HLSL):**
```hlsl
// CSResidual
RWTexture2D<float4> _ResidualOut;   // R16G16B16A16_SFloat linear
Texture2D<float4> _BaseLinear;      // normalized base (linear)
Texture2D<float4> _VcInterp;        // R8G8B8A8_UNorm linear (quantized Color32 interp); alpha = coverage
float _VcFloor;

[numthreads(8,8,1)]
void CSResidual(uint3 id : SV_DispatchThreadID)
{
    float3 b = _BaseLinear[id.xy].rgb;
    float3 v = max(_VcInterp[id.xy].rgb, _VcFloor);  // guard near-zero vcInterp
    _ResidualOut[id.xy] = float4(b / v, _BaseLinear[id.xy].a); // alpha = base alpha (transparency)
}
```

### Pattern 3: Seam-safe vertex split + generated mesh write
**What:** Weld vertices by `(position, normal, tangent, uv)` to guarantee each UV-seam side is a distinct vertex; detect color discontinuities (Claude's discretion threshold) to split further; re-emit all attribute streams; write as a `.asset`.
**Example (write path, grounded in Unity 6 Mesh API):**
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
AssetDatabase.CreateAsset(outMesh, meshPath);   // generated mesh .asset (not imported FBX)
```

### Anti-Patterns to Avoid
- **Subtractive residual under a multiply shader:** storing `base - vcInterp` and expecting `albedo = residual × vcInterp` to reconstruct — mathematically wrong. The residual must be the quotient `base / vcInterp`.
- **Quantizing vertex colors after the residual is computed:** the residual must be derived from the **quantized** Color32 colors (what the runtime interpolates), not the float fit, or reconstruction drifts by the quantization delta.
- **Fitting against sRGB source bytes:** the fit and residual must run in **linear** space (the shader multiplies in linear); use the normalized linear base, never the raw sRGB source.
- **Per-pixel C# loops** for residual/error/heatmap — violates NORM-03; use compute kernels.
- **Splitting a `SkinnedMeshRenderer` mesh without preserving `boneWeights`/`bindposes`** — breaks skinning; preserve or warn (blend shapes are out of scope for MVP).
- **Ignoring the sub-mesh boundary** during splitting — split per sub-mesh so material slots stay intact.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| EXR encoding | A custom OpenEXR writer | `Texture2D.EncodeToEXR` / `ImageConversion.EncodeToEXR` (RGBAHalf/RGBAFloat, `isReadable=true`) | Built-in, correct 16-bit-half EXR (D-02) |
| Mesh asset serialization | Custom mesh file format | Unity `Mesh` API + `AssetDatabase.CreateAsset(..., ".asset")` | Generated (not imported) mesh, correct importer semantics |
| Barycentric point-in-triangle | Reimplement | Reuse the exact math in `NamerAOBaker.RayTriangleJob.TryReconstruct` | Already proven + deterministic |
| Per-pixel residual/error | C# loops | Compute kernels in a new `.compute` file | NORM-03 project constraint |
| Scalar error reduction | Full-res C# scan | Hierarchical block-average (mirror `NamerAOPipeline.ReduceToScalarMean`) | GPU-resident, one-shot readback |
| Viridis heatmap ramp | Hand-tuned colors | The 5 anchors from 04-UI-SPEC (violet→blue→teal→green→yellow) | Perceptually uniform (D-11) |

**Key insight:** The 3×3 solve is the *exception that proves the rule* — CLAUDE.md and the decisions explicitly **sanction** hand-rolling the symmetric 3×3 (Cramer/closed-form or small Jacobi) because pulling in a general SVD library for 3×3 systems is worse than the ~15 lines of deterministic math. Everything else (EXR, mesh serialization, per-pixel work) has a battle-tested Unity built-in or the existing AO compute precedent.

## Common Pitfalls

### Pitfall 1: Division blow-up at near-zero vertex-color interpolation
**What goes wrong:** In dark regions, `vcInterp` approaches 0; the quotient `base / vcInterp` explodes to HDR values that quantize poorly and produce visible artifacts.
**Why it happens:** Multiplicative quotient residual has a genuine singularity at `vcInterp = 0`.
**How to avoid:** Floor the divisor at a named constant (e.g. `kVcFloor = 1e-3`, distinct from `NamerConstants.Epsilon`) before dividing; the residual then carries the "miss" (reconstruction error) rather than a NaN/blown-out value.
**Warning signs:** EXR residuals with bright speckles in near-black seams; `avg error` fine but `max error` spiky.

### Pitfall 2: Residual derived from float fit instead of quantized Color32
**What goes wrong:** Runtime interpolates the *quantized* Color32, so a residual computed against the float fit leaves a visible reconstruction error equal to the quantization step.
**Why it happens:** The fit and the runtime consume different representations of "the vertex color."
**How to avoid:** Quantize to Color32 **first**, then rasterize/interpolate the Color32 values for both the residual and the error metric. Make this a test invariant (`residual × quantizedVcInterp ≈ base`).
**Warning signs:** Reconstruction error floors at ~1/255 even on a "perfect" fit.

### Pitfall 3: Forgetting the mesh swap on scene bind
**What goes wrong:** Process writes the split mesh + residual material, but the scene renderer still uses the original mesh (no color stream) → vertex colors are white → residual (a quotient ≈ base) over-multiplies or the win is invisible.
**Why it happens:** The existing `NamerProcessor.BindGeneratedMaterials` swaps materials only; decomposition introduces a mesh dependency.
**How to avoid:** Extend the bind to swap `MeshFilter.sharedMesh` / `SkinnedMeshRenderer.sharedMesh` to the generated split mesh when decomposition is ON (and record the original for D-14 switch-back).
**Warning signs:** After preview (which shows colors) the scene object renders differently.

### Pitfall 4: Stale "EXR path ready" assumption
**What goes wrong:** Planning against a nonexistent helper — `AssetGenerator.cs` has no `EncodeToEXR` call (only PNG writes).
**Why it happens:** PROJECT.md/CLAUDE.md carry a Phase-3-era note; the EXR path was never actually built.
**How to avoid:** Treat `WriteResidualExr` as a **new** AssetGenerator method: read back the residual RT as RGBAHalf, `EncodeToEXR`, import linear/uncompressed/point/no-mips (mirror `WriteSurfaceTexture` but `sRGBTexture=false`).
**Warning signs:** `grep EncodeToEXR` returns nothing (verified: zero hits in the package).

### Pitfall 5: Alpha/transparency lost in the no-texture endgame (D-13)
**What goes wrong:** Dropping the residual entirely also drops `baseResidual.a`, breaking cutout/transparent alpha.
**Why it happens:** The shader derives alpha from `baseResidual.a × _BaseColor.a`; "residual not required" removes that source.
**How to avoid:** Only auto-drop for fully opaque materials (`alpha ≡ 1`); otherwise keep a residual (even a trivial white-RGB / base-alpha one).
**Warning signs:** Cutout materials become solid after decomposition.

### Pitfall 6: `SetIndexBufferData`/low-level Mesh API leaves sub-meshes unset
**What goes wrong:** Using the low-level `SetIndexBufferParams`/`SetIndexBufferData` path without `subMeshCount` + `SetSubMesh` yields a mesh that renders nothing.
**Why it happens:** The low-level API does not initialize sub-mesh descriptors (documented gotcha).
**How to avoid:** Prefer the high-level `SetVertices`/`SetTriangles` path (simpler, matches existing code); if the low-level path is used, always set `subMeshCount` + `SetSubMesh` per sub-mesh.
**Warning signs:** Zero-triangle render, `mesh.triangles.Length == 0` after write.

## Code Examples

### Residual quotient kernel (grounded in NAMERPack.compute conventions)
```hlsl
// Source: pattern from Packages/.../Compute/NAMERPack.compute + NAMERAO.compute (codebase)
#pragma kernel CSRasterizeVertexColors
#pragma kernel CSResidual
#pragma kernel CSErrorHeatmap

RWTexture2D<float4> _VcInterp;      // R8G8B8A8_UNorm linear; a = coverage
RWTexture2D<float4> _ResidualOut;   // R16G16B16A16_SFloat linear
RWTexture2D<float4> _ErrorHeatmap;  // R8G8B8A8_UNorm (viridis)
Texture2D<float4>  _BaseLinear;
StructuredBuffer<float3> _Verts; StructuredBuffer<float2> _Uvs; StructuredBuffer<int3> _Tris;
StructuredBuffer<float4> _Colors;   // quantized Color32 as float4
float _VcFloor; float _Threshold; uint _TriCount; uint2 _Size;

[numthreads(8,8,1)]
void CSResidual(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= _Size.x || id.y >= _Size.y) return;
    float4 vc = _VcInterp[id.xy];
    if (vc.a < 0.5) { _ResidualOut[id.xy] = float4(1,1,1,_BaseLinear[id.xy].a); return; } // uncovered
    float3 b = _BaseLinear[id.xy].rgb;
    float3 v = max(vc.rgb, _VcFloor);
    _ResidualOut[id.xy] = float4(b / v, _BaseLinear[id.xy].a);
}
```

### Error metric (one consistent definition — Claude's discretion)
```
err(x) = mean channel MAE = ( |r_rec − r_base| + |g_rec − g_base| + |b_rec − b_base| ) / 3
  where rec = vcInterp(x) · residual(x)   (post-residual reconstruction)
        fit-only case: residual ≡ 1  →  rec = vcInterp(x)
Coverage = % of UV-covered texels with err(x) ≤ _Threshold (default 0.02)
Avg      = mean err over covered texels ;  Max = max err
Residual requirement = (fit-only max err > threshold)  →  "required" else "not required" (D-13)
```
The same `err(x)` drives the stats block (D-09), the heatmap (D-11), and the adaptive-search gate (D-16). Heatmap normalizes `err / maxObservedErr` through the viridis 5-anchor ramp.

### Adaptive residual search (D-16 / VCOL-05)
```
1. compute fit-only err; if maxErr ≤ threshold → residual "not required" (D-13), stop.
2. else residual required: generate full-res quotient residual.
3. for R in {source/2, source/4, ..., 128}:      // downward halving
       downsample residual to R (bilinear), upsample to source, re-evaluate err
       if maxErr > threshold → stop; choose the previous R.
4. manual override (D-17): ladder dropdown selects one of {Auto, 2048, 1024, 512, 256, 128}.
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Per-loop nearest-texel averaging (Blender `assign_vertex_colors`) | Per-triangle multi-sample barycentric least-squares | Phase 4 (D-03/D-04) | Deliberately diverges from Blender; fit-quality alpha replaces luminance-variance heuristic |
| `RenderTextureFormat` legacy enum | `GraphicsFormat` | Phase 2 | Continue using `GraphicsFormat` (R16G16B16A16_SFloat for residual, R8G8B8A8_UNorm for packed/VC-interp) |
| `Texture2D.ReadPixels` (sync) | `AsyncGPUReadback` | Phase 3 | Residual/error readback must use the async contract (`RequestReadback`) |
| Single monolithic compute file | Per-stage compute files (`NAMERPack.compute`, `NAMERAO.compute`) | Phase 03.1 | Add `NAMERDecomp.compute`, not more kernels in `NAMERPack.compute` |

**Deprecated/outdated:**
- `RenderTextureFormat` for new code (use `GraphicsFormat`).
- sRGB `GraphicsFormat` as a compute write target (compute into linear; residual is linear EXR).
- Any assumption that `AssetGenerator` already writes EXR — it does not (verified).

## Assumptions Log

> Claims tagged `[ASSUMED]` — no external package registry, but design choices where multiple valid approaches exist and no authoritative reference is available. These need planner/discuss confirmation only if they contradict a locked decision (they do not — all are within Claude's Discretion).

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | Residual is the multiplicative quotient `base / max(vcInterp, eps)` (not subtractive) | Summary / D-01 resolution | Low — derived directly from the locked multiply shader; changing it means changing the runtime shader (already flagged as out of scope) |
| A2 | Error metric = mean-channel MAE in linear RGB (not perceptual ΔE) | Error metric | Low-Med — UI-SPEC auto-selected MAE [0,1]; ΔE would be more perceptually accurate but GPU-costlier and non-linear |
| A3 | Fit uses a fixed interior barycentric grid (~16 samples/tri) rather than texel-aligned walk | LSQ pattern | Low — both satisfy "multi-sample"; grid is deterministic and well-conditioned |
| A4 | Per-vertex fit-quality alpha = `1 − min(perVertexErr/kRef, 1)` | D-04 | Low — exact formula is Claude's discretion; only consumed by debug tooling |
| A5 | Adaptive gate = `maxErr ≤ threshold` (i.e. 100% coverage) for the quality-first bar | Adaptive search | Medium — a single noisy texel could block downsampling; may want a coverage target (e.g. 99%) instead |
| A6 | 3×3 solve = symmetric closed-form/Cramer (not Jacobi) | LSQ pattern | Low — either is deterministic; Cramer is simplest for well-conditioned Gram matrices |

## Open Questions

1. **Coverage gate vs max-error gate for the adaptive search (A5)**
   - What we know: threshold is "maximum acceptable reconstruction error"; D-16 wants "never worse than full-res".
   - What's unclear: whether to gate on 100% coverage (strict) or a high percentile (practical for noisy textures).
   - Recommendation: default to `maxErr ≤ threshold` (strict, matches "never worse"); expose a named `kCoverageTarget` constant the planner can relax to 99% if real assets show single-texel outliers.

2. **Blend shapes / skinned meshes in the splitter**
   - What we know: `boneWeights`/`bindposes` must be preserved for skinned renderers; blend shapes reference vertex indices and are hard to re-map.
   - What's unclear: whether any v1 source assets use blend shapes.
   - Recommendation: preserve bone weights + bindposes; emit a warning (not a hard failure) and skip blend shapes for MVP.

3. **D-14 switch depth (preview-only vs scene-object rebind)**
   - What we know: UI-SPEC specifies the toggle doubles as the switch and re-binds the after pane via `NamerAfterPanelState`; D-14 says "instant, no reprocessing".
   - What's unclear: whether the scene object must also flip between original/split mesh on toggle (or only the preview pane).
   - Recommendation: preview pane flips instantly (reuse `PreferGenerated`/`MarkTweaking`); the scene object is re-bound by Process (split mesh + residual) and stays decomposed until reprocessed — document this MVP boundary.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Unity Editor | host | ✓ | 6000.0.82f1 (Linear color space) | — |
| Unity.Burst | LSQ fit + split | ✓ | bundled 1.8.x (Editor asmdef ref) | — |
| Unity.Mathematics | 3×3 solver | ✓ | 1.3.x (Editor asmdef ref) | — |
| Unity.Collections | NativeArray buffers | ✓ | 2.5.x (transitive via Burst) | — |
| Compute (Metal/DX/Vulkan) | residual/error kernels | ✓ | already exercised by NAMERPack/NAMERAO | — |
| `EncodeToEXR` | residual write | ✓ | Unity 6 built-in | `ImageConversion.EncodeToEXR` |

**Missing dependencies with no fallback:** none — all required capabilities are Unity built-ins or already-installed packages.

**Missing dependencies with fallback:** none.

## Security Domain

`security_enforcement` is absent from `.planning/config.json` (absent = enabled per policy), so this section is included. This phase is a **local, offline editor tool** with no network surface, no authentication, no session, and no cryptography. Most ASVS categories are not applicable.

### Applicable ASVS Categories

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | n/a — no auth surface |
| V3 Session Management | no | n/a — no sessions |
| V4 Access Control | no | n/a — no multi-user access |
| V5 Input Validation | yes (partial) | Reuse the existing hardened path validation in `AssetGenerator` (destination confinement, prefix/suffix segment checks, `NamerGenerated`-stamp overwrite gate); extend to the new mesh/EXR composed paths so no write escapes the destination folder |
| V6 Cryptography | no | n/a — no crypto; do not introduce any (no hashing beyond the existing SHA-256 immutability test) |

### Known Threat Patterns for {Unity editor mesh/EXR generation}

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Path traversal via prefix/suffix/destination escaping the generated folder | Tampering | Reuse `ValidateComposedPath`/`ValidatePathSegment`/`ValidateDestinationFolder` (already present) for the mesh `.asset` and residual `.exr` paths before any write |
| Overwriting a non-generated source asset | Tampering | Reuse `EnsureWritableTarget` + `NamerGenerated` label stamp on the mesh + EXR (same as textures) |
| Malformed mesh/topology (degenerate UVs, zero-area triangles) causing divide-by-zero in the fit | Denial of Service (editor hang/crash) | Guard barycentric denominators (reuse `kBarycentricAreaEps` from `NamerAOBaker`); clamp `vcInterp` floor before quotient; bail to a per-triangle fallback rather than throwing |

## Sources

### Primary (HIGH confidence — codebase, read directly)
- `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl` (line 94) — multiply recombination + vertex-color plumbing
- `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader` (lines 113–123, 195–261) — `COLOR` semantic already plumbed; Meta pass omits vertex color (limitation)
- `Packages/com.graffitientertainment.namer/Editor/Bake/NamerAOBaker.cs` + `NamerAOBvh.cs` — Burst/Collections/Mathematics mesh-processing precedent + barycentric math to reuse
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs` — GPU harness + hierarchical reduction precedent (`ReduceToScalarMean`)
- `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs` — sole disk writer (PNG only; **no EXR path**)
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs` + `ComputeTexturePool.cs` — `RequestReadback`/pool contracts
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — `BindGeneratedMaterials` (materials only; no mesh swap)
- `Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs` + `NamerEditorConstants.cs` — EditorPrefs + named-constant pattern
- `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader` — debug channels 0–5 (no COLOR semantic yet)
- `Packages/com.graffitientertainment.namer/Editor/GraffitiEntertainment.Namer.Editor.asmdef` — Burst + Mathematics refs confirmed
- `ProjectSettings/ProjectSettings.asset` (line 50) — `m_ActiveColorSpace: 1` (Linear)

### Secondary (MEDIUM confidence — official docs via web search)
- [Unity ScriptReference: Mesh.indexFormat](https://docs.unity3d.com/ScriptReference/Mesh-indexFormat.html) — 16-bit default (65535 verts) vs `IndexFormat.UInt32`
- [Unity 6 Docs: Mesh.SetIndexBufferParams](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Mesh.SetIndexBufferParams.html) — low-level API; sub-mesh must be set manually
- [Unity ScriptReference: Texture2D.EncodeToEXR](https://docs.unity3d.com/560/Documentation/ScriptReference/Texture2D.EncodeToEXR.html) — RGBAHalf/RGBAFloat requirement, `isReadable`
- [Unity 6 Docs: ImageConversion.EncodeToEXR](https://docs.unity3d.com/6000.5/Documentation/ScriptReference/ImageConversion.EncodeToEXR.html) — `EXRFlags.OutputAsFloat`

### Tertiary (LOW confidence — community, for corroboration only)
- Unity Discussions "65535 vertices limit" / "SetIndexBufferData submesh gotcha" — corroborate the two Mesh API facts above

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — no new packages; all deps verified present in the asmdef/manifest
- Architecture: HIGH — recombination math + GPU/Burst tier split grounded in the actual shader and AO precedents; seam-splitting specifics are MEDIUM (no authoritative reference, flagged in STATE.md)
- Pitfalls: HIGH — Pitfalls 1–6 are derived from the read code (stale EXR claim, mesh-swap gap, quantization timing) and verified API gotchas

**Research date:** 2026-08-31
**Valid until:** 2026-09-14 (stable domain; recompute if Unity mesh/EXR APIs change)

## RESEARCH COMPLETE

**Phase:** 4 - Vertex-Color Decomposition + Residual
**Confidence:** HIGH

### Key Findings
1. **Recombination stays multiply (D-01)**; the residual is a multiplicative **quotient** `base / max(vcInterp, eps)`, not a subtractive difference. Runtime shader is already fully plumbed (`NAMER.shader` `COLOR` semantic + `NamerSurface.hlsl` line 94) — zero runtime shader changes needed.
2. **The "EXR path ready" claim is stale** — `AssetGenerator.cs` has no `EncodeToEXR` anywhere; the residual EXR writer is a new build item.
3. **Scene-object bind must swap the mesh**, not just the material: `NamerProcessor.BindGeneratedMaterials` only swaps materials today, but decomposition requires `sharedMesh` → split mesh (else vertex colors are white at runtime).
4. **Per-triangle LSQ fit is CPU Burst** (mirror `NamerAOBaker`): per-vertex 3×3 Gram matrix + hand-rolled solve, sampling the linear base via one RGBA32 readback; residual/error/heatmap are a new `NAMERDecomp.compute` (mirror `NAMERAO.compute`).
5. **Zero new packages** — Burst/Mathematics/Collections/compute already present; the only new Unity built-in is `EncodeToEXR` (RGBAHalf) + Mesh `.asset` write (`UInt32` index when >65535 verts).

### File Created
`.planning/phases/04-vertex-color-decomposition-residual/04-RESEARCH.md`

### Confidence Assessment
| Area | Level | Reason |
|------|-------|--------|
| Standard Stack | HIGH | No new packages; deps verified in asmdef/manifest |
| Architecture | HIGH | Tier split + quotient residual grounded in read source; seam-split specifics MEDIUM |
| Pitfalls | HIGH | Derived from the read code and verified API gotchas |

### Open Questions
- Coverage-gate vs max-error-gate for the adaptive search (recommend strict `maxErr ≤ threshold` with a tunable `kCoverageTarget`).
- Blend-shape handling in the splitter (recommend preserve bone weights/bindposes, warn+skip blend shapes for MVP).
- D-14 switch depth: preview-only vs scene-object rebind (recommend preview flip instant, scene stays decomposed until reprocess).

### Ready for Planning
Research complete. Planner can now create PLAN.md files.

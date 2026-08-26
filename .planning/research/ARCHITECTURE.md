# Architecture Research

**Domain:** Unity editor asset-processing plugin (texture-processing pipeline, GPU compute + CPU mesh math)
**Researched:** 2026-08-25
**Confidence:** HIGH (standard Unity editor patterns; specific GPU-readback details verified against official docs)

## Standard Architecture

### System Overview

NAMER is a layered editor-time pipeline. The defining structural fact is a **one-way data flow**:
source assets are **read-only**, intermediate data lives in **GPU RenderTextures**, and output is **written only
to a generated-assets directory**. CPU work (asset inspection, vertex-color math, serialization) and GPU work
(per-pixel texture ops) are split along the constraint line from `PROJECT.md`.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            UX LAYER (Editor asmdef)                          │
├─────────────────────────────────────────────────────────────────────────────┤
│  ┌────────────────────┐   ┌───────────────────────────────┐                 │
│  │  NAMEREditorWindow  │──▶│  PreviewRenderer              │                 │
│  │  (UI Toolkit panels │   │  (PreviewRenderUtility +      │                 │
│  │   + IMGUIContainer  │   │   debug-channel material)     │                 │
│  │   for mesh preview) │   └───────────────────────────────┘                 │
│  └─────────┬──────────┘                                                      │
│            │ command + settings                                              │
├────────────┴─────────────────────────────────────────────────────────────────┤
│                      ORCHESTRATION LAYER (Editor asmdef)                     │
├─────────────────────────────────────────────────────────────────────────────┤
│  ┌──────────────────────────────────────────────────────────┐               │
│  │  NAMERProcessor  (Process with NAMER command)             │               │
│  │  - builds NAMERProcessContext (immutable input snapshot)  │               │
│  │  - sequences stages, owns progress + cancellation         │               │
│  └───────┬───────────────────────────────────────────────────┘               │
│          │                                                                    │
│   ┌──────┴──────┐        read-only          write-only                        │
│   ▼             ▼                           (AssetDatabase)                   │
│  SourceModel  ──Pipeline stages──────────────────────────▶ AssetGenerator    │
└───────────────────────────────────────────────────────────────────────────────┘
```

### Pipeline Stages (the "processor" dimension)

```
Source (read-only)                     Generated (write-only)
─────────────                         ─────────────
 FBX / GameObject / Material
   │  SourceInspector (CPU, AssetDatabase.Load*)
   ▼
 NAMERSourceModel  (materials, meshes, texture refs, importer settings)
   │
   ▼  TextureNormalizer (GPU compute)  ──▶ normalized albedo/normal/AO/metallic/roughness/emission
   │
   ▼  OctahedralEncoder (GPU compute)  ──▶ octahedral normal RG
   │
   ▼  SurfacePacker (GPU compute)      ──▶ surface texture (RGBA packed)
   │                                          R=octX  G=octY  B=AO  A=metallic|emissive|roughness(6bit)
   │
   ├─▶ VertexColorFitter (CPU) ──▶ vertex colors (Mesh)
   │        │ read base-color RT back to CPU (AsyncGPUReadback)
   │        ▼
   │   ResidualProcessor (GPU + CPU)  ──▶ residual texture + error metric + adaptive resolution
   │
   ▼  Stylizer (GPU compute, profile-driven, optional)
   │
   ▼  AssetGenerator (CPU)  ──▶ base/residual texture, surface texture, mesh(+vertex colors),
   │                             URP material, shader reference  →  saved under generated-assets/
   │
   └──▶ PreviewRenderer reads stage RTs / output for before-after + debug channels
```

### Component Boundaries

| Component | Responsibility | Talks To | Implementation |
|-----------|----------------|----------|----------------|
| **NAMEREditorWindow** | Entry point at `Tools > NAMER > Processor`; settings UI; preview host | NAMERProcessor, PreviewRenderer | `EditorWindow` + UI Toolkit `CreateGUI()`; IMGUIContainer for mesh viewport |
| **NAMERProcessor** | Orchestrates the run; builds immutable context; progress/cancel | all stages, SourceInspector | Plain C# class, called from window and menu command |
| **SourceInspector** | Reads source materials/meshes/textures into an immutable model; resolves missing-map defaults | NAMERProcessor | `AssetDatabase.LoadAssetAtPath` / `AssetDatabase.LoadAllAssetsAtPath` |
| **NAMERSourceModel** | Immutable snapshot of source refs + importer settings (sRGB flags, etc.) | passed into every stage | Plain C# data record (no Unity asset mutation) |
| **TextureNormalizer** | Normalize arbitrary source maps to canonical NAMER maps with scalar/missing-map fallbacks | GPU compute | `ComputeShader` kernel + C# wrapper |
| **OctahedralEncoder** | Encode tangent-space normal → octahedral RG | GPU compute | `ComputeShader` kernel |
| **SurfacePacker** | Pack octahedral normal/AO/metallic/emissive/roughness into RGBA | GPU compute | `ComputeShader` kernel |
| **VertexColorFitter** | Barycentric least-squares fit of base color → vertex colors; seam splitting | Core solver, mesh data, GPU readback | CPU C# (per-triangle math, no per-pixel loops) |
| **ResidualProcessor** | Compute residual texture, reconstruction error, adaptive resolution | VertexColorFitter, GPU compute | GPU kernel + CPU reduction |
| **Stylizer** | Profile-driven palette mapping, edge-preserving smoothing, AO/cavity/normal/roughness control | NAMERStyleProfile, GPU compute | One-or-more `ComputeShader` kernels |
| **AssetGenerator** | Write textures/mesh/material to generated-assets dir; never touch source | AssetDatabase | `Texture2D.EncodeToPNG`/EXR, `AssetDatabase.CreateAsset` |
| **PreviewRenderer** | Interactive before/after mesh preview + debug channels | stage RTs, shader | `PreviewRenderUtility` |
| **ComputeTexturePool** | Lease/reuse `RenderTexture`s to avoid churn | all GPU stages | Small RT pool, `enableRandomWrite` |
| **Core format library** | Octahedral encode/decode, bit packing, LSQ solver, color math — single source of truth | CPU stages + tests + shader mirror | Pure C# (no `UnityEngine.Object`), unit-testable |
| **Runtime NAMER shader** | Decode packed surface + vertex-color reconstruction at runtime | material at render time | URP `.shader` (HLSL) |
| **NAMERStyleProfile / NAMERSettings** | Authoring data (3–10 ref images, palette, per-channel controls) | Stylizer, window | `ScriptableObject` |

## Recommended Project Structure

UPM package `com.graffitientertainment.namer`, split by compilation domain. Every leaf that
contains code gets its own `.asmdef` so assemblies compile independently and reference only what they need.

```
Packages/com.graffitientertainment.namer/
├── package.json                       # package metadata + dependencies
├── README.md
├── Runtime/                           # ships with player (decode + material helpers only)
│   ├── Namer.Runtime.asmdef           # refs: UnityEngine (no UnityEditor)
│   ├── Shaders/
│   │   └── NamerLit.shader            # URP NAMER decode shader
│   └── Scripts/
│       └── NamerMaterial.cs           # property IDs, decode helper (thin)
├── Core/                              # shared format math — referenced by Runtime, Editor, Tests
│   ├── Namer.Core.asmdef              # refs: UnityEngine (types only, no Object)
│   └── Scripts/
│       ├── Octahedral.cs              # encode/decode float2 <-> unit vector
│       ├── SurfacePacking.cs          # bit packing (metallic/emissive/roughness into A)
│       ├── VertexColorSolver.cs       # barycentric least-squares
│       └── ColorMath.cs               # hue-aware mapping, palette math
├── Editor/                            # editor-time pipeline (excluded from player)
│   ├── Namer.Editor.asmdef            # refs: Core, UnityEngine, UnityEditor
│   ├── Compute/                       # .compute shaders (GPU passes)
│   │   ├── Normalize.compute
│   │   ├── OctahedralEncode.compute
│   │   ├── SurfacePack.compute
│   │   ├── Residual.compute
│   │   └── Stylize*.compute
│   ├── Pipeline/
│   │   ├── NamerProcessor.cs
│   │   ├── SourceInspector.cs
│   │   ├── NamerSourceModel.cs
│   │   ├── NamerProcessContext.cs
│   │   ├── ComputeTexturePool.cs
│   │   └── Stages/                    # one file per stage (see table)
│   ├── UI/
│   │   ├── NamerEditorWindow.cs
│   │   └── PreviewRenderer.cs
│   └── Generation/
│       └── AssetGenerator.cs
├── Profiles/                          # ScriptableObject assets (authored, not code)
│   └── DefaultStyleProfile.asset
├── Tests/                             # edit-mode tests (Editor platform only)
│   ├── EditMode/
│   │   ├── Namer.Tests.EditMode.asmdef   # refs: Core, Editor, UnityEngine.TestRunner, UnityEditor.TestRunner
│   │   ├── OctahedralTests.cs
│   │   ├── SurfacePackingTests.cs
│   │   ├── VertexColorSolverTests.cs
│   │   ├── PipelineTests.cs           # GPU-in-editor integration (readback round-trip)
│   │   └── AssetSafetyTests.cs        # source immutability, output paths
│   └── PlayMode/
│       ├── Namer.Tests.PlayMode.asmdef   # refs: Runtime, Core, UnityEngine.TestRunner
│       └── DecodeTests.cs             # runtime shader decode correctness
└── Samples~/                          # tilde = excluded from package import
    └── ...
```

### Structure Rationale

- **`Runtime/` vs `Editor/`:** the hard split Unity enforces at build time. `Runtime/` is the only
  code that ships in a player; the entire pipeline is editor-only and must not leak into builds.
- **`Core/` as a third assembly:** the encoding/bit-packing/math spec is shared by the editor pipeline
  (encode), the runtime shader (decode), and the tests (verify). Extracting it keeps `Runtime/` free of
  editor types and makes the format spec independently unit-testable without a GPU. This is the
  "encapsulate what varies" boundary — the packed format is the contract everything else depends on.
- **`Compute/` inside `Editor/`:** compute shaders only run in-editor for v1 (no runtime conversion),
  so they belong to the editor assembly, not Runtime.
- **`Tests/EditMode` vs `Tests/PlayMode`:** Unity Test Framework splits by platform — edit-mode tests
  select **Editor** platform only; play-mode tests run in a player. GPU-compute integration tests run
  in edit mode (dispatch + readback without entering play mode). Runtime decode tests run in play mode.
- **`Profiles/`:** ScriptableObject *data* lives alongside code as authored assets; the profile class
  itself lives in the Editor assembly (or a shared `Profiles` assembly if runtime ever needs it — it
  doesn't for v1, so keep it in Editor).

## Architectural Patterns

### Pattern 1: Compute pass as a stateless kernel wrapper

**What:** Each GPU stage is a thin C# wrapper that (a) resolves the kernel index once, (b) binds
inputs/constants, (c) sets the output `RWTexture2D`/`RWStructuredBuffer`, (d) dispatches, and (e)
returns the output `RenderTexture` — holding no mutable state.

**When to use:** Every per-pixel texture operation (normalize, octahedral, pack, residual, stylize).

**Trade-offs:** Cheap, composable, easily parallelized/ordered. Requires an explicit `ComputeTexturePool`
to avoid `RenderTexture` churn and an explicit readback path (`AsyncGPUReadback`) whenever CPU needs the
result.

**Example:**
```csharp
// Editor/Pipeline/Stages/OctahedralEncoder.cs
public RenderTexture Encode(NAMERProcessContext ctx, RenderTexture normalRT)
{
    int kernel = shader.FindKernel("CSOctahedralEncode");
    shader.SetTexture(kernel, "Normal", normalRT);
    shader.SetTexture(kernel, "Result", ctx.pool.Get(ctx.width, ctx.height));
    shader.SetInts("_Size", ctx.width, ctx.height);
    shader.Dispatch(kernel, ctx.width / 8, ctx.height / 8, 1);   // [numthreads(8,8,1)]
    return result;
}
```

### Pattern 2: Pipeline stage as a pure transform over an immutable context

**What:** Every stage is a function `StageOutput Run(NAMERProcessContext, StageInput)`. The context
holds the read-only source snapshot + user settings + profile; each stage returns new artifacts and
never mutates the input. `NAMERProcessor` sequences them.

**When to use:** The whole pipeline. It makes stages independently testable (feed a synthetic context),
lets the processor report progress between stages, and guarantees source immutability by construction.

**Trade-offs:** More boilerplate (context objects) vs. a monolithic "do everything" method. The payoff
is that cancellation, progress, and unit tests become trivial to insert between stages.

### Pattern 3: Core encoding spec as the single source of truth (C# reference + HLSL mirror)

**What:** The packed-surface format, octahedral mapping, and vertex-color model are defined once in
`Core/` as pure C#. The HLSL `.compute` and `.shader` files implement the *same* math, and edit-mode
tests verify the GPU result against the CPU reference via `AsyncGPUReadback` round-trips.

**When to use:** Any format that must match across CPU (encode/serialize), GPU (compute), and shader
(decode) — i.e., the NAMER texture format itself.

**Trade-offs:** Duplicated logic (C# and HLSL) risks drift. Mitigated by round-trip tests that fail the
moment the two implementations disagree.

**Example (bit packing — the highest-risk contract):**
```csharp
// Core/Scripts/SurfacePacking.cs
public static Color32 PackSurface(Vector2 oct, byte ao, bool metallic, bool emissive, byte roughness6)
{
    byte a = (byte)((roughness6 & 0x3F)              // bits 0-5: 64 roughness values
                   | (emissive  ? 0x40 : 0)          // bit 6
                   | (metallic  ? 0x80 : 0));        // bit 7
    return new Color32(ToByte(oct.x), ToByte(oct.y), ao, a);
}
```
```hlsl
// Editor/Compute/SurfacePack.compute (mirror)
uint roughness = floor(saturate(roughness) * 63.0 + 0.5);   // 0..63
uint a = roughness | (emissive ? 0x40 : 0) | (metallic ? 0x80 : 0);
Result[id.xy] = float4(octX, octY, ao, a / 255.0);
```

### Pattern 4: ScriptableObject as authored data (profile-driven, style-agnostic)

**What:** Stylization parameters (reference images, palette, per-channel controls) are a
`NAMERStyleProfile : ScriptableObject`. The `Stylizer` stage reads a profile instance; the editor
window binds a profile selection field. No hard-coded styles.

**When to use:** Anything an artist authors and reuses across unrelated assets.

**Trade-offs:** ScriptableObjects are Unity serialized assets (editor-friendly, inspector-editable),
but their mutable state means the pipeline should copy the values it needs into the context rather than
read the asset mid-run.

### Pattern 5: Asset generation as a separate write-only stage

**What:** `AssetGenerator` is the *only* code that writes to the `AssetDatabase`, and it only ever
writes under `generated-assets/`. It never calls `AssetDatabase.ImportAsset` on source, never mutates
source importer settings, and never loads a source texture with read/write enabled (it reads via
`RenderTexture`/`Graphics.Blit` instead).

**When to use:** End of every pipeline run; also exercised heavily by `AssetSafetyTests`.

**Trade-offs:** Separating generation from processing means the pipeline can run purely in memory for
preview (fast) and only materialize files on "Save". Cost is a second code path for encode-to-file vs.
keep-in-memory.

## Data Flow

### Run Flow (end to end)

```
User: select FBX/GameObject → Tools > NAMER > Processor → Process with NAMER
  ↓
NAMEREditorWindow ──settings + profile──▶ NAMERProcessor
  ↓
NAMERProcessor → SourceInspector → NAMERSourceModel (read-only snapshot)
  ↓
build NAMERProcessContext { sourceModel, settings, profile, pool, width/height }
  ↓
for each stage (progress reported per stage, cancellable):
  TextureNormalizer   : source maps ──GPU──▶ normalized RTs
  OctahedralEncoder   : normal RT    ──GPU──▶ octahedral RG RT
  SurfacePacker       : octa+AO+met+emis+rough ──GPU──▶ surface RT (RGBA)
  VertexColorFitter   : base RT ──AsyncGPUReadback──▶ CPU ──LSQ──▶ vertex colors (Mesh)
  ResidualProcessor   : base RT + fitted interp ──GPU──▶ residual RT + error metric
  Stylizer (optional) : base RT + surface RT + profile ──GPU──▶ stylized RTs
  ↓
AssetGenerator : RTs ──EncodeToPNG/EXR──▶ textures; mesh(+vertex colors); material; shader
  ↓
write under generated-assets/  (source never modified)
  ↓
PreviewRenderer ← stage RTs / output  →  before/after + debug channel views
```

### Key Data Flows

1. **Source read (CPU, read-only):** `SourceInspector` loads materials/textures/mesh via
   `AssetDatabase`, records importer flags (sRGB, wrap mode), and stores references — no pixels copied,
   no settings mutated.

2. **GPU-resident intermediate flow:** Normalized → octahedral → packed surface → residual → stylized.
   All intermediates are `RenderTexture`s leased from `ComputeTexturePool`; nothing round-trips to CPU
   unless the CPU stage (vertex-color fit) or the file writer needs it.

3. **GPU → CPU readback (only where required):** `VertexColorFitter` reads the normalized base color
   back via `AsyncGPUReadback` (with `SystemInfo.supportsAsyncGPUReadback` guard), runs the per-triangle
   least-squares fit on the CPU, then pushes vertex colors into the output mesh. This is the single
   expensive CPU↔GPU boundary and the reason the constraint forbids per-pixel C# loops.

4. **CPU → GPU re-upload:** After fitting, the residual stage re-receives the fitted interpolation (as
   vertex-color-interpolated texture or mesh data) to compute `residual = source − fitted` on the GPU.

5. **File materialization (write-only):** `AssetGenerator` is the only writer. It converts RTs to
   `Texture2D` (via `ReadPixels`/`AsyncGPUReadback`), encodes to PNG/EXR, and creates assets under the
   generated directory.

## Scaling Considerations

This is an editor tool, not a service — "scaling" means texture resolution and editor responsiveness,
not user count.

| Scale | Architecture Adjustment |
|-------|--------------------------|
| 512–1K textures, single asset | Straightforward: full pipeline in one synchronous dispatch sequence; naive RT pooling is fine. |
| 2K–4K textures (project target) | Must use `ComputeTexturePool` + `AsyncGPUReadback`; avoid repeated `GetPixels` on large readable textures; dispatch in chunks; show per-stage progress. |
| 4K+ / batch of many assets | Split preview from processing (preview at reduced res, full-res on save); consider `EditorCoroutine`/async + `CancellationToken`; move vertex-color fit to Burst `IJob`/`IJobParallelFor` if per-triangle cost dominates. |

### Scaling Priorities

1. **First bottleneck — GPU readback stalls:** synchronous `GetData`/`ReadPixels` on 4K RTs freezes the
   editor. Fix: `AsyncGPUReadback` + cache results; only read back what the CPU truly needs.
2. **Second bottleneck — `RenderTexture` churn:** allocating a fresh RT per stage per run leaks/GC-thrashes.
   Fix: `ComputeTexturePool` with descriptor reuse.
3. **Third bottleneck — per-triangle CPU fit on high-poly meshes:** the LSQ fit is O(triangles × samples).
   Fix: Burst-compiled jobs, and only when profiling shows it; do not preemptively optimize.

## Anti-Patterns

### Anti-Pattern 1: Per-pixel C# loops on textures

**What people do:** iterate `GetPixels` and loop per-pixel in C# for normalization/packing/stylization.
**Why it's wrong:** explicitly forbidden by `PROJECT.md`; 4K = 16M pixels × 7 maps, unreasonably slow.
**Do this instead:** GPU compute kernels; CPU is only for asset inspection, mesh math, serialization.

### Anti-Pattern 2: Mutating source assets or importer settings

**What people do:** set `textureImporter.isReadable = true`, change sRGB/compression, or `SaveAssets()`
on the source to make processing easier.
**Why it's wrong:** violates the core "never modify sources" guarantee; pollutes the project and VCS.
**Do this instead:** read via `RenderTexture`/`Graphics.Blit` (honoring the source's sRGB flag); write
only to `generated-assets/`.

### Anti-Pattern 3: Divergent encode/decode implementations

**What people do:** write the pack/unpack bit math separately in C# and HLSL without a shared reference.
**Why it's wrong:** a single off-by-one (e.g., roughness rounding, octahedral sign) produces subtly
wrong materials that pass visual review but break cross-tool compatibility.
**Do this instead:** `Core/` as single source of truth + round-trip tests comparing CPU reference to GPU.

### Anti-Pattern 4: Blocking the editor UI on long synchronous compute

**What people do:** run the entire pipeline in `OnGUI`/button callback with no progress or cancellation.
**Why it's wrong:** editor freezes for seconds; user cannot cancel a 4K stylization.
**Do this instead:** sequence stages with progress callbacks and a `CancellationToken`; keep the window
responsive between stages; preview at reduced resolution.

### Anti-Pattern 5: sRGB vs linear mishandling in color math

**What people do:** treat albedo/base-color (sRGB-encoded) and normal/AO/roughness (linear) identically
in compute shaders.
**Why it's wrong:** color math in the wrong space shifts hue/lighting; breaks palette mapping and
residual correctness.
**Do this instead:** record each source texture's `sRGBTexture` flag in `NAMERSourceModel`; sample/convert
explicitly in the compute shader; do hue-aware palette math in a consistent (linear) space.

### Anti-Pattern 6: One giant editor assembly

**What people do:** put everything in a single asmdef that references `UnityEditor`, then try to unit-test it.
**Why it's wrong:** editor assembly cannot be referenced by play-mode tests and cannot ship; slow recompiles;
hard to keep the format spec testable.
**Do this instead:** three assemblies — `Core` (shared math), `Runtime` (decode), `Editor` (pipeline) —
with explicit asmdef references.

## Integration Points

### External / Unity Boundaries

| Boundary | Integration Pattern | Notes |
|----------|---------------------|-------|
| Source assets → pipeline | `AssetDatabase.LoadAssetAtPath` (read-only) | Never `ImportAsset`/`SaveAssets` on source. |
| Pipeline → GPU | `ComputeShader.Dispatch` + `RenderTexture` (`enableRandomWrite`) | Guard readback with `SystemInfo.supportsAsyncGPUReadback`. |
| GPU → CPU | `AsyncGPUReadback.Request` / `RequestIntoNativeArray` | Adds a few frames latency; check completion. |
| Pipeline → files | `Texture2D.EncodeToPNG`/`EncodeToEXR` + `AssetDatabase.CreateAsset` | Only under generated-assets dir. |
| Preview → screen | `PreviewRenderUtility` (BeginPreview/DrawMesh/Render/EndPreview) | Hosted in an `IMGUIContainer` inside the UI Toolkit window. |
| Editor UI | UI Toolkit (`CreateGUI`) for panels; `IMGUIContainer` for mesh viewport | UI Toolkit is the current standard for new editor UI. |

### Internal Boundaries

| Boundary | Communication | Notes |
|----------|---------------|-------|
| Window ↔ Processor | direct method call + settings/profile objects | Window never touches `AssetDatabase` directly. |
| Processor ↔ Stages | immutable `NAMERProcessContext` in / stage output out | Enables progress, cancellation, unit tests. |
| CPU stage ↔ Core solver | direct call (pure functions) | Core has no `UnityEngine.Object` deps. |
| Editor ↔ Runtime shader | material asset with shader reference | Editor writes the material; Runtime only decodes. |
| Tests ↔ GPU | edit-mode dispatch + `AsyncGPUReadback` round-trip | Round-trip is the format-correctness gate. |

## Suggested Build Order (dependency-ordered)

1. **Core format library + unit tests** — octahedral encode/decode, surface bit packing, LSQ solver.
   No GPU needed; pure C#; fastest feedback; establishes the format contract everything else depends on.
2. **Runtime NAMER shader (decode)** — verifies Core's encoding decodes correctly at runtime (play-mode
   round-trip test against Core).
3. **Compute dispatch harness + one GPU pass** — texture normalizer; proves GPU-in-editor works end-to-end
   with readback; establishes `ComputeTexturePool` + edit-mode GPU test scaffold.
4. **Remaining surface passes** — octahedral encode + surface pack (GPU), verified against Core.
5. **Asset generator** — materialize surface/base textures + material + mesh; this yields a minimal
   end-to-end "source material → NAMER material" pipeline (normalize → pack → generate → runtime render).
6. **Editor window + preview** — basic before/after mesh preview; the UX layer on top of a working pipeline.
7. **Vertex-color fitter + residual + adaptive resolution + debug views** — the CPU/GPU hybrid; highest
   algorithmic risk, done after the simple pipeline is proven.
8. **Stylization** — profile ScriptableObject + GPU passes; most optional and most complex; last.

**Ordering rationale:** Core and the runtime decode are pure and cheap to build first; they lock the
format. The GPU harness de-risks the "compute in editor" question early. Asset generation turns the
pipeline into a shippable vertical slice before investing in the hard parts (vertex-color fitting,
stylization). UX (window/preview) is layered on only after the pipeline works, per the "no deferred
polish" philosophy but respecting that previews need real pipeline output to render.

## Sources

- Unity Manual — Assembly definitions: https://docs.unity3d.com/Manual/assembly-definition-files.html (HIGH)
- Unity Manual — Custom packages / package.json: https://docs.unity3d.com/Manual/CustomPackages.html (HIGH)
- Unity Scripting — `ComputeShader` (FindKernel, SetTexture, Dispatch, SetFloat/Int/Vector, keywords): https://docs.unity3d.com/ScriptReference/ComputeShader.html (HIGH)
- Unity Scripting — `AsyncGPUReadback` (Request, RequestIntoNativeArray, supportsAsyncGPUReadback): https://docs.unity3d.com/ScriptReference/Rendering.AsyncGPUReadback.html (HIGH)
- Unity Manual — UI Toolkit editor windows (CreateGUI, IMGUIContainer): https://docs.unity3d.com/Manual/UIE-HowTo-CreateEditorWindow.html (HIGH)
- Unity Test Framework — edit-mode vs play-mode, TestRunner references, Editor-only platform: https://docs.unity3d.com/Packages/com.unity.test-framework@1.1/manual/workflow-create-test.html (HIGH)
- Octahedral normal encoding: Cigolle et al., "A Survey of Efficient Representations for Independent Unit Vectors" (well-known GPU technique; MEDIUM — standard algorithm, not re-verified against a single authoritative page)
- `PreviewRenderUtility` (BeginPreview/DrawMesh/Render/EndPreview): MEDIUM — from training knowledge; official script-reference page returned 404 during research, flag for phase-6 validation

---
*Architecture research for: NAMER Unity editor plugin*
*Researched: 2026-08-25*

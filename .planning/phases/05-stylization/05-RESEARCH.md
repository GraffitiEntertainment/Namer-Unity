# Phase 5: Stylization - Research

**Researched:** 2026-09-23
**Domain:** Reference-image-driven palette/style-stat extraction (deterministic CPU Burst), hue-preserving palette transfer + island-gated bilateral smoothing (GPU compute), bake-side AO/normal/roughness styling controls, `NamerStyleProfile` ScriptableObject + IMGUI integration
**Confidence:** HIGH

## Summary

Phase 5 is almost entirely an extension of shipped house patterns — the risk is not algorithmic novelty but integration correctness. Every D-01..D-08 decision maps onto a concrete, verified code anchor: the stylize stage inserts into `NamerComputePipeline.Process` between `BindAndDispatchNormalizeEncode` and `projection.Run` (`NamerComputePipeline.cs:176-185`); the island-gated bilateral extends the `CSDilateResidual` neighborhood-loop + coverage-gate kernel shape (`NAMERDecomp.compute:257-294`); palette/stat extraction replicates the `NamerAOBaker` deterministic-Burst discipline with the `Bake_IsDeterministic` element-wise test bar (`NamerAOBakeTests.cs:49`); the D-07 controls land as identity-defaulted uniforms inside `CSSurfacePack` (after the dip-adoption branch, before `NamerPackAlphaBits`) and `CSOctahedralEncode` (before `NamerOctahedralEncode`); D-08's authored-AO remap reuses the existing `CSAoRemap` direct mode (`NamerAOPipeline.ApplyStrengthContrastDirect`, `NamerAOPipeline.cs:394-411`) plus a cavity-floor parameter in `NamerAoRemap` (`NAMERAO.hlsl:28`).

The one genuinely new subsystem is the **UV-island-ID buffer** (D-03): CPU union-find over UV-adjacent triangles of the split mesh, plus a rasterize kernel that reuses the `CSRasterizeVertexColors` per-texel/per-triangle dispatch shape. Two subtle requirements surfaced: (1) adjacency must key on **quantized UV pairs**, not vertex indices — `MeshVertexSplitter` splits vertices by the full (position, normal, tangent, uv) key, so two triangles sharing a UV-space edge across a position seam get different indices though their UVs are identical; (2) the stylize stage needs the split mesh even when decomposition is OFF (stylized preview without VCOL), so the processor/window must prime the source mesh on the same pattern as the 2026-09-21 AO contract (`primeBakeMesh = decompWillRun || _aoStageEnabled` gains `|| stylizationNeedsMesh`), with a graceful coverage-gate-only fallback when no mesh resolves.

The binding UI contract is `.planning/phases/05-stylization/05-UI-SPEC.md` (approved, checker signed off). Its copy, control inventory (W1-W4, P1-P14), defaults, and persistence model (profile values on the SO asset; window foldout/view/GUID on `NamerProcessor.*` EditorPrefs) are locked below as user constraints. Note one deviation pressure: the approved extraction-error copy ("Check the texture's Read/Write import setting") presumes a `GetPixels` acquisition path, which `docs.unity3d.com` confirms requires `Texture.isReadable == true`; an alternative GPU raw-copy + `AsyncGPUReadback` acquisition (no import-setting dependency) would make that copy unreachable — flagged as an open question with a recommendation.

**Primary recommendation:** Build 05-01 (profile + deterministic extraction) strictly on the `NamerAOBaker`/`VertexColorFitter` deterministic-Burst template; build 05-02 (island IDs + bilateral + palette transfer) as a new `NAMERStylize.compute` extending the `CSDilateResidual`/`CSRasterizeVertexColors` kernel shapes; build 05-03 (AO/normal/roughness + integration) as identity-defaulted uniforms + a trailing `Process` parameter, keeping the profile-null / all-sliders-0 path byte-identical to Phase 04.3 output so the existing 158-test EditMode suite stays green.

<user_constraints>
## User Constraints (from CONTEXT.md + approved UI-SPEC)

### Locked Decisions (05-CONTEXT.md, verbatim)

**Pipeline position**
- **D-01:** Stylization runs in UV space **after the normalize/clean dispatch and BEFORE the VC fit/projection** (`NamerComputePipeline.Process` insertion point; projection is `CSProjectBase`, vertex rasterization `CSRasterizeVertexColors`, both in `NAMERDecomp.compute`). The fit reads the stylized source, so vertex colors AND the residual both come out stylized; the one-texture default (D-13, `AssetGenerator` leaves `_BaseResidualMap` white — VCs are the sole color carrier) is covered by the same single stage. The user explicitly confirmed this as "stylize the source, then fit" — the style lands in the VCs. Post-decomposition mesh-domain VC editing was rejected: edge-preserving smoothing is atlas-space only (STYL-04 islands/seams/decals), and an un-stylized residual would re-add the original detail on top.
- **D-02:** 04.2's residual-dividend semantics carry over with the referent updated: the dividend is the **styled source** albedo (styled base minus its per-triangle projection), so the residual remains the honest reconstruction error of what actually ships. Requires a doc/test note updating 04.2-CONTEXT wording; the operator itself is unchanged.

**Smoothing filter**
- **D-03:** **Island-gated bilateral** with a strength slider (2–3 ping-pong iterations at strong flatten). Extends the existing coverage-gated neighborhood-loop pattern (`CSDilateResidual`, `NAMERDecomp.compute:257`). Requires a NEW **UV-island-ID buffer** (CPU union-find over UV-adjacent triangles + rasterized island ID reusing the `CSRasterizeVertexColors` dispatch shape) — a binary coverage mask alone fails when the atlas gutter is narrower than the filter radius. Kuwahara (painterly) is deferred: it imposes a hard-coded look, in tension with the style-agnostic mandate.

**Hue preservation + palette**
- **D-04:** ONE style-strength slider (0 = original → 1 = full palette snap) drives a single blended GPU operator: nearest palette anchor matched **by hue**, luma/sat pulled toward the anchor's stats (hue preserved), and a strength-derived hue-replacement factor ramping to 1 at slider max — this is where STYL-03's "unless stronger replacement is chosen" lives. All 3–10 references are sampled at capped resolution into ONE merged aggregate palette/stat set stored on the profile (STYL-02 stats list). Extraction is deterministic CPU Burst (NamerAOBaker pattern; `Bake_IsDeterministic` element-wise-identical test bar), run once per profile via an explicit Extract action — never on the ~300 ms debounce.
- **D-05:** An **Advanced foldout** on the profile exposes per-reference weights and an explicit preserve/replace mode enum (user: "advanced selection triggers option 2"). The simple slider remains the default face; serialization carries both so the advanced layer can evolve without breaking profiles. Test matrix must cover the advanced path explicitly.

**AO / normal / roughness controls**
- **D-06:** The profile is a **dual-channel live asset**, matching the house slider pattern (live uniform + debounced bake): sliders/gates live on the `NAMERStyleProfile`; attaching/selecting it in the Processor panel applies it to the preview on load/change; edits trigger the debounced re-Process, AND controls with existing live shader uniforms (AO strength `_OcclusionStrength`, `Shaders/NamerSurface.hlsl:85`) also respond instantly without re-Process. The bake persists final values so the saved packed asset always equals the preview.
- **D-07:** Normal-detail reduction and roughness simplification are **bake-side, before quantization** (STYL-06): roughness simplify runs after the 04.2 dip adoption and before `NamerPackAlphaBits` (`Compute/NamerEncode.hlsl:40`); normal flatten runs before octahedral encode in the surface-pack path. The packed format stores one AO channel — runtime AO blending is not representable and is out of scope.
- **D-08:** STYL-05's separate Runtime-AO / Painted-AO controls resolve to: run the AO remap on the **authored** occlusion path too (today `NamerComputePipeline.cs:143-150` uploads the authored map as raw source data with no remap), and add a **cavity floor** after contrast (`NamerAoRemap`, `Compute/NAMERAO.hlsl:28` — contrast > 1 currently saturates cavities to 0 with no floor) for the "avoid crushing cavities to black" requirement.

### Claude's Discretion (05-CONTEXT.md, verbatim)
- Mid-slider blend curve shape, palette anchor count and k-means details (fixed-seed, deterministic), island-ID rasterization specifics, kernel file layout for `NAMERStylize` compute, exact gate/slider naming and foldout copy, debounce wiring details — all within existing house patterns.

### Locked UI contract (05-UI-SPEC.md — approved 2026-09-23, binding)
- **Window:** W1 "Stylization" foldout between "AO" and "Decomposition" (draw order mirrors pipeline order); W2 Style Profile `ObjectField` (persistence = EditorPrefs GUID string); W3 attach-state miniLabel ("not extracted" / "palette extracted (N anchors, M references)"); W4 "After View" popup `{ "NAMER", "Stylized NAMER" }` in Preview/Debug above the Before/After captions, disabled with hint when no profile attached.
- **Profile inspector:** P1 header "Reference Images (3–10)"; P2 thumbnail-left rows (64×64 `EditorGUIUtility.ObjectContent` image + `ObjectField(Texture2D)`), list STARTS EMPTY (user-locked); P3 "+ / –" `miniButton` footer ("+" appends a slot disabled at 10, "–" removes LAST disabled at 0, `Undo.RecordObject` + `EditorUtility.SetDirty`, no recompute); P4 count honesty line; P5 "Extract Style Stats" button (disabled < 3 assigned refs or while extracting; synchronous with cancellable progress bar; NEVER on debounce); P6 Statistics block ("—" before first extract, staleness Warning when fingerprint ≠ current refs); P7 "Style Strength" 0–1 default 0.5; P8 "Smoothing" 0–1 default 0; P9 "Cavity Floor" 0–0.5 default 0; P10 "Normal Detail" 0–1 default 0; P11 "Roughness Simplify" 0–1 default 0; P12 "Advanced" foldout default collapsed; P13 "Hue Mode" popup `{ "Preserve", "Replace" }` default Preserve; P14 per-ref weight sliders 0–1 default 1, rendered only for assigned slots.
- **Persistence:** `NamerProcessorSettings` gains `FoldoutStylization` (default false), `AfterView` (int, default 0), `StyleProfileGuid` (string) under `NamerProcessor.*` keys; `NamerEditorConstants` gains `MinReferences = 3`, `MaxReferences = 10`, `DefaultStyleStrength = 0.5f`, `DefaultSmoothingStrength = 0f`, `DefaultCavityFloor = 0f`, `DefaultNormalDetailReduction = 0f`, `DefaultRoughnessSimplify = 0f`, `DefaultReferenceWeight = 1f`, `DefaultHueMode = 0`. Profile-side values serialize on the SO — NOT EditorPrefs.
- **Interaction:** profile attach/detach/swap and After-View flip → debounced 0.3 s recompute; ALL profile sliders → debounced recompute + `Undo.RecordObject`/`SetDirty` (live-uniform instant path NOT used for new controls — all Phase 5 controls are compute/bake stages); "+/–" → immediate asset edit, no recompute; Extract → synchronous, cancellable, deterministic CPU Burst, never on debounce; Process bakes with the attached profile's persisted values (saved packed output == preview).
- **No new Stage & Shader Gates toggle** — the profile reference itself is the opt-in gate (null = stylization stage off), matching the `DecompositionEnabled` precedent. Profile sliders at 0 are identity ("0 = off" house semantics).

### Deferred Ideas (OUT OF SCOPE)
- Guided filter — rejected for this phase (no offline-bake benefit; 4–6 extra dispatches, no reusable intermediates)
- Kuwahara painterly variant — future selectable filter if artists demand a painterly look; would ride the same island-ID gate
- Bilateral + Kuwahara dropdown — doubles kernels/fixtures; revisit on user demand
- Live-uniform-only stylization — not representable in the packed format without a bake; rejected (preview would diverge from saved output)
- Global color transfer / arbitrary-size SVD (MathNet-class features) — explicitly out of scope per stack decisions
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| STYL-01 | Reusable `NamerStyleProfile` ScriptableObject holding 3–10 reference images + stylization parameters | Profile SO pattern (see Pattern 8): Editor-asmdef placement, serialized refs/weights/sliders/stats/fingerprint, custom inspector per UI-SPEC P1–P14; no runtime consumer, so `Editor/` placement is correct and needs no asmdef changes [VERIFIED: codebase asmdefs] |
| STYL-02 | Extract palette/style statistics (dominant colors, hue families, saturation, luminance distribution, shadow/midtone/highlight colors, warm/cool bias, contrast, palette density) from references | Deterministic CPU Burst extraction (Pattern 4): per-ref fixed-cap downsample → merged weighted histogram → fixed-init k-means; recommended stat formulas in Pattern 4b (definitions are discretion — formulas recommended, not locked) |
| STYL-03 | Palette mapping preserves source hue identity unless stronger replacement chosen | Hue-anchored GPU transfer kernel (Pattern 5): circular hue distance (red wraparound), achromatic/low-sat luma matching, strength-ramped hue-replacement factor reaching 1 at slider max, Replace mode via P13 |
| STYL-04 | GPU edge-preserving smoothing reduces photographic noise while preserving UV island boundaries, seams, decals, large transitions | Island-ID buffer (Pattern 2) + island-gated bilateral kernel (Pattern 3) extending `CSDilateResidual`; gutter-narrower-than-radius failure mode is why IDs, not a coverage mask (D-03) |
| STYL-05 | Runtime AO / Painted AO as separate controls; cavity strength + "avoid crushing cavities to black" | D-08: authored path routed through `CSAoRemap` direct mode + cavity-floor param in `NamerAoRemap` (Pattern 7); pool-ownership pitfall documented |
| STYL-06 | Normal detail reduction and roughness simplification before quantization | D-07: identity-defaulted uniforms in `CSOctahedralEncode` (flatten before octahedral encode) and `CSSurfacePack` (simplify after dip adoption, before `NamerPackAlphaBits`) (Pattern 6) |
| UI-03 (slice) | Style strength / smoothing / palette / normal detail / roughness simplification controls | Window W1–W3 + profile P7–P14 per UI-SPEC; profile persistence on the SO, window persistence on EditorPrefs |
| UI-04 (slice) | Stylized NAMER preview mode | W4 After View popup — debounced recompute with the stylize stage toggled (decomposition-toggle flip precedent, `NamerEditorWindow.cs:349` area) |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Reference-list UI, sliders, Advanced foldout | Editor UI (IMGUI, profile custom inspector) | — | All stylization controls are editor-time; `NamerStyleProfileEditor` owns P1–P14 |
| Profile attach/select, After View popup, foldout state | Editor UI (`NamerEditorWindow`) | EditorPrefs (window prefs) | W1–W4 mirror existing foldout/popup idioms; GUID string persistence because EditorPrefs cannot hold object refs |
| Palette/style-stat extraction | CPU Burst (Editor asmdef, explicit action) | — | Determinism mandate (D-04) excludes GPU; `NamerAOBaker`/`VertexColorFitter` template; synchronous with cancellable progress, off debounce |
| Palette transfer (stylize color) | GPU compute (`NAMERStylize.compute`) | ComputeBuffer upload of anchors/stats | Per-texel operator on the linear base RT; buffer upload follows the shipped `StructuredBuffer` pattern already proven on Metal (`NAMERDecomp.compute:87-90`) |
| Island-gated bilateral smoothing | GPU compute (ping-pong RT) | CPU union-find (island IDs) | Filter is atlas-space per-texel GPU work (NORM-03); island topology is mesh data — CPU computes triangle→island mapping, GPU rasterizes IDs |
| UV-island identification | CPU (Editor, union-find over split mesh) | GPU rasterize of IDs | Adjacency is a mesh-graph problem; rasterization reuses the `CSRasterizeVertexColors` dispatch shape |
| Roughness simplification | GPU compute (pack kernel) | — | Must land pre-quantization inside `CSSurfacePack` after dip adoption (D-07) |
| Normal-detail reduction | GPU compute (octahedral-encode kernel) | — | Must land before `NamerOctahedralEncode` in `CSOctahedralEncode` (D-07) |
| AO strength/contrast/cavity floor on authored AO | GPU compute (`CSAoRemap` direct mode) | — | Authored map is source data shaped through remap, never replaced (authored-wins clause 0 + D-08) |
| Residual dividend semantics (styled source) | Editor pipeline (no operator change) | Docs/tests | D-02 is a referent rename in `Process` wiring + 04.2 docs/test comments |
| Instant AO response (existing) | Runtime shader (`_OcclusionStrength`, `NamerSurface.hlsl:85`) | — | Unchanged this phase; the ONLY live-uniform channel (D-06) — all new controls are bake-side |
| Saved-output == preview | Editor pipeline (`NamerProcessor.Process` + window `RecomputePreview`) | Profile SO | Both paths must pass the same profile + settings; the phase boundary invariant |

## Standard Stack

### Core (all already in the project — zero new dependencies)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Unity Editor | 6000.0.82f1 (`ProjectVersion.txt`) | Host | Locked project version |
| Compute Shaders (HLSL) | Unity 6 built-in | New `NAMERStylize.compute` kernels; `NAMERPack.compute`/`NAMERAO.compute` uniform extensions | Mandated by project constraints; house kernels all `numthreads(8,8,1)` = 64 threads (within Metal's 256-threadgroup limit; no groupshared used — within 16 KB limit) [VERIFIED: codebase] |
| Unity.Burst + Unity.Mathematics | 1.8.x / 1.3.2 (already referenced by Editor asmdef) | Deterministic extraction job | `NamerAOBaker`/`VertexColorFitter` discipline; `kGoldenRatio`-style fixed tables, never `System.Random` [VERIFIED: codebase asmdef + Editor/NamerEditorConstants] |
| `NativeArray<T>` (engine) | Unity 6 | Job-safe buffers | Ships in `UnityEngine.CoreModule` — **no asmdef change needed**; the `Unity.Collections` *package* (NativeList/NativeHashMap) is NOT referenced by the Editor asmdef — avoid it or add the reference deliberately [VERIFIED: codebase asmdef] |
| Unity Test Framework | 1.4.x (bundled) | EditMode tests | House `Tests/Editor` asmdef already set up (`UNITY_INCLUDE_TESTS`, nunit precompiled) |

**Installation:** none. This phase installs no packages — do not add dependencies to `package.json`.

## Package Legitimacy Audit

This phase installs **zero external packages**. Everything needed (compute shaders, Burst, Mathematics, IMGUI built-ins, `EditorGUIUtility.ObjectContent`, `ProjectWindowUtil`) is already in the project or the Unity 6 editor. No slopcheck run required (no install surface).

## Architecture Patterns

### System Architecture Diagram

```
                       ┌─────────────────────────────────────────────┐
                       │ NamerStyleProfile (ScriptableObject asset)  │
                       │  refs[3..10] · weights · HueMode · sliders   │
                       │  ── Extract Style Stats (explicit, sync) ──► │
                       │  anchors + STYL-02 stats + fingerprint      │
                       └───────────────┬─────────────────────────────┘
                                       │ (attach/select → debounced recompute;
                                       │  edits → static change event → MarkDirty)
  source refs (Texture2D)              ▼
  ───────────────► CPU Burst extractor ── merged stats stored on profile
                   (deterministic, off-debounce)

  NamerComputePipeline.Process(inspection, projection, stylization)
  ┌────────────────────────────────────────────────────────────────────┐
  │ Upload (base/normal/AO/metallic)                                   │
  │   └─ D-08: authored AO → CSAoRemap direct mode (+ cavity floor)    │
  │ CSNormalize + CSOctahedralEncode                                   │
  │   └─ D-07: normal flatten uniform inside CSOctahedralEncode        │
  │            (before NamerOctahedralEncode)                          │
  │ ►►► STYLIZE STAGE (D-01 — NEW, profile != null only) ◄◄◄           │
  │   1. rasterize island IDs (CSRasterizeVertexColors shape,          │
  │      per-triangle island ids from CPU union-find over the split)   │
  │   2. island-gated bilateral × 2–3 (ping-pong RT, _pool.Lease)      │
  │   3. palette transfer kernel (ComputeBuffer anchors/stats;         │
  │      hue-anchored match, luma/sat pull, replacement ramp)          │
  │   → styledBase (pool lease) replaces baseColorOut downstream      │
  │ projection.Run(styledBase, projectedOut)  ← fit reads STYLED base  │
  │   └─ CPU fit + rasterize + CSProjectBase + CSResidual              │
  │      (dividend = styled source — D-02; operator unchanged)         │
  │ roughness transfer / Sobel dip                                     │
  │ CSSurfacePack                                                      │
  │   └─ D-07: roughness-simplify uniform after dip adoption,          │
  │      before NamerPackAlphaBits (6-bit quantization)                 │
  └────────────────────────────────────────────────────────────────────┘
        │                                    │
        ▼                                    ▼
  NormalizedBaseColor (styled)        saved packed assets == preview
```

### Recommended Project Structure
```
Packages/com.graffitientertainment.namer/
├── Editor/
│   ├── Stylize/
│   │   ├── NamerStyleProfile.cs          # ScriptableObject: refs, weights, sliders, stats, fingerprint
│   │   ├── NamerStyleExtractor.cs        # deterministic CPU Burst extraction (static, NamerAOBaker shape)
│   │   ├── NamerStylizePipeline.cs       # GPU harness: island rasterize + bilateral + palette transfer
│   │   └── NamerUvIslands.cs             # CPU union-find over split UV adjacency → per-triangle ids
│   ├── UI/
│   │   └── NamerStyleProfileEditor.cs    # custom inspector P1–P14 (assets/create menu lives here too)
│   ├── Pipeline/
│   │   └── NamerComputePipeline.cs       # D-01 insertion + D-08 authored remap (edited, not new)
│   └── Settings/NamerProcessorSettings.cs # +FoldoutStylization/AfterView/StyleProfileGuid (edited)
├── Compute/
│   └── NAMERStylize.compute             # CSRasterizeIslandIds + CSIslandBilateral + CSPaletteTransfer
├── Compute/NAMERPack.compute            # +_NormalDetailReduction (CSOctahedralEncode), +_RoughnessSimplify (CSSurfacePack)
├── Compute/NAMERAO.compute + NAMERAO.hlsl # +_CavityFloor in NamerAoRemap path
└── Tests/Editor/
    ├── NamerStyleProfileTests.cs        # list caps, serialization, fingerprint, extraction determinism
    ├── NamerStyleExtractionTests.cs     # stat oracles on synthetic refs (Bake_IsDeterministic bar)
    ├── NamerUvIslandsTests.cs           # union-find adjacency + narrow-gutter fixtures (CPU, no GPU)
    ├── NamerStylizeKernelTests.cs       # island-gate bleed, bilateral range gate, palette hue preservation
    └── NamerStylizationIntegrationTests.cs # Process insertion, byte-identity, AO/roughness/normal controls
```
Exact file layout is discretion (D-05 CONTEXT grants "kernel file layout for NAMERStylize compute"); the above is the recommended shape. The union-find helper deserves its own file — it is pure CPU mesh logic with standalone tests, mirroring how `MeshVertexSplitter`/`VertexColorFitter` are isolated under `Editor/Decompose/`.

### Pattern 1: D-01 Process insertion (the load-bearing wiring)
**What:** Stylize stage runs after `BindAndDispatchNormalizeEncode`, before `projection.Run`, producing `styledBase` that then flows everywhere `baseColorOut` flowed.
**When to use:** exactly here — not after the projection (an un-stylized residual would re-add original detail; mesh-domain VC editing was rejected).
**Why it is nearly free downstream:** `CreateProjectionContext`'s `Run = (sourceBase, projectedOut) => { ReadBackBase(sourceBase); VertexColorFitter.Fit(...); decomp.GenerateResidual(..., sourceBase, ...) }` consumes whatever RT `Process` passes (`NamerProcessor.cs:610-629`). Passing `styledBase` instead of `baseColorOut` makes the fit, the projection, AND the residual dividend styled with **zero changes to `CreateProjectionContext`** — D-02's "operator unchanged" holds by construction [VERIFIED: codebase, NamerProcessor.cs:610-629].
**Wiring sketch (house trailing-param precedent):**
```csharp
// NamerComputePipeline.Process — after BindAndDispatchNormalizeEncode (line ~177):
RenderTexture styledBase = baseColorOut;
if (stylization != null && stylization.Active)   // profile attached + any effect != identity
{
    styledBase = EnsureStylizePipeline().Run(baseColorOut, stylization, w, h); // pool-leased
}

if (projection != null)
{
    projectedOut = _pool.Lease(intermediate);
    projection.Run(styledBase, projectedOut);    // was baseColorOut — THE D-01/D-02 edit
}
// roughness transfer reads styledBase as the removed-luma minuend (line ~193)
// result: NormalizedBaseColor = projectedOut ?? styledBase; SourceBaseColor = styledBase when projected
// catch-block: Release(styledBase) when != baseColorOut (WR-03 mid-pipeline-throw pattern, lines 228-247)
```
**Threading the profile:** recommend a trailing optional parameter `Process(inspection, projection, stylization)` — the 04.2/04.3 trailing-param precedent (`NamerDecompPipeline.GenerateResidual` optional params, `CreateProjectionContext` trailing `coverageTarget`) — rather than new `NamerMaterialInspection` fields; the profile is one object, not per-material inspection state. Plumb from `NamerProcessor.Process` (bake) and `RecomputePreview` (preview) exactly like `settings.*` → `inspection.*` today (`NamerProcessor.cs:140-146`, `NamerEditorWindow.cs:391-397`).
**Byte-identity guard:** `stylization == null` (or all-identity values) must take the zero-dispatch path — `styledBase = baseColorOut` directly — so the profile-null regression class stays bit-identical to 04.3.

### Pattern 2: UV-island-ID buffer (union-find + rasterize)
**CPU side (`NamerUvIslands`):**
- Input: `NamerSplitResult` (`split.Uvs`, `FlattenTriangles(split.SubMeshTriangles)` — the same data `GenerateResidual` uploads, `NamerDecompPipeline.cs:218-261`).
- **Key adjacency on quantized UV pairs, NOT vertex indices.** `MeshVertexSplitter` welds by the full quantized (position, normal, tangent, uv) key [VERIFIED: codebase, STATE.md Phase 04 note] — two triangles sharing a UV-space edge across a 3D position seam (cylinder wrap, mirrored halves) get different vertex indices though their UVs are identical. Edge key = `(minUV, maxUV)` of the two endpoints' quantized UVs (reuse the splitter's quantization granularity concept); triangles sharing an edge key → `union(triA, triB)`.
- Union-find with path compression + union-by-size over triangle indices — a serial CPU pass over one dictionary; deterministic (fixed triangle order from the flattened array). Compact roots to consecutive ids `[0, N)`.
- Output: `int[] triIsland` uploaded as `StructuredBuffer<int>` (the `CreateBuffer` pattern, `NamerDecompPipeline.cs:755-760`).
**GPU side (`CSRasterizeIslandIds` in NAMERStylize.compute):** copy the `CSRasterizeVertexColors` dispatch shape verbatim — per-texel walk of all triangles, texel-space barycentric test with `NAMER_DECOMP_AREA_EPS`/`NAMER_DECOMP_INSIDE_EPS`, first-hit wins, write `float(_TriIsland[t])` and a coverage alpha (mirror `_VcInterp`'s `.a` coverage convention so uncovered texels are identifiable) [VERIFIED: codebase, NAMERDecomp.compute:111-160]. The texel-space area epsilon (denominator in texel units) is load-bearing — the [0,1] UV-space form silently discarded ~92% of triangles at 2048² (documented in-kernel).
**Precision bound (flag):** half-float `R16G16B16A16_SFloat` stores integers exactly only to **2048** [ASSUMED — IEEE 754 binary16]. Compact ids keep typical meshes (hundreds of islands) far below this; assert/warn in `NamerUvIslands` if `islandCount > 2048` and clamp, or lease an `R32G32B32A32_SFloat` RT for the pathological case (pool descriptors make this a one-line variant).
**Mesh priming (integration requirement):** the island buffer needs the split mesh even when decomposition is OFF (stylized preview of the one-texture/Phase-3 shape). Mirror the AO contract: `NamerProcessor.Process` resolves `decomposeSourceMesh` when `(DecompositionEnabled || AoStageEnabled)` today (`NamerProcessor.cs:107`) — add `|| StylizationNeedsMesh` (profile attached + smoothing > 0); same for the window's `primeBakeMesh` (`NamerEditorWindow.cs:409`). When no mesh resolves (bare material), smoothing falls back to coverage-gate-only (still correct, gutter risk documented) or is skipped with a warning — never a hard failure (the `NamerAOBaker` D-06 fallback discipline). The palette transfer needs NO mesh (per-texel) — it must run even with no mesh.

### Pattern 3: Island-gated bilateral kernel (extends CSDilateResidual)
**What:** one `numthreads(8,8,1)` kernel; per texel, loop `dy/dx` in `[-radius, radius]`, reject samples whose island ID ≠ center's (or coverage < 0.5), accumulate `w = exp(-dist²/2σs²) · exp(-Δluma²/2σr²)` [ASSUMED — textbook Tomasi-Manduchi bilateral; formulation is standard], write `Σw·c / Σw` to `_Dst` reading from `_Src` (never in place).
**The exact kernel shape already ships** — `CSDilateResidual` (`NAMERDecomp.compute:257-294`) is this loop minus the bilateral weights: static coverage gate (`_VcInterp[p].a >= 0.5`), bounds-checked `int2` neighbor loop, read-source/write-dest ping-pong, pooled leases. Extend the gate to `islandId[p] == islandId[center] && covered[p]` — this is precisely why a coverage mask fails (D-03): a gutter narrower than the radius lets the filter reach a *covered* texel of a *different* island.
**Ping-pong + pooling:** lease two `R16G16B16A16_SFloat` linear RTs via `_pool.Lease`, alternate `_Src`/`_Dst` per iteration (2–3 iterations), release both in `finally` — the `CSDespike`/`CSDilateResidual` hand-off pattern (`NamerDecompPipeline.cs:387-409`). Never write sRGB from compute (house law, D-11).
**Slider mapping (discretion — recommended):** smoothing `s ∈ [0,1]`: `iterations = 1 + round(2s)` (1–3), `radius = 2` texels fixed, `σs = radius * 0.5`, `σr = lerp(0.05, 0.40, s)` — a stronger slider tolerates larger luma deltas (flattens more) while hard decals/edges (Δluma >> σr) always survive. `s = 0` → skip dispatches entirely (identity).

### Pattern 4: Deterministic palette/stat extraction (the NamerAOBaker template)
**Discipline (all verified in shipped code):** static class, `IJobParallelFor` Burst jobs or deterministic serial accumulation, **fixed tables / fixed arithmetic init — never `System.Random`** (`NamerAOBaker.kGoldenRatio` direction table, `BuildDirectionTable`, `NamerAOBaker.cs:59-72`); `Allocator.Persistent` result owned by caller; cancellable via `shouldCancel` polled between slabs; synchronous explicit action off the debounce (`RequestBake` precedent, STATE.md 03.1: "EditorApplication.delayCall does not fire during headless EditMode tests" — keep the Extract path synchronous so it is headless-testable).
**Recommended pipeline (memory-bounded + order-deterministic):**
1. Per reference, ONE AT A TIME (never hold N×4K Color[] arrays — a 4K ref is ~67 MB of Color): read texels, box-downsample on CPU to a fixed cap (e.g., 128² — fixed grid, fixed rounding; the `kGrid = 16` fixed-sample precedent, `VertexColorFitter.cs:104`).
2. sRGB handling: if the ref is sRGB-imported (detect via `TextureImporter.sRGBTexture` through `AssetDatabase.GetAssetPath` — **never** `graphicsFormat`, which Unity 6 normalizes to linear variants, STATE.md Phase 02 WR-04), apply explicit sRGB→linear in fixed float math before binning (the palette must live in the pipeline's linear space).
3. Accumulate a merged weighted **histogram** (e.g., 16³ linear-RGB bins or HSL bins; per-ref weight multiplies counts — weights apply AT EXTRACT TIME).
4. Anchors: initialize k-means deterministically from the histogram — e.g., top-K bins by weighted count (ties broken by bin index ascending), then a fixed 8 iterations of Lloyd's over bin centers. No PRNG anywhere. Recommended K ≈ 8, including dedicated achromatic/low-sat slots (see Pattern 5).
5. Stats from the same histogram + anchors (Pattern 4b).
**Determinism test bar (replicate exactly):** run Extract twice on the same refs, assert **element-wise identical floats** — `NamerAOBakeTests.Bake_IsDeterministic` (`NamerAOBakeTests.cs:49-80`).
**Texel acquisition (open question OQ-1):** `GetPixels` requires `Texture.isReadable == true` and throws otherwise [CITED: docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.GetPixels.html]. The approved UI error copy presumes this path. Alternative: raw-copy blit + `AsyncGPUReadback` (no import dependency, house `ReadBackBase` pattern) makes the copy unreachable. Recommendation inside OQ-1.

**Pattern 4b: STYL-02 stat formulas (recommended definitions — discretion)**
| Stat | Recommended formula |
|------|---------------------|
| Dominant colors | k-means anchor colors (K ≈ 8) |
| Hue families | count of distinct 30° hue sectors containing ≥ 1 chromatic anchor |
| Saturation | weighted mean + per-anchor saturation (HSV-style S = (max−min)/max) |
| Luminance distribution | weighted luma histogram over shadow/mid/high bands (< 1/3, 1/3–2/3, > 2/3 of Rec.709 luma) |
| Shadow/mid/highlight colors | weighted mean linear color per band |
| Warm/cool bias | scalar ∈ [−1, 1]: (warm − cool)/(warm + cool) luma-weighted energy, warm = hue ∈ [330°, 90°], cool = [180°, 270°] |
| Contrast | luma p90/p10 (or weighted stddev) over the merged histogram |
| Palette density | occupied-bin fraction (or effective anchor count = exp of anchor-weight entropy) |

### Pattern 5: Hue-anchored palette transfer kernel (STYL-03)
**One blended GPU operator (D-04):** per texel of the linear base —
1. Compute source hue/sat/luma (HSV-style; hue in [0,1]).
2. **Anchor match by hue with red wraparound:** circular distance `min(|h−a|, 1−|h−a|)` [ASSUMED — textbook circular metric]; achromatic/low-sat texels (S < ~0.15, threshold a named constant) match **achromatic anchors by luma distance** instead — hue of a near-gray texel is noise, and this is what keeps "dark brown stays brown" from drifting gray-ward.
3. Pull luma/sat toward the matched anchor's stats: `outLuma = lerp(srcLuma, anchorLuma, s)`, `outSat = lerp(srcSat, anchorSat, s)` — hue untouched.
4. **Hue-replacement factor** `f`: Preserve mode → `f = s` (ramps to 1 only at slider max = "full palette snap"); Replace mode (P13) → `f = 1` at any strength. `outHue = lerp(srcHue, anchorHue, f)` (circular lerp).
5. Rebuild RGB from (hue, sat, luma), `saturate`, preserve base alpha (Pitfall-5 discipline — alpha flows to `AlphaDiscard`).
**Uniform/buffer shape:** one `ComputeBuffer` of anchor records (e.g., `float4` color + `float4` hue/sat/luma/flags) + count + `_StyleStrength` + `_HueReplaceMode`; upload per `Run` via the `CreateBuffer` pattern. `StructuredBuffer` on Metal is already proven in-shipped on this platform (`_Verts/_Uvs/_Tris/_Colors`, `NAMERDecomp.compute:87-90`, suite green on macOS Metal) — no shader-model workaround needed [VERIFIED: codebase].
**Achromatic anchors:** reserve 2–3 of the K slots for low-sat cluster centers so gray/black/white references produce matchable anchors; if the merged palette has no chromatic anchors near a texel's hue, keep the min-circular-distance winner (never fail).
**Order vs smoothing (discretion — recommended):** bilateral first, then palette transfer — denoise the photographic signal, then stylize the denoised base so palette assignment is crisp per texel; smoothing after would smear the stylized bands.

### Pattern 6: D-07 bake-side uniforms (identity-defaulted)
**Roughness simplify — inside `CSSurfacePack`** (`NAMERPack.compute:116-146`), after the `_HasExtractedRoughness` dip-adoption branch computes final `roughness`, before `NamerPackSurfaceFromOct` (which calls `NamerPackAlphaBits` → 6-bit quantization, `NamerEncode.hlsl:40-46`):
```hlsl
// D-07: roughness simplification, bake-side, AFTER dip adoption, BEFORE 6-bit quantization.
if (_RoughnessSimplify > 0.0)
{
    // small fixed-radius box mean over _PackInputs.y (green = roughness), then pull
    float mean = ...; // neighborhood loop over _PackInputs (readable Texture2D), CSDilateResidual shape
    roughness = lerp(roughness, mean, _RoughnessSimplify);
}
_SurfaceOut[id.xy] = NamerPackSurfaceFromOct(oct, ao, metallic, emissive, roughness);
```
Single insertion covers all three roughness paths (transfer full-adoption, Sobel slider dip, scalar/authored-map) because they all converge on the local `roughness` variable. In-kernel neighborhood read avoids extra dispatches/leases; `_RoughnessSimplify = 0` short-circuits to byte-identical.
**Normal flatten — inside `CSOctahedralEncode`** (`NAMERPack.compute:94-113`), after Z-rebuild, before `NamerOctahedralEncode(texel.xyz)`: decode signed XY, `n = normalize(lerp(n, float3(0,0,1), _NormalDetailReduction))`, re-encode to the [0,1] DirectX texel, then octahedral-encode as today. Lerping toward +Z keeps the texel non-negative (the encoder's domain). Do NOT touch `_Octahedral.b` (it carries AO). Neutral fill (0.5, 0.5, 1) is a fixed point — identity at 0.

### Pattern 7: D-08 authored-AO remap + cavity floor
**Cavity floor — `NamerAoRemap` (`NAMERAO.hlsl:28-33`):**
```hlsl
float NamerAoRemap(float ao, float strength, float contrast, float cavityFloor)
{
    ao = saturate(lerp(1.0, ao, strength));
    ao = saturate((ao - 0.5) * contrast + 0.5);
    return max(ao, cavityFloor);   // D-08: contrast > 1 saturates cavities to 0 — floor them
}
```
`cavityFloor = 0` is exactly the old function (`max(ao, 0) == ao` post-saturate) — byte-identical default. Update the single call site (`CSAoRemap`, `NAMERAO.compute:179`) to pass `_CavityFloor`; floor applies on ALL remapped paths (extracted, baked, authored).
**Authored-path remap:** `NamerComputePipeline.Process` authored branch (`:143-150`) currently uploads raw. Route through `CSAoRemap` **direct mode** (`_AoDirect = 1` reads `.g` — the `_AoIn.g` contract) exactly like `ApplyStrengthContrastDirect` (`NamerAOPipeline.cs:394-411`), including its identity short-circuit (`strength ≈ 1 && contrast ≈ 1 && floor ≈ 0` → return input unchanged → byte-identical authored path when no profile).
**POOL-OWNERSHIP PITFALL (critical):** `ApplyStrengthContrastDirect` leases from and RELEASES TO the **AO pipeline's** pool — but the authored `aoIn` in `Process` is leased from the **compute** pool (`_pool.Lease(unorm8)`, `NamerComputePipeline.cs:148`). Releasing a compute-pool RT into the AO pool (or vice versa) corrupts both pools. Add an internal `NamerAOPipeline` API that dispatches `CSAoRemap` with **caller-owned** src/dst RTs (no pool involvement, no release) — the pipeline leases the dst from its own pool and releases both itself. Same discipline for any stylize-path dispatch that touches AO pipeline kernels.

### Pattern 8: `NamerStyleProfile` ScriptableObject + wiring
**Placement:** `Editor/` folder (Editor asmdef) — stylization is editor-time only ("Runtime texture conversion" is out of scope), so nothing in `Runtime/` consumes it and no runtime build ever references it [VERIFIED: codebase asmdef layout]. No asmdef changes needed (Editor already references Burst/Mathematics; `NativeArray` is engine-provided).
**Shape (recommendation):**
```csharp
// Editor/Stylize/NamerStyleProfile.cs
public sealed class NamerStyleProfile : ScriptableObject
{
    public List<Texture2D> References = new List<Texture2D>();   // starts EMPTY (P2 locked); 0..10 slots
    public List<float> ReferenceWeights = new List<float>();      // parallel; default 1 (P14)
    [Range(0,1)] public float StyleStrength = NamerEditorConstants.DefaultStyleStrength; // 0.5
    [Range(0,1)] public float Smoothing = ...;                    // P8, default 0
    [Range(0,0.5f)] public float CavityFloor = ...;               // P9
    [Range(0,1)] public float NormalDetailReduction = ...;        // P10
    [Range(0,1)] public float RoughnessSimplify = ...;            // P11
    public int HueMode = NamerEditorConstants.DefaultHueMode;     // P13 {Preserve, Replace}
    // Extracted (written only by the Extract action):
    public Vector4[] PaletteAnchors;   // or a serializable struct list
    public NamerStyleStats Stats;      // STYL-02 fields, "—"-renderable defaults
    public string ReferenceFingerprint; // merged identity of refs used at extract time
}
```
**Fingerprint:** concatenation of the assigned refs' asset GUIDs (+ optionally the weights) captured at Extract; the inspector's staleness Warning compares it to the current refs (P6). Scope question (weights in/out) → OQ-4.
**Profile→window notification (discretion — recommended):** the profile inspector and the Processor window are different windows; `SetDirty` alone does not notify. Add a `static event Action<NamerStyleProfile> Changed` on the profile class, raised by `NamerStyleProfileEditor` after any P3/P7–P14 edit; the window subscribes on enable → `MarkTweaking()` + `MarkDirty()` → the existing `Tick` debounce (`NamerEditorWindow.cs:345-369`) drives the 0.3 s recompute. This is the "debounce wiring details" discretion granted by CONTEXT.
**Window persistence:** `StyleProfileGuid` string in EditorPrefs (UI-SPEC C); resolve via `AssetDatabase.GUIDToAssetPath` + `LoadAssetAtPath<NamerStyleProfile>` on enable/selection-change; null when the asset was deleted (detach silently, W4 disables with hint).
**Creation menu:** `[MenuItem("Assets/Create/NAMER/Style Profile")]` → `ProjectWindowUtil` (house root "NAMER"; no existing menu root collision — the window menu is `Tools > NAMER > Processor`).
**Undo:** every inspector edit wraps `Undo.RecordObject(profile, "...")` + `EditorUtility.SetDirty` (P3 locked; "–" and re-Extract need no confirmation — both undoable/recomputable per UI-SPEC Copywriting).

### D-02 dividend-wording inventory (docs/tests to touch)
The referent rename (dividend = **styled** source) needs a note/comment at exactly these places (comment-only edits in code/tests; the 04.2-CONTEXT edit is the required doc note):
- `.planning/phases/04.2-.../04.2-CONTEXT.md:39` (decision text) and `:83` (pattern note) — add a short Phase-5 addendum: "Phase 5 D-02: with a style profile attached, 'SOURCE albedo' reads as the STYLED source; the operator is unchanged."
- `NamerComputePipeline.cs:215-221` (result-construction comment: SourceBaseColor semantics) and `NamerProcessor.cs:583-586` (CreateProjectionContext doc: "generates the residual against the SOURCE base").
- Test comments (assertions unchanged): `NamerProjectionAcceptanceTests.cs:163,206,211-213,244-245,275-276,294`; `ResidualPipelineTests.cs:733,783,1442`; `NamerProjectionTests.cs:49,54`.
[VERIFIED: codebase grep "dividend" — full list above]

### Anti-Patterns to Avoid
- **Stylize after the projection / editing VCs in mesh domain:** rejected by D-01 — edge-preserving smoothing is atlas-space; an un-stylized residual re-adds original detail.
- **Gating the bilateral on a coverage mask instead of island IDs:** fails when the gutter is narrower than the filter radius (D-03's reason for the island buffer).
- **Matching palette anchors by RGB distance:** breaks hue identity (dark brown ↔ dark green are RGB-close); match by circular hue distance, achromatic by luma (STYL-03).
- **Writing sRGB from compute / leasing sRGB RTs:** house law — compute into linear `R16G16B16A16_SFloat`/`R8G8B8A8_UNorm` only (D-11).
- **`System.Random` or `DateTime`-seeded anything in extraction:** breaks `Bake_IsDeterministic`; fixed tables/arithmetic init only.
- **Releasing a RT into the wrong pool** (compute pool vs AO pool vs roughness pool): see Pattern 7 pitfall.
- **Running extraction on the debounce:** D-04 — Extract is an explicit synchronous action; the debounce only re-Processes.
- **New live shader uniforms for Phase 5 controls:** UI-SPEC locks all new controls to the debounced bake path; `_OcclusionStrength` stays the only instant channel.
- **Magic numbers:** every threshold (hue distance epsilon, low-sat cutoff, histogram bins, K, iterations, radius, sigmas, caps) is a `constexpr`/`kCamelCase`/`NamerEditorConstants` member (user global CLAUDE.md law; house style throughout).

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Editor UI | Custom control toolkit | `EditorGUILayout`/`EditorGUI` built-ins per UI-SPEC | Locked by the approved UI contract; house is text-only built-ins |
| RT lifetime | Per-call `new RenderTexture` | `ComputeTexturePool` `_pool.Lease`/`Release` | Existing pooling + leak-watchdog tests (`LiveRenderTargetCount`) |
| Mesh split for island IDs | New splitter | `MeshVertexSplitter.Split(mesh)` → `NamerSplitResult` | Same seam-safe quantized split the fit uses; island edges must match the fit's topology |
| Deterministic job scaffolding | Ad-hoc threading | Burst `IJobParallelFor` / serial fixed-math accumulation (`NamerAOBaker` shape) | Determinism bar + headless testability |
| Reduce/readback plumbing | New readback paths | `NamerComputePipeline.RequestReadback` + `forcePlayerLoopUpdate` + `WaitForCompletion` (edit-mode pumping, `NamerDecompPipeline.ReadBackStats`) | Async readback stalls in edit mode without the pump (IN-02 lesson) |
| sRGB detection | `graphicsFormat` checks | `TextureImporter.sRGBTexture` via asset path | Unity 6 normalizes Texture2D.graphicsFormat to linear variants — WR-04 verified false-negatives |
| Progress bar | New async progress framework | `EditorUtility.DisplayCancelableProgressBar` + `shouldCancel` polling (bake precedent) | Matches P5's synchronous-cancellable contract |

**Key insight:** every infrastructure need this phase has (pooling, dispatch, readback, buffers, determinism, cancellable progress, foldouts, debounce) already ships in this package — the phase's job is composition, not invention.

## Common Pitfalls

### Pitfall 1: Cross-pool render-target release
**What goes wrong:** passing a compute-pool-leased RT to an AO-pipeline helper that releases it into the AO pool (or leasing the remap dst from the wrong pool) — pools desync, the leak watchdog trips or stale leases resurface.
**Why:** `ApplyStrengthContrastDirect` looks reusable for D-08 but owns pool semantics internally (`NamerAOPipeline.cs:394-411`).
**How to avoid:** caller-owned src/dst API for cross-pipeline dispatches (Pattern 7); each pipeline leases/releases only its own targets.
**Warning signs:** `LiveRenderTargetCount` drift in tests; "stale texels" in remapped output.

### Pitfall 2: Island adjacency broken by position-split vertices
**What goes wrong:** union-find keyed on vertex indices splits one UV island into pieces at 3D seams (splitter splits by the full attribute key) — the bilateral then bleeds exactly where it shouldn't.
**How to avoid:** key edges on quantized UV pairs (Pattern 2).
**Warning signs:** island count implausibly high; bleed across a mirrored/cylinder seam in the narrow-gutter test.

### Pitfall 3: Half-float island IDs above 2048
**What goes wrong:** `R16G16B16A16_SFloat` cannot represent integers > 2048 exactly — distinct islands alias to the same ID and the gate opens between them.
**How to avoid:** compact consecutive ids + assert `islandCount <= 2048` (or an R32 float RT variant).
**Warning signs:** none at runtime — this is a silent correctness cliff; the assert is the only detector.

### Pitfall 4: Extraction nondeterminism via accumulation order or float paths
**What goes wrong:** per-ref processing order varying, dictionary iteration order leaking into anchor selection, or `System.Random` k-means init — two Extract runs differ by 1 ulp or worse.
**How to avoid:** fixed ref order (list order), histogram-binned accumulation with index-ordered tiebreaks, arithmetic init (Pattern 4); the `Bake_IsDeterministic` element-wise test is the gate.
**Warning signs:** flaky determinism test across runs.

### Pitfall 5: GPU-test fixture flattened by the AO un-multiply
**What goes wrong:** smooth luminance-ramp fixtures produce zero signal — `CSNormalize` divides the base by the low-pass AO and flattens any gradient the blur can track (verified memory: ramp strip luma min=mean=max=0.4941).
**How to avoid:** stylize-kernel tests should disable the AO stage (or supply an authored white occlusion) to isolate the stage; where Sobel-like signal is needed use deterministic hash-noise grain (`Mathf.Sin(x*12.9898f + y*78.233f)*43758.5453f`, frac via `f - Mathf.Floor(f)`) and assert strip MEDIANS, never single texels.
**Warning signs:** all-zero deltas in a "colorful" fixture.

### Pitfall 6: EditorPrefs leakage in pipeline tests
**What goes wrong:** tests constructing `NamerProcessorSettings` read the LIVE editor's prefs at property-get time — an editor left with `WriteResidual=ON` flipped a prior phase's test path (verified incident).
**How to avoid:** pin every outcome-relevant bool/float in the initializer; the `PrefsSnapshot` restore pattern does NOT help (restores after the run).
**Warning signs:** tests failing only on the user's live editor.

### Pitfall 7: Byte-identity regression on the no-profile path
**What goes wrong:** new uniforms dispatched unconditionally (or defaulting non-identity) shift Phase-04.3 output; 158 existing tests go red in bulk.
**How to avoid:** every new control defaults to identity (0 strength / floor 0 / mode Preserve-with-f(s)=s), every new dispatch is gated (`profile != null && effect != identity`), identity short-circuits mirror `ApplyStrengthContrastDirect`'s `Mathf.Approximately` guards.
**Warning signs:** byte-comparison tests (`NamerProjectionAcceptanceTests` identity class) failing without a profile attached.

### Pitfall 8: Non-readable reference textures crash Extract
**What goes wrong:** `GetPixels` throws on `isReadable == false` [CITED: Unity docs] — a raw exception, not the approved HelpBox copy.
**How to avoid:** pre-check `texture.isReadable` per ref and surface the P5 error copy naming the first offending texture (or adopt the GPU-readback acquisition — OQ-1).
**Warning signs:** Extract button throwing instead of showing the HelpBox.

### Pitfall 9: Live-editor test-run constraints (this project specifically)
**What goes wrong:** batch-mode test runs exit 134 (interactive editor holds the project lock); `AssetDatabase.Refresh` before a run wipes dynamic assemblies; console-log reads are unreliable after domain reloads.
**How to avoid:** the verified recipe — `unity-mcp RunCommand` + `TestRunnerApi` with results written to `Temp/<slug>-results.txt` (never console), top-level `ICallbacks` classes, `using UnityEditor.TestTools.TestRunner.Api;`, no Refresh first; headless-with-graphics fallback (never `-nographics`) once the editor closes; snapshot `ProjectSettings.asset` around batch runs. Full recipe in project memory `live-editor-editmode-test-run`.
**Warning signs:** invisible test finishes, duplicate FINISHED lines, "User interactions are not supported" from the relay.

### Pitfall 10: Vision-model judged output
**What goes wrong:** asking a vision model whether a render "looks stylized" — explicitly banned by project memory (no secondhand image analysis).
**How to avoid:** all stylization tests are numeric: GPU readback of RTs, per-texel/channel assertions (hue preserved = circular hue delta below epsilon; bleed test = neighbor-island texel unchanged; island interior variance reduced).
**Warning signs:** any test plan with screenshots.

### Pitfall 11: Overlay/overlap nondeterminism in island rasterization
**What goes wrong:** UV-overlapping triangles make the "covering triangle" ambiguous — but the rasterize walks triangles in fixed order and first-hit wins (same as `CSRasterizeVertexColors`), so the island ID is deterministic; just don't "improve" it with parallel writes.
**How to avoid:** keep the first-hit-return loop shape verbatim.

## Code Examples

### Island-gated neighborhood loop (the exact extension shape)
```hlsl
// Source: Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute:257-294 (CSDilateResidual)
// The bilateral extends this: same bounds-checked loop, same read-src/write-dst,
// gate becomes (sameIsland && covered) and the accumulator gains bilateral weights.
for (int dy = -radius; dy <= radius; dy++)
{
    for (int dx = -radius; dx <= radius; dx++)
    {
        int2 p = int2(id.xy) + int2(dx, dy);
        if (p.x < 0 || p.y < 0 || p.x >= sizeX || p.y >= sizeY) { continue; }
        if (_VcInterp[p].a >= 0.5)                       // <- becomes: islandId[p] == centerId && covered[p]
        {
            sum += _ResidualSrc[p].rgb;                  // <- becomes: w * src[p].rgb; wsum += w
            count += 1.0;
        }
    }
}
```

### Determinism test bar to replicate
```csharp
// Source: Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs:49-80
NamerStyleExtractResult first = NamerStyleExtractor.Extract(profile, shouldCancel: null);
NamerStyleExtractResult second = NamerStyleExtractor.Extract(profile, shouldCancel: null);
Assert.AreEqual(first.Anchors.Length, second.Anchors.Length);
for (int i = 0; i < first.Anchors.Length; i++)
{
    Assert.AreEqual(first.Anchors[i].hue, second.Anchors[i].hue,
        "palette anchors must be element-wise identical across two extracts (fixed tables + fixed float math)");
    // ... every stat field element-wise
}
```

### D-07 roughness-simplify insertion point (verbatim neighborhood)
```hlsl
// Source: Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute:127-145
if (_HasExtractedRoughness > 0.5) { /* dip adoption — transfer as-is OR Sobel slider dip */ }
// >>> D-07 INSERT HERE: roughness = lerp(roughness, neighborhoodMean(_PackInputs.y), _RoughnessSimplify);
float emissive = _PackInputs[id.xy].z;
_SurfaceOut[id.xy] = NamerPackSurfaceFromOct(oct, ao, metallic, emissive, roughness); // -> NamerPackAlphaBits (6-bit)
```

### Authored-AO remap caller-owned dispatch (D-08)
```csharp
// Shape only — modeled on NamerAOPipeline.ApplyStrengthContrastDirect (NamerAOPipeline.cs:394-411)
// but with CALLER-OWNED targets (no pool involvement — the pitfall-1 guard):
internal void RemapAuthored(RenderTexture src, RenderTexture dst, float strength, float contrast, float cavityFloor, int w, int h)
{
    _compute.SetInts("_Size", new[] { w, h });
    _compute.SetFloat("_AoDirect", 1f);          // read .g (the _AoIn.g contract)
    _compute.SetFloat("_AoStrength", strength);
    _compute.SetFloat("_AoContrast", contrast);
    _compute.SetFloat("_CavityFloor", cavityFloor);
    _compute.SetTexture(_kernelAoRemap, "_Dst", src);
    _compute.SetTexture(_kernelAoRemap, "_AoOut", dst);
    Dispatch(_kernelAoRemap, w, h);              // caller leases/releases both from the compute pool
}
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Kuwahara/guided alternatives for stylization smoothing | Island-gated bilateral (this phase); Kuwahara deferred; guided rejected | Phase-5 discuss (2026-09-23) | Bilateral is the minimal-kernel choice; the island-ID gate is reusable by a future Kuwahara variant |
| Coverage-mask-only spatial gating (dilate) | Island-ID gating for filters wider than the gutter | D-03 | The island buffer is the phase's new shared infrastructure |
| Authored AO = raw passthrough | Authored AO shaped through the remap + cavity floor | D-08 | STYL-05's painted-AO control becomes real |
| Blender NAMER as stylization reference | Void — the Blender reference is AI-written WIP with wrong math (project memory) | 04.x | No external oracle exists; numeric self-consistency tests only |

**Deprecated/outdated in-project knowledge:** none blocking; note that `NamerComputePipeline` doc comments referencing the 04.1 evaluate-callback flow are historical (04.2 replaced it with `NamerProjectionContext`).

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | IEEE 754 binary16 stores integers exactly up to 2048 (island-ID RT bound) | Pattern 2 / Pitfall 3 | IDs alias above 2048 islands — mitigated by the assert + R32 variant |
| A2 | Bilateral formulation (Gaussian spatial × Gaussian range on luma) is the right denoise-and-preserve operator here | Pattern 3 | Wrong σ mapping just tunes badly; slider still bounded [0,1] — low risk |
| A3 | STYL-02 stat formulas (Pattern 4b table) — names locked by requirement, formulas are my recommendation | Pattern 4b | Stats render differently than a user imagines; formulas are UI-displayed discretion, adjustable |
| A4 | Per-ref weights apply AT EXTRACT TIME (changing weights requires re-Extract) | Pattern 4 / OQ-4 | If weights should apply at Process time, the anchor upload must re-weight per run — cheap either way, but staleness UX differs |
| A5 | Replace mode (P13) = hue-replacement factor 1 at any strength; Preserve = factor ramps with the slider to 1 at max | Pattern 5 | Mis-reads "stronger replacement" — both readings converge at slider max, so blast radius is mid-slider behavior |
| A6 | "After View: NAMER vs Stylized NAMER" = same preview recomputed with the stylize stage off/on | phase_requirements UI-04 | If it instead means generated-vs-live, W4 wiring changes — recommend confirming in plan review |
| A7 | HSV-style hue/sat/luma decomposition (not OKLab/HLB) is sufficient for hue identity + anchor stats | Patterns 4/5 | OKLab would give perceptually better luma pull but adds conversion complexity; HSV is the CLAUDE.md "smallest change" reading |
| A8 | W4 default 0 (NAMER) means the pre-stylization view stays the default after attaching a profile | UI constraints | Cosmetic default flip only |

## Open Questions (RESOLVED)

> All four resolved during planning (2026-09-23) — rulings recorded in the plan `<decisions>` blocks (05-01-PLAN T2, 05-03-PLAN). Do not re-litigate.

1. **Reference-texel acquisition: `GetPixels` vs GPU readback (OQ-1)**
   - What we know: `GetPixels` requires `Texture.isReadable == true` and throws otherwise [CITED: Unity docs]; the approved error copy ("Check the texture's Read/Write import setting") presumes this path. The house `Upload`+`AsyncGPUReadback` path reads any importable texture with the sRGB contract already solved.
   - What's unclear: whether the UI contract intended to REQUIRE Read/Write on refs (a real user constraint) or simply assumed the simplest read.
   - Recommendation: implement the GetPixels path WITH the pre-check + approved error copy (it is the approved contract, and extraction is explicit/rare); keep the GPU-readback alternative in the back pocket as a UX follow-up. If the planner flips to GPU readback, the P5 error copy becomes dead text — a UI-spec amendment would be needed.
   - **RESOLVED:** GetPixels + `isReadable` pre-check + approved error copy — adopted in 05-01-PLAN `<decisions>`.
2. **Profile-edit → window recompute notification mechanism**
   - What we know: UI-SPEC locks the behavior (debounced recompute on profile edits) but not the mechanism; `SetDirty` alone does not notify other windows.
   - Recommendation: static `Changed` event on `NamerStyleProfile` (Pattern 8) — editor-only, no persistence concerns. Alternative: window polls the profile's `EditSequence` int each `Tick` (zero events, one extra field).
   - **RESOLVED:** static `Changed` event on `NamerStyleProfile` — adopted in 05-01-PLAN `<decisions>`.
3. **Smoothing when no mesh resolves (bare-material selection with a profile attached)**
   - What we know: island IDs need the split mesh; palette transfer doesn't; bare materials have no mesh.
   - Recommendation: palette transfer runs; smoothing skips with an `MessageType.Info` HelpBox ("Smoothing unavailable — no mesh to derive UV islands") — never a hard failure (D-06 baker fallback discipline). Needs a plan-level decision because it shapes the guard code.
   - **RESOLVED:** palette transfer runs, smoothing skips with `MessageType.Info` HelpBox — adopted in 05-03-PLAN `<decisions>`.
4. **Fingerprint scope: refs only, or refs + weights?**
   - What we know: staleness copy says "Reference images changed since extraction"; weights change the merged aggregate only if applied at extract time (A4).
   - Recommendation: fingerprint = GUIDs + weights (any weight edit marks stale) — honest, zero extra machinery.
   - **RESOLVED:** fingerprint = reference GUIDs + weights — adopted in 05-01-PLAN `<decisions>`.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Unity Editor | Everything | ✓ | 6000.0.82f1 (`/Applications/Unity/Hub/Editor/6000.0.82f1`) | 2022.3.62f2 also installed (NOT the project version — do not use) |
| Metal compute + AsyncGPUReadback | All new kernels, GPU tests | ✓ (suite green on this machine, macOS 24.6) | — | — |
| Interactive editor session | Live EditMode test runs | ✓ (editor/AssetImportWorker processes active — project lock held) | — | Headless-with-graphics batch fallback (recipe in memory `live-editor-editmode-test-run`); NEVER `-nographics` |
| unity-mcp relay | Live TestRunnerApi runs | Likely (used 2026-09-19..21) | — | If relay dead after domain reload: close editor, run headless batch with graphics |
| Burst / Mathematics | Extraction job | ✓ (already in Editor asmdef) | 1.8.x / 1.3.2 | — |
| Unity.Collections package | NOT required | NativeArray ships in the engine | — | If NativeList/NativeHashMap wanted, add the asmdef reference first (currently absent) |

**Missing dependencies with no fallback:** none.
**Missing dependencies with fallback:** none (all present; only runtime-lock considerations for test execution).

## Security Domain

Editor-only asset processing; no network, auth, sessions, or cryptography anywhere in this phase.

| ASVS Category | Applies | Standard Control |
|---------------|---------|------------------|
| V2 Authentication | no | n/a (editor tool) |
| V3 Session Management | no | n/a |
| V4 Access Control | no (editor-scoped) | Source-asset immutability enforced by `SourceImmutabilityTests` (GEN-02) — stylization must not weaken this: profile refs are READ, never written |
| V5 Input Validation | yes | Pre-validate profile refs: null slots, `isReadable` pre-check (Pitfall 8), 3–10 count clamp, weights clamped [0,1]; all GPU paths bounds-guarded (`id.x >= _Size.x` returns) |
| V6 Cryptography | no | n/a — the "fingerprint" is an identity string (GUID concat), NOT a security hash; do not use it for integrity decisions |

| Pattern | STRIDE | Standard Mitigation |
|---------|--------|---------------------|
| Malicious/corrupt reference textures | Tampering (DoS) | `isReadable` + dimension caps + try/catch per ref → HelpBox naming the texture; extraction never modifies any asset |
| Unbounded memory (10 × 4K refs) | DoS | One-ref-at-a-time acquisition + fixed downsample cap (Pattern 4) |
| Generated-asset overwrite | Tampering | Existing `NamerGenerated` label + overwrite-generated guard (GEN-03) — unchanged |

## Sources

### Primary (HIGH confidence)
- Codebase (read this session): `NamerComputePipeline.cs` (full), `NAMERDecomp.compute` (full), `NAMERPack.compute` (full), `NAMERAO.compute:140-220`, `NAMERAO.hlsl` (full), `NamerEncode.hlsl` (full), `NamerSurface.hlsl` (full), `NamerAOPipeline.cs:100-170,375-445`, `NamerDecompPipeline.cs` (full), `NamerProcessor.cs:42-281,560-700`, `NamerEditorWindow.cs:100-150,300-430,845-1019,1465-1620`, `NamerProcessorSettings.cs` (full), `NamerAOBaker.cs:1-140`, `NamerAOBakeTests.cs:1-120`, `MeshVertexSplitter.cs` (result shape), `VertexColorFitter.cs:1-150,389-512`, asmdefs, `package.json`, `ProjectVersion.txt`
- `.planning/phases/05-stylization/05-CONTEXT.md` (D-01..D-08) and `05-UI-SPEC.md` (binding UI contract, approved 2026-09-23)
- `.planning/REQUIREMENTS.md` §STYL-01..06, §UI-03/04; `.planning/STATE.md` (phase decisions, pitfalls, incidents)
- Project memories: `live-editor-editmode-test-run`, `namer-test-fixtures-ao-unmultiply`, `no-secondhand-image-analysis`, `texture-forensics-no-pil`, `blender-namer-reference-void`
- docs.unity3d.com/6000.0/Documentation/ScriptReference/Texture2D.GetPixels.html — isReadable requirement (fetched this session)
- docs.unity3d.com/6000.0/Documentation/ScriptReference/EditorGUIUtility.ObjectContent.html — GUIContent name+icon for thumbnails (fetched this session)

### Secondary (MEDIUM confidence)
- STATE.md-verified engineering notes (WR-04 sRGB detection, IN-02 readback pumping, PR#5 reduce weighting) — point-in-time but cross-checked against current code this session

### Tertiary (LOW confidence)
- None used for recommendations; textbook math (bilateral weights, circular hue distance, HSV, binary16 range) tagged [ASSUMED] inline where it appears

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — zero new dependencies; everything verified in-tree
- Architecture: HIGH — every decision maps to a read code anchor with line numbers; D-01 insertion proven compatible with `CreateProjectionContext` by reading the actual callback
- Pitfalls: HIGH — most pitfalls are documented incidents in STATE.md/project memory, verified against current code
- Discretionary formulas (stat definitions, slider mappings, σ values): MEDIUM — recommendations, not verified behavior; planner should treat them as tunable defaults

**Research date:** 2026-09-23
**Valid until:** 2026-10-23 (stable codebase; re-verify only if Phase 4.x follow-ups land first)

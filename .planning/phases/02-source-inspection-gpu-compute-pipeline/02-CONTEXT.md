# Phase 2: Source Inspection + GPU Compute Pipeline - Context

**Gathered:** 2026-08-26
**Status:** Ready for planning
**Mode:** auto (recommended defaults selected; review before execution if desired)

<domain>
## Phase Boundary

Read source materials from user selections (GameObject, prefab, FBX/model, material, or folder — URP Lit first, Unity Standard where available) and produce the normalized base color texture and the packed NAMER surface texture entirely in GPU compute, with kernels verified against the CPU Core reference. Deliverables: `SourceInspector` (map/scalar discovery with defaults), a compute dispatch harness + `ComputeTexturePool` + normalization/octahedral/packing kernels, and GPU-vs-CPU golden tests plus a cross-platform compute smoke test. Asset writing, editor window, preview, vertex-color decomposition, and stylization are OUT — this phase produces textures in-memory and proves GPU equivalence.

</domain>

<decisions>
## Implementation Decisions

### Source Selection & Entry Point (INSP-01)
- **D-01:** `SourceInspector` ships as a C# API in Phase 2, not UI. It accepts whatever the user selects (GameObject, prefab, FBX/model, material, or folder) and returns an inspection result. A minimal `Tools > NAMER > Inspect Selection` menu item logs the inspection report for manual validation — enough to prove INSP-01 without building Phase 3's editor window.
- **D-02:** Folder sources recurse subfolders for supported renderers/materials. Materials are deduplicated by instance ID so shared materials are inspected/processed once. Unsupported entries produce a warning entry in the result, never a failure.
- **D-03:** The inspection/processing unit is the unique material (renderer → its materials → dedupe). Multi-material meshes yield one inspection unit per unique material.

### Map Discovery & Scalar Fallbacks (INSP-02, INSP-03, INSP-04)
- **D-04:** Map discovery uses per-shader property-name convention tables read via `Material.GetTexture` — URP Lit first (`_BaseMap`, `_BumpMap`, `_MetallicGlossMap`, `_EmissionMap`, …), Unity Standard second (`_MainTex`, `_BumpMap`, …). No filename/suffix heuristics in v1 (deferred idea).
- **D-05:** URP smoothness channel handling is a correctness requirement: read `_SmoothnessSource` and honor both source paths (metallic-map alpha vs base-map alpha), converting roughness = 1 − smoothness before packing.
- **D-06:** Unknown/custom (e.g. Shader Graph) shaders get a best-effort scan of known property names; when no maps are found, scalar properties apply, then neutral defaults — with a warning in the inspection result. The pipeline never fails on missing data (INSP-04).
- **D-07:** Neutral defaults when nothing is present: metallic 0, roughness 0.5, AO 1.0, emission black, normal (0, 0, 1).

### Base Color Cleaning (NORM-01)
- **D-08:** Phase 2 cleaning is conservative: color-space normalization (sRGB→linear where the source is authored sRGB) plus AO un-multiply (`albedo ÷ max(AO, ε)`) behind a strength parameter exposed by the pipeline (UI control lands in Phase 3). Full iterative/gradient-based delighting is rejected for this phase (research-heavy, albedo-damage risk) — recorded as a deferred idea.
- **D-09:** Researcher instruction: check the Blender reference implementation (`GraffitiEntertainment/BlenderNamerPlugin`, develop) texture pipeline for any existing base-color cleaning behavior and mirror it where present, in the same spirit as the Phase 1 format mirroring.

### GPU Pipeline (NORM-02, NORM-03)
- **D-10:** Staged kernels (normalize → octahedral encode → surface pack) rather than one fused kernel, hosted in the existing `Compute/NAMERPack.compute`, plus a shared HLSL include that mirrors the Core encode math so the compute path and the runtime decode shader cannot drift from Core.
- **D-11:** Intermediate compute targets are `R16G16B16A16_SFloat`; the final packed surface output is `R8G8B8A8_UNorm` linear. Compute never writes sRGB targets (locked by project tech stack).
- **D-12:** `ComputeTexturePool` pools temporary render textures (sized to the working resolution) for reuse across dispatches instead of allocating per stage — roadmap-named component; internal design is Claude's discretion.
- **D-13:** GPU→CPU readback uses `AsyncGPUReadback` throughout the pipeline and tests; synchronous reads only for tiny one-shot cases.

### Verification (TEST-04)
- **D-14:** GPU-vs-CPU comparison semantics: expected values come from the Core reference (`NamerFormat`). The packed alpha byte must match EXACTLY (integer packing is deterministic); octahedral R/G and AO within 1/255; decoded-normal agreement via dot ≥ 1 − 1e-3 (Phase 1 tolerance convention).
- **D-15:** Backend scope for this phase: verify locally on Metal (the dev machine). Tests capability-gate on unavailable backends (skip with an explicit report entry, not silent pass). A D3D/Vulkan CI matrix is v2. Researcher must verify the flagged compute limits (threadgroup ≤ 256 threads, groupshared ≤ 16 KB) against Unity 6 docs.
- **D-16:** Phase 1 carry-in housekeeping: rename `NamerFormat.PackSurface`'s `normal` parameter to reflect that it receives a raw DirectX normal-map texel (e.g. `normalTexel`) — do this while writing the kernels that call it.

### Claude's Discretion
- Exact class/file names within the package (SourceInspector API shape, kernel names, pool internals).
- Whether GPU golden fixtures are committed as assets/JSON — planner's choice with researcher input.
- Menu-item naming details beyond `Tools > NAMER/…`.
- Dispatch sizing and synchronization mechanics (as long as D-14 semantics hold).

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project definition
- `NAMER_UNITY_PLUGIN_PRD.md` — authoritative PRD: processing pipeline stages, source material support, asset-safety rules, performance constraints
- `.planning/PROJECT.md` — project context, constraints, processing-pipeline description, current state after Phase 1
- `.planning/REQUIREMENTS.md` — INSP-01..04, NORM-01..03, TEST-04 are this phase's requirements
- `.planning/STATE.md` §Blockers/Concerns — Phase 2 entry: Metal/DX11/Vulkan compute limits flagged MEDIUM, verify during planning

### Research (Phase 1 corpus — still authoritative)
- `.planning/research/STACK.md` — Unity 6 LTS/URP 17.x baseline, GraphicsFormat guidance, compute/AsyncGPUReadback usage
- `.planning/research/ARCHITECTURE.md` — Core/Runtime/Editor assembly split, one-way data flow, component boundaries the pipeline must respect
- `.planning/research/PITFALLS.md` — sRGB/linear contract (pitfall #1), compression corruption (#7), untestable-GPU (#9)

### Code to build on
- `Packages/com.graffitientertainment.namer/Core/NamerFormat.cs` — the CPU reference every kernel must match (TEST-04 oracle)
- `Packages/com.graffitientertainment.namer/Core/NamerConstants.cs` — thresholds/bits/levels constants kernels must reuse
- `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl` — established HLSL-mirrors-Core pattern; the new shared encode include follows it
- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` — existing kernel scaffold (intentionally empty; kernels land here)
- `Packages/com.graffitientertainment.namer/Tests/Editor/` — golden-vector and round-trip test patterns to extend for GPU comparison tests

### External reference (not in repo)
- `GraffitiEntertainment/BlenderNamerPlugin` (GitHub, develop branch) — reference implementation. Phase 2 focus: its texture-processing/cleaning pipeline (D-09), not just the encode math already mirrored in Phase 1.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- `Core/NamerFormat.cs` + `Core/NamerConstants.cs`: complete CPU encode/pack implementation — the verification oracle (D-14)
- `Shaders/NamerSurface.hlsl`: proven HLSL mirror of Core decode; establishes the shared-include pattern for the encode side (D-10)
- `Compute/NAMERPack.compute`: scaffolded asset awaiting Phase 2 kernels
- `Tests/Editor/*Tests.cs`: EditMode test harness conventions (asmdef-gated, headless-runnable) used for the 31/31 green suite
- `Editor/NamerSmokeSetup.cs`: programmatic URP/material/asset creation patterns for editor tooling

### Established Patterns
- Core purity: no `UnityEngine.Object` types in Core; engine types live in Runtime/Editor assemblies
- Constants centralized in `NamerConstants` — no magic numbers (project rule)
- CPU reference ↔ HLSL manual mirroring with golden-vector tests enforcing equivalence
- Headless test invocation without `-quit` (STATE.md accumulated decision)

### Integration Points
- Compute kernels write textures the Phase 3 AssetGenerator will eventually save — produce in-memory outputs with final formats now (`R8G8B8A8_UNorm` packed, per D-11)
- Inspection result shape feeds Phase 3's editor window; design it as serializable data, not log output

</code_context>

<specifics>
## Specific Ideas

- Roughness must be quantized LINEAR (floor(r·63)) through the GPU path exactly as Core does — any smoothness→roughness conversion happens before quantization.
- The inspection report should be human-readable in the debug menu output (found maps per material, fallbacks used, warnings) since it doubles as Phase 2's manual validation surface.
- AO un-multiply must clamp the divisor (`max(AO, ε)`) — mirrors Core's epsilon-guard style.

</specifics>

<deferred>
## Deferred Ideas

- Full delighting (iterative luminance-gradient baked-lighting removal) — potential later phase/v2 if conservative cleaning proves insufficient
- Filename/suffix-based map heuristics for unknown shaders — v2 automation territory
- Shader Graph material property auto-discovery — v2
- D3D/Vulkan/continuous-integration GPU test matrix — v2 (this phase: Metal + capability-gated skips)

</deferred>

---

*Phase: 2-Source Inspection + GPU Compute Pipeline*
*Context gathered: 2026-08-26*

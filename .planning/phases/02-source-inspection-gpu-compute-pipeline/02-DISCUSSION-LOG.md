# Phase 2: Source Inspection + GPU Compute Pipeline - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-08-26
**Phase:** 2-Source Inspection + GPU Compute Pipeline
**Mode:** `--auto` (recommended defaults auto-selected; no interactive prompts)
**Areas discussed:** Source selection & entry point, Map discovery & scalar fallbacks, Base color cleaning scope, GPU pipeline & verification

---

## Source Selection & Entry Point

| Option | Description | Selected |
|--------|-------------|----------|
| API + debug menu item | `SourceInspector` C# API over the current selection + `Tools > NAMER > Inspect Selection` logging the report; full UI in Phase 3 | ✓ |
| API only, no menu | Pure library surface, tests as sole validation | |
| Mini editor window now | Builds UI ahead of Phase 3 (scope creep) | |

**User's choice:** [auto] Recommended default: API + debug menu item
**Notes:** Folder sources recurse with material dedupe by instance ID; unsupported entries warn-and-skip (INSP-04 spirit). Inspection unit = unique material.

---

## Map Discovery & Scalar Fallbacks

| Option | Description | Selected |
|--------|-------------|----------|
| Per-shader property-name tables | URP Lit + Unity Standard known-slot lookup via `Material.GetTexture` | ✓ |
| Filename/suffix heuristics | `_normal`, `_ao` name matching for unknown shaders | |
| Shader property auto-scan | Enumerate shader properties generically | |

**User's choice:** [auto] Recommended default: per-shader property-name tables
**Notes:** `_SmoothnessSource` honored (metallic-map alpha vs base-map alpha), roughness = 1 − smoothness. Unknown shaders: best-effort scan → scalar fallback → neutral defaults (metallic 0, roughness 0.5, AO 1, emission black, normal +Z) with warning; never fatal.

---

## Base Color Cleaning Scope

| Option | Description | Selected |
|--------|-------------|----------|
| Conservative cleaning | sRGB→linear normalization + AO un-multiply behind a strength parameter | ✓ |
| Full iterative delighting | Luminance-gradient baked-lighting removal | |
| No cleaning | Color-space normalization only | |

**User's choice:** [auto] Recommended default: conservative cleaning
**Notes:** Researcher to check the Blender reference pipeline for existing cleaning behavior to mirror (D-09). Full delighting recorded as deferred idea.

---

## GPU Pipeline & Verification

| Option | Description | Selected |
|--------|-------------|----------|
| Staged kernels + shared HLSL include | normalize → oct-encode → pack in `NAMERPack.compute`; encode math mirrored from Core in one include | ✓ |
| Single fused kernel | One pass over all inputs | |
| Alpha byte exact; RG/AO ±1/255; normal dot ≥ 1−1e-3 | TEST-04 comparison semantics | ✓ |
| Float-tolerance-only comparison | Looser uniform epsilon everywhere | |
| Metal locally + capability-gated skips | Backend verification scope | ✓ |
| Full D3D/Vulkan matrix now | Requires hardware/CI beyond dev machine | |

**User's choice:** [auto] Recommended defaults: staged kernels + shared include; exact-alpha/±1/255/dot tolerances; Metal-first with gated skips
**Notes:** Intermediates `R16G16B16A16_SFloat`, packed output `R8G8B8A8_UNorm` linear, `AsyncGPUReadback` for readback. `ComputeTexturePool` pools temp RTs across dispatches. Researcher verifies threadgroup ≤ 256 / groupshared ≤ 16 KB limits. Phase 1 carry-in: `PackSurface` param rename while writing kernels.

---

## Claude's Discretion

- API/class/file naming within the package, kernel names, pool internals, dispatch/sync mechanics, menu-item naming details, fixture format (assets vs JSON)

## Deferred Ideas

- Full delighting (later phase/v2)
- Filename heuristics for unknown shaders (v2)
- Shader Graph property auto-discovery (v2)
- D3D/Vulkan CI GPU matrix (v2)

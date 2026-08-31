# NAMER Unity Plugin

## What This Is

A Unity-native editor plugin (UPM package, C# + HLSL) that converts ordinary Unity PBR materials into compact NAMER materials entirely inside Unity — generating, processing, compressing, previewing, and stylizing textures without round-tripping through Blender. It replaces the Blender-only NAMER workflow for artists and developers building Unity games who want reduced texture counts/memory and optional stylization driven by reference images.

## Core Value

A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

## Requirements

### Validated

- [x] Convert URP Lit (and Unity Standard where available) materials into NAMER materials inside Unity — Validated through Phase 3 (full inspect → GPU pipeline → generate flow)
- [x] Pack surface data into the NAMER surface texture: octahedral normal (RG), AO (B), metallic (A bit 7), emissive (A bit 6), 6-bit roughness (A bits 0–5) — Validated in Phase 1 (format contract + golden vectors) and Phase 2 (GPU pack pipeline vs Core oracle)
- [x] Provide a URP runtime NAMER shader that decodes the packed format (octahedral normals, AO, metallic/emissive flags, roughness) and supports vertex-color reconstruction — Validated in Phase 1 (SHDR-03 parity human-approved); vertex-color reconstruction input awaits Phase 4 meshes
- [x] Normalize source maps (albedo, normal, AO, metallic, roughness/smoothness, emission, alpha) with sensible defaults for missing maps and scalar fallbacks — Validated in Phase 2
- [x] Editor window at `Tools > NAMER > Processor` plus a `Process with NAMER` explicit command; all generated assets saved under a dedicated generated-assets directory, never overwriting sources — Validated in Phase 3 (AssetGenerator sole-writer + NamerGenerated-stamp overwrite gate + SHA-256 immutability test; path-confinement hardened after review CR-01)
- [x] GPU compute shaders for high-resolution texture operations; CPU C# for asset inspection, UI, mesh processing, serialization — Validated through Phase 3

### Active

- [ ] Produce a base/color-residual texture (cleaned base color initially; residual after vertex-color decomposition) — Phase 3 delivered the cleaned base; residual awaits Phase 4
- [ ] Vertex-color decomposition: barycentric least-squares fitting of low-frequency color into vertex colors (Color32 where sufficient), vertex splitting at seams/discontinuities, residual texture generation, reconstruction-error metrics and debug visualization
- [ ] Adaptive residual texture resolution reduction based on measured reconstruction error, with manual override
- [ ] Stylization: NAMERStyleProfile ScriptableObject with 3–10 reference images, palette extraction, hue-aware palette mapping that preserves source hue identity, edge-preserving smoothing (bilateral/Kuwahara/guided-class GPU compute), separate runtime AO / painted AO / cavity controls, normal detail reduction, roughness simplification
- [ ] Interactive before/after preview of the actual mesh with debug channel views (vertex color, residual, AO, normal, roughness, metallic, emissive, reconstruction error) — Phase 3 delivered the preview + 6 channels; vertex-color/residual/reconstruction-error views await Phase 4
- [ ] Automated tests covering encoding/decoding, bit packing, vertex color fitting, residual reconstruction, UV seams, asset paths, and source-asset immutability — encoding/packing/asset-paths/immutability green; vertex-color/residual/UV-seam tests await Phase 4
- [ ] Format compatibility with the Blender NAMER implementation (decode-equivalent packed textures) — C#-oracle equivalence green; literal Blender fixtures deferred to v2 (PIPE-02)

### Out of Scope

- Blender or Maya dependency — Unity becomes the primary processing environment
- AI object/semantic recognition (skin, clothing, trees), diffusion models, cloud processing — ordinary image processing only for v1; AI-assisted processing may be considered later
- C++ native plugins — first implementation is C#/HLSL only
- Runtime texture conversion — NAMER processing is editor-time
- Destructive editing of imported source assets — never allowed
- Hard-coded art styles — stylization is profile-driven and style-agnostic
- Automatic processing of every model on import (AssetPostprocessor automation is a later optional feature)

## Context

- **Current state (after Phase 3, 2026-08-27):** Phases 1–2 delivered the `com.graffitientertainment.namer` UPM scaffold, format contract (`Core/NamerFormat.cs` ⇄ `Shaders/NamerSurface.hlsl`), the URP runtime decode shader (SHDR-03 parity human-approved), `SourceInspector` (all 5 selection kinds → deduped `NamerMaterialInspection` units), and `NamerComputePipeline` (3 staged kernels, GPU-golden-verified vs the Core C# oracle). Phase 3 closed the loop to disk and UI: `AssetGenerator` is the sole disk writer (PNG base via sRGB `ConvertTexture`, PNG surface, EXR residual path ready; importer stamped linear/uncompressed/point/no-mips; `NamerGenerated`-label overwrite gate; prefix/suffix path-confinement validation hardened after review CR-01), `NamerProcessor` orchestrates inspect → pipeline → generate into `NAMERGenerated/{source}/`, and `NamerEditorWindow` (`Tools > NAMER > Processor`) provides the before/after `PreviewRenderUtility` preview (`Render(true)` — URP-verified non-magenta on Metal), a 7-mode debug-channel toolbar sharing `NAMER_DECODE_SURFACE`, AO-strength recompute debounced ~300 ms, orbit/zoom, and configurable output controls; `Assets/Process with NAMER` + `GameObject/Process with NAMER` context menus route through the same processor. SHA-256 source+.meta immutability and asset-path/overwrite tests green; code review 2 Critical + 6 Warning findings all fixed with regression coverage. EditMode 53/53 + PlayMode 3/3 headless at HEAD. Deferred into later phases: vertex-color decomposition + residual + their preview channels (4), stylization (5), literal Blender fixtures (v2 PIPE-02), GBuffer pass (unscheduled). Two live-editor UAT items pending (`03-HUMAN-UAT.md`, `/gsd:verify-work 3`).
- The existing `GraffitiEntertainment/BlenderNamerPlugin` (develop branch) is the reference implementation for the NAMER encoding and texture-processing concepts; Unity should preserve format compatibility where useful but use Unity-native APIs and GPU compute rather than porting the Blender implementation.
- NAMER runtime representation: two textures plus optional mesh vertex colors. Texture 1 RGB = base/residual color (alpha free). Texture 2 RGBA = packed surface data (octahedral normal X/Y, AO, metallic bit, emissive bit, 6-bit roughness → 64 roughness values). Emissive color stored as material metadata.
- Processing pipeline: select source → inspect meshes/materials → read/normalize PBR textures → remove baked lighting where feasible → normalize AO → encode octahedral normals → pack surface texture → optional vertex-color decomposition → optional stylization → generate textures/mesh/material/shader → preview → save under generated-assets directory.
- Vertex color model: `BaseColor ≈ VertexColorInterpolation × ResidualColor`; per-triangle multi-sample barycentric least-squares fitting (not simple averaging); residual = difference between fitted interpolation and source texture.
- HDRP Lit support is desirable only if it doesn't jeopardize the first milestone (URP first).
- Target package: `com.graffitientertainment.namer` with Runtime/, Editor/, Shaders/, Compute/, Tests/ layout.
- Performance targets: practical for 2K/4K texture sets, GPU-favored interactive preview, runtime shader cost near standard Unity PBR, no expensive decompression stage at runtime.

## Constraints

- **Tech stack**: C# for editor/runtime code, Unity compute shaders (HLSL) for GPU image processing — project requirement
- **Render pipeline**: URP first; Unity Standard where available; HDRP only if practical without harming the first milestone
- **Compatibility**: NAMER packed textures must decode equivalently to the Blender NAMER implementation
- **Asset safety**: Source assets must never be modified; generated output lives in a separate directory
- **Performance**: No repeated per-pixel C# loops on large textures; GPU compute for high-resolution work
- **Precision**: Roughness limited to 64 values (6 bits); vertex colors Color32 where sufficient
- **Package**: Must ship as a reusable Unity Package Manager package

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| C# + Unity compute shaders (no C++ native plugins) | Unity-native, portable, sufficient for v1 | Phase 2: three staged kernels (normalize/encode/pack) in `NAMERPack.compute` + `NamerEncode.hlsl` running via direct `Dispatch`; GPU golden tests match the Core C# oracle within D-14 tolerances; `ComputeTexturePool` governs temp RT lifecycle |
| URP-first shader; HDRP deferred | Largest target audience first, avoid milestone risk | Phase 1: URP 17.0.4 hand-written HLSL decode shader shipped; SHDR-03 parity vs URP Lit human-approved; GBuffer/deferred unsupported (tracked limitation) |
| Format compatibility with Blender NAMER encoding | Cross-tool workflow equivalency | Phase 1: contract locked in pure-C# Core mirroring `namer_core.py` math (barycentric octahedral, strict bit thresholds, linear 6-bit roughness); golden vectors green; literal Blender-executed fixtures deferred to v2 PIPE-02 |
| Explicit `Process with NAMER` command (no auto-import processing in v1) | Predictability; automation added later | Phase 3: `Assets/` + `GameObject/` context menus and the window button all route through `NamerProcessor.Process`; destination/prefix/suffix configurable, overwrite gated to `NamerGenerated`-stamped assets (path traversal rejected — review CR-01) |
| Stylization via reusable NAMERStyleProfile ScriptableObject | Profiles reusable across unrelated assets; no hard-coded styles | — Pending (Phase 5) |
| Authored-sRGB detection reads `TextureImporter.sRGBTexture`, not `Texture2D.graphicsFormat` | Unity 6000.0.82f1/Metal normalizes graphicsFormat to linear variants — sRGB imports report `R8G8B8A8_UNorm`, so graphicsFormat checks never return true | Phase 2 (code-review WR-04 root cause): `SourceInspector.IsSrgb` is importer-first via `AssetDatabase.GetAssetPath`; runtime-created textures fall back to graphicsFormat. Fixed real false-negatives that would have skipped sRGB decode on user assets |
| Geometry bake / high-res occluder stay preview-scope; generated assets always use extraction/authored AO | User decision at Phase 03.1 close-out (verification W1): the generated material is a separate asset the user can swap/adjust manually after generation | Phase 03.1: accepted divergence; bake-to-generation wiring not planned for v1 |

## Evolution

This document evolves at phase transitions and milestone boundaries.

**After each phase transition** (via `/gsd-transition`):
1. Requirements invalidated? → Move to Out of Scope with reason
2. Requirements validated? → Move to Validated with phase reference
3. New requirements emerged? → Add to Active
4. Decisions to log? → Add to Key Decisions
5. "What This Is" still accurate? → Update if drifted

**After each milestone** (via `/gsd:complete-milestone`):
1. Full review of all sections
2. Core Value check — still the right priority?
3. Audit Out of Scope — reasons still valid?
4. Update Context with current state

---
*Last updated: 2026-08-31 after Phase 03.1 completion*

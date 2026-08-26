# Walking Skeleton — NAMER Unity Plugin

**Phase:** 1
**Generated:** 2026-08-25

## Capability Proven End-to-End

> The smallest user-visible capability that exercises the full stack.

"Given a known NAMER packed surface sample (neutral normal, AO 1.0, metallic 0, emissive 0, roughness 0.5), the pure-C# `Core` assembly encodes it to the Blender-equivalent packed bytes, the URP runtime shader decodes those bytes back to the correct normal/AO/metallic/emissive/roughness, and a NAMER material renders without error — verified headless against the Blender golden vectors."

## Architectural Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Editor host / framework | Unity 6000.0 LTS (minimum `"unity": "6000.0"`), URP 17.x | Unity 6 LTS is the current line; URP-first per project decision. Editor at `/Applications/Unity/Hub/Editor/6000.0.82f1`. |
| Format source of truth | Pure-C# `Core` asmdef (`Unity.Mathematics` only, no `UnityEngine.Object`) | Single lockable contract for encode/decode/bit-packing, headless-testable, manually mirrored in HLSL (D-03). |
| Octahedral encoding | Blender `namer_core.py` barycentric projection of the RAW `[0,1]` DirectX normal-map texel (no corner-fold) | Byte-equivalence with `GraffitiEntertainment/BlenderNamerPlugin` is ENCD-05; R/G always in `[0.5, 1.0]`. |
| Alpha bit layout | A bit 7 = metallic (`>0.5`), bit 6 = emissive (`>0.1`), bits 0–5 = 6-bit roughness (linear `floor(roughness*63)`) | Fixed by PRD; strict-inequality thresholds are load-bearing (Pitfall 5). |
| Runtime decode | Hand-written URP ShaderLab + HLSLPROGRAM (`Shader "GraffitiEntertainment.Namer/NAMER"`), reusing `UniversalFragmentPBR` | Bit-unpacking + octahedral decode are infeasible in Shader Graph; `UniversalFragmentPBR` keeps SHDR-03 apples-to-apples with URP Lit. |
| Vertex-color path | `albedo = baseResidual.rgb * vertexColor.rgb` wired from day one (residual == base, vertex color defaults white) | Decode contract complete now so Phase 4 decomposition plugs in without shader changes (D-05). |
| "Data layer" (analog) | NAMER packed surface texture (linear, uncompressed `R8G8B8A8_UNorm`) + base/residual texture (sRGB) | The format is the "schema"; linear sampling on packed R/G/B is the sRGB/linear contract (ENCD-04). |
| Deployment target (analog) | Local Unity project at the repo root; `Packages/com.graffitientertainment.namer/` | Package-in-`Packages/` for development (D-10); no network/services. |
| Directory layout | `Core/`, `Runtime/`, `Editor/`, `Shaders/`, `Compute/`, `Tests/Editor/`, `Tests/Runtime/` — asmdef per C# folder | Compilation-domain split (D-10); `Shaders/`+`Compute/` hold HLSL assets (no asmdef). |
| Test strategy | UTF EditMode (headless, pure math) + PlayMode (render smoke); fixed tolerances (normal 1e-3 dot, golden float 1e-4, bits exact) | D-06/D-07; GPU-vs-CPU comparison deferred to Phase 2 (D-08). |

## Stack Touched in Phase 1

- [x] Project scaffold — Unity project at repo root + UPM package + `package.json` + five asmdefs
- [x] Format contract — `Core/NamerFormat.cs` (octahedral encode/decode, alpha bit packing) + golden-vector EditMode tests
- [x] Runtime decode — `Shaders/NAMER.shader` + `Shaders/NamerSurface.hlsl` (URP `UniversalFragmentPBR`)
- [x] End-to-end slice — PlayMode round-trip smoke test: Core encode → packed bytes → shader decode → material render
- [x] Local "run" command — `Unity -batchmode -runTests` (EditMode + PlayMode) as the full-stack exercise

## Out of Scope (Deferred to Later Slices)

- GPU compute kernels (Phase 2) — `.compute` folder is scaffolded only (`NAMERPack.compute` placeholder).
- Source material inspection / map normalization / defaults (Phase 2).
- Asset generation, editor window, `Process with NAMER`, before/after preview (Phase 3).
- Vertex-color decomposition fitting, residual, seam splitting, adaptive resolution (Phase 4).
- Stylization, `NAMERStyleProfile`, palette extraction, edge-preserving smoothing (Phase 5).
- Emissive-color average computation — Phase 1 only carries the `_EmissionColor` slot + `_EMISSION` keyword gate; the average-emissive calculation is Phase 2/3.
- Blender golden-vector regeneration from the live plugin (optional follow-up; hand-derived formula-vectors are authoritative for now — research Open Question 3).

## Subsequent Slice Plan

Each later phase adds one vertical slice on top of this skeleton without altering its architectural decisions:

- Phase 2: read a source URP Lit material and produce normalized + packed textures in GPU compute, verified against Core.
- Phase 3: generate a NAMER material non-destructively under `NAMERGenerated/` from an editor window with before/after preview.
- Phase 4: fit low-frequency base color into vertex colors (barycentric least-squares) with a residual texture.
- Phase 5: reference-image-driven, hue-preserving stylization via `NAMERStyleProfile`.

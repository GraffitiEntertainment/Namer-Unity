# NAMER Unity Plugin

![The NAMER Processor window](docs/images/NAMER-preview.png)

NAMER is a compact material format for real-time 3D: instead of shipping a stack of full-resolution PBR textures per material, a NAMER material packs roughness, ambient occlusion, metallic, emissive, and the surface normal into **one small RGBA surface texture** — barycentric octahedral normals, 6-bit roughness (64 values), metallic/emissive bits in the alpha — and can go further, decomposing the base color itself into **mesh vertex colors** so the material ships with a single texture (or none) and an HDR EXR residual only when the fit needs one.

This repo is the Unity-native implementation of that format: a Unity Package Manager plugin (C# + HLSL compute shaders) that converts ordinary Unity PBR materials into NAMER materials entirely inside the Unity Editor — generating, processing, compressing, previewing, and (eventually) stylizing textures without round-tripping through Blender.

**The whole workflow in one sentence:** select a textured FBX, GameObject, material, or folder, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

## What it does

- **Packs the PBR surface** — three staged GPU compute kernels (normalize → octahedral encode → pack) compress roughness/AO/metallic/emissive + normal into a single `R8G8B8A8_UNorm` surface texture, decode-equivalent to the original Blender NAMER implementation.
- **Un-multiplies baked AO** from the base color, with an optional geometry-based AO bake (blur/strength/contrast controls) when the source has no authored occlusion map.
- **Decomposes the base color into vertex colors** — a Gouraud projection fits the base color to per-vertex Color32 data so the divided base is white by construction; the removed detail can be re-expressed as roughness gloss, and a residual EXR is written only when you ask for it.
- **Live before/after preview** — a dual-pane render of the actual mesh with per-channel debug views, so every parameter is tuned against what Process will actually write.
- **Source-safe** — imported source assets are never touched; all generated output goes to a separate destination folder (`Assets/NAMERGenerated/` by default).

## Getting started

1. Open **Tools > NAMER > Processor** (or right-click an asset → **Assets > Process with NAMER** for the no-window fast path).
2. Select something with PBR materials — a GameObject, prefab, FBX, single material, or a folder.
3. Tune the stage gates, sliders, and decomposition options against the live preview.
4. Press **Process with NAMER**.

Full documentation of every window section, slider, and toggle: **[docs/USER_GUIDE.md](docs/USER_GUIDE.md)**.

## Status

Phases 1–4 complete — the full conversion pipeline is working end to end:

- **Format contract** (`Core/NamerFormat.cs`) — barycentric octahedral normals, 6-bit roughness, metallic/emissive bits, mirrored line-for-line in HLSL
- **Source inspection** — Material / GameObject / prefab / FBX sub-asset / folder selections resolved into deduplicated material inspections with per-map sRGB metadata
- **GPU compute pipeline** (`Compute/NAMERPack.compute`) — normalize → octahedral encode → pack, `R16G16B16A16_SFloat` intermediates, pooled render-texture lifecycle
- **Runtime NAMER shader** (`Shaders/`) — URP hand-written HLSL decode
- **Editor workflow + preview** — the full NAMER Processor window with before/after preview, channel debug views, and debounced live recomputes
- **AO un-multiply + geometry bake** — authored `_OcclusionMap` always transfers; the AO checkbox gates synthesis only
- **Vertex-color decomposition + residual** — Gouraud-projection one-texture mode with roughness transfer from removed detail

Roadmap: stylization profiles driven by reference images (Phase 5). See `.planning/ROADMAP.md`.

## Requirements

- Unity **6000.0 LTS** (developed against 6000.0.82f1)
- **URP 17.x** (Universal Render Pipeline)
- C# 9 / .NET Standard 2.1 (Unity 6 profile)
- [Git LFS](https://git-lfs.com) — binary assets (models, textures) are stored via LFS

## Repository layout

```
Packages/com.graffitientertainment.namer/   # The UPM plugin package
  Core/        # Pure C# format oracle (no UnityEngine.Object dependencies)
  Runtime/     # Runtime shader + assembly
  Editor/      # Inspector, compute pipeline, texture pool (Editor-only assembly)
  Compute/     # HLSL compute kernels + shared encode include
  Shaders/     # Runtime NAMER surface shader
  Tests/       # EditMode + PlayMode test assemblies
docs/                                       # User guide + section images
ProjectSettings/                            # Unity project configuration
Assets/                                     # Smoke-test scene + fixtures
.planning/                                  # GSD workflow artifacts (roadmap, plans, verification)
```

## Development

The project uses the [GSD workflow](.planning/PROJECT.md); phase plans, verification reports, and security audits live under `.planning/phases/`.

Tests run through Unity Test Framework (EditMode + PlayMode). From the repo root with the editor closed:

```bash
Unity -batchmode -projectPath . -runTests -testPlatform EditMode \
  -testResults test-results.xml -logFile test.log
```

## Constraints

- Source assets must never be modified; generated output lives in a separate directory
- NAMER packed textures must decode equivalently to the Blender NAMER implementation
- GPU compute (HLSL) for high-resolution texture work; no per-pixel C# loops
- Ships as a Unity Package Manager package — no native plugins

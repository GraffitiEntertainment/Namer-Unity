# NAMER Unity Plugin

A Unity-native editor plugin (UPM package) that converts ordinary Unity PBR materials into compact NAMER materials entirely inside Unity — generating, processing, compressing, previewing, and stylizing textures without round-tripping through Blender.

Select a textured FBX, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

## Status

Phase 2 of 5 complete — source inspection + GPU compute pipeline:

- **Format contract** (`Core/NamerFormat.cs`) — barycentric octahedral normals, 6-bit roughness (64 values), metallic/emissive bits in the packed alpha, mirrored line-for-line in HLSL
- **Source inspector** (`Editor/Pipeline/SourceInspector.cs`) — resolves Material / scene GameObject / prefab / FBX sub-asset / folder selections into deduplicated material inspections with per-map sRGB metadata and scalar fallbacks
- **GPU compute pipeline** (`Editor/Pipeline/NamerComputePipeline.cs`) — three staged kernels (normalize → octahedral encode → pack) in `Compute/NAMERPack.compute`, `R16G16B16A16_SFloat` intermediates, `R8G8B8A8_UNorm` packed output, pooled render-texture lifecycle
- **Runtime NAMER shader** (`Shaders/`) — URP hand-written HLSL decode, decode-equivalent to the Blender NAMER implementation

Roadmap: asset generation + editor workflow + preview (Phase 3) → vertex-color decomposition (Phase 4) → stylization profiles (Phase 5). See `.planning/ROADMAP.md`.

## Requirements

- Unity **6000.0 LTS** (developed against 6000.0.82f1)
- **URP 17.x** (Universal Render Pipeline)
- C# 9 / .NET Standard 2.1 (Unity 6 profile)
- [Git LFS](https://git-lfs.com) — binary assets (models, textures) are stored via LFS

## Repository Layout

```
Packages/com.graffitientertainment.namer/   # The UPM plugin package
  Core/        # Pure C# format oracle (no UnityEngine.Object dependencies)
  Runtime/     # Runtime shader + assembly
  Editor/      # Inspector, compute pipeline, texture pool (Editor-only assembly)
  Compute/     # HLSL compute kernels + shared encode include
  Shaders/     # Runtime NAMER surface shader
  Tests/       # EditMode + PlayMode test assemblies
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

Source assets under test are never modified — all generated output stays in-memory (Phase 2) or in a dedicated generated-assets directory (Phase 3+).

## Constraints

- Source assets must never be modified; generated output lives in a separate directory
- NAMER packed textures must decode equivalently to the Blender NAMER implementation
- GPU compute (HLSL) for high-resolution texture work; no per-pixel C# loops
- Ships as a Unity Package Manager package — no native plugins

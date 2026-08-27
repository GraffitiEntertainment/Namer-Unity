# NAMER

A Unity-native editor plugin (UPM package, C# + HLSL) that converts ordinary Unity PBR
materials into compact NAMER materials entirely inside Unity — generating, processing,
compressing, previewing, and stylizing textures without round-tripping through Blender.

## Packed Surface Format

The NAMER packed surface texture (`R8G8B8A8_UNorm`, linear/uncompressed) stores, per texel:

| Channel | Value |
|---------|-------|
| R / G   | Octahedral normal (DirectX normal-map texel, barycentric projection — always in `[0.5, 1.0]`) |
| B       | Ambient occlusion (AO, raw) |
| A       | Packed bits: bit 7 = metallic (`> 0.5`), bit 6 = emissive (`> 0.1`), bits 0–5 = 6-bit roughness (64 values) |

The base/residual color texture is sRGB; emissive color is stored as material metadata.

## Installation

Add the package by path or via UPM from the `Packages/com.graffitientertainment.namer` folder; requires Unity 6000.0+ and Universal Render Pipeline 17.x.

## Workflow

Select a textured FBX, prefab, model, material, or folder, then run `Tools > NAMER > Processor` (or `Assets > Process with NAMER` / `GameObject > Process with NAMER`), preview before/after and debug channels, adjust AO un-multiply, then press `Process with NAMER`. Generated assets land under `Assets/NAMERGenerated/{source}/`; source assets are never modified.

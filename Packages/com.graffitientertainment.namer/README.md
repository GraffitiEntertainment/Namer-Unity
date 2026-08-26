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

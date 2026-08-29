#ifndef GRAFFITI_ENTERTAINMENT_NAMER_ENCODE_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_ENCODE_INCLUDED

// NAMER GPU encode + pack math (plan 02-02).
//
// Line-for-line HLSL mirror of GraffitiEntertainment.Namer.Core.NamerFormat
// (plan 01-01) and its NamerConstants. This include is the single shared source of
// truth for the GPU kernels so they never re-derive the packed-format math. Do not
// edit the constants or thresholds here independently of Core/NamerConstants.cs.

// ------------------------------------------------------------------
// Constants mirroring Core/NamerConstants.cs (D-02, D-03).
// ------------------------------------------------------------------
static const uint  NAMER_METALLIC_BIT        = 0x80u;
static const uint  NAMER_EMISSIVE_BIT        = 0x40u;
static const uint  NAMER_ROUGHNESS_MASK      = 0x3Fu;
static const float NAMER_METALLIC_THRESHOLD  = 0.5;
static const float NAMER_EMISSIVE_THRESHOLD  = 0.1;
static const float NAMER_ROUGHNESS_LEVELS    = 63.0;
static const float NAMER_ALPHA_BYTE_SCALE    = 255.0;
static const float NAMER_EPSILON             = 1e-6;
static const float NAMER_AO_FLOOR = 0.1;

// ------------------------------------------------------------------
// Mirrors NamerFormat.OctahedralEncode EXACTLY.
// Barycentric projection of the non-negative DirectX texel (the reference's
// normalize and L1-divide cancel for these inputs). R/G land in [0.5, 1.0].
// ------------------------------------------------------------------
float2 NamerOctahedralEncode(float3 v)
{
    float sum = max(v.x + v.y + v.z, NAMER_EPSILON);
    return float2(0.5 + 0.5 * v.x / sum, 0.5 + 0.5 * v.y / sum);
}

// ------------------------------------------------------------------
// Mirrors NamerFormat.PackAlphaBits EXACTLY.
// Metallic (bit 7) and emissive (bit 6) use STRICT > thresholds; roughness
// uses linear floor quantization to [0, 63].
// ------------------------------------------------------------------
uint NamerPackAlphaBits(float metallic, float emissive, float roughness)
{
    uint metallicBit   = metallic > NAMER_METALLIC_THRESHOLD ? NAMER_METALLIC_BIT : 0u;
    uint emissiveBit   = emissive > NAMER_EMISSIVE_THRESHOLD ? NAMER_EMISSIVE_BIT : 0u;
    uint roughnessBits = (uint)clamp((int)floor(roughness * NAMER_ROUGHNESS_LEVELS), 0, (int)NAMER_ROUGHNESS_MASK);
    return metallicBit | emissiveBit | roughnessBits;
}

// ------------------------------------------------------------------
// Mirrors NamerFormat.PackSurface's POST-encode body EXACTLY. The oct
// argument is ALREADY encoded (do NOT re-encode). This is the helper the
// CSSurfacePack kernel calls.
// ------------------------------------------------------------------
float4 NamerPackSurfaceFromOct(float2 oct, float ao, float metallic, float emissive, float roughness)
{
    float alpha = NamerPackAlphaBits(metallic, emissive, roughness) / NAMER_ALPHA_BYTE_SCALE;
    return float4(oct.x, oct.y, ao, alpha);
}

// ------------------------------------------------------------------
// Mirrors NamerFormat.PackSurface EXACTLY (full path: encode then pack).
// normalTexel is a raw DirectX normal-map texel in [0,1].
// ------------------------------------------------------------------
float4 NamerPackSurface(float3 normalTexel, float ao, float metallic, float emissive, float roughness)
{
    return NamerPackSurfaceFromOct(NamerOctahedralEncode(normalTexel), ao, metallic, emissive, roughness);
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_ENCODE_INCLUDED

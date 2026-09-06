#ifndef GRAFFITI_ENTERTAINMENT_NAMER_ROUGHNESS_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_ROUGHNESS_INCLUDED

// NAMER image-space roughness extraction math (Phase 04.1, plan 01).
//
// Rec.601 luminance weights + the Sobel luminance helper for the Blender-parity
// roughness estimator. These weights INTENTIONALLY differ from NAMERAO.hlsl's Rec.709
// NAMER_LUMA_* constants: Blender's extract_roughness uses Rec.601
// (0.299 / 0.587 / 0.114), so the Sobel path mirrors Rec.601 for byte-level parity
// while the AO path stays Rec.709 (RESEARCH Pitfall 4). Do NOT reuse NAMER_LUMA_* here.

// ------------------------------------------------------------------
// Rec.601 luminance weights (Blender-parity Sobel estimator, RESEARCH Pitfall 4).
// ------------------------------------------------------------------
static const float NAMER_ROUGH_LUMA_R = 0.299;
static const float NAMER_ROUGH_LUMA_G = 0.587;
static const float NAMER_ROUGH_LUMA_B = 0.114;

// ------------------------------------------------------------------
// Blender-parity luminance: lum = 0.299R + 0.587G + 0.114B.
// ------------------------------------------------------------------
float NamerRoughnessLuminance(float3 c)
{
    return dot(c, float3(NAMER_ROUGH_LUMA_R, NAMER_ROUGH_LUMA_G, NAMER_ROUGH_LUMA_B));
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_ROUGHNESS_INCLUDED

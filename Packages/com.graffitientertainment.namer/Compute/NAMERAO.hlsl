#ifndef GRAFFITI_ENTERTAINMENT_NAMER_AO_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_AO_INCLUDED

// NAMER image-space AO extraction math (Phase 03.1, plan 01).
//
// HLSL mirror of the AO floor constant in GraffitiEntertainment.Namer.Core.NamerConstants
// (D-09) plus the Rec.709 luminance weights and the source-agnostic AO strength/contrast
// remap (D-11). This include is the single shared source of truth for the AO kernels so
// they never re-derive the un-multiply floor. Do not edit the constants here
// independently of Core/NamerConstants.cs.

// ------------------------------------------------------------------
// Constants mirroring Core/NamerConstants.cs (D-09).
// ------------------------------------------------------------------
static const float NAMER_AO_FLOOR = 0.1;

// ------------------------------------------------------------------
// Rec.709 luminance weights (D-01 image-space extraction).
// ------------------------------------------------------------------
static const float NAMER_LUMA_R = 0.2126;
static const float NAMER_LUMA_G = 0.7152;
static const float NAMER_LUMA_B = 0.0722;

// ------------------------------------------------------------------
// Source-agnostic AO tweak remap (D-11). strength = 1 is full AO,
// strength = 0 is white (no AO); contrast = 1 is identity (pivot 0.5).
// ------------------------------------------------------------------
float NamerAoRemap(float ao, float strength, float contrast)
{
    ao = saturate(lerp(1.0, ao, strength));
    ao = saturate((ao - 0.5) * contrast + 0.5);
    return ao;
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_AO_INCLUDED

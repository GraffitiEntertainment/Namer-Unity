#ifndef GRAFFITI_ENTERTAINMENT_NAMER_DECOMP_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_DECOMP_INCLUDED

// NAMER vertex-color decomposition + residual math (Phase 4, plan 02).
//
// HLSL mirror of the vertex-color floor constant in
// GraffitiEntertainment.Namer.Core.NamerConstants (Pitfall 1) plus the viridis-style
// perceptually-uniform reconstruction-error heatmap ramp (D-11). This include is the
// single shared source of truth for the decomp kernels so they never re-derive the
// residual-quotient divisor floor or the heatmap ramp. Do not edit the constants here
// independently of Core/NamerConstants.cs.

// ------------------------------------------------------------------
// Constants mirroring Core/NamerConstants.cs (Pitfall 1).
// ------------------------------------------------------------------
static const float NAMER_VC_FLOOR = 1e-3;

// ------------------------------------------------------------------
// Viridis-style 5-anchor error-heatmap ramp (D-11). Perceptually uniform, not
// grayscale. Anchors from 04-UI-SPEC: violet -> blue -> teal -> green -> yellow.
// x is clamped to [0,1] before piecewise linear interpolation.
// ------------------------------------------------------------------
float3 NAMER_DECOMP_HEATMAP(float x)
{
    x = saturate(x);

    const float3 c0 = float3(0.2667, 0.0039, 0.3294); // #440154 deep violet
    const float3 c1 = float3(0.2314, 0.3216, 0.5451); // #3B528B blue
    const float3 c2 = float3(0.1294, 0.5686, 0.5490); // #21918C teal
    const float3 c3 = float3(0.3686, 0.7882, 0.3843); // #5EC962 green
    const float3 c4 = float3(0.9922, 0.9059, 0.1451); // #FDE725 yellow

    if (x < 0.25)
    {
        return lerp(c0, c1, x / 0.25);
    }

    if (x < 0.5)
    {
        return lerp(c1, c2, (x - 0.25) / 0.25);
    }

    if (x < 0.75)
    {
        return lerp(c2, c3, (x - 0.5) / 0.25);
    }

    return lerp(c3, c4, (x - 0.75) / 0.25);
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_DECOMP_INCLUDED

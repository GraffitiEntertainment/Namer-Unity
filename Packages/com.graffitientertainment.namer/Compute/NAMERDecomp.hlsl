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
// The body is a single-exit-point nested ternary (WR-04): HLSL->MSL translation
// turns early returns into predicated assignment that the Metal frontend misreads as
// an uninitialized result. Segment math is bit-identical to the early-return form.
// ------------------------------------------------------------------
float3 NAMER_DECOMP_HEATMAP(float x)
{
    x = saturate(x);

    const float3 c0 = float3(0.2667, 0.0039, 0.3294); // #440154 deep violet
    const float3 c1 = float3(0.2314, 0.3216, 0.5451); // #3B528B blue
    const float3 c2 = float3(0.1294, 0.5686, 0.5490); // #21918C teal
    const float3 c3 = float3(0.3686, 0.7882, 0.3843); // #5EC962 green
    const float3 c4 = float3(0.9922, 0.9059, 0.1451); // #FDE725 yellow

    // Single exit point (Metal MSL translation): the four piecewise-linear segments
    // are computed unconditionally (lerp is total, no side effects) and selected by a
    // nested ternary, so the translated code has no early returns the Metal frontend
    // can misread as an uninitialized result. Segment math is IDENTICAL to the
    // early-return form.
    float3 seg0 = lerp(c0, c1, x / 0.25);
    float3 seg1 = lerp(c1, c2, (x - 0.25) / 0.25);
    float3 seg2 = lerp(c2, c3, (x - 0.5) / 0.25);
    float3 seg3 = lerp(c3, c4, (x - 0.75) / 0.25);
    return (x < 0.25) ? seg0 : (x < 0.5) ? seg1 : (x < 0.75) ? seg2 : seg3;
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_DECOMP_INCLUDED

#ifndef GRAFFITI_ENTERTAINMENT_NAMER_POST_PREVIEW_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_POST_PREVIEW_INCLUDED

// NAMER in-shader post-preview chain (05.1 D-04, STYL-09).
//
// Function-for-function twin of the URP 17.0.4 LDR grading path the fitted
// Volume drives at runtime: steps 2-3 mirror UberPost/Common.hlsl
// (ApplyColorGrading's postExposure multiply + ApplyTonemap's ACES), steps
// 4a-4g mirror LutBuilderLdr.shader (white balance, LogC contrast, Rec.709
// saturation, YRGB curves, final saturate). The chain is composed in the
// ForwardLit pass ONLY, between UniversalFragmentPBR and MixFog, gated by
// _PostEnable (default 0 = identity) so shipped runtime materials are
// byte-identical — the scene Volume remains the runtime truth (phase premise:
// nothing post-processed is baked into any albedo or saved texture).
//
// The URP core shader library functions are INCLUDED, not re-derived
// (LutBuilderLdr.shader's own include set): LinearToLogC/LogCToLinear,
// LinearToLMS/LMSToLinear, AcesTonemap, Luminance from core Color.hlsl;
// unity_to_ACES + the ACEScc_MIDGRAY pivot from core ACES.hlsl (pulled in by
// Color.hlsl). The uniforms live in NamerSurface.hlsl's single
// UnityPerMaterial CBUFFER (SRP-batcher stable layout); _PostCurveTex is
// declared beside the other texture samplers there.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"

// Lens gate threshold: _PostEnable is written as a full 0/1 float table by the
// processor window (ApplyPostLook — the ApplyDebugGates unconditional-binding
// discipline; Metal silently skips unbound properties).
static const float kNamerPostEnableThreshold = 0.5;

// YRGB curve pre-offset — URP's own kHalfPixel (LutBuilderLdr.shader line
// 122): half a texel of the 128-texel curve strip = 1/256. Added before the
// master pass and again before the R/G/B pass.
static const float kNamerPostCurveHalfPixel = (1.0 / 128.0) / 2.0;

// Samples the window-built 128x1 curve strip (linear filter, clamp wrap — the
// LinearClamp sampling URP's LUT-builder EvaluateCurve uses). Channel packing:
// .r/.g/.b = fitted R/G/B curves, .a = fitted master curve.
float4 EvaluatePostCurve(float t)
{
    return SAMPLE_TEXTURE2D(_PostCurveTex, sampler_PostCurveTex, float2(t, 0.0));
}

// Applies the fitted post grade in-shader. Mirrors, step for step:
//   step 2  UberPost/Common.hlsl ApplyColorGrading   — input *= 2^postExposure
//   step 3  UberPost/Common.hlsl ApplyTonemap        — unity_to_ACES/AcesTonemap
//   step 4a LutBuilderLdr.shader lines 45-48         — LMS white balance
//   step 4b LutBuilderLdr.shader lines 50-53         — LogC contrast pivot
//   step 4c (omitted — colorFilter stays neutral white per UI-SPEC)
//   step 4d (omitted — split toning/mixer/SMH/LGG stay neutral)
//   step 4e LutBuilderLdr.shader lines 116-118       — Rec.709 luma saturation
//   step 4f LutBuilderLdr.shader lines 120-138       — YRGB curves, 1/256 offsets
//   step 4g LutBuilderLdr.shader line 140            — final saturate
void ApplyNamerPostPreview(inout half3 color)
{
    if (_PostEnable < kNamerPostEnableThreshold)
    {
        return;
    }

    float3 c = color;

    // Step 2 — post-exposure. URP computes 2^EV CPU-side (PostProcessPass.cs
    // line 1534: postExposureLinear = Pow(2, EV)); the EV rides the uniform so
    // the exp2 lands in-shader.
    c *= exp2(_PostExposureEv);

    // Step 3 — ACES tonemap, saturate-clamped exactly like ApplyTonemap.
    c = saturate(AcesTonemap(unity_to_ACES(c)));

    // Step 4a — white balance in LMS space. The CPU side evaluates
    // ColorUtils.ColorBalanceToLMSCoeffs(temp, tint) — the exact function
    // URP's LUT pass calls (ColorGradingLutPass.cs line 175).
    float3 colorLms = LinearToLMS(c);
    colorLms *= _PostLmsCoeffs;
    c = LMSToLinear(colorLms);

    // Step 4b — contrast in LogC after white balance: deviation scaling around
    // ACEScc_MIDGRAY (0.4135884 — URP's actual pivot, not a typo). The CPU
    // pre-computes _PostContrastZ = contrast/100 + 1 (ColorGradingLutPass.cs
    // line 176).
    float3 colorLog = LinearToLogC(c);
    colorLog = (colorLog - ACEScc_MIDGRAY) * _PostContrastZ + ACEScc_MIDGRAY;
    c = LogCToLinear(colorLog);

    // Step 4e — global saturation. LutBuilderLdr's GetLuminance resolves to
    // Luminance() (Rec.709 luma) on the LUT path — no _TONEMAP_ACES keyword
    // there — and satMult is 1 (hue/sat curves stay neutral). The CPU
    // pre-computes _PostSaturationS = saturation/100 + 1.
    float luma = Luminance(c);
    c = luma.xxx + _PostSaturationS * (c - luma.xxx);

    // Step 4f — YRGB curves: add the 1/256 pre-offset, apply the MASTER curve
    // (.a) per channel, add 1/256 again, apply the R/G/B curves (.r/.g/.b) at
    // the (already master-mapped, re-offset) channel values.
    {
        float3 curved = c + kNamerPostCurveHalfPixel.xxx;
        float3 master = float3(
            EvaluatePostCurve(curved.r).a,
            EvaluatePostCurve(curved.g).a,
            EvaluatePostCurve(curved.b).a);
        curved = master + kNamerPostCurveHalfPixel.xxx;
        c = float3(
            EvaluatePostCurve(curved.r).r,
            EvaluatePostCurve(curved.g).g,
            EvaluatePostCurve(curved.b).b);
    }

    // Step 4g — final saturate.
    color = saturate(c);
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_POST_PREVIEW_INCLUDED

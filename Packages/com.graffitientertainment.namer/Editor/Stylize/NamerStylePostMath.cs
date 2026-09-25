using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// CPU twin of the URP 17.0.4 LDR grading-path math: LogC (El 1000), CAT02-style LMS
    /// conversion, and the fitted ACES tonemap used by the LDR color-grading LUT path.
    /// Every constant is transcribed verbatim from the installed core RP package
    /// (com.unity.render-pipelines.core@17.0.4, PackageCache f663854f0ceb) with a
    /// file:line citation so the twin cannot drift from the HLSL it mirrors
    /// (same contract as NamerFormat.cs twinning NamerSurface.hlsl).
    /// Pinned by Tests/Editor/NamerStylePostMathTests.cs golden values.
    /// Pure C# on Unity.Mathematics only; no engine object types, no state, no RNG.
    /// </summary>
    public static class NamerStylePostMath
    {
        // -------- LogC (El 1000) — core ShaderLibrary/Color.hlsl:345-357 (ParamsLogC LogC) --------
        public const float kLogCCut = 0.011361f;
        public const float kLogCa = 5.555556f;
        public const float kLogCb = 0.047996f;
        public const float kLogCc = 0.244161f;
        public const float kLogCd = 0.386036f;
        public const float kLogCe = 5.301883f;
        public const float kLogCf = 0.092819f;

        // -------- LMS (CAT02-style) — core ShaderLibrary/Color.hlsl:252-273 --------
        private static readonly float3x3 kLin2Lms = new float3x3(
            3.90405e-1f, 5.49941e-1f, 8.92632e-3f,
            7.08416e-2f, 9.63172e-1f, 1.35775e-3f,
            2.31082e-2f, 1.28021e-1f, 9.36245e-1f);

        private static readonly float3x3 kLms2Lin = new float3x3(
            2.85847e+0f, -1.62879e+0f, -2.48910e-2f,
            -2.10182e-1f, 1.15820e+0f, 3.24281e-4f,
            -4.18120e-2f, -1.18169e-1f, 1.06867e+0f);

        // -------- pivots --------
        // ACES.hlsl:67 ACEScc_MIDGRAY; provenance: (log2(0.18) + 9.72) / 17.52 = 0.4135884.
        // URP's LDR contrast reuses this value as the LogC pivot (URP Grading LUTContrast).
        public const float kAcesCcMidGray = 0.4135884f;

        // Rec.709 luma weights — core Color.hlsl luminance helpers.
        private static readonly float3 kLuma709 = new float3(0.2126f, 0.7152f, 0.0722f);

        // -------- ACES matrices — core ShaderLibrary/ACES.hlsl:60-160 --------
        private static readonly float3x3 kSrgb2Ap0 = new float3x3(
            0.4397010f, 0.3829780f, 0.1773350f,
            0.0897923f, 0.8134230f, 0.0967616f,
            0.0175440f, 0.1115440f, 0.8707040f); // ACES.hlsl:62-66 sRGB_2_AP0

        private static readonly float3x3 kAp0ToAp1 = new float3x3(
            1.4514393161f, -0.2365107469f, -0.2149285693f,
            -0.0765537734f, 1.1762296998f, -0.0996759264f,
            0.0083161484f, -0.0060324498f, 0.9977163014f); // ACES.hlsl:94-98 AP0_2_AP1_MAT

        private static readonly float3x3 kAp1ToXyz = new float3x3(
            0.6624541811f, 0.1340042065f, 0.1561876870f,
            0.2722287168f, 0.6740817658f, 0.0536895174f,
            -0.0055746495f, 0.0040607335f, 1.0103391003f); // ACES.hlsl:106-110 AP1_2_XYZ_MAT

        private static readonly float3x3 kXyzToAp1 = new float3x3(
            1.6410233797f, -0.3248032942f, -0.2364246952f,
            -0.6636628587f, 1.6153315917f, 0.0167563477f,
            0.0117218943f, -0.0082844420f, 0.9883948585f); // ACES.hlsl:112-116 XYZ_2_AP1_MAT

        private static readonly float3x3 kXyzToRec709 = new float3x3(
            3.2409699419f, -1.5373831776f, -0.4986107603f,
            -0.9692436363f, 1.8759675015f, 0.0415550574f,
            0.0556300797f, -0.2039769589f, 1.0569715142f); // ACES.hlsl:118-122 XYZ_2_REC709_MAT

        private static readonly float3x3 kD60ToD65 = new float3x3(
            0.98722400f, -0.00611327f, 0.0159533f,
            -0.00759836f, 1.00186000f, 0.0053302f,
            0.00307257f, -0.00509595f, 1.0816800f); // ACES.hlsl:156-160 D60_2_D65_CAT

        private static readonly float3 kAp1Rgb2Y = new float3(0.272229f, 0.674082f, 0.0536895f); // ACES.hlsl:142

        // -------- RRT / ODT constants — core ShaderLibrary/ACES.hlsl:658-666, 814 --------
        private const float kRrtGlowGain = 0.05f;      // ACES.hlsl:658 RRT_GLOW_GAIN
        private const float kRrtGlowMid = 0.08f;       // ACES.hlsl:659 RRT_GLOW_MID
        private const float kRrtRedScale = 0.82f;      // ACES.hlsl:661 RRT_RED_SCALE
        private const float kRrtRedPivot = 0.03f;      // ACES.hlsl:662 RRT_RED_PIVOT
        private const float kRrtRedHue = 0.0f;         // ACES.hlsl:663 RRT_RED_HUE
        private const float kRrtRedWidth = 135.0f;     // ACES.hlsl:664 RRT_RED_WIDTH
        private const float kRrtSatFactor = 0.96f;     // ACES.hlsl:666 RRT_SAT_FACTOR
        private const float kOdtSatFactor = 0.93f;     // ACES.hlsl:814 ODT_SAT_FACTOR
        private const float kDimSurroundGamma = 0.9811f; // ACES.hlsl:732 DIM_SURROUND_GAMMA
        private const float kHalfMax = 65504.0f;       // Macros.hlsl:50 HALF_MAX

        // -------- rgb_2_saturation — ACES.hlsl:320-326 --------
        private const float kSaturationTiny = 1e-4f;   // ACES.hlsl:322 TINY
        private const float kSaturationMaxFloor = 1e-2f; // ACES.hlsl:325 max(ma, 1e-2)

        // -------- rgb_2_yc — ACES.hlsl:328-356 --------
        private const float kYcRadiusWeight = 1.75f;   // ACES.hlsl:330 ycRadiusWeight
        private const float kYcDivisor = 3.0f;         // ACES.hlsl:355 / 3.0

        // -------- rgb_2_hue / center_hue — ACES.hlsl:358-379 --------
        private const float kHueDegreesPerRadian = 57.29577951308232f; // (180/PI), ACES.hlsl:366
        private const float kHueChromaScaleSqrt3 = 1.7320508075688772f; // sqrt(3.0), ACES.hlsl:366
        private const float kHueRedWeight = 2.0f;      // ACES.hlsl:366 2.0 * rgb.x
        private const float kHueFullTurn = 360.0f;     // ACES.hlsl:368
        private const float kHueHalfTurn = 180.0f;     // ACES.hlsl:376-377

        // -------- sigmoid_shaper — ACES.hlsl:381-389 --------
        private const float kSigmoidSpan = 2.0f;       // ACES.hlsl:385 x / 2.0 and :389 y / 2.0

        // -------- glow_fwd — ACES.hlsl:391-403 --------
        private const float kGlowLowBand = 2.0f / 3.0f;  // ACES.hlsl:395 2.0 / 3.0
        private const float kGlowHighBand = 2.0f;        // ACES.hlsl:397 2.0
        private const float kGlowPivotOffset = 0.5f;     // ACES.hlsl:400 1.0 / 2.0

        // -------- AcesTonemap glow/red-modifier entry constants — core Color.hlsl:645-662 --------
        private const float kRrtSigmoidCenter = 0.4f;    // Color.hlsl:648 (saturation - 0.4)
        private const float kRrtSigmoidSpan = 0.2f;      // Color.hlsl:648 / 0.2
        private const float kRedModifierHueScale = 2.0f; // Color.hlsl:658 2.0 * centeredHue

        // -------- RRTAndODT rational fit — core Color.hlsl:673-677 --------
        private const float kFitA = 0.0245786f;
        private const float kFitB = 0.000090537f;
        private const float kFitC = 0.983729f;
        private const float kFitD = 0.4329510f;
        private const float kFitE = 0.238081f;

        // -------- darkSurround_to_dimSurround — ACES.hlsl:718-745 --------
        private const float kXyzSumFloor = 1e-4f;      // ACES.hlsl:720 max(dot(XYZ, 1), 1e-4)
        private const float kChromaYFloor = 1e-4f;     // ACES.hlsl:726 max(xyY.y, 1e-4)

        /// <summary>
        /// LogC (El 1000) encode of a scalar, non-precise URP form:
        /// c * log10(max(a * x + b, 0)) + d — core Color.hlsl LinearToLogC.
        /// For x >= 0 (the grading domain) the max() guard is inactive
        /// (a * 0 + b = b > 0), it only hardens negative inputs.
        /// </summary>
        public static float LinearToLogC(float x)
        {
            return kLogCc * log10(max(kLogCa * x + kLogCb, 0f)) + kLogCd;
        }

        /// <summary>
        /// LogC (El 1000) encode per component — non-precise URP form, core Color.hlsl.
        /// </summary>
        public static float3 LinearToLogC(float3 v)
        {
            return kLogCc * log10(max(kLogCa * v + kLogCb, 0f)) + kLogCd;
        }

        /// <summary>
        /// LogC (El 1000) decode of a scalar: (10^(x - d) - b) / a — core Color.hlsl LogCToLinear.
        /// math.exp10 is the Unity.Mathematics form of the HLSL pow(10, ...).
        /// </summary>
        public static float LogCToLinear(float x)
        {
            return (exp10((x - kLogCd) / kLogCc) - kLogCb) / kLogCa;
        }

        /// <summary>
        /// LogC (El 1000) decode per component — core Color.hlsl LogCToLinear.
        /// </summary>
        public static float3 LogCToLinear(float3 v)
        {
            return (exp10((v - kLogCd) / kLogCc) - kLogCb) / kLogCa;
        }

        /// <summary>
        /// Linear RGB to CAT02-style LMS — core Color.hlsl:252-263 LinearToLMS.
        /// </summary>
        public static float3 LinearToLms(float3 x)
        {
            return mul(kLin2Lms, x);
        }

        /// <summary>
        /// CAT02-style LMS back to linear RGB — core Color.hlsl:265-275 LMSToLinear.
        /// </summary>
        public static float3 LmsToLinear(float3 x)
        {
            return mul(kLms2Lin, x);
        }

        /// <summary>
        /// Rec.709 luma of a linear RGB triple — core Color.hlsl luminance weights.
        /// </summary>
        public static float Luma709(float3 c)
        {
            return dot(c, kLuma709);
        }

        /// <summary>
        /// Unity raw (sRGB primaries, linear encoding) to ACES2065-1 (AP0, linear) —
        /// core ACES.hlsl:160-170 unity_to_ACES. NOTE: the installed core 17.0.4
        /// implementation applies NO 1.0/0.6 input scaling; it is a bare
        /// mul(sRGB_2_AP0, x). Transcribed as-is so the twin matches the shader bit for bit.
        /// </summary>
        public static float3 UnityToAces(float3 x)
        {
            return mul(kSrgb2Ap0, x);
        }

        /// <summary>
        /// Fitted ACES RRT+ODT tonemap (input/output linear, ACES2065-1 in) —
        /// core Color.hlsl:635-714 AcesTonemap, TONEMAPPING_USE_FULL_ACES == 0 path.
        /// This is the exact curve URP 17.0.4 bakes into the LDR grading LUT.
        /// </summary>
        public static float3 AcesTonemap(float3 aces)
        {
            // Glow module (Color.hlsl:645-650).
            float saturation = Rgb2Saturation(aces);
            float ycIn = Rgb2Yc(aces);
            float s = SigmoidShaper((saturation - kRrtSigmoidCenter) / kRrtSigmoidSpan);
            float addedGlow = 1f + GlowFwd(ycIn, kRrtGlowGain * s, kRrtGlowMid);
            aces *= addedGlow;

            // Red modifier (Color.hlsl:652-662).
            float hue = Rgb2Hue(aces);
            float centeredHue = CenterHue(hue, kRrtRedHue);
            float hueWeight = smoothstep(0f, 1f, 1f - abs(kRedModifierHueScale * centeredHue / kRrtRedWidth));
            hueWeight *= hueWeight;
            aces.x = aces.x + hueWeight * saturation * (kRrtRedPivot - aces.x) * (1f - kRrtRedScale);

            // ACES2065-1 to ACEScg rendering space (Color.hlsl:664-665).
            float3 acescg = max(0f, mul(kAp0ToAp1, aces));

            // RRT global desaturation (Color.hlsl:667-669).
            float rrtLuma = dot(acescg, kAp1Rgb2Y);
            acescg = rrtLuma + (acescg - rrtLuma) * kRrtSatFactor;

            // RRT + ODT rational fit (Color.hlsl:671-684).
            float3 rgbPost = (acescg * (acescg + kFitA) - kFitB) / (acescg * (kFitC * acescg + kFitD) + kFitE);

            // Dim-surround gamma compensation (Color.hlsl:689).
            float3 linearCv = DarkSurroundToDimSurround(rgbPost);

            // ODT desaturation (Color.hlsl:692-694).
            float odtLuma = dot(linearCv, kAp1Rgb2Y);
            linearCv = odtLuma + (linearCv - odtLuma) * kOdtSatFactor;

            // Back to display primaries via D60 -> D65 CAT (Color.hlsl:696-708).
            float3 xyz = mul(kAp1ToXyz, linearCv);
            xyz = mul(kD60ToD65, xyz);
            return mul(kXyzToRec709, xyz);
        }

        /// <summary>
        /// The full LDR tonemap stage as URP 17.0.4 applies it in the LDR grading path
        /// (URP PostProcessing/Common.hlsl:101-111 ApplyTonemap, ACES mode):
        /// saturate(AcesTonemap(unity_to_ACES(c))). Output is display-referred
        /// linear Rec.709 in [0, 1] — the domain of the source statistics in the
        /// post-processing look fit.
        /// </summary>
        public static float3 TonemapLdr(float3 c)
        {
            return saturate(AcesTonemap(UnityToAces(c)));
        }

        /// <summary>
        /// rgb_2_saturation — ACES.hlsl:320-326.
        /// </summary>
        private static float Rgb2Saturation(float3 rgb)
        {
            float mi = cmin(rgb);
            float ma = cmax(rgb);
            return (max(ma, kSaturationTiny) - max(mi, kSaturationTiny)) / max(ma, kSaturationMaxFloor);
        }

        /// <summary>
        /// rgb_2_yc luminance proxy — ACES.hlsl:328-356.
        /// </summary>
        private static float Rgb2Yc(float3 rgb)
        {
            float r = rgb.x;
            float g = rgb.y;
            float b = rgb.z;
            float k = b * (b - g) + g * (g - r) + r * (r - b);
            k = max(k, 0f);
            float chroma = sqrt(k);
            return (b + g + r + kYcRadiusWeight * chroma) / kYcDivisor;
        }

        /// <summary>
        /// rgb_2_hue geometric hue angle in degrees (0-360) — ACES.hlsl:358-371.
        /// Neutral colors return 0 (the core guards the undefined atan2).
        /// </summary>
        private static float Rgb2Hue(float3 rgb)
        {
            float hue = 0f;
            if (!(rgb.x == rgb.y && rgb.y == rgb.z))
            {
                hue = kHueDegreesPerRadian * atan2(kHueChromaScaleSqrt3 * (rgb.y - rgb.z), kHueRedWeight * rgb.x - rgb.y - rgb.z);
                if (hue < 0f)
                {
                    hue = hue + kHueFullTurn;
                }
            }

            return hue;
        }

        /// <summary>
        /// center_hue — ACES.hlsl:373-379. Wraps hue into +/-180 degrees around centerH.
        /// </summary>
        private static float CenterHue(float hue, float centerH)
        {
            float hueCentered = hue - centerH;
            if (hueCentered < -kHueHalfTurn)
            {
                hueCentered = hueCentered + kHueFullTurn;
            }
            else if (hueCentered > kHueHalfTurn)
            {
                hueCentered = hueCentered - kHueFullTurn;
            }

            return hueCentered;
        }

        /// <summary>
        /// FastSign — core Common.hlsl:817-843 (CopySign(1, s) with ignoreNegZero):
        /// +1 for s &gt;= 0 (including +/-0), -1 otherwise.
        /// </summary>
        private static float FastSign(float s)
        {
            if (s >= 0f)
            {
                return 1f;
            }

            return -1f;
        }

        /// <summary>
        /// sigmoid_shaper — ACES.hlsl:381-389. Sigmoid in [0, 1] spanning -2 to +2.
        /// </summary>
        private static float SigmoidShaper(float x)
        {
            float t = max(1f - abs(x / kSigmoidSpan), 0f);
            float y = 1f + FastSign(x) * (1f - t * t);
            return y / kSigmoidSpan;
        }

        /// <summary>
        /// glow_fwd — ACES.hlsl:391-403.
        /// </summary>
        private static float GlowFwd(float ycIn, float glowGainIn, float glowMid)
        {
            if (ycIn <= kGlowLowBand * glowMid)
            {
                return glowGainIn;
            }

            if (ycIn >= kGlowHighBand * glowMid)
            {
                return 0f;
            }

            return glowGainIn * (glowMid / ycIn - kGlowPivotOffset);
        }

        /// <summary>
        /// darkSurround_to_dimSurround — ACES.hlsl:734-745 (XYZ_2_xyY / xyY_2_XYZ at :718-730).
        /// </summary>
        private static float3 DarkSurroundToDimSurround(float3 linearCv)
        {
            float3 xyz = mul(kAp1ToXyz, linearCv);

            // XYZ_2_xyY (ACES.hlsl:718-722).
            float divisor = max(csum(xyz), kXyzSumFloor);
            float2 xy = xyz.xy / divisor;
            float y = xyz.y;

            // Clamp Y to half-float range, then apply the dim-surround power (ACES.hlsl:740-741).
            y = clamp(y, 0f, kHalfMax);
            y = pow(y, kDimSurroundGamma);

            // xyY_2_XYZ (ACES.hlsl:724-729): half3(xyY.xz, 1 - x - y) with .xz scaled by m.
            float m = y / max(xy.y, kChromaYFloor);
            float3 outXyz = new float3(xy.x, y, 1f - xy.x - xy.y);
            outXyz.x = outXyz.x * m;
            outXyz.z = outXyz.z * m;
            return mul(kXyzToAp1, outXyz);
        }
    }
}

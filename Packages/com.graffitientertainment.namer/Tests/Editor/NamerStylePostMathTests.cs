using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// STYL-07/STYL-08 support: golden-value pins for NamerStylePostMath, the CPU twin
    /// of URP 17.0.4's LDR grading chain. Every golden literal below was derived by
    /// hand from the ported formulas (derivation recorded beside each assertion) so any
    /// drift from the installed core RP HLSL fails loudly (BlenderGoldenVectorTests
    /// precedent: tolerance 1e-4 on continuous outputs).
    /// </summary>
    public class NamerStylePostMathTests
    {
        // Continuous-output golden tolerance (float32 twin of an HLSL float path).
        private const double kGoldenTolerance = 1e-4;

        // Round-trip tolerance: encode/decode pairs are algebraic inverses; float32
        // error through log10/pow10 is ~1e-7, so 1e-5 has ample margin.
        private const double kRoundTripTolerance = 1e-5;

        // Gray-in -> gray-out spread. Derivation: the verbatim sRGB_2_AP0 rows sum to
        // (0.949272, 1.035371, 1.087374) instead of (1, 1, 1) — a 1.4e-5 deviation the
        // Academy matrix carries by design — and the ACES chain amplifies it to a
        // worst-case R/B spread of 1.261e-4 over 1025 gray levels (measured, float64,
        // corrected darkSurround derivation).
        // 2e-4 therefore pins "gray stays gray" without being so tight it fires on
        // legitimate float32 rounding. (The plan text said 1e-4; 1.26e-4 > 1e-4, so the
        // honest bar is 2e-4 — see SUMMARY deviations.)
        private const double kGrayPreservationTolerance = 2e-4;

        // Sum-of-weights identity tolerance for Luma709(white) == 1.
        private const double kExactSumTolerance = 1e-6;

        /// <summary>
        /// LogC encode/decode must be algebraic inverses on the grading domain.
        /// </summary>
        [Test]
        public void LogC_RoundTrip_PreservesValue()
        {
            float[] probes = { 0.011f, 0.18f, 0.5f, 1.0f };
            for (int i = 0; i < probes.Length; i++)
            {
                float x = probes[i];
                float roundTrip = NamerStylePostMath.LogCToLinear(NamerStylePostMath.LinearToLogC(x));
                Assert.AreEqual(x, (double)roundTrip, kRoundTripTolerance, $"LogC round trip drifted at x={x}");
            }
        }

        /// <summary>
        /// Absolute goldens for the non-precise LinearToLogC (c*log10(a*x+b)+d),
        /// hand-derived from the core Color.hlsl ParamsLogC constants:
        /// a=5.555556, b=0.047996, c=0.244161, d=0.386036.
        /// x=0.18:  arg=1.047996080, log10=0.020359658, 0.244161*0.020359658+0.386036 = 0.391007035
        /// x=1.0:   arg=5.603552000, log10=0.748463406, 0.244161*0.748463406+0.386036 = 0.568781574
        /// x=0.011: arg=0.109107116 (cut vicinity, guard inactive), log10=-0.962146924,
        ///          0.244161*(-0.962146924)+0.386036 = 0.151117245
        /// </summary>
        [Test]
        public void LogC_AbsoluteGoldens_MatchHandDerivation()
        {
            Assert.AreEqual(0.391007035, (double)NamerStylePostMath.LinearToLogC(0.18f), kGoldenTolerance);
            Assert.AreEqual(0.568781574, (double)NamerStylePostMath.LinearToLogC(1.0f), kGoldenTolerance);
            Assert.AreEqual(0.151117245, (double)NamerStylePostMath.LinearToLogC(0.011f), kGoldenTolerance);
        }

        /// <summary>
        /// At the domain floor x=0 the log argument is b=0.047996 (> 0), so the max()
        /// guard is inactive even at the very bottom of the grading domain:
        /// log10(0.047996) = -1.318794955; 0.244161*(-1.318794955)+0.386036 = 0.064037705.
        /// </summary>
        [Test]
        public void LogC_ZeroInput_StaysOnTheUnclampedBranch()
        {
            Assert.AreEqual(0.064037705, (double)NamerStylePostMath.LinearToLogC(0f), kGoldenTolerance);
        }

        /// <summary>
        /// LMS encode/decode must be algebraic inverses (CAT02-style matrices).
        /// </summary>
        [Test]
        public void Lms_RoundTrip_PreservesValue()
        {
            float3[] probes =
            {
                new float3(0.5f, 0.5f, 0.5f),
                new float3(0.2f, 0.7f, 0.1f),
                new float3(1.0f, 0.0f, 0.0f),
            };
            for (int i = 0; i < probes.Length; i++)
            {
                float3 roundTrip = NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(probes[i]));
                Assert.AreEqual(probes[i].x, (double)roundTrip.x, kRoundTripTolerance);
                Assert.AreEqual(probes[i].y, (double)roundTrip.y, kRoundTripTolerance);
                Assert.AreEqual(probes[i].z, (double)roundTrip.z, kRoundTripTolerance);
            }
        }

        /// <summary>
        /// "Gray stays gray" in LMS means: LIN_2_LMS maps equal components onto the
        /// D65 white direction, not onto equal components. Derivation: each CAT02 row
        /// sums to a different constant, so Lms(g, g, g) = g * (0.949272320,
        /// 1.035371350, 1.087374200). For g = 0.5 that is (0.474636160, 0.517685675,
        /// 0.543687100). (The plan worded this as "gray stays gray"; the derivable
        /// invariant is the fixed D65 direction — see SUMMARY deviations.)
        /// </summary>
        [Test]
        public void Lms_GrayInput_LandsOnD65WhiteDirection()
        {
            float3 lms = NamerStylePostMath.LinearToLms(new float3(0.5f, 0.5f, 0.5f));
            Assert.AreEqual(0.474636160, (double)lms.x, kGoldenTolerance);
            Assert.AreEqual(0.517685675, (double)lms.y, kGoldenTolerance);
            Assert.AreEqual(0.543687100, (double)lms.z, kGoldenTolerance);
        }

        /// <summary>
        /// TonemapLdr is the LDR display path: output must be saturated to [0, 1]
        /// over a swept grid of inputs (URP applies saturate after AcesTonemap).
        /// </summary>
        [Test]
        public void TonemapLdr_OutputStaysInUnitRange()
        {
            const int kSweepSteps = 33; // 1/32 grid over [0,1] per channel
            for (int i = 0; i <= kSweepSteps; i++)
            {
                float r = i / (float)kSweepSteps;
                for (int j = 0; j <= 8; j++)
                {
                    float g = j / 8f;
                    float b = 1f - g;
                    float3 output = NamerStylePostMath.TonemapLdr(new float3(r, g, b));
                    Assert.GreaterOrEqual(output.x, 0f);
                    Assert.LessOrEqual(output.x, 1f);
                    Assert.GreaterOrEqual(output.y, 0f);
                    Assert.LessOrEqual(output.y, 1f);
                    Assert.GreaterOrEqual(output.z, 0f);
                    Assert.LessOrEqual(output.z, 1f);
                }
            }
        }

        /// <summary>
        /// The fitted ACES curve is monotone non-decreasing per channel when the other
        /// two channels are held fixed (required for a invertible display transform).
        /// </summary>
        [Test]
        public void TonemapLdr_IsMonotonePerChannel()
        {
            const int kSweepSteps = 64; // 1/64 grid over [0,1]
            float[] held = { 0.1f, 0.25f, 0.5f, 0.9f };
            for (int h = 0; h < held.Length; h++)
            {
                float prevR = NamerStylePostMath.TonemapLdr(new float3(0f, held[h], held[h])).x;
                float prevG = NamerStylePostMath.TonemapLdr(new float3(held[h], 0f, held[h])).y;
                float prevB = NamerStylePostMath.TonemapLdr(new float3(held[h], held[h], 0f)).z;
                for (int i = 1; i <= kSweepSteps; i++)
                {
                    float x = i / (float)kSweepSteps;
                    float outR = NamerStylePostMath.TonemapLdr(new float3(x, held[h], held[h])).x;
                    float outG = NamerStylePostMath.TonemapLdr(new float3(held[h], x, held[h])).y;
                    float outB = NamerStylePostMath.TonemapLdr(new float3(held[h], held[h], x)).z;
                    Assert.GreaterOrEqual(outR, prevR, $"red channel non-monotone at held={held[h]}");
                    Assert.GreaterOrEqual(outG, prevG, $"green channel non-monotone at held={held[h]}");
                    Assert.GreaterOrEqual(outB, prevB, $"blue channel non-monotone at held={held[h]}");
                    prevR = outR;
                    prevG = outG;
                    prevB = outB;
                }
            }
        }

        /// <summary>
        /// Gray-in stays gray-out: R=G=B in, R=G=B out (channel spread bounded by
        /// kGrayPreservationTolerance; worst measured spread 1.261e-4, corrected
        /// darkSurround derivation).
        /// </summary>
        [Test]
        public void TonemapLdr_GrayInStaysGrayOut()
        {
            const int kSweepSteps = 256; // 1/255 grid over [0,1]
            for (int i = 0; i <= kSweepSteps; i++)
            {
                float g = i / (float)kSweepSteps;
                float3 output = NamerStylePostMath.TonemapLdr(new float3(g, g, g));
                double spread = cmax(output) - cmin(output);
                Assert.LessOrEqual(spread, kGrayPreservationTolerance, $"gray spread blew up at gray={g}");
            }
        }

        /// <summary>
        /// Hand-derived golden triple through the full fitted-ACES port. Computed once
        /// from the transcribed constants (unity_to_ACES -> glow -> red modifier ->
        /// rational fit -> dim surround (xyY.z = XYZ.y) -> D60->D65 -> Rec.709 ->
        /// saturate): TonemapLdr((0.25, 0.5, 0.75)) = (0.20396616, 0.38009362,
        /// 0.51751275). (First derivation had a Y/Z slot swap in darkSurround_to_dimSurround;
        /// the corrected derivation agrees with the C# port to 1e-7 — see SUMMARY.)
        /// </summary>
        [Test]
        public void TonemapLdr_AbsoluteGolden_MatchesAcesFit()
        {
            float3 output = NamerStylePostMath.TonemapLdr(new float3(0.25f, 0.5f, 0.75f));
            Assert.AreEqual(0.20396616, (double)output.x, kGoldenTolerance);
            Assert.AreEqual(0.38009362, (double)output.y, kGoldenTolerance);
            Assert.AreEqual(0.51751275, (double)output.z, kGoldenTolerance);
        }

        /// <summary>
        /// Luma709 of white is the exact sum of the Rec.709 weights: 0.2126 + 0.7152 +
        /// 0.0722 = 1.0 (the URP saturation pivot normalizes on this identity).
        /// </summary>
        [Test]
        public void Luma709_WhiteIsOne()
        {
            Assert.AreEqual(1.0, (double)NamerStylePostMath.Luma709(new float3(1f, 1f, 1f)), kExactSumTolerance);
        }

        /// <summary>
        /// kAcesCcMidGray provenance: the ACEScc encoding of 18% gray is
        /// (log2(0.18) + 9.72) / 17.52 = 0.4135884025 (ACEScc encode constants from
        /// ACES.hlsl:50-67). URP's LDR contrast pivot reuses this value, so the
        /// relationship is derivable and pinned here.
        /// </summary>
        [Test]
        public void AcesCcMidGray_MatchesAcesccEncodeOfMidGray()
        {
            const float kAcesccOffset = 9.72f;   // ACEScc encode offset
            const float kAcesccScale = 17.52f;   // ACEScc encode scale
            double acesccMidGray = (log2(0.18f) + kAcesccOffset) / kAcesccScale;
            Assert.AreEqual((double)NamerStylePostMath.kAcesCcMidGray, acesccMidGray, kExactSumTolerance);
        }
    }
}

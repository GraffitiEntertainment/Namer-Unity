using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// STYL-13/STYL-14 groundwork: golden and round-trip pins for NamerStyleGradeMath,
    /// the CPU twin of the pinned URP 17.0.4 LDR grading chain and its inverse
    /// (the de-grade operator). Every golden literal below was measured from the
    /// twin itself running the real NamerStylePostMath/TonemapLdr forward code
    /// (float32, deterministic; derivation recorded beside each assertion) so any
    /// drift from the pinned chain or silent weakening of an inverse fails loudly
    /// (NamerStylePostMathTests golden-tolerance precedent).
    /// Round-trip floor measurements (05.3-01 harness, probe grid below): saturation
    /// 6.0e-8, contrast 1.5e-7, white balance 1.3e-5, tonemap 1.4e-4 (worst at
    /// EV +/-5 near the shoulder — the documented best-effort class edge), whole
    /// chain 2.2e-5 at worst-case knobs. kRoundTripTolerance = 1e-3 therefore has
    /// 7x headroom over the largest honest stage residual.
    /// </summary>
    public class NamerStyleGradeMathTests
    {
        // Continuous-output golden tolerance (float32 twin of an HLSL float path —
        // NamerStylePostMathTests precedent).
        private const double kGoldenTolerance = 1e-4;

        // Round-trip tolerance over unclipped probes; derivation in the class header
        // (largest measured stage residual 1.4e-4, chain 2.2e-5 — 1e-3 keeps 7x
        // headroom without blessing real regressions, which are 3+ orders larger).
        private const double kRoundTripTolerance = 1e-3;

        // Curve round-trip tolerance: grid-node-linear fixtures evaluate exactly
        // piecewise-linear between the 9 uniform grid keys, and MasterInverse
        // interpolates the same grid linearly, so the inverse is exact up to float
        // arithmetic (~1e-7 measured shape; 1e-5 leaves margin without hiding drift).
        private const double kCurveRoundTripTolerance = 1e-5;

        // Exact-pin tolerance for structurally derived values (0.18 normalization,
        // 0.125 scale-invariant chroma, hand-computed master-inverse times):
        // float32 exact arithmetic, 1e-6 is generous.
        private const double kExactPinTolerance = 1e-6;

        // Probe edge margin: outputs within 1/255 of the [0,1] rails are the D-07
        // "clipping" class — non-invertible by construction, excluded from
        // round-trip probes (kNamerPostClipEpsilon scale, NamerPostPreview.hlsl).
        private const float kEdgeMargin = 1f / 255f;

        // Curve-fixture node arrays below use the 9 uniform grid keys — they MUST
        // match NamerStylePostFit.cs:196 kCurveKeyCount because MasterInverse maps
        // grid indices to times through it (the InverseCurves contract).

        // Neutral-fixture golden anchors, measured from BuildNeutralSourceStats()
        // through the shipped accumulator (float32, deterministic):
        private const double kNeutralRawMeanLuma = 0.18; // the normalization contract
        private const double kNeutralFixtureMeanChroma = 0.125; // exactly half of kNeutralCanonicalChroma (scale-invariant metric, half achromatic rows)
        private const double kNeutralStatsMeanLuma = 0.118498608; // post-tonemap accumulator mean
        private const double kNeutralStatsMeanChroma = 0.16477482; // post-tonemap accumulator chroma (ACES toe lifts dark-pixel chroma)
        private const double kNeutralLogCSpread = 0.398133785; // LinearToLogC(p90) - LinearToLogC(p10), WeightedPercentile mirror below

        // WeightedPercentile mirror constants (verbatim copy of the private
        // NamerStylePostFit.cs:477-510 implementation, test-sanctioned by the plan
        // so the spread golden is computed by the same discipline the fit uses).
        private const float kPercentile10 = 0.1f;
        private const float kPercentile90 = 0.9f;
        private const float kBinCenterOffset = 0.5f;

        // Chroma-metric denominator floor mirroring the accumulator's private
        // kStatEpsilon (NamerStylePostFit.cs AccumulateLinear); it never binds for
        // fixture pixels (darkest channel max ≈ 0.013).
        private const float kChromaDenominatorFloor = 1e-6f;

        /// <summary>
        /// Contrast twin goldens: z = contrast/100 + 1 around the LogC mid-gray pivot.
        /// x=0.18 (== the LogCC mid-gray anchor domain): z=2 doubles the LogC distance
        /// from 0.4135884; measured through the real LinearToLogC/LogCToLinear pair.
        /// </summary>
        [Test]
        public void ForwardContrast_Goldens()
        {
            float3 gray = NamerStyleGradeMath.ForwardContrast(new float3(0.18f, 0.18f, 0.18f), 100f);
            Assert.AreEqual(0.143817514, (double)gray.x, kGoldenTolerance, "gray 0.18 at +100 drifted");
            Assert.AreEqual((double)gray.x, (double)gray.y, kExactPinTolerance, "gray contrast must stay gray (g)");
            Assert.AreEqual((double)gray.x, (double)gray.z, kExactPinTolerance, "gray contrast must stay gray (b)");

            float3 probe = NamerStyleGradeMath.ForwardContrast(new float3(0.25f, 0.5f, 0.75f), 100f);
            Assert.AreEqual(0.277957529, (double)probe.x, kGoldenTolerance, "contrast +100 r drifted");
            Assert.AreEqual(1.099775, (double)probe.y, kGoldenTolerance, "contrast +100 g drifted");
            Assert.AreEqual(2.45713449, (double)probe.z, kGoldenTolerance, "contrast +100 b drifted (mid-chain HDR is expected — only the chain's final saturate clamps)");

            float3 soft = NamerStyleGradeMath.ForwardContrast(new float3(0.25f, 0.5f, 0.75f), -50f);
            Assert.AreEqual(0.237061232, (double)soft.x, kGoldenTolerance, "contrast -50 r drifted");
            Assert.AreEqual(0.335919827, (double)soft.y, kGoldenTolerance, "contrast -50 g drifted");
            Assert.AreEqual(0.412161529, (double)soft.z, kGoldenTolerance, "contrast -50 b drifted");
        }

        /// <summary>
        /// Contrast -100 collapses z to 0: every input maps to the pivot decoded back
        /// to linear — LogCToLinear(kAcesCcMidGray), independent of the input.
        /// </summary>
        [Test]
        public void ForwardContrast_MinusHundredCollapsesToPivotLinear()
        {
            float collapse = NamerStylePostMath.LogCToLinear(NamerStylePostMath.kAcesCcMidGray);
            float3 probe = NamerStyleGradeMath.ForwardContrast(new float3(0.25f, 0.5f, 0.75f), -100f);
            Assert.AreEqual((double)collapse, (double)probe.x, kExactPinTolerance, "collapse constant (r) must be LogCToLinear(pivot)");
            Assert.AreEqual((double)collapse, (double)probe.y, kExactPinTolerance, "collapse constant (g)");
            Assert.AreEqual((double)collapse, (double)probe.z, kExactPinTolerance, "collapse constant (b)");
        }

        /// <summary>
        /// Saturation twin goldens: s = saturation/100 + 1 around the input Rec.709
        /// luma pivot. Luma709(0.8,0.4,0.4) = 0.4848, s = 2:
        /// r = 0.4848 + 2*(0.8-0.4848) = 1.11496, g = b = 0.4848 + 2*(0.4-0.4848) = 0.31496.
        /// </summary>
        [Test]
        public void ForwardSaturation_GoldenAndIdentity()
        {
            float3 boosted = NamerStyleGradeMath.ForwardSaturation(new float3(0.8f, 0.4f, 0.4f), 100f);
            Assert.AreEqual(1.11496, (double)boosted.x, kGoldenTolerance, "saturation +100 r drifted");
            Assert.AreEqual(0.31496, (double)boosted.y, kGoldenTolerance, "saturation +100 g drifted");
            Assert.AreEqual(0.31496, (double)boosted.z, kGoldenTolerance, "saturation +100 b drifted");

            float3 identity = NamerStyleGradeMath.ForwardSaturation(new float3(0.8f, 0.4f, 0.4f), 0f);
            Assert.AreEqual(0.8, (double)identity.x, kExactPinTolerance, "saturation 0 must be the identity (r)");
            Assert.AreEqual(0.4, (double)identity.y, kExactPinTolerance, "saturation 0 must be the identity (g)");
            Assert.AreEqual(0.4, (double)identity.z, kExactPinTolerance, "saturation 0 must be the identity (b)");
        }

        /// <summary>
        /// Exposure+tonemap goldens: at EV 0 the stage IS the pinned TonemapLdr golden
        /// (NamerStylePostMathTests pin); EV +/-1 on mid-gray measured through the
        /// real ACES chain.
        /// </summary>
        [Test]
        public void ForwardExposureTonemap_Goldens()
        {
            float3 neutral = NamerStyleGradeMath.ForwardExposureTonemap(new float3(0.25f, 0.5f, 0.75f), 0f);
            Assert.AreEqual(0.203966051, (double)neutral.x, kGoldenTolerance, "EV 0 must equal the pinned TonemapLdr golden (r)");
            Assert.AreEqual(0.380093455, (double)neutral.y, kGoldenTolerance, "EV 0 must equal the pinned TonemapLdr golden (g)");
            Assert.AreEqual(0.5175128, (double)neutral.z, kGoldenTolerance, "EV 0 must equal the pinned TonemapLdr golden (b)");

            float3 up = NamerStyleGradeMath.ForwardExposureTonemap(new float3(0.18f, 0.18f, 0.18f), 1f);
            Assert.AreEqual(0.2721179, (double)up.x, kGoldenTolerance, "EV +1 on mid-gray drifted");

            float3 down = NamerStyleGradeMath.ForwardExposureTonemap(new float3(0.18f, 0.18f, 0.18f), -1f);
            Assert.AreEqual(0.03819923, (double)down.x, kGoldenTolerance, "EV -1 on mid-gray drifted");
        }

        /// <summary>
        /// White-balance twin goldens through the engine ColorBalanceToLMSCoeffs map
        /// (asymmetry included: (50,50) warm-tint vs (-50,-50) cool — the second
        /// legitimately drives the red channel NEGATIVE mid-chain; only the chain's
        /// final saturate clamps, the stage itself must not).
        /// </summary>
        [Test]
        public void ForwardWhiteBalance_GoldensAndNeutralKnobIdentity()
        {
            float3 warm = NamerStyleGradeMath.ForwardWhiteBalance(new float3(0.25f, 0.5f, 0.75f), 50f, 50f);
            Assert.AreEqual(0.437285632, (double)warm.x, kGoldenTolerance, "WB (50,50) r drifted");
            Assert.AreEqual(0.451028883, (double)warm.y, kGoldenTolerance, "WB (50,50) g drifted");
            Assert.AreEqual(0.6564369, (double)warm.z, kGoldenTolerance, "WB (50,50) b drifted");

            float3 cool = NamerStyleGradeMath.ForwardWhiteBalance(new float3(0.25f, 0.5f, 0.75f), -50f, -50f);
            Assert.AreEqual(-0.0336533226, (double)cool.x, kGoldenTolerance, "WB (-50,-50) r drifted (negative mid-chain value is correct)");
            Assert.AreEqual(0.5873672, (double)cool.y, kGoldenTolerance, "WB (-50,-50) g drifted");
            Assert.AreEqual(1.11237288, (double)cool.z, kGoldenTolerance, "WB (-50,-50) b drifted");

            // Neutral knobs: the ENGINE map itself is not exactly unit at (0,0) —
            // measured ColorBalanceToLMSCoeffs(0,0) = (1.00000036, 0.9999993,
            // 1.00000346), and through the LMS round trip the deviation reaches
            // 6.0e-6 on this probe. The twin mirrors the shader's own coefficients,
            // so the identity bar is the golden scale, not exact equality (real
            // knob-map drift is >= 1e-2 and still fails loudly).
            float3 identity = NamerStyleGradeMath.ForwardWhiteBalance(new float3(0.25f, 0.5f, 0.75f), 0f, 0f);
            Assert.AreEqual(0.25, (double)identity.x, kGoldenTolerance, "WB (0,0) must be the identity (r)");
            Assert.AreEqual(0.5, (double)identity.y, kGoldenTolerance, "WB (0,0) must be the identity (g)");
            Assert.AreEqual(0.75, (double)identity.z, kGoldenTolerance, "WB (0,0) must be the identity (b)");
        }

        /// <summary>
        /// The curve stage's shipped identity law: with all-null (identity diagonal)
        /// curves the stage still applies BOTH 1/256 half-pixel pre-offsets, so the
        /// output is exactly c + 2*kCurveHalfPixel (NamerPostPreview.hlsl:94-108 —
        /// the offsets are unconditional in the shader).
        /// </summary>
        [Test]
        public void ForwardCurves_IdentityCurvesShiftByTwoHalfPixels()
        {
            float3 probe = new float3(0.25f, 0.5f, 0.75f);
            float3 shifted = NamerStyleGradeMath.ForwardCurves(probe, null, null, null, null);
            double expected = 2.0 * NamerStyleGradeMath.kCurveHalfPixel;
            Assert.AreEqual(probe.x + expected, (double)shifted.x, kExactPinTolerance, "identity-curve shift (r)");
            Assert.AreEqual(probe.y + expected, (double)shifted.y, kExactPinTolerance, "identity-curve shift (g)");
            Assert.AreEqual(probe.z + expected, (double)shifted.z, kExactPinTolerance, "identity-curve shift (b)");
        }

        /// <summary>
        /// STYL-14 clamp-effective law: out-of-cap params must apply (and de-grade)
        /// identically to their capped values — the render only ever applies the
        /// clamped knobs, so the twin must too. Exact equality: both paths clamp to
        /// the same floats and then run identical float operations.
        /// </summary>
        [Test]
        public void ForwardGrade_AndDeGrade_ClampOutOfCapParamsToEffectiveKnobs()
        {
            NamerStylePostFitParams overCap = MakeParams(200f, -200f, 40f, 300f, -300f);
            NamerStylePostFitParams capped = MakeParams(100f, -100f, 10f, 100f, -100f);
            float3 x = new float3(0.18f, 0.45f, 0.7f);

            float3 forwardOver = NamerStyleGradeMath.ForwardGrade(x, overCap, null, null, null);
            float3 forwardCapped = NamerStyleGradeMath.ForwardGrade(x, capped, null, null, null);
            Assert.AreEqual(forwardCapped.x, forwardOver.x, "ForwardGrade must clamp T/Tint to +/-100, EV to +/-10, percent knobs to +/-100 (r)");
            Assert.AreEqual(forwardCapped.y, forwardOver.y, "clamp-effective equality (g)");
            Assert.AreEqual(forwardCapped.z, forwardOver.z, "clamp-effective equality (b)");

            float3 deOver = NamerStyleGradeMath.DeGrade(forwardCapped, overCap, null, null, null);
            float3 deCapped = NamerStyleGradeMath.DeGrade(forwardCapped, capped, null, null, null);
            Assert.AreEqual(deCapped.x, deOver.x, "DeGrade must clamp to the same effective knobs (r)");
            Assert.AreEqual(deCapped.y, deOver.y, "DeGrade clamp-effective equality (g)");
            Assert.AreEqual(deCapped.z, deOver.z, "DeGrade clamp-effective equality (b)");
        }

        /// <summary>
        /// Saturation inverse round-trips the +100 boost in both directions on
        /// unclipped probes (the map preserves its input luma exactly, so the
        /// fixed-point solve converges in one step; s &lt;= 0 is the non-invertible
        /// luma collapse and passes through — covered by the collapse pin below).
        /// </summary>
        [Test]
        public void SaturationInverse_RoundTripBothDirections()
        {
            ForEachUnclippedProbe((x, y) =>
            {
                float3 back = NamerStyleGradeMath.InverseSaturation(y, 100f);
                AssertMaxAbs(x, back, x, kRoundTripTolerance, $"saturation inverse drifted at x={x}");
                float3 forth = NamerStyleGradeMath.ForwardSaturation(NamerStyleGradeMath.InverseSaturation(x, 100f), 100f);
                AssertMaxAbs(x, forth, x, kRoundTripTolerance, $"saturation forward-of-inverse drifted at x={x}");
            });
        }

        /// <summary>
        /// Contrast inverse round-trips the +100 boost in both directions on
        /// unclipped probes.
        /// </summary>
        [Test]
        public void ContrastInverse_RoundTripBothDirections()
        {
            ForEachUnclippedProbe((x, y) =>
            {
                float3 back = NamerStyleGradeMath.InverseContrast(y, 100f);
                AssertMaxAbs(x, back, x, kRoundTripTolerance, $"contrast inverse drifted at x={x}");
                float3 forth = NamerStyleGradeMath.ForwardContrast(NamerStyleGradeMath.InverseContrast(x, 100f), 100f);
                AssertMaxAbs(x, forth, x, kRoundTripTolerance, $"contrast forward-of-inverse drifted at x={x}");
            }, x => NamerStyleGradeMath.ForwardContrast(x, 100f));
        }

        /// <summary>
        /// White-balance inverse (LMS division by the forward coefficients — never a
        /// knob negation) round-trips the worst-case knob corners on unclipped probes.
        /// </summary>
        [Test]
        public void WhiteBalanceInverse_RoundTripInverseOfForward()
        {
            float[][] corners = { new[] { 50f, 50f }, new[] { -50f, -50f }, new[] { 50f, -50f }, new[] { -50f, 50f } };
            for (int corner = 0; corner < corners.Length; corner++)
            {
                float temperature = corners[corner][0];
                float tint = corners[corner][1];
                ForEachUnclippedProbe((x, y) =>
                {
                    float3 back = NamerStyleGradeMath.InverseWhiteBalance(y, temperature, tint);
                    AssertMaxAbs(x, back, x, kRoundTripTolerance, $"WB inverse drifted at T={temperature} tint={tint} x={x}");
                }, x => NamerStyleGradeMath.ForwardWhiteBalance(x, temperature, tint));
            }
        }

        /// <summary>
        /// Tonemap inverse round-trips exposure+tonemap at moderate EVs on unclipped
        /// probes. Worst measured residual 1.4e-4 sits at EV +/-5 near the tonemap
        /// shoulder (flat-Jacobian best-effort class); kRoundTripTolerance covers it
        /// while staying far below real regressions (the radial-only defect this
        /// refinement fixed measured 0.74).
        /// </summary>
        [Test]
        public void TonemapInverse_RoundTripInverseOfForward()
        {
            float[] exposures = { 0f, 2f, -2f, 5f, -5f };
            for (int i = 0; i < exposures.Length; i++)
            {
                float ev = exposures[i];
                ForEachUnclippedProbe((x, y) =>
                {
                    float3 back = NamerStyleGradeMath.InverseExposureTonemap(y, ev);
                    AssertMaxAbs(x, back, x, kRoundTripTolerance, $"tonemap inverse drifted at EV={ev} x={x}");
                }, x => NamerStyleGradeMath.ForwardExposureTonemap(x, ev));
            }
        }

        /// <summary>
        /// A fully clipped white input must invert to a finite value (no NaN/Inf) —
        /// the documented best-effort class; the CALLER masks such texels (D-07).
        /// Finiteness only, never a value pin.
        /// </summary>
        [Test]
        public void TonemapInverse_ClippedWhiteStaysFinite()
        {
            float3 result = NamerStyleGradeMath.InverseExposureTonemap(new float3(1f, 1f, 1f), 0f);
            Assert.IsTrue(!float.IsNaN(result.x) && !float.IsInfinity(result.x), "clipped white must not invert to NaN/Inf (r)");
            Assert.IsTrue(!float.IsNaN(result.y) && !float.IsInfinity(result.y), "clipped white must not invert to NaN/Inf (g)");
            Assert.IsTrue(!float.IsNaN(result.z) && !float.IsInfinity(result.z), "clipped white must not invert to NaN/Inf (b)");
        }

        /// <summary>
        /// Curve inverse round-trips grid-node-linear fixtures (kinks exactly at the
        /// 9 uniform grid keys, so curve evaluation and MasterInverse's grid
        /// interpolation are the SAME piecewise-linear function — exact up to float).
        /// Also pins the flat-plateau semantics: a value on the rising segment
        /// inverts to its hand-computed grid time; a plateau value inverts to the
        /// flat region's start (MasterInverse law, NamerStylePostFit.cs:596-619).
        /// </summary>
        [Test]
        public void CurvesInverse_RoundTripAndFlatStartPin()
        {
            // Master: flat 0 on nodes 0..3, linear rise to 1 on node 8.
            float[] masterNodes = { 0f, 0f, 0f, 0f, 0.2f, 0.4f, 0.6f, 0.8f, 1f };
            AnimationCurve master = LinearCurveFromNodeValues(masterNodes);
            AnimationCurve diagonal = LinearCurveFromNodeValues(new[] { 0f, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1f });

            // Probes whose (x + half-pixel) master input stays on the rising segment.
            float[] probes = { 0.55f, 0.65f, 0.75f, 0.85f, 0.95f };
            for (int i = 0; i < probes.Length; i++)
            {
                for (int j = 0; j < probes.Length; j++)
                {
                    float3 x = new float3(probes[i], probes[j], probes[(i + j) % probes.Length]);
                    float3 y = NamerStyleGradeMath.ForwardCurves(x, master, diagonal, diagonal, diagonal);
                    float3 back = NamerStyleGradeMath.InverseCurves(y, master, diagonal, diagonal, diagonal);
                    AssertMaxAbs(x, back, x, kCurveRoundTripTolerance, $"curve inverse drifted at x={x}");
                    float3 forth = NamerStyleGradeMath.ForwardCurves(back, master, diagonal, diagonal, diagonal);
                    AssertMaxAbs(y, forth, y, kCurveRoundTripTolerance, $"curve forward-of-inverse drifted at x={x}");
                }
            }

            // Hand-computed mid-rise pin: value 0.1 -> channel diagonal pass-through,
            // master input 0.1 - 1/256, rising segment from node 3 (t=0.375, v=0) with
            // slope 0.2 per 1/8 grid step, then the post-master half-pixel subtract:
            // t = 0.375 + ((0.1 - 1/256)/0.2)/8 - 1/256 = 0.43115234375.
            float3 mid = NamerStyleGradeMath.InverseCurves(new float3(0.1f, 0.1f, 0.1f), master, null, null, null);
            Assert.AreEqual(0.43115234375, (double)mid.x, kExactPinTolerance, "mid-rise master inverse time");

            // Flat-plateau pin: value 0 is on the plateau; the inverse resolves to
            // the flat region's start (grid time 0) minus the half-pixel offset.
            float3 flat = NamerStyleGradeMath.InverseCurves(new float3(0f, 0f, 0f), master, null, null, null);
            Assert.AreEqual(-NamerStyleGradeMath.kCurveHalfPixel, (double)flat.x, kExactPinTolerance, "plateau value must invert to the flat region start");
        }

        /// <summary>
        /// The composed de-grade round-trips the whole chain (identity/null curves)
        /// in BOTH directions at strong and worst-case clamped knobs, over probes
        /// whose PRE-saturate chain value is unclipped (mid-chain clipping by the
        /// final saturate is the D-07 non-invertible set — excluded by mirroring the
        /// chain stage order in ComposeForwardPreSaturate below, the same filter the
        /// 05.3-01 harness used: 71 probes survive at the strong set, 8 at worst).
        /// Measured maxima 1.2e-5 (strong) / 2.2e-5 (worst) direction 1.
        /// </summary>
        [Test]
        public void DeGrade_WholeChainRoundTripBothDirectionsAtStrongKnobs()
        {
            NamerStylePostFitParams strong = MakeParams(30f, -20f, -2f, 60f, 50f);
            AssertChainRoundTrip(strong);
        }

        /// <summary>Worst-case clamped knobs (all four scalars at their caps).</summary>
        [Test]
        public void DeGrade_WholeChainRoundTripBothDirectionsAtWorstCaseKnobs()
        {
            NamerStylePostFitParams worst = MakeParams(50f, -50f, -1f, 100f, 100f);
            AssertChainRoundTrip(worst);
        }

        /// <summary>
        /// Neutral-source golden anchors: the fixture's raw luma mean is EXACTLY
        /// kNeutralMidGrayLinear (the normalization contract that makes the shipped
        /// EV bisection absolute), its raw chroma mean is exactly half the canonical
        /// chroma (scale-invariant metric, half achromatic rows), and the accumulated
        /// anchors (post-tonemap mean luma/chroma, LogC p90-p10 spread) pin the
        /// fixture against silent drift. Spread is computed with the verbatim
        /// WeightedPercentile mirror below (the fit's own discipline).
        /// </summary>
        [Test]
        public void NeutralSource_GoldenAnchors()
        {
            NamerStylePostStats stats = NamerStyleGradeMath.BuildNeutralSourceStats();
            Assert.Greater(stats.TotalWeight, 0.0, "neutral source must accumulate mass");
            int gridPixels = NamerStyleGradeMath.kNeutralGridResolution * NamerStyleGradeMath.kNeutralGridResolution;
            Assert.AreEqual((double)gridPixels, stats.TotalWeight, kExactPinTolerance, "16x16 fixture must accumulate exactly 256 unit weights");

            Color[] pixels = NamerStyleGradeMath.BuildNeutralSourcePixels();
            Assert.AreEqual(gridPixels, pixels.Length, "fixture must be 16x16");

            double lumaSum = 0.0;
            double chromaSum = 0.0;
            for (int i = 0; i < pixels.Length; i++)
            {
                float3 v = new float3(pixels[i].r, pixels[i].g, pixels[i].b);
                lumaSum += NamerStylePostMath.Luma709(v);
                chromaSum += (cmax(v) - cmin(v)) / max(cmax(v), kChromaDenominatorFloor);
            }

            Assert.AreEqual(kNeutralRawMeanLuma, lumaSum / pixels.Length, kExactPinTolerance, "raw luma mean must be exactly kNeutralMidGrayLinear");
            Assert.AreEqual(kNeutralFixtureMeanChroma, chromaSum / pixels.Length, kExactPinTolerance, "raw chroma mean must be exactly half the canonical chroma");
            float3 rawMean = new float3(stats.RawMeanLinear.r, stats.RawMeanLinear.g, stats.RawMeanLinear.b);
            Assert.AreEqual(kNeutralRawMeanLuma, (double)NamerStylePostMath.Luma709(rawMean), kExactPinTolerance, "accumulator raw mean luma must carry the normalization");
            Assert.AreEqual(kNeutralStatsMeanLuma, (double)stats.MeanLuma, kGoldenTolerance, "post-tonemap mean luma golden");
            Assert.AreEqual(kNeutralStatsMeanChroma, (double)stats.MeanChroma, kGoldenTolerance, "post-tonemap mean chroma golden");

            float total = (float)stats.TotalWeight;
            float p10 = WeightedPercentileMirror(stats.LumaHistogram, total, kPercentile10);
            float p90 = WeightedPercentileMirror(stats.LumaHistogram, total, kPercentile90);
            double spread = NamerStylePostMath.LinearToLogC(p90) - NamerStylePostMath.LinearToLogC(p10);
            Assert.AreEqual(kNeutralLogCSpread, spread, kGoldenTolerance, "LogC p90-p10 spread golden");
        }

        /// <summary>
        /// Determinism law: two builds of the neutral stats must be element-wise
        /// identical (fixed order, fixed grids, no RNG).
        /// </summary>
        [Test]
        public void NeutralSource_RunTwiceHistogramsIdentical()
        {
            NamerStylePostStats first = NamerStyleGradeMath.BuildNeutralSourceStats();
            NamerStylePostStats second = NamerStyleGradeMath.BuildNeutralSourceStats();
            float[] a = first.LumaHistogram;
            float[] b = second.LumaHistogram;
            Assert.AreEqual(a.Length, b.Length, "histogram lengths");
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i], b[i], $"neutral stats must be run-twice identical (bin {i})");
            }
        }

        // ================= helpers =================

        /// <summary>Fit params with null curves (identity diagonals).</summary>
        private static NamerStylePostFitParams MakeParams(float temperature, float tint, float exposureEv, float contrast, float saturation)
        {
            return new NamerStylePostFitParams
            {
                Temperature = temperature,
                Tint = tint,
                PostExposureEv = exposureEv,
                Contrast = contrast,
                Saturation = saturation,
                MasterKeys = null,
            };
        }

        /// <summary>Probe luma levels spanning the display range.</summary>
        private static readonly float[] kProbeLumas = { 0.05f, 0.175f, 0.3f, 0.5f, 0.7f, 0.9f };

        /// <summary>Probe chroma levels from achromatic to a saturated albedo.</summary>
        private static readonly float[] kProbeChromas = { 0f, 0.15f, 0.3f, 0.5f };

        /// <summary>Hue wheel stops (primary + secondary).</summary>
        private static readonly float[] kProbeHues = { 0f, 1f / 6f, 2f / 6f, 3f / 6f, 4f / 6f, 5f / 6f };

        /// <summary>
        /// Walks the probe grid, applies stage (or saturation +100 by default),
        /// skips probes whose stage output is in the D-07 edge class, and invokes
        /// assertOn with (probe, stageOutput).
        /// </summary>
        private static void ForEachUnclippedProbe(Action<float3, float3> assertOn, Func<float3, float3> stage = null)
        {
            if (stage == null)
            {
                stage = x => NamerStyleGradeMath.ForwardSaturation(x, 100f);
            }

            for (int l = 0; l < kProbeLumas.Length; l++)
            {
                for (int c = 0; c < kProbeChromas.Length; c++)
                {
                    for (int h = 0; h < kProbeHues.Length; h++)
                    {
                        float3 x = MixProbe(kProbeLumas[l], kProbeChromas[c], kProbeHues[h]);
                        float3 y = stage(x);
                        if (cmin(y) >= kEdgeMargin && cmax(y) <= 1f - kEdgeMargin)
                        {
                            assertOn(x, y);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Asserts the whole-chain round trip in both directions over probes whose
        /// pre-saturate chain value is unclipped.
        /// </summary>
        private static void AssertChainRoundTrip(NamerStylePostFitParams p)
        {
            int probes = 0;
            for (int l = 0; l < kProbeLumas.Length; l++)
            {
                for (int c = 0; c < kProbeChromas.Length; c++)
                {
                    for (int h = 0; h < kProbeHues.Length; h++)
                    {
                        float3 x = MixProbe(kProbeLumas[l], kProbeChromas[c], kProbeHues[h]);
                        float3 preSaturate = ComposeForwardPreSaturate(x, p);
                        if (cmin(preSaturate) < kEdgeMargin || cmax(preSaturate) > 1f - kEdgeMargin)
                        {
                            continue;
                        }

                        probes++;
                        float3 y = NamerStyleGradeMath.ForwardGrade(x, p, null, null, null);
                        float3 back = NamerStyleGradeMath.DeGrade(y, p, null, null, null);
                        AssertMaxAbs(x, back, x, kRoundTripTolerance, $"DeGrade(ForwardGrade(x)) drifted at x={x}");
                        float3 forth = NamerStyleGradeMath.ForwardGrade(back, p, null, null, null);
                        AssertMaxAbs(y, forth, y, kRoundTripTolerance, $"ForwardGrade(DeGrade(y)) drifted at y={y}");
                    }
                }
            }

            Assert.Greater(probes, 0, "probe filter excluded every probe — filter or chain regressed");
        }

        /// <summary>
        /// TEST-SIDE mirror of the pinned forward stage order (exposure*tonemap ->
        /// white balance -> contrast -> saturation -> curves) WITHOUT the final
        /// saturate, used only to exclude the D-07 mid-chain-clipped probe set from
        /// the round-trip assertions (the same filter the 05.3-01 harness used; the
        /// final saturate is where clipping destroys information).
        /// </summary>
        private static float3 ComposeForwardPreSaturate(float3 x, NamerStylePostFitParams p)
        {
            float3 c = NamerStyleGradeMath.ForwardExposureTonemap(x, p.PostExposureEv);
            c = NamerStyleGradeMath.ForwardWhiteBalance(c, p.Temperature, p.Tint);
            c = NamerStyleGradeMath.ForwardContrast(c, p.Contrast);
            c = NamerStyleGradeMath.ForwardSaturation(c, p.Saturation);
            return NamerStyleGradeMath.ForwardCurves(c, null, null, null, null);
        }

        /// <summary>
        /// Probe constructor: achromatic-chromatic mix at the target luma — the same
        /// construction as the neutral fixture's chromatic rows.
        /// </summary>
        private static float3 MixProbe(float luma, float chroma, float hueFraction)
        {
            float3 pureHue = PureHue(hueFraction);
            float pureHueLuma = NamerStylePostMath.Luma709(pureHue);
            float m = luma / ((1f - chroma) + chroma * pureHueLuma);
            return (1f - chroma) * m + chroma * m * pureHue;
        }

        /// <summary>Pure hue direction on the RGB unit wheel (six stops).</summary>
        private static float3 PureHue(float hueFraction)
        {
            float wheel = hueFraction * 6f;
            int stop = (int)wheel;
            float fraction = wheel - stop;
            switch (stop)
            {
                case 0: return new float3(1f, fraction, 0f);
                case 1: return new float3(1f - fraction, 1f, 0f);
                case 2: return new float3(0f, 1f, fraction);
                case 3: return new float3(0f, 1f - fraction, 1f);
                case 4: return new float3(fraction, 0f, 1f);
                default: return new float3(1f, 0f, 1f - fraction);
            }
        }

        /// <summary>Max-abs component assertion helper.</summary>
        private static void AssertMaxAbs(float3 reference, float3 actual, float3 context, double tolerance, string message)
        {
            double error = cmax(abs(actual - reference));
            Assert.AreEqual(0.0, error, tolerance, $"{message} (max abs error {error:0.0e0} at {context})");
        }

        /// <summary>
        /// AnimationCurve over the 9 uniform grid nodes with EXACT linear tangents
        /// (in/out tangent = segment slope), so evaluation is piecewise linear with
        /// kinks exactly at the nodes — the class MasterInverse inverts exactly.
        /// </summary>
        private static AnimationCurve LinearCurveFromNodeValues(float[] values)
        {
            Keyframe[] keys = new Keyframe[values.Length];
            float gridStep = 1f / (values.Length - 1);
            for (int i = 0; i < values.Length; i++)
            {
                float leftSlope = i > 0 ? (values[i] - values[i - 1]) / gridStep : (values[i + 1] - values[i]) / gridStep;
                float rightSlope = i < values.Length - 1 ? (values[i + 1] - values[i]) / gridStep : (values[i] - values[i - 1]) / gridStep;
                keys[i] = new Keyframe(i * gridStep, values[i], leftSlope, rightSlope);
            }

            return new AnimationCurve(keys);
        }

        /// <summary>
        /// Verbatim copy of NamerStylePostFit.WeightedPercentile (private,
        /// NamerStylePostFit.cs:477-510) — the plan-sanctioned test mirror so the
        /// neutral spread golden is computed by the fit's own percentile discipline.
        /// </summary>
        private static float WeightedPercentileMirror(float[] histogram, float total, float fraction)
        {
            if (total <= 0f)
            {
                return 0f;
            }

            float need = fraction * total;
            float cumulative = 0f;
            float previousCenter = 0f;
            bool hasPrevious = false;
            for (int i = 0; i < histogram.Length; i++)
            {
                float mass = histogram[i];
                if (mass <= 0f)
                {
                    continue;
                }

                float center = (i + kBinCenterOffset) / histogram.Length;
                if (cumulative + mass >= need)
                {
                    float within = (need - cumulative) / mass;
                    float baseCenter = hasPrevious ? previousCenter : center;
                    return baseCenter + (center - baseCenter) * within;
                }

                cumulative += mass;
                previousCenter = center;
                hasPrevious = true;
            }

            return hasPrevious ? previousCenter : 0f;
        }
    }
}

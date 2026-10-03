using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// STYL-07/STYL-08: determinism and known-answer pins for the NamerStylePostFit
    /// estimator suite. All fixtures are synthetic Color[] arrays; every expected
    /// value is either hand-derived (derivation recorded beside the assertion) or
    /// computed from NamerStylePostMath so the pin is wiring, not a re-implementation.
    /// </summary>
    public class NamerStylePostFitTests
    {
        // --- fixture-only sRGB encode (inverse of the extractor decode; construction, not fit math) ---
        private const float kFixtureEncodeCutoff = 0.0031308f;
        private const float kFixtureEncodeSlope = 12.92f;
        private const float kFixtureEncodeOffset = 0.055f;
        private const float kFixtureEncodeScale = 1.055f;
        private const float kFixtureEncodeExponent = 1f / 2.4f;

        // --- pinned ColorUtils.ColorBalanceToLMSCoeffs(20, -5), the FitSyntheticPair determinism cast ---
        private const float kPinnedWbRed = 1.023302972f;
        private const float kPinnedWbGreen = 1.006599730f;
        private const float kPinnedWbBlue = 0.857412236f;

        private const float kFixtureGammaPairExponent = 2.2f; // reference = pow(tonemapped, 2.2)
        private const float kHistogramBinWidth = 1f / 256f;   // one histogram bin over [0,1]
        private const float kP10Fraction = 0.1f;              // WeightedPercentile fraction for p10
        private const float kP90Fraction = 0.9f;              // WeightedPercentile fraction for p90
        // Sequential-composition oracles (05.3 volume-washout fix): the modeled chain
        // re-bins at bin centers (exposure, contrast), and the baked master curve —
        // BuildKeys' zero-tangent Keyframes sampled through AnimationCurve —
        // smoothsteps between keys, deviating up to ~9.6% of each adjacent-key delta
        // from the piecewise-linear spec map the fit derived (MasterInverse inverts
        // it linearly). The composed MEAN therefore lands within one bin width
        // (quantization and interpolation average out) and the percentiles within
        // seven bins — measured worst 5.6 bins on the identical ramp (p90; the
        // tonemap-shoulder-conjugated lift has the largest key-delta curvature),
        // 4.1 on the gamma fixture. The retired independent-solve composition misses
        // by an EV-sized margin (a large fraction of a stop of double correction),
        // far outside this bound, so the bar still discriminates decisively.
        private const double kComposedMeanTolerance = kHistogramBinWidth;
        private const double kComposedPercentileTolerance = 7.0 * kHistogramBinWidth;
        private const double kIdentityRampContrastBound = 6.0; // the identical ramp's EV Jensen residual (~-0.13 through the tonemap shoulder) re-measured as a spread correction — small residual, not an independent full correction
        private const float kNeutralKnobTolerance = 1e-3f;    // EV/contrast/saturation residual on identical pairs
        // WB neutrality floor: the fixed 24-probe golden-section resolves each axis to
        // ~200 * 0.618^24 = 1.2e-3 (float64 measured 2.3e-4), but float32 noise in the
        // objective near the flat minimum walks the converged point to ~1.2e-2
        // (measured -0.0118). 0.05 keeps a strong neutrality bar with margin; the
        // plan's stated 1e-4 is below any deterministic numerical-solve floor.
        private const double kNeutralWhiteBalanceTolerance = 0.05;
        private const float kWhiteBalanceRecoveryTolerance = 0.5f; // plan-specified recovery bar
        // |WhiteBalanceLumaScale - 1| on the rail fixture must exceed this: below it
        // the WB luma effect is second-order noise and the rail composition test
        // would pass vacuously against the uncorrected EV solve. Measured 0.0143
        // (coeffs(-50, 0) over TonemapLdr((0.8, 0.55, 0.35))); margin at half the
        // measurement. The corresponding EV correction log2(1.0143) ~ 0.02 EV is
        // ~1.8 histogram bins of composed-mean error — above the 1-bin composed
        // bar, so the composition assertions still catch an uncorrected solve.
        private const double kFirstOrderLumaMargin = 0.007;
        private const double kEstimatorTolerance = 1e-3;      // hand-formula agreement on constructed populations
        private const double kBloomExactTolerance = 1e-5;     // bin-center arithmetic is exact to float rounding
        private const double kKeyRiseTolerance = 1e-4;        // clip-guard rise bar: capped keys are prev + exactly the cap, so only float32 add rounding remains

        /// <summary>
        /// Fixture sRGB encode of one linear channel (inverse of NamerStyleExtractor.SrgbToLinear).
        /// </summary>
        private static float EncodeSrgb(float v)
        {
            if (v <= kFixtureEncodeCutoff)
            {
                return v * kFixtureEncodeSlope;
            }

            return kFixtureEncodeScale * pow(v, kFixtureEncodeExponent) - kFixtureEncodeOffset;
        }

        private static Color EncodePixel(float3 linear)
        {
            return new Color(EncodeSrgb(linear.x), EncodeSrgb(linear.y), EncodeSrgb(linear.z), 1f);
        }

        private static Color GrayPixel(float g)
        {
            return new Color(g, g, g, 1f);
        }

        /// <summary>Center of the histogram bin a [0,1] value falls into.</summary>
        private static float BinCenter(float v)
        {
            return ((int)(v * 256f) + 0.5f) / 256f;
        }

        /// <summary>
        /// Double-run over a non-trivial synthetic pair (mixed-color source, WB-cast
        /// + gain + gamma reference) must produce element-wise identical parameters —
        /// the Bake_IsDeterministic bar.
        /// </summary>
        [Test]
        public void Fit_IsDeterministic()
        {
            NamerStylePostFitParams first = FitSyntheticPair();
            NamerStylePostFitParams second = FitSyntheticPair();
            Assert.AreEqual(first.Temperature, second.Temperature);
            Assert.AreEqual(first.Tint, second.Tint);
            Assert.AreEqual(first.PostExposureEv, second.PostExposureEv);
            Assert.AreEqual(first.Contrast, second.Contrast);
            Assert.AreEqual(first.Saturation, second.Saturation);
            Assert.AreEqual(first.BloomThreshold, second.BloomThreshold);
            Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            AssertKeysAreIdentical(first.MasterKeys, second.MasterKeys);
            AssertKeysAreIdentical(first.RedKeys, second.RedKeys);
            AssertKeysAreIdentical(first.GreenKeys, second.GreenKeys);
            AssertKeysAreIdentical(first.BlueKeys, second.BlueKeys);
        }

        /// <summary>
        /// WR-01 (05.1 review): a zero-mass reference distribution (every slot cleared
        /// after extraction, or every weight 0) must be refused, not fitted — the old
        /// path returned a maximal black grade (contrast/exposure/saturation railed,
        /// every curve value 0) as success. The guard's message flows to the
        /// "Fit failed: {message}" HelpBox via RunStylePostFit's existing catch.
        /// </summary>
        [Test]
        public void Fit_ZeroReferenceMass_Throws()
        {
            NamerStylePostStats source = NamerStylePostFit.AccumulateSource(new[] { GrayPixel(0.5f) });
            NamerStylePostStats emptyReference = new NamerStylePostStats();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                () => NamerStylePostFit.Fit(source, emptyReference, "zero-mass"));
            Assert.That(ex.Message, Does.Contain("no weight"),
                "the refusal must name the zero-mass reason (flows to the Fit failed HelpBox)");
        }

        /// <summary>
        /// Source == reference distribution is the neutral fixed point: WB and
        /// saturation ~0, and — since the sequential-composition fix (05.3
        /// volume-washout) — the COMPOSED chain reproduces the reference
        /// distribution (mean and p10/p90), which is where identity now lives:
        /// contrast and the master curve deliberately carry the residual the
        /// previous knobs leave (here the EV solve's Jensen gap), and the runtime
        /// composes them back to neutral. EV is NOT ~0 on this fixture: the
        /// full-range 256-level ramp is maximally wide, so tonemap-of-mean vs
        /// mean-of-tonemap curvature is FIRST-order (with the twin's constants,
        /// tonemap-of-mean(0.5) = 0.3673 vs mean-of-tonemap = 0.3364, identity EV
        /// ~ -0.13) — asserted against the twin-mirrored bisection instead of a
        /// constant. Contrast is bounded, not pinned to 0: it neutralizes the
        /// spread that the ~-0.13 EV shifts through the tonemap shoulder, measured
        /// at kIdentityRampContrastBound. WB tolerance derivation at
        /// kNeutralWhiteBalanceTolerance.
        /// </summary>
        [Test]
        public void Fit_IdenticalPair_IsNeutral()
        {
            const int kFixtureLevels = 256;
            Color[] source = new Color[kFixtureLevels];
            Color[] reference = new Color[kFixtureLevels];
            for (int i = 0; i < kFixtureLevels; i++)
            {
                float g = i / (float)(kFixtureLevels - 1);
                float tonemapped = NamerStylePostMath.TonemapLdr(new float3(g, g, g)).x;
                source[i] = GrayPixel(g);
                reference[i] = EncodePixel(new float3(tonemapped, tonemapped, tonemapped));
            }

            NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(source);
            NamerStylePostStats referenceStats = AccumulateReferencePixels(reference, 1f);
            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(sourceStats, referenceStats, "identical");

            Assert.AreEqual(0.0, (double)fitted.Temperature, kNeutralWhiteBalanceTolerance);
            Assert.AreEqual(0.0, (double)fitted.Tint, kNeutralWhiteBalanceTolerance);
            double expectedRampEv = SolveExposureMirrorForTest(sourceStats, referenceStats);
            Assert.AreEqual(expectedRampEv, (double)fitted.PostExposureEv, kEstimatorTolerance,
                "ramp identity EV must equal the twin-mirrored bisection (first-order curvature, ~ -0.13)");
            Assert.Less((double)Mathf.Abs(fitted.Contrast), kIdentityRampContrastBound,
                "identity-pair contrast is the EV's Jensen-gap spread residual, not an independent full correction");
            Assert.AreEqual(0.0, (double)fitted.Saturation, kNeutralKnobTolerance);
            AssertComposedChainMatchesReference(sourceStats, referenceStats, fitted,
                kComposedMeanTolerance, kComposedPercentileTolerance);
        }

        /// <summary>
        /// Root-cause regression for the pre-tonemap EV model (260927-jkb): the fixture
        /// is built BACKWARD from a known EV so the model inverts exactly — for a
        /// uniform single-tone population, mean-of-tonemap equals tonemap-of-mean, so
        /// the construction is exact up to the sRGB encode/decode round trip and the
        /// bisection's float convergence. Raw gray 0.8 * 2^0.75 ~ 1.35 sits well into
        /// the ACES shoulder where compression — the retired dark-bias defect — is
        /// strong. Three assertions: (1) the bisection recovers kKnownPreTonemapEv;
        /// (2) root cause — the retired post-tonemap log2 luma ratio, computed in-test
        /// from the exact post-tonemap means the old code consumed, undershoots the
        /// known EV by strictly more than kDarkBiasMargin; (3) model closure — the
        /// fitted EV reproduces the reference mean luma through the CPU twin.
        /// </summary>
        [Test]
        public void Fit_Exposure_SolvesPreTonemapThroughTonemap()
        {
            const int kFixturePixelCount = 64;
            const float kKnownPreTonemapEv = 0.75f;
            const float kDarkBiasMargin = 0.01f; // shoulder compression makes the old post-tonemap ratio undershoot EV by strictly more than this — the reported dark bias
            float3 rawGray = new float3(0.8f, 0.8f, 0.8f);
            float3 tonemappedKnown = NamerStylePostMath.TonemapLdr(rawGray * exp2(kKnownPreTonemapEv));
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = GrayPixel(rawGray.x);
                reference[i] = EncodePixel(tonemappedKnown);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "exposure");

            Assert.AreEqual((double)kKnownPreTonemapEv, (double)fitted.PostExposureEv, kEstimatorTolerance,
                "the bisection must invert the known pre-tonemap EV exactly on a single-tone pair");

            // Root cause: the retired v1 formula consumed the exact post-tonemap means —
            // the source's mean of tonemapped 0.8s and the reference triple's luma — and
            // its log2 ratio undershoots the true pre-tonemap EV by the shoulder compression.
            float srcLumaMean = NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(rawGray));
            float refLumaMean = NamerStylePostMath.Luma709(tonemappedKnown);
            float oldEv = log2(refLumaMean / srcLumaMean);
            Assert.Greater((double)kKnownPreTonemapEv, (double)(oldEv + kDarkBiasMargin),
                "the old post-tonemap ratio must undershoot EV by strictly more than kDarkBiasMargin (the reported dark bias)");

            // Model closure: the fitted EV pushed through the pre-tonemap model lands on
            // the reference mean luma (pin the wiring, never re-implement the math).
            float closure = NamerStylePostMath.Luma709(
                NamerStylePostMath.TonemapLdr(rawGray * exp2(fitted.PostExposureEv)));
            Assert.AreEqual((double)refLumaMean, (double)closure, kEstimatorTolerance,
                "the fitted EV must reproduce the reference mean luma through the tonemapped model");
        }

        /// <summary>
        /// Single-tone identity pair: source 64 pixels of raw gray 0.4, reference the
        /// sRGB encoding of the tonemapped source. Mean-of-tonemap equals
        /// tonemap-of-mean on a single tone, so the identity EV is exactly 0 up to the
        /// sRGB round trip and bisection float noise (kNeutralKnobTolerance). The
        /// full-range ramp fixture is deliberately NOT reused here because its Jensen
        /// gap moves EV off exact zero.
        /// </summary>
        [Test]
        public void Fit_Exposure_SingleToneIdentity_IsExactZero()
        {
            const int kFixturePixelCount = 64;
            float3 rawGray = new float3(0.4f, 0.4f, 0.4f);
            float3 tonemapped = NamerStylePostMath.TonemapLdr(rawGray);
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = GrayPixel(rawGray.x);
                reference[i] = EncodePixel(tonemapped);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "exposure-identity");

            Assert.AreEqual(0.0, (double)fitted.PostExposureEv, kNeutralKnobTolerance);
        }

        /// <summary>
        /// Positive rail: the fit's ACES asymptote is 1/kFitC = 1/0.983729 ~ 1.0165 &gt; 1.0
        /// (NamerStylePostMath RRTAndODTFit constants, URP RRTAndODT rational fit) and
        /// TonemapLdr's final saturate clamps the overshoot to exactly 1.0 (first
        /// reached at raw input ~25.7), so a white target IS reachable for bright
        /// sources — the fixture must make it unreachable INSIDE the bracket instead:
        /// the dim raw gray 0.01 source caps the model input at 0.01 * 2^10 = 10.24,
        /// whose tonemapped luma ~0.97 &lt; 1.0 (0.01's post-tonemap mean luma
        /// ~9.2e-4 clears the 1e-6 near-black guard, so Fit does not throw). The
        /// bisection converges to exactly +10 — the float-collapsed bracket endpoint;
        /// asserted with NO tolerance (rail semantics preserved).
        /// </summary>
        [Test]
        public void Fit_Exposure_UnreachableBrightTarget_RailsAtPlusTen()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = GrayPixel(0.01f);
                reference[i] = new Color(1f, 1f, 1f, 1f); // raw sRGB white decodes to exactly 1.0
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "exposure-rail+");

            Assert.AreEqual(10.0, (double)fitted.PostExposureEv);
        }

        /// <summary>
        /// Negative rail: HDR gray 30f source (AccumulateSource's documented HDR
        /// contract — TonemapLdr(30) is saturate-clamped to 1.0, so the near-black
        /// source guard passes) onto black reference pixels at weight 1 each (WR-01
        /// refuses only zero WEIGHT; MeanLuma 0 floors to kStatEpsilon via the existing
        /// max, and Luma709(TonemapLdr(30/1024)) ~ 7e-3 &gt;&gt; 1e-6 stays above it at
        /// ev = -10), so the target is unreachable below and the bisection converges to
        /// exactly -10 — the float-collapsed bracket endpoint; asserted with NO
        /// tolerance.
        /// </summary>
        [Test]
        public void Fit_Exposure_UnreachableDarkTarget_RailsAtMinusTen()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = GrayPixel(30f);
                reference[i] = new Color(0f, 0f, 0f, 1f);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "exposure-rail-");

            Assert.AreEqual(-10.0, (double)fitted.PostExposureEv);
        }

        /// <summary>
        /// Contrast on 50/50 two-value populations is hand-computable: with two
        /// occupied bins of equal mass, p10 = center(bin A) exactly and
        /// p90 = center A + (2*0.9 - 1) * (center B - center A) (within-bin fraction
        /// 0.8 at the p90 crossing). Since the sequential-composition fix (05.3
        /// volume-washout), the SOURCE percentiles are taken on the histogram
        /// transformed through the fitted EV — production's own internal transform,
        /// so the pin is wiring (SolveExposureMirrorForTest precedent); the reference
        /// percentiles stay hand-derived bin-center arithmetic. Expected contrast is
        /// the LogC spread ratio of those values; the estimator must match (measured
        /// exact in float64).
        /// </summary>
        [Test]
        public void Fit_Contrast_SolvesLogCSpread()
        {
            const int kHalfPopulation = 32;
            Color[] source = new Color[kHalfPopulation * 2];
            Color[] reference = new Color[kHalfPopulation * 2];
            for (int i = 0; i < kHalfPopulation; i++)
            {
                source[i] = GrayPixel(0.25f);
                source[i + kHalfPopulation] = GrayPixel(0.75f);
                float refLow = NamerStylePostMath.TonemapLdr(new float3(0.35f, 0.35f, 0.35f)).x;
                float refHigh = NamerStylePostMath.TonemapLdr(new float3(0.85f, 0.85f, 0.85f)).x;
                reference[i] = EncodePixel(new float3(refLow, refLow, refLow));
                reference[i + kHalfPopulation] = EncodePixel(new float3(refHigh, refHigh, refHigh));
            }

            NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(source);
            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                sourceStats,
                AccumulateReferencePixels(reference, 1f),
                "contrast");

            float[] evTransformed = NamerStylePostFit.TransformHistogramThroughExposure(
                sourceStats.LumaHistogram, fitted.PostExposureEv);
            float srcTotal = (float)sourceStats.TotalWeight;
            float srcP10 = NamerStylePostFit.WeightedPercentile(evTransformed, srcTotal, kP10Fraction);
            float srcP90 = NamerStylePostFit.WeightedPercentile(evTransformed, srcTotal, kP90Fraction);
            float refLowCenter = BinCenter(NamerStylePostMath.TonemapLdr(new float3(0.35f, 0.35f, 0.35f)).x);
            float refHighCenter = BinCenter(NamerStylePostMath.TonemapLdr(new float3(0.85f, 0.85f, 0.85f)).x);
            const float kP90WithinFraction = 0.8f; // (2 * p90 - 1) with two equal-mass bins
            float refP10 = refLowCenter;
            float refP90 = refLowCenter + kP90WithinFraction * (refHighCenter - refLowCenter);
            float srcSpread = NamerStylePostMath.LinearToLogC(srcP90) - NamerStylePostMath.LinearToLogC(srcP10);
            float refSpread = NamerStylePostMath.LinearToLogC(refP90) - NamerStylePostMath.LinearToLogC(refP10);
            float expectedContrast = (refSpread / srcSpread - 1f) * 100f;

            Assert.AreEqual((double)expectedContrast, (double)fitted.Contrast, kEstimatorTolerance);
        }

        /// <summary>
        /// Reference = source white-balanced by ColorUtils.ColorBalanceToLMSCoeffs(12, -5):
        /// the solve must recover (12, -5). The cast is constructed live from ColorUtils
        /// so any upstream grading-math change fails loudly instead of silently
        /// mis-fitting. The fixture moved in-band (12 inside the clamp) because the
        /// fitted knobs now clamp to ±kFittedWhiteBalanceLimit — a (20, -5) fixture
        /// would assert the clamp, not the solve.
        /// </summary>
        [Test]
        public void Fit_WhiteBalance_RecoversKnownCast()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(12f, -5f);
            float3 cast = new float3(coeffs.x, coeffs.y, coeffs.z);
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                float3 tonemapped = NamerStylePostMath.TonemapLdr(new float3(0.5f, 0.5f, 0.5f));
                source[i] = GrayPixel(0.5f);
                float3 balanced = NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(tonemapped) * cast);
                reference[i] = EncodePixel(balanced);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "wb");

            Assert.AreEqual(12.0, (double)fitted.Temperature, kWhiteBalanceRecoveryTolerance);
            Assert.AreEqual(-5.0, (double)fitted.Tint, kWhiteBalanceRecoveryTolerance);
        }

        /// <summary>
        /// Extreme cast recovers the FULL range: the solver finds ~(-100, +100) and the
        /// fitted knobs carry it untruncated — ±kFittedWhiteBalanceLimit now equals the
        /// solve bounds at URP's full ±100 slider range (260927-jkb lineage: 15 -> 50
        /// -> 100; the retired ±50 hue-preservation bound silently railed a genuinely
        /// warm source). Without the clamp truncating to exact floats, the fitted
        /// values carry the solver's golden-section convergence error (~7e-4 at the
        /// bound) — the recovery-bar tolerance covers it.
        /// </summary>
        [Test]
        public void Fit_WhiteBalance_ExtremeCast_RecoversFullRange()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(-100f, 100f);
            float3 cast = new float3(coeffs.x, coeffs.y, coeffs.z);
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                float3 tonemapped = NamerStylePostMath.TonemapLdr(new float3(0.5f, 0.5f, 0.5f));
                source[i] = GrayPixel(0.5f);
                float3 balanced = NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(tonemapped) * cast);
                reference[i] = EncodePixel(balanced);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "wb-clamp");

            Assert.AreEqual(-100.0, (double)fitted.Temperature, kWhiteBalanceRecoveryTolerance);
            Assert.AreEqual(100.0, (double)fitted.Tint, kWhiteBalanceRecoveryTolerance);
        }

        /// <summary>
        /// Stall regression: the pre-multistart 0-seeded alternating-axis solve
        /// converged to ~(-44.1, +30.2) on this diagonal coeffs(-40, +40) target; the
        /// grid-seeded second start must recover (-40, +40) within 2.0. Calls the
        /// internal NamerStylePostFit.SolveWhiteBalance directly (InternalsVisibleTo in
        /// Editor/AssemblyInfo.cs); the target is geometric-mean normalized with a
        /// test-local mirror of the production helper.
        /// </summary>
        [Test]
        public void SolveWhiteBalance_Multistart_EscapesStallBasin()
        {
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(-40f, 40f);
            float3 target = NormalizeGeometricMeanForTest(new float3(coeffs.x, coeffs.y, coeffs.z));

            NamerStylePostFit.SolveWhiteBalance(target, out float temperature, out float tint);

            Assert.AreEqual(-40.0, (double)temperature, 2.0);
            Assert.AreEqual(40.0, (double)tint, 2.0);
        }

        /// <summary>
        /// Saturation from a constructed chroma ratio: source chroma is
        /// chroma(TonemapLdr((0.6, 0.35, 0.15))) (computed from the twin), reference
        /// sRGB (0.03, 0.02, 0.01) decodes exactly on the linear segment (v / 12.92),
        /// so reference chroma = (0.03 - 0.01) / 0.03 = 2/3 exactly. Expected
        /// saturation = max(0f, (ratio - 1) * 100) — the floored formula per the
        /// saturation floor (the fit never desaturates).
        /// </summary>
        [Test]
        public void Fit_Saturation_ChromaRatio()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            float3 srcLinear = new float3(0.6f, 0.35f, 0.15f);
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = new Color(srcLinear.x, srcLinear.y, srcLinear.z, 1f);
                reference[i] = new Color(0.03f, 0.02f, 0.01f, 1f);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "sat");

            float3 tonemapped = NamerStylePostMath.TonemapLdr(srcLinear);
            float srcChroma = (cmax(tonemapped) - cmin(tonemapped)) / cmax(tonemapped);
            const float kRefChromaExact = 2f / 3f; // (0.03 - 0.01) / 0.03 after the exact linear-segment decode
            float expectedSaturation = max(0f, (kRefChromaExact / srcChroma - 1f) * 100f);

            Assert.AreEqual((double)expectedSaturation, (double)fitted.Saturation, kEstimatorTolerance);
        }

        /// <summary>
        /// The fit never desaturates: a saturated source (0.6, 0.35, 0.15) onto a
        /// near-gray reference (0.05, 0.048, 0.046) — all reference channels decode on
        /// the exact linear segment v / 12.92, so reference chroma = (0.05 - 0.046) /
        /// 0.05 = 0.08, far above the 1e-3 deadband, and the ratio path runs. The raw
        /// ratio ~ (0.08 / srcChroma - 1) * 100 is strongly negative; the floor pins
        /// fitted.Saturation at exactly 0.
        /// </summary>
        [Test]
        public void Fit_Saturation_NeverDesaturates_FloorsAtZero()
        {
            const int kFixturePixelCount = 64;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = new Color(0.6f, 0.35f, 0.15f, 1f);
                reference[i] = new Color(0.05f, 0.048f, 0.046f, 1f);
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "sat-floor");

            Assert.AreEqual(0.0, (double)fitted.Saturation);
        }

        /// <summary>
        /// Bloom threshold is the p95 of the reference raw sRGB Max3 histogram:
        /// 9 pixels at Max3 = 0.5 and 1 at Max3 = 0.9 gives need = 9.5 of mass 10,
        /// crossing the second bin at fraction 0.5 — the midpoint of the two occupied
        /// bin centers ((0.5 + 0.5/256) + (0.9 + 0.5/256)) / 2 = 0.701171875.
        /// </summary>
        [Test]
        public void Fit_BloomThreshold_IsReferenceMax3Percentile()
        {
            Color[] source = { GrayPixel(0.5f) };
            Color[] reference = new Color[10];
            for (int i = 0; i < 9; i++)
            {
                reference[i] = new Color(0.5f, 0.2f, 0.1f, 1f);
            }

            reference[9] = new Color(0.9f, 0.3f, 0.2f, 1f);

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "bloom");

            double expected = (BinCenter(0.5f) + BinCenter(0.9f)) / 2f;
            Assert.AreEqual(expected, (double)fitted.BloomThreshold, kBloomExactTolerance);
        }

        /// <summary>
        /// Composition regression — the 05.3 volume-washout root cause's
        /// specification. URP composes the knobs in series (EV -> ACES tonemap ->
        /// LUT contrast -> ... -> YRGB curves), so the FITTED SET must reproduce the
        /// reference distribution when chained in that order. The retired
        /// independent solves each carried the FULL correction of their statistic
        /// (EV the mean, contrast the spread, the curve the whole CDF), so the
        /// composed chain over-corrected — the mean/spread landed two to three
        /// times past the target, which the user saw as muddy, washed-out generated
        /// volumes; this assertion fails on that behavior. Fixture: reference =
        /// pow(tonemapped source, 2.2) on a dense 256-level ramp, so every knob
        /// does real work (the EV takes the mean drop, contrast the spread change,
        /// the curve the residual gamma shape).
        /// </summary>
        [Test]
        public void Fit_ComposedChain_ReproducesReferenceDistribution()
        {
            const int kFixtureLevels = 256;
            Color[] source = new Color[kFixtureLevels];
            Color[] reference = new Color[kFixtureLevels];
            for (int i = 0; i < kFixtureLevels; i++)
            {
                float g = i / (float)(kFixtureLevels - 1);
                float tonemapped = NamerStylePostMath.TonemapLdr(new float3(g, g, g)).x;
                float gammaMapped = pow(tonemapped, kFixtureGammaPairExponent);
                source[i] = GrayPixel(g);
                reference[i] = EncodePixel(new float3(gammaMapped, gammaMapped, gammaMapped));
            }

            NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(source);
            NamerStylePostStats referenceStats = AccumulateReferencePixels(reference, 1f);
            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(sourceStats, referenceStats, "curve");

            AssertComposedChainMatchesReference(sourceStats, referenceStats, fitted,
                kComposedMeanTolerance, kComposedPercentileTolerance);
        }

        /// <summary>
        /// Master-curve clip-guard regression (05.3 green-washout fix): a
        /// bright-heavy reference (a fifth of the mass in one near-white cluster)
        /// lets the histogram specification STEP a populated mid band straight onto
        /// the saturated top — measured on the live Neo fit, the (0.5, 0.534)/
        /// (0.625, 1.0) key pair (slope 3.73) blew 32% of green-dominant texels to
        /// >= 0.999 and destroyed their chroma. Fixture mirrors that shape: source =
        /// 78% dark mass (uniform over [0, 0.45]) + a populated mid-high band at
        /// [0.55, 0.75] (the greens stand-in — dark model, bright accent band);
        /// reference = the same 78/22 mass split bimodal, 78% in a low cluster
        /// (~0.05 linear) and 22% at the top bin's center (~0.994 linear), so
        /// CdfInvert jumps from the low cluster to ~0.996 for every quantile past
        /// 0.78 and one 1/8 key segment carries the whole ~0.96 rise (the band's
        /// quantiles sit past the jump, so unguarded it lands entirely on the
        /// saturated cluster). Three assertions: (1) every adjacent master-key rise
        /// respects the CLAHE-style clip limit (the guard spec), (2) the top key
        /// still reaches the saturated cluster (the guard limits climb RATE, not
        /// range), (3) the key at 0.625 — inside the populated band — is not blown
        /// onto the saturated top (pre-fix it is the ~0.996 cluster; post-fix it
        /// sits on the capped climb).
        /// </summary>
        [Test]
        public void Fit_MasterCurve_BrightHeavyReference_LimitsRiseIntoSaturatedTop()
        {
            const int kFixturePixelCount = 256;
            const int kBandPixelCount = 56;   // 22% mid-high band, matching the reference's top cluster mass
            int bandStart = kFixturePixelCount - kBandPixelCount;
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                if (i < bandStart)
                {
                    source[i] = GrayPixel((i / (float)bandStart) * 0.45f);
                    // low cluster: two pixels per bin over the bottom of the sRGB ramp
                    reference[i] = GrayPixel((i * 0.5f + 0.5f) / NamerStylePostFit.kPostHistogramBins);
                }
                else
                {
                    source[i] = GrayPixel(0.55f + ((i - bandStart) / (float)(kBandPixelCount - 1)) * 0.2f);
                    // the top bin's center (sRGB ~0.998 -> decoded linear ~0.994, bin 254)
                    reference[i] = GrayPixel((255f + 0.5f) / NamerStylePostFit.kPostHistogramBins);
                }
            }

            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "master-clip-guard");

            Keyframe[] keys = fitted.MasterKeys;
            for (int i = 1; i < keys.Length; i++)
            {
                Assert.LessOrEqual(
                    (double)(keys[i].value - keys[i - 1].value),
                    (double)NamerStylePostFit.kMasterClipGuardMaxRise + kKeyRiseTolerance,
                    string.Format("master key rise {0}->{1} must respect the clip guard (measured {2})",
                        i - 1, i, keys[i].value - keys[i - 1].value));
            }

            Assert.GreaterOrEqual((double)keys[keys.Length - 1].value, (double)NamerStylePostFit.kMasterClipGuardEntry,
                "the guard limits the climb rate into the top, not the curve's reach — the reference's bright cluster still maps to the saturated top");

            Assert.Less((double)keys[5].value, (double)NamerStylePostFit.kMasterClipGuardEntry,
                string.Format("the populated mid-high band's key at 0.625 must sit on the capped climb, not the saturated top (measured {0})",
                    keys[5].value));
        }

        /// <summary>
        /// White-balance luma scale premise pins (05.3 volume-washout follow-up):
        /// neutral knobs are the identity (ColorBalanceToLMSCoeffs(0, 0) is exactly
        /// (1, 1, 1), so the scale is 1 up to the LMS round-trip float error), and
        /// the rail fixture's scale is first-order — the defect's premise, without
        /// which the rail composition test below would pass vacuously.
        /// </summary>
        [Test]
        public void WhiteBalanceLumaScale_NeutralIsIdentityAndRailIsFirstOrder()
        {
            float3 tonemappedWarm = NamerStylePostMath.TonemapLdr(new float3(0.8f, 0.55f, 0.35f));
            Assert.AreEqual(1.0, (double)NamerStylePostFit.WhiteBalanceLumaScale(0f, 0f, tonemappedWarm), 1e-5,
                "neutral knobs apply identity LMS gains, so the luma scale must be 1 up to the round trip");
            Assert.Greater(
                Math.Abs((double)NamerStylePostFit.WhiteBalanceLumaScale(-50f, 0f, tonemappedWarm) - 1.0),
                kFirstOrderLumaMargin,
                string.Format("the rail cast must shift luma first-order (measured scale {0})",
                    NamerStylePostFit.WhiteBalanceLumaScale(-50f, 0f, tonemappedWarm)));
        }

        /// <summary>
        /// Root-cause regression for the white-balance-at-the-rail defect (05.3
        /// volume-washout follow-up): the runtime applies the fitted WB between the
        /// tonemap and the contrast with RAW ColorBalanceToLMSCoeffs gains, so a fit
        /// whose temperature sits at a strong -50 shifts luma
        /// first-order — the EV solve must divide that scale out or the composed
        /// mean overshoots by exactly the shift (the reported "a small EV decrease
        /// makes it look right"). Fixture: warm source single tone, reference = that
        /// tone tonemapped then white-balanced by coeffs(-50, 0) — the runtime LUT
        /// stage verbatim. Three assertions: (1) the fixture drives the fitted
        /// temperature to a strong cool value (<= -40), (2) the fitted EV equals the
        /// twin-mirrored TWO-PASS solve (pass 1 on the reference mean, pass 2 on that
        /// mean divided by the fitted WB's luma scale — the retired single-pass solve
        /// misses by log2(scale)), (3) the composed chain lands on the reference
        /// mean/percentiles.
        /// </summary>
        [Test]
        public void Fit_ComposedChain_WhiteBalanceRailLandsOnReferenceMean()
        {
            const float kRailTemperature = -50f;
            const float kRailTint = 0f;
            const int kFixturePixelCount = 64;
            float3 warmLinear = new float3(0.8f, 0.55f, 0.35f);
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(kRailTemperature, kRailTint);
            float3 cast = new float3(coeffs.x, coeffs.y, coeffs.z);
            float3 balanced = NamerStylePostMath.LmsToLinear(
                NamerStylePostMath.LinearToLms(NamerStylePostMath.TonemapLdr(warmLinear)) * cast);
            Color warm = new Color(warmLinear.x, warmLinear.y, warmLinear.z, 1f);
            Color[] source = new Color[kFixturePixelCount];
            Color[] reference = new Color[kFixturePixelCount];
            for (int i = 0; i < kFixturePixelCount; i++)
            {
                source[i] = warm;
                reference[i] = EncodePixel(balanced);
            }

            NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(source);
            NamerStylePostStats referenceStats = AccumulateReferencePixels(reference, 1f);
            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(sourceStats, referenceStats, "wb-rail");

            Assert.LessOrEqual((double)fitted.Temperature, -40.0,
                "the fixture's cast must drive the fitted temperature to a strong cool value where WB luma is first-order");

            float firstPassEv = SolveExposureMirrorForTest(sourceStats, referenceStats);
            float3 tonemappedMean = NamerStylePostMath.TonemapLdr(
                max(new float3(sourceStats.RawMeanLinear.r, sourceStats.RawMeanLinear.g, sourceStats.RawMeanLinear.b), 1e-6f)
                * exp2(firstPassEv));
            float fittedScale = NamerStylePostFit.WhiteBalanceLumaScale(fitted.Temperature, fitted.Tint, tonemappedMean);
            float expectedEv = SolveExposureMirrorForMean(
                sourceStats, max((float)referenceStats.MeanLuma / fittedScale, 1e-6f));
            Assert.AreEqual((double)expectedEv, (double)fitted.PostExposureEv, kEstimatorTolerance,
                "the fitted EV must be the two-pass solve: pass 1 on the reference mean, pass 2 divided by the fitted WB's luma scale");

            AssertComposedChainMatchesReference(sourceStats, referenceStats, fitted,
                kComposedMeanTolerance, kComposedPercentileTolerance);
        }

        /// <summary>
        /// Accumulating a reference twice at weight 1 must equal accumulating it once
        /// at weight 2 exactly: all accumulators are linear in the weight and the
        /// fixture masses are small integers, which are exactly representable in
        /// float32/double — so the merge is asserted with zero tolerance.
        /// </summary>
        [Test]
        public void AccumulateReference_MergesWeights()
        {
            Color[] pixels =
            {
                new Color(0.4f, 0.3f, 0.2f, 1f),
                new Color(0.1f, 0.5f, 0.9f, 1f),
                new Color(0.7f, 0.7f, 0.05f, 1f),
            };

            NamerStylePostStats merged = new NamerStylePostStats();
            NamerStylePostFit.AccumulateReference(merged, pixels, 1f);
            NamerStylePostFit.AccumulateReference(merged, pixels, 1f);

            NamerStylePostStats single = new NamerStylePostStats();
            NamerStylePostFit.AccumulateReference(single, pixels, 2f);

            Assert.AreEqual(single.TotalWeight, merged.TotalWeight);
            Assert.AreEqual(single.RedSum, merged.RedSum);
            Assert.AreEqual(single.GreenSum, merged.GreenSum);
            Assert.AreEqual(single.BlueSum, merged.BlueSum);
            Assert.AreEqual(single.LumaSum, merged.LumaSum);
            Assert.AreEqual(single.ChromaSum, merged.ChromaSum);
            Assert.AreEqual(single.MeanLinear.r, merged.MeanLinear.r);
            Assert.AreEqual(single.MeanLuma, merged.MeanLuma);
            Assert.AreEqual(single.MeanChroma, merged.MeanChroma);
            for (int i = 0; i < NamerStylePostFit.kPostHistogramBins; i++)
            {
                Assert.AreEqual(single.LumaHistogram[i], merged.LumaHistogram[i], $"luma bin {i}");
                Assert.AreEqual(single.RedHistogram[i], merged.RedHistogram[i], $"red bin {i}");
                Assert.AreEqual(single.GreenHistogram[i], merged.GreenHistogram[i], $"green bin {i}");
                Assert.AreEqual(single.BlueHistogram[i], merged.BlueHistogram[i], $"blue bin {i}");
                Assert.AreEqual(single.Max3SrgbHistogram[i], merged.Max3SrgbHistogram[i], $"max3 bin {i}");
            }
        }

        /// <summary>
        /// Non-trivial synthetic pair for determinism runs: mixed-color source,
        /// reference built through the twin with a white-balance cast, a uniform gain
        /// and a gamma map, so every estimator does real work.
        /// </summary>
        private static NamerStylePostFitParams FitSyntheticPair()
        {
            Color[] source =
            {
                new Color(0.2f, 0.3f, 0.4f, 1f),
                new Color(0.6f, 0.5f, 0.4f, 1f),
                new Color(0.1f, 0.1f, 0.12f, 1f),
                GrayPixel(0.55f),
                new Color(0.05f, 0.45f, 0.25f, 1f),
            };

            float3 cast = new float3(kPinnedWbRed, kPinnedWbGreen, kPinnedWbBlue);
            const float kSyntheticGain = 1.25f;
            Color[] reference = new Color[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                float3 s = new float3(source[i].r, source[i].g, source[i].b);
                float3 tonemapped = NamerStylePostMath.TonemapLdr(s);
                float3 balanced = NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(tonemapped) * cast);
                float3 gained = balanced * kSyntheticGain;
                float3 gammaMapped = pow(gained, kFixtureGammaPairExponent);
                reference[i] = EncodePixel(gammaMapped);
            }

            return NamerStylePostFit.Fit(
                NamerStylePostFit.AccumulateSource(source),
                AccumulateReferencePixels(reference, 1f),
                "synthetic");
        }

        private static NamerStylePostStats AccumulateReferencePixels(Color[] pixels, float weight)
        {
            NamerStylePostStats stats = new NamerStylePostStats();
            NamerStylePostFit.AccumulateReference(stats, pixels, weight);
            return stats;
        }

        /// <summary>
        /// Composition oracle (05.3 volume-washout fix): pushes the SOURCE luma
        /// histogram through the modeled runtime chain — the fit's exposure
        /// transform, the white balance's scalar luma scale, its contrast
        /// transform, then master-curve evaluation per occupied bin center (URP
        /// applies the YRGB curves last, on the LDR value) — and asserts the
        /// composed output's mean luma and p10/p90 land on the reference
        /// distribution's. Uses the fit's own internal transforms (the pin is
        /// composition semantics, not a re-implementation of the stage math).
        /// </summary>
        private static void AssertComposedChainMatchesReference(
            NamerStylePostStats sourceStats,
            NamerStylePostStats referenceStats,
            NamerStylePostFitParams fitted,
            double meanTolerance,
            double percentileTolerance)
        {
            float[] chained = NamerStylePostFit.TransformHistogramThroughExposure(
                sourceStats.LumaHistogram, fitted.PostExposureEv);
            float3 oracleRawMean = max(
                new float3(sourceStats.RawMeanLinear.r, sourceStats.RawMeanLinear.g, sourceStats.RawMeanLinear.b),
                1e-6f);
            float3 oracleTonemappedMean = NamerStylePostMath.TonemapLdr(oracleRawMean * exp2(fitted.PostExposureEv));
            float oracleWbScale = NamerStylePostFit.WhiteBalanceLumaScale(
                fitted.Temperature, fitted.Tint, oracleTonemappedMean);
            chained = NamerStylePostFit.TransformHistogramThroughLumaScale(chained, oracleWbScale);
            chained = NamerStylePostFit.TransformHistogramThroughContrast(chained, fitted.Contrast);
            AnimationCurve master = new AnimationCurve(fitted.MasterKeys);
            float[] output = new float[NamerStylePostFit.kPostHistogramBins];
            float total = 0f;
            for (int i = 0; i < chained.Length; i++)
            {
                float mass = chained[i];
                if (mass <= 0f)
                {
                    continue;
                }

                // The runtime applies the curve per-pixel continuously; model that by
                // splitting each bin's mass fractionally between the two destination
                // bins the mapped center straddles (CdfEval's within-bin-linear
                // convention) instead of clumping it into one bin.
                float center = (i + 0.5f) / NamerStylePostFit.kPostHistogramBins;
                float pos = clamp(master.Evaluate(center), 0f, 1f) * (NamerStylePostFit.kPostHistogramBins - 1);
                int lowBin = (int)pos;
                int highBin = min(lowBin + 1, NamerStylePostFit.kPostHistogramBins - 1);
                float within = pos - lowBin;
                output[lowBin] += mass * (1f - within);
                output[highBin] += mass * within;
                total += mass;
            }

            double outputMean = 0.0;
            for (int i = 0; i < output.Length; i++)
            {
                outputMean += ((i + 0.5f) / NamerStylePostFit.kPostHistogramBins) * (double)output[i];
            }

            outputMean /= (double)total;
            float outputP10 = NamerStylePostFit.WeightedPercentile(output, total, kP10Fraction);
            float outputP90 = NamerStylePostFit.WeightedPercentile(output, total, kP90Fraction);
            float refTotal = (float)referenceStats.TotalWeight;
            float refP10 = NamerStylePostFit.WeightedPercentile(referenceStats.LumaHistogram, refTotal, kP10Fraction);
            float refP90 = NamerStylePostFit.WeightedPercentile(referenceStats.LumaHistogram, refTotal, kP90Fraction);

            Assert.AreEqual((double)referenceStats.MeanLuma, outputMean, meanTolerance,
                "the composed chain (EV -> WB -> contrast -> master curve) must land on the reference mean luma");
            Assert.AreEqual((double)refP10, (double)outputP10, percentileTolerance,
                "the composed chain must land on the reference p10");
            Assert.AreEqual((double)refP90, (double)outputP90, percentileTolerance,
                "the composed chain must land on the reference p90");
        }

        private static void AssertKeysAreIdentical(Keyframe[] expected, Keyframe[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i].time, actual[i].time, $"key {i} time");
                Assert.AreEqual(expected[i].value, actual[i].value, $"key {i} value");
            }
        }

        /// <summary>
        /// Test-local mirror of the production exposure bisection (260927-jkb,
        /// NamerStylePostFit.Fit step (2)): derives the identity-pair EV expectation
        /// from the CPU twin so the pin is wiring, not a magic constant (house rule).
        /// Verbatim same bracket, per-channel epsilon floor, sign rule, fixed iteration
        /// count and float ops, consuming the SAME accumulated stats — a verbatim
        /// mirror is float-identical, and the kEstimatorTolerance on the assertion
        /// absorbs trivial ordering drift.
        /// </summary>
        private static float SolveExposureMirrorForTest(NamerStylePostStats source, NamerStylePostStats reference)
        {
            return SolveExposureMirrorForMean(source, max(reference.MeanLuma, 1e-6f));
        }

        /// <summary>
        /// Target-parametrized exposure-bisection mirror: same bracket, epsilon
        /// floor, sign rule, fixed iteration count and float ops as production's
        /// SolveExposureForMeanLuma local function — used by the white-balance-rail
        /// test to mirror BOTH passes of the two-pass EV solve (pass 1 against the
        /// reference mean, pass 2 against that mean divided by the fitted WB's
        /// scalar luma scale).
        /// </summary>
        private static float SolveExposureMirrorForMean(NamerStylePostStats source, float targetLumaMean)
        {
            const int kMirrorBisectionIterations = 48;
            const float kMirrorBracketEv = 10f;
            const float kMirrorStatEpsilon = 1e-6f;
            float3 srcRawMean = max(
                new float3(source.RawMeanLinear.r, source.RawMeanLinear.g, source.RawMeanLinear.b),
                kMirrorStatEpsilon);
            float evLow = -kMirrorBracketEv;
            float evHigh = kMirrorBracketEv;
            for (int i = 0; i < kMirrorBisectionIterations; i++)
            {
                float evMid = (evLow + evHigh) * 0.5f;
                float predicted = NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(srcRawMean * exp2(evMid)));
                if (predicted < targetLumaMean)
                {
                    evLow = evMid;
                }
                else
                {
                    evHigh = evMid;
                }
            }

            return clamp((evLow + evHigh) * 0.5f, -kMirrorBracketEv, kMirrorBracketEv);
        }

        /// <summary>
        /// Test-local mirror of NamerStylePostFit.NormalizeGeometricMean (kept private
        /// there): v / pow(|x|*|y|*|z|, 1/3); a degenerate (zero) geometric mean
        /// returns v unchanged.
        /// </summary>
        private static float3 NormalizeGeometricMeanForTest(float3 v)
        {
            float geometricMean = pow(abs(v.x) * abs(v.y) * abs(v.z), 1f / 3f);
            if (geometricMean <= 0f)
            {
                return v;
            }

            return v / geometricMean;
        }
    }
}

using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Plain-data result of the reference-fitted post-processing look (STYL-07/STYL-08).
    /// All knob values use the URP Volume parameter ranges: temperature/tint/contrast/
    /// saturation in [-100, 100] percent, postExposure in EV stops, bloomThreshold in
    /// raw sRGB gamma space. Curve keys are uniform-grid Keyframes in [0, 1] ready for
    /// AnimationCurve construction by the Volume-profile writer (plan 03).
    /// </summary>
    public sealed class NamerStylePostFitParams
    {
        public float Temperature;
        public float Tint;
        public float PostExposureEv;
        public float Contrast;
        public float Saturation;
        public float BloomThreshold;
        public Keyframe[] MasterKeys;
        public Keyframe[] RedKeys;
        public Keyframe[] GreenKeys;
        public Keyframe[] BlueKeys;
        public string Fingerprint;
    }

    /// <summary>
    /// Result of one Fit Look run (05.1 plan 03): the fitted parameters plus the
    /// exemplar object's display name on success, or the failure shape (cancelled /
    /// unreadable reference texture / prerequisites unmet). Mirrors
    /// <see cref="NamerStyleExtractResult"/>'s dual-ctor contract — no partial result
    /// is ever written on cancel or failure.
    /// </summary>
    public sealed class NamerStylePostFitRunResult
    {
        /// <summary>Fitted post parameters; null when failed/cancelled.</summary>
        public readonly NamerStylePostFitParams Params;

        /// <summary>Display name of the exemplar object the fit was computed from; empty when failed/cancelled.</summary>
        public readonly string ExemplarName;

        /// <summary>True when the user cancelled before completion; no partial result is returned.</summary>
        public readonly bool Cancelled;

        /// <summary>Name of the first reference texture that could not be read; null otherwise.</summary>
        public readonly string ErrorTextureName;

        /// <summary>True when the profile (or the window prerequisites) were unmet — the prereq reason path.</summary>
        public readonly bool NotExtracted;

        /// <summary>Success shape (D-03: one per-profile fit, scene-coherent).</summary>
        public NamerStylePostFitRunResult(NamerStylePostFitParams parameters, string exemplarName)
        {
            Params = parameters;
            ExemplarName = exemplarName;
            Cancelled = false;
            ErrorTextureName = null;
            NotExtracted = false;
        }

        /// <summary>Failure shape: cancelled, unreadable reference texture, or unmet prerequisites.</summary>
        public NamerStylePostFitRunResult(bool cancelled, string errorTextureName, bool notExtracted)
        {
            Params = null;
            ExemplarName = "";
            Cancelled = cancelled;
            ErrorTextureName = errorTextureName;
            NotExtracted = notExtracted;
        }
    }

    /// <summary>
    /// Accumulated pixel statistics for one distribution. Domains (RESEARCH Pattern 1,
    /// binding for the project's m_ColorGradingMode: 0 LDR asset):
    /// - Source stats: ACES-tonemapped (NamerStylePostMath.TonemapLdr) linear values in [0, 1].
    /// - Reference stats: sRGB-decoded linear values in [0, 1] (display-referred), plus a
    ///   raw sRGB Max3 histogram (gamma-space, no decode) for the bloom threshold.
    /// Built only by the NamerStylePostFit accumulators; fields are internal.
    /// </summary>
    public sealed class NamerStylePostStats
    {
        internal double RedSum;
        internal double GreenSum;
        internal double BlueSum;
        internal double LumaSum;
        internal double ChromaSum;
        internal double TotalWeight;
        internal readonly float[] LumaHistogram = new float[NamerStylePostFit.kPostHistogramBins];
        internal readonly float[] RedHistogram = new float[NamerStylePostFit.kPostHistogramBins];
        internal readonly float[] GreenHistogram = new float[NamerStylePostFit.kPostHistogramBins];
        internal readonly float[] BlueHistogram = new float[NamerStylePostFit.kPostHistogramBins];
        internal readonly float[] Max3SrgbHistogram = new float[NamerStylePostFit.kPostHistogramBins];

        internal NamerStylePostStats()
        {
        }

        /// <summary>Mean linear color of the distribution's own domain (post-tonemap for source, decoded for reference).</summary>
        internal Color MeanLinear
        {
            get
            {
                if (TotalWeight <= 0)
                {
                    return new Color(0f, 0f, 0f, 0f);
                }

                return new Color(
                    (float)(RedSum / TotalWeight),
                    (float)(GreenSum / TotalWeight),
                    (float)(BlueSum / TotalWeight),
                    1f);
            }
        }

        /// <summary>Mean Rec.709 luma in the distribution's own linear domain.</summary>
        internal float MeanLuma
        {
            get
            {
                if (TotalWeight <= 0)
                {
                    return 0f;
                }

                return (float)(LumaSum / TotalWeight);
            }
        }

        /// <summary>Mean HSV-style chroma (max - min) / max(max, eps) in the distribution's own linear domain.</summary>
        internal float MeanChroma
        {
            get
            {
                if (TotalWeight <= 0)
                {
                    return 0f;
                }

                return (float)(ChromaSum / TotalWeight);
            }
        }
    }

    /// <summary>
    /// Deterministic CPU fit of a URP LDR post-processing look from a source distribution
    /// onto a reference distribution (ROADMAP 5.1 criteria 1, 2 and 5). All domain math
    /// routes through NamerStylePostMath (the URP 17.0.4 CPU twin) or
    /// ColorUtils.ColorBalanceToLMSCoeffs (forward white-balance evaluation only);
    /// no color math is re-derived here.
    /// Determinism contract (extractor precedent): fixed iteration order and counts,
    /// strict comparisons, no RNG, no time or threading inputs — running the same
    /// accumulations and Fit twice produces element-wise identical parameters
    /// (pinned by Fit_IsDeterministic).
    /// Deliberately self-contained for wave-1 parallel execution: this file must NOT
    /// reference NamerEditorConstants (plan 02 owns it); kMaxFittedPostExposureEv lives
    /// here and plan 02's PascalCase MaxFittedPostExposureEv is value-pinned to it.
    /// </summary>
    public static class NamerStylePostFit
    {
        /// <summary>Fixed histogram resolution over [0,1] for CDFs and percentiles (planner decision 6).</summary>
        public const int kPostHistogramBins = 256;

        private const int kCurveKeyCount = 9;                    // uniform key grid per curve
        private const float kDefaultBloomHighlightPercentile = 0.95f; // p95 of raw sRGB Max3
        private const float kPercentile10 = 0.1f;                // contrast spread low pick (extractor precedent)
        private const float kPercentile90 = 0.9f;                // contrast spread high pick (extractor precedent)
        private const float kContrastDenominatorFloor = 0.001f;  // extractor kContrastDenominatorFloor precedent
        private const float kStatEpsilon = 1e-6f;                // denominator floor for means/chroma/LMS gains
        private const float kChromaNoiseFloor = 1e-3f;           // deadband for chroma differences (5x the ACES gray-spread noise)
        private const float kParamRangeLimit = 100f;             // URP percent-knob clamp and percent scale
        private const float kMaxFittedPostExposureEv = 10f;      // EV clamp; plan 02 pins MaxFittedPostExposureEv to this value
        private const float kWhiteBalanceRange = 100f;           // temperature/tint search bounds [-100, 100]
        private const int kWhiteBalanceSolveSweeps = 32;         // alternating-axis golden-section sweeps (fixed)
        private const int kWhiteBalanceLineProbes = 24;          // golden-section probes per line (fixed)
        private const int kWhiteBalanceAxisCount = 2;            // temperature and tint axes alternate
        private const float kGoldenSectionConjugate = 0.6180339887f; // (sqrt(5) - 1) / 2
        private const float kCubeRootExponent = 1f / 3f;         // geometric-mean normalization
        private const float kBinCenterOffset = 0.5f;             // occupied-bin center = (i + 0.5) / bins

        /// <summary>
        /// Accumulate source statistics from HDR linear pixels. Each pixel is mapped
        /// through NamerStylePostMath.TonemapLdr (the URP LDR display path) and binned
        /// in the post-tonemap linear domain. Non-finite pixels are skipped so GPU
        /// readback garbage cannot poison the fit (threat T-05.1-01-01).
        /// </summary>
        public static NamerStylePostStats AccumulateSource(Color[] linearPixels)
        {
            NamerStylePostStats stats = new NamerStylePostStats();
            for (int i = 0; i < linearPixels.Length; i++)
            {
                Color pixel = linearPixels[i];
                if (!IsFinitePixel(pixel))
                {
                    continue;
                }

                float3 tonemapped = NamerStylePostMath.TonemapLdr(new float3(pixel.r, pixel.g, pixel.b));
                AccumulateLinear(stats, tonemapped, 1f);
            }

            return stats;
        }

        /// <summary>
        /// Accumulate reference statistics from raw sRGB pixels at the given weight
        /// (weighted tiles merge linearly into one stats object). Channels are decoded
        /// via NamerStyleExtractor.SrgbToLinear (the exact extractor decode), and the
        /// raw sRGB Max3 is additionally binned unconverted for the bloom threshold.
        /// Non-finite pixels are skipped (threat T-05.1-01-01).
        /// </summary>
        public static void AccumulateReference(NamerStylePostStats stats, Color[] srgbPixels, float weight)
        {
            for (int i = 0; i < srgbPixels.Length; i++)
            {
                Color pixel = srgbPixels[i];
                if (!IsFinitePixel(pixel))
                {
                    continue;
                }

                float3 decoded = new float3(
                    NamerStyleExtractor.SrgbToLinear(pixel.r),
                    NamerStyleExtractor.SrgbToLinear(pixel.g),
                    NamerStyleExtractor.SrgbToLinear(pixel.b));
                AccumulateLinear(stats, decoded, weight);
                stats.Max3SrgbHistogram[BinIndexOf(cmax(new float3(pixel.r, pixel.g, pixel.b)))] += weight;
            }
        }

        /// <summary>
        /// Fit the full post-processing parameter set from a source distribution onto a
        /// reference distribution. Knob order: (1) von Kries white balance via a
        /// deterministic alternating-axis golden-section solve over temperature/tint,
        /// (2) post-exposure as the log2 post-tonemap linear mean-luma ratio, (3) contrast
        /// as the LogC p90-p10 spread ratio, (4) saturation as the mean-chroma ratio,
        /// (5) master/R/G/B curves via histogram specification (channels composed with the
        /// master mapping), (6) bloom threshold as the p95 of the raw sRGB Max3 histogram.
        /// Every parameter is clamped to its named cap (threat T-05.1-01-01).
        /// </summary>
        public static NamerStylePostFitParams Fit(NamerStylePostStats source, NamerStylePostStats reference, string referenceFingerprint)
        {
            NamerStylePostFitParams fitted = new NamerStylePostFitParams();
            fitted.Fingerprint = referenceFingerprint;

            float3 srcMean = ToFloat3(source.MeanLinear);
            float3 refMean = ToFloat3(reference.MeanLinear);

            // (1) White balance: von Kries gains on LMS means, unit-geometric-mean
            // normalized (the overall-gain axis belongs to post-exposure).
            float3 srcLms = NamerStylePostMath.LinearToLms(srcMean);
            float3 refLms = NamerStylePostMath.LinearToLms(refMean);
            float3 gains = refLms / max(srcLms, kStatEpsilon);
            float3 target = NormalizeGeometricMean(gains);
            SolveWhiteBalance(target, out float temperature, out float tint);
            fitted.Temperature = clamp(temperature, -kParamRangeLimit, kParamRangeLimit);
            fitted.Tint = clamp(tint, -kParamRangeLimit, kParamRangeLimit);

            // (2) Post-exposure: log2 ratio of post-tonemap linear mean luma
            // (reference is sRGB-decoded linear). Accepted v1 behavior: tonemap
            // compression biases the ratio low on bright references (planner decision 1).
            float srcLumaMean = max(source.MeanLuma, kStatEpsilon);
            float refLumaMean = max(reference.MeanLuma, kStatEpsilon);
            fitted.PostExposureEv = clamp(log2(refLumaMean / srcLumaMean), -kMaxFittedPostExposureEv, kMaxFittedPostExposureEv);

            // (3) Contrast: LogC of the p10/p90 percentile spreads (source percentiles
            // post-tonemap linear, reference percentiles sRGB-decoded linear).
            float srcTotal = (float)source.TotalWeight;
            float refTotal = (float)reference.TotalWeight;
            float srcP10 = WeightedPercentile(source.LumaHistogram, srcTotal, kPercentile10);
            float srcP90 = WeightedPercentile(source.LumaHistogram, srcTotal, kPercentile90);
            float refP10 = WeightedPercentile(reference.LumaHistogram, refTotal, kPercentile10);
            float refP90 = WeightedPercentile(reference.LumaHistogram, refTotal, kPercentile90);
            float srcSpread = NamerStylePostMath.LinearToLogC(srcP90) - NamerStylePostMath.LinearToLogC(srcP10);
            float refSpread = NamerStylePostMath.LinearToLogC(refP90) - NamerStylePostMath.LinearToLogC(refP10);
            float spreadRatio = refSpread / max(srcSpread, kContrastDenominatorFloor);
            fitted.Contrast = clamp((spreadRatio - 1f) * kParamRangeLimit, -kParamRangeLimit, kParamRangeLimit);

            // (4) Saturation: plain URP Saturation from the mean-chroma ratio (D-05).
            // Two degenerate guards (Rule 1 fixes found by the neutrality fixture):
            // - Deadband: a tonemapped near-gray image carries mean chroma up to
            //   ~3.5e-4 purely from float32 channel spread in the ACES chain (see the
            //   gray-preservation tolerance derivation in NamerStylePostMathTests),
            //   while an sRGB round-tripped gray is exactly 0 — the raw ratio would
            //   rail saturation to -100 on an identical pair. Differences below
            //   kChromaNoiseFloor are noise, not signal, and read as neutral.
            // - A pure-gray source (chroma <= kStatEpsilon) makes the ratio undefined:
            //   gray source onto a colored reference asks for maximal saturation.
            float saturationValue = 0f;
            float chromaDelta = reference.MeanChroma - source.MeanChroma;
            if (abs(chromaDelta) > kChromaNoiseFloor)
            {
                if (source.MeanChroma > kStatEpsilon)
                {
                    float chromaRatio = reference.MeanChroma / source.MeanChroma;
                    saturationValue = (chromaRatio - 1f) * kParamRangeLimit;
                }
                else
                {
                    saturationValue = kParamRangeLimit;
                }
            }

            fitted.Saturation = clamp(saturationValue, -kParamRangeLimit, kParamRangeLimit);

            // (5) Curves: histogram specification y = Fref^-1(Fsrc(x)) on the uniform key
            // grid; R/G/B CDFs are composed with the master mapping (sequential
            // specification — RGB corrects the residual left after master).
            float[] masterValues = FitCurveValues(source.LumaHistogram, srcTotal, reference.LumaHistogram, refTotal, null);
            fitted.MasterKeys = BuildKeys(masterValues);
            fitted.RedKeys = BuildKeys(FitCurveValues(source.RedHistogram, srcTotal, reference.RedHistogram, refTotal, masterValues));
            fitted.GreenKeys = BuildKeys(FitCurveValues(source.GreenHistogram, srcTotal, reference.GreenHistogram, refTotal, masterValues));
            fitted.BlueKeys = BuildKeys(FitCurveValues(source.BlueHistogram, srcTotal, reference.BlueHistogram, refTotal, masterValues));

            // (6) Bloom threshold: p95 of the reference raw sRGB Max3 histogram,
            // written as-is (gamma space, matching URP's default threshold domain).
            fitted.BloomThreshold = WeightedPercentile(reference.Max3SrgbHistogram, refTotal, kDefaultBloomHighlightPercentile);

            return fitted;
        }

        /// <summary>
        /// Writes a successful fit onto the profile's serialized fitted-post block
        /// (05.1 D-02 source of truth). Pure data write — no Undo, no SetDirty, no
        /// Volume regeneration: the caller (the inspector's RunFitLook) owns the
        /// transaction. The four curves are constructed from cloned key arrays, so
        /// later curve edits can never alias the fit result.
        /// </summary>
        public static void ApplyToProfile(NamerStyleProfile profile, NamerStylePostFitParams fit, string exemplarName)
        {
            profile.PostTemperature = fit.Temperature;
            profile.PostTint = fit.Tint;
            profile.PostExposureEv = fit.PostExposureEv;
            profile.PostContrast = fit.Contrast;
            profile.PostSaturation = fit.Saturation;
            profile.PostBloomThreshold = fit.BloomThreshold;
            profile.PostMasterCurve = new AnimationCurve(fit.MasterKeys);
            profile.PostRedCurve = new AnimationCurve(fit.RedKeys);
            profile.PostGreenCurve = new AnimationCurve(fit.GreenKeys);
            profile.PostBlueCurve = new AnimationCurve(fit.BlueKeys);
            profile.PostFitFingerprint = fit.Fingerprint;
            profile.PostFitExemplarName = exemplarName;
        }

        /// <summary>
        /// Shared linear-domain accumulation: channel/luma/chroma sums plus luma and
        /// per-channel histogram mass at the given weight.
        /// </summary>
        private static void AccumulateLinear(NamerStylePostStats stats, float3 c, float weight)
        {
            stats.TotalWeight += weight;
            stats.RedSum += c.x * weight;
            stats.GreenSum += c.y * weight;
            stats.BlueSum += c.z * weight;
            float luma = NamerStylePostMath.Luma709(c);
            stats.LumaSum += luma * weight;
            float chroma = (cmax(c) - cmin(c)) / max(cmax(c), kStatEpsilon);
            stats.ChromaSum += chroma * weight;
            stats.LumaHistogram[BinIndexOf(luma)] += weight;
            stats.RedHistogram[BinIndexOf(c.x)] += weight;
            stats.GreenHistogram[BinIndexOf(c.y)] += weight;
            stats.BlueHistogram[BinIndexOf(c.z)] += weight;
        }

        /// <summary>
        /// Weighted-bin percentile with linear interpolation between occupied-bin
        /// centers (extractor PercentileLuma precedent). The first occupied bin returns
        /// its own center; fraction is a mass fraction in [0, 1].
        /// </summary>
        private static float WeightedPercentile(float[] histogram, float total, float fraction)
        {
            if (total <= 0f)
            {
                return 0f;
            }

            float need = fraction * total;
            float cum = 0f;
            float previousCenter = 0f;
            bool hasPrevious = false;
            for (int i = 0; i < histogram.Length; i++)
            {
                float mass = histogram[i];
                if (mass <= 0f)
                {
                    continue;
                }

                float center = (i + kBinCenterOffset) / kPostHistogramBins;
                if (cum + mass >= need)
                {
                    float within = (need - cum) / mass;
                    float baseCenter = hasPrevious ? previousCenter : center;
                    return baseCenter + (center - baseCenter) * within;
                }

                cum += mass;
                previousCenter = center;
                hasPrevious = true;
            }

            return hasPrevious ? previousCenter : 0f;
        }

        /// <summary>
        /// CDF of a weighted histogram at x, linear within each bin edge interval.
        /// </summary>
        private static float CdfEval(float[] histogram, float total, float x)
        {
            if (total <= 0f)
            {
                return 0f;
            }

            float pos = clamp(x, 0f, 1f) * kPostHistogramBins;
            int bin = min((int)pos, kPostHistogramBins - 1);
            float frac = pos - bin;
            float cum = 0f;
            for (int i = 0; i < bin; i++)
            {
                cum += histogram[i];
            }

            return (cum + histogram[bin] * frac) / total;
        }

        /// <summary>
        /// Inverse CDF: smallest x whose histogram CDF reaches q, linear within the
        /// crossing bin (edge convention, consistent with CdfEval).
        /// </summary>
        private static float CdfInvert(float[] histogram, float total, float q)
        {
            if (total <= 0f)
            {
                return 0f;
            }

            float cum = 0f;
            for (int i = 0; i < kPostHistogramBins; i++)
            {
                float mass = histogram[i];
                if (mass <= 0f)
                {
                    continue;
                }

                if (cum + mass >= q * total)
                {
                    float within = (q * total - cum) / mass;
                    return (i + within) / kPostHistogramBins;
                }

                cum += mass;
            }

            return 1f;
        }

        /// <summary>
        /// Histogram-specification curve values on the uniform key grid. When
        /// masterValues is provided the channel CDF is composed with the master
        /// mapping, Fs'(t) = Fs(m^-1(t)), so master and RGB never double-correct.
        /// Keys are made monotone by running max and clamped to [0, 1].
        /// </summary>
        private static float[] FitCurveValues(float[] srcHist, float srcTotal, float[] refHist, float refTotal, float[] masterValues)
        {
            float[] values = new float[kCurveKeyCount];
            float runningMax = 0f;
            for (int i = 0; i < kCurveKeyCount; i++)
            {
                float x = i / (float)(kCurveKeyCount - 1);
                float srcPos = masterValues != null ? MasterInverse(masterValues, x) : x;
                float q = CdfEval(srcHist, srcTotal, srcPos);
                float y = CdfInvert(refHist, refTotal, q);
                if (y > runningMax)
                {
                    runningMax = y;
                }

                values[i] = clamp(runningMax, 0f, 1f);
            }

            return values;
        }

        /// <summary>
        /// Inverse of the master mapping over its monotone key values: the smallest key
        /// time whose value reaches t. Flat segments resolve to the start of the flat
        /// region; t above the last value maps to 1.
        /// </summary>
        private static float MasterInverse(float[] masterValues, float t)
        {
            for (int i = 0; i < masterValues.Length - 1; i++)
            {
                float v0 = masterValues[i];
                float v1 = masterValues[i + 1];
                if (t <= v1)
                {
                    if (v1 > v0)
                    {
                        float u = (t - v0) / (v1 - v0);
                        float time0 = i / (float)(kCurveKeyCount - 1);
                        float time1 = (i + 1) / (float)(kCurveKeyCount - 1);
                        return lerp(time0, time1, clamp(u, 0f, 1f));
                    }

                    return i / (float)(kCurveKeyCount - 1);
                }
            }

            return 1f;
        }

        /// <summary>
        /// Uniform-grid Keyframe construction from curve values.
        /// </summary>
        private static Keyframe[] BuildKeys(float[] values)
        {
            Keyframe[] keys = new Keyframe[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                keys[i] = new Keyframe(i / (float)(kCurveKeyCount - 1), values[i]);
            }

            return keys;
        }

        /// <summary>
        /// Deterministic white-balance solve: alternating-axis golden-section over
        /// (temperature, tint) in [-100, 100], minimizing the squared distance between
        /// the unit-geometric-mean-normalized ColorUtils.ColorBalanceToLMSCoeffs
        /// candidates and the normalized target gains. Fixed sweep and probe counts;
        /// strict less-than comparisons; no early exit on data values.
        /// </summary>
        private static void SolveWhiteBalance(float3 target, out float temperature, out float tint)
        {
            float t = 0f;
            float ti = 0f;
            for (int sweep = 0; sweep < kWhiteBalanceSolveSweeps; sweep++)
            {
                if (sweep % kWhiteBalanceAxisCount == 0)
                {
                    t = MinimizeWhiteBalanceAxis(true, t, ti, target);
                }
                else
                {
                    ti = MinimizeWhiteBalanceAxis(false, t, ti, target);
                }
            }

            temperature = t;
            tint = ti;
        }

        /// <summary>
        /// One golden-section line minimization along one white-balance axis.
        /// </summary>
        private static float MinimizeWhiteBalanceAxis(bool solveTemperature, float t, float ti, float3 target)
        {
            float lo = -kWhiteBalanceRange;
            float hi = kWhiteBalanceRange;
            float c = hi - kGoldenSectionConjugate * (hi - lo);
            float d = lo + kGoldenSectionConjugate * (hi - lo);
            float fc = WhiteBalanceDistance(solveTemperature, c, t, ti, target);
            float fd = WhiteBalanceDistance(solveTemperature, d, t, ti, target);
            for (int probe = 0; probe < kWhiteBalanceLineProbes; probe++)
            {
                if (fc < fd)
                {
                    hi = d;
                    d = c;
                    fd = fc;
                    c = hi - kGoldenSectionConjugate * (hi - lo);
                    fc = WhiteBalanceDistance(solveTemperature, c, t, ti, target);
                }
                else
                {
                    lo = c;
                    c = d;
                    fc = fd;
                    d = lo + kGoldenSectionConjugate * (hi - lo);
                    fd = WhiteBalanceDistance(solveTemperature, d, t, ti, target);
                }
            }

            if (fc < fd)
            {
                return c;
            }

            return d;
        }

        /// <summary>
        /// Squared distance between the normalized ColorUtils coefficients at a
        /// candidate (temperature, tint) and the normalized target gains.
        /// </summary>
        private static float WhiteBalanceDistance(bool solveTemperature, float candidate, float t, float ti, float3 target)
        {
            float temperature = solveTemperature ? candidate : t;
            float tint = solveTemperature ? ti : candidate;
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(temperature, tint);
            float3 normalized = NormalizeGeometricMean(new float3(coeffs.x, coeffs.y, coeffs.z));
            return distancesq(normalized, target);
        }

        /// <summary>
        /// Normalize to unit geometric mean of absolute components; a degenerate
        /// (zero) geometric mean returns the input unchanged.
        /// </summary>
        private static float3 NormalizeGeometricMean(float3 v)
        {
            float geometricMean = pow(abs(v.x) * abs(v.y) * abs(v.z), kCubeRootExponent);
            if (geometricMean <= 0f)
            {
                return v;
            }

            return v / geometricMean;
        }

        /// <summary>
        /// Bin index of a [0, 1] value clamped into the histogram range.
        /// </summary>
        private static int BinIndexOf(float v)
        {
            int index = (int)(v * kPostHistogramBins);
            if (index < 0)
            {
                return 0;
            }

            if (index > kPostHistogramBins - 1)
            {
                return kPostHistogramBins - 1;
            }

            return index;
        }

        /// <summary>
        /// True when all color channels of a pixel are finite (NaN/Inf pixels are skipped).
        /// </summary>
        private static bool IsFinitePixel(Color pixel)
        {
            return float.IsFinite(pixel.r) && float.IsFinite(pixel.g) && float.IsFinite(pixel.b);
        }

        /// <summary>
        /// Color to float3 (alpha dropped).
        /// </summary>
        private static float3 ToFloat3(Color c)
        {
            return new float3(c.r, c.g, c.b);
        }
    }
}

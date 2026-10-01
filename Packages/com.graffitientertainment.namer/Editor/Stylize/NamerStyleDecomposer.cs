using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Result of one Decompose Style run (05.3 STYL-13/STYL-14): the grade estimate,
    /// the palette anchors, the classification confidence fractions, and the bare
    /// decomposition fingerprint on success; the failure shape (cancelled /
    /// unreadable reference texture / too few references) otherwise. Mirrors
    /// <see cref="NamerStyleExtractResult"/>'s dual-ctor contract — no partial result
    /// is ever written on cancel or failure.
    /// CURVE-CARRY pin: the curve payload IS <see cref="Grade"/>'s four
    /// <see cref="Keyframe"/> arrays (MasterKeys + the identity Red/Green/BlueKeys of
    /// the shipped fit). This result deliberately carries NO AnimationCurve members —
    /// every consumer (the de-grade below, plan 06's post-block write) builds
    /// <c>new AnimationCurve(keys)</c> clones exactly as
    /// <see cref="NamerStylePostFit.ApplyToPostLook"/> does, so key arrays are never
    /// shared across consumers.
    /// </summary>
    public sealed class NamerStyleDecomposeResult
    {
        /// <summary>The grade estimate (shipped <see cref="NamerStylePostFit.Fit"/> over the neutral source, or the caller's exemplar override); null when failed/cancelled.</summary>
        public readonly NamerStylePostFitParams Grade;

        /// <summary>Palette anchors over the de-graded (or raw, per the asset's PaletteSource) histogram; null when failed/cancelled.</summary>
        public readonly NamerStyleAnchor[] PaletteAnchors;

        /// <summary>Fraction of weighted texels confidently classified as clean albedo; -1 = failed/cancelled (the asset's never-decomposed sentinel).</summary>
        public readonly float ConfidentAlbedoFraction;

        /// <summary>Fraction of weighted texels classified as grade-contaminated (zero/low confidence); -1 = failed/cancelled.</summary>
        public readonly float ContaminatedFraction;

        /// <summary>The BARE <see cref="NamerStyleAsset.ComputeFingerprint"/> return captured at decompose time (no suffix of any kind); empty when failed/cancelled.</summary>
        public readonly string DecompositionFingerprint;

        /// <summary>Empty on the neutral (model-free) path; the named exemplar on the grade-override path (the S6 honesty name).</summary>
        public readonly string GradeSourceExemplarName;

        /// <summary>True when the user cancelled before completion; no partial result is returned.</summary>
        public readonly bool Cancelled;

        /// <summary>Name of the first reference texture that could not be read; null otherwise.</summary>
        public readonly string ErrorTextureName;

        /// <summary>True when fewer than <see cref="NamerEditorConstants.MinReferences"/> positive-weight refs were assigned.</summary>
        public readonly bool TooFewReferences;

        /// <summary>Success shape: one grade estimate feeding BOTH the post block and the de-grade (T-05.3-03-04).</summary>
        public NamerStyleDecomposeResult(NamerStylePostFitParams grade, NamerStyleAnchor[] paletteAnchors,
            float confidentAlbedoFraction, float contaminatedFraction, string decompositionFingerprint,
            string gradeSourceExemplarName)
        {
            Grade = grade;
            PaletteAnchors = paletteAnchors;
            ConfidentAlbedoFraction = confidentAlbedoFraction;
            ContaminatedFraction = contaminatedFraction;
            DecompositionFingerprint = decompositionFingerprint;
            GradeSourceExemplarName = gradeSourceExemplarName;
            Cancelled = false;
            ErrorTextureName = null;
            TooFewReferences = false;
        }

        /// <summary>Failure shape: cancelled, unreadable texture, or too few references.</summary>
        public NamerStyleDecomposeResult(bool cancelled, string errorTextureName, bool tooFewReferences)
        {
            Grade = null;
            PaletteAnchors = null;
            ConfidentAlbedoFraction = -1f;
            ContaminatedFraction = -1f;
            DecompositionFingerprint = "";
            GradeSourceExemplarName = "";
            Cancelled = cancelled;
            ErrorTextureName = errorTextureName;
            TooFewReferences = tooFewReferences;
        }
    }

    /// <summary>
    /// The STYL-17 validation metric: the maximum knob deviation of a re-fit from
    /// identity, each deviation normalized by its knob's effective range, plus the
    /// name of the worst knob (rendered by plan 06's validation row via
    /// <see cref="NamerStyleDecomposer.BuildValidationMeasureString"/>).
    /// </summary>
    public readonly struct NamerStyleValidationMeasure
    {
        /// <summary>Max normalized knob deviation from identity in [0, 1]-scale units (NaN if any knob is NaN).</summary>
        public readonly float Measure;

        /// <summary>Display name of the knob carrying the maximum deviation.</summary>
        public readonly string KnobName;

        /// <summary>Success shape.</summary>
        public NamerStyleValidationMeasure(float measure, string knobName)
        {
            Measure = measure;
            KnobName = knobName;
        }
    }

    /// <summary>
    /// Two-pass model-free reference decomposition (05.3 STYL-13/STYL-14, D-02/D-04/D-07):
    /// reads the merged asset's shared weighted reference list ONE AT A TIME with the
    /// extractor discipline (isReadable pre-check, named-texture failure, buffer drop,
    /// cancellable progress), box-downsamples each to
    /// <see cref="NamerStyleExtractor.kExtractResolutionCap"/> with fixed integer grid
    /// boundaries, and estimates the grade by calling the SHIPPED
    /// <see cref="NamerStylePostFit.Fit"/> verbatim over
    /// <see cref="NamerStyleGradeMath.BuildNeutralSourceStats"/> (D-02: no second
    /// estimator family exists here — no bisection, no von Kries solve, no curve
    /// fitting outside the reused Fit call). Per-texel classification (RESEARCH
    /// Pattern 4) masks bloom/specular/emissive/deep-shadow/clipping texels to zero
    /// and fog/veil texels to a low weight in BOTH the grade stats and the palette
    /// histogram; the palette itself is k-means-clustered (shipped
    /// <see cref="NamerStyleExtractor.BuildAnchors"/>) over the DE-GRADED texel
    /// histogram by default (transform-then-bin — never de-graded after k-means) with
    /// the raw-pixel source behind the asset's PaletteSource switch.
    /// Determinism contract (extractor/fit precedent): fixed reference order, fixed
    /// serial accumulation order, fixed grid boundaries, strict comparisons, no RNG,
    /// no time or threading inputs, no data-dependent early exits — two Decomposes on
    /// the same state are element-wise identical (pinned by Decompose_IsDeterministic).
    /// The STYL-17 validation metric lives here as pure statics over params objects
    /// (no asset/window/engine-state arguments).
    /// </summary>
    public static class NamerStyleDecomposer
    {
        // -------- classification thresholds (RESEARCH Pattern 4; A5 — fixture-revisitable) --------
        // Internal (05.3-03): the test seam — NamerStyleDecomposerTests builds its
        // per-class cue combinations FROM these named constants so a revisited
        // threshold (A5) retargets the fixtures instead of pinning stale literals
        // (kDefaultBloomHighlightPercentile widening precedent).

        /// <summary>Clipping cut: raw sRGB max channel at or above 254/255 (one 8-bit
        /// code below rail) is clipped — also the exact non-invertible set of the
        /// tonemap inverse, so the mask and the inverse's domain gaps coincide.</summary>
        internal const float kClipThresholdSrgb = 254f / 255f;

        /// <summary>Chroma cut separating desaturated-bright (specular/bloom, and the
        /// low-chroma fog cue) from saturated-bright (emissive): HSV-style
        /// (max-min)/max on the raw sRGB texel. A5 — fixture-revisitable.</summary>
        internal const float kSpecularChromaMax = 0.15f;

        /// <summary>Absolute deep-shadow luma floor (decoded linear Rec.709): texels at
        /// or below it are noise-dominated and non-invertible near black. A5.</summary>
        internal const float kShadowLumaFloor = 0.02f;

        /// <summary>Deep-shadow percentile cut: the p5 of the merged decoded-luma
        /// histogram (the shadow-side twin of the fit's p95 highlight percentile).</summary>
        private const float kShadowPercentile = 0.05f;

        /// <summary>Fog/veil cue: per-texel local luma box-gradient below this value
        /// marks flat regions (the 3x3 box mean vs the center texel). A5.</summary>
        internal const float kFogGradientThreshold = 0.02f;

        /// <summary>Fog/veil weight: conservative LOW, not zero — the most fragile
        /// heuristic keeps partial palette mass (RESEARCH Pattern 4). A5.</summary>
        internal const float kFogWeight = 0.25f;

        /// <summary>Fog/veil mid-luma floor: a veil is mid-to-high luma, above the
        /// shadow band (extractor kShadowBandUpperBound = 1/3 precedent) so flat dark
        /// regions never read as fog (the shadow class returns first regardless).</summary>
        internal const float kFogMidLumaFloor = 1f / 3f;

        /// <summary>Half-extent of the local-gradient box neighborhood (1 = 3x3 box
        /// on the cached downsampled grid — the extractor fixed-grid discipline).</summary>
        private const int kGradientBoxRadius = 1;

        /// <summary>Chroma denominator floor (kStatEpsilon precedent, NamerStylePostFit.cs:201).</summary>
        private const float kChromaEpsilon = 1e-6f;

        /// <summary>Confidence of a clean albedo/shading texel (everything not
        /// contaminated or fog-weighted).</summary>
        private const float kCleanConfidence = 1f;

        /// <summary>Hard-zero weight of the contaminated classes (clipping,
        /// specular/bloom, emissive, deep shadow).</summary>
        private const float kContaminatedWeight = 0f;

        // -------- validation-metric constants (STYL-17) --------

        /// <summary>Effective white-balance knob range for the metric's normalization —
        /// mirrors the fit-side clamp kFittedWhiteBalanceLimit (NamerStylePostFit.cs:204).</summary>
        private const float kValidationWhiteBalanceRange = 50f;

        /// <summary>Percent-knob range for the metric's normalization — mirrors
        /// kParamRangeLimit (NamerStylePostFit.cs:203, URP's own ±100 slider range).</summary>
        private const float kValidationPercentKnobRange = 100f;

        /// <summary>Master-curve deviation normalizer: the curve grid spans [0,1] by
        /// construction, so a deviation of 1.0 is the full output range.</summary>
        private const float kValidationCurveRange = 1f;

        /// <summary>Uniform key-grid resolution the metric samples the master curve on —
        /// the SAME fixed 9-point grid MasterInverse maps indices through
        /// (kCurveKeyCount, NamerStylePostFit.cs:196), so metric and de-grade share
        /// one curve-domain discretization.</summary>
        private const int kValidationCurveGridResolution = 9;

        /// <summary>Percent scale of the rendered measure string (integer percent).</summary>
        private const float kPercentScale = 100f;

        /// <summary>Copy prefix of the STYL-17 "{measure}" fragment (UI-SPEC S17).</summary>
        private const string kValidationMeasurePrefix = "max knob deviation ";

        /// <summary>Knob display names of the validation metric, in fixed evaluation order.</summary>
        private const string kTemperatureKnobName = "Temperature";
        private const string kTintKnobName = "Tint";
        private const string kExposureKnobName = "PostExposureEv";
        private const string kContrastKnobName = "Contrast";
        private const string kSaturationKnobName = "Saturation";
        private const string kBloomKnobName = "BloomThreshold";
        private const string kMasterCurveKnobName = "MasterCurve";

        /// <summary>
        /// Decomposes the merged style asset's shared reference list into the grade
        /// estimate, the de-graded (or raw) palette anchors, and the classification
        /// confidence fractions (STYL-13/STYL-14). Pre-validates via
        /// <see cref="NamerStyleAsset.ClampToCaps"/> and requires at least
        /// <see cref="NamerEditorConstants.MinReferences"/> assigned references with
        /// weight &gt; 0 (weight-0 slots are skipped entirely — a zero weight adds
        /// exactly zero mass AND zero influence on the percentile cuts, the A4
        /// discipline). References are read one at a time with an isReadable
        /// pre-check — the first unreadable texture aborts with
        /// <see cref="NamerStyleDecomposeResult.ErrorTextureName"/> naming it and no
        /// exception escapes. <paramref name="shouldCancel"/> is polled between
        /// references; when null (the interactive path) a cancellable progress bar is
        /// shown instead. <paramref name="gradeOverride"/> non-null engages the D-02
        /// complement: the grade comes from the caller's exemplar-render fit (plan 06
        /// supplies it from the window seam), <see cref="NamerStyleDecomposeResult.GradeSourceExemplarName"/>
        /// records <paramref name="gradeOverrideExemplarName"/>, and ONE grade estimate
        /// feeds both the post block and the de-grade either way; null (the default)
        /// is the model-free neutral primary. The result is owned by the caller; the
        /// asset is never written here (the asset editor's RunDecompose owns that
        /// transaction).
        /// </summary>
        public static NamerStyleDecomposeResult DecomposeStyle(NamerStyleAsset asset, Func<bool> shouldCancel = null,
            NamerStylePostFitParams gradeOverride = null, string gradeOverrideExemplarName = null)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }

            asset.ClampToCaps();

            // The bare stamp: DecompositionFingerprint is the EXACT ComputeFingerprint
            // return (the NamerPostLook PostFitFingerprint idiom) — no suffix of any
            // kind, so writing it to the asset leaves IsDecompositionStale false
            // immediately after Decompose. PaletteSource drift is detected by the
            // separate PaletteSourceAtExtract seam, never by this fingerprint.
            string fingerprint = asset.ComputeFingerprint();

            if (CountPositiveWeightReferences(asset) < NamerEditorConstants.MinReferences)
            {
                return new NamerStyleDecomposeResult(cancelled: false, errorTextureName: null, tooFewReferences: true);
            }

            List<CachedReference> cached = new List<CachedReference>();
            try
            {
                // ================= PASS 1: read, downsample, cache, uniform accumulate =================
                // Two-pass necessity (RESEARCH Pattern 2): the percentile cuts and the
                // grade estimate must exist BEFORE per-pixel weighting — the first pass
                // accumulates grade stats with a UNIFORM confidence of 1 (whose
                // Luma/Max3 histograms, binned at the per-reference weight over ALL
                // texels, are also the percentile-cut source — cuts describe the whole
                // image, never the confident subset), the second pass re-accumulates
                // with the classifier's per-texel confidence.
                NamerStylePostStats uniformStats = new NamerStylePostStats();
                int referencesToRead = CountPositiveWeightReferences(asset);
                int processedReferences = 0;
                for (int i = 0; i < asset.References.Count; i++)
                {
                    Texture2D reference = asset.References[i];
                    if (reference == null)
                    {
                        continue;
                    }

                    float weight = i < asset.ReferenceWeights.Count
                        ? asset.ReferenceWeights[i]
                        : NamerEditorConstants.DefaultReferenceWeight;
                    if (weight <= 0f)
                    {
                        continue;
                    }

                    // OQ-1/T-05.3-03-03: GetPixels requires Read/Write; pre-check so the
                    // failure is a named-texture result, never a raw exception.
                    if (!reference.isReadable)
                    {
                        return new NamerStyleDecomposeResult(cancelled: false, errorTextureName: reference.name, tooFewReferences: false);
                    }

                    Color[] pixels;
                    try
                    {
                        pixels = reference.GetPixels();
                    }
                    catch (Exception)
                    {
                        return new NamerStyleDecomposeResult(cancelled: false, errorTextureName: reference.name, tooFewReferences: false);
                    }

                    Color[] grid = DownsampleSrgb(pixels, reference.width, reference.height, out int downWidth, out int downHeight);
                    pixels = null; // T-05.3-03-02: drop the per-ref raw buffer before the next reference.

                    CachedReference cachedReference = new CachedReference(grid, downWidth, downHeight, weight);
                    cached.Add(cachedReference);

                    // First-pass grade stats: uniform confidence 1 per texel via the
                    // per-pixel-weight seam (the overload exists for exactly this shape).
                    NamerStylePostFit.AccumulateReference(uniformStats, grid, UniformConfidence(grid.Length), weight);
                    processedReferences++;

                    bool cancelled;
                    if (shouldCancel != null)
                    {
                        // Injectable headless path (tests): no editor UI.
                        cancelled = shouldCancel();
                    }
                    else
                    {
                        // Interactive-editor path: visible, cancellable progress.
                        float progress = (float)processedReferences / referencesToRead;
                        cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "Decomposing NAMER Style",
                            "Reading " + reference.name,
                            progress);
                    }

                    if (cancelled)
                    {
                        return new NamerStyleDecomposeResult(cancelled: true, errorTextureName: null, tooFewReferences: false);
                    }
                }

                // ================= Percentile cuts (once, from the uniform pass-1 histograms) =================
                float p95Max3Cut = NamerStylePostFit.WeightedPercentile(
                    uniformStats.Max3SrgbHistogram, (float)uniformStats.TotalWeight, NamerStylePostFit.kDefaultBloomHighlightPercentile);
                float p5LumaCut = NamerStylePostFit.WeightedPercentile(
                    uniformStats.LumaHistogram, (float)uniformStats.TotalWeight, kShadowPercentile);

                // ================= Classification + confidence-weighted grade stats =================
                // Fixed order over the cached grids; the confidence-weighted estimate
                // REPLACES the uniform pass-1 stats (fixed order keeps determinism).
                NamerStylePostStats referenceStats = new NamerStylePostStats();
                for (int r = 0; r < cached.Count; r++)
                {
                    CachedReference cachedReference = cached[r];
                    cachedReference.LumaGrid = ComputeDecodedLumaGrid(cachedReference.Grid);
                    cachedReference.Confidence = ClassifyGrid(cachedReference, p95Max3Cut, p5LumaCut);
                    NamerStylePostFit.AccumulateReference(
                        referenceStats, cachedReference.Grid, cachedReference.Confidence, cachedReference.Weight);
                }

                // ================= GRADE (one estimate feeds post block AND de-grade) =================
                NamerStylePostFitParams grade;
                string gradeSourceExemplarName = "";
                if (gradeOverride == null)
                {
                    // D-02 primary: the shipped Fit verbatim over the synthetic neutral
                    // source — the same code path, only the source-stats constructor is new.
                    grade = NamerStylePostFit.Fit(
                        NamerStyleGradeMath.BuildNeutralSourceStats(), referenceStats, fingerprint);
                }
                else
                {
                    // D-02 complement (engaged only when supplied, never silent): the
                    // caller's exemplar-render fit, fingerprint recomputed over the
                    // shared list so staleness reads against the refs actually consumed.
                    grade = gradeOverride;
                    grade.Fingerprint = fingerprint;
                    gradeSourceExemplarName = gradeOverrideExemplarName ?? "";
                }

                // ================= PASS 2: de-grade + palette histogram + confidence mass =================
                // Per-channel curves cloned ONCE per decompose (ApplyToPostLook clone
                // discipline, NamerStylePostFit.cs:443-445); the master curve travels
                // inside the params argument via MasterKeys.
                AnimationCurve redCurve = CloneCurveOrNull(grade.RedKeys);
                AnimationCurve greenCurve = CloneCurveOrNull(grade.GreenKeys);
                AnimationCurve blueCurve = CloneCurveOrNull(grade.BlueKeys);
                bool deGradePalette = asset.PaletteSource == NamerEditorConstants.kPaletteSourceDeGraded;

                float[] paletteHistogram = new float[PaletteHistogramBinCount];
                double confidentMass = 0.0;
                double contaminatedMass = 0.0;
                double totalMass = 0.0;
                for (int r = 0; r < cached.Count; r++)
                {
                    CachedReference cachedReference = cached[r];
                    Color[] gridTexels = cachedReference.Grid;
                    for (int t = 0; t < gridTexels.Length; t++)
                    {
                        Color texel = gridTexels[t];
                        if (!IsFiniteTexel(texel))
                        {
                            continue; // T-05.3-03-01: NaN/Inf texels poison nothing.
                        }

                        // Mass bookkeeping runs over ALL texels (masked included) —
                        // the fractions describe the whole image.
                        totalMass += cachedReference.Weight;
                        float confidence = cachedReference.Confidence[t];
                        if (confidence >= kCleanConfidence)
                        {
                            confidentMass += cachedReference.Weight;
                        }
                        else
                        {
                            contaminatedMass += cachedReference.Weight;
                        }

                        if (confidence <= 0f)
                        {
                            continue;
                        }

                        float3 decoded = new float3(
                            NamerStyleExtractor.SrgbToLinear(texel.r),
                            NamerStyleExtractor.SrgbToLinear(texel.g),
                            NamerStyleExtractor.SrgbToLinear(texel.b));
                        float3 paletteColor = decoded;
                        if (deGradePalette)
                        {
                            // Transform-then-bin: the de-grade applies per TEXEL before
                            // binning (never to anchors after k-means). Raw mode skips
                            // ONLY this call — the confidence mask applies in BOTH modes.
                            paletteColor = NamerStyleGradeMath.DeGrade(decoded, grade, redCurve, greenCurve, blueCurve);
                        }

                        paletteHistogram[PaletteBinIndex(paletteColor)] += cachedReference.Weight * confidence;
                    }
                }

                // ================= PALETTE (shipped deterministic k-means, reused verbatim) =================
                NamerStyleAnchor[] anchors = NamerStyleExtractor.BuildAnchors(paletteHistogram);

                float confidentFraction = totalMass > 0.0 ? (float)(confidentMass / totalMass) : 0f;
                float contaminatedFraction = totalMass > 0.0 ? (float)(contaminatedMass / totalMass) : 0f;
                return new NamerStyleDecomposeResult(
                    grade, anchors, confidentFraction, contaminatedFraction, fingerprint, gradeSourceExemplarName);
            }
            finally
            {
                if (shouldCancel == null)
                {
                    // Only the interactive path ever showed a bar.
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        // ================= classification (RESEARCH Pattern 4) =================

        /// <summary>
        /// Per-texel classification weight of one cached-grid texel (pure — the test
        /// seam). Cues: <paramref name="rawMaxChannel"/>/<paramref name="rawMinChannel"/>
        /// bound the raw sRGB texel (the chroma metric is HSV-style (max-min)/max on
        /// raw sRGB), <paramref name="decodedLuma"/> is the decoded linear Rec.709
        /// luma, and <paramref name="localLumaGradient"/> is the 3x3 box gradient of
        /// the decoded-luma grid at the texel. Order per the Pattern 4 table: clipping
        /// (raw max >= 254/255) -> 0; above the p95 Max3 cut -> 0 (specular/bloom when
        /// desaturated, emissive when saturated — both highlight contaminants); deep
        /// shadow (luma <= p5 cut or <= the absolute floor) -> 0; fog/veil (low local
        /// gradient AND mid-high luma AND low chroma) -> kFogWeight; else clean
        /// albedo/shading -> 1. All thresholds are named constants (A5).
        /// </summary>
        internal static float ClassifyTexel(float rawMaxChannel, float rawMinChannel, float decodedLuma,
            float localLumaGradient, float p95Max3Cut, float p5LumaCut)
        {
            float chroma = (rawMaxChannel - rawMinChannel) / Mathf.Max(rawMaxChannel, kChromaEpsilon);
            if (rawMaxChannel >= kClipThresholdSrgb)
            {
                // Clipping — also the exact non-invertible set of the tonemap inverse.
                return kContaminatedWeight;
            }

            if (rawMaxChannel >= p95Max3Cut)
            {
                // Above the p95 highlight cut: specular/bloom when desaturated
                // (chroma <= kSpecularChromaMax) or emissive when saturated — both
                // carry the hard-zero weight (Pattern 4 rows 2-3; the chroma split
                // names the two contaminant classes so fixtures address each).
                return kContaminatedWeight;
            }

            if (decodedLuma <= p5LumaCut || decodedLuma <= kShadowLumaFloor)
            {
                // Deep shadow — noise-dominated and non-invertible near black.
                return kContaminatedWeight;
            }

            if (localLumaGradient < kFogGradientThreshold && decodedLuma >= kFogMidLumaFloor && chroma <= kSpecularChromaMax)
            {
                // Fog/veil (and transparency/additive veils, which fold here): flat,
                // mid-to-high luma, desaturated — conservative low weight, not zero.
                return kFogWeight;
            }

            return kCleanConfidence;
        }

        /// <summary>
        /// Classifies every texel of one cached grid (fixed serial order), returning
        /// the per-texel confidence array.
        /// </summary>
        private static float[] ClassifyGrid(CachedReference cachedReference, float p95Max3Cut, float p5LumaCut)
        {
            Color[] grid = cachedReference.Grid;
            float[] lumaGrid = cachedReference.LumaGrid;
            float[] confidence = new float[grid.Length];
            for (int t = 0; t < grid.Length; t++)
            {
                Color texel = grid[t];
                if (!IsFiniteTexel(texel))
                {
                    confidence[t] = kCleanConfidence;
                    continue; // non-finite texels are skipped by every accumulator anyway
                }

                float rawMax = Mathf.Max(texel.r, Mathf.Max(texel.g, texel.b));
                float rawMin = Mathf.Min(texel.r, Mathf.Min(texel.g, texel.b));
                int x = t % cachedReference.Width;
                int y = t / cachedReference.Width;
                confidence[t] = ClassifyTexel(
                    rawMax, rawMin, lumaGrid[t], LocalLumaGradient(lumaGrid, x, y, cachedReference.Width, cachedReference.Height),
                    p95Max3Cut, p5LumaCut);
            }

            return confidence;
        }

        /// <summary>
        /// Local luma box gradient at one texel: |center - mean| over the fixed 3x3
        /// in-bounds neighborhood (kGradientBoxRadius), fixed iteration order.
        /// </summary>
        private static float LocalLumaGradient(float[] lumaGrid, int x, int y, int width, int height)
        {
            float sum = 0f;
            int count = 0;
            for (int dy = -kGradientBoxRadius; dy <= kGradientBoxRadius; dy++)
            {
                int ny = y + dy;
                if (ny < 0 || ny >= height)
                {
                    continue;
                }

                for (int dx = -kGradientBoxRadius; dx <= kGradientBoxRadius; dx++)
                {
                    int nx = x + dx;
                    if (nx < 0 || nx >= width)
                    {
                        continue;
                    }

                    sum += lumaGrid[ny * width + nx];
                    count++;
                }
            }

            return Mathf.Abs(lumaGrid[y * width + x] - sum / count);
        }

        /// <summary>
        /// Decoded linear Rec.709 luma grid of a cached sRGB grid (fixed serial order).
        /// </summary>
        private static float[] ComputeDecodedLumaGrid(Color[] grid)
        {
            float[] lumaGrid = new float[grid.Length];
            for (int t = 0; t < grid.Length; t++)
            {
                Color texel = grid[t];
                float3 decoded = new float3(
                    NamerStyleExtractor.SrgbToLinear(texel.r),
                    NamerStyleExtractor.SrgbToLinear(texel.g),
                    NamerStyleExtractor.SrgbToLinear(texel.b));
                lumaGrid[t] = NamerStylePostMath.Luma709(decoded);
            }

            return lumaGrid;
        }

        // ================= validation metric (STYL-17, pure statics) =================

        /// <summary>
        /// The validation metric: max over knobs of |refit knob - identity| normalized
        /// by each knob's effective range — |T|/50, |Tint|/50, |EV|/10,
        /// |Contrast|/100, |Saturation|/100, |BloomThreshold|/MaxBloomThreshold, and
        /// the master curve's max deviation from the neutral diagonal sampled on the
        /// fixed 9-point key grid MasterInverse maps through (normalized by 1.0).
        /// Identity = all knobs zero + the neutral diagonal curve. Pure static: no
        /// asset/window/engine-state arguments, no curve evaluation outside the plain
        /// key array (linear interpolation between key values — the MasterInverse
        /// discipline itself, NamerStylePostFit.cs:598-619). Fixed knob order, strict
        /// less-than replacement (ties keep the earlier knob). Consumed by plan 06's
        /// Validate Look; the mismatch is EVIDENCE, never an automatic change.
        /// </summary>
        public static NamerStyleValidationMeasure ComputeValidationMeasure(NamerStylePostFitParams refit)
        {
            if (refit == null)
            {
                throw new ArgumentNullException(nameof(refit));
            }

            float measure = Mathf.Abs(refit.Temperature) / kValidationWhiteBalanceRange;
            string knobName = kTemperatureKnobName;

            float candidate = Mathf.Abs(refit.Tint) / kValidationWhiteBalanceRange;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kTintKnobName;
            }

            candidate = Mathf.Abs(refit.PostExposureEv) / NamerEditorConstants.MaxFittedPostExposureEv;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kExposureKnobName;
            }

            candidate = Mathf.Abs(refit.Contrast) / kValidationPercentKnobRange;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kContrastKnobName;
            }

            candidate = Mathf.Abs(refit.Saturation) / kValidationPercentKnobRange;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kSaturationKnobName;
            }

            candidate = Mathf.Abs(refit.BloomThreshold) / NamerEditorConstants.MaxBloomThreshold;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kBloomKnobName;
            }

            candidate = MaxMasterCurveDeviation(refit.MasterKeys) / kValidationCurveRange;
            if (candidate > measure)
            {
                measure = candidate;
                knobName = kMasterCurveKnobName;
            }

            return new NamerStyleValidationMeasure(measure, knobName);
        }

        /// <summary>
        /// The "{measure}" copy fragment of the STYL-17 validation row:
        /// "max knob deviation {P}% ({knob})" (integer percent, invariant formatting).
        /// </summary>
        public static string BuildValidationMeasureString(NamerStylePostFitParams refit)
        {
            NamerStyleValidationMeasure validationMeasure = ComputeValidationMeasure(refit);
            int percent = Mathf.RoundToInt(validationMeasure.Measure * kPercentScale);
            return kValidationMeasurePrefix + percent.ToString(CultureInfo.InvariantCulture)
                + "% (" + validationMeasure.KnobName + ")";
        }

        /// <summary>
        /// True when the validation measure sits within the mismatch threshold
        /// (<see cref="NamerEditorConstants.kValidationMismatchThreshold"/>, landed by
        /// plan 02) — plan 06's Warning HelpBox renders the complement.
        /// </summary>
        internal static bool IsWithinValidationTolerance(NamerStyleValidationMeasure measure)
        {
            return measure.Measure < NamerEditorConstants.kValidationMismatchThreshold;
        }

        /// <summary>
        /// Max |value - time| of the master keys over the fixed 9-point uniform grid;
        /// a null/empty key array (identity diagonal resolution) deviates nothing.
        /// </summary>
        private static float MaxMasterCurveDeviation(Keyframe[] masterKeys)
        {
            if (masterKeys == null || masterKeys.Length == 0)
            {
                return 0f;
            }

            float maxDeviation = 0f;
            for (int i = 0; i < kValidationCurveGridResolution; i++)
            {
                float t = i / (float)(kValidationCurveGridResolution - 1);
                float deviation = Mathf.Abs(EvaluateKeysLinear(masterKeys, t) - t);
                if (deviation > maxDeviation)
                {
                    maxDeviation = deviation;
                }
            }

            return maxDeviation;
        }

        /// <summary>
        /// Linear interpolation between adjacent key values at time t (edge-clamped;
        /// degenerate zero-span segments keep the left value) — the same between-keys
        /// linear discipline MasterInverse applies, kept pure so the metric needs no
        /// engine curve evaluation.
        /// </summary>
        private static float EvaluateKeysLinear(Keyframe[] keys, float t)
        {
            int last = keys.Length - 1;
            if (t <= keys[0].time)
            {
                return keys[0].value;
            }

            if (t >= keys[last].time)
            {
                return keys[last].value;
            }

            for (int i = 0; i < last; i++)
            {
                if (t >= keys[i].time && t <= keys[i + 1].time)
                {
                    float span = keys[i + 1].time - keys[i].time;
                    if (span <= 0f)
                    {
                        return keys[i].value;
                    }

                    float within = (t - keys[i].time) / span;
                    return keys[i].value + (keys[i + 1].value - keys[i].value) * within;
                }
            }

            return keys[last].value;
        }

        // ================= shared helpers =================

        /// <summary>
        /// Number of assigned reference slots carrying a positive weight (the S4
        /// min-3 gate's honest count — weight-0 slots are not "there"). Internal:
        /// the merged-asset editor's DecomposeButtonState seam + S6 status line
        /// share this single counter (05.3-06 seam widening,
        /// EvaluateRefitMaxError precedent).
        /// </summary>
        internal static int CountPositiveWeightReferences(NamerStyleAsset asset)
        {
            int count = 0;
            for (int i = 0; i < asset.References.Count; i++)
            {
                if (asset.References[i] == null)
                {
                    continue;
                }

                float weight = i < asset.ReferenceWeights.Count
                    ? asset.ReferenceWeights[i]
                    : NamerEditorConstants.DefaultReferenceWeight;
                if (weight > 0f)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Box-downsamples raw sRGB pixels to at most
        /// <see cref="NamerStyleExtractor.kExtractResolutionCap"/> per axis — the
        /// extractor's fixed integer grid boundaries and fixed accumulation order
        /// verbatim (AccumulateHistogram :245-282 discipline), averaging in the RAW
        /// sRGB domain because the cached grid IS the sRGB texel source every later
        /// consumer decodes from (decode/de-grade happen per texel in pass 2).
        /// </summary>
        private static Color[] DownsampleSrgb(Color[] pixels, int width, int height, out int downWidth, out int downHeight)
        {
            downWidth = Math.Min(width, NamerStyleExtractor.kExtractResolutionCap);
            downHeight = Math.Min(height, NamerStyleExtractor.kExtractResolutionCap);
            Color[] grid = new Color[downWidth * downHeight];
            for (int ty = 0; ty < downHeight; ty++)
            {
                int sy0 = (int)((long)ty * height / downHeight);
                int sy1 = Math.Max(sy0 + 1, (int)((long)(ty + 1) * height / downHeight));
                for (int tx = 0; tx < downWidth; tx++)
                {
                    int sx0 = (int)((long)tx * width / downWidth);
                    int sx1 = Math.Max(sx0 + 1, (int)((long)(tx + 1) * width / downWidth));

                    float sumR = 0f;
                    float sumG = 0f;
                    float sumB = 0f;
                    long count = 0;
                    for (int sy = sy0; sy < sy1; sy++)
                    {
                        int rowStart = sy * width;
                        for (int sx = sx0; sx < sx1; sx++)
                        {
                            Color pixel = pixels[rowStart + sx];
                            sumR += pixel.r;
                            sumG += pixel.g;
                            sumB += pixel.b;
                            count++;
                        }
                    }

                    float invCount = 1f / count;
                    grid[ty * downWidth + tx] = new Color(sumR * invCount, sumG * invCount, sumB * invCount, 1f);
                }
            }

            return grid;
        }

        /// <summary>
        /// A uniform confidence array of the given length (pass 1's first-pass weight
        /// of 1 per texel).
        /// </summary>
        private static float[] UniformConfidence(int length)
        {
            float[] weights = new float[length];
            for (int i = 0; i < length; i++)
            {
                weights[i] = 1f;
            }

            return weights;
        }

        /// <summary>
        /// Total bin count of the 16^3 linear palette histogram
        /// (<see cref="NamerStyleExtractor.kHistogramBinsPerChannel"/> per channel).
        /// </summary>
        private static int PaletteHistogramBinCount
        {
            get
            {
                return NamerStyleExtractor.kHistogramBinsPerChannel
                    * NamerStyleExtractor.kHistogramBinsPerChannel
                    * NamerStyleExtractor.kHistogramBinsPerChannel;
            }
        }

        /// <summary>
        /// Clamp-then-bin index of a (possibly HDR) linear color into the 16^3 palette
        /// histogram — the extractor's ClampToBinIndex shape (values above 1 from the
        /// de-grade's raw-linear output clamp into the top bin).
        /// </summary>
        private static int PaletteBinIndex(float3 c)
        {
            int rBin = Math.Min((int)(Mathf.Clamp01(c.x) * NamerStyleExtractor.kHistogramBinsPerChannel), NamerStyleExtractor.kHistogramBinsPerChannel - 1);
            int gBin = Math.Min((int)(Mathf.Clamp01(c.y) * NamerStyleExtractor.kHistogramBinsPerChannel), NamerStyleExtractor.kHistogramBinsPerChannel - 1);
            int bBin = Math.Min((int)(Mathf.Clamp01(c.z) * NamerStyleExtractor.kHistogramBinsPerChannel), NamerStyleExtractor.kHistogramBinsPerChannel - 1);
            return (rBin * NamerStyleExtractor.kHistogramBinsPerChannel + gBin) * NamerStyleExtractor.kHistogramBinsPerChannel + bBin;
        }

        /// <summary>
        /// Clone of a fit's key array for one per-channel curve (ApplyToPostLook clone
        /// discipline — arrays are never shared across consumers); null/empty keys
        /// resolve to a null curve, which the grade math treats as the identity
        /// diagonal.
        /// </summary>
        private static AnimationCurve CloneCurveOrNull(Keyframe[] keys)
        {
            if (keys == null || keys.Length == 0)
            {
                return null;
            }

            return new AnimationCurve(keys);
        }

        /// <summary>
        /// True when all color channels of a texel are finite (the IsFinitePixel skip
        /// discipline, mirrored locally so the frozen estimator file needs no further
        /// widening).
        /// </summary>
        private static bool IsFiniteTexel(Color texel)
        {
            return float.IsFinite(texel.r) && float.IsFinite(texel.g) && float.IsFinite(texel.b);
        }

        /// <summary>
        /// One cached pass-1 reference: the ≤128-cap sRGB grid, its extents, its
        /// per-reference weight, and the pass-1.5 derived grids (decoded luma +
        /// per-texel confidence) filled in fixed order.
        /// </summary>
        private sealed class CachedReference
        {
            public readonly Color[] Grid;
            public readonly int Width;
            public readonly int Height;
            public readonly float Weight;
            public float[] LumaGrid;
            public float[] Confidence;

            public CachedReference(Color[] grid, int width, int height, float weight)
            {
                Grid = grid;
                Width = width;
                Height = height;
                Weight = weight;
            }
        }
    }
}

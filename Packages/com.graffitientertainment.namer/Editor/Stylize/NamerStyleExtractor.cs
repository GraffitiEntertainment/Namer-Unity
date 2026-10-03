using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Result of a style extraction. <see cref="Anchors"/> and <see cref="Stats"/> are
    /// managed arrays owned by the caller; they are null when the extract failed or was
    /// cancelled (no partial result may be cached or written to the profile).
    /// </summary>
    public sealed class NamerStyleExtractResult
    {
        /// <summary>Extracted palette anchors (kAnchorCount entries); null when failed/cancelled.</summary>
        public readonly NamerStyleAnchor[] Anchors;

        /// <summary>Merged aggregate statistics (STYL-02); null when failed/cancelled.</summary>
        public readonly NamerStyleStats Stats;

        /// <summary>Fingerprint of the refs + weights captured at extract time (OQ-4); empty when failed/cancelled.</summary>
        public readonly string Fingerprint;

        /// <summary>True when the user cancelled before completion; no partial result is returned.</summary>
        public readonly bool Cancelled;

        /// <summary>Name of the first reference texture that could not be read; null otherwise.</summary>
        public readonly string ErrorTextureName;

        /// <summary>True when fewer than <see cref="NamerEditorConstants.MinReferences"/> refs were assigned.</summary>
        public readonly bool TooFewReferences;

        /// <summary>Success shape (D-04: one merged aggregate set).</summary>
        public NamerStyleExtractResult(NamerStyleAnchor[] anchors, NamerStyleStats stats, string fingerprint)
        {
            Anchors = anchors;
            Stats = stats;
            Fingerprint = fingerprint;
            Cancelled = false;
            ErrorTextureName = null;
            TooFewReferences = false;
        }

        /// <summary>Failure shape: cancelled, unreadable texture, or too few references.</summary>
        public NamerStyleExtractResult(bool cancelled, string errorTextureName, bool tooFewReferences)
        {
            Anchors = null;
            Stats = null;
            Fingerprint = "";
            Cancelled = cancelled;
            ErrorTextureName = errorTextureName;
            TooFewReferences = tooFewReferences;
        }
    }

    /// <summary>
    /// Deterministic CPU style extraction (STYL-02, D-04): reads the profile's 3-10
    /// weighted reference images ONE AT A TIME, downsamples each to a fixed cap, and
    /// accumulates ONE merged weighted linear-RGB histogram, then derives the palette
    /// anchors (top-K init + fixed Lloyd iterations, no random number generation
    /// anywhere) and the STYL-02 statistics. Determinism contract: fixed ref order,
    /// fixed accumulation order, index-ordered tiebreaks — two Extracts on the same
    /// refs are element-wise identical (the NamerAOBaker Bake_IsDeterministic bar).
    ///
    /// Synchronous and cancellable; runs ONLY from the explicit Extract action, never
    /// on the debounced preview path (D-04). Extraction reads references and never
    /// writes any asset.
    /// </summary>
    public static class NamerStyleExtractor
    {
        /// <summary>Per-axis downsample cap for each reference (memory bound, T-05-02).</summary>
        public const int kExtractResolutionCap = 128;

        /// <summary>Linear-RGB histogram bins per channel (16^3 total).</summary>
        public const int kHistogramBinsPerChannel = 16;

        /// <summary>Palette anchor slots (6 chromatic + 2 achromatic reserved at init).</summary>
        public const int kAnchorCount = 8;

        /// <summary>Fixed Lloyd (k-means) iterations over the bin centers.</summary>
        public const int kKMeansIterations = 8;

        /// <summary>Achromatic/low-sat anchor slots reserved at init (Pattern 5 gray/black/white matchability).</summary>
        public const int kAchromaticAnchorSlots = 2;

        /// <summary>Chromatic anchor slots reserved at init.</summary>
        public const int kChromaticAnchorSlots = kAnchorCount - kAchromaticAnchorSlots;

        private const int kHistogramBinCount = kHistogramBinsPerChannel * kHistogramBinsPerChannel * kHistogramBinsPerChannel;
        private const float kLumaR = 0.2126f;
        private const float kLumaG = 0.7152f;
        private const float kLumaB = 0.0722f;
        private const float kSrgbLinearCutoff = 0.04045f;
        private const float kSrgbLinearDivisor = 12.92f;
        private const float kSrgbSegmentOffset = 0.055f;
        private const float kSrgbSegmentScale = 1.055f;
        private const float kSrgbDecodeExponent = 2.4f;
        private const float kAchromaticSaturationThreshold = 0.15f;
        private const float kHueFamilyDegrees = 30f;
        private const int kHueFamilyCount = 12;
        private const float kWarmHueStartDegrees = 330f;
        private const float kWarmHueEndDegrees = 90f;
        private const float kCoolHueStartDegrees = 180f;
        private const float kCoolHueEndDegrees = 270f;
        private const float kShadowBandUpperBound = 1f / 3f;
        private const float kHighlightBandLowerBound = 2f / 3f;
        private const float kPercentile10 = 0.1f;
        private const float kPercentile90 = 0.9f;
        private const float kContrastDenominatorFloor = 0.001f;
        private const float kNeutralGrayChannel = 0.5f;
        private const float kDegreesPerHueUnit = 360f;

        /// <summary>
        /// Extracts the merged palette/style statistics from the profile's assigned
        /// references. Pre-validates via <see cref="NamerStyleProfile.ClampToCaps"/>
        /// (T-05-03) and requires at least <see cref="NamerEditorConstants.MinReferences"/>
        /// assigned refs (the UI prevents this via the disabled Extract button; the API
        /// reports it honestly). References are read one at a time with an
        /// isReadable pre-check (OQ-1) — the first unreadable texture aborts with
        /// <see cref="NamerStyleExtractResult.ErrorTextureName"/> naming it and no
        /// exception escapes. <paramref name="shouldCancel"/> is polled between
        /// references; when null (the interactive path) a cancellable progress bar is
        /// shown instead. The returned anchors/stats/fingerprint are owned by the caller.
        /// </summary>
        public static NamerStyleExtractResult Extract(NamerStyleProfile profile, Func<bool> shouldCancel = null)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            profile.ClampToCaps();

            if (profile.AssignedReferenceCount < NamerEditorConstants.MinReferences)
            {
                return new NamerStyleExtractResult(cancelled: false, errorTextureName: null, tooFewReferences: true);
            }

            float[] histogram = new float[kHistogramBinCount];
            try
            {
                int processedReferences = 0;
                for (int i = 0; i < profile.References.Count; i++)
                {
                    Texture2D reference = profile.References[i];
                    if (reference == null)
                    {
                        continue;
                    }

                    float weight = i < profile.ReferenceWeights.Count
                        ? profile.ReferenceWeights[i]
                        : NamerEditorConstants.DefaultReferenceWeight;

                    // OQ-1: GetPixels requires Read/Write; pre-check so the failure is a
                    // named-texture result, never a raw exception (T-05-01).
                    if (!reference.isReadable)
                    {
                        return new NamerStyleExtractResult(cancelled: false, errorTextureName: reference.name, tooFewReferences: false);
                    }

                    Color[] pixels;
                    try
                    {
                        pixels = reference.GetPixels();
                    }
                    catch (Exception)
                    {
                        return new NamerStyleExtractResult(cancelled: false, errorTextureName: reference.name, tooFewReferences: false);
                    }

                    bool isSrgb = IsSrgbReference(reference);
                    AccumulateHistogram(pixels, reference.width, reference.height, isSrgb, weight, histogram);
                    pixels = null; // T-05-02: drop the per-ref buffer before the next reference.

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
                        float progress = (float)(i + 1) / profile.References.Count;
                        cancelled = EditorUtility.DisplayCancelableProgressBar(
                            "Extracting NAMER Style",
                            "Reading " + reference.name,
                            progress);
                    }

                    if (cancelled)
                    {
                        return new NamerStyleExtractResult(cancelled: true, errorTextureName: null, tooFewReferences: false);
                    }
                }

                NamerStyleAnchor[] anchors = BuildAnchors(histogram);
                NamerStyleStats stats = BuildStats(histogram, anchors, processedReferences);
                return new NamerStyleExtractResult(anchors, stats, profile.ComputeFingerprint());
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

        /// <summary>
        /// sRGB detection reads <see cref="TextureImporter.sRGBTexture"/> through the
        /// asset path (the Phase 02 WR-04 contract: the texture format property never
        /// reports sRGB on this Unity version). Runtime-created textures (no asset
        /// path) are treated as linear.
        /// </summary>
        private static bool IsSrgbReference(Texture2D reference)
        {
            string assetPath = AssetDatabase.GetAssetPath(reference);
            if (string.IsNullOrEmpty(assetPath))
            {
                return false;
            }

            TextureImporter importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            return importer != null && importer.sRGBTexture;
        }

        /// <summary>
        /// Box-downsamples the source pixels to at most <see cref="kExtractResolutionCap"/>
        /// per axis (fixed integer grid boundaries, fixed accumulation order — the
        /// VertexColorFitter fixed-grid discipline) and accumulates the weighted
        /// linear-RGB histogram. sRGB references are converted with fixed float math
        /// BEFORE binning; weights apply at extract time (A4) — a zero weight adds
        /// exactly zero mass.
        /// </summary>
        private static void AccumulateHistogram(Color[] pixels, int width, int height, bool isSrgb, float weight, float[] histogram)
        {
            int downWidth = Math.Min(width, kExtractResolutionCap);
            int downHeight = Math.Min(height, kExtractResolutionCap);

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
                            float r = pixel.r;
                            float g = pixel.g;
                            float b = pixel.b;
                            if (isSrgb)
                            {
                                r = SrgbToLinear(r);
                                g = SrgbToLinear(g);
                                b = SrgbToLinear(b);
                            }

                            sumR += r;
                            sumG += g;
                            sumB += b;
                            count++;
                        }
                    }

                    float invCount = 1f / count;
                    int rBin = ClampToBinIndex(sumR * invCount);
                    int gBin = ClampToBinIndex(sumG * invCount);
                    int bBin = ClampToBinIndex(sumB * invCount);
                    histogram[BinIndex(rBin, gBin, bBin)] += weight;
                }
            }
        }

        /// <summary>Fixed-float sRGB to linear conversion (EOTF), never a per-platform lookup.</summary>
        internal static float SrgbToLinear(float channel)
        {
            if (channel <= kSrgbLinearCutoff)
            {
                return channel / kSrgbLinearDivisor;
            }

            return Mathf.Pow((channel + kSrgbSegmentOffset) / kSrgbSegmentScale, kSrgbDecodeExponent);
        }

        private static int ClampToBinIndex(float channelValue)
        {
            int bin = (int)(Mathf.Clamp01(channelValue) * kHistogramBinsPerChannel);
            return Math.Min(bin, kHistogramBinsPerChannel - 1);
        }

        private static int BinIndex(int rBin, int gBin, int bBin)
        {
            return (rBin * kHistogramBinsPerChannel + gBin) * kHistogramBinsPerChannel + bBin;
        }

        private static Color BinCenterColor(int binIndex)
        {
            int bBin = binIndex % kHistogramBinsPerChannel;
            int remainder = binIndex / kHistogramBinsPerChannel;
            int gBin = remainder % kHistogramBinsPerChannel;
            int rBin = remainder / kHistogramBinsPerChannel;
            float scale = 1f / kHistogramBinsPerChannel;
            return new Color((rBin + 0.5f) * scale, (gBin + 0.5f) * scale, (bBin + 0.5f) * scale, 1f);
        }

        /// <summary>
        /// Builds the <see cref="kAnchorCount"/> palette anchors: deterministic
        /// hue-quantile init — the occupied chromatic bins are walked in hue order
        /// (ties by bin index ascending, never dictionary-iteration order) and the
        /// <see cref="kChromaticAnchorSlots"/> chromatic seeds are placed at
        /// equal-mass quantile targets along that walk, so the anchors tile the
        /// populated chromatic hue ranges as a gradient instead of collapsing onto
        /// the heaviest clusters. Duplicate seed bins deduplicate, remaining
        /// chromatic slots backfill from the mass-ranked bins, and
        /// <see cref="kAchromaticAnchorSlots"/> slots stay reserved for
        /// achromatic/low-sat bins (ranked by mass), followed by
        /// <see cref="kKMeansIterations"/> fixed Lloyd iterations over the bin
        /// centers. No random number generation anywhere.
        /// Internal (05.3-03): reused verbatim by <see cref="NamerStyleDecomposer"/>
        /// over the de-graded histogram (D-02/D-04 — one palette-clustering home, so
        /// the decomposer's anchors cannot drift from the extractor's).
        /// </summary>
        internal static NamerStyleAnchor[] BuildAnchors(float[] histogram)
        {
            List<int> chromaticBins = new List<int>();
            List<int> achromaticBins = new List<int>();
            float[] chromaticHueDegrees = new float[kHistogramBinCount];
            for (int bin = 0; bin < kHistogramBinCount; bin++)
            {
                if (histogram[bin] > 0f)
                {
                    Color center = BinCenterColor(bin);
                    float hueDegrees;
                    float saturation;
                    RgbToHueSaturation(center.r, center.g, center.b, out hueDegrees, out saturation);
                    if (saturation >= kAchromaticSaturationThreshold)
                    {
                        chromaticBins.Add(bin);
                        chromaticHueDegrees[bin] = hueDegrees;
                    }
                    else
                    {
                        achromaticBins.Add(bin);
                    }
                }
            }

            IComparer<int> massOrder = new BinMassComparer(histogram);
            chromaticBins.Sort(massOrder);
            achromaticBins.Sort(massOrder);

            // Chromatic seeds at equal-mass hue quantiles (05.3 option B): walk the
            // occupied chromatic bins in hue order and seed each chromatic slot at
            // the first unseeded bin whose cumulative mass reaches (slot + 0.5) /
            // slots of the total. A populated hue range carrying moderate mass (the
            // live references' greens: 21.6% of texels across hue 0.251..0.473)
            // earns anchors inside the range, where top-K-by-mass init starved it
            // behind heavier warm clusters and the hue-only palette transfer then
            // pinned those greens to a single olive anchor. Seeds only ever land on
            // occupied bins, so even a Lloyd-unclaimed seed reports a populated hue.
            List<int> hueOrder = new List<int>(chromaticBins);
            hueOrder.Sort((a, b) =>
            {
                int byHue = chromaticHueDegrees[a].CompareTo(chromaticHueDegrees[b]);
                return byHue != 0 ? byHue : a.CompareTo(b);
            });
            float totalChromaticMass = 0f;
            for (int i = 0; i < hueOrder.Count; i++)
            {
                totalChromaticMass += histogram[hueOrder[i]];
            }

            List<int> initBins = new List<int>(kAnchorCount);
            for (int slot = 0; slot < kChromaticAnchorSlots && hueOrder.Count > 0; slot++)
            {
                float target = (slot + 0.5f) * totalChromaticMass / kChromaticAnchorSlots;
                float cumulative = 0f;
                for (int i = 0; i < hueOrder.Count; i++)
                {
                    cumulative += histogram[hueOrder[i]];
                    if (cumulative >= target && !initBins.Contains(hueOrder[i]))
                    {
                        initBins.Add(hueOrder[i]);
                        break;
                    }
                }
            }

            // Achromatic slots stay mass-ranked; any still-empty slot backfills from
            // the other pool's ranked leftovers (skipping already-seeded bins), and
            // any remaining slot seeds at neutral gray (weight zero after Lloyd).
            int achromaticTake = Math.Min(kAchromaticAnchorSlots, achromaticBins.Count);
            for (int i = 0; i < achromaticTake; i++)
            {
                initBins.Add(achromaticBins[i]);
            }

            for (int i = 0; i < chromaticBins.Count && initBins.Count < kAnchorCount; i++)
            {
                if (!initBins.Contains(chromaticBins[i]))
                {
                    initBins.Add(chromaticBins[i]);
                }
            }

            for (int i = 0; i < achromaticBins.Count && initBins.Count < kAnchorCount; i++)
            {
                if (!initBins.Contains(achromaticBins[i]))
                {
                    initBins.Add(achromaticBins[i]);
                }
            }

            Color[] centers = new Color[kAnchorCount];
            for (int i = 0; i < kAnchorCount; i++)
            {
                centers[i] = i < initBins.Count
                    ? BinCenterColor(initBins[i])
                    : new Color(kNeutralGrayChannel, kNeutralGrayChannel, kNeutralGrayChannel, 1f);
            }

            float[] weights = new float[kAnchorCount];
            for (int iteration = 0; iteration < kKMeansIterations; iteration++)
            {
                float[] sumR = new float[kAnchorCount];
                float[] sumG = new float[kAnchorCount];
                float[] sumB = new float[kAnchorCount];
                float[] sumW = new float[kAnchorCount];
                for (int bin = 0; bin < kHistogramBinCount; bin++)
                {
                    float mass = histogram[bin];
                    if (mass <= 0f)
                    {
                        continue;
                    }

                    Color center = BinCenterColor(bin);
                    int nearest = 0;
                    float nearestDistance = float.MaxValue;
                    for (int a = 0; a < kAnchorCount; a++)
                    {
                        float dr = center.r - centers[a].r;
                        float dg = center.g - centers[a].g;
                        float db = center.b - centers[a].b;
                        float distance = dr * dr + dg * dg + db * db;
                        if (distance < nearestDistance)
                        {
                            nearestDistance = distance;
                            nearest = a; // strict less-than: ties keep the lowest anchor index.
                        }
                    }

                    sumR[nearest] += center.r * mass;
                    sumG[nearest] += center.g * mass;
                    sumB[nearest] += center.b * mass;
                    sumW[nearest] += mass;
                }

                for (int a = 0; a < kAnchorCount; a++)
                {
                    if (sumW[a] > 0f)
                    {
                        centers[a] = new Color(sumR[a] / sumW[a], sumG[a] / sumW[a], sumB[a] / sumW[a], 1f);
                    }

                    if (iteration == kKMeansIterations - 1)
                    {
                        weights[a] = sumW[a];
                    }
                }
            }

            var anchors = new NamerStyleAnchor[kAnchorCount];
            for (int a = 0; a < kAnchorCount; a++)
            {
                float hueDegrees;
                float saturation;
                RgbToHueSaturation(centers[a].r, centers[a].g, centers[a].b, out hueDegrees, out saturation);
                anchors[a] = new NamerStyleAnchor
                {
                    LinearColor = centers[a],
                    Hue = hueDegrees / kDegreesPerHueUnit,
                    Saturation = saturation,
                    Luma = Luminance(centers[a].r, centers[a].g, centers[a].b),
                    Weight = weights[a],
                };
            }

            return anchors;
        }

        /// <summary>
        /// Derives the merged STYL-02 statistics from the same histogram plus anchors
        /// (05-RESEARCH Pattern 4b): Rec.709 luminance, shadow/mid/highlight bands at
        /// 1/3 and 2/3, warm [330,90] / cool [180,270] degree sectors with red
        /// wraparound (chromatic bins only), luma p90/p10 contrast, and the
        /// occupied-bin palette density.
        /// </summary>
        private static NamerStyleStats BuildStats(float[] histogram, NamerStyleAnchor[] anchors, int referenceCount)
        {
            var stats = new NamerStyleStats();
            stats.ReferenceCount = referenceCount;

            float totalWeight = 0f;
            foreach (float mass in histogram)
            {
                totalWeight += mass;
            }

            if (totalWeight <= 0f)
            {
                return stats; // all-zero weights: honest empty stats, never a division by zero.
            }

            float saturationSum = 0f;
            float shadowWeight = 0f;
            float midtoneWeight = 0f;
            float highlightWeight = 0f;
            Color shadowSum = new Color(0f, 0f, 0f, 0f);
            Color midtoneSum = new Color(0f, 0f, 0f, 0f);
            Color highlightSum = new Color(0f, 0f, 0f, 0f);
            float warmEnergy = 0f;
            float coolEnergy = 0f;
            int occupiedBins = 0;

            List<int> occupiedByLuma = new List<int>();
            float[] binLuma = new float[kHistogramBinCount];
            for (int bin = 0; bin < kHistogramBinCount; bin++)
            {
                float mass = histogram[bin];
                if (mass <= 0f)
                {
                    continue;
                }

                occupiedBins++;
                Color center = BinCenterColor(bin);
                float luma = Luminance(center.r, center.g, center.b);
                binLuma[bin] = luma;
                occupiedByLuma.Add(bin);

                float hueDegrees;
                float saturation;
                RgbToHueSaturation(center.r, center.g, center.b, out hueDegrees, out saturation);
                saturationSum += mass * saturation;

                if (luma < kShadowBandUpperBound)
                {
                    shadowWeight += mass;
                    shadowSum += new Color(center.r * mass, center.g * mass, center.b * mass, 0f);
                }
                else if (luma < kHighlightBandLowerBound)
                {
                    midtoneWeight += mass;
                    midtoneSum += new Color(center.r * mass, center.g * mass, center.b * mass, 0f);
                }
                else
                {
                    highlightWeight += mass;
                    highlightSum += new Color(center.r * mass, center.g * mass, center.b * mass, 0f);
                }

                if (saturation >= kAchromaticSaturationThreshold)
                {
                    if (hueDegrees >= kWarmHueStartDegrees || hueDegrees <= kWarmHueEndDegrees)
                    {
                        warmEnergy += mass * luma;
                    }

                    if (hueDegrees >= kCoolHueStartDegrees && hueDegrees <= kCoolHueEndDegrees)
                    {
                        coolEnergy += mass * luma;
                    }
                }
            }

            occupiedByLuma.Sort(new BinLumaComparer(binLuma));
            float p10 = PercentileLuma(occupiedByLuma, histogram, binLuma, totalWeight, kPercentile10);
            float p90 = PercentileLuma(occupiedByLuma, histogram, binLuma, totalWeight, kPercentile90);

            float warmCoolTotal = warmEnergy + coolEnergy;
            stats.MeanSaturation = saturationSum / totalWeight;
            stats.ShadowFraction = shadowWeight / totalWeight;
            stats.MidtoneFraction = midtoneWeight / totalWeight;
            stats.HighlightFraction = highlightWeight / totalWeight;
            stats.ShadowColor = BandColor(shadowSum, shadowWeight);
            stats.MidtoneColor = BandColor(midtoneSum, midtoneWeight);
            stats.HighlightColor = BandColor(highlightSum, highlightWeight);
            stats.WarmCoolBias = warmCoolTotal > 0f ? (warmEnergy - coolEnergy) / warmCoolTotal : 0f;
            stats.Contrast = p90 / Mathf.Max(p10, kContrastDenominatorFloor);
            stats.PaletteDensity = (float)occupiedBins / kHistogramBinCount;
            stats.AnchorCount = CountAnchorsWithMass(anchors);
            stats.HueFamilyCount = CountHueFamilies(anchors);
            return stats;
        }

        private static int CountAnchorsWithMass(NamerStyleAnchor[] anchors)
        {
            int count = 0;
            for (int a = 0; a < anchors.Length; a++)
            {
                if (anchors[a].Weight > 0f)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountHueFamilies(NamerStyleAnchor[] anchors)
        {
            bool[] sectors = new bool[kHueFamilyCount];
            for (int a = 0; a < anchors.Length; a++)
            {
                if (anchors[a].Weight > 0f && anchors[a].Saturation >= kAchromaticSaturationThreshold)
                {
                    int sector = Mathf.Clamp((int)(anchors[a].Hue * kDegreesPerHueUnit / kHueFamilyDegrees), 0, kHueFamilyCount - 1);
                    sectors[sector] = true;
                }
            }

            int count = 0;
            for (int s = 0; s < sectors.Length; s++)
            {
                if (sectors[s])
                {
                    count++;
                }
            }

            return count;
        }

        private static Color BandColor(Color weightedSum, float bandWeight)
        {
            if (bandWeight <= 0f)
            {
                return new Color(0f, 0f, 0f, 0f);
            }

            return new Color(weightedSum.r / bandWeight, weightedSum.g / bandWeight, weightedSum.b / bandWeight, 1f);
        }

        private static float PercentileLuma(List<int> occupiedByLuma, float[] histogram, float[] binLuma, float totalWeight, float fraction)
        {
            float cumulative = 0f;
            for (int i = 0; i < occupiedByLuma.Count; i++)
            {
                int bin = occupiedByLuma[i];
                cumulative += histogram[bin];
                if (cumulative / totalWeight >= fraction)
                {
                    return binLuma[bin];
                }
            }

            return binLuma[occupiedByLuma[occupiedByLuma.Count - 1]];
        }

        private static float Luminance(float r, float g, float b)
        {
            return kLumaR * r + kLumaG * g + kLumaB * b;
        }

        /// <summary>
        /// HSV-style hue (degrees, red wraps at 0/360) and saturation (max-min)/max in
        /// fixed float math. Achromatic inputs report hue 0 / saturation 0.
        /// </summary>
        private static void RgbToHueSaturation(float r, float g, float b, out float hueDegrees, out float saturation)
        {
            float max = Mathf.Max(r, Mathf.Max(g, b));
            float min = Mathf.Min(r, Mathf.Min(g, b));
            float delta = max - min;
            if (max <= 0f || delta <= 0f)
            {
                hueDegrees = 0f;
                saturation = 0f;
                return;
            }

            saturation = delta / max;
            float hue;
            if (r >= g && r >= b)
            {
                hue = 60f * (((g - b) / delta) % 6f);
            }
            else if (g >= r && g >= b)
            {
                hue = 60f * (((b - r) / delta) + 2f);
            }
            else
            {
                hue = 60f * (((r - g) / delta) + 4f);
            }

            if (hue < 0f)
            {
                hue += kDegreesPerHueUnit;
            }

            hueDegrees = hue;
        }

        /// <summary>Occupied-bin order: weighted mass descending, ties by bin index ascending.</summary>
        private sealed class BinMassComparer : IComparer<int>
        {
            private readonly float[] histogram;

            public BinMassComparer(float[] histogram)
            {
                this.histogram = histogram;
            }

            public int Compare(int x, int y)
            {
                int byMass = histogram[y].CompareTo(histogram[x]);
                return byMass != 0 ? byMass : x.CompareTo(y);
            }
        }

        /// <summary>Occupied-bin order: bin luminance ascending, ties by bin index ascending.</summary>
        private sealed class BinLumaComparer : IComparer<int>
        {
            private readonly float[] binLuma;

            public BinLumaComparer(float[] binLuma)
            {
                this.binLuma = binLuma;
            }

            public int Compare(int x, int y)
            {
                int byLuma = binLuma[x].CompareTo(binLuma[y]);
                return byLuma != 0 ? byLuma : x.CompareTo(y);
            }
        }
    }
}

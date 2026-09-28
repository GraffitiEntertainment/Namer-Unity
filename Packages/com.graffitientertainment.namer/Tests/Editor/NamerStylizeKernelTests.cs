using System;
using System.Collections;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05 plan 02 GPU stylize-kernel tests (D-01/D-03/D-05): island-gated
    /// bilateral (two islands across a 1-texel gutter narrower than the radius must not
    /// bleed; hash-noise grain variance must drop inside one island; a hard luma step
    /// far beyond sigmaR must survive) and the hue-anchored palette RAMP re-specced to
    /// the 05.2-03 (STYL-11) operator: luma ALWAYS stays at the source's own — the
    /// palette transfers hue/vibrance only, a Post Look asset owns tonality — with
    /// boost-only
    /// saturation in both directions (higher anchor sat reached by lerp, lower anchor
    /// sat refused by the max guard), hue preservation at mid strength Preserve, full
    /// hue snap at strength 1 in both modes, the f = strength ramp pinned at mid
    /// strength, achromatic texels matching achromatic anchors by LUMA distance and
    /// never tinted by an all-chromatic anchor set (the black-eyes defect), alpha
    /// preservation. All oracles are numeric readback (no screenshots, Pitfall 10)
    /// computed against the READBACK source so half-float quantization cancels;
    /// aggregates are strip medians / region maxima over deterministic constants,
    /// never single noisy texels (Pitfall 5). Capability-gated per D-15:
    /// skipped-with-report when compute or async readback is unavailable, never a
    /// silent pass.
    /// </summary>
    public class NamerStylizeKernelTests
    {
        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        private const int kSize = 64;

        // Half-float (binary16, 10 mantissa bits) quantization bounds for R16G16B16A16_SFloat.
        private const float kHalfEpsilon = 0.001f;
        private const float kExactTolerance = 1e-5f;
        private const float kHueTolerance = 0.002f;
        private const float kLumaTolerance = 0.002f;

        // Strip-MEAN preservation bound: R16G16B16A16_SFloat stores luma ~0.5 at a
        // 2^-11 (~0.00049) step, and the run does 3 bilateral passes plus the final
        // store — each rounding once — while the strip window exchanges boundary
        // texels with identically distributed neighbors. 0.002 (~4 binary16 steps)
        // covers pass rounding plus that second-order boundary exchange, with margin.
        // (Observed 2026-09-23 round 3: median drift 0.00146484375 = exactly 3 steps
        // = one ulp per pass — pure storage rounding, no filter bias.)
        private const float kMeanPreservationTolerance = 0.002f;

        // Deterministic hash-noise grain (memory: namer-test-fixtures-ao-unmultiply):
        // high-frequency detail; a smooth luminance ramp would flatten to zero signal.
        private const float kGrainMid = 0.5f;
        private const float kGrainAmplitude = 0.05f;

        // Rec.709 luma weights (kernel mirror).
        private const float kLumaWeightR = 0.2126f;
        private const float kLumaWeightG = 0.7152f;
        private const float kLumaWeightB = 0.0722f;

        // Two-island gutter fixture (64 texels): island A covers texels [0..27]^2, island
        // B covers [29..56]^2 — the uncovered gutter at 28 is ONE texel wide, narrower
        // than the bilateral radius of 2, so a coverage-only gate would bleed A into B.
        private const float kIslandAMax = 28f / 64f;
        private const float kIslandBMin = 29f / 64f;
        private const float kIslandBMax = 57f / 64f;
        private const int kGutterLine = 28;
        private const int kIslandALast = 27;
        private const int kIslandBFirst = 29;
        private const int kIslandBLast = 56;

        private const float kAchromaticCutoff = 0.15f;

        // ------------------------------------------------------------------
        // Bilateral: island gate
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Bilateral_TwoIslands_NarrowGutter_NoCrossIslandBleed()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            NamerSplitResult split = CreateTwoQuadSplit();
            NamerStyleStylization stylization = CreateSmoothingContext(1f, split);
            Color[] output = RunStylize(
                (x, y) =>
                {
                    if (x <= kIslandALast && y <= kIslandALast)
                    {
                        return new Color(1f, 1f, 1f, 1f);
                    }

                    if (x >= kIslandBFirst && x <= kIslandBLast && y >= kIslandBFirst && y <= kIslandBLast)
                    {
                        return new Color(0f, 0f, 0f, 1f);
                    }

                    return new Color(0.5f, 0.5f, 0.5f, 1f);
                },
                stylization,
                split);

            // Island A is CONSTANT white: with the island gate its weighted average is
            // exactly white no matter how close the black island sits across the 1-texel
            // gutter. A failed gate darkens the corner ring — assert over ALL of A.
            float maxDeviationA = 0f;
            float maxDeviationB = 0f;
            float maxDeviationGutter = 0f;
            for (int y = 0; y < kSize; y++)
            {
                for (int x = 0; x < kSize; x++)
                {
                    Color o = output[y * kSize + x];
                    if (x <= kIslandALast && y <= kIslandALast)
                    {
                        maxDeviationA = Mathf.Max(maxDeviationA, Mathf.Max(1f - o.r, Mathf.Max(1f - o.g, 1f - o.b)));
                    }
                    else if (x >= kIslandBFirst && x <= kIslandBLast && y >= kIslandBFirst && y <= kIslandBLast)
                    {
                        maxDeviationB = Mathf.Max(maxDeviationB, Mathf.Max(o.r, Mathf.Max(o.g, o.b)));
                    }
                    else
                    {
                        maxDeviationGutter = Mathf.Max(maxDeviationGutter, Mathf.Abs(o.r - 0.5f));
                    }
                }
            }

            Assert.LessOrEqual(maxDeviationA, kExactTolerance,
                "island A must stay exactly white — a same-island-only average cannot mix island B across the 1-texel gutter");
            Assert.LessOrEqual(maxDeviationB, kExactTolerance, "island B must stay exactly black");
            Assert.LessOrEqual(maxDeviationGutter, kExactTolerance, "uncovered gutter texels pass through unchanged");

            yield return null;
        }

        [UnityTest]
        public IEnumerator Bilateral_OneIsland_HashGrainVarianceDrops()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            NamerSplitResult split = CreateSingleQuadSplit();
            NamerStyleStylization stylization = CreateSmoothingContext(1f, split);
            RenderTexture src = UploadBase(kSize, kSize, (x, y) =>
            {
                float v = kGrainMid + kGrainAmplitude * GrainNoise(x, y);
                return new Color(v, v, v, 1f);
            });
            try
            {
                Color[] input = ReadBackFloat(src);
                Color[] output = RunStylize(src, stylization, split);

                // Strip means/MADs over a well-covered interior band (never single texels).
                float inMean, inMad, outMean, outMad;
                CollectStripStats(input, output, out inMean, out inMad, out outMean, out outMad);

                // MEAN preservation is the invariant a smoothing pass actually
                // guarantees: the island bilateral is a weighted average with symmetric
                // spatial/range weights, so over homogeneous zero-mean hash grain the
                // strip MEAN survives to binary16 precision. The MEDIAN does not —
                // repeated averaging pulls a skewed sample's median toward its mean,
                // which is exactly the 3-step drift the 2026-09-23 round-3 gate
                // measured (0.00146484375); that is fixture statistics, not filter
                // bias, so the oracle asserts the mean (with a MAD still centered on
                // the mean) instead of loosening the median bound.
                Assert.LessOrEqual(Mathf.Abs(outMean - inMean), kMeanPreservationTolerance,
                    "smoothing preserves the strip mean (the weighted average is mean-preserving; the median of a skewed grain sample is not)");
                Assert.LessOrEqual(outMad, 0.5f * inMad,
                    "intra-island hash-noise variance must drop under full-strength smoothing (strip MAD)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Bilateral_RangeGate_HardLumaStepSurvives()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // One island split by a 0.2 -> 0.8 luma step (delta 0.6 >> sigmaR ~0.085 at
            // s = 0.1): the range gaussian suppresses cross-step neighbors (~exp(-25)),
            // so the step must survive one bilateral iteration.
            const float kStepLow = 0.2f;
            const float kStepHigh = 0.8f;
            const int kStepColumn = 32;
            const float kSurvivingStepThreshold = 0.5f;

            NamerSplitResult split = CreateSingleQuadSplit();
            NamerStyleStylization stylization = CreateSmoothingContext(0.1f, split);
            RenderTexture src = UploadBase(kSize, kSize, (x, y) =>
            {
                float v = x < kStepColumn ? kStepLow : kStepHigh;
                return new Color(v, v, v, 1f);
            });
            try
            {
                Color[] input = ReadBackFloat(src);
                Color[] output = RunStylize(src, stylization, split);

                var edgeDeltas = new System.Collections.Generic.List<float>(kSize);
                var edgeColumnShifts = new System.Collections.Generic.List<float>(kSize);
                for (int y = 5; y < kSize - 5; y++)
                {
                    Color leftOut = output[y * kSize + kStepColumn - 1];
                    Color rightOut = output[y * kSize + kStepColumn];
                    Color leftIn = input[y * kSize + kStepColumn - 1];
                    edgeDeltas.Add(Mathf.Abs(Luma(rightOut) - Luma(leftOut)));
                    edgeColumnShifts.Add(Mathf.Max(
                        Mathf.Abs(Luma(leftOut) - Luma(leftIn)),
                        Mathf.Abs(Luma(output[y * kSize + kStepColumn]) - Luma(input[y * kSize + kStepColumn]))));
                }

                Assert.GreaterOrEqual(Median(edgeDeltas.ToArray()), kSurvivingStepThreshold,
                    "the hard luma step (0.6) must survive the bilateral range gate (median across-edge delta)");
                Assert.LessOrEqual(Median(edgeColumnShifts.ToArray()), kHalfEpsilon,
                    "edge-adjacent texels keep their own side's value (cross-step weight ~exp(-25) is invisible)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        // ------------------------------------------------------------------
        // Palette transfer
        // ------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Palette_PreserveMidStrength_HuePreservedLumaPreserved()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Tonal-flattening regression pin (quick 260924-hk7): source is a saturated
            // half-exact orange (1, 0.5, 0) — hue 1/12, sat 1, luma ~0.570 — and the
            // anchor sits at the SAME hue with a much DARKER luma (0.2) and a LOWER sat
            // (0.2). At strength 0.5 Preserve, strength must move NEITHER luma NOR
            // sat: hue stays bit-stable, luma stays at the source's own, and the
            // max() guard keeps sat 1. Semantics (05.2-03 STYL-11): StyleStrength
            // means hue-family shift + vibrance; the palette NEVER transfers luma —
            // a Post Look asset owns tonality.
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];
                float srcHue = Hue(inColor);
                float srcLuma = Luma(inColor);
                float srcSat = Sat(inColor);
                const float kAnchorSat = 0.2f;
                const float kAnchorLuma = 0.2f;

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(srcHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), srcHue), kHueTolerance,
                    "Preserve at mid strength with a hue-matched anchor keeps the source hue");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - srcLuma), kLumaTolerance,
                    "strength never moves luma — a dark anchor must not flatten the source tonality (the palette never transfers luma)");
                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - srcSat), kHalfEpsilon,
                    "boost-only sat: an anchor below the source sat can never desaturate (max guard)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_MidStrength_BoostOnlySat_ReachesTowardHigherAnchorSat()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Boost-only pin (a): the anchor sat is ABOVE the source sat, so the pull
            // reaches it by lerp. No-clip derivation: out = (hue 1/12, sat 0.6, srcLuma
            // 0.5) -> luma(HsvUnit(1/12, 0.6)) = 0.748, so V = 0.5 / 0.748 = 0.67 < 1
            // and the stored rgb carries the exact ramped values (no saturate() bend).
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.4f;
            const float kSrcLuma = 0.5f;
            const float kAnchorSat = 0.8f;
            const float kAnchorLuma = 0.3f;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];
                float srcHue = Hue(inColor);
                float srcSat = Sat(inColor);
                float srcLuma = Luma(inColor);

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(kSrcHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                float expectedSat = Mathf.Lerp(srcSat, kAnchorSat, 0.5f);
                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - expectedSat), kHalfEpsilon,
                    "sat boosts exactly lerp(srcSat, anchorSat, strength) toward the higher anchor sat");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - srcLuma), kLumaTolerance,
                    "luma stays at the source's despite the darker anchor (the palette never transfers luma)");
                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), srcHue), kHueTolerance,
                    "same-hue anchor keeps hue bit-stable");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_MidStrength_BoostOnlySat_NeverDesaturatesBelowSource()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Boost-only pin (b): the anchor is LESS saturated (0.3) and much BRIGHTER
            // (0.9) than the source — both pulls must be refused. No-clip: out =
            // (1/12, 0.8, 0.5) -> luma(HsvUnit(1/12, 0.8)) = 0.656, V = 0.5 / 0.656 =
            // 0.76 < 1. (Trade-off acknowledged by the design: a pastel reference
            // cannot soften a vivid source.)
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.8f;
            const float kSrcLuma = 0.5f;
            const float kAnchorSat = 0.3f;
            const float kAnchorLuma = 0.9f;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];
                float srcSat = Sat(inColor);
                float srcLuma = Luma(inColor);

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(kSrcHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - srcSat), kHalfEpsilon,
                    "max() pin: outSat == srcSat exactly — the operator never desaturates below the source");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - srcLuma), kLumaTolerance,
                    "a bright anchor must not lift luma — the palette never transfers luma");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_ReplaceFullStrength_SnapsToAnchorHue()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Source sat 0.4 < anchor sat 0.8 so the boost-only pull reaches the anchor
            // sat exactly. No-clip derivation: out = (0.2, 0.8, 0.5) -> luma(HsvUnit
            // (0.2, 0.8)) = 0.908, V = 0.5 / 0.908 = 0.55 < 1 (the old 0.6 blue anchor
            // sits in a low-K family; green/yellow families keep V < 1). Luma stays at
            // the SOURCE — 05.2-03 (STYL-11): the palette never transfers luma, so no
            // strength moves tonality.
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.4f;
            const float kSrcLuma = 0.5f;
            const float kAnchorHue = 0.2f;
            const float kAnchorSat = 0.8f;
            const float kAnchorLuma = 0.25f;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcLuma = Luma(input[kSize / 2 * kSize + kSize / 2]);

                NamerStyleStylization stylization = CreatePaletteContext(1f, 1, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), kAnchorHue), kHueTolerance,
                    "Replace at strength 1 snaps to the anchor hue (f = 1)");
                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - kAnchorSat), kHalfEpsilon,
                    "sat reaches the (higher) anchor sat at strength 1");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - srcLuma), kLumaTolerance,
                    "luma stays at the source's — full strength never flattens tonality (STYL-11)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_PreserveFullStrength_ConvergesToAnchorHue()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Same no-clip fixture as the Replace variant: at strength 1 Preserve ramps
            // with f = strength = 1, so the hue still converges to the anchor.
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.4f;
            const float kSrcLuma = 0.5f;
            const float kAnchorHue = 0.2f;
            const float kAnchorSat = 0.8f;
            const float kAnchorLuma = 0.25f;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcLuma = Luma(input[kSize / 2 * kSize + kSize / 2]);

                NamerStyleStylization stylization = CreatePaletteContext(1f, 0, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), kAnchorHue), kHueTolerance,
                    "Preserve ramps with f = strength — at strength 1 the hue still converges to the anchor (STYL-03)");
                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - kAnchorSat), kHalfEpsilon,
                    "sat reaches the (higher) anchor sat at strength 1");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - srcLuma), kLumaTolerance,
                    "luma stays at the source's — the palette never transfers luma");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_PreserveMidStrength_HueRampedHalfwayToAnchor()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Pins A5 directly: at strength 0.5 Preserve with a FAR anchor, outHue must
            // be the circular midpoint — Replace at the same strength would land on the
            // anchor hue instead.
            //
            // No-clip re-derivation (quick 260924-hk7): under boost-only the source sat
            // can no longer drop, so out = (midpoint hue, srcSat, srcLuma). The old
            // fixture (sat-1 orange + 0.6 anchor) lands on the wrap-around midpoint
            // hue 0.84167 where luma(HsvUnit(0.84167, 1)) = 0.281 — V = 0.570 / 0.281
            // ~ 2.0, a massive clip. This fixture uses a mid-sat source (0.5) and a
            // green-family anchor: out = (0.24167, 0.5, 0.5) -> luma(HsvUnit(0.24167,
            // 0.5)) = 0.916, V = 0.5 / 0.916 = 0.55 < 1.
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.5f;
            const float kSrcLuma = 0.5f;
            const float kAnchorHue = 0.4f;
            const float kAnchorSat = 0.3f;   // below src sat -> outSat stays 0.5 (max guard)
            const float kAnchorLuma = 0.85f; // bright anchor — must not lift luma

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcHue = Hue(input[kSize / 2 * kSize + kSize / 2]);

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                float outHue = Hue(output[kSize / 2 * kSize + kSize / 2]);

                float wrapped = kAnchorHue - srcHue;
                wrapped -= Mathf.Floor(wrapped + 0.5f);
                float expectedMidpoint = Mathf.Repeat(srcHue + wrapped * 0.5f, 1f);

                Assert.LessOrEqual(CircularHueDelta(outHue, expectedMidpoint), kHueTolerance,
                    "Preserve ramps hue by f = strength (midpoint at 0.5), not f = 1");
                Assert.Greater(CircularHueDelta(outHue, kAnchorHue), kHueTolerance,
                    "the midpoint is measurably NOT the anchor hue — this discriminates Preserve from Replace");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_ReplaceFull_LumaStaysAtSource()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // 05.2-03 (STYL-11): the palette luma transfer is retired — output luma
            // is the source's own, unconditionally, at every strength. The anchor
            // sits at a much darker luma (0.25 vs the source's 0.5) under a full
            // Replace ramp, so the retired snap is observable by its absence: hue
            // reaches the anchor while luma stays at the source (the palette
            // transfers hue/vibrance only; a Post Look asset owns tonality).
            //
            // No-clip: out = (0.4, 0.8, 0.5) -> luma(HsvUnit(0.4, 0.8)) = 0.795,
            // V = 0.5 / 0.795 = 0.63 < 1.
            const float kSrcHue = 1f / 12f;
            const float kSrcSat = 0.5f;
            const float kSrcLuma = 0.5f;
            const float kAnchorHue = 0.4f;
            const float kAnchorSat = 0.8f;
            const float kAnchorLuma = 0.25f;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => HsvColor(kSrcHue, kSrcSat, kSrcLuma));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];
                float srcLuma = Luma(inColor);

                NamerStyleStylization fullRamp = CreatePaletteContext(1f, 1, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] ramped = RunStylize(src, fullRamp, null);
                Color rampedColor = ramped[kSize / 2 * kSize + kSize / 2];
                Assert.LessOrEqual(Mathf.Abs(Luma(rampedColor) - srcLuma), kLumaTolerance,
                    "the palette never transfers luma — output luma stays at the source's own even under a full Replace ramp (STYL-11)");
                Assert.LessOrEqual(CircularHueDelta(Hue(rampedColor), kAnchorHue), kHueTolerance,
                    "the full Replace ramp still snaps the hue to the anchor");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_AchromaticTexel_FullPassthrough()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Gray texel (sat 0 < cutoff, luma 0.6) + two achromatic anchors (luma 0.2 /
            // 0.8, garbage hues) + two chromatic anchors whose hue-noise match would pull
            // luma elsewhere. 05.2-03 (STYL-11): the achromatic texel is a FULL
            // passthrough, unconditionally — the retired luma transfer used to snap it
            // to the luma-nearest achromatic anchor (0.8); now every channel is
            // preserved verbatim and sat stays below the cutoff.
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(0.6f, 0.6f, 0.6f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];

                NamerStyleStylization passthrough = CreatePaletteContext(
                    0.5f,
                    0,
                    new Vector3(0.5f, 0.9f, 0.9f),   // chromatic: hue-match decoy (would have pulled luma to 0.75)
                    new Vector3(0.55f, 0.9f, 0.1f),  // chromatic decoy
                    new Vector3(0.9f, 0.0f, 0.2f),   // achromatic (sat 0 < cutoff)
                    new Vector3(0.1f, 0.0f, 0.8f));  // achromatic — the retired luma-nearest winner
                Color[] output = RunStylize(src, passthrough, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(Mathf.Abs(outColor.r - inColor.r), kExactTolerance,
                    "achromatic passthrough: red preserved");
                Assert.LessOrEqual(Mathf.Abs(outColor.g - inColor.g), kExactTolerance,
                    "achromatic passthrough: green preserved");
                Assert.LessOrEqual(Mathf.Abs(outColor.b - inColor.b), kExactTolerance,
                    "achromatic passthrough: blue preserved");
                Assert.LessOrEqual(Sat(outColor), kAchromaticCutoff,
                    "a gray texel stays achromatic");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - Luma(inColor)), kLumaTolerance,
                    "the retired snap: an achromatic texel keeps its own luma even with the luma-nearest achromatic anchor (0.8) in the set (STYL-11)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_AchromaticTexel_AllChromaticAnchors_NeverTinted()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // The black-eyes-turn-gray defect regression (quick 260924-hk7): this
            // project's real extraction produced anchors whose k-means centroids ALL
            // drifted above the 0.15 achromatic cutoff (sat >= 0.20), so an achromatic
            // texel fell into the never-fail hue fallback and the old absolute pull
            // lifted/tinted it. The redesigned operator keeps the fallback match
            // pull-free for achromatic texels: output == input even at strength 1 +
            // Replace (05.2-03 removed the luma lerp outright — the match is
            // pull-free by construction).
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(0.05f, 0.05f, 0.05f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color inColor = input[kSize / 2 * kSize + kSize / 2];

                NamerStyleStylization stylization = CreatePaletteContext(
                    1f,
                    1,
                    new Vector3(0.5f, 0.3f, 0.6f),   // chromatic, sat >= 0.20 (real-extraction shape)
                    new Vector3(0.7f, 0.45f, 0.4f)); // chromatic
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(Mathf.Abs(outColor.r - inColor.r), kExactTolerance,
                    "a black texel's red is untouched by an all-chromatic anchor set");
                Assert.LessOrEqual(Mathf.Abs(outColor.g - inColor.g), kExactTolerance,
                    "a black texel's green is untouched by an all-chromatic anchor set");
                Assert.LessOrEqual(Mathf.Abs(outColor.b - inColor.b), kExactTolerance,
                    "a black texel's blue is untouched by an all-chromatic anchor set");
                Assert.LessOrEqual(Sat(outColor), kAchromaticCutoff,
                    "black stays black — the chromatic fallback match never tints achromatic texels");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - Luma(inColor)), kLumaTolerance,
                    "a black texel's luma is never lifted — the palette transfers no luma");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_ZeroControls_PassthroughCopy()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Both zero-control guards in one case: anchors present with strength 0,
            // and no anchors at all with strength 1 — each dispatch must
            // write a byte-exact copy of the source (dst always fully written).
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);

                NamerStyleStylization zeroControls = CreatePaletteContext(0f, 0, new Vector3(0.5f, 0.5f, 0.5f));
                Color[] zeroed = RunStylize(src, zeroControls, null);
                Assert.LessOrEqual(MaxAbsChannelDelta(input, zeroed), kExactTolerance,
                    "strength 0 with anchors present is a byte-exact passthrough");

                NamerStyleStylization noAnchors = CreatePaletteContext(1f, 1);
                Color[] anchorless = RunStylize(src, noAnchors, null);
                Assert.LessOrEqual(MaxAbsChannelDelta(input, anchorless), kExactTolerance,
                    "an empty anchor set is a byte-exact passthrough at any strength");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Palette_FullChain_AlphaPreservedPerTexel()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Full chain (smoothing + palette) with varying authored alpha: both stages
            // pass the center/source alpha through verbatim.
            NamerSplitResult split = CreateSingleQuadSplit();
            NamerStyleStylization stylization = CreateSmoothingContext(1f, split);
            stylization.StyleStrength = 1f;
            stylization.HueReplaceMode = 1;
            stylization.Anchors = new[] { new NamerStyleAnchor { Hue = 0.6f, Saturation = 0.6f, Luma = 0.4f, Weight = 1f } };
            stylization.AnchorCount = 1;
            stylization.PaletteAvailable = true;

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 0.25f + 0.5f * (x / (float)(kSize - 1))));
            try
            {
                Color[] input = ReadBackFloat(src);
                Color[] output = RunStylize(src, stylization, split);

                var shifts = new System.Collections.Generic.List<float>(kSize * kSize);
                for (int y = 4; y < kSize - 4; y++)
                {
                    for (int x = 4; x < kSize - 4; x++)
                    {
                        shifts.Add(Mathf.Abs(output[y * kSize + x].a - input[y * kSize + x].a));
                    }
                }

                Assert.LessOrEqual(Median(shifts.ToArray()), kExactTolerance,
                    "alpha passes through smoothing + palette verbatim (median |delta alpha| over the interior)");
            }
            finally
            {
                Release(src);
            }

            yield return null;
        }

        [Test]
        public void Context_IdentitySettings_InactiveAndNoMeshFlagged()
        {
            // The zero-dispatch contract starts CPU-side: strength 0 + smoothing 0
            // (or smoothing with no mesh) must leave Active false so the caller
            // skips Run. 05.2-03 (STYL-11): the tone-only dispatch path died with
            // the palette luma knob — anchors alone never make a context active.
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.StyleStrength = 0f;
                profile.Smoothing = 0f;
                NamerStyleStylization identity = NamerStyleStylization.Create(profile, null);
                Assert.IsFalse(identity.Active, "strength 0 + smoothing 0 is the identity context (zero-dispatch path)");

                profile.Smoothing = 1f;
                NamerStyleStylization noMesh = NamerStyleStylization.Create(profile, null);
                Assert.IsFalse(noMesh.Active, "smoothing with no mesh has no runnable effect");
                Assert.IsTrue(noMesh.SmoothingSkippedNoMesh, "the no-mesh skip is flagged for the 05-03 Info note (OQ-3)");

                profile.Smoothing = 0f;
                profile.ReferenceFingerprint = "anchors-but-idle";
                profile.PaletteAnchors.Add(new NamerStyleAnchor { Hue = 0.5f, Saturation = 0.5f, Luma = 0.5f, Weight = 1f });
                NamerStyleStylization anchorsIdle = NamerStyleStylization.Create(profile, null);
                Assert.IsFalse(anchorsIdle.Active,
                    "strength 0 + smoothing 0 with valid anchors is still the identity context — the retired luma knob no longer dispatches on its own");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static Color[] RunStylize(RenderTexture src, NamerStyleStylization stylization, NamerSplitResult split)
        {
            RenderTexture dst = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, kSize, kSize);
            try
            {
                RenderTexture islandIds = null;
                RenderTexture scratchA = null;
                RenderTexture scratchB = null;
                try
                {
                    if (stylization.TriIsland != null)
                    {
                        islandIds = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, kSize, kSize);
                        scratchA = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, kSize, kSize);
                        scratchB = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, kSize, kSize);
                    }

                    using (NamerStylizePipeline pipeline = new NamerStylizePipeline())
                    {
                        pipeline.Run(src, dst, islandIds, scratchA, scratchB, stylization, kSize, kSize);
                    }

                    return ReadBackFloat(dst);
                }
                finally
                {
                    Release(islandIds, scratchA, scratchB);
                }
            }
            finally
            {
                Release(dst);
            }
        }

        private static Color[] RunStylize(System.Func<int, int, Color> author, NamerStyleStylization stylization, NamerSplitResult split)
        {
            RenderTexture src = UploadBase(kSize, kSize, author);
            try
            {
                return RunStylize(src, stylization, split);
            }
            finally
            {
                Release(src);
            }
        }

        private static NamerStyleStylization CreateSmoothingContext(float smoothing, NamerSplitResult split)
        {
            NamerStyleStylization stylization = new NamerStyleStylization
            {
                Smoothing = Mathf.Clamp01(smoothing),
                StyleStrength = 0f,
            };
            if (split != null)
            {
                stylization.Uvs = split.Uvs;
                stylization.FlattenedTriangles = NamerUvIslands.FlattenTriangles(split.SubMeshTriangles);
                NamerUvIslandResult islands = NamerUvIslands.BuildIslandIds(split);
                stylization.TriIsland = islands.TriIsland;
                stylization.IslandCount = islands.IslandCount;
            }

            return stylization;
        }

        private static NamerStyleStylization CreatePaletteContext(float strength, int hueMode, params Vector3[] anchorsHsl)
        {
            var anchors = new NamerStyleAnchor[anchorsHsl.Length];
            for (int i = 0; i < anchorsHsl.Length; i++)
            {
                anchors[i] = new NamerStyleAnchor
                {
                    Hue = anchorsHsl[i].x,
                    Saturation = anchorsHsl[i].y,
                    Luma = anchorsHsl[i].z,
                    Weight = 1f,
                    LinearColor = Color.white,
                };
            }

            return new NamerStyleStylization
            {
                Anchors = anchors,
                AnchorCount = anchors.Length,
                PaletteAvailable = anchors.Length > 0,
                StyleStrength = Mathf.Clamp01(strength),
                HueReplaceMode = hueMode,
                Smoothing = 0f,
            };
        }

        private static NamerSplitResult CreateSingleQuadSplit()
        {
            const float kQuadMin = 0.02f;
            const float kQuadMax = 0.98f;
            return CreateQuadSplit(kQuadMin, kQuadMin, kQuadMax, kQuadMax);
        }

        private static NamerSplitResult CreateTwoQuadSplit()
        {
            return new NamerSplitResult
            {
                Positions = new Vector3[8],
                Normals = new Vector3[8],
                Tangents = new Vector4[8],
                Uvs = new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(kIslandAMax, 0f),
                    new Vector2(kIslandAMax, kIslandAMax),
                    new Vector2(0f, kIslandAMax),
                    new Vector2(kIslandBMin, kIslandBMin),
                    new Vector2(kIslandBMax, kIslandBMin),
                    new Vector2(kIslandBMax, kIslandBMax),
                    new Vector2(kIslandBMin, kIslandBMax),
                },
                SubMeshTriangles = new[] { new[] { 0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7 } },
            };
        }

        private static NamerSplitResult CreateQuadSplit(float uvMinX, float uvMinY, float uvMaxX, float uvMaxY)
        {
            return new NamerSplitResult
            {
                Positions = new Vector3[4],
                Normals = new Vector3[4],
                Tangents = new Vector4[4],
                Uvs = new[]
                {
                    new Vector2(uvMinX, uvMinY),
                    new Vector2(uvMaxX, uvMinY),
                    new Vector2(uvMaxX, uvMaxY),
                    new Vector2(uvMinX, uvMaxY),
                },
                SubMeshTriangles = new[] { new[] { 0, 1, 2, 0, 2, 3 } },
            };
        }

        private static void CollectStripStats(
            Color[] input,
            Color[] output,
            out float inMean,
            out float inMad,
            out float outMean,
            out float outMad)
        {
            var inValues = new System.Collections.Generic.List<float>();
            var outValues = new System.Collections.Generic.List<float>();
            for (int y = 30; y <= 33; y++)
            {
                for (int x = 5; x <= kSize - 6; x++)
                {
                    inValues.Add(input[y * kSize + x].r);
                    outValues.Add(output[y * kSize + x].r);
                }
            }

            inMean = Mean(inValues.ToArray());
            outMean = Mean(outValues.ToArray());
            inMad = Median(Deviation(inValues.ToArray(), inMean));
            outMad = Median(Deviation(outValues.ToArray(), outMean));
        }

        private static float Mean(float[] values)
        {
            float sum = 0f;
            for (int i = 0; i < values.Length; i++)
            {
                sum += values[i];
            }

            return sum / values.Length;
        }

        private static float[] Deviation(float[] values, float center)
        {
            var deviations = new float[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                deviations[i] = Mathf.Abs(values[i] - center);
            }

            return deviations;
        }

        private static float Median(float[] values)
        {
            float[] sorted = (float[])values.Clone();
            Array.Sort(sorted);
            int n = sorted.Length;
            return n % 2 == 1 ? sorted[n / 2] : 0.5f * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        private static float GrainNoise(int x, int y)
        {
            // Deterministic hash-noise grain (memory: namer-test-fixtures-ao-unmultiply).
            float v = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            return (v - Mathf.Floor(v)) * 2f - 1f; // [-1, 1]
        }

        private static float Luma(Color c)
        {
            return c.r * kLumaWeightR + c.g * kLumaWeightG + c.b * kLumaWeightB;
        }

        private static float Sat(Color c)
        {
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return maxc <= 1e-6f ? 0f : (maxc - minc) / maxc;
        }

        private static float Hue(Color c)
        {
            // C# mirror of NamerStylizeHue (NAMERStylize.compute).
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float chroma = maxc - minc;
            if (chroma <= 1e-6f)
            {
                return 0f;
            }

            float h;
            if (c.r >= maxc)
            {
                h = (c.g - c.b) / chroma;
            }
            else if (c.g >= maxc)
            {
                h = (c.b - c.r) / chroma + 2f;
            }
            else
            {
                h = (c.r - c.g) / chroma + 4f;
            }

            return Mathf.Repeat(h / 6f, 1f);
        }

        private static float CircularHueDelta(float a, float b)
        {
            float d = Mathf.Abs(a - b);
            return Mathf.Min(d, 1f - d);
        }

        /// <summary>The V=1 sector table for (hue, saturation) — the kernel's NamerStylizeHsvUnit (C# mirror).</summary>
        private static Color HsvUnit(float h, float s)
        {
            h = Mathf.Repeat(h, 1f) * 6f;
            int i = Mathf.FloorToInt(h);
            float f = h - i;
            float p = 1f - s;
            float q = 1f - s * f;
            float t = p + s * f;
            switch (i)
            {
                case 0: return new Color(1f, t, p, 1f);
                case 1: return new Color(q, 1f, p, 1f);
                case 2: return new Color(p, 1f, t, 1f);
                case 3: return new Color(p, q, 1f, 1f);
                case 4: return new Color(t, p, 1f, 1f);
                default: return new Color(1f, p, q, 1f);
            }
        }

        /// <summary>
        /// Authors a linear color with the exact target hue/sat/luma via the kernel's
        /// own rebuild math (unit * luma / luma(unit)) — fixture authoring helper for
        /// the no-clip regime; oracles still derive from the READBACK input.
        /// </summary>
        private static Color HsvColor(float h, float s, float luma)
        {
            Color unit = HsvUnit(h, s);
            float unitLuma = unit.r * kLumaWeightR + unit.g * kLumaWeightG + unit.b * kLumaWeightB;
            Color c = unit * (luma / unitLuma);
            c.a = 1f;
            return c;
        }

        /// <summary>Max absolute per-channel RGB delta between two same-size readbacks.</summary>
        private static float MaxAbsChannelDelta(Color[] a, Color[] b)
        {
            float max = 0f;
            for (int i = 0; i < a.Length; i++)
            {
                max = Mathf.Max(max, Mathf.Abs(a[i].r - b[i].r));
                max = Mathf.Max(max, Mathf.Abs(a[i].g - b[i].g));
                max = Mathf.Max(max, Mathf.Abs(a[i].b - b[i].b));
            }

            return max;
        }

        private static RenderTexture UploadBase(int w, int h, System.Func<int, int, Color> pixel)
        {
            Texture2D upload = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color[] pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[y * w + x] = pixel(x, y);
                }
            }

            upload.SetPixels(pixels);
            upload.Apply(false, false);

            RenderTexture rt = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);
            Graphics.Blit(upload, rt);
            UnityEngine.Object.DestroyImmediate(upload);
            return rt;
        }

        private static RenderTexture CreateRenderTexture(GraphicsFormat format, int w, int h)
        {
            RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(w, h, format, 0)
            {
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            });
            rt.Create();
            return rt;
        }

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            // RGBAFloat readback of the R16G16B16A16_SFloat target is lossless (half
            // widens to float exactly); QueuePlayerLoopUpdate forces the editor player
            // loop to pump the async request before the blocking wait.
            AsyncGPUReadbackRequest req = NamerComputePipeline.RequestReadback(rt, 0, TextureFormat.RGBAFloat);
            EditorApplication.QueuePlayerLoopUpdate();
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color> data = req.GetData<Color>();
            try
            {
                Color[] result = new Color[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    result[i] = data[i];
                }

                return result;
            }
            finally
            {
                data.Dispose();
            }
        }

        private static void Release(params RenderTexture[] rts)
        {
            foreach (RenderTexture rt in rts)
            {
                if (rt != null)
                {
                    if (RenderTexture.active == rt)
                    {
                        RenderTexture.active = null;
                    }

                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
        }
    }
}

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
    /// far beyond sigmaR must survive) and the hue-anchored palette transfer (hue
    /// preservation at mid strength Preserve, full snap at strength 1 in both modes,
    /// the f = strength ramp pinned at mid strength, achromatic texels matching
    /// achromatic anchors by LUMA distance, alpha preservation). All oracles are
    /// numeric readback (no screenshots, Pitfall 10) computed against the READBACK
    /// source so half-float quantization cancels; aggregates are strip medians /
    /// region maxima over deterministic constants, never single noisy texels (Pitfall 5).
    /// Capability-gated per D-15: skipped-with-report when compute or async readback is
    /// unavailable, never a silent pass.
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

                // Strip medians/MADs over a well-covered interior band (never single texels).
                float inMedian, inMad, outMedian, outMad;
                CollectStripStats(input, output, out inMedian, out inMad, out outMedian, out outMad);

                Assert.LessOrEqual(Mathf.Abs(outMedian - inMedian), kHalfEpsilon,
                    "smoothing preserves the strip median (grain is zero-mean)");
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
        public IEnumerator Palette_PreserveMidStrength_HuePreservedLumaPulled()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Source: saturated half-exact orange (1, 0.5, 0) — hue 1/12, sat 1. Anchor
            // sits at the SAME hue (nearest-by-hue) with a much darker luma: Preserve at
            // 0.5 must keep hue bit-stable while pulling luma halfway to the anchor.
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcHue = Hue(input[kSize / 2 * kSize + kSize / 2]);
                float srcLuma = Luma(input[kSize / 2 * kSize + kSize / 2]);
                const float kAnchorLuma = 0.2f;

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(srcHue, 1f, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                float outHue = Hue(outColor);
                float outLuma = Luma(outColor);
                float expectedLuma = Mathf.Lerp(srcLuma, kAnchorLuma, 0.5f);

                Assert.LessOrEqual(CircularHueDelta(outHue, srcHue), kHueTolerance,
                    "Preserve at mid strength with a hue-matched anchor keeps the source hue");
                Assert.LessOrEqual(Mathf.Abs(outLuma - expectedLuma), kLumaTolerance,
                    "luma lerps exactly halfway to the anchor");
                Assert.Less(Mathf.Abs(outLuma - kAnchorLuma), Mathf.Abs(srcLuma - kAnchorLuma),
                    "luma moved toward the anchor");
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

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                // Anchor far from the source hue, moderate sat/luma so the rebuild never
                // clips (a saturated anchor would saturate() B and bend the read-back hue).
                const float kAnchorHue = 0.6f;
                const float kAnchorSat = 0.6f;
                const float kAnchorLuma = 0.4f;

                NamerStyleStylization stylization = CreatePaletteContext(1f, 1, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), kAnchorHue), kHueTolerance,
                    "Replace at strength 1 snaps to the anchor hue (f = 1)");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - kAnchorLuma), kLumaTolerance, "luma snaps to the anchor luma");
                Assert.LessOrEqual(Mathf.Abs(Sat(outColor) - kAnchorSat), kLumaTolerance, "sat snaps to the anchor sat");
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

            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                const float kAnchorHue = 0.6f;
                const float kAnchorSat = 0.6f;
                const float kAnchorLuma = 0.4f;

                NamerStyleStylization stylization = CreatePaletteContext(1f, 0, new Vector3(kAnchorHue, kAnchorSat, kAnchorLuma));
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                Assert.LessOrEqual(CircularHueDelta(Hue(outColor), kAnchorHue), kHueTolerance,
                    "Preserve ramps with f = strength — at strength 1 the hue still converges to the anchor (STYL-03)");
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - kAnchorLuma), kLumaTolerance);
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
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(1f, 0.5f, 0f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcHue = Hue(input[kSize / 2 * kSize + kSize / 2]);
                const float kAnchorHue = 0.6f;

                NamerStyleStylization stylization = CreatePaletteContext(0.5f, 0, new Vector3(kAnchorHue, 0.6f, 0.4f));
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
        public IEnumerator Palette_AchromaticTexel_MatchesByLumaNotHue()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — D-15 gated skip");
            }

            // Gray texel (sat 0 < cutoff, luma 0.6) + two achromatic anchors (luma 0.2 /
            // 0.8, garbage hues) + two chromatic anchors whose hue-noise match would pull
            // luma elsewhere: the luma-nearest ACHROMATIC anchor (0.8) must win.
            RenderTexture src = UploadBase(kSize, kSize, (x, y) => new Color(0.6f, 0.6f, 0.6f, 1f));
            try
            {
                Color[] input = ReadBackFloat(src);
                float srcLuma = Luma(input[kSize / 2 * kSize + kSize / 2]);

                NamerStyleStylization stylization = CreatePaletteContext(
                    0.5f,
                    0,
                    new Vector3(0.5f, 0.9f, 0.9f),   // chromatic: hue-match decoy (would pull luma to 0.75)
                    new Vector3(0.55f, 0.9f, 0.1f),  // chromatic decoy
                    new Vector3(0.9f, 0.0f, 0.2f),   // achromatic (sat 0 < cutoff)
                    new Vector3(0.1f, 0.0f, 0.8f));  // achromatic — luma-nearest winner
                Color[] output = RunStylize(src, stylization, null);
                Color outColor = output[kSize / 2 * kSize + kSize / 2];

                float expectedLuma = Mathf.Lerp(srcLuma, 0.8f, 0.5f);
                Assert.LessOrEqual(Mathf.Abs(Luma(outColor) - expectedLuma), kLumaTolerance,
                    "achromatic texels match the luma-nearest ACHROMATIC anchor (0.8), never hue noise");
                Assert.LessOrEqual(Sat(outColor), kAchromaticCutoff,
                    "a gray texel pulled toward a gray anchor stays achromatic");
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
            // The zero-dispatch contract starts CPU-side: strength 0 + smoothing 0 (or
            // smoothing with no mesh) must leave Active false so the caller skips Run.
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
            out float inMedian,
            out float inMad,
            out float outMedian,
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

            inMedian = Median(inValues.ToArray());
            outMedian = Median(outValues.ToArray());
            inMad = Median(Deviation(inValues.ToArray(), inMedian));
            outMad = Median(Deviation(outValues.ToArray(), outMedian));
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

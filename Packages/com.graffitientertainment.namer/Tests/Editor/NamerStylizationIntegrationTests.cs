using System;
using System.Collections;
using System.Collections.Generic;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05 plan 02 integration tests — the D-01 stylize stage proven through
    /// <see cref="NamerComputePipeline.Process"/> (STYL-03/STYL-04), not just kernels:
    ///   byte-identical null path          -> <see cref="Stylization_NullPath_ByteIdenticalAcrossRuns"/>
    ///   identity context = zero leases    -> <see cref="Stylization_InactiveContext_ByteIdenticalZeroExtraLeases"/>
    ///   styled base flows into fit/result -> <see cref="Stylization_ActiveReplace_StylingFlowsIntoProjectionAndResult"/>
    ///   residual dividends the STYLED base-> <see cref="Stylization_ActiveReplace_ResidualDividendIsStyledSource"/>
    ///   mid-stage throw leaks nothing     -> <see cref="Stylization_ActiveContext_ThrowInProjection_RunPropagatesAndPoolReturnsToBaseline"/>
    ///
    /// Phase 05 plan 03 additions — the D-07/D-08 bake-side controls through the same
    /// Process surface: identity defaults stay byte-identical, authored AO honors the
    /// strength/contrast remap with a cavity floor (STYL-05), and roughness simplify /
    /// normal flatten run before quantization / octahedral encode (STYL-06).
    ///
    /// The fixture mirrors <see cref="NamerProjectionAcceptanceTests"/>: a quad mesh, an
    /// authored white occlusion (D-07 gate stays on the authored branch), and a chromatic
    /// hash-grain base (constant hue/saturation, luma-only grain — a luminance ramp would
    /// flatten the palette signal, project-memory fixture lesson). Every quantitative
    /// assertion reads an in-memory <see cref="RenderTexture"/> (Pitfall 4). All GPU paths
    /// are capability-gated (D-15); generated objects are destroyed in <c>finally</c>.
    /// </summary>
    public class NamerStylizationIntegrationTests
    {
        private const int WorkingSize = 128;
        private const float ErrorThreshold = 0.02f;
        private const int ResidualResolution = 0;
        private const float DefaultDipDepth = 0.25f;

        // Anchor fixture: hue 0.6 / sat 0.6 / luma 0.4 — the rebuild unit color peaks at
        // V = luma / luma(unit) ~ 0.65, so saturate() never clips and the readback hue is
        // exact to binary16 storage.
        private const float kAnchorHue = 0.6f;
        private const float kAnchorSaturation = 0.6f;
        private const float kAnchorLuma = 0.4f;

        // Source fixture: pure orange (hue 1/12, full saturation) with luma-only hash
        // grain — Replace mode must move the hue all the way to the anchor.
        private const float kSourceRed = 1f;
        private const float kSourceGreen = 0.5f;
        private const float kSourceBlue = 0f;
        private const float kGrainAmplitude = 0.1f;
        private const float kGrainCenter = 0.9f;

        // Binary16 (RGBA16F) storage bounds the hue readback error at ~2e-3; sat/luma
        // sit comfortably under 1e-3. VC/projection colors pass through Color32, so the
        // anchor-propagation bound is the 8-bit quantization step.
        private const float kHueTolerance = 0.002f;
        private const float kHalfEpsilon = 0.001f;
        private const float kColor32Tolerance = 0.01f;

        // Residual identity bound: residual x projected == styled up to float16 rounding
        // of the two stores plus the Color32 Gouraud quantization (~1/255 each) — 3/255
        // is the plan's stated budget.
        private const float kReconstructionTolerance = 3f / 255f;

        // HueMode indices (the profile stores a plain int — D-05: 0 = Preserve, 1 = Replace).
        private const int kHueModePreserve = 0;
        private const int kHueModeReplace = 1;

        // Rec.709 luma weights (the fitter/extractor/kernel definition).
        private const float kLumaR = 0.2126f;
        private const float kLumaG = 0.7152f;
        private const float kLumaB = 0.0722f;

        // HSV sector constants (kernel mirrors).
        private const float kHueSectors = 6f;
        private const float kSectorGreen = 2f;
        private const float kSectorBlue = 4f;

        // Edge inset for per-texel sampling (avoid rasterization boundary rounding).
        private const int kSampleInset = 4;

        // 05-03 D-07/D-08 fixture + oracle constants. The packed alpha carries the
        // 6-bit roughness in the low bits (Core/NamerFormat NamerPackAlphaBits).
        private const int kRoughnessAlphaMask = 0x3F;
        private const float kRoughnessQuantLevels = 63f;
        private const float kNormalDotTolerance = 1e-3f;

        // Authored-AO grain fixture: occlusion texels in [0.25, 0.75] (center 0.5,
        // amplitude 0.25) — wide enough that contrast 2 crushes texels to black
        // without a floor and strength 0.5 lands at a predictable median.
        private const float kOcclusionGrainAmplitude = 0.25f;
        private const float kOcclusionGrainCenter = 0.5f;

        // Row-alternating gloss fixture: smoothness centers alternate 0.5 / 0.75 per
        // row (period 2, smaller than the radius-2 5-tap window) with x-grain.
        private const float kSmoothnessRowEven = 0.5f;
        private const float kSmoothnessRowOdd = 0.75f;
        private const float kSmoothnessGrainAmplitude = 0.1f;

        // Grain normal fixture amplitude: signed xy spans [-0.8, 0.8] — genuinely
        // tilted normals (median +Z dot far below 1) that flatten must collapse.
        private const float kNormalGrainAmplitude = 0.4f;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // --------------------------------------------------------------------
        // Byte-identity guard: the null path adds nothing — two null runs agree byte for byte.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Stylization_NullPath_ByteIdenticalAcrossRuns()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU stylization null-path test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                using (var pipeline = new NamerComputePipeline())
                {
                    NamerComputeResult first = pipeline.Process(inspection, stylization: null);
                    Color32[] firstBase;
                    Color32[] firstSurface;
                    try
                    {
                        firstBase = ReadBackColor32(first.NormalizedBaseColor);
                        firstSurface = ReadBackColor32(first.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(first);
                    }

                    NamerComputeResult second = pipeline.Process(inspection, stylization: null);
                    try
                    {
                        Color32[] secondBase = ReadBackColor32(second.NormalizedBaseColor);
                        Color32[] secondSurface = ReadBackColor32(second.PackedSurface);
                        Assert.AreEqual(0, CountByteDifferences(firstBase, secondBase),
                            "null stylization: NormalizedBaseColor must be byte-identical across runs");
                        Assert.AreEqual(0, CountByteDifferences(firstSurface, secondSurface),
                            "null stylization: PackedSurface must be byte-identical across runs");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(second);
                    }
                }
            }
            finally
            {
                Destroy(baseMap, occlusion, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Identity guard: a built-but-inactive context (Active == false) leases zero
        // extra targets and produces the same bytes as the null path.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Stylization_InactiveContext_ByteIdenticalZeroExtraLeases()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU stylization identity test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            NamerStyleProfile profile = CreateExtractedProfile(0f, 0f, kHueModePreserve);
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                NamerStyleStylization inactive = NamerStyleStylization.Create(profile, split);
                Assert.IsFalse(inactive.Active,
                    "strength 0 + smoothing 0 with valid anchors must be the identity context");

                using (var pipeline = new NamerComputePipeline())
                {
                    int baseline = pipeline.LiveRenderTargetCount;

                    NamerComputeResult nullRun = pipeline.Process(inspection, stylization: null);
                    Color32[] nullBase;
                    Color32[] nullSurface;
                    int nullRunLive;
                    try
                    {
                        nullBase = ReadBackColor32(nullRun.NormalizedBaseColor);
                        nullSurface = ReadBackColor32(nullRun.PackedSurface);
                        nullRunLive = pipeline.LiveRenderTargetCount;
                    }
                    finally
                    {
                        pipeline.ReleaseResult(nullRun);
                    }

                    Assert.AreEqual(baseline, pipeline.LiveRenderTargetCount,
                        "null run must return the pool to baseline after ReleaseResult");

                    NamerComputeResult identityRun = pipeline.Process(inspection, stylization: inactive);
                    try
                    {
                        Assert.AreEqual(nullRunLive, pipeline.LiveRenderTargetCount,
                            "identity context must not lease a single extra render target");
                        Assert.AreEqual(0, CountByteDifferences(nullBase, ReadBackColor32(identityRun.NormalizedBaseColor)),
                            "identity context: NormalizedBaseColor must match the null path byte for byte");
                        Assert.AreEqual(0, CountByteDifferences(nullSurface, ReadBackColor32(identityRun.PackedSurface)),
                            "identity context: PackedSurface must match the null path byte for byte");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(identityRun);
                    }

                    Assert.AreEqual(baseline, pipeline.LiveRenderTargetCount,
                        "identity run must return the pool to baseline after ReleaseResult");
                }
            }
            finally
            {
                Destroy(profile, baseMap, occlusion, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-01 propagation: Replace at strength 1 styles the source BEFORE the fit —
        // the styled hue reaches the result base AND the fitted vertex colors.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Stylization_ActiveReplace_StylingFlowsIntoProjectionAndResult()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU stylization propagation test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            NamerStyleProfile profile = CreateExtractedProfile(1f, 0f, kHueModeReplace);
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                NamerStyleStylization stylization = NamerStyleStylization.Create(profile, split);
                Assert.IsTrue(stylization.Active, "Replace at strength 1 with valid anchors must be active");

                using (var decomp = new NamerDecompPipeline())
                using (var pipeline = new NamerComputePipeline())
                {
                    NamerProjectionContext context = BuildProjectionContext(split, decomp, true);
                    NamerComputeResult result = pipeline.Process(inspection, context, stylization);
                    try
                    {
                        // The styled base is the pre-projection source: hue/sat/luma snap to
                        // the anchor (D-04/D-05) while the pipeline stays in linear space.
                        Color[] styled = ReadBackFloat(result.SourceBaseColor);
                        float hue = MedianOf(styled, c => HueOf(c));
                        float saturation = MedianOf(styled, c => SaturationOf(c));
                        float luma = MedianOf(styled, c => LumaOf(c));
                        Assert.AreEqual(kAnchorHue, hue, kHueTolerance,
                            "Replace at strength 1: styled base hue must snap to the anchor hue");
                        Assert.AreEqual(kAnchorSaturation, saturation, kHalfEpsilon,
                            "Replace at strength 1: styled base saturation must snap to the anchor saturation");
                        Assert.AreEqual(kAnchorLuma, luma, kHalfEpsilon,
                            "Replace at strength 1: styled base luma must snap to the anchor luma");

                        // The vertex-color fit ran against the STYLED source: every fitted
                        // color is the anchor color up to Color32 quantization.
                        Assert.IsNotNull(context.Colors, "projection context must carry the fitted colors");
                        Color32 expected = ToColor32(AnchorLinearColor);
                        foreach (Color32 fitted in context.Colors)
                        {
                            Assert.AreEqual(expected.r, fitted.r, kColor32Tolerance, "fitted VC red carries the anchor");
                            Assert.AreEqual(expected.g, fitted.g, kColor32Tolerance, "fitted VC green carries the anchor");
                            Assert.AreEqual(expected.b, fitted.b, kColor32Tolerance, "fitted VC blue carries the anchor");
                        }
                    }
                    finally
                    {
                        context.Decomp?.Dispose();
                        pipeline.ReleaseResult(result);
                    }
                }
            }
            finally
            {
                Destroy(profile, baseMap, occlusion, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-02: the residual dividends the STYLED source — residual x projected
        // reconstructs the styled base (the operator is unchanged).
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Stylization_ActiveReplace_ResidualDividendIsStyledSource()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU stylization residual-dividend test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            NamerStyleProfile profile = CreateExtractedProfile(1f, 0f, kHueModeReplace);
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                NamerStyleStylization stylization = NamerStyleStylization.Create(profile, split);

                using (var decomp = new NamerDecompPipeline())
                using (var pipeline = new NamerComputePipeline())
                {
                    NamerProjectionContext context = BuildProjectionContext(split, decomp, true);
                    NamerComputeResult result = pipeline.Process(inspection, context, stylization);
                    try
                    {
                        Assert.IsNotNull(context.Decomp?.Residual,
                            "Write Residual ON must produce the in-memory residual");
                        Color[] styled = ReadBackFloat(result.SourceBaseColor);
                        Color[] projected = ReadBackFloat(result.NormalizedBaseColor);
                        Color[] residual = ReadBackFloat(context.Decomp.Residual);

                        float maxDeviation = 0f;
                        for (int y = kSampleInset; y < WorkingSize - kSampleInset; y++)
                        {
                            for (int x = kSampleInset; x < WorkingSize - kSampleInset; x++)
                            {
                                int i = y * WorkingSize + x;
                                float devR = Mathf.Abs(residual[i].r * projected[i].r - styled[i].r);
                                float devG = Mathf.Abs(residual[i].g * projected[i].g - styled[i].g);
                                float devB = Mathf.Abs(residual[i].b * projected[i].b - styled[i].b);
                                maxDeviation = Mathf.Max(maxDeviation, Mathf.Max(devR, Mathf.Max(devG, devB)));
                            }
                        }

                        Assert.LessOrEqual(maxDeviation, kReconstructionTolerance,
                            "residual x projected must reconstruct the STYLED source (max deviation "
                            + maxDeviation.ToString("0.0000") + " > " + kReconstructionTolerance.ToString("0.0000") + ")");
                    }
                    finally
                    {
                        context.Decomp?.Dispose();
                        pipeline.ReleaseResult(result);
                    }
                }
            }
            finally
            {
                Destroy(profile, baseMap, occlusion, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Catch discipline: a throw inside projection.Run propagates and every stylize
        // lease (styled base, island ids, scratch pair) returns to the pool.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Stylization_ActiveContext_ThrowInProjection_RunPropagatesAndPoolReturnsToBaseline()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU stylization throw-discipline test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            NamerStyleProfile profile = CreateExtractedProfile(1f, 0f, kHueModeReplace);
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                NamerStyleStylization stylization = NamerStyleStylization.Create(profile, split);

                using (var pipeline = new NamerComputePipeline())
                {
                    int baseline = pipeline.LiveRenderTargetCount;
                    var throwing = new NamerProjectionContext { Split = split };
                    throwing.Run = (sourceBase, projectedOut) => throw new InvalidOperationException("projection callback failure");

                    Assert.Throws<InvalidOperationException>(
                        () => pipeline.Process(inspection, throwing, stylization),
                        "a projection.Run throw must propagate out of Process");
                    Assert.AreEqual(baseline, pipeline.LiveRenderTargetCount,
                        "every stylize lease must return to the pool after the throw");
                }
            }
            finally
            {
                Destroy(profile, baseMap, occlusion, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 05-03 D-08 identity guard: the new bake controls default to identity —
        // explicitly-zeroed fields must match the pristine-default path byte for byte.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator BakeControls_AllIdentity_ByteIdenticalToDefaultPath()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU bake-controls identity test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateGrainOcclusion();
            Texture2D normalMap = CreateGrainNormalMap();
            try
            {
                var pristine = BuildInspection(baseMap, mesh, occlusion);
                pristine.NormalMap = normalMap;

                var explicitIdentity = BuildInspection(baseMap, mesh, occlusion);
                explicitIdentity.NormalMap = normalMap;
                explicitIdentity.AoStrength = 1f;
                explicitIdentity.AoContrast = 1f;
                explicitIdentity.CavityFloor = 0f;
                explicitIdentity.NormalDetailReduction = 0f;
                explicitIdentity.RoughnessSimplify = 0f;

                using (var pipeline = new NamerComputePipeline())
                {
                    NamerComputeResult pristineRun = pipeline.Process(pristine);
                    Color32[] pristineBase;
                    Color32[] pristineSurface;
                    try
                    {
                        pristineBase = ReadBackColor32(pristineRun.NormalizedBaseColor);
                        pristineSurface = ReadBackColor32(pristineRun.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(pristineRun);
                    }

                    NamerComputeResult identityRun = pipeline.Process(explicitIdentity);
                    try
                    {
                        Assert.AreEqual(0, CountByteDifferences(pristineBase, ReadBackColor32(identityRun.NormalizedBaseColor)),
                            "D-07/D-08 identity fields: NormalizedBaseColor must match the pristine-default path byte for byte");
                        Assert.AreEqual(0, CountByteDifferences(pristineSurface, ReadBackColor32(identityRun.PackedSurface)),
                            "D-07/D-08 identity fields: PackedSurface must match the pristine-default path byte for byte");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(identityRun);
                    }
                }
            }
            finally
            {
                Destroy(normalMap, occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-08: authored AO honors the strength remap — strength 0 whitens ANY
        // authored occlusion; strength 0.5 lands halfway to white.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator AuthoredAoRemap_StrengthControlsAuthoredOcclusion()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU authored-AO remap test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateGrainOcclusion();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                inspection.AoStrength = 0f;
                inspection.AoContrast = 1f;

                using (var pipeline = new NamerComputePipeline())
                {
                    NamerComputeResult whitened = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(whitened.PackedSurface);
                        float medianB = MedianOf(surface, kSampleInset, c => c.b / 255f);
                        Assert.GreaterOrEqual(medianB, 254f / 255f,
                            "authored AO at strength 0 must collapse to white (D-08)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(whitened);
                    }

                    inspection.AoStrength = 0.5f;
                    NamerComputeResult halved = pipeline.Process(inspection);
                    try
                    {
                        // Grain center 0.5 -> lerp(1, 0.5, 0.5) = 0.75 -> byte ~191.
                        Color32[] surface = ReadBackColor32(halved.PackedSurface);
                        float medianB = MedianOf(surface, kSampleInset, c => c.b / 255f);
                        Assert.AreEqual(0.75f, medianB, 2f / 255f,
                            "authored AO at strength 0.5 must land halfway to white (grain median 0.5)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(halved);
                    }
                }
            }
            finally
            {
                Destroy(occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-08 / STYL-05: the cavity floor clamps the REMAPPED authored AO — with
        // contrast 2 a floor of 0.25 lifts the crushed floor; floor 0 keeps the crush.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator AuthoredAoRemap_CavityFloorKeepsCavitiesAboveFloor()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU cavity-floor test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateGrainOcclusion();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                inspection.AoStrength = 1f;
                inspection.AoContrast = 2f;

                using (var pipeline = new NamerComputePipeline())
                {
                    inspection.CavityFloor = 0.25f;
                    NamerComputeResult floored = pipeline.Process(inspection);
                    Color32[] flooredSurface;
                    try
                    {
                        flooredSurface = ReadBackColor32(floored.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(floored);
                    }

                    inspection.CavityFloor = 0f;
                    NamerComputeResult unfloored = pipeline.Process(inspection);
                    try
                    {
                        Color32[] unflooredSurface = ReadBackColor32(unfloored.PackedSurface);

                        byte minFloored = MinChannel(flooredSurface, kSampleInset, c => c.b);
                        Assert.GreaterOrEqual((int)minFloored, Mathf.RoundToInt(0.25f * 255f) - 1,
                            "cavity floor 0.25 must keep every remapped AO texel at or above the floor (STYL-05)");

                        byte minUnfloored = MinChannel(unflooredSurface, kSampleInset, c => c.b);
                        Assert.LessOrEqual((int)minUnfloored, 2,
                            "floor 0 (identity) must keep the contrast crush — texels reach black");

                        for (int i = 0; i < flooredSurface.Length; i++)
                        {
                            Assert.GreaterOrEqual((int)flooredSurface[i].b, (int)unflooredSurface[i].b - 1,
                                "the floor may only LIFT AO bytes, never lower them (texel " + i + ")");
                        }
                    }
                    finally
                    {
                        pipeline.ReleaseResult(unfloored);
                    }
                }
            }
            finally
            {
                Destroy(occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-07 / STYL-06: roughness simplify is a radius-2 neighborhood mean over
        // _PackInputs BEFORE the 6-bit quantization — row-alternating smoothness
        // (period 2, smaller than the 5-tap window) collapses to flat roughness.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator RoughnessSimplify_BoxMeanSmoothsRoughnessBeforeQuantization()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU roughness-simplify test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            Texture2D glossMap = CreateRowAlternatingGlossMap();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                inspection.MetallicGlossMap = glossMap;
                inspection.Smoothness = 1f;

                using (var pipeline = new NamerComputePipeline())
                {
                    inspection.RoughnessSimplify = 0f;
                    NamerComputeResult baseline = pipeline.Process(inspection);
                    Color32[] baselineSurface;
                    try
                    {
                        baselineSurface = ReadBackColor32(baseline.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(baseline);
                    }

                    inspection.RoughnessSimplify = 1f;
                    NamerComputeResult simplified = pipeline.Process(inspection);
                    try
                    {
                        Color32[] simplifiedSurface = ReadBackColor32(simplified.PackedSurface);

                        float baselineSpread = RowMedianSpread(baselineSurface, RoughnessOf);
                        float simplifiedSpread = RowMedianSpread(simplifiedSurface, RoughnessOf);

                        Assert.Greater(baselineSpread, 0.05f,
                            "row-alternating smoothness (period 2) must produce separated row-medians before simplifying");
                        Assert.Less(simplifiedSpread, 0.02f,
                            "radius-2 box mean at strength 1 must collapse the row structure (STYL-06)");
                        Assert.Less(simplifiedSpread, 0.5f * baselineSpread,
                            "simplify must strictly reduce the row-median spread");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(simplified);
                    }
                }
            }
            finally
            {
                Destroy(glossMap, occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-07 ordering: simplify applies AFTER dip adoption on the Sobel path —
        // the dipped roughness is pulled toward the neighborhood mean of the
        // (uniform) scalar, so the Sobel dents vanish at strength 1.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator RoughnessSimplify_AppliesAfterDipAdoption_SobelPath()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU simplify-ordering test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                inspection.DipSource = NamerDipSource.SobelEdge;
                inspection.RoughnessExtractStrength = DefaultDipDepth;

                using (var pipeline = new NamerComputePipeline())
                {
                    inspection.RoughnessSimplify = 0f;
                    NamerComputeResult dipped = pipeline.Process(inspection);
                    Color32[] dippedSurface;
                    try
                    {
                        dippedSurface = ReadBackColor32(dipped.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(dipped);
                    }

                    inspection.RoughnessSimplify = 1f;
                    NamerComputeResult simplified = pipeline.Process(inspection);
                    try
                    {
                        Color32[] simplifiedSurface = ReadBackColor32(simplified.PackedSurface);

                        // Scalar roughness is uniform 1 -> the neighborhood mean is 1 ->
                        // strength 1 adopts the mean entirely: every texel quantizes to 63.
                        int dentedBelowSobel = 0;
                        int simplifiedNotFull = 0;
                        int sampled = 0;
                        for (int y = kSampleInset; y < WorkingSize - kSampleInset; y++)
                        {
                            for (int x = kSampleInset; x < WorkingSize - kSampleInset; x++)
                            {
                                int i = y * WorkingSize + x;
                                sampled++;
                                if (RoughnessOf(dippedSurface[i]) < 0.95f)
                                {
                                    dentedBelowSobel++;
                                }

                                if (RoughnessOf(simplifiedSurface[i]) < 1f - 1f / kRoughnessQuantLevels - kHalfEpsilon)
                                {
                                    simplifiedNotFull++;
                                }
                            }
                        }

                        Assert.Greater(dentedBelowSobel, sampled / 20,
                            "the Sobel dip must dent the roughness on the grain base before simplifying");
                        Assert.AreEqual(0, simplifiedNotFull,
                            "simplify AFTER dip adoption at strength 1 must pull every texel to the scalar mean (roughness 1 -> alpha 63)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(simplified);
                    }
                }
            }
            finally
            {
                Destroy(occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // D-07 / STYL-06: normal flatten lerps every rebuilt normal toward +Z
        // BEFORE the octahedral encode — at strength 1 every packed RG decodes to
        // +Z; at strength 0 the tilted grain normals survive untouched.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator NormalFlatten_Full_BlendsAllNormalsTowardPlusZ()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU normal-flatten test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateGrainBaseMap();
            Texture2D occlusion = CreateWhiteOcclusion();
            Texture2D normalMap = CreateGrainNormalMap();
            try
            {
                var inspection = BuildInspection(baseMap, mesh, occlusion);
                inspection.NormalMap = normalMap;

                using (var pipeline = new NamerComputePipeline())
                {
                    inspection.NormalDetailReduction = 0f;
                    NamerComputeResult tilted = pipeline.Process(inspection);
                    Color32[] tiltedSurface;
                    try
                    {
                        tiltedSurface = ReadBackColor32(tilted.PackedSurface);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(tilted);
                    }

                    inspection.NormalDetailReduction = 1f;
                    NamerComputeResult flattened = pipeline.Process(inspection);
                    try
                    {
                        Color32[] flattenedSurface = ReadBackColor32(flattened.PackedSurface);
                        float3 up = new float3(0f, 0f, 1f);

                        float minFlattenedDot = 1f;
                        float medianTiltedDot = MedianOf(tiltedSurface, kSampleInset, c => math.dot(
                            NamerFormat.OctahedralDecode(new float2(c.r / 255f, c.g / 255f)), up));
                        for (int i = 0; i < flattenedSurface.Length; i++)
                        {
                            float3 decoded = NamerFormat.OctahedralDecode(new float2(
                                flattenedSurface[i].r / 255f, flattenedSurface[i].g / 255f));
                            minFlattenedDot = Mathf.Min(minFlattenedDot, math.dot(decoded, up));
                        }

                        Assert.Less(medianTiltedDot, 0.99f,
                            "the grain normal fixture must be genuinely tilted before flattening");
                        Assert.GreaterOrEqual(minFlattenedDot, 1f - kNormalDotTolerance,
                            "normal flatten at strength 1 must decode every texel to +Z within the oct-quantization slack (STYL-06)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(flattened);
                    }
                }
            }
            finally
            {
                Destroy(normalMap, occlusion, baseMap, mesh);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Fixture helpers (mirror NamerProjectionAcceptanceTests).
        // --------------------------------------------------------------------

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap, Mesh mesh, Texture2D occlusionMap)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                OcclusionMap = occlusionMap,
                MetallicGlossMap = null,
                Metallic = 0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = DefaultDipDepth,
                DipSource = NamerDipSource.RemovedDetail,
                Emissive = 0f,
                AoUnmultiplyStrength = 1f,
                SmoothnessTextureChannel = 0,
                BakeSourceMesh = mesh,
            };
        }

        /// <summary>Builds a projection context the way <see cref="NamerProcessor"/> does (its
        /// internal CreateProjectionContext is assembly-private, so the test mirrors the Run
        /// contract).</summary>
        private static NamerProjectionContext BuildProjectionContext(NamerSplitResult split, NamerDecompPipeline decomp, bool writeResidual)
        {
            var context = new NamerProjectionContext { Split = split };
            context.Run = (sourceBase, projectedOut) =>
            {
                NativeArray<Color32> texels = ReadBackColor32Native(sourceBase);
                try
                {
                    using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, WorkingSize, WorkingSize))
                    {
                        context.Colors = fit.ToColor32Array();
                        context.Decomp = decomp.GenerateResidual(
                            split, context.Colors, sourceBase, WorkingSize, WorkingSize,
                            ErrorThreshold, ResidualResolution, projectedOut,
                            writeResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep);
                    }
                }
                finally
                {
                    texels.Dispose();
                }
            };

            return context;
        }

        /// <summary>In-memory extracted profile: hand-set anchor + fingerprint so
        /// <see cref="NamerStyleProfile.IsExtracted"/> holds without an Extract pass.</summary>
        private static NamerStyleProfile CreateExtractedProfile(float strength, float smoothing, int hueMode)
        {
            var profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.StyleStrength = strength;
            profile.Smoothing = smoothing;
            profile.HueMode = hueMode;
            profile.PaletteAnchors.Add(new NamerStyleAnchor
            {
                LinearColor = AnchorLinearColor,
                Hue = kAnchorHue,
                Saturation = kAnchorSaturation,
                Luma = kAnchorLuma,
                Weight = 1f,
            });
            profile.ReferenceFingerprint = "integration-test";
            return profile;
        }

        /// <summary>The anchor's linear color, rebuilt with the kernel's own math (unit color
        /// scaled so the Rec.709 luma hits the anchor luma).</summary>
        private static Color AnchorLinearColor
        {
            get
            {
                Color unit = HsvUnit(kAnchorHue, kAnchorSaturation);
                float unitLuma = unit.r * kLumaR + unit.g * kLumaG + unit.b * kLumaB;
                return unit * (kAnchorLuma / unitLuma);
            }
        }

        /// <summary>Chromatic hash-grain base: pure orange scaled by luma-only grain — hue and
        /// saturation are constant, so the palette assertions are exact.</summary>
        private static Color GrainOrange(int x, int y, int size)
        {
            float grain = HashNoise(x, y) * kGrainAmplitude + kGrainCenter;
            return new Color(kSourceRed * grain, kSourceGreen * grain, kSourceBlue * grain, 1f);
        }

        /// <summary>Deterministic hash noise in [-1, 1] (the project-memory fixture recipe).</summary>
        private static float HashNoise(int x, int y)
        {
            float f = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            return (f - Mathf.Floor(f)) * 2f - 1f;
        }

        private static Texture2D CreateGrainBaseMap()
        {
            var texture = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true)
            {
                name = "GrainOrangeBase",
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < WorkingSize; y++)
            {
                for (int x = 0; x < WorkingSize; x++)
                {
                    texture.SetPixel(x, y, GrainOrange(x, y, WorkingSize));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateWhiteOcclusion()
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
            {
                name = "WhiteOcclusion",
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>Grayscale hash-grain occlusion (authored _OcclusionMap stand-in):
        /// texels in [0.25, 0.75], median 0.5 — the D-08 remap oracles are exact.</summary>
        private static Texture2D CreateGrainOcclusion()
        {
            var texture = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true)
            {
                name = "GrainOcclusion",
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < WorkingSize; y++)
            {
                for (int x = 0; x < WorkingSize; x++)
                {
                    float ao = HashNoise(x, y) * kOcclusionGrainAmplitude + kOcclusionGrainCenter;
                    texture.SetPixel(x, y, new Color(ao, ao, ao, 1f));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        /// <summary>Row-alternating metallic/gloss map: .r = 0 (no metallic), .a =
        /// smoothness center 0.5 (even rows) / 0.75 (odd rows) with hash grain — the
        /// period-2 structure is smaller than the radius-2 simplify window.</summary>
        private static Texture2D CreateRowAlternatingGlossMap()
        {
            var texture = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true)
            {
                name = "RowAlternatingGloss",
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < WorkingSize; y++)
            {
                float center = (y % 2 == 0) ? kSmoothnessRowEven : kSmoothnessRowOdd;
                for (int x = 0; x < WorkingSize; x++)
                {
                    float smoothness = center + HashNoise(x, y) * kSmoothnessGrainAmplitude;
                    texture.SetPixel(x, y, new Color(0f, 0f, 0f, smoothness));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        /// <summary>Raw-DirectX-layout grain normals: x/y = 0.5 ± hash grain (signed xy
        /// spans [-0.8, 0.8]), z is rebuilt by the kernel (never sampled), a = 1
        /// (the raw-RGB layout flag).</summary>
        private static Texture2D CreateGrainNormalMap()
        {
            var texture = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true)
            {
                name = "GrainNormals",
                hideFlags = HideFlags.HideAndDontSave,
            };
            for (int y = 0; y < WorkingSize; y++)
            {
                for (int x = 0; x < WorkingSize; x++)
                {
                    float nx = 0.5f + HashNoise(x, y) * kNormalGrainAmplitude;
                    float ny = 0.5f + HashNoise(y, x) * kNormalGrainAmplitude;
                    texture.SetPixel(x, y, new Color(nx, ny, 0.5f, 1f));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        private static Mesh CreateQuadMesh()
        {
            Mesh mesh = new Mesh { name = "GrainOrangeQuad", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        // -- readback helpers -------------------------------------------------

        private static NativeArray<Color32> ReadBackColor32Native(RenderTexture rt)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.forcePlayerLoopUpdate = true;
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "GPU readback must not error");
            return request.GetData<Color32>();
        }

        private static Color32[] ReadBackColor32(RenderTexture rt)
        {
            NativeArray<Color32> data = ReadBackColor32Native(rt);
            try
            {
                var result = new Color32[data.Length];
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

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color> data = req.GetData<Color>();
            try
            {
                var result = new Color[data.Length];
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

        /// <summary>Counts exact per-channel byte mismatches between two readbacks.</summary>
        private static int CountByteDifferences(Color32[] a, Color32[] b)
        {
            Assert.AreEqual(a.Length, b.Length, "readback sizes must match");
            int differences = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a)
                {
                    differences++;
                }
            }

            return differences;
        }

        // -- kernel-math mirrors (assertion oracles) ---------------------------

        private static float LumaOf(Color c)
        {
            return c.r * kLumaR + c.g * kLumaG + c.b * kLumaB;
        }

        private static float SaturationOf(Color c)
        {
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            return maxc <= 0f ? 0f : (maxc - minc) / maxc;
        }

        private static float HueOf(Color c)
        {
            float maxc = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            float minc = Mathf.Min(c.r, Mathf.Min(c.g, c.b));
            float chroma = maxc - minc;
            if (chroma <= 0f)
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
                h = (c.b - c.r) / chroma + kSectorGreen;
            }
            else
            {
                h = (c.r - c.g) / chroma + kSectorBlue;
            }

            return Mathf.Repeat(h / kHueSectors, 1f);
        }

        /// <summary>The V=1 sector table for (hue, saturation) — the kernel's NamerStylizeHsvUnit.</summary>
        private static Color HsvUnit(float h, float s)
        {
            h = Mathf.Repeat(h, 1f) * kHueSectors;
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

        private static float MedianOf(Color[] texels, Func<Color, float> selector)
        {
            var values = new float[texels.Length];
            for (int i = 0; i < texels.Length; i++)
            {
                values[i] = selector(texels[i]);
            }

            Array.Sort(values);
            return values[values.Length / 2];
        }

        /// <summary>Median of a selector over the inset interior of a square
        /// <see cref="Color32"/> readback (edge texels excluded).</summary>
        private static float MedianOf(Color32[] texels, int inset, Func<Color32, float> selector)
        {
            var values = new List<float>();
            for (int y = inset; y < WorkingSize - inset; y++)
            {
                for (int x = inset; x < WorkingSize - inset; x++)
                {
                    values.Add(selector(texels[y * WorkingSize + x]));
                }
            }

            values.Sort();
            return values[values.Count / 2];
        }

        /// <summary>Minimum byte of a selector over the inset interior.</summary>
        private static byte MinChannel(Color32[] texels, int inset, Func<Color32, byte> selector)
        {
            byte min = byte.MaxValue;
            for (int y = inset; y < WorkingSize - inset; y++)
            {
                for (int x = inset; x < WorkingSize - inset; x++)
                {
                    byte value = selector(texels[y * WorkingSize + x]);
                    if (value < min)
                    {
                        min = value;
                    }
                }
            }

            return min;
        }

        /// <summary>Decodes the 6-bit roughness from the packed surface alpha byte
        /// (Core/NamerFormat NamerPackAlphaBits mirror).</summary>
        private static float RoughnessOf(Color32 surface)
        {
            return (surface.a & kRoughnessAlphaMask) / kRoughnessQuantLevels;
        }

        /// <summary>Max deviation of per-row medians (inset interior rows) from their
        /// own median — the spread metric for the roughness-simplify oracle. A radius-2
        /// box mean cross-cuts period-2 row structure, so the spread collapses.</summary>
        private static float RowMedianSpread(Color32[] texels, Func<Color32, float> selector)
        {
            int inner = WorkingSize - 2 * kSampleInset;
            var rowMedians = new float[inner];
            var rowValues = new float[inner];
            for (int r = 0; r < inner; r++)
            {
                int y = kSampleInset + r;
                for (int x = kSampleInset; x < WorkingSize - kSampleInset; x++)
                {
                    rowValues[x - kSampleInset] = selector(texels[y * WorkingSize + x]);
                }

                Array.Sort(rowValues, 0, inner);
                rowMedians[r] = rowValues[inner / 2];
            }

            float[] sorted = (float[])rowMedians.Clone();
            Array.Sort(sorted);
            float center = sorted[inner / 2];
            float spread = 0f;
            for (int r = 0; r < inner; r++)
            {
                spread = Mathf.Max(spread, Mathf.Abs(rowMedians[r] - center));
            }

            return spread;
        }

        private static Color32 ToColor32(Color c)
        {
            return new Color32(
                (byte)Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f),
                (byte)Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255f));
        }

        // -- teardown helpers ---------------------------------------------------

        private static void Destroy(params UnityEngine.Object[] objects)
        {
            foreach (UnityEngine.Object o in objects)
            {
                if (o != null)
                {
                    UnityEngine.Object.DestroyImmediate(o);
                }
            }
        }
    }
}

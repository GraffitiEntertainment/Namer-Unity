using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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

        // Roughness-simplify derived oracle (run A pinned this to the digit): a
        // radius-2 box mean over period-2 rows mixes 3 own-parity + 2 other-parity
        // taps, so roughness 0.5 / 0.25 filters to 0.40 / 0.35 — the Dirichlet
        // residual is exactly 1/5 of the 0.25 row spread. The alpha quantizer is
        // FLOOR to 63 levels (NamerPackAlphaBits), so the row medians floor to
        // 25/63 and 22/63: spread EXACTLY 3/63. That value pins the radius: a
        // radius-1 (3x3) window would give 26/21 = 5/63 and radius-3 (7x7)
        // 24/22 = 2/63 — both outside the half-step tolerance. (Pre-vs-post
        // quantization ORDER is not separable through this metric under floor
        // quantization; the Sobel-path test pins the ordering.)
        private const float kSimplifySpreadSteps = 3f;
        private const float kHalfQuantStep = 0.5f;

        // Grain normal fixture amplitude: signed xy spans [-0.8, 0.8] — genuinely
        // tilted normals (median +Z dot far below 1) that flatten must collapse.
        private const float kNormalGrainAmplitude = 0.4f;

        // 05-03 Task 2 E2E constants. The E2E temp folder holds the source mesh/base/
        // material/profile assets and the generated output, deleted in finally. The
        // monochrome reference family is magenta (hue exactly 5/6 — R == B makes the
        // hue ratio invariant under any per-channel encoding, so the PNG readback hue
        // is exact), three refs of one hue at varying luma.
        private const string StyleE2ETempFolder = "Assets/NAMER_Style_E2E_Temp";
        private const float kMonochromeFamilyHue = 5f / 6f;
        private const float kE2EHueTolerance = 0.01f;
        // Derived-bound tolerance (round 2): the saturation oracle is computed from
        // the tie-winning anchor's sRGB byte round-trip, so it only wobbles by the
        // +-1-byte PNG quantization of each channel (~1/200 here) — 0.01 covers it.
        private const float kE2ESaturationTolerance = 0.01f;
        private const float kSmoothStdReductionFactor = 0.75f;
        private const string kDeadAssetGuid = "deadbeefdeadbeefdeadbeefdeadbeef";

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
        // _PackInputs BEFORE the 6-bit quantization. A 5-tap box CANNOT fully
        // collapse period-2 rows — it attenuates them by the Dirichlet factor
        // 1/5 — so the oracle is the DERIVED residual spread (3/63 quantized;
        // see the kSimplifySpreadSteps derivation), not a collapse threshold.
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
                        Assert.AreEqual(kSimplifySpreadSteps / kRoughnessQuantLevels, simplifiedSpread,
                            kHalfQuantStep / kRoughnessQuantLevels,
                            "radius-2 box mean at strength 1 must leave the DERIVED 1/5 Dirichlet residual " +
                            "(row medians floor to 25/63 vs 22/63 = 3/63; 5/63 = radius-1 window, 2/63 = radius-3)");
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
        // 05-03 Task 2: the mesh-priming predicate (unit, no GPU).
        // --------------------------------------------------------------------

        [Test]
        public void StylizationNeedsMesh_OnlyWhenProfileRequestsSmoothing()
        {
            Assert.IsFalse(NamerProcessor.StylizationNeedsMesh(null),
                "no profile must never prime a mesh (byte-identity guard)");

            NamerStyleProfile paletteOnly = ScriptableObject.CreateInstance<NamerStyleProfile>();
            paletteOnly.Smoothing = 0f;
            Assert.IsFalse(NamerProcessor.StylizationNeedsMesh(paletteOnly),
                "Smoothing 0 (palette-only) must not prime a mesh");

            NamerStyleProfile smoothing = ScriptableObject.CreateInstance<NamerStyleProfile>();
            smoothing.Smoothing = 0.5f;
            Assert.IsTrue(NamerProcessor.StylizationNeedsMesh(smoothing),
                "Smoothing > 0 must prime the source mesh for island ids");
            Destroy(paletteOnly, smoothing);
        }

        // --------------------------------------------------------------------
        // 05-03 Task 2 E2E: an attached profile restyles the BAKED assets through
        // the full NamerProcessor (STYL-05/STYL-06 wire-out). Three same-hue
        // magenta references (a monochrome family — single hue, varying luma)
        // extract to chromatic anchors whose hue is exactly 5/6; Replace at
        // strength 1 must snap the baked base's hue to that anchor hue.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Process_E2E_AttachedProfile_StylingFlowsIntoBakedAssets()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU styled-bake E2E test (D-15).");
                yield break;
            }

            EnsureStyleE2ETempFolder();
            E2ESettingsSnapshot prefs = CaptureE2ESettings();
            GameObject gameObject = null;
            try
            {
                Mesh mesh = CreateMeshAsset(StyleE2ETempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateGrainBaseMapAsset(StyleE2ETempFolder + "/SourceBase.png");
                Material source = CreateE2ESourceMaterial(StyleE2ETempFolder, "SourceMat", baseMap);
                gameObject = CreateE2ESceneObject(mesh, source);

                NamerStyleProfile profile = CreateExtractedProfileAsset(StyleE2ETempFolder + "/StyleProfile.asset");
                var settings = NewPinnedE2ESettings(StyleE2ETempFolder + "/Out");
                settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));

                // Resolution contract (the same composition NamerProcessor uses).
                NamerStyleProfile resolved = NamerProcessor.ResolveStyleProfile(settings);
                Assert.AreSame(profile, resolved, "a live GUID must resolve back to the profile asset");
                Assert.IsNotNull(resolved.PaletteAnchors, "extraction must have produced anchors");
                Assert.Greater(resolved.Stats.ReferenceCount, 0, "extraction must have counted the references");

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "styled Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                Assert.IsTrue(File.Exists(asset.BaseTexturePath), "the styled base PNG must be written: " + asset.BaseTexturePath);
                Assert.IsTrue(File.Exists(asset.SurfaceTexturePath), "the packed surface PNG must be written: " + asset.SurfaceTexturePath);

                // The dominant chromatic anchor's hue IS the oracle (self-calibrating —
                // the refs are one hue family, so every anchor hue is exactly 5/6).
                float anchorHue = DominantAnchorHue(resolved);
                Assert.AreEqual(kMonochromeFamilyHue, anchorHue, kE2EHueTolerance,
                    "the magenta-family refs must extract a 5/6-hue dominant anchor");

                Color32[] baked = DecodePng(asset.BaseTexturePath, out int width, out int height);
                float medianHue = MedianOf(baked, kSampleInset, c => HueOf(c));
                float medianSaturation = MedianOf(baked, kSampleInset, c => SaturationOf(c));

                Assert.AreEqual(anchorHue, medianHue, kE2EHueTolerance,
                    "Replace at strength 1: the BAKED base hue must snap to the anchor hue (orange 1/12 is the unstyled control)");

                // DERIVED saturation bound (round 2): a 0.9 floor is unattainable. The
                // extractor's 16-bin/channel histogram reconstructs every occupied bin
                // at its bin CENTER, so a pure (x, 0, x) magenta ref extracts as
                // (x', 0.5/16, x') — anchor saturation is structurally < 1. All three
                // anchors tie at the family hue, so CSPaletteTransfer's strict-less
                // -than scan picks the FIRST chromatic anchor (slot 0 = the darkest
                // equal-mass bin, (0.59375, 0.5/16, 0.59375)), and at strength 1 +
                // Replace NamerStylizeRebuild inverts the extraction exactly: the
                // styled base IS that uniform color. The base PNG then sRGB-encodes it
                // to bytes (202, 49, 202), and the byte-space saturation reads
                // (202 - 49) / 202 = 0.7574 — the sRGB curve amplifies the dark G
                // (byte ratio 49/202 = 0.243 vs linear 0.03125/0.59375 = 0.0526).
                // Derive the oracle from the same anchor instead of assuming 1.0.
                float expectedSaturation = ExpectedByteSaturation(TieWinningChromaticAnchor(resolved).LinearColor);
                Assert.AreEqual(expectedSaturation, medianSaturation, kE2ESaturationTolerance,
                    "Replace at strength 1: the baked base must carry the tie-winning anchor's byte-round-tripped saturation");
            }
            finally
            {
                Destroy(gameObject);
                RestoreE2ESettings(prefs);
                AssetDatabase.DeleteAsset(StyleE2ETempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 05-03 Task 2 identity E2E: empty GUID, dead (deleted-asset) GUID, and an
        // attached all-identity profile all bake the SAME bytes — the 04.3
        // baseline (zero stylize dispatches, silent detach, no exception).
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Process_NullOrDeadGuid_ByteIdenticalToIdentityProfile()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU identity E2E test (D-15).");
                yield break;
            }

            EnsureStyleE2ETempFolder();
            E2ESettingsSnapshot prefs = CaptureE2ESettings();
            GameObject gameObject = null;
            try
            {
                Mesh mesh = CreateMeshAsset(StyleE2ETempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateGrainBaseMapAsset(StyleE2ETempFolder + "/SourceBase.png");
                Material source = CreateE2ESourceMaterial(StyleE2ETempFolder, "SourceMat", baseMap);
                gameObject = CreateE2ESceneObject(mesh, source);

                // All-identity attached profile: unextracted (no anchors), strength 0,
                // smoothing 0 — every new branch is walked but each is identity.
                NamerStyleProfile identity = ScriptableObject.CreateInstance<NamerStyleProfile>();
                identity.name = "IdentityProfile";
                AssetDatabase.CreateAsset(identity, StyleE2ETempFolder + "/IdentityProfile.asset");

                var settings = NewPinnedE2ESettings(StyleE2ETempFolder + "/Out");
                settings.OverwriteGenerated = true;

                settings.StyleProfileGuid = string.Empty;
                NamerProcessResult nullGuid = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(nullGuid.Error, "null-GUID Process should succeed: " + nullGuid.Error);
                Color32[] nullGuidBase = DecodePng(nullGuid.GeneratedAssets[0].BaseTexturePath, out int _, out int _);
                Color32[] nullGuidSurface = DecodePng(nullGuid.GeneratedAssets[0].SurfaceTexturePath, out int _, out int _);

                settings.StyleProfileGuid = kDeadAssetGuid;
                NamerProcessResult deadGuid = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(deadGuid.Error,
                    "a deleted-asset GUID must silently detach (T-05-11), never error: " + deadGuid.Error);
                Assert.IsNull(NamerProcessor.ResolveStyleProfile(settings),
                    "a dead GUID must resolve to a null profile");
                Color32[] deadGuidBase = DecodePng(deadGuid.GeneratedAssets[0].BaseTexturePath, out int _, out int _);
                Color32[] deadGuidSurface = DecodePng(deadGuid.GeneratedAssets[0].SurfaceTexturePath, out int _, out int _);

                settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(identity));
                NamerProcessResult identityProfile = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(identityProfile.Error, "identity-profile Process should succeed: " + identityProfile.Error);
                Color32[] identityBase = DecodePng(identityProfile.GeneratedAssets[0].BaseTexturePath, out int _, out int _);
                Color32[] identitySurface = DecodePng(identityProfile.GeneratedAssets[0].SurfaceTexturePath, out int _, out int _);

                Assert.AreEqual(0, CountByteDifferences(nullGuidBase, deadGuidBase),
                    "null GUID and dead GUID must bake byte-identical bases");
                Assert.AreEqual(0, CountByteDifferences(nullGuidSurface, deadGuidSurface),
                    "null GUID and dead GUID must bake byte-identical surfaces");
                Assert.AreEqual(0, CountByteDifferences(nullGuidBase, identityBase),
                    "an all-identity attached profile must bake the 04.3 baseline base byte for byte");
                Assert.AreEqual(0, CountByteDifferences(nullGuidSurface, identitySurface),
                    "an all-identity attached profile must bake the 04.3 baseline surface byte for byte");
            }
            finally
            {
                Destroy(gameObject);
                RestoreE2ESettings(prefs);
                AssetDatabase.DeleteAsset(StyleE2ETempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 05-03 Task 2 priming E2E: Smoothing > 0 with decomposition OFF primes
        // the source mesh — the island-gated bilateral runs and measurably
        // flattens the grain base in the BAKED asset (no priming = skip = the
        // grain survives untouched).
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Process_ProfileSmoothing_PrimesMeshWithoutDecomposition()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU smoothing-priming E2E test (D-15).");
                yield break;
            }

            EnsureStyleE2ETempFolder();
            E2ESettingsSnapshot prefs = CaptureE2ESettings();
            GameObject gameObject = null;
            try
            {
                Mesh mesh = CreateMeshAsset(StyleE2ETempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateGrainBaseMapAsset(StyleE2ETempFolder + "/SourceBase.png");
                Material source = CreateE2ESourceMaterial(StyleE2ETempFolder, "SourceMat", baseMap);
                gameObject = CreateE2ESceneObject(mesh, source);

                // Smoothing-only profile: unextracted (no palette) so the ONLY effect is
                // the island-gated bilateral — its variance drop proves the mesh was
                // primed, split, and rasterized (skip would leave the grain untouched).
                NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
                profile.name = "SmoothingProfile";
                profile.Smoothing = 0.5f;
                AssetDatabase.CreateAsset(profile, StyleE2ETempFolder + "/SmoothingProfile.asset");

                var settings = NewPinnedE2ESettings(StyleE2ETempFolder + "/Out");
                settings.OverwriteGenerated = true;
                string profileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));
                settings.StyleProfileGuid = profileGuid;

                // The unsmoothed CONTROL must run with NO profile attached (the empty
                // GUID is 05-03 Task 2's identity path): with the smoothing profile
                // attached, the bilateral flattens the "plain" bake too and the
                // variance oracle loses its baseline (round 2 — both runs came out
                // byte-identical smoothed output).
                settings.StyleProfileGuid = string.Empty;
                settings.OverwriteGenerated = false;
                NamerProcessResult unsmoothed = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(unsmoothed.Error, "unsmoothed Process should succeed: " + unsmoothed.Error);
                Color32[] plain = DecodePng(unsmoothed.GeneratedAssets[0].BaseTexturePath, out int _, out int _);

                settings.OverwriteGenerated = true;
                settings.StyleProfileGuid = profileGuid;
                NamerProcessResult smoothed = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(smoothed.Error, "smoothed Process should succeed: " + smoothed.Error);
                Color32[] island = DecodePng(smoothed.GeneratedAssets[0].BaseTexturePath, out int _, out int _);

                float plainStd = InteriorLumaStdDev(plain);
                float islandStd = InteriorLumaStdDev(island);
                Assert.Greater(plainStd, 0.01f, "the grain base must carry measurable luma variance");
                Assert.Less(islandStd, kSmoothStdReductionFactor * plainStd,
                    "Smoothing 0.5 with decomposition OFF must prime the mesh and flatten the grain in the baked base");
            }
            finally
            {
                Destroy(gameObject);
                RestoreE2ESettings(prefs);
                AssetDatabase.DeleteAsset(StyleE2ETempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 05-03 Task 2 no-mesh fallback: a bare-material selection (no mesh) with
        // Smoothing > 0 degrades to palette-only — the skip flag is set on the
        // composed context, palette transfer still styles the bake, no exception.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Process_BareMaterial_PaletteRunsSmoothingSkips()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU no-mesh fallback E2E test (D-15).");
                yield break;
            }

            EnsureStyleE2ETempFolder();
            E2ESettingsSnapshot prefs = CaptureE2ESettings();
            try
            {
                Texture2D baseMap = CreateGrainBaseMapAsset(StyleE2ETempFolder + "/SourceBase.png");
                Material source = CreateE2ESourceMaterial(StyleE2ETempFolder, "SourceMat", baseMap);

                NamerStyleProfile profile = CreateExtractedProfileAsset(StyleE2ETempFolder + "/StyleProfile.asset");
                profile.Smoothing = 0.5f;

                // The composed context for a mesh-less selection: palette available +
                // strength 1 => Active, but the islands are missing => skip flag set.
                NamerStyleStylization context = NamerStyleStylization.Create(profile, null);
                Assert.IsTrue(context.Active, "palette at strength 1 must keep the context active without a mesh");
                Assert.IsTrue(context.SmoothingSkippedNoMesh,
                    "Smoothing > 0 without island data must set SmoothingSkippedNoMesh (OQ-3)");

                var settings = NewPinnedE2ESettings(StyleE2ETempFolder + "/Out");
                settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(profile));

                NamerProcessResult result = NamerProcessor.Process(source, settings);
                Assert.IsNull(result.Error, "bare-material Process should succeed (palette-only degrade): " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                Color32[] baked = DecodePng(result.GeneratedAssets[0].BaseTexturePath, out int _, out int _);
                float medianHue = MedianOf(baked, kSampleInset, c => HueOf(c));
                Assert.AreEqual(DominantAnchorHue(profile), medianHue, kE2EHueTolerance,
                    "palette transfer must still restyle the bare-material bake to the anchor hue");
            }
            finally
            {
                RestoreE2ESettings(prefs);
                AssetDatabase.DeleteAsset(StyleE2ETempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 05-03 Task 3 — the window's W3/W4 seams: the preview lens composition
        // (ComposePreviewStylization) and the attach-state copy
        // (DescribeProfileState). Pure statics — no GPU, no prefs.
        // --------------------------------------------------------------------

        [Test]
        public void ComposePreviewStylization_LensGatesPreviewOnly()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.Smoothing = 0.5f;

                // Lens NAMER (0): no stylization even with a profile — the preview
                // must stay byte-identical to the 04.3 baseline (scalars zeroed).
                Assert.IsNull(
                    NamerEditorWindow.ComposePreviewStylization(profile, NamerEditorWindow.kAfterViewNamer, null),
                    "After View = NAMER must preview the un-stylized baseline regardless of profile/split");

                // No profile: the popup is disabled — composition must be null even at
                // the stylized index (no NullReference, no default-asset styling).
                Assert.IsNull(
                    NamerEditorWindow.ComposePreviewStylization(null, NamerEditorWindow.kAfterViewStylized, null),
                    "no attached profile must compose to null even at After View = Stylized NAMER");

                // Lens Stylized NAMER (1) with a profile: a composed context. Without
                // a split the smoothing branch sets the skip flag — the W1 Info note's
                // trigger (OQ-3).
                NamerStyleStylization composed = NamerEditorWindow.ComposePreviewStylization(
                    profile, NamerEditorWindow.kAfterViewStylized, null);
                Assert.IsNotNull(composed, "profile + Stylized NAMER must compose a preview context");
                Assert.IsTrue(composed.SmoothingSkippedNoMesh,
                    "no split handed to the composition must set SmoothingSkippedNoMesh (OQ-3)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void DescribeProfileState_MatchesUiSpecCopy()
        {
            NamerStyleProfile notExtracted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            notExtracted.name = "Ink";
            NamerStyleProfile extracted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                // IsExtracted reads the fingerprint Extract writes (T-05-11), so a
                // synthetic non-empty one marks the extracted state; anchors + stats
                // supply the copy's counts.
                extracted.name = "Ink";
                extracted.ReferenceFingerprint = "synthetic-fingerprint";
                extracted.PaletteAnchors.Add(new NamerStyleAnchor());
                extracted.PaletteAnchors.Add(new NamerStyleAnchor());
                extracted.Stats = new NamerStyleStats { ReferenceCount = 3 };
                Assert.IsTrue(extracted.IsExtracted, "fixture setup: a written fingerprint must mark the profile extracted");
                Assert.IsFalse(notExtracted.IsExtracted, "fixture setup: a fresh profile must not read as extracted");

                // W3 locked copy (05-UI-SPEC): "" for nothing attached (the popup's
                // disabled hint carries that state), the honesty line otherwise.
                Assert.AreEqual(string.Empty, NamerEditorWindow.DescribeProfileState(null));

                Assert.AreEqual(
                    "Ink — not extracted",
                    NamerEditorWindow.DescribeProfileState(notExtracted));

                Assert.AreEqual(
                    "Ink — palette extracted (2 anchors, 3 references)",
                    NamerEditorWindow.DescribeProfileState(extracted));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(notExtracted);
                UnityEngine.Object.DestroyImmediate(extracted);
            }
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

        // -- 05-03 Task 2 E2E fixtures (NamerAOBakeTests processor-E2E idiom) ----

        private static void EnsureStyleE2ETempFolder()
        {
            if (AssetDatabase.IsValidFolder(StyleE2ETempFolder))
            {
                AssetDatabase.DeleteAsset(StyleE2ETempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_Style_E2E_Temp");
        }

        /// <summary>Quad mesh persisted as an asset so the scene object's renderer
        /// wears a resolvable sharedMesh (ResolveSourceMesh's scene path).</summary>
        private static Mesh CreateMeshAsset(string path)
        {
            // CreateQuadMesh's HideAndDontSave is for the in-memory fixtures only —
            // AssetDatabase.CreateAsset on a DontSave-flagged object trips Unity's
            // saver assert (kAllowDontSaveObjectsToBePersistent). The proven idiom
            // is NamerProjectionAcceptanceTests.CreateQuadMeshAsset: a flag-free
            // mesh on the persisted path. Clear the flag before the asset write.
            Mesh mesh = CreateQuadMesh();
            mesh.hideFlags = HideFlags.None;
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        /// <summary>The grain-orange base as an imported linear PNG asset — the
        /// processor's source BaseMap (CreateImportedSolidBaseMap idiom).</summary>
        private static Texture2D CreateGrainBaseMapAsset(string path)
        {
            Texture2D source = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < WorkingSize; y++)
            {
                for (int x = 0; x < WorkingSize; x++)
                {
                    source.SetPixel(x, y, GrainOrange(x, y, WorkingSize));
                }
            }

            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material CreateE2ESourceMaterial(string folder, string name, Texture2D baseMap)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");
            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static GameObject CreateE2ESceneObject(Mesh mesh, Material material)
        {
            var go = new GameObject("StyleE2ETarget");
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return go;
        }

        /// <summary>Extracted profile ASSET via the REAL extractor: three readable
        /// same-hue magenta refs (monochrome family), extraction applied with the
        /// editor's exact apply sequence, then strength 1 + Replace mode, saved to
        /// <paramref name="path"/> so its GUID is resolvable.</summary>
        private static NamerStyleProfile CreateExtractedProfileAsset(string path)
        {
            Texture2D bright = CreateSolidRef("RefMagentaBright", new Color(1f, 0f, 1f, 1f));
            Texture2D mid = CreateSolidRef("RefMagentaMid", new Color(0.8f, 0f, 0.8f, 1f));
            Texture2D dark = CreateSolidRef("RefMagentaDark", new Color(0.6f, 0f, 0.6f, 1f));
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(bright);
                profile.References.Add(mid);
                profile.References.Add(dark);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.IsFalse(result.Cancelled, "headless extraction must not cancel");
                Assert.IsFalse(result.TooFewReferences, "three refs must satisfy the minimum");
                Assert.IsTrue(string.IsNullOrEmpty(result.ErrorTextureName), "readable in-memory refs must not error");
                Assert.IsNotNull(result.Anchors, "three readable refs must extract anchors");

                // The NamerStyleProfileEditor apply sequence (05-01), verbatim.
                profile.PaletteAnchors.Clear();
                for (int i = 0; i < result.Anchors.Length; i++)
                {
                    profile.PaletteAnchors.Add(result.Anchors[i]);
                }

                profile.Stats = result.Stats;
                profile.ReferenceFingerprint = result.Fingerprint;
                profile.StyleStrength = 1f;
                profile.Smoothing = 0f;
                profile.HueMode = kHueModeReplace;

                // The refs are in-memory (non-persistent) — Stats.ReferenceCount is
                // already counted, so drop them before serialization instead of
                // writing broken references into the asset.
                profile.References.Clear();

                AssetDatabase.CreateAsset(profile, path);
                return profile;
            }
            finally
            {
                Destroy(bright, mid, dark);
            }
        }

        private static Texture2D CreateSolidRef(string name, Color color)
        {
            var texture = new Texture2D(16, 16, TextureFormat.RGBA32, false, true)
            {
                name = name,
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color[] pixels = new Color[16 * 16];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static bool NeverCancel()
        {
            return false;
        }

        /// <summary>The dominant chromatic anchor's hue (max weight, sat above the
        /// achromatic cutoff) — the extraction-derived oracle hue.</summary>
        private static float DominantAnchorHue(NamerStyleProfile profile)
        {
            bool found = false;
            NamerStyleAnchor dominant = default;
            foreach (NamerStyleAnchor anchor in profile.PaletteAnchors)
            {
                if (anchor.Saturation >= 0.15f && (!found || anchor.Weight > dominant.Weight))
                {
                    found = true;
                    dominant = anchor;
                }
            }

            Assert.IsTrue(found, "the magenta-family refs must extract at least one chromatic anchor");
            return dominant.Hue;
        }

        /// <summary>The anchor CSPaletteTransfer actually matches for this fixture:
        /// every extracted anchor ties at the family hue (R == B refs), and the
        /// kernel's chromatic scan keeps the FIRST anchor on a tie (strict
        /// less-than), so the winner is the first anchor above the achromatic
        /// cutoff (0.15 mirrors NAMER_STYLIZE_ACHROMATIC_CUTOFF; the extractor
        /// reserves the flagged slots LAST, so chromatic slots come first).</summary>
        private static NamerStyleAnchor TieWinningChromaticAnchor(NamerStyleProfile profile)
        {
            const float kAchromaticCutoff = 0.15f;
            foreach (NamerStyleAnchor anchor in profile.PaletteAnchors)
            {
                if (anchor.Saturation >= kAchromaticCutoff)
                {
                    return anchor;
                }
            }

            Assert.Fail("the extracted profile must carry at least one chromatic anchor");
            return default;
        }

        /// <summary>The saturation the baked-base readback measures for a uniform
        /// linear color: sRGB-encode each channel exactly as the base PNG does
        /// (IEC 61966-2-1 piecewise), quantize to bytes, then apply the same
        /// (max - min) / max the test computes on the decoded Color32.</summary>
        private static float ExpectedByteSaturation(Color linear)
        {
            byte r = SrgbByte(linear.r);
            byte g = SrgbByte(linear.g);
            byte b = SrgbByte(linear.b);
            byte max = Math.Max(r, Math.Max(g, b));
            byte min = Math.Min(r, Math.Min(g, b));
            return max > 0 ? (max - min) / (float)max : 0f;
        }

        /// <summary>Linear-to-sRGB encode (IEC 61966-2-1) rounded to a byte — the
        /// base PNG save path's exact per-channel quantization.</summary>
        private static byte SrgbByte(float linear)
        {
            const float kLinearCutoff = 0.0031308f;
            const float kLinearSlope = 12.92f;
            const float kGammaScale = 1.055f;
            const float kGammaOffset = 0.055f;
            const float kGammaExponent = 1f / 2.4f;
            const float kMaxByte = 255f;
            float c = Mathf.Clamp01(linear);
            float encoded = c <= kLinearCutoff
                ? kLinearSlope * c
                : kGammaScale * Mathf.Pow(c, kGammaExponent) - kGammaOffset;
            return (byte)Mathf.RoundToInt(kMaxByte * encoded);
        }

        private static Color32[] DecodePng(string path, out int width, out int height)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            bool loaded = ImageConversion.LoadImage(texture, bytes);
            Assert.IsTrue(loaded, "failed to decode PNG at " + path);
            Color32[] pixels = texture.GetPixels32();
            width = texture.width;
            height = texture.height;
            Destroy(texture);
            return pixels;
        }

        /// <summary>Standard deviation of Rec.709 luma over the inset interior — the
        /// smoothing-flattens-grain metric for the priming E2E.</summary>
        private static float InteriorLumaStdDev(Color32[] texels)
        {
            double sum = 0;
            double sumSquares = 0;
            long count = 0;
            for (int y = kSampleInset; y < WorkingSize - kSampleInset; y++)
            {
                for (int x = kSampleInset; x < WorkingSize - kSampleInset; x++)
                {
                    float luma = LumaOf(texels[y * WorkingSize + x]);
                    sum += luma;
                    sumSquares += (double)luma * luma;
                    count++;
                }
            }

            Assert.Greater(count, 0, "the interior sample region must be non-empty");
            double mean = sum / count;
            return (float)Math.Sqrt(sumSquares / count - mean * mean);
        }

        /// <summary>Pins every outcome-relevant setting (Pitfall 6): the E2E writes
        /// these EditorPrefs-backed keys, so they are explicitly identity/default here
        /// and snapshot-restored in the test's finally.</summary>
        private static NamerProcessorSettings NewPinnedE2ESettings(string destination)
        {
            return new NamerProcessorSettings
            {
                Destination = destination,
                DecompositionEnabled = false,
                AoStageEnabled = false,
                RoughnessStageEnabled = false,
                MetallicContributionEnabled = false,
                EmissiveContributionEnabled = false,
                AoStrength = 1f,
                AoContrast = 1f,
                AoBlurRadius = 0f,
            };
        }

        // -- E2E EditorPrefs isolation (the keys NewPinnedE2ESettings writes) ----

        private sealed class E2ESettingsSnapshot
        {
            public string Destination;
            public string StyleProfileGuid;
            public bool DecompositionEnabled;
            public bool AoStageEnabled;
            public bool RoughnessStageEnabled;
            public bool MetallicContributionEnabled;
            public bool EmissiveContributionEnabled;
            public bool OverwriteGenerated;
            public float AoStrength;
            public float AoContrast;
            public float AoBlurRadius;
            public bool HadDestination;
            public bool HadStyleProfileGuid;
            public bool HadDecompositionEnabled;
            public bool HadAoStageEnabled;
            public bool HadRoughnessStageEnabled;
            public bool HadMetallicContributionEnabled;
            public bool HadEmissiveContributionEnabled;
            public bool HadOverwriteGenerated;
            public bool HadAoStrength;
            public bool HadAoContrast;
            public bool HadAoBlurRadius;
        }

        private static E2ESettingsSnapshot CaptureE2ESettings()
        {
            return new E2ESettingsSnapshot
            {
                Destination = EditorPrefs.GetString("NamerProcessor.Destination", string.Empty),
                StyleProfileGuid = EditorPrefs.GetString("NamerProcessor.StyleProfileGuid", string.Empty),
                DecompositionEnabled = EditorPrefs.GetBool("NamerProcessor.DecompositionEnabled", false),
                AoStageEnabled = EditorPrefs.GetBool("NamerProcessor.AoStageEnabled", true),
                RoughnessStageEnabled = EditorPrefs.GetBool("NamerProcessor.RoughnessStageEnabled", true),
                MetallicContributionEnabled = EditorPrefs.GetBool("NamerProcessor.MetallicContributionEnabled", true),
                EmissiveContributionEnabled = EditorPrefs.GetBool("NamerProcessor.EmissiveContributionEnabled", true),
                OverwriteGenerated = EditorPrefs.GetBool("NamerProcessor.OverwriteGenerated", false),
                AoStrength = EditorPrefs.GetFloat("NamerProcessor.AoStrength", 1f),
                AoContrast = EditorPrefs.GetFloat("NamerProcessor.AoContrast", 1f),
                AoBlurRadius = EditorPrefs.GetFloat("NamerProcessor.AoBlurRadius", 0f),
                HadDestination = EditorPrefs.HasKey("NamerProcessor.Destination"),
                HadStyleProfileGuid = EditorPrefs.HasKey("NamerProcessor.StyleProfileGuid"),
                HadDecompositionEnabled = EditorPrefs.HasKey("NamerProcessor.DecompositionEnabled"),
                HadAoStageEnabled = EditorPrefs.HasKey("NamerProcessor.AoStageEnabled"),
                HadRoughnessStageEnabled = EditorPrefs.HasKey("NamerProcessor.RoughnessStageEnabled"),
                HadMetallicContributionEnabled = EditorPrefs.HasKey("NamerProcessor.MetallicContributionEnabled"),
                HadEmissiveContributionEnabled = EditorPrefs.HasKey("NamerProcessor.EmissiveContributionEnabled"),
                HadOverwriteGenerated = EditorPrefs.HasKey("NamerProcessor.OverwriteGenerated"),
                HadAoStrength = EditorPrefs.HasKey("NamerProcessor.AoStrength"),
                HadAoContrast = EditorPrefs.HasKey("NamerProcessor.AoContrast"),
                HadAoBlurRadius = EditorPrefs.HasKey("NamerProcessor.AoBlurRadius"),
            };
        }

        private static void RestoreE2ESettings(E2ESettingsSnapshot snapshot)
        {
            if (snapshot.HadDestination) { EditorPrefs.SetString("NamerProcessor.Destination", snapshot.Destination); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Destination"); }

            if (snapshot.HadStyleProfileGuid) { EditorPrefs.SetString("NamerProcessor.StyleProfileGuid", snapshot.StyleProfileGuid); }
            else { EditorPrefs.DeleteKey("NamerProcessor.StyleProfileGuid"); }

            if (snapshot.HadDecompositionEnabled) { EditorPrefs.SetBool("NamerProcessor.DecompositionEnabled", snapshot.DecompositionEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.DecompositionEnabled"); }

            if (snapshot.HadAoStageEnabled) { EditorPrefs.SetBool("NamerProcessor.AoStageEnabled", snapshot.AoStageEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.AoStageEnabled"); }

            if (snapshot.HadRoughnessStageEnabled) { EditorPrefs.SetBool("NamerProcessor.RoughnessStageEnabled", snapshot.RoughnessStageEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.RoughnessStageEnabled"); }

            if (snapshot.HadMetallicContributionEnabled) { EditorPrefs.SetBool("NamerProcessor.MetallicContributionEnabled", snapshot.MetallicContributionEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.MetallicContributionEnabled"); }

            if (snapshot.HadEmissiveContributionEnabled) { EditorPrefs.SetBool("NamerProcessor.EmissiveContributionEnabled", snapshot.EmissiveContributionEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.EmissiveContributionEnabled"); }

            if (snapshot.HadOverwriteGenerated) { EditorPrefs.SetBool("NamerProcessor.OverwriteGenerated", snapshot.OverwriteGenerated); }
            else { EditorPrefs.DeleteKey("NamerProcessor.OverwriteGenerated"); }

            if (snapshot.HadAoStrength) { EditorPrefs.SetFloat("NamerProcessor.AoStrength", snapshot.AoStrength); }
            else { EditorPrefs.DeleteKey("NamerProcessor.AoStrength"); }

            if (snapshot.HadAoContrast) { EditorPrefs.SetFloat("NamerProcessor.AoContrast", snapshot.AoContrast); }
            else { EditorPrefs.DeleteKey("NamerProcessor.AoContrast"); }

            if (snapshot.HadAoBlurRadius) { EditorPrefs.SetFloat("NamerProcessor.AoBlurRadius", snapshot.AoBlurRadius); }
            else { EditorPrefs.DeleteKey("NamerProcessor.AoBlurRadius"); }
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

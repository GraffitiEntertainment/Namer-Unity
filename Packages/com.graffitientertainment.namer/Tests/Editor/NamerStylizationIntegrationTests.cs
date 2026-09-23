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
    /// Phase 05 plan 02 integration tests — the D-01 stylize stage proven through
    /// <see cref="NamerComputePipeline.Process"/> (STYL-03/STYL-04), not just kernels:
    ///   byte-identical null path          -> <see cref="Stylization_NullPath_ByteIdenticalAcrossRuns"/>
    ///   identity context = zero leases    -> <see cref="Stylization_InactiveContext_ByteIdenticalZeroExtraLeases"/>
    ///   styled base flows into fit/result -> <see cref="Stylization_ActiveReplace_StylingFlowsIntoProjectionAndResult"/>
    ///   residual dividends the STYLED base-> <see cref="Stylization_ActiveReplace_ResidualDividendIsStyledSource"/>
    ///   mid-stage throw leaks nothing     -> <see cref="Stylization_ActiveContext_ThrowInProjection_RunPropagatesAndPoolReturnsToBaseline"/>
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

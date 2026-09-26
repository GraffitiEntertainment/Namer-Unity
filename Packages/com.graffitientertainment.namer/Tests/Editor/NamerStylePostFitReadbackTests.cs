using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Regression tests for the Fit Look source-readback seam
    /// (<see cref="NamerEditorWindow.RunStylePostFit"/>): the stylized base is an
    /// R16G16B16A16_SFloat compute-pool target, so the fit's readback requests
    /// RGBAFloat and views the buffer with GetData&lt;Color&gt; (16 bytes/element) —
    /// the decomp/roughness float-RT convention. The previous RGBA32 request paired
    /// with GetData&lt;Color&gt; viewed the 4-byte-per-pixel buffer as w*h/4 sixteen-byte
    /// "pixels" whose channels were texel byte-quads reinterpreted as ~-1e38 finite
    /// floats; TonemapLdr crushed them to 0 and every fitted knob railed at its cap
    /// (EV +10, contrast/saturation +100, staircase curves — the white After pane).
    /// Pinned here: one Color per texel, means matching an independent
    /// RGBA32+Color32 byte oracle of the SAME target, and a non-railed fit when the
    /// healthy source is fitted onto itself (identity reference).
    /// </summary>
    public class NamerStylePostFitReadbackTests
    {
        private const int WorkingSize = 64;
        private const float MeanTolerance = 0.02f;
        private const float WarmRed = 0.55f;
        private const float WarmGreen = 0.36f;
        private const float WarmBlue = 0.18f;
        private const float GrainFloor = 0.25f;
        private const float GrainSpan = 0.75f;
        private const float HashScale = 41f;
        private const float RailCapEv = 10f;
        private const float RailCapPercent = 100f;
        private const int ReferenceTexelCount = 256;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [Test]
        public void FitLookReadbackSeam_OneColorPerTexel_AndMatchesIndependentByteOracle()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping fit-look readback test.");
                return;
            }

            Texture2D baseMap = CreateHealthyBaseMap();
            try
            {
                NamerMaterialInspection inspection = BuildInspection(baseMap);
                using (var pipeline = new NamerComputePipeline())
                {
                    NamerComputeResult run = pipeline.Process(inspection);
                    try
                    {
                        RenderTexture baseRt = (RenderTexture)NamerEditorWindow.ResolveStylizePreviewBase(run);

                        // The fit seam (RunStylePostFit's readback calls): RGBAFloat +
                        // GetData<Color> — element size and buffer layout agree.
                        AsyncGPUReadbackRequest fitRequest =
                            NamerComputePipeline.RequestReadback(baseRt, 0, TextureFormat.RGBAFloat);
                        fitRequest.forcePlayerLoopUpdate = true;
                        fitRequest.WaitForCompletion();
                        Assert.IsFalse(fitRequest.hasError, "fit-seam RGBAFloat readback must not error");
                        Color[] fitPixels = ToArray(fitRequest.GetData<Color>());

                        // Independent oracle of the SAME target: RGBA32 + Color32
                        // (the only correct pairing for a 4-byte-per-pixel buffer).
                        AsyncGPUReadbackRequest byteRequest =
                            NamerComputePipeline.RequestReadback(baseRt, 0, TextureFormat.RGBA32);
                        byteRequest.forcePlayerLoopUpdate = true;
                        byteRequest.WaitForCompletion();
                        Assert.IsFalse(byteRequest.hasError, "oracle RGBA32 readback must not error");
                        Color32[] bytePixels = ToArray(byteRequest.GetData<Color32>());

                        NamerStylePostStats fitStats = NamerStylePostFit.AccumulateSource(fitPixels);
                        NamerStylePostStats oracleStats = AccumulateBytes(bytePixels);

                        Assert.AreEqual(WorkingSize * WorkingSize, fitPixels.Length,
                            "the fit seam must yield exactly one Color per texel");
                        Assert.AreEqual(WorkingSize * WorkingSize, bytePixels.Length,
                            "the byte oracle must yield exactly one Color32 per texel");
                        Assert.Greater(fitStats.MeanLuma, 0.05f,
                            "a healthy warm base must accumulate a healthy mean luma (was 0 in the RGBA32+Color bug)");
                        Assert.AreEqual(oracleStats.MeanLuma, fitStats.MeanLuma, MeanTolerance,
                            "fit-seam mean luma must match the independent byte oracle");
                        Assert.AreEqual(oracleStats.MeanChroma, fitStats.MeanChroma, MeanTolerance,
                            "fit-seam mean chroma must match the independent byte oracle");

                        AssertFitOntoSelfDoesNotRail(fitPixels);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(run);
                    }
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(baseMap);
            }
        }

        /// <summary>
        /// The near-black/NaN source guard in <see cref="NamerStylePostFit.Fit"/>:
        /// a degenerate source distribution (failed readback / black render) must
        /// refuse loudly via the "Fit failed" path instead of railing every knob
        /// at its cap — the exact silent failure the buffer-layout bug produced.
        /// </summary>
        [Test]
        public void Fit_DegenerateSource_RefusesLoudlyInsteadOfRailing()
        {
            var referenceStats = new NamerStylePostStats();
            var referencePixels = new Color[ReferenceTexelCount];
            for (int i = 0; i < ReferenceTexelCount; i++)
            {
                referencePixels[i] = new Color(WarmRed, WarmGreen, WarmBlue, 1f);
            }

            NamerStylePostFit.AccumulateReference(referenceStats, referencePixels, 1f);

            var blackPixels = new Color[ReferenceTexelCount];
            NamerStylePostStats blackStats = NamerStylePostFit.AccumulateSource(blackPixels);
            Assert.Throws<InvalidOperationException>(
                () => NamerStylePostFit.Fit(blackStats, referenceStats, "readback-tests"));

            var nanPixels = new Color[ReferenceTexelCount];
            for (int i = 0; i < ReferenceTexelCount; i++)
            {
                nanPixels[i] = new Color(float.NaN, float.NaN, float.NaN, 1f);
            }

            NamerStylePostStats nanStats = NamerStylePostFit.AccumulateSource(nanPixels);
            Assert.Throws<InvalidOperationException>(
                () => NamerStylePostFit.Fit(nanStats, referenceStats, "readback-tests"));
        }

        /// <summary>
        /// Fits the healthy readback pixels onto an sRGB-encoded copy of themselves
        /// (the identity reference) and asserts no knob rails at its cap — the exact
        /// failure signature of the buffer-layout bug.
        /// </summary>
        private static void AssertFitOntoSelfDoesNotRail(Color[] fitPixels)
        {
            NamerStylePostStats sourceStats = NamerStylePostFit.AccumulateSource(fitPixels);
            NamerStylePostStats referenceStats = new NamerStylePostStats();
            var referencePixels = new Color[ReferenceTexelCount];
            for (int i = 0; i < ReferenceTexelCount; i++)
            {
                Color source = fitPixels[(i * (fitPixels.Length - 1)) / (ReferenceTexelCount - 1)];
                referencePixels[i] = new Color(
                    Mathf.LinearToGammaSpace(source.r),
                    Mathf.LinearToGammaSpace(source.g),
                    Mathf.LinearToGammaSpace(source.b),
                    1f);
            }

            NamerStylePostFit.AccumulateReference(referenceStats, referencePixels, 1f);
            NamerStylePostFitParams fitted = NamerStylePostFit.Fit(sourceStats, referenceStats, "readback-tests");

            Assert.Less(Mathf.Abs(fitted.PostExposureEv), RailCapEv,
                "identity-reference EV must not rail at the ±10 cap");
            Assert.Less(Mathf.Abs(fitted.Contrast), RailCapPercent,
                "identity-reference contrast must not rail at ±100");
            Assert.Less(Mathf.Abs(fitted.Saturation), RailCapPercent,
                "identity-reference saturation must not rail at ±100");
        }

        /// <summary>Accumulates a byte oracle through the same source accumulator.</summary>
        private static NamerStylePostStats AccumulateBytes(Color32[] bytePixels)
        {
            var pixels = new Color[bytePixels.Length];
            for (int i = 0; i < bytePixels.Length; i++)
            {
                pixels[i] = new Color(bytePixels[i].r / 255f, bytePixels[i].g / 255f, bytePixels[i].b / 255f, 1f);
            }

            return NamerStylePostFit.AccumulateSource(pixels);
        }

        /// <summary>Warm healthy linear base with hash-noise luma grain (never black).</summary>
        private static Texture2D CreateHealthyBaseMap()
        {
            var texture = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBAFloat, false, true)
            {
                name = "FitLookReadbackHealthyBase",
                hideFlags = HideFlags.HideAndDontSave,
            };

            for (int y = 0; y < WorkingSize; y++)
            {
                for (int x = 0; x < WorkingSize; x++)
                {
                    float grain = GrainFloor + GrainSpan * Hash01(x, y);
                    texture.SetPixel(x, y, new Color(WarmRed * grain, WarmGreen * grain, WarmBlue * grain, 1f));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        private static float Hash01(int x, int y)
        {
            float value = Mathf.Sin((x * 127.1f + y * 311.7f) * HashScale);
            return value - Mathf.Floor(value);
        }

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                OcclusionMap = null,
                MetallicGlossMap = null,
                Metallic = 0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = 0f,
                DipSource = NamerDipSource.SobelEdge,
                Emissive = 0f,
                AoUnmultiplyStrength = 1f,
                SmoothnessTextureChannel = 0,
                BakeSourceMesh = null,
            };
        }

        private static T[] ToArray<T>(NativeArray<T> data) where T : struct
        {
            var result = new T[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = data[i];
            }

            return result;
        }
    }
}

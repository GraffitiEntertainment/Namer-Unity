using System.Collections;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 4 plan 02 GPU golden tests (TEST-02). Proves the residual is the multiplicative
    /// quotient <c>base / max(vcInterp, VcFloor)</c> derived from the QUANTIZED Color32
    /// vertex colors (Pitfall 2): (1) the GPU residual reconstructs the base within 2/255 per
    /// channel, (2) uncovered texels carry the identity residual, (3) the D-13
    /// residual-required gate distinguishes a constant fit from a gradient base, and (4) the
    /// adaptive search picks a ladder resolution within threshold with the manual ladder
    /// override. All tests are capability-gated (D-15): skipped-with-report when
    /// compute/async-readback is unavailable, never a silent pass.
    /// </summary>
    public class ResidualPipelineTests
    {
        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator Residual_ReconstructsBase_WithinByteTolerance()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float baseValue = 0.25f;
            const byte vcByte = 128;

            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, vcByte);
            RenderTexture baseRt = CreateBase(w, h, new Color(baseValue, baseValue, baseValue, 1f));

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                    try
                    {
                        Assert.IsNotNull(output.Residual, "a non-matching constant fit must require a residual");
                        Assert.IsTrue(output.Stats.ResidualRequired);
                        Color32[] residual = ReadBackColor32(output.Residual);

                        float vcInterp = vcByte / 255f;
                        // CPU oracle: residual = base / max(vcInterp, 1e-3f) — the same quotient
                        // the GPU computes in CSResidual (Pitfall 2 end-to-end).
                        float oracle = baseValue / Mathf.Max(vcInterp, 1e-3f);

                        for (int i = 0; i < residual.Length; i++)
                        {
                            float gpuR = residual[i].r / 255f;
                            float gpuG = residual[i].g / 255f;
                            float gpuB = residual[i].b / 255f;

                            // Reconstruction invariant: residual.rgb * vcInterp ≈ base.rgb.
                            Assert.LessOrEqual(Mathf.Abs(gpuR * vcInterp - baseValue), 2 / 255f, "reconstruction R texel " + i);
                            Assert.LessOrEqual(Mathf.Abs(gpuG * vcInterp - baseValue), 2 / 255f, "reconstruction G texel " + i);
                            Assert.LessOrEqual(Mathf.Abs(gpuB * vcInterp - baseValue), 2 / 255f, "reconstruction B texel " + i);

                            // CPU oracle vs GPU residual.
                            Assert.LessOrEqual(Mathf.Abs(gpuR - oracle), 2 / 255f, "CPU-oracle residual R texel " + i);
                            Assert.LessOrEqual(Mathf.Abs(gpuG - oracle), 2 / 255f, "CPU-oracle residual G texel " + i);
                            Assert.LessOrEqual(Mathf.Abs(gpuB - oracle), 2 / 255f, "CPU-oracle residual B texel " + i);
                        }
                    }
                    finally
                    {
                        output.Dispose();
                    }
                }
                finally
                {
                    Release(baseRt);
                }
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Residual_UncoveredTexels_AreIdentity()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float baseValue = 0.25f;

            // UV spans [0, 0.5]^2 so the top-right corner of the texture is uncovered.
            NamerSplitResult split = CreateSplitQuad(0f, 0.5f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateBase(w, h, new Color(baseValue, baseValue, baseValue, 1f));

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired);
                        Assert.IsNotNull(output.Residual);
                        Color32[] residual = ReadBackColor32(output.Residual);

                        int uncovered = (h - 1) * w + (w - 1);
                        Assert.AreEqual(255, (int)residual[uncovered].r, "uncovered residual R must be 1.0 (identity)");
                        Assert.AreEqual(255, (int)residual[uncovered].g, "uncovered residual G must be 1.0 (identity)");
                        Assert.AreEqual(255, (int)residual[uncovered].b, "uncovered residual B must be 1.0 (identity)");
                        Assert.AreEqual(255, (int)residual[uncovered].a, "uncovered residual A must preserve base alpha");
                    }
                    finally
                    {
                        output.Dispose();
                    }
                }
                finally
                {
                    Release(baseRt);
                }
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator GenerateResidual_Gate_DistinguishesConstantVsGradient()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // Constant fit: base matches the quantized vertex color -> residual not required.
            {
                float constant = 128f / 255f;
                NamerSplitResult split = CreateSplitQuad(0f, 1f);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateBase(w, h, new Color(constant, constant, constant, 1f));

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                        try
                        {
                            Assert.IsFalse(output.Stats.ResidualRequired, "a constant fit within threshold must not require a residual");
                            Assert.AreEqual(0, output.Stats.ChosenResolution);
                            Assert.IsNull(output.Residual);
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    finally
                    {
                        Release(baseRt);
                    }
                }
            }

            // Gradient base: fit-only error exceeds threshold -> residual required.
            {
                NamerSplitResult split = CreateSplitQuad(0f, 1f);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateGradientBase(w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                        try
                        {
                            Assert.IsTrue(output.Stats.ResidualRequired, "a gradient base must require a residual");
                            Assert.IsNotNull(output.Residual);
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    finally
                    {
                        Release(baseRt);
                    }
                }
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator GenerateResidual_AdaptiveAndManualOverride()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            // Adaptive (D-16): a gradient base with Auto resolution picks a ladder value
            // <= source whose reconstruction stays within threshold.
            {
                const int w = 256;
                const int h = 256;
                const float threshold = 0.05f;
                NamerSplitResult split = CreateSplitQuad(0f, 1f);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateGradientBase(w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                        try
                        {
                            Assert.IsTrue(output.Stats.ResidualRequired);
                            Assert.IsNotNull(output.Residual);
                            Assert.LessOrEqual(output.Stats.ChosenResolution, w);
                            Assert.LessOrEqual(output.Stats.MaxError, threshold, "chosen resolution must stay within the error threshold");
                            Assert.AreEqual(output.Stats.ChosenResolution, output.Residual.width, "residual must be produced at the chosen resolution");

                            bool inLadder = false;
                            foreach (int r in NamerDecompPipeline.ResolutionLadder)
                            {
                                if (r == output.Stats.ChosenResolution)
                                {
                                    inLadder = true;
                                    break;
                                }
                            }

                            Assert.IsTrue(inLadder, "adaptive choice must be a ResolutionLadder value");
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    finally
                    {
                        Release(baseRt);
                    }
                }
            }

            // Manual override (D-17): popup index 3 resolves to ResolutionLadder[2] = 512 px.
            {
                const int w = 512;
                const int h = 512;
                NamerSplitResult split = CreateSplitQuad(0f, 1f);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateGradientBase(w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.05f, 3);
                        try
                        {
                            Assert.AreEqual(512, output.Stats.ChosenResolution, "manual index 3 resolves to 512px (not the raw index)");
                            Assert.IsNotNull(output.Residual);
                            Assert.AreEqual(512, output.Residual.width);
                        }
                        finally
                        {
                            output.Dispose();
                        }
                    }
                    finally
                    {
                        Release(baseRt);
                    }
                }
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private static NamerSplitResult CreateSplitQuad(float uvMin, float uvMax)
        {
            return new NamerSplitResult
            {
                Positions = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(1f, 0f, 1f),
                    new Vector3(0f, 0f, 1f),
                },
                Normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                Tangents = new Vector4[4],
                Uvs = new[]
                {
                    new Vector2(uvMin, uvMin),
                    new Vector2(uvMax, uvMin),
                    new Vector2(uvMax, uvMax),
                    new Vector2(uvMin, uvMax),
                },
                SubMeshTriangles = new[] { new[] { 0, 2, 1, 0, 3, 2 } },
            };
        }

        private static Color32[] ConstantColors(int count, byte value)
        {
            Color32[] colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                colors[i] = new Color32(value, value, value, 255);
            }

            return colors;
        }

        private static RenderTexture CreateBase(int w, int h, Color color)
        {
            return UploadBase(w, h, (x, y) => color);
        }

        private static RenderTexture CreateGradientBase(int w, int h)
        {
            return UploadBase(w, h, (x, y) =>
            {
                float v = x / (float)(w - 1);
                return new Color(v, v, v, 1f);
            });
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
            Object.DestroyImmediate(upload);
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

        private static Color32[] ReadBackColor32(RenderTexture rt)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color32> data = req.GetData<Color32>();
            try
            {
                Color32[] result = new Color32[data.Length];
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
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }
    }
}

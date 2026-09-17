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
    /// Phase 04.2 plan 01 projection identity tests. Proves the by-construction residual
    /// collapse: (1) writing the rasterized Gouraud surface back as the base makes the residual
    /// quotient white by construction, (2) the symmetric VcFloor keeps dark / below-floor texels
    /// white too, (3) the projection preserves source alpha and passes uncovered texels through
    /// unchanged, and (4) <see cref="NamerResidualMode"/> forces keep/drop in both directions.
    /// All tests are capability-gated (D-15): skipped-with-report when compute/async-readback is
    /// unavailable, never a silent pass.
    /// </summary>
    public class NamerProjectionTests
    {
        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator Projection_QuotientIsWhite_ByConstruction()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping projection identity test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;

            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = FitColors(split, CreateRampTexels(w, h), w, h);
            RenderTexture sourceBase = CreateGradientBase(w, h);
            RenderTexture projected = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);

            try
            {
                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    // First pass: rasterize + write the Gouraud surface back into projected
                    // (dividend = source base, which is the removed-detail error we ignore here).
                    NamerDecompOutput first = pipeline.GenerateResidual(
                        split, colors, sourceBase, w, h, threshold, 0, projected, NamerResidualMode.AlwaysKeep);
                    first.Dispose();

                    // Second pass: dividend == the projection == divisor -> quotient white.
                    NamerDecompOutput second = pipeline.GenerateResidual(
                        split, colors, projected, w, h, threshold, 0, null, NamerResidualMode.AlwaysKeep);
                    try
                    {
                        Assert.IsNotNull(second.Residual, "AlwaysKeep must produce a residual");
                        Assert.IsTrue(second.Stats.ResidualRequired);
                        Assert.LessOrEqual(second.Stats.MaxError, 0.01f,
                            "the projection residual must reconstruct the projected base near-exactly");

                        Color[] residual = ReadBackFloat(second.Residual);
                        foreach (Color c in residual)
                        {
                            Assert.LessOrEqual(Mathf.Abs(c.r - 1f), 1f / 255f, "quotient R must be white by construction");
                            Assert.LessOrEqual(Mathf.Abs(c.g - 1f), 1f / 255f, "quotient G must be white by construction");
                            Assert.LessOrEqual(Mathf.Abs(c.b - 1f), 1f / 255f, "quotient B must be white by construction");
                        }
                    }
                    finally
                    {
                        second.Dispose();
                    }
                }
            }
            finally
            {
                Release(sourceBase, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Projection_DarkVc_SymmetricFloorKeepsQuotientWhite()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping projection dark-VC test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;

            // Dark fixture: B channel quantizes to 0 (below VcFloor = 1e-3), mirroring
            // RESEARCH Pitfall 1 (Neo's dark B channel). The symmetric floor must still
            // produce a white quotient — the legacy asymmetric floor would read 0 / 1e-3 = 0.
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = FitColors(split, CreateConstantTexels(w, h, new Color32(26, 26, 0, 255)), w, h);
            RenderTexture sourceBase = CreateBase(w, h, new Color(26f / 255f, 26f / 255f, 0.0005f, 1f));
            RenderTexture projected = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);

            try
            {
                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    NamerDecompOutput first = pipeline.GenerateResidual(
                        split, colors, sourceBase, w, h, threshold, 0, projected, NamerResidualMode.AlwaysKeep);
                    first.Dispose();

                    NamerDecompOutput second = pipeline.GenerateResidual(
                        split, colors, projected, w, h, threshold, 0, null, NamerResidualMode.AlwaysKeep);
                    try
                    {
                        Assert.IsNotNull(second.Residual, "AlwaysKeep must produce a residual");
                        Color[] residual = ReadBackFloat(second.Residual);
                        foreach (Color c in residual)
                        {
                            Assert.LessOrEqual(Mathf.Abs(c.r - 1f), 1f / 255f, "quotient R must be white on dark VCs");
                            Assert.LessOrEqual(Mathf.Abs(c.g - 1f), 1f / 255f, "quotient G must be white on dark VCs");
                            Assert.LessOrEqual(Mathf.Abs(c.b - 1f), 1f / 255f,
                                "quotient B must be white even though the VC channel quantized to 0 (symmetric floor)");
                        }
                    }
                    finally
                    {
                        second.Dispose();
                    }
                }
            }
            finally
            {
                Release(sourceBase, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Projection_PreservesSourceAlpha_UncoveredPassthrough()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping projection alpha test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // Partial coverage ([0,0.5]^2 quad, matching Residual_UncoveredTexels_AreIdentity)
            // so the top-right region is uncovered and must pass the source through unchanged.
            NamerSplitResult split = CreateSplitQuad(0f, 0.5f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture sourceBase = CreateVaryingAlphaBase(w, h);
            RenderTexture projected = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);

            try
            {
                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    // Default Gate mode: the write-back happens before the gate regardless.
                    NamerDecompOutput output = pipeline.GenerateResidual(
                        split, colors, sourceBase, w, h, 0.02f, 0, projected);
                    output.Dispose();
                }

                Color[] src = ReadBackFloat(sourceBase);
                Color[] proj = ReadBackFloat(projected);

                for (int i = 0; i < proj.Length; i++)
                {
                    Assert.LessOrEqual(Mathf.Abs(proj[i].a - src[i].a), 1f / 255f,
                        "projected alpha must equal source alpha at texel " + i);
                }

                // Uncovered region (x >= 32, y >= 32 for the [0,0.5]^2 quad): RGB must pass
                // through unchanged. Check the top-right corner and its neighbors.
                for (int y = 32; y < h; y++)
                {
                    for (int x = 32; x < w; x++)
                    {
                        int i = y * w + x;
                        Assert.LessOrEqual(Mathf.Abs(proj[i].r - src[i].r), 1f / 255f, "uncovered R passthrough at (" + x + "," + y + ")");
                        Assert.LessOrEqual(Mathf.Abs(proj[i].g - src[i].g), 1f / 255f, "uncovered G passthrough at (" + x + "," + y + ")");
                        Assert.LessOrEqual(Mathf.Abs(proj[i].b - src[i].b), 1f / 255f, "uncovered B passthrough at (" + x + "," + y + ")");
                    }
                }
            }
            finally
            {
                Release(sourceBase, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator ResidualMode_NeverKeep_DropsDespiteError()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping NeverKeep mode test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // A gradient base would be KEPT by the D-13 gate (fit-only error exceeds
            // threshold); NeverKeep must drop it unconditionally.
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateGradientBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(
                        split, colors, baseRt, w, h, 0.02f, 0, null, NamerResidualMode.NeverKeep);
                    try
                    {
                        Assert.IsNull(output.Residual, "NeverKeep must drop the residual despite the fit error");
                        Assert.IsFalse(output.Stats.ResidualRequired);
                        Assert.AreEqual(0, output.Stats.ChosenResolution);
                        Assert.Greater(output.Stats.Coverage, 0f, "NeverKeep must still populate coverage stats");
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
        public IEnumerator ResidualMode_AlwaysKeep_KeepsDespitePerfectFit()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping AlwaysKeep mode test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // A constant base matching the quantized vertex color is a perfect fit — the D-13
            // gate would DROP it. AlwaysKeep must keep the residual anyway.
            float constant = 128f / 255f;
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateBase(w, h, new Color(constant, constant, constant, 1f));

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(
                        split, colors, baseRt, w, h, 0.02f, 0, null, NamerResidualMode.AlwaysKeep);
                    try
                    {
                        Assert.IsNotNull(output.Residual, "AlwaysKeep must keep the residual despite the perfect fit");
                        Assert.IsTrue(output.Stats.ResidualRequired);
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

        private static Color32[] FitColors(NamerSplitResult split, NativeArray<Color32> baseTexels, int w, int h)
        {
            try
            {
                VertexColorFitResult fit = VertexColorFitter.Fit(split, baseTexels, w, h);
                try
                {
                    return fit.ToColor32Array();
                }
                finally
                {
                    fit.Dispose();
                }
            }
            finally
            {
                baseTexels.Dispose();
            }
        }

        private static NativeArray<Color32> CreateConstantTexels(int w, int h, Color32 color)
        {
            var texels = new NativeArray<Color32>(w * h, Allocator.TempJob);
            for (int i = 0; i < texels.Length; i++)
            {
                texels[i] = color;
            }

            return texels;
        }

        private static NativeArray<Color32> CreateRampTexels(int w, int h)
        {
            // Grayscale x-ramp: texel 0 is black, texel (w-1) is white, r/g/b equal the x
            // fraction so the fit recovers a piecewise-linear Gouraud surface.
            var texels = new NativeArray<Color32>(w * h, Allocator.TempJob);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = (byte)Mathf.RoundToInt(x / (float)(w - 1) * 255f);
                    texels[y * w + x] = new Color32(v, v, v, 255);
                }
            }

            return texels;
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

        private static RenderTexture CreateVaryingAlphaBase(int w, int h)
        {
            // Varying RGB (x ramp) and varying alpha (y ramp) so both the covered-alpha
            // preservation and the uncovered-RGB passthrough are meaningfully exercised.
            return UploadBase(w, h, (x, y) =>
            {
                float r = x / (float)(w - 1);
                float a = y / (float)(h - 1);
                return new Color(r, 0.25f, 0.1f, a);
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

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
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
                    // Graphics.Blit leaves RenderTexture.active pointing at its destination;
                    // unbind before release (mirrors ComputeTexturePool's guard).
                    if (RenderTexture.active == rt)
                    {
                        RenderTexture.active = null;
                    }

                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }
    }
}

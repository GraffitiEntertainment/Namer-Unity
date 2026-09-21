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
    /// Phase 04.2 plan 02 removed-luminance roughness-transfer tests. Proves the signed
    /// Rec.601 removed-luma transfer end-to-end through
    /// <see cref="NamerRoughnessPipeline.ExtractTransferRoughness"/>: a flat projection yields
    /// the authored scalar exactly, bright removed detail dips toward gloss, dark removed detail
    /// raises toward matte, strength 0 is the identity, p90 headroom saturates both ends, and the
    /// Rec.601 weights (0.299/0.587/0.114) are pinned by a hand-computed oracle. Capability-gated
    /// (D-15): every GPU test skips with an explicit report when compute/async-readback is
    /// unavailable, never a silent pass.
    /// </summary>
    public class NamerRoughnessTransferTests
    {
        private const int WorkingSize = 32;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        private NamerRoughnessPipeline _pipeline;

        [SetUp]
        public void SetUp()
        {
            _pipeline = new NamerRoughnessPipeline();
        }

        [TearDown]
        public void TearDown()
        {
            _pipeline.Dispose();
        }

        [UnityTest]
        public IEnumerator Transfer_FlatProjection_YieldsScalarExactly()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping flat-projection identity test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;

            RenderTexture source = CreateFlat(w, h, 0.5f);
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 1.0f);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int i = 0; i < texels.Length; i++)
                {
                    float r = texels[i].r / 255f;
                    Assert.LessOrEqual(Mathf.Abs(r - scalar), 1f / 255f,
                        "flat projection (removed-luma == 0) must yield the authored scalar at texel " + i);
                }
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_BrightRemovedLuma_DipsTowardGloss()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping bright-polarity test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const int px = 12, py = 12, pw = 8, ph = 8; // centered 8x8 bright patch

            RenderTexture source = CreateBase(w, h, (x, y) =>
                InRect(x, y, px, py, pw, ph) ? new Color(0.9f, 0.9f, 0.9f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f));
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 0.5f);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float r = texels[y * w + x].r / 255f;
                        if (InRect(x, y, px, py, pw, ph))
                        {
                            Assert.Less(r, scalar - 0.05f,
                                $"bright removed-luma texel ({x},{y}) must dip toward gloss (removed luma > 0)");
                        }
                        else
                        {
                            Assert.LessOrEqual(Mathf.Abs(r - scalar), 1f / 255f,
                                $"non-patch texel ({x},{y}) must stay at the authored scalar");
                        }
                    }
                }
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_DarkRemovedLuma_RaisesTowardMatte()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping dark-polarity test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const int px = 12, py = 12, pw = 8, ph = 8;

            // Removed = source - projected, so a bright patch in the PROJECTED texture makes the
            // removed signal negative (dark response) in that region.
            RenderTexture source = CreateFlat(w, h, 0.5f);
            RenderTexture projected = CreateBase(w, h, (x, y) =>
                InRect(x, y, px, py, pw, ph) ? new Color(0.9f, 0.9f, 0.9f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f));
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 0.5f);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        float r = texels[y * w + x].r / 255f;
                        if (InRect(x, y, px, py, pw, ph))
                        {
                            Assert.Greater(r, scalar + 0.05f,
                                $"dark removed-luma texel ({x},{y}) must raise toward matte (removed luma < 0)");
                            Assert.LessOrEqual(r, 1.0f,
                                $"dark removed-luma texel ({x},{y}) must not exceed the 6-bit matte clip");
                        }
                        else
                        {
                            Assert.LessOrEqual(Mathf.Abs(r - scalar), 1f / 255f,
                                $"non-patch texel ({x},{y}) must stay at the authored scalar");
                        }
                    }
                }
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_StrengthZero_EqualsScalarExactly()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping strength-0 identity test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const int px = 12, py = 12, pw = 8, ph = 8;

            RenderTexture source = CreateBase(w, h, (x, y) =>
                InRect(x, y, px, py, pw, ph) ? new Color(0.9f, 0.9f, 0.9f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f));
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 0f);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int i = 0; i < texels.Length; i++)
                {
                    float r = texels[i].r / 255f;
                    Assert.LessOrEqual(Mathf.Abs(r - scalar), 1f / 255f,
                        "strength 0 must yield the authored scalar exactly (pure-taste slider identity) at texel " + i);
                }
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_P90Headroom_SaturatesBothEnds()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping p90-headroom test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const int bx = 4, by = 4, bw = 8, bh = 8;   // bright patch (+0.4)
            const int dx = 20, dy = 20, dw = 8, dh = 8; // dark patch (-0.4)

            RenderTexture source = CreateBase(w, h, (x, y) =>
            {
                if (InRect(x, y, bx, by, bw, bh)) return new Color(0.9f, 0.9f, 0.9f, 1f);
                if (InRect(x, y, dx, dy, dw, dh)) return new Color(0.1f, 0.1f, 0.1f, 1f);
                return new Color(0.5f, 0.5f, 0.5f, 1f);
            });
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 1.0f);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        if (InRect(x, y, bx, by, bw, bh))
                        {
                            Assert.AreEqual(0, texels[i].r,
                                $"bright patch texel ({x},{y}) must reach the gloss clip exactly (removed luma far above p90)");
                        }
                        else if (InRect(x, y, dx, dy, dw, dh))
                        {
                            Assert.AreEqual(255, texels[i].r,
                                $"dark patch texel ({x},{y}) must reach the matte clip exactly (|removed luma| far above p90)");
                        }
                    }
                }
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_SignedSignal_FollowsRec601Weights()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping Rec.601 oracle test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const float strength = 0.5f;

            // Removed-luma oracle: two patches of equal delta magnitude in different channels.
            //   G patch (dominant 16x16): delta (0, 0.3, 0) -> luma = 0.587 * 0.3 = 0.1761 (sets p90)
            //   R patch (8x8):             delta (0.3, 0, 0) -> luma = 0.299 * 0.3 = 0.0897
            // The R/G dip ratio pins 0.299/0.587 (Rec.601); Rec.709 (0.2126/0.7152) would give a
            // very different ratio, so a wrong weighting fails the reconstruction below.
            const float rec601R = 0.299f;
            const float rec601G = 0.587f;
            const float delta = 0.3f;
            float lumaR = rec601R * delta; // 0.0897
            float lumaG = rec601G * delta; // 0.1761
            float p90 = lumaG;             // the dominant G patch owns the |luma| p90

            RenderTexture source = CreateBase(w, h, (x, y) =>
            {
                if (InRect(x, y, 0, 0, 16, 16)) return new Color(0.5f, 0.5f + delta, 0.5f, 1f);   // G patch
                if (InRect(x, y, 24, 24, 8, 8)) return new Color(0.5f + delta, 0.5f, 0.5f, 1f);   // R patch
                return new Color(0.5f, 0.5f, 0.5f, 1f);
            });
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, strength);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                // G patch center (8,8): mag = lumaG/p90 == 1 -> full gloss clip (sanity anchor).
                float measuredG = texels[8 * w + 8].r / 255f;
                Assert.LessOrEqual(measuredG, 0.02f,
                    "G-delta patch must reach the gloss clip (mag == 1 at the p90 normalization scale)");

                // R patch center (28,28): invert the dip to recover the removed luma and pin the
                // Rec.601 R weight (delta (0.3,0,0) -> 0.0897, not 0.3 or Rec.709's 0.0638).
                float measuredR = texels[28 * w + 28].r / 255f;
                float measuredLumaR = (scalar - measuredR) * p90 / strength;
                Assert.AreEqual(lumaR, measuredLumaR, 0.005f,
                    "removed-luma must follow Rec.601: delta (0.3,0,0) -> 0.0897, got " + measuredLumaR);
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_SeamOutlier_SoftClipsAtUnitDepth()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping seam-outlier soft-clip test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const float strength = 0.25f;
            const int px = 16, py = 16, pw = 2, ph = 2; // 2x2 seam outlier far above p90

            RenderTexture source = CreateBase(w, h, (x, y) =>
                InRect(x, y, px, py, pw, ph) ? new Color(0.95f, 0.95f, 0.95f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f));
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness = null;

            try
            {
                roughness = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, strength);
                Color32[] texels = ReadBackColor32(roughness, w * h);

                for (int y = py; y < py + ph; y++)
                {
                    for (int x = px; x < px + pw; x++)
                    {
                        float r = texels[y * w + x].r / 255f;
                        Assert.AreEqual(scalar - strength, r, 0.03f,
                            $"seam-outlier texel ({x},{y}) must soft-clip to scalar - strength (= 0.25), not saturate to gloss 0");
                    }
                }

                float quiet = texels[0 * w + 0].r / 255f;
                Assert.LessOrEqual(Mathf.Abs(quiet - scalar), 1f / 255f,
                    "quiet texel must stay at the authored scalar");
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness);
                Release(source, projected);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Transfer_SeamOutlier_DipIsMonotonicInStrength()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping seam-outlier monotonic-strength test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float scalar = 0.5f;
            const int px = 16, py = 16, pw = 2, ph = 2;

            RenderTexture source = CreateBase(w, h, (x, y) =>
                InRect(x, y, px, py, pw, ph) ? new Color(0.95f, 0.95f, 0.95f, 1f) : new Color(0.5f, 0.5f, 0.5f, 1f));
            RenderTexture projected = CreateFlat(w, h, 0.5f);
            RenderTexture roughness025 = null;
            RenderTexture roughness06 = null;

            try
            {
                roughness025 = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 0.25f);
                roughness06 = _pipeline.ExtractTransferRoughness(scalar, source, projected, w, h, 0.6f);

                Color32[] texels025 = ReadBackColor32(roughness025, w * h);
                Color32[] texels06 = ReadBackColor32(roughness06, w * h);

                float r025 = texels025[py * w + px].r / 255f;
                float r06 = texels06[py * w + px].r / 255f;

                Assert.Less(r06, r025 - 0.05f,
                    "seam-outlier texel must dip strictly more at strength 0.6 than 0.25 (monotonic slider)");
                Assert.Less(r025, scalar, "seam texel must dip below the authored scalar at strength 0.25");
                Assert.Less(r06, scalar, "seam texel must dip below the authored scalar at strength 0.6");
            }
            finally
            {
                _pipeline.ReleaseRoughness(roughness025);
                _pipeline.ReleaseRoughness(roughness06);
                Release(source, projected);
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private static RenderTexture CreateFlat(int w, int h, float value)
        {
            return CreateBase(w, h, (x, y) => new Color(value, value, value, 1f));
        }

        private static RenderTexture CreateBase(int w, int h, System.Func<int, int, Color> pixel)
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

        private static bool InRect(int x, int y, int x0, int y0, int w0, int h0)
        {
            return x >= x0 && x < x0 + w0 && y >= y0 && y < y0 + h0;
        }

        private static Color32[] ReadBackColor32(RenderTexture rt, int expectedCount)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color32> data = req.GetData<Color32>();
            try
            {
                Assert.AreEqual(expectedCount, data.Length, "readback must return the expected pixel count");
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

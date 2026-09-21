using System.Collections;
using GraffitiEntertainment.Namer.Core;
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
    /// quotient <c>max(base, VcFloor) / max(vcInterp, VcFloor)</c> derived from the QUANTIZED
    /// Color32 vertex colors (Pitfall 2; 04.2 RESEARCH Pattern 2 symmetric floor): (1) the GPU
    /// residual reconstructs the base within 2/255 per channel, (2) uncovered texels carry the
    /// identity residual, (3) the D-13 residual-required gate distinguishes a constant fit from
    /// a gradient base, and (4) the adaptive search picks a ladder resolution within threshold
    /// with the manual ladder override. All tests are capability-gated (D-15): skipped-with-
    /// report when compute/async-readback is unavailable, never a silent pass.
    /// </summary>
    public class ResidualPipelineTests
    {
        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // 04.2 E2E reconstruction-identity fixture + per-texel tolerance constants. The
        // source is a deterministic hash-noise grain (high-frequency detail a low-res
        // residual would smear) plus ONE sparse dark texel below VcFloor (the heavy-tail
        // shape of real AI albedos), so the symmetric-floor clamp is exercised.
        private const float kGrainMid = 0.5f;
        private const float kGrainAmplitude = 0.05f;
        private const int kDarkTexelX = 5;
        private const int kDarkTexelY = 5;
        private const float kBelowFloorValue = 0.0005f; // < NamerConstants.VcFloor, exercises the floor clamp
        private const float kHalfFloatRelativeEpsilon = 0.001f; // 2^-10; half has 10 mantissa bits
        private const float kReconstructionAbsTolerance = 0.001f; // ~VcFloor, covers the below-floor clamp region

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
                        // CPU oracle: residual = max(base, 1e-3f) / max(vcInterp, 1e-3f) — the
                        // same symmetric-floor quotient the GPU computes in CSResidual
                        // (04.2 RESEARCH Pattern 2: the measured 1.70% below-floor dark-texel
                        // exception class is removed at sub-LSB reconstruction cost).
                        float oracle = Mathf.Max(baseValue, 1e-3f) / Mathf.Max(vcInterp, 1e-3f);

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
        public IEnumerator Residual_TiledUvLayout_FlagsCannotDecompose()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU tiled-UV test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // Island crossing the [0,1] tile boundary: the in-bounds portion would
            // rasterize (coverage > 0, so the zero-coverage fallback cannot catch it) and
            // the uncovered portion would reconstruct as source color — silently wrong.
            // The out-of-range pre-check must flag the honest CannotDecompose fallback.
            NamerSplitResult split = CreateSplitQuad(0.5f, 1.5f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateBase(w, h, new Color(0.25f, 0.25f, 0.25f, 1f));

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                    try
                    {
                        Assert.IsNull(output.Residual, "a tiled layout must not emit a residual");
                        Assert.IsTrue(output.Stats.CannotDecompose,
                            "UVs outside [0,1] must trip the honest CannotDecompose fallback");
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

        [UnityTest]
        public IEnumerator GenerateResidual_TinyUvFootprint_StillDecomposes_AndZeroCoverageStillFallsBack()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            // WR-01: an island covering 12x12 texels of a 512x512 base is a legitimate
            // ~0.055% coverage — BELOW the ~0.196% effective cutoff the old RGBA32 stats
            // readback imposed (round(0.00055 * 255) == 0 quantized the fraction to byte
            // zero) but far above true zero — so it must decompose, not CannotDecompose.
            {
                const int w = 512;
                const int h = 512;
                const float span = 12f / 512f; // 12 texels per axis -> 144 covered texels
                NamerSplitResult split = CreateSplitQuad(0f, span);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateGradientBase(w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                        try
                        {
                            Assert.IsFalse(output.Stats.CannotDecompose,
                                "a small-but-legitimate UV footprint (~0.055% of texels) must decompose, not trip the zero-coverage guard (WR-01)");
                            Assert.IsTrue(output.Stats.ResidualRequired,
                                "a gradient base must still require a residual at tiny coverage");
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

            // True zero coverage (UVs entirely in [1,2]^2, outside texel-center range)
            // must still trip the CR-03 guard and fall back.
            {
                const int w = 64;
                const int h = 64;
                NamerSplitResult split = CreateSplitQuad(1f, 2f);
                Color32[] colors = ConstantColors(split.VertexCount, 128);
                RenderTexture baseRt = CreateGradientBase(w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    try
                    {
                        NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.02f, 0);
                        try
                        {
                            Assert.IsTrue(output.Stats.CannotDecompose,
                                "zero rasterizer coverage must still trip the CR-03 guard");
                            Assert.IsFalse(output.Stats.ResidualRequired);
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

            yield return null;
        }

        [UnityTest]
        public IEnumerator GenerateResidual_NonSquareBase_PreservesSourceAspect()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            // WR-01 (04.2-08): the residual ladder value is the LONG edge of the residual;
            // the short edge scales to preserve the source aspect. A 256x128 base with manual
            // index 5 (ResolutionLadder[4] = 128) must produce a 128x64 residual — never the
            // square 128x128 target the width-only clamp produced before the fix.
            const int w = 256;
            const int h = 128;
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateGradientBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, 0.05f, 5);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "a gradient base must require a residual");
                        Assert.IsNotNull(output.Residual);
                        Assert.AreEqual(128, output.Stats.ChosenResolution, "manual index 5 resolves to the 128px long edge");
                        Assert.AreEqual(128, output.Residual.width, "residual long edge must be the chosen resolution");
                        Assert.AreEqual(64, output.Residual.height, "residual short edge must preserve the 2:1 source aspect");
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

        /// <summary>
        /// Durable E2E reconstruction-identity regression (04.2 residual-smeared-reconstruction).
        /// Proves that a CORRECT full-resolution residual reconstructs the AO-unmultiplied
        /// source: for every covered texel, <c>vcInterp x residual == max(source, VcFloor)</c>
        /// when <c>vcInterp >= VcFloor</c>, i.e. the source-dividend identity
        /// <c>residual = max(source, VcFloor) / max(vcInterp, VcFloor)</c>. Texels whose source
        /// sits BELOW VcFloor reconstruct to VcFloor (not source) — the symmetric floor is BY
        /// DESIGN (04.2 RESEARCH Pattern 2) — so the assertion compares each texel against its
        /// own oracle with a relative+absolute tolerance scaled to the oracle, NOT a flat byte
        /// tolerance, so a legitimate below-floor texel is not a false failure. A smeared
        /// low-resolution residual (the 128-class failure) blurs the hash-noise grain and either
        /// trips the full-resolution ladder assertion or blows the per-texel tolerance.
        /// </summary>
        [UnityTest]
        public IEnumerator Residual_FullResolution_ReconstructsSourcePerCoveredTexel()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 256;
            const int h = 256;
            const float threshold = 0.02f;
            const byte vcByte = 128;

            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, vcByte);
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "grain detail must require a residual");
                        Assert.IsNotNull(output.Residual);
                        Assert.AreEqual(w, output.Stats.ChosenResolution,
                            "auto resolution must stay at the full source long edge — a smeared low-res residual would report a smaller rung");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        float vcInterp = vcByte / 255f;
                        float vcFloor = NamerConstants.VcFloor;
                        for (int i = 0; i < residual.Length; i++)
                        {
                            int x = i % w;
                            int y = i / w;
                            float source = SourceAt(x, y);

                            // Source-dividend oracle: reconstruction = vcInterp * residual,
                            // residual = max(source, VcFloor) / max(vcInterp, VcFloor), so the
                            // expected reconstruction is the symmetric-floor quotient. This is
                            // per-texel (not a flat byte tolerance) and stays correct even when
                            // source or vcInterp sits below VcFloor.
                            float oracle = vcInterp * Mathf.Max(source, vcFloor) / Mathf.Max(vcInterp, vcFloor);
                            float tolerance = kHalfFloatRelativeEpsilon * oracle + kReconstructionAbsTolerance;

                            Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[i].r - oracle), tolerance,
                                "reconstruction R texel (" + x + "," + y + ")");
                            Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[i].g - oracle), tolerance,
                                "reconstruction G texel (" + x + "," + y + ")");
                            Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[i].b - oracle), tolerance,
                                "reconstruction B texel (" + x + "," + y + ")");
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

        // Discriminating test for debug session residual-missing-triangles (2026-09-19).
        // User signature: triangles with 2 dark vertices + 1 near-white vertex lose
        // residual signal. The kernel reads the QUANTIZED 8-bit _VcInterp, and any
        // nonzero quantized value is >= 1/255 > VcFloor, where the quotient reconstructs
        // exactly — so the operator's only total-loss zone is vcInterp quantizing to
        // exactly 0. This test asserts every covered texel whose reconstruction misses
        // the base falls INSIDE that predicted zone. Missing signal OUTSIDE it
        // reproduces the bug in a clean fixture and exonerates the operator
        // (coverage/generation defect); loss confined to the zone implicates the
        // operator only for triangles small enough in UV space to be covered by it.
        [UnityTest]
        public IEnumerator Residual_DarkVertexTriangle_ClassifiesMissingSignal()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;
            const byte kWhiteVert = 250; // user signature: one near-white vertex...
            const byte kMidVert = 128;   // ...and darker ones. 0 (not merely dark-10)
            const byte kBlackVert = 0;   // exercises the quantized-zero loss zone.
            const float kReconstructionTolerance = 2f / 255f;
            const float kNeededCorrection = 2f / 255f;
            const float kQuantZeroLimit = 0.5f / 255f; // below this the 8-bit store rounds to 0
            const float kInsideEps = 1e-4f;            // mirrors NAMER_DECOMP_INSIDE_EPS

            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors =
            {
                new Color32(kWhiteVert, kWhiteVert, kWhiteVert, 255), // v0 (0,0)
                new Color32(kMidVert, kMidVert, kMidVert, 255),       // v1 (1,0)
                new Color32(kBlackVert, kBlackVert, kBlackVert, 255), // v2 (1,1)
                new Color32(kBlackVert, kBlackVert, kBlackVert, 255), // v3 (0,1)
            };
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "the dark-gradient fit must require a residual");
                        Assert.IsNotNull(output.Residual);
                        Assert.AreEqual(w, output.Stats.ChosenResolution,
                            "auto resolution must stay at the full source long edge — a lower rung would smear the classification");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        int covered = 0;
                        int quantZeroMissing = 0;
                        int unexplained = 0;
                        int unexplainedX = -1;
                        int unexplainedY = -1;

                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                Vector2 p = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                                float vcInterp;
                                if (!TryInterpVertexColor(split, colors, p, kInsideEps, out vcInterp))
                                {
                                    continue;
                                }

                                covered++;
                                float vcQuantized = Mathf.Round(vcInterp * 255f) / 255f;
                                float source = SourceAt(x, y);
                                float reconstruction = vcQuantized * residual[y * w + x].r;

                                bool needed = Mathf.Abs(source - vcQuantized) > kNeededCorrection;
                                bool missing = needed
                                    && Mathf.Abs(reconstruction - source) > kReconstructionTolerance;
                                if (!missing)
                                {
                                    continue;
                                }

                                if (vcInterp < kQuantZeroLimit)
                                {
                                    quantZeroMissing++;
                                }
                                else
                                {
                                    unexplained++;
                                    if (unexplainedX < 0)
                                    {
                                        unexplainedX = x;
                                        unexplainedY = y;
                                    }
                                }
                            }
                        }

                        TestContext.Out.WriteLine(
                            "[NAMER] classifier: covered=" + covered + " quantZeroMissing=" + quantZeroMissing
                            + " unexplained=" + unexplained);
                        Assert.AreEqual(0, unexplained,
                            "missing residual signal OUTSIDE the quantized-zero zone — first at ("
                            + unexplainedX + "," + unexplainedY + ") with analytic vcInterp; "
                            + "reproduces residual-missing-triangles in a clean fixture (operator exonerated). "
                            + "quantZeroMissing=" + quantZeroMissing + " covered=" + covered);
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

        // Regression pin for the orange edge-line artifact (debug session
        // residual-missing-triangles, 2026-09-19 follow-up). Texel centers on or beside
        // an edge between two black vertices interpolate vcInterp to ~0; VcFloor (1e-3)
        // sits below one 8-bit step (1/255), so it never bounds the divide and the
        // quotient reaches base*1000 in isolated 1-texel spikes (real asset: up to
        // 405x, always at bary=0.000 with 2-3 identity neighbors). The runtime material
        // samples the residual bilinearly (NamerSurface.hlsl), smearing each spike into
        // a bright hue-tinted line along the edge. The despike pass must replace any
        // channel exceeding NamerConstants.ResidualSpikeFactor times the second-smallest
        // of its 4 clamp-to-edge neighbors with that value; this test pins that
        // invariant. The black edge is DIAGONAL so the spikes stay isolated; the
        // axis-aligned companion test below covers adjacent spike runs.
        [UnityTest]
        public IEnumerator Residual_BlackEdgeTexel_DespikesIsolatedQuotientSpikes()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;
            const float kFloat16Slack = 1e-3f; // R16 storage round-trip of references near 1.0
            const float kQuantZeroVc = 0.5f / 255f; // _VcInterp is UNorm: interp below half an 8-bit step stores as exactly 0 — the despike's spike-candidate gate

            // ~19-texel triangle in texel-center coordinates: white apex opposite a
            // diagonal black-black edge, so scattered texel centers near that edge
            // interpolate vc to ~0 while the grain base stays ~0.5 — the real asset's
            // spike geometry in miniature.
            NamerSplitResult split = CreateSplitTriangle(
                new Vector2(30.5f / w, 46.5f / h), // white apex
                new Vector2(22.5f / w, 28.5f / h), // black
                new Vector2(38.5f / w, 38.5f / h)); // black — the v1-v2 edge carries vc = 0
            Color32[] colors =
            {
                new Color32(250, 250, 250, 255), // white, opposite the black edge
                new Color32(0, 0, 0, 255),       // black
                new Color32(0, 0, 0, 255),       // black
            };
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "the white-to-black gradient must require a residual");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        // Spikes only exist in COVERED space (the quotient is only computed
                        // there); uncovered texels are identity or the dilation padding ring,
                        // whose outer step is legitimate atlas padding and never sampled from
                        // inside the island. And among covered texels, only those whose
                        // interpolated vc QUANTIZED TO ZERO are spike candidates: the quotient
                        // gradient 1/vc near a black vertex is legitimately steeper than any
                        // local factor (the real asset's legit band runs 60-130x), so the
                        // invariant mirrors the kernel's vc gate. Grayscale fixture: one vc
                        // value serves all three channels.
                        bool[] covered = new bool[w * h];
                        float[] vcInterp = new float[w * h];
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                covered[y * w + x] = TryInterpVertexColor(
                                    split, colors, new Vector2((x + 0.5f) / w, (y + 0.5f) / h), 1e-4f, out vcInterp[y * w + x]);
                            }
                        }

                        int violations = 0;
                        int firstX = -1;
                        int firstY = -1;
                        float firstValue = 0f;
                        float firstLimit = 0f;
                        System.Text.StringBuilder firstDump = new System.Text.StringBuilder();

                        void Check(float value, float reference, int x, int y)
                        {
                            if (vcInterp[y * w + x] > kQuantZeroVc)
                            {
                                return; // nonzero quantized vc: protected legit quotient, not a spike candidate
                            }

                            float limit = NamerConstants.ResidualSpikeFactor * reference + kFloat16Slack;
                            if (value > limit)
                            {
                                violations++;
                                if (firstX < 0)
                                {
                                    firstX = x;
                                    firstY = y;
                                    firstValue = value;
                                    firstLimit = limit;
                                    firstDump.Length = 0;
                                    for (int dy = -1; dy <= 1; dy++)
                                    {
                                        for (int dx = -1; dx <= 1; dx++)
                                        {
                                            int nx = Mathf.Clamp(x + dx, 0, w - 1);
                                            int ny = Mathf.Clamp(y + dy, 0, h - 1);
                                            Color n = residual[ny * w + nx];
                                            firstDump.Append("(" + nx + "," + ny + (covered[ny * w + nx] ? "C" : "u") + ")"
                                                + n.r.ToString("F1") + "/" + n.g.ToString("F1") + "/" + n.b.ToString("F1") + " ");
                                        }

                                        firstDump.Append("| ");
                                    }
                                }
                            }
                        }

                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                if (!covered[y * w + x])
                                {
                                    continue;
                                }

                                Color self = residual[y * w + x];
                                Color n2 = SecondSmallest4(
                                    residual[y * w + Mathf.Max(x - 1, 0)],
                                    residual[y * w + Mathf.Min(x + 1, w - 1)],
                                    residual[Mathf.Max(y - 1, 0) * w + x],
                                    residual[Mathf.Min(y + 1, h - 1) * w + x]);
                                Check(self.r, n2.r, x, y);
                                Check(self.g, n2.g, x, y);
                                Check(self.b, n2.b, x, y);
                            }
                        }

                        Assert.AreEqual(0, violations,
                            "isolated residual spike survives the despike pass — first at ("
                            + firstX + "," + firstY + ") value=" + firstValue.ToString("F1")
                            + " limit=" + firstLimit.ToString("F1")
                            + " 3x3 (C=covered/u=uncovered) r/g/b: " + firstDump
                            + "; bilinear sampling smears it into the orange edge-line artifact (residual-missing-triangles)");
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

        // Regression pin for the SPIKE-RUN blind spot (debug session
        // residual-missing-triangles, 2026-09-19 render-diff follow-up). The saved
        // real-asset EXR still contained 2+-texel runs of 4-10x residual after the
        // median-based despike: a collinear spiky neighbor inflates the median of 4
        // toward the spike, so limit ~= 2x spike > spike and the run survives. The
        // second-smallest of 4 neighbors cannot be inflated by more than one spiky
        // neighbor, so a 1-texel-wide run still trips. This fixture aligns the
        // black-black edge exactly ON a column of texel centers (x = 20.5), producing
        // a vertical RUN of quantized-zero-vc spikes — the geometry the diagonal
        // fixture above deliberately avoids — and pins the same no-channel-exceeds-
        // ResidualSpikeFactor-x-second-smallest invariant over the whole output.
        [UnityTest]
        public IEnumerator Residual_AxisAlignedBlackEdge_DespikesAdjacentSpikeRuns()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;
            const float kFloat16Slack = 1e-3f; // R16 storage round-trip of references near 1.0
            const float kQuantZeroVc = 0.5f / 255f; // _VcInterp is UNorm: interp below half an 8-bit step stores as exactly 0 — the despike's spike-candidate gate

            // White apex opposite a VERTICAL black-black edge at x = 20.5 texels: every
            // texel center of column 20 lies exactly on the edge (bary = 0 between two
            // black verts), so the whole column interpolates vc to 0 and divides by
            // VcFloor — an adjacent spike run, not isolated spikes.
            NamerSplitResult split = CreateSplitTriangle(
                new Vector2(50.5f / w, 32.5f / h), // white apex
                new Vector2(20.5f / w, 12.5f / h), // black
                new Vector2(20.5f / w, 52.5f / h)); // black — the v1-v2 edge carries vc = 0
            Color32[] colors =
            {
                new Color32(250, 250, 250, 255), // white, opposite the black edge
                new Color32(0, 0, 0, 255),       // black
                new Color32(0, 0, 0, 255),       // black
            };
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "the white-to-black gradient must require a residual");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        // Scope the invariant to COVERED, quantized-zero-vc texels (see the
                        // isolated-spike test above): the spike run lives there; uncovered
                        // texels are identity or dilation padding, and nonzero-vc quotients
                        // are legitimately steep near the black edge.
                        bool[] covered = new bool[w * h];
                        float[] vcInterp = new float[w * h];
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                covered[y * w + x] = TryInterpVertexColor(
                                    split, colors, new Vector2((x + 0.5f) / w, (y + 0.5f) / h), 1e-4f, out vcInterp[y * w + x]);
                            }
                        }

                        int violations = 0;
                        int firstX = -1;
                        int firstY = -1;
                        float firstValue = 0f;
                        float firstLimit = 0f;
                        System.Text.StringBuilder firstDump = new System.Text.StringBuilder();

                        void Check(float value, float reference, int x, int y)
                        {
                            if (vcInterp[y * w + x] > kQuantZeroVc)
                            {
                                return; // nonzero quantized vc: protected legit quotient, not a spike candidate
                            }

                            float limit = NamerConstants.ResidualSpikeFactor * reference + kFloat16Slack;
                            if (value > limit)
                            {
                                violations++;
                                if (firstX < 0)
                                {
                                    firstX = x;
                                    firstY = y;
                                    firstValue = value;
                                    firstLimit = limit;
                                    firstDump.Length = 0;
                                    for (int dy = -1; dy <= 1; dy++)
                                    {
                                        for (int dx = -1; dx <= 1; dx++)
                                        {
                                            int nx = Mathf.Clamp(x + dx, 0, w - 1);
                                            int ny = Mathf.Clamp(y + dy, 0, h - 1);
                                            Color n = residual[ny * w + nx];
                                            firstDump.Append("(" + nx + "," + ny + (covered[ny * w + nx] ? "C" : "u") + ")"
                                                + n.r.ToString("F1") + "/" + n.g.ToString("F1") + "/" + n.b.ToString("F1") + " ");
                                        }

                                        firstDump.Append("| ");
                                    }
                                }
                            }
                        }

                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                if (!covered[y * w + x])
                                {
                                    continue;
                                }

                                Color self = residual[y * w + x];
                                Color n2 = SecondSmallest4(
                                    residual[y * w + Mathf.Max(x - 1, 0)],
                                    residual[y * w + Mathf.Min(x + 1, w - 1)],
                                    residual[Mathf.Max(y - 1, 0) * w + x],
                                    residual[Mathf.Min(y + 1, h - 1) * w + x]);
                                Check(self.r, n2.r, x, y);
                                Check(self.g, n2.g, x, y);
                                Check(self.b, n2.b, x, y);
                            }
                        }

                        Assert.AreEqual(0, violations,
                            "adjacent residual spike run survives the despike pass — first at ("
                            + firstX + "," + firstY + ") value=" + firstValue.ToString("F1")
                            + " limit=" + firstLimit.ToString("F1")
                            + " 3x3 (C=covered/u=uncovered) r/g/b: " + firstDump
                            + "; bilinear sampling smears the run into the tinted edge-line artifact (residual-missing-triangles)");
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

        // Regression pin for the SEAM-STEP artifact (debug session
        // residual-missing-triangles, 2026-09-19 render-diff follow-up). Uncovered
        // atlas texels carry the identity residual 1.0; at UV island borders the
        // runtime's BILINEAR residual sample mixes the correct covered residual with
        // that identity (measured 0.21 next to 1.0 -> after = vc * ~1.0 >> base, a
        // 1px orange line; despike cannot fix a step by principle). The pipeline must
        // DILATE the covered residual outward: an uncovered texel within
        // NamerConstants.ResidualDilateRadius (Chebyshev, texel index distance) of
        // covered texels takes their mean residual; uncovered texels with no covered
        // neighbor in range keep the identity. Fixture: [0,0.5]^2 quad on a 64^2
        // atlas (covered columns/rows 0..31) with constant vc 100/255 over a grain
        // base, so every covered residual is ~1.3, never 1.0.
        [UnityTest]
        public IEnumerator Residual_UncoveredBorderTexels_ReceiveDilatedResidual()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;
            const float threshold = 0.02f;
            const byte vcByte = 100; // ~0.392: grain ~0.5 / 0.392 ~ 1.3x residual, never identity
            const float kIdentity = 1f; // uncovered no-neighbor branch writes exactly 1.0
            const float kFloat16Epsilon = 2e-3f; // R16 storage round-trip of a <= 24-texel mean

            NamerSplitResult split = CreateSplitQuad(0f, 0.5f);
            Color32[] colors = ConstantColors(4, vcByte);
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "grain base under a flat vc fit must require a residual");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        // CPU coverage oracle (same barycentric test as the rasterizer).
                        bool[] covered = new bool[w * h];
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                covered[y * w + x] = TryInterpVertexColor(
                                    split, colors, new Vector2((x + 0.5f) / w, (y + 0.5f) / h), 1e-4f, out _);
                            }
                        }

                        int radius = NamerConstants.ResidualDilateRadius;
                        int mismatches = 0;
                        int firstX = -1;
                        int firstY = -1;
                        float firstActual = 0f;
                        float firstExpected = 0f;

                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                if (covered[y * w + x])
                                {
                                    continue; // covered texels pass through; reconstruction is pinned elsewhere
                                }

                                // Expected: mean of the covered texels' residuals within the
                                // Chebyshev radius, or the identity when none are in range.
                                float sum = 0f;
                                int count = 0;
                                for (int dy = -radius; dy <= radius; dy++)
                                {
                                    for (int dx = -radius; dx <= radius; dx++)
                                    {
                                        int nx = x + dx;
                                        int ny = y + dy;
                                        if (nx < 0 || ny < 0 || nx >= w || ny >= h)
                                        {
                                            continue;
                                        }

                                        if (covered[ny * w + nx])
                                        {
                                            sum += residual[ny * w + nx].r;
                                            count++;
                                        }
                                    }
                                }

                                float expected = count > 0 ? sum / count : kIdentity;
                                float actual = residual[y * w + x].r;
                                if (Mathf.Abs(actual - expected) > kFloat16Epsilon)
                                {
                                    mismatches++;
                                    if (firstX < 0)
                                    {
                                        firstX = x;
                                        firstY = y;
                                        firstActual = actual;
                                        firstExpected = expected;
                                    }
                                }
                            }
                        }

                        Assert.AreEqual(0, mismatches,
                            "uncovered border texel residual does not match the dilated covered mean — first at ("
                            + firstX + "," + firstY + ") actual=" + firstActual.ToString("F3")
                            + " expected=" + firstExpected.ToString("F3")
                            + "; a covered-vs-identity step at a UV island border smears into the orange seam-line artifact (residual-missing-triangles)");
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

        // Regression pin for debug session residual-missing-triangles (2026-09-19). The
        // rasterizer's degeneracy gate compared denom = (2*Area_UV)^2 against a 1e-8
        // epsilon — QUADRATIC in UV edge length, so at higher atlas resolutions real
        // triangles fall under the gate and are discarded as "degenerate" (~92% of the
        // Neo mesh's triangles at 2048^2). Their texels then take the identity residual
        // branch and reconstruction loses all correcting signal. This fixture keeps each
        // quad triangle at 32 texel^2 — below the pre-fix gate at 1024^2, far above true
        // zero-area — and asserts every covered texel still reconstructs the source.
        [UnityTest]
        public IEnumerator Residual_SmallUVTriangle_ReconstructsSourcePerCoveredTexel()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU residual test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 1024;
            const int h = 1024;
            const float threshold = 0.02f;
            const byte vcByte = 128;
            const float span = 8f / 1024f; // 8-texel quad -> 32-texel^2 triangles: below the
                                           // pre-fix denom gate at 1024^2, far above true
                                           // zero-area degeneracy.
            const float kInsideEps = 1e-4f;  // mirrors NAMER_DECOMP_INSIDE_EPS
            const int kMinCoveredTexels = 32; // fixture sanity floor (8x8 quad)

            NamerSplitResult split = CreateSplitQuad(0f, span);
            Color32[] colors = ConstantColors(split.VertexCount, vcByte);
            RenderTexture baseRt = CreateGrainBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    NamerDecompOutput output = pipeline.GenerateResidual(split, colors, baseRt, w, h, threshold, 0);
                    try
                    {
                        Assert.IsTrue(output.Stats.ResidualRequired, "grain detail must require a residual");
                        Assert.IsNotNull(output.Residual);
                        Assert.AreEqual(w, output.Stats.ChosenResolution,
                            "auto resolution must stay at the full source long edge — a lower rung would shrink the UV footprint below one texel");

                        Color[] residual = ReadBackFloat(output.Residual);
                        Assert.AreEqual(w * h, residual.Length, "residual readback must cover every source texel");

                        float vcInterp = vcByte / 255f;
                        float vcFloor = NamerConstants.VcFloor;
                        int covered = 0;
                        for (int y = 0; y < h; y++)
                        {
                            for (int x = 0; x < w; x++)
                            {
                                Vector2 p = new Vector2((x + 0.5f) / w, (y + 0.5f) / h);
                                float analyticVc;
                                if (!TryInterpVertexColor(split, colors, p, kInsideEps, out analyticVc))
                                {
                                    continue; // outside the small quad — identity branch is correct there
                                }

                                covered++;
                                float source = SourceAt(x, y);

                                // Source-dividend oracle (same as the full-resolution test):
                                // reconstruction = vcInterp * residual with
                                // residual = max(source, VcFloor) / max(vcInterp, VcFloor).
                                float oracle = vcInterp * Mathf.Max(source, vcFloor) / Mathf.Max(vcInterp, vcFloor);
                                float tolerance = kHalfFloatRelativeEpsilon * oracle + kReconstructionAbsTolerance;

                                Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[y * w + x].r - oracle), tolerance,
                                    "reconstruction R texel (" + x + "," + y + ") — a below-gate triangle fell to the identity branch and lost all correcting signal");
                                Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[y * w + x].g - oracle), tolerance,
                                    "reconstruction G texel (" + x + "," + y + ")");
                                Assert.LessOrEqual(Mathf.Abs(vcInterp * residual[y * w + x].b - oracle), tolerance,
                                    "reconstruction B texel (" + x + "," + y + ")");
                            }
                        }

                        Assert.GreaterOrEqual(covered, kMinCoveredTexels,
                            "fixture sanity: the 8x8-texel quad must contain enough texel centers to discriminate");
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

        // Single-triangle variant of CreateSplitQuad for fixtures that need one
        // isolated edge's geometry (quotient-spike fixtures) rather than a full quad.
        private static NamerSplitResult CreateSplitTriangle(Vector2 uv0, Vector2 uv1, Vector2 uv2)
        {
            return new NamerSplitResult
            {
                Positions = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(0f, 0f, 1f),
                },
                Normals = new[] { Vector3.up, Vector3.up, Vector3.up },
                Tangents = new Vector4[3],
                Uvs = new[] { uv0, uv1, uv2 },
                SubMeshTriangles = new[] { new[] { 0, 1, 2 } },
            };
        }

        // Mirrors SecondSmallest4 in NAMERDecomp.compute (CSDespike): component-wise
        // second-smallest of the 4 clamp-to-edge neighbors via a 5-compare-exchange
        // sorting network. Unlike the median of 4 it cannot be inflated by a spiky
        // neighbor, so 1-texel spike RUNS trip the limit too.
        private static Color SecondSmallest4(Color a, Color b, Color c, Color d)
        {
            return new Color(
                SecondSmallest4(a.r, b.r, c.r, d.r),
                SecondSmallest4(a.g, b.g, c.g, d.g),
                SecondSmallest4(a.b, b.b, c.b, d.b));
        }

        private static float SecondSmallest4(float a, float b, float c, float d)
        {
            float t = Mathf.Min(a, b); b = Mathf.Max(a, b); a = t;
            t = Mathf.Min(c, d); d = Mathf.Max(c, d); c = t;
            t = Mathf.Min(a, c); c = Mathf.Max(a, c); a = t;
            t = Mathf.Min(b, d); d = Mathf.Max(b, d); b = t;
            t = Mathf.Min(b, c); c = Mathf.Max(b, c); b = t;
            return b;
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

        // CPU oracle for the GPU's UV-space Gouraud rasterization: barycentric
        // interpolation of the vertex colors at p, tested against every triangle of
        // the split (first containing triangle wins, matching dominant-triangle
        // selection). Returns false when p is outside every triangle (uncovered).
        private static bool TryInterpVertexColor(
            NamerSplitResult split,
            Color32[] colors,
            Vector2 p,
            float insideEps,
            out float vcInterp)
        {
            foreach (int[] triangle in split.SubMeshTriangles)
            {
                for (int i = 0; i < triangle.Length; i += 3)
                {
                    int ia = triangle[i];
                    int ib = triangle[i + 1];
                    int ic = triangle[i + 2];
                    Vector2 a = split.Uvs[ia];
                    Vector2 b = split.Uvs[ib];
                    Vector2 c = split.Uvs[ic];

                    float area2 = Cross(b - a, c - a);
                    if (Mathf.Abs(area2) < 1e-8f)
                    {
                        continue;
                    }

                    float wa = Cross(b - p, c - p) / area2;
                    float wb = Cross(c - p, a - p) / area2;
                    float wc = 1f - wa - wb;
                    if (wa < -insideEps || wb < -insideEps || wc < -insideEps)
                    {
                        continue;
                    }

                    vcInterp = (wa * colors[ia].r + wb * colors[ib].r + wc * colors[ic].r) / 255f;
                    return true;
                }
            }

            vcInterp = 0f;
            return false;
        }

        private static float Cross(Vector2 u, Vector2 v)
        {
            return (u.x * v.y) - (u.y * v.x);
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

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            // RGBAFloat readback of the R16G16B16A16_SFloat residual RT is lossless (half
            // widens to float exactly) and returns Color[] directly, matching the residual's
            // 16-bit-half precision the saved EXR carries (AssetGenerator.ReadBackResidual).
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

        private static RenderTexture CreateGrainBase(int w, int h)
        {
            return UploadBase(w, h, (x, y) =>
            {
                float s = SourceAt(x, y);
                return new Color(s, s, s, 1f);
            });
        }

        private static float SourceAt(int x, int y)
        {
            if (x == kDarkTexelX && y == kDarkTexelY)
            {
                return kBelowFloorValue;
            }

            return kGrainMid + kGrainAmplitude * GrainNoise(x, y);
        }

        private static float GrainNoise(int x, int y)
        {
            // Deterministic hash-noise grain (memory: namer-test-fixtures-ao-unmultiply):
            // high-frequency detail a low-pass AO blur cannot track; a smooth luminance ramp
            // would be flattened to a constant. frac via f - Floor(f) (C# has no Mathf.frac).
            float v = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            return (v - Mathf.Floor(v)) * 2f - 1f; // [-1, 1]
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

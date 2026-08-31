using System;
using GraffitiEntertainment.Namer.Core;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Reconstruction-error statistics for the vertex-color decomposition (VCOL-04 / D-09).
    /// Every field derives from the one consistent mean-channel MAE metric
    /// <c>err = mean |vcInterp * residual - base|</c>. <see cref="Coverage"/> is the
    /// fraction of UV-covered texels reconstructed within the threshold;
    /// <see cref="AvgError"/> / <see cref="MaxError"/> are the mean / max error over the
    /// covered texels; <see cref="ResidualRequired"/> is the D-13 gate; and
    /// <see cref="ChosenResolution"/> is 0 when the residual is not required, otherwise the
    /// chosen residual width.
    /// </summary>
    public sealed class NamerDecompErrorStats
    {
        public float Coverage;
        public float AvgError;
        public float MaxError;
        public bool ResidualRequired;
        public int ChosenResolution;
    }

    /// <summary>
    /// In-memory result of <see cref="NamerDecompPipeline.GenerateResidual"/>. The residual
    /// is a pool-leased <see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear render
    /// target (null when <see cref="NamerDecompErrorStats.ResidualRequired"/> is false); the
    /// caller owns it and must <see cref="Dispose"/> it to release it back to the pool. It
    /// is produced at the chosen resolution (not upsampled back to source) — the runtime
    /// material samples it with hardware bilinear filtering.
    /// </summary>
    public sealed class NamerDecompOutput : IDisposable
    {
        public RenderTexture Residual;
        public NamerDecompErrorStats Stats;

        private readonly NamerDecompPipeline _owner;
        private bool _disposed;

        internal NamerDecompOutput(RenderTexture residual, NamerDecompErrorStats stats, NamerDecompPipeline owner)
        {
            Residual = residual;
            Stats = stats;
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Release(Residual);
            Residual = null;
        }
    }

    /// <summary>
    /// GPU dispatch harness for the vertex-color decomposition residual stage (Phase 4,
    /// plan 02 — VCOL-03/VCOL-04/VCOL-05). Turns the quantized Color32 vertex colors (plan
    /// 04-01) and the already-uploaded linear base color into the multiplicative quotient
    /// residual <c>base / max(vcInterp, VcFloor)</c>, then reduces coverage/avg/max error
    /// and runs the adaptive downward-halving residual-resolution search with the manual
    /// ladder override.
    ///
    /// All per-pixel work is compute (<c>Compute/NAMERDecomp.compute</c>); the only CPU
    /// loops are the tiny block-reduction readback and the halving decision loop. No disk
    /// writes — 04-03 owns persistence. The returned residual is pool-leased via
    /// <see cref="NamerDecompOutput"/>.
    /// </summary>
    public sealed class NamerDecompPipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERDecomp.compute";

        private const int ReduceDownsampleFactor = 8;
        private const float kMaxObservedErrFloor = 0.25f;
        private const float kOpaqueAlphaThreshold = 0.999f;

        /// <summary>D-17 halving ladder (0 = Auto maps to the adaptive search).</summary>
        public static readonly int[] ResolutionLadder = { 2048, 1024, 512, 256, 128 };

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelRasterize;
        private readonly int _kernelResidual;
        private readonly int _kernelErrorHeatmap;
        private readonly int _kernelReduce;

        public NamerDecompPipeline()
        {
            _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            if (_compute == null)
            {
                throw new InvalidOperationException("NAMER decomp compute shader not found at " + ComputeShaderPath);
            }

            _kernelRasterize = _compute.FindKernel("CSRasterizeVertexColors");
            _kernelResidual = _compute.FindKernel("CSResidual");
            _kernelErrorHeatmap = _compute.FindKernel("CSErrorHeatmap");
            _kernelReduce = _compute.FindKernel("CSReduce");
        }

        /// <summary>
        /// Rasterizes the quantized <paramref name="quantizedColors"/> into UV space, computes
        /// the quotient residual, reduces the error stats, applies the D-13 residual-required
        /// gate (with the alpha-opaque guard, Pitfall 5), and runs the downward-halving
        /// adaptive search (D-16) with the manual ladder override (D-17). Returns a
        /// <see cref="NamerDecompOutput"/> whose <see cref="NamerDecompOutput.Residual"/> is
        /// at the chosen resolution, or null when the fit alone reconstructs within
        /// <paramref name="errorThreshold"/> for a fully opaque base.
        /// </summary>
        public NamerDecompOutput GenerateResidual(
            NamerSplitResult split,
            Color32[] quantizedColors,
            RenderTexture baseLinear,
            int w,
            int h,
            float errorThreshold,
            int manualResolution)
        {
            if (split == null)
            {
                throw new ArgumentNullException(nameof(split));
            }

            if (baseLinear == null)
            {
                throw new ArgumentNullException(nameof(baseLinear));
            }

            if (quantizedColors == null || quantizedColors.Length != split.VertexCount)
            {
                throw new ArgumentException("quantizedColors length must equal the split vertex count.", nameof(quantizedColors));
            }

            if (w < 1 || h < 1)
            {
                throw new ArgumentException("Base dimensions must be positive.", nameof(w));
            }

            float[] verts = ToFloat3(split.Positions);
            float[] uvs = ToFloat2(split.Uvs);
            int[] tris = FlattenTriangles(split.SubMeshTriangles);
            float[] colors = ToFloat4(quantizedColors);
            int triCount = tris.Length / 3;

            ComputeBuffer vertsBuf = null;
            ComputeBuffer uvsBuf = null;
            ComputeBuffer trisBuf = null;
            ComputeBuffer colorsBuf = null;

            RenderTexture vcInterp = null;
            RenderTexture fullResidual = null;
            RenderTexture heatmap = null;
            RenderTexture errorStat = null;
            RenderTexture coverageStat = null;
            RenderTexture avgA = null;
            RenderTexture avgB = null;
            RenderTexture returnedResidual = null;

            try
            {
                vertsBuf = CreateBuffer(verts, split.VertexCount, sizeof(float) * 3);
                uvsBuf = CreateBuffer(uvs, split.VertexCount, sizeof(float) * 2);
                trisBuf = CreateBuffer(tris, triCount, sizeof(int) * 3);
                colorsBuf = CreateBuffer(colors, split.VertexCount, sizeof(float) * 4);

                RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);
                RenderTextureDescriptor floatDesc = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
                int avgW = (w + ReduceDownsampleFactor - 1) / ReduceDownsampleFactor;
                int avgH = (h + ReduceDownsampleFactor - 1) / ReduceDownsampleFactor;
                RenderTextureDescriptor avgDesc = NewDescriptor(avgW, avgH, GraphicsFormat.R16G16B16A16_SFloat);

                vcInterp = _pool.Lease(unorm8);
                fullResidual = _pool.Lease(floatDesc);
                heatmap = _pool.Lease(unorm8);
                errorStat = _pool.Lease(floatDesc);
                coverageStat = _pool.Lease(unorm8);
                avgA = _pool.Lease(avgDesc);
                avgB = _pool.Lease(avgDesc);

                // 1. Rasterize the quantized colors into UV space (_VcInterp, a = coverage).
                _compute.SetInts("_Size", new[] { w, h });
                _compute.SetInt("_TriCount", triCount);
                _compute.SetBuffer(_kernelRasterize, "_Verts", vertsBuf);
                _compute.SetBuffer(_kernelRasterize, "_Uvs", uvsBuf);
                _compute.SetBuffer(_kernelRasterize, "_Tris", trisBuf);
                _compute.SetBuffer(_kernelRasterize, "_Colors", colorsBuf);
                _compute.SetTexture(_kernelRasterize, "_VcInterp", vcInterp);
                Dispatch(_kernelRasterize, w, h);

                _compute.SetFloat("_VcFloor", NamerConstants.VcFloor);
                _compute.SetFloat("_Threshold", errorThreshold);
                _compute.SetFloat("_MaxObservedErr", Mathf.Max(errorThreshold, kMaxObservedErrFloor));

                // 2. Fit-only error (residual == identity) + base opacity for the D-13 gate.
                RunErrorHeatmap(vcInterp, fullResidual, baseLinear, heatmap, errorStat, coverageStat, 1f);
                ReduceStats fitStats = ReduceStats(errorStat, w, h, avgA, avgB);

                // 3. D-13 residual-required gate: drop only for a within-threshold fit on a
                //    fully opaque base (Pitfall 5 — transparent/cutout always keep a residual).
                bool opaque = fitStats.MinAlpha >= kOpaqueAlphaThreshold;
                if (fitStats.MaxError <= errorThreshold && opaque)
                {
                    ReduceStats coverage = ReduceStats(coverageStat, w, h, avgA, avgB);
                    return new NamerDecompOutput(null, BuildStats(fitStats, coverage, required: false, chosenResolution: 0), this);
                }

                // 4. Residual required: compute the full-resolution quotient residual.
                _compute.SetTexture(_kernelResidual, "_VcInterp", vcInterp);
                _compute.SetTexture(_kernelResidual, "_BaseLinear", baseLinear);
                _compute.SetTexture(_kernelResidual, "_ResidualOut", fullResidual);
                Dispatch(_kernelResidual, w, h);

                // 5. Adaptive downward-halving search (D-16) with the manual ladder override
                //    (D-17).
                int chosenResolution = ChooseResolution(fullResidual, vcInterp, baseLinear, heatmap, errorStat, coverageStat,
                    w, h, errorThreshold, manualResolution, avgA, avgB);

                // 6. Produce the final residual at the chosen resolution (not upsampled back).
                if (chosenResolution >= w)
                {
                    returnedResidual = fullResidual;
                    fullResidual = null;
                }
                else
                {
                    returnedResidual = Resample(fullResidual, chosenResolution, chosenResolution);
                    Release(fullResidual);
                    fullResidual = null;
                }

                // 7. Final stats at the chosen resolution: reconstruct with the chosen residual
                //    (upsampled to source for the error metric), reduce error + coverage.
                RenderTexture eval = returnedResidual;
                if (chosenResolution < w)
                {
                    eval = Resample(returnedResidual, w, h);
                }

                try
                {
                    RunErrorHeatmap(vcInterp, eval, baseLinear, heatmap, errorStat, coverageStat, 0f);
                    ReduceStats errorStats = ReduceStats(errorStat, w, h, avgA, avgB);
                    ReduceStats covStats = ReduceStats(coverageStat, w, h, avgA, avgB);
                    return new NamerDecompOutput(returnedResidual, BuildStats(errorStats, covStats, required: true, chosenResolution), this);
                }
                finally
                {
                    if (eval != returnedResidual)
                    {
                        Release(eval);
                    }
                }
            }
            finally
            {
                Release(vcInterp);
                Release(fullResidual);
                Release(heatmap);
                Release(errorStat);
                Release(coverageStat);
                Release(avgA);
                Release(avgB);
                DisposeBuffer(vertsBuf);
                DisposeBuffer(uvsBuf);
                DisposeBuffer(trisBuf);
                DisposeBuffer(colorsBuf);
            }
        }

        public void Dispose()
        {
            _pool.Dispose();
        }

        private int ChooseResolution(
            RenderTexture fullResidual,
            RenderTexture vcInterp,
            RenderTexture baseLinear,
            RenderTexture heatmap,
            RenderTexture errorStat,
            RenderTexture coverageStat,
            int w,
            int h,
            float errorThreshold,
            int manualResolution,
            RenderTexture avgA,
            RenderTexture avgB)
        {
            // Manual override (D-17): the POPUP INDEX resolves to a pixel size, clamped to
            // <= source, and skips the search entirely.
            if (manualResolution > 0)
            {
                int idx = Mathf.Clamp(manualResolution - 1, 0, ResolutionLadder.Length - 1);
                return Mathf.Min(ResolutionLadder[idx], w);
            }

            // Adaptive (D-16): walk the ladder largest -> smallest, skipping any step >=
            // source (full-res already covers it). Stop at the first violating step and keep
            // the previous (larger) passing step; fall back to full source when the first
            // step already violates or no step is <= source (e.g. a 4096 source where the
            // ladder tops out at 2048).
            int chosen = w;
            for (int i = 0; i < ResolutionLadder.Length; i++)
            {
                int r = ResolutionLadder[i];
                if (r >= w)
                {
                    continue;
                }

                float evalMaxError = EvaluateResolution(fullResidual, vcInterp, baseLinear, heatmap, errorStat, coverageStat, w, h, r, avgA, avgB);
                if (evalMaxError > errorThreshold)
                {
                    break;
                }

                chosen = r;
            }

            return chosen;
        }

        private float EvaluateResolution(
            RenderTexture fullResidual,
            RenderTexture vcInterp,
            RenderTexture baseLinear,
            RenderTexture heatmap,
            RenderTexture errorStat,
            RenderTexture coverageStat,
            int w,
            int h,
            int resolution,
            RenderTexture avgA,
            RenderTexture avgB)
        {
            RenderTexture down = Resample(fullResidual, resolution, resolution);
            RenderTexture up = Resample(down, w, h);
            Release(down);

            try
            {
                RunErrorHeatmap(vcInterp, up, baseLinear, heatmap, errorStat, coverageStat, 0f);
                ReduceStats stats = ReduceStats(errorStat, w, h, avgA, avgB);
                return stats.MaxError;
            }
            finally
            {
                Release(up);
            }
        }

        private void RunErrorHeatmap(
            RenderTexture vcInterp,
            RenderTexture residualIn,
            RenderTexture baseLinear,
            RenderTexture heatmap,
            RenderTexture errorStat,
            RenderTexture coverageStat,
            float fitOnly)
        {
            _compute.SetFloat("_FitOnly", fitOnly);
            _compute.SetTexture(_kernelErrorHeatmap, "_VcInterp", vcInterp);
            _compute.SetTexture(_kernelErrorHeatmap, "_ResidualOut", residualIn);
            _compute.SetTexture(_kernelErrorHeatmap, "_BaseLinear", baseLinear);
            _compute.SetTexture(_kernelErrorHeatmap, "_ErrorHeatmap", heatmap);
            _compute.SetTexture(_kernelErrorHeatmap, "_ErrorStat", errorStat);
            _compute.SetTexture(_kernelErrorHeatmap, "_CoverageStat", coverageStat);
        }

        private ReduceStats ReduceStats(RenderTexture src, int srcW, int srcH, RenderTexture avgA, RenderTexture avgB)
        {
            RenderTexture current = src;
            int curW = srcW;
            int curH = srcH;
            bool useAvgA = true;

            while (curW > 1 || curH > 1)
            {
                RenderTexture dst = useAvgA ? avgA : avgB;
                int dstW = (curW + ReduceDownsampleFactor - 1) / ReduceDownsampleFactor;
                int dstH = (curH + ReduceDownsampleFactor - 1) / ReduceDownsampleFactor;

                _compute.SetInts("_AvgSrcSize", new[] { curW, curH });
                _compute.SetInts("_AvgDstSize", new[] { dstW, dstH });
                _compute.SetTexture(_kernelReduce, "_SrcAvg", current);
                _compute.SetTexture(_kernelReduce, "_DstAvg", dst);
                Dispatch(_kernelReduce, dstW, dstH);

                current = dst;
                curW = dstW;
                curH = dstH;
                useAvgA = !useAvgA;
            }

            return ReadBackStats(current, curW, curH);
        }

        private static ReduceStats ReadBackStats(RenderTexture src, int validW, int validH)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("NAMER decomp stats readback failed.");
            }

            NativeArray<Color32> data = request.GetData<Color32>();
            try
            {
                int rowStride = src.width;
                float meanErr = 0f;
                float maxErr = 0f;
                float coverage = 0f;
                float minAlpha = 1f;
                int count = 0;
                for (int y = 0; y < validH; y++)
                {
                    for (int x = 0; x < validW; x++)
                    {
                        Color32 c = data[y * rowStride + x];
                        meanErr += c.r / 255f;
                        maxErr = Mathf.Max(maxErr, c.g / 255f);
                        coverage += c.b / 255f;
                        minAlpha = Mathf.Min(minAlpha, c.a / 255f);
                        count++;
                    }
                }

                return new ReduceStats
                {
                    MeanErr = count > 0 ? meanErr / count : 0f,
                    MaxError = maxErr,
                    CoverageFraction = count > 0 ? coverage / count : 0f,
                    MinAlpha = minAlpha,
                };
            }
            finally
            {
                data.Dispose();
            }
        }

        private static NamerDecompErrorStats BuildStats(ReduceStats error, ReduceStats coverage, bool required, int chosenResolution)
        {
            float fractionCovered = error.CoverageFraction;
            float avgError = fractionCovered > 0f ? error.MeanErr / fractionCovered : 0f;
            float coveragePct = fractionCovered > 0f ? coverage.MeanErr / fractionCovered : 0f;
            return new NamerDecompErrorStats
            {
                Coverage = coveragePct,
                AvgError = avgError,
                MaxError = error.MaxError,
                ResidualRequired = required,
                ChosenResolution = chosenResolution,
            };
        }

        private RenderTexture Resample(RenderTexture source, int dstW, int dstH)
        {
            RenderTexture dst = _pool.Lease(NewDescriptor(dstW, dstH, GraphicsFormat.R16G16B16A16_SFloat));
            Graphics.Blit(source, dst, NamerComputePipeline.RawCopyMaterial());
            return dst;
        }

        private static ComputeBuffer CreateBuffer<T>(T[] data, int count, int stride) where T : struct
        {
            ComputeBuffer buffer = new ComputeBuffer(count, stride);
            buffer.SetData(data);
            return buffer;
        }

        private void Dispatch(int kernel, int w, int h)
        {
            _compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }

        private void Release(RenderTexture rt)
        {
            _pool.Release(rt);
        }

        private static void DisposeBuffer(ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Dispose();
            }
        }

        private static RenderTextureDescriptor NewDescriptor(int width, int height, GraphicsFormat format)
        {
            return new RenderTextureDescriptor(width, height, format, 0)
            {
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            };
        }

        private static float[] ToFloat2(Vector2[] uvs)
        {
            float[] result = new float[uvs.Length * 2];
            for (int i = 0; i < uvs.Length; i++)
            {
                result[i * 2] = uvs[i].x;
                result[i * 2 + 1] = uvs[i].y;
            }

            return result;
        }

        private static float[] ToFloat3(Vector3[] verts)
        {
            float[] result = new float[verts.Length * 3];
            for (int i = 0; i < verts.Length; i++)
            {
                result[i * 3] = verts[i].x;
                result[i * 3 + 1] = verts[i].y;
                result[i * 3 + 2] = verts[i].z;
            }

            return result;
        }

        private static float[] ToFloat4(Color32[] colors)
        {
            float[] result = new float[colors.Length * 4];
            for (int i = 0; i < colors.Length; i++)
            {
                result[i * 4] = colors[i].r / 255f;
                result[i * 4 + 1] = colors[i].g / 255f;
                result[i * 4 + 2] = colors[i].b / 255f;
                result[i * 4 + 3] = colors[i].a / 255f;
            }

            return result;
        }

        private static int[] FlattenTriangles(int[][] subMeshTriangles)
        {
            if (subMeshTriangles == null)
            {
                return Array.Empty<int>();
            }

            int totalTriangles = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub != null)
                {
                    totalTriangles += sub.Length / 3;
                }
            }

            int[] tris = new int[totalTriangles * 3];
            int t = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub == null)
                {
                    continue;
                }

                for (int i = 0; i + 2 < sub.Length; i += 3)
                {
                    tris[t * 3] = sub[i];
                    tris[t * 3 + 1] = sub[i + 1];
                    tris[t * 3 + 2] = sub[i + 2];
                    t++;
                }
            }

            return tris;
        }

        private struct ReduceStats
        {
            public float MeanErr;
            public float MaxError;
            public float CoverageFraction;
            public float MinAlpha;
        }
    }
}

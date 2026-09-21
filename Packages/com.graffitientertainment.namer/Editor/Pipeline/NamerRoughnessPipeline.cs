using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// GPU dispatch harness for the NAMER image-space roughness dip-source stage (Phase 04.2).
    /// The removed-luminance roughness transfer (<see cref="ExtractTransferRoughness"/>) is the
    /// PRIMARY dip source: the signed Rec.601 luminance of (source - projected) is p90-normalized
    /// and fed through the surviving D-08 consume site <c>saturate(scalar - strength * mag)</c>.
    /// The Sobel chain (<see cref="ExtractSobel"/>) survives as the FALLBACK alternate dip source
    /// — used when decomposition is off (or CR-01-guarded) or when the user selects Sobel Edge.
    ///
    /// The 04.1 fit-driven machinery is RETIRED (plan 03): the strength ladder search, the fit
    /// cache, the frequency-separation (blur/sharp) kernels, the sharp-removal cleaned base, and
    /// the strength-ladder fitter no longer exist. The dip-depth taste slider owns the dip
    /// magnitude end to end — no precompute, no search.
    ///
    /// All render targets are declared with <see cref="GraphicsFormat"/> (intermediates
    /// <see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear; the roughness output
    /// <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear), never sRGB, so compute can write
    /// them directly.
    /// </summary>
    public sealed class NamerRoughnessPipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERRoughness.compute";

        private const int AverageDownsampleFactor = 8;

        // Robust Sobel normalization (UAT round 4): the normalization scale is the p90 of the
        // Sobel magnitude over a fixed-size subsample, not the global max — see RobustSobelScale.
        private const int SobelScaleSubsampleSize = 256;
        private const float SobelScalePercentile = 0.9f;

        // Removed-luminance transfer normalization (04.2): the p90 of |removed-luma| over a
        // fixed-size subsample, floored at 1e-4 so a flat projection yields scalar passthrough
        // (T-04.2-05) — see RemovedLumaP90.
        private const int LumaScaleSubsampleSize = 256;
        private const float LumaScalePercentile = 0.9f;
        private const float LumaScaleFloor = 1e-4f;

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelSobel;
        private readonly int _kernelMaxReduce;
        private readonly int _kernelNormalize;
        private readonly int _kernelRemovedLuma;
        private readonly int _kernelTransferRemap;

        public NamerRoughnessPipeline()
        {
            _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            if (_compute == null)
            {
                throw new InvalidOperationException("NAMER roughness compute shader not found at " + ComputeShaderPath);
            }

            _kernelSobel = _compute.FindKernel("CSRoughnessSobel");
            _kernelMaxReduce = _compute.FindKernel("CSRoughnessMaxReduce");
            _kernelNormalize = _compute.FindKernel("CSRoughnessNormalize");
            _kernelRemovedLuma = _compute.FindKernel("CSRemovedLuma");
            _kernelTransferRemap = _compute.FindKernel("CSRoughnessTransferRemap");
        }

        /// <summary>
        /// Returns an extracted-roughness target to this pipeline's pool. No-ops for null.
        /// </summary>
        public void ReleaseRoughness(RenderTexture roughness)
        {
            _pool.Release(roughness);
        }

        public void Dispose()
        {
            _pool.Dispose();
        }

        // ------------------------------------------------------------------
        // Sobel (plan-01) fallback dip-source path
        // ------------------------------------------------------------------

        /// <summary>
        /// Extracts image-space roughness from <paramref name="baseColorOut"/> (the
        /// already-linear cleaned base produced by <c>CSNormalize</c>) using the Blender-parity
        /// Sobel estimator (CSRoughnessSobel -> max-reduce -> CSRoughnessNormalize). This is the
        /// ALTERNATE dip source (04.2): the returned map is direct <c>mag/p90</c> in <c>.r</c>,
        /// and <c>CSSurfacePack</c> applies the anchored-inverted dip
        /// <c>saturate(scalar - strength * map.r)</c> at pack time. The returned target is
        /// pool-leased; the caller owns it and must release via <see cref="ReleaseRoughness"/>.
        /// </summary>
        public RenderTexture ExtractSobel(RenderTexture baseColorOut, int w, int h)
        {
            if (baseColorOut == null)
            {
                throw new ArgumentNullException(nameof(baseColorOut));
            }

            RenderTextureDescriptor intermediate = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
            RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);
            int avgW = (w + AverageDownsampleFactor - 1) / AverageDownsampleFactor;
            int avgH = (h + AverageDownsampleFactor - 1) / AverageDownsampleFactor;
            RenderTextureDescriptor avgDesc = NewDescriptor(avgW, avgH, GraphicsFormat.R16G16B16A16_SFloat);

            RenderTexture roughnessRaw = null;
            RenderTexture avgA = null;
            RenderTexture avgB = null;
            RenderTexture roughnessOut = null;

            try
            {
                roughnessRaw = _pool.Lease(intermediate);
                avgA = _pool.Lease(avgDesc);
                avgB = _pool.Lease(avgDesc);
                roughnessOut = _pool.Lease(unorm8);

                _compute.SetInts("_Size", new[] { w, h });

                // 1. Sobel edge magnitude -> _RoughnessRaw.g.
                _compute.SetTexture(_kernelSobel, "_BaseColorOut", baseColorOut);
                _compute.SetTexture(_kernelSobel, "_RoughnessRaw", roughnessRaw);
                Dispatch(_kernelSobel, w, h);

                // 2. Hierarchical max-reduce -> true global Sobel max, then the robust p90 scale
                //    that keeps smooth regions matte when a few extreme edges would otherwise
                //    own the normalization (UAT round 4; deliberate np.max parity deviation).
                float trueMax = ReduceToGlobalMax(roughnessRaw, w, h, avgA, avgB);
                float robustScale = RobustSobelScale(roughnessRaw, trueMax);

                // 3. Normalize: roughness = saturate(mag / robustScale), in the roughness domain.
                _compute.SetFloat("_GlobalMax", robustScale);
                _compute.SetTexture(_kernelNormalize, "_RoughnessRaw", roughnessRaw);
                _compute.SetTexture(_kernelNormalize, "_RoughnessOut", roughnessOut);
                Dispatch(_kernelNormalize, w, h);

                return roughnessOut;
            }
            catch
            {
                // WR-03: the lease this method RETURNS must not leak when a Sobel/reduce/
                // normalize stage throws — finally only owns the intermediates.
                if (roughnessOut != null)
                {
                    Release(roughnessOut);
                }
                throw;
            }
            finally
            {
                Release(roughnessRaw);
                Release(avgA);
                Release(avgB);
            }
        }

        // ------------------------------------------------------------------
        // Removed-luminance transfer (plan 04.2-02) path
        // ------------------------------------------------------------------

        /// <summary>
        /// Produces the 04.2 roughness-transfer map: the signed Rec.601 removed-luminance of
        /// <paramref name="sourceBase"/> minus <paramref name="projectedBase"/> is p90-normalized
        /// and fed through the surviving D-08 consume site
        /// <c>saturate(scalarRoughness - strength * removedLuma / p90)</c>. Bright removed detail
        /// (positive luma) dips toward gloss; dark removed occlusion (negative luma) raises toward
        /// matte (locked 2026-09-16 polarity decision). Strength 0 — or a flat projection — yields
        /// the authored scalar exactly.
        ///
        /// The returned target is pool-leased (UNorm8 linear); the caller owns it and must release
        /// via <see cref="ReleaseRoughness"/>. The two input targets are only ever bound read-only
        /// and are never released here.
        /// </summary>
        public RenderTexture ExtractTransferRoughness(
            float scalarRoughness,
            RenderTexture sourceBase,
            RenderTexture projectedBase,
            int w,
            int h,
            float strength)
        {
            if (sourceBase == null)
            {
                throw new ArgumentNullException(nameof(sourceBase));
            }

            if (projectedBase == null)
            {
                throw new ArgumentNullException(nameof(projectedBase));
            }

            RenderTextureDescriptor lumaDesc = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
            RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);

            RenderTexture lumaRT = null;
            RenderTexture roughnessOut = null;

            try
            {
                lumaRT = _pool.Lease(lumaDesc);
                roughnessOut = _pool.Lease(unorm8);

                _compute.SetInts("_Size", new[] { w, h });

                // 1. Signed removed-luma (source - projected) -> _RemovedLuma.r.
                _compute.SetTexture(_kernelRemovedLuma, "_BaseColorOut", sourceBase);
                _compute.SetTexture(_kernelRemovedLuma, "_ProjectedBase", projectedBase);
                _compute.SetTexture(_kernelRemovedLuma, "_RemovedLuma", lumaRT);
                Dispatch(_kernelRemovedLuma, w, h);

                // 2. p90 of |removed-luma| — the normalization scale (RobustSobelScale precedent).
                float p90 = RemovedLumaP90(lumaRT);

                // 3. Transfer remap: roughness = saturate(scalar - strength * removedLuma / p90).
                _compute.SetFloat("_ScalarRoughness", scalarRoughness);
                _compute.SetFloat("_Strength", strength);
                _compute.SetFloat("_LumaP90", p90);
                _compute.SetTexture(_kernelTransferRemap, "_RemovedLuma", lumaRT);
                _compute.SetTexture(_kernelTransferRemap, "_RoughnessOut", roughnessOut);
                Dispatch(_kernelTransferRemap, w, h);

                return roughnessOut;
            }
            catch
            {
                // WR-03: the lease this method RETURNS must not leak when a stage throws —
                // the finally only owns the removed-luma intermediate.
                Release(roughnessOut);
                throw;
            }
            finally
            {
                Release(lumaRT);
            }
        }

        /// <summary>
        /// p90 normalization scale for the removed-luminance transfer: the p90 of |removed-luma|
        /// read back from a <see cref="LumaScaleSubsampleSize"/>² blit subsample of the signed
        /// luma RT and floored at <see cref="LumaScaleFloor"/>. Follows the
        /// <see cref="RobustSobelScale"/> precedent (research measured |luma| p90 = 0.173 on Neo,
        /// so strength 1.0 reaches full-clip only on the top decile of detail). Unlike the Sobel
        /// scale there is NO trueMax ceiling — removed-luma has no precomputed true max and the
        /// <c>saturate</c> in the remap kernel owns the clip. A flat projection (all-zero luma)
        /// returns exactly <see cref="LumaScaleFloor"/>, never zero, so flat inputs cannot amplify
        /// noise or divide by zero (T-04.2-05).
        /// </summary>
        private float RemovedLumaP90(RenderTexture luma)
        {
            RenderTexture subsample = null;
            try
            {
                subsample = _pool.Lease(NewDescriptor(
                    LumaScaleSubsampleSize, LumaScaleSubsampleSize, GraphicsFormat.R32G32B32A32_SFloat));
                Graphics.Blit(luma, subsample);

                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(subsample, 0, TextureFormat.RGBAFloat);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    throw new InvalidOperationException("NAMER roughness removed-luma p90 readback failed.");
                }

                NativeArray<float> data = request.GetData<float>();
                try
                {
                    int texelCount = data.Length / 4;
                    float[] magnitudes = new float[texelCount];
                    int detailCount = 0;
                    for (int i = 0; i < texelCount; i++)
                    {
                        // RGBAFloat is 4 floats per texel, row-major; the signed removed-luma is in .r.
                        // CSProjectBase passes the source through outside the UV islands, so
                        // uncovered atlas texels are exactly zero — they are NOT removed detail.
                        // A sparse layout would otherwise flood the p90 with zeros, collapse the
                        // scale to the floor, and clamp nearly every covered texel to full
                        // gloss/matte (Codex PR #1 review); the percentile runs over detail
                        // texels only — "the top decile of detail" the doc above promises.
                        float magnitude = Mathf.Abs(data[i * 4]);
                        if (magnitude > 0f)
                        {
                            magnitudes[detailCount++] = magnitude;
                        }
                    }

                    // Flat projection (or detail so sparse the subsample missed it): no
                    // nonzero texels, so the scale is the floor — never zero, keeping the
                    // T-04.2-05 divide-by-zero guarantee.
                    if (detailCount == 0)
                    {
                        return LumaScaleFloor;
                    }

                    Array.Sort(magnitudes, 0, detailCount);
                    int percentileIndex = Mathf.Clamp(
                        Mathf.FloorToInt(detailCount * LumaScalePercentile), 0, detailCount - 1);
                    return Mathf.Max(magnitudes[percentileIndex], LumaScaleFloor);
                }
                finally
                {
                    data.Dispose();
                }
            }
            finally
            {
                Release(subsample);
            }
        }

        // ------------------------------------------------------------------
        // Shared reduction + dispatch helpers (plan-01 Sobel reduce)
        // ------------------------------------------------------------------

        private float ReduceToGlobalMax(RenderTexture roughnessRaw, int w, int h, RenderTexture avgA, RenderTexture avgB)
        {
            RenderTexture src = roughnessRaw;
            int srcW = w;
            int srcH = h;
            bool useAvgA = true;

            while (srcW > 1 || srcH > 1)
            {
                RenderTexture dst = useAvgA ? avgA : avgB;
                int dstW = (srcW + AverageDownsampleFactor - 1) / AverageDownsampleFactor;
                int dstH = (srcH + AverageDownsampleFactor - 1) / AverageDownsampleFactor;

                _compute.SetInts("_AvgSrcSize", new[] { srcW, srcH });
                _compute.SetInts("_AvgDstSize", new[] { dstW, dstH });
                _compute.SetTexture(_kernelMaxReduce, "_SrcAvg", src);
                _compute.SetTexture(_kernelMaxReduce, "_DstAvg", dst);
                Dispatch(_kernelMaxReduce, dstW, dstH);

                src = dst;
                srcW = dstW;
                srcH = dstH;
                useAvgA = !useAvgA;
            }

            return ReadBackGlobalMax(src, srcW, srcH);
        }

        private static float ReadBackGlobalMax(RenderTexture src, int validWidth, int validHeight)
        {
            // The reduced RT is tiny (<= 64 valid texels); a single blocking RGBAFloat readback
            // of a constant-bound buffer is the intended one-shot scalar reduction, not a
            // per-pixel C# loop over the full-resolution texture (NORM-03). Only the top-left
            // validWidth x validHeight region holds the reduction result — the over-allocated
            // reduce target's remaining texels are stale and must be ignored.
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(src, 0, TextureFormat.RGBAFloat);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("NAMER roughness global-max readback failed.");
            }

            NativeArray<float> data = request.GetData<float>();
            try
            {
                float globalMax = 0f;
                for (int y = 0; y < validHeight; y++)
                {
                    for (int x = 0; x < validWidth; x++)
                    {
                        // RGBAFloat is 4 floats per texel, row-major; the reduce stores the max in .g.
                        int index = (y * src.width + x) * 4 + 1;
                        globalMax = Mathf.Max(globalMax, data[index]);
                    }
                }

                return globalMax;
            }
            finally
            {
                data.Dispose();
            }
        }

        /// <summary>
        /// Robust Sobel normalization scale: the p90 of the Sobel magnitude, read back from a
        /// <see cref="SobelScaleSubsampleSize"/>² blit subsample of the raw magnitude and
        /// clamped to (1e-4, trueMax). Blender's np.max normalization lets a few extreme edges
        /// set the scale, collapsing the smooth majority of heavy-tailed AI albedo textures to
        /// mirror gloss (UAT round 4 — accepted parity deviation). The 1e-4 floor keeps a flat
        /// base at ~0 roughness (a percentile of all-zero magnitudes must not divide by zero);
        /// the trueMax ceiling is belt-and-braces (a percentile of a distribution never exceeds
        /// its max, and it keeps an all-uniform texture normalizing to exactly 1).
        /// </summary>
        private float RobustSobelScale(RenderTexture roughnessRaw, float trueMax)
        {
            RenderTexture subsample = null;
            try
            {
                subsample = _pool.Lease(NewDescriptor(
                    SobelScaleSubsampleSize, SobelScaleSubsampleSize, GraphicsFormat.R32G32B32A32_SFloat));
                Graphics.Blit(roughnessRaw, subsample);

                AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(subsample, 0, TextureFormat.RGBAFloat);
                request.WaitForCompletion();
                if (request.hasError)
                {
                    throw new InvalidOperationException("NAMER roughness p90-scale readback failed.");
                }

                NativeArray<float> data = request.GetData<float>();
                try
                {
                    int texelCount = data.Length / 4;
                    float[] magnitudes = new float[texelCount];
                    for (int i = 0; i < texelCount; i++)
                    {
                        // RGBAFloat is 4 floats per texel, row-major; the Sobel magnitude is in .g.
                        magnitudes[i] = data[i * 4 + 1];
                    }

                    Array.Sort(magnitudes);
                    int percentileIndex = Mathf.Clamp(
                        Mathf.FloorToInt(texelCount * SobelScalePercentile), 0, texelCount - 1);
                    return Mathf.Clamp(magnitudes[percentileIndex], 1e-4f, Mathf.Max(trueMax, 1e-4f));
                }
                finally
                {
                    data.Dispose();
                }
            }
            finally
            {
                Release(subsample);
            }
        }

        private void Dispatch(int kernel, int w, int h)
        {
            _compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }

        private void Release(RenderTexture rt)
        {
            _pool.Release(rt);
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
    }
}

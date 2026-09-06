using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// GPU dispatch harness for the NAMER image-space roughness extraction stage
    /// (Phase 04.1, plan 01). Turns the already-cleaned linear base color (the
    /// <c>CSNormalize</c> output, post AO un-multiply) into a per-texel roughness texture
    /// (roughness in the red channel, linear) by dispatching the three staged kernels in
    /// <c>Compute/NAMERRoughness.compute</c> — never per-pixel C#.
    ///
    /// The Sobel estimator mirrors Blender's <c>extract_roughness</c>: Rec.601 luminance ->
    /// 3x3 Sobel edge magnitude -> global-max normalization (Blender's <c>np.max</c>).
    /// The result is returned pool-leased and must be released by the caller via
    /// <see cref="ReleaseRoughness"/>.
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

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelSobel;
        private readonly int _kernelMaxReduce;
        private readonly int _kernelNormalize;

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
        }

        /// <summary>
        /// Extracts image-space Sobel roughness from <paramref name="baseColorOut"/> (the
        /// already-linear cleaned base produced by <c>CSNormalize</c>) and returns a
        /// pool-leased <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear render target with
        /// the global-max-normalized roughness in the red channel. The caller owns only the
        /// returned target and must release it via <see cref="ReleaseRoughness"/>.
        /// </summary>
        public RenderTexture ExtractRoughness(NamerMaterialInspection inspection, RenderTexture baseColorOut, int w, int h)
        {
            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

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

                // 2. Hierarchical max-reduce -> global Sobel max (Blender np.max normalization).
                float globalMax = ReduceToGlobalMax(roughnessRaw, w, h, avgA, avgB);

                // 3. Normalize: roughness = saturate(mag / globalMax), in the roughness domain.
                _compute.SetFloat("_GlobalMax", globalMax);
                _compute.SetTexture(_kernelNormalize, "_RoughnessRaw", roughnessRaw);
                _compute.SetTexture(_kernelNormalize, "_RoughnessOut", roughnessOut);
                Dispatch(_kernelNormalize, w, h);

                return roughnessOut;
            }
            finally
            {
                Release(roughnessRaw);
                Release(avgA);
                Release(avgB);
            }
        }

        /// <summary>
        /// Returns an extracted roughness target to this pipeline's pool. No-ops for null.
        /// </summary>
        public void ReleaseRoughness(RenderTexture roughness)
        {
            _pool.Release(roughness);
        }

        public void Dispose()
        {
            _pool.Dispose();
        }

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

using System;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// GPU dispatch harness for the NAMER image-space AO extraction stage (Phase 03.1,
    /// plan 01). Turns the already-uploaded linear/raw base-color render target into an
    /// extracted ambient-occlusion texture (AO in the green channel, linear) by dispatching
    /// the five staged kernels in <c>Compute/NAMERAO.compute</c> — never per-pixel C#.
    ///
    /// The extraction is the automatic D-07 path when a source material has no authored
    /// <c>_OcclusionMap</c>: CSLuminance -> hierarchical block-average (overflow-free) ->
    /// separable gaussian low-pass -> AO remap. The result is returned pool-leased and must
    /// be released by the caller via <see cref="ReleaseAo"/>.
    ///
    /// All render targets are declared with <see cref="GraphicsFormat"/> (intermediates
    /// <see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear; the AO output
    /// <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear), never sRGB, so compute can write
    /// them directly.
    /// </summary>
    public sealed class NamerAOPipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute";

        private const int AverageDownsampleFactor = 8;
        private const int MinLowPassRadius = 8;
        private const int MaxLowPassRadius = 64;
        private const int LowPassRadiusDivisor = 32;
        private const float LowPassSigmaDivisor = 3.0f;
        private const float IdentityAoStrength = 1.0f;
        private const float IdentityAoContrast = 1.0f;

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelLuminance;
        private readonly int _kernelAverage;
        private readonly int _kernelBlurH;
        private readonly int _kernelBlurV;
        private readonly int _kernelAoRemap;

        public NamerAOPipeline()
        {
            _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            if (_compute == null)
            {
                throw new InvalidOperationException("NAMER AO compute shader not found at " + ComputeShaderPath);
            }

            _kernelLuminance = _compute.FindKernel("CSLuminance");
            _kernelAverage = _compute.FindKernel("CSAverage");
            _kernelBlurH = _compute.FindKernel("CSBlurH");
            _kernelBlurV = _compute.FindKernel("CSBlurV");
            _kernelAoRemap = _compute.FindKernel("CSAoRemap");
        }

        /// <summary>
        /// Extracts image-space AO from <paramref name="baseColorIn"/> (the linear/raw base
        /// already uploaded by <see cref="NamerComputePipeline"/>) and returns a pool-leased
        /// <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear render target with AO in the
        /// green channel. The caller owns only the returned target and must release it via
        /// <see cref="ReleaseAo"/>.
        /// </summary>
        public RenderTexture Extract(NamerMaterialInspection inspection, RenderTexture baseColorIn, int w, int h)
        {
            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

            if (baseColorIn == null)
            {
                throw new ArgumentNullException(nameof(baseColorIn));
            }

            int radius = Mathf.Clamp(Mathf.Max(w, h) / LowPassRadiusDivisor, MinLowPassRadius, MaxLowPassRadius);
            float sigma = radius / LowPassSigmaDivisor;

            RenderTextureDescriptor intermediate = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
            RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);
            int avgW = (w + AverageDownsampleFactor - 1) / AverageDownsampleFactor;
            int avgH = (h + AverageDownsampleFactor - 1) / AverageDownsampleFactor;
            RenderTextureDescriptor avgDesc = NewDescriptor(avgW, avgH, GraphicsFormat.R16G16B16A16_SFloat);

            RenderTexture luma = null;
            RenderTexture blurA = null;
            RenderTexture blurB = null;
            RenderTexture avgA = null;
            RenderTexture avgB = null;
            RenderTexture aoOut = null;

            try
            {
                luma = _pool.Lease(intermediate);
                blurA = _pool.Lease(intermediate);
                blurB = _pool.Lease(intermediate);
                avgA = _pool.Lease(avgDesc);
                avgB = _pool.Lease(avgDesc);
                aoOut = _pool.Lease(unorm8);

                _compute.SetInts("_Size", new[] { w, h });
                _compute.SetFloat("_SourceIsSrgb", inspection.BaseMapIsSrgb ? 1f : 0f);

                // 1. Luminance (linear-space Rec.709) from the uploaded base.
                _compute.SetTexture(_kernelLuminance, "_BaseColorIn", baseColorIn);
                _compute.SetTexture(_kernelLuminance, "_Luma", luma);
                Dispatch(_kernelLuminance, w, h);

                // 2. Hierarchical block-average -> scalar mean of a tiny (<= 64 texel) RT.
                float lumaAverage = ReduceToScalarMean(luma, w, h, avgA, avgB);

                // 3. Separable gaussian low-pass over the luminance.
                _compute.SetInt("_Radius", radius);
                _compute.SetFloat("_Sigma", sigma);
                _compute.SetFloat("_AoStrength", IdentityAoStrength);
                _compute.SetFloat("_AoContrast", IdentityAoContrast);

                _compute.SetTexture(_kernelBlurH, "_Src", luma);
                _compute.SetTexture(_kernelBlurH, "_Dst", blurA);
                Dispatch(_kernelBlurH, w, h);

                _compute.SetTexture(_kernelBlurV, "_Src", blurA);
                _compute.SetTexture(_kernelBlurV, "_Dst", blurB);
                Dispatch(_kernelBlurV, w, h);

                // 4. AO remap -> green-channel output (the _AoIn.g contract).
                _compute.SetFloat("_LumaAverage", lumaAverage);
                _compute.SetTexture(_kernelAoRemap, "_Dst", blurB);
                _compute.SetTexture(_kernelAoRemap, "_AoOut", aoOut);
                Dispatch(_kernelAoRemap, w, h);

                return aoOut;
            }
            finally
            {
                Release(luma);
                Release(blurA);
                Release(blurB);
                Release(avgA);
                Release(avgB);
            }
        }

        /// <summary>
        /// Returns an extracted AO target to this pipeline's pool. No-ops for null.
        /// </summary>
        public void ReleaseAo(RenderTexture ao)
        {
            _pool.Release(ao);
        }

        public void Dispose()
        {
            _pool.Dispose();
        }

        private float ReduceToScalarMean(RenderTexture luma, int w, int h, RenderTexture avgA, RenderTexture avgB)
        {
            RenderTexture src = luma;
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
                _compute.SetTexture(_kernelAverage, "_SrcAvg", src);
                _compute.SetTexture(_kernelAverage, "_DstAvg", dst);
                Dispatch(_kernelAverage, dstW, dstH);

                src = dst;
                srcW = dstW;
                srcH = dstH;
                useAvgA = !useAvgA;
            }

            return ReadBackLumaAverage(src);
        }

        private static float ReadBackLumaAverage(RenderTexture src)
        {
            // The reduced RT is tiny (<= 64 texels); a single blocking readback of a
            // constant-bound buffer is the intended one-shot scalar reduction, not a
            // per-pixel C# loop over the full-resolution texture (NORM-03).
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("NAMER AO average readback failed.");
            }

            NativeArray<Color32> data = request.GetData<Color32>();
            float sum = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                sum += data[i].r / 255.0f;
            }

            return sum / data.Length;
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

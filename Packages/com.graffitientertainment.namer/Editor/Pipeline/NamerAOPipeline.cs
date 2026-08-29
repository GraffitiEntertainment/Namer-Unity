using System;
using System.Collections.Generic;
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
        private const int kSeamDilatePx = 16;

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelLuminance;
        private readonly int _kernelAverage;
        private readonly int _kernelBlurH;
        private readonly int _kernelBlurV;
        private readonly int _kernelAoRemap;
        private readonly int _kernelJumpFloodInit;
        private readonly int _kernelJumpFloodStep;

        // In-memory bake cache keyed by (low mesh id, resolved occluder id, w, h). The
        // cached Texture2D holds the dilated AO in the green channel (linear), reused on
        // subsequent Process calls so the bake never re-runs on the debounce tick (D-07).
        private readonly Dictionary<(int low, int occ, int w, int h), Texture2D> _bakeCache =
            new Dictionary<(int, int, int, int), Texture2D>();

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
            _kernelJumpFloodInit = _compute.FindKernel("CSJumpFloodInit");
            _kernelJumpFloodStep = _compute.FindKernel("CSJumpFloodStep");
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
            ClearBakeCache();
            _pool.Dispose();
        }

        /// <summary>
        /// True when a geometry bake for <paramref name="lowMesh"/>/<paramref name="occluder"/>
        /// at <paramref name="w"/>×<paramref name="h"/> is already cached (D-07). A null
        /// <paramref name="lowMesh"/> always reports false (no bake possible).
        /// </summary>
        public bool HasCachedBake(Mesh lowMesh, Mesh occluder, int w, int h)
        {
            if (lowMesh == null)
            {
                return false;
            }

            Mesh resolved = ResolveOccluder(lowMesh, occluder);
            return _bakeCache.ContainsKey(MakeKey(lowMesh, resolved, w, h));
        }

        /// <summary>
        /// Returns a pool-leased linear RT with the geometry-baked AO in the green channel,
        /// upsampled to <paramref name="w"/>×<paramref name="h"/> and dilated 16 px at UV
        /// seams (D-05). A cached bake is re-uploaded through the raw-copy path; otherwise a
        /// fresh bake runs at <c>min(w, h, 512)</c>. The occluder falls back to the selected
        /// mesh when null/invalid (D-06) and never throws. Returns null when no bake source
        /// exists. The caller releases the returned target via <see cref="ReleaseAo"/>.
        /// </summary>
        public RenderTexture BakeAndUpload(NamerMaterialInspection inspection, int w, int h)
        {
            if (inspection == null || inspection.BakeSourceMesh == null)
            {
                return null;
            }

            Mesh low = inspection.BakeSourceMesh;
            Mesh occluder = ResolveOccluder(low, inspection.OccluderMesh);
            (int low, int occ, int w, int h) key = MakeKey(low, occluder, w, h);

            if (_bakeCache.TryGetValue(key, out Texture2D cached))
            {
                RenderTexture cachedRt = _pool.Lease(NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm));
                NamerComputePipeline.Upload(cached, cachedRt, null);
                return cachedRt;
            }

            int bakeRes = Mathf.Min(Mathf.Min(w, h), NamerAOBaker.kBakeResolutionCap);
            NamerAOBakeResult baked = NamerAOBaker.Bake(
                low,
                occluder,
                bakeRes,
                NamerAOBaker.kCageOffset,
                NamerAOBaker.kMaxDistanceFactor,
                NamerAOBaker.kRayCount);

            RenderTexture seed = null;
            RenderTexture jfaA = null;
            RenderTexture jfaB = null;
            RenderTexture result = null;
            Texture2D lowResTex = null;

            try
            {
                lowResTex = BakeResultToTexture(baked, bakeRes);
                baked.Ao.Dispose();

                seed = _pool.Lease(NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm));
                jfaA = _pool.Lease(NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm));
                jfaB = _pool.Lease(NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm));

                // Bilinear upsample low-res bake -> full-res seed (Graphics.Blit resamples).
                NamerComputePipeline.Upload(lowResTex, seed, null);

                _compute.SetInts("_Size", new[] { w, h });
                _compute.SetInt("_MaxDilate", kSeamDilatePx);

                _compute.SetTexture(_kernelJumpFloodInit, "_Seed", seed);
                _compute.SetTexture(_kernelJumpFloodInit, "_Jfa", jfaA);
                Dispatch(_kernelJumpFloodInit, w, h);

                RenderTexture src = jfaA;
                RenderTexture dst = jfaB;
                int maxDim = Mathf.Max(w, h);
                if (maxDim > 1)
                {
                    int passes = Mathf.CeilToInt(Mathf.Log(maxDim, 2f));
                    int step = 1 << (passes - 1);
                    for (; step >= 1; step >>= 1)
                    {
                        _compute.SetInt("_Step", step);
                        _compute.SetTexture(_kernelJumpFloodStep, "_Jfa", src);
                        _compute.SetTexture(_kernelJumpFloodStep, "_JfaOut", dst);
                        Dispatch(_kernelJumpFloodStep, w, h);

                        RenderTexture tmp = src;
                        src = dst;
                        dst = tmp;
                    }
                }

                result = src;

                StoreBake(key, ReadBackToTexture(result, w, h));
                return result;
            }
            finally
            {
                if (lowResTex != null)
                {
                    UnityEngine.Object.DestroyImmediate(lowResTex);
                }

                _pool.Release(seed);
                if (jfaA != null && jfaA != result)
                {
                    _pool.Release(jfaA);
                }

                if (jfaB != null && jfaB != result)
                {
                    _pool.Release(jfaB);
                }
            }
        }

        /// <summary>
        /// Bakes (or re-uses a cached bake) and caches the result, then invokes
        /// <paramref name="onComplete"/>. Runs synchronously as an explicit one-time action —
        /// never on the 300 ms debounce tick (the 03.1-03 window calls this off the recompute
        /// path). No-ops (and still invokes the callback) when a bake is already cached.
        /// </summary>
        public void RequestBake(NamerMaterialInspection inspection, int w, int h, Action onComplete)
        {
            if (inspection == null || inspection.BakeSourceMesh == null)
            {
                onComplete?.Invoke();
                return;
            }

            Mesh low = inspection.BakeSourceMesh;
            Mesh occluder = ResolveOccluder(low, inspection.OccluderMesh);
            (int low, int occ, int w, int h) key = MakeKey(low, occluder, w, h);

            if (_bakeCache.ContainsKey(key))
            {
                onComplete?.Invoke();
                return;
            }

            RenderTexture baked = BakeAndUpload(inspection, w, h);
            if (baked != null)
            {
                ReleaseAo(baked);
            }

            onComplete?.Invoke();
        }

        /// <summary>Drops every cached bake texture (used by tests and <see cref="Dispose"/>).</summary>
        public void ClearBakeCache()
        {
            foreach (Texture2D tex in _bakeCache.Values)
            {
                if (tex != null)
                {
                    UnityEngine.Object.DestroyImmediate(tex);
                }
            }

            _bakeCache.Clear();
        }

        private static Mesh ResolveOccluder(Mesh low, Mesh occluder)
        {
            return occluder != null && occluder.vertexCount > 0 ? occluder : low;
        }

        private static (int low, int occ, int w, int h) MakeKey(Mesh low, Mesh occluder, int w, int h)
        {
            return (low.GetInstanceID(), occluder.GetInstanceID(), w, h);
        }

        private void StoreBake((int low, int occ, int w, int h) key, Texture2D texture)
        {
            if (_bakeCache.TryGetValue(key, out Texture2D old) && old != null)
            {
                UnityEngine.Object.DestroyImmediate(old);
            }

            _bakeCache[key] = texture;
        }

        private static Texture2D BakeResultToTexture(NamerAOBakeResult baked, int size)
        {
            NativeArray<Color32> pixels = new NativeArray<Color32>(size * size, Allocator.Temp);
            try
            {
                for (int i = 0; i < size * size; i++)
                {
                    float ao = baked.Ao[i];
                    if (ao >= 0f)
                    {
                        byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(ao * 255f), 0, 255);
                        pixels[i] = new Color32(0, g, 0, 255);
                    }
                    else
                    {
                        // Uncovered texel: white AO (no occlusion), marked invalid for JFA.
                        pixels[i] = new Color32(0, 255, 0, 0);
                    }
                }

                Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                tex.LoadRawTextureData(pixels);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                pixels.Dispose();
            }
        }

        private static Texture2D ReadBackToTexture(RenderTexture rt, int w, int h)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("NAMER AO bake readback failed.");
            }

            NativeArray<Color32> data = request.GetData<Color32>();
            try
            {
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false, true)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                tex.LoadRawTextureData(data);
                tex.Apply(false, false);
                return tex;
            }
            finally
            {
                data.Dispose();
            }
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

            return ReadBackLumaAverage(src, srcW, srcH);
        }

        private static float ReadBackLumaAverage(RenderTexture src, int validWidth, int validHeight)
        {
            // The reduced RT is tiny (<= 64 valid texels); a single blocking readback of
            // a constant-bound buffer is the intended one-shot scalar reduction, not a
            // per-pixel C# loop over the full-resolution texture (NORM-03). Only the
            // top-left validWidth x validHeight region holds the reduction result — the
            // over-allocated avg target's remaining texels are stale and must be ignored.
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(src, 0, TextureFormat.RGBA32);
            request.WaitForCompletion();
            if (request.hasError)
            {
                throw new InvalidOperationException("NAMER AO average readback failed.");
            }

            NativeArray<Color32> data = request.GetData<Color32>();
            int rowStride = src.width;
            float sum = 0f;
            for (int y = 0; y < validHeight; y++)
            {
                for (int x = 0; x < validWidth; x++)
                {
                    sum += data[y * rowStride + x].r / 255.0f;
                }
            }

            return sum / (validWidth * validHeight);
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

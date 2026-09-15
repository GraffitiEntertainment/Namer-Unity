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
    /// Result of one roughness-extraction run. <see cref="Roughness"/> is always pool-leased
    /// (<see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear); <see cref="CleanedBase"/> is only
    /// set by the fit-driven path (<see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear — the
    /// sharp-removal cleaned base the refit consumes, D-05). Both are owned by the roughness
    /// pipeline's pool and released via <see cref="NamerRoughnessPipeline.ReleaseRoughness"/>.
    /// </summary>
    public sealed class NamerRoughnessExtractResult
    {
        public RenderTexture Roughness;
        public RenderTexture CleanedBase;
    }

    /// <summary>
    /// GPU dispatch harness for the NAMER image-space roughness extraction stage (Phase 04.1).
    /// Plan 01 delivered the Sobel (Blender-parity) estimator; plan 02 adds the fit-driven
    /// estimator and its frequency-separation kernels (blur H/V -> sharp-detail -> sharp-removal
    /// -> remap) plus the 3A AO-three-way fit cache.
    ///
    /// The fit-driven path runs the strength search synchronously on cache miss: it calls
    /// <see cref="NamerRoughnessFitter.Fit"/> with the <c>Func&lt;float,float&gt;</c> evaluate
    /// callback that <c>NamerProcessor</c> composes (the callback owns the splitter/fitter/decomp
    /// state and drives the per-step GPU sharp-removal + readback + refit). This pipeline owns
    /// only the GPU per-iteration sharp-removal eval (<see cref="RunSharpRemoval"/>) and the fit
    /// cache — never the splitter/fitter/decomp state (3A).
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
        private const int MinBlurRadius = 8;
        private const int MaxBlurRadius = 64;
        private const int BlurRadiusDivisor = 32;
        private const float BlurSigmaDivisor = 3.0f;
        private const float MinBlurSigma = 1e-3f;

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelSobel;
        private readonly int _kernelMaxReduce;
        private readonly int _kernelNormalize;
        private readonly int _kernelBlurH;
        private readonly int _kernelBlurV;
        private readonly int _kernelSharpDetail;
        private readonly int _kernelSharpRemoval;
        private readonly int _kernelRemap;

        // 3A fit cache keyed by the full fit identity (mesh, base-map content, AO controls,
        // estimator, dimensions, threshold — see MakeFitKey, WR-01) — mirrors NamerAOPipeline's
        // bake cache. Stores the SELECTED strength so a later Process call reuses it without
        // re-running the strength search. The threshold is part of the key: a strength that
        // passes one threshold may not pass another, so a fit must be re-searched when the
        // threshold changes. Only a PASSED search is cached — a failed search re-runs on the
        // next request instead of reusing the honest-but-invalid max ladder strength.
        private readonly Dictionary<(int meshId, int baseMapId, float aoStrength, float aoContrast, float aoBlurRadius, float aoUnmultiplyStrength, int estimator, int w, int h, float maxErrorThreshold), NamerRoughnessFitResult> _fitCache =
            new();

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
            _kernelBlurH = _compute.FindKernel("CSRoughnessBlurH");
            _kernelBlurV = _compute.FindKernel("CSRoughnessBlurV");
            _kernelSharpDetail = _compute.FindKernel("CSRoughnessSharpDetail");
            _kernelSharpRemoval = _compute.FindKernel("CSRoughnessSharpRemoval");
            _kernelRemap = _compute.FindKernel("CSRoughnessRemap");
        }

        /// <summary>
        /// Extracts image-space roughness from <paramref name="baseColorOut"/> (the
        /// already-linear cleaned base produced by <c>CSNormalize</c>), dispatching on
        /// <c>inspection.RoughnessEstimator</c>:
        /// <list type="bullet">
        /// <item><see cref="NamerRoughnessEstimator.Sobel"/> — the plan-01 Sobel estimator
        /// (returns only a roughness target, no cleaned base).</item>
        /// <item><see cref="NamerRoughnessEstimator.FitDriven"/> — the strength search via
        /// <see cref="NamerRoughnessFitter.Fit"/> (driven by the caller-composed
        /// <paramref name="evaluate"/>), returning BOTH the roughness and the sharp-removal
        /// cleaned base (D-05).</item>
        /// </list>
        /// Both returned targets are pool-leased; the caller owns them and must release via
        /// <see cref="ReleaseRoughness"/>.
        /// </summary>
        public NamerRoughnessExtractResult ExtractRoughness(
            NamerMaterialInspection inspection,
            RenderTexture baseColorOut,
            int w,
            int h,
            Func<float, float> evaluate = null,
            Func<bool> shouldCancel = null,
            float maxErrorThreshold = 0f)
        {
            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

            if (baseColorOut == null)
            {
                throw new ArgumentNullException(nameof(baseColorOut));
            }

            if (inspection.RoughnessEstimator == NamerRoughnessEstimator.Sobel)
            {
                return new NamerRoughnessExtractResult { Roughness = ExtractSobel(baseColorOut, w, h) };
            }

            return ExtractFitDriven(inspection, baseColorOut, w, h, evaluate, shouldCancel, maxErrorThreshold);
        }

        /// <summary>
        /// Runs one self-contained fit-driven sharp-removal at <paramref name="strength"/>
        /// (blur H/V -> sharp-detail -> sharp-removal) and returns the cleaned base. This is
        /// the GPU per-iteration eval the <c>NamerProcessor</c>-composed evaluate callback
        /// drives (3A) — the pipeline owns the GPU work, the caller owns the readback + refit.
        /// The returned target is pool-leased; release via <see cref="ReleaseRoughness"/>.
        /// </summary>
        public RenderTexture RunSharpRemoval(RenderTexture baseColorOut, int w, int h, float strength)
        {
            if (baseColorOut == null)
            {
                throw new ArgumentNullException(nameof(baseColorOut));
            }

            RenderTextureDescriptor intermediate = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);

            RenderTexture blurA = null;
            RenderTexture blurB = null;
            RenderTexture sharpDetail = null;
            RenderTexture cleanedBase = null;

            try
            {
                blurA = _pool.Lease(intermediate);
                blurB = _pool.Lease(intermediate);
                sharpDetail = _pool.Lease(intermediate);
                cleanedBase = _pool.Lease(intermediate);

                RunBlurAndSharpDetail(baseColorOut, w, h, blurA, blurB, sharpDetail);

                _compute.SetFloat("_Strength", strength);
                _compute.SetTexture(_kernelSharpRemoval, "_BaseColorOut", baseColorOut);
                _compute.SetTexture(_kernelSharpRemoval, "_SharpDetail", sharpDetail);
                _compute.SetTexture(_kernelSharpRemoval, "_CleanedBaseOut", cleanedBase);
                Dispatch(_kernelSharpRemoval, w, h);

                return cleanedBase;
            }
            finally
            {
                Release(blurA);
                Release(blurB);
                Release(sharpDetail);
            }
        }

        /// <summary>
        /// Returns an extracted-roughness (or cleaned-base) target to this pipeline's pool.
        /// No-ops for null.
        /// </summary>
        public void ReleaseRoughness(RenderTexture roughness)
        {
            _pool.Release(roughness);
        }

        /// <summary>
        /// Runs (or reuses a cached) fit-driven strength search, then invokes
        /// <paramref name="onComplete"/>. The off-debounce interactive path (3A) — mirrors
        /// <c>NamerAOPipeline.RequestBake</c>. No-ops (and still invokes the callback) when a
        /// fit is already cached or no mesh/objective exists. Returns <c>false</c> (and does
        /// NOT cache or invoke <paramref name="onComplete"/>) when the search is cancelled via
        /// <paramref name="shouldCancel"/>.
        /// </summary>
        public bool RequestFit(
            NamerMaterialInspection inspection,
            int w,
            int h,
            float maxErrorThreshold,
            Func<float, float> evaluate,
            Action onComplete,
            Func<bool> shouldCancel = null)
        {
            if (inspection == null || inspection.BakeSourceMesh == null || evaluate == null)
            {
                onComplete?.Invoke();
                return true;
            }

            var key = MakeFitKey(inspection, w, h, maxErrorThreshold);
            if (_fitCache.ContainsKey(key))
            {
                onComplete?.Invoke();
                return true;
            }

            NamerRoughnessFitResult fit = NamerRoughnessFitter.Fit(evaluate, shouldCancel, maxErrorThreshold);
            if (fit.Cancelled)
            {
                // A cancelled fit must never be cached: no callback, so the caller falls back
                // to the identity legacy path (mirrors NamerAOPipeline's cancelled-bake contract).
                return false;
            }

            // WR-02: only a PASSED search is cached — a failed search (Passed == false) re-runs
            // on the next request instead of reusing its honest-but-invalid max-ladder strength.
            if (fit.Passed)
            {
                _fitCache[key] = fit;
            }

            onComplete?.Invoke();
            return true;
        }

        /// <summary>Drops every cached fit (used by tests and <see cref="Dispose"/>).</summary>
        public void ClearFitCache()
        {
            _fitCache.Clear();
        }

        public void Dispose()
        {
            ClearFitCache();
            _pool.Dispose();
        }

        // ------------------------------------------------------------------
        // Sobel (plan-01) path
        // ------------------------------------------------------------------

        private RenderTexture ExtractSobel(RenderTexture baseColorOut, int w, int h)
        {
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
        // Fit-driven (plan-02) path
        // ------------------------------------------------------------------

        private NamerRoughnessExtractResult ExtractFitDriven(
            NamerMaterialInspection inspection,
            RenderTexture baseColorOut,
            int w,
            int h,
            Func<float, float> evaluate,
            Func<bool> shouldCancel,
            float maxErrorThreshold)
        {
            if (evaluate == null)
            {
                // No refit objective => no fit => identity (1A). NamerComputePipeline gates on
                // this before calling; this is a defensive no-op.
                return new NamerRoughnessExtractResult();
            }

            var key = MakeFitKey(inspection, w, h, maxErrorThreshold);

            NamerRoughnessFitResult fit;
            if (_fitCache.TryGetValue(key, out fit))
            {
                // Cached strength: reuse it without re-running the search.
            }
            else
            {
                fit = NamerRoughnessFitter.Fit(evaluate, shouldCancel, maxErrorThreshold);
                if (fit.Cancelled)
                {
                    return new NamerRoughnessExtractResult();
                }

                // WR-02: only a PASSED search is cached — a failed search re-runs on the next
                // request instead of reusing its honest-but-invalid max-ladder strength.
                if (fit.Passed)
                {
                    _fitCache[key] = fit;
                }
            }

            return RunFrequencySeparation(baseColorOut, w, h, fit.Strength);
        }

        /// <summary>
        /// Produces the final fit-driven outputs at <paramref name="strength"/>: the
        /// sharp-removal cleaned base AND the strength-scaled roughness, computing the
        /// frequency-separation blur once.
        /// </summary>
        private NamerRoughnessExtractResult RunFrequencySeparation(RenderTexture baseColorOut, int w, int h, float strength)
        {
            RenderTextureDescriptor intermediate = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
            RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);

            RenderTexture blurA = null;
            RenderTexture blurB = null;
            RenderTexture sharpDetail = null;
            RenderTexture cleanedBase = null;
            RenderTexture roughnessOut = null;

            try
            {
                blurA = _pool.Lease(intermediate);
                blurB = _pool.Lease(intermediate);
                sharpDetail = _pool.Lease(intermediate);
                cleanedBase = _pool.Lease(intermediate);
                roughnessOut = _pool.Lease(unorm8);

                RunBlurAndSharpDetail(baseColorOut, w, h, blurA, blurB, sharpDetail);

                _compute.SetFloat("_Strength", strength);

                _compute.SetTexture(_kernelSharpRemoval, "_BaseColorOut", baseColorOut);
                _compute.SetTexture(_kernelSharpRemoval, "_SharpDetail", sharpDetail);
                _compute.SetTexture(_kernelSharpRemoval, "_CleanedBaseOut", cleanedBase);
                Dispatch(_kernelSharpRemoval, w, h);

                _compute.SetTexture(_kernelRemap, "_SharpDetail", sharpDetail);
                _compute.SetTexture(_kernelRemap, "_RoughnessOut", roughnessOut);
                Dispatch(_kernelRemap, w, h);

                return new NamerRoughnessExtractResult { Roughness = roughnessOut, CleanedBase = cleanedBase };
            }
            finally
            {
                Release(blurA);
                Release(blurB);
                Release(sharpDetail);
            }
        }

        /// <summary>
        /// Runs the shared frequency-separation prelude: separable gaussian blur H/V over the
        /// full-RGB base followed by the sharp-detail extraction. <paramref name="blurA"/>/<paramref name="blurB"/>
        /// ping-pong the blur; <paramref name="sharpDetail"/> receives base - blurred.
        /// </summary>
        private void RunBlurAndSharpDetail(
            RenderTexture baseColorOut,
            int w,
            int h,
            RenderTexture blurA,
            RenderTexture blurB,
            RenderTexture sharpDetail)
        {
            int radius = Mathf.Clamp(Mathf.Max(w, h) / BlurRadiusDivisor, MinBlurRadius, MaxBlurRadius);
            float sigma = Mathf.Max(radius / BlurSigmaDivisor, MinBlurSigma);

            _compute.SetInts("_Size", new[] { w, h });
            _compute.SetInt("_Radius", radius);
            _compute.SetFloat("_Sigma", sigma);

            _compute.SetTexture(_kernelBlurH, "_BaseColorOut", baseColorOut);
            _compute.SetTexture(_kernelBlurH, "_BlurPing", blurA);
            Dispatch(_kernelBlurH, w, h);

            _compute.SetTexture(_kernelBlurV, "_BlurPing", blurA);
            _compute.SetTexture(_kernelBlurV, "_Blurred", blurB);
            Dispatch(_kernelBlurV, w, h);

            _compute.SetTexture(_kernelSharpDetail, "_BaseColorOut", baseColorOut);
            _compute.SetTexture(_kernelSharpDetail, "_Blurred", blurB);
            _compute.SetTexture(_kernelSharpDetail, "_SharpDetail", sharpDetail);
            Dispatch(_kernelSharpDetail, w, h);
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

        // WR-01 (04.1 review): the fit drives on the base map's baked response sampled at
        // the mesh's UVs, and the evaluate callback re-runs the pipeline under the current
        // AO controls — so the key must identify the base-map CONTENT and the AO settings,
        // not just mesh + dimensions. A mesh + dimensions-only key let a persistent window
        // pipeline reuse a stale fitted strength across different materials sharing a mesh.
        private static (int meshId, int baseMapId, float aoStrength, float aoContrast, float aoBlurRadius, float aoUnmultiplyStrength, int estimator, int w, int h, float maxErrorThreshold) MakeFitKey(
            NamerMaterialInspection inspection, int w, int h, float maxErrorThreshold)
        {
            int meshId = inspection.BakeSourceMesh != null ? inspection.BakeSourceMesh.GetInstanceID() : 0;
            int baseMapId = inspection.BaseMap != null ? inspection.BaseMap.GetInstanceID() : 0;
            return (meshId, baseMapId, inspection.AoStrength, inspection.AoContrast,
                inspection.AoBlurRadius, inspection.AoUnmultiplyStrength,
                (int)inspection.RoughnessEstimator, w, h, maxErrorThreshold);
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

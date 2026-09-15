using GraffitiEntertainment.Namer.Core;
using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// GPU dispatch harness for the NAMER compute pipeline (NORM-03, D-12/D-13).
    /// Turns a <see cref="NamerMaterialInspection"/> (produced by 02-01) into in-memory
    /// normalized base-color and packed surface render targets by dispatching the three
    /// staged kernels in <c>Compute/NAMERPack.compute</c> on the GPU — never per-pixel C#.
    ///
    /// All render targets are declared with <see cref="GraphicsFormat"/> (intermediates
    /// <see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear; inputs and the final
    /// packed surface <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> raw), never sRGB, so
    /// compute can write them directly (D-11) and uploads carry source bytes through
    /// unconverted regardless of the project color space. Source sRGB/isReadable flags
    /// are never mutated.
    ///
    /// The <c>_BaseColor</c> tint is intentionally NOT baked into the normalized base —
    /// it stays as material metadata on the runtime NAMER shader.
    /// </summary>
    public sealed class NamerComputePipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute";
        private const string RawCopyShaderPath = "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerRawCopy.shader";
        internal const string ReencodeSrgbKeyword = "_REENCODE_SRGB";
        internal const int DefaultBaseResolution = 256;

        private static readonly Color NeutralNormalFill = new Color(0.5f, 0.5f, 1.0f, 1.0f);
        private static readonly Color NeutralMetallicGlossFill = new Color(0.0f, 0.0f, 0.0f, 1.0f);

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelNormalize;
        private readonly int _kernelOctahedralEncode;
        private readonly int _kernelSurfacePack;
        private NamerAOPipeline _aoPipeline;
        private NamerRoughnessPipeline _roughnessPipeline;

        // The normalized base of the in-flight Process. Set right after the normalize stage so
        // the NamerProcessor-composed evaluate callback (which the roughness fit invokes
        // reentrantly during Process) can drive the per-step GPU sharp-removal against it (3A).
        private RenderTexture _currentBaseColorOut;

        private static Texture2D _whiteFill;
        private static Texture2D _neutralNormalTexture;
        private static Texture2D _neutralMetallicGlossTexture;
        private static Material _rawCopyMaterial;

        public NamerComputePipeline()
        {
            _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            if (_compute == null)
            {
                throw new InvalidOperationException("NAMER compute shader not found at " + ComputeShaderPath);
            }

            _kernelNormalize = _compute.FindKernel("CSNormalize");
            _kernelOctahedralEncode = _compute.FindKernel("CSOctahedralEncode");
            _kernelSurfacePack = _compute.FindKernel("CSSurfacePack");
        }

        /// <summary>
        /// Number of render targets currently live in the pool. 02-03's leak watchdog
        /// asserts this returns to baseline after each <see cref="Process"/> +
        /// <see cref="ReleaseResult"/> pair, before <see cref="Dispose"/>.
        /// </summary>
        public int LiveRenderTargetCount => _pool.LiveCount;

        /// <summary>
        /// Runs the three staged kernels and returns in-memory normalized base-color and
        /// packed surface render targets. The result's two targets are leased from the
        /// pool: read them back (via <see cref="RequestReadback"/>), then return them
        /// with <see cref="ReleaseResult"/> — a batch of N materials must not accumulate
        /// live outputs. Targets still unreleased when this pipeline is disposed are
        /// destroyed with it, so release every result before disposing.
        /// </summary>
        public NamerComputeResult Process(
            NamerMaterialInspection inspection,
            Func<float, float> evaluate = null,
            Func<bool> shouldCancel = null,
            float maxErrorThreshold = 0f)
        {
            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

            int w = inspection.BaseMap != null ? inspection.BaseMap.width : DefaultBaseResolution;
            int h = inspection.BaseMap != null ? inspection.BaseMap.height : DefaultBaseResolution;

            RenderTextureDescriptor intermediate = NewDescriptor(w, h, GraphicsFormat.R16G16B16A16_SFloat);
            // Inputs and the packed surface are 8-bit raw staging: copy-compatible with
            // the RGBA32 sources so Upload can raw-copy them (kernels alone own any
            // color conversion, D-11). Sources are 8-bit, so nothing is lost vs float16.
            RenderTextureDescriptor unorm8 = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);

            RenderTexture baseColorIn = null;
            RenderTexture normalTexel = null;
            RenderTexture aoIn = null;
            RenderTexture metallicGlossIn = null;
            RenderTexture baseColorOut = null;
            RenderTexture octahedral = null;
            RenderTexture packInputs = null;
            RenderTexture surfaceOut = null;

            RenderTexture roughnessTex = null;
            RenderTexture cleanedBase = null;
            bool usesExtractedAo = false;
            bool usesBakedAo = false;
            bool shouldExtract = inspection.MetallicGlossMap == null && inspection.RoughnessExtractStrength > 0f;
            bool isFitDriven = inspection.RoughnessEstimator == NamerRoughnessEstimator.FitDriven;

            try
            {
                baseColorIn = _pool.Lease(unorm8);
                normalTexel = _pool.Lease(unorm8);
                metallicGlossIn = _pool.Lease(unorm8);
                baseColorOut = _pool.Lease(intermediate);
                octahedral = _pool.Lease(intermediate);
                packInputs = _pool.Lease(intermediate);
                surfaceOut = _pool.Lease(unorm8);

                Upload(inspection.BaseMap, baseColorIn, WhiteFill());
                Upload(inspection.NormalMap, normalTexel, NeutralNormalTexture());

                if (inspection.OcclusionMap != null)
                {
                    aoIn = _pool.Lease(unorm8);
                    Upload(inspection.OcclusionMap, aoIn, WhiteFill());
                    usesExtractedAo = false;
                    usesBakedAo = false;
                }
                else if (EnsureAoPipeline().HasCachedBake(inspection.BakeSourceMesh, inspection.OccluderMesh, w, h))
                {
                    // D-07 three-way gate: a cached geometry bake supersedes image-space extraction.
                    aoIn = EnsureAoPipeline().BakeAndUpload(inspection, w, h);
                    usesBakedAo = true;
                }
                else
                {
                    // D-07 three-way gate: no authored map and no cached bake -> extract AO.
                    aoIn = EnsureAoPipeline().Extract(inspection, baseColorIn, w, h);
                    usesExtractedAo = true;
                }

                Upload(inspection.MetallicGlossMap, metallicGlossIn, NeutralMetallicGlossTexture());

                // (a) Normalize + octahedral encode -> _BaseColorOut / _PackInputs / _Octahedral.
                BindAndDispatchNormalizeEncode(inspection, baseColorIn, normalTexel, aoIn, metallicGlossIn,
                    baseColorOut, octahedral, packInputs, w, h, usesExtractedAo || usesBakedAo);

                _currentBaseColorOut = baseColorOut;

                // (b) Roughness extraction runs between the normalized base and the surface
                //     pack (D-01 authored-map-wins gate + the 1A fit-driven gate).
                if (shouldExtract && !isFitDriven)
                {
                    // Sobel: explicit standalone extraction (parity-only — no cleaned base).
                    NamerRoughnessExtractResult sobel = EnsureRoughnessPipeline().ExtractRoughness(inspection, baseColorOut, w, h);
                    roughnessTex = sobel.Roughness;
                }
                else if (shouldExtract && isFitDriven && inspection.BakeSourceMesh != null && evaluate != null)
                {
                    // Fit-driven: decomposition enabled AND source mesh resolved AND an evaluate
                    // objective (composed by NamerProcessor) — the 1A refit-precondition. On
                    // cache miss the strength search runs synchronously here (3A).
                    NamerRoughnessExtractResult fitDriven = EnsureRoughnessPipeline().ExtractRoughness(
                        inspection, baseColorOut, w, h, evaluate, shouldCancel, maxErrorThreshold);
                    roughnessTex = fitDriven.Roughness;
                    cleanedBase = fitDriven.CleanedBase;
                }
                // else: identity legacy path — fit-driven without a refit (1A) or strength == 0.

                // (c) Surface pack, overriding the scalar roughness with the extracted texture.
                // Fit-driven packs at FULL adoption: RunFrequencySeparation already baked the
                // SEARCHED strength into both the roughness texture and the cleaned base, and
                // the fit validated that exact pairing — re-blending by the user slider
                // (the Sobel-only D-02 control) under-applied the searched response (a live
                // slider of 0.25 packed only ~25% of it) and made the slider look like it
                // drives the fit. It does not; it only gates extraction on (> 0).
                BindAndDispatchSurfacePack(octahedral, packInputs, surfaceOut,
                    roughnessTex != null ? (Texture)roughnessTex : WhiteFill(),
                    roughnessTex != null,
                    isFitDriven ? 1f : inspection.RoughnessExtractStrength, w, h);

                // D-05: the fit-driven path repoints NormalizedBaseColor at the sharp-removal
                // cleaned base so the refit consumes the post-extraction base.
                return new NamerComputeResult
                {
                    NormalizedBaseColor = cleanedBase != null ? cleanedBase : baseColorOut,
                    PackedSurface = surfaceOut,
                    ExtractedRoughness = roughnessTex,
                    NormalizedBaseColorOwnedByRoughnessPool = cleanedBase != null,
                    Width = w,
                    Height = h,
                };
            }
            catch
            {
                // WR-03: on a mid-pipeline throw the caller never receives the result, so
                // ReleaseResult never runs — release the un-returned output leases here
                // (inputs stay in finally). The window's persistent pipeline would otherwise
                // accumulate live RTs per failed preview recompute. cleanedBase/roughnessTex
                // belong to the ROUGHNESS pool (compute-pool Release would silently no-op);
                // baseColorOut is either released here (no cleaned base yet) or by finally's
                // cleanedBase branch — never both.
                if (surfaceOut != null)
                {
                    Release(surfaceOut);
                }
                if (roughnessTex != null)
                {
                    ReleaseExtractedRoughness(roughnessTex);
                }
                if (cleanedBase != null)
                {
                    ReleaseExtractedRoughness(cleanedBase);
                }
                else if (baseColorOut != null)
                {
                    Release(baseColorOut);
                }
                throw;
            }
            finally
            {
                _currentBaseColorOut = null;
                Release(baseColorIn);
                Release(normalTexel);
                if (usesExtractedAo || usesBakedAo)
                {
                    _aoPipeline.ReleaseAo(aoIn);
                }
                else
                {
                    Release(aoIn);
                }
                Release(metallicGlossIn);
                Release(octahedral);
                Release(packInputs);

                // Plan 02: ownership of the extracted-roughness RT (and the fit-driven cleaned
                // base, which repoints NormalizedBaseColor) MOVED to NamerComputeResult —
                // released by ReleaseResult, never here. Releasing them through _pool would
                // silently no-op on foreign targets and leak them.
                if (cleanedBase != null)
                {
                    // NormalizedBaseColor now points at the cleaned base, so the normalize
                    // output (baseColorOut) is surplus — return it to the compute pool.
                    Release(baseColorOut);
                }
            }
        }

        /// <summary>
        /// Returns a <see cref="Process"/> result's two render targets to the pool for
        /// reuse by later <see cref="Process"/> calls. Call after readback; afterwards
        /// the result is invalid (its targets are null) and must not be used again.
        /// </summary>
        public void ReleaseResult(NamerComputeResult result)
        {
            if (result == null)
            {
                return;
            }

            if (result.NormalizedBaseColorOwnedByRoughnessPool)
            {
                // The fit-driven cleaned base came from the roughness pipeline's OWN pool; the
                // compute pool's Release silently no-ops on it (not in its _live set), so it
                // must be returned there or it leaks (D-05).
                _roughnessPipeline.ReleaseRoughness(result.NormalizedBaseColor);
            }
            else
            {
                _pool.Release(result.NormalizedBaseColor);
            }

            if (result.ExtractedRoughness != null)
            {
                _roughnessPipeline.ReleaseRoughness(result.ExtractedRoughness);
            }

            _pool.Release(result.PackedSurface);
            result.NormalizedBaseColor = null;
            result.ExtractedRoughness = null;
            result.PackedSurface = null;
        }

        /// <summary>
        /// Non-blocking GPU -> CPU readback of an in-memory render target (D-13).
        /// </summary>
        public static AsyncGPUReadbackRequest RequestReadback(RenderTexture source, int mip = 0, TextureFormat format = TextureFormat.RGBA32)
        {
            return AsyncGPUReadback.Request(source, mip, format);
        }

        public void Dispose()
        {
            _aoPipeline?.Dispose();
            _roughnessPipeline?.Dispose();
            _pool.Dispose();
        }

        private NamerAOPipeline EnsureAoPipeline()
        {
            if (_aoPipeline == null)
            {
                _aoPipeline = new NamerAOPipeline();
            }

            return _aoPipeline;
        }

        private NamerRoughnessPipeline EnsureRoughnessPipeline()
        {
            if (_roughnessPipeline == null)
            {
                _roughnessPipeline = new NamerRoughnessPipeline();
            }

            return _roughnessPipeline;
        }

        /// <summary>
        /// Forwards to <see cref="NamerRoughnessPipeline.RunSharpRemoval"/> — the GPU
        /// per-iteration sharp-removal eval the NamerProcessor-composed evaluate callback
        /// drives (3A). Uses the in-flight normalized base (see <c>_currentBaseColorOut</c>).
        /// </summary>
        public RenderTexture ExtractSharpRemoval(int w, int h, float strength)
        {
            return EnsureRoughnessPipeline().RunSharpRemoval(_currentBaseColorOut, w, h, strength);
        }

        /// <summary>
        /// Forwards to <see cref="NamerRoughnessPipeline.ReleaseRoughness"/> — returns an
        /// extracted-roughness (or cleaned-base probe) target to the roughness pipeline's pool.
        /// </summary>
        public void ReleaseExtractedRoughness(RenderTexture rt)
        {
            _roughnessPipeline?.ReleaseRoughness(rt);
        }

        /// <summary>
        /// Thin forwarder to <see cref="NamerRoughnessPipeline.TryGetFitStrength"/> — the
        /// window's fit-driven readout of the strength the search picked.
        /// </summary>
        public bool TryGetFitStrength(NamerMaterialInspection inspection, int w, int h, float maxErrorThreshold, out float strength)
        {
            return EnsureRoughnessPipeline().TryGetFitStrength(inspection, w, h, maxErrorThreshold, out strength);
        }

        /// <summary>
        /// Thin forwarder to <see cref="NamerAOPipeline.HasCachedBake"/> — true when a
        /// geometry bake for this inspection's meshes is already cached (D-07).
        /// </summary>
        public bool HasCachedBake(NamerMaterialInspection inspection, int w, int h)
        {
            return EnsureAoPipeline().HasCachedBake(inspection.BakeSourceMesh, inspection.OccluderMesh, w, h);
        }

        /// <summary>
        /// Thin forwarder to <see cref="NamerAOPipeline.RequestBake"/> — schedules an
        /// off-debounce geometry bake (called by the 03.1-03 window). Returns
        /// <c>false</c> when the bake was cancelled.
        /// </summary>
        public bool RequestBake(NamerMaterialInspection inspection, int w, int h, Action onComplete, Func<bool> shouldCancel = null)
        {
            return EnsureAoPipeline().RequestBake(inspection, w, h, onComplete, shouldCancel);
        }

        private void BindAndDispatchNormalizeEncode(
            NamerMaterialInspection inspection,
            RenderTexture baseColorIn,
            RenderTexture normalTexel,
            RenderTexture aoIn,
            RenderTexture metallicGlossIn,
            RenderTexture baseColorOut,
            RenderTexture octahedral,
            RenderTexture packInputs,
            int w,
            int h,
            bool usesSyntheticAo)
        {
            _compute.SetInts("_Size", new[] { w, h });

            _compute.SetFloat("_SourceIsSrgb", inspection.BaseMapIsSrgb ? 1f : 0f);
            _compute.SetFloat("_AoUnmultiplyStrength", inspection.AoUnmultiplyStrength);
            _compute.SetFloat("_AoUnmultiplyFloor", usesSyntheticAo ? NamerConstants.AoFloor : NamerConstants.Epsilon);
            _compute.SetFloat("_Metallic", inspection.Metallic);
            _compute.SetFloat("_SmoothnessScalar", inspection.Smoothness);
            _compute.SetFloat("_Roughness", inspection.Roughness);
            _compute.SetFloat("_Emissive", inspection.Emissive);
            _compute.SetFloat("_SmoothnessTextureChannel", inspection.SmoothnessTextureChannel);
            _compute.SetFloat("_HasMetallicGlossMap", inspection.MetallicGlossMap != null ? 1f : 0f);

            _compute.SetTexture(_kernelNormalize, "_BaseColorIn", baseColorIn);
            _compute.SetTexture(_kernelNormalize, "_AoIn", aoIn);
            _compute.SetTexture(_kernelNormalize, "_MetallicGlossIn", metallicGlossIn);
            _compute.SetTexture(_kernelNormalize, "_BaseColorOut", baseColorOut);
            _compute.SetTexture(_kernelNormalize, "_PackInputs", packInputs);

            _compute.SetTexture(_kernelOctahedralEncode, "_NormalTexel", normalTexel);
            _compute.SetTexture(_kernelOctahedralEncode, "_AoIn", aoIn);
            _compute.SetTexture(_kernelOctahedralEncode, "_Octahedral", octahedral);

            Dispatch(_kernelNormalize, w, h);
            Dispatch(_kernelOctahedralEncode, w, h);
        }

        private void BindAndDispatchSurfacePack(
            RenderTexture octahedral,
            RenderTexture packInputs,
            RenderTexture surfaceOut,
            Texture roughnessTex,
            bool hasExtractedRoughness,
            float roughnessExtractStrength,
            int w,
            int h)
        {
            _compute.SetTexture(_kernelSurfacePack, "_Octahedral", octahedral);
            _compute.SetTexture(_kernelSurfacePack, "_PackInputs", packInputs);
            _compute.SetTexture(_kernelSurfacePack, "_SurfaceOut", surfaceOut);
            _compute.SetFloat("_HasExtractedRoughness", hasExtractedRoughness ? 1f : 0f);
            _compute.SetFloat("_RoughnessExtractStrength", roughnessExtractStrength);
            _compute.SetTexture(_kernelSurfacePack, "_RoughnessTex", roughnessTex);

            Dispatch(_kernelSurfacePack, w, h);
        }

        private void Dispatch(int kernel, int w, int h)
        {
            _compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }

        private void Release(RenderTexture rt)
        {
            _pool.Release(rt);
        }

        internal static void Upload(Texture source, RenderTexture target, Texture2D fallback)
        {
            Texture upload = source != null ? source : fallback;

            // Raw upload through the NamerRawCopy material: a plain Graphics.Blit
            // hardware-decodes sRGB-declared sources under a Linear project, which the
            // kernels' _SourceIsSrgb decode would then double-decode. The material's
            // _REENCODE_SRGB variant cancels that decode in-shader (both conversions
            // run in float with one quantization at the write, so the source bytes
            // arrive bit-exactly), and a sampled blit accepts every source shape —
            // RGB24 layouts, mip chains, and non-base resolutions alike, none of which
            // Graphics.CopyTexture's format/mip restrictions tolerate.
            Material rawCopy = RawCopyMaterial();
            bool reencode = GraphicsFormatUtility.IsSRGBFormat(upload.graphicsFormat)
                && QualitySettings.activeColorSpace == ColorSpace.Linear;
            if (reencode)
            {
                rawCopy.EnableKeyword(ReencodeSrgbKeyword);
            }
            else
            {
                rawCopy.DisableKeyword(ReencodeSrgbKeyword);
            }

            Graphics.Blit(upload, target, rawCopy);
        }

        internal static Material RawCopyMaterial()
        {
            if (_rawCopyMaterial == null)
            {
                Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(RawCopyShaderPath);
                if (shader == null)
                {
                    throw new InvalidOperationException("NAMER raw-copy shader not found at " + RawCopyShaderPath);
                }

                _rawCopyMaterial = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            return _rawCopyMaterial;
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

        private static Texture2D WhiteFill()
        {
            if (_whiteFill == null)
            {
                _whiteFill = CreateFill(Color.white, "NamerWhiteFill");
            }

            return _whiteFill;
        }

        private static Texture2D NeutralNormalTexture()
        {
            if (_neutralNormalTexture == null)
            {
                _neutralNormalTexture = CreateFill(NeutralNormalFill, "NamerNeutralNormal");
            }

            return _neutralNormalTexture;
        }

        private static Texture2D NeutralMetallicGlossTexture()
        {
            if (_neutralMetallicGlossTexture == null)
            {
                _neutralMetallicGlossTexture = CreateFill(NeutralMetallicGlossFill, "NamerNeutralMetallicGloss");
            }

            return _neutralMetallicGlossTexture;
        }

        private static Texture2D CreateFill(Color color, string name)
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            texture.name = name;
            texture.hideFlags = HideFlags.HideAndDontSave;
            texture.SetPixel(0, 0, color);
            texture.Apply(false, false);
            return texture;
        }
    }

    /// <summary>
    /// In-memory result of <see cref="NamerComputePipeline.Process"/>. The two render
    /// targets are leased from the pipeline's pool, not owned by the caller: read them
    /// back, then return them with <see cref="NamerComputePipeline.ReleaseResult"/>.
    /// After <see cref="NamerComputePipeline.ReleaseResult"/> the fields are null; if
    /// the pipeline is disposed while a result is still unreleased, its targets are
    /// destroyed with it and become unusable.
    /// </summary>
    public sealed class NamerComputeResult
    {
        public RenderTexture NormalizedBaseColor;
        public RenderTexture PackedSurface;

        /// <summary>
        /// The extracted roughness render target (roughness pipeline pool), set only when
        /// extraction ran. Null in the identity/legacy path (and when extraction was skipped).
        /// <see cref="NamerComputePipeline.ReleaseResult"/> owns releasing it. The D-07 debug
        /// channel binds it for the Extracted Roughness view.
        /// </summary>
        public RenderTexture ExtractedRoughness;

        /// <summary>
        /// True when <see cref="NormalizedBaseColor"/> is the fit-driven sharp-removal cleaned
        /// base (leased from the roughness pipeline's own pool) rather than the compute pool's
        /// normalize output. Drives the correct release in
        /// <see cref="NamerComputePipeline.ReleaseResult"/> (D-05).
        /// </summary>
        internal bool NormalizedBaseColorOwnedByRoughnessPool;

        public int Width;
        public int Height;
    }
}

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
        private const int DefaultResolution = 256;

        private static readonly Color NeutralNormalFill = new Color(0.5f, 0.5f, 1.0f, 1.0f);
        private static readonly Color NeutralMetallicGlossFill = new Color(0.0f, 0.0f, 0.0f, 1.0f);

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelNormalize;
        private readonly int _kernelOctahedralEncode;
        private readonly int _kernelSurfacePack;
        private NamerAOPipeline _aoPipeline;

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
        public NamerComputeResult Process(NamerMaterialInspection inspection)
        {
            if (inspection == null)
            {
                throw new ArgumentNullException(nameof(inspection));
            }

            int w = inspection.BaseMap != null ? inspection.BaseMap.width : DefaultResolution;
            int h = inspection.BaseMap != null ? inspection.BaseMap.height : DefaultResolution;

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

            bool usesExtractedAo = false;
            bool usesBakedAo = false;

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

                BindAndDispatch(inspection, baseColorIn, normalTexel, aoIn, metallicGlossIn,
                    baseColorOut, octahedral, packInputs, surfaceOut, w, h, usesExtractedAo || usesBakedAo);

                return new NamerComputeResult
                {
                    NormalizedBaseColor = baseColorOut,
                    PackedSurface = surfaceOut,
                    Width = w,
                    Height = h,
                };
            }
            finally
            {
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

            _pool.Release(result.NormalizedBaseColor);
            _pool.Release(result.PackedSurface);
            result.NormalizedBaseColor = null;
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
        /// off-debounce geometry bake (called by the 03.1-03 window).
        /// </summary>
        public void RequestBake(NamerMaterialInspection inspection, int w, int h, Action onComplete)
        {
            EnsureAoPipeline().RequestBake(inspection, w, h, onComplete);
        }

        private void BindAndDispatch(
            NamerMaterialInspection inspection,
            RenderTexture baseColorIn,
            RenderTexture normalTexel,
            RenderTexture aoIn,
            RenderTexture metallicGlossIn,
            RenderTexture baseColorOut,
            RenderTexture octahedral,
            RenderTexture packInputs,
            RenderTexture surfaceOut,
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

            _compute.SetTexture(_kernelSurfacePack, "_Octahedral", octahedral);
            _compute.SetTexture(_kernelSurfacePack, "_PackInputs", packInputs);
            _compute.SetTexture(_kernelSurfacePack, "_SurfaceOut", surfaceOut);

            Dispatch(_kernelNormalize, w, h);
            Dispatch(_kernelOctahedralEncode, w, h);
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
        public int Width;
        public int Height;
    }
}

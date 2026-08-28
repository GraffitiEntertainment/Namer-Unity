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
        private const int DefaultResolution = 256;

        private static readonly Color NeutralNormalFill = new Color(0.5f, 0.5f, 1.0f, 1.0f);
        private static readonly Color NeutralMetallicGlossFill = new Color(0.0f, 0.0f, 0.0f, 1.0f);

        private readonly ComputeTexturePool _pool = new ComputeTexturePool();
        private readonly ComputeShader _compute;
        private readonly int _kernelNormalize;
        private readonly int _kernelOctahedralEncode;
        private readonly int _kernelSurfacePack;

        private static Texture2D _whiteFill;
        private static Texture2D _neutralNormalTexture;
        private static Texture2D _neutralMetallicGlossTexture;

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

            try
            {
                baseColorIn = _pool.Lease(unorm8);
                normalTexel = _pool.Lease(unorm8);
                aoIn = _pool.Lease(unorm8);
                metallicGlossIn = _pool.Lease(unorm8);
                baseColorOut = _pool.Lease(intermediate);
                octahedral = _pool.Lease(intermediate);
                packInputs = _pool.Lease(intermediate);
                surfaceOut = _pool.Lease(unorm8);

                Upload(inspection.BaseMap, baseColorIn, WhiteFill());
                Upload(inspection.NormalMap, normalTexel, NeutralNormalTexture());
                Upload(inspection.OcclusionMap, aoIn, WhiteFill());
                Upload(inspection.MetallicGlossMap, metallicGlossIn, NeutralMetallicGlossTexture());

                BindAndDispatch(inspection, baseColorIn, normalTexel, aoIn, metallicGlossIn,
                    baseColorOut, octahedral, packInputs, surfaceOut, w, h);

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
                Release(aoIn);
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
            _pool.Dispose();
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
            int h)
        {
            _compute.SetInts("_Size", new[] { w, h });

            _compute.SetFloat("_SourceIsSrgb", inspection.BaseMapIsSrgb ? 1f : 0f);
            _compute.SetFloat("_AoUnmultiplyStrength", inspection.AoUnmultiplyStrength);
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

        private static void Upload(Texture source, RenderTexture target, Texture2D fallback)
        {
            Texture upload = source != null ? source : fallback;

            // Raw texel copy — no sampling, so no color-space conversion in any project
            // color space. A sampled Graphics.Blit hardware-decodes sRGB-declared
            // sources under a Linear project, double-decoding base color (the shader's
            // _SourceIsSrgb decode) and violating the raw data-map contract in the
            // kernels. CopyTexture requires equal dimensions, so only the rare
            // resolution-mismatch path (including the 1x1 fills) still blits — those
            // fills are linear-declared, which never converts in either color space.
            if (upload.width == target.width && upload.height == target.height)
            {
                Graphics.CopyTexture(upload, target);
            }
            else
            {
                Graphics.Blit(upload, target);
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

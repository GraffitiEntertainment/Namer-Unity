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
    /// <see cref="GraphicsFormat.R16G16B16A16_SFloat"/> linear, final packed surface
    /// <see cref="GraphicsFormat.R8G8B8A8_UNorm"/> linear), never sRGB, so compute can
    /// write them directly (D-11). Source sRGB/isReadable flags are never mutated.
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
        /// asserts this returns to baseline after <see cref="Process"/> + <see cref="Dispose"/>.
        /// </summary>
        public int LiveRenderTargetCount => _pool.LiveCount;

        /// <summary>
        /// Runs the three staged kernels and returns in-memory normalized base-color and
        /// packed surface render targets. The caller owns the returned targets and must
        /// read them back (via <see cref="RequestReadback"/>) before disposing this pipeline.
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
            RenderTextureDescriptor surface = NewDescriptor(w, h, GraphicsFormat.R8G8B8A8_UNorm);

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
                baseColorIn = _pool.Lease(intermediate);
                normalTexel = _pool.Lease(intermediate);
                aoIn = _pool.Lease(intermediate);
                metallicGlossIn = _pool.Lease(intermediate);
                baseColorOut = _pool.Lease(intermediate);
                octahedral = _pool.Lease(intermediate);
                packInputs = _pool.Lease(intermediate);
                surfaceOut = _pool.Lease(surface);

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
            Graphics.Blit(source != null ? source : fallback, target);
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
    /// In-memory result of <see cref="NamerComputePipeline.Process"/>. The caller owns
    /// the two render targets and must read them back before the pipeline is disposed.
    /// </summary>
    public sealed class NamerComputeResult
    {
        public RenderTexture NormalizedBaseColor;
        public RenderTexture PackedSurface;
        public int Width;
        public int Height;
    }
}

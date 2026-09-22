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
    /// 04.2 (plan 03): <see cref="Process"/> accepts a <see cref="NamerProjectionContext"/>
    /// instead of the retired 04.1 fit-driven evaluate/shouldCancel/maxErrorThreshold
    /// triplet. When a projection is supplied, the Gouraud projection write-back runs after
    /// the normalize stage, the removed-detail transfer feeds the roughness dip, and the
    /// projected base becomes <see cref="NamerComputeResult.NormalizedBaseColor"/> while the
    /// source base is preserved as <see cref="NamerComputeResult.SourceBaseColor"/>.
    ///
    /// The <c>_BaseColor</c> tint is intentionally NOT baked into the normalized base —
    /// it stays as material metadata on the runtime NAMER shader.
    /// </summary>
    public sealed class NamerComputePipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute";
        private const string RawCopyShaderPath = "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerRawCopy.shader";
        internal const string ReencodeSrgbKeyword = "_REENCODE_SRGB";
        internal const string UnpackNormalKeyword = "_UNPACK_NORMAL";
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
        /// packed surface render targets. The result's targets are leased from the
        /// pool: read them back (via <see cref="RequestReadback"/>), then return them
        /// with <see cref="ReleaseResult"/> — a batch of N materials must not accumulate
        /// live outputs. Targets still unreleased when this pipeline is disposed are
        /// destroyed with it, so release every result before disposing.
        ///
        /// 04.2 flow: when <paramref name="projection"/> is supplied, the pipeline leases a
        /// projected base target after the normalize stage and invokes
        /// <see cref="NamerProjectionContext.Run"/>, which writes the Gouraud projection and
        /// produces the residual. Extraction then branches: the removed-detail transfer
        /// (DipSource == RemovedDetail) or the Sobel fallback (DipSource == SobelEdge or no
        /// projection); an authored metallic/gloss map leaves the scalar path untouched.
        /// </summary>
        public NamerComputeResult Process(NamerMaterialInspection inspection, NamerProjectionContext projection = null)
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
            RenderTexture projectedOut = null;
            bool usesBakedAo = false;
            // SmoothnessTextureChannel == 1 authors per-pixel smoothness in the base-map
            // alpha (URP Lit / Standard): that is authored data just like a
            // _MetallicGlossMap, so extraction must stay off or the dip would overwrite it
            // (Codex PR #1 review).
            bool shouldExtract = inspection.MetallicGlossMap == null
                && inspection.SmoothnessTextureChannel != 1
                && inspection.RoughnessExtractStrength > 0f;
            bool removedDetail = inspection.DipSource == NamerDipSource.RemovedDetail;
            bool roughnessDipApplied = false;

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
                Upload(inspection.NormalMap, normalTexel, NeutralNormalTexture(), unpackNormal: true);

                if (inspection.OcclusionMap != null)
                {
                    // D-07 gate, clause 0 (2026-09-21 contract): an authored map is
                    // source data and always transfers, regardless of the AO stage
                    // checkbox (the checkbox gates synthesis only).
                    aoIn = _pool.Lease(unorm8);
                    Upload(inspection.OcclusionMap, aoIn, WhiteFill());
                }
                else if (inspection.AoStageEnabled)
                {
                    // D-07 gate, clauses 2-3: AO stage on and no authored map -> the
                    // GEOMETRY BAKE is the synthetic source (the albedo-conflated
                    // luminance extraction is retired from the automatic path).
                    // BakeAndUpload reuses the cache or bakes fresh on demand; it
                    // returns null when no bake mesh is primed (decomposition off),
                    // leaving aoIn null for the white fill below.
                    aoIn = EnsureAoPipeline().BakeAndUpload(inspection, w, h);
                    usesBakedAo = aoIn != null;
                }

                if (aoIn == null)
                {
                    // D-07 gate: AO stage off (or no bake source) -> surface B packs
                    // white (1.0) through the existing WhiteFill upload path, and the
                    // un-multiply divides by 1 whatever the strength — the stage gate
                    // subsumes the old strength=0-only behavior.
                    aoIn = _pool.Lease(unorm8);
                    Upload(null, aoIn, WhiteFill());
                }

                Upload(inspection.MetallicGlossMap, metallicGlossIn, NeutralMetallicGlossTexture());

                // (a) Normalize + octahedral encode -> _BaseColorOut / _PackInputs / _Octahedral.
                BindAndDispatchNormalizeEncode(inspection, baseColorIn, normalTexel, aoIn, metallicGlossIn,
                    baseColorOut, octahedral, packInputs, w, h, usesBakedAo);

                // (b) 04.2 projection write-back + roughness dip, between the normalized base
                //     and the surface pack (D-01 authored-map-wins gate).
                if (projection != null)
                {
                    projectedOut = _pool.Lease(intermediate);
                    projection.Run(baseColorOut, projectedOut);
                }

                if (projection != null && removedDetail && shouldExtract)
                {
                    // Removed-detail transfer: the removed-luma minuend is the SOURCE base
                    // (baseColorOut), the projected base is the subtrahend, and the transfer
                    // map already carries the dip (adopt as-is at pack time).
                    roughnessTex = EnsureRoughnessPipeline().ExtractTransferRoughness(
                        inspection.Roughness, baseColorOut, projectedOut, w, h, inspection.RoughnessExtractStrength);
                    roughnessDipApplied = true;
                }
                else if (shouldExtract)
                {
                    // Sobel fallback: decomposition off (or CR-01-guarded) or DipSource ==
                    // SobelEdge — CSSurfacePack applies the slider-scaled dip.
                    roughnessTex = EnsureRoughnessPipeline().ExtractSobel(baseColorOut, w, h);
                    roughnessDipApplied = false;
                }
                // else: authored roughness map present (or strength == 0) -> scalar path.

                // (c) Surface pack, overriding the scalar roughness with the extracted texture.
                // The transfer path packs at FULL adoption (dipApplied): the map is already
                // dip-encoded by CSRoughnessTransferRemap. The Sobel path re-blends by the
                // taste slider at pack time.
                BindAndDispatchSurfacePack(octahedral, packInputs, surfaceOut,
                    roughnessTex != null ? (Texture)roughnessTex : WhiteFill(),
                    roughnessTex != null,
                    roughnessDipApplied ? 1f : inspection.RoughnessExtractStrength,
                    roughnessDipApplied, w, h);

                // 04.2: NormalizedBaseColor is the projected base when projection ran (so the
                // written Base PNG IS the projection); the source base is preserved as
                // SourceBaseColor for the residual-ON dividend and the window preview.
                return new NamerComputeResult
                {
                    NormalizedBaseColor = projectedOut != null ? projectedOut : baseColorOut,
                    SourceBaseColor = projectedOut != null ? baseColorOut : null,
                    PackedSurface = surfaceOut,
                    ExtractedRoughness = roughnessTex,
                    Width = w,
                    Height = h,
                };
            }
            catch
            {
                // WR-03: on a mid-pipeline throw the caller never receives the result, so
                // ReleaseResult never runs — release the un-returned output leases here
                // (inputs stay in finally). roughnessTex belongs to the ROUGHNESS pool;
                // projectedOut and baseColorOut belong to the compute pool.
                if (surfaceOut != null)
                {
                    Release(surfaceOut);
                }
                if (roughnessTex != null)
                {
                    _roughnessPipeline?.ReleaseRoughness(roughnessTex);
                }
                if (projectedOut != null)
                {
                    Release(projectedOut);
                }
                Release(baseColorOut);
                throw;
            }
            finally
            {
                Release(baseColorIn);
                Release(normalTexel);
                if (usesBakedAo)
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
        /// Returns a <see cref="Process"/> result's render targets to the pool for
        /// reuse by later <see cref="Process"/> calls. Call after readback; afterwards
        /// the result is invalid (its targets are null) and must not be used again.
        /// </summary>
        public void ReleaseResult(NamerComputeResult result)
        {
            if (result == null)
            {
                return;
            }

            // NormalizedBaseColor and SourceBaseColor are both compute-pool-owned targets
            // (the projected base and the source base respectively); SourceBaseColor is null
            // in the non-projection path, and Release no-ops on null.
            _pool.Release(result.NormalizedBaseColor);
            _pool.Release(result.SourceBaseColor);

            if (result.ExtractedRoughness != null)
            {
                _roughnessPipeline.ReleaseRoughness(result.ExtractedRoughness);
            }

            _pool.Release(result.PackedSurface);
            result.NormalizedBaseColor = null;
            result.SourceBaseColor = null;
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
            bool roughnessDipApplied,
            int w,
            int h)
        {
            _compute.SetTexture(_kernelSurfacePack, "_Octahedral", octahedral);
            _compute.SetTexture(_kernelSurfacePack, "_PackInputs", packInputs);
            _compute.SetTexture(_kernelSurfacePack, "_SurfaceOut", surfaceOut);
            _compute.SetFloat("_HasExtractedRoughness", hasExtractedRoughness ? 1f : 0f);
            _compute.SetFloat("_RoughnessExtractStrength", roughnessExtractStrength);
            _compute.SetFloat("_RoughnessDipApplied", roughnessDipApplied ? 1f : 0f);
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
            Upload(source, target, fallback, unpackNormal: false);
        }

        internal static void Upload(Texture source, RenderTexture target, Texture2D fallback, bool unpackNormal)
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

            // Normal-map uploads go through the _UNPACK_NORMAL variant: a source
            // imported as TextureImporterType.NormalMap does not keep plain RGB on
            // the GPU (desktop DXT5 swizzles to (1, y, 1, x), BC5 stores (x, y, 0, 1)),
            // and the octahedral encode needs the authored [0,1] DirectX texel, not
            // the swizzled bytes. Normal-map textures are always linear, so the
            // sRGB-reencode and unpack variants never combine in practice.
            if (unpackNormal)
            {
                rawCopy.EnableKeyword(UnpackNormalKeyword);
            }
            else
            {
                rawCopy.DisableKeyword(UnpackNormalKeyword);
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
    /// In-memory result of <see cref="NamerComputePipeline.Process"/>. The render targets
    /// are leased from the pipeline's pool, not owned by the caller: read them back, then
    /// return them with <see cref="NamerComputePipeline.ReleaseResult"/>. After
    /// <see cref="NamerComputePipeline.ReleaseResult"/> the fields are null; if the pipeline
    /// is disposed while a result is still unreleased, its targets are destroyed with it and
    /// become unusable.
    /// </summary>
    public sealed class NamerComputeResult
    {
        public RenderTexture NormalizedBaseColor;
        public RenderTexture PackedSurface;

        /// <summary>
        /// The pre-projection SOURCE base (the normalize output), set only when the 04.2
        /// projection ran (else null). Kept alive for the residual-ON dividend (the honest
        /// source ÷ vcInterp removed-detail EXR) and the window's before-pane preview.
        /// Released by <see cref="NamerComputePipeline.ReleaseResult"/> alongside
        /// <see cref="NormalizedBaseColor"/>.
        /// </summary>
        public RenderTexture SourceBaseColor;

        /// <summary>
        /// The extracted roughness render target (roughness pipeline pool), set only when
        /// extraction ran. Null in the identity/legacy path (and when extraction was skipped).
        /// <see cref="NamerComputePipeline.ReleaseResult"/> owns releasing it. The D-07 debug
        /// channel binds it for the Extracted Roughness view.
        /// </summary>
        public RenderTexture ExtractedRoughness;

        public int Width;
        public int Height;
    }

    /// <summary>
    /// 04.2 projection context handed to <see cref="NamerComputePipeline.Process"/>. It owns
    /// the seam-safe split, the quantized vertex colors, the residual output, and the
    /// <see cref="Run"/> callback the pipeline invokes after the normalize stage. The callback
    /// reads the source base back, runs the vertex-color fit, and generates the residual
    /// against the SOURCE base while writing the Gouraud projection into
    /// <paramref name="projectedOut"/>.
    /// </summary>
    public sealed class NamerProjectionContext
    {
        /// <summary>The seam-safe split the projection and residual are computed against.</summary>
        public NamerSplitResult Split;

        /// <summary>Quantized vertex colors from the projection fit (the mesh color stream).</summary>
        public Color32[] Colors;

        /// <summary>The residual output produced by <see cref="Run"/> (residual-ON only; null when dropped).</summary>
        public NamerDecompOutput Decomp;

        /// <summary>Residual error threshold passed to <c>GenerateResidual</c>.</summary>
        public float ErrorThreshold;

        /// <summary>Coverage target passed to <c>GenerateResidual</c> (D-05 percentile gate).
        /// Defaults to <see cref="NamerEditorConstants.DefaultCoverageTarget"/> so a hand-built
        /// context that skips the field does not silently gate at 0 (the smallest rung).</summary>
        public float CoverageTarget = NamerEditorConstants.DefaultCoverageTarget;

        /// <summary>Residual-resolution popup index passed to <c>GenerateResidual</c>.</summary>
        public int ManualResolution;

        /// <summary>Write Residual checkbox: forces AlwaysKeep vs NeverKeep.</summary>
        public bool WriteResidual;

        /// <summary>
        /// Projection + residual callback. <c>sourceBase</c> is the normalized base (the
        /// residual-ON dividend); <c>projectedOut</c> is the caller-owned write-back target.
        /// </summary>
        public Action<RenderTexture, RenderTexture> Run;
    }
}

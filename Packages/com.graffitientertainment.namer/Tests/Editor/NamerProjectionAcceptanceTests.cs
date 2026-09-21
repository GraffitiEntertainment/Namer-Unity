using System;
using System.Collections;
using System.IO;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 04.2 plan 04 acceptance tests — the three user-visible ROADMAP outcomes proven
    /// end-to-end through <see cref="NamerProcessor.Process"/>, not just kernels (VCOL-04,
    /// SHDR-02). Each test maps to a phase-goal criterion:
    ///   criterion 1 (residual white by construction)              -> <see cref="Acceptance_WhitenessByIdentity_E2E"/>
    ///   criterion 2 (removed detail re-expressed as gloss)        -> <see cref="Acceptance_OneTextureDefault_NoResidualExr_GlossReexpressed"/> + <see cref="Acceptance_DipDepth_TasteSlider"/>
    ///   criterion 3 (one-texture default + checkbox)              -> <see cref="Acceptance_WriteResidualOn_SourceDividendExr"/>
    ///
    /// The fixture is a baked-speckle base: a low-frequency gradient (fittable by vertex
    /// colors) plus deterministic hash-noise grain (the removable "baked speckle"). The
    /// grain — not a luminance ramp — is the signal, because a pure ramp flattens to zero
    /// Sobel/luma (project-memory fixture lesson). Assertion discipline follows Pitfall 4:
    /// every quantitative pixel assertion reads an in-memory <see cref="RenderTexture"/>
    /// (packed surface or residual), never a re-read PNG/EXR; file-existence checks are
    /// path-based only. All GPU paths are capability-gated (D-15) and generated assets are
    /// cleaned up in <c>finally</c>.
    /// </summary>
    public class NamerProjectionAcceptanceTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";
        private const int WorkingSize = 128;
        private const int RoughnessMask = 0x3F;
        private const float ErrorThreshold = 0.02f;
        private const int ResidualResolution = 0;
        private const float DefaultDipDepth = 0.25f;
        private const float DeepDipDepth = 0.6f;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // --------------------------------------------------------------------
        // Criterion 2 + 3: one-texture default — no residual EXR, gloss re-expressed.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Acceptance_OneTextureDefault_NoResidualExr_GlossReexpressed()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU one-texture default acceptance test (D-15).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                // End-to-end default run: decomposition ON, RemovedDetail dip source, Write
                // Residual OFF, dip depth at the shipped default (0.25).
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", WorkingSize, BakedSpeckle);
                Texture2D occlusion = CreateImportedWhiteOcclusion(TempFolder + "/SourceOcclusion.png");
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap, occlusion);
                gameObject = CreateSceneObject(sourceMesh, source, "OneTextureDefaultTarget");

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                    DecompositionEnabled = true,
                    ErrorThreshold = ErrorThreshold,
                    ResidualResolution = ResidualResolution,
                    DipSource = (int)NamerDipSource.RemovedDetail,
                    WriteResidual = false,
                    RoughnessExtractStrength = DefaultDipDepth,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];

                // One-texture default: no residual EXR on disk; the split mesh still carries
                // the fitted vertex colors; the packed surface PNG is written.
                Assert.IsTrue(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "a default run must write no residual EXR (one-texture outcome)");
                Assert.IsFalse(string.IsNullOrEmpty(asset.MeshPath),
                    "the split mesh is still written in the one-texture outcome");
                Assert.IsFalse(string.IsNullOrEmpty(asset.SurfaceTexturePath),
                    "the packed surface PNG must be written");

                Mesh generatedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(asset.MeshPath);
                Assert.IsNotNull(generatedMesh, "generated mesh must load: " + asset.MeshPath);
                Assert.Greater(generatedMesh.colors32.Length, 0,
                    "the split mesh must carry non-empty Color32 vertex colors");

                // In-memory packed-surface alpha (never the re-read PNG — Pitfall 4): a
                // companion strength-0 run encodes the authored scalar exactly (roughness 1.0
                // -> 63 bits) while the default 0.25 run dips some texels below it (the
                // removed speckle re-expressed as gloss).
                int min0 = 0, max0 = 0, minDefault = 0, maxDefault = 0;
                using (NamerDecompPipeline decomp = new NamerDecompPipeline())
                using (NamerComputePipeline pipeline = new NamerComputePipeline())
                {
                    Mesh inMesh = CreateQuadMesh();
                    Texture2D inBase = CreateBaseMap(WorkingSize, BakedSpeckle);
                    Texture2D inOcclusion = CreateWhiteOcclusion();
                    NamerSplitResult split = MeshVertexSplitter.Split(inMesh);

                    NamerMaterialInspection ins0 = BuildInspection(inBase, inMesh, inOcclusion, 0f);
                    NamerProjectionContext ctx0 = BuildProjectionContext(split, decomp, WorkingSize, WorkingSize, false);
                    NamerComputeResult res0 = pipeline.Process(ins0, ctx0);
                    try
                    {
                        ReadResultAlphaBounds(res0, out min0, out max0);
                    }
                    finally
                    {
                        ctx0.Decomp?.Dispose();
                        pipeline.ReleaseResult(res0);
                    }

                    NamerMaterialInspection insDefault = BuildInspection(inBase, inMesh, inOcclusion, DefaultDipDepth);
                    NamerProjectionContext ctxDefault = BuildProjectionContext(split, decomp, WorkingSize, WorkingSize, false);
                    NamerComputeResult resDefault = pipeline.Process(insDefault, ctxDefault);
                    try
                    {
                        ReadResultAlphaBounds(resDefault, out minDefault, out maxDefault);
                    }
                    finally
                    {
                        ctxDefault.Decomp?.Dispose();
                        pipeline.ReleaseResult(resDefault);
                    }

                    Destroy(inBase, inOcclusion, inMesh);
                }

                Assert.AreEqual(63, max0, "strength 0 must pack the authored scalar exactly (63 bits)");
                Assert.AreEqual(63, min0, "strength 0 must leave every texel at the authored scalar");
                Assert.Less(minDefault, 63,
                    "the default dip-depth 0.25 must dip some texels below the authored scalar (removed speckle -> gloss)");
            }
            finally
            {
                Destroy(gameObject);
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Criterion 3: Write Residual ON — the source-dividend EXR carries removed detail.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Acceptance_WriteResidualOn_SourceDividendExr()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU Write-Residual acceptance test (D-15).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", WorkingSize, BakedSpeckle);
                Texture2D occlusion = CreateImportedWhiteOcclusion(TempFolder + "/SourceOcclusion.png");
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap, occlusion);
                gameObject = CreateSceneObject(sourceMesh, source, "WriteResidualTarget");

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                    DecompositionEnabled = true,
                    ErrorThreshold = ErrorThreshold,
                    ResidualResolution = ResidualResolution,
                    DipSource = (int)NamerDipSource.RemovedDetail,
                    WriteResidual = true,
                    RoughnessExtractStrength = DefaultDipDepth,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                Assert.IsFalse(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "Write Residual ON must write the residual EXR (the source-dividend removed-detail archive)");
                Texture2D writtenResidual = AssetDatabase.LoadAssetAtPath<Texture2D>(asset.ResidualTexturePath);
                Assert.IsNotNull(writtenResidual, "the residual EXR must load: " + asset.ResidualTexturePath);

                // In-memory residual quotient (never the re-read EXR — Pitfall 4): the source
                // dividend (gradient + speckle) divided by the projection (gradient) leaves a
                // non-white quotient in the speckle region — the honest removed-detail error.
                // Mean |q-1| must sit above a loose floor; a projection-dividend quotient
                // would instead be dead white (the criterion-1 pairing).
                double meanDeviation;
                using (NamerDecompPipeline decomp = new NamerDecompPipeline())
                using (NamerComputePipeline pipeline = new NamerComputePipeline())
                {
                    Mesh inMesh = CreateQuadMesh();
                    Texture2D inBase = CreateBaseMap(WorkingSize, BakedSpeckle);
                    Texture2D inOcclusion = CreateWhiteOcclusion();
                    NamerSplitResult split = MeshVertexSplitter.Split(inMesh);

                    NamerMaterialInspection inspection = BuildInspection(inBase, inMesh, inOcclusion, DefaultDipDepth);
                    NamerProjectionContext context = BuildProjectionContext(split, decomp, WorkingSize, WorkingSize, true);
                    NamerComputeResult computeResult = pipeline.Process(inspection, context);
                    try
                    {
                        Assert.IsNotNull(context.Decomp?.Residual,
                            "Write Residual ON must produce an in-memory residual render target");
                        Color[] residual = ReadBackFloat(context.Decomp.Residual);
                        meanDeviation = MeanMaxChannelDeviation(residual);
                    }
                    finally
                    {
                        context.Decomp?.Dispose();
                        pipeline.ReleaseResult(computeResult);
                    }

                    Destroy(inBase, inOcclusion, inMesh);
                }

                Assert.Greater(meanDeviation, 0.01,
                    "the source-dividend quotient must be non-white in the speckle region (mean |q-1| = "
                    + meanDeviation.ToString("0.000") + ") — a projection-dividend quotient would be white");
            }
            finally
            {
                Destroy(gameObject);
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Criterion 1: residual white by construction (dividend == divisor identity).
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Acceptance_WhitenessByIdentity_E2E()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU whiteness-by-identity acceptance test (D-15).");
                yield break;
            }

            const int w = WorkingSize;
            const int h = WorkingSize;
            const float threshold = ErrorThreshold;

            // Process the baked-speckle fixture: fit the vertex colors against the source,
            // write the Gouraud projection, then generate the residual with dividend := the
            // projection. dividend == divisor at every covered texel, so the quotient is 1.0
            // up to float16 dust (UV-overlap texels included — first-wins rasterization
            // writes the same surface into both operands).
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            RenderTexture sourceBase = CreateBakedSpeckleBase(w, h);
            RenderTexture projected = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);

            try
            {
                Color32[] colors = FitColors(split, sourceBase, w, h);

                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    // First pass: rasterize + write the Gouraud surface back into projected.
                    NamerDecompOutput first = pipeline.GenerateResidual(
                        split, colors, sourceBase, w, h, threshold, ResidualResolution, projected, NamerResidualMode.AlwaysKeep);
                    first.Dispose();

                    // Second pass: dividend == projection == divisor -> quotient white.
                    NamerDecompOutput second = pipeline.GenerateResidual(
                        split, colors, projected, w, h, threshold, ResidualResolution, null, NamerResidualMode.AlwaysKeep);
                    try
                    {
                        Assert.IsNotNull(second.Residual, "AlwaysKeep must produce a residual");
                        Assert.IsTrue(second.Stats.ResidualRequired);

                        Color[] residual = ReadBackFloat(second.Residual);
                        foreach (Color c in residual)
                        {
                            Assert.LessOrEqual(Mathf.Abs(c.r - 1f), 1f / 255f,
                                "quotient R must be white by construction (dividend == divisor)");
                            Assert.LessOrEqual(Mathf.Abs(c.g - 1f), 1f / 255f,
                                "quotient G must be white by construction (dividend == divisor)");
                            Assert.LessOrEqual(Mathf.Abs(c.b - 1f), 1f / 255f,
                                "quotient B must be white by construction (dividend == divisor)");
                        }
                    }
                    finally
                    {
                        second.Dispose();
                    }
                }
            }
            finally
            {
                Release(sourceBase, projected);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Criterion 2: dip-depth taste slider — deeper dip, wider roughness spread.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Acceptance_DipDepth_TasteSlider()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU dip-depth acceptance test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateBaseMap(WorkingSize, BakedSpeckle);
            Texture2D whiteOcclusion = CreateWhiteOcclusion();
            NamerSplitResult split = MeshVertexSplitter.Split(mesh);

            int min0 = 0, max0 = 0, minDeep = 0, maxDeep = 0;
            using (NamerDecompPipeline decomp = new NamerDecompPipeline())
            using (NamerComputePipeline pipeline = new NamerComputePipeline())
            {
                NamerMaterialInspection ins0 = BuildInspection(baseMap, mesh, whiteOcclusion, 0f);
                NamerProjectionContext ctx0 = BuildProjectionContext(split, decomp, WorkingSize, WorkingSize, false);
                NamerComputeResult res0 = pipeline.Process(ins0, ctx0);
                try
                {
                    ReadResultAlphaBounds(res0, out min0, out max0);
                }
                finally
                {
                    ctx0.Decomp?.Dispose();
                    pipeline.ReleaseResult(res0);
                }

                NamerMaterialInspection insDeep = BuildInspection(baseMap, mesh, whiteOcclusion, DeepDipDepth);
                NamerProjectionContext ctxDeep = BuildProjectionContext(split, decomp, WorkingSize, WorkingSize, false);
                NamerComputeResult resDeep = pipeline.Process(insDeep, ctxDeep);
                try
                {
                    ReadResultAlphaBounds(resDeep, out minDeep, out maxDeep);
                }
                finally
                {
                    ctxDeep.Decomp?.Dispose();
                    pipeline.ReleaseResult(resDeep);
                }
            }

            Destroy(baseMap, whiteOcclusion, mesh);

            int spread0 = max0 - min0;
            int spreadDeep = maxDeep - minDeep;
            Assert.Greater(spreadDeep, spread0,
                "the roughness spread must be strictly larger at dip-depth 0.6 than at 0 ("
                + spreadDeep + " vs " + spread0 + ")");
            Assert.GreaterOrEqual(minDeep, 0, "decoded roughness must stay within [0, 1]");
            Assert.LessOrEqual(maxDeep, 63, "decoded roughness must stay within [0, 1]");
            Assert.GreaterOrEqual(min0, 0, "decoded roughness must stay within [0, 1]");
            Assert.LessOrEqual(max0, 63, "decoded roughness must stay within [0, 1]");

            yield return null;
        }

        // --------------------------------------------------------------------
        // Projection-context + inspection helpers (mirror NamerProcessor's internal wiring).
        // --------------------------------------------------------------------

        /// <summary>Builds a projection context the way <see cref="NamerProcessor"/> does (its
        /// internal CreateProjectionContext is assembly-private, so the test mirrors the Run
        /// contract).</summary>
        private static NamerProjectionContext BuildProjectionContext(NamerSplitResult split, NamerDecompPipeline decomp, int w, int h, bool writeResidual)
        {
            var context = new NamerProjectionContext { Split = split };
            context.Run = (sourceBase, projectedOut) =>
            {
                NativeArray<Color32> texels = ReadBackColor32Native(sourceBase);
                try
                {
                    using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, w, h))
                    {
                        context.Colors = fit.ToColor32Array();
                        context.Decomp = decomp.GenerateResidual(
                            split, context.Colors, sourceBase, w, h,
                            ErrorThreshold, ResidualResolution, projectedOut,
                            writeResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep);
                    }
                }
                finally
                {
                    texels.Dispose();
                }
            };

            return context;
        }

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap, Mesh mesh, Texture2D occlusionMap, float strength)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                // Authored (white) occlusion keeps the D-07 gate on the authored branch so
                // the pipeline does NOT extract-and-un-multiply synthetic AO out of the
                // fixture's own gradient (which would flatten the speckle).
                OcclusionMap = occlusionMap,
                MetallicGlossMap = null, // baked speckle — no authored metallic/gloss map
                Metallic = 0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = strength,
                DipSource = NamerDipSource.RemovedDetail,
                Emissive = 0f,
                AoUnmultiplyStrength = 1f,
                SmoothnessTextureChannel = 0,
                BakeSourceMesh = mesh,
            };
        }

        /// <summary>Baked-speckle base: a low-frequency gradient (fittable by vertex colors)
        /// plus deterministic hash-noise grain (the removable speckle the Gouraud projection
        /// removes and the roughness transfer re-expresses as gloss).</summary>
        private static Color BakedSpeckle(int x, int y, int size)
        {
            float gradient = 0.5f + 0.4f * ((float)x / size - 0.5f);
            float grain = HashNoise(x, y) * 0.04f;
            float v = Mathf.Clamp01(gradient + grain);
            return new Color(v, v, v, 1f);
        }

        /// <summary>Deterministic hash noise in [-1, 1] (the project-memory fixture recipe).</summary>
        private static float HashNoise(int x, int y)
        {
            float f = Mathf.Sin(x * 12.9898f + y * 78.233f) * 43758.5453f;
            return (f - Mathf.Floor(f)) * 2f - 1f;
        }

        // -- readback helpers -------------------------------------------------

        private static NativeArray<Color32> ReadBackColor32Native(RenderTexture rt)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.forcePlayerLoopUpdate = true;
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "GPU readback must not error");
            return request.GetData<Color32>();
        }

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color> data = req.GetData<Color>();
            try
            {
                var result = new Color[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    result[i] = data[i];
                }

                return result;
            }
            finally
            {
                data.Dispose();
            }
        }

        private static void ReadResultAlphaBounds(NamerComputeResult result, out int minBits, out int maxBits)
        {
            NativeArray<Color32> data = ReadBackColor32Native(result.PackedSurface);
            try
            {
                minBits = 63;
                maxBits = 0;
                for (int i = 0; i < data.Length; i++)
                {
                    int bits = data[i].a & RoughnessMask;
                    minBits = Mathf.Min(minBits, bits);
                    maxBits = Mathf.Max(maxBits, bits);
                }
            }
            finally
            {
                data.Dispose();
            }
        }

        private static double MeanMaxChannelDeviation(Color[] texels)
        {
            double sum = 0.0;
            for (int i = 0; i < texels.Length; i++)
            {
                float r = Mathf.Abs(texels[i].r - 1f);
                float g = Mathf.Abs(texels[i].g - 1f);
                float b = Mathf.Abs(texels[i].b - 1f);
                sum += Mathf.Max(r, Mathf.Max(g, b));
            }

            return sum / texels.Length;
        }

        private static Color32[] FitColors(NamerSplitResult split, RenderTexture sourceBase, int w, int h)
        {
            NativeArray<Color32> texels = ReadBackColor32Native(sourceBase);
            try
            {
                using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, w, h))
                {
                    return fit.ToColor32Array();
                }
            }
            finally
            {
                texels.Dispose();
            }
        }

        // -- in-memory fixture helpers -----------------------------------------

        private static NamerSplitResult CreateSplitQuad(float uvMin, float uvMax)
        {
            return new NamerSplitResult
            {
                Positions = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(1f, 0f, 1f),
                    new Vector3(0f, 0f, 1f),
                },
                Normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                Tangents = new Vector4[4],
                Uvs = new[]
                {
                    new Vector2(uvMin, uvMin),
                    new Vector2(uvMax, uvMin),
                    new Vector2(uvMax, uvMax),
                    new Vector2(uvMin, uvMax),
                },
                SubMeshTriangles = new[] { new[] { 0, 2, 1, 0, 3, 2 } },
            };
        }

        private static Mesh CreateQuadMesh()
        {
            Mesh mesh = new Mesh { name = "BakedSpeckleQuad", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D CreateBaseMap(int size, Func<int, int, int, Color> pixel)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    texture.SetPixel(x, y, pixel(x, y, size));
                }
            }

            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateWhiteOcclusion()
        {
            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, true)
            {
                name = "WhiteOcclusion",
                hideFlags = HideFlags.HideAndDontSave,
            };
            texture.SetPixel(0, 0, Color.white);
            texture.Apply(false, false);
            return texture;
        }

        private static RenderTexture CreateBakedSpeckleBase(int w, int h)
        {
            return UploadBase(w, h, (x, y) => BakedSpeckle(x, y, w));
        }

        private static RenderTexture UploadBase(int w, int h, Func<int, int, Color> pixel)
        {
            Texture2D upload = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color[] pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[y * w + x] = pixel(x, y);
                }
            }

            upload.SetPixels(pixels);
            upload.Apply(false, false);

            RenderTexture rt = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);
            Graphics.Blit(upload, rt);
            DestroyImmediate(upload);
            return rt;
        }

        private static RenderTexture CreateRenderTexture(GraphicsFormat format, int w, int h)
        {
            RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(w, h, format, 0)
            {
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            });
            rt.Create();
            return rt;
        }

        // -- AssetDatabase fixture helpers (end-to-end Process tests) ---------

        private static Mesh CreateQuadMeshAsset(string path)
        {
            Mesh mesh = new Mesh { name = "SourceQuad" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        private static Texture2D CreateImportedBaseMap(string path, int size, Func<int, int, int, Color> pixel)
        {
            Texture2D source = CreateBaseMap(size, pixel);
            File.WriteAllBytes(path, source.EncodeToPNG());
            DestroyImmediate(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D CreateImportedWhiteOcclusion(string path)
        {
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, Color.white);
            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            DestroyImmediate(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material CreateSourceMaterial(string folder, string name, Texture2D baseMap, Texture2D occlusionMap)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetTexture("_OcclusionMap", occlusionMap);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0f);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static GameObject CreateSceneObject(Mesh mesh, Material material, string name)
        {
            GameObject go = new GameObject(name);
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return go;
        }

        private static void EnsureTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_Tests_Temp");
        }

        private static void Release(params RenderTexture[] rts)
        {
            foreach (RenderTexture rt in rts)
            {
                if (rt != null)
                {
                    // Graphics.Blit (the UploadBase path) leaves RenderTexture.active pointing
                    // at its destination; unbind before release (mirrors ComputeTexturePool's
                    // guard and the sibling test-suite Release helpers).
                    if (RenderTexture.active == rt)
                    {
                        RenderTexture.active = null;
                    }

                    rt.Release();
                    DestroyImmediate(rt);
                }
            }
        }

        private static void Destroy(params UnityEngine.Object[] objects)
        {
            foreach (UnityEngine.Object o in objects)
            {
                if (o != null)
                {
                    DestroyImmediate(o);
                }
            }
        }

        private static void DestroyImmediate(UnityEngine.Object o)
        {
            if (o != null)
            {
                UnityEngine.Object.DestroyImmediate(o);
            }
        }

        // -- EditorPrefs isolation ---------------------------------------------

        private sealed class PrefsSnapshot
        {
            public string Destination;
            public string Prefix;
            public string Suffix;
            public bool OverwriteGenerated;
            public bool DecompositionEnabled;
            public float ErrorThreshold;
            public int ResidualResolution;
            public float RoughnessExtractStrength;
            public int DipSource;
            public bool WriteResidual;
            public bool HadDestination;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
            public bool HadDecompositionEnabled;
            public bool HadErrorThreshold;
            public bool HadResidualResolution;
            public bool HadRoughnessExtractStrength;
            public bool HadDipSource;
            public bool HadWriteResidual;
        }

        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Destination = EditorPrefs.GetString("NamerProcessor.Destination", string.Empty),
                Prefix = EditorPrefs.GetString("NamerProcessor.Prefix", string.Empty),
                Suffix = EditorPrefs.GetString("NamerProcessor.Suffix", string.Empty),
                OverwriteGenerated = EditorPrefs.GetBool("NamerProcessor.OverwriteGenerated", false),
                DecompositionEnabled = EditorPrefs.GetBool("NamerProcessor.DecompositionEnabled", false),
                ErrorThreshold = EditorPrefs.GetFloat("NamerProcessor.ErrorThreshold", 0.02f),
                ResidualResolution = EditorPrefs.GetInt("NamerProcessor.ResidualResolution", 0),
                RoughnessExtractStrength = EditorPrefs.GetFloat("NamerProcessor.RoughnessExtractStrength", 1f),
                DipSource = EditorPrefs.GetInt("NamerProcessor.DipSource", 0),
                WriteResidual = EditorPrefs.GetBool("NamerProcessor.WriteResidual", false),
                HadDestination = EditorPrefs.HasKey("NamerProcessor.Destination"),
                HadPrefix = EditorPrefs.HasKey("NamerProcessor.Prefix"),
                HadSuffix = EditorPrefs.HasKey("NamerProcessor.Suffix"),
                HadOverwriteGenerated = EditorPrefs.HasKey("NamerProcessor.OverwriteGenerated"),
                HadDecompositionEnabled = EditorPrefs.HasKey("NamerProcessor.DecompositionEnabled"),
                HadErrorThreshold = EditorPrefs.HasKey("NamerProcessor.ErrorThreshold"),
                HadResidualResolution = EditorPrefs.HasKey("NamerProcessor.ResidualResolution"),
                HadRoughnessExtractStrength = EditorPrefs.HasKey("NamerProcessor.RoughnessExtractStrength"),
                HadDipSource = EditorPrefs.HasKey("NamerProcessor.DipSource"),
                HadWriteResidual = EditorPrefs.HasKey("NamerProcessor.WriteResidual"),
            };
        }

        private static void RestorePrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadDestination) { EditorPrefs.SetString("NamerProcessor.Destination", snapshot.Destination); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Destination"); }

            if (snapshot.HadPrefix) { EditorPrefs.SetString("NamerProcessor.Prefix", snapshot.Prefix); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Prefix"); }

            if (snapshot.HadSuffix) { EditorPrefs.SetString("NamerProcessor.Suffix", snapshot.Suffix); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Suffix"); }

            if (snapshot.HadOverwriteGenerated) { EditorPrefs.SetBool("NamerProcessor.OverwriteGenerated", snapshot.OverwriteGenerated); }
            else { EditorPrefs.DeleteKey("NamerProcessor.OverwriteGenerated"); }

            if (snapshot.HadDecompositionEnabled) { EditorPrefs.SetBool("NamerProcessor.DecompositionEnabled", snapshot.DecompositionEnabled); }
            else { EditorPrefs.DeleteKey("NamerProcessor.DecompositionEnabled"); }

            if (snapshot.HadErrorThreshold) { EditorPrefs.SetFloat("NamerProcessor.ErrorThreshold", snapshot.ErrorThreshold); }
            else { EditorPrefs.DeleteKey("NamerProcessor.ErrorThreshold"); }

            if (snapshot.HadResidualResolution) { EditorPrefs.SetInt("NamerProcessor.ResidualResolution", snapshot.ResidualResolution); }
            else { EditorPrefs.DeleteKey("NamerProcessor.ResidualResolution"); }

            if (snapshot.HadRoughnessExtractStrength) { EditorPrefs.SetFloat("NamerProcessor.RoughnessExtractStrength", snapshot.RoughnessExtractStrength); }
            else { EditorPrefs.DeleteKey("NamerProcessor.RoughnessExtractStrength"); }

            if (snapshot.HadDipSource) { EditorPrefs.SetInt("NamerProcessor.DipSource", snapshot.DipSource); }
            else { EditorPrefs.DeleteKey("NamerProcessor.DipSource"); }

            if (snapshot.HadWriteResidual) { EditorPrefs.SetBool("NamerProcessor.WriteResidual", snapshot.WriteResidual); }
            else { EditorPrefs.DeleteKey("NamerProcessor.WriteResidual"); }
        }
    }
}

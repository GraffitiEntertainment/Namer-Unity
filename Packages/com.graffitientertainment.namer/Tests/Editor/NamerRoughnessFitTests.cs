using System;
using System.Collections;
using System.IO;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 04.2 projection-era roughness tests (plan 03, flipped from the retired 04.1
    /// fit-driven suite). Proves (1) the by-construction residual collapse (the projection makes
    /// base ÷ vcInterp white, so WriteResidual OFF writes no EXR), (2) the packed surface dip
    /// follows the removed-detail transfer (strength 0 = authored scalar, strength &gt; 0 = dip),
    /// (3) the dip signal is removed-luminance rather than a Sobel edge (a smooth interior patch
    /// dips under RemovedDetail), and (4) the default-on one-texture end-to-end acceptance.
    /// GPU paths are capability-gated (D-15) — they skip with an explicit report when
    /// compute/async-readback is unavailable, never a silent pass.
    /// </summary>
    public class NamerRoughnessFitTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";
        private const int WorkingSize = 64;
        private const int RoughnessMask = 0x3F;
        private const float ErrorThreshold = 0.02f;
        private const int ResidualResolution = 0;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // --------------------------------------------------------------------
        // 1. Projection collapse (end-to-end)
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Projection_BakedResponse_ResidualCollapsesByConstruction()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU projection-collapse test (D-15).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", WorkingSize, BakedResponse);
                Texture2D occlusion = CreateImportedWhiteOcclusion(TempFolder + "/SourceOcclusion.png");
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap, occlusion);
                gameObject = CreateSceneObject(sourceMesh, source, "ProjectionTarget");

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
                    RoughnessExtractStrength = NamerEditorConstants.DefaultRoughnessExtractStrength,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                Assert.IsFalse(string.IsNullOrEmpty(asset.MeshPath), "decomposition ON writes the split mesh");
                Assert.IsTrue(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "the projection makes base ÷ vcInterp white by construction — WriteResidual OFF must write no residual EXR");

                // The written split mesh carries Color32 vertex colors.
                Mesh generatedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(asset.MeshPath);
                Assert.IsNotNull(generatedMesh, "generated mesh must load: " + asset.MeshPath);
                Assert.Greater(generatedMesh.colors32.Length, 0, "the split mesh must carry Color32 vertex colors");

                // The packed surface is written.
                Assert.IsFalse(string.IsNullOrEmpty(asset.SurfaceTexturePath), "the packed surface PNG must be written");
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
        // 2. Packed surface dip follows the transfer (pipeline-level)
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Transfer_PackedRoughness_DipsBySliderDepth()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU pack-adoption test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateBaseMap(WorkingSize, BakedResponse);
            Texture2D whiteOcclusion = CreateWhiteOcclusion();
            NamerSplitResult split = MeshVertexSplitter.Split(mesh);

            using (NamerDecompPipeline decomp = new NamerDecompPipeline())
            using (NamerComputePipeline pipeline = new NamerComputePipeline())
            {
                // Strength 0: no extraction runs, so the packed alpha encodes the authored
                // scalar (roughness 1.0 -> 63 bits) exactly.
                NamerMaterialInspection ins0 = BuildInspection(baseMap, mesh, whiteOcclusion, 0f);
                NamerProjectionContext ctx0 = BuildProjectionContext(split, decomp);
                NamerComputeResult result0 = pipeline.Process(ins0, ctx0);
                int min0, max0;
                try
                {
                    ReadResultAlphaBits(result0, out min0, out max0);
                }
                finally
                {
                    ctx0.Decomp?.Dispose();
                    pipeline.ReleaseResult(result0);
                }

                Assert.AreEqual(63, max0, "strength 0 must pack the authored scalar exactly (63 bits)");
                Assert.AreEqual(63, min0, "strength 0 must leave every texel at the authored scalar");

                // Strength 0.5: the removed-detail transfer runs and dips some texels below scalar.
                NamerMaterialInspection ins5 = BuildInspection(baseMap, mesh, whiteOcclusion, 0.5f);
                NamerProjectionContext ctx5 = BuildProjectionContext(split, decomp);
                NamerComputeResult result5 = pipeline.Process(ins5, ctx5);
                int min5, max5;
                try
                {
                    ReadResultAlphaBits(result5, out min5, out max5);
                }
                finally
                {
                    ctx5.Decomp?.Dispose();
                    pipeline.ReleaseResult(result5);
                }

                Assert.Less(min5, 63, "strength 0.5 must dip some texels below the authored scalar (dip present)");
            }

            Destroy(baseMap, whiteOcclusion, mesh);
            yield return null;
        }

        // --------------------------------------------------------------------
        // 3. The dip signal is removed-luminance, not a Sobel edge
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Transfer_Roughness_UsesRemovedLumaNotSobel()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU removed-luma signal test (D-15).");
                yield break;
            }

            // A smooth interior bump (no sharp edges): the quad's vertex-color fit is ~the
            // corner value everywhere, so the Gouraud projection removes the bump — removed
            // luma is large at the bump center and ~0 at the corners. Sobel-of-base is ~0 in
            // the smooth interior, so a Sobel signal would NOT dip the center; removed-luma
            // does. The discriminator is smoothness.
            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateBaseMap(WorkingSize, SmoothBump);
            Texture2D whiteOcclusion = CreateWhiteOcclusion();
            NamerSplitResult split = MeshVertexSplitter.Split(mesh);

            using (NamerDecompPipeline decomp = new NamerDecompPipeline())
            using (NamerComputePipeline pipeline = new NamerComputePipeline())
            {
                NamerMaterialInspection inspection = BuildInspection(baseMap, mesh, whiteOcclusion, 0.5f);
                NamerProjectionContext context = BuildProjectionContext(split, decomp);
                NamerComputeResult result = pipeline.Process(inspection, context);
                try
                {
                    Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                    int centerIndex = (WorkingSize / 2) * WorkingSize + (WorkingSize / 2);
                    int cornerIndex = 0;
                    int centerBits = surface[centerIndex].a & RoughnessMask;
                    int cornerBits = surface[cornerIndex].a & RoughnessMask;

                    Assert.Less(centerBits, cornerBits,
                        "the smooth interior bump must dip BELOW the quiet corner under RemovedDetail "
                        + "(removed-luma is high at the center, ~0 at the corner; a Sobel signal would "
                        + "leave the smooth interior undipped). center=" + centerBits + " corner=" + cornerBits);
                }
                finally
                {
                    context.Decomp?.Dispose();
                    pipeline.ReleaseResult(result);
                }
            }

            Destroy(baseMap, whiteOcclusion, mesh);
            yield return null;
        }

        // --------------------------------------------------------------------
        // 4. Default-on one-texture acceptance (end-to-end)
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator DefaultOn_OneTextureWithDip()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU default-on end-to-end test (D-15).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", WorkingSize, BakedResponse);
                Texture2D occlusion = CreateImportedWhiteOcclusion(TempFolder + "/SourceOcclusion.png");
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap, occlusion);
                gameObject = CreateSceneObject(sourceMesh, source, "DefaultOnTarget");

                // Fresh-default controls: decomposition on, RemovedDetail dip source, Write
                // Residual off, dip depth at the shipped default (0.25).
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
                    RoughnessExtractStrength = NamerEditorConstants.DefaultRoughnessExtractStrength,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                Assert.IsFalse(string.IsNullOrEmpty(asset.MeshPath), "decomposition ON writes the split mesh");
                Assert.IsTrue(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "default WriteResidual OFF produces the one-texture outcome (no residual EXR)");

                // The default dip-depth 0.25 transfers the removed-luma into the packed alpha.
                ReadSurfaceAlphaBits(asset.SurfaceTexturePath, out int minBits, out int maxBits);
                Assert.Less(minBits, 63,
                    "the default-on transfer must pack a non-scalar roughness dip into surface alpha bits 0-5");

                Material generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(asset.MaterialPath);
                Assert.IsNotNull(generatedMaterial, "generated material must load: " + asset.MaterialPath);
                Assert.IsNull(generatedMaterial.GetTexture("_BaseResidualMap"),
                    "no residual written => _BaseResidualMap stays unbound (white default)");
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

        /// <summary>Builds a projection context the way NamerProcessor does (its internal
        /// CreateProjectionContext is assembly-private, so the test mirrors the Run contract).</summary>
        private static NamerProjectionContext BuildProjectionContext(NamerSplitResult split, NamerDecompPipeline decomp)
        {
            var context = new NamerProjectionContext { Split = split };
            context.Run = (sourceBase, projectedOut) =>
            {
                NativeArray<Color32> texels = ReadBack(sourceBase);
                try
                {
                    using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, WorkingSize, WorkingSize))
                    {
                        context.Colors = fit.ToColor32Array();
                        context.Decomp = decomp.GenerateResidual(
                            split, context.Colors, sourceBase, WorkingSize, WorkingSize,
                            ErrorThreshold, ResidualResolution, projectedOut, NamerResidualMode.NeverKeep);
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
                // Authored (white) occlusion keeps the D-07 gate on the authored branch: with
                // a null map the pipeline EXTRACTS AO from the base's own luminance and the
                // normalize pass un-multiplies it, so the fixture's gradient is read as baked
                // shading and the base is destroyed. The fixtures isolate the projection/transfer
                // behavior, so no AO may interfere.
                OcclusionMap = occlusionMap,
                MetallicGlossMap = null, // baked response — no authored metallic/gloss map
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

        /// <summary>Baked-response base: a low-frequency gradient (fittable by vertex colors)
        /// with a baked high-frequency gloss detail (what the Gouraud projection removes).</summary>
        private static Color BakedResponse(int x, int y, int size)
        {
            float gradient = (float)x / size;
            float gloss = 0.10f * Mathf.Sin(x * 0.6f) * Mathf.Sin(y * 0.6f);
            float v = Mathf.Clamp01(gradient + gloss);
            return new Color(v, v, v, 1f);
        }

        /// <summary>Smooth interior bump (no sharp edges): the removed-luma is large at the
        /// center and ~0 at the corners; the Sobel magnitude is ~0 across the smooth interior.</summary>
        private static Color SmoothBump(int x, int y, int size)
        {
            float cx = (size - 1) * 0.5f;
            float cy = (size - 1) * 0.5f;
            float dx = (x - cx) / (size * 0.45f);
            float dy = (y - cy) / (size * 0.45f);
            float g = Mathf.Exp(-(dx * dx + dy * dy));
            return new Color(g, g, g, 1f);
        }

        // -- fixture helpers -------------------------------------------------

        private static Mesh CreateQuadMesh()
        {
            Mesh mesh = new Mesh { name = "BakedResponseQuad", hideFlags = HideFlags.HideAndDontSave };
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

        private static NativeArray<Color32> ReadBack(RenderTexture rt)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            request.forcePlayerLoopUpdate = true;
            request.WaitForCompletion();
            Assert.IsFalse(request.hasError, "GPU readback must not error");
            return request.GetData<Color32>();
        }

        private static Color32[] ReadBackColor32(RenderTexture rt, int expectedCount)
        {
            NativeArray<Color32> data = ReadBack(rt);
            try
            {
                Assert.AreEqual(expectedCount, data.Length, "readback must return the expected pixel count");
                var result = new Color32[data.Length];
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

        private static void ReadResultAlphaBits(NamerComputeResult result, out int minBits, out int maxBits)
        {
            Color32[] surface = ReadBackColor32(result.PackedSurface, result.Width * result.Height);
            minBits = 63;
            maxBits = 0;
            for (int i = 0; i < surface.Length; i++)
            {
                int bits = surface[i].a & RoughnessMask;
                minBits = Mathf.Min(minBits, bits);
                maxBits = Mathf.Max(maxBits, bits);
            }
        }

        private static void ReadSurfaceAlphaBits(string path, out int minBits, out int maxBits)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            bool loaded = ImageConversion.LoadImage(texture, bytes);
            Assert.IsTrue(loaded, "failed to decode PNG at " + path);
            Color32[] pixels = texture.GetPixels32();
            Destroy(texture);

            minBits = 63;
            maxBits = 0;
            for (int i = 0; i < pixels.Length; i++)
            {
                int bits = pixels[i].a & RoughnessMask;
                minBits = Mathf.Min(minBits, bits);
                maxBits = Mathf.Max(maxBits, bits);
            }
        }

        // -- AssetDatabase fixture helpers --------------------------------

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
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        /// <summary>In-memory 1x1 white linear occlusion map (pipeline-direct fixtures).</summary>
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

        /// <summary>Persisted 1x1 white linear occlusion map (material-driven fixtures — the
        /// source material is a persisted asset, so the bound texture must be one too).</summary>
        private static Texture2D CreateImportedWhiteOcclusion(string path)
        {
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, Color.white);
            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material CreateSourceMaterial(string folder, string name, Texture2D baseMap, Texture2D occlusionMap = null)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            // Same D-07 isolation as BuildInspection: an authored white _OcclusionMap keeps
            // the pipeline from extracting (and un-multiplying) synthetic AO out of the base's
            // own gradient.
            if (occlusionMap != null)
            {
                material.SetTexture("_OcclusionMap", occlusionMap);
            }

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

        private static void Destroy(params UnityEngine.Object[] objects)
        {
            foreach (UnityEngine.Object o in objects)
            {
                if (o != null)
                {
                    UnityEngine.Object.DestroyImmediate(o);
                }
            }
        }

        // -- EditorPrefs isolation ------------------------------------------

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

using System;
using System.Collections;
using System.Collections.Generic;
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
    /// Phase 04.1 fit-driven roughness estimator tests (plan 02). Proves (1) the deterministic
    /// first-passing minimal-strength search, (2) the extraction-driven residual collapse via
    /// the post-extraction (sharp-removal-cleaned) base refit (D-05), and (3) the 2A default-on
    /// end-to-end acceptance through <see cref="NamerProcessor.Process"/>. GPU paths are
    /// capability-gated (D-15) — they skip with an explicit report when compute/async-readback
    /// is unavailable, never a silent pass.
    /// </summary>
    public class NamerRoughnessFitTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";
        private const int WorkingSize = 64;
        private const int RoughnessMask = 0x3F;
        private const float ErrorThreshold = 0.02f;
        // Test-local fit/D-13 threshold for the three collapse tests. Measured on the
        // white-occlusion BakedResponse fixture: the fit ladder's first passing strength is
        // 0.70 with a fit-only maxError of 0.0438, so 0.06 gates the collapse with margin
        // while the un-extracted error is 0.1098 (the full gloss amplitude) — passing
        // still requires genuine extraction. The honest-gate test keeps the shipped 0.02.
        private const float CollapseErrorThreshold = 0.06f;
        private const int ResidualResolution = 0;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // -- Pure-CPU minimizer (no GPU dispatch) ----------------------------

        [Test]
        public void FitDriven_SelectsFirstPassingStrength()
        {
            // A U-shaped objective whose global minimum is 0.5: the ascending walk must return
            // the FIRST passing strength (0.3), NOT the global argmin (0.5).
            var maxErrorByStrength = new Dictionary<float, float>
            {
                { 0.0f, 0.50f },
                { 0.15f, 0.20f },
                { 0.3f, 0.015f },
                { 0.5f, 0.005f },
                { 0.7f, 0.008f },
                { 0.9f, 0.012f },
                { 1.0f, 0.018f },
            };

            var visited = new List<float>();
            NamerRoughnessFitResult result = NamerRoughnessFitter.Fit(
                strength =>
                {
                    visited.Add(strength);
                    return maxErrorByStrength[strength];
                },
                shouldCancel: () => false,
                maxErrorThreshold: ErrorThreshold);

            Assert.IsTrue(result.Passed, "the fit must pass within the threshold");
            Assert.IsFalse(result.Cancelled, "the fit must not cancel");
            Assert.AreEqual(0.3f, result.Strength, 1e-6f,
                "first-passing minimal strength must be 0.3, NOT the global argmin 0.5");

            // The ladder walked the fixed StrengthLadder deterministically, ascending, and
            // stopped at the first pass (never System.Random, never a full argmin scan).
            Assert.AreEqual(3, visited.Count, "must stop after three steps (0.0, 0.15, 0.3)");
            Assert.AreEqual(0.0f, visited[0], 1e-6f, "ladder must start at 0.0");
            Assert.AreEqual(0.15f, visited[1], 1e-6f, "ladder second step must be 0.15");
            Assert.AreEqual(0.3f, visited[2], 1e-6f, "ladder third step must be 0.3");

            // An always-above-threshold objective returns the max strength with Passed == false.
            NamerRoughnessFitResult failing = NamerRoughnessFitter.Fit(
                _ => 1.0f, shouldCancel: () => false, maxErrorThreshold: ErrorThreshold);
            Assert.AreEqual(1.0f, failing.Strength, 1e-6f,
                "a no-pass search returns the max ladder strength (1.0)");
            Assert.IsFalse(failing.Passed, "an always-above-threshold objective must not pass");
            Assert.IsFalse(failing.Cancelled);
        }

        // -- Fit-driven extraction collapse (D-05) ---------------------------

        [UnityTest]
        public IEnumerator FitDriven_BakedResponse_ResidualCollapses()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU fit-driven collapse test (D-15).");
                yield break;
            }

            Mesh mesh = CreateQuadMesh();
            Texture2D baseMap = CreateBaseMap(WorkingSize, BakedResponse);
            Texture2D whiteOcclusion = CreateWhiteOcclusion();
            NamerMaterialInspection inspection = BuildFitDrivenInspection(baseMap, mesh, whiteOcclusion, strength: 1f);

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                Func<float, float> evaluate = ComposeEvaluate(pipeline, split);

                NamerComputeResult result = pipeline.Process(inspection, evaluate, null, CollapseErrorThreshold);
                try
                {
                    // D-05: NormalizedBaseColor is the sharp-removal-cleaned base in the
                    // fit-driven path, so this refit consumes the post-extraction base.
                    NamerDecompErrorStats stats = Decompose(split, result.NormalizedBaseColor, CollapseErrorThreshold);
                    Color32[] cleanedTexels = ReadBackColor32(result.NormalizedBaseColor, WorkingSize * WorkingSize);
                    byte minAlpha = 255;
                    foreach (Color32 t in cleanedTexels)
                    {
                        minAlpha = Math.Min(minAlpha, t.a);
                    }

                    Assert.IsFalse(stats.ResidualRequired,
                        "fit-driven extraction must collapse the residual (D-05 refit consumes the cleaned base); fit-only maxError="
                        + stats.FitOnlyMaxError.ToString("F4") + " vs threshold " + CollapseErrorThreshold
                        + "; cleaned-base minAlpha=" + (minAlpha / 255f).ToString("F4")
                        + "; coverage=" + stats.Coverage.ToString("F4")
                        + "; cannotDecompose=" + stats.CannotDecompose);

                    Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);
                    int maxBits = 0;
                    for (int i = 0; i < surface.Length; i++)
                    {
                        maxBits = Mathf.Max(maxBits, surface[i].a & RoughnessMask);
                    }

                    Assert.Greater(maxBits, 0,
                        "packed surface alpha bits 0-5 must carry non-zero extracted roughness where the base had baked gloss");
                }
                finally
                {
                    pipeline.ReleaseResult(result);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            Destroy(baseMap, whiteOcclusion, mesh);
            yield return null;
        }

        // -- 2A: default-on end-to-end acceptance ---------------------------

        [UnityTest]
        public IEnumerator FitDriven_DefaultOn_DecomposedNoMapAsset_ExtractsAndDropsResidual()
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
                gameObject = CreateSceneObject(sourceMesh, source, "FitDrivenDefaultTarget");

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                    DecompositionEnabled = true,
                    ErrorThreshold = CollapseErrorThreshold,
                    ResidualResolution = ResidualResolution,
                    // Pinned explicitly (2026-09-15 regression): both live in EditorPrefs,
                    // and the live user session held Estimator = Sobel, which routed this
                    // "DefaultOn" run through the Sobel path and kept the residual. The
                    // file's CapturePrefs/RestorePrefs keeps the user's session intact.
                    RoughnessEstimator = (int)NamerRoughnessEstimator.FitDriven,
                    RoughnessExtractStrength = NamerEditorConstants.DefaultRoughnessExtractStrength,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                Assert.IsFalse(string.IsNullOrEmpty(asset.MeshPath), "decomposition ON writes the split mesh");
                Assert.IsTrue(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "fit-driven extraction must collapse the residual (no residual written)");

                // Extraction ran: the packed surface alpha bits 0-5 carry non-trivial roughness.
                Color32 packed = ReadPngPixel32(asset.SurfaceTexturePath);
                Assert.Greater((int)(packed.a & RoughnessMask), 0,
                    "default-on fit-driven extraction must pack non-zero roughness into alpha bits 0-5");

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

        /// <summary>Composes the 3A evaluate callback the way NamerProcessor does: per-strength
        /// GPU sharp-removal -> readback -> vertex-color refit + residual -> post-refit MaxError.</summary>
        private static Func<float, float> ComposeEvaluate(NamerComputePipeline pipeline, NamerSplitResult split)
        {
            return strength =>
            {
                RenderTexture cleaned = pipeline.ExtractSharpRemoval(WorkingSize, WorkingSize, strength);
                try
                {
                    // FitOnlyMaxError (the D-13 gate statistic, residual == identity) is the
                    // search objective — mirroring NamerProcessor.EvaluateRefitMaxError. The
                    // with-residual MaxError reconstructs near-perfectly whenever the residual
                    // is kept (it is the exact quotient base/vc) and would stop the ladder at
                    // strength 0.0. Both stats are threshold-independent (the threshold only
                    // drives the D-13 ResidualRequired gate / resolution search), so the
                    // evaluate callback keeps the shipped ErrorThreshold while the fit uses
                    // CollapseErrorThreshold.
                    return Decompose(split, cleaned, ErrorThreshold).FitOnlyMaxError;
                }
                finally
                {
                    pipeline.ReleaseExtractedRoughness(cleaned);
                }
            };
        }

        private static NamerDecompErrorStats Decompose(NamerSplitResult split, RenderTexture baseRt, float threshold)
        {
            NativeArray<Color32> texels = ReadBack(baseRt);
            try
            {
                using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, WorkingSize, WorkingSize))
                {
                    Color32[] colors = fit.ToColor32Array();
                    using (NamerDecompPipeline decomp = new NamerDecompPipeline())
                    using (NamerDecompOutput output = decomp.GenerateResidual(
                        split, colors, baseRt, WorkingSize, WorkingSize, threshold, ResidualResolution))
                    {
                        return output.Stats;
                    }
                }
            }
            finally
            {
                texels.Dispose();
            }
        }

        private static NamerMaterialInspection BuildFitDrivenInspection(Texture2D baseMap, Mesh mesh, Texture2D occlusionMap, float strength)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                // Authored (white) occlusion keeps the D-07 gate on the authored branch: with
                // a null map the pipeline EXTRACTS AO from the base's own luminance and the
                // normalize pass un-multiplies it, so the BakedResponse gradient is read as
                // baked shading and the left half of NormalizedBaseColor is destroyed
                // (measured fit-only error 0.2869 — un-fittable, collapse impossible). The
                // fixtures isolate vertex-fit/extraction behavior, so no AO may interfere.
                OcclusionMap = occlusionMap,
                MetallicGlossMap = null, // baked response — no authored metallic/gloss map
                Metallic = 0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = strength,
                RoughnessEstimator = NamerRoughnessEstimator.FitDriven,
                Emissive = 0f,
                AoUnmultiplyStrength = 1f,
                SmoothnessTextureChannel = 0,
                BakeSourceMesh = mesh,
            };
        }

        /// <summary>Baked-response base: a low-frequency gradient (fittable by vertex colors)
        /// with a baked high-frequency gloss detail (what the fit-driven extraction removes).</summary>
        private static Color BakedResponse(int x, int y, int size)
        {
            float gradient = (float)x / size;
            float gloss = 0.10f * Mathf.Sin(x * 0.6f) * Mathf.Sin(y * 0.6f);
            float v = Mathf.Clamp01(gradient + gloss);
            return new Color(v, v, v, 1f);
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

        // -- AssetDatabase fixture helpers (test 3) --------------------------

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
            // Same D-07 isolation as BuildFitDrivenInspection: an authored white
            // _OcclusionMap keeps the pipeline from extracting (and un-multiplying)
            // synthetic AO out of the base's own gradient.
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

        private static Color32 ReadPngPixel32(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            bool loaded = ImageConversion.LoadImage(texture, bytes);
            Assert.IsTrue(loaded, "failed to decode PNG at " + path);
            Color pixel = texture.GetPixel(0, 0);
            Destroy(texture);
            return new Color32(
                (byte)Mathf.RoundToInt(pixel.r * 255f),
                (byte)Mathf.RoundToInt(pixel.g * 255f),
                (byte)Mathf.RoundToInt(pixel.b * 255f),
                (byte)Mathf.RoundToInt(pixel.a * 255f));
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
            public int RoughnessEstimator;
            public bool HadDestination;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
            public bool HadDecompositionEnabled;
            public bool HadErrorThreshold;
            public bool HadResidualResolution;
            public bool HadRoughnessExtractStrength;
            public bool HadRoughnessEstimator;
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
                RoughnessEstimator = EditorPrefs.GetInt("NamerProcessor.RoughnessEstimator", 0),
                HadDestination = EditorPrefs.HasKey("NamerProcessor.Destination"),
                HadPrefix = EditorPrefs.HasKey("NamerProcessor.Prefix"),
                HadSuffix = EditorPrefs.HasKey("NamerProcessor.Suffix"),
                HadOverwriteGenerated = EditorPrefs.HasKey("NamerProcessor.OverwriteGenerated"),
                HadDecompositionEnabled = EditorPrefs.HasKey("NamerProcessor.DecompositionEnabled"),
                HadErrorThreshold = EditorPrefs.HasKey("NamerProcessor.ErrorThreshold"),
                HadResidualResolution = EditorPrefs.HasKey("NamerProcessor.ResidualResolution"),
                HadRoughnessExtractStrength = EditorPrefs.HasKey("NamerProcessor.RoughnessExtractStrength"),
                HadRoughnessEstimator = EditorPrefs.HasKey("NamerProcessor.RoughnessEstimator"),
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

            if (snapshot.HadRoughnessEstimator) { EditorPrefs.SetInt("NamerProcessor.RoughnessEstimator", snapshot.RoughnessEstimator); }
            else { EditorPrefs.DeleteKey("NamerProcessor.RoughnessEstimator"); }
        }
    }
}

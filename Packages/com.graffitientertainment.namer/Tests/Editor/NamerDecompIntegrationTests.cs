using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// TEST-02 / D-06 / D-07 / D-13: proves the end-to-end vertex-color decomposition flow
    /// through <see cref="NamerProcessor.Process"/>. Builds a real scene GameObject with a
    /// persistent quad mesh + URP Lit source material, runs Process with decomposition ON
    /// and OFF, and asserts the written mesh + residual + material bind + scene sharedMesh
    /// swap + source immutability. GPU path is <c>[UnityTest]</c> with the compute /
    /// async-readback capability gate (D-15); the seven <c>NamerProcessor.*</c> EditorPrefs
    /// keys are snapshotted and restored.
    /// </summary>
    public class NamerDecompIntegrationTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";
        private const string DecompKey = "NamerProcessor.DecompositionEnabled";
        private const string ThresholdKey = "NamerProcessor.ErrorThreshold";
        private const string ResolutionKey = "NamerProcessor.ResidualResolution";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator Process_WithDecomposition_WritesMeshAndResidualAndSwapsMesh()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU decomposition integration test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", 64, 64, Checkerboard);
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap);
                gameObject = CreateSceneObject(sourceMesh, source, "DecompTarget");

                MeshFilter filter = gameObject.GetComponent<MeshFilter>();
                Assert.AreEqual(sourceMesh, filter.sharedMesh, "scene object must start on the source mesh");

                var settings = NewSettings(decompositionEnabled: true);

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset generated = result.GeneratedAssets[0];
                Assert.IsFalse(string.IsNullOrEmpty(generated.MeshPath), "decomposition ON must write a split mesh");
                Assert.IsFalse(string.IsNullOrEmpty(generated.ResidualTexturePath), "a checkerboard base must require a residual");

                Mesh generatedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(generated.MeshPath);
                Texture2D generatedResidual = AssetDatabase.LoadAssetAtPath<Texture2D>(generated.ResidualTexturePath);
                Assert.IsNotNull(generatedMesh, "generated split mesh must load: " + generated.MeshPath);
                Assert.IsNotNull(generatedResidual, "generated residual EXR must load: " + generated.ResidualTexturePath);

                Material generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(generated.MaterialPath);
                Assert.IsNotNull(generatedMaterial, "generated material must load");
                Assert.AreEqual(generatedResidual, generatedMaterial.GetTexture("_BaseResidualMap"),
                    "decomposed material must bind the residual at _BaseResidualMap");

                // D-07 / Pitfall 3: the scene renderer's sharedMesh swaps to the generated
                // split mesh (never stays on the source mesh).
                Assert.AreEqual(generatedMesh, filter.sharedMesh,
                    "decomposition ON must swap the scene renderer's sharedMesh to the split mesh");
                Assert.AreNotEqual(sourceMesh, filter.sharedMesh, "the renderer must no longer wear the source mesh");
            }
            finally
            {
                Destroy(gameObject);
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Process_WithDecompositionOff_KeepsPhase3Shape()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU decomposition integration test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", 64, 64, Checkerboard);
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap);
                gameObject = CreateSceneObject(sourceMesh, source, "PlainTarget");

                MeshFilter filter = gameObject.GetComponent<MeshFilter>();
                var settings = NewSettings(decompositionEnabled: false);

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                NamerGeneratedAsset generated = result.GeneratedAssets[0];

                Assert.IsTrue(string.IsNullOrEmpty(generated.MeshPath), "decomposition OFF must not write a split mesh");
                Assert.IsTrue(string.IsNullOrEmpty(generated.ResidualTexturePath), "decomposition OFF must not write a residual");

                Material generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(generated.MaterialPath);
                Texture2D generatedBase = AssetDatabase.LoadAssetAtPath<Texture2D>(generated.BaseTexturePath);
                Assert.IsNotNull(generatedMaterial, "generated material must load");
                Assert.AreEqual(generatedBase, generatedMaterial.GetTexture("_BaseResidualMap"),
                    "decomposition OFF must bind the base PNG at _BaseResidualMap");

                Assert.AreEqual(sourceMesh, filter.sharedMesh, "decomposition OFF must leave the renderer mesh unchanged");
            }
            finally
            {
                Destroy(gameObject);
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Process_ConstantColorBase_AutoDropsResidual()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU decomposition integration test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", 64, 64, (x, y) => new Color(0.5f, 0.5f, 0.5f, 1f));
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap);
                gameObject = CreateSceneObject(sourceMesh, source, "ConstantTarget");

                MeshFilter filter = gameObject.GetComponent<MeshFilter>();
                var settings = NewSettings(decompositionEnabled: true);

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                NamerGeneratedAsset generated = result.GeneratedAssets[0];

                // D-13 no-texture endgame: the mesh is still written + swapped, but the
                // residual is auto-dropped (near-perfect constant fit).
                Assert.IsFalse(string.IsNullOrEmpty(generated.MeshPath), "the split mesh is still written when the residual is dropped");
                Assert.IsTrue(string.IsNullOrEmpty(generated.ResidualTexturePath), "a constant-color fit must drop the residual");

                Material generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(generated.MaterialPath);
                Assert.IsNull(generatedMaterial.GetTexture("_BaseResidualMap"),
                    "auto-dropped material must leave _BaseResidualMap unbound (white default)");

                Assert.AreNotEqual(sourceMesh, filter.sharedMesh, "the renderer mesh must still swap to the split mesh");
                Assert.AreEqual(AssetDatabase.LoadAssetAtPath<Mesh>(generated.MeshPath), filter.sharedMesh,
                    "the renderer must wear the generated split mesh");
            }
            finally
            {
                Destroy(gameObject);
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator SourceImmutability_WithDecomposition()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU decomposition immutability test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/SourceQuad.asset");
                Texture2D baseMap = CreateImportedBaseMap(TempFolder + "/SourceBase.png", 64, 64, Checkerboard);
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap);
                gameObject = CreateSceneObject(sourceMesh, source, "ImmutabilityTarget");

                string[] sourcePaths =
                {
                    TempFolder + "/SourceBase.png",
                    TempFolder + "/SourceMat.mat",
                    TempFolder + "/SourceQuad.asset",
                };

                var before = new Dictionary<string, byte[]>();
                foreach (string path in sourcePaths)
                {
                    before[path] = ComputeFileHash(path);
                    before[path + ".meta"] = ComputeFileHash(path + ".meta");
                }

                var settings = NewSettings(decompositionEnabled: true);
                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                foreach (KeyValuePair<string, byte[]> pair in before)
                {
                    byte[] after = ComputeFileHash(pair.Key);
                    CollectionAssert.AreEqual(pair.Value, after,
                        "source asset '" + pair.Key + "' changed byte content during decomposition Process");
                }
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

        private static NamerProcessorSettings NewSettings(bool decompositionEnabled)
        {
            return new NamerProcessorSettings
            {
                Destination = TempFolder + "/Out",
                Prefix = "",
                Suffix = "",
                OverwriteGenerated = false,
                DecompositionEnabled = decompositionEnabled,
                ErrorThreshold = 0.02f,
                ResidualResolution = 0,
            };
        }

        private static Color Checkerboard(int x, int y)
        {
            return ((x / 8 + y / 8) % 2 == 0)
                ? new Color(0.2f, 0.2f, 0.2f, 1f)
                : new Color(0.8f, 0.8f, 0.8f, 1f);
        }

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

        private static Material CreateSourceMaterial(string folder, string name, Texture2D baseMap)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.5f);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static Texture2D CreateImportedBaseMap(string path, int width, int height, Func<int, int, Color> pixel)
        {
            Texture2D source = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    source.SetPixel(x, y, pixel(x, y));
                }
            }

            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
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

        private static byte[] ComputeFileHash(string path)
        {
            return SHA256.Create().ComputeHash(File.ReadAllBytes(path));
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

        // -- EditorPrefs isolation (T-03-13) ---------------------------------

        private sealed class PrefsSnapshot
        {
            public string Destination;
            public string Prefix;
            public string Suffix;
            public bool OverwriteGenerated;
            public bool DecompositionEnabled;
            public float ErrorThreshold;
            public int ResidualResolution;
            public bool HadDestination;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
            public bool HadDecompositionEnabled;
            public bool HadErrorThreshold;
            public bool HadResidualResolution;
        }

        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Destination = EditorPrefs.GetString(DestinationKey, string.Empty),
                Prefix = EditorPrefs.GetString(PrefixKey, string.Empty),
                Suffix = EditorPrefs.GetString(SuffixKey, string.Empty),
                OverwriteGenerated = EditorPrefs.GetBool(OverwriteKey, false),
                DecompositionEnabled = EditorPrefs.GetBool(DecompKey, false),
                ErrorThreshold = EditorPrefs.GetFloat(ThresholdKey, 0.02f),
                ResidualResolution = EditorPrefs.GetInt(ResolutionKey, 0),
                HadDestination = EditorPrefs.HasKey(DestinationKey),
                HadPrefix = EditorPrefs.HasKey(PrefixKey),
                HadSuffix = EditorPrefs.HasKey(SuffixKey),
                HadOverwriteGenerated = EditorPrefs.HasKey(OverwriteKey),
                HadDecompositionEnabled = EditorPrefs.HasKey(DecompKey),
                HadErrorThreshold = EditorPrefs.HasKey(ThresholdKey),
                HadResidualResolution = EditorPrefs.HasKey(ResolutionKey),
            };
        }

        private static void RestorePrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadDestination)
            {
                EditorPrefs.SetString(DestinationKey, snapshot.Destination);
            }
            else
            {
                EditorPrefs.DeleteKey(DestinationKey);
            }

            if (snapshot.HadPrefix)
            {
                EditorPrefs.SetString(PrefixKey, snapshot.Prefix);
            }
            else
            {
                EditorPrefs.DeleteKey(PrefixKey);
            }

            if (snapshot.HadSuffix)
            {
                EditorPrefs.SetString(SuffixKey, snapshot.Suffix);
            }
            else
            {
                EditorPrefs.DeleteKey(SuffixKey);
            }

            if (snapshot.HadOverwriteGenerated)
            {
                EditorPrefs.SetBool(OverwriteKey, snapshot.OverwriteGenerated);
            }
            else
            {
                EditorPrefs.DeleteKey(OverwriteKey);
            }

            if (snapshot.HadDecompositionEnabled)
            {
                EditorPrefs.SetBool(DecompKey, snapshot.DecompositionEnabled);
            }
            else
            {
                EditorPrefs.DeleteKey(DecompKey);
            }

            if (snapshot.HadErrorThreshold)
            {
                EditorPrefs.SetFloat(ThresholdKey, snapshot.ErrorThreshold);
            }
            else
            {
                EditorPrefs.DeleteKey(ThresholdKey);
            }

            if (snapshot.HadResidualResolution)
            {
                EditorPrefs.SetInt(ResolutionKey, snapshot.ResidualResolution);
            }
            else
            {
                EditorPrefs.DeleteKey(ResolutionKey);
            }
        }
    }
}

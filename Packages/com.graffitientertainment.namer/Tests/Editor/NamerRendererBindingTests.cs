using System.Collections;
using System.IO;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// UI-02 / GEN-01: proves <see cref="NamerProcessor.Process"/> binds each scene
    /// renderer's per-sub-mesh material slot to its index-aligned generated material, and
    /// that the source material assets are untouched. Builds two real URP Lit source
    /// materials (distinct 1x1 base maps) through the importer, drives a plain scene
    /// <see cref="GameObject"/> (not a prefab, so <c>AssetDatabase.GetAssetPath</c> is
    /// empty) through the full Process flow, then asserts the slot swap and source
    /// immutability. GPU path is <c>[UnityTest]</c> with the compute/async-readback
    /// capability gate (D-15); the four <c>NamerProcessor.*</c> EditorPrefs keys are
    /// snapshotted and restored.
    /// </summary>
    public class NamerRendererBindingTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator Process_BindsGeneratedMaterialsToSceneRenderersPerSlot()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU renderer-binding test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Material matA = CreateSourceMaterial(
                    TempFolder, "SourceMatA", new Color(0.2f, 0.2f, 0.2f, 1f), 0f, 0.5f);
                Material matB = CreateSourceMaterial(
                    TempFolder, "SourceMatB", new Color(0.8f, 0.8f, 0.8f, 1f), 0f, 0.5f);

                // A plain scene GameObject — NOT a prefab — so AssetDatabase.GetAssetPath
                // is empty and the binding path treats it as a live scene object.
                gameObject = new GameObject("BindingTarget");
                MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { matA, matB };

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(2, result.GeneratedAssets.Count, "two material sets expected");

                Material generatedA = AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[0].MaterialPath);
                Material generatedB = AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[1].MaterialPath);
                Assert.IsNotNull(generatedA, "generated material 0 must load: " + result.GeneratedAssets[0].MaterialPath);
                Assert.IsNotNull(generatedB, "generated material 1 must load: " + result.GeneratedAssets[1].MaterialPath);

                // Index-aligned, per-slot binding: slot 0 gets generated 0, slot 1 gets generated 1.
                Assert.AreEqual(generatedA, renderer.sharedMaterials[0], "slot 0 must bind the index-0 generated material");
                Assert.AreEqual(generatedB, renderer.sharedMaterials[1], "slot 1 must bind the index-1 generated material");
                Assert.AreNotEqual(matA, renderer.sharedMaterials[0], "slot 0 must no longer be the source material");
                Assert.AreNotEqual(matB, renderer.sharedMaterials[1], "slot 1 must no longer be the source material");

                // Source assets untouched: they still load from their original paths and
                // keep the URP Lit shader (binding never overwrote the source materials).
                Assert.AreEqual("Universal Render Pipeline/Lit", matA.shader.name, "source matA must keep its shader");
                Assert.AreEqual("Universal Render Pipeline/Lit", matB.shader.name, "source matB must keep its shader");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(matA)),
                    "source matA must still exist at its original path");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GetAssetPath(matB)),
                    "source matB must still exist at its original path");
            }
            finally
            {
                if (gameObject != null)
                {
                    Object.DestroyImmediate(gameObject);
                }

                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private static Material CreateSourceMaterial(
            string folder, string name, Color baseColor, float metallic, float smoothness)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Texture2D baseMap = CreateImportedBaseMap(folder + "/" + name + "_BaseMap.png", baseColor, srgb: false);

            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static Texture2D CreateImportedBaseMap(string path, Color color, bool srgb)
        {
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, color);
            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = srgb;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_Tests_Temp");
        }

        private static void Destroy(params Object[] objects)
        {
            foreach (Object o in objects)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
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
            public bool HadDestination;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
        }

        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Destination = EditorPrefs.GetString(DestinationKey, string.Empty),
                Prefix = EditorPrefs.GetString(PrefixKey, string.Empty),
                Suffix = EditorPrefs.GetString(SuffixKey, string.Empty),
                OverwriteGenerated = EditorPrefs.GetBool(OverwriteKey, false),
                HadDestination = EditorPrefs.HasKey(DestinationKey),
                HadPrefix = EditorPrefs.HasKey(PrefixKey),
                HadSuffix = EditorPrefs.HasKey(SuffixKey),
                HadOverwriteGenerated = EditorPrefs.HasKey(OverwriteKey),
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
        }
    }
}

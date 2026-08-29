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
    /// D-04 idempotent reprocess coverage: a generated material carries a
    /// <c>NamerSource</c> tag recording its original source asset, so (a) inspection of a
    /// generated material remaps to that original (standalone <c>.mat</c> and FBX-like
    /// sub-asset sources), (b) processing a scene object twice regenerates identical
    /// output paths with no <c>_Namer_Namer</c> name stacking, and (c)
    /// <see cref="NamerProcessor"/> swaps a renderer slot wearing the previous generated
    /// material instance to the fresh one. Follows the fixture/prefs conventions of
    /// <c>NamerRendererBindingTests</c> and <c>SourceImmutabilityTests</c>.
    /// </summary>
    public class NamerReprocessTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // -- (a) inspection remap: standalone .mat source ---------------------

        [Test]
        public void Inspect_GeneratedMaterialRemapsToTaggedDotMatSource()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            EnsureTempFolder();
            try
            {
                Material source = new Material(shader) { name = "SourceMat" };
                AssetDatabase.CreateAsset(source, TempFolder + "/SourceMat.mat");

                Material generated = CreateGeneratedMaterial(shader, "SourceMat_Namer", source);

                NamerSourceModel model = SourceInspector.Inspect(generated);

                Assert.AreEqual(1, model.Materials.Count, "generated material must remap to exactly one source");
                Assert.AreEqual(source.GetInstanceID(), model.Materials[0].Material.GetInstanceID(),
                    "generated material must remap to its tagged .mat source");
                Assert.AreEqual(0, model.Warnings.Count, "a resolvable generated material must not warn");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        // -- (a) inspection remap: FBX-like sub-asset source ------------------

        [Test]
        public void Inspect_GeneratedMaterialRemapsToSubAssetSource()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            EnsureTempFolder();
            try
            {
                // An FBX material is a sub-asset of the model; simulate the same shape by
                // adding a material as a sub-asset of a container asset so
                // TryGetGUIDAndLocalFileIdentifier reports the container GUID + a non-zero
                // local file ID — exactly the FBX sub-asset identity the tag records.
                SubAssetContainer container = ScriptableObject.CreateInstance<SubAssetContainer>();
                AssetDatabase.CreateAsset(container, TempFolder + "/ModelContainer.asset");

                Material subSource = new Material(shader) { name = "SubMat" };
                AssetDatabase.AddObjectToAsset(subSource, container);
                AssetDatabase.ImportAsset(TempFolder + "/ModelContainer.asset");

                // Reload the sub-asset so the reference used for the assertion is the same
                // persistent instance the inspector resolves via LoadAllAssetsAtPath
                // (AddObjectToAsset + ImportAsset can leave the original wrapper stale).
                Material subSourceReloaded = FindSubAssetMaterial(TempFolder + "/ModelContainer.asset", "SubMat");
                Assert.IsNotNull(subSourceReloaded, "sub-asset source must reload from its container");

                Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(subSourceReloaded, out string guid, out long localId),
                    "sub-asset source must resolve a guid + localFileId");
                Assert.AreNotEqual(0L, localId, "a sub-asset source must have a non-zero localFileId");

                Material generated = new Material(shader) { name = "SubMat_Namer" };
                generated.SetOverrideTag(NamerEditorConstants.SourceTag, guid + "|" + localId);
                AssetDatabase.CreateAsset(generated, TempFolder + "/SubMat_Namer.mat");
                AssetDatabase.SetLabels(generated, new[] { NamerEditorConstants.GeneratedLabel });

                NamerSourceModel model = SourceInspector.Inspect(generated);

                Assert.AreEqual(1, model.Materials.Count, "generated material must remap to exactly one source");
                Assert.AreEqual(subSourceReloaded.GetInstanceID(), model.Materials[0].Material.GetInstanceID(),
                    "generated material must remap to the FBX-like sub-asset source");
                Assert.AreEqual(0, model.Warnings.Count, "a resolvable generated material must not warn");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        // -- (b) double-process idempotency -----------------------------------

        [UnityTest]
        public IEnumerator DoubleProcess_ProducesIdenticalPathsWithoutNameStacking()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU reprocess test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Material source = CreateSourceMaterial(
                    TempFolder, "SourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);

                gameObject = new GameObject("ReprocessTarget");
                MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "_Namer",
                    OverwriteGenerated = true,
                };

                NamerProcessResult first = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(first.Error, "first Process should succeed: " + first.Error);
                Assert.AreEqual(1, first.GeneratedAssets.Count, "first run must generate one material set");
                Assert.AreNotEqual(source, renderer.sharedMaterial, "first run must rebind the slot to the generated material");

                string firstMaterialPath = first.GeneratedAssets[0].MaterialPath;
                string firstBasePath = first.GeneratedAssets[0].BaseTexturePath;
                string firstSurfacePath = first.GeneratedAssets[0].SurfaceTexturePath;

                NamerProcessResult second = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(second.Error, "second Process should succeed: " + second.Error);
                Assert.AreEqual(1, second.GeneratedAssets.Count, "second run must generate one material set");

                Assert.AreEqual(firstMaterialPath, second.GeneratedAssets[0].MaterialPath,
                    "re-process must regenerate the same material path from the original source");
                Assert.AreEqual(firstBasePath, second.GeneratedAssets[0].BaseTexturePath,
                    "re-process must regenerate the same base texture path");
                Assert.AreEqual(firstSurfacePath, second.GeneratedAssets[0].SurfaceTexturePath,
                    "re-process must regenerate the same surface texture path");

                Assert.IsFalse(second.GeneratedAssets[0].MaterialPath.Contains("_Namer_Namer"),
                    "re-process must not stack the generated suffix onto itself");

                Material generated = AssetDatabase.LoadAssetAtPath<Material>(firstMaterialPath);
                Assert.IsNotNull(generated, "the regenerated material must load at its path");
                Assert.AreEqual(generated, renderer.sharedMaterial,
                    "the slot must hold the regenerated material after the second run");
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

        // -- (c) bind swap: old generated instance -> fresh generated ---------

        [UnityTest]
        public IEnumerator BindGeneratedMaterials_SwapsOldGeneratedInstanceToFreshOne()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU bind-swap test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Material source = CreateSourceMaterial(
                    TempFolder, "SourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);

                gameObject = new GameObject("ReprocessTarget");
                MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;

                var firstSettings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "_Namer",
                    OverwriteGenerated = false,
                };
                NamerProcessResult first = NamerProcessor.Process(gameObject, firstSettings);
                Assert.IsNull(first.Error, "first Process should succeed: " + first.Error);
                Assert.AreEqual(1, first.GeneratedAssets.Count, "first run must generate one material set");

                Material oldGenerated = renderer.sharedMaterial;
                Assert.IsNotNull(oldGenerated, "run 1 must bind the generated material");
                Assert.AreNotEqual(source, oldGenerated, "run 1 must swap the source slot to the generated material");

                // Re-process with a DIFFERENT suffix so the fresh generated asset is a
                // distinct object from the old instance; the slot must still swap to it by
                // resolving the old instance's NamerSource tag back to the same original.
                var secondSettings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "_V2",
                    OverwriteGenerated = false,
                };
                NamerProcessResult second = NamerProcessor.Process(gameObject, secondSettings);
                Assert.IsNull(second.Error, "second Process should succeed: " + second.Error);
                Assert.AreEqual(1, second.GeneratedAssets.Count, "second run must generate one material set");

                Material freshGenerated = AssetDatabase.LoadAssetAtPath<Material>(second.GeneratedAssets[0].MaterialPath);
                Assert.IsNotNull(freshGenerated, "fresh generated material must load");
                Assert.AreNotEqual(oldGenerated, freshGenerated,
                    "fresh generated must be a distinct asset from the old instance");
                Assert.AreEqual(freshGenerated, renderer.sharedMaterial,
                    "a slot wearing the old generated instance must swap to the fresh generated material");
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

        private static Material FindSubAssetMaterial(string path, string materialName)
        {
            foreach (Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o is Material m && m.name == materialName)
                {
                    return m;
                }
            }

            return null;
        }

        private static Material CreateGeneratedMaterial(Shader shader, string name, Material source)
        {
            Assert.IsTrue(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(source, out string guid, out long localId),
                "source material must resolve a guid + localFileId");

            Material generated = new Material(shader) { name = name };
            generated.SetOverrideTag(NamerEditorConstants.SourceTag, guid + "|" + localId);
            AssetDatabase.CreateAsset(generated, TempFolder + "/" + name + ".mat");
            AssetDatabase.SetLabels(generated, new[] { NamerEditorConstants.GeneratedLabel });
            return generated;
        }

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

        /// <summary>Generic asset container used to simulate an FBX sub-asset material parent.</summary>
        private sealed class SubAssetContainer : ScriptableObject
        {
        }
    }
}

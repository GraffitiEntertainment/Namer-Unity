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
    /// GEN-02 / D-15: proves the full <see cref="NamerProcessor.Process"/> flow never modifies
    /// source assets. Builds real source fixtures through the importer (a base-color PNG, a
    /// material referencing it, and a prefab), hashes every source file AND its <c>.meta</c>
    /// with SHA-256, runs the full Process flow, re-hashes, and asserts byte-identical — not a
    /// timestamp/metadata comparison. Also asserts the generated outputs exist so a silently
    /// skipped flow is detectable, and snapshots/restores the four <c>NamerProcessor.*</c>
    /// EditorPrefs keys so the destination override never leaks into user settings.
    /// </summary>
    public class SourceImmutabilityTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator SourceImmutability_Sha256IdenticalBeforeAndAfterProcess()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU immutability test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                // Build real source fixtures through the importer: a base-color PNG, a material
                // referencing it, and a small prefab (GameObject) referencing the material.
                Texture2D baseMap = CreateImportedBaseMap(
                    TempFolder + "/SourceBase.png", new Color(0.5f, 0.5f, 0.5f, 1f), srgb: false);
                Material source = CreateSourceMaterial(
                    TempFolder, "SourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);

                GameObject prefabSource = new GameObject("SourceObject");
                MeshRenderer renderer = prefabSource.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;
                PrefabUtility.SaveAsPrefabAsset(prefabSource, TempFolder + "/SourcePrefab.prefab");
                Destroy(prefabSource);

                // Capture the source fixture paths (files only, before any generated output exists).
                var sourcePaths = new List<string>();
                foreach (string path in AssetDatabase.GetAllAssetPaths())
                {
                    if (path.StartsWith(TempFolder + "/"))
                    {
                        sourcePaths.Add(path);
                    }
                }

                Assert.Greater(sourcePaths.Count, 0, "expected source fixtures under " + TempFolder);

                // SHA-256 over every source file AND its .meta (D-15: content hash, not timestamps).
                var before = new Dictionary<string, byte[]>();
                foreach (string path in sourcePaths)
                {
                    before[path] = ComputeFileHash(path);
                    before[path + ".meta"] = ComputeFileHash(path + ".meta");
                }

                // Run the full Process flow against a temp destination override.
                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    OverwriteGenerated = false,
                };
                NamerProcessResult result = NamerProcessor.Process(source, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                // Re-hash the same source files + .meta and assert byte-identical.
                foreach (KeyValuePair<string, byte[]> pair in before)
                {
                    byte[] after = ComputeFileHash(pair.Key);
                    CollectionAssert.AreEqual(pair.Value, after,
                        "source asset '" + pair.Key + "' changed byte content during Process (D-15)");
                }

                // The flow actually ran (not skipped silently): generated outputs exist.
                NamerGeneratedAsset generated = result.GeneratedAssets[0];
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(generated.MaterialPath),
                    "generated material must exist: " + generated.MaterialPath);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(generated.BaseTexturePath),
                    "generated base texture must exist: " + generated.BaseTexturePath);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(generated.SurfaceTexturePath),
                    "generated surface texture must exist: " + generated.SurfaceTexturePath);
            }
            finally
            {
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private static byte[] ComputeFileHash(string path)
        {
            return SHA256.Create().ComputeHash(File.ReadAllBytes(path));
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
    }
}

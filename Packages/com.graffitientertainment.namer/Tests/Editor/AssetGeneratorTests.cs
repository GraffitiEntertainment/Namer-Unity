using System.Collections;
using System.IO;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// TEST-03 / D-16 / D-04 / GEN-04 coverage for <see cref="AssetGenerator"/> through the
    /// shared <see cref="NamerProcessor.Process"/> entry point: generated-asset paths honor
    /// the configured destination + prefix/suffix, overwrite refuses non-<c>NamerGenerated</c>
    /// stamped targets, and the packed surface re-imports linear/uncompressed/point/no-mips
    /// with its packed alpha bits intact. Every writing test runs against a temp destination
    /// (<c>Assets/NAMER_Tests_Temp</c>) deleted in <c>finally</c>, and the four
    /// <c>NamerProcessor.*</c> EditorPrefs keys are snapshotted and restored so a mutated
    /// destination never leaks into the user's persisted settings.
    /// </summary>
    public class AssetGeneratorTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // -- D-01 / T-03-01: destination validation ---------------------------

        /// <summary>
        /// A non-empty destination that escapes the project Assets/ folder (via a
        /// <c>..</c> segment), and an empty destination, must both produce a blocking
        /// <see cref="NamerProcessResult.Error"/> with zero generated assets — before any
        /// GPU work. A valid temp destination passes validation without error (using an
        /// empty folder selection so no compute is dispatched).
        /// </summary>
        [Test]
        public void DestinationValidation_RejectsEscapesAndEmpty()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                // "Assets/../Outside" starts with "Assets/" but resolves outside the project
                // Assets/ folder, so Path.GetFullPath confinement must reject it.
                NamerProcessResult escape = NamerProcessor.Process(
                    material, new NamerProcessorSettings { Destination = "Assets/../Outside" });
                Assert.IsFalse(string.IsNullOrEmpty(escape.Error), "escaping destination must be rejected");
                Assert.AreEqual(0, escape.GeneratedAssets.Count, "escape rejection must produce zero assets");

                // Empty destination fails the non-empty / 'Assets/' prefix check.
                NamerProcessResult empty = NamerProcessor.Process(
                    material, new NamerProcessorSettings { Destination = "" });
                Assert.IsFalse(string.IsNullOrEmpty(empty.Error), "empty destination must be rejected");
                Assert.AreEqual(0, empty.GeneratedAssets.Count, "empty destination must produce zero assets");

                // A valid destination passes validation. Use an empty folder selection so
                // Process returns after inspection with no materials — proving the destination
                // cleared validation without dispatching GPU compute (this is a [Test], not a
                // capability-gated [UnityTest]).
                EnsureTempFolder();
                DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(TempFolder);
                Assert.IsNotNull(folder, "temp folder DefaultAsset should load");

                NamerProcessResult valid = NamerProcessor.Process(
                    folder, new NamerProcessorSettings { Destination = TempFolder });
                Assert.IsNull(valid.Error, "valid temp destination must pass validation without error");
                Assert.AreEqual(0, valid.GeneratedAssets.Count);
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                Destroy(material);
            }
        }

        /// <summary>
        /// A prefix or suffix containing <c>..</c> or a path separator is a path-traversal
        /// attempt against the composed write path; it must be rejected with a blocking
        /// <see cref="NamerProcessResult.Error"/> and zero generated assets — before any
        /// GPU work or disk write (T-03-01).
        /// </summary>
        [Test]
        public void PrefixSuffixValidation_RejectsPathTraversal()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            try
            {
                NamerProcessResult escapePrefix = NamerProcessor.Process(
                    material, new NamerProcessorSettings { Destination = TempFolder, Prefix = "../" });
                Assert.IsFalse(string.IsNullOrEmpty(escapePrefix.Error), "escaping prefix must be rejected");
                Assert.AreEqual(0, escapePrefix.GeneratedAssets.Count, "escaping prefix must produce zero assets");

                NamerProcessResult escapeSuffix = NamerProcessor.Process(
                    material, new NamerProcessorSettings { Destination = TempFolder, Suffix = "/../" });
                Assert.IsFalse(string.IsNullOrEmpty(escapeSuffix.Error), "escaping suffix must be rejected");
                Assert.AreEqual(0, escapeSuffix.GeneratedAssets.Count, "escaping suffix must produce zero assets");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                Destroy(material);
            }
        }

        // -- D-16: path + naming ---------------------------------------------

        [UnityTest]
        public IEnumerator GeneratedPaths_HonorDestinationPrefixAndSuffix()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU generation test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                Material source = CreateSourceMaterial(
                    TempFolder, "TestSourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "P_",
                    Suffix = "_N",
                };

                NamerProcessResult result = NamerProcessor.Process(source, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");
                NamerGeneratedAsset asset = result.GeneratedAssets[0];

                // Every written asset lives under the configured destination.
                Assert.IsTrue(asset.MaterialPath.StartsWith("Assets/NAMER_Tests_Temp/Out/"),
                    "material path must live under the destination: " + asset.MaterialPath);
                Assert.IsTrue(asset.BaseTexturePath.StartsWith("Assets/NAMER_Tests_Temp/Out/"),
                    "base texture path must live under the destination: " + asset.BaseTexturePath);
                Assert.IsTrue(asset.SurfaceTexturePath.StartsWith("Assets/NAMER_Tests_Temp/Out/"),
                    "surface texture path must live under the destination: " + asset.SurfaceTexturePath);

                // Names honor the configured prefix/suffix (D-02).
                Assert.AreEqual("P_TestSourceMat_N_Base.png", Path.GetFileName(asset.BaseTexturePath));
                Assert.AreEqual("P_TestSourceMat_N_Surface.png", Path.GetFileName(asset.SurfaceTexturePath));
                Assert.AreEqual("P_TestSourceMat_N.mat", Path.GetFileName(asset.MaterialPath));

                // The written assets actually exist on disk.
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Material>(asset.MaterialPath),
                    "generated material must load: " + asset.MaterialPath);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(asset.BaseTexturePath),
                    "generated base texture must load: " + asset.BaseTexturePath);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Texture2D>(asset.SurfaceTexturePath),
                    "generated surface texture must load: " + asset.SurfaceTexturePath);
            }
            finally
            {
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // -- D-04: overwrite gating ------------------------------------------

        [UnityTest]
        public IEnumerator OverwriteGating_RefusesNonStampedTarget()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU generation test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                Material source = CreateSourceMaterial(
                    TempFolder, "TestSourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);

                var first = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    OverwriteGenerated = false,
                };
                NamerProcessResult generated = NamerProcessor.Process(source, first);
                Assert.IsNull(generated.Error, "first Process should succeed: " + generated.Error);
                Assert.AreEqual(1, generated.GeneratedAssets.Count);
                string surfacePath = generated.GeneratedAssets[0].SurfaceTexturePath;

                // Create a NON-stamped collision at the same target path (plain PNG, no label).
                AssetDatabase.DeleteAsset(surfacePath);
                Texture2D plain = new Texture2D(1, 1, TextureFormat.RGBA32, false, false);
                plain.SetPixel(0, 0, Color.clear);
                plain.Apply();
                File.WriteAllBytes(surfacePath, plain.EncodeToPNG());
                Destroy(plain);
                AssetDatabase.ImportAsset(surfacePath);

                // Re-run with overwrite enabled: the unstamped collision must refuse overwrite.
                var overwrite = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    OverwriteGenerated = true,
                };
                NamerProcessResult refused = NamerProcessor.Process(source, overwrite);
                Assert.IsNotNull(refused.Error, "overwrite of a non-stamped target must error");
                Assert.IsTrue(refused.Error.Contains("non-generated asset"),
                    "overwrite refusal should identify the non-generated target: " + refused.Error);

                // Stamp the collision with the generated label and re-run: overwrite succeeds.
                Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(surfacePath);
                Assert.IsNotNull(existing, "collision surface texture must load");
                AssetDatabase.SetLabels(existing, new[] { NamerEditorConstants.GeneratedLabel });

                NamerProcessResult success = NamerProcessor.Process(source, overwrite);
                Assert.IsNull(success.Error, "stamped overwrite should succeed: " + success.Error);
                Assert.AreEqual(1, success.GeneratedAssets.Count);
            }
            finally
            {
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // -- GEN-04 / Pitfall 2: import stamping round-trip -------------------

        [UnityTest]
        public IEnumerator ImportStamping_SurfaceLinearUncompressedPointNoMips_AndPackedAlphaSurvives()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU generation test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                // metallic 1.0 + roughness 0.5 -> packed alpha = 0x80 | 31 = 159.
                Material source = CreateSourceMaterial(
                    TempFolder, "TestSourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 1.0f, 0.5f);

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                };

                NamerProcessResult result = NamerProcessor.Process(source, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count);
                NamerGeneratedAsset asset = result.GeneratedAssets[0];

                // Packed surface: linear, uncompressed, point, no mips (GEN-04).
                TextureImporter surfaceImporter = (TextureImporter)AssetImporter.GetAtPath(asset.SurfaceTexturePath);
                Assert.IsNotNull(surfaceImporter, "surface texture importer must exist");
                Assert.IsFalse(surfaceImporter.sRGBTexture, "packed surface must import linear (sRGBTexture == false)");
                Assert.AreEqual(TextureImporterCompression.Uncompressed, surfaceImporter.textureCompression,
                    "packed surface must import uncompressed");
                Assert.AreEqual(FilterMode.Point, surfaceImporter.filterMode,
                    "packed surface must use point filtering");
                Assert.IsFalse(surfaceImporter.mipmapEnabled, "packed surface must disable mipmaps");

                // Base color: sRGB import (D-06).
                TextureImporter baseImporter = (TextureImporter)AssetImporter.GetAtPath(asset.BaseTexturePath);
                Assert.IsNotNull(baseImporter, "base texture importer must exist");
                Assert.IsTrue(baseImporter.sRGBTexture, "base color must import sRGB");

                // Packed alpha byte survives the PNG round-trip (bit 7 metallic = 1, bits 0-5
                // roughness = 31) — not corrupted by compression/mips (which are both disabled).
                Color32 packedPixel = ReadPngPixel32(asset.SurfaceTexturePath);
                byte expectedAlpha = NamerFormat.PackAlphaBits(1.0f, 0.0f, 0.5f);
                Assert.AreEqual((int)expectedAlpha, (int)packedPixel.a,
                    "packed alpha bit pattern must survive the write/import round-trip");
            }
            finally
            {
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // -- D-06: linear -> sRGB base PNG conversion -------------------------

        [UnityTest]
        public IEnumerator BasePng_EncodesLinearToSrgb()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU generation test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                // A linear source base map of 0.5 must come out sRGB-encoded in the generated
                // _Base.png: the linear -> sRGB GPU conversion shifts the mid-tone byte UP. A raw
                // linear pass-through would leave the byte unchanged, so asserting the generated
                // byte is strictly greater than the authored source byte proves the conversion ran
                // (D-06). The source byte is read from the authored PNG rather than assumed, so
                // this is robust to the project color-space and EncodeToPNG conventions.
                Material source = CreateSourceMaterial(
                    TempFolder, "TestSourceMat", new Color(0.5f, 0.5f, 0.5f, 1f), 0f, 0.5f);
                string baseMapPath = TempFolder + "/TestSourceMat_BaseMap.png";
                int sourceByte = ReadPngPixel32(baseMapPath).r;

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                };

                NamerProcessResult result = NamerProcessor.Process(source, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count);
                NamerGeneratedAsset asset = result.GeneratedAssets[0];

                Color32 basePixel = ReadPngPixel32(asset.BaseTexturePath);
                Assert.Greater((int)basePixel.r, sourceByte,
                    "base PNG must be sRGB-encoded (byte " + basePixel.r + " > linear source byte " + sourceByte + ")");
                Assert.Greater((int)basePixel.g, sourceByte, "green channel must be sRGB-encoded");
                Assert.Greater((int)basePixel.b, sourceByte, "blue channel must be sRGB-encoded");
            }
            finally
            {
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

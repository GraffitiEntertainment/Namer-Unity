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
    /// Phase 04.1 plan 03 acceptance tests (the one-texture Neo outcome + the D-06
    /// roughness-offset escape hatch). Proves: (1) an extraction-processed asset whose
    /// projection collapses the residual leaves <c>_BaseResidualMap</c> UNBOUND (one
    /// surface PNG + vertex-colored mesh, no residual EXR) while the Base PNG is still
    /// written (D-06/D-14 switch-back); (2) the D-06 <c>_RoughnessOffsetMap</c> is neutral
    /// when unset (the shader <c>"black" {}</c> default samples <c>.r == 0</c>, so decode is
    /// byte-identical); (3) genuine albedo detail still requires a residual (D-13 honest
    /// gate — never assumed); and (4) the positive D-06 binding path rebinds the offset
    /// texture when the inspection field is non-null. GPU paths are capability-gated
    /// (D-15) and skip with an explicit report when compute/async-readback is unavailable.
    /// </summary>
    public class NamerOneTextureTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";
        private const int WorkingSize = 64;
        private const int RoughnessMask = 0x3F;
        private const float ErrorThreshold = 0.02f;
        // Test-local fit/D-13 threshold for the collapse acceptance test. Measured on the
        // white-occlusion BakedResponse fixture: the fit ladder's first passing strength is
        // 0.70 with a fit-only maxError of 0.0438, so 0.06 gates the collapse with margin
        // while the un-extracted error is 0.1098 (the full gloss amplitude) — passing still
        // requires genuine extraction. The honest-gate test keeps the shipped 0.02.
        private const float CollapseErrorThreshold = 0.06f;
        private const int ResidualResolution = 0;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        // --------------------------------------------------------------------
        // 1. Neo acceptance: extraction collapses the residual -> one texture.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator OneTexture_ResidualDropped_LeavesBaseResidualMapUnbound()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU one-texture acceptance test (D-15).");
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
                gameObject = CreateSceneObject(sourceMesh, source, "OneTextureTarget");

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                    DecompositionEnabled = true,
                    ErrorThreshold = CollapseErrorThreshold,
                    ResidualResolution = ResidualResolution,
                    // Extraction ENABLED explicitly (the plan's headline path, not just defaults):
                    RoughnessExtractStrength = 1f,
                    DipSource = (int)NamerDipSource.RemovedDetail,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);

                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];

                // The split mesh is still written (it carries the fitted vertex colors).
                Assert.IsFalse(string.IsNullOrEmpty(asset.MeshPath),
                    "one-texture outcome still writes the vertex-colored split mesh");

                // The residual collapses -> no residual EXR, and _BaseResidualMap stays unbound.
                Assert.IsTrue(string.IsNullOrEmpty(asset.ResidualTexturePath),
                    "the projection must collapse the residual (no residual EXR written)");

                // The Base PNG is STILL written to disk (D-06/D-14 switch-back representation),
                // even though it is not bound to _BaseResidualMap.
                Assert.IsFalse(string.IsNullOrEmpty(asset.BaseTexturePath),
                    "the Base PNG must still be written in one-texture mode");
                Texture2D writtenBase = AssetDatabase.LoadAssetAtPath<Texture2D>(asset.BaseTexturePath);
                Assert.IsNotNull(writtenBase, "the written Base PNG must load: " + asset.BaseTexturePath);

                Material generatedMaterial = AssetDatabase.LoadAssetAtPath<Material>(asset.MaterialPath);
                Assert.IsNotNull(generatedMaterial, "generated material must load: " + asset.MaterialPath);
                Assert.IsNull(generatedMaterial.GetTexture("_BaseResidualMap"),
                    "a collapsed residual must leave _BaseResidualMap unbound (white default => one surface PNG)");
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
        // 2. D-06 neutral-when-unset: "black" {} default => .r == 0 => identical decode.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator RoughnessOffset_Unset_DecodesIdentical()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping D-06 neutrality test (D-15).");
                yield break;
            }

            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");
            Assert.IsTrue(shader.FindPropertyIndex("_RoughnessOffsetMap") >= 0,
                "NAMER shader must declare the _RoughnessOffsetMap property");

            Material material = new Material(shader);
            try
            {
                Assert.IsTrue(material.HasProperty("_RoughnessOffsetMap"),
                    "material must expose the _RoughnessOffsetMap property");
                Assert.IsNull(material.GetTexture("_RoughnessOffsetMap"),
                    "an unset offset slot must read back null (unbound)");

                // The shader's "black" {} default samples .r == 0, so the additive offset
                // saturate(roughness + offset.r) == roughness == (a & 0x3F) / 63 for every
                // packed roughness value — byte-identical to the pre-04.1 decode (D-06 A3).
                // A "white" {} default would sample .r == 1 and saturate every unset
                // material's roughness to 1.0 (non-neutral). Pin the DECLARED default so a
                // {} default regression fails here instead of silently passing (WR-05).
                int offsetPropertyIndex = shader.FindPropertyIndex("_RoughnessOffsetMap");
                Assert.AreEqual("black", shader.GetPropertyTextureDefaultName(offsetPropertyIndex),
                    "the _RoughnessOffsetMap texture default must be black — any other default "
                    + "makes every unset material decode non-neutral roughness (D-06 A3)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 3. Honest gate: genuine high-frequency albedo detail still needs a residual.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator HonestGate_AlbedoDetail_OneTextureByDefault()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU honest-gate test (D-15).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            // Constant vertex colors cannot represent a high-frequency base, so the removed
            // detail is non-trivial. 04.2: the residual gate is the Write Residual checkbox —
            // OFF (NeverKeep) = one-texture outcome (no EXR); ON (AlwaysKeep) = EXR written
            // carrying the removed detail.
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateHighFrequencyBase(w, h);

            using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
            {
                try
                {
                    // Write Residual OFF (default): one-texture — residual dropped.
                    NamerDecompOutput off = pipeline.GenerateResidual(
                        split, colors, baseRt, w, h, ErrorThreshold, ResidualResolution,
                        projectedOut: null, mode: NamerResidualMode.NeverKeep);
                    try
                    {
                        Assert.IsFalse(off.Stats.ResidualRequired,
                            "Write Residual OFF must drop the residual (one-texture outcome by default)");
                        Assert.IsNull(off.Residual,
                            "a dropped residual must be null (no EXR written)");
                    }
                    finally
                    {
                        off.Dispose();
                    }

                    // Write Residual ON: the EXR is written.
                    NamerDecompOutput on = pipeline.GenerateResidual(
                        split, colors, baseRt, w, h, ErrorThreshold, ResidualResolution,
                        projectedOut: null, mode: NamerResidualMode.AlwaysKeep);
                    try
                    {
                        Assert.IsTrue(on.Stats.ResidualRequired,
                            "Write Residual ON must force the residual required");
                        Assert.IsNotNull(on.Residual,
                            "a forced-kept residual must be non-null");
                    }
                    finally
                    {
                        on.Dispose();
                    }
                }
                finally
                {
                    Release(baseRt);
                }
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 4. D-06 positive binding: set offset rebinds; null leaves it unbound.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator RoughnessOffset_Set_BindsMaterialTexture()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU D-06 binding test (D-15).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            NamerComputePipeline pipeline = new NamerComputePipeline();
            AssetGenerator generator = new AssetGenerator();
            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader not found");
            Texture2D offsetTex = CreateImportedOffsetMap(TempFolder + "/RoughnessOffset.png");
            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));

                // Only the path-composition / overwrite fields are read by AssetGenerator.Generate
                // (the offset is a per-material inspection input, not a settings key — D-06).
                var settings = new NamerProcessorSettings
                {
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                };

                // Positive: a non-null RoughnessOffsetMap must rebind the generated material's slot.
                NamerMaterialInspection setInspection = BuildInspection(baseMap);
                setInspection.Material = new Material(shader) { name = "OffsetSet" };
                setInspection.RoughnessOffsetMap = offsetTex;

                NamerGeneratedAsset setAsset = Generate(pipeline, generator, setInspection, settings);
                Material setMaterial = AssetDatabase.LoadAssetAtPath<Material>(setAsset.MaterialPath);
                Assert.IsNotNull(setMaterial, "generated material must load: " + setAsset.MaterialPath);
                Assert.IsNotNull(setMaterial.GetTexture("_RoughnessOffsetMap"),
                    "a non-null inspection offset must bind _RoughnessOffsetMap");
                Assert.AreEqual(offsetTex, setMaterial.GetTexture("_RoughnessOffsetMap"),
                    "the bound offset texture must be the assigned texture");

                // Control: a null field must leave _RoughnessOffsetMap unbound ("black" default).
                NamerMaterialInspection nullInspection = BuildInspection(baseMap);
                nullInspection.Material = new Material(shader) { name = "OffsetNull" };
                nullInspection.RoughnessOffsetMap = null;

                NamerGeneratedAsset nullAsset = Generate(pipeline, generator, nullInspection, settings);
                Material nullMaterial = AssetDatabase.LoadAssetAtPath<Material>(nullAsset.MaterialPath);
                Assert.IsNotNull(nullMaterial, "control material must load: " + nullAsset.MaterialPath);
                Assert.IsNull(nullMaterial.GetTexture("_RoughnessOffsetMap"),
                    "a null inspection offset must leave _RoughnessOffsetMap unbound");
            }
            finally
            {
                Destroy(baseMap);
                pipeline.Dispose();
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Shared fixtures / helpers
        // --------------------------------------------------------------------

        private static NamerGeneratedAsset Generate(
            NamerComputePipeline pipeline,
            AssetGenerator generator,
            NamerMaterialInspection inspection,
            NamerProcessorSettings settings)
        {
            NamerComputeResult result = pipeline.Process(inspection);
            try
            {
                return generator.Generate(result, inspection, settings, TempFolder + "/", null);
            }
            finally
            {
                pipeline.ReleaseResult(result);
            }
        }

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                OcclusionMap = null,
                MetallicGlossMap = null,
                Metallic = 0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = 0f,
                DipSource = NamerDipSource.RemovedDetail,
                Emissive = 0f,
                AoUnmultiplyStrength = 1f,
                SmoothnessTextureChannel = 0,
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

        // -- ResidualPipelineTests-style helpers (Test 3) --------------------

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

        private static Color32[] ConstantColors(int count, byte value)
        {
            Color32[] colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                colors[i] = new Color32(value, value, value, 255);
            }

            return colors;
        }

        private static RenderTexture CreateHighFrequencyBase(int w, int h)
        {
            return UploadBase(w, h, (x, y) =>
            {
                float v = ((x / 8 + y / 8) % 2 == 0) ? 0.15f : 0.85f;
                return new Color(v, v, v, 1f);
            });
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
            UnityEngine.Object.DestroyImmediate(upload);
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

        private static void Release(params RenderTexture[] rts)
        {
            foreach (RenderTexture rt in rts)
            {
                if (rt != null)
                {
                    // Graphics.Blit leaves RenderTexture.active pointing at its destination;
                    // unbind before release (mirrors ComputeTexturePool's guard).
                    if (RenderTexture.active == rt)
                    {
                        RenderTexture.active = null;
                    }

                    rt.Release();
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }
        }

        private static void FillSolid(Texture2D texture, Color color)
        {
            Color[] pixels = new Color[texture.width * texture.height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        // -- AssetDatabase fixtures (Tests 1 + 4) ---------------------------

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
            Texture2D source = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    source.SetPixel(x, y, pixel(x, y, size));
                }
            }

            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Texture2D CreateImportedOffsetMap(string path)
        {
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, new Color(0.25f, 0f, 0f, 1f));
            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
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
            if (occlusionMap != null)
            {
                // Same D-07 isolation as NamerRoughnessFitTests: a null occlusion map makes
                // the pipeline EXTRACT AO from the base's own luminance and un-multiply it,
                // destroying the BakedResponse gradient. Authored white keeps it neutral.
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

        // -- EditorPrefs isolation (Tests 1) --------------------------------

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

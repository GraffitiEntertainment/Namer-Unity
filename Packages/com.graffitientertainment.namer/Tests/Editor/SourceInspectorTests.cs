using System.IO;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// INSP-01..04 EditMode coverage for <see cref="SourceInspector"/>: selection
    /// resolution (Material / scene GameObject / prefab asset / folder / Standard),
    /// URP Lit map discovery, scalar fallbacks, neutral defaults, dedupe, and
    /// source-asset immutability.
    /// </summary>
    public class SourceInspectorTests
    {
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        // -- INSP-01 / INSP-02: URP Lit discovery ----------------------------

        [Test]
        public void UrpLit_RecordsMapsScalarsSrgbAndSmoothnessChannel()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            Texture2D baseMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            Texture2D metallicGlossMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            Texture2D occlusionMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                material.SetTexture("_BaseMap", baseMap);
                material.SetTexture("_MetallicGlossMap", metallicGlossMap);
                material.SetTexture("_OcclusionMap", occlusionMap);
                material.SetFloat("_Metallic", 0.8f);
                material.SetFloat("_Smoothness", 0.25f);

                NamerSourceModel model = SourceInspector.Inspect(material);

                Assert.AreEqual(1, model.Materials.Count);
                NamerMaterialInspection i = model.Materials[0];
                Assert.AreSame(baseMap, i.BaseMap);
                Assert.AreSame(metallicGlossMap, i.MetallicGlossMap);
                Assert.AreSame(occlusionMap, i.OcclusionMap);
                Assert.AreEqual(0.8, (double)i.Metallic, 1e-6);
                Assert.AreEqual(0.25, (double)i.Smoothness, 1e-6);
                Assert.IsTrue(i.IsUrpLit);
                Assert.IsFalse(i.IsStandard);
                Assert.AreEqual(0, i.SmoothnessTextureChannel);
                Assert.IsFalse(i.BaseMapIsSrgb, "a linear Texture2D should record BaseMapIsSrgb == false");
            }
            finally
            {
                Destroy(material, baseMap, metallicGlossMap, occlusionMap);
            }
        }

        [Test]
        public void SmoothnessTextureChannel_ReflectsUrpProperty()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            try
            {
                material.SetFloat("_SmoothnessTextureChannel", 0f);
                Assert.AreEqual(0, SourceInspector.Inspect(material).Materials[0].SmoothnessTextureChannel);

                material.SetFloat("_SmoothnessTextureChannel", 1f);
                Assert.AreEqual(1, SourceInspector.Inspect(material).Materials[0].SmoothnessTextureChannel);
            }
            finally
            {
                Destroy(material);
            }
        }

        // -- WR-04: per-map sRGB metadata for data maps ------------------------

        [Test]
        public void DataMapSrgbFlags_RecordedAndWarnedOnlyForSrgbDataMaps()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);

            // Runtime-created Texture2Ds never surface an sRGB graphicsFormat on this
            // platform, and imported textures report linear graphicsFormat variants
            // even when imported sRGB — so the importer flag is the authoritative
            // authored-sRGB signal, and the fixture must come through the importer:
            // the exact path production consumes. Self-check the flags so a fixture
            // regression fails here loudly instead of as an inspector failure.
            EnsureTempFolder();
            Texture2D linearAo = CreateImportedMap(TempFolder + "/NamerTestAoLinear.png", false);
            Texture2D srgbMap = CreateImportedMap(TempFolder + "/NamerTestDataSrgb.png", true);
            TextureImporter linearImporter = (TextureImporter)AssetImporter.GetAtPath(TempFolder + "/NamerTestAoLinear.png");
            Assert.IsFalse(linearImporter.sRGBTexture, "fixture: linear map must keep sRGBTexture == false");
            TextureImporter srgbImporter = (TextureImporter)AssetImporter.GetAtPath(TempFolder + "/NamerTestDataSrgb.png");
            Assert.IsTrue(srgbImporter.sRGBTexture, "fixture: sRGB map must keep sRGBTexture == true");

            try
            {
                // Linear-authored AO map: flag recorded false, no sRGB warning.
                material.SetTexture("_OcclusionMap", linearAo);
                NamerMaterialInspection linearInspect = SourceInspector.Inspect(material).Materials[0];
                Assert.IsFalse(linearInspect.OcclusionMapIsSrgb, "linear AO map should record OcclusionMapIsSrgb == false");
                Assert.AreEqual(0, CountSrgbWarnings(linearInspect), "linear data maps must not raise sRGB warnings");

                // sRGB-authored data maps (TextureImporter default): flags recorded true
                // and one warning per map, since the kernels read them as raw texel data.
                material.SetTexture("_OcclusionMap", srgbMap);
                material.SetTexture("_MetallicGlossMap", srgbMap);
                material.SetTexture("_BumpMap", srgbMap);
                NamerMaterialInspection srgbInspect = SourceInspector.Inspect(material).Materials[0];
                Assert.IsTrue(srgbInspect.OcclusionMapIsSrgb, "sRGB AO map should record OcclusionMapIsSrgb == true");
                Assert.IsTrue(srgbInspect.MetallicGlossMapIsSrgb, "sRGB metallicGloss map should record MetallicGlossMapIsSrgb == true");
                Assert.IsTrue(srgbInspect.NormalMapIsSrgb, "sRGB normal map should record NormalMapIsSrgb == true");
                Assert.AreEqual(3, CountSrgbWarnings(srgbInspect), "one sRGB warning per sRGB-flagged data map");
            }
            finally
            {
                Destroy(material);
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// Creates a 1x1 imported texture whose sRGB flag comes from the
        /// TextureImporter (the path real source assets take), because
        /// runtime-constructed Texture2Ds do not report sRGB graphics formats.
        /// </summary>
        private static Texture2D CreateImportedMap(string path, bool srgb)
        {
            Texture2D source = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, new Color(0.5f, 0.5f, 0.5f, 1f));
            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = srgb;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static int CountSrgbWarnings(NamerMaterialInspection inspection)
        {
            int count = 0;
            foreach (string warning in inspection.Warnings)
            {
                if (warning.Contains("sRGB"))
                {
                    count++;
                }
            }

            return count;
        }

        // -- INSP-04: neutral defaults ----------------------------------------

        [Test]
        public void NeutralDefaults_BareUrpLitMaterialFallsBack()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            try
            {
                NamerSourceModel model = SourceInspector.Inspect(material);
                Assert.AreEqual(1, model.Materials.Count);
                NamerMaterialInspection i = model.Materials[0];

                Assert.AreEqual(0.0, (double)i.Metallic, 1e-6);
                Assert.AreEqual(0.5, (double)i.Roughness, 1e-6);
                Assert.AreEqual(1.0, (double)i.OcclusionStrength, 1e-6);
                Assert.IsNull(i.OcclusionMap, "absent occlusion map -> AO neutral default (1.0) downstream");
                Assert.AreEqual(0.0, (double)i.EmissionColor.r, 1e-6, "emission should be black");
                Assert.AreEqual(0.0, (double)i.EmissionColor.g, 1e-6);
                Assert.AreEqual(0.0, (double)i.EmissionColor.b, 1e-6);
                Assert.AreEqual(0.0, (double)i.Emissive, 1e-6);
                Assert.IsNotNull(i.Warnings);
                Assert.Greater(i.Warnings.Count, 0, "bare material should record fallback warnings");
            }
            finally
            {
                Destroy(material);
            }
        }

        // -- D-03: dedupe -----------------------------------------------------

        [Test]
        public void Dedupe_SharedMaterialYieldsSingleInspection()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material shared = new Material(shader);
            GameObject parent = new GameObject("DedupeParent");
            GameObject childA = new GameObject("ChildA");
            GameObject childB = new GameObject("ChildB");
            childA.transform.SetParent(parent.transform);
            childB.transform.SetParent(parent.transform);
            MeshRenderer rendererA = childA.AddComponent<MeshRenderer>();
            MeshRenderer rendererB = childB.AddComponent<MeshRenderer>();
            try
            {
                rendererA.sharedMaterial = shared;
                rendererB.sharedMaterial = shared;

                NamerSourceModel model = SourceInspector.Inspect(parent);
                Assert.AreEqual(1, model.Materials.Count, "shared material must dedupe to one inspection unit");
                Assert.AreEqual(shared.GetInstanceID(), model.Materials[0].Material.GetInstanceID());
            }
            finally
            {
                Destroy(parent, shared);
            }
        }

        // -- D-06: unknown shader --------------------------------------------

        [Test]
        public void UnknownShader_ReturnsWarningWithoutThrowing()
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Assert.Ignore("Sprites/Default built-in shader not available");
            }

            Material material = new Material(shader);
            try
            {
                NamerSourceModel model = SourceInspector.Inspect(material);
                Assert.AreEqual(1, model.Materials.Count);
                Assert.IsNotNull(model.Materials[0].Warnings);
                Assert.Greater(model.Materials[0].Warnings.Count, 0, "unknown shader should record a warning");
            }
            finally
            {
                Destroy(material);
            }
        }

        // -- INSP-03: Unity Standard -----------------------------------------

        [Test]
        public void Standard_RecordsMapsAndScalars()
        {
            Shader standard = Shader.Find("Standard");
            if (standard == null)
            {
                Assert.Ignore("Standard shader not available");
            }

            Material material = new Material(standard);
            Texture2D mainTex = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            Texture2D metallicGlossMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            try
            {
                material.SetTexture("_MainTex", mainTex);
                material.SetTexture("_MetallicGlossMap", metallicGlossMap);
                material.SetFloat("_Glossiness", 0.7f);

                NamerSourceModel model = SourceInspector.Inspect(material);
                Assert.AreEqual(1, model.Materials.Count);
                NamerMaterialInspection i = model.Materials[0];
                Assert.IsTrue(i.IsStandard);
                Assert.IsFalse(i.IsUrpLit);
                Assert.AreSame(mainTex, i.BaseMap);
                Assert.AreSame(metallicGlossMap, i.MetallicGlossMap);
                Assert.AreEqual(0.7, (double)i.Smoothness, 1e-6);
            }
            finally
            {
                Destroy(material, mainTex, metallicGlossMap);
            }
        }

        // -- INSP-01: folder resolution --------------------------------------

        [Test]
        public void FolderResolution_FindsMaterialInFolder()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            EnsureTempFolder();
            Material material = new Material(shader);
            try
            {
                AssetDatabase.CreateAsset(material, TempFolder + "/NamerTestMaterial.mat");
                DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(TempFolder);
                Assert.IsNotNull(folder, "temp folder DefaultAsset should load");

                NamerSourceModel model = SourceInspector.Inspect(folder);
                bool found = false;
                foreach (NamerMaterialInspection i in model.Materials)
                {
                    if (i.Material == material)
                    {
                        found = true;
                    }
                }

                Assert.IsTrue(found, "folder inspection should find the material created inside it");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        // -- INSP-01 / WR-03: folder resolution of prefab assets ----------------

        [Test]
        public void FolderResolution_FindsMaterialsInsidePrefabs()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            EnsureTempFolder();
            Material shared = new Material(shader);
            AssetDatabase.CreateAsset(shared, TempFolder + "/NamerTestFolderSharedMaterial.mat");
            GameObject source = new GameObject("FolderPrefabSource");
            MeshRenderer renderer = source.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = shared;
            try
            {
                PrefabUtility.SaveAsPrefabAsset(source, TempFolder + "/NamerTestFolderPrefab.prefab");
                DefaultAsset folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(TempFolder);
                Assert.IsNotNull(folder, "temp folder DefaultAsset should load");

                // The .mat also lives in the folder, so the t:Material pass finds it too;
                // the prefab pass must resolve the same instance and dedupe to one entry.
                NamerSourceModel model = SourceInspector.Inspect(folder);
                Assert.AreEqual(1, model.Materials.Count,
                    "folder of prefabs should resolve the prefab's material, deduped with the direct material asset");
                Assert.AreEqual(shared.GetInstanceID(), model.Materials[0].Material.GetInstanceID());
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(source);
            }
        }

        // -- INSP-01: prefab-asset resolution ---------------------------------

        [Test]
        public void PrefabAssetResolution_FindsMaterialViaPrefabContents()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            EnsureTempFolder();
            Material shared = new Material(shader);
            AssetDatabase.CreateAsset(shared, TempFolder + "/NamerTestSharedMaterial.mat");
            GameObject source = new GameObject("PrefabSource");
            MeshRenderer renderer = source.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = shared;
            try
            {
                PrefabUtility.SaveAsPrefabAsset(source, TempFolder + "/NamerTestPrefab.prefab");
                GameObject prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(TempFolder + "/NamerTestPrefab.prefab");
                Assert.IsNotNull(prefabAsset, "saved prefab asset should load");

                // Branch discriminator: scene source has an empty asset path; the prefab
                // asset has a non-empty path and takes the LoadPrefabContents path.
                Assert.AreEqual("", AssetDatabase.GetAssetPath(source), "scene GameObject should have an empty asset path");
                Assert.IsNotEmpty(AssetDatabase.GetAssetPath(prefabAsset), "prefab asset should have a non-empty asset path");

                NamerSourceModel model = SourceInspector.Inspect(prefabAsset);
                Assert.AreEqual(1, model.Materials.Count, "prefab inspection should resolve one material");
                Assert.AreEqual(shared.GetInstanceID(), model.Materials[0].Material.GetInstanceID());
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(source);
            }
        }

        // -- Pitfall 5: source immutability ----------------------------------

        [Test]
        public void SourceImmutability_InspectionDoesNotChangeTextureFlags()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Material material = new Material(shader);
            Texture2D baseMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            material.SetTexture("_BaseMap", baseMap);
            try
            {
                bool isReadableBefore = baseMap.isReadable;
                bool srgbBefore = GraphicsFormatUtility.IsSRGBFormat(baseMap.graphicsFormat);

                SourceInspector.Inspect(material);

                Assert.AreEqual(isReadableBefore, baseMap.isReadable, "isReadable must not change during inspection");
                Assert.AreEqual(srgbBefore, GraphicsFormatUtility.IsSRGBFormat(baseMap.graphicsFormat), "sRGB flag must not change during inspection");
            }
            finally
            {
                Destroy(material, baseMap);
            }
        }

        // --------------------------------------------------------------------

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
    }
}

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// CPU front door of the NAMER pipeline (D-01: an API, not UI). Resolves any
    /// selection kind — Material, scene GameObject, prefab asset, FBX/model asset, or
    /// folder — into a deduplicated set of unique materials (D-02/D-03) and reads each
    /// material's PBR maps and scalar fallbacks through per-shader property tables.
    ///
    /// Map-discovery conventions locked here (URP Lit <c>_SmoothnessTextureChannel</c>
    /// indirection, AO in the green channel of <c>_OcclusionMap</c>, metallic = R /
    /// smoothness = A of <c>_MetallicGlossMap</c>) before the GPU kernels consume them.
    /// Source assets are never mutated (no importer-flag flips or asset writes);
    /// source sRGB/isReadable flags are only read.
    /// </summary>
    public static class SourceInspector
    {
        [MenuItem("Tools/NAMER/Inspect Selection")]
        public static void InspectSelection()
        {
            Object selection = Selection.activeObject != null ? Selection.activeObject : Selection.activeGameObject;
            if (selection == null)
            {
                Debug.Log("[NAMER] Inspect Selection: no selection.");
                return;
            }

            NamerSourceModel model = Inspect(selection);
            LogReport(selection, model);
        }

        /// <summary>
        /// Inspects <paramref name="selection"/> and returns a <see cref="NamerSourceModel"/>
        /// with one inspection unit per unique material. Never throws on missing data
        /// (D-02/D-06); unsupported inputs and fallbacks are recorded as warnings.
        /// </summary>
        public static NamerSourceModel Inspect(Object selection)
        {
            var model = new NamerSourceModel();
            if (selection == null)
            {
                model.Warnings.Add("No selection provided.");
                return model;
            }

            var materials = new List<Material>();
            var seen = new HashSet<int>();
            CollectMaterials(selection, materials, seen, model.Warnings);

            foreach (Material material in materials)
            {
                model.Materials.Add(InspectMaterial(material));
            }

            return model;
        }

        // ---------------------------------------------------------------------
        // Selection resolution (INSP-01)
        // ---------------------------------------------------------------------

        private static void CollectMaterials(Object selection, List<Material> materials, HashSet<int> seen, List<string> warnings)
        {
            if (selection is Material material)
            {
                AddUnique(material, materials, seen);
                return;
            }

            if (selection is GameObject go)
            {
                string assetPath = AssetDatabase.GetAssetPath(go);

                // Scene object: an empty asset path means the GameObject lives in the
                // open scene, so read renderers directly.
                if (string.IsNullOrEmpty(assetPath))
                {
                    foreach (Renderer renderer in go.GetComponentsInChildren<Renderer>(true))
                    {
                        AddSharedMaterials(renderer, materials, seen);
                    }

                    return;
                }

                // Prefab asset vs model asset. Prefab assets support LoadPrefabContents;
                // FBX/model assets expose materials only as sub-assets.
                PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(go);
                if (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant)
                {
                    AddPrefabContentsMaterials(assetPath, materials, seen);
                    return;
                }

                // Model asset (FBX) or any GameObject asset without prefab contents:
                // materials are sub-assets, not components.
                AddSubAssetMaterials(assetPath, materials, seen);

                return;
            }

            string path = AssetDatabase.GetAssetPath(selection);

            // Folder (DefaultAsset): recurse for materials, prefab-contained materials,
            // and model sub-asset materials.
            if (selection is DefaultAsset && AssetDatabase.IsValidFolder(path))
            {
                foreach (string guid in AssetDatabase.FindAssets("t:Material", new[] { path }))
                {
                    Material m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                    AddUnique(m, materials, seen);
                }

                // Prefab assets are the most common material container; resolve their
                // renderers through prefab contents exactly like a direct prefab
                // selection would (WR-03).
                foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { path }))
                {
                    AddPrefabContentsMaterials(AssetDatabase.GUIDToAssetPath(guid), materials, seen);
                }

                foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { path }))
                {
                    AddSubAssetMaterials(AssetDatabase.GUIDToAssetPath(guid), materials, seen);
                }

                return;
            }

            // Any other asset with a path (e.g. a model selected as a non-GameObject
            // asset): materials are sub-assets.
            if (!string.IsNullOrEmpty(path))
            {
                AddSubAssetMaterials(path, materials, seen);

                return;
            }

            warnings.Add("Unsupported selection type '" + (selection != null ? selection.GetType().Name : "null") + "' — no materials inspected.");
        }

        private static void AddSharedMaterials(Renderer renderer, List<Material> materials, HashSet<int> seen)
        {
            foreach (Material m in renderer.sharedMaterials)
            {
                AddUnique(m, materials, seen);
            }
        }

        /// <summary>
        /// Resolves a prefab asset's materials by loading its prefab contents and walking
        /// all renderers (inactive included), unloading the contents afterwards. Shared by
        /// the prefab-selection path and the folder path so both resolve identically.
        /// </summary>
        private static void AddPrefabContentsMaterials(string assetPath, List<Material> materials, HashSet<int> seen)
        {
            GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
            try
            {
                if (contents != null)
                {
                    foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                    {
                        AddSharedMaterials(renderer, materials, seen);
                    }
                }
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
        }

        private static void AddSubAssetMaterials(string assetPath, List<Material> materials, HashSet<int> seen)
        {
            // LoadAllAssetsAtPath is non-generic (returns Object[]); filter materials.
            foreach (Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (subAsset is Material material)
                {
                    AddUnique(material, materials, seen);
                }
            }
        }

        private static void AddUnique(Material material, List<Material> materials, HashSet<int> seen)
        {
            if (material == null)
            {
                return;
            }

            // Deduplicate by instance ID so shared materials are inspected once (D-02/D-03).
            if (seen.Add(material.GetInstanceID()))
            {
                materials.Add(material);
            }
        }

        // ---------------------------------------------------------------------
        // Per-material inspection (INSP-02/03/04)
        // ---------------------------------------------------------------------

        private static NamerMaterialInspection InspectMaterial(Material material)
        {
            var result = new NamerMaterialInspection { Material = material };
            result.ShaderName = material.shader != null ? material.shader.name : "(missing shader)";
            result.IsUrpLit = result.ShaderName == "Universal Render Pipeline/Lit";
            result.IsStandard = result.ShaderName == "Standard";

            // Neutral defaults (D-07): metallic 0, smoothness 0.5 -> roughness 0.5,
            // AO 1.0 (recorded as no occlusion map downstream), emission black,
            // base color white, normal neutral (no map).
            result.Metallic = 0f;
            result.Smoothness = 0.5f;
            result.SmoothnessTextureChannel = 0;
            result.OcclusionStrength = 1f;
            result.AoUnmultiplyStrength = 1f;
            result.EmissionColor = Color.black;
            result.BaseColor = Color.white;
            result.BumpScale = 1f;
            result.Cutoff = 0.5f;
            result.SurfaceType = 0f;

            if (result.IsUrpLit)
            {
                ReadUrpLit(material, result);
            }
            else if (result.IsStandard)
            {
                ReadStandard(material, result);
            }
            else
            {
                ReadGeneric(material, result);
                result.Warnings.Add("Unsupported shader '" + result.ShaderName + "' — using best-effort property scan.");
            }

            FinalizeInspection(result);
            return result;
        }

        private static void ReadUrpLit(Material material, NamerMaterialInspection result)
        {
            result.BaseMap = TryGetTexture(material, "_BaseMap");
            result.BaseMapIsSrgb = result.BaseMap != null && GraphicsFormatUtility.IsSRGBFormat(result.BaseMap.graphicsFormat);
            result.BaseColor = TryGetColor(material, "_BaseColor", Color.white);

            result.NormalMap = TryGetTexture(material, "_BumpMap");
            result.BumpScale = TryGetFloat(material, "_BumpScale", 1f);

            result.MetallicGlossMap = TryGetTexture(material, "_MetallicGlossMap");
            result.Metallic = TryGetFloat(material, "_Metallic", 0f);
            result.Smoothness = TryGetFloat(material, "_Smoothness", 0.5f);
            result.SmoothnessTextureChannel = TryGetInt(material, "_SmoothnessTextureChannel", 0);

            result.OcclusionMap = TryGetTexture(material, "_OcclusionMap");
            result.OcclusionStrength = TryGetFloat(material, "_OcclusionStrength", 1f);

            result.EmissionMap = TryGetTexture(material, "_EmissionMap");
            result.EmissionColor = TryGetColor(material, "_EmissionColor", Color.black);

            result.SurfaceType = TryGetFloat(material, "_Surface", 0f);
            result.Cutoff = TryGetFloat(material, "_Cutoff", 0.5f);
        }

        private static void ReadStandard(Material material, NamerMaterialInspection result)
        {
            result.BaseMap = TryGetTexture(material, "_MainTex");
            result.BaseMapIsSrgb = result.BaseMap != null && GraphicsFormatUtility.IsSRGBFormat(result.BaseMap.graphicsFormat);
            result.BaseColor = TryGetColor(material, "_Color", Color.white);

            result.NormalMap = TryGetTexture(material, "_BumpMap");
            result.BumpScale = TryGetFloat(material, "_BumpScale", 1f);

            result.MetallicGlossMap = TryGetTexture(material, "_MetallicGlossMap");
            result.Metallic = TryGetFloat(material, "_Metallic", 0f);
            // Standard names the smoothness scalar _Glossiness (not _Smoothness).
            result.Smoothness = TryGetFloat(material, "_Glossiness", 0.5f);
            result.SmoothnessTextureChannel = 0;

            result.OcclusionMap = TryGetTexture(material, "_OcclusionMap");
            result.OcclusionStrength = TryGetFloat(material, "_OcclusionStrength", 1f);

            result.EmissionMap = TryGetTexture(material, "_EmissionMap");
            result.EmissionColor = TryGetColor(material, "_EmissionColor", Color.black);

            result.Cutoff = TryGetFloat(material, "_Cutoff", 0.5f);

            // Best-effort surface type from the Standard _Mode keyword (2 = Fade, 3 = Transparent).
            if (material.HasProperty("_Mode"))
            {
                result.SurfaceType = material.GetFloat("_Mode") >= 2f ? 1f : 0f;
            }
        }

        private static void ReadGeneric(Material material, NamerMaterialInspection result)
        {
            // Best-effort scan of the known property names (D-06): try URP Lit names
            // first, then Standard aliases, guarded by HasProperty.
            result.BaseMap = TryGetTexture(material, "_BaseMap") ?? TryGetTexture(material, "_MainTex");
            result.BaseMapIsSrgb = result.BaseMap != null && GraphicsFormatUtility.IsSRGBFormat(result.BaseMap.graphicsFormat);
            result.BaseColor = TryGetColor(material, "_BaseColor", TryGetColor(material, "_Color", Color.white));

            result.NormalMap = TryGetTexture(material, "_BumpMap");
            result.BumpScale = TryGetFloat(material, "_BumpScale", 1f);

            result.MetallicGlossMap = TryGetTexture(material, "_MetallicGlossMap");
            result.Metallic = TryGetFloat(material, "_Metallic", 0f);
            result.Smoothness = TryGetFloat(material, "_Smoothness", TryGetFloat(material, "_Glossiness", 0.5f));
            result.SmoothnessTextureChannel = TryGetInt(material, "_SmoothnessTextureChannel", 0);

            result.OcclusionMap = TryGetTexture(material, "_OcclusionMap");
            result.OcclusionStrength = TryGetFloat(material, "_OcclusionStrength", 1f);

            result.EmissionMap = TryGetTexture(material, "_EmissionMap");
            result.EmissionColor = TryGetColor(material, "_EmissionColor", Color.black);

            result.Cutoff = TryGetFloat(material, "_Cutoff", 0.5f);
            result.SurfaceType = TryGetFloat(material, "_Surface", 0f);
        }

        private static void FinalizeInspection(NamerMaterialInspection result)
        {
            result.Roughness = 1f - result.Smoothness;

            // Per-map sRGB metadata (WR-04). The compute kernels consume normal / AO /
            // metallicGloss maps as raw texel data — only the base map is ever sRGB
            // decoded — so an sRGB-imported data map contradicts the kernels' assumption
            // and is worth surfacing to the artist.
            result.NormalMapIsSrgb = IsSrgb(result.NormalMap);
            result.MetallicGlossMapIsSrgb = IsSrgb(result.MetallicGlossMap);
            result.OcclusionMapIsSrgb = IsSrgb(result.OcclusionMap);

            if (result.NormalMapIsSrgb)
            {
                result.Warnings.Add("NormalMap '" + result.NormalMap.name
                    + "' is flagged sRGB — NAMER reads normal maps as raw texel data without conversion; reimport it as linear for exact results.");
            }

            if (result.MetallicGlossMapIsSrgb)
            {
                result.Warnings.Add("MetallicGlossMap '" + result.MetallicGlossMap.name
                    + "' is flagged sRGB — NAMER reads metallic/smoothness as raw texel data without conversion; reimport it as linear for exact results.");
            }

            if (result.OcclusionMapIsSrgb)
            {
                result.Warnings.Add("OcclusionMap '" + result.OcclusionMap.name
                    + "' is flagged sRGB — NAMER reads AO as raw texel data without conversion; reimport it as linear for exact results.");
            }

            // Emissive = max(EmissionColor.r, g, b) when emission is present
            // (map exists OR emission color is non-black), else 0.
            bool emissionColorNonBlack =
                result.EmissionColor.r > 0f || result.EmissionColor.g > 0f || result.EmissionColor.b > 0f;
            bool hasEmission = result.EmissionMap != null || emissionColorNonBlack;
            result.Emissive = hasEmission
                ? Mathf.Max(result.EmissionColor.r, result.EmissionColor.g, result.EmissionColor.b)
                : 0f;

            // D-07 neutral-default notice for a fully bare material.
            if (result.BaseMap == null
                && result.NormalMap == null
                && result.MetallicGlossMap == null
                && result.OcclusionMap == null
                && result.EmissionMap == null)
            {
                result.Warnings.Add("No PBR maps found — using scalar and neutral defaults.");
            }
        }

        // ---------------------------------------------------------------------
        // Guarded property reads (T-02-02 input validation)
        // ---------------------------------------------------------------------

        private static Texture2D TryGetTexture(Material material, string name)
        {
            return material.HasProperty(name) ? material.GetTexture(name) as Texture2D : null;
        }

        private static bool IsSrgb(Texture2D map)
        {
            return map != null && GraphicsFormatUtility.IsSRGBFormat(map.graphicsFormat);
        }

        private static float TryGetFloat(Material material, string name, float fallback)
        {
            return material.HasProperty(name) ? material.GetFloat(name) : fallback;
        }

        private static Color TryGetColor(Material material, string name, Color fallback)
        {
            return material.HasProperty(name) ? material.GetColor(name) : fallback;
        }

        private static int TryGetInt(Material material, string name, int fallback)
        {
            return material.HasProperty(name) ? Mathf.RoundToInt(material.GetFloat(name)) : fallback;
        }

        // ---------------------------------------------------------------------
        // Manual-validation report (D-01)
        // ---------------------------------------------------------------------

        private static void LogReport(Object selection, NamerSourceModel model)
        {
            Debug.Log("[NAMER] Inspect Selection: '" + selection.name + "' (" + selection.GetType().Name + ") -> "
                + model.Materials.Count + " unique material(s)");

            foreach (string warning in model.Warnings)
            {
                Debug.Log("[NAMER]   [warning] " + warning);
            }

            foreach (NamerMaterialInspection i in model.Materials)
            {
                string name = i.Material != null ? i.Material.name : "(null)";
                Debug.Log("[NAMER]   [Material] " + name + " — shader '" + i.ShaderName + "'");
                Debug.Log("[NAMER]     BaseMap: " + DescribeMap(i.BaseMap, i.BaseMapIsSrgb)
                    + ", BaseColor: " + i.BaseColor);
                Debug.Log("[NAMER]     NormalMap: " + DescribeMap(i.NormalMap, i.NormalMapIsSrgb) + ", BumpScale: " + i.BumpScale);
                Debug.Log("[NAMER]     MetallicGlossMap: " + DescribeMap(i.MetallicGlossMap, i.MetallicGlossMapIsSrgb)
                    + ", Metallic: " + i.Metallic + ", Smoothness: " + i.Smoothness
                    + ", Roughness: " + i.Roughness + ", SmoothnessTextureChannel: " + i.SmoothnessTextureChannel);
                Debug.Log("[NAMER]     OcclusionMap: " + DescribeMap(i.OcclusionMap, i.OcclusionMapIsSrgb)
                    + ", OcclusionStrength: " + i.OcclusionStrength + ", AoUnmultiplyStrength: " + i.AoUnmultiplyStrength);
                Debug.Log("[NAMER]     EmissionMap: " + DescribeMap(i.EmissionMap, false)
                    + ", EmissionColor: " + i.EmissionColor + ", Emissive: " + i.Emissive);
                Debug.Log("[NAMER]     SurfaceType: " + i.SurfaceType + ", Cutoff: " + i.Cutoff
                    + ", Warnings: " + i.Warnings.Count);
            }
        }

        private static string DescribeMap(Texture2D map, bool isSrgb)
        {
            if (map == null)
            {
                return "(fallback)";
            }

            return map.name + (isSrgb ? " (sRGB)" : " (linear)");
        }
    }
}

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Editor-only scaffolding for the NAMER visual smoke check (SHDR-03).
    /// Configures URP, then generates a smoke scene with a NAMER material and a
    /// URP Lit material side by side, both fed by a deterministic golden neutral
    /// packed surface sample. Run via <c>Tools &gt; NAMER &gt; Create Smoke Scene</c>.
    /// </summary>
    public static class NamerSmokeSetup
    {
        private const string NamerRoot = "Assets/NAMER";
        private const string URPAssetPath = NamerRoot + "/NamerSmokeURP.asset";
        private const string RendererDataPath = NamerRoot + "/NamerSmokeRendererData.asset";
        private const string SmokeScenePath = NamerRoot + "/NamerSmoke.unity";
        private const string SurfaceMapPath = NamerRoot + "/NamerSmoke_Surface.asset";
        private const string BaseResidualMapPath = NamerRoot + "/NamerSmoke_BaseResidual.asset";
        private const string NamerMaterialPath = NamerRoot + "/NamerSmoke_Material.mat";
        private const string UrpLitMaterialPath = NamerRoot + "/NamerSmoke_URPLit.mat";

        [MenuItem("Tools/NAMER/Create Smoke Scene")]
        public static void CreateSmokeScene()
        {
            EnsureFolder();

            // 1. Configure URP if no URP pipeline is currently active.
            ConfigureURP();

            // 2. Build the NAMER material with the golden neutral packed surface texel.
            Material namerMaterial = CreateNamerMaterial();

            // 3. Build the side-by-side smoke scene (NAMER + URP Lit spheres).
            if (!BuildSmokeScene(namerMaterial))
            {
                return; // The user declined to save/close the open scene.
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log("[NAMER] Smoke scene created at " + SmokeScenePath);
        }

        private static void EnsureFolder()
        {
            if (!AssetDatabase.IsValidFolder(NamerRoot))
            {
                AssetDatabase.CreateFolder("Assets", "NAMER");
            }
        }

        private static void ConfigureURP()
        {
            // URP is only effectively active when the project default is a URP asset
            // AND the active quality level has no non-URP override (a null quality
            // override inherits the project default).
            bool defaultIsUrp = GraphicsSettings.defaultRenderPipeline is UniversalRenderPipelineAsset;
            bool qualityOverrideIsUrpOrNone =
                QualitySettings.renderPipeline == null ||
                QualitySettings.renderPipeline is UniversalRenderPipelineAsset;

            if (defaultIsUrp && qualityOverrideIsUrpOrNone)
            {
                return; // A URP asset is already active at every level that matters.
            }

            // Assigning below permanently rewrites ProjectSettings (Graphics +
            // Quality); confirm before touching global state (cancel = skip).
            bool overwritesExistingPipeline =
                GraphicsSettings.defaultRenderPipeline != null ||
                QualitySettings.renderPipeline != null;

            string message = overwritesExistingPipeline
                ? "The current render pipeline settings are not fully configured for URP.\n\n"
                    + "Assign 'NamerSmokeURP' to GraphicsSettings.defaultRenderPipeline and the "
                    + "active quality level's renderPipeline override?\n\n"
                    + "This permanently rewrites ProjectSettings (Graphics + Quality) and is not "
                    + "restored by this tool."
                : "No render pipeline is assigned.\n\n"
                    + "Assign 'NamerSmokeURP' to GraphicsSettings.defaultRenderPipeline and the "
                    + "active quality level's renderPipeline?\n\n"
                    + "This permanently rewrites ProjectSettings (Graphics + Quality) and is not "
                    + "restored by this tool.";

            if (!EditorUtility.DisplayDialog("NAMER Smoke Setup", message, "Assign URP Asset", "Skip"))
            {
                Debug.Log("[NAMER] URP configuration skipped by user; assign a URP asset manually "
                    + "(Project Settings > Graphics / Quality) if the smoke scene renders pink.");
                return;
            }

            UniversalRendererData rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
            AssetDatabase.CreateAsset(rendererData, RendererDataPath);

            UniversalRenderPipelineAsset urpAsset = UniversalRenderPipelineAsset.Create(rendererData);
            AssetDatabase.CreateAsset(urpAsset, URPAssetPath);

            GraphicsSettings.defaultRenderPipeline = urpAsset;
            QualitySettings.renderPipeline = urpAsset;
        }

        private static Material CreateNamerMaterial()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            if (shader == null)
            {
                throw new System.InvalidOperationException("Shader 'GraffitiEntertainment.Namer/NAMER' was not found. Ensure the NAMER shader compiled and imported.");
            }

            // Golden neutral packed surface texel: octahedral (0.625, 0.625),
            // AO 1.0, packed alpha 31/255 (metallic=0, emissive=0, roughness=floor(0.5*63)=31).
            // Linear (non-sRGB), point-filtered, R8G8B8A8_UNorm.
            Texture2D surfaceMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            surfaceMap.name = "NamerSmoke_Surface";
            surfaceMap.filterMode = FilterMode.Point;
            surfaceMap.SetPixel(0, 0, new Color(0.625f, 0.625f, 1.0f, 31.0f / 255.0f));
            surfaceMap.Apply(false, false);

            // Base/residual: sRGB white (residual == base in Phase 1).
            Texture2D baseResidualMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, false);
            baseResidualMap.name = "NamerSmoke_BaseResidual";
            baseResidualMap.filterMode = FilterMode.Point;
            baseResidualMap.SetPixel(0, 0, Color.white);
            baseResidualMap.Apply(false, false);

            Material material = new Material(shader);
            material.name = "NamerSmoke_Material";
            material.SetTexture("_SurfaceMap", surfaceMap);
            material.SetTexture("_BaseResidualMap", baseResidualMap);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_EmissionColor", Color.black);
            material.SetFloat("_OcclusionStrength", 1.0f);

            AssetDatabase.CreateAsset(surfaceMap, SurfaceMapPath);
            AssetDatabase.CreateAsset(baseResidualMap, BaseResidualMapPath);
            AssetDatabase.CreateAsset(material, NamerMaterialPath);

            return material;
        }

        private static bool BuildSmokeScene(Material namerMaterial)
        {
            // Prompt before closing the open scene: NewScene(Single) would otherwise
            // silently discard unsaved scene modifications with no undo path.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsToContinue())
            {
                Debug.Log("[NAMER] Smoke scene creation cancelled; the open scene was left untouched.");
                return false;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // NAMER sphere (left).
            GameObject namerSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            namerSphere.name = "NAMER_Sphere";
            namerSphere.transform.position = new Vector3(-1.0f, 0.0f, 0.0f);
            namerSphere.GetComponent<Renderer>().sharedMaterial = namerMaterial;

            // URP Lit sphere (right) for the apples-to-apples SHDR-03 comparison.
            Shader urpLitShader = Shader.Find("Universal Render Pipeline/Lit");
            if (urpLitShader == null)
            {
                throw new System.InvalidOperationException("Shader 'Universal Render Pipeline/Lit' was not found. Ensure URP is installed.");
            }

            Material urpLitMaterial = new Material(urpLitShader);
            urpLitMaterial.name = "NamerSmoke_URPLit";
            urpLitMaterial.color = Color.white;
            AssetDatabase.CreateAsset(urpLitMaterial, UrpLitMaterialPath);

            GameObject urpSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            urpSphere.name = "URP_Lit_Sphere";
            urpSphere.transform.position = new Vector3(1.0f, 0.0f, 0.0f);
            urpSphere.GetComponent<Renderer>().sharedMaterial = urpLitMaterial;

            // Camera.
            GameObject camera = new GameObject("Main Camera");
            camera.tag = "MainCamera";
            Camera cam = camera.AddComponent<Camera>();
            cam.transform.position = new Vector3(0.0f, 0.0f, -5.0f);
            cam.transform.LookAt(Vector3.zero);

            // Directional light.
            GameObject light = new GameObject("Directional Light");
            Light directionalLight = light.AddComponent<Light>();
            directionalLight.type = LightType.Directional;
            directionalLight.intensity = 1.0f;
            light.transform.rotation = Quaternion.Euler(50.0f, -30.0f, 0.0f);

            EditorSceneManager.SaveScene(scene, SmokeScenePath);
            return true;
        }
    }
}

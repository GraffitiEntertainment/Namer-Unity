using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// UI-03: proves the generated/live flip contract of <see cref="NamerAfterPanelState"/>
    /// and that the window's generated-path detection uses the exact same folder/file
    /// convention <see cref="NamerProcessor.Process"/> writes (D-16). Pure logic — no GPU
    /// or compute gate. The two <c>NamerProcessor.Prefix</c>/<c>Suffix</c> EditorPrefs keys
    /// are snapshotted and restored so a mutated prefix/suffix never leaks into user state.
    /// </summary>
    public class NamerAfterPanelStateTests
    {
        [Test]
        public void AfterPanelState_FlipsBetweenGeneratedAndLive()
        {
            NamerAfterPanelState state = new NamerAfterPanelState();
            state.GeneratedAvailable = true;
            state.Reset();
            Assert.IsTrue(state.PreferGenerated, "generated mode when a generated material exists");

            state.MarkTweaking();
            Assert.IsFalse(state.PreferGenerated, "flip to live on AO slider/occluder change");

            state.Reset();
            Assert.IsTrue(state.PreferGenerated, "flip back to generated after a successful Process");

            state.GeneratedAvailable = false;
            Assert.IsFalse(state.PreferGenerated, "live until a generated material exists");
        }

        [Test]
        public void GeneratedPaths_MatchProcessConvention()
        {
            const string prefixKey = "NamerProcessor.Prefix";
            const string suffixKey = "NamerProcessor.Suffix";

            string prefix = EditorPrefs.GetString(prefixKey, string.Empty);
            string suffix = EditorPrefs.GetString(suffixKey, string.Empty);
            bool hadPrefix = EditorPrefs.HasKey(prefixKey);
            bool hadSuffix = EditorPrefs.HasKey(suffixKey);

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");
            Material material = new Material(shader)
            {
                name = "MyMat",
                hideFlags = HideFlags.HideAndDontSave,
            };

            try
            {
                var settings = new NamerProcessorSettings
                {
                    Prefix = "P_",
                    Suffix = "_N",
                };

                NamerMaterialInspection inspection = new NamerMaterialInspection { Material = material };

                string folder = AssetGenerator.ComposeDestinationFolder("Assets/NAMERGenerated/", "My:Model/01");
                Assert.AreEqual("Assets/NAMERGenerated/MyModel01/", folder, "sanitizer strips ':' and '/'");

                Assert.AreEqual(
                    "Assets/NAMERGenerated/MyModel01/P_MyMat_N.mat",
                    AssetGenerator.ComposePath(inspection, settings, folder, ".mat"),
                    "material path must match the Process convention");

                Assert.AreEqual(
                    "Assets/NAMERGenerated/MyModel01/P_MyMat_N_Surface.png",
                    AssetGenerator.ComposePath(inspection, settings, folder, "_Surface.png"),
                    "surface path must match the Process convention");

                Assert.AreEqual(
                    "Assets/NAMERGenerated/MyModel01/P_MyMat_N_Base.png",
                    AssetGenerator.ComposePath(inspection, settings, folder, "_Base.png"),
                    "base path must match the Process convention");
            }
            finally
            {
                Object.DestroyImmediate(material);

                if (hadPrefix)
                {
                    EditorPrefs.SetString(prefixKey, prefix);
                }
                else
                {
                    EditorPrefs.DeleteKey(prefixKey);
                }

                if (hadSuffix)
                {
                    EditorPrefs.SetString(suffixKey, suffix);
                }
                else
                {
                    EditorPrefs.DeleteKey(suffixKey);
                }
            }
        }
    }
}

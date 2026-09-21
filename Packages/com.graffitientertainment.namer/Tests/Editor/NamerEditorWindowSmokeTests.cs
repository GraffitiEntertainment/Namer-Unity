using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Headless EditMode smoke for the window facts that do not require window rendering:
    /// the <see cref="NamerProcessorSettings"/> foldout defaults (Source open, others collapsed)
    /// and the <see cref="NamerEditorWindow.ShaderInputToggleLabels"/> contents/order (the seven
    /// neutral-default shaded-view toggles, read via reflection). The render-only claims (window
    /// fits a small screen with all sections collapsed; every control reachable by scrolling) are
    /// manual-verification checklist items, not automated tests.
    /// </summary>
    public class NamerEditorWindowSmokeTests
    {
        private const string FoldoutSourceKey = "NamerProcessor.FoldoutSource";
        private const string FoldoutPreviewKey = "NamerProcessor.FoldoutPreview";
        private const string FoldoutRoughnessExtractionKey = "NamerProcessor.FoldoutRoughnessExtraction";
        private const string FoldoutAoKey = "NamerProcessor.FoldoutAo";
        private const string FoldoutDecompositionKey = "NamerProcessor.FoldoutDecomposition";
        private const string FoldoutOutputKey = "NamerProcessor.FoldoutOutput";

        [Test]
        public void FoldoutDefaults_SourceOpenOthersCollapsed()
        {
            PrefsSnapshot prefs = CaptureFoldoutPrefs();
            try
            {
                DeleteFoldoutKeys();
                var settings = new NamerProcessorSettings();

                Assert.IsTrue(settings.FoldoutSource, "Source must default open so the selection is visible");
                Assert.IsFalse(settings.FoldoutPreview, "Preview/Debug must default collapsed");
                Assert.IsFalse(settings.FoldoutRoughnessExtraction, "Roughness Extraction must default collapsed");
                Assert.IsFalse(settings.FoldoutAo, "AO must default collapsed");
                Assert.IsFalse(settings.FoldoutDecomposition, "Decomposition must default collapsed");
                Assert.IsFalse(settings.FoldoutOutput, "Output must default collapsed");
            }
            finally
            {
                RestoreFoldoutPrefs(prefs);
            }
        }

        [Test]
        public void ShaderInputToggleLabels_AreFiveNeutralToggles()
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                "ShaderInputToggleLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "ShaderInputToggleLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])field.GetValue(null);
            Assert.IsNotNull(labels, "ShaderInputToggleLabels must resolve to a string array");

            string[] expected = { "Base/Residual", "Roughness", "AO", "Metallic", "Emissive", "Vertex Color", "Normal" };
            CollectionAssert.AreEqual(expected, labels,
                "ShaderInputToggleLabels must be the seven neutral-default shaded-view input toggles");
        }

        [Test]
        public void ChannelPaneLabels_AreSixChannelPanes()
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                "ChannelPaneLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "ChannelPaneLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])field.GetValue(null);
            Assert.IsNotNull(labels, "ChannelPaneLabels must resolve to a string array");

            string[] expected = { "Base", "Roughness", "AO", "Metallic", "Emissive", "Normal" };
            CollectionAssert.AreEqual(expected, labels,
                "ChannelPaneLabels must be the six channel panes decoded from the After material's packed textures");
        }

        // -- EditorPrefs isolation for the foldout keys -----------------------

        private sealed class PrefsSnapshot
        {
            public bool Source;
            public bool Preview;
            public bool RoughnessExtraction;
            public bool Ao;
            public bool Decomposition;
            public bool Output;
            public bool HadSource;
            public bool HadPreview;
            public bool HadRoughnessExtraction;
            public bool HadAo;
            public bool HadDecomposition;
            public bool HadOutput;
        }

        private static PrefsSnapshot CaptureFoldoutPrefs()
        {
            return new PrefsSnapshot
            {
                Source = EditorPrefs.GetBool(FoldoutSourceKey, true),
                Preview = EditorPrefs.GetBool(FoldoutPreviewKey, false),
                RoughnessExtraction = EditorPrefs.GetBool(FoldoutRoughnessExtractionKey, false),
                Ao = EditorPrefs.GetBool(FoldoutAoKey, false),
                Decomposition = EditorPrefs.GetBool(FoldoutDecompositionKey, false),
                Output = EditorPrefs.GetBool(FoldoutOutputKey, false),
                HadSource = EditorPrefs.HasKey(FoldoutSourceKey),
                HadPreview = EditorPrefs.HasKey(FoldoutPreviewKey),
                HadRoughnessExtraction = EditorPrefs.HasKey(FoldoutRoughnessExtractionKey),
                HadAo = EditorPrefs.HasKey(FoldoutAoKey),
                HadDecomposition = EditorPrefs.HasKey(FoldoutDecompositionKey),
                HadOutput = EditorPrefs.HasKey(FoldoutOutputKey),
            };
        }

        private static void RestoreFoldoutPrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadSource) { EditorPrefs.SetBool(FoldoutSourceKey, snapshot.Source); }
            else { EditorPrefs.DeleteKey(FoldoutSourceKey); }

            if (snapshot.HadPreview) { EditorPrefs.SetBool(FoldoutPreviewKey, snapshot.Preview); }
            else { EditorPrefs.DeleteKey(FoldoutPreviewKey); }

            if (snapshot.HadRoughnessExtraction) { EditorPrefs.SetBool(FoldoutRoughnessExtractionKey, snapshot.RoughnessExtraction); }
            else { EditorPrefs.DeleteKey(FoldoutRoughnessExtractionKey); }

            if (snapshot.HadAo) { EditorPrefs.SetBool(FoldoutAoKey, snapshot.Ao); }
            else { EditorPrefs.DeleteKey(FoldoutAoKey); }

            if (snapshot.HadDecomposition) { EditorPrefs.SetBool(FoldoutDecompositionKey, snapshot.Decomposition); }
            else { EditorPrefs.DeleteKey(FoldoutDecompositionKey); }

            if (snapshot.HadOutput) { EditorPrefs.SetBool(FoldoutOutputKey, snapshot.Output); }
            else { EditorPrefs.DeleteKey(FoldoutOutputKey); }
        }

        private static void DeleteFoldoutKeys()
        {
            EditorPrefs.DeleteKey(FoldoutSourceKey);
            EditorPrefs.DeleteKey(FoldoutPreviewKey);
            EditorPrefs.DeleteKey(FoldoutRoughnessExtractionKey);
            EditorPrefs.DeleteKey(FoldoutAoKey);
            EditorPrefs.DeleteKey(FoldoutDecompositionKey);
            EditorPrefs.DeleteKey(FoldoutOutputKey);
        }
    }
}

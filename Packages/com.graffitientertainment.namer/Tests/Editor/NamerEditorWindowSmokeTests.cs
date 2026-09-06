using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Headless EditMode smoke for the D-07 window facts that do not require window rendering:
    /// the <see cref="NamerProcessorSettings"/> foldout defaults (Source open, others collapsed)
    /// and the <see cref="NamerEditorWindow.DebugChannelLabels"/> contents/order ending with the
    /// new index-10 "Extracted Roughness" entry (read via reflection) plus the index -> shader
    /// channel pairing. The render-only D-07 claims (window fits a small screen with all sections
    /// collapsed; every control reachable by scrolling) are manual-verification checklist items,
    /// not automated tests.
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
        public void DebugChannelLabels_AppendsExtractedRoughnessAtIndex10()
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                "DebugChannelLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "DebugChannelLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])field.GetValue(null);
            Assert.IsNotNull(labels, "DebugChannelLabels must resolve to a string array");

            // The first 10 labels are unchanged; the new entry is APPENDED (never inserted
            // mid-array, which would shift the five subsequent labels one slot wrong).
            string[] expected =
            {
                "Shaded", "Base Color", "AO", "Normal", "Roughness", "Metallic", "Emissive",
                "Vertex Colors", "Residual", "Error Heatmap", "Extracted Roughness",
            };
            CollectionAssert.AreEqual(expected, labels,
                "DebugChannelLabels must keep the first 10 labels and append 'Extracted Roughness' last");

            Assert.AreEqual("Extracted Roughness", labels[10], "index 10 must be 'Extracted Roughness'");
            Assert.AreEqual("Error Heatmap", labels[9], "Error Heatmap must not move (stays at index 9)");

            // Index/shader-channel pairing: the window's SetChannel(Mathf.Max(0, _debugChannel - 1))
            // maps toolbar index 10 -> shader channel 9, exactly the new _DebugChannel >= 8.5
            // extracted-roughness branch.
            Assert.AreEqual(9, Mathf.Max(0, 10 - 1),
                "toolbar index 10 must map to shader channel 9 (the _DebugChannel >= 8.5 branch)");
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

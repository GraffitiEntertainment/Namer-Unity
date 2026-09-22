using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 04.2 plan 04 controls tests (mirrors <see cref="NamerAOControlsTests"/>). Proves
    /// the three new/relabeled projection-era controls persist through EditorPrefs with the
    /// shipped defaults (DipSource int 0 = RemovedDetail, WriteResidual bool false,
    /// RoughnessExtractStrength float 0.25), and that the decomposition statistics rows carry
    /// the honest removed-detail relabel ("Removed-detail max error" /
    /// "not written (one-texture)") via reflection on <see cref="NamerEditorWindow"/>. No test
    /// asserts a computed overlap-depth statistic — the honest-copy decision (measured numbers
    /// in tooltips, not a computed counter) is deliberate (04.2 CONTEXT: "at most an honest
    /// statement, not repair").
    /// 04.3 adds the CoverageTarget knob (D-05, slider 0.90-1.00, default 0.99, persisted
    /// under "NamerProcessor.CoverageTarget"): persistence and the fresh-prefs default are
    /// test-enforced below.
    /// </summary>
    public class NamerUIControlsTests
    {
        private const string DipSourceKey = "NamerProcessor.DipSource";
        private const string WriteResidualKey = "NamerProcessor.WriteResidual";
        private const string RoughnessExtractStrengthKey = "NamerProcessor.RoughnessExtractStrength";
        private const string CoverageTargetKey = "NamerProcessor.CoverageTarget";

        /// <summary>
        /// Settings round-trip: writing the three controls through one
        /// <see cref="NamerProcessorSettings"/> instance is visible to a NEW instance (the
        /// values are EditorPrefs-backed, not instance-local). Pre-test values are snapshotted
        /// and restored in <c>finally</c>.
        /// </summary>
        [Test]
        public void ProjectionControls_PersistViaEditorPrefs()
        {
            int dipSource = EditorPrefs.GetInt(DipSourceKey, 0);
            bool writeResidual = EditorPrefs.GetBool(WriteResidualKey, false);
            float strength = EditorPrefs.GetFloat(RoughnessExtractStrengthKey, 0.25f);
            bool hadDipSource = EditorPrefs.HasKey(DipSourceKey);
            bool hadWriteResidual = EditorPrefs.HasKey(WriteResidualKey);
            bool hadStrength = EditorPrefs.HasKey(RoughnessExtractStrengthKey);

            try
            {
                var first = new NamerProcessorSettings
                {
                    DipSource = (int)NamerDipSource.SobelEdge,
                    WriteResidual = true,
                    RoughnessExtractStrength = 0.6f,
                };

                NamerProcessorSettings second = new NamerProcessorSettings();
                Assert.AreEqual((int)NamerDipSource.SobelEdge, second.DipSource,
                    "DipSource must persist via EditorPrefs (SobelEdge = 1)");
                Assert.IsTrue(second.WriteResidual,
                    "WriteResidual must persist via EditorPrefs");
                Assert.AreEqual(0.6f, second.RoughnessExtractStrength,
                    "RoughnessExtractStrength must persist via EditorPrefs");
            }
            finally
            {
                if (hadDipSource) { EditorPrefs.SetInt(DipSourceKey, dipSource); }
                else { EditorPrefs.DeleteKey(DipSourceKey); }

                if (hadWriteResidual) { EditorPrefs.SetBool(WriteResidualKey, writeResidual); }
                else { EditorPrefs.DeleteKey(WriteResidualKey); }

                if (hadStrength) { EditorPrefs.SetFloat(RoughnessExtractStrengthKey, strength); }
                else { EditorPrefs.DeleteKey(RoughnessExtractStrengthKey); }
            }
        }

        /// <summary>
        /// Fresh-prefs defaults: with no keys present the shipped 04.2 defaults read back as
        /// RemovedDetail (0) / Write Residual off (false) / dip depth 0.25.
        /// </summary>
        [Test]
        public void ProjectionControls_FreshPrefsDefaults()
        {
            int dipSource = EditorPrefs.GetInt(DipSourceKey, 0);
            bool writeResidual = EditorPrefs.GetBool(WriteResidualKey, false);
            float strength = EditorPrefs.GetFloat(RoughnessExtractStrengthKey, 0.25f);
            bool hadDipSource = EditorPrefs.HasKey(DipSourceKey);
            bool hadWriteResidual = EditorPrefs.HasKey(WriteResidualKey);
            bool hadStrength = EditorPrefs.HasKey(RoughnessExtractStrengthKey);

            try
            {
                EditorPrefs.DeleteKey(DipSourceKey);
                EditorPrefs.DeleteKey(WriteResidualKey);
                EditorPrefs.DeleteKey(RoughnessExtractStrengthKey);

                NamerProcessorSettings settings = new NamerProcessorSettings();
                Assert.AreEqual(0, settings.DipSource,
                    "fresh DipSource must default to 0 (RemovedDetail)");
                Assert.AreEqual((int)NamerDipSource.RemovedDetail, settings.DipSource,
                    "fresh DipSource 0 must be NamerDipSource.RemovedDetail");
                Assert.IsFalse(settings.WriteResidual,
                    "fresh WriteResidual must default to false (one-texture outcome)");
                Assert.AreEqual(0.25f, settings.RoughnessExtractStrength,
                    "fresh RoughnessExtractStrength must default to the shipped 0.25 dip depth");
            }
            finally
            {
                if (hadDipSource) { EditorPrefs.SetInt(DipSourceKey, dipSource); }
                else { EditorPrefs.DeleteKey(DipSourceKey); }

                if (hadWriteResidual) { EditorPrefs.SetBool(WriteResidualKey, writeResidual); }
                else { EditorPrefs.DeleteKey(WriteResidualKey); }

                if (hadStrength) { EditorPrefs.SetFloat(RoughnessExtractStrengthKey, strength); }
                else { EditorPrefs.DeleteKey(RoughnessExtractStrengthKey); }
            }
        }

        /// <summary>
        /// Coverage-target round-trip (D-05): writing CoverageTarget through one
        /// <see cref="NamerProcessorSettings"/> instance is visible to a NEW instance.
        /// Pre-test value is snapshotted and restored in <c>finally</c>.
        /// </summary>
        [Test]
        public void CoverageTarget_PersistsViaEditorPrefs()
        {
            float coverageTarget = EditorPrefs.GetFloat(CoverageTargetKey, 0.99f);
            bool hadCoverageTarget = EditorPrefs.HasKey(CoverageTargetKey);

            try
            {
                var first = new NamerProcessorSettings
                {
                    CoverageTarget = 0.95f,
                };

                NamerProcessorSettings second = new NamerProcessorSettings();
                Assert.AreEqual(0.95f, second.CoverageTarget,
                    "CoverageTarget must persist via EditorPrefs (D-05)");
            }
            finally
            {
                if (hadCoverageTarget) { EditorPrefs.SetFloat(CoverageTargetKey, coverageTarget); }
                else { EditorPrefs.DeleteKey(CoverageTargetKey); }
            }
        }

        /// <summary>
        /// Fresh-prefs coverage-target default (D-05): with no key present the shipped
        /// default reads back as the constant 0.99.
        /// </summary>
        [Test]
        public void CoverageTarget_FreshPrefsDefault()
        {
            float coverageTarget = EditorPrefs.GetFloat(CoverageTargetKey, 0.99f);
            bool hadCoverageTarget = EditorPrefs.HasKey(CoverageTargetKey);

            try
            {
                EditorPrefs.DeleteKey(CoverageTargetKey);

                NamerProcessorSettings settings = new NamerProcessorSettings();
                Assert.AreEqual(NamerEditorConstants.DefaultCoverageTarget, settings.CoverageTarget,
                    "fresh CoverageTarget must default to NamerEditorConstants.DefaultCoverageTarget");
                Assert.AreEqual(0.99f, settings.CoverageTarget,
                    "fresh CoverageTarget must default to the shipped 0.99 percentile gate target");
            }
            finally
            {
                if (hadCoverageTarget) { EditorPrefs.SetFloat(CoverageTargetKey, coverageTarget); }
                else { EditorPrefs.DeleteKey(CoverageTargetKey); }
            }
        }

        /// <summary>
        /// Label presence: the decomposition statistics rows carry the honest removed-detail
        /// relabel. Read via reflection on the static fields <see cref="NamerEditorWindow"/>'s
        /// <c>DrawDecompStats</c> uses (the established <c>DebugChannelLabels</c> pattern).
        /// </summary>
        [Test]
        public void DecompStatLabels_CarryRemovedDetailSemantics()
        {
            FieldInfo labelsField = typeof(NamerEditorWindow).GetField(
                "DecompStatLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(labelsField, "DecompStatLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])labelsField.GetValue(null);
            Assert.IsNotNull(labels, "DecompStatLabels must resolve to a string array");

            AssertContains(labels, "Removed-detail max error");
            AssertContains(labels, "Coverage");
            AssertContains(labels, "Avg Error");
            AssertContains(labels, "Max Error");
            AssertContains(labels, "Residual");

            FieldInfo notWrittenField = typeof(NamerEditorWindow).GetField(
                "ResidualNotWrittenLabel", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(notWrittenField, "ResidualNotWrittenLabel field must exist on NamerEditorWindow");
            Assert.AreEqual("not written (one-texture)", (string)notWrittenField.GetValue(null),
                "the residual row must use the one-texture 'not written (one-texture)' value");
        }

        private static void AssertContains(string[] names, string expected)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == expected)
                {
                    return;
                }
            }

            Assert.Fail("Expected stat label '" + expected + "' not found in NamerEditorWindow.DecompStatLabels");
        }
    }
}

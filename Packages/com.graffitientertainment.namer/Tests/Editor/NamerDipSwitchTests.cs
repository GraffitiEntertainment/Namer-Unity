using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// DIP-01/DIP-02 debug dip-switch tests. Pins the four persisted step-gate bools
    /// (EditorPrefs round-trip + fresh defaults) and the shader's neutral-default debug
    /// gates (five _DbgEnable* floats default 1.0, _DbgRoughnessNeutral defaults 0.5).
    /// Mirrors <see cref="NamerUIControlsTests"/> prefs snapshot/restore and
    /// <see cref="NamerOneTextureTests"/> shader-default patterns.
    /// </summary>
    public class NamerDipSwitchTests
    {
        private const string RoughnessStageEnabledKey = "NamerProcessor.RoughnessStageEnabled";
        private const string AoStageEnabledKey = "NamerProcessor.AoStageEnabled";
        private const string MetallicContributionEnabledKey = "NamerProcessor.MetallicContributionEnabled";
        private const string EmissiveContributionEnabledKey = "NamerProcessor.EmissiveContributionEnabled";

        private sealed class StepGateSnapshot
        {
            public bool Roughness;
            public bool Ao;
            public bool Metallic;
            public bool Emissive;
            public bool HadRoughness;
            public bool HadAo;
            public bool HadMetallic;
            public bool HadEmissive;
        }

        private static StepGateSnapshot CaptureStepGates()
        {
            return new StepGateSnapshot
            {
                Roughness = EditorPrefs.GetBool(RoughnessStageEnabledKey, true),
                Ao = EditorPrefs.GetBool(AoStageEnabledKey, true),
                Metallic = EditorPrefs.GetBool(MetallicContributionEnabledKey, true),
                Emissive = EditorPrefs.GetBool(EmissiveContributionEnabledKey, true),
                HadRoughness = EditorPrefs.HasKey(RoughnessStageEnabledKey),
                HadAo = EditorPrefs.HasKey(AoStageEnabledKey),
                HadMetallic = EditorPrefs.HasKey(MetallicContributionEnabledKey),
                HadEmissive = EditorPrefs.HasKey(EmissiveContributionEnabledKey),
            };
        }

        private static void RestoreStepGates(StepGateSnapshot snapshot)
        {
            if (snapshot.HadRoughness) { EditorPrefs.SetBool(RoughnessStageEnabledKey, snapshot.Roughness); }
            else { EditorPrefs.DeleteKey(RoughnessStageEnabledKey); }

            if (snapshot.HadAo) { EditorPrefs.SetBool(AoStageEnabledKey, snapshot.Ao); }
            else { EditorPrefs.DeleteKey(AoStageEnabledKey); }

            if (snapshot.HadMetallic) { EditorPrefs.SetBool(MetallicContributionEnabledKey, snapshot.Metallic); }
            else { EditorPrefs.DeleteKey(MetallicContributionEnabledKey); }

            if (snapshot.HadEmissive) { EditorPrefs.SetBool(EmissiveContributionEnabledKey, snapshot.Emissive); }
            else { EditorPrefs.DeleteKey(EmissiveContributionEnabledKey); }
        }

        private static void DeleteStepGateKeys()
        {
            EditorPrefs.DeleteKey(RoughnessStageEnabledKey);
            EditorPrefs.DeleteKey(AoStageEnabledKey);
            EditorPrefs.DeleteKey(MetallicContributionEnabledKey);
            EditorPrefs.DeleteKey(EmissiveContributionEnabledKey);
        }

        /// <summary>
        /// Settings round-trip: writing the four step gates through one
        /// <see cref="NamerProcessorSettings"/> instance is visible to a NEW instance (the
        /// values are EditorPrefs-backed, not instance-local). Pre-test values are
        /// snapshotted and restored in <c>finally</c>.
        /// </summary>
        [Test]
        public void StepGates_PersistViaEditorPrefs()
        {
            StepGateSnapshot snapshot = CaptureStepGates();
            try
            {
                var first = new NamerProcessorSettings
                {
                    RoughnessStageEnabled = false,
                    AoStageEnabled = false,
                    MetallicContributionEnabled = false,
                    EmissiveContributionEnabled = false,
                };

                NamerProcessorSettings second = new NamerProcessorSettings();
                Assert.IsFalse(second.RoughnessStageEnabled, "RoughnessStageEnabled must persist via EditorPrefs");
                Assert.IsFalse(second.AoStageEnabled, "AoStageEnabled must persist via EditorPrefs");
                Assert.IsFalse(second.MetallicContributionEnabled, "MetallicContributionEnabled must persist via EditorPrefs");
                Assert.IsFalse(second.EmissiveContributionEnabled, "EmissiveContributionEnabled must persist via EditorPrefs");
            }
            finally
            {
                RestoreStepGates(snapshot);
            }
        }

        /// <summary>
        /// Fresh-prefs defaults: with no keys present the four step gates read back true
        /// (opt-out — the default pipeline is unchanged).
        /// </summary>
        [Test]
        public void StepGates_FreshPrefsDefaults()
        {
            StepGateSnapshot snapshot = CaptureStepGates();
            try
            {
                DeleteStepGateKeys();

                NamerProcessorSettings settings = new NamerProcessorSettings();
                Assert.IsTrue(settings.RoughnessStageEnabled, "fresh RoughnessStageEnabled must default to true");
                Assert.IsTrue(settings.AoStageEnabled, "fresh AoStageEnabled must default to true");
                Assert.IsTrue(settings.MetallicContributionEnabled, "fresh MetallicContributionEnabled must default to true");
                Assert.IsTrue(settings.EmissiveContributionEnabled, "fresh EmissiveContributionEnabled must default to true");
            }
            finally
            {
                RestoreStepGates(snapshot);
            }
        }

        /// <summary>
        /// Shader defaults: the five _DbgEnable* gates default to neutral 1.0 and
        /// _DbgRoughnessNeutral defaults to 0.5 on a freshly created material, so an
        /// untouched material decodes byte-identically to the pre-dip-switch shader.
        /// </summary>
        [Test]
        public void ShaderDebugGates_DefaultNeutral()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            string[] gates =
            {
                "_DbgEnableResidual",
                "_DbgEnableRoughness",
                "_DbgEnableAO",
                "_DbgEnableMetallic",
                "_DbgEnableEmissive",
            };
            foreach (string gate in gates)
            {
                Assert.IsTrue(shader.FindPropertyIndex(gate) >= 0, "NAMER shader must declare " + gate);
            }

            Assert.IsTrue(shader.FindPropertyIndex("_DbgRoughnessNeutral") >= 0,
                "NAMER shader must declare _DbgRoughnessNeutral");

            Material material = new Material(shader);
            try
            {
                foreach (string gate in gates)
                {
                    Assert.AreEqual(1.0f, material.GetFloat(gate), "material " + gate + " must default to neutral 1.0");
                }

                Assert.AreEqual(0.5f, material.GetFloat("_DbgRoughnessNeutral"),
                    "material _DbgRoughnessNeutral must default to neutral 0.5");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }
    }
}

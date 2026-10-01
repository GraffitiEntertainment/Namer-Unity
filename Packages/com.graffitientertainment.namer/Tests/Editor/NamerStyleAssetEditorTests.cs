using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.3 plan 06 headless prereq-seam pins for the merged Style Asset editor:
    /// DecomposeButtonState's single min-3 gate (exactly one reason — no exemplar
    /// prereq; weight-0 slots never count), ValidateButtonState's LOCKED
    /// two-reason order (not-decomposed beats no-exemplar; decomposed + injected
    /// fake Processor window → enabled), the S15/S16 STYL-15 migrated-gating seam
    /// (HasFit without IsDecomposed keeps the carried post block and Regenerate
    /// Volume enabled — no silent loss), and the S17 validation result-string
    /// formatting law (measure interpolation, mismatch-prefix discriminator,
    /// LUT-unlock copy only on the mismatch line).
    /// </summary>
    public class NamerStyleAssetEditorTests
    {
        /// <summary>
        /// The Decompose prereq is EXACTLY the min-3 positive-weight gate: the
        /// locked reason is the only one (no exemplar prereq exists), three
        /// positive-weight assigned references enable the button, and weight-0
        /// slots do not count (the honest-count law).
        /// </summary>
        [Test]
        public void DecomposeButtonState_MinGate_ExactReasonEnabledAtThree()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                NamerStyleAssetEditor.StyleAssetButtonState empty =
                    NamerStyleAssetEditor.DecomposeButtonState(asset);
                Assert.IsTrue(empty.Disabled, "a fresh asset has no references — Decompose must be disabled");
                Assert.AreEqual(
                    string.Format(
                        NamerEditorConstants.StyleAssetDecomposePrereqFormat,
                        NamerEditorConstants.MinReferences),
                    empty.ReasonMiniLabel,
                    "the ONLY Decompose prereq reason is the locked min-3 copy — no exemplar prereq exists");

                AddWeightedReference(asset, 1f);
                AddWeightedReference(asset, 1f);
                Assert.IsTrue(
                    NamerStyleAssetEditor.DecomposeButtonState(asset).Disabled,
                    "two positive-weight references are still below the min of three");

                AddWeightedReference(asset, 1f);
                NamerStyleAssetEditor.StyleAssetButtonState ready =
                    NamerStyleAssetEditor.DecomposeButtonState(asset);
                Assert.IsFalse(ready.Disabled,
                    "three positive-weight assigned references must enable Decompose Style");
                Assert.IsNull(ready.ReasonMiniLabel, "an enabled state carries no reason line");

                asset.ReferenceWeights[2] = 0f;
                Assert.IsTrue(
                    NamerStyleAssetEditor.DecomposeButtonState(asset).Disabled,
                    "weight-0 slots are not 'there' — the gate counts positive-weight references only");
            }
            finally
            {
                DestroyReferences(asset);
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>
        /// The LOCKED reason order: not-decomposed (reason 1) beats no-exemplar
        /// (reason 2) even when both hold; a decomposed asset with no loadable
        /// window gets reason 2; a decomposed asset plus a Processor window with
        /// an injected loadable source is enabled.
        /// </summary>
        [Test]
        public void ValidateButtonState_LockedReasonOrder_NotDecomposedBeatsNoExemplar()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // Neither decomposed nor a window: reason 1 wins (LOCKED order).
                NamerStyleAssetEditor.StyleAssetButtonState neither =
                    NamerStyleAssetEditor.ValidateButtonState(asset, null);
                Assert.IsTrue(neither.Disabled);
                Assert.AreEqual(
                    NamerEditorConstants.StyleAssetValidateNeedsDecompositionReason,
                    neither.ReasonMiniLabel,
                    "not-decomposed is reason 1 and must beat the no-exemplar reason when both hold");

                // Decomposed but no window: reason 2.
                asset.DecompositionFingerprint = "synthetic-decomposition-fingerprint";
                NamerStyleAssetEditor.StyleAssetButtonState noWindow =
                    NamerStyleAssetEditor.ValidateButtonState(asset, null);
                Assert.IsTrue(noWindow.Disabled);
                Assert.AreEqual(
                    NamerEditorConstants.StyleAssetValidateNoExemplarReason,
                    noWindow.ReasonMiniLabel);

                // Decomposed + injected fake window: enabled — no GPU work, the
                // HasLoadableSource seam is armed through the private fields
                // (the retired lifecycle-test donor idiom).
                NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
                try
                {
                    ScriptableObject sourceToken = InjectWindowSource(window);
                    try
                    {
                        NamerStyleAssetEditor.StyleAssetButtonState ready =
                            NamerStyleAssetEditor.ValidateButtonState(asset, window);
                        Assert.IsFalse(ready.Disabled,
                            "decomposed + loadable exemplar window must enable Validate Look");
                        Assert.IsNull(ready.ReasonMiniLabel);
                    }
                    finally
                    {
                        Object.DestroyImmediate(sourceToken);
                    }
                }
                finally
                {
                    Object.DestroyImmediate(window);
                }
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>
        /// STYL-15 no-silent-loss seam: a migrated legacy asset carries a fit
        /// without a decomposition, and BOTH the S15 post-block gate
        /// (!(HasFit || IsDecomposed)) and the S16 Regenerate Volume gate
        /// (!HasFit) evaluate enabled for it — the carried blocks are never
        /// silently locked behind Decompose Style.
        /// </summary>
        [Test]
        public void MigratedCarry_HasFitWithoutDecomposition_KeepsPostAndVolumeEnabled()
        {
            NamerStyleAsset migrated = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                migrated.PostFitFingerprint = "migrated-legacy-fit-fingerprint";
                Assert.IsTrue(migrated.HasFit,
                    "a non-empty PostFitFingerprint is the carried-fit marker");
                Assert.IsFalse(migrated.IsDecomposed,
                    "a migrated legacy asset carries a fit without a decomposition fingerprint");

                // S15 gate: !(HasFit || IsDecomposed) — the exact expression the
                // editor's DisabledScope uses.
                Assert.IsFalse(!(migrated.HasFit || migrated.IsDecomposed),
                    "the carried post block must stay tweakable (S15, STYL-15)");

                // S16 gate: !HasFit on Regenerate Volume.
                Assert.IsFalse(!migrated.HasFit,
                    "Regenerate Volume must stay available for a carried fit (S16)");
            }
            finally
            {
                Object.DestroyImmediate(migrated);
            }
        }

        /// <summary>
        /// The S17 result-string contract: both lines interpolate the measure,
        /// the mismatch line starts with the severity discriminator the
        /// rendering keys on and carries the verbatim STYL-17 LUT-unlock copy,
        /// and the within-tolerance line carries none of it.
        /// </summary>
        [Test]
        public void ValidationResultStrings_MeasureInterpolationAndSeverityDiscriminator()
        {
            const string measure = "max knob deviation 12% (Post Exposure)";
            string within = string.Format(
                NamerEditorConstants.StyleAssetValidationWithinToleranceFormat, measure);
            StringAssert.Contains("within tolerance (" + measure + ")", within,
                "the within-tolerance line interpolates the measure in place");
            StringAssert.DoesNotContain("3D-LUT", within,
                "the within-tolerance line carries no LUT-unlock copy");

            string mismatch = string.Format(
                NamerEditorConstants.StyleAssetValidationMismatchWarningFormat, measure);
            StringAssert.StartsWith(
                NamerEditorConstants.StyleAssetValidationMismatchPrefix, mismatch,
                "the mismatch result must start with the discriminator the severity rendering keys on");
            StringAssert.Contains("3D-LUT fallback", mismatch,
                "the STYL-17 LUT-unlock evidence must be verbatim on the mismatch line");
            StringAssert.Contains(measure, mismatch,
                "the mismatch line interpolates the measure in place");

            Assert.IsFalse(
                within.StartsWith(NamerEditorConstants.StyleAssetValidationMismatchPrefix),
                "the within-tolerance line must NOT render as the mismatch Warning HelpBox");
        }

        /// <summary>Destroys the synthetic reference textures so the test leaks nothing.</summary>
        private static void DestroyReferences(NamerStyleAsset asset)
        {
            for (int i = 0; i < asset.References.Count; i++)
            {
                if (asset.References[i] != null)
                {
                    Object.DestroyImmediate(asset.References[i]);
                }
            }
        }

        /// <summary>
        /// Appends a non-null reference slot with the given weight (length-synced
        /// through the editor's default-weight fallback contract).
        /// </summary>
        private static void AddWeightedReference(NamerStyleAsset asset, float weight)
        {
            Texture2D reference = new Texture2D(2, 2);
            reference.name = "EditorTestReference" + asset.References.Count;
            asset.References.Add(reference);
            asset.ReferenceWeights.Add(weight);
        }

        /// <summary>
        /// Arms <see cref="NamerEditorWindow.HasLoadableSource"/> without GPU
        /// work by injecting a non-null selection and a model with one material
        /// inspection through the private fields (the retired
        /// NamerStylePostFitLifecycleTests donor idiom). Returns the source
        /// token for disposal.
        /// </summary>
        private static ScriptableObject InjectWindowSource(NamerEditorWindow window)
        {
            ScriptableObject token = ScriptableObject.CreateInstance<ScriptableObject>();
            token.name = "EditorTestSourceToken";
            NamerSourceModel model = new NamerSourceModel();
            model.Materials.Add(new NamerMaterialInspection());
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            FieldInfo selection = typeof(NamerEditorWindow).GetField("_selection", flags);
            FieldInfo modelField = typeof(NamerEditorWindow).GetField("_model", flags);
            Assert.IsNotNull(selection, "the window's _selection field must exist (donor idiom)");
            Assert.IsNotNull(modelField, "the window's _model field must exist (donor idiom)");
            selection.SetValue(window, token);
            modelField.SetValue(window, model);
            return token;
        }
    }
}

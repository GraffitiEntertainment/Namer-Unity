using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 plan 03 lifecycle tests (STYL-08 tail) / 05.2 plan 04 (R3) / 05.2 plan 05:
    /// the Fit Look prerequisite seam lives on the ASSET editor now —
    /// <see cref="NamerPostLookEditor.FitButtonState"/> with its LOCKED three-reason
    /// chain (positive-weight reference → exemplar window with a loadable source →
    /// stylized output) — and the load-time migration
    /// (<see cref="NamerPostLookMigration.MigrateProfile"/>) pins the D-07 contract:
    /// verbatim copy, never overwriting an existing target, source fit cleared so
    /// the scan filter never re-selects. Also here: the
    /// <see cref="NamerStylePostFit.ApplyToProfile"/> write (every fitted field lands
    /// on the profile, the fit is fresh, and re-fitting replaces wholesale with no
    /// accumulation) and the W8 attach-state honesty clauses of
    /// <see cref="NamerEditorWindow.DescribeProfileState"/> (W12 is plan 06 — the fit
    /// clause stays until then). Pure EditMode logic — no
    /// GPU, no Process run (the Volume regen gate itself is covered by the plan-02
    /// writer tests; the Process hook is compile-proven here and regression-covered by
    /// the full suite). Asset-writing tests use a temp folder deleted in
    /// <c>finally</c> so fingerprints are real GUID-based identity strings; the
    /// EditorPrefs-backed Style Profile pref is snapshotted and restored in
    /// <c>finally</c> so stylized-output pinning never leaks the selection.
    /// </summary>
    public class NamerStylePostFitLifecycleTests
    {
        private const string TempFolder = "Assets/NAMER_PostFit_Lifecycle_Tests_Temp";

        private const string ReasonNoPositiveReference =
            "Fit Look needs at least one reference with weight above 0 — all slots empty or every weight 0 leaves nothing to fit.";

        private const string ReasonNoExemplar =
            "Fit Look uses the object loaded in the Processor window (Tools > NAMER/Processor) — load a Source there first.";

        private const string ReasonNoStylizedOutput =
            "Fit Look fits the Processor window's stylized render — attach a Style Profile in the Processor window first.";

        [Test]
        public void ApplyToProfile_WritesAllFields()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.name = "LifecycleFitProfile";
            EnsureTempFolder();
            Texture2D reference = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "LifecycleFitReference",
            };
            AssetDatabase.CreateAsset(reference, TempFolder + "/LifecycleFitReference.asset");
            profile.References.Add(reference);
            try
            {
                NamerStylePostFitParams fit = new NamerStylePostFitParams
                {
                    Temperature = 12.5f,
                    Tint = -3.25f,
                    PostExposureEv = 0.75f,
                    Contrast = 8f,
                    Saturation = -4.5f,
                    BloomThreshold = 0.82f,
                    MasterKeys = new[] { new Keyframe(0f, 0f), new Keyframe(0.5f, 0.55f), new Keyframe(1f, 1f) },
                    RedKeys = new[] { new Keyframe(0f, 0.05f), new Keyframe(1f, 0.95f) },
                    GreenKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    BlueKeys = new[] { new Keyframe(0f, 0.1f), new Keyframe(1f, 0.9f) },
                    Fingerprint = profile.ComputeFingerprint(),
                };

                NamerStylePostFit.ApplyToProfile(profile, fit, "ExemplarA");

                Assert.AreEqual(12.5f, profile.PostTemperature);
                Assert.AreEqual(-3.25f, profile.PostTint);
                Assert.AreEqual(0.75f, profile.PostExposureEv);
                Assert.AreEqual(8f, profile.PostContrast);
                Assert.AreEqual(-4.5f, profile.PostSaturation);
                Assert.AreEqual(0.82f, profile.PostBloomThreshold);

                Assert.AreEqual(3, profile.PostMasterCurve.length, "master curve keys land verbatim");
                Assert.AreEqual(2, profile.PostRedCurve.length);
                Assert.AreEqual(2, profile.PostGreenCurve.length);
                Assert.AreEqual(2, profile.PostBlueCurve.length);
                Assert.AreEqual(0.55f, profile.PostMasterCurve.keys[1].value, "curve VALUES copy, not just lengths");

                Assert.AreEqual(fit.Fingerprint, profile.PostFitFingerprint, "the fit fingerprint is the refs identity");
                Assert.AreEqual("ExemplarA", profile.PostFitExemplarName);
                Assert.IsTrue(profile.HasPostFit, "a fit fingerprint makes the profile fitted");
                Assert.IsFalse(profile.IsFitStale, "a fit against the current refs fingerprint is fresh");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ApplyToProfile_IsIdempotentOnRefit()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                NamerStylePostFitParams firstFit = new NamerStylePostFitParams
                {
                    Temperature = 12.5f,
                    Tint = -3.25f,
                    PostExposureEv = 0.75f,
                    Contrast = 8f,
                    Saturation = -4.5f,
                    BloomThreshold = 0.82f,
                    MasterKeys = new[] { new Keyframe(0f, 0f), new Keyframe(0.5f, 0.55f), new Keyframe(1f, 1f) },
                    RedKeys = new[] { new Keyframe(0f, 0.05f), new Keyframe(1f, 0.95f) },
                    GreenKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    BlueKeys = new[] { new Keyframe(0f, 0.1f), new Keyframe(1f, 0.9f) },
                    Fingerprint = "first-fit-fingerprint",
                };
                NamerStylePostFit.ApplyToProfile(profile, firstFit, "FirstExemplar");

                NamerStylePostFitParams secondFit = new NamerStylePostFitParams
                {
                    Temperature = -7.25f,
                    Tint = 2.5f,
                    PostExposureEv = -0.4f,
                    Contrast = -5f,
                    Saturation = 6.25f,
                    BloomThreshold = 0.95f,
                    MasterKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    RedKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    GreenKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    BlueKeys = new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) },
                    Fingerprint = "second-fit-fingerprint",
                };
                NamerStylePostFit.ApplyToProfile(profile, secondFit, "SecondExemplar");

                Assert.AreEqual("SecondExemplar", profile.PostFitExemplarName, "the last fit's exemplar wins");
                Assert.AreEqual(-7.25f, profile.PostTemperature, "the last fit's temperature wins");
                Assert.AreEqual(2.5f, profile.PostTint);
                Assert.AreEqual(-0.4f, profile.PostExposureEv);
                Assert.AreEqual(-5f, profile.PostContrast);
                Assert.AreEqual(6.25f, profile.PostSaturation);
                Assert.AreEqual(0.95f, profile.PostBloomThreshold);
                Assert.AreEqual(2, profile.PostMasterCurve.length,
                    "re-fit REPLACES the curve wholesale — no key accumulation from the first fit");
                Assert.AreEqual("second-fit-fingerprint", profile.PostFitFingerprint);
                Assert.IsTrue(profile.HasPostFit);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void DescribeProfileState_ReportsFitTruth()
        {
            NamerStyleProfile unextracted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            unextracted.name = "ProfileU";
            NamerStyleProfile unfitted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            unfitted.name = "ProfileN";
            unfitted.ReferenceFingerprint = "hand-set-extract-fingerprint";
            unfitted.Stats.ReferenceCount = 2;
            NamerStyleProfile fitted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            fitted.name = "ProfileF";
            fitted.ReferenceFingerprint = "hand-set-extract-fingerprint";
            fitted.Stats.ReferenceCount = 2;
            fitted.PostFitFingerprint = "hand-set-fit-fingerprint";
            fitted.PostFitExemplarName = "ExemplarA";
            try
            {
                Assert.AreEqual("ProfileU — not extracted",
                    NamerEditorWindow.DescribeProfileState(unextracted));
                Assert.AreEqual("ProfileN — palette extracted (0 anchors, 2 references), post look not fitted",
                    NamerEditorWindow.DescribeProfileState(unfitted));
                Assert.AreEqual("ProfileF — palette extracted (0 anchors, 2 references), post look fitted from ExemplarA",
                    NamerEditorWindow.DescribeProfileState(fitted));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unextracted);
                UnityEngine.Object.DestroyImmediate(unfitted);
                UnityEngine.Object.DestroyImmediate(fitted);
            }
        }

        /// <summary>
        /// 05.2-05 prereq 1: an assigned reference whose only weight is 0 leaves
        /// nothing to fit — the button disables with the WR-01 mirror reason even
        /// before the window/prereq-2 question is asked (LOCKED reason order). The
        /// seam is pure — no window, no EditorPrefs reads for this reason.
        /// </summary>
        [Test]
        public void FitButtonState_NoPositiveReferenceWeight_DisabledWithReason1()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            look.ReferenceWeights.Add(0f);
            try
            {
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, null, new NamerProcessorSettings());
                Assert.IsTrue(state.Disabled, "zero-mass references disable the button");
                Assert.AreEqual(ReasonNoPositiveReference, state.ReasonMiniLabel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 prereq 2: references with positive weight but no exemplar window
        /// (null window — the seam's injectable-optional shape) disables with the
        /// Processor-window reason. The reason's menu-path slash form is copy-locked.
        /// </summary>
        [Test]
        public void FitButtonState_NoExemplarWindow_DisabledWithReason2()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            try
            {
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, null, new NamerProcessorSettings());
                Assert.IsTrue(state.Disabled, "no exemplar window disables the button");
                Assert.AreEqual(ReasonNoExemplar, state.ReasonMiniLabel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 prereq 3 (D-04 LOCKED): a window WITH a loadable source (reflection
        /// seam — _selection + _model, the smoke-test precedent) but no resolvable
        /// Style Profile in settings disables with the stylized-output reason. The
        /// EditorPrefs-backed pref is snapshotted and restored in finally.
        /// </summary>
        [Test]
        public void FitButtonState_NoStylizedOutput_DisabledWithReason3()
        {
            string savedGuid = new NamerProcessorSettings().StyleProfileGuid;
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            ScriptableObject token = ScriptableObject.CreateInstance<ScriptableObject>();
            token.name = "LifecycleSourceToken";
            try
            {
                InjectWindowSource(window, token);
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleProfileGuid = string.Empty;
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, window, settings);
                Assert.IsTrue(state.Disabled, "no stylized output disables the button");
                Assert.AreEqual(ReasonNoStylizedOutput, state.ReasonMiniLabel);
            }
            finally
            {
                new NamerProcessorSettings().StyleProfileGuid = savedGuid;
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(token);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 ready state: positive-weight reference + a window with a loadable
        /// source + a resolvable attached Style Profile → enabled, no reason line.
        /// The Style Profile pref is pinned to a real temp asset GUID (round-trip
        /// discipline) and restored in finally.
        /// </summary>
        [Test]
        public void FitButtonState_AllPrereqsSatisfied_EnabledWithNoReason()
        {
            string savedGuid = new NamerProcessorSettings().StyleProfileGuid;
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            ScriptableObject token = ScriptableObject.CreateInstance<ScriptableObject>();
            token.name = "LifecycleSourceToken";
            NamerStyleProfile attached = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                EnsureTempFolder();
                AssetDatabase.CreateAsset(attached, TempFolder + "/LifecycleStyleProfile.asset");
                InjectWindowSource(window, token);
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(TempFolder + "/LifecycleStyleProfile.asset");
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, window, settings);
                Assert.IsFalse(state.Disabled, "all prereqs met — the button is enabled");
                Assert.IsNull(state.ReasonMiniLabel, "an enabled button renders no reason line");
            }
            finally
            {
                new NamerProcessorSettings().StyleProfileGuid = savedGuid;
                AssetDatabase.DeleteAsset(TempFolder);
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(token);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// D-07 M1 verbatim-copy law: a fitted temp profile migrates to a
        /// "{name} Post.asset" NamerPostLook beside itself with every fitted field
        /// equal (six floats, curve KEYS, fingerprint, exemplar), the reference lists
        /// seeded with null slots and weights preserved, HasFit true, and the artist
        /// knob defaulted (the profile never stored Bloom Intensity).
        /// </summary>
        [Test]
        public void MigrateProfile_CopiesLookVerbatim()
        {
            EnsureTempFolder();
            Texture2D refA = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "MigrationRefA",
            };
            Texture2D refB = new Texture2D(4, 4, TextureFormat.RGBA32, false)
            {
                name = "MigrationRefB",
            };
            AssetDatabase.CreateAsset(refA, TempFolder + "/MigrationRefA.asset");
            AssetDatabase.CreateAsset(refB, TempFolder + "/MigrationRefB.asset");
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.name = "MigrationSource";
            profile.PostTemperature = 15.5f;
            profile.PostTint = -8.25f;
            profile.PostExposureEv = 1.25f;
            profile.PostContrast = -12f;
            profile.PostSaturation = 7.5f;
            profile.PostBloomThreshold = 0.77f;
            profile.PostMasterCurve = new AnimationCurve(new[]
            {
                new Keyframe(0f, 0.1f), new Keyframe(0.5f, 0.6f), new Keyframe(1f, 0.9f),
            });
            profile.PostRedCurve = new AnimationCurve(new[] { new Keyframe(0f, 0.05f), new Keyframe(1f, 0.95f) });
            profile.PostGreenCurve = new AnimationCurve(new[] { new Keyframe(0f, 0f), new Keyframe(1f, 1f) });
            profile.PostBlueCurve = new AnimationCurve(new[] { new Keyframe(0f, 0.1f), new Keyframe(1f, 0.9f) });
            profile.PostFitFingerprint = "migration-test-fingerprint";
            profile.PostFitExemplarName = "MigrationExemplar";
            profile.References.Add(refA);
            profile.References.Add(null);
            profile.References.Add(refB);
            profile.ReferenceWeights.Add(0.5f);
            profile.ReferenceWeights.Add(1f);
            profile.ReferenceWeights.Add(0.25f);
            AssetDatabase.CreateAsset(profile, TempFolder + "/MigrationSource.asset");
            try
            {
                Assert.IsTrue(NamerPostLookMigration.MigrateProfile(profile), "a fitted persisted profile converts");

                string targetPath = TempFolder + "/MigrationSource Post.asset";
                NamerPostLook look = AssetDatabase.LoadAssetAtPath<NamerPostLook>(targetPath);
                Assert.IsNotNull(look, "the migrated asset exists beside the profile");

                Assert.AreEqual(15.5f, look.PostTemperature);
                Assert.AreEqual(-8.25f, look.PostTint);
                Assert.AreEqual(1.25f, look.PostExposureEv);
                Assert.AreEqual(-12f, look.PostContrast);
                Assert.AreEqual(7.5f, look.PostSaturation);
                Assert.AreEqual(0.77f, look.PostBloomThreshold);

                Assert.AreEqual(3, look.PostMasterCurve.length, "curve keys copy verbatim");
                Assert.AreEqual(0.6f, look.PostMasterCurve.keys[1].value, "curve VALUES copy, not just lengths");
                Assert.AreEqual(2, look.PostRedCurve.length);
                Assert.AreEqual(2, look.PostGreenCurve.length);
                Assert.AreEqual(2, look.PostBlueCurve.length);

                Assert.AreEqual("migration-test-fingerprint", look.PostFitFingerprint, "the fingerprint carries verbatim");
                Assert.AreEqual("MigrationExemplar", look.PostFitExemplarName);
                Assert.IsTrue(look.HasFit);

                Assert.AreEqual(3, look.References.Count, "references seed with null slots preserved");
                Assert.AreSame(refA, look.References[0], "reference instances carry over");
                Assert.IsNull(look.References[1], "null slots are preserved");
                Assert.AreSame(refB, look.References[2]);
                Assert.AreEqual(0.5f, look.ReferenceWeights[0], "weights stay parallel");
                Assert.AreEqual(1f, look.ReferenceWeights[1]);
                Assert.AreEqual(0.25f, look.ReferenceWeights[2]);

                Assert.AreEqual(NamerEditorConstants.DefaultBloomIntensity, look.PostBloomIntensity,
                    "the artist knob moves home defaulted — the profile never stored it");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// D-07 GEN-03: an existing target at "{name} Post.asset" is NEVER
        /// overwritten — MigrateProfile returns false, the source fit stays intact,
        /// and the pre-existing target's parameters are untouched.
        /// </summary>
        [Test]
        public void MigrateProfile_SkipsExistingTarget()
        {
            EnsureTempFolder();
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.name = "MigrationSkip";
            profile.PostTemperature = 9.5f;
            profile.PostFitFingerprint = "skip-fingerprint";
            AssetDatabase.CreateAsset(profile, TempFolder + "/MigrationSkip.asset");
            NamerPostLook existing = ScriptableObject.CreateInstance<NamerPostLook>();
            existing.PostTemperature = 42f;
            AssetDatabase.CreateAsset(existing, TempFolder + "/MigrationSkip Post.asset");
            try
            {
                Assert.IsFalse(NamerPostLookMigration.MigrateProfile(profile), "an existing target skips the profile whole");
                Assert.IsTrue(profile.HasPostFit, "the source fit stays intact on skip");
                Assert.AreEqual(9.5f, profile.PostTemperature);
                NamerPostLook atTarget = AssetDatabase.LoadAssetAtPath<NamerPostLook>(TempFolder + "/MigrationSkip Post.asset");
                Assert.AreEqual(42f, atTarget.PostTemperature, "the pre-existing target is untouched");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// D-07 idempotence: after a successful migration the source's fit
        /// fingerprint is cleared (HasPostFit false — the scan filter never
        /// re-selects it) while the drained fitted fields stay on disk for plan 06's
        /// deletion, and a second call is a no-op returning false.
        /// </summary>
        [Test]
        public void MigrateProfile_ClearsSourceFitAndSecondCallIsNoOp()
        {
            EnsureTempFolder();
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.name = "MigrationClear";
            profile.PostTemperature = 3.25f;
            profile.PostFitFingerprint = "clear-fingerprint";
            profile.PostFitExemplarName = "ClearExemplar";
            AssetDatabase.CreateAsset(profile, TempFolder + "/MigrationClear.asset");
            try
            {
                Assert.IsTrue(NamerPostLookMigration.MigrateProfile(profile));
                Assert.IsFalse(profile.HasPostFit, "the fit has moved — the filter never re-selects");
                Assert.AreEqual(string.Empty, profile.PostFitFingerprint);
                Assert.AreEqual(3.25f, profile.PostTemperature,
                    "only the fingerprint clears now — the drained fields stay until plan 06 deletes them");
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<NamerPostLook>(TempFolder + "/MigrationClear Post.asset"),
                    "the migrated asset from the first call is on disk");
                Assert.IsFalse(NamerPostLookMigration.MigrateProfile(profile), "a second call is a no-op");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// Reflection seam (the smoke-test precedent): injects a non-null
        /// <c>_selection</c> plus a <see cref="NamerSourceModel"/> holding one
        /// material inspection so the window reports HasLoadableSource without any
        /// GPU or Process work.
        /// </summary>
        private static void InjectWindowSource(NamerEditorWindow window, UnityEngine.Object token)
        {
            FieldInfo selectionField = typeof(NamerEditorWindow).GetField(
                "_selection", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(selectionField, "_selection field must exist on NamerEditorWindow");
            selectionField.SetValue(window, token);

            FieldInfo modelField = typeof(NamerEditorWindow).GetField(
                "_model", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(modelField, "_model field must exist on NamerEditorWindow");
            NamerSourceModel model = new NamerSourceModel();
            model.Materials.Add(new NamerMaterialInspection());
            modelField.SetValue(window, model);
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            }
        }
    }
}

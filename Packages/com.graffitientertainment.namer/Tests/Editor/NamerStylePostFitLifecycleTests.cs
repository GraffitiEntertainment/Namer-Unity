using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 plan 03 lifecycle tests (STYL-08 tail): the Fit Look prerequisite seam
    /// (<see cref="NamerStyleProfileEditor.FitButtonState"/> — disabled with the locked
    /// reasons while unextracted / exemplar-less, enabled otherwise), the
    /// <see cref="NamerStylePostFit.ApplyToProfile"/> write (every fitted field lands on
    /// the profile, the fit is fresh, and re-fitting replaces wholesale with no
    /// accumulation), and the W8 attach-state honesty clauses of
    /// <see cref="NamerEditorWindow.DescribeProfileState"/>. Pure EditMode logic — no
    /// GPU, no Process run (the Volume regen gate itself is covered by the plan-02
    /// writer tests; the Process hook is compile-proven here and regression-covered by
    /// the full suite). The one asset-writing test uses a temp folder deleted in
    /// <c>finally</c> so the fingerprint is a real GUID-based identity string.
    /// </summary>
    public class NamerStylePostFitLifecycleTests
    {
        private const string TempFolder = "Assets/NAMER_PostFit_Lifecycle_Tests_Temp";

        private const string NotExtractedReason =
            "Fit Look needs extracted statistics first — press Extract Style Stats.";

        private const string NoExemplarReason =
            "Fit Look uses the object loaded in the Processor window (Tools > NAMER > Processor) — load a Source there first.";

        [Test]
        public void FitButtonState_NotExtracted_DisabledWithReason()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                Assert.IsFalse(profile.IsExtracted, "hand-set state: no extract fingerprint");

                NamerStyleProfileEditor.FitLookButtonState state =
                    NamerStyleProfileEditor.FitButtonState(profile, true);
                Assert.IsTrue(state.Disabled, "unextracted profile disables Fit Look even with an exemplar");
                Assert.AreEqual(NotExtractedReason, state.ReasonMiniLabel,
                    "the locked not-extracted prereq reason renders verbatim");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void FitButtonState_NoExemplar_DisabledWithReason()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.ReferenceFingerprint = "hand-set-extract-fingerprint";
            try
            {
                NamerStyleProfileEditor.FitLookButtonState state =
                    NamerStyleProfileEditor.FitButtonState(profile, false);
                Assert.IsTrue(state.Disabled, "extracted profile with no loadable exemplar window disables Fit Look");
                Assert.AreEqual(NoExemplarReason, state.ReasonMiniLabel,
                    "the locked no-exemplar prereq reason renders verbatim");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void FitButtonState_Ready_Enabled()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.ReferenceFingerprint = "hand-set-extract-fingerprint";
            try
            {
                NamerStyleProfileEditor.FitLookButtonState state =
                    NamerStyleProfileEditor.FitButtonState(profile, true);
                Assert.IsFalse(state.Disabled, "extracted profile plus an exemplar window enables Fit Look");
                Assert.IsNull(state.ReasonMiniLabel, "no prereq reason renders under an enabled button");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

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

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            }
        }
    }
}

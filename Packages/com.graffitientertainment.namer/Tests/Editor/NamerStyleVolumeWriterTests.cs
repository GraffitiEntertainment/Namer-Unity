using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 plan 02 volume-writer tests (STYL-07/STYL-08): a fitted profile generates
    /// a five-component VolumeProfile (ACES tonemapping, fitted WB/CA/bloom values,
    /// curve round-trip) with every component wired as a real sub-asset (Pitfall 1),
    /// the NamerGenerated stamp gates future overwrites, the non-generated refusal of
    /// the D-02 overwrite discipline fires while the fit's own stamped companion is
    /// re-writable with the opt-in OFF (CR-01, 05.1 review), and
    /// the composed path is profile-keyed at the destination ROOT (UI-SPEC section C —
    /// never a per-selection subfolder). Every writing test runs against a temp
    /// destination deleted in <c>finally</c>; the four touched NamerProcessor EditorPrefs
    /// keys are snapshotted and restored so nothing leaks into the user's settings.
    /// </summary>
    public class NamerStyleVolumeWriterTests
    {
        private const string TempFolder = "Assets/NAMER_Volume_Tests_Temp";

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";

        private struct PrefsSnapshot
        {
            public string Destination;
            public string Prefix;
            public string Suffix;
            public bool OverwriteGenerated;
        }

        [Test]
        public void Write_CreatesVolumeWithFiveComponents()
        {
            NamerStyleProfile profile = CreateFittedProfile("VolumeWriterFit");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            NamerProcessorSettings settings = NewSettings();
            try
            {
                VolumeProfile written = NamerStyleVolumeWriter.Write(profile, settings);
                Assert.IsNotNull(written, "Write returns the loaded, stamped VolumeProfile");

                string path = NamerStyleVolumeWriter.ComposeVolumePath(profile, settings);
                VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                Assert.IsNotNull(loaded, "the VolumeProfile must persist to disk at the composed path");
                Assert.AreEqual(5, loaded.components.Count,
                    "exactly the five UI-SPEC assembly-table components are generated");

                Assert.IsTrue(loaded.TryGet(out Tonemapping tonemapping));
                Assert.IsTrue(tonemapping.mode.overrideState, "tonemapping mode is an override (STYL-08 default ACES)");
                Assert.AreEqual(TonemappingMode.ACES, tonemapping.mode.value);

                Assert.IsTrue(loaded.TryGet(out WhiteBalance whiteBalance));
                Assert.IsTrue(whiteBalance.temperature.overrideState);
                Assert.AreEqual(profile.PostTemperature, whiteBalance.temperature.value);
                Assert.IsTrue(whiteBalance.tint.overrideState);
                Assert.AreEqual(profile.PostTint, whiteBalance.tint.value);

                Assert.IsTrue(loaded.TryGet(out ColorAdjustments colorAdjustments));
                Assert.IsTrue(colorAdjustments.postExposure.overrideState);
                Assert.AreEqual(profile.PostExposureEv, colorAdjustments.postExposure.value);
                Assert.IsTrue(colorAdjustments.contrast.overrideState);
                Assert.AreEqual(profile.PostContrast, colorAdjustments.contrast.value);
                Assert.IsTrue(colorAdjustments.saturation.overrideState);
                Assert.AreEqual(profile.PostSaturation, colorAdjustments.saturation.value);
                Assert.IsFalse(colorAdjustments.colorFilter.overrideState,
                    "colorFilter stays visibly neutral — not a fitted knob");
                Assert.AreEqual(Color.white, colorAdjustments.colorFilter.value);

                Assert.IsTrue(loaded.TryGet(out Bloom bloom));
                Assert.IsTrue(bloom.threshold.overrideState);
                Assert.AreEqual(profile.PostBloomThreshold, bloom.threshold.value);
                Assert.IsTrue(bloom.intensity.overrideState);
                Assert.AreEqual(NamerEditorConstants.DefaultBloomIntensity, bloom.intensity.value,
                    "bloom intensity is the named-constant artist knob, never fitted (STYL-08)");

                Assert.IsTrue(loaded.TryGet(out ColorCurves curves));
                Assert.IsTrue(curves.master.overrideState);
                Assert.IsNotNull(curves.master.value, "curves round-trip as TextureCurves");
                Assert.IsNotNull(curves.red.value);
                Assert.IsNotNull(curves.green.value);
                Assert.IsNotNull(curves.blue.value);

                // Pitfall 1: the components must be wired as REAL sub-assets of the
                // file, not dangling in-memory instances lost on the next load.
                UnityEngine.Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(path);
                bool hasTonemappingSubAsset = false;
                bool hasColorCurvesSubAsset = false;
                foreach (UnityEngine.Object subAsset in subAssets)
                {
                    if (subAsset is Tonemapping)
                    {
                        hasTonemappingSubAsset = true;
                    }

                    if (subAsset is ColorCurves)
                    {
                        hasColorCurvesSubAsset = true;
                    }
                }

                Assert.IsTrue(hasTonemappingSubAsset, "Tonemapping is serialized as a sub-asset");
                Assert.IsTrue(hasColorCurvesSubAsset, "ColorCurves is serialized as a sub-asset");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Write_StampsGeneratedLabel()
        {
            NamerStyleProfile profile = CreateFittedProfile("VolumeWriterStamp");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            NamerProcessorSettings settings = NewSettings();
            try
            {
                NamerStyleVolumeWriter.Write(profile, settings);

                VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(
                    NamerStyleVolumeWriter.ComposeVolumePath(profile, settings));
                Assert.IsNotNull(loaded);
                Assert.Contains(NamerEditorConstants.GeneratedLabel, AssetDatabase.GetLabels(loaded),
                    "the generated Volume carries the NamerGenerated stamp so the overwrite opt-in gate covers it (GEN-03)");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Write_RefusesNonGeneratedOverwrite()
        {
            NamerStyleProfile profile = CreateFittedProfile("VolumeWriterForeign");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            // Opt-in ON, so ONLY the non-generated refusal can fire.
            NamerProcessorSettings settings = NewSettings();
            settings.OverwriteGenerated = true;
            try
            {
                string path = NamerStyleVolumeWriter.ComposeVolumePath(profile, settings);
                VolumeProfile foreign = ScriptableObject.CreateInstance<VolumeProfile>();
                foreign.name = "ForeignNonGenerated";
                AssetDatabase.CreateAsset(foreign, path);

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => NamerStyleVolumeWriter.Write(profile, settings));
                Assert.That(ex.Message, Does.Contain("non-generated"),
                    "the refusal must name the non-generated-target reason (flows to the Fit failed HelpBox in plan 03)");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void Write_RefitOverwritesStampedVolumeWhenOptInOff()
        {
            NamerStyleProfile profile = CreateFittedProfile("VolumeWriterOptIn");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            // NewSettings leaves the opt-in OFF — the default-settings path CR-01 fixed.
            NamerProcessorSettings settings = NewSettings();
            try
            {
                // First write succeeds and stamps the asset (fresh target — the
                // opt-in is irrelevant when nothing exists at the path yet).
                Assert.IsNotNull(NamerStyleVolumeWriter.Write(profile, settings));

                // CR-01 (05.1 review), re-pinned from Write_RefusesWhenOptInOff: the
                // companion Volume is auto-derived from profile truth (D-02) and the
                // Fit Look contract promises refit-overwrite unconditionally, so a
                // stamped Volume must be re-writable with the opt-in OFF — the old
                // opt-in refusal broke every second Fit Look (and every Process-time
                // regeneration) on default settings. The re-write still regenerates
                // the Volume wholesale with a FRESH profile instance — no
                // "component already exists" throw (Pitfall 10).
                VolumeProfile rewritten = NamerStyleVolumeWriter.Write(profile, settings);
                Assert.IsNotNull(rewritten, "re-fit with the opt-in off regenerates the companion Volume");
                Assert.AreEqual(5, rewritten.components.Count);
                Assert.IsTrue(rewritten.TryGet(out Tonemapping tonemapping),
                    "the re-written Volume loads a full fresh component set from disk");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ComposeVolumePath_IsProfileKeyedAtDestinationRoot()
        {
            NamerStyleProfile profile = CreateFittedProfile("My/Style: Profile");
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                NamerProcessorSettings settings = NewSettings();
                settings.Prefix = "Pfx";
                settings.Suffix = "Sfx";

                string path = NamerStyleVolumeWriter.ComposeVolumePath(profile, settings);
                string safeName = AssetGenerator.SanitizeFileName(profile.name);
                string expected = TempFolder + "/" + "Pfx" + safeName + "Sfx" + "_Post.asset";
                Assert.AreEqual(expected, path,
                    "the Volume path is {prefix}{SanitizeFileName(profile.name)}{suffix}_Post.asset (UI-SPEC section C)");
                Assert.IsFalse(path.Contains("/" + safeName + "/"),
                    "the Volume must NOT live in a per-selection subfolder — the fit is profile-scoped and scene-wide");
            }
            finally
            {
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// A hand-built fitted profile with distinct, unclamped-safe knob values and
        /// non-trivial curves (mirrors the state Fit Look leaves behind, plan 03).
        /// </summary>
        private static NamerStyleProfile CreateFittedProfile(string profileName)
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            profile.name = profileName;
            profile.PostTemperature = 12.5f;
            profile.PostTint = -3.25f;
            profile.PostExposureEv = 0.75f;
            profile.PostContrast = 8f;
            profile.PostSaturation = -4.5f;
            profile.PostBloomThreshold = 0.82f;
            profile.PostMasterCurve = new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.5f, 0.55f), new Keyframe(1f, 1f));
            profile.PostRedCurve = new AnimationCurve(new Keyframe(0f, 0.05f), new Keyframe(1f, 0.95f));
            profile.PostGreenCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
            profile.PostBlueCurve = new AnimationCurve(new Keyframe(0f, 0.1f), new Keyframe(1f, 0.9f));
            profile.PostFitFingerprint = "test-fit-fingerprint";
            profile.PostFitExemplarName = "TestExemplar";
            return profile;
        }

        private static NamerProcessorSettings NewSettings()
        {
            return new NamerProcessorSettings
            {
                Destination = TempFolder + "/",
                Prefix = "",
                Suffix = "",
                OverwriteGenerated = false,
            };
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            }
        }

        /// <summary>
        /// Snapshots the four EditorPrefs keys the settings object writes through to,
        /// reading through the same defaults so restore is value-faithful.
        /// </summary>
        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Destination = EditorPrefs.GetString(DestinationKey, NamerEditorConstants.DefaultDestination),
                Prefix = EditorPrefs.GetString(PrefixKey, NamerEditorConstants.DefaultPrefix),
                Suffix = EditorPrefs.GetString(SuffixKey, NamerEditorConstants.DefaultSuffix),
                OverwriteGenerated = EditorPrefs.GetBool(OverwriteKey, false),
            };
        }

        private static void RestorePrefs(PrefsSnapshot prefs)
        {
            EditorPrefs.SetString(DestinationKey, prefs.Destination);
            EditorPrefs.SetString(PrefixKey, prefs.Prefix);
            EditorPrefs.SetString(SuffixKey, prefs.Suffix);
            EditorPrefs.SetBool(OverwriteKey, prefs.OverwriteGenerated);
        }
    }
}

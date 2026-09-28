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
    /// 05.2 volume-writer tests (STYL-07/STYL-08/STYL-10): a fitted
    /// <see cref="NamerPostLook"/> generates the five-component VolumeProfile (ACES
    /// tonemapping, WB/CA/bloom values reading the UNSCALED asset fields at FULL
    /// strength — R7, no tone-match scaling anywhere — and curve round-trip) with
    /// every component wired as a real sub-asset (Pitfall 1), the NamerGenerated
    /// stamp gates future overwrites, the non-generated refusal of the D-02
    /// overwrite discipline fires while the asset's own stamped companion is
    /// re-writable with the opt-in OFF (CR-01 carried over), the A13
    /// volume-staleness fingerprint records at write time, and the composed path is
    /// asset-name-keyed at the destination ROOT (05.2 D-06 — never a per-selection
    /// subfolder; name-keyed so duplicates stay independent). 05.2-06 (STYL-10):
    /// the 5.1 profile-path fixtures retired with the deleted profile writer
    /// members — the asset twins above carry the full contract now. Every writing
    /// test runs against a temp destination deleted in <c>finally</c>; the four
    /// touched NamerProcessor EditorPrefs keys are snapshotted and restored so
    /// nothing leaks into the user's settings.
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

        /// <summary>
        /// 05.2 plan 01 (STYL-12, R7 full-strength pin): Write(NamerPostLook) generates
        /// the five-component Volume with every override reading the UNSCALED asset
        /// field EXACTLY — distinct non-neutral fixture values incl. the
        /// PostBloomIntensity artist knob — and the curve keys surviving the
        /// TextureCurve bake within tolerance.
        /// </summary>
        [Test]
        public void Write_FromAsset_CreatesVolumeWithFiveComponents()
        {
            NamerPostLook look = CreateFittedLook("VolumeWriterAssetFit");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            NamerProcessorSettings settings = NewSettings();
            try
            {
                VolumeProfile written = NamerStyleVolumeWriter.Write(look, settings);
                Assert.IsNotNull(written, "Write returns the loaded, stamped VolumeProfile");

                string path = NamerStyleVolumeWriter.ComposeVolumePath(look, settings);
                VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
                Assert.IsNotNull(loaded, "the VolumeProfile must persist to disk at the composed asset-keyed path");
                Assert.AreEqual(5, loaded.components.Count,
                    "exactly the five UI-SPEC assembly-table components are generated");

                Assert.IsTrue(loaded.TryGet(out Tonemapping tonemapping));
                Assert.IsTrue(tonemapping.mode.overrideState, "tonemapping mode is an override (STYL-08 default ACES)");
                Assert.AreEqual(TonemappingMode.ACES, tonemapping.mode.value);

                // R7 full-strength pin: every override equals the asset field EXACTLY —
                // no tone-match scaling anywhere on the asset path.
                Assert.IsTrue(loaded.TryGet(out WhiteBalance whiteBalance));
                Assert.IsTrue(whiteBalance.temperature.overrideState);
                Assert.AreEqual(look.PostTemperature, whiteBalance.temperature.value,
                    "temperature reads the unscaled asset field at FULL strength");
                Assert.IsTrue(whiteBalance.tint.overrideState);
                Assert.AreEqual(look.PostTint, whiteBalance.tint.value,
                    "tint reads the unscaled asset field at FULL strength");

                Assert.IsTrue(loaded.TryGet(out ColorAdjustments colorAdjustments));
                Assert.IsTrue(colorAdjustments.postExposure.overrideState);
                Assert.AreEqual(look.PostExposureEv, colorAdjustments.postExposure.value,
                    "post exposure reads the unscaled asset field at FULL strength");
                Assert.IsTrue(colorAdjustments.contrast.overrideState);
                Assert.AreEqual(look.PostContrast, colorAdjustments.contrast.value,
                    "contrast reads the unscaled asset field at FULL strength");
                Assert.IsTrue(colorAdjustments.saturation.overrideState);
                Assert.AreEqual(look.PostSaturation, colorAdjustments.saturation.value,
                    "saturation reads the unscaled asset field at FULL strength");
                Assert.IsFalse(colorAdjustments.colorFilter.overrideState,
                    "colorFilter stays visibly neutral — not a fitted knob");
                Assert.AreEqual(Color.white, colorAdjustments.colorFilter.value);

                Assert.IsTrue(loaded.TryGet(out Bloom bloom));
                Assert.IsTrue(bloom.threshold.overrideState);
                Assert.AreEqual(look.PostBloomThreshold, bloom.threshold.value,
                    "bloom threshold reads the asset field exactly (a separability floor, never scaled)");
                Assert.IsTrue(bloom.intensity.overrideState);
                Assert.AreEqual(look.PostBloomIntensity, bloom.intensity.value,
                    "bloom intensity reads the asset's own artist knob (A9) — unscaled, never fitted");

                Assert.IsTrue(loaded.TryGet(out ColorCurves curves));
                Assert.IsTrue(curves.master.overrideState);
                Assert.IsNotNull(curves.master.value, "curves round-trip as TextureCurves");
                Assert.IsNotNull(curves.red.value);
                Assert.IsNotNull(curves.green.value);
                Assert.IsNotNull(curves.blue.value);
                Assert.That(Mathf.Abs(curves.master.value.Evaluate(0.5f) - 0.55f), Is.LessThan(0.01f),
                    "the master key (0.5, 0.55) bakes at FULL strength (TextureCurve bake tolerance — a half-strength scale would read 0.525)");
                Assert.That(Mathf.Abs(curves.red.value.Evaluate(0.5f) - 0.5f), Is.LessThan(0.01f),
                    "the red curve keeps its shape at FULL strength");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        [Test]
        public void Write_FromAsset_StampAndRefitOverwrite()
        {
            NamerPostLook look = CreateFittedLook("VolumeWriterAssetStamp");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            // NewSettings leaves the opt-in OFF — the default-settings path.
            NamerProcessorSettings settings = NewSettings();
            try
            {
                VolumeProfile written = NamerStyleVolumeWriter.Write(look, settings);
                Assert.IsNotNull(written);

                VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(
                    NamerStyleVolumeWriter.ComposeVolumePath(look, settings));
                Assert.IsNotNull(loaded);
                Assert.Contains(NamerEditorConstants.GeneratedLabel, AssetDatabase.GetLabels(loaded),
                    "the generated Volume carries the NamerGenerated stamp so the overwrite opt-in gate covers it (GEN-03)");

                // Own-companion exemption carried over: a stamped Volume re-writes
                // with the opt-in OFF (regeneration is promised unconditionally).
                VolumeProfile rewritten = NamerStyleVolumeWriter.Write(look, settings);
                Assert.IsNotNull(rewritten, "re-write with the opt-in off regenerates the asset's companion Volume");
                Assert.AreEqual(5, rewritten.components.Count);
                Assert.IsTrue(rewritten.TryGet(out Tonemapping tonemapping),
                    "the re-written Volume loads a full fresh component set from disk");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        [Test]
        public void Write_FromAsset_RefusesNonGeneratedOverwrite()
        {
            NamerPostLook look = CreateFittedLook("VolumeWriterAssetForeign");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            // Opt-in ON, so ONLY the non-generated refusal can fire.
            NamerProcessorSettings settings = NewSettings();
            settings.OverwriteGenerated = true;
            try
            {
                string path = NamerStyleVolumeWriter.ComposeVolumePath(look, settings);
                VolumeProfile foreign = ScriptableObject.CreateInstance<VolumeProfile>();
                foreign.name = "ForeignNonGenerated";
                AssetDatabase.CreateAsset(foreign, path);

                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => NamerStyleVolumeWriter.Write(look, settings));
                Assert.That(ex.Message, Does.Contain("non-generated"),
                    "the refusal must name the non-generated-target reason (carried-over profile-path gate)");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        [Test]
        public void Write_FromAsset_RefusesUnfitted()
        {
            NamerPostLook unfitted = ScriptableObject.CreateInstance<NamerPostLook>();
            unfitted.name = "VolumeWriterAssetUnfitted";
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            NamerProcessorSettings settings = NewSettings();
            try
            {
                InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
                    () => NamerStyleVolumeWriter.Write(unfitted, settings));
                Assert.That(ex.Message, Does.Contain("carries no fitted post look"),
                    "the null/unfitted guard keeps the profile-path InvalidOperationException shape");

                InvalidOperationException nullEx = Assert.Throws<InvalidOperationException>(
                    () => NamerStyleVolumeWriter.Write((NamerPostLook)null, settings));
                Assert.That(nullEx.Message, Does.Contain("carries no fitted post look"),
                    "a null asset refuses with the same guard shape");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(unfitted);
            }
        }

        [Test]
        public void Write_FromAsset_RecordsVolumeWriteFingerprint()
        {
            NamerPostLook look = CreateFittedLook("VolumeWriterAssetFingerprint");
            PrefsSnapshot prefs = CapturePrefs();
            EnsureTempFolder();
            NamerProcessorSettings settings = NewSettings();
            try
            {
                VolumeProfile written = NamerStyleVolumeWriter.Write(look, settings);
                Assert.IsNotNull(written);
                Assert.AreEqual(look.ComputeParamsFingerprint(), look.VolumeWriteFingerprint,
                    "the write stamps the params fingerprint at write time (A13)");
                Assert.IsFalse(look.IsVolumeStale,
                    "immediately after a write the asset reports not volume-stale");

                look.PostSaturation = look.PostSaturation + 1.5f;
                Assert.IsTrue(look.IsVolumeStale,
                    "a parameter tweak after the write flips the A13 volume-stale warning on");

                VolumeProfile regenerated = NamerStyleVolumeWriter.Write(look, settings);
                Assert.IsNotNull(regenerated);
                Assert.IsFalse(look.IsVolumeStale,
                    "regenerating re-stamps the fingerprint and clears the staleness (ClampToCaps keeps the tweaked value in range)");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        [Test]
        public void ComposeVolumePath_IsAssetNameKeyedAtDestinationRoot()
        {
            NamerPostLook look = CreateFittedLook("My/Post: Look");
            PrefsSnapshot prefs = CapturePrefs();
            try
            {
                NamerProcessorSettings settings = NewSettings();
                settings.Prefix = "Pfx";
                settings.Suffix = "Sfx";

                string path = NamerStyleVolumeWriter.ComposeVolumePath(look, settings);
                string safeName = AssetGenerator.SanitizeFileName(look.name);
                string expected = TempFolder + "/" + "Pfx" + safeName + "Sfx" + "_Post.asset";
                Assert.AreEqual(expected, path,
                    "the asset Volume path is {prefix}{SanitizeFileName(look.name)}{suffix}_Post.asset (05.2 D-06 — name-keyed so duplicates stay independent)");
                Assert.IsFalse(path.Contains("/" + safeName + "/"),
                    "the Volume must NOT live in a per-selection subfolder — the look is asset-scoped and scene-wide");
            }
            finally
            {
                RestorePrefs(prefs);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// A hand-built fitted post-look asset with distinct, unclamped-safe knob
        /// values and non-trivial curves (mirrors the state Fit Look leaves behind;
        /// 05.2 plan 01 — PostBloomIntensity carries a DISTINCT non-neutral value so
        /// the full-strength pin actually tests the knob).
        /// </summary>
        private static NamerPostLook CreateFittedLook(string lookName)
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.name = lookName;
            look.PostTemperature = 12.5f;
            look.PostTint = -3.25f;
            look.PostExposureEv = 0.75f;
            look.PostContrast = 8f;
            look.PostSaturation = -4.5f;
            look.PostBloomThreshold = 0.82f;
            look.PostBloomIntensity = 2.25f;
            look.PostMasterCurve = new AnimationCurve(
                new Keyframe(0f, 0f), new Keyframe(0.5f, 0.55f), new Keyframe(1f, 1f));
            look.PostRedCurve = new AnimationCurve(new Keyframe(0f, 0.05f), new Keyframe(1f, 0.95f));
            look.PostGreenCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
            look.PostBlueCurve = new AnimationCurve(new Keyframe(0f, 0.1f), new Keyframe(1f, 0.9f));
            look.PostFitFingerprint = "test-asset-fit-fingerprint";
            look.PostFitExemplarName = "TestExemplar";
            return look;
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

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Assembles the generated URP Volume asset from a fitted
    /// <see cref="NamerPostLook"/> (05.2 D-06: the ASSET is the source of truth —
    /// this writer derives the Volume from it and re-derives it on every Fit Look /
    /// Regenerate Volume; hand-tweaks made on the generated Volume are
    /// accepted-lost on re-fit/regeneration). Asset-only since 05.2 plan 06
    /// (STYL-10): the 5.1 profile-path members are deleted — the post look exists
    /// exactly once, on the asset. Every write rides the shared generated-asset
    /// discipline: <see cref="AssetGenerator.ValidateComposedPath"/> destination
    /// confinement, <see cref="AssetGenerator.EnsureWritableTarget"/>
    /// (NamerGenerated-label check with the own-companion exemption — see
    /// <see cref="PreflightWriteGate(NamerPostLook, NamerProcessorSettings)"/>), and
    /// the <see cref="AssetGenerator.Stamp"/> that makes the fresh asset
    /// overwritable by the next regeneration. Ping feedback belongs to the call
    /// sites (Fit Look / Regenerate Volume), never here.
    /// </summary>
    public static class NamerStyleVolumeWriter
    {
        /// <summary>
        /// Composes the generated Volume path for a post-look asset at the configured
        /// destination ROOT (05.2 D-06 re-home; the asset is the source of truth —
        /// same GEN-01 formula as the profile path, keyed on the ASSET name):
        /// <c>{prefix}{SanitizeFileName(look.name)}{suffix}_Post.asset</c>. Because the
        /// path keys off the asset name, Unity's native duplicate ("{name} 1") yields a
        /// naturally independent Volume — the per-scene duplicate-and-tweak workflow
        /// rides the naming for free.
        /// </summary>
        public static string ComposeVolumePath(NamerPostLook look, NamerProcessorSettings settings)
        {
            string prefix = settings.Prefix ?? string.Empty;
            string suffix = settings.Suffix ?? string.Empty;
            return settings.Destination + prefix + AssetGenerator.SanitizeFileName(look.name) + suffix + "_Post.asset";
        }

        /// <summary>
        /// CR-01 gate discipline carried over verbatim from the profile path (05.2 D-06):
        /// the write gates <see cref="Write(NamerPostLook, NamerProcessorSettings)"/>
        /// will run, evaluated WITHOUT writing — destination confinement
        /// (<see cref="AssetGenerator.ValidateComposedPath"/>) plus the
        /// NamerGenerated-label check with the own-companion exemption
        /// (<see cref="AssetGenerator.EnsureWritableTarget"/>, overwriteGenerated:
        /// true). The Fit Look / Regenerate Volume transactions call this BEFORE
        /// mutating the asset, so a refusal leaves the asset untouched.
        /// </summary>
        internal static void PreflightWriteGate(NamerPostLook look, NamerProcessorSettings settings)
        {
            string path = ComposeVolumePath(look, settings);
            AssetGenerator.ValidateComposedPath(path, settings.Destination);
            AssetGenerator.EnsureWritableTarget(path, overwriteGenerated: true);
        }

        /// <summary>
        /// Generates the VolumeProfile asset for a fitted post-look asset and returns
        /// the loaded, NamerGenerated-stamped asset (05.2 D-06: the ASSET is the source
        /// of truth — this writer derives the Volume from it; hand-tweaks made on the
        /// generated Volume are accepted-lost on re-fit/regeneration). Throws
        /// <see cref="InvalidOperationException"/> when the asset is null or unfitted,
        /// when the composed path escapes the configured destination, or when a
        /// non-generated asset sits at the target path (carried-over profile-path
        /// gates: <see cref="PreflightWriteGate(NamerPostLook, NamerProcessorSettings)"/>).
        /// Every parameter applies at FULL STRENGTH — the UNSCALED
        /// <c>look.Post*</c> fields (05.2 R7: post owns tone unconditionally, no
        /// tone-match scaling anywhere) — and bloom intensity reads the asset's own
        /// <see cref="NamerPostLook.PostBloomIntensity"/> artist knob. After the stamp,
        /// the write records <see cref="NamerPostLook.VolumeWriteFingerprint"/> so the
        /// A13 volume-staleness seam starts honest.
        /// </summary>
        public static VolumeProfile Write(NamerPostLook look, NamerProcessorSettings settings)
        {
            if (look == null || !look.HasFit)
            {
                throw new InvalidOperationException(
                    "Refusing to generate the post Volume: post look '"
                    + (look != null ? look.name : "null")
                    + "' carries no fitted post look. Run Fit Look first.");
            }

            // Threat T-05.2-01-01: hand-edited serialized fit values are sanitized
            // before any parameter is read.
            look.ClampToCaps();

            PreflightWriteGate(look, settings);

            string path = ComposeVolumePath(look, settings);

            // Pitfall 10 (carried over): a FRESH profile instance every write — never
            // Add into a loaded stale profile (that both orphans old TextureCurve
            // sub-assets and throws "component already exists" on re-fit).
            VolumeProfile volume = ScriptableObject.CreateInstance<VolumeProfile>();
            volume.name = Path.GetFileNameWithoutExtension(path);

            Tonemapping tonemapping = volume.Add<Tonemapping>();
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;

            WhiteBalance whiteBalance = volume.Add<WhiteBalance>();
            whiteBalance.temperature.overrideState = true;
            whiteBalance.temperature.value = look.PostTemperature;
            whiteBalance.tint.overrideState = true;
            whiteBalance.tint.value = look.PostTint;

            ColorAdjustments colorAdjustments = volume.Add<ColorAdjustments>();
            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = look.PostExposureEv;
            colorAdjustments.contrast.overrideState = true;
            colorAdjustments.contrast.value = look.PostContrast;
            colorAdjustments.saturation.overrideState = true;
            colorAdjustments.saturation.value = look.PostSaturation;
            // colorFilter stays at its neutral-white default with overrideState false —
            // visibly neutral, not a fitted knob (UI-SPEC assembly table).

            Bloom bloom = volume.Add<Bloom>();
            bloom.threshold.overrideState = true;
            bloom.threshold.value = look.PostBloomThreshold;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = look.PostBloomIntensity;

            ColorCurves curves = volume.Add<ColorCurves>();
            curves.master.overrideState = true;
            curves.master.value = BuildTextureCurve(look.PostMasterCurve);
            curves.red.overrideState = true;
            curves.red.value = BuildTextureCurve(look.PostRedCurve);
            curves.green.overrideState = true;
            curves.green.value = BuildTextureCurve(look.PostGreenCurve);
            curves.blue.overrideState = true;
            curves.blue.value = BuildTextureCurve(look.PostBlueCurve);

            AssetDatabase.CreateAsset(volume, path);
            // Pitfall 1 (carried over): every component must be parented to the asset
            // file or it dangles and loads back null.
            AssetDatabase.AddObjectToAsset(tonemapping, volume);
            AssetDatabase.AddObjectToAsset(whiteBalance, volume);
            AssetDatabase.AddObjectToAsset(colorAdjustments, volume);
            AssetDatabase.AddObjectToAsset(bloom, volume);
            AssetDatabase.AddObjectToAsset(curves, volume);
            // WR-04 (carried over): persist ONLY this Volume — SaveAssets() flushes
            // every dirty asset in the project. SetDirty(look) below marks the asset
            // for the fingerprint write but never force-saves unrelated user work.
            EditorUtility.SetDirty(volume);
            AssetDatabase.SaveAssetIfDirty(volume);

            VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            AssetGenerator.Stamp(loaded);

            // A13 seam: record the params fingerprint at write time so IsVolumeStale
            // reads false immediately after a write and flips once any parameter or
            // curve key drifts. The look asset may be an in-memory instance (tests)
            // or a loaded project asset (Fit Look / Regenerate Volume).
            look.VolumeWriteFingerprint = look.ComputeParamsFingerprint();
            EditorUtility.SetDirty(look);
            return loaded;
        }

        /// <summary>
        /// Converts one asset-side <see cref="AnimationCurve"/> into a URP
        /// <see cref="TextureCurve"/> on the [0,1] diagonal bounds. Curves exceeding
        /// <see cref="NamerEditorConstants.MaxCurveKeys"/> keys are resampled to
        /// MaxCurveKeys uniform-grid keys first (deterministic — AnimationCurve.Evaluate
        /// is pure); TextureCurve bakes to a 128-texel texture, so more keys buy nothing.
        /// </summary>
        private static TextureCurve BuildTextureCurve(AnimationCurve curve)
        {
            Keyframe[] keys = curve.keys;
            if (keys.Length > NamerEditorConstants.MaxCurveKeys)
            {
                int keyCount = NamerEditorConstants.MaxCurveKeys;
                float step = 1f / (keyCount - 1);
                Keyframe[] resampled = new Keyframe[keyCount];
                for (int i = 0; i < keyCount; i++)
                {
                    float time = i * step;
                    resampled[i] = new Keyframe(time, curve.Evaluate(time));
                }

                keys = resampled;
            }

            return new TextureCurve(keys, 0f, false, new Vector2(0f, 1f));
        }
    }
}

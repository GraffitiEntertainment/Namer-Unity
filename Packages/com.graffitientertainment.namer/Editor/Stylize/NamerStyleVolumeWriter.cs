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
    /// <see cref="NamerStyleProfile"/> (05.1 STYL-07/STYL-08). D-02: the profile is the
    /// source of truth — this writer derives the Volume from it and re-derives it on
    /// every Fit Look; hand-tweaks made on the generated Volume are accepted-lost on
    /// re-fit. Every write rides the shared generated-asset discipline:
    /// <see cref="AssetGenerator.ValidateComposedPath"/> destination confinement,
    /// <see cref="AssetGenerator.EnsureWritableTarget"/> (NamerGenerated-label check;
    /// the user's overwrite opt-in deliberately does NOT gate this auto-derived
    /// companion — see <see cref="PreflightWriteGate"/>), and the
    /// <see cref="AssetGenerator.Stamp"/> that makes the fresh asset overwritable by
    /// the next fit. Ping feedback belongs to the call
    /// sites (Fit Look / Process), never here.
    /// </summary>
    public static class NamerStyleVolumeWriter
    {
        /// <summary>
        /// Composes the generated Volume path at the configured destination ROOT — never
        /// a per-selection subfolder, because the fit is profile-scoped and scene-wide
        /// (05.1 UI-SPEC section C):
        /// <c>{prefix}{SanitizeFileName(profile.name)}{suffix}_Post.asset</c>, the
        /// <c>_Post</c> channel suffix mirroring <c>_Base</c>/<c>_Surface</c>/<c>_Residual</c>.
        /// </summary>
        public static string ComposeVolumePath(NamerStyleProfile profile, NamerProcessorSettings settings)
        {
            string prefix = settings.Prefix ?? string.Empty;
            string suffix = settings.Suffix ?? string.Empty;
            return settings.Destination + prefix + AssetGenerator.SanitizeFileName(profile.name) + suffix + "_Post.asset";
        }

        /// <summary>
        /// CR-01 (05.1 review): the write gates <see cref="Write"/> will run, evaluated
        /// WITHOUT writing — destination confinement plus the NamerGenerated-label
        /// check. The Fit Look transaction calls this BEFORE mutating the profile, so a
        /// refusal leaves the profile untouched instead of committing a fit whose
        /// companion Volume then fails to regenerate (profile/Volume desync, and a
        /// "Fit failed" report after the fit itself was committed). The user's
        /// generated-asset overwrite opt-in (GEN-03) deliberately does not gate this
        /// asset: the companion Volume is auto-derived from profile truth (D-02) and
        /// the Fit Look contract promises refit-overwrite unconditionally (the P17
        /// tooltip and USER_GUIDE both state it), so the opt-in would refuse every
        /// second Fit Look — and every Process-time regeneration — on default
        /// settings. Foreign (non-generated) assets at the path are still refused.
        /// </summary>
        internal static void PreflightWriteGate(NamerStyleProfile profile, NamerProcessorSettings settings)
        {
            string path = ComposeVolumePath(profile, settings);
            AssetGenerator.ValidateComposedPath(path, settings.Destination);
            AssetGenerator.EnsureWritableTarget(path, overwriteGenerated: true);
        }

        /// <summary>
        /// Generates the VolumeProfile asset for a fitted profile and returns the loaded,
        /// NamerGenerated-stamped asset. Throws <see cref="InvalidOperationException"/>
        /// when the profile is null or unfitted, when the composed path escapes the
        /// configured destination, or when a non-generated asset sits at the target
        /// path (the overwrite opt-in does not gate this companion Volume —
        /// <see cref="PreflightWriteGate"/>; the refusal text flows to the
        /// "Fit failed: {message}" HelpBox in plan 03).
        /// </summary>
        public static VolumeProfile Write(NamerStyleProfile profile, NamerProcessorSettings settings)
        {
            if (profile == null || !profile.HasPostFit)
            {
                throw new InvalidOperationException(
                    "Refusing to generate the post Volume: profile '"
                    + (profile != null ? profile.name : "null")
                    + "' carries no fitted post look. Run Fit Look first.");
            }

            // Threat T-05.1-02-03: hand-edited serialized fit values are sanitized
            // before any parameter is read.
            profile.ClampToCaps();

            PreflightWriteGate(profile, settings);

            string path = ComposeVolumePath(profile, settings);

            // Pitfall 10: a FRESH profile instance every write — never Add into a
            // loaded stale profile (that both orphans old TextureCurve sub-assets and
            // throws "component already exists" on re-fit).
            VolumeProfile volume = ScriptableObject.CreateInstance<VolumeProfile>();
            volume.name = Path.GetFileNameWithoutExtension(path);

            Tonemapping tonemapping = volume.Add<Tonemapping>();
            tonemapping.mode.overrideState = true;
            tonemapping.mode.value = TonemappingMode.ACES;

            WhiteBalance whiteBalance = volume.Add<WhiteBalance>();
            whiteBalance.temperature.overrideState = true;
            whiteBalance.temperature.value = profile.PostTemperature;
            whiteBalance.tint.overrideState = true;
            whiteBalance.tint.value = profile.PostTint;

            ColorAdjustments colorAdjustments = volume.Add<ColorAdjustments>();
            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = profile.PostExposureEv;
            colorAdjustments.contrast.overrideState = true;
            colorAdjustments.contrast.value = profile.PostContrast;
            colorAdjustments.saturation.overrideState = true;
            colorAdjustments.saturation.value = profile.PostSaturation;
            // colorFilter stays at its neutral-white default with overrideState false —
            // visibly neutral, not a fitted knob (UI-SPEC assembly table).

            Bloom bloom = volume.Add<Bloom>();
            bloom.threshold.overrideState = true;
            bloom.threshold.value = profile.PostBloomThreshold;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = NamerEditorConstants.DefaultBloomIntensity;

            ColorCurves curves = volume.Add<ColorCurves>();
            curves.master.overrideState = true;
            curves.master.value = BuildTextureCurve(profile.PostMasterCurve);
            curves.red.overrideState = true;
            curves.red.value = BuildTextureCurve(profile.PostRedCurve);
            curves.green.overrideState = true;
            curves.green.value = BuildTextureCurve(profile.PostGreenCurve);
            curves.blue.overrideState = true;
            curves.blue.value = BuildTextureCurve(profile.PostBlueCurve);

            AssetDatabase.CreateAsset(volume, path);
            // Pitfall 1: every component must be parented to the asset file or it
            // dangles and loads back null.
            AssetDatabase.AddObjectToAsset(tonemapping, volume);
            AssetDatabase.AddObjectToAsset(whiteBalance, volume);
            AssetDatabase.AddObjectToAsset(colorAdjustments, volume);
            AssetDatabase.AddObjectToAsset(bloom, volume);
            AssetDatabase.AddObjectToAsset(curves, volume);
            AssetDatabase.SaveAssets();

            VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            AssetGenerator.Stamp(loaded);
            return loaded;
        }

        /// <summary>
        /// Converts one profile-side <see cref="AnimationCurve"/> into a URP
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

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Assembles the generated URP Volume asset from a fitted post source — either
    /// the standalone <see cref="NamerPostLook"/> (05.2 D-06) or the merged
    /// <see cref="NamerStyleAsset"/> post block (05.3 STYL-15 carry-over; the
    /// ASSET is the source of truth in both cases — this writer derives the Volume
    /// from it and re-derives it on every fit / Decompose / Regenerate Volume;
    /// hand-tweaks made on the generated Volume are accepted-lost on
    /// re-fit/regeneration). Asset-only since 05.2 plan 06 (STYL-10): every write
    /// rides the shared generated-asset discipline:
    /// <see cref="AssetGenerator.ValidateComposedPath"/> destination confinement,
    /// <see cref="AssetGenerator.EnsureWritableTarget"/> (NamerGenerated-label
    /// check with the own-companion exemption — see
    /// <see cref="PreflightWriteGate(NamerPostLook, NamerProcessorSettings)"/>),
    /// and the <see cref="AssetGenerator.Stamp"/> that makes the fresh asset
    /// overwritable by the next regeneration. The Volume-assembly body lives
    /// exactly once, in <see cref="WriteVolumeAtPath"/> — both public overloads
    /// are thin type-specific shells over it (05.3-02 extraction; the NamerPostLook
    /// overload's behavior is unchanged). Ping feedback belongs to the call sites
    /// (Fit Look / Decompose / Regenerate Volume), never here.
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
            return ComposeVolumePathFromName(look.name, settings);
        }

        /// <summary>
        /// Composes the generated Volume path for a merged style asset at the
        /// configured destination ROOT (05.3 STYL-15; 05.2 D-06 carry-over — the
        /// unchanged name-keyed formula over the MERGED asset's name):
        /// <c>{prefix}{SanitizeFileName(asset.name)}{suffix}_Post.asset</c>. A merged
        /// asset therefore owns a Volume at a DIFFERENT path than the source look's
        /// look-keyed Volume: the old look-keyed Volume orphans in place by design
        /// (05.2 R8 precedent — orphaned Volumes are never deleted silently), and the
        /// per-scene duplicate-and-tweak workflow rides the merged asset's naming.
        /// </summary>
        public static string ComposeVolumePath(NamerStyleAsset asset, NamerProcessorSettings settings)
        {
            return ComposeVolumePathFromName(asset.name, settings);
        }

        /// <summary>
        /// The name-keyed GEN-01 path formula shared by both source types:
        /// <c>{prefix}{SanitizeFileName(name)}{suffix}_Post.asset</c>.
        /// </summary>
        private static string ComposeVolumePathFromName(string assetName, NamerProcessorSettings settings)
        {
            string prefix = settings.Prefix ?? string.Empty;
            string suffix = settings.Suffix ?? string.Empty;
            return settings.Destination + prefix + AssetGenerator.SanitizeFileName(assetName) + suffix + "_Post.asset";
        }

        /// <summary>
        /// CR-01 gate discipline carried over verbatim from the profile path (05.2 D-06):
        /// the write gates <see cref="Write(NamerPostLook, NamerProcessorSettings)"/>
        /// will run, evaluated WITHOUT writing — destination-folder existence
        /// (ensure-not-create: the gate refuses, it never creates the folder),
        /// destination confinement
        /// (<see cref="AssetGenerator.ValidateComposedPath"/>) plus the
        /// NamerGenerated-label check with the own-companion exemption
        /// (<see cref="AssetGenerator.EnsureWritableTarget"/>, overwriteGenerated:
        /// true). The Fit Look / Regenerate Volume transactions call this BEFORE
        /// mutating the asset, so a refusal leaves the asset untouched.
        /// </summary>
        internal static void PreflightWriteGate(NamerPostLook look, NamerProcessorSettings settings)
        {
            PreflightWriteGateAtPath(ComposeVolumePath(look, settings), settings);
        }

        /// <summary>
        /// The 05.2 CR-01 gate discipline over the merged style asset's composed
        /// Volume path (05.3 STYL-15 carry-over — same refusal set as
        /// <see cref="PreflightWriteGate(NamerPostLook, NamerProcessorSettings)"/>).
        /// The Decompose / Regenerate Volume transactions call this BEFORE mutating
        /// the asset, so a refusal leaves the asset untouched.
        /// </summary>
        internal static void PreflightWriteGate(NamerStyleAsset asset, NamerProcessorSettings settings)
        {
            PreflightWriteGateAtPath(ComposeVolumePath(asset, settings), settings);
        }

        /// <summary>
        /// The shared gate body: destination-folder existence (refuses, never
        /// creates), destination confinement, and the NamerGenerated-label
        /// overwrite check with the own-companion exemption.
        /// </summary>
        private static void PreflightWriteGateAtPath(string path, NamerProcessorSettings settings)
        {
            // CR-01 (05.2): a destination folder that legitimately does not exist
            // (renamed/deleted folder, restored prefs on a new machine) makes
            // CreateAsset log "Couldn't create asset file" WITHOUT throwing —
            // refuse up front instead, before any fit mutates the asset.
            // "Assets"/empty always exist, mirroring NamerProcessor.EnsureFolder's
            // own special case (IsValidFolder answers only for sub-folders).
            string destinationFolder = settings.Destination.TrimEnd('/', '\\');
            bool folderMissing = !string.IsNullOrEmpty(destinationFolder)
                && destinationFolder != "Assets"
                && !AssetDatabase.IsValidFolder(destinationFolder);
            if (folderMissing)
            {
                throw new InvalidOperationException(
                    "Refusing to generate the post Volume: the destination folder '"
                    + destinationFolder + "' does not exist. "
                    + "Create it (or run Process once) and retry.");
            }

            AssetGenerator.ValidateComposedPath(path, settings.Destination);
            AssetGenerator.EnsureWritableTarget(path, overwriteGenerated: true);
        }

        /// <summary>
        /// Generates the VolumeProfile asset for a fitted post-look asset and returns
        /// the loaded, NamerGenerated-stamped asset (05.2 D-06: the ASSET is the source
        /// of truth — this writer derives the Volume from it; hand-tweaks made on the
        /// generated Volume are accepted-lost on re-fit/regeneration). Throws
        /// <see cref="InvalidOperationException"/> when the asset is null or unfitted,
        /// when the destination folder does not exist, when the composed path escapes
        /// the configured destination, or when a non-generated asset sits at the
        /// target path (carried-over profile-path gates:
        /// <see cref="PreflightWriteGate(NamerPostLook, NamerProcessorSettings)"/>),
        /// and when the generated VolumeProfile fails to persist (verified BEFORE
        /// the A13 fingerprint stamp so staleness never reports a phantom write).
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

            return WriteVolumeAtPath(
                ComposeVolumePath(look, settings),
                look.PostTemperature, look.PostTint, look.PostExposureEv,
                look.PostContrast, look.PostSaturation,
                look.PostBloomThreshold, look.PostBloomIntensity,
                look.PostMasterCurve, look.PostRedCurve, look.PostGreenCurve, look.PostBlueCurve,
                () =>
                {
                    look.VolumeWriteFingerprint = look.ComputeParamsFingerprint();
                    EditorUtility.SetDirty(look);
                });
        }

        /// <summary>
        /// Generates the VolumeProfile asset for a merged style asset's fitted post
        /// block and returns the loaded, NamerGenerated-stamped asset (05.3 STYL-15;
        /// 05.2 D-06 carry-over — the merged ASSET is the source of truth, this
        /// writer derives the Volume from it). Same refusal set as
        /// <see cref="Write(NamerPostLook, NamerProcessorSettings)"/> (null or
        /// unfitted asset, missing destination folder, escaping path, non-generated
        /// target, failed persistence — verified BEFORE the fingerprint stamp),
        /// the same full-strength parameter law (05.2 R7), and the same
        /// name-keyed path formula keyed on the MERGED asset's name (see
        /// <see cref="ComposeVolumePath(NamerStyleAsset, NamerProcessorSettings)"/>
        /// — the source look's old look-keyed Volume orphans in place, 05.2 R8).
        /// After the stamp, the write records
        /// <see cref="NamerStyleAsset.VolumeWriteFingerprint"/> from the asset's
        /// <see cref="NamerStyleAsset.ComputeParamsFingerprint"/> so the volume
        /// staleness seam starts honest.
        /// </summary>
        public static VolumeProfile Write(NamerStyleAsset asset, NamerProcessorSettings settings)
        {
            if (asset == null || !asset.HasFit)
            {
                throw new InvalidOperationException(
                    "Refusing to generate the post Volume: style asset '"
                    + (asset != null ? asset.name : "null")
                    + "' carries no fitted post look. Run Decompose Style first.");
            }

            // Threat T-05.3-02-01: hand-edited serialized values are sanitized
            // before any parameter is read.
            asset.ClampToCaps();

            PreflightWriteGate(asset, settings);

            return WriteVolumeAtPath(
                ComposeVolumePath(asset, settings),
                asset.PostTemperature, asset.PostTint, asset.PostExposureEv,
                asset.PostContrast, asset.PostSaturation,
                asset.PostBloomThreshold, asset.PostBloomIntensity,
                asset.PostMasterCurve, asset.PostRedCurve, asset.PostGreenCurve, asset.PostBlueCurve,
                () =>
                {
                    asset.VolumeWriteFingerprint = asset.ComputeParamsFingerprint();
                    EditorUtility.SetDirty(asset);
                });
        }

        /// <summary>
        /// The shared verified-write body (05.3-02 extraction — the ONE home of the
        /// Volume component wiring; both public Write overloads delegate here):
        /// a FRESH VolumeProfile per write, the Tonemapping(ACES)/White
        /// Balance/Color Adjustments/Bloom/Color Curves overrides from the Post*
        /// values, sub-asset parenting, SaveAssetIfDirty (never SaveAssets),
        /// persistence verification, the NamerGenerated stamp, and finally the
        /// caller's Volume-write fingerprint stamp — every failure-throwing step
        /// runs BEFORE the fingerprint stamp (CR-01 discipline), so staleness never
        /// reports a phantom write.
        /// </summary>
        private static VolumeProfile WriteVolumeAtPath(
            string path,
            float temperature,
            float tint,
            float exposureEv,
            float contrast,
            float saturation,
            float bloomThreshold,
            float bloomIntensity,
            AnimationCurve masterCurve,
            AnimationCurve redCurve,
            AnimationCurve greenCurve,
            AnimationCurve blueCurve,
            Action stampWriteFingerprint)
        {
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
            whiteBalance.temperature.value = temperature;
            whiteBalance.tint.overrideState = true;
            whiteBalance.tint.value = tint;

            ColorAdjustments colorAdjustments = volume.Add<ColorAdjustments>();
            colorAdjustments.postExposure.overrideState = true;
            colorAdjustments.postExposure.value = exposureEv;
            colorAdjustments.contrast.overrideState = true;
            colorAdjustments.contrast.value = contrast;
            colorAdjustments.saturation.overrideState = true;
            colorAdjustments.saturation.value = saturation;
            // colorFilter stays at its neutral-white default with overrideState false —
            // visibly neutral, not a fitted knob (UI-SPEC assembly table).

            Bloom bloom = volume.Add<Bloom>();
            bloom.threshold.overrideState = true;
            bloom.threshold.value = bloomThreshold;
            bloom.intensity.overrideState = true;
            bloom.intensity.value = bloomIntensity;

            ColorCurves curves = volume.Add<ColorCurves>();
            curves.master.overrideState = true;
            curves.master.value = BuildTextureCurve(masterCurve);
            curves.red.overrideState = true;
            curves.red.value = BuildTextureCurve(redCurve);
            curves.green.overrideState = true;
            curves.green.value = BuildTextureCurve(greenCurve);
            curves.blue.overrideState = true;
            curves.blue.value = BuildTextureCurve(blueCurve);

            AssetDatabase.CreateAsset(volume, path);
            // Pitfall 1 (carried over): every component must be parented to the asset
            // file or it dangles and loads back null.
            AssetDatabase.AddObjectToAsset(tonemapping, volume);
            AssetDatabase.AddObjectToAsset(whiteBalance, volume);
            AssetDatabase.AddObjectToAsset(colorAdjustments, volume);
            AssetDatabase.AddObjectToAsset(bloom, volume);
            AssetDatabase.AddObjectToAsset(curves, volume);
            // WR-04 (carried over): persist ONLY this Volume — SaveAssets() flushes
            // every dirty asset in the project. The caller's fingerprint stamp marks
            // the source asset for its own write but never force-saves unrelated
            // user work.
            EditorUtility.SetDirty(volume);
            AssetDatabase.SaveAssetIfDirty(volume);

            VolumeProfile loaded = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            // CR-01 (05.2): CreateAsset logs "Couldn't create asset file" WITHOUT
            // throwing when the write fails, so verify the asset actually
            // persisted BEFORE stamping — otherwise the fingerprint below would
            // mark a Volume that does not exist as fresh, suppressing the A13
            // staleness warning while the callers' locked HelpBox stays silent.
            if (loaded == null)
            {
                throw new InvalidOperationException(
                    "Failed to generate the post Volume at '" + path
                    + "'. Ensure the destination folder exists (run Process once) and retry.");
            }
            AssetGenerator.Stamp(loaded);

            stampWriteFingerprint();
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

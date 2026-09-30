using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Standalone post-look asset (05.2 STYL-10/STYL-12, D-03): the new home of the
    /// reference-fitted post look, moved off <see cref="NamerStyleProfile"/>. The asset
    /// owns its own reference-image list (D-05 — graded screenshots with bloom and all,
    /// decoupled from the style profile's palette references), the fitted-post block
    /// re-homed field-for-field from the 5.1 profile, and the bloom-intensity artist
    /// knob moved home from the generated Volume (UI-SPEC A9). The asset is the source
    /// of truth; its URP Volume profile derives and re-derives from it (D-06) via
    /// <see cref="NamerStyleVolumeWriter.Write(NamerPostLook, NamerProcessorSettings)"/>.
    /// An unfitted/neutral asset is identity post (UI-SPEC section C defaults law):
    /// zero white balance, zero EV, unity contrast/saturation, URP-default bloom
    /// threshold/intensity, the neutral curve diagonal. Partial: the Changed raise
    /// helper lands beside its only caller in <c>Editor/UI/NamerPostLookEditor.cs</c>
    /// (plan 05) — C# events can only be raised from the declaring type.
    /// </summary>
    public sealed partial class NamerPostLook : ScriptableObject
    {
        /// <summary>Reference image slots; starts EMPTY, 0-10 entries (D-05 own-list law, UI-SPEC A2).</summary>
        public List<Texture2D> References = new List<Texture2D>();

        /// <summary>Per-slot fit weights, parallel to <see cref="References"/> (D-05 Advanced; default 1).</summary>
        public List<float> ReferenceWeights = new List<float>();

        // Fitted post look (05.1 D-02 block re-homed verbatim, 05.2 D-03; written only
        // by Fit Look — the asset is the source of truth, the generated Volume
        // derives). An unfitted/neutral fit is identity post: zero white balance,
        // zero EV, unity contrast/saturation, the neutral curve diagonal. Old 5.1
        // profile data migrates here via the plan-05 load-time migration.

        /// <summary>Fitted white-balance temperature in [-100,100] percent (05.1 D-02; written only by Fit Look).</summary>
        public float PostTemperature = 0f;

        /// <summary>Fitted white-balance tint in [-100,100] percent (05.1 D-02; written only by Fit Look).</summary>
        public float PostTint = 0f;

        /// <summary>Fitted post-exposure in EV stops (05.1 D-02; written only by Fit Look; sanitized to ±MaxFittedPostExposureEv).</summary>
        public float PostExposureEv = 0f;

        /// <summary>Fitted contrast in [-100,100] percent (05.1 D-02; written only by Fit Look).</summary>
        public float PostContrast = 0f;

        /// <summary>Fitted saturation in [-100,100] percent (05.1 D-02; written only by Fit Look).</summary>
        public float PostSaturation = 0f;

        /// <summary>Fitted bloom threshold, gamma-space like URP's own slider (05.1 STYL-08; default = URP Bloom's 0.9).</summary>
        public float PostBloomThreshold = NamerEditorConstants.DefaultBloomThreshold;

        /// <summary>
        /// Bloom intensity artist knob (05.2 UI-SPEC A9; moved home from the generated
        /// Volume). NEVER written by the fit — tweakable on the asset, clamped to
        /// [0, <see cref="NamerEditorConstants.MaxBloomIntensity"/>] by
        /// <see cref="ClampToCaps"/>, default <see cref="NamerEditorConstants.DefaultBloomIntensity"/>.
        /// </summary>
        public float PostBloomIntensity = NamerEditorConstants.DefaultBloomIntensity;

        /// <summary>Fitted master luma curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostMasterCurve = CreateNeutralCurve();

        /// <summary>Fitted red channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostRedCurve = CreateNeutralCurve();

        /// <summary>Fitted green channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostGreenCurve = CreateNeutralCurve();

        /// <summary>Fitted blue channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostBlueCurve = CreateNeutralCurve();

        /// <summary>Fingerprint of the refs + weights the last Fit Look consumed (05.1 D-02 shape, over the ASSET's own references; written only by Fit Look).</summary>
        public string PostFitFingerprint = "";

        /// <summary>Display name of the exemplar object the last Fit Look was fitted from (05.1 D-03; written only by Fit Look).</summary>
        public string PostFitExemplarName = "";

        /// <summary>
        /// Params fingerprint captured at the last Volume write (05.2 UI-SPEC A13):
        /// <see cref="ComputeParamsFingerprint"/> stamped by
        /// <see cref="NamerStyleVolumeWriter.Write(NamerPostLook, NamerProcessorSettings)"/>
        /// so <see cref="IsVolumeStale"/> starts honest — false immediately after a
        /// write, true once any parameter or curve key drifts.
        /// </summary>
        public string VolumeWriteFingerprint = "";

        /// <summary>
        /// Raised by <see cref="NamerPostLookEditor"/> after every value edit (05.2
        /// plan 05; mirrors <see cref="NamerStyleProfile.Changed"/>). The Processor
        /// window subscribes while this asset is selected and routes it through its
        /// existing debounced preview recompute; this event NEVER triggers a fit
        /// (explicit-action law).
        /// </summary>
        public static event Action<NamerPostLook> Changed;

        /// <summary>Number of non-null reference slots (null slots are skipped everywhere).</summary>
        public int AssignedReferenceCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < References.Count; i++)
                {
                    if (References[i] != null)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <summary>
        /// True once a Fit Look has written a fit fingerprint onto the asset (05.1
        /// D-02 shape). An asset with no serialized fit data reports false — fresh
        /// assets and pre-migration state take this path.
        /// </summary>
        public bool HasFit
        {
            get { return !string.IsNullOrEmpty(PostFitFingerprint); }
        }

        /// <summary>
        /// True when the current refs + weights no longer match the fitted
        /// <see cref="PostFitFingerprint"/> (05.2 UI-SPEC A10: drives the fit-staleness
        /// warning). Only meaningful once fitted.
        /// </summary>
        public bool IsFitStale
        {
            get { return HasFit && ComputeFingerprint() != PostFitFingerprint; }
        }

        /// <summary>
        /// True when any Volume-relevant parameter drifted since the last Volume
        /// write (05.2 UI-SPEC A13: drives the volume-staleness warning). False
        /// until a fit exists or while the last-written params fingerprint still
        /// matches <see cref="ComputeParamsFingerprint"/>.
        /// </summary>
        public bool IsVolumeStale
        {
            get { return HasFit && VolumeWriteFingerprint != ComputeParamsFingerprint(); }
        }

        /// <summary>
        /// Identity string of the assigned refs' asset GUIDs plus their weights, pipe
        /// separated (05.1 shape, over the ASSET's own reference list). Null slots
        /// are skipped. This is an identity string, not a security hash.
        /// Deterministic: two calls on the same state return equal strings.
        /// </summary>
        public string ComputeFingerprint()
        {
            StringBuilder builder = new StringBuilder();
            for (int i = 0; i < References.Count; i++)
            {
                Texture2D reference = References[i];
                if (reference == null)
                {
                    continue;
                }

                float weight = i < ReferenceWeights.Count ? ReferenceWeights[i] : NamerEditorConstants.DefaultReferenceWeight;
                if (builder.Length > 0)
                {
                    builder.Append('|');
                }

                string assetPath = AssetDatabase.GetAssetPath(reference);
                builder.Append(AssetDatabase.AssetPathToGUID(assetPath));
                builder.Append(':');
                builder.Append(weight.ToString("R", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        /// <summary>
        /// Identity string of every Volume-relevant parameter (05.2 A13): the six
        /// fitted floats plus the bloom-intensity artist knob and the time/value
        /// keys of all four curves, pipe separated with invariant round-trip
        /// formatting. Deterministic: two calls on the same state return equal
        /// strings. Curve tangents are deliberately excluded — the Volume bakes
        /// curves through <see cref="AnimationCurve.Evaluate"/>, and the 05.2
        /// UI contract keys staleness on time/value edits only.
        /// </summary>
        internal string ComputeParamsFingerprint()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(PostTemperature.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostTint.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostExposureEv.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostContrast.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostSaturation.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostBloomThreshold.ToString("R", CultureInfo.InvariantCulture));
            builder.Append('|');
            builder.Append(PostBloomIntensity.ToString("R", CultureInfo.InvariantCulture));
            AppendCurveKeys(builder, PostMasterCurve);
            AppendCurveKeys(builder, PostRedCurve);
            AppendCurveKeys(builder, PostGreenCurve);
            AppendCurveKeys(builder, PostBlueCurve);
            return builder.ToString();
        }

        /// <summary>
        /// Sanitizes a hand-edited serialized asset (T-05.2-01-01): truncates
        /// <see cref="References"/> to <see cref="NamerEditorConstants.MaxReferences"/>,
        /// syncs <see cref="ReferenceWeights"/> to the reference count, clamps every
        /// weight to [0,1], and replaces non-numeric weights with the default weight.
        /// Sanitizes the fitted-post block with the NaN-then-clamp discipline (NaN
        /// falls back to the neutral identity BEFORE clamping — Mathf.Clamp
        /// propagates NaN) and clamps the bloom-intensity artist knob to
        /// [0, <see cref="NamerEditorConstants.MaxBloomIntensity"/>]. Consumed at
        /// Volume-write time and by the asset editor (plan 05).
        /// </summary>
        public void ClampToCaps()
        {
            if (References.Count > NamerEditorConstants.MaxReferences)
            {
                References.RemoveRange(NamerEditorConstants.MaxReferences, References.Count - NamerEditorConstants.MaxReferences);
            }

            while (ReferenceWeights.Count < References.Count)
            {
                ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
            }

            if (ReferenceWeights.Count > References.Count)
            {
                ReferenceWeights.RemoveRange(References.Count, ReferenceWeights.Count - References.Count);
            }

            for (int i = 0; i < ReferenceWeights.Count; i++)
            {
                float weight = ReferenceWeights[i];
                if (float.IsNaN(weight))
                {
                    weight = NamerEditorConstants.DefaultReferenceWeight;
                }

                ReferenceWeights[i] = Mathf.Clamp(weight, 0f, 1f);
            }

            // Fitted-post block (05.1 plan 02 discipline re-homed verbatim; the
            // retired 05.1 tone-scaling sanitize is deliberately absent — 05.2
            // STYL-11 removed that knob entirely).
            // NaN falls back to the neutral identity (0), except the bloom floats,
            // which fall back to their URP-default constants.
            if (float.IsNaN(PostTemperature))
            {
                PostTemperature = 0f;
            }

            PostTemperature = Mathf.Clamp(PostTemperature, -100f, 100f);

            if (float.IsNaN(PostTint))
            {
                PostTint = 0f;
            }

            PostTint = Mathf.Clamp(PostTint, -100f, 100f);

            if (float.IsNaN(PostExposureEv))
            {
                PostExposureEv = 0f;
            }

            PostExposureEv = Mathf.Clamp(PostExposureEv, -NamerEditorConstants.MaxFittedPostExposureEv, NamerEditorConstants.MaxFittedPostExposureEv);

            if (float.IsNaN(PostContrast))
            {
                PostContrast = 0f;
            }

            PostContrast = Mathf.Clamp(PostContrast, -100f, 100f);

            if (float.IsNaN(PostSaturation))
            {
                PostSaturation = 0f;
            }

            PostSaturation = Mathf.Clamp(PostSaturation, -100f, 100f);

            if (float.IsNaN(PostBloomThreshold))
            {
                PostBloomThreshold = NamerEditorConstants.DefaultBloomThreshold;
            }

            PostBloomThreshold = Mathf.Clamp(PostBloomThreshold, 0f, NamerEditorConstants.MaxBloomThreshold);

            // The artist knob (A9): same NaN-then-clamp discipline, identity
            // fallback DefaultBloomIntensity, cap MaxBloomIntensity.
            if (float.IsNaN(PostBloomIntensity))
            {
                PostBloomIntensity = NamerEditorConstants.DefaultBloomIntensity;
            }

            PostBloomIntensity = Mathf.Clamp(PostBloomIntensity, 0f, NamerEditorConstants.MaxBloomIntensity);

            SanitizeCurve(ref PostMasterCurve);
            SanitizeCurve(ref PostRedCurve);
            SanitizeCurve(ref PostGreenCurve);
            SanitizeCurve(ref PostBlueCurve);
        }

        /// <summary>
        /// Neutral identity curve for the fitted-post block: the two-key diagonal
        /// (0,0)→(1,1). Shared by the field initializers and the
        /// <see cref="ClampToCaps"/> reset paths so a fresh asset and a sanitized
        /// one agree on what "neutral" means.
        /// </summary>
        private static AnimationCurve CreateNeutralCurve()
        {
            return new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
        }

        /// <summary>
        /// Sanitizes one fitted-post curve (05.1 plan 02 discipline re-homed): null
        /// or degenerate (&lt;2-key) curves reset to the neutral diagonal; otherwise
        /// every key's time and value is clamped into [0,1]. A NaN in any key's time
        /// or value resets the whole curve to the neutral diagonal — Mathf.Clamp
        /// propagates NaN, so a clamp cannot repair it. Unity's AnimationCurve
        /// constructor silently drops NaN keys, so a NaN authored into a curve often
        /// surfaces here as a short curve rather than as a NaN.
        /// </summary>
        private static void SanitizeCurve(ref AnimationCurve curve)
        {
            if (curve == null || curve.length < 2)
            {
                curve = CreateNeutralCurve();
                return;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (float.IsNaN(keys[i].time) || float.IsNaN(keys[i].value))
                {
                    curve = CreateNeutralCurve();
                    return;
                }

                keys[i].time = Mathf.Clamp(keys[i].time, 0f, 1f);
                keys[i].value = Mathf.Clamp(keys[i].value, 0f, 1f);
            }

            curve.keys = keys;
        }

        /// <summary>
        /// Appends one curve's time/value keys to the params fingerprint as a
        /// semicolon-separated <c>time,value</c> segment behind a pipe (05.2 A13).
        /// A null curve appends an empty segment — null and 0-key curves collide
        /// deliberately: both are pre-sanitization states ClampToCaps maps to the
        /// same neutral diagonal.
        /// </summary>
        private static void AppendCurveKeys(StringBuilder builder, AnimationCurve curve)
        {
            builder.Append('|');
            if (curve == null)
            {
                return;
            }

            Keyframe[] keys = curve.keys;
            for (int i = 0; i < keys.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(';');
                }

                builder.Append(keys[i].time.ToString("R", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(keys[i].value.ToString("R", CultureInfo.InvariantCulture));
            }
        }
    }
}

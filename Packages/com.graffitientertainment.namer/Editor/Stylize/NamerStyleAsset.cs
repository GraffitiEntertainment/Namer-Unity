using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Merged style asset (05.3 STYL-15, D-05 — SUPERSEDES the 05.2 D-03/D-05
    /// two-asset/two-list split): ONE ScriptableObject with ONE shared weighted
    /// reference list feeding BOTH derived blocks — the palette block (HSL anchors
    /// from the de-graded base colors) and the fitted post block (from the grade
    /// estimate). Creation is menu-only at Assets/Create/NAMER/Style (D-06/M1 —
    /// the Processor panel only loads/uses the asset). Decomposition NEVER
    /// auto-runs (M3 law): a migrated asset honestly reads "not decomposed" until
    /// the user presses Decompose Style. The three state dimensions — decomposed,
    /// fitted, palette-present — are INDEPENDENT (05.2 D-07 no-silent-loss applies
    /// to the merge: a migrated asset loses neither palette nor fit). The post
    /// block re-homes the <see cref="NamerPostLook"/> fields field-for-field; the
    /// asset is the source of truth and its URP Volume profile derives from it via
    /// <see cref="NamerStyleVolumeWriter.Write(NamerStyleAsset, NamerProcessorSettings)"/>
    /// (05.2 D-06 carry-over). Partial: the Changed raise helper lands beside its
    /// only caller in the asset editor (plan 06) — C# events can only be raised
    /// from the declaring type.
    /// </summary>
    public sealed partial class NamerStyleAsset : ScriptableObject
    {
        // -- Shared weighted reference list (D-05: ONE list feeds both blocks) ------

        /// <summary>Reference image slots; starts EMPTY, 0-10 entries — the ONE shared list (D-05).</summary>
        public List<Texture2D> References = new List<Texture2D>();

        /// <summary>Per-slot weights, parallel to <see cref="References"/> (D-05 Advanced; default 1).</summary>
        public List<float> ReferenceWeights = new List<float>();

        // -- Fitted post block (NamerPostLook donor, field-for-field; 05.2 D-03
        // re-homed onto the merged asset — written only by the decomposition's fit
        // stage / hand edits in the asset inspector; the generated Volume derives).

        /// <summary>Fitted white-balance temperature in [-100,100] percent.</summary>
        public float PostTemperature = 0f;

        /// <summary>Fitted white-balance tint in [-100,100] percent.</summary>
        public float PostTint = 0f;

        /// <summary>Fitted post-exposure in EV stops (sanitized to ±MaxFittedPostExposureEv).</summary>
        public float PostExposureEv = 0f;

        /// <summary>Fitted contrast in [-100,100] percent.</summary>
        public float PostContrast = 0f;

        /// <summary>Fitted saturation in [-100,100] percent.</summary>
        public float PostSaturation = 0f;

        /// <summary>Fitted bloom threshold, gamma-space like URP's own slider (default = URP Bloom's 0.9).</summary>
        public float PostBloomThreshold = NamerEditorConstants.DefaultBloomThreshold;

        /// <summary>
        /// Bloom intensity artist knob (05.2 A9). NEVER written by a fit — tweakable
        /// on the asset, clamped to [0, <see cref="NamerEditorConstants.MaxBloomIntensity"/>
        /// by <see cref="ClampToCaps"/>.
        /// </summary>
        public float PostBloomIntensity = NamerEditorConstants.DefaultBloomIntensity;

        /// <summary>Fitted master luma curve on the neutral [0,1] diagonal.</summary>
        public AnimationCurve PostMasterCurve = CreateNeutralCurve();

        /// <summary>Fitted red channel curve on the neutral [0,1] diagonal.</summary>
        public AnimationCurve PostRedCurve = CreateNeutralCurve();

        /// <summary>Fitted green channel curve on the neutral [0,1] diagonal.</summary>
        public AnimationCurve PostGreenCurve = CreateNeutralCurve();

        /// <summary>Fitted blue channel curve on the neutral [0,1] diagonal.</summary>
        public AnimationCurve PostBlueCurve = CreateNeutralCurve();

        /// <summary>Fingerprint of the refs + weights the last fit consumed (over the SHARED list).</summary>
        public string PostFitFingerprint = "";

        /// <summary>Display name of the exemplar the last fit was fitted from.</summary>
        public string PostFitExemplarName = "";

        /// <summary>
        /// Params fingerprint captured at the last Volume write (05.2 A13 shape):
        /// stamped by <see cref="NamerStyleVolumeWriter.Write(NamerStyleAsset, NamerProcessorSettings)"/>
        /// so <see cref="IsVolumeStale"/> starts honest.
        /// </summary>
        public string VolumeWriteFingerprint = "";

        // -- Palette block (NamerStyleProfile donor verbatim) -----------------------

        /// <summary>Extracted palette anchors (written only by the decomposition's extract stage).</summary>
        public List<NamerStyleAnchor> PaletteAnchors = new List<NamerStyleAnchor>();

        /// <summary>Extracted merged statistics (written only by the decomposition's extract stage).</summary>
        public NamerStyleStats Stats = new NamerStyleStats();

        /// <summary>Fingerprint of the refs + weights the last palette extraction consumed (over the SHARED list).</summary>
        public string ReferenceFingerprint = "";

        /// <summary>
        /// Palette source the CURRENT palette was extracted from (S12 AtExtract
        /// twin): <see cref="NamerEditorConstants.kPaletteSourceDeGraded"/> or
        /// <see cref="NamerEditorConstants.kPaletteSourceRaw"/>. Kept separate from
        /// <see cref="PaletteSource"/> so <see cref="IsPaletteSourceStale"/> can
        /// detect the pending-switch state.
        /// </summary>
        public int PaletteSourceAtExtract = NamerEditorConstants.kPaletteSourceDeGraded;

        /// <summary>
        /// Palette source input flag for the NEXT decomposition (S12 popup; D-04
        /// switch — never a silent re-extraction). Differs from
        /// <see cref="PaletteSourceAtExtract"/> after a user flip.
        /// </summary>
        public int PaletteSource = NamerEditorConstants.kPaletteSourceDeGraded;

        // -- Palette-application sliders (NamerStyleProfile donor, same Ranges/defaults)

        /// <summary>Style strength, 0 = original, 1 = full palette snap (D-04).</summary>
        [Range(0f, 1f)] public float StyleStrength = NamerEditorConstants.DefaultStyleStrength;

        /// <summary>Island-gated bilateral smoothing strength (D-03).</summary>
        [Range(0f, 1f)] public float Smoothing = NamerEditorConstants.DefaultSmoothingStrength;

        /// <summary>Cavity floor applied after AO contrast, keeps cavities from crushing to black (D-08).</summary>
        [Range(0f, 0.5f)] public float CavityFloor = NamerEditorConstants.DefaultCavityFloor;

        /// <summary>Normal-detail reduction before octahedral encode (D-07).</summary>
        [Range(0f, 1f)] public float NormalDetailReduction = NamerEditorConstants.DefaultNormalDetailReduction;

        /// <summary>Roughness simplification before 6-bit quantization (D-07).</summary>
        [Range(0f, 1f)] public float RoughnessSimplify = NamerEditorConstants.DefaultRoughnessSimplify;

        /// <summary>Hue mode index: 0 = Preserve (default), 1 = Replace (D-05).</summary>
        public int HueMode = NamerEditorConstants.DefaultHueMode;

        // -- Decomposition state (S6 honesty pair + classification confidences) ----

        /// <summary>
        /// Fingerprint of the refs + weights the last Decompose consumed (the exact
        /// <see cref="ComputeFingerprint"/> return, NamerPostLook PostFitFingerprint
        /// idiom). Empty = never decomposed. INDEPENDENT of
        /// <see cref="PostFitFingerprint"/> and of <see cref="PaletteAnchors"/>.
        /// </summary>
        public string DecompositionFingerprint = "";

        /// <summary>
        /// S6 honesty pair: empty = the grade estimate is neutral/model-free;
        /// non-empty = the estimate was refined against that named exemplar. The
        /// status line renders which variant — honest reporting, never silent.
        /// </summary>
        public string GradeSourceExemplarName = "";

        /// <summary>
        /// Fraction of texels confidently classified as clean albedo; -1 = never
        /// decomposed (renders the em-dash placeholder). Clamped to [-1,1] by
        /// <see cref="ClampToCaps"/>.
        /// </summary>
        public float ConfidentAlbedoFraction = -1f;

        /// <summary>
        /// Fraction of texels classified as grade-contaminated; -1 = never
        /// decomposed (renders the em-dash placeholder). Clamped to [-1,1] by
        /// <see cref="ClampToCaps"/>.
        /// </summary>
        public float ContaminatedFraction = -1f;

        // -- Validation state (STYL-17 forward validation; empty = never validated)

        /// <summary>Human-readable result line of the last Validate Look run.</summary>
        public string ValidationResult = "";

        /// <summary>Fingerprint of the state the last Validate Look ran against.</summary>
        public string ValidationFingerprint = "";

        /// <summary>
        /// Raised by the asset editor after every value edit (plan 06; mirrors
        /// <see cref="NamerPostLook.Changed"/>). The Processor window subscribes
        /// while this asset is attached and routes the event through its debounced
        /// preview recompute; this event NEVER triggers a decomposition or a fit
        /// (explicit-action law).
        /// </summary>
        // Transient CS0067 silence: the RaiseChanged partial (declaring-type-only
        // law) lands beside the asset editor in plan 06 — remove this pragma with it.
#pragma warning disable CS0067
        public static event Action<NamerStyleAsset> Changed;
#pragma warning restore CS0067

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
        /// True once a decomposition has written
        /// <see cref="DecompositionFingerprint"/>. INDEPENDENT of
        /// <see cref="HasFit"/> and of palette presence: a migrated asset carries a
        /// fit and/or a palette while honestly reading not-decomposed (M3).
        /// </summary>
        public bool IsDecomposed
        {
            get { return !string.IsNullOrEmpty(DecompositionFingerprint); }
        }

        /// <summary>
        /// True once a fit has written <see cref="PostFitFingerprint"/> (the
        /// NamerPostLook idiom). INDEPENDENT of <see cref="IsDecomposed"/> and of
        /// palette presence.
        /// </summary>
        public bool HasFit
        {
            get { return !string.IsNullOrEmpty(PostFitFingerprint); }
        }

        /// <summary>
        /// True when the current refs + weights no longer match the decomposed
        /// <see cref="DecompositionFingerprint"/>. Immediately after a decomposition
        /// the stored fingerprint equals <see cref="ComputeFingerprint"/>, so this
        /// reads false (the NamerPostLook IsFitStale idiom).
        /// </summary>
        public bool IsDecompositionStale
        {
            get { return IsDecomposed && ComputeFingerprint() != DecompositionFingerprint; }
        }

        /// <summary>
        /// True when the S12 palette-source popup no longer matches the source the
        /// current palette was extracted from — the input for the NEXT decomposition
        /// has changed (never a silent re-extraction). Only meaningful once
        /// decomposed.
        /// </summary>
        public bool IsPaletteSourceStale
        {
            get { return IsDecomposed && PaletteSource != PaletteSourceAtExtract; }
        }

        /// <summary>
        /// True when any Volume-relevant parameter drifted since the last Volume
        /// write (05.2 A13 shape). False until a fit exists or while the
        /// last-written params fingerprint still matches
        /// <see cref="ComputeParamsFingerprint"/>.
        /// </summary>
        public bool IsVolumeStale
        {
            get { return HasFit && VolumeWriteFingerprint != ComputeParamsFingerprint(); }
        }

        /// <summary>
        /// Identity string of the assigned refs' asset GUIDs plus their weights, pipe
        /// separated (over the SHARED reference list). Null slots are skipped. This
        /// is an identity string, not a security hash. Deterministic: two calls on
        /// the same state return equal strings.
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
        /// Identity string of every Volume-relevant parameter (05.2 A13 shape): the
        /// six fitted floats plus the bloom-intensity artist knob and the time/value
        /// keys of all four curves, pipe separated with invariant round-trip
        /// formatting. Deterministic: two calls on the same state return equal
        /// strings. Curve tangents are deliberately excluded — the Volume bakes
        /// curves through <see cref="AnimationCurve.Evaluate"/>, and the 05.2 UI
        /// contract keys staleness on time/value edits only.
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
        /// Sanitizes a hand-edited serialized asset (T-05.3-02-01): truncates
        /// <see cref="References"/> to <see cref="NamerEditorConstants.MaxReferences"/>,
        /// syncs <see cref="ReferenceWeights"/> to the reference count, clamps every
        /// weight to [0,1], and replaces non-numeric weights with the default weight.
        /// Sanitizes the fitted-post block with the NaN-then-clamp discipline (NaN
        /// falls back to the neutral identity BEFORE clamping — Mathf.Clamp
        /// propagates NaN), the palette-application sliders the same way (NaN falls
        /// back to each slider's default), the palette-source ints to
        /// [<see cref="NamerEditorConstants.kPaletteSourceDeGraded"/>,
        /// <see cref="NamerEditorConstants.kPaletteSourceRaw"/>], and the
        /// classification-confidence fractions to [-1,1] (NaN falls back to the -1
        /// never-decomposed sentinel). Consumed before every read: at Decompose time
        /// (plan 03) and at Volume-write time.
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

            // Fitted-post block (NamerPostLook discipline verbatim): NaN falls back
            // to the neutral identity (0), except the bloom floats, which fall back
            // to their URP-default constants.
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

            if (float.IsNaN(PostBloomIntensity))
            {
                PostBloomIntensity = NamerEditorConstants.DefaultBloomIntensity;
            }

            PostBloomIntensity = Mathf.Clamp(PostBloomIntensity, 0f, NamerEditorConstants.MaxBloomIntensity);

            SanitizeCurve(ref PostMasterCurve);
            SanitizeCurve(ref PostRedCurve);
            SanitizeCurve(ref PostGreenCurve);
            SanitizeCurve(ref PostBlueCurve);

            // Palette-application sliders (NamerStyleProfile donor ranges): NaN
            // falls back to each slider's default before the clamp.
            StyleStrength = Mathf.Clamp(
                float.IsNaN(StyleStrength) ? NamerEditorConstants.DefaultStyleStrength : StyleStrength, 0f, 1f);
            Smoothing = Mathf.Clamp(
                float.IsNaN(Smoothing) ? NamerEditorConstants.DefaultSmoothingStrength : Smoothing, 0f, 1f);
            CavityFloor = Mathf.Clamp(
                float.IsNaN(CavityFloor) ? NamerEditorConstants.DefaultCavityFloor : CavityFloor, 0f, 0.5f);
            NormalDetailReduction = Mathf.Clamp(
                float.IsNaN(NormalDetailReduction) ? NamerEditorConstants.DefaultNormalDetailReduction : NormalDetailReduction, 0f, 1f);
            RoughnessSimplify = Mathf.Clamp(
                float.IsNaN(RoughnessSimplify) ? NamerEditorConstants.DefaultRoughnessSimplify : RoughnessSimplify, 0f, 1f);

            // Palette-source ints (S12): clamp to the two valid popup indices. At
            // Extract mirrors the clamp so a hand-edited out-of-range pair can never
            // fabricate a false IsPaletteSourceStale match.
            PaletteSourceAtExtract = Mathf.Clamp(
                PaletteSourceAtExtract, NamerEditorConstants.kPaletteSourceDeGraded, NamerEditorConstants.kPaletteSourceRaw);
            PaletteSource = Mathf.Clamp(
                PaletteSource, NamerEditorConstants.kPaletteSourceDeGraded, NamerEditorConstants.kPaletteSourceRaw);

            // Classification confidences: NaN falls back to the -1
            // never-decomposed sentinel, then clamps to [-1,1].
            if (float.IsNaN(ConfidentAlbedoFraction))
            {
                ConfidentAlbedoFraction = -1f;
            }

            ConfidentAlbedoFraction = Mathf.Clamp(ConfidentAlbedoFraction, -1f, 1f);

            if (float.IsNaN(ContaminatedFraction))
            {
                ContaminatedFraction = -1f;
            }

            ContaminatedFraction = Mathf.Clamp(ContaminatedFraction, -1f, 1f);
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
        /// Sanitizes one fitted-post curve (NamerPostLook discipline verbatim): null
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
        /// semicolon-separated <c>time,value</c> segment behind a pipe (05.2 A13
        /// shape). A null curve appends an empty segment — null and 0-key curves
        /// collide deliberately: both are pre-sanitization states ClampToCaps maps
        /// to the same neutral diagonal.
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

        /// <summary>
        /// Creates a new merged Style Asset in the Project window (05.3 D-06/M1 —
        /// the ONLY creation path; house menu root "NAMER" under Assets/Create,
        /// UI-SPEC C-series Copywriting).
        /// </summary>
        [MenuItem("Assets/Create/NAMER/Style")]
        private static void CreateStyleAsset()
        {
            ProjectWindowUtil.CreateAsset(
                ScriptableObject.CreateInstance<NamerStyleAsset>(),
                NamerEditorConstants.StyleAssetDefaultAssetName + ".asset");
        }
    }
}

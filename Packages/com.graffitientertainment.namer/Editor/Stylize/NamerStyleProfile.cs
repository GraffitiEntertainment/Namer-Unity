using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// One extracted palette anchor (D-04). The color is LINEAR (the pipeline's working
    /// space); hue is normalized [0,1] (red wraparound), saturation is HSV-style
    /// (max-min)/max, luma is the Rec.709 dot of <see cref="LinearColor"/>, and weight is
    /// the merged weighted-histogram mass assigned to the anchor's cluster.
    /// </summary>
    [Serializable]
    public struct NamerStyleAnchor
    {
        /// <summary>Cluster-center color in linear space (written only by Extract).</summary>
        public Color LinearColor;

        /// <summary>Normalized hue [0,1] of <see cref="LinearColor"/> (red wraps at 0/1).</summary>
        public float Hue;

        /// <summary>HSV-style saturation (max-min)/max of <see cref="LinearColor"/>.</summary>
        public float Saturation;

        /// <summary>Rec.709 luminance of <see cref="LinearColor"/>.</summary>
        public float Luma;

        /// <summary>Merged weighted-histogram mass assigned to this anchor's cluster.</summary>
        public float Weight;
    }

    /// <summary>
    /// Merged aggregate style statistics over all weighted references (STYL-02), written
    /// only by the explicit Extract action (D-04: ONE merged set, never per-reference).
    /// Every numeric field defaults to zero and every color to transparent black; the
    /// inspector renders the em-dash placeholder until the profile reports
    /// <see cref="NamerStyleProfile.IsExtracted"/>.
    /// </summary>
    [Serializable]
    public class NamerStyleStats
    {
        /// <summary>Number of anchors carrying histogram mass (k-means clusters that received texels).</summary>
        public int AnchorCount;

        /// <summary>Distinct 30-degree hue sectors holding at least one chromatic anchor.</summary>
        public int HueFamilyCount;

        /// <summary>Weighted mean HSV-style saturation over the merged histogram.</summary>
        public float MeanSaturation;

        /// <summary>Fraction of weighted mass with luma below 1/3 (shadow band).</summary>
        public float ShadowFraction;

        /// <summary>Fraction of weighted mass in the [1/3, 2/3) midtone band.</summary>
        public float MidtoneFraction;

        /// <summary>Fraction of weighted mass with luma at or above 2/3 (highlight band).</summary>
        public float HighlightFraction;

        /// <summary>Weighted mean linear color of the shadow band.</summary>
        public Color ShadowColor;

        /// <summary>Weighted mean linear color of the midtone band.</summary>
        public Color MidtoneColor;

        /// <summary>Weighted mean linear color of the highlight band.</summary>
        public Color HighlightColor;

        /// <summary>Warm-minus-cool luma-weighted energy bias in [-1,1] (warm hue [330,90] deg, cool [180,270] deg).</summary>
        public float WarmCoolBias;

        /// <summary>Luma p90/p10 ratio over the merged histogram (contrast measure).</summary>
        public float Contrast;

        /// <summary>Occupied-histogram-bin fraction (palette spread measure).</summary>
        public float PaletteDensity;

        /// <summary>Number of assigned references read into the merged histogram.</summary>
        public int ReferenceCount;
    }

    /// <summary>
    /// Reusable stylization profile asset (STYL-01). Holds 3-10 reference images with
    /// per-reference weights, the six stylization sliders, and the Advanced hue mode
    /// (D-05). The extracted block (anchors, stats, fingerprint) is written ONLY by the
    /// explicit Extract action in <see cref="NamerStyleProfileEditor"/> (D-04: extraction
    /// is never run on the debounced preview path). Attaching the profile in the
    /// Processor window applies it to the preview on load/change (D-06 dual-channel live
    /// asset); the window subscribes to <see cref="Changed"/> for edit notification.
    /// Partial: the Changed raise helper lives beside its only caller in
    /// <c>Editor/UI/NamerStyleProfileEditor.cs</c> (C# events can only be raised from
    /// the declaring type).
    /// </summary>
    public sealed partial class NamerStyleProfile : ScriptableObject
    {
        /// <summary>Reference image slots; starts EMPTY, 0-10 entries (user-locked list model, UI-SPEC P2).</summary>
        public List<Texture2D> References = new List<Texture2D>();

        /// <summary>Per-slot extraction weights, parallel to <see cref="References"/> (D-05 Advanced; default 1).</summary>
        public List<float> ReferenceWeights = new List<float>();

        /// <summary>Style strength, 0 = original, 1 = full palette snap (D-04, UI-SPEC P7).</summary>
        [Range(0f, 1f)] public float StyleStrength = NamerEditorConstants.DefaultStyleStrength;

        /// <summary>
        /// Tone match [0,1] (quick 260924-hk7 semantics amendment): 0 preserves the
        /// source's own tonality; higher values pull brightness toward the reference
        /// anchors' luma. StyleStrength now governs hue + vibrance only — tonality is
        /// this slider, independent of strength. Old profile assets deserialize the
        /// missing field to the default 0 (safe no-migration).
        /// </summary>
        [Range(0f, 1f)] public float ToneMatch = NamerEditorConstants.DefaultToneMatch;

        /// <summary>Island-gated bilateral smoothing strength (D-03, UI-SPEC P8).</summary>
        [Range(0f, 1f)] public float Smoothing = NamerEditorConstants.DefaultSmoothingStrength;

        /// <summary>Cavity floor applied after AO contrast, keeps cavities from crushing to black (D-08, UI-SPEC P9).</summary>
        [Range(0f, 0.5f)] public float CavityFloor = NamerEditorConstants.DefaultCavityFloor;

        /// <summary>Normal-detail reduction before octahedral encode (D-07, UI-SPEC P10).</summary>
        [Range(0f, 1f)] public float NormalDetailReduction = NamerEditorConstants.DefaultNormalDetailReduction;

        /// <summary>Roughness simplification before 6-bit quantization (D-07, UI-SPEC P11).</summary>
        [Range(0f, 1f)] public float RoughnessSimplify = NamerEditorConstants.DefaultRoughnessSimplify;

        /// <summary>Hue mode index: 0 = Preserve (default), 1 = Replace (D-05, UI-SPEC P13; STYL-03's stronger replacement).</summary>
        public int HueMode = NamerEditorConstants.DefaultHueMode;

        /// <summary>Extracted palette anchors (D-04 merged aggregate; written only by Extract).</summary>
        public List<NamerStyleAnchor> PaletteAnchors = new List<NamerStyleAnchor>();

        /// <summary>Extracted merged statistics (STYL-02; written only by Extract).</summary>
        public NamerStyleStats Stats = new NamerStyleStats();

        /// <summary>Fingerprint of the refs + weights the last Extract consumed (OQ-4; written only by Extract).</summary>
        public string ReferenceFingerprint = "";

        // Fitted post look (05.1 D-02; written only by Fit Look — the profile is the
        // source of truth, the generated Volume derives). An unfitted/neutral fit is
        // identity post: zero white balance, zero EV, unity contrast/saturation, the
        // neutral curve diagonal. Old profile assets deserialize the missing block to
        // these neutral defaults (safe no-migration, UI-SPEC section C).

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

        /// <summary>Fitted master luma curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostMasterCurve = CreateNeutralCurve();

        /// <summary>Fitted red channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostRedCurve = CreateNeutralCurve();

        /// <summary>Fitted green channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostGreenCurve = CreateNeutralCurve();

        /// <summary>Fitted blue channel curve on the neutral [0,1] diagonal (05.1 D-02; written only by Fit Look).</summary>
        public AnimationCurve PostBlueCurve = CreateNeutralCurve();

        /// <summary>Fingerprint of the refs + weights the last Fit Look consumed (05.1 D-02; written only by Fit Look).</summary>
        public string PostFitFingerprint = "";

        /// <summary>Display name of the exemplar object the last Fit Look was fitted from (05.1 D-03; written only by Fit Look).</summary>
        public string PostFitExemplarName = "";

        /// <summary>
        /// Raised by <see cref="NamerStyleProfileEditor"/> after every value edit (OQ-2).
        /// The Processor window subscribes and routes the profile through its existing
        /// debounced preview recompute; this event NEVER triggers extraction (D-04).
        /// </summary>
        public static event Action<NamerStyleProfile> Changed;

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

        /// <summary>True once an Extract has written a fingerprint onto the asset.</summary>
        public bool IsExtracted
        {
            get { return !string.IsNullOrEmpty(ReferenceFingerprint); }
        }

        /// <summary>
        /// True when the current refs + weights no longer match the extracted
        /// <see cref="ReferenceFingerprint"/> (OQ-4: any weight edit marks stale, because
        /// weights apply at extract time). Only meaningful when extracted.
        /// </summary>
        public bool IsStale
        {
            get { return IsExtracted && ComputeFingerprint() != ReferenceFingerprint; }
        }

        /// <summary>
        /// True once a Fit Look has written a fit fingerprint onto the asset (05.1 D-02).
        /// A profile with no serialized fit data reports false — old pre-05.1 assets take
        /// this path (safe no-migration).
        /// </summary>
        public bool HasPostFit
        {
            get { return !string.IsNullOrEmpty(PostFitFingerprint); }
        }

        /// <summary>
        /// True when the current refs + weights no longer match the fitted
        /// <see cref="PostFitFingerprint"/> (05.1 UI-SPEC P19: drives the fit-staleness
        /// warning). INDEPENDENT of the extract-staleness check <see cref="IsStale"/> —
        /// the two warnings can fire separately. Only meaningful once fitted.
        /// </summary>
        public bool IsFitStale
        {
            get { return HasPostFit && ComputeFingerprint() != PostFitFingerprint; }
        }

        /// <summary>
        /// Identity string of the assigned refs' asset GUIDs plus their weights, pipe
        /// separated (OQ-4). Null slots are skipped. This is an identity string, not a
        /// security hash. Deterministic: two calls on the same state return equal strings.
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
        /// Sanitizes a hand-edited serialized asset (T-05-03): truncates
        /// <see cref="References"/> to <see cref="NamerEditorConstants.MaxReferences"/>,
        /// syncs <see cref="ReferenceWeights"/> to the reference count, clamps every
        /// weight to [0,1], and replaces non-numeric weights with the default weight.
        /// Sanitizes <see cref="ToneMatch"/> the same way (T-hk7-01: NaN falls back to
        /// the default, then clamps to [0,1] — Mathf.Clamp propagates NaN, and the
        /// Create()/Run Clamp01 chain would otherwise ship it to the GPU). Consumed at
        /// Extract time and at Process time (05-03).
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

            if (float.IsNaN(ToneMatch))
            {
                ToneMatch = NamerEditorConstants.DefaultToneMatch;
            }

            ToneMatch = Mathf.Clamp(ToneMatch, 0f, 1f);

            // Fitted-post block (05.1 plan 02; the ToneMatch NaN-then-clamp precedent —
            // Mathf.Clamp propagates NaN, so the fallback must run first on every float).
            // NaN falls back to the neutral identity (0), except the bloom threshold,
            // which falls back to its URP-default constant.
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

            SanitizeCurve(ref PostMasterCurve);
            SanitizeCurve(ref PostRedCurve);
            SanitizeCurve(ref PostGreenCurve);
            SanitizeCurve(ref PostBlueCurve);
        }

        /// <summary>
        /// Neutral identity curve for the fitted-post block: the two-key diagonal
        /// (0,0)→(1,1). Shared by the field initializers and the
        /// <see cref="ClampToCaps"/> reset paths so a fresh profile and a sanitized
        /// one agree on what "neutral" means.
        /// </summary>
        private static AnimationCurve CreateNeutralCurve()
        {
            return new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
        }

        /// <summary>
        /// Sanitizes one fitted-post curve (05.1 plan 02): null or zero-key curves reset
        /// to the neutral diagonal; otherwise every key's time and value is clamped into
        /// [0,1]. A NaN in any key's time or value resets the whole curve to the neutral
        /// diagonal — Mathf.Clamp propagates NaN, so a clamp cannot repair it.
        /// </summary>
        private static void SanitizeCurve(ref AnimationCurve curve)
        {
            if (curve == null || curve.length == 0)
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
        /// Creates a new style profile asset in the Project window (STYL-01; house menu
        /// root "NAMER" under Assets/Create, UI-SPEC section B).
        /// </summary>
        [MenuItem("Assets/Create/NAMER/Style Profile")]
        private static void CreateStyleProfileAsset()
        {
            ProjectWindowUtil.CreateAsset(
                ScriptableObject.CreateInstance<NamerStyleProfile>(),
                "New Namer Style Profile.asset");
        }
    }
}

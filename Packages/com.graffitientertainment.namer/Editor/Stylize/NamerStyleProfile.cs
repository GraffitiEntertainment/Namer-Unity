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
    /// per-reference weights, the five stylization sliders, and the Advanced hue mode
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
        /// Consumed at Extract time and at Process time (05-03).
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

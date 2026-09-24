using System;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="NamerStyleProfile"/> implementing the 05 UI-SPEC
    /// section B component inventory exactly: the reference-list header (P1), slot rows
    /// with 64x64 thumbnails (P2), the +/– footer (P3), the count-honesty line (P4), the
    /// explicit Extract Style Stats button (P5, STYL-02), the Statistics block with the
    /// em-dash placeholder and staleness warning (P6), the five debounced parameter
    /// sliders (P7-P11) plus the Tone Match slider (quick 260924-hk7), and the Advanced
    /// foldout holding Hue Mode and per-reference
    /// weights (P12-P14). Every value edit routes through Undo.RecordObject +
    /// EditorUtility.SetDirty + <see cref="NamerStyleProfile.Changed"/> (OQ-2); list-shape
    /// edits (+/–) never raise Changed and never recompute — extraction is the only path
    /// that reads references (D-04). Zero custom colors or fonts; EditorStyles roles and
    /// MessageType semantics only, per the UI-SPEC Typography and Color tables.
    /// </summary>
    [CustomEditor(typeof(NamerStyleProfile))]
    public class NamerStyleProfileEditor : UnityEditor.Editor
    {
        /// <summary>Reference-slot thumbnail size in pixels (UI-SPEC Spacing table).</summary>
        private const int kThumbnailSize = 64;

        /// <summary>Cavity Floor slider maximum (UI-SPEC P9 range 0-0.5).</summary>
        private const float kCavityFloorMax = 0.5f;

        /// <summary>Statistics placeholder rendered before the first extract (UI-SPEC P6).</summary>
        private const string kStatPlaceholder = "—";

        /// <summary>Hue Mode popup options (UI-SPEC P13; index into profile.HueMode).</summary>
        private static readonly string[] HueModeOptions = { "Preserve", "Replace" };

        private NamerStyleProfile profile;
        private bool advancedFoldout;
        private bool isExtracting;
        private string extractError;

        private static string DebounceSuffix
        {
            get { return "Recomputes the preview in memory " + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."; }
        }

        private void OnEnable()
        {
            profile = (NamerStyleProfile)target;
            advancedFoldout = false;
        }

        /// <summary>Draws the full P1-P14 inspector; sections disable while extracting.</summary>
        public override void OnInspectorGUI()
        {
            profile = (NamerStyleProfile)target;

            // P1: header block.
            EditorGUILayout.LabelField("Reference Images (3–10)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Reference textures define the palette — Extract computes the anchors and stats from them.", EditorStyles.miniLabel);

            using (new EditorGUI.DisabledScope(isExtracting))
            {
                DrawReferenceSlots();
                DrawReferenceFooter();
                DrawCountLine();
                DrawParameterSliders();
                DrawAdvanced();
            }

            DrawExtractButton();
            DrawStatistics();
        }

        /// <summary>P2 slot rows, or the empty-state block when the list has no slots.</summary>
        private void DrawReferenceSlots()
        {
            if (profile.References.Count == 0)
            {
                EditorGUILayout.LabelField("No reference images.", EditorStyles.largeLabel);
                EditorGUILayout.LabelField("Add 3–10 reference images with the + button, assign textures, then Extract Style Stats.", EditorStyles.label);
                EditorGUILayout.Space();
                return;
            }

            for (int i = 0; i < profile.References.Count; i++)
            {
                Texture2D current = profile.References[i];
                EditorGUILayout.BeginHorizontal();
                Rect thumbnail = GUILayoutUtility.GetRect(kThumbnailSize, kThumbnailSize);
                Texture image = EditorGUIUtility.ObjectContent(current, typeof(Texture2D)).image;
                if (image != null)
                {
                    GUI.DrawTexture(thumbnail, image, ScaleMode.ScaleToFit);
                }

                Texture2D assigned = (Texture2D)EditorGUILayout.ObjectField(
                    GUIContent.none, current, typeof(Texture2D), false);
                EditorGUILayout.EndHorizontal();
                if (assigned == current)
                {
                    continue;
                }

                Undo.RecordObject(profile, "Assign Reference Image");
                SyncWeights();
                profile.References[i] = assigned;
                EditorUtility.SetDirty(profile);
                NamerStyleProfile.RaiseChanged(profile);
            }
        }

        /// <summary>P3 footer: "+"/"–" pair appending/removing the LAST slot. No recompute.</summary>
        private void DrawReferenceFooter()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(profile.References.Count >= NamerEditorConstants.MaxReferences))
            {
                if (GUILayout.Button("+", EditorStyles.miniButtonLeft))
                {
                    Undo.RecordObject(profile, "Add Reference Slot");
                    profile.References.Add(null);
                    profile.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
                    EditorUtility.SetDirty(profile);
                }
            }

            using (new EditorGUI.DisabledScope(profile.References.Count == 0))
            {
                if (GUILayout.Button("–", EditorStyles.miniButtonRight))
                {
                    Undo.RecordObject(profile, "Remove Reference Slot");
                    int lastIndex = profile.References.Count - 1;
                    profile.References.RemoveAt(lastIndex);
                    if (profile.ReferenceWeights.Count > lastIndex)
                    {
                        profile.ReferenceWeights.RemoveAt(lastIndex);
                    }

                    EditorUtility.SetDirty(profile);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>P4: count honesty line — the visible reason for the disabled Extract.</summary>
        private void DrawCountLine()
        {
            EditorGUILayout.LabelField(
                profile.AssignedReferenceCount + " / " + NamerEditorConstants.MaxReferences
                + " references — Extract needs at least " + NamerEditorConstants.MinReferences + ".",
                EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        /// <summary>P5: explicit synchronous extract with the cancellable progress bar.</summary>
        private void DrawExtractButton()
        {
            bool disabled = isExtracting || profile.AssignedReferenceCount < NamerEditorConstants.MinReferences;
            using (new EditorGUI.DisabledScope(disabled))
            {
                if (GUILayout.Button(isExtracting ? "Extracting…" : "Extract Style Stats"))
                {
                    RunExtract();
                }
            }

            if (!string.IsNullOrEmpty(extractError))
            {
                EditorGUILayout.HelpBox(extractError, MessageType.Error);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Runs <see cref="NamerStyleExtractor.Extract"/> once per click (D-04: never on a
        /// debounce). On success the anchors, stats, and fingerprint are written onto the
        /// asset via Undo/SetDirty; failure paths surface the UI-SPEC HelpBox copy.
        /// </summary>
        private void RunExtract()
        {
            extractError = null;
            isExtracting = true;
            try
            {
                NamerStyleExtractResult result;
                try
                {
                    result = NamerStyleExtractor.Extract(profile);
                }
                catch (Exception exception)
                {
                    extractError = "Extraction failed: " + exception.Message;
                    return;
                }

                if (result.Cancelled)
                {
                    return;
                }

                if (result.TooFewReferences)
                {
                    extractError = "Extraction failed: assign at least " + NamerEditorConstants.MinReferences + " reference images.";
                    return;
                }

                if (!string.IsNullOrEmpty(result.ErrorTextureName))
                {
                    extractError = "Extraction failed: " + result.ErrorTextureName
                        + " could not be read. Check the texture's Read/Write import setting.";
                    return;
                }

                Undo.RecordObject(profile, "Extract Style Stats");
                profile.PaletteAnchors.Clear();
                for (int i = 0; i < result.Anchors.Length; i++)
                {
                    profile.PaletteAnchors.Add(result.Anchors[i]);
                }

                profile.Stats = result.Stats;
                profile.ReferenceFingerprint = result.Fingerprint;
                EditorUtility.SetDirty(profile);
            }
            finally
            {
                isExtracting = false;
            }
        }

        /// <summary>P6: stat rows ("—" before first extract) + staleness warning.</summary>
        private void DrawStatistics()
        {
            EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                new GUIContent("Palette Anchors", "Dominant palette anchors extracted from the references (STYL-02)."),
                new GUIContent(profile.IsExtracted ? profile.Stats.AnchorCount.ToString() : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Warm/Cool Bias", "Weighted warm-minus-cool hue energy: > 0 leans warm (330-90 degrees), < 0 leans cool (180-270 degrees)."),
                new GUIContent(profile.IsExtracted ? profile.Stats.WarmCoolBias.ToString("F2") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Contrast", "Luma 90th/10th percentile ratio over the merged reference histogram."),
                new GUIContent(profile.IsExtracted ? profile.Stats.Contrast.ToString("F2") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Palette Density", "Fraction of occupied histogram bins — how broadly the reference palette spreads."),
                new GUIContent(profile.IsExtracted ? profile.Stats.PaletteDensity.ToString("F2") : kStatPlaceholder));

            if (profile.IsStale)
            {
                EditorGUILayout.HelpBox(
                    "Reference images changed since extraction — Extract Style Stats again to update the palette.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
        }

        /// <summary>P7-P11 + Tone Match: the six parameter sliders, each with its decision tooltip.</summary>
        private void DrawParameterSliders()
        {
            DrawSlider(
                new GUIContent("Style Strength", "Palette styling strength: 0 keeps the original look, 1 fully ramps hue toward the reference palette and boosts vibrance (tonality is governed by Tone Match, quick 260924-hk7). " + DebounceSuffix),
                profile.StyleStrength, 0f, 1f, "Set Style Strength",
                (edited, value) => edited.StyleStrength = value);
            DrawSlider(
                new GUIContent("Tone Match", "0 preserves the source's own tonality; higher values pull brightness toward the reference anchors' luma. " + DebounceSuffix),
                profile.ToneMatch, 0f, 1f, "Set Tone Match",
                (edited, value) => edited.ToneMatch = value);
            DrawSlider(
                new GUIContent("Smoothing", "Island-gated bilateral smoothing strength: reduces photographic noise without crossing UV islands, seams, or decals (D-03, STYL-04). " + DebounceSuffix),
                profile.Smoothing, 0f, 1f, "Set Smoothing",
                (edited, value) => edited.Smoothing = value);
            DrawSlider(
                new GUIContent("Cavity Floor", "Applied after AO contrast on the remapped path including authored AO: keeps contrast from crushing cavities to black (D-08, STYL-05). " + DebounceSuffix),
                profile.CavityFloor, 0f, kCavityFloorMax, "Set Cavity Floor",
                (edited, value) => edited.CavityFloor = value);
            DrawSlider(
                new GUIContent("Normal Detail", "0 keeps authored detail; reduces high-frequency normal variation bake-side before octahedral encode (D-07, STYL-06). " + DebounceSuffix),
                profile.NormalDetailReduction, 0f, 1f, "Set Normal Detail",
                (edited, value) => edited.NormalDetailReduction = value);
            DrawSlider(
                new GUIContent("Roughness Simplify", "0 = off; simplifies roughness bake-side after the roughness-dip adoption, before 6-bit quantization (D-07, STYL-06). " + DebounceSuffix),
                profile.RoughnessSimplify, 0f, 1f, "Set Roughness Simplify",
                (edited, value) => edited.RoughnessSimplify = value);
            EditorGUILayout.Space();
        }

        /// <summary>P12-P14: Advanced foldout (default collapsed) with Hue Mode + weights.</summary>
        private void DrawAdvanced()
        {
            advancedFoldout = EditorGUILayout.Foldout(advancedFoldout, "Advanced", true);
            if (!advancedFoldout)
            {
                return;
            }

            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            int hueMode = EditorGUILayout.Popup(
                new GUIContent("Hue Mode", "Preserve keeps the source hue; Replace adopts the reference palette hue (STYL-03) — the replacement ramp stays governed by Style Strength. " + DebounceSuffix),
                profile.HueMode, HueModeOptions);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(profile, "Set Hue Mode");
                profile.HueMode = hueMode;
                EditorUtility.SetDirty(profile);
                NamerStyleProfile.RaiseChanged(profile);
            }

            for (int i = 0; i < profile.References.Count; i++)
            {
                Texture2D reference = profile.References[i];
                if (reference == null)
                {
                    continue;
                }

                float weight = i < profile.ReferenceWeights.Count
                    ? profile.ReferenceWeights[i]
                    : NamerEditorConstants.DefaultReferenceWeight;
                int slot = i + 1;
                DrawSlider(
                    new GUIContent("Ref " + slot + " — " + reference.name,
                        "Per-reference extraction weight: 0 excludes this reference from the palette; weights apply at extract time (D-05). " + DebounceSuffix),
                    weight, 0f, 1f, "Set Reference Weight",
                    (edited, value) =>
                    {
                        SyncWeights();
                        edited.ReferenceWeights[i] = value;
                    });
            }

            EditorGUI.indentLevel--;
        }

        /// <summary>
        /// Slider row with the shared value-edit contract: Undo.RecordObject + write +
        /// SetDirty + <see cref="NamerStyleProfile.Changed"/> (OQ-2).
        /// </summary>
        private void DrawSlider(GUIContent label, float value, float min, float max, string undoName, Action<NamerStyleProfile, float> write)
        {
            EditorGUI.BeginChangeCheck();
            float updated = EditorGUILayout.Slider(label, value, min, max);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(profile, undoName);
                write(profile, updated);
                EditorUtility.SetDirty(profile);
                NamerStyleProfile.RaiseChanged(profile);
            }
        }

        /// <summary>Length-syncs ReferenceWeights to References using the default weight.</summary>
        private void SyncWeights()
        {
            while (profile.ReferenceWeights.Count < profile.References.Count)
            {
                profile.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
            }

            if (profile.ReferenceWeights.Count > profile.References.Count)
            {
                profile.ReferenceWeights.RemoveRange(profile.References.Count, profile.ReferenceWeights.Count - profile.References.Count);
            }
        }
    }

    /// <summary>
    /// Partial declaration of <see cref="NamerStyleProfile"/> holding the Changed raise
    /// helper. C# events can only be raised from the declaring type, so the raise lives
    /// in a partial of the profile beside its only caller — this editor (OQ-2). The
    /// helper is internal to the Editor assembly; no other code may raise the event.
    /// </summary>
    public sealed partial class NamerStyleProfile
    {
        /// <summary>Raises <see cref="Changed"/> after an inspector value edit (OQ-2).</summary>
        internal static void RaiseChanged(NamerStyleProfile profile)
        {
            Changed?.Invoke(profile);
        }
    }
}

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="NamerPostLook"/> — since 05.3 (R6) the LEGACY
    /// migration-source surface: the 05.2 UI-SPEC section A inventory minus the Fit
    /// Look block (deleted with the merged <see cref="NamerStyleAsset"/> taking over
    /// fitting — A6-A8 live on the merged asset's own inspector). Remaining: the
    /// reference-list header + slot rows + +/– footer + count-honesty line (A1-A4,
    /// D-05 — slot rows render material-inspector style — label left, square
    /// thumbnail ObjectField right — per the 2026-09-28 user amendment, superseding
    /// the 2026-09-27 no-thumbnail call), the Advanced foldout with per-reference
    /// fit weights (A5), the EDITABLE fitted parameters (A9-A10 — the 5.1
    /// read-only rows re-homed as the artist tweak surface; the asset is the source
    /// of truth, D-06), and the Volume block (A11-A14: status / Regenerate /
    /// staleness / per-scene hint). Every value edit routes through
    /// Undo.RecordObject + EditorUtility.SetDirty +
    /// <see cref="NamerPostLook.Changed"/>; list-shape edits (+/–) never raise
    /// Changed and never recompute — no remaining editor path reads the references
    /// (Q4: legacy assets stay inspectable migration sources). Zero custom colors
    /// or fonts; EditorStyles roles and MessageType semantics only, per the UI-SPEC
    /// Typography and Color tables.
    /// </summary>
    [CustomEditor(typeof(NamerPostLook))]
    public class NamerPostLookEditor : UnityEditor.Editor
    {
        private NamerPostLook look;
        private bool advancedFoldout;
        private string volumeError;

        /// <summary>Reference-slot thumbnail size in pixels (material-inspector row style; 2026-09-28 user amendment).</summary>
        private const int kThumbnailSize = 64;

        private void OnEnable()
        {
            look = (NamerPostLook)target;
            advancedFoldout = false;
        }

        /// <summary>
        /// Draws the surviving A1-A14 inventory (A6-A8 deleted, 05.3 R6) in
        /// data-flow order: references, parameters, Volume. All copy is
        /// verbatim-locked against the 05.2 UI-SPEC Copywriting table.
        /// </summary>
        public override void OnInspectorGUI()
        {
            look = (NamerPostLook)target;

            DrawReferenceSection();
            EditorGUILayout.Space();

            DrawParametersBlock();
            EditorGUILayout.Space();

            DrawVolumeBlock();
        }

        /// <summary>
        /// A1-A5: the asset's own reference list (D-05) — header + help line, slot
        /// rows (label + ObjectField, no separate thumbnail), the +/– footer, the
        /// count-honesty line, and the Advanced foldout's per-reference fit weights.
        /// </summary>
        private void DrawReferenceSection()
        {
            // A1: header block.
            EditorGUILayout.LabelField("Reference Images", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Graded reference screenshots define the post look — bloom and all. Fit compares them with the Processor window's stylized render.",
                EditorStyles.miniLabel);

            DrawReferenceSlots();
            DrawReferenceFooter();
            DrawCountLine();
            DrawAdvanced();
        }

        /// <summary>
        /// A2 slot rows, or the empty-state block when the list has no slots. Each
        /// row is a 1-based "Reference Texture #N" label on the left plus a square
        /// kThumbnailSize ObjectField whose built-in preview is the thumbnail,
        /// material-inspector style, per the 2026-09-28 user amendment.
        /// </summary>
        private void DrawReferenceSlots()
        {
            if (look.References.Count == 0)
            {
                EditorGUILayout.LabelField("No reference images.", EditorStyles.largeLabel);
                EditorGUILayout.LabelField(
                    "Add reference images with the + button, assign graded screenshots, then Fit Look.",
                    EditorStyles.label);
                EditorGUILayout.Space();
                return;
            }

            for (int i = 0; i < look.References.Count; i++)
            {
                Texture2D current = look.References[i];
                int slot = i + 1;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(new GUIContent("Reference Texture #" + slot));
                Texture2D assigned = (Texture2D)EditorGUILayout.ObjectField(
                    current, typeof(Texture2D), false,
                    GUILayout.Width(kThumbnailSize), GUILayout.Height(kThumbnailSize));
                EditorGUILayout.EndHorizontal();
                if (assigned == current)
                {
                    continue;
                }

                Undo.RecordObject(look, "Assign Reference Image");
                SyncWeights();
                look.References[i] = assigned;
                EditorUtility.SetDirty(look);
                NamerPostLook.RaiseChanged(look);
            }
        }

        /// <summary>
        /// A3 footer: "+"/"–" pair appending/removing the LAST slot. No recompute —
        /// list-shape edits never raise <see cref="NamerPostLook.Changed"/>. The
        /// single-symbol buttons explain themselves on hover (copy-locked tooltips).
        /// </summary>
        private void DrawReferenceFooter()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(look.References.Count >= NamerEditorConstants.MaxReferences))
            {
                if (GUILayout.Button(new GUIContent("+", "+ Add reference image"), EditorStyles.miniButtonLeft))
                {
                    Undo.RecordObject(look, "Add Reference Slot");
                    look.References.Add(null);
                    look.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
                    EditorUtility.SetDirty(look);
                }
            }

            using (new EditorGUI.DisabledScope(look.References.Count == 0))
            {
                if (GUILayout.Button(new GUIContent("–", "− Remove last reference image"), EditorStyles.miniButtonRight))
                {
                    Undo.RecordObject(look, "Remove Reference Slot");
                    int lastIndex = look.References.Count - 1;
                    look.References.RemoveAt(lastIndex);
                    if (look.ReferenceWeights.Count > lastIndex)
                    {
                        look.ReferenceWeights.RemoveAt(lastIndex);
                    }

                    EditorUtility.SetDirty(look);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// A4: count honesty line — the plain assigned count. 05.3 R8 dropped the
        /// Fit Look clause with the button; the min-3 gate copy lives on the merged
        /// asset's editor (S4).
        /// </summary>
        private void DrawCountLine()
        {
            EditorGUILayout.LabelField(
                look.AssignedReferenceCount + " / " + NamerEditorConstants.MaxReferences
                + " references assigned",
                EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        /// <summary>
        /// A5: Advanced foldout (default collapsed) with per-reference fit weight
        /// sliders ("Ref {n} — {name}", 0-1). Weight 0 excludes the reference from
        /// the fit. Weight edits write the asset (Undo/SetDirty/RaiseChanged) but
        /// trigger no recompute — Fit Look is the only reader of the weights.
        /// </summary>
        private void DrawAdvanced()
        {
            advancedFoldout = EditorGUILayout.Foldout(advancedFoldout, "Advanced", true);
            if (!advancedFoldout)
            {
                return;
            }

            EditorGUI.indentLevel++;
            for (int i = 0; i < look.References.Count; i++)
            {
                Texture2D reference = look.References[i];
                if (reference == null)
                {
                    continue;
                }

                float weight = i < look.ReferenceWeights.Count
                    ? look.ReferenceWeights[i]
                    : NamerEditorConstants.DefaultReferenceWeight;
                int slot = i + 1;
                DrawSlider(
                    new GUIContent("Ref " + slot + " — " + reference.name,
                        "Per-reference fit weight: 0 excludes this reference from the fit; weights apply at fit time (D-05)."),
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
        /// Exemplar resolution (the 5.1 donor minus the embedded-host branch —
        /// this asset editor is never embedded in the Processor window): the first
        /// open <see cref="NamerEditorWindow"/> with a loadable source — the "one
        /// visible, honest source of exemplar truth" contract. Null when no candidate
        /// resolves. 05.3 (R6): the Fit Look button that consumed this is deleted
        /// with the merged asset taking over fitting; the seam survives as the
        /// INTERNAL Validate Look donor for the merged asset's editor (S17, plan
        /// 06 — the EvaluateRefitMaxError widening precedent).
        /// </summary>
        internal static NamerEditorWindow ResolveExemplarWindow()
        {
            NamerEditorWindow[] windows = Resources.FindObjectsOfTypeAll<NamerEditorWindow>();
            for (int i = 0; i < windows.Length; i++)
            {
                if (windows[i].HasLoadableSource)
                {
                    return windows[i];
                }
            }

            return null;
        }

        /// <summary>
        /// A9-A10: the EDITABLE fitted parameters (STYL-10/STYL-12 delta — the 5.1
        /// read-only P18 rows re-homed as the artist tweak surface; the asset is the
        /// source of truth, D-06) plus the fit-staleness warning. Rows render in the
        /// locked order inside <see cref="EditorGUI.DisabledScope"/>(!HasFit), with
        /// the unfitted-params reason visible when disabled. Tonemapping stays a
        /// read-only constant row. Every editable tooltip = the 5.1 fit-statistic
        /// string + <see cref="NamerEditorConstants.PostLookParameterTooltipSuffix"/>
        /// (NOT the debounce sentence — these sliders write the asset).
        /// </summary>
        private void DrawParametersBlock()
        {
            EditorGUILayout.LabelField("Fitted Post Look", EditorStyles.boldLabel);

            if (!look.HasFit)
            {
                EditorGUILayout.LabelField(
                    "Not fitted — Fit Look fills these parameters from the references; tweak them afterwards.",
                    EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(!look.HasFit))
            {
                // Constant row — never a placeholder (URP's ACES curve is parameter-free).
                EditorGUILayout.LabelField(
                    new GUIContent("Tonemapping", "Fixed ACES — URP's curve is parameter-free; the fit shapes exposure/contrast/curves around it."),
                    new GUIContent("ACES (default)"));

                string whiteBalanceTooltip = "Von Kries fit of the source mean chromaticity onto the references' (temperature / tint). "
                    + NamerEditorConstants.PostLookParameterTooltipSuffix;
                DrawSlider(
                    new GUIContent("White Balance Temperature", whiteBalanceTooltip),
                    look.PostTemperature, -100f, 100f, "Set White Balance Temperature",
                    (edited, value) => edited.PostTemperature = value);
                DrawSlider(
                    new GUIContent("White Balance Tint", whiteBalanceTooltip),
                    look.PostTint, -100f, 100f, "Set White Balance Tint",
                    (edited, value) => edited.PostTint = value);
                DrawSlider(
                    new GUIContent("Post Exposure", "Mean-luma EV ratio of the tonemapped source onto the decoded references. "
                        + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    look.PostExposureEv, -NamerEditorConstants.MaxFittedPostExposureEv, NamerEditorConstants.MaxFittedPostExposureEv,
                    "Set Post Exposure",
                    (edited, value) => edited.PostExposureEv = value);
                DrawSlider(
                    new GUIContent("Contrast", "LogC p90-p10 percentile-spread ratio between source and references. "
                        + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    look.PostContrast, -100f, 100f, "Set Contrast",
                    (edited, value) => edited.PostContrast = value);
                DrawSlider(
                    new GUIContent("Saturation", "Perceptual mean-chroma ratio between source and references (plain URP Saturation). "
                        + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    look.PostSaturation, -100f, 100f, "Set Saturation",
                    (edited, value) => edited.PostSaturation = value);
                DrawSlider(
                    new GUIContent("Bloom Threshold", "Reference highlight percentile (p95 of raw sRGB Max3), written gamma-space like URP's own slider. "
                        + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    look.PostBloomThreshold, 0f, NamerEditorConstants.MaxBloomThreshold, "Set Bloom Threshold",
                    (edited, value) => edited.PostBloomThreshold = value);
                DrawSlider(
                    new GUIContent("Bloom Intensity", "Artist knob — defaulted, never fitted; tweak it here, then Regenerate Volume. "
                        + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    look.PostBloomIntensity, 0f, NamerEditorConstants.MaxBloomIntensity, "Set Bloom Intensity",
                    (edited, value) => edited.PostBloomIntensity = value);

                string curvesTooltip = "Histogram specification (CDF matching) of source luma/channels onto the references'. "
                    + NamerEditorConstants.PostLookParameterTooltipSuffix;
                DrawCurveField(
                    new GUIContent("Master Curve", curvesTooltip), look.PostMasterCurve, Color.white,
                    "Set Master Curve", (edited, value) => edited.PostMasterCurve = value);
                DrawCurveField(
                    new GUIContent("Red Curve", curvesTooltip), look.PostRedCurve, Color.red,
                    "Set Red Curve", (edited, value) => edited.PostRedCurve = value);
                DrawCurveField(
                    new GUIContent("Green Curve", curvesTooltip), look.PostGreenCurve, Color.green,
                    "Set Green Curve", (edited, value) => edited.PostGreenCurve = value);
                DrawCurveField(
                    new GUIContent("Blue Curve", curvesTooltip), look.PostBlueCurve, Color.blue,
                    "Set Blue Curve", (edited, value) => edited.PostBlueCurve = value);
            }

            // A10: the asset's own reference fingerprint no longer matches fit time.
            if (look.IsFitStale)
            {
                EditorGUILayout.HelpBox(
                    "References changed since the fit — Fit Look again to refresh the post look.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Curve row with the value-edit contract of <see cref="DrawSlider"/> plus the
        /// curve-specific sanitize: after the write, <see cref="NamerPostLook.ClampToCaps"/>
        /// clamps every key to [0,1] and drops degenerates. The key-COUNT cap
        /// (<see cref="NamerEditorConstants.MaxCurveKeys"/>) rides the Volume-write
        /// resample in NamerStyleVolumeWriter.BuildTextureCurve — the editor path does
        /// not second-guess it.
        /// </summary>
        private void DrawCurveField(GUIContent label, AnimationCurve value, Color color, string undoName, Action<NamerPostLook, AnimationCurve> write)
        {
            EditorGUI.BeginChangeCheck();
            AnimationCurve updated = EditorGUILayout.CurveField(label, value, color, new Rect(0f, 0f, 1f, 1f));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(look, undoName);
                write(look, updated);
                look.ClampToCaps();
                EditorUtility.SetDirty(look);
                NamerPostLook.RaiseChanged(look);
            }
        }

        /// <summary>
        /// A11-A14: the Volume block — status line (name-keyed companion Volume
        /// derived from this asset, D-06), the explicit Regenerate Volume button
        /// (A12, preflighted + pinging, never debounced), the Volume-staleness
        /// warning (A13, rides the plan-01 VolumeWriteFingerprint stamp), and the
        /// per-scene duplicate-and-tweak hint (A14).
        /// </summary>
        private void DrawVolumeBlock()
        {
            EditorGUILayout.LabelField("Volume", EditorStyles.boldLabel);

            // A11: the Volume the writer will (re)generate for this asset.
            if (look.HasFit)
            {
                NamerProcessorSettings settings = new NamerProcessorSettings();
                string volumeFileName = Path.GetFileName(NamerStyleVolumeWriter.ComposeVolumePath(look, settings));
                EditorGUILayout.LabelField(
                    "Volume: " + volumeFileName + " — generated from this asset; hand-tweaks to it are lost on re-fit or regeneration.",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("No Volume yet — Fit Look generates one.", EditorStyles.miniLabel);
            }

            // A12: explicit regeneration — disabled until fitted, with the
            // unfitted-params reason under it.
            using (new EditorGUI.DisabledScope(!look.HasFit))
            {
                if (GUILayout.Button(new GUIContent("Regenerate Volume",
                        "Rebuilds this asset's generated URP Volume profile from the current parameters. Overwrites the generated Volume — hand-tweaks to it are lost. For a per-scene look, duplicate this asset and tweak the copy.")))
                {
                    RunRegenerateVolume();
                }
            }

            if (!look.HasFit)
            {
                EditorGUILayout.LabelField(
                    "Not fitted — Fit Look fills these parameters from the references; tweak them afterwards.",
                    EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(volumeError))
            {
                EditorGUILayout.HelpBox(volumeError, MessageType.Error);
            }

            // A13: parameters changed since the last Volume write.
            if (look.IsVolumeStale)
            {
                EditorGUILayout.HelpBox(
                    "Asset parameters changed since the Volume was generated — Regenerate Volume to update the scene look.",
                    MessageType.Warning);
            }

            // A14: the D-06 per-scene workflow hint.
            EditorGUILayout.LabelField(
                "For a per-scene look, duplicate this asset and tweak the copy — each asset owns its own Volume.",
                EditorStyles.miniLabel);
        }

        /// <summary>
        /// A12 click: preflight the write gates, rebuild the companion Volume from
        /// the asset's current parameters, ping it. Any refusal or failure surfaces
        /// as the locked "Volume regeneration failed: …" HelpBox. Explicit action —
        /// never debounced, no confirmation (house law; the overwrite is
        /// pre-documented in the button tooltip).
        /// </summary>
        private void RunRegenerateVolume()
        {
            volumeError = null;
            try
            {
                NamerProcessorSettings settings = new NamerProcessorSettings();
                NamerStyleVolumeWriter.PreflightWriteGate(look, settings);
                VolumeProfile volume = NamerStyleVolumeWriter.Write(look, settings);
                EditorGUIUtility.PingObject(volume);
            }
            catch (Exception exception)
            {
                volumeError = "Volume regeneration failed: " + exception.Message;
            }
        }

        /// <summary>
        /// Slider row with the shared value-edit contract (the 5.1 DrawSlider donor
        /// re-homed, retargeted to <see cref="NamerPostLook"/>): Undo.RecordObject +
        /// write + SetDirty + <see cref="NamerPostLook.Changed"/>. Changed makes a
        /// selected Processor-window preview refresh debounced and flips
        /// <see cref="NamerPostLook.IsVolumeStale"/> via
        /// <see cref="NamerPostLook.ComputeParamsFingerprint"/> — this method never
        /// recomputes anything itself.
        /// </summary>
        private void DrawSlider(GUIContent label, float value, float min, float max, string undoName, Action<NamerPostLook, float> write)
        {
            EditorGUI.BeginChangeCheck();
            float updated = EditorGUILayout.Slider(label, value, min, max);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(look, undoName);
                write(look, updated);
                EditorUtility.SetDirty(look);
                NamerPostLook.RaiseChanged(look);
            }
        }

        /// <summary>Length-syncs ReferenceWeights to References using the default weight.</summary>
        private void SyncWeights()
        {
            while (look.ReferenceWeights.Count < look.References.Count)
            {
                look.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
            }

            if (look.ReferenceWeights.Count > look.References.Count)
            {
                look.ReferenceWeights.RemoveRange(look.References.Count, look.ReferenceWeights.Count - look.References.Count);
            }
        }
    }

    /// <summary>
    /// Partial declaration of <see cref="NamerPostLook"/> holding the Changed raise
    /// helper. C# events can only be raised from the declaring type, so the raise
    /// lives in a partial of the asset beside its only caller — this editor (the
    /// <see cref="NamerStyleProfile"/> RaiseChanged precedent). The helper is
    /// internal to the Editor assembly; no other code may raise the event.
    /// </summary>
    public sealed partial class NamerPostLook
    {
        /// <summary>Raises <see cref="Changed"/> after an inspector value edit or fit (05.2 plan 05).</summary>
        internal static void RaiseChanged(NamerPostLook asset)
        {
            Changed?.Invoke(asset);
        }
    }
}

using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Custom inspector for the merged <see cref="NamerStyleAsset"/> implementing
    /// the 05.3 UI-SPEC section S inventory — the ONE surface that owns style
    /// end to end (STYL-15 no-silent-loss: a migrated asset's carried blocks stay
    /// visible and tweakable here). Draw order = data flow: references →
    /// Decompose → stats → staleness → palette → sliders → post → Volume →
    /// validation. The shared weighted reference list (S1-S5, the A1-A5 donor
    /// re-homed) feeds the explicit Decompose Style action (S6-S8); the
    /// decomposition fills the palette block (S11-S13), the confidence stats
    /// (S9), and the fitted post parameters (S15) — all tweakable afterwards.
    /// The palette-application sliders (S14) and the Volume block (S16) ride the
    /// same value-edit contract as their legacy donors. Every value edit routes
    /// through Undo.RecordObject + EditorUtility.SetDirty +
    /// <see cref="NamerStyleAsset.RaiseChanged"/> (OQ-2); list-shape edits (+/–)
    /// never raise Changed and never recompute — Decompose Style is the only
    /// path that reads the references. Zero custom colors or fonts; EditorStyles
    /// roles and MessageType semantics only, per the UI-SPEC Typography and
    /// Color tables. All user-facing copy lives in
    /// <see cref="NamerEditorConstants"/> (constants law).
    /// </summary>
    [CustomEditor(typeof(NamerStyleAsset))]
    public class NamerStyleAssetEditor : UnityEditor.Editor
    {
        private NamerStyleAsset asset;
        private bool advancedFoldout;
        private bool isDecomposing;
        private string decomposeError;

        /// <summary>Reference-slot thumbnail size in pixels (material-inspector row style; A2 donor).</summary>
        private const int kThumbnailSize = 64;

        private void OnEnable()
        {
            asset = (NamerStyleAsset)target;
            advancedFoldout = false;
        }

        /// <summary>
        /// Draws the S-series inventory in data-flow order. All copy is
        /// verbatim-locked against the 05.3 UI-SPEC Copywriting table (via
        /// <see cref="NamerEditorConstants"/>).
        /// </summary>
        public override void OnInspectorGUI()
        {
            asset = (NamerStyleAsset)target;

            DrawReferenceSection();
            EditorGUILayout.Space();

            DrawDecomposeBlock();

            DrawStatsBlock();

            DrawStalenessWarnings();

            DrawPaletteBlock();
        }

        /// <summary>
        /// S1-S5: the ONE shared weighted reference list (D-05) — header + help
        /// line, slot rows (label left, square thumbnail ObjectField right, per the
        /// 2026-09-28 user amendment), the +/– footer, the count-honesty line with
        /// the min-3 gate copy (S4), and the Advanced foldout's per-reference
        /// decomposition weights (S5).
        /// </summary>
        private void DrawReferenceSection()
        {
            // S1: header block — the shared-list contract in one line.
            EditorGUILayout.LabelField("Reference Images", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                NamerEditorConstants.StyleAssetReferenceHelpLine, EditorStyles.miniLabel);

            DrawReferenceSlots();
            DrawReferenceFooter();
            DrawCountLine();
            DrawAdvanced();
        }

        /// <summary>
        /// S2 slot rows, or the empty-state block when the list has no slots. Each
        /// row is a 1-based "Reference Texture #N" label on the left plus a square
        /// kThumbnailSize ObjectField whose built-in preview is the thumbnail,
        /// material-inspector style (A2 donor shape).
        /// </summary>
        private void DrawReferenceSlots()
        {
            if (asset.References.Count == 0)
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetReferencesEmptyTitle, EditorStyles.largeLabel);
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetReferencesEmptyBody, EditorStyles.label);
                EditorGUILayout.Space();
                return;
            }

            for (int i = 0; i < asset.References.Count; i++)
            {
                Texture2D current = asset.References[i];
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

                Undo.RecordObject(asset, "Assign Reference Image");
                SyncWeights();
                asset.References[i] = assigned;
                EditorUtility.SetDirty(asset);
                NamerStyleAsset.RaiseChanged(asset);
            }
        }

        /// <summary>
        /// S3 footer: "+"/"–" pair appending/removing the LAST slot. No recompute —
        /// list-shape edits never raise <see cref="NamerStyleAsset.Changed"/>
        /// (A3 donor shape; copy-locked tooltips).
        /// </summary>
        private void DrawReferenceFooter()
        {
            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(asset.References.Count >= NamerEditorConstants.MaxReferences))
            {
                if (GUILayout.Button(
                        new GUIContent("+", NamerEditorConstants.StyleAssetAddReferenceTooltip),
                        EditorStyles.miniButtonLeft))
                {
                    Undo.RecordObject(asset, "Add Reference Slot");
                    asset.References.Add(null);
                    asset.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
                    EditorUtility.SetDirty(asset);
                }
            }

            using (new EditorGUI.DisabledScope(asset.References.Count == 0))
            {
                if (GUILayout.Button(
                        new GUIContent("–", NamerEditorConstants.StyleAssetRemoveReferenceTooltip),
                        EditorStyles.miniButtonRight))
                {
                    Undo.RecordObject(asset, "Remove Reference Slot");
                    int lastIndex = asset.References.Count - 1;
                    asset.References.RemoveAt(lastIndex);
                    if (asset.ReferenceWeights.Count > lastIndex)
                    {
                        asset.ReferenceWeights.RemoveAt(lastIndex);
                    }

                    EditorUtility.SetDirty(asset);
                }
            }

            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// S4: count honesty line — the plain assigned count plus the min-3 gate
        /// clause naming the weight requirement (the Decompose prereq copy).
        /// </summary>
        private void DrawCountLine()
        {
            EditorGUILayout.LabelField(
                string.Format(
                    NamerEditorConstants.StyleAssetCountLineFormat,
                    asset.AssignedReferenceCount, NamerEditorConstants.MaxReferences,
                    NamerEditorConstants.MinReferences),
                EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        /// <summary>
        /// S5: Advanced foldout (default collapsed) with per-reference decomposition
        /// weight sliders ("Ref {n} — {name}", 0-1). Weight 0 excludes the reference
        /// from the decomposition. Weight edits write the asset
        /// (Undo/SetDirty/RaiseChanged) but trigger no recompute — Decompose Style
        /// is the only reader of the weights.
        /// </summary>
        private void DrawAdvanced()
        {
            advancedFoldout = EditorGUILayout.Foldout(advancedFoldout, "Advanced", true);
            if (!advancedFoldout)
            {
                return;
            }

            EditorGUI.indentLevel++;
            for (int i = 0; i < asset.References.Count; i++)
            {
                Texture2D reference = asset.References[i];
                if (reference == null)
                {
                    continue;
                }

                float weight = i < asset.ReferenceWeights.Count
                    ? asset.ReferenceWeights[i]
                    : NamerEditorConstants.DefaultReferenceWeight;
                int slot = i + 1;
                DrawSlider(
                    new GUIContent("Ref " + slot + " — " + reference.name,
                        NamerEditorConstants.StyleAssetReferenceWeightTooltip),
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
        /// S6-S8: the decomposition block — the status line naming the weighted
        /// reference count (and the exemplar when the D-02 refinement engaged, so
        /// it is never silent), the explicit Decompose Style button with its
        /// single min-3 prereq, and the failure HelpBox.
        /// </summary>
        private void DrawDecomposeBlock()
        {
            // S6: status line — decomposed (primary or refined variant) or not.
            if (asset.IsDecomposed)
            {
                int weightedCount = NamerStyleDecomposer.CountPositiveWeightReferences(asset);
                string statusLine = string.IsNullOrEmpty(asset.GradeSourceExemplarName)
                    ? string.Format(
                        NamerEditorConstants.StyleAssetDecomposedPrimaryFormat, weightedCount)
                    : string.Format(
                        NamerEditorConstants.StyleAssetDecomposedRefinedFormat,
                        weightedCount, asset.GradeSourceExemplarName);
                EditorGUILayout.LabelField(statusLine, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetNotDecomposedStatus, EditorStyles.miniLabel);
            }

            // S7: the explicit action — the ONLY writer of the decomposition
            // (never automatic, never debounced).
            StyleAssetButtonState decompose = DecomposeButtonState(asset);
            using (new EditorGUI.DisabledScope(decompose.Disabled || isDecomposing))
            {
                string buttonLabel = isDecomposing
                    ? NamerEditorConstants.StyleAssetDecomposeBusyLabel
                    : NamerEditorConstants.StyleAssetDecomposeButtonLabel;
                if (GUILayout.Button(new GUIContent(buttonLabel,
                        NamerEditorConstants.StyleAssetDecomposeTooltip)))
                {
                    RunDecompose();
                }
            }

            if (decompose.Disabled)
            {
                EditorGUILayout.LabelField(decompose.ReasonMiniLabel, EditorStyles.miniLabel);
            }

            // S8: failure surface — decompose failures AND the Volume-write
            // failure of a successful decomposition.
            if (!string.IsNullOrEmpty(decomposeError))
            {
                EditorGUILayout.HelpBox(decomposeError, MessageType.Error);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// The Decompose Style prereq seam (headless-testable; the
        /// FitLookButtonState donor shape). EXACTLY ONE reason: fewer than
        /// <see cref="NamerEditorConstants.MinReferences"/> positive-weight
        /// assigned references. There is deliberately NO exemplar-window prereq —
        /// the D-02 exemplar refinement is a quality boost, never a gate.
        /// </summary>
        /// <param name="asset">Asset to inspect; null counts as zero references.</param>
        internal static StyleAssetButtonState DecomposeButtonState(NamerStyleAsset asset)
        {
            if (asset == null
                || NamerStyleDecomposer.CountPositiveWeightReferences(asset)
                    < NamerEditorConstants.MinReferences)
            {
                return new StyleAssetButtonState(
                    true,
                    string.Format(
                        NamerEditorConstants.StyleAssetDecomposePrereqFormat,
                        NamerEditorConstants.MinReferences));
            }

            return new StyleAssetButtonState(false, null);
        }

        /// <summary>
        /// S7 click: the full decomposition transaction. (a) exemplar refinement
        /// probe — when a Processor window with a loadable source is open, run the
        /// stylized-render fit and pass its params as the grade override (the D-02
        /// complement; a failure here is NOT a decomposition failure — the probe
        /// is skipped, never a gate); (b) <see cref="NamerStyleDecomposer.DecomposeStyle"/>
        /// with the override; (c) preflight the Volume write gate BEFORE any
        /// mutation; (d) apply the grade to the post block, rebuild the palette
        /// anchors, and stamp the decomposition state (one Undo group, one
        /// SetDirty); (e) regenerate the companion Volume and ping it. The
        /// finally block always lowers the busy flag and raises
        /// <see cref="NamerStyleAsset.Changed"/> — the Processor preview picks up
        /// the new palette/post state debounced.
        /// </summary>
        private void RunDecompose()
        {
            decomposeError = null;
            isDecomposing = true;
            try
            {
                // (a) Exemplar refinement probe — quality boost only, never a gate.
                NamerStylePostFitParams gradeOverride = null;
                string gradeOverrideExemplarName = null;
                NamerEditorWindow exemplarWindow = NamerPostLookEditor.ResolveExemplarWindow();
                if (exemplarWindow != null)
                {
                    NamerStylePostFitRunResult refinement = exemplarWindow.RunStylePostFit(asset);
                    if (!refinement.Cancelled
                        && string.IsNullOrEmpty(refinement.ErrorTextureName)
                        && !refinement.NotExtracted)
                    {
                        gradeOverride = refinement.Params;
                        gradeOverrideExemplarName = refinement.ExemplarName;
                    }
                }

                // (b) The decomposition itself.
                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(
                    asset, null, gradeOverride, gradeOverrideExemplarName);
                if (result.Cancelled)
                {
                    return;
                }

                if (result.TooFewReferences)
                {
                    decomposeError = string.Format(
                        NamerEditorConstants.StyleAssetDecomposePrereqFormat,
                        NamerEditorConstants.MinReferences);
                    return;
                }

                if (!string.IsNullOrEmpty(result.ErrorTextureName))
                {
                    decomposeError = string.Format(
                        NamerEditorConstants.StyleAssetDecomposeUnreadableFormat,
                        result.ErrorTextureName);
                    return;
                }

                // (c) Preflight the Volume write BEFORE mutating anything — a
                // refusal leaves the asset exactly as it was.
                NamerProcessorSettings settings = new NamerProcessorSettings();
                try
                {
                    NamerStyleVolumeWriter.PreflightWriteGate(asset, settings);
                }
                catch (Exception preflightRefusal)
                {
                    decomposeError = string.Format(
                        NamerEditorConstants.StyleAssetDecomposeExceptionFormat,
                        preflightRefusal.Message);
                    return;
                }

                // (d) Apply the decomposition (one Undo group).
                Debug.Assert(result.Grade != null,
                    "a non-failed decomposition always carries a grade");
                Undo.RecordObject(asset, "Decompose Style");
                ApplyFittedPost(asset, result.Grade, result.GradeSourceExemplarName);
                asset.PaletteAnchors.Clear();
                for (int i = 0; i < result.PaletteAnchors.Length; i++)
                {
                    asset.PaletteAnchors.Add(result.PaletteAnchors[i]);
                }

                asset.ConfidentAlbedoFraction = result.ConfidentAlbedoFraction;
                asset.ContaminatedFraction = result.ContaminatedFraction;
                asset.DecompositionFingerprint = result.DecompositionFingerprint;
                asset.PaletteSourceAtExtract = asset.PaletteSource;
                asset.GradeSourceExemplarName = result.GradeSourceExemplarName;
                EditorUtility.SetDirty(asset);

                // (e) Regenerate the companion Volume and ping it.
                try
                {
                    VolumeProfile volume = NamerStyleVolumeWriter.Write(asset, settings);
                    EditorGUIUtility.PingObject(volume);
                }
                catch (Exception writeFailure)
                {
                    decomposeError = string.Format(
                        NamerEditorConstants.StyleAssetVolumeRegenerationFailedFormat,
                        writeFailure.Message);
                }
            }
            catch (Exception exception)
            {
                decomposeError = string.Format(
                    NamerEditorConstants.StyleAssetDecomposeExceptionFormat, exception.Message);
            }
            finally
            {
                isDecomposing = false;
                NamerStyleAsset.RaiseChanged(asset);
            }
        }

        /// <summary>
        /// Writes a fitted grade into the asset's post block — the
        /// <see cref="NamerStylePostFit.ApplyToPostLook"/> shape mirrored onto
        /// <see cref="NamerStyleAsset"/> (no asset variant exists there; the
        /// types differ). Curves are cloned so the asset never shares key arrays
        /// with the transient fit. <see cref="NamerStyleAsset.PostBloomIntensity"/>
        /// is deliberately NOT written — the artist knob is never fitted.
        /// </summary>
        private static void ApplyFittedPost(
            NamerStyleAsset asset, NamerStylePostFitParams grade, string exemplarName)
        {
            asset.PostTemperature = grade.Temperature;
            asset.PostTint = grade.Tint;
            asset.PostExposureEv = grade.PostExposureEv;
            asset.PostContrast = grade.Contrast;
            asset.PostSaturation = grade.Saturation;
            asset.PostBloomThreshold = grade.BloomThreshold;
            asset.PostMasterCurve = new AnimationCurve(grade.MasterKeys);
            asset.PostRedCurve = new AnimationCurve(grade.RedKeys);
            asset.PostGreenCurve = new AnimationCurve(grade.GreenKeys);
            asset.PostBlueCurve = new AnimationCurve(grade.BlueKeys);
            asset.PostFitFingerprint = grade.Fingerprint;
            asset.PostFitExemplarName = exemplarName;
        }

        /// <summary>
        /// S9: the decomposition confidence stats — two read-only rows. Before
        /// the first decomposition both fractions are -1 and render the em-dash
        /// placeholder; afterwards they render as whole percentages.
        /// </summary>
        private void DrawStatsBlock()
        {
            EditorGUILayout.LabelField(NamerEditorConstants.StyleAssetStatsHeader, EditorStyles.boldLabel);
            DrawStatsRow(
                NamerEditorConstants.StyleAssetConfidentAlbedoLabel,
                NamerEditorConstants.StyleAssetConfidentAlbedoTooltip,
                asset.ConfidentAlbedoFraction);
            DrawStatsRow(
                NamerEditorConstants.StyleAssetContaminatedLabel,
                NamerEditorConstants.StyleAssetContaminatedTooltip,
                asset.ContaminatedFraction);
            EditorGUILayout.Space();
        }

        /// <summary>
        /// One S9 row: label-with-tooltip on the left, whole-percent value (or the
        /// pre-decomposition placeholder) on the right.
        /// </summary>
        private static void DrawStatsRow(string label, string tooltip, float fraction)
        {
            string value = fraction < 0f
                ? NamerEditorConstants.StyleAssetStatsPlaceholder
                : Mathf.RoundToInt(fraction * NamerEditorConstants.StyleAssetPercentScale) + "%";
            EditorGUILayout.LabelField(new GUIContent(label, tooltip), new GUIContent(value));
        }

        /// <summary>
        /// S10: staleness Warnings — the palette source flipped since the last
        /// decomposition (D-04), and the references changed since it (the
        /// decomposition fingerprint no longer matches).
        /// </summary>
        private void DrawStalenessWarnings()
        {
            if (asset.IsPaletteSourceStale)
            {
                EditorGUILayout.HelpBox(
                    NamerEditorConstants.StyleAssetPaletteSourceStaleWarning, MessageType.Warning);
            }

            if (asset.IsDecompositionStale)
            {
                EditorGUILayout.HelpBox(
                    NamerEditorConstants.StyleAssetDecompositionStaleWarning, MessageType.Warning);
            }
        }

        /// <summary>
        /// S11-S13: the palette block — the state line naming the source the
        /// CURRENT palette was extracted under (S11), the Palette Source popup
        /// (S12 — the input for the NEXT decomposition; switching it writes the
        /// asset but never recomputes, D-04), and the read-only palette rows with
        /// their HSL value text (S13).
        /// </summary>
        private void DrawPaletteBlock()
        {
            EditorGUILayout.LabelField(NamerEditorConstants.StyleAssetPaletteHeader, EditorStyles.boldLabel);

            // S11: the extraction variant the current palette came from.
            if (asset.IsDecomposed)
            {
                string paletteStateLine = asset.PaletteSourceAtExtract == NamerEditorConstants.kPaletteSourceRaw
                    ? NamerEditorConstants.StyleAssetPaletteFromRaw
                    : NamerEditorConstants.StyleAssetPaletteFromDeGraded;
                EditorGUILayout.LabelField(paletteStateLine, EditorStyles.miniLabel);
            }

            // S12: the popup — input for the NEXT decomposition, never a trigger.
            EditorGUI.BeginChangeCheck();
            int paletteSource = EditorGUILayout.Popup(
                new GUIContent(
                    NamerEditorConstants.StyleAssetPaletteSourceLabel,
                    NamerEditorConstants.StyleAssetPaletteSourceTooltip),
                asset.PaletteSource, NamerEditorConstants.StyleAssetPaletteSourceOptions);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(asset, "Set Palette Source");
                asset.PaletteSource = paletteSource;
                EditorUtility.SetDirty(asset);
                NamerStyleAsset.RaiseChanged(asset);
            }

            // S13: the read-only rows (raw linear swatch — data display, no
            // invented gamma conversion; the HSL text carries the numbers).
            if (asset.PaletteAnchors.Count == 0)
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetPaletteEmptyState, EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    string.Format(
                        NamerEditorConstants.StyleAssetPaletteCountFormat,
                        asset.PaletteAnchors.Count),
                    EditorStyles.miniLabel);
                for (int i = 0; i < asset.PaletteAnchors.Count; i++)
                {
                    NamerStyleAnchor anchor = asset.PaletteAnchors[i];
                    int colorIndex = i + 1;
                    EditorGUILayout.BeginHorizontal();
                    GUILayout.Label(new GUIContent(
                        string.Format(NamerEditorConstants.StyleAssetPaletteColorLabelFormat, colorIndex)));
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.ColorField(anchor.LinearColor);
                    }

                    GUILayout.FlexibleSpace();
                    EditorGUILayout.LabelField(
                        string.Format(
                            NamerEditorConstants.StyleAssetPaletteColorValueFormat,
                            Mathf.RoundToInt(anchor.Hue * NamerEditorConstants.StyleAssetHueDegreeRange),
                            Mathf.RoundToInt(anchor.Saturation * NamerEditorConstants.StyleAssetPercentScale),
                            Mathf.RoundToInt(anchor.Luma * NamerEditorConstants.StyleAssetPercentScale)),
                        EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Slider row with the shared value-edit contract (the DrawSlider donor
        /// re-homed, retargeted to <see cref="NamerStyleAsset"/>): Undo.RecordObject +
        /// write + SetDirty + <see cref="NamerStyleAsset.RaiseChanged"/>. Changed
        /// makes an attached Processor-window preview refresh debounced and flips
        /// <see cref="NamerStyleAsset.IsVolumeStale"/> via
        /// <see cref="NamerStyleAsset.ComputeParamsFingerprint"/> — this method
        /// never recomputes anything itself.
        /// </summary>
        private void DrawSlider(GUIContent label, float value, float min, float max, string undoName, Action<NamerStyleAsset, float> write)
        {
            EditorGUI.BeginChangeCheck();
            float updated = EditorGUILayout.Slider(label, value, min, max);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(asset, undoName);
                write(asset, updated);
                EditorUtility.SetDirty(asset);
                NamerStyleAsset.RaiseChanged(asset);
            }
        }

        /// <summary>Length-syncs ReferenceWeights to References using the default weight.</summary>
        private void SyncWeights()
        {
            while (asset.ReferenceWeights.Count < asset.References.Count)
            {
                asset.ReferenceWeights.Add(NamerEditorConstants.DefaultReferenceWeight);
            }

            if (asset.ReferenceWeights.Count > asset.References.Count)
            {
                asset.ReferenceWeights.RemoveRange(
                    asset.References.Count, asset.ReferenceWeights.Count - asset.References.Count);
            }
        }

        /// <summary>
        /// Button-state value: the enabled bit plus the single miniLabel reason
        /// shown under a disabled button (the FitLookButtonState donor shape).
        /// </summary>
        internal readonly struct StyleAssetButtonState
        {
            /// <summary>True when the button renders disabled.</summary>
            public readonly bool Disabled;

            /// <summary>The one reason line rendered under a disabled button; null when enabled.</summary>
            public readonly string ReasonMiniLabel;

            /// <summary>Constructs a button state.</summary>
            public StyleAssetButtonState(bool disabled, string reasonMiniLabel)
            {
                Disabled = disabled;
                ReasonMiniLabel = reasonMiniLabel;
            }
        }
    }

    /// <summary>
    /// Partial declaration of <see cref="NamerStyleAsset"/> holding the Changed
    /// raise helper (the <see cref="NamerPostLook"/> donor pattern). C# events
    /// can only be raised from the declaring type, so the raise lives in a
    /// partial of the asset beside its only caller — this editor. The helper is
    /// internal to the Editor assembly; no other code may raise the event.
    /// </summary>
    public sealed partial class NamerStyleAsset
    {
        /// <summary>Raises <see cref="Changed"/> after an inspector value edit or a decomposition (05.3 plan 06).</summary>
        internal static void RaiseChanged(NamerStyleAsset asset)
        {
            Changed?.Invoke(asset);
        }
    }
}

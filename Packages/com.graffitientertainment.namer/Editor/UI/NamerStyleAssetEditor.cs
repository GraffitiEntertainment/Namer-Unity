using System;
using System.IO;
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
        private volatile bool decomposeCancelRequested;
        private volatile float decomposeProgress;
        private volatile string decomposePhase;
        private string decomposeError;
        private string volumeError;
        private bool isValidating;
        private volatile bool validateCancelRequested;
        private volatile float validateProgress;
        private volatile string validatePhase;
        private string validationError;

        /// <summary>Reference-slot thumbnail size in pixels (material-inspector row style; A2 donor).</summary>
        private const int kThumbnailSize = 64;

        /// <summary>Cavity Floor slider maximum (UI-SPEC P9 range 0-0.5).</summary>
        private const float kCavityFloorMax = 0.5f;

        /// <summary>
        /// Fraction of the Decompose progress bar owned by the exemplar refinement
        /// probe (05.3 threading): the probe reports 0 → this span, the decomposition
        /// itself this span → 1.
        /// </summary>
        private const float kProbeProgressSpan = 0.15f;

        /// <summary>
        /// Validate Look progress-bar title (05.3 threading; the Decompose tick's
        /// title pattern, re-homed for S17).
        /// </summary>
        internal const string kValidateProgressTitle = "Validating NAMER Style";

        /// <summary>Hue Mode popup options (UI-SPEC P13; index into the asset's HueMode).</summary>
        private static readonly string[] HueModeOptions = { "Preserve", "Replace" };

        /// <summary>
        /// The S14 debounce sentence appended to every palette-application control
        /// tooltip — these recompute the attached Processor-window preview
        /// (debounce law; the profile editor's DebounceSuffix donor, verbatim).
        /// </summary>
        private static string DebounceSuffix
        {
            get { return "Recomputes the preview in memory " + NamerEditorConstants.DebounceSeconds + " s after the slider stops — nothing is written to disk."; }
        }

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

            DrawSlidersBlock();

            DrawPostBlock();

            DrawVolumeBlock();

            DrawValidationBlock();
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

            // S5: Hue Mode — the palette-application control living in the foldout
            // (P13 donor placement; debounce-suffixed, it recomputes the preview).
            EditorGUI.BeginChangeCheck();
            int hueMode = EditorGUILayout.Popup(
                new GUIContent("Hue Mode",
                    NamerEditorConstants.StyleAssetHueModeTooltipPrefix + DebounceSuffix),
                asset.HueMode, HueModeOptions);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(asset, "Set Hue Mode");
                asset.HueMode = hueMode;
                EditorUtility.SetDirty(asset);
                NamerStyleAsset.RaiseChanged(asset);
            }

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
        /// S7 click: the full decomposition transaction, threaded (05.3) so the
        /// editor loop stays responsive. (a) exemplar refinement probe via the
        /// awaitable <see cref="NamerEditorWindow.RunStylePostFitAsync"/> seam —
        /// when a Processor window with a loadable source is open, run the
        /// stylized-render fit and pass its params as the grade override (the D-02
        /// complement; a failure here is NOT a decomposition failure — the probe is
        /// skipped, never a gate); the probe owns the first kProbeProgressSpan of
        /// the bar, the decomposition the rest; (b)
        /// <see cref="NamerStyleDecomposer.DecomposeStyleAsync"/> — the main-thread
        /// pixel gather, then the pure compute core on a worker thread while an
        /// <see cref="EditorApplication.update"/> tick renders the cancellable
        /// progress bar from volatile fields the worker's progress callback
        /// writes; (c) preflight the Volume write gate BEFORE any mutation; (d)
        /// apply the grade to the post block, rebuild the palette anchors, and
        /// stamp the decomposition state (one Undo group, one SetDirty); (e)
        /// regenerate the companion Volume and ping it. Steps (c)-(e) run on the
        /// main thread after the await resumes (Unity's synchronization context).
        /// The finally block always detaches the tick, clears the bar, lowers the
        /// busy flag, and raises <see cref="NamerStyleAsset.Changed"/> — the
        /// Processor preview picks up the new palette/post state debounced.
        /// </summary>
        private async void RunDecompose()
        {
            decomposeError = null;
            isDecomposing = true;
            decomposeCancelRequested = false;
            decomposeProgress = 0f;
            decomposePhase = NamerEditorConstants.StyleAssetDecomposeBusyLabel;
            EditorApplication.update += OnDecomposeTick;
            try
            {
                // (a) Exemplar refinement probe — quality boost only, never a gate.
                // Awaitable seam (05.3 threading): the probe's gather, render, and
                // fit release the main thread between stages instead of beachballing
                // the editor before the decomposition's own await.
                NamerStylePostFitParams gradeOverride = null;
                string gradeOverrideExemplarName = null;
                NamerEditorWindow exemplarWindow = NamerPostLookEditor.ResolveExemplarWindow();
                if (exemplarWindow != null)
                {
                    NamerStylePostFitRunResult refinement = await exemplarWindow.RunStylePostFitAsync(
                        asset,
                        () => decomposeCancelRequested,
                        (fraction, phase) =>
                        {
                            decomposeProgress = fraction * kProbeProgressSpan;
                            decomposePhase = phase;
                        });
                    if (!refinement.Cancelled
                        && string.IsNullOrEmpty(refinement.ErrorTextureName)
                        && !refinement.NotExtracted)
                    {
                        gradeOverride = refinement.Params;
                        gradeOverrideExemplarName = refinement.ExemplarName;
                    }
                }

                // (b) The decomposition itself — gather on this thread, compute on
                // a worker; cancel and progress flow through the volatile fields
                // the update tick reads (rescaled onto the span the probe leaves).
                NamerStyleDecomposeResult result = await NamerStyleDecomposer.DecomposeStyleAsync(
                    asset,
                    () => decomposeCancelRequested,
                    gradeOverride,
                    gradeOverrideExemplarName,
                    (fraction, phase) =>
                    {
                        decomposeProgress = kProbeProgressSpan + (1f - kProbeProgressSpan) * fraction;
                        decomposePhase = phase;
                    });
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
                EditorApplication.update -= OnDecomposeTick;
                EditorUtility.ClearProgressBar();
                isDecomposing = false;
                NamerStyleAsset.RaiseChanged(asset);
            }
        }

        /// <summary>
        /// The decompose progress-bar tick (05.3 threading): main-thread paint of
        /// the worker-written volatile progress fields. The bar doubles as the
        /// cancel surface — a user Cancel press latches
        /// <see cref="decomposeCancelRequested"/> for the worker's
        /// <c>shouldCancel</c> delegate. Self-detaches when this inspector is
        /// destroyed mid-decompose (the async continuation still completes its
        /// own finally detach, which is a safe no-op).
        /// </summary>
        private void OnDecomposeTick()
        {
            if (this == null)
            {
                EditorApplication.update -= OnDecomposeTick;
                EditorUtility.ClearProgressBar();
                return;
            }

            if (EditorUtility.DisplayCancelableProgressBar(
                    NamerStyleDecomposer.kDecomposeProgressTitle, decomposePhase, decomposeProgress))
            {
                decomposeCancelRequested = true;
            }

            Repaint();
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
        /// S14: the five palette-application sliders re-homed from the profile
        /// editor with the 05 locked copy, ranges, and DEBOUNCE suffixes verbatim
        /// (these recompute the attached Processor-window preview — debounce law,
        /// NOT the write-suffix). Application controls, not extraction inputs:
        /// edits never touch DecompositionFingerprint or VolumeWriteFingerprint
        /// directly (staleness flips only through the params fingerprint the
        /// base contract already recomputes).
        /// </summary>
        private void DrawSlidersBlock()
        {
            DrawSlider(
                new GUIContent("Style Strength",
                    NamerEditorConstants.StyleAssetStyleStrengthTooltipPrefix + DebounceSuffix),
                asset.StyleStrength, 0f, 1f, "Set Style Strength",
                (edited, value) => edited.StyleStrength = value);
            DrawSlider(
                new GUIContent("Smoothing",
                    NamerEditorConstants.StyleAssetSmoothingTooltipPrefix + DebounceSuffix),
                asset.Smoothing, 0f, 1f, "Set Smoothing",
                (edited, value) => edited.Smoothing = value);
            DrawSlider(
                new GUIContent("Cavity Floor",
                    NamerEditorConstants.StyleAssetCavityFloorTooltipPrefix + DebounceSuffix),
                asset.CavityFloor, 0f, kCavityFloorMax, "Set Cavity Floor",
                (edited, value) => edited.CavityFloor = value);
            DrawSlider(
                new GUIContent("Normal Detail",
                    NamerEditorConstants.StyleAssetNormalDetailTooltipPrefix + DebounceSuffix),
                asset.NormalDetailReduction, 0f, 1f, "Set Normal Detail",
                (edited, value) => edited.NormalDetailReduction = value);
            DrawSlider(
                new GUIContent("Roughness Simplify",
                    NamerEditorConstants.StyleAssetRoughnessSimplifyTooltipPrefix + DebounceSuffix),
                asset.RoughnessSimplify, 0f, 1f, "Set Roughness Simplify",
                (edited, value) => edited.RoughnessSimplify = value);

            EditorGUILayout.Space();
        }

        /// <summary>
        /// S15: the EDITABLE fitted post parameters (the A9 donor re-homed onto
        /// the merged asset). Gated on <c>!(HasFit || IsDecomposed)</c> — NOT
        /// literal !IsDecomposed — so a migrated asset's carried post block stays
        /// tweakable (STYL-15 no-silent-loss); the not-decomposed reason shows
        /// only when NEITHER holds. Every editable tooltip = the 5.1
        /// fit-statistic string + <see cref="NamerEditorConstants.PostLookParameterTooltipSuffix"/>
        /// (the write-suffix — NOT the debounce sentence; these write the asset).
        /// Tonemapping stays a read-only constant row.
        /// </summary>
        private void DrawPostBlock()
        {
            EditorGUILayout.LabelField(
                NamerEditorConstants.StyleAssetPostBlockHeader, EditorStyles.boldLabel);

            if (!(asset.HasFit || asset.IsDecomposed))
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetNotDecomposedStatus, EditorStyles.miniLabel);
            }

            using (new EditorGUI.DisabledScope(!(asset.HasFit || asset.IsDecomposed)))
            {
                // Constant row — never a placeholder (URP's ACES curve is parameter-free).
                EditorGUILayout.LabelField(
                    new GUIContent(
                        NamerEditorConstants.StyleAssetTonemappingLabel,
                        NamerEditorConstants.StyleAssetTonemappingTooltip),
                    new GUIContent(NamerEditorConstants.StyleAssetTonemappingValue));

                string whiteBalanceTooltip =
                    NamerEditorConstants.StyleAssetWhiteBalanceTooltipPrefix
                        + NamerEditorConstants.PostLookParameterTooltipSuffix;
                DrawSlider(
                    new GUIContent("White Balance Temperature", whiteBalanceTooltip),
                    asset.PostTemperature, -100f, 100f, "Set White Balance Temperature",
                    (edited, value) => edited.PostTemperature = value);
                DrawSlider(
                    new GUIContent("White Balance Tint", whiteBalanceTooltip),
                    asset.PostTint, -100f, 100f, "Set White Balance Tint",
                    (edited, value) => edited.PostTint = value);
                DrawSlider(
                    new GUIContent("Post Exposure",
                        NamerEditorConstants.StyleAssetPostExposureTooltipPrefix
                            + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    asset.PostExposureEv,
                    -NamerEditorConstants.MaxFittedPostExposureEv,
                    NamerEditorConstants.MaxFittedPostExposureEv,
                    "Set Post Exposure",
                    (edited, value) => edited.PostExposureEv = value);
                DrawSlider(
                    new GUIContent("Contrast",
                        NamerEditorConstants.StyleAssetContrastTooltipPrefix
                            + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    asset.PostContrast, -100f, 100f, "Set Contrast",
                    (edited, value) => edited.PostContrast = value);
                DrawSlider(
                    new GUIContent("Saturation",
                        NamerEditorConstants.StyleAssetSaturationTooltipPrefix
                            + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    asset.PostSaturation, -100f, 100f, "Set Saturation",
                    (edited, value) => edited.PostSaturation = value);
                DrawSlider(
                    new GUIContent("Bloom Threshold",
                        NamerEditorConstants.StyleAssetBloomThresholdTooltipPrefix
                            + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    asset.PostBloomThreshold, 0f, NamerEditorConstants.MaxBloomThreshold,
                    "Set Bloom Threshold",
                    (edited, value) => edited.PostBloomThreshold = value);
                DrawSlider(
                    new GUIContent("Bloom Intensity",
                        NamerEditorConstants.StyleAssetBloomIntensityTooltipPrefix
                            + NamerEditorConstants.PostLookParameterTooltipSuffix),
                    asset.PostBloomIntensity, 0f, NamerEditorConstants.MaxBloomIntensity,
                    "Set Bloom Intensity",
                    (edited, value) => edited.PostBloomIntensity = value);

                string curvesTooltip =
                    NamerEditorConstants.StyleAssetCurvesTooltipPrefix
                        + NamerEditorConstants.PostLookParameterTooltipSuffix;
                DrawCurveField(
                    new GUIContent("Master Curve", curvesTooltip), asset.PostMasterCurve, Color.white,
                    "Set Master Curve", (edited, value) => edited.PostMasterCurve = value);
                DrawCurveField(
                    new GUIContent("Red Curve", curvesTooltip), asset.PostRedCurve, Color.red,
                    "Set Red Curve", (edited, value) => edited.PostRedCurve = value);
                DrawCurveField(
                    new GUIContent("Green Curve", curvesTooltip), asset.PostGreenCurve, Color.green,
                    "Set Green Curve", (edited, value) => edited.PostGreenCurve = value);
                DrawCurveField(
                    new GUIContent("Blue Curve", curvesTooltip), asset.PostBlueCurve, Color.blue,
                    "Set Blue Curve", (edited, value) => edited.PostBlueCurve = value);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// Curve row with the value-edit contract of <see cref="DrawSlider"/> plus
        /// the curve-specific sanitize: after the write,
        /// <see cref="NamerStyleAsset.ClampToCaps"/> clamps every key to [0,1]
        /// and drops degenerates (the DrawCurveField donor shape, retargeted to
        /// the merged asset).
        /// </summary>
        private void DrawCurveField(GUIContent label, AnimationCurve value, Color color, string undoName, Action<NamerStyleAsset, AnimationCurve> write)
        {
            EditorGUI.BeginChangeCheck();
            AnimationCurve updated = EditorGUILayout.CurveField(label, value, color, new Rect(0f, 0f, 1f, 1f));
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(asset, undoName);
                write(asset, updated);
                asset.ClampToCaps();
                EditorUtility.SetDirty(asset);
                NamerStyleAsset.RaiseChanged(asset);
            }
        }

        /// <summary>
        /// S16: the Volume block — status line (name-keyed companion Volume
        /// derived from this asset, D-06), the explicit Regenerate Volume button
        /// (gated on HasFit — the params exist to rebuild from; the
        /// not-decomposed reason shows only when NEITHER fit nor decomposition
        /// holds), the Volume-staleness warning (rides the VolumeWriteFingerprint
        /// stamp), and the per-scene duplicate-and-tweak hint.
        /// </summary>
        private void DrawVolumeBlock()
        {
            EditorGUILayout.LabelField(
                NamerEditorConstants.StyleAssetVolumeBlockHeader, EditorStyles.boldLabel);

            // The Volume the writer will (re)generate for this asset.
            if (asset.HasFit)
            {
                NamerProcessorSettings settings = new NamerProcessorSettings();
                string volumeFileName = Path.GetFileName(
                    NamerStyleVolumeWriter.ComposeVolumePath(asset, settings));
                EditorGUILayout.LabelField(
                    string.Format(NamerEditorConstants.StyleAssetVolumeStatusFormat, volumeFileName),
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetNoVolumeStatus, EditorStyles.miniLabel);
            }

            // Explicit regeneration — never debounced, no confirmation (the
            // overwrite is pre-documented in the button tooltip).
            using (new EditorGUI.DisabledScope(!asset.HasFit))
            {
                if (GUILayout.Button(new GUIContent(
                        NamerEditorConstants.StyleAssetRegenerateVolumeLabel,
                        NamerEditorConstants.StyleAssetRegenerateVolumeTooltip)))
                {
                    RunRegenerateVolume();
                }
            }

            if (!(asset.HasFit || asset.IsDecomposed))
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetNotDecomposedStatus, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(volumeError))
            {
                EditorGUILayout.HelpBox(volumeError, MessageType.Error);
            }

            // Parameters changed since the last Volume write.
            if (asset.IsVolumeStale)
            {
                EditorGUILayout.HelpBox(
                    NamerEditorConstants.StyleAssetVolumeStaleWarning, MessageType.Warning);
            }

            EditorGUILayout.LabelField(
                NamerEditorConstants.StyleAssetPerSceneHint, EditorStyles.miniLabel);
            EditorGUILayout.Space();
        }

        /// <summary>
        /// S16 Regenerate click: preflight the write gates, rebuild the companion
        /// Volume from the asset's current parameters, ping it. Any refusal or
        /// failure surfaces as the locked "Volume regeneration failed: …"
        /// HelpBox (the RunRegenerateVolume donor shape).
        /// </summary>
        private void RunRegenerateVolume()
        {
            volumeError = null;
            try
            {
                NamerProcessorSettings settings = new NamerProcessorSettings();
                NamerStyleVolumeWriter.PreflightWriteGate(asset, settings);
                VolumeProfile volume = NamerStyleVolumeWriter.Write(asset, settings);
                EditorGUIUtility.PingObject(volume);
            }
            catch (Exception exception)
            {
                volumeError = string.Format(
                    NamerEditorConstants.StyleAssetVolumeRegenerationFailedFormat, exception.Message);
            }
        }

        /// <summary>
        /// S17: Forward Validation (STYL-17/D-03) — the explicit Validate Look
        /// action plus the persisted-result surface. The stored result renders
        /// CONTENT-KEYED by severity: lines starting with the mismatch prefix
        /// render as a Warning HelpBox (the STYL-17 LUT-unlock evidence),
        /// everything else as a plain miniLabel. A stale result (the fingerprint
        /// the last validation ran against no longer matches the asset) renders
        /// the stale line prompting a re-run. Evidence only — no RaiseChanged,
        /// no Volume or palette writes; nothing changes on the asset until the
        /// 5.1 parametric-knobs decision is revisited.
        /// </summary>
        private void DrawValidationBlock()
        {
            EditorGUILayout.LabelField(
                NamerEditorConstants.StyleAssetValidationHeader, EditorStyles.boldLabel);

            // Resolve the exemplar ONCE — the button state and the click share
            // the same window (race-safe).
            NamerEditorWindow exemplarWindow = NamerPostLookEditor.ResolveExemplarWindow();
            StyleAssetButtonState validate = ValidateButtonState(asset, exemplarWindow);
            using (new EditorGUI.DisabledScope(validate.Disabled || isValidating))
            {
                string buttonLabel = isValidating
                    ? NamerEditorConstants.StyleAssetValidateBusyLabel
                    : NamerEditorConstants.StyleAssetValidateButtonLabel;
                if (GUILayout.Button(new GUIContent(buttonLabel,
                        NamerEditorConstants.StyleAssetValidateTooltip)))
                {
                    RunValidate(exemplarWindow);
                }
            }

            if (validate.Disabled)
            {
                EditorGUILayout.LabelField(validate.ReasonMiniLabel, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(validationError))
            {
                EditorGUILayout.HelpBox(validationError, MessageType.Error);
            }

            // The persisted result of the last run.
            if (!string.IsNullOrEmpty(asset.ValidationResult))
            {
                if (asset.ValidationResult.StartsWith(
                        NamerEditorConstants.StyleAssetValidationMismatchPrefix))
                {
                    EditorGUILayout.HelpBox(asset.ValidationResult, MessageType.Warning);
                }
                else
                {
                    EditorGUILayout.LabelField(asset.ValidationResult, EditorStyles.miniLabel);
                }
            }

            // Stale result — the last validation ran against different state.
            if (asset.IsDecomposed
                && !string.IsNullOrEmpty(asset.ValidationFingerprint)
                && asset.ValidationFingerprint != asset.ComputeFingerprint())
            {
                EditorGUILayout.LabelField(
                    NamerEditorConstants.StyleAssetValidationStale, EditorStyles.miniLabel);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// The Validate Look prereq seam (headless-testable). LOCKED two-reason
        /// order: (1) null asset or not decomposed — validation needs the
        /// decomposition to compare against; (2) null exemplar window or no
        /// loadable source — validation renders the Processor window's stylized
        /// output as the subject. Both satisfied → enabled.
        /// </summary>
        /// <param name="asset">Asset to inspect.</param>
        /// <param name="exemplarWindow">The resolved Processor window (may be null).</param>
        internal static StyleAssetButtonState ValidateButtonState(
            NamerStyleAsset asset, NamerEditorWindow exemplarWindow)
        {
            if (asset == null || !asset.IsDecomposed)
            {
                return new StyleAssetButtonState(
                    true, NamerEditorConstants.StyleAssetValidateNeedsDecompositionReason);
            }

            if (exemplarWindow == null || !exemplarWindow.HasLoadableSource)
            {
                return new StyleAssetButtonState(
                    true, NamerEditorConstants.StyleAssetValidateNoExemplarReason);
            }

            return new StyleAssetButtonState(false, null);
        }

        /// <summary>
        /// S17 click: render the exemplar window's stylized decomposition of its
        /// loaded source, re-fit the reference grade against it, and store the
        /// verdict — threaded (05.3) through the awaitable
        /// <see cref="NamerEditorWindow.RunStylePostFitAsync"/> seam so the editor
        /// loop keeps pumping (an <see cref="EditorApplication.update"/> tick paints
        /// the cancellable progress bar from the volatile fields the fit's
        /// progress callback writes). Within tolerance → the miniLabel result
        /// line; visible residual → the mismatch Warning (the STYL-17 LUT-unlock
        /// evidence). The run's fingerprint stamps the state the verdict was
        /// earned against. Failure buckets: cancelled → silent; unreadable
        /// reference or no stylized output → the failed wrapper with the bucket
        /// detail; anything else → the wrapper with the exception message. NO
        /// <see cref="NamerStyleAsset.RaiseChanged"/> — evidence only, the
        /// preview never recomputes for a validation. The writes (Undo group,
        /// verdict fields, SetDirty) happen on the main thread after the await
        /// resumes; the finally block always detaches the tick, clears the bar,
        /// and lowers the busy flag.
        /// </summary>
        private async void RunValidate(NamerEditorWindow exemplarWindow)
        {
            validationError = null;
            isValidating = true;
            validateCancelRequested = false;
            validateProgress = 0f;
            validatePhase = NamerEditorConstants.StyleAssetValidateBusyLabel;
            EditorApplication.update += OnValidateTick;
            try
            {
                // The window closed between the state read and the click —
                // nothing to validate against; the next repaint re-runs the gate.
                if (exemplarWindow == null)
                {
                    return;
                }

                NamerStylePostFitRunResult result = await exemplarWindow.RunStylePostFitAsync(
                    asset,
                    () => validateCancelRequested,
                    (fraction, phase) =>
                    {
                        validateProgress = fraction;
                        validatePhase = phase;
                    });
                if (result.Cancelled)
                {
                    return;
                }

                if (!string.IsNullOrEmpty(result.ErrorTextureName))
                {
                    validationError = string.Format(
                        NamerEditorConstants.StyleAssetValidationFailedFormat,
                        string.Format(
                            NamerEditorConstants.StyleAssetValidationUnreadableMessageFormat,
                            result.ErrorTextureName));
                    return;
                }

                if (result.NotExtracted)
                {
                    validationError = string.Format(
                        NamerEditorConstants.StyleAssetValidationFailedFormat,
                        NamerEditorConstants.StyleAssetValidationNoStylizedOutputMessage);
                    return;
                }

                NamerStyleValidationMeasure measure =
                    NamerStyleDecomposer.ComputeValidationMeasure(result.Params);
                string measureString =
                    NamerStyleDecomposer.BuildValidationMeasureString(result.Params);
                Undo.RecordObject(asset, "Validate Look");
                asset.ValidationResult = NamerStyleDecomposer.IsWithinValidationTolerance(measure)
                    ? string.Format(
                        NamerEditorConstants.StyleAssetValidationWithinToleranceFormat, measureString)
                    : string.Format(
                        NamerEditorConstants.StyleAssetValidationMismatchWarningFormat, measureString);
                asset.ValidationFingerprint = asset.ComputeFingerprint();
                EditorUtility.SetDirty(asset);
            }
            catch (Exception exception)
            {
                validationError = string.Format(
                    NamerEditorConstants.StyleAssetValidationFailedFormat, exception.Message);
            }
            finally
            {
                EditorApplication.update -= OnValidateTick;
                EditorUtility.ClearProgressBar();
                isValidating = false;
            }
        }

        /// <summary>
        /// The Validate Look progress-bar tick (05.3 threading): main-thread paint
        /// of the fit seam's volatile progress fields — the OnDecomposeTick pattern
        /// kept separate rather than abstracted (the two runs own different titles,
        /// cancel fields, and busy flags). The bar doubles as the cancel surface —
        /// a user Cancel press latches <see cref="validateCancelRequested"/> for the
        /// fit's shouldCancel delegate. Self-detaches when this inspector is
        /// destroyed mid-validation (the async continuation still completes its own
        /// finally detach, which is a safe no-op).
        /// </summary>
        private void OnValidateTick()
        {
            if (this == null)
            {
                EditorApplication.update -= OnValidateTick;
                EditorUtility.ClearProgressBar();
                return;
            }

            if (EditorUtility.DisplayCancelableProgressBar(
                    kValidateProgressTitle, validatePhase, validateProgress))
            {
                validateCancelRequested = true;
            }

            Repaint();
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

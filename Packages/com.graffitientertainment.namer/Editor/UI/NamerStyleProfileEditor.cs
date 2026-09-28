using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Custom inspector for <see cref="NamerStyleProfile"/> implementing the 05 UI-SPEC
    /// section B component inventory exactly: the reference-list header (P1), slot rows
    /// with 64x64 thumbnails (P2), the +/– footer (P3), the count-honesty line (P4), the
    /// explicit Extract Style Stats button (P5, STYL-02), the Statistics block with the
    /// em-dash placeholder and staleness warning (P6), the five debounced parameter
    /// sliders (P7-P11) — 05.2-03 (STYL-11/R1) retired the palette luma knob: the
    /// palette transfer never touches tonality — and the Advanced
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
        private bool isFitting;
        private string fitError;

        /// <summary>
        /// Host Processor window of the embedded-inspector context (05.1 plan 03 P17):
        /// the exemplar source for Fit Look. Set by
        /// <see cref="NamerEditorWindow.EnsureProfileEditor"/> right after
        /// CreateEditor; null in the standalone Project-inspector context, which
        /// resolves any open window with a loadable source instead.
        /// </summary>
        internal NamerEditorWindow HostWindow;

        /// <summary>
        /// Pure prerequisite state of the P17 Fit Look button (05.1 plan 03):
        /// <see cref="Disabled"/> plus the locked prereq reason rendered as a
        /// miniLabel under the disabled button (null when enabled). Busy state is
        /// deliberately NOT part of this seam — the "Fitting…" label swap covers it;
        /// the seam is about prerequisites only.
        /// </summary>
        internal readonly struct FitLookButtonState
        {
            /// <summary>Creates the button state from the prereq evaluation.</summary>
            public FitLookButtonState(bool disabled, string reasonMiniLabel)
            {
                Disabled = disabled;
                ReasonMiniLabel = reasonMiniLabel;
            }

            /// <summary>True when the Fit Look button must render disabled.</summary>
            public bool Disabled { get; }

            /// <summary>Locked prereq reason shown under the disabled button; null when enabled.</summary>
            public string ReasonMiniLabel { get; }
        }

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
            DrawFittedPostLookBlock();
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

        /// <summary>
        /// P17 pure seam (05.1 plan 03, headless-testable): the Fit Look button is
        /// disabled while the profile carries no extracted statistics (locked
        /// not-extracted reason), while no exemplar window resolves (locked
        /// no-exemplar reason), or while the references carry no positive weight (the
        /// WR-01 mirror of NamerStylePostFit.Fit's zero-mass guard — the button
        /// explains itself instead of failing on click); enabled otherwise. Busy state
        /// is not part of the seam.
        /// </summary>
        internal static FitLookButtonState FitButtonState(NamerStyleProfile profile, bool exemplarAvailable)
        {
            if (profile == null || !profile.IsExtracted)
            {
                return new FitLookButtonState(
                    true, "Fit Look needs extracted statistics first — press Extract Style Stats.");
            }

            if (!exemplarAvailable)
            {
                return new FitLookButtonState(
                    true, "Fit Look uses the object loaded in the Processor window (Tools > NAMER > Processor) — load a Source there first.");
            }

            if (!HasPositiveReferenceWeight(profile))
            {
                return new FitLookButtonState(
                    true, "Fit Look needs at least one reference with weight above 0 — all slots empty or every weight 0 leaves nothing to fit.");
            }

            return new FitLookButtonState(false, null);
        }

        /// <summary>
        /// WR-01 (05.1 review): true when any assigned reference slot carries a weight
        /// above 0 — the profile-side mirror of the TotalWeight guard in
        /// <see cref="NamerStylePostFit.Fit"/>. Null slots are skipped everywhere;
        /// a missing weight entry falls back to the default weight (the shared
        /// read discipline of RunStylePostFit / ClampToCaps / ComputeFingerprint).
        /// </summary>
        private static bool HasPositiveReferenceWeight(NamerStyleProfile profile)
        {
            for (int i = 0; i < profile.References.Count; i++)
            {
                if (profile.References[i] == null)
                {
                    continue;
                }

                float weight = i < profile.ReferenceWeights.Count
                    ? profile.ReferenceWeights[i]
                    : NamerEditorConstants.DefaultReferenceWeight;
                if (weight > 0f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// P15-P20: the Fitted Post Look block — header + help line (P15), the
        /// fit-source status line (P16, D-03 one-fit-per-profile honesty), the explicit
        /// Fit Look button with the verbatim CTA tooltip and prereq reasons (P17, D-01:
        /// never on the debounce, never automatically on Process), the read-only
        /// fitted-parameter rows with "—" placeholders before the first fit (P18), and
        /// the fit staleness warning (P19, independent of the extract-staleness box).
        /// Read-only is deliberate: the artist-tweak surface is the generated Volume
        /// (D-02); the profile stays the regeneration truth.
        /// </summary>
        private void DrawFittedPostLookBlock()
        {
            EditorGUILayout.LabelField("Fitted Post Look", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "Fitted URP post parameters — regenerated into a Volume profile asset; this profile is the source of truth.",
                EditorStyles.miniLabel);

            // P16: fit-source status line (D-03).
            EditorGUILayout.LabelField(
                profile.HasPostFit
                    ? "Fitted from: " + profile.PostFitExemplarName + " — one fit per profile; refitting with another object replaces the scene look."
                    : "Not fitted — load a source in the Processor window, then press Fit Look.",
                EditorStyles.miniLabel);

            // P17: the button. Exemplar resolution per the planner decision — the host
            // window (embedded context), else the first open Processor window with a
            // loadable source; no candidate leaves the button disabled with the locked
            // no-exemplar reason.
            FitLookButtonState buttonState = FitButtonState(profile, ResolveExemplarWindow() != null);
            bool disabled = isFitting || buttonState.Disabled;
            using (new EditorGUI.DisabledScope(disabled))
            {
                if (GUILayout.Button(new GUIContent(
                        isFitting ? "Fitting…" : "Fit Look",
                        "Fits the reference look's global grade (white balance, post-exposure, contrast, saturation, master/RGB curves, bloom threshold) from the palette-mapped preview of the object currently loaded in the Processor window onto this profile, then regenerates the URP Volume asset. Explicit action — never runs on the slider debounce or on Process. Re-fitting replaces the fit and overwrites the generated Volume; hand-tweaks to the generated Volume are lost (the profile is the truth).")))
                {
                    RunFitLook();
                }
            }

            if (!isFitting && buttonState.ReasonMiniLabel != null)
            {
                EditorGUILayout.LabelField(buttonState.ReasonMiniLabel, EditorStyles.miniLabel);
            }

            if (!string.IsNullOrEmpty(fitError))
            {
                EditorGUILayout.HelpBox(fitError, MessageType.Error);
            }

            DrawFittedPostRows();

            // P19: fit staleness — fires independently of the extract-staleness box.
            if (profile.IsFitStale)
            {
                EditorGUILayout.HelpBox(
                    "References changed since the fit — Fit Look again to refresh the post look.",
                    MessageType.Warning);
            }

            EditorGUILayout.Space();
        }

        /// <summary>
        /// P17 exemplar resolution (05.1 planner decision): the host window when drawn
        /// embedded AND its Source is loaded, else the first open Processor window with
        /// a loadable source — the "one visible, honest source of exemplar truth"
        /// contract. The P16 line names the exemplar used after the fact. Null when no
        /// candidate resolves.
        /// </summary>
        private NamerEditorWindow ResolveExemplarWindow()
        {
            if (HostWindow != null && HostWindow.HasLoadableSource)
            {
                return HostWindow;
            }

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
        /// Runs one explicit Fit Look (D-01: synchronous, cancellable, never on the
        /// debounce): resolves the exemplar window, runs the window's fit seam inside
        /// try/catch, and on success writes the fit onto the profile via
        /// Undo/SetDirty + <see cref="NamerStylePostFit.ApplyToProfile"/>, regenerates
        /// the Volume asset (pinging it), and finally raises
        /// <see cref="NamerStyleProfile.RaiseChanged"/> so the debounced preview
        /// refresh sees the new fit. The companion-Volume write gates are pre-flighted
        /// (<see cref="NamerStyleVolumeWriter.PreflightWriteGate"/>, CR-01) BEFORE the
        /// profile is mutated, so a refusal never desyncs profile and Volume. Failure
        /// paths surface the UI-SPEC HelpBox copy.
        /// </summary>
        private void RunFitLook()
        {
            fitError = null;
            isFitting = true;
            try
            {
                NamerEditorWindow exemplarWindow = ResolveExemplarWindow();
                if (exemplarWindow == null)
                {
                    return;
                }

                NamerStylePostFitRunResult result;
                try
                {
                    result = exemplarWindow.RunStylePostFit(profile);
                }
                catch (Exception exception)
                {
                    fitError = "Fit failed: " + exception.Message;
                    return;
                }

                if (result.Cancelled)
                {
                    return;
                }

                if (result.NotExtracted)
                {
                    // Prereq reason path: the next repaint re-evaluates FitButtonState
                    // and renders the active locked reason under the disabled button.
                    return;
                }

                if (!string.IsNullOrEmpty(result.ErrorTextureName))
                {
                    fitError = "Fit failed: " + result.ErrorTextureName
                        + " could not be read. Check the texture's Read/Write import setting.";
                    return;
                }

                // CR-01 (05.1 review): pre-flight the companion-Volume write gates
                // BEFORE the profile is mutated, so a refusal (foreign asset at the
                // path, destination escape) leaves the profile untouched — profile
                // and generated Volume can never desync, and "Fit failed" never
                // reports after the fit itself was committed. The gate exempts the
                // fit's own companion from the overwrite opt-in (see
                // NamerStyleVolumeWriter.PreflightWriteGate), so re-fits succeed on
                // default settings as the P17 tooltip promises.
                NamerProcessorSettings settings = new NamerProcessorSettings();
                NamerStyleVolumeWriter.PreflightWriteGate(profile, settings);

                Undo.RecordObject(profile, "Fit Look");
                NamerStylePostFit.ApplyToProfile(profile, result.Params, result.ExemplarName);
                EditorUtility.SetDirty(profile);

                // D-02: regenerate the companion Volume from profile truth. Any
                // write-gate refusal surfaces here as the locked "Fit failed:
                // {message}" copy.
                try
                {
                    VolumeProfile volume = NamerStyleVolumeWriter.Write(profile, settings);
                    EditorGUIUtility.PingObject(volume);
                }
                catch (Exception exception)
                {
                    fitError = "Fit failed: " + exception.Message;
                }
            }
            finally
            {
                isFitting = false;
                NamerStyleProfile.RaiseChanged(profile);
            }
        }

        /// <summary>
        /// P18: read-only fitted-parameter rows in the Statistics idiom ("—" before
        /// the first fit). Each row's tooltip names the fit statistic behind it (von
        /// Kries / EV ratio / percentile spread / chroma ratio / histogram spec).
        /// Tonemapping and Bloom Intensity are constants (never fitted), so they never
        /// take the placeholder.
        /// </summary>
        private void DrawFittedPostRows()
        {
            bool fitted = profile.HasPostFit;
            EditorGUILayout.LabelField(
                new GUIContent("Tonemapping", "Fixed ACES — URP's curve is parameter-free; the fit shapes exposure/contrast/curves around it."),
                new GUIContent("ACES (default)"));
            EditorGUILayout.LabelField(
                new GUIContent("White Balance", "Von Kries fit of the source mean chromaticity onto the references' (temperature / tint)."),
                new GUIContent(fitted ? profile.PostTemperature.ToString("F1") + " / " + profile.PostTint.ToString("F1") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Post Exposure", "Mean-luma EV ratio of the tonemapped source onto the decoded references."),
                new GUIContent(fitted ? profile.PostExposureEv.ToString("F2") + " EV" : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Contrast", "LogC p90-p10 percentile-spread ratio between source and references."),
                new GUIContent(fitted ? profile.PostContrast.ToString("F2") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Saturation", "Perceptual mean-chroma ratio between source and references (plain URP Saturation)."),
                new GUIContent(fitted ? profile.PostSaturation.ToString("F2") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Bloom Threshold", "Reference highlight percentile (p95 of raw sRGB Max3), written gamma-space like URP's own slider."),
                new GUIContent(fitted ? profile.PostBloomThreshold.ToString("F2") : kStatPlaceholder));
            EditorGUILayout.LabelField(
                new GUIContent("Bloom Intensity", "Artist knob — defaulted, never fitted; tweak it on the generated Volume."),
                new GUIContent(NamerEditorConstants.DefaultBloomIntensity.ToString("F2") + " (tweak on the Volume)"));
            EditorGUILayout.LabelField(
                new GUIContent("Color Curves", "Histogram specification (CDF matching) of source luma/channels onto the references'."),
                new GUIContent(fitted ? "master + R/G/B, " + profile.PostMasterCurve.length + " keys" : kStatPlaceholder));
        }

        /// <summary>P7-P11: the five parameter sliders, each with its decision tooltip.</summary>
        private void DrawParameterSliders()
        {
            DrawSlider(
                new GUIContent("Style Strength", "Palette styling strength: 0 keeps the original look, 1 fully ramps hue toward the reference palette and boosts vibrance. Palette transfer never touches tonality — a Post Look asset owns tone. " + DebounceSuffix),
                profile.StyleStrength, 0f, 1f, "Set Style Strength",
                (edited, value) => edited.StyleStrength = value);
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

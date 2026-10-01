namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Shared constants for the Phase 3 asset-generation and editor-workflow layer.
    /// Centralizes the generated-asset label (D-04), the default destination (D-01),
    /// the prefix/suffix defaults (D-02), and the UI debounce interval (UI-SPEC) so no
    /// magic strings or magic numbers are inlined across the processor, generator, and
    /// window. Replicates the <c>Core/NamerConstants.cs</c> shape in the Editor namespace.
    /// </summary>
    public static class NamerEditorConstants
    {
        /// <summary>Asset label applied to every generated asset; the D-04 overwrite stamp.</summary>
        public const string GeneratedLabel = "NamerGenerated";

        /// <summary>
        /// Override tag stamped on generated materials recording the source asset identity
        /// (<c>"&lt;guid&gt;|&lt;localFileId&gt;"</c>) so a re-process resolves back to the
        /// original source material (D-04 idempotent regeneration).
        /// </summary>
        public const string SourceTag = "NamerSource";

        /// <summary>Default generated-output root under the project Assets/ folder (D-01).</summary>
        public const string DefaultDestination = "Assets/NAMERGenerated/";

        /// <summary>Default file-name prefix applied to generated assets (D-02).</summary>
        public const string DefaultPrefix = "";

        /// <summary>Default file-name suffix applied to generated assets (D-02).</summary>
        public const string DefaultSuffix = "_Namer";

        /// <summary>UI debounce interval in seconds (UI-SPEC; named constant, not a magic number).</summary>
        public const float DebounceSeconds = 0.3f;

        /// <summary>Default AO un-multiply strength (D-08; persisted in <see cref="NamerProcessorSettings"/>).</summary>
        public const float DefaultAoUnmultiplyStrength = 1f;

        /// <summary>Default AO blur radius in texels (D-11; 0 = off — a second separable pass distinct from the internal low-pass).</summary>
        public const float DefaultAoBlurRadius = 0f;

        /// <summary>Default AO strength (D-11; 1 = full AO, 0 = white/no AO).</summary>
        public const float DefaultAoStrength = 1f;

        /// <summary>Default AO contrast (D-11; 1 = identity, pivot 0.5).</summary>
        public const float DefaultAoContrast = 1f;

        /// <summary>Default roughness dip-depth (04.2 SHDR-02; pure-taste slider, research
        /// taste range 0.2-0.3 — 0 = keep the authored scalar unchanged). Extraction runs
        /// when no authored metallic/roughness map exists and the value is > 0; the
        /// removed-detail path additionally requires vertex-color decomposition to be
        /// enabled (otherwise the scalar roughness is used unchanged).</summary>
        public const float DefaultRoughnessExtractStrength = 0.25f;

        /// <summary>Default roughness dip-source popup index (04.2 UI-03; 0 = RemovedDetail default).</summary>
        public const int DefaultDipSource = 0;

        /// <summary>Default Write Residual Texture checkbox (04.2 VCOL-05; OFF = one-texture outcome).</summary>
        public const bool DefaultWriteResidual = false;

        /// <summary>Default vertex-color decomposition toggle (D-05: opt-in, OFF).</summary>
        public const bool DefaultDecompositionEnabled = false;

        /// <summary>Default roughness-extraction stage hard gate (DIP-01; true = opt-out gate, pipeline unchanged).</summary>
        public const bool DefaultRoughnessStageEnabled = true;

        /// <summary>Default AO un-multiply stage hard gate (DIP-01; true = opt-out gate, pipeline unchanged).</summary>
        public const bool DefaultAoStageEnabled = true;

        /// <summary>Default metallic-contribution shader gate (DIP-02; true = opt-out, shader unchanged).</summary>
        public const bool DefaultMetallicContributionEnabled = true;

        /// <summary>Default emissive-contribution shader gate (DIP-02; true = opt-out, shader unchanged).</summary>
        public const bool DefaultEmissiveContributionEnabled = true;

        /// <summary>Default reconstruction-error threshold (D-15).</summary>
        public const float DefaultErrorThreshold = 0.02f;

        /// <summary>Default percentile coverage target for the adaptive residual-resolution search (D-05: fraction of UV-covered texels that must reconstruct within the error threshold; 0.99).</summary>
        public const float DefaultCoverageTarget = 0.99f;

        /// <summary>Minimum coverage target the window slider writes (04.3 D-05); the persisted pref is clamped up to this floor on read so the slider display and Process always agree.</summary>
        public const float MinCoverageTarget = 0.90f;

        /// <summary>Default residual-resolution ladder index (D-17: 0 = Auto).</summary>
        public const int DefaultResidualResolution = 0;

        /// <summary>Minimum assigned reference images required by Extract Style Stats (05 UI-SPEC section C; STYL-01).</summary>
        public const int MinReferences = 3;

        /// <summary>Maximum reference-image slots on a style profile (05 UI-SPEC section C; STYL-01).</summary>
        public const int MaxReferences = 10;

        /// <summary>Default style strength (D-04; 0 = original, 1 = full palette snap).</summary>
        public const float DefaultStyleStrength = 0.5f;

        /// <summary>Default island-gated bilateral smoothing strength (D-03; 0 = off).</summary>
        public const float DefaultSmoothingStrength = 0f;

        /// <summary>Default cavity floor after AO contrast (D-08; 0 = no floor).</summary>
        public const float DefaultCavityFloor = 0f;

        /// <summary>Default normal-detail reduction (D-07; 0 = keep authored detail).</summary>
        public const float DefaultNormalDetailReduction = 0f;

        /// <summary>Default roughness simplification (D-07; 0 = off).</summary>
        public const float DefaultRoughnessSimplify = 0f;

        /// <summary>Default per-reference extraction weight (D-05 Advanced; weights apply at extract time).</summary>
        public const float DefaultReferenceWeight = 1f;

        /// <summary>Default hue mode popup index (D-05; 0 = Preserve, 1 = Replace).</summary>
        public const int DefaultHueMode = 0;

        /// <summary>Default fitted bloom threshold (05.1 STYL-08; 0.9 = URP Bloom's own default, gamma-space).</summary>
        public const float DefaultBloomThreshold = 0.9f;

        /// <summary>Default bloom intensity written onto the generated Volume (05.1 STYL-08; the artist knob — tweakable on the Volume, never fitted).</summary>
        public const float DefaultBloomIntensity = 1f;

        /// <summary>Maximum key count per fitted post curve (05.1; TextureCurve bakes to a 128-texel texture, so more keys buy nothing).</summary>
        public const int MaxCurveKeys = 16;

        /// <summary>Maximum fitted post-exposure magnitude in EV (05.1). Value-pinned to plan 01's private fit-side cap NamerStylePostFit.kMaxFittedPostExposureEv (same 10f) — two deliberate same-value definitions because the plans are wave-1 parallel and each must pass its own standalone Roslyn gate.</summary>
        public const float MaxFittedPostExposureEv = 10f;

        /// <summary>Maximum fitted bloom threshold (05.1; URP Bloom threshold is Min 0 — upper bound for sanitized hand edits).</summary>
        public const float MaxBloomThreshold = 10f;

        /// <summary>Maximum bloom intensity on the post-look asset's artist knob (05.2 UI-SPEC A9; upper bound for the slider and ClampToCaps).</summary>
        public const float MaxBloomIntensity = 10f;

        /// <summary>Tooltip suffix appended to every editable fitted-parameter tooltip on the post-look asset (05.2 UI-SPEC Copywriting — the debounce suffix is NOT used there).</summary>
        public const string PostLookParameterTooltipSuffix = "Writes to this asset (undoable); Regenerate Volume to update the scene look.";

        /// <summary>Default asset name for a newly created post look (05.2 UI-SPEC Copywriting; menu + create-and-select dialog).</summary>
        public const string PostLookDefaultAssetName = "New Namer Post Look";

        /// <summary>Title of the Processor window's create-and-select post-look dialog (05.2 UI-SPEC Copywriting).</summary>
        public const string PostLookCreateDialogTitle = "Create NAMER Post Look";

        /// <summary>Default asset name for a newly created merged Style Asset (05.3 UI-SPEC C-series Copywriting; menu path Assets/Create/NAMER/Style is the ONLY creation path, D-06/M1).</summary>
        public const string StyleAssetDefaultAssetName = "New Namer Style";

        /// <summary>Palette-source popup index: derive the palette from the DE-GRADED base colors (05.3 S12/STYL-14; the default source).</summary>
        public const int kPaletteSourceDeGraded = 0;

        /// <summary>Palette-source popup index: derive the palette from the RAW base colors, no de-grade applied (05.3 S12).</summary>
        public const int kPaletteSourceRaw = 1;

        /// <summary>Default palette source for a fresh Style Asset (05.3 S12 popup default: de-graded).</summary>
        public const int DefaultPaletteSource = kPaletteSourceDeGraded;

        /// <summary>
        /// Normalized knob-distance threshold above which the STYL-17 forward-validation
        /// mismatch surfaces (05.3 RESEARCH Open Question 5: honesty over precision —
        /// the mismatch is evidence, never an automatic change). Consumed by plan 03's
        /// NamerStyleDecomposer.ComputeValidationMeasure and rendered by plan 06's
        /// validation row; landed here by plan 02 so the wave-2 plans never edit this
        /// shared constants file.
        /// </summary>
        public const float kValidationMismatchThreshold = 0.10f;

        // -- Merge dialog (05.3 UI-SPEC M-series; every merge-dialog string lives
        // here — constants law; consumed by Editor/UI/NamerStyleAssetMerger.cs) ----

        /// <summary>Title of the merge EditorWindow's boldLabel row and of its save dialog (05.3 M3 locked copy).</summary>
        public const string MergeWindowTitle = "Merge NAMER Style Assets";

        /// <summary>Row label of the REQUIRED Style Profile ObjectField (05.3 M3).</summary>
        public const string MergeProfileFieldLabel = "Style Profile";

        /// <summary>Row label of the OPTIONAL Post Look ObjectField (05.3 M3; null = palette-only merge).</summary>
        public const string MergeLookFieldLabel = "Post Look";

        /// <summary>
        /// Merge-preview miniLabel line, with-look variant (05.3 M3 locked copy;
        /// {0} = profile name, {1} = profile assigned references, {2} = look name,
        /// {3} = look assigned references).
        /// </summary>
        public const string MergePreviewLineWithLookFormat =
            "Merges {0}: {1} references + palette + controls + {2}: fitted post look + {3} references into a new Style Asset.";

        /// <summary>
        /// Merge-preview miniLabel line, look-less variant (05.3 M3 locked copy;
        /// {0} = profile name, {1} = profile assigned references).
        /// </summary>
        public const string MergePreviewLineLookLessFormat =
            "Merges {0}: {1} references + palette + controls into a new Style Asset.";

        /// <summary>Merge button label (05.3 M3 locked copy; disabled while the profile is null or the cap Error shows).</summary>
        public const string MergeButtonLabel = "Merge into New Asset…";

        /// <summary>Suffix appended to the profile name for the save dialog's default asset name — "{profileName} Style" (05.3 M3 locked copy).</summary>
        public const string MergeSaveDefaultNameSuffix = " Style";

        /// <summary>File-extension argument of the merge save dialog (05.3 M3; the created asset is a ScriptableObject).</summary>
        public const string MergeSaveDialogExtension = "asset";

        /// <summary>Message line of the merge save dialog (05.3 M3; save-dialog chrome, not in-panel copy).</summary>
        public const string MergeSaveDialogMessage = "Create the merged NAMER Style Asset.";

        /// <summary>
        /// Cap-honesty Error copy (05.3 M3 locked; {0} = combined union count,
        /// {1} = <see cref="MaxReferences"/>). Rendered with the button disabled
        /// BEFORE any asset is created — the merge never drops references silently.
        /// </summary>
        public const string MergeCapErrorFormat =
            "Combined references ({0}) exceed the {1}-slot cap — remove references from the sources and try again; the merge never drops references silently.";

        /// <summary>Never-silent announcement console line, with-look variant (05.3 M4 locked copy; {0} = profile name, {1} = look name, {2} = created asset path).</summary>
        public const string MergeAnnouncementWithLookFormat = "NAMER: merged {0} + {1} into {2}.";

        /// <summary>Never-silent announcement console line, look-less variant (05.3 M4 locked copy; {0} = profile name, {1} = created asset path).</summary>
        public const string MergeAnnouncementLookLessFormat = "NAMER: merged {0} into {1}.";

        // -- Processor window W15-W18 (05.3 UI-SPEC Copywriting; the window's new
        // Style Asset copy lives here — constants law; consumed by
        // Editor/UI/NamerEditorWindow.cs) ------------------------------------------

        /// <summary>
        /// W15 Style Asset ObjectField tooltip (05.3 locked copy; {0} =
        /// <see cref="DebounceSeconds"/>). NO Create affordance anywhere in the
        /// panel (D-06) — creation is menu-only.
        /// </summary>
        public const string StyleAssetFieldTooltipFormat =
            "Attaches the NAMER Style Asset: its palette (closest-match albedo remap before the vertex-color build), "
                + "island-gated smoothing, and bake-side controls apply to the stylized previews and to every Process run; "
                + "its fitted post look drives the Stylized + Post view. Create it via Assets/Create/NAMER/Style — "
                + "decomposing, fitting, and tweaking live in the asset's own inspector. Recomputes the preview in memory {0} s "
                + "after the change — nothing is written to disk.";

        /// <summary>W18 popup-disabled hint, no asset attached (05.3 locked copy; menu-pointing, D-06).</summary>
        public const string StyleAssetAttachHint =
            "Attach a Style Asset to enable the stylized views — create one via Assets/Create/NAMER/Style.";

        /// <summary>W16 single stylized-lens fallback, attached but not decomposed (05.3 locked copy; {0} = asset name).</summary>
        public const string StyleAssetNotDecomposedFallbackFormat =
            "Not decomposed on {0} — the stylized views fall back to the NAMER preview. Press Decompose Style in the asset's inspector.";

        /// <summary>W18 attach-state honesty line, decomposed (05.3 locked copy; {0} = asset name, {1} = palette color count, {2} = positive-weight reference count).</summary>
        public const string StyleAssetStateDecomposedFormat =
            "{0} — decomposed ({1} palette colors, {2} weighted references)";

        /// <summary>W18 attach-state honesty line, not decomposed (05.3 locked copy; {0} = asset name).</summary>
        public const string StyleAssetStateNotDecomposedFormat = "{0} — not decomposed";

        // -- Merged Style Asset editor S-series (05.3 UI-SPEC Copywriting; every
        // merged-asset inspector string lives here — constants law; consumed by
        // Editor/UI/NamerStyleAssetEditor.cs) ---------------------------------------

        /// <summary>S1 header help line over the ONE shared weighted reference list (05.3 locked copy).</summary>
        public const string StyleAssetReferenceHelpLine =
            "One shared reference list drives both blocks — Decompose estimates the reference grade, then derives the de-graded palette and the fitted post look from it.";

        /// <summary>S2 empty-state heading for a fresh asset's reference list (05.3 locked copy).</summary>
        public const string StyleAssetReferencesEmptyTitle = "No reference images.";

        /// <summary>S2 empty-state instruction line (05.3 locked copy; 3–10 uses an en dash).</summary>
        public const string StyleAssetReferencesEmptyBody =
            "Add 3–10 reference images with the + button, assign graded screenshots, then Decompose Style.";

        /// <summary>S3 "+" footer-button tooltip (5.2 A3 donor, verbatim).</summary>
        public const string StyleAssetAddReferenceTooltip = "+ Add reference image";

        /// <summary>S3 "–" footer-button tooltip (5.2 A3 donor, verbatim; leading U+2212 minus).</summary>
        public const string StyleAssetRemoveReferenceTooltip = "− Remove last reference image";

        /// <summary>
        /// S4 count-honesty line (05.3 locked copy; {0} = assigned non-null slots,
        /// {1} = <see cref="MaxReferences"/>, {2} = <see cref="MinReferences"/>).
        /// </summary>
        public const string StyleAssetCountLineFormat =
            "{0} / {1} references assigned — Decompose Style needs at least {2} with weight above 0.";

        /// <summary>
        /// S7 Decompose-button prereq reason (05.3 locked copy; {0} =
        /// <see cref="MinReferences"/>). The ONLY Decompose prereq — no exemplar gate.
        /// </summary>
        public const string StyleAssetDecomposePrereqFormat =
            "Decompose Style needs at least {0} assigned references with weight above 0 — the grade estimate needs the list to have mass.";

        /// <summary>S7 button label (05.3 locked copy; never debounced).</summary>
        public const string StyleAssetDecomposeButtonLabel = "Decompose Style";

        /// <summary>S7 busy label swap while a decomposition runs (05.3 locked copy).</summary>
        public const string StyleAssetDecomposeBusyLabel = "Decomposing…";

        /// <summary>S7 button tooltip (05.3 locked copy, full).</summary>
        public const string StyleAssetDecomposeTooltip =
            "Decomposes the shared reference list with the model-free inverse of the NAMER fit math: estimates the reference grade "
                + "(exposure, white balance, tone curve, contrast, saturation, bloom), classifies pixels (albedo / shading / specular / "
                + "emissive / bloom) with a confidence mask, and recovers de-graded base colors. The palette block comes from the "
                + "de-graded base colors; the fitted post block comes from the grade estimate. Contaminated pixels (bloom, specular, "
                + "emissive, deep shadow, clipping, fog, transparency) carry zero/low palette weight. Explicit action — never runs "
                + "automatically. Re-decomposing replaces both blocks, overwrites hand-tweaked post parameters, and regenerates the "
                + "Volume; hand-tweaks to the generated Volume are lost (this asset is the truth).";

        /// <summary>
        /// S6 status line, decomposed primary variant (05.3 locked copy; {0} =
        /// positive-weight non-null reference count).
        /// </summary>
        public const string StyleAssetDecomposedPrimaryFormat =
            "Decomposed from {0} weighted references — one decomposition per asset; re-decomposing replaces the palette and the post look.";

        /// <summary>
        /// S6 status line, refined variant — the D-02 complement engaged and is NAMED,
        /// never silent (05.3 locked copy; {0} = positive-weight non-null reference
        /// count, {1} = <see cref="NamerStyleAsset.GradeSourceExemplarName"/>).
        /// </summary>
        public const string StyleAssetDecomposedRefinedFormat =
            "Decomposed from {0} weighted references — grade refined against {1} (Processor window render). Re-decomposing replaces the palette and the post look.";

        /// <summary>S6 status line, un-decomposed (05.3 locked copy; also the S15/S16 no-fit reason).</summary>
        public const string StyleAssetNotDecomposedStatus =
            "Not decomposed — Decompose Style fills the palette, the confidence stats, and the post parameters from the references; tweak them afterwards.";

        /// <summary>S8 error, unreadable reference (05.3 locked copy; {0} = texture name).</summary>
        public const string StyleAssetDecomposeUnreadableFormat =
            "Decompose failed: {0} could not be read. Check the texture's Read/Write import setting.";

        /// <summary>S8 error, unexpected decompose failure (05.3 locked copy; {0} = exception message).</summary>
        public const string StyleAssetDecomposeExceptionFormat = "Decompose failed: {0}";

        /// <summary>S9 stats block header (05.3 locked boldLabel row text).</summary>
        public const string StyleAssetStatsHeader = "Decomposition";

        /// <summary>S9 confidence row label (05.3 locked copy).</summary>
        public const string StyleAssetConfidentAlbedoLabel = "Confident Albedo Pixels";

        /// <summary>S9 confidence row tooltip (05.3 locked copy).</summary>
        public const string StyleAssetConfidentAlbedoTooltip =
            "Fraction of reference pixels classified as albedo with confidence — these carry full palette weight (STYL-13).";

        /// <summary>S9 contamination row label (05.3 locked copy).</summary>
        public const string StyleAssetContaminatedLabel = "Contaminated Pixels";

        /// <summary>S9 contamination row tooltip (05.3 locked copy).</summary>
        public const string StyleAssetContaminatedTooltip =
            "Bloom, specular highlights, emissive pixels, deep shadows, clipping, fog, and transparent effects — zero/low palette weight so the palette estimates what the artist put in the material (D-07).";

        /// <summary>S9 row-value placeholder before the first decomposition (05.3 locked copy; em dash).</summary>
        public const string StyleAssetStatsPlaceholder = "—";

        /// <summary>S9/S13 percent scale for whole-percent row values (named constant — no magic numbers).</summary>
        public const float StyleAssetPercentScale = 100f;

        /// <summary>S13 hue-degree scale for the "H{h}°" palette value text (named constant — no magic numbers).</summary>
        public const float StyleAssetHueDegreeRange = 360f;

        /// <summary>S10 decomposition-staleness Warning (05.3 locked copy).</summary>
        public const string StyleAssetDecompositionStaleWarning =
            "References changed since the decomposition — Decompose Style again to refresh the palette and the post look.";

        /// <summary>S10 palette-source staleness Warning (05.3 locked copy).</summary>
        public const string StyleAssetPaletteSourceStaleWarning =
            "Palette source changed since the last decomposition — Decompose Style again to refresh the palette.";

        /// <summary>S11 palette block header (05.3 locked boldLabel row text).</summary>
        public const string StyleAssetPaletteHeader = "Palette";

        /// <summary>S11 state line naming the source the CURRENT palette was extracted under — de-graded (05.3 locked copy).</summary>
        public const string StyleAssetPaletteFromDeGraded =
            "From de-graded base colors — the estimated grade is removed before palette extraction.";

        /// <summary>S11 state line, raw variant (05.3 locked copy).</summary>
        public const string StyleAssetPaletteFromRaw =
            "From raw pixels — the reference grade stays baked into the palette (shipped STYL-02 behavior).";

        /// <summary>S12 popup label (05.3 locked copy).</summary>
        public const string StyleAssetPaletteSourceLabel = "Palette Source";

        /// <summary>S12 popup entries, index-parallel to kPaletteSourceDeGraded/kPaletteSourceRaw (05.3 locked copy).</summary>
        public static readonly string[] StyleAssetPaletteSourceOptions = { "De-graded Base Colors", "Raw Pixels" };

        /// <summary>S12 popup tooltip (05.3 locked copy, full — D-04 switch semantics).</summary>
        public const string StyleAssetPaletteSourceTooltip =
            "De-graded Base Colors removes the estimated reference grade before palette extraction, so vertex colors carry what the "
                + "artist put in the material (default). Raw Pixels is the shipped STYL-02 behavior — the grade stays baked into the "
                + "palette. The grade is applied exactly once — de-graded at extract OR Volume at render, never doubled; switching "
                + "source requires Decompose Style again.";

        /// <summary>S13 per-row label (05.3 locked copy; {0} = 1-based palette color index).</summary>
        public const string StyleAssetPaletteColorLabelFormat = "Color #{0}";

        /// <summary>S13 per-row HSL value text (05.3 locked copy; {0} = hue degrees, {1} = saturation %, {2} = luma %).</summary>
        public const string StyleAssetPaletteColorValueFormat = "H{0}° S{1}% L{2}%";

        /// <summary>S13 count line above the rows (05.3 locked copy; {0} = palette color count).</summary>
        public const string StyleAssetPaletteCountFormat = "{0} palette colors";

        /// <summary>S13 empty state (05.3 locked copy).</summary>
        public const string StyleAssetPaletteEmptyState = "No palette yet — Decompose Style extracts it.";

        /// <summary>S5 per-reference weight tooltip on the merged asset — the decomposition reads the weights (05 D-05 copy, adapted from extraction/fit to decomposition; no debounce sentence — weight edits never recompute the preview).</summary>
        public const string StyleAssetReferenceWeightTooltip =
            "Per-reference decomposition weight: 0 excludes this reference from the decomposition; weights apply at decompose time (D-05).";

        // -- S14 palette-application sliders (05 copy re-homed onto the merged asset;
        // debounce-suffix prefixes — the suffix sentence itself is appended by the
        // editor's DebounceSuffix property, the two donor editors' idiom) ------------

        /// <summary>S14 Style Strength tooltip prefix (05 locked copy; the Style Strength amendment is in — "the fitted post look owns tone").</summary>
        public const string StyleAssetStyleStrengthTooltipPrefix =
            "Palette styling strength: 0 keeps the original look, 1 fully ramps hue toward the reference palette and boosts vibrance. Palette transfer never touches tonality — the fitted post look owns tone. ";

        /// <summary>S14 Smoothing tooltip prefix (05 locked copy).</summary>
        public const string StyleAssetSmoothingTooltipPrefix =
            "Island-gated bilateral smoothing strength: reduces photographic noise without crossing UV islands, seams, or decals (D-03, STYL-04). ";

        /// <summary>S14 Cavity Floor tooltip prefix (05 locked copy).</summary>
        public const string StyleAssetCavityFloorTooltipPrefix =
            "Applied after AO contrast on the remapped path including authored AO: keeps contrast from crushing cavities to black (D-08, STYL-05). ";

        /// <summary>S14 Normal Detail tooltip prefix (05 locked copy).</summary>
        public const string StyleAssetNormalDetailTooltipPrefix =
            "0 keeps authored detail; reduces high-frequency normal variation bake-side before octahedral encode (D-07, STYL-06). ";

        /// <summary>S14 Roughness Simplify tooltip prefix (05 locked copy).</summary>
        public const string StyleAssetRoughnessSimplifyTooltipPrefix =
            "0 = off; simplifies roughness bake-side after the roughness-dip adoption, before 6-bit quantization (D-07, STYL-06). ";

        /// <summary>S5 Hue Mode popup tooltip prefix (05 locked copy, verbatim incl. trailing space before the debounce sentence).</summary>
        public const string StyleAssetHueModeTooltipPrefix =
            "Preserve keeps the source hue; Replace adopts the reference palette hue (STYL-03) — the replacement ramp stays governed by Style Strength. ";

        // -- S15/S16 carried 5.2 post/Volume copy (retargeted onto the merged asset;
        // the legacy NamerPostLookEditor keeps its frozen inline copies — out of this
        // plan's file list) -----------------------------------------------------------

        /// <summary>S15 block header (5.2 A9 locked copy, carried).</summary>
        public const string StyleAssetPostBlockHeader = "Fitted Post Look";

        /// <summary>S15 constant Tonemapping row label (5.2 locked copy, carried).</summary>
        public const string StyleAssetTonemappingLabel = "Tonemapping";

        /// <summary>S15 constant Tonemapping row tooltip (5.2 locked copy, carried).</summary>
        public const string StyleAssetTonemappingTooltip =
            "Fixed ACES — URP's curve is parameter-free; the fit shapes exposure/contrast/curves around it.";

        /// <summary>S15 constant Tonemapping row value (5.2 locked copy, carried).</summary>
        public const string StyleAssetTonemappingValue = "ACES (default)";

        /// <summary>S15 White Balance slider tooltip prefix — shared by Temperature and Tint (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetWhiteBalanceTooltipPrefix =
            "Von Kries fit of the source mean chromaticity onto the references' (temperature / tint). ";

        /// <summary>S15 Post Exposure tooltip prefix (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetPostExposureTooltipPrefix =
            "Mean-luma EV ratio of the tonemapped source onto the decoded references. ";

        /// <summary>S15 Contrast tooltip prefix (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetContrastTooltipPrefix =
            "LogC p90-p10 percentile-spread ratio between source and references. ";

        /// <summary>S15 Saturation tooltip prefix (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetSaturationTooltipPrefix =
            "Perceptual mean-chroma ratio between source and references (plain URP Saturation). ";

        /// <summary>S15 Bloom Threshold tooltip prefix (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetBloomThresholdTooltipPrefix =
            "Reference highlight percentile (p95 of raw sRGB Max3), written gamma-space like URP's own slider. ";

        /// <summary>S15 Bloom Intensity tooltip prefix (5.2 copy, carried — the artist knob, never fitted).</summary>
        public const string StyleAssetBloomIntensityTooltipPrefix =
            "Artist knob — defaulted, never fitted; tweak it here, then Regenerate Volume. ";

        /// <summary>S15 shared curve-field tooltip prefix (5.2 fit-statistic copy, carried).</summary>
        public const string StyleAssetCurvesTooltipPrefix =
            "Histogram specification (CDF matching) of source luma/channels onto the references'. ";

        /// <summary>S16 block header (5.2 A11 locked copy, carried).</summary>
        public const string StyleAssetVolumeBlockHeader = "Volume";

        /// <summary>S16 status line, fitted (5.2 A11 locked copy, carried; {0} = Volume file name).</summary>
        public const string StyleAssetVolumeStatusFormat =
            "Volume: {0} — generated from this asset; hand-tweaks to it are lost on re-fit or regeneration.";

        /// <summary>S16 status line, no fit yet (5.2 A11 copy, retargeted Fit Look → Decompose Style).</summary>
        public const string StyleAssetNoVolumeStatus = "No Volume yet — Decompose Style generates one.";

        /// <summary>S16 Regenerate Volume button label (5.2 A12 locked copy, carried; never debounced).</summary>
        public const string StyleAssetRegenerateVolumeLabel = "Regenerate Volume";

        /// <summary>S16 Regenerate Volume tooltip (5.2 A12 locked copy, carried).</summary>
        public const string StyleAssetRegenerateVolumeTooltip =
            "Rebuilds this asset's generated URP Volume profile from the current parameters. Overwrites the generated Volume — hand-tweaks to it are lost. For a per-scene look, duplicate this asset and tweak the copy.";

        /// <summary>S16 staleness Warning (5.2 A13 locked copy, carried).</summary>
        public const string StyleAssetVolumeStaleWarning =
            "Asset parameters changed since the Volume was generated — Regenerate Volume to update the scene look.";

        /// <summary>S16 per-scene hint (5.2 A14 locked copy, carried).</summary>
        public const string StyleAssetPerSceneHint =
            "For a per-scene look, duplicate this asset and tweak the copy — each asset owns its own Volume.";

        /// <summary>S16/S8 error when the (re)generation write fails (5.2 locked copy, carried; {0} = message).</summary>
        public const string StyleAssetVolumeRegenerationFailedFormat = "Volume regeneration failed: {0}";

        // -- S17 Forward Validation (STYL-17/D-03: evidence only, never an automatic
        // change) -----------------------------------------------------------------------

        /// <summary>S17 block header (05.3 locked boldLabel row text).</summary>
        public const string StyleAssetValidationHeader = "Forward Validation";

        /// <summary>S17 button label (05.3 locked copy; never debounced).</summary>
        public const string StyleAssetValidateButtonLabel = "Validate Look";

        /// <summary>S17 busy label swap while a validation runs (05.3 locked copy).</summary>
        public const string StyleAssetValidateBusyLabel = "Validating…";

        /// <summary>S17 button tooltip — includes the LUT-unlock notice (05.3 locked copy, full).</summary>
        public const string StyleAssetValidateTooltip =
            "Renders the NAMER decomposition (vertex colors, packed surface, recovered Volume) of the object loaded in the Processor "
                + "window and compares it against the reference style. Visible residual mismatch is the unlock condition for the "
                + "3D-LUT fallback (locked by the 5.1 parametric-knobs decision). Explicit action — never runs automatically.";

        /// <summary>S17 prereq reason 1 — not decomposed (05.3 locked copy; beats reason 2, LOCKED order).</summary>
        public const string StyleAssetValidateNeedsDecompositionReason =
            "Validate Look needs a decomposition first — press Decompose Style.";

        /// <summary>S17 prereq reason 2 — no exemplar window with a loadable source (05.3 locked copy).</summary>
        public const string StyleAssetValidateNoExemplarReason =
            "Validate Look renders the object loaded in the Processor window (Tools > NAMER/Processor) — load a Source there first.";

        /// <summary>S17 error wrapper for validation failures (05.3 locked copy; {0} = message).</summary>
        public const string StyleAssetValidationFailedFormat = "Validation failed: {0}";

        /// <summary>
        /// S17 error detail under the wrapper when the exemplar window reports no
        /// stylized output (adapter-composed; the UI-SPEC locks only the wrapper).
        /// </summary>
        public const string StyleAssetValidationNoStylizedOutputMessage =
            "the Processor window is not rendering this asset's stylized output — attach it there and let the preview recompute";

        /// <summary>
        /// S17 error detail under the wrapper when a reference texture could not be
        /// read (the locked unreadable sentence, reused as the message; adapter-composed wrapper).
        /// </summary>
        public const string StyleAssetValidationUnreadableMessageFormat =
            "{0} could not be read. Check the texture's Read/Write import setting.";

        /// <summary>S17 within-tolerance result line (05.3 locked copy; {0} = measure string).</summary>
        public const string StyleAssetValidationWithinToleranceFormat =
            "Validation: reference match within tolerance ({0}).";

        /// <summary>S17 mismatch Warning — the STYL-17 LUT-unlock evidence, verbatim (05.3 locked copy; {0} = measure string).</summary>
        public const string StyleAssetValidationMismatchWarningFormat =
            "Validation: visible residual mismatch ({0}) — the parametric post look does not fully reproduce the reference grade. "
                + "This is the unlock evidence for the 3D-LUT fallback (locked by the 5.1 decision); nothing changes on this asset "
                + "until that decision is revisited.";

        /// <summary>
        /// S17 persisted-result discriminator: a stored ValidationResult starting with
        /// this prefix renders as the mismatch Warning HelpBox (everything else renders
        /// as the within-tolerance miniLabel).
        /// </summary>
        public const string StyleAssetValidationMismatchPrefix = "Validation: visible residual mismatch";

        /// <summary>S17 stale-result line (05.3 locked copy).</summary>
        public const string StyleAssetValidationStale =
            "Validation stale — decomposed or references changed since the last validation; Validate Look again.";
    }
}

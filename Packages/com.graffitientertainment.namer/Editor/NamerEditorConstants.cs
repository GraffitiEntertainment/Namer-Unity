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
    }
}

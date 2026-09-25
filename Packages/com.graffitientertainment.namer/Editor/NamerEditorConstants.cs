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

        /// <summary>Default tone match (quick 260924-hk7; 0 = preserve source tonality — the redesign's safe default, so old profile assets deserialize ToneMatch to 0 automatically).</summary>
        public const float DefaultToneMatch = 0f;

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
    }
}

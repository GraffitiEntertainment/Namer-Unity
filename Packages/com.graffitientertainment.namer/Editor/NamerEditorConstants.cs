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
    }
}

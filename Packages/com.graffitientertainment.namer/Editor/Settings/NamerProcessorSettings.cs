using UnityEditor;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// User-scoped, EditorPrefs-backed settings for the NAMER processor (D-03).
    /// Destination / prefix / suffix / overwrite persist across sessions without any
    /// serialized settings asset (zero asset noise). Setters write through to
    /// <see cref="EditorPrefs"/> immediately; getters read with the locked defaults
    /// from <see cref="NamerEditorConstants"/>.
    /// </summary>
    public sealed class NamerProcessorSettings
    {
        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteGeneratedKey = "NamerProcessor.OverwriteGenerated";
        private const string AoUnmultiplyStrengthKey = "NamerProcessor.AoUnmultiplyStrength";
        private const string AoBlurRadiusKey = "NamerProcessor.AoBlurRadius";
        private const string AoStrengthKey = "NamerProcessor.AoStrength";
        private const string AoContrastKey = "NamerProcessor.AoContrast";
        private const string DecompositionEnabledKey = "NamerProcessor.DecompositionEnabled";
        private const string ErrorThresholdKey = "NamerProcessor.ErrorThreshold";
        private const string ResidualResolutionKey = "NamerProcessor.ResidualResolution";

        /// <summary>Generated-output root folder (D-01 default: Assets/NAMERGenerated/).</summary>
        public string Destination
        {
            get { return EditorPrefs.GetString(DestinationKey, NamerEditorConstants.DefaultDestination); }
            set { EditorPrefs.SetString(DestinationKey, value); }
        }

        /// <summary>File-name prefix applied to generated assets (D-02).</summary>
        public string Prefix
        {
            get { return EditorPrefs.GetString(PrefixKey, NamerEditorConstants.DefaultPrefix); }
            set { EditorPrefs.SetString(PrefixKey, value); }
        }

        /// <summary>File-name suffix applied to generated assets (D-02).</summary>
        public string Suffix
        {
            get { return EditorPrefs.GetString(SuffixKey, NamerEditorConstants.DefaultSuffix); }
            set { EditorPrefs.SetString(SuffixKey, value); }
        }

        /// <summary>Whether existing NamerGenerated-stamped assets may be overwritten (D-04).</summary>
        public bool OverwriteGenerated
        {
            get { return EditorPrefs.GetBool(OverwriteGeneratedKey, false); }
            set { EditorPrefs.SetBool(OverwriteGeneratedKey, value); }
        }

        /// <summary>AO un-multiply strength applied during generation (D-08; drives the live preview).</summary>
        public float AoUnmultiplyStrength
        {
            get { return EditorPrefs.GetFloat(AoUnmultiplyStrengthKey, NamerEditorConstants.DefaultAoUnmultiplyStrength); }
            set { EditorPrefs.SetFloat(AoUnmultiplyStrengthKey, value); }
        }

        /// <summary>AO blur radius in texels (D-11; 0 = off — the user blur pass).</summary>
        public float AoBlurRadius
        {
            get { return EditorPrefs.GetFloat(AoBlurRadiusKey, NamerEditorConstants.DefaultAoBlurRadius); }
            set { EditorPrefs.SetFloat(AoBlurRadiusKey, value); }
        }

        /// <summary>AO strength (D-11; 1 = full AO, 0 = white/no AO).</summary>
        public float AoStrength
        {
            get { return EditorPrefs.GetFloat(AoStrengthKey, NamerEditorConstants.DefaultAoStrength); }
            set { EditorPrefs.SetFloat(AoStrengthKey, value); }
        }

        /// <summary>AO contrast (D-11; 1 = identity, pivot 0.5).</summary>
        public float AoContrast
        {
            get { return EditorPrefs.GetFloat(AoContrastKey, NamerEditorConstants.DefaultAoContrast); }
            set { EditorPrefs.SetFloat(AoContrastKey, value); }
        }

        /// <summary>Whether vertex-color decomposition runs during Process (D-05; default OFF).</summary>
        public bool DecompositionEnabled
        {
            get { return EditorPrefs.GetBool(DecompositionEnabledKey, NamerEditorConstants.DefaultDecompositionEnabled); }
            set { EditorPrefs.SetBool(DecompositionEnabledKey, value); }
        }

        /// <summary>Max reconstruction error before a residual is required (D-15; default 0.02).</summary>
        public float ErrorThreshold
        {
            get { return EditorPrefs.GetFloat(ErrorThresholdKey, NamerEditorConstants.DefaultErrorThreshold); }
            set { EditorPrefs.SetFloat(ErrorThresholdKey, value); }
        }

        /// <summary>Residual resolution popup index (D-17; 0 = Auto, else ladder index).</summary>
        public int ResidualResolution
        {
            get { return EditorPrefs.GetInt(ResidualResolutionKey, NamerEditorConstants.DefaultResidualResolution); }
            set { EditorPrefs.SetInt(ResidualResolutionKey, value); }
        }
    }
}

using UnityEditor;
using UnityEngine;

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
        private const string CoverageTargetKey = "NamerProcessor.CoverageTarget";
        private const string ResidualResolutionKey = "NamerProcessor.ResidualResolution";
        private const string RoughnessExtractStrengthKey = "NamerProcessor.RoughnessExtractStrength";
        private const string DipSourceKey = "NamerProcessor.DipSource";
        private const string WriteResidualKey = "NamerProcessor.WriteResidual";
        private const string RoughnessStageEnabledKey = "NamerProcessor.RoughnessStageEnabled";
        private const string AoStageEnabledKey = "NamerProcessor.AoStageEnabled";
        private const string MetallicContributionEnabledKey = "NamerProcessor.MetallicContributionEnabled";
        private const string EmissiveContributionEnabledKey = "NamerProcessor.EmissiveContributionEnabled";
        private const string FoldoutSourceKey = "NamerProcessor.FoldoutSource";
        private const string FoldoutPreviewKey = "NamerProcessor.FoldoutPreview";
        private const string FoldoutRoughnessExtractionKey = "NamerProcessor.FoldoutRoughnessExtraction";
        private const string FoldoutAoKey = "NamerProcessor.FoldoutAo";
        private const string FoldoutDecompositionKey = "NamerProcessor.FoldoutDecomposition";
        private const string FoldoutOutputKey = "NamerProcessor.FoldoutOutput";

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

        /// <summary>Hard stage gate for roughness extraction (DIP-01; default ON = pipeline unchanged).</summary>
        public bool RoughnessStageEnabled
        {
            get { return EditorPrefs.GetBool(RoughnessStageEnabledKey, NamerEditorConstants.DefaultRoughnessStageEnabled); }
            set { EditorPrefs.SetBool(RoughnessStageEnabledKey, value); }
        }

        /// <summary>Hard stage gate for AO un-multiply (DIP-01; default ON = pipeline unchanged).</summary>
        public bool AoStageEnabled
        {
            get { return EditorPrefs.GetBool(AoStageEnabledKey, NamerEditorConstants.DefaultAoStageEnabled); }
            set { EditorPrefs.SetBool(AoStageEnabledKey, value); }
        }

        /// <summary>Shader-only gate for the metallic contribution (DIP-02; default ON = shader unchanged).</summary>
        public bool MetallicContributionEnabled
        {
            get { return EditorPrefs.GetBool(MetallicContributionEnabledKey, NamerEditorConstants.DefaultMetallicContributionEnabled); }
            set { EditorPrefs.SetBool(MetallicContributionEnabledKey, value); }
        }

        /// <summary>Shader-only gate for the emissive contribution (DIP-02; default ON = shader unchanged).</summary>
        public bool EmissiveContributionEnabled
        {
            get { return EditorPrefs.GetBool(EmissiveContributionEnabledKey, NamerEditorConstants.DefaultEmissiveContributionEnabled); }
            set { EditorPrefs.SetBool(EmissiveContributionEnabledKey, value); }
        }

        /// <summary>Max reconstruction error before a residual is required (D-15; default 0.02).</summary>
        public float ErrorThreshold
        {
            get { return EditorPrefs.GetFloat(ErrorThresholdKey, NamerEditorConstants.DefaultErrorThreshold); }
            set { EditorPrefs.SetFloat(ErrorThresholdKey, value); }
        }

        /// <summary>Coverage target for the percentile residual-resolution gate — the fraction of UV-covered texels that must stay within the error threshold (D-05; default 0.99). Out-of-range persisted values are clamped to the slider range on read so the window preview and Process always agree.</summary>
        public float CoverageTarget
        {
            get { return Mathf.Clamp(EditorPrefs.GetFloat(CoverageTargetKey, NamerEditorConstants.DefaultCoverageTarget), NamerEditorConstants.MinCoverageTarget, 1f); }
            set { EditorPrefs.SetFloat(CoverageTargetKey, value); }
        }

        /// <summary>Residual resolution popup index (D-17; 0 = Auto, else ladder index).</summary>
        public int ResidualResolution
        {
            get { return EditorPrefs.GetInt(ResidualResolutionKey, NamerEditorConstants.DefaultResidualResolution); }
            set { EditorPrefs.SetInt(ResidualResolutionKey, value); }
        }

        /// <summary>Roughness dip-depth slider (04.2 SHDR-02; 0 = keep the authored scalar, default 0.25).</summary>
        public float RoughnessExtractStrength
        {
            get { return EditorPrefs.GetFloat(RoughnessExtractStrengthKey, NamerEditorConstants.DefaultRoughnessExtractStrength); }
            set { EditorPrefs.SetFloat(RoughnessExtractStrengthKey, value); }
        }

        /// <summary>Roughness dip-source popup index (04.2 UI-03; 0 = RemovedDetail default, 1 = SobelEdge).</summary>
        public int DipSource
        {
            get { return EditorPrefs.GetInt(DipSourceKey, NamerEditorConstants.DefaultDipSource); }
            set { EditorPrefs.SetInt(DipSourceKey, value); }
        }

        /// <summary>Write Residual Texture checkbox (04.2 VCOL-05; default OFF = one-texture outcome).</summary>
        public bool WriteResidual
        {
            get { return EditorPrefs.GetBool(WriteResidualKey, NamerEditorConstants.DefaultWriteResidual); }
            set { EditorPrefs.SetBool(WriteResidualKey, value); }
        }

        /// <summary>Source foldout open/closed state (D-07; Source defaults OPEN so the selection is visible).</summary>
        public bool FoldoutSource
        {
            get { return EditorPrefs.GetBool(FoldoutSourceKey, true); }
            set { EditorPrefs.SetBool(FoldoutSourceKey, value); }
        }

        /// <summary>Preview/Debug foldout open/closed state (D-07; default collapsed).</summary>
        public bool FoldoutPreview
        {
            get { return EditorPrefs.GetBool(FoldoutPreviewKey, false); }
            set { EditorPrefs.SetBool(FoldoutPreviewKey, value); }
        }

        /// <summary>Roughness Extraction foldout open/closed state (D-07; default collapsed).</summary>
        public bool FoldoutRoughnessExtraction
        {
            get { return EditorPrefs.GetBool(FoldoutRoughnessExtractionKey, false); }
            set { EditorPrefs.SetBool(FoldoutRoughnessExtractionKey, value); }
        }

        /// <summary>AO foldout open/closed state (D-07; default collapsed).</summary>
        public bool FoldoutAo
        {
            get { return EditorPrefs.GetBool(FoldoutAoKey, false); }
            set { EditorPrefs.SetBool(FoldoutAoKey, value); }
        }

        /// <summary>Decomposition foldout open/closed state (D-07; default collapsed).</summary>
        public bool FoldoutDecomposition
        {
            get { return EditorPrefs.GetBool(FoldoutDecompositionKey, false); }
            set { EditorPrefs.SetBool(FoldoutDecompositionKey, value); }
        }

        /// <summary>Output foldout open/closed state (D-07; default collapsed).</summary>
        public bool FoldoutOutput
        {
            get { return EditorPrefs.GetBool(FoldoutOutputKey, false); }
            set { EditorPrefs.SetBool(FoldoutOutputKey, value); }
        }
    }
}

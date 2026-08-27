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
    }
}

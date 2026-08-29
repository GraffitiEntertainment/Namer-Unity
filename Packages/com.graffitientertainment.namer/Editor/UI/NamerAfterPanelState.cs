namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Generated/live flip state for the processor window's After panel (UI-02 / UI-03).
    /// The After panel prefers the generated <c>.mat</c> when one exists for the selection
    /// and the user has not started tweaking; the moment an AO slider or the occluder
    /// changes, <see cref="MarkTweaking"/> flips the panel to the live in-memory preview,
    /// and the next successful Process calls <see cref="Reset"/> to flip back to generated.
    /// </summary>
    public sealed class NamerAfterPanelState
    {
        /// <summary>True when a generated material exists for the current selection.</summary>
        public bool GeneratedAvailable { get; set; }

        /// <summary>True after an AO slider/occluder change until the next successful Process.</summary>
        public bool Tweaking { get; private set; }

        /// <summary>The After panel should show the generated material.</summary>
        public bool PreferGenerated
        {
            get { return GeneratedAvailable && !Tweaking; }
        }

        /// <summary>Marks the state as tweaking (flip to live preview).</summary>
        public void MarkTweaking()
        {
            Tweaking = true;
        }

        /// <summary>Resets the tweaking flag (flip back to generated).</summary>
        public void Reset()
        {
            Tweaking = false;
        }
    }
}

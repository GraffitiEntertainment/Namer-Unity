namespace GraffitiEntertainment.Namer.Core
{
    /// <summary>
    /// Bit masks and thresholds for the NAMER packed surface format.
    /// These are the single source of truth (D-02, D-03) for the alpha-channel
    /// layout: bit 7 = metallic, bit 6 = emissive, bits 0-5 = 6-bit roughness.
    /// Mirrored manually in HLSL (plan 01-02) and shared by the GPU compute path.
    /// </summary>
    public static class NamerConstants
    {
        /// <summary>Alpha bit 7 — metallic flag.</summary>
        public const byte MetallicBit = 0x80;

        /// <summary>Alpha bit 6 — emissive flag.</summary>
        public const byte EmissiveBit = 0x40;

        /// <summary>Alpha bits 0-5 — 6-bit roughness value.</summary>
        public const byte RoughnessMask = 0x3F;

        /// <summary>Strict threshold (> not >=) for setting the metallic bit.</summary>
        public const float MetallicThreshold = 0.5f;

        /// <summary>Strict threshold (> not >=) for setting the emissive bit.</summary>
        public const float EmissiveThreshold = 0.1f;

        /// <summary>One 6-bit roughness quantization step (1 / 63).</summary>
        public const float RoughnessStep = 1.0f / 63.0f;
    }
}

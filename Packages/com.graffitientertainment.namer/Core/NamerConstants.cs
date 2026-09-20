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

        /// <summary>Number of distinct 6-bit roughness quantization levels (2^6 - 1).</summary>
        public const float RoughnessLevels = 63.0f;

        /// <summary>Byte scale for normalizing packed alpha bytes to/from [0,1].</summary>
        public const float AlphaByteScale = 255.0f;

        /// <summary>Numeric epsilon for divide guards shared by encode/decode.</summary>
        public const float Epsilon = 1e-6f;

        /// <summary>Un-multiply divisor floor for extracted/baked AO (D-09); divisor-side only — the packed B stores the raw AO.</summary>
        public const float AoFloor = 0.1f;

        /// <summary>Vertex-color interpolation floor for the residual quotient (D-01); prevents divide blow-up at near-zero vcInterp. Mirrored manually in HLSL (NAMERDecomp.hlsl).</summary>
        public const float VcFloor = 1e-3f;

        /// <summary>CSDespike limit (debug session residual-missing-triangles): a residual channel whose interpolated vc quantized to ZERO (vc &lt;= VcFloor, the total-loss zone between two black verts) and which sits above this factor times the SECOND-SMALLEST of its 4 clamp-to-edge neighbors is a quotient spike and is replaced by that value. The quantized-zero gate protects the legitimate low-vc band (vc &gt;= 1/255 — its quotient is meaningful; collapsing it toward identity would paint a dark line). Second-smallest, not the median of 4, because the median is blind to 1-texel spike runs (a collinear spiky neighbor pulls it to ~half the spike). 4 keeps similar-neighbor values untouched while collapsing zero-vc spikes (hundreds against a reference of a few).</summary>
        public const float ResidualSpikeFactor = 4f;

        /// <summary>CSResidual dilation radius in texels (debug session residual-missing-triangles, seam-step artifact): uncovered atlas texels within this Chebyshev radius of covered texels take the mean covered residual, so bilinear sampling at UV island borders never mixes a real residual with the identity 1.0 (measured 0.21-vs-1.0 step rendered as a 1px orange line). Standard atlas padding; single pass over the static coverage mask.</summary>
        public const int ResidualDilateRadius = 2;
    }
}

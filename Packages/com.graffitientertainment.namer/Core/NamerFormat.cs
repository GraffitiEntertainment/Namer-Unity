using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Core
{
    /// <summary>
    /// Single source of truth for the NAMER packed surface format (D-03).
    /// Mirrors GraffitiEntertainment/BlenderNamerPlugin <c>namer_core.py</c> (develop)
    /// byte-for-byte so NAMER packed textures decode equivalently (ENCD-05).
    /// Pure C# — Unity.Mathematics math types only, no engine object types.
    /// </summary>
    public static class NamerFormat
    {
        /// <summary>
        /// Encodes a raw [0,1] DirectX normal-map texel into NAMER octahedral R/G.
        /// Barycentric projection of the non-negative texel (the reference's normalize
        /// and L1-divide cancel for these inputs). R/G always land in [0.5, 1.0];
        /// no corner-fold is applied.
        /// </summary>
        public static float2 OctahedralEncode(float3 v)
        {
            float sum = max(v.x + v.y + v.z, 1e-6f);
            return new float2(0.5f + 0.5f * v.x / sum, 0.5f + 0.5f * v.y / sum);
        }

        /// <summary>
        /// Full quadratic inverse of <see cref="OctahedralEncode"/>. Solves
        /// |p|^2 S^2 - 2 S + 2 = 0 for the larger root, recovering the unit tangent
        /// normal AND un-flipping the DirectX green channel (n.y = 1 - p.y * S).
        /// </summary>
        public static float3 OctahedralDecode(float2 oct)
        {
            float2 pxy = oct * 2.0f - 1.0f;
            float pz = 1.0f - pxy.x - pxy.y;
            float3 p = new float3(pxy.x, pxy.y, pz);

            float dotP = dot(p, p);
            float disc = max(1.0f - 2.0f * dotP, 0.0f);
            float S = (1.0f + sqrt(disc)) / max(dotP, 1e-6f);

            float3 n;
            n.x = p.x * S - 1.0f;
            n.y = 1.0f - p.y * S;
            n.z = p.z * S - 1.0f;
            return normalize(n);
        }

        /// <summary>
        /// Packs metallic (bit 7), emissive (bit 6), and 6-bit roughness (bits 0-5)
        /// into a single byte. Metallic and emissive use STRICT > thresholds; roughness
        /// uses linear floor quantization to [0, 63].
        /// </summary>
        public static byte PackAlphaBits(float metallic, float emissive, float roughness)
        {
            byte metallicBit = metallic > NamerConstants.MetallicThreshold ? NamerConstants.MetallicBit : (byte)0;
            byte emissiveBit = emissive > NamerConstants.EmissiveThreshold ? NamerConstants.EmissiveBit : (byte)0;
            byte roughnessBits = (byte)clamp((int)floor(roughness * 63.0f), 0, NamerConstants.RoughnessMask);
            return (byte)(metallicBit | emissiveBit | roughnessBits);
        }

        /// <summary>
        /// Unpacks a packed alpha byte into metallic/emissive flags and 6-bit roughness.
        /// </summary>
        public static void UnpackAlphaBits(byte a, out bool metallic, out bool emissive, out float roughness)
        {
            metallic = (a & NamerConstants.MetallicBit) != 0;
            emissive = (a & NamerConstants.EmissiveBit) != 0;
            roughness = (a & NamerConstants.RoughnessMask) / 63.0f;
        }

        /// <summary>
        /// Packs a full NAMER surface: R/G = octahedral normal, B = AO (no transform),
        /// A = packed alpha bits normalized to [0,1].
        /// </summary>
        public static float4 PackSurface(float3 normal, float ao, float metallic, float emissive, float roughness)
        {
            float2 oct = OctahedralEncode(normal);
            float alpha = PackAlphaBits(metallic, emissive, roughness) / 255.0f;
            return new float4(oct.x, oct.y, ao, alpha);
        }

        /// <summary>
        /// Unpacks a full NAMER surface into octahedral normal, AO, and alpha bits.
        /// Inverse of <see cref="PackSurface"/>.
        /// </summary>
        public static void UnpackSurface(float4 surface, out float3 normal, out float ao, out bool metallic, out bool emissive, out float roughness)
        {
            normal = OctahedralDecode(new float2(surface.x, surface.y));
            ao = surface.z;
            byte alpha = (byte)clamp((int)floor(surface.w * 255.0f + 0.5f), 0, 255);
            UnpackAlphaBits(alpha, out metallic, out emissive, out roughness);
        }
    }
}

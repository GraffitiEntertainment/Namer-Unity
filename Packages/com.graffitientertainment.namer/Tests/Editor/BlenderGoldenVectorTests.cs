using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// ENCD-05: the five Blender golden vectors (float-level, derived from the exact
    /// reference formula in namer_core.py) match within 1e-4 float / exact integer bits.
    /// </summary>
    public class BlenderGoldenVectorTests
    {
        // A: neutral, m=0 e=0 r=0.5 ao=1.0  -> oct(0.625,0.625) B=1.0 alpha=31
        // B: neutral, m=1 e=0 r=1.0 ao=1.0  -> alpha=191
        // C: neutral, m=0.5 e=0.1 r=0.0 ao=1.0 -> alpha=0 (strict boundaries CLEAR)
        // D: +X texel (1,0,0), m=0 e=0 r=0.5 ao=1.0 -> oct(1.0,0.5) alpha=31
        // E: neutral, m=0.6 e=0.2 r=0.5 ao=0.25 -> B=0.25 alpha=223
        [TestCase(0.5f, 0.5f, 1.0f, 0.0f, 0.0f, 0.5f, 1.0f, 0.625f, 0.625f, 1.0f, 31)]
        [TestCase(0.5f, 0.5f, 1.0f, 1.0f, 0.0f, 1.0f, 1.0f, 0.625f, 0.625f, 1.0f, 191)]
        [TestCase(0.5f, 0.5f, 1.0f, 0.5f, 0.1f, 0.0f, 1.0f, 0.625f, 0.625f, 1.0f, 0)]
        [TestCase(1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.5f, 1.0f, 1.0f, 0.5f, 1.0f, 31)]
        [TestCase(0.5f, 0.5f, 1.0f, 0.6f, 0.2f, 0.5f, 0.25f, 0.625f, 0.625f, 0.25f, 223)]
        public void GoldenVector_MatchesBlenderReference(
            float vx, float vy, float vz,
            float metallic, float emissive, float roughness, float ao,
            float expOctX, float expOctY, float expB, int expAlpha)
        {
            float3 normal = new float3(vx, vy, vz);

            // Octahedral encode: float-level oct with 1e-4 tolerance.
            float2 oct = NamerFormat.OctahedralEncode(normal);
            Assert.AreEqual(expOctX, (double)oct.x, 1e-4);
            Assert.AreEqual(expOctY, (double)oct.y, 1e-4);

            // Packed surface: B (AO) float-level with 1e-4 tolerance.
            float4 surface = NamerFormat.PackSurface(normal, ao, metallic, emissive, roughness);
            Assert.AreEqual(expB, (double)surface.z, 1e-4);

            // Packed alpha: exact integer bits.
            Assert.AreEqual(expAlpha, (int)NamerFormat.PackAlphaBits(metallic, emissive, roughness));
        }
    }
}

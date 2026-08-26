using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Mathematics;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// ENCD-02 / ENCD-03: exact alpha bit packing (metallic bit 7, emissive bit 6,
    /// 6-bit roughness bits 0-5) and AO preservation through the B channel.
    /// </summary>
    public class BitPackingTests
    {
        /// <summary>
        /// PackAlphaBits produces byte-exact values; metallic/emissive use STRICT > thresholds.
        /// </summary>
        [TestCase(0.0f, 0.0f, 0.5f, 31)]    // roughness floor(0.5*63)=31
        [TestCase(1.0f, 0.0f, 1.0f, 191)]   // 128 (metallic) + 63 (roughness)
        [TestCase(0.5f, 0.1f, 0.0f, 0)]     // both strict > boundaries CLEAR the bit
        [TestCase(0.6f, 0.2f, 0.5f, 223)]   // 128 + 64 + 31
        [TestCase(0.0f, 1.0f, 0.0f, 64)]    // 64 (emissive) only
        public void PackAlphaBits_MatchesExpectedByte(float metallic, float emissive, float roughness, int expected)
        {
            Assert.AreEqual(expected, (int)NamerFormat.PackAlphaBits(metallic, emissive, roughness));
        }

        /// <summary>
        /// UnpackAlphaBits inverts PackAlphaBits: metallic/emissive are exact bools,
        /// roughness is within one quantization step (1/63).
        /// </summary>
        [TestCase(31, false, false, 31.0f)]
        [TestCase(191, true, false, 63.0f)]
        [TestCase(0, false, false, 0.0f)]
        [TestCase(223, true, true, 31.0f)]
        [TestCase(64, false, true, 0.0f)]
        public void UnpackAlphaBits_InvertsPack(int packed, bool expMetallic, bool expEmissive, float expRoughnessBits)
        {
            NamerFormat.UnpackAlphaBits((byte)packed, out bool metallic, out bool emissive, out float roughness);
            Assert.AreEqual(expMetallic, metallic);
            Assert.AreEqual(expEmissive, emissive);
            Assert.AreEqual(expRoughnessBits / 63.0f, (double)roughness, 1e-6);
        }

        /// <summary>
        /// ENCD-03: AO is preserved exactly in the B channel (no transform).
        /// </summary>
        [Test]
        public void PackSurface_PreservesAoInBlueChannel()
        {
            float4 surface = NamerFormat.PackSurface(new float3(0.5f, 0.5f, 1.0f), 0.25f, 0.0f, 0.0f, 0.5f);
            Assert.AreEqual(0.25, (double)surface.z, 1e-7);
        }

        /// <summary>
        /// UnpackSurface recovers AO and the packed alpha bits from a packed surface.
        /// </summary>
        [Test]
        public void UnpackSurface_RecoversAoAndPackedBits()
        {
            float4 surface = NamerFormat.PackSurface(new float3(0.5f, 0.5f, 1.0f), 0.25f, 0.6f, 0.2f, 0.5f);
            NamerFormat.UnpackSurface(surface, out float3 normal, out float ao, out bool metallic, out bool emissive, out float roughness);
            Assert.AreEqual(0.25, (double)ao, 1e-7);
            Assert.IsTrue(metallic);
            Assert.IsTrue(emissive);
            Assert.AreEqual(31.0f / 63.0f, (double)roughness, 1e-6);
        }
    }
}

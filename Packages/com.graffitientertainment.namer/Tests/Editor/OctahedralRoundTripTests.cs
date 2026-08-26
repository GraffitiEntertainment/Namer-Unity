using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// ENCD-01: octahedral encode/decode round-trips unit tangent normals within tolerance.
    /// The encode operates on the raw [0,1] DirectX normal-map texel; the decode recovers
    /// the unit tangent normal (undoing the [0,1] bias and the DirectX green flip).
    /// </summary>
    public class OctahedralRoundTripTests
    {
        /// <summary>
        /// Encode invariant: R/G always land in [0.5, 1.0] for any [0,1] input
        /// (the Blender reference's non-folded barycentric projection never leaves
        /// the top-right quadrant).
        /// </summary>
        [TestCase(0.5f, 0.5f, 1.0f)]
        [TestCase(1.0f, 0.5f, 0.5f)]
        [TestCase(0.0f, 0.5f, 0.5f)]
        [TestCase(0.5f, 0.0f, 0.5f)]
        [TestCase(0.5f, 1.0f, 0.5f)]
        [TestCase(0.7887f, 0.2113f, 0.7887f)]
        public void Encode_LandsInExpectedRange(float tx, float ty, float tz)
        {
            float2 oct = NamerFormat.OctahedralEncode(new float3(tx, ty, tz));
            Assert.GreaterOrEqual((double)oct.x, 0.5);
            Assert.LessOrEqual((double)oct.x, 1.0);
            Assert.GreaterOrEqual((double)oct.y, 0.5);
            Assert.LessOrEqual((double)oct.y, 1.0);
        }

        /// <summary>
        /// The neutral DirectX normal-map texel encodes to the golden oct (0.625, 0.625).
        /// </summary>
        [Test]
        public void NeutralTexel_EncodesToGoldenOct()
        {
            float2 oct = NamerFormat.OctahedralEncode(new float3(0.5f, 0.5f, 1.0f));
            Assert.AreEqual(0.625, (double)oct.x, 1e-4);
            Assert.AreEqual(0.625, (double)oct.y, 1e-4);
        }

        /// <summary>
        /// A unit tangent normal maps to its DirectX texel, encodes, decodes, and returns
        /// to the same direction (directional round-trip within 1e-3 dot tolerance, D-06).
        /// </summary>
        [TestCase(0.0f, 0.0f, 1.0f)]   // +Z (neutral)
        [TestCase(1.0f, 0.0f, 0.0f)]   // +X
        [TestCase(-1.0f, 0.0f, 0.0f)]  // -X
        [TestCase(0.0f, 1.0f, 0.0f)]   // +Y
        [TestCase(0.0f, -1.0f, 0.0f)]  // -Y
        [TestCase(0.5f, 0.3f, 0.8f)]   // generic non-axis
        public void RoundTrip_RecoversUnitTangentNormal(float nx, float ny, float nz)
        {
            float3 normal = normalize(new float3(nx, ny, nz));

            // DirectX normal-map texel for this unit tangent normal (green is flipped).
            float3 texel = new float3(
                (normal.x + 1.0f) * 0.5f,
                (1.0f - normal.y) * 0.5f,
                (normal.z + 1.0f) * 0.5f);

            float3 decoded = NamerFormat.OctahedralDecode(NamerFormat.OctahedralEncode(texel));
            Assert.GreaterOrEqual((double)dot(decoded, normal), 1.0 - 1e-3);
        }
    }
}

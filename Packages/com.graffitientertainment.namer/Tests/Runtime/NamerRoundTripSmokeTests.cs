using System.Collections;
using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    public class NamerRoundTripSmokeTests
    {
        [UnityTest]
        public IEnumerator NeutralNormalRoundTripsThroughCoreAndShaderBinding()
        {
            Texture2D surfaceMap = null;
            Material material = null;
            try
            {
                // 1. Encode the neutral DirectX normal-map texel (golden: (0.625, 0.625)).
                float2 oct = NamerFormat.OctahedralEncode(new float3(0.5f, 0.5f, 1.0f));
                Assert.AreEqual(0.625, (double)oct.x, 1e-4);
                Assert.AreEqual(0.625, (double)oct.y, 1e-4);

                // 2. Pack alpha: metallic = 0, emissive = 0, roughness = 0.5 -> floor(0.5 * 63) = 31.
                byte packedAlpha = NamerFormat.PackAlphaBits(0.0f, 0.0f, 0.5f);
                Assert.AreEqual(31, (int)packedAlpha);

                // 3. Build a 1x1 linear R8G8B8A8_UNorm surface texture holding the golden texel.
                surfaceMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                surfaceMap.filterMode = FilterMode.Point;
                surfaceMap.SetPixel(0, 0, new Color(0.625f, 0.625f, 1.0f, 31.0f / 255.0f));
                surfaceMap.Apply(false, false);
                Assert.AreEqual(GraphicsFormat.R8G8B8A8_UNorm, surfaceMap.graphicsFormat);

                // 4. Resolve the NAMER runtime shader (plan 01-02) and bind the surface map.
                // (GPU leg is shader resolution + binding only — the CPU decode golden
                // lives in Tests/Editor/BlenderGoldenVectorTests.cs.)
                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
                Assert.IsNotNull(shader, "Shader 'GraffitiEntertainment.Namer/NAMER' not found (plan 01-02 provides it).");
                material = new Material(shader);
                material.SetTexture("_SurfaceMap", surfaceMap);

                // 5. Assert the shader binding resolved the NAMER shader and the Core decode contract (neutral normal recovers +Z).
                Assert.AreEqual("GraffitiEntertainment.Namer/NAMER", material.shader.name);
                float3 decoded = NamerFormat.OctahedralDecode(new float2(0.625f, 0.625f));
                Assert.GreaterOrEqual((double)math.dot(decoded, new float3(0.0f, 0.0f, 1.0f)), 1.0 - 1e-3);
            }
            finally
            {
                // Cleanup runs even on assertion failure so the material/texture
                // never leak into the PlayMode domain.
                if (material != null)
                {
                    UnityEngine.Object.Destroy(material);
                }

                if (surfaceMap != null)
                {
                    UnityEngine.Object.Destroy(surfaceMap);
                }
            }

            yield return null;
        }
    }
}

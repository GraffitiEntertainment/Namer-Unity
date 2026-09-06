using System.Collections;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 04.1 Sobel roughness extraction tests (plan 01). Proves the D-01 extraction
    /// path: a no-authored-_MetallicGlossMap source with RoughnessExtractStrength > 0 packs
    /// per-texel roughness derived from the cleaned base's Sobel edge magnitude into surface
    /// PNG alpha bits 0-5 (instead of the scalar _Roughness), while an authored
    /// _MetallicGlossMap locks the legacy path byte-identically. Capability-gated (D-15):
    /// every GPU test skips with an explicit report when compute/async-readback is
    /// unavailable, never a silent pass.
    /// </summary>
    public class NamerRoughnessExtractionTests
    {
        private const int WorkingSize = 64;
        private const int RoughnessMask = 0x3F;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator Sobel_FlatBase_YieldsNearZeroRoughness()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU roughness extraction test (D-15: Metal is the verified target).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                try
                {
                    FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));

                    NamerMaterialInspection inspection = BuildInspection(
                        baseMap, baseIsSrgb: false, metallicGlossMap: null,
                        smoothness: 0f, roughness: 1f,
                        roughnessExtractStrength: 1f, roughnessEstimator: NamerRoughnessEstimator.Sobel);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                        int maxBits = 0;
                        for (int i = 0; i < surface.Length; i++)
                        {
                            maxBits = Mathf.Max(maxBits, surface[i].a & RoughnessMask);
                        }

                        Assert.LessOrEqual(maxBits, 1,
                            "flat base must yield near-zero roughness (no Sobel edges) in alpha bits 0-5");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after extraction (no leak)");
                }
                finally
                {
                    Destroy(baseMap);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator Sobel_SharpEdge_YieldsHighRoughness()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU roughness edge test (D-15: Metal is the verified target).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                try
                {
                    Color[] pixels = new Color[WorkingSize * WorkingSize];
                    for (int y = 0; y < WorkingSize; y++)
                    {
                        for (int x = 0; x < WorkingSize; x++)
                        {
                            // Bright half (linear ~0.9) vs dark half (linear ~0.15) split down the middle.
                            float value = x < WorkingSize / 2 ? 0.9f : 0.15f;
                            pixels[y * WorkingSize + x] = new Color(value, value, value, 1.0f);
                        }
                    }

                    baseMap.SetPixels(pixels);
                    baseMap.Apply(false, false);

                    NamerMaterialInspection inspection = BuildInspection(
                        baseMap, baseIsSrgb: false, metallicGlossMap: null,
                        smoothness: 0f, roughness: 1f,
                        roughnessExtractStrength: 1f, roughnessEstimator: NamerRoughnessEstimator.Sobel);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                        // Edge texel sits at the bright/dark boundary (x = 32); flat texel sits well
                        // inside the bright half (x = 8), far from the boundary and the Sobel taps.
                        int flatIndex = (WorkingSize / 2) * WorkingSize + 8;
                        int edgeIndex = (WorkingSize / 2) * WorkingSize + (WorkingSize / 2);

                        int flatBits = surface[flatIndex].a & RoughnessMask;
                        int edgeBits = surface[edgeIndex].a & RoughnessMask;

                        Assert.Greater(edgeBits, flatBits,
                            "edge texel roughness must exceed flat-region texel roughness (Sobel extracts the baked edge)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after extraction (no leak)");
                }
                finally
                {
                    Destroy(baseMap);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            yield return null;
        }

        [UnityTest]
        public IEnumerator AuthoredMetallicGlossMap_IsByteIdentical_NoExtraction()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU authored-map gate test (D-15: Metal is the verified target).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                Texture2D metallicGlossMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                try
                {
                    FillSolid(baseMap, new Color(0.6f, 0.4f, 0.2f, 1.0f));
                    // Solid authored metallic/gloss: metallic = 0 (r), smoothness = 0.5 (a).
                    FillSolid(metallicGlossMap, new Color(0.0f, 0.0f, 0.0f, 0.5f));

                    // D-01: an authored MetallicGlossMap locks the legacy path — extraction never runs
                    // regardless of RoughnessExtractStrength, so both packed surfaces must be byte-identical.
                    NamerMaterialInspection inspection = BuildInspection(
                        baseMap, baseIsSrgb: false, metallicGlossMap: metallicGlossMap,
                        smoothness: 1f, roughness: 0.5f,
                        roughnessExtractStrength: 0f, roughnessEstimator: NamerRoughnessEstimator.FitDriven);

                    Color32[] surfaceOff;
                    NamerComputeResult resultOff = pipeline.Process(inspection);
                    try
                    {
                        surfaceOff = ReadBackColor32(resultOff.PackedSurface, WorkingSize * WorkingSize);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(resultOff);
                    }

                    inspection.RoughnessExtractStrength = 1f;
                    Color32[] surfaceOn;
                    NamerComputeResult resultOn = pipeline.Process(inspection);
                    try
                    {
                        surfaceOn = ReadBackColor32(resultOn.PackedSurface, WorkingSize * WorkingSize);
                    }
                    finally
                    {
                        pipeline.ReleaseResult(resultOn);
                    }

                    for (int i = 0; i < surfaceOff.Length; i++)
                    {
                        Assert.AreEqual(surfaceOff[i].r, surfaceOn[i].r, $"texel {i} red byte must be identical (authored map locks the legacy path)");
                        Assert.AreEqual(surfaceOff[i].g, surfaceOn[i].g, $"texel {i} green byte must be identical (authored map locks the legacy path)");
                        Assert.AreEqual(surfaceOff[i].b, surfaceOn[i].b, $"texel {i} blue byte must be identical (authored map locks the legacy path)");
                        Assert.AreEqual(surfaceOff[i].a, surfaceOn[i].a, $"texel {i} alpha byte must be identical (authored map locks the legacy path)");
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after authored-map path (no leak)");
                }
                finally
                {
                    Destroy(baseMap, metallicGlossMap);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private static NamerMaterialInspection BuildInspection(
            Texture2D baseMap,
            bool baseIsSrgb,
            Texture2D metallicGlossMap,
            float smoothness,
            float roughness,
            float roughnessExtractStrength,
            NamerRoughnessEstimator roughnessEstimator)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = baseIsSrgb,
                NormalMap = null,
                OcclusionMap = null,
                MetallicGlossMap = metallicGlossMap,
                Metallic = 0.0f,
                Smoothness = smoothness,
                Roughness = roughness,
                RoughnessExtractStrength = roughnessExtractStrength,
                RoughnessEstimator = roughnessEstimator,
                Emissive = 0.0f,
                AoUnmultiplyStrength = 1.0f,
                SmoothnessTextureChannel = 0,
            };
        }

        private static Color32[] ReadBackColor32(RenderTexture rt, int expectedCount)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color32> data = req.GetData<Color32>();
            Assert.AreEqual(expectedCount, data.Length, "readback must return the expected pixel count");
            Color32[] result = new Color32[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = data[i];
            }

            return result;
        }

        private static void FillSolid(Texture2D texture, Color color)
        {
            Color[] pixels = new Color[texture.width * texture.height];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        private static void Destroy(params Object[] objects)
        {
            foreach (Object o in objects)
            {
                if (o != null)
                {
                    Object.DestroyImmediate(o);
                }
            }
        }
    }
}

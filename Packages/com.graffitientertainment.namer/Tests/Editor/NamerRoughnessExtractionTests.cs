using System.Collections;
using System.Collections.Generic;
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
        public IEnumerator Sobel_FlatBase_YieldsAuthoredScalarRoughness()
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
                        roughnessExtractStrength: 1f, dipSource: NamerDipSource.SobelEdge);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                        int maxBits = 0;
                        for (int i = 0; i < surface.Length; i++)
                        {
                            maxBits = Mathf.Max(maxBits, surface[i].a & RoughnessMask);
                        }

                        Assert.GreaterOrEqual(maxBits, 62,
                            "flat base must yield approximately the authored scalar roughness (1.0 → 63 bits): zero Sobel → no dip → scalar anchor (D-08)");
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
        public IEnumerator Sobel_SharpEdge_DipsBelowFlatRoughness()
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
                        roughnessExtractStrength: 1f, dipSource: NamerDipSource.SobelEdge);

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

                        Assert.Less(edgeBits, flatBits,
                            "edge texel must dip BELOW flat texels (anchored-inverted D-08: sharp edge = gloss dip)");
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
        public IEnumerator Sobel_SparseExtremeEdges_SmoothMedianStaysAtScalar()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU roughness sparse-edge test (D-15: Metal is the verified target).");
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
                            // QUIET fill: a flat mid-gray has zero Sobel magnitude, so under
                            // the anchored-inverted map it stays exactly at the authored scalar
                            // (the "smooth median ≈ scalar" premise, D-08). The hash-grain body
                            // was retired because grain survives the AO un-multiply (±0.022) and
                            // dips BELOW the scalar, which cannot satisfy the flipped premise.
                            // One sparse extreme-contrast dark line at x = 32 punches a
                            // ~35x-larger Sobel response — the heavy-tail shape of real AI
                            // albedos.
                            float value = 0.5f;
                            if (x == WorkingSize / 2)
                            {
                                value = 0.02f;
                            }

                            pixels[y * WorkingSize + x] = new Color(value, value, value, 1.0f);
                        }
                    }

                    baseMap.SetPixels(pixels);
                    baseMap.Apply(false, false);

                    NamerMaterialInspection inspection = BuildInspection(
                        baseMap, baseIsSrgb: false, metallicGlossMap: null,
                        smoothness: 0f, roughness: 1f,
                        roughnessExtractStrength: 1f, dipSource: NamerDipSource.SobelEdge);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                        // Smooth population = interior strip away from the stamped line; its
                        // MEDIAN texel must stay matte. The peak Sobel response sits one column
                        // RIGHT of the line (x = 33 — Sobel's gx skips the center column, so
                        // the line texel itself stays at grain magnitude).
                        List<int> smoothBits = new List<int>();
                        for (int y = 0; y < WorkingSize; y++)
                        {
                            for (int x = 8; x <= 24; x++)
                            {
                                smoothBits.Add(surface[y * WorkingSize + x].a & RoughnessMask);
                            }
                        }

                        smoothBits.Sort();
                        int medianBits = smoothBits[smoothBits.Count / 2];
                        int edgeBits = surface[(WorkingSize / 2) * WorkingSize + (WorkingSize / 2 + 1)].a & RoughnessMask;

                        Assert.GreaterOrEqual(medianBits, 62,
                            "smooth median must stay approximately at the authored scalar (quiet regions are anchored, not collapsed)");
                        Assert.Less(edgeBits, medianBits,
                            "the sparse extreme edge must dip strictly BELOW the smooth anchor (gloss dip)");
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
                        roughnessExtractStrength: 0f, dipSource: NamerDipSource.RemovedDetail);

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
            NamerDipSource dipSource)
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
                DipSource = dipSource,
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

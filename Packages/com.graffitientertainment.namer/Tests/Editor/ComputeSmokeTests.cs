using System.Collections;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// TEST-04 cross-platform full-pipeline smoke: runs <see cref="NamerComputePipeline"/>
    /// (upload -&gt; normalize -&gt; octahedral -&gt; pack -&gt; async readback) end-to-end on the GPU,
    /// verifies the sRGB/linear upload + normalize contract numerically (W2, D-14), and watches
    /// the <see cref="ComputeTexturePool"/> leak count return to baseline (D-13). Capability-gated
    /// (D-15): skips with an explicit report entry when compute/async-readback is unavailable.
    /// </summary>
    public class ComputeSmokeTests
    {
        private const int WorkingSize = 64;

        [UnityTest]
        public IEnumerator FullPipeline_ProducesNormalizedAndPackedTextures_OnMetal()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable on this backend (" + SystemInfo.graphicsDeviceType + ") — skipping (D-15: Metal is the verified target, CI matrix is v2).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            int baseline = pipeline.LiveRenderTargetCount;
            Assert.AreEqual(0, baseline, "pool should start empty");

            try
            {
                // Scenario 1: scalar-only, sRGB-authored base color (sRGB decode + AO un-multiply).
                yield return RunScenario(pipeline, baseIsSrgb: true, useMetallicGlossMap: false, dataMapsAreSrgb: false);

                // Scenario 2: scalar-only, linear-authored base color (must NOT be sRGB-decoded).
                yield return RunScenario(pipeline, baseIsSrgb: false, useMetallicGlossMap: false, dataMapsAreSrgb: false);

                // Scenario 3: metallic/gloss map path (R = metallic, A = smoothness).
                yield return RunScenario(pipeline, baseIsSrgb: true, useMetallicGlossMap: true, dataMapsAreSrgb: false);

                // Scenario 4 (WR-04): sRGB-authored AO + metallicGloss data maps. The
                // kernels treat data maps as raw texel data; this scenario pins that an
                // sRGB-flagged data map still arrives byte-exact as authored (no sRGB
                // decode), so the packed bytes match the CPU oracle computed from the
                // raw values.
                yield return RunScenario(pipeline, baseIsSrgb: true, useMetallicGlossMap: true, dataMapsAreSrgb: true);

                // D-13 leak watchdog, asserted BEFORE Dispose (Dispose clears the live set,
                // so asserting after it would compare 0 == 0 and could never fail).
                Assert.AreEqual(baseline, pipeline.LiveRenderTargetCount,
                    "pool must be empty after a released batch (no leak, D-13)");
            }
            finally
            {
                pipeline.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Process_AcceptsMipmappedSourceTextures_CopyingMip0Raw()
        {
            if (!SystemInfo.supportsComputeShaders || !SystemInfo.supportsAsyncGPUReadback)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable on this backend (" + SystemInfo.graphicsDeviceType + ") — skipping (D-15: Metal is the verified target, CI matrix is v2).");
                yield break;
            }

            // Imported asset textures carry mip chains while the upload targets are
            // single-mip; Upload must carry mip 0 raw (the only level the kernels
            // read). CopyTexture-based staging threw on real assets ("mismatching
            // mip counts", then "mismatching data size" for RGB24 sources).
            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, true, false);
                Texture2D aoMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, true, true);
                try
                {
                    FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                    baseMap.Apply(true, false);
                    FillSolid(aoMap, new Color(0.0f, 0.5f, 0.0f, 1.0f));
                    aoMap.Apply(true, false);
                    Assert.Greater(baseMap.mipmapCount, 1, "source must actually carry a mip chain for this test to mean anything");

                    NamerMaterialInspection inspection = new NamerMaterialInspection
                    {
                        BaseMap = baseMap,
                        BaseMapIsSrgb = true,
                        NormalMap = null,
                        OcclusionMap = aoMap,
                        MetallicGlossMap = null,
                        Metallic = 0.0f,
                        Smoothness = 0.5f,
                        Roughness = 0.5f,
                        Emissive = 0.0f,
                        AoUnmultiplyStrength = 1.0f,
                        SmoothnessTextureChannel = 0,
                    };

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        AsyncGPUReadbackRequest baseReq = AsyncGPUReadback.Request(result.NormalizedBaseColor, 0, TextureFormat.RGBA32);
                        yield return new WaitUntil(() => baseReq.done);
                        Assert.IsFalse(baseReq.hasError, "normalized base readback must not error");

                        Color32 normalized = baseReq.GetData<Color32>()[0];
                        float expected = SRGBToLinear(baseMap.GetPixel(0, 0).r) / Mathf.Max(aoMap.GetPixel(0, 0).g, 1e-6f);
                        Assert.AreEqual((double)expected, normalized.r / 255.0, 1.0 / 255.0,
                            "mip-0 upload must carry the raw authored bytes (single sRGB decode), not mip-filtered or converted values");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }
                }
                finally
                {
                    Destroy(baseMap, aoMap);
                }
            }
            finally
            {
                pipeline.Dispose();
            }
        }

        private static IEnumerator RunScenario(NamerComputePipeline pipeline, bool baseIsSrgb, bool useMetallicGlossMap, bool dataMapsAreSrgb)
        {
            // Base color is uniform gray; AO is a data map (g = 0.5 to exercise un-multiply)
            // authored either linear or sRGB to pin the raw-data-map upload contract (WR-04).
            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, baseIsSrgb ? false : true);
            Texture2D aoMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, dataMapsAreSrgb ? false : true);
            Texture2D metallicGlossMap = useMetallicGlossMap
                ? new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, dataMapsAreSrgb ? false : true)
                : null;

            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                FillSolid(aoMap, new Color(0.0f, 0.5f, 0.0f, 1.0f));
                if (metallicGlossMap != null)
                {
                    FillSolid(metallicGlossMap, new Color(0.6f, 0.0f, 0.0f, 0.4f));
                }

                NamerMaterialInspection inspection = new NamerMaterialInspection
                {
                    BaseMap = baseMap,
                    BaseMapIsSrgb = baseIsSrgb,
                    NormalMap = null,
                    OcclusionMap = aoMap,
                    MetallicGlossMap = metallicGlossMap,
                    Metallic = 0.0f,
                    Smoothness = 0.5f,
                    Roughness = 0.5f,
                    Emissive = 0.0f,
                    AoUnmultiplyStrength = 1.0f,
                    SmoothnessTextureChannel = 0,
                };

                NamerComputeResult result = pipeline.Process(inspection);
                try
                {
                    Assert.IsNotNull(result, "Process must return a result");
                    Assert.IsNotNull(result.PackedSurface, "PackedSurface must be non-null");
                    Assert.IsNotNull(result.NormalizedBaseColor, "NormalizedBaseColor must be non-null");
                    Assert.AreEqual(GraphicsFormat.R8G8B8A8_UNorm, result.PackedSurface.graphicsFormat);
                    Assert.AreEqual(GraphicsFormat.R16G16B16A16_SFloat, result.NormalizedBaseColor.graphicsFormat);
                    Assert.AreEqual(WorkingSize, result.Width);
                    Assert.AreEqual(WorkingSize, result.Height);

                    // Packed surface readback: proves a real GPU round-trip completed, not just RT allocation.
                    AsyncGPUReadbackRequest surfaceReq = AsyncGPUReadback.Request(result.PackedSurface, 0, TextureFormat.RGBA32);
                    yield return new WaitUntil(() => surfaceReq.done);
                    Assert.IsFalse(surfaceReq.hasError, "packed surface readback must not error");
                    NativeArray<Color32> surfaceData = surfaceReq.GetData<Color32>();
                    Assert.Greater(surfaceData.Length, 0, "packed surface readback must return pixels");

                    // WR-04: data maps are raw texel data — pin the packed bytes against the CPU
                    // oracle computed from the raw authored values. An sRGB-flagged data map must
                    // arrive byte-exact as authored (no sRGB decode), so the strict metallic
                    // threshold and 6-bit roughness quantization track the oracle either way.
                    // (Smoothness scalar 0.5 mirrors the inspection's Smoothness below.)
                    Color32 surfacePixel = surfaceData[0];
                    float aoRaw = aoMap.GetPixel(0, 0).g;
                    float metallicRaw = metallicGlossMap != null ? metallicGlossMap.GetPixel(0, 0).r : 0.0f;
                    float smoothnessRaw = metallicGlossMap != null ? metallicGlossMap.GetPixel(0, 0).a : 0.0f;
                    float effectiveMetallic = metallicGlossMap != null ? metallicRaw : 0.0f;
                    float effectiveRoughness = metallicGlossMap != null ? 1f - smoothnessRaw * 0.5f : 0.5f;
                    byte expectedAlpha = NamerFormat.PackAlphaBits(effectiveMetallic, 0.0f, effectiveRoughness);
                    Assert.AreEqual((int)expectedAlpha, (int)surfacePixel.a,
                        "packed alpha byte must match the CPU oracle for raw data-map values (sRGB data maps = " + dataMapsAreSrgb + ")");
                    Assert.AreEqual((double)aoRaw, surfacePixel.b / 255.0, 1.0 / 255.0,
                        "packed AO byte must be the raw authored value, no sRGB decode (sRGB data maps = " + dataMapsAreSrgb + ")");

                    // W2: end-to-end normalize assertion against the CPU-computed expectation.
                    AsyncGPUReadbackRequest baseReq = AsyncGPUReadback.Request(result.NormalizedBaseColor, 0, TextureFormat.RGBA32);
                    yield return new WaitUntil(() => baseReq.done);
                    Assert.IsFalse(baseReq.hasError, "normalized base readback must not error");

                    NativeArray<Color32> baseData = baseReq.GetData<Color32>();
                    Assert.Greater(baseData.Length, 0, "normalized base readback must return pixels");
                    Color32 normalized = baseData[0];

                    float actualBase = baseMap.GetPixel(0, 0).r;
                    float actualAo = aoMap.GetPixel(0, 0).g;
                    float expected = (baseIsSrgb ? SRGBToLinear(actualBase) : actualBase) / Mathf.Max(actualAo, 1e-6f);

                    Assert.AreEqual((double)expected, normalized.r / 255.0, 1.0 / 255.0, "normalized base R (sRGB=" + baseIsSrgb + ")");
                    Assert.AreEqual((double)expected, normalized.g / 255.0, 1.0 / 255.0, "normalized base G (sRGB=" + baseIsSrgb + ")");
                    Assert.AreEqual((double)expected, normalized.b / 255.0, 1.0 / 255.0, "normalized base B (sRGB=" + baseIsSrgb + ")");
                }
                finally
                {
                    pipeline.ReleaseResult(result);
                }

                Assert.IsNull(result.NormalizedBaseColor, "ReleaseResult must clear the leased target reference");
                Assert.IsNull(result.PackedSurface, "ReleaseResult must clear the leased target reference");
                Assert.AreEqual(0, pipeline.LiveRenderTargetCount,
                    "pool must return to baseline after each Process + ReleaseResult (no leak between batches, D-13)");
            }
            finally
            {
                Destroy(baseMap, aoMap, metallicGlossMap);
            }
        }

        private static float SRGBToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
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

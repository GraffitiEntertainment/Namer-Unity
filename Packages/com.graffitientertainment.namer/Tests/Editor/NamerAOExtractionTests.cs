using System.Collections;
using GraffitiEntertainment.Namer.Core;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEditor;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// D-07 AO stage-gate tests (2026-09-21 contract). Prove that the AO stage checkbox
    /// gates SYNTHESIS only: OFF with no authored <c>_OcclusionMap</c> packs surface B
    /// flat white via the WhiteFill path (the albedo-conflated luminance extraction is
    /// retired from the automatic path), while an authored map transfers its raw bytes
    /// and round-trips the un-multiply identically whether the stage is ON or OFF. Also
    /// pins that the un-multiply divisor and packed B channel are the SAME AO (round-trip
    /// at strength 1), that the 0.1 floor is divisor-side only, and that an authored map
    /// stays byte-identical with the epsilon (not 0.1) floor. Capability-gated (D-15):
    /// every GPU test skips with an explicit report when compute/async-readback is
    /// unavailable, never a silent pass. The bake-as-synthetic-source (stage ON, no
    /// authored map) is covered in <c>NamerAOBakeTests</c>.
    /// </summary>
    public class NamerAOExtractionTests
    {
        private const string PackComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute";
        private const int WorkingSize = 64;
        private const double ByteTolerance = 1.0 / 255.0;
        private const double RoundTripTolerance = 2.0 / 255.0;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [UnityTest]
        public IEnumerator AoStageOff_NoOcclusionMap_PacksWhiteAo()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO stage-gate test (D-15: Metal is the verified target).");
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
                            // Bright half (linear ~0.9) vs dark half (linear ~0.15), no sRGB.
                            float value = x < WorkingSize / 2 ? 0.9f : 0.15f;
                            pixels[y * WorkingSize + x] = new Color(value, value, value, 1.0f);
                        }
                    }

                    baseMap.SetPixels(pixels);
                    baseMap.Apply(false, false);

                    // 2026-09-21 contract, clause 2: AO stage OFF (the field's default)
                    // skips the whole synthetic stage — surface B packs white via the
                    // WhiteFill path regardless of albedo. The retired luminance
                    // extraction darkened exactly the dark-painted half (ao ~ 0.29 there),
                    // so the dark-region byte is the discriminating assertion.
                    NamerMaterialInspection inspection = BuildInspection(baseMap, baseIsSrgb: false, occlusionMap: null);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                        int brightIndex = 8 * WorkingSize + 8;
                        int darkIndex = 8 * WorkingSize + (WorkingSize - 8);

                        Assert.AreEqual(255, (int)surface[brightIndex].b,
                            "AO stage off must pack a white (255) surface B byte on the bright half");
                        Assert.AreEqual(255, (int)surface[darkIndex].b,
                            "AO stage off must pack a white (255) surface B byte on the dark half — no albedo-driven synthetic AO");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after the stage-off run (no leak)");
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
        public IEnumerator AoStageOff_AuthoredOcclusionMap_TransfersAuthoredAo()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU authored-transfer test (D-15: Metal is the verified target).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                Texture2D aoMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
                try
                {
                    FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                    FillOcclusionSplit(aoMap);

                    int left = (WorkingSize / 2) * WorkingSize + WorkingSize / 4;
                    int right = (WorkingSize / 2) * WorkingSize + (3 * WorkingSize / 4);

                    // 2026-09-21 contract, clause 0: an authored map is source data — it
                    // transfers with the stage OFF, and the run is byte-identical to the
                    // stage-ON run of the same fixture (authored data round-trips the
                    // same either way; the checkbox gates synthesis only).
                    NamerComputeResult off = pipeline.Process(
                        BuildInspection(baseMap, baseIsSrgb: false, occlusionMap: aoMap, aoStageEnabled: false));
                    Color32[] offSurface;
                    Color32[] offBase;
                    try
                    {
                        offSurface = ReadBackColor32(off.PackedSurface, WorkingSize * WorkingSize);
                        offBase = ReadBackColor32(off.NormalizedBaseColor, WorkingSize * WorkingSize);

                        Assert.AreEqual(255, (int)offSurface[left].b,
                            "authored ao == 1 must pack to surface B 255 with the AO stage OFF — authored data is not gated");
                        Assert.AreEqual(128, (int)offSurface[right].b,
                            "authored ao byte 128 must transfer raw into surface B with the AO stage OFF");
                        Assert.Greater((int)offBase[right].r, (int)offBase[left].r,
                            "the un-multiply must run at the slider strength (1) with the stage OFF — 0.5 / 0.5 brightens the occluded half");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(off);
                    }

                    NamerComputeResult on = pipeline.Process(
                        BuildInspection(baseMap, baseIsSrgb: false, occlusionMap: aoMap, aoStageEnabled: true));
                    try
                    {
                        Color32[] onSurface = ReadBackColor32(on.PackedSurface, WorkingSize * WorkingSize);
                        Color32[] onBase = ReadBackColor32(on.NormalizedBaseColor, WorkingSize * WorkingSize);

                        Assert.AreEqual(0, CountByteMismatches(offSurface, onSurface),
                            "packed surface must be byte-identical across the AO stage toggle when an AO map is authored");
                        Assert.AreEqual(0, CountByteMismatches(offBase, onBase),
                            "normalized base must be byte-identical across the AO stage toggle when an AO map is authored");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(on);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after the authored-transfer runs (no leak)");
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

            yield return null;
        }

        [UnityTest]
        public IEnumerator RoundTrip_BaseUnmultiplyTimesPackedAo_EqualsBase()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO round-trip test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadPackShader();

            // D-08: base_unmul x ao_packed ~= base at strength 1, when AO is above the floor.
            RunNormalizeEncodePack(compute, baseValue: 0.3f, aoValue: 0.5f, NamerConstants.AoFloor,
                out float normalizedBase, out float packedAo);

            Assert.AreEqual(0.3, (double)(normalizedBase * packedAo), RoundTripTolerance,
                "base_unmul x ao_packed must reconstruct the source base (divisor and B are the SAME ao)");

            yield return null;
        }

        [UnityTest]
        public IEnumerator Floor_IsDivisorSideOnly_NotStoredInB()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO floor test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadPackShader();

            // D-09: AO 0.05 is below the 0.1 floor -> divisor floored to 0.1, but B stores raw 0.05.
            RunNormalizeEncodePack(compute, baseValue: 0.08f, aoValue: 0.05f, NamerConstants.AoFloor,
                out float normalizedBase, out float packedAo);

            Assert.AreEqual(0.08 / 0.1, (double)normalizedBase, ByteTolerance,
                "divisor must be floored to 0.1 (base / 0.1), not the raw 0.05 AO");
            Assert.AreEqual(0.05, (double)packedAo, ByteTolerance,
                "packed B must store the raw AO (0.05), not the floored divisor (0.1) — Pitfall 3");

            yield return null;
        }

        [UnityTest]
        public IEnumerator AuthoredOcclusionMap_IsByteIdentical_AndUsesEpsilonFloor()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU authored-AO floor test (D-15: Metal is the verified target).");
                yield break;
            }

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                Texture2D aoMap = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
                try
                {
                    FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                    FillSolid(aoMap, new Color(0.0f, 0.05f, 0.0f, 1.0f));

                    NamerMaterialInspection inspection = BuildInspection(baseMap, baseIsSrgb: false, occlusionMap: aoMap);

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        // D-07: authored AO stays byte-identical in packed B (raw 0.05, not floored).
                        float actualAo = aoMap.GetPixel(0, 0).g;
                        Color32 surface = ReadBackColor32(result.PackedSurface, 1)[0];
                        Assert.AreEqual((double)actualAo, surface.b / 255.0, ByteTolerance,
                            "authored AO must stay byte-identical in packed B");

                        // Discriminating case: g = 0.05 is below the 0.1 extracted floor, so a
                        // 0.1-floor regression divides by 0.1 (~4.9) instead of the raw AO (~9.8).
                        float actualBase = baseMap.GetPixel(0, 0).r;
                        float expected = actualBase / actualAo;
                        float normalizedBase = ReadBackFloatR(result.NormalizedBaseColor);
                        Assert.AreEqual((double)expected, (double)normalizedBase, 0.1,
                            "authored path must divide by the raw AO (epsilon floor), not the 0.1 extracted floor");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline (no leak)");
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

            yield return null;
        }

        // --------------------------------------------------------------------

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap, bool baseIsSrgb, Texture2D occlusionMap, bool aoStageEnabled = false)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = baseIsSrgb,
                NormalMap = null,
                OcclusionMap = occlusionMap,
                MetallicGlossMap = null,
                Metallic = 0.0f,
                Smoothness = 0.0f,
                Roughness = 1.0f,
                Emissive = 0.0f,
                AoUnmultiplyStrength = 1.0f,
                AoStageEnabled = aoStageEnabled,
                SmoothnessTextureChannel = 0,
            };
        }

        /// <summary>Left half ao = 1 (white), right half ao = 128/255, in the green channel.</summary>
        private static void FillOcclusionSplit(Texture2D occlusion)
        {
            Color[] pixels = new Color[occlusion.width * occlusion.height];
            for (int y = 0; y < occlusion.height; y++)
            {
                for (int x = 0; x < occlusion.width; x++)
                {
                    float ao = x < occlusion.width / 2 ? 1f : 128f / 255f;
                    pixels[y * occlusion.width + x] = new Color(0f, ao, 0f, 1f);
                }
            }

            occlusion.SetPixels(pixels);
            occlusion.Apply(false, false);
        }

        private static int CountByteMismatches(Color32[] expected, Color32[] actual)
        {
            Assert.AreEqual(expected.Length, actual.Length, "mismatch count needs equal-length arrays");
            int mismatches = 0;
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i].r != actual[i].r || expected[i].g != actual[i].g
                    || expected[i].b != actual[i].b || expected[i].a != actual[i].a)
                {
                    mismatches++;
                }
            }

            return mismatches;
        }

        private static void RunNormalizeEncodePack(
            ComputeShader compute,
            float baseValue,
            float aoValue,
            float aoUnmultiplyFloor,
            out float normalizedBase,
            out float packedAo)
        {
            int kernelNormalize = compute.FindKernel("CSNormalize");
            int kernelOctahedralEncode = compute.FindKernel("CSOctahedralEncode");
            int kernelSurfacePack = compute.FindKernel("CSSurfacePack");

            RenderTexture baseColorIn = null;
            RenderTexture aoIn = null;
            RenderTexture normalTexel = null;
            RenderTexture metallicGlossIn = null;
            RenderTexture baseColorOut = null;
            RenderTexture packInputs = null;
            RenderTexture octahedral = null;
            RenderTexture surfaceOut = null;
            try
            {
                baseColorIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                aoIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                normalTexel = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                metallicGlossIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                baseColorOut = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                packInputs = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                octahedral = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                surfaceOut = CreateRenderTexture(GraphicsFormat.R8G8B8A8_UNorm, 1, 1);

                UploadPixels(baseColorIn, new[] { new Color(baseValue, baseValue, baseValue, 1.0f) }, 1, 1);
                UploadPixels(aoIn, new[] { new Color(0.0f, aoValue, 0.0f, 1.0f) }, 1, 1);
                UploadPixels(normalTexel, new[] { new Color(0.5f, 0.5f, 1.0f, 1.0f) }, 1, 1);
                UploadPixels(metallicGlossIn, new[] { new Color(0.0f, 0.0f, 0.0f, 1.0f) }, 1, 1);

                compute.SetInts("_Size", new[] { 1, 1 });
                compute.SetFloat("_SourceIsSrgb", 0.0f);
                compute.SetFloat("_AoUnmultiplyStrength", 1.0f);
                compute.SetFloat("_AoUnmultiplyFloor", aoUnmultiplyFloor);
                compute.SetFloat("_Metallic", 0.0f);
                compute.SetFloat("_SmoothnessScalar", 0.0f);
                compute.SetFloat("_Roughness", 1.0f);
                compute.SetFloat("_Emissive", 0.0f);
                compute.SetFloat("_SmoothnessTextureChannel", 0.0f);
                compute.SetFloat("_HasMetallicGlossMap", 0.0f);

                compute.SetTexture(kernelNormalize, "_BaseColorIn", baseColorIn);
                compute.SetTexture(kernelNormalize, "_AoIn", aoIn);
                compute.SetTexture(kernelNormalize, "_MetallicGlossIn", metallicGlossIn);
                compute.SetTexture(kernelNormalize, "_BaseColorOut", baseColorOut);
                compute.SetTexture(kernelNormalize, "_PackInputs", packInputs);

                compute.SetTexture(kernelOctahedralEncode, "_NormalTexel", normalTexel);
                compute.SetTexture(kernelOctahedralEncode, "_AoIn", aoIn);
                compute.SetTexture(kernelOctahedralEncode, "_Octahedral", octahedral);

                compute.SetTexture(kernelSurfacePack, "_Octahedral", octahedral);
                compute.SetTexture(kernelSurfacePack, "_PackInputs", packInputs);
                compute.SetTexture(kernelSurfacePack, "_SurfaceOut", surfaceOut);

                compute.Dispatch(kernelNormalize, 1, 1, 1);
                compute.Dispatch(kernelOctahedralEncode, 1, 1, 1);
                compute.Dispatch(kernelSurfacePack, 1, 1, 1);

                normalizedBase = ReadBackFloatR(baseColorOut);
                Color32 surface = ReadBackColor32(surfaceOut, 1)[0];
                packedAo = surface.b / 255.0f;
            }
            finally
            {
                Release(baseColorIn, aoIn, normalTexel, metallicGlossIn, baseColorOut, packInputs, octahedral, surfaceOut);
            }
        }

        private static ComputeShader LoadPackShader()
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(PackComputeShaderPath);
            Assert.IsNotNull(shader, "NAMERPack.compute must load at " + PackComputeShaderPath);
            return shader;
        }

        private static RenderTexture CreateRenderTexture(GraphicsFormat format, int width, int height)
        {
            RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(width, height, format, 0)
            {
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            });
            rt.Create();
            return rt;
        }

        private static void UploadPixels(RenderTexture rt, Color[] pixels, int width, int height)
        {
            // RGBAHalf is always linear, so Graphics.Blit copies the exact float values
            // into the linear render target without any sRGB conversion.
            Texture2D upload = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            upload.SetPixels(pixels);
            upload.Apply(false, false);
            Graphics.Blit(upload, rt);
            Object.DestroyImmediate(upload);
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

        private static float ReadBackFloatR(RenderTexture rt)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "RGBAFloat readback must not error");
            NativeArray<float> data = req.GetData<float>();
            Assert.AreEqual(4, data.Length, "1x1 float readback must return RGBA");
            return data[0];
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

        private static void Release(params RenderTexture[] rts)
        {
            foreach (RenderTexture rt in rts)
            {
                if (rt != null)
                {
                    // Graphics.Blit leaves RenderTexture.active pointing at its destination;
                    // unbind before release (mirrors ComputeTexturePool's guard).
                    if (RenderTexture.active == rt)
                    {
                        RenderTexture.active = null;
                    }

                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
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

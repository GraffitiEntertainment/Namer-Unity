using System.Collections;
using GraffitiEntertainment.Namer.Core;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using UnityEditor;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// TEST-04: kernel golden tests proving the three GPU kernels in
    /// <c>Compute/NAMERPack.compute</c> (<c>CSOctahedralEncode</c>, <c>CSSurfacePack</c>,
    /// <c>CSNormalize</c>) are a faithful mirror of <see cref="NamerFormat"/> within the
    /// D-14 tolerances: packed alpha byte EXACT, octahedral R/G and AO within 1/255, and
    /// decoded-normal dot &gt;= 1 - 1e-3. Capability-gated (D-15): every test skips with an
    /// explicit report entry when compute/async-readback is unavailable, never a silent pass.
    /// </summary>
    public class GpuGoldenTests
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute";
        private const double ByteTolerance = 1.0 / 255.0;
        private const double NormalDotTolerance = 1e-3;

        // The five Blender golden vectors (DirectX normal-map texels + pack inputs),
        // mirroring BlenderGoldenVectorTests.cs so the GPU and CPU oracles share inputs.
        private static readonly float3[] GoldenNormals =
        {
            new float3(0.5f, 0.5f, 1.0f), // A: neutral
            new float3(0.5f, 0.5f, 1.0f), // B: neutral
            new float3(0.5f, 0.5f, 1.0f), // C: neutral
            new float3(1.0f, 0.0f, 0.0f), // D: +X-ish texel
            new float3(0.5f, 0.5f, 1.0f), // E: neutral
        };
        private static readonly float[] GoldenMetallic = { 0.0f, 1.0f, 0.5f, 0.0f, 0.6f };
        private static readonly float[] GoldenEmissive = { 0.0f, 0.0f, 0.1f, 0.0f, 0.2f };
        private static readonly float[] GoldenRoughness = { 0.5f, 1.0f, 0.0f, 0.5f, 0.5f };
        private static readonly float[] GoldenAo = { 1.0f, 1.0f, 1.0f, 1.0f, 0.25f };

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        /// <summary>
        /// CSOctahedralEncode produces the same octahedral R/G as
        /// <see cref="NamerFormat.OctahedralEncode"/> applied to the unpacked raw DirectX
        /// texel (within 1/255), and the oct decoded with
        /// <see cref="NamerFormat.OctahedralDecode"/> agrees with the source unit
        /// tangent normal via dot &gt;= 1 - 1e-3.
        /// </summary>
        [UnityTest]
        public IEnumerator CSOctahedralEncode_KernelMatchesCore()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU golden test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSOctahedralEncode");

            for (int i = 0; i < GoldenNormals.Length; i++)
            {
                float3 texel = GoldenNormals[i];
                RenderTexture normalTexel = null;
                RenderTexture aoIn = null;
                RenderTexture octahedral = null;
                try
                {
                    normalTexel = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                    aoIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                    octahedral = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);

                    // Raw DirectX texel in RGB; the kernel reads _NormalTexel[id.xy].xyz.
                    UploadPixels(normalTexel, new[] { new Color(texel.x, texel.y, texel.z, 1.0f) }, 1, 1);
                    // White AO so _AoIn[id.xy].g == 1 (only R/G are asserted here).
                    UploadPixels(aoIn, new[] { Color.white }, 1, 1);

                    compute.SetInts("_Size", new[] { 1, 1 });
                    compute.SetTexture(kernel, "_NormalTexel", normalTexel);
                    compute.SetTexture(kernel, "_AoIn", aoIn);
                    compute.SetTexture(kernel, "_Octahedral", octahedral);
                    compute.Dispatch(kernel, 1, 1, 1);

                    Color32 pixel = ReadBackColor32(octahedral, 1)[0];
                    float2 oct = new float2(pixel.r / 255.0f, pixel.g / 255.0f);
                    float2 expected = NamerFormat.OctahedralEncode(ReconstructRawTexel(texel, 1.0f));

                    Assert.AreEqual((double)expected.x, (double)oct.x, ByteTolerance, "oct R vector " + i);
                    Assert.AreEqual((double)expected.y, (double)oct.y, ByteTolerance, "oct G vector " + i);

                    float3 sourceUnitNormal = NamerFormat.OctahedralDecode(expected);
                    float3 decoded = NamerFormat.OctahedralDecode(oct);
                    Assert.GreaterOrEqual((double)math.dot(decoded, sourceUnitNormal), 1.0 - NormalDotTolerance,
                        "decoded normal must agree with the source unit tangent normal (D-14) vector " + i);
                }
                finally
                {
                    Release(normalTexel, aoIn, octahedral);
                }
            }

            yield return null;
        }

        /// <summary>
        /// CSOctahedralEncode unpacks Unity's delivered normal-map layouts before
        /// encoding: a raw-RGB upload (X in R, A = 1) and the NormalMap importer's
        /// DXT5nm/AG swizzle (X in A, R white, B unreliable) must produce identical
        /// octahedral bytes for the same authored direction. Live UAT regression
        /// (2026-08-28): the kernel encoded the raw [0,1] texel, so a NormalMap-imported
        /// 2048x2048 source (measured R = 255 everywhere, X in A) packed a phantom
        /// normal dominated by the white R channel — the result neither looked like a
        /// normal map nor lit like one.
        /// </summary>
        [UnityTest]
        public IEnumerator CSOctahedralEncode_UnpacksAgSwizzleAndRgbLayoutsIdentically()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU golden test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSOctahedralEncode");

            // Authored DirectX X/Y bytes (Z is rebuilt by the unpack, never sampled).
            float2[] cases =
            {
                new float2(0.5f, 0.5f),   // neutral (0, 0, 1)
                new float2(0.8f, 0.7f),   // +X +Y quadrant
                new float2(0.25f, 0.6f),  // -X +Y quadrant
                new float2(0.6f, 0.3f),   // +X -Y quadrant
            };

            for (int i = 0; i < cases.Length; i++)
            {
                float x = cases[i].x;
                float y = cases[i].y;
                float3 rawTexel = ReconstructRawTexel(new float3(x, y, 0.0f), 1.0f);
                float2 expected = NamerFormat.OctahedralEncode(rawTexel);

                // The same direction delivered in both Unity layouts; B differs to
                // prove it is ignored (the AG layout overwrites it during import).
                Color rgbDelivered = new Color(x, y, rawTexel.z, 1.0f);
                Color agDelivered = new Color(1.0f, y, 0.13f, x);

                Color32 rgbOct = EncodeSingleTexel(compute, kernel, rgbDelivered);
                Color32 agOct = EncodeSingleTexel(compute, kernel, agDelivered);

                Assert.AreEqual((double)expected.x, (double)(rgbOct.r / 255.0f), ByteTolerance, "raw-RGB oct R case " + i);
                Assert.AreEqual((double)expected.y, (double)(rgbOct.g / 255.0f), ByteTolerance, "raw-RGB oct G case " + i);
                Assert.AreEqual((double)expected.x, (double)(agOct.r / 255.0f), ByteTolerance, "AG-swizzle oct R case " + i);
                Assert.AreEqual((double)expected.y, (double)(agOct.g / 255.0f), ByteTolerance, "AG-swizzle oct G case " + i);
                Assert.AreEqual((int)rgbOct.r, (int)agOct.r, "both layouts must produce identical oct R bytes case " + i);
                Assert.AreEqual((int)rgbOct.g, (int)agOct.g, "both layouts must produce identical oct G bytes case " + i);
            }

            yield return null;
        }

        /// <summary>
        /// CSSurfacePack produces a packed surface whose alpha byte matches
        /// <see cref="NamerFormat.PackAlphaBits"/> EXACTLY, and whose R/G/B match
        /// <see cref="NamerFormat.PackSurface"/> within 1/255.
        /// </summary>
        [UnityTest]
        public IEnumerator CSSurfacePack_KernelMatchesCore()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU golden test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSSurfacePack");

            for (int i = 0; i < GoldenNormals.Length; i++)
            {
                float3 texel = GoldenNormals[i];
                float metallic = GoldenMetallic[i];
                float emissive = GoldenEmissive[i];
                float roughness = GoldenRoughness[i];
                float ao = GoldenAo[i];

                RenderTexture octahedral = null;
                RenderTexture packInputs = null;
                RenderTexture surfaceOut = null;
                try
                {
                    octahedral = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                    packInputs = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                    surfaceOut = CreateRenderTexture(GraphicsFormat.R8G8B8A8_UNorm, 1, 1);

                    float2 oct = NamerFormat.OctahedralEncode(texel);
                    // _Octahedral = (oct.x, oct.y, ao, 0); _PackInputs = (metallic, roughness, emissive, 0).
                    UploadPixels(octahedral, new[] { new Color(oct.x, oct.y, ao, 0.0f) }, 1, 1);
                    UploadPixels(packInputs, new[] { new Color(metallic, roughness, emissive, 0.0f) }, 1, 1);

                    compute.SetInts("_Size", new[] { 1, 1 });
                    compute.SetTexture(kernel, "_Octahedral", octahedral);
                    compute.SetTexture(kernel, "_PackInputs", packInputs);
                    compute.SetTexture(kernel, "_SurfaceOut", surfaceOut);
                    compute.Dispatch(kernel, 1, 1, 1);

                    Color32 pixel = ReadBackColor32(surfaceOut, 1)[0];
                    float4 expected = NamerFormat.PackSurface(texel, ao, metallic, emissive, roughness);

                    Assert.AreEqual((int)NamerFormat.PackAlphaBits(metallic, emissive, roughness), (int)pixel.a,
                        "packed alpha byte must match EXACTLY (D-14) vector " + i);
                    Assert.AreEqual((double)expected.x, pixel.r / 255.0, ByteTolerance, "surface R vector " + i);
                    Assert.AreEqual((double)expected.y, pixel.g / 255.0, ByteTolerance, "surface G vector " + i);
                    Assert.AreEqual((double)expected.z, pixel.b / 255.0, ByteTolerance, "surface B vector " + i);
                }
                finally
                {
                    Release(octahedral, packInputs, surfaceOut);
                }
            }

            yield return null;
        }

        /// <summary>
        /// CSNormalize applies the sRGB-&gt;linear decode (IEC 61966-2-1) and AO un-multiply
        /// exactly like the CPU oracle, and routes metallic/smoothness from the map path
        /// (or scalar fallbacks) into <c>_PackInputs</c>.
        /// </summary>
        [UnityTest]
        public IEnumerator CSNormalize_KernelMatchesReference()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU golden test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSNormalize");

            // -- Scalar path: sRGB decode + AO un-multiply + scalar metallic/roughness. --
            const int width = 4;
            const int height = 1;
            RenderTexture baseColorIn = null;
            RenderTexture aoIn = null;
            RenderTexture metallicGlossIn = null;
            RenderTexture baseColorOut = null;
            RenderTexture packInputs = null;
            try
            {
                baseColorIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, width, height);
                aoIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, width, height);
                metallicGlossIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, width, height);
                baseColorOut = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, width, height);
                packInputs = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, width, height);

                Color[] basePixels =
                {
                    new Color(0.25f, 0.25f, 0.25f, 1.0f),
                    new Color(0.5f, 0.5f, 0.5f, 1.0f),
                    new Color(0.5f, 0.5f, 0.5f, 1.0f),
                    new Color(1.0f, 1.0f, 1.0f, 1.0f),
                };
                Color[] aoPixels =
                {
                    new Color(0.0f, 1.0f, 0.0f, 1.0f),
                    new Color(0.0f, 1.0f, 0.0f, 1.0f),
                    new Color(0.0f, 0.5f, 0.0f, 1.0f),
                    new Color(0.0f, 1.0f, 0.0f, 1.0f),
                };
                UploadPixels(baseColorIn, basePixels, width, height);
                UploadPixels(aoIn, aoPixels, width, height);

                compute.SetInts("_Size", new[] { width, height });
                compute.SetFloat("_SourceIsSrgb", 1.0f);
                compute.SetFloat("_AoUnmultiplyStrength", 1.0f);
                compute.SetFloat("_Metallic", 0.3f);
                compute.SetFloat("_SmoothnessScalar", 1.0f);
                compute.SetFloat("_Roughness", 0.5f);
                compute.SetFloat("_Emissive", 0.0f);
                compute.SetFloat("_SmoothnessTextureChannel", 0.0f);
                compute.SetFloat("_HasMetallicGlossMap", 0.0f);

                compute.SetTexture(kernel, "_BaseColorIn", baseColorIn);
                compute.SetTexture(kernel, "_AoIn", aoIn);
                compute.SetTexture(kernel, "_MetallicGlossIn", metallicGlossIn);
                compute.SetTexture(kernel, "_BaseColorOut", baseColorOut);
                compute.SetTexture(kernel, "_PackInputs", packInputs);
                compute.Dispatch(kernel, (width + 7) / 8, (height + 7) / 8, 1);

                Color32[] baseOut = ReadBackColor32(baseColorOut, width * height);
                Color32[] packOut = ReadBackColor32(packInputs, width * height);

                float[] srgbs = { 0.25f, 0.5f, 0.5f, 1.0f };
                float[] aos = { 1.0f, 1.0f, 0.5f, 1.0f };
                for (int i = 0; i < width; i++)
                {
                    float expected = SRGBToLinear(srgbs[i]) / math.lerp(1.0f, math.max(aos[i], 1e-6f), 1.0f);
                    Assert.AreEqual((double)expected, baseOut[i].r / 255.0, ByteTolerance, "base R texel " + i);
                    Assert.AreEqual((double)expected, baseOut[i].g / 255.0, ByteTolerance, "base G texel " + i);
                    Assert.AreEqual((double)expected, baseOut[i].b / 255.0, ByteTolerance, "base B texel " + i);

                    Assert.AreEqual(0.3, (double)(packOut[i].r / 255.0f), ByteTolerance, "scalar metallic texel " + i);
                    Assert.AreEqual(0.5, (double)(packOut[i].g / 255.0f), ByteTolerance, "scalar roughness texel " + i);
                }
            }
            finally
            {
                Release(baseColorIn, aoIn, metallicGlossIn, baseColorOut, packInputs);
            }

            // -- Metallic/gloss map path: metallic from R, smoothness from A (channel 0). --
            Color32 map0 = RunNormalizePack(compute, kernel,
                new Color(0.5f, 0.5f, 0.5f, 1.0f), new Color(0.0f, 1.0f, 0.0f, 1.0f),
                new Color(0.6f, 0.0f, 0.0f, 0.4f),
                1.0f, 1.0f, 0.0f, 1.0f, 0.5f, 0.0f, 0.0f, 1.0f);
            Assert.AreEqual(0.6, (double)(map0.r / 255.0f), ByteTolerance, "map metallic (channel 0)");
            Assert.AreEqual(0.6, (double)(map0.g / 255.0f), ByteTolerance, "map roughness = 1 - smoothness (channel 0)");

            // -- Smoothness scalar multiply: smoothness = channelSample * _SmoothnessScalar. --
            Color32 mapScalar = RunNormalizePack(compute, kernel,
                new Color(0.5f, 0.5f, 0.5f, 1.0f), new Color(0.0f, 1.0f, 0.0f, 1.0f),
                new Color(0.6f, 0.0f, 0.0f, 0.8f),
                1.0f, 1.0f, 0.0f, 0.5f, 0.5f, 0.0f, 0.0f, 1.0f);
            Assert.AreEqual(0.6, (double)(mapScalar.r / 255.0f), ByteTolerance, "map metallic (scalar 0.5)");
            Assert.AreEqual(0.6, (double)(mapScalar.g / 255.0f), ByteTolerance, "roughness = 1 - smoothness*scalar");

            // -- Smoothness from base alpha: _SmoothnessTextureChannel = 1. --
            Color32 mapChannel1 = RunNormalizePack(compute, kernel,
                new Color(0.5f, 0.5f, 0.5f, 0.8f), new Color(0.0f, 1.0f, 0.0f, 1.0f),
                new Color(0.6f, 0.0f, 0.0f, 0.0f),
                1.0f, 1.0f, 0.0f, 1.0f, 0.5f, 0.0f, 1.0f, 1.0f);
            Assert.AreEqual(0.6, (double)(mapChannel1.r / 255.0f), ByteTolerance, "map metallic (channel 1)");
            Assert.AreEqual(0.2, (double)(mapChannel1.g / 255.0f), ByteTolerance, "roughness from base alpha (channel 1)");

            yield return null;
        }

        // --------------------------------------------------------------------

        // Mirrors CSOctahedralEncode's Unity-layout unpack: X selected from R (raw
        // RGB upload, A = 1) or A (DXT5nm/AG swizzle, R white) via x *= w, then Z
        // rebuilt from the signed XY as the authored DirectX byte.
        private static float3 ReconstructRawTexel(float3 rgb, float a)
        {
            float x = rgb.x * a;
            float2 signedXY = new float2(x, rgb.y) * 2.0f - 1.0f;
            float z = math.sqrt(1.0f - math.saturate(math.dot(signedXY, signedXY))) * 0.5f + 0.5f;
            return new float3(x, rgb.y, z);
        }

        private static Color32 EncodeSingleTexel(ComputeShader compute, int kernel, Color delivered)
        {
            RenderTexture normalTexel = null;
            RenderTexture aoIn = null;
            RenderTexture octahedral = null;
            try
            {
                normalTexel = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                aoIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                octahedral = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);

                UploadPixels(normalTexel, new[] { delivered }, 1, 1);
                UploadPixels(aoIn, new[] { Color.white }, 1, 1);

                compute.SetInts("_Size", new[] { 1, 1 });
                compute.SetTexture(kernel, "_NormalTexel", normalTexel);
                compute.SetTexture(kernel, "_AoIn", aoIn);
                compute.SetTexture(kernel, "_Octahedral", octahedral);
                compute.Dispatch(kernel, 1, 1, 1);

                return ReadBackColor32(octahedral, 1)[0];
            }
            finally
            {
                Release(normalTexel, aoIn, octahedral);
            }
        }

        private static ComputeShader LoadShader()
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            Assert.IsNotNull(shader, "NAMERPack.compute must load at " + ComputeShaderPath);
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

        private static Color32 RunNormalizePack(
            ComputeShader compute,
            int kernel,
            Color baseColor,
            Color ao,
            Color metallicGloss,
            float sourceIsSrgb,
            float aoStrength,
            float metallic,
            float smoothnessScalar,
            float roughness,
            float emissive,
            float smoothnessChannel,
            float hasMetallicGlossMap)
        {
            RenderTexture baseColorIn = null;
            RenderTexture aoIn = null;
            RenderTexture metallicGlossIn = null;
            RenderTexture baseColorOut = null;
            RenderTexture packInputs = null;
            try
            {
                baseColorIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                aoIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                metallicGlossIn = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                baseColorOut = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                packInputs = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);

                UploadPixels(baseColorIn, new[] { baseColor }, 1, 1);
                UploadPixels(aoIn, new[] { ao }, 1, 1);
                UploadPixels(metallicGlossIn, new[] { metallicGloss }, 1, 1);

                compute.SetInts("_Size", new[] { 1, 1 });
                compute.SetFloat("_SourceIsSrgb", sourceIsSrgb);
                compute.SetFloat("_AoUnmultiplyStrength", aoStrength);
                compute.SetFloat("_Metallic", metallic);
                compute.SetFloat("_SmoothnessScalar", smoothnessScalar);
                compute.SetFloat("_Roughness", roughness);
                compute.SetFloat("_Emissive", emissive);
                compute.SetFloat("_SmoothnessTextureChannel", smoothnessChannel);
                compute.SetFloat("_HasMetallicGlossMap", hasMetallicGlossMap);

                compute.SetTexture(kernel, "_BaseColorIn", baseColorIn);
                compute.SetTexture(kernel, "_AoIn", aoIn);
                compute.SetTexture(kernel, "_MetallicGlossIn", metallicGlossIn);
                compute.SetTexture(kernel, "_BaseColorOut", baseColorOut);
                compute.SetTexture(kernel, "_PackInputs", packInputs);
                compute.Dispatch(kernel, 1, 1, 1);

                return ReadBackColor32(packInputs, 1)[0];
            }
            finally
            {
                Release(baseColorIn, aoIn, metallicGlossIn, baseColorOut, packInputs);
            }
        }

        private static float SRGBToLinear(float c)
        {
            return c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static void Release(params RenderTexture[] rts)
        {
            foreach (RenderTexture rt in rts)
            {
                if (rt != null)
                {
                    rt.Release();
                    Object.DestroyImmediate(rt);
                }
            }
        }
    }
}

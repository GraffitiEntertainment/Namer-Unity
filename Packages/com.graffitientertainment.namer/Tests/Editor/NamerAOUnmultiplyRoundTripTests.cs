using System.Collections;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Pins the AO un-multiply round-trip contract (debug session
    /// ao-unmultiply-roundtrip, user decision C). The pack stage divides the saved
    /// albedo by <c>lerp(1, max(ao, floor), strength)</c> (NAMERPack.compute
    /// CSNormalize), the processor persists that pack-time strength on the generated
    /// material as <c>_AoUnmultiplyStrength</c>, and the runtime decode re-multiplies
    /// the GATED occlusion back into the albedo — so the AO gate ON reconstructs the
    /// original albedo under any lighting, and the gate OFF neutralizes BOTH the
    /// ambient occlusion term and the re-multiply (the albedo shows the packed base
    /// unchanged). The shader property defaults to 0 — the neutral of
    /// <c>lerp(1, ao, s)</c> — so legacy materials generated before the value was
    /// persisted decode byte-identically to the pre-fix shader. GPU tests are
    /// capability-gated (D-15); the round-trip tests execute the REAL NAMER ForwardLit
    /// pass through <see cref="NamerPreviewRenderer"/> (the production After-pane
    /// render path) and compare pane pixels, so a shader-side regression cannot hide
    /// behind a C# mirror of the formula.
    /// </summary>
    public class NamerAOUnmultiplyRoundTripTests
    {
        private const int WorkingSize = 64;
        private const float UnmultiplyStrength = 0.8f;
        private const float AuthoredAo = 128f / 255f;   // right-half occlusion gray byte
        private const float SourceAlbedo = 128f / 255f; // solid base gray byte, linear
        private const string TempFolder = "Assets/NAMER_Tests_Temp";

        // Pane-pixel comparison tolerances (bytes, patch means).
        private const float EqualTolerance = 3f;
        private const float BrighterMargin = 8f;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        private static bool RenderAvailable =>
            SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

        // --------------------------------------------------------------------
        // 1. Legacy neutrality: the shader property defaults to 0.
        // --------------------------------------------------------------------

        /// <summary>
        /// A freshly created NAMER material must read _AoUnmultiplyStrength as 0 (the
        /// neutral of lerp(1, ao, s)) — materials generated before the processor
        /// persisted the value (and hand-created ones) must decode byte-identically to
        /// the pre-fix shader, mirroring the neutral-default pattern of the _DbgEnable*
        /// gates and the "black" {} _RoughnessOffsetMap default.
        /// </summary>
        [Test]
        public void ShaderAoUnmultiplyStrength_DefaultsToLegacyNeutralZero()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");
            Assert.IsTrue(shader.FindPropertyIndex("_AoUnmultiplyStrength") >= 0,
                "NAMER shader must declare _AoUnmultiplyStrength");

            Material material = new Material(shader);
            try
            {
                Assert.AreEqual(0.0f, material.GetFloat("_AoUnmultiplyStrength"),
                    "a fresh material must default _AoUnmultiplyStrength to 0 so legacy "
                    + "materials never persisted render exactly as they do today");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        // --------------------------------------------------------------------
        // 2. Pack-side oracle: the divide the decode must invert.
        // --------------------------------------------------------------------

        /// <summary>
        /// With an authored (raw-copied) occlusion map, the pack stage must divide the
        /// saved base by lerp(1, ao, strength) on the occluded half and leave it
        /// unchanged where ao == 1, and the packed surface B channel must carry the
        /// authored AO bytes — the exact premises the decode re-multiply inverts.
        /// </summary>
        [UnityTest]
        public IEnumerator Pack_DividesBaseByAuthoredAo_SurfaceCarriesRawAo()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU pack-divide test (D-15: Metal is the verified target).");
                yield break;
            }

            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            Texture2D occlusion = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                FillOcclusionSplit(occlusion);

                NamerMaterialInspection inspection = BuildInspection(baseMap, occlusion, UnmultiplyStrength);
                NamerComputeResult result = pipeline.Process(inspection);
                try
                {
                    Color[] packedBase = ReadBackFloat(result.NormalizedBaseColor, WorkingSize * WorkingSize);
                    Color32[] surface = ReadBackColor32(result.PackedSurface, WorkingSize * WorkingSize);

                    int left = IndexAt(WorkingSize / 4, WorkingSize / 2);
                    int right = IndexAt(3 * WorkingSize / 4, WorkingSize / 2);
                    float expectedDivided = SourceAlbedo / Mathf.Lerp(1f, AuthoredAo, UnmultiplyStrength);

                    Assert.AreEqual(SourceAlbedo, packedBase[left].r, 0.01f,
                        "ao == 1 half must keep the source albedo (divide is identity)");
                    Assert.AreEqual(expectedDivided, packedBase[right].r, 0.01f,
                        "ao < 1 half must carry the pack-time divide base / lerp(1, ao, strength)");
                    Assert.AreEqual(255, (int)surface[left].b, 1,
                        "authored ao == 1 must pack to surface B ~255");
                    Assert.AreEqual(128, (int)surface[right].b, 1,
                        "authored ao gray byte must pack raw into surface B");
                }
                finally
                {
                    pipeline.ReleaseResult(result);
                }

                Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after the pack run");
            }
            finally
            {
                pipeline.Dispose();
                Destroy(baseMap, occlusion);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 3. Happy path: gate ON, strength persisted -> decode inverts the divide.
        // --------------------------------------------------------------------

        /// <summary>
        /// Rendering the REAL NAMER lit pass (production After-pane path): a divided
        /// base + the persisted pack-time strength + gate ON must render byte-equal to
        /// an undivided strength-0 pack of the same surface — the re-multiply exactly
        /// inverts the pack-time divide under real lighting, on BOTH the ao == 1 and
        /// ao &lt; 1 halves. A control render with the re-multiply off (strength 0 on the
        /// divided base) must come out brighter on the occluded half, proving the
        /// equality above is not vacuous (magenta/black failure renders cannot pass).
        /// </summary>
        [UnityTest]
        public IEnumerator RoundTrip_GateOn_RemultiplyInvertsPackDivide()
        {
            if (!ComputeAvailable || !RenderAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback or graphics device unavailable — skipping GPU round-trip render test (D-15).");
                yield break;
            }

            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            Texture2D occlusion = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            NamerComputePipeline pipeline = new NamerComputePipeline();
            NamerPreviewRenderer renderer = new NamerPreviewRenderer();
            Mesh quad = CreateCameraFacingQuad();
            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                FillOcclusionSplit(occlusion);

                NamerComputeResult divided = pipeline.Process(BuildInspection(baseMap, occlusion, UnmultiplyStrength));
                NamerComputeResult undivided = pipeline.Process(BuildInspection(baseMap, occlusion, 0f));
                try
                {
                    Material roundTrip = BuildNamerMaterial(
                        shader, divided.NormalizedBaseColor, divided.PackedSurface, UnmultiplyStrength, gate: 1f);
                    Material source = BuildNamerMaterial(
                        shader, undivided.NormalizedBaseColor, undivided.PackedSurface, 0f, gate: 1f);
                    Material control = BuildNamerMaterial(
                        shader, divided.NormalizedBaseColor, divided.PackedSurface, 0f, gate: 1f);
                    try
                    {
                        int rtW, rtH, srcW, srcH, ctlW, ctlH;
                        Color32[] roundTripPx = RenderAfterBytes(renderer, quad, roundTrip, out rtW, out rtH);
                        Color32[] sourcePx = RenderAfterBytes(renderer, quad, source, out srcW, out srcH);
                        Color32[] controlPx = RenderAfterBytes(renderer, quad, control, out ctlW, out ctlH);

                        AssertPatchMeanEqual(roundTripPx, rtW, rtH, sourcePx, srcW, srcH, LeftPatchRange,
                            "gate ON round-trip must reconstruct the original albedo (ao == 1 half)");
                        AssertPatchMeanEqual(roundTripPx, rtW, rtH, sourcePx, srcW, srcH, RightPatchRange,
                            "gate ON round-trip must reconstruct the original albedo (ao < 1 half: re-multiply inverts the divide under real lighting)");

                        AssertPatchMeanEqual(roundTripPx, rtW, rtH, controlPx, ctlW, ctlH, LeftPatchRange,
                            "control: ao == 1 half is divide-identity, so the re-multiply must not matter");
                        AssertPatchMeanBrighter(controlPx, ctlW, ctlH, roundTripPx, rtW, rtH, RightPatchRange,
                            "control: without the re-multiply the divided base renders brighter than the source albedo (pre-fix appearance)");
                    }
                    finally
                    {
                        Destroy(roundTrip, source, control);
                    }
                }
                finally
                {
                    pipeline.ReleaseResult(divided);
                    pipeline.ReleaseResult(undivided);
                }
            }
            finally
            {
                renderer.Dispose();
                pipeline.Dispose();
                Destroy(quad, baseMap, occlusion);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 4. Unhappy path: gate OFF neutralizes BOTH terms.
        // --------------------------------------------------------------------

        /// <summary>
        /// Gate OFF must neutralize BOTH the ambient occlusion term AND the albedo
        /// re-multiply: the persisted strength becomes irrelevant (a divided base
        /// renders identically at strength s and strength 0), and the albedo shows the
        /// PACKED base unchanged — brighter than the reconstructed original on the
        /// occluded half, equal on the ao == 1 half. If the gate only moved the ambient
        /// term (the pre-fix bug shape), the strength-s render would instead equal the
        /// reconstructed original and differ from the strength-0 render.
        /// </summary>
        [UnityTest]
        public IEnumerator GateOff_NeutralizesOcclusionAndRemultiply_AlbedoEqualsPackedBase()
        {
            if (!ComputeAvailable || !RenderAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback or graphics device unavailable — skipping GPU gate-off render test (D-15).");
                yield break;
            }

            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            Texture2D occlusion = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            NamerComputePipeline pipeline = new NamerComputePipeline();
            NamerPreviewRenderer renderer = new NamerPreviewRenderer();
            Mesh quad = CreateCameraFacingQuad();
            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                FillOcclusionSplit(occlusion);

                NamerComputeResult divided = pipeline.Process(BuildInspection(baseMap, occlusion, UnmultiplyStrength));
                NamerComputeResult undivided = pipeline.Process(BuildInspection(baseMap, occlusion, 0f));
                try
                {
                    Material gateOffStrength = BuildNamerMaterial(
                        shader, divided.NormalizedBaseColor, divided.PackedSurface, UnmultiplyStrength, gate: 0f);
                    Material gateOffNeutral = BuildNamerMaterial(
                        shader, divided.NormalizedBaseColor, divided.PackedSurface, 0f, gate: 0f);
                    Material gateOffOriginal = BuildNamerMaterial(
                        shader, undivided.NormalizedBaseColor, undivided.PackedSurface, 0f, gate: 0f);
                    try
                    {
                        int sW, sH, nW, nH, oW, oH;
                        Color32[] strengthPx = RenderAfterBytes(renderer, quad, gateOffStrength, out sW, out sH);
                        Color32[] neutralPx = RenderAfterBytes(renderer, quad, gateOffNeutral, out nW, out nH);
                        Color32[] originalPx = RenderAfterBytes(renderer, quad, gateOffOriginal, out oW, out oH);

                        AssertPatchMeanEqual(strengthPx, sW, sH, neutralPx, nW, nH, LeftPatchRange,
                            "gate OFF must make the strength irrelevant on the ao == 1 half (re-multiply neutralized)");
                        AssertPatchMeanEqual(strengthPx, sW, sH, neutralPx, nW, nH, RightPatchRange,
                            "gate OFF must neutralize the re-multiply — strength s and strength 0 render identically");
                        AssertPatchMeanEqual(strengthPx, sW, sH, originalPx, oW, oH, LeftPatchRange,
                            "gate OFF ao == 1 half: divide is identity, packed base equals the original");
                        AssertPatchMeanBrighter(strengthPx, sW, sH, originalPx, oW, oH, RightPatchRange,
                            "gate OFF albedo must show the PACKED (divided) base unchanged — brighter than the reconstructed original, never re-multiplied");
                    }
                    finally
                    {
                        Destroy(gateOffStrength, gateOffNeutral, gateOffOriginal);
                    }
                }
                finally
                {
                    pipeline.ReleaseResult(divided);
                    pipeline.ReleaseResult(undivided);
                }
            }
            finally
            {
                renderer.Dispose();
                pipeline.Dispose();
                Destroy(quad, baseMap, occlusion);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 5. Persistence: the generator writes the pack-time strength to the .mat.
        // --------------------------------------------------------------------

        /// <summary>
        /// <see cref="AssetGenerator"/> must persist the inspection's pack-time
        /// AoUnmultiplyStrength onto the generated material, so the runtime decode
        /// re-multiplies by exactly the strength the pack divided with (the written
        /// .mat is reloaded from disk for the assertion — the full serialization
        /// round-trip).
        /// </summary>
        [UnityTest]
        public IEnumerator GeneratedMaterial_PersistsPackTimeAoUnmultiplyStrength()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU generator persistence test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            NamerComputePipeline pipeline = new NamerComputePipeline();
            AssetGenerator generator = new AssetGenerator();
            Texture2D baseMap = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            Texture2D occlusion = new Texture2D(WorkingSize, WorkingSize, TextureFormat.RGBA32, false, true);
            try
            {
                FillSolid(baseMap, new Color(0.5f, 0.5f, 0.5f, 1.0f));
                FillOcclusionSplit(occlusion);

                Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
                Assert.IsNotNull(shader, "NAMER shader must load");

                NamerMaterialInspection inspection = BuildInspection(baseMap, occlusion, UnmultiplyStrength);
                inspection.Material = new Material(shader) { name = "AoRoundTripPersist" };

                // Only the path-composition fields are read by Generate (the strength is a
                // per-material inspection input, not a settings key).
                var settings = new NamerProcessorSettings
                {
                    Prefix = "",
                    Suffix = "",
                    OverwriteGenerated = false,
                };

                NamerComputeResult result = pipeline.Process(inspection);
                string materialPath;
                try
                {
                    NamerGeneratedAsset asset = generator.Generate(result, inspection, settings, TempFolder + "/", null);
                    materialPath = asset.MaterialPath;
                }
                finally
                {
                    pipeline.ReleaseResult(result);
                }

                Material generated = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                Assert.IsNotNull(generated, "generated material must load: " + materialPath);
                Assert.AreEqual(UnmultiplyStrength, generated.GetFloat("_AoUnmultiplyStrength"),
                    "the generated material must carry the pack-time AO un-multiply strength");

                Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after generation");
            }
            finally
            {
                pipeline.Dispose();
                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(baseMap, occlusion);
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // Fixtures / helpers
        // --------------------------------------------------------------------

        private static NamerMaterialInspection BuildInspection(Texture2D baseMap, Texture2D occlusion, float unmultiplyStrength)
        {
            return new NamerMaterialInspection
            {
                BaseMap = baseMap,
                BaseMapIsSrgb = false,
                NormalMap = null,
                OcclusionMap = occlusion,
                OcclusionStrength = 1f,
                MetallicGlossMap = null,
                Metallic = 0.0f,
                Smoothness = 0f,
                Roughness = 1f,
                RoughnessExtractStrength = 0f,
                DipSource = NamerDipSource.RemovedDetail,
                Emissive = 0.0f,
                AoUnmultiplyStrength = unmultiplyStrength,
                SmoothnessTextureChannel = 0,
            };
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

        /// <summary>
        /// Authored occlusion: left half white (ao == 1), right half gray byte 128
        /// (ao == 128/255). Authored maps are raw-copied (D-07), so the packed surface
        /// B channel and the pack-time divide see exactly these values.
        /// </summary>
        private static void FillOcclusionSplit(Texture2D texture)
        {
            Color[] pixels = new Color[texture.width * texture.height];
            for (int y = 0; y < texture.height; y++)
            {
                for (int x = 0; x < texture.width; x++)
                {
                    pixels[y * texture.width + x] = x < texture.width / 2
                        ? new Color(1f, 1f, 1f, 1f)
                        : new Color(0.5f, 0.5f, 0.5f, 1f);
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
        }

        private static int IndexAt(int x, int y)
        {
            return y * WorkingSize + x;
        }

        private static Color[] ReadBackFloat(RenderTexture rt, int expectedCount)
        {
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color> data = req.GetData<Color>();
            Assert.AreEqual(expectedCount, data.Length, "readback must return the expected pixel count");
            Color[] result = new Color[data.Length];
            for (int i = 0; i < data.Length; i++)
            {
                result[i] = data[i];
            }

            return result;
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

        /// <summary>
        /// A camera-facing XY quad (normals -Z toward the preview camera, explicit
        /// tangents, WHITE vertex colors so the NAMER vertex-color term is identity).
        /// _Cull is forced to 0 on the compared materials, so facing cannot vary the
        /// shading (the explicit normals make both rasterized sides shade identically).
        /// </summary>
        private static Mesh CreateCameraFacingQuad()
        {
            Mesh mesh = new Mesh { name = "NamerAoRoundTripQuad" };
            mesh.vertices = new[]
            {
                new Vector3(-1f, -1f, 0f), new Vector3(1f, -1f, 0f),
                new Vector3(1f, 1f, 0f), new Vector3(-1f, 1f, 0f),
            };
            mesh.normals = new[]
            {
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f),
                new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, -1f),
            };
            mesh.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
                new Vector4(1f, 0f, 0f, 1f), new Vector4(1f, 0f, 0f, 1f),
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f),
            };
            Color32[] colors = new Color32[4];
            for (int i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color32(255, 255, 255, 255);
            }

            mesh.colors32 = colors;
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Material BuildNamerMaterial(Shader shader, Texture baseMap, Texture surfaceMap, float strength, float gate)
        {
            Material material = new Material(shader)
            {
                name = "NamerAoRoundTripMaterial",
                hideFlags = HideFlags.HideAndDontSave,
            };
            material.SetTexture("_SurfaceMap", surfaceMap);
            material.SetTexture("_BaseResidualMap", baseMap);
            material.SetFloat("_AoUnmultiplyStrength", strength);
            material.SetFloat("_DbgEnableAO", gate);
            material.SetFloat("_Cull", 0f);
            return material;
        }

        /// <summary>
        /// Renders one material through the production After-pane path and reads the
        /// after pane's pixels back (NamerPreviewRendererTests' ReadPanePixels pattern).
        /// Reports the pane RT's actual dimensions for the patch comparisons.
        /// </summary>
        private static Color32[] RenderAfterBytes(NamerPreviewRenderer renderer, Mesh mesh, Material material, out int width, out int height)
        {
            PreviewRenderResult render = renderer.Render(mesh, mesh, material, material, new Rect(0, 0, 256, 128));
            Assert.IsNotNull(render.After, "the after pane must render");

            RenderTexture rt = (RenderTexture)render.After;
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = rt;
            Texture2D read = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true)
            {
                name = "NamerAoRoundTripReadback",
                hideFlags = HideFlags.HideAndDontSave,
            };
            read.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            read.Apply();
            RenderTexture.active = prev;

            Color32[] pixels = read.GetPixels32();
            width = rt.width;
            height = rt.height;
            UnityEngine.Object.DestroyImmediate(read);
            return pixels;
        }

        // Patch windows as FRACTIONS of the pane RT: the after object lives in the right
        // half of the full-width RT; within it, the texture's left (ao == 1) region and
        // right (ao &lt; 1) region map to these interior windows (clear of the framing
        // margins and the occlusion seam at u == 0.5).
        private static readonly Vector2 LeftPatchRange = new Vector2(0.57f, 0.71f);
        private static readonly Vector2 RightPatchRange = new Vector2(0.80f, 0.94f);
        private static readonly Vector2 PatchYRange = new Vector2(0.35f, 0.66f);

        private static float PatchMean(Color32[] pixels, int width, int height, Vector2 xRange)
        {
            int x0 = Mathf.RoundToInt(width * xRange.x);
            int x1 = Mathf.RoundToInt(width * xRange.y);
            int y0 = Mathf.RoundToInt(height * PatchYRange.x);
            int y1 = Mathf.RoundToInt(height * PatchYRange.y);

            double sum = 0.0;
            int count = 0;
            for (int y = y0; y < y1; y++)
            {
                for (int x = x0; x < x1; x++)
                {
                    Color32 p = pixels[y * width + x];
                    sum += (p.r + p.g + p.b) / 3.0;
                    count++;
                }
            }

            Assert.Greater(count, 0, "patch must contain pixels");
            return (float)(sum / count);
        }

        private static void AssertPatchMeanEqual(Color32[] actual, int actualW, int actualH,
            Color32[] expected, int expectedW, int expectedH, Vector2 xRange, string message)
        {
            float diff = Mathf.Abs(PatchMean(actual, actualW, actualH, xRange) - PatchMean(expected, expectedW, expectedH, xRange));
            Assert.LessOrEqual(diff, EqualTolerance, message + " (patch mean diff " + diff.ToString("F2") + " bytes)");
        }

        private static void AssertPatchMeanBrighter(Color32[] brighter, int brighterW, int brighterH,
            Color32[] dimmer, int dimmerW, int dimmerH, Vector2 xRange, string message)
        {
            float diff = PatchMean(brighter, brighterW, brighterH, xRange) - PatchMean(dimmer, dimmerW, dimmerH, xRange);
            Assert.GreaterOrEqual(diff, BrighterMargin, message + " (patch mean diff " + diff.ToString("F2") + " bytes)");
        }

        private static void EnsureTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_Tests_Temp");
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

        // -- EditorPrefs isolation (generator settings are EditorPrefs-backed) ----

        private sealed class PrefsSnapshot
        {
            public string Prefix;
            public string Suffix;
            public bool OverwriteGenerated;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
        }

        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Prefix = EditorPrefs.GetString("NamerProcessor.Prefix", string.Empty),
                Suffix = EditorPrefs.GetString("NamerProcessor.Suffix", string.Empty),
                OverwriteGenerated = EditorPrefs.GetBool("NamerProcessor.OverwriteGenerated", false),
                HadPrefix = EditorPrefs.HasKey("NamerProcessor.Prefix"),
                HadSuffix = EditorPrefs.HasKey("NamerProcessor.Suffix"),
                HadOverwriteGenerated = EditorPrefs.HasKey("NamerProcessor.OverwriteGenerated"),
            };
        }

        private static void RestorePrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadPrefix) { EditorPrefs.SetString("NamerProcessor.Prefix", snapshot.Prefix); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Prefix"); }

            if (snapshot.HadSuffix) { EditorPrefs.SetString("NamerProcessor.Suffix", snapshot.Suffix); }
            else { EditorPrefs.DeleteKey("NamerProcessor.Suffix"); }

            if (snapshot.HadOverwriteGenerated) { EditorPrefs.SetBool("NamerProcessor.OverwriteGenerated", snapshot.OverwriteGenerated); }
            else { EditorPrefs.DeleteKey("NamerProcessor.OverwriteGenerated"); }
        }
    }
}

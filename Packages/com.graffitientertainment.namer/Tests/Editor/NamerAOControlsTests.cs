using System.Collections;
using System.Reflection;
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
    /// Phase 03.1 AO-tweak-control tests (plan 03). Proves the source-agnostic AO
    /// strength/contrast remap semantics at the <c>CSAoRemap</c> kernel level (D-11), the
    /// EditorPrefs persistence of the three new AO tweak settings (D-13), and that
    /// <c>AoUnmultiplyStrength</c> remains the single un-multiply gate — no new toggle
    /// was introduced (D-10). GPU tests are <c>[UnityTest]</c> with the <c>Assert.Ignore</c>
    /// capability gate (D-15); EditorPrefs tests are plain <c>[Test]</c> with snapshot/restore
    /// of the three <c>NamerProcessor.Ao*</c> keys in <c>finally</c>. The occluder fallback
    /// logic and bake routing are covered by 03.1-02's bake tests, so no live window is
    /// required here (the occluder warning UI is visual).
    /// </summary>
    public class NamerAOControlsTests
    {
        private const string AoComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute";
        private const double ByteTolerance = 1.0 / 255.0;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        /// <summary>
        /// D-11 strength semantic: <c>AoStrength = 0</c> collapses ANY input AO to white
        /// (no AO), while <c>AoStrength = 1</c> (with <c>AoContrast = 1</c>) is identity —
        /// the AO is unchanged.
        /// </summary>
        [UnityTest]
        public IEnumerator AoStrength_Zero_YieldsWhiteAo()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO strength test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSAoRemap");

            float[] inputs = { 0.0f, 0.25f, 0.6f, 1.0f };
            foreach (float input in inputs)
            {
                float white = RemapAoGreen(compute, kernel, input, strength: 0f, contrast: 1f);
                Assert.AreEqual(1.0, (double)white, ByteTolerance,
                    "AoStrength 0 must yield white AO (1.0) for input AO " + input);
            }

            float identity = RemapAoGreen(compute, kernel, 0.3f, strength: 1f, contrast: 1f);
            Assert.AreEqual(0.3, (double)identity, ByteTolerance,
                "AoStrength 1 (contrast 1) must leave the AO unchanged (identity)");

            yield return null;
        }

        /// <summary>
        /// D-11 contrast semantic: <c>AoContrast = 1</c> leaves the AO byte-identical to the
        /// input (within 1/255), while a contrast != 1 shifts the midpoint as
        /// <c>(ao - 0.5) * contrast + 0.5</c>.
        /// </summary>
        [UnityTest]
        public IEnumerator AoContrast_Identity_LeavesAoUnchanged()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO contrast test (D-15: Metal is the verified target).");
                yield break;
            }

            ComputeShader compute = LoadShader();
            int kernel = compute.FindKernel("CSAoRemap");

            const float input = 0.25f;
            float identity = RemapAoGreen(compute, kernel, input, strength: 1f, contrast: 1f);
            Assert.AreEqual((double)input, (double)identity, ByteTolerance,
                "AoContrast 1 must leave the AO byte-identical to the input");

            const float contrast = 2f;
            float shifted = RemapAoGreen(compute, kernel, input, strength: 1f, contrast: contrast);
            float expected = (input - 0.5f) * contrast + 0.5f;
            Assert.AreEqual((double)expected, (double)shifted, ByteTolerance,
                "AoContrast 2 must shift the midpoint as (ao - 0.5) * contrast + 0.5");

            yield return null;
        }

        /// <summary>
        /// D-13 persistence: writing the three AO tweak settings through one
        /// <see cref="NamerProcessorSettings"/> instance is visible to a NEW instance (the
        /// values are EditorPrefs-backed, not instance-local). The pre-test values are
        /// snapshot and restored in <c>finally</c> so the user's live editor state is
        /// untouched (mirrors <c>AssetGeneratorTests</c> prefs isolation).
        /// </summary>
        [Test]
        public void AoBlurRadius_PersistsViaEditorPrefs()
        {
            const string blurKey = "NamerProcessor.AoBlurRadius";
            const string strengthKey = "NamerProcessor.AoStrength";
            const string contrastKey = "NamerProcessor.AoContrast";

            float blur = EditorPrefs.GetFloat(blurKey, 0f);
            float strength = EditorPrefs.GetFloat(strengthKey, 1f);
            float contrast = EditorPrefs.GetFloat(contrastKey, 1f);
            bool hadBlur = EditorPrefs.HasKey(blurKey);
            bool hadStrength = EditorPrefs.HasKey(strengthKey);
            bool hadContrast = EditorPrefs.HasKey(contrastKey);

            try
            {
                const float kBlur = 6f;
                const float kStrength = 0.25f;
                const float kContrast = 2.5f;

                NamerProcessorSettings first = new NamerProcessorSettings
                {
                    AoBlurRadius = kBlur,
                    AoStrength = kStrength,
                    AoContrast = kContrast,
                };

                NamerProcessorSettings second = new NamerProcessorSettings();
                Assert.AreEqual(kBlur, second.AoBlurRadius, "AoBlurRadius must persist via EditorPrefs");
                Assert.AreEqual(kStrength, second.AoStrength, "AoStrength must persist via EditorPrefs");
                Assert.AreEqual(kContrast, second.AoContrast, "AoContrast must persist via EditorPrefs");
            }
            finally
            {
                if (hadBlur)
                {
                    EditorPrefs.SetFloat(blurKey, blur);
                }
                else
                {
                    EditorPrefs.DeleteKey(blurKey);
                }

                if (hadStrength)
                {
                    EditorPrefs.SetFloat(strengthKey, strength);
                }
                else
                {
                    EditorPrefs.DeleteKey(strengthKey);
                }

                if (hadContrast)
                {
                    EditorPrefs.SetFloat(contrastKey, contrast);
                }
                else
                {
                    EditorPrefs.DeleteKey(contrastKey);
                }
            }
        }

        /// <summary>
        /// D-10 gate discipline: no NEW un-multiply toggle was introduced. The settings
        /// surface exposes exactly the four AO controls (un-multiply, blur, strength,
        /// contrast), and <c>AoUnmultiplyStrength</c> remains the SINGLE un-multiply on/off
        /// (a continuous float, not a separate boolean gate).
        /// </summary>
        [Test]
        public void AoUnmultiplyStrength_RemainsTheOnlyGate()
        {
            PropertyInfo[] properties = typeof(NamerProcessorSettings).GetProperties(
                BindingFlags.Public | BindingFlags.Instance);

            string[] aoNames = new string[properties.Length];
            int count = 0;
            foreach (PropertyInfo property in properties)
            {
                if (property.Name.StartsWith("Ao"))
                {
                    aoNames[count++] = property.Name;
                }
            }

            Assert.AreEqual(4, count, "NamerProcessorSettings must expose exactly four AO controls");
            AssertContains(aoNames, count, "AoBlurRadius");
            AssertContains(aoNames, count, "AoStrength");
            AssertContains(aoNames, count, "AoContrast");
            AssertContains(aoNames, count, "AoUnmultiplyStrength");

            int unmultiplyCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (aoNames[i] != null && aoNames[i].Contains("Unmultiply"))
                {
                    unmultiplyCount++;
                }
            }

            Assert.AreEqual(1, unmultiplyCount,
                "AoUnmultiplyStrength must remain the single un-multiply gate");
        }

        // --------------------------------------------------------------------

        /// <summary>
        /// Dispatches <c>CSAoRemap</c> in direct mode (the bake tweak path) against a 1x1
        /// green-channel AO input and returns the remapped green AO byte normalized to
        /// [0,1]. Direct mode reads the AO from <c>_Dst.g</c> and writes it to <c>_AoOut.g</c>
        /// — the exact same stage the pipeline uses for both extracted and baked AO (D-11).
        /// </summary>
        private static float RemapAoGreen(ComputeShader compute, int kernel, float inputAo, float strength, float contrast)
        {
            RenderTexture dst = null;
            RenderTexture aoOut = null;
            try
            {
                dst = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, 1, 1);
                aoOut = CreateRenderTexture(GraphicsFormat.R8G8B8A8_UNorm, 1, 1);

                UploadPixels(dst, new[] { new Color(0f, inputAo, 0f, 1f) }, 1, 1);

                compute.SetInts("_Size", new[] { 1, 1 });
                compute.SetFloat("_AoDirect", 1f);
                compute.SetFloat("_AoStrength", strength);
                compute.SetFloat("_AoContrast", contrast);
                compute.SetTexture(kernel, "_Dst", dst);
                compute.SetTexture(kernel, "_AoOut", aoOut);
                compute.Dispatch(kernel, 1, 1, 1);

                Color32 pixel = ReadBackColor32(aoOut, 1)[0];
                return pixel.g / 255.0f;
            }
            finally
            {
                Release(dst, aoOut);
            }
        }

        private static ComputeShader LoadShader()
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(AoComputeShaderPath);
            Assert.IsNotNull(shader, "NAMERAO.compute must load at " + AoComputeShaderPath);
            return shader;
        }

        private static void AssertContains(string[] names, int count, string expected)
        {
            for (int i = 0; i < count; i++)
            {
                if (names[i] == expected)
                {
                    return;
                }
            }

            Assert.Fail("Expected AO control '" + expected + "' not found in NamerProcessorSettings");
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

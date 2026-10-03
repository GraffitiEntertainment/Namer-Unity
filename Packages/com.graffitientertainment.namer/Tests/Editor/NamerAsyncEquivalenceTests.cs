using System.Collections;
using System.IO;
using System.Threading.Tasks;
using GraffitiEntertainment.Namer.Core;
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
    /// 05.3 threading equivalence coverage: every awaitable mirror
    /// (<c>NamerAOBaker.BakeAsync</c>, <c>NamerDecompPipeline.GenerateResidualAsync</c>,
    /// <c>NamerComputePipeline.RequestReadbackAsync</c>, and the end-to-end
    /// <see cref="NamerProcessor.ProcessAsync"/>) must produce element-wise /
    /// byte-identical results to its blocking twin on identical inputs — the async twins
    /// exist to keep the editor responsive, never to change output. Async tasks are
    /// awaited with the wait-condition pattern ([UnityTest] +
    /// <c>while (!task.IsCompleted) yield return null</c>) so the editor loop pumps the
    /// readbacks and the worker-thread continuations resume on the main thread. Follows
    /// the fixture/prefs conventions of <c>NamerAOBakeTests</c>,
    /// <c>ResidualPipelineTests</c>, and <c>NamerReprocessTests</c>.
    /// </summary>
    public class NamerAsyncEquivalenceTests
    {
        private const string TempFolder = "Assets/NAMER_Async_Tests_Temp";
        private const int BakeResolution = 64;
        private const int BaseMapSize = 32;

        private const string DestinationKey = "NamerProcessor.Destination";
        private const string PrefixKey = "NamerProcessor.Prefix";
        private const string SuffixKey = "NamerProcessor.Suffix";
        private const string OverwriteKey = "NamerProcessor.OverwriteGenerated";
        private const string DecompositionEnabledKey = "NamerProcessor.DecompositionEnabled";
        private const string ErrorThresholdKey = "NamerProcessor.ErrorThreshold";
        private const string ResidualResolutionKey = "NamerProcessor.ResidualResolution";
        private const string AoStageEnabledKey = "NamerProcessor.AoStageEnabled";
        private const string WriteResidualKey = "NamerProcessor.WriteResidual";

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        /// <summary>
        /// The worker-thread bake must be element-wise identical to the blocking bake:
        /// same gather, same ray math, same slab batching (see
        /// <c>NamerAOBakeTests.Bake_IsDeterministic</c> for the sync-vs-sync baseline).
        /// </summary>
        [UnityTest]
        public IEnumerator BakeAsync_ProducesElementWiseIdenticalAoToBake()
        {
            Mesh floor = CreateFlatQuad();
            Mesh roof = CreateRoofQuad();
            try
            {
                NamerAOBakeResult sync = NamerAOBaker.Bake(
                    floor, roof, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                Task<NamerAOBakeResult> asyncTask = NamerAOBaker.BakeAsync(
                    floor, roof, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                while (!asyncTask.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(asyncTask.IsFaulted, "BakeAsync must not fault: " + asyncTask.Exception);
                NamerAOBakeResult asyncResult = asyncTask.Result;
                try
                {
                    Assert.AreEqual(sync.Width, asyncResult.Width);
                    Assert.AreEqual(sync.Height, asyncResult.Height);
                    Assert.AreEqual(sync.Ao.Length, asyncResult.Ao.Length);
                    for (int i = 0; i < sync.Ao.Length; i++)
                    {
                        Assert.AreEqual(sync.Ao[i], asyncResult.Ao[i],
                            "AO texel " + i + " must be identical between Bake and BakeAsync (identical ray math on the worker thread)");
                    }
                }
                finally
                {
                    sync.Ao.Dispose();
                    asyncResult.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(floor, roof);
            }

            yield return null;
        }

        /// <summary>
        /// The awaitable residual pipeline must return identical gate stats and an
        /// element-wise identical residual to the blocking pipeline (the only difference
        /// is when the stats readbacks wait).
        /// </summary>
        [UnityTest]
        public IEnumerator GenerateResidualAsync_MatchesSyncGenerateResidual()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU equivalence test (D-15: Metal is the verified target).");
                yield break;
            }

            const int size = 64;
            NamerSplitResult split = CreateSplitQuad(0f, 1f);
            Color32[] colors = ConstantColors(split.VertexCount, 128);
            RenderTexture baseRt = CreateGradientBase(size, size);
            try
            {
                using (NamerDecompPipeline pipeline = new NamerDecompPipeline())
                {
                    NamerDecompOutput sync = pipeline.GenerateResidual(
                        split, colors, baseRt, size, size, 0.02f, 0, null, NamerResidualMode.AlwaysKeep);
                    Task<NamerDecompOutput> asyncTask = pipeline.GenerateResidualAsync(
                        split, colors, baseRt, size, size, 0.02f, 0, null, NamerResidualMode.AlwaysKeep);
                    while (!asyncTask.IsCompleted)
                    {
                        yield return null;
                    }

                    Assert.IsFalse(asyncTask.IsFaulted, "GenerateResidualAsync must not fault: " + asyncTask.Exception);
                    NamerDecompOutput asyncOutput = asyncTask.Result;
                    try
                    {
                        Assert.AreEqual(sync.Stats.ResidualRequired, asyncOutput.Stats.ResidualRequired, "ResidualRequired must match");
                        Assert.AreEqual(sync.Stats.ChosenResolution, asyncOutput.Stats.ChosenResolution, "ChosenResolution must match");
                        Assert.AreEqual(sync.Stats.AchievedCoverage, asyncOutput.Stats.AchievedCoverage, "AchievedCoverage must match");
                        Assert.AreEqual(sync.Stats.AvgError, asyncOutput.Stats.AvgError, "AvgError must match");
                        Assert.AreEqual(sync.Stats.MaxError, asyncOutput.Stats.MaxError, "MaxError must match");
                        Assert.AreEqual(sync.Stats.FitOnlyMaxError, asyncOutput.Stats.FitOnlyMaxError, "FitOnlyMaxError must match");
                        Assert.AreEqual(sync.Stats.CannotDecompose, asyncOutput.Stats.CannotDecompose, "CannotDecompose must match");

                        Assert.IsNotNull(sync.Residual, "AlwaysKeep on a gradient-vs-constant mismatch must produce a residual");
                        Assert.IsNotNull(asyncOutput.Residual, "the async twin must produce a residual for the same inputs");
                        Color[] syncResidual = ReadBackFloat(sync.Residual);
                        Color[] asyncResidual = ReadBackFloat(asyncOutput.Residual);
                        Assert.AreEqual(syncResidual.Length, asyncResidual.Length);
                        for (int i = 0; i < syncResidual.Length; i++)
                        {
                            Assert.AreEqual(syncResidual[i], asyncResidual[i],
                                "residual texel " + i + " must be identical between GenerateResidual and GenerateResidualAsync");
                        }
                    }
                    finally
                    {
                        sync.Dispose();
                        asyncOutput.Dispose();
                    }
                }
            }
            finally
            {
                baseRt.Release();
                Object.DestroyImmediate(baseRt);
            }

            yield return null;
        }

        /// <summary>
        /// The awaitable readback must return byte-identical data to the blocking
        /// readback of the same target. The target is written by a compute dispatch
        /// (<see cref="NamerStylizePipeline.Run"/>'s always-on palette transfer —
        /// the dispatch shape behind the editor window's stylizedBase readback).
        /// Platform contract (Unity 6, edit mode — confirmed by probe r17 2026-10-02):
        /// a fulfilled request is disposed on the next editor update and a disposed
        /// request reports <c>hasError</c>, so the async bytes are harvested INSIDE
        /// the await continuation (<see cref="ReadBackBytesAsync"/>) — the same
        /// consumption window every production call site uses
        /// (<c>ReadBackStatsAsync</c>, <c>AssetGenerator.ReadBackPairAsync</c>, the
        /// window fit flow). Inspecting the request a coroutine step later reads the
        /// disposed state, not a readback failure.
        /// </summary>
        [UnityTest]
        public IEnumerator RequestReadbackAsync_MatchesSyncReadbackBytes()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU equivalence test (D-15: Metal is the verified target).");
                yield break;
            }

            const int size = 64;
            RenderTexture src = CreateGradientBase(size, size);
            try
            {
                RenderTexture dst = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, size, size);
                try
                {
                    NamerStyleStylization stylization = new NamerStyleStylization { StyleStrength = 0.5f };
                    using (NamerStylizePipeline stylize = new NamerStylizePipeline())
                    {
                        stylize.Run(src, dst, null, null, null, stylization, size, size);
                    }

                    Task<byte[]> asyncTask = ReadBackBytesAsync(dst, TextureFormat.RGBA32);
                    while (!asyncTask.IsCompleted)
                    {
                        yield return null;
                    }

                    Assert.IsFalse(asyncTask.IsFaulted, "RequestReadbackAsync must not fault: " + asyncTask.Exception);
                    byte[] asyncBytes = asyncTask.Result;

                    AsyncGPUReadbackRequest syncRequest = NamerComputePipeline.RequestReadback(dst, 0, TextureFormat.RGBA32);
                    syncRequest.forcePlayerLoopUpdate = true;
                    syncRequest.WaitForCompletion();
                    Assert.IsFalse(syncRequest.hasError, "the blocking reference readback must not error");
                    byte[] syncBytes = CopyToManaged(syncRequest.GetData<byte>());

                    CollectionAssert.AreEqual(syncBytes, asyncBytes,
                        "RequestReadbackAsync must return byte-identical data to the blocking RequestReadback");
                }
                finally
                {
                    dst.Release();
                    Object.DestroyImmediate(dst);
                }
            }
            finally
            {
                src.Release();
                Object.DestroyImmediate(src);
            }

            yield return null;
        }

        /// <summary>
        /// Harvests a pump-serviced readback's bytes inside the await continuation —
        /// the only window in which a fulfilled request is still alive (see the
        /// platform contract on <see cref="RequestReadbackAsync_MatchesSyncReadbackBytes"/>).
        /// </summary>
        private static async Task<byte[]> ReadBackBytesAsync(RenderTexture rt, TextureFormat format)
        {
            AsyncGPUReadbackRequest request = await NamerComputePipeline.RequestReadbackAsync(rt, 0, format);
            Assert.IsFalse(request.hasError, "the awaitable readback must not error inside its continuation window");
            return CopyToManaged(request.GetData<byte>());
        }

        /// <summary>
        /// End-to-end equivalence through the full seam chain (projection RunAsync +
        /// ReadBackBaseAsync + FitOnWorkerAsync + GenerateResidualAsync + AO bake
        /// BakeAndUploadAsync + GenerateAsync readback pair): reprocessing the same
        /// object through ProcessAsync must overwrite the sync run's outputs with
        /// byte-identical base PNG, surface PNG, and residual EXR files.
        /// </summary>
        [UnityTest]
        public IEnumerator ProcessAsync_ProducesByteIdenticalTexturesToProcess()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU equivalence test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/AsyncSourceQuad.asset");
                Material source = CreateSourceMaterial(TempFolder, "AsyncSourceMat");

                gameObject = new GameObject("AsyncEquivalenceTarget");
                MeshFilter filter = gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = sourceMesh;
                MeshRenderer renderer = gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = source;

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "_Namer",
                    OverwriteGenerated = true,
                    DecompositionEnabled = true,
                    ErrorThreshold = 0.06f,
                    ResidualResolution = 0,
                    AoStageEnabled = true,
                    WriteResidual = true,
                };

                NamerProcessResult sync = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(sync.Error, "sync Process should succeed: " + sync.Error);
                Assert.AreEqual(1, sync.GeneratedAssets.Count, "sync run must generate one material set");
                Assert.IsFalse(string.IsNullOrEmpty(sync.GeneratedAssets[0].MeshPath),
                    "decomposition ON writes the split mesh");
                Assert.IsFalse(string.IsNullOrEmpty(sync.GeneratedAssets[0].ResidualTexturePath),
                    "WriteResidual ON with a bilinear-product base (not barycentrically representable) must write the residual EXR");

                // Capture the sync outputs before the async run overwrites the same paths.
                byte[] syncBase = File.ReadAllBytes(sync.GeneratedAssets[0].BaseTexturePath);
                byte[] syncSurface = File.ReadAllBytes(sync.GeneratedAssets[0].SurfaceTexturePath);
                byte[] syncResidual = File.ReadAllBytes(sync.GeneratedAssets[0].ResidualTexturePath);

                Task<NamerProcessResult> asyncTask = NamerProcessor.ProcessAsync(gameObject, settings);
                while (!asyncTask.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(asyncTask.IsFaulted, "ProcessAsync must not fault: " + asyncTask.Exception);
                NamerProcessResult asyncResult = asyncTask.Result;
                Assert.IsNull(asyncResult.Error, "ProcessAsync should succeed: " + asyncResult.Error);
                Assert.AreEqual(1, asyncResult.GeneratedAssets.Count, "async run must generate one material set");

                byte[] asyncBase = File.ReadAllBytes(asyncResult.GeneratedAssets[0].BaseTexturePath);
                byte[] asyncSurface = File.ReadAllBytes(asyncResult.GeneratedAssets[0].SurfaceTexturePath);
                byte[] asyncResidual = File.ReadAllBytes(asyncResult.GeneratedAssets[0].ResidualTexturePath);
                CollectionAssert.AreEqual(syncBase, asyncBase,
                    "the base PNG must be byte-identical between Process and ProcessAsync");
                CollectionAssert.AreEqual(syncSurface, asyncSurface,
                    "the surface PNG must be byte-identical between Process and ProcessAsync");
                CollectionAssert.AreEqual(syncResidual, asyncResidual,
                    "the residual EXR must be byte-identical between Process and ProcessAsync");
            }
            finally
            {
                if (gameObject != null)
                {
                    Object.DestroyImmediate(gameObject);
                }

                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        /// <summary>
        /// A user cancel observed before the first material must stop gracefully: no
        /// error, no generated assets, and the recorded "Cancelled after" warning.
        /// </summary>
        [UnityTest]
        public IEnumerator ProcessAsync_UserCancelBeforeFirstMaterial_StopsGracefully()
        {
            EnsureTempFolder();
            PrefsSnapshot prefs = CapturePrefs();
            GameObject gameObject = null;
            try
            {
                Mesh sourceMesh = CreateQuadMeshAsset(TempFolder + "/AsyncCancelQuad.asset");
                Material source = CreateSourceMaterial(TempFolder, "AsyncCancelSourceMat");

                gameObject = new GameObject("AsyncCancelTarget");
                gameObject.AddComponent<MeshFilter>().sharedMesh = sourceMesh;
                gameObject.AddComponent<MeshRenderer>().sharedMaterial = source;

                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    Prefix = "",
                    Suffix = "_Namer",
                    OverwriteGenerated = true,
                    DecompositionEnabled = true,
                    AoStageEnabled = true,
                };

                Task<NamerProcessResult> task = NamerProcessor.ProcessAsync(gameObject, settings, shouldCancel: () => true);
                while (!task.IsCompleted)
                {
                    yield return null;
                }

                Assert.IsFalse(task.IsFaulted, "a between-materials cancel must not fault: " + task.Exception);
                NamerProcessResult result = task.Result;
                Assert.IsNull(result.Error, "a user cancel before the first material is a graceful stop, not an error");
                Assert.AreEqual(0, result.GeneratedAssets.Count,
                    "no material may be generated when cancel fires before the first");
                Assert.IsTrue(result.Warnings.Contains("Cancelled after 0 material(s); remaining materials were not processed."),
                    "the cancel warning must be recorded: " + string.Join("; ", result.Warnings));
            }
            finally
            {
                if (gameObject != null)
                {
                    Object.DestroyImmediate(gameObject);
                }

                RestorePrefs(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
            }

            yield return null;
        }

        // --------------------------------------------------------------------

        private sealed class PrefsSnapshot
        {
            public string Destination;
            public string Prefix;
            public string Suffix;
            public bool OverwriteGenerated;
            public bool DecompositionEnabled;
            public float ErrorThreshold;
            public int ResidualResolution;
            public bool AoStageEnabled;
            public bool WriteResidual;
            public bool HadDestination;
            public bool HadPrefix;
            public bool HadSuffix;
            public bool HadOverwriteGenerated;
            public bool HadDecompositionEnabled;
            public bool HadErrorThreshold;
            public bool HadResidualResolution;
            public bool HadAoStageEnabled;
            public bool HadWriteResidual;
        }

        private static PrefsSnapshot CapturePrefs()
        {
            return new PrefsSnapshot
            {
                Destination = EditorPrefs.GetString(DestinationKey),
                Prefix = EditorPrefs.GetString(PrefixKey),
                Suffix = EditorPrefs.GetString(SuffixKey),
                OverwriteGenerated = EditorPrefs.GetBool(OverwriteKey),
                DecompositionEnabled = EditorPrefs.GetBool(DecompositionEnabledKey),
                ErrorThreshold = EditorPrefs.GetFloat(ErrorThresholdKey),
                ResidualResolution = EditorPrefs.GetInt(ResidualResolutionKey),
                AoStageEnabled = EditorPrefs.GetBool(AoStageEnabledKey),
                WriteResidual = EditorPrefs.GetBool(WriteResidualKey),
                HadDestination = EditorPrefs.HasKey(DestinationKey),
                HadPrefix = EditorPrefs.HasKey(PrefixKey),
                HadSuffix = EditorPrefs.HasKey(SuffixKey),
                HadOverwriteGenerated = EditorPrefs.HasKey(OverwriteKey),
                HadDecompositionEnabled = EditorPrefs.HasKey(DecompositionEnabledKey),
                HadErrorThreshold = EditorPrefs.HasKey(ErrorThresholdKey),
                HadResidualResolution = EditorPrefs.HasKey(ResidualResolutionKey),
                HadAoStageEnabled = EditorPrefs.HasKey(AoStageEnabledKey),
                HadWriteResidual = EditorPrefs.HasKey(WriteResidualKey),
            };
        }

        private static void RestorePrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadDestination)
            {
                EditorPrefs.SetString(DestinationKey, snapshot.Destination);
            }
            else
            {
                EditorPrefs.DeleteKey(DestinationKey);
            }

            if (snapshot.HadPrefix)
            {
                EditorPrefs.SetString(PrefixKey, snapshot.Prefix);
            }
            else
            {
                EditorPrefs.DeleteKey(PrefixKey);
            }

            if (snapshot.HadSuffix)
            {
                EditorPrefs.SetString(SuffixKey, snapshot.Suffix);
            }
            else
            {
                EditorPrefs.DeleteKey(SuffixKey);
            }

            if (snapshot.HadOverwriteGenerated)
            {
                EditorPrefs.SetBool(OverwriteKey, snapshot.OverwriteGenerated);
            }
            else
            {
                EditorPrefs.DeleteKey(OverwriteKey);
            }

            if (snapshot.HadDecompositionEnabled)
            {
                EditorPrefs.SetBool(DecompositionEnabledKey, snapshot.DecompositionEnabled);
            }
            else
            {
                EditorPrefs.DeleteKey(DecompositionEnabledKey);
            }

            if (snapshot.HadErrorThreshold)
            {
                EditorPrefs.SetFloat(ErrorThresholdKey, snapshot.ErrorThreshold);
            }
            else
            {
                EditorPrefs.DeleteKey(ErrorThresholdKey);
            }

            if (snapshot.HadResidualResolution)
            {
                EditorPrefs.SetInt(ResidualResolutionKey, snapshot.ResidualResolution);
            }
            else
            {
                EditorPrefs.DeleteKey(ResidualResolutionKey);
            }

            if (snapshot.HadAoStageEnabled)
            {
                EditorPrefs.SetBool(AoStageEnabledKey, snapshot.AoStageEnabled);
            }
            else
            {
                EditorPrefs.DeleteKey(AoStageEnabledKey);
            }

            if (snapshot.HadWriteResidual)
            {
                EditorPrefs.SetBool(WriteResidualKey, snapshot.WriteResidual);
            }
            else
            {
                EditorPrefs.DeleteKey(WriteResidualKey);
            }
        }

        // -- AO bake fixtures (mirror NamerAOBakeTests) ------------------------

        private static Mesh CreateFlatQuad()
        {
            Mesh mesh = new Mesh { name = "NamerAsyncQuad" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateRoofQuad()
        {
            Mesh mesh = new Mesh { name = "NamerAsyncRoof" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0.05f, 0f),
                new Vector3(1f, 0.05f, 0f),
                new Vector3(1f, 0.05f, 1f),
                new Vector3(0f, 0.05f, 1f),
            };
            mesh.normals = new[]
            {
                Vector3.down, Vector3.down, Vector3.down, Vector3.down,
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        // -- residual fixtures (mirror ResidualPipelineTests) ------------------

        private static NamerSplitResult CreateSplitQuad(float uvMin, float uvMax)
        {
            return new NamerSplitResult
            {
                Positions = new[]
                {
                    new Vector3(0f, 0f, 0f),
                    new Vector3(1f, 0f, 0f),
                    new Vector3(1f, 0f, 1f),
                    new Vector3(0f, 0f, 1f),
                },
                Normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up },
                Tangents = new Vector4[4],
                Uvs = new[]
                {
                    new Vector2(uvMin, uvMin),
                    new Vector2(uvMax, uvMin),
                    new Vector2(uvMax, uvMax),
                    new Vector2(uvMin, uvMax),
                },
                SubMeshTriangles = new[] { new[] { 0, 2, 1, 0, 3, 2 } },
            };
        }

        private static Color32[] ConstantColors(int count, byte value)
        {
            Color32[] colors = new Color32[count];
            for (int i = 0; i < count; i++)
            {
                colors[i] = new Color32(value, value, value, 255);
            }

            return colors;
        }

        private static RenderTexture CreateGradientBase(int w, int h)
        {
            return UploadBase(w, h, (x, y) =>
            {
                float v = x / (float)(w - 1);
                return new Color(v, v, v, 1f);
            });
        }

        private static RenderTexture UploadBase(int w, int h, System.Func<int, int, Color> pixel)
        {
            Texture2D upload = new Texture2D(w, h, TextureFormat.RGBAHalf, false, true)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            Color[] pixels = new Color[w * h];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[y * w + x] = pixel(x, y);
                }
            }

            upload.SetPixels(pixels);
            upload.Apply(false, false);

            RenderTexture rt = CreateRenderTexture(GraphicsFormat.R16G16B16A16_SFloat, w, h);
            Graphics.Blit(upload, rt);
            Object.DestroyImmediate(upload);
            return rt;
        }

        private static RenderTexture CreateRenderTexture(GraphicsFormat format, int w, int h)
        {
            RenderTexture rt = new RenderTexture(new RenderTextureDescriptor(w, h, format, 0)
            {
                enableRandomWrite = true,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false,
            });
            rt.Create();
            return rt;
        }

        private static Color[] ReadBackFloat(RenderTexture rt)
        {
            // RGBAFloat readback of the R16G16B16A16_SFloat residual RT is lossless
            // (half widens to float exactly), matching the residual's 16-bit-half
            // precision the saved EXR carries.
            AsyncGPUReadbackRequest req = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBAFloat);
            req.WaitForCompletion();
            Assert.IsFalse(req.hasError, "AsyncGPUReadback must not error");
            NativeArray<Color> data = req.GetData<Color>();
            try
            {
                Color[] result = new Color[data.Length];
                for (int i = 0; i < data.Length; i++)
                {
                    result[i] = data[i];
                }

                return result;
            }
            finally
            {
                data.Dispose();
            }
        }

        private static byte[] CopyToManaged(NativeArray<byte> data)
        {
            try
            {
                byte[] managed = new byte[data.Length];
                data.CopyTo(managed);
                return managed;
            }
            finally
            {
                data.Dispose();
            }
        }

        // -- processor fixtures (mirror NamerReprocessTests) -------------------

        private static Mesh CreateQuadMeshAsset(string path)
        {
            Mesh mesh = new Mesh { name = "AsyncSourceQuad" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2 };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static Material CreateSourceMaterial(string folder, string name)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            Texture2D baseMap = CreateImportedGradientBaseMap(folder + "/" + name + "_BaseMap.png");

            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.5f);
            material.SetFloat("_SmoothnessTextureChannel", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static Texture2D CreateImportedGradientBaseMap(string path)
        {
            // Bilinear product (u * v) plus an XOR grain in blue: NOT barycentrically
            // linear, so the 4-vertex fit cannot cover it and the residual ladder has
            // real error to absorb at every rung (a plain luminance ramp is exactly
            // representable by vertex colors and may pass the gate residual-free).
            Texture2D source = new Texture2D(BaseMapSize, BaseMapSize, TextureFormat.RGBA32, false, true);
            Color[] pixels = new Color[BaseMapSize * BaseMapSize];
            for (int y = 0; y < BaseMapSize; y++)
            {
                for (int x = 0; x < BaseMapSize; x++)
                {
                    float u = x / (float)(BaseMapSize - 1);
                    float v = y / (float)(BaseMapSize - 1);
                    float product = u * v;
                    pixels[y * BaseMapSize + x] = new Color(product, 1f - product, ((x ^ y) & 0xFF) / 255f, 1f);
                }
            }

            source.SetPixels(pixels);
            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void EnsureTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_Async_Tests_Temp");
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


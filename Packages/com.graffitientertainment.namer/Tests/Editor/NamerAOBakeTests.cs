using System.Collections;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 03.1 geometry-bake tests (plan 02). Proves the CPU Burst bake is deterministic
    /// (fixed direction table + fixed float math), that a flat plane bakes to AO ~= 1.0 (the
    /// 0.01 cage offset is load-bearing, Pitfall 4), that a null/invalid occluder falls back
    /// to the selected mesh without failing (D-06), that the BVH raycast is correct (t_min >
    /// eps ignores the origin surface), and — critically — that a cached bake is routed
    /// through <see cref="NamerComputePipeline.Process"/> into the packed B channel
    /// (superseding image-space extraction, D-07/D-08). The CPU baker tests are pure [Test]
    /// (no GPU); only the pipeline-routing test is [UnityTest] with the compute capability
    /// gate (D-15).
    /// </summary>
    public class NamerAOBakeTests
    {
        private const int BakeResolution = 64;

        private static bool ComputeAvailable =>
            SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;

        [Test]
        public void Bake_IsDeterministic()
        {
            Mesh floor = CreateFlatQuad();
            Mesh roof = CreateRoofQuad();
            try
            {
                NamerAOBakeResult first = NamerAOBaker.Bake(
                    floor, roof, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                NamerAOBakeResult second = NamerAOBaker.Bake(
                    floor, roof, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                try
                {
                    Assert.AreEqual(first.Width, second.Width);
                    Assert.AreEqual(first.Height, second.Height);
                    Assert.AreEqual(first.Ao.Length, second.Ao.Length);
                    for (int i = 0; i < first.Ao.Length; i++)
                    {
                        Assert.AreEqual(first.Ao[i], second.Ao[i],
                            "AO buffer must be element-wise identical across two bakes (deterministic direction table + float math)");
                    }
                }
                finally
                {
                    first.Ao.Dispose();
                    second.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(floor, roof);
            }
        }

        [Test]
        public void Bake_FlatPlane_ProducesAoNearOne()
        {
            Mesh quad = CreateFlatQuad();
            try
            {
                NamerAOBakeResult result = NamerAOBaker.Bake(
                    quad, quad, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                try
                {
                    Assert.AreEqual(BakeResolution * BakeResolution, result.Ao.Length);
                    for (int i = 0; i < result.Ao.Length; i++)
                    {
                        Assert.GreaterOrEqual(result.Ao[i], 0.999f,
                            "flat plane must bake to AO ~= 1.0 (the 0.01 cage offset prevents self-occlusion, Pitfall 4)");
                    }
                }
                finally
                {
                    result.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void Bake_NullOccluder_FallsBackToSelectedMesh()
        {
            Mesh quad = CreateFlatQuad();
            try
            {
                NamerAOBakeResult withNull = NamerAOBaker.Bake(
                    quad, null, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                NamerAOBakeResult withSelf = NamerAOBaker.Bake(
                    quad, quad, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                try
                {
                    Assert.AreEqual(withSelf.Ao.Length, withNull.Ao.Length);
                    for (int i = 0; i < withNull.Ao.Length; i++)
                    {
                        Assert.AreEqual(withSelf.Ao[i], withNull.Ao[i],
                            "a null occluder must fall back to the selected mesh (D-06) and produce the same AO");
                    }
                }
                finally
                {
                    withNull.Ao.Dispose();
                    withSelf.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void Bvh_RayCast_HitsTriangle()
        {
            NativeArray<float3> verts = new NativeArray<float3>(3, Allocator.Temp);
            NativeArray<int3> tris = new NativeArray<int3>(1, Allocator.Temp);
            try
            {
                verts[0] = new float3(0f, 0f, 0f);
                verts[1] = new float3(1f, 0f, 0f);
                verts[2] = new float3(0f, 0f, 1f);
                tris[0] = new int3(0, 1, 2);

                NamerAOBvh bvh = NamerAOBvh.Build(verts, tris);
                try
                {
                    Assert.IsTrue(
                        bvh.RayCast(new float3(0.3f, 1f, 0.3f), new float3(0f, -1f, 0f), 10f, out float t),
                        "a ray from above must hit the triangle");
                    Assert.Greater(t, 0f, "hit distance must be positive");

                    Assert.IsFalse(
                        bvh.RayCast(new float3(0.3f, 0f, 0.3f), new float3(0f, -1f, 0f), 10f, out _),
                        "a ray originating on the surface must be ignored (t_min > eps)");
                }
                finally
                {
                    bvh.Dispose();
                }
            }
            finally
            {
                verts.Dispose();
                tris.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator Process_WithCachedBake_RoutesBakedAoIntoPackedSurface()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU bake-routing test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                Mesh quad = CreateFlatQuad();
                try
                {
                    Color[] pixels = new Color[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
                            // Bright half (linear ~0.9) vs dark half (linear ~0.15), no sRGB.
                            float value = x < w / 2 ? 0.9f : 0.15f;
                            pixels[y * w + x] = new Color(value, value, value, 1.0f);
                        }
                    }

                    baseMap.SetPixels(pixels);
                    baseMap.Apply(false, false);

                    NamerMaterialInspection inspection = new NamerMaterialInspection
                    {
                        BaseMap = baseMap,
                        BaseMapIsSrgb = false,
                        NormalMap = null,
                        OcclusionMap = null,
                        MetallicGlossMap = null,
                        Metallic = 0.0f,
                        Smoothness = 0.0f,
                        Roughness = 1.0f,
                        Emissive = 0.0f,
                        AoUnmultiplyStrength = 1.0f,
                        SmoothnessTextureChannel = 0,
                        BakeSourceMesh = quad,
                        OccluderMesh = null,
                    };

                    // Prime the cache through the pipeline's OWN forwarder so Process reads
                    // the same instance, then let the deferred bake complete.
                    pipeline.RequestBake(inspection, w, h, onComplete: null);
                    for (int i = 0; i < 10 && !pipeline.HasCachedBake(inspection, w, h); i++)
                    {
                        yield return null;
                    }

                    Assert.IsTrue(pipeline.HasCachedBake(inspection, w, h),
                        "the geometry bake must be cached after RequestBake completes");

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, w * h);

                        // The flat-plane bake produces AO ~= 1.0 everywhere, so a dark-region
                        // packed B byte must be ~255. Image-space extraction CANNOT produce this
                        // (it yields luma/average < 1 on the dark half), so this discriminates
                        // bake-routed from extraction-fallback.
                        int darkIndex = 8 * w + (w - 8);
                        Assert.GreaterOrEqual((int)surface[darkIndex].b, 250,
                            "dark-region packed AO byte must be ~255 — only a routed flat-plane bake (AO=1.0) produces this");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount, "pool must return to baseline after bake routing");
                }
                finally
                {
                    Destroy(baseMap, quad);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            yield return null;
        }

        private static Mesh CreateFlatQuad()
        {
            Mesh mesh = new Mesh { name = "NamerAOBakeTestQuad" };
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
            Mesh mesh = new Mesh { name = "NamerAOBakeTestRoof" };
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

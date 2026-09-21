using System.Collections;
using System.IO;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
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
    /// to the selected mesh without failing (D-06), that the BVH raycast is correct (t_min &gt;
    /// eps ignores the origin surface), and — critically — that the bake is routed through
    /// <see cref="NamerComputePipeline.Process"/> into the packed B channel: on demand when
    /// the AO stage is on with no authored map (the 2026-09-21 contract's synthetic source)
    /// and from the cache once primed. The contract follow-up E2E additionally proves
    /// <see cref="NamerProcessor.Process"/> PRIMES the bake mesh when the AO stage is on
    /// with decomposition OFF (self-occluding open-box fixture — a flat quad's self-bake is
    /// AO ~= 1.0 and cannot distinguish the bake from the white fill). The CPU baker tests
    /// are pure [Test] (no GPU); only the pipeline/E2E routing tests are [UnityTest] with
    /// the compute capability gate (D-15).
    /// </summary>
    public class NamerAOBakeTests
    {
        private const int BakeResolution = 64;
        private const string TempFolder = "Assets/NAMER_AO_Tests_Temp";

        // EditorPrefs keys the AO-priming E2E writes through NamerProcessorSettings
        // (its setters persist; the test snapshots and restores these).
        private const string DestinationKey = "NamerProcessor.Destination";
        private const string DecompositionEnabledKey = "NamerProcessor.DecompositionEnabled";
        private const string AoStageEnabledKey = "NamerProcessor.AoStageEnabled";
        private const string AoStrengthKey = "NamerProcessor.AoStrength";
        private const string AoContrastKey = "NamerProcessor.AoContrast";
        private const string AoBlurRadiusKey = "NamerProcessor.AoBlurRadius";
        private const string MetallicContributionEnabledKey = "NamerProcessor.MetallicContributionEnabled";
        private const string EmissiveContributionEnabledKey = "NamerProcessor.EmissiveContributionEnabled";

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

        [Test]
        public void Bvh_RayCast_HitsTriangleOutsideFirstLeaf()
        {
            // 4 quads = 8 triangles — beyond kLeafSize (4), forcing a median split.
            const int quadCount = 4;
            NativeArray<float3> verts = new NativeArray<float3>(quadCount * 4, Allocator.Temp);
            NativeArray<int3> tris = new NativeArray<int3>(quadCount * 2, Allocator.Temp);
            try
            {
                // Four quads side by side along X (y = 0, z in [0,1]). The centroid extent is
                // largest on X, so the median split places the lowest-X quads in the FIRST-built
                // (leftmost) leaf and the highest-X quad in a later leaf. A ray at the highest-X
                // quad is therefore unreachable when traversal starts at node 0 (the pre-fix
                // bug) and only hits once traversal starts at the post-order root.
                for (int q = 0; q < quadCount; q++)
                {
                    float x0 = q;
                    verts[q * 4 + 0] = new float3(x0, 0f, 0f);
                    verts[q * 4 + 1] = new float3(x0 + 1f, 0f, 0f);
                    verts[q * 4 + 2] = new float3(x0 + 1f, 0f, 1f);
                    verts[q * 4 + 3] = new float3(x0, 0f, 1f);
                    tris[q * 2 + 0] = new int3(q * 4 + 0, q * 4 + 2, q * 4 + 1);
                    tris[q * 2 + 1] = new int3(q * 4 + 0, q * 4 + 3, q * 4 + 2);
                }

                NamerAOBvh bvh = NamerAOBvh.Build(verts, tris);
                try
                {
                    float3 origin = new float3((quadCount - 1) + 0.5f, 1f, 0.5f);
                    Assert.IsTrue(
                        bvh.RayCast(origin, new float3(0f, -1f, 0f), 10f, out float t),
                        "a ray at the highest-X quad must hit — traversal must reach the non-first leaf");
                    Assert.Greater(t, 0f, "hit distance must be positive");
                    Assert.Less(t, 1.5f, "hit distance must match the ray height above the y=0 quads");
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

        [Test]
        public void Bake_RoofAboveSubdividedFloor_Darkens()
        {
            Mesh floor = CreateFlatQuad();
            Mesh roof = CreateSubdividedRoof(4);
            try
            {
                NamerAOBakeResult result = NamerAOBaker.Bake(
                    floor, roof, BakeResolution, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                try
                {
                    int center = (BakeResolution / 2) * BakeResolution + (BakeResolution / 2);
                    Assert.GreaterOrEqual(result.Ao[center], 0f,
                        "the central texel is UV-covered so its AO must not be the -1 empty sentinel");
                    Assert.Less(result.Ao[center], 0.9f,
                        "the central texel must be darkened by the >4-triangle roof occluder (traversal must reach every leaf)");
                }
                finally
                {
                    result.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(floor, roof);
            }
        }

        [Test]
        public void Bake_SmallUVTriangle_IsCoveredNotDegenerate()
        {
            // Regression pin for debug session residual-missing-triangles (2026-09-19).
            // TryReconstruct's degeneracy gate compared denom = (2*Area_UV)^2 against a
            // 1e-8 epsilon — quadratic in UV edge length, so at the 512 bake cap any
            // triangle under ~13 texel^2 was discarded as "degenerate" and its texels
            // fell to the -1 empty sentinel. This fixture is a 4-texel^2 right triangle
            // (4x2 texel legs): below the pre-fix gate, far above true zero-area. Its
            // texel centers must reconstruct (AO in [0,1], not the sentinel) while a
            // far-away texel stays empty.
            const int res = 512;
            Mesh tri = CreateSmallUVTriangle();
            try
            {
                NamerAOBakeResult result = NamerAOBaker.Bake(
                    tri, tri, res, NamerAOBaker.kCageOffset, NamerAOBaker.kMaxDistanceFactor, NamerAOBaker.kRayCount);
                try
                {
                    int inside = 257 * res + 257;
                    Assert.GreaterOrEqual(result.Ao[inside], 0.999f,
                        "a texel center inside a 4-texel^2 triangle must reconstruct to the flat-plane AO (~1.0), not the -1 empty sentinel (pre-fix: discarded as degenerate)");
                    Assert.LessOrEqual(result.Ao[inside], 1f, "AO is a ratio and must stay within [0,1]");

                    int farAway = 10 * res + 10;
                    Assert.AreEqual(-1f, result.Ao[farAway],
                        "a texel outside every triangle must stay the -1 empty sentinel (zero-coverage semantics unchanged)");
                }
                finally
                {
                    result.Ao.Dispose();
                }
            }
            finally
            {
                Destroy(tri);
            }
        }

        [Test]
        public void RequestBake_Cancelled_DoesNotCachePartialBake()
        {
            Mesh floor = CreateFlatQuad();
            Mesh roof = CreateSubdividedRoof(4);
            NamerAOPipeline pipeline = new NamerAOPipeline();
            try
            {
                NamerMaterialInspection inspection = new NamerMaterialInspection
                {
                    BakeSourceMesh = floor,
                    OccluderMesh = roof,
                };

                const int w = 64;
                const int h = 64;

                bool completed = pipeline.RequestBake(inspection, w, h, onComplete: null, shouldCancel: () => true);
                Assert.IsFalse(completed, "a cancelled bake must report cancellation, not completion");
                Assert.IsFalse(pipeline.HasCachedBake(floor, roof, w, h),
                    "a cancelled bake must NOT cache a partial result — the AO gate must pack white next recompute");
            }
            finally
            {
                pipeline.Dispose();
                Destroy(floor, roof);
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
                        AoStageEnabled = true,
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
                        // packed B byte must be ~255 (the stage flag keeps this run on the
                        // bake branch; without it the gate would white-fill and pass vacuously).
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

        [UnityTest]
        public IEnumerator Process_AoStageOn_NoAuthoredMap_BakesGeometryOnDemand()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU on-demand bake test (D-15: Metal is the verified target).");
                yield break;
            }

            const int w = 64;
            const int h = 64;

            NamerComputePipeline pipeline = new NamerComputePipeline();
            try
            {
                Texture2D baseMap = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
                Mesh floor = CreateFlatQuad();
                Mesh roof = CreateSubdividedRoof(4);
                try
                {
                    // Bright half (linear ~0.9) vs dark half (linear ~0.15): the bake must
                    // be albedo-blind — the retired luminance extraction darkened exactly
                    // the dark-painted half (its global-mean normalization conflated albedo
                    // with occlusion; see .planning/debug/ao-luma-false-occlusion.md).
                    Color[] pixels = new Color[w * h];
                    for (int y = 0; y < h; y++)
                    {
                        for (int x = 0; x < w; x++)
                        {
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
                        AoStageEnabled = true,
                        SmoothnessTextureChannel = 0,
                        BakeSourceMesh = floor,
                        OccluderMesh = roof,
                    };

                    // No RequestBake priming: the production gate itself must bake on
                    // demand (previously the bake was unreachable in production because
                    // nothing ever seeded the cache — chicken-and-egg with HasCachedBake).
                    Assert.IsFalse(pipeline.HasCachedBake(inspection, w, h),
                        "precondition: no cached bake before the first Process");

                    NamerComputeResult result = pipeline.Process(inspection);
                    try
                    {
                        Color32[] surface = ReadBackColor32(result.PackedSurface, w * h);

                        // The subdivided roof darkens the covered texels (geometry-driven):
                        // a white fill packs exactly 255, and the retired extractor packed
                        // 255 on the bright half — so both halves < 250 is decisive.
                        int brightQuarter = (h / 2) * w + (w / 4);
                        int darkQuarter = (h / 2) * w + (3 * w / 4);
                        Assert.Less((int)surface[brightQuarter].b, 250,
                            "AO stage on with no authored map must bake geometry INTO Process — a white fill (255) cannot produce b < 250");
                        Assert.Less((int)surface[darkQuarter].b, 250,
                            "the roof must darken the dark-albedo half too — the synthetic source is albedo-blind");
                        Assert.LessOrEqual(Mathf.Abs((int)surface[brightQuarter].b - (int)surface[darkQuarter].b), 24,
                            "bright- and dark-albedo texels under the same roof must carry nearly the same packed AO byte (the retired extractor's spread was ~180 bytes)");
                    }
                    finally
                    {
                        pipeline.ReleaseResult(result);
                    }

                    Assert.IsTrue(pipeline.HasCachedBake(inspection, w, h),
                        "the on-demand bake must be cached after the run");
                    Assert.AreEqual(0, pipeline.LiveRenderTargetCount,
                        "pool must return to baseline after the on-demand bake");
                }
                finally
                {
                    Destroy(baseMap, floor, roof);
                }
            }
            finally
            {
                pipeline.Dispose();
            }

            yield return null;
        }

        // --------------------------------------------------------------------
        // 2026-09-21 contract follow-up: the AO stage primes the bake mesh even
        // when decomposition is OFF (user decision). Processor-level E2E — the
        // priming lives in NamerProcessor.Process, upstream of the pipeline gate
        // the on-demand test above covers.
        // --------------------------------------------------------------------

        [UnityTest]
        public IEnumerator Process_AoStageOn_DecompositionOff_PrimesBakeMesh()
        {
            if (!ComputeAvailable)
            {
                Assert.Ignore("[NAMER] compute/async-readback unavailable — skipping GPU AO-priming E2E test (D-15: Metal is the verified target).");
                yield break;
            }

            EnsureTempFolder();
            SettingsSnapshot prefs = CaptureSettings();
            GameObject gameObject = null;
            try
            {
                // Self-occluding open box (floor + wall meeting at an inside corner).
                // Production never sets OccluderMesh, so the bake self-occludes (D-06
                // null fallback) — and a flat quad's self-bake is AO ~= 1.0 (see
                // Bake_FlatPlane_ProducesAoNearOne), indistinguishable from the white
                // fill. The inside corner is what makes the pin decisive.
                Mesh sourceMesh = CreateOpenBoxMeshAsset(TempFolder + "/SourceOpenBox.asset");
                Texture2D baseMap = CreateImportedSolidBaseMap(TempFolder + "/SourceBase.png", BakeResolution);
                Material source = CreateSourceMaterial(TempFolder, "SourceMat", baseMap);
                gameObject = CreateSceneObject(sourceMesh, source, "AoPrimeTarget");

                // Every field that can reach the packed B channel is pinned explicitly:
                // NamerProcessorSettings is EditorPrefs-backed (reads leak the live
                // session's values; writes mutate them — hence the snapshot/restore).
                // No _OcclusionMap is authored on the source material.
                var settings = new NamerProcessorSettings
                {
                    Destination = TempFolder + "/Out",
                    DecompositionEnabled = false,
                    AoStageEnabled = true,
                    AoStrength = 1f,
                    AoContrast = 1f,
                    AoBlurRadius = 0f,
                    MetallicContributionEnabled = false,
                    EmissiveContributionEnabled = false,
                };

                NamerProcessResult result = NamerProcessor.Process(gameObject, settings);
                Assert.IsNull(result.Error, "Process should succeed: " + result.Error);
                Assert.AreEqual(1, result.GeneratedAssets.Count, "one material set expected");

                NamerGeneratedAsset asset = result.GeneratedAssets[0];
                DecodeSurfacePng(asset.SurfaceTexturePath, out Color32[] surface, out int width, out int height);
                Assert.AreEqual(BakeResolution, width, "surface width must match the bake/base resolution");
                Assert.AreEqual(BakeResolution, height, "surface height must match the bake/base resolution");

                // The floor occupies the bottom UV half (v in [0, 0.5], v=0 AT the wall),
                // so bottom pixel rows sample the inside corner and rows just below the
                // v=0.5 seam sample the open far floor (the wall band v >= 0.5 is its own
                // dark base and is excluded on purpose). Rows 0-3 = v < 0.0625 (corner,
                // wall blocks ~half the hemisphere); rows 24-30 = v in [0.375, 0.469)
                // (floor z beyond -1.5, far past the 1-unit-tall wall's reach).
                float cornerMean = BlueBandMean(surface, width, 0, 4);
                float farMean = BlueBandMean(surface, width, 24, 30);

                // Without priming (the defect this pins), BakeSourceMesh stays null,
                // BakeAndUpload returns null, and the gate packs the WHITE fill — exactly
                // 255 everywhere. The inside corner blocks half the cosine-weighted
                // hemisphere, so a real bake lands near ~128 there.
                Assert.Less(cornerMean, 220f,
                    "AO stage on + decomposition off must prime BakeSourceMesh and bake the geometry — "
                    + "the inside-corner band packs ~128, a white fill packs exactly 255");
                Assert.Greater(farMean - cornerMean, 30f,
                    "the packed AO must be geometry-shaped: the far-floor band sits in open space "
                    + "(well above the wall's reach) and must out-bright the corner band by a clear margin");
            }
            finally
            {
                Destroy(gameObject);
                RestoreSettings(prefs);
                AssetDatabase.DeleteAsset(TempFolder);
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

        private static Mesh CreateSmallUVTriangle()
        {
            // Right triangle whose UV footprint is 4x2 texels at the 512 cap: v0 sits on
            // the center of texel (256, 256), legs run +4 and +2 texels. The center of
            // texel (257, 257) is strictly inside (barycentric 0.25/0.5/0.25).
            const float texel = 1f / 512f;
            Mesh mesh = new Mesh { name = "NamerAOBakeTestSmallUVTriangle" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 0.5f),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up };
            mesh.uv = new[]
            {
                new Vector2(256.5f * texel, 256.5f * texel),
                new Vector2(260.5f * texel, 256.5f * texel),
                new Vector2(256.5f * texel, 258.5f * texel),
            };
            mesh.triangles = new[] { 0, 1, 2 };
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

        private static Mesh CreateSubdividedRoof(int divisions)
        {
            Mesh mesh = new Mesh { name = "NamerAOBakeTestSubdividedRoof" };
            int cellsPerAxis = divisions;
            int vertexCount = (cellsPerAxis + 1) * (cellsPerAxis + 1);
            Vector3[] verts = new Vector3[vertexCount];
            Vector3[] normals = new Vector3[vertexCount];
            Vector2[] uvs = new Vector2[vertexCount];
            int[] tris = new int[cellsPerAxis * cellsPerAxis * 6];

            float step = 1f / cellsPerAxis;
            int vi = 0;
            for (int z = 0; z <= cellsPerAxis; z++)
            {
                for (int x = 0; x <= cellsPerAxis; x++)
                {
                    verts[vi] = new Vector3(x * step, 0.05f, z * step);
                    normals[vi] = Vector3.down;
                    uvs[vi] = new Vector2(x * step, z * step);
                    vi++;
                }
            }

            int ti = 0;
            for (int z = 0; z < cellsPerAxis; z++)
            {
                for (int x = 0; x < cellsPerAxis; x++)
                {
                    int a = z * (cellsPerAxis + 1) + x;
                    int b = a + 1;
                    int c = a + (cellsPerAxis + 1);
                    int d = c + 1;

                    tris[ti++] = a;
                    tris[ti++] = c;
                    tris[ti++] = b;

                    tris[ti++] = b;
                    tris[ti++] = c;
                    tris[ti++] = d;
                }
            }

            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
            return mesh;
        }

        // -- Processor E2E fixtures (AO-priming test) -----------------------

        /// <summary>
        /// Open inside-corner fixture persisted as an asset: a floor (y=0, z in [-2, 0],
        /// +Y normal, UV v in [0, 0.5] with v=0 AT the wall) meeting a wall (x in [0, 1],
        /// y in [0, 1], z=0, -Z normal facing over the floor, UV v in [0.5, 1]). The shared
        /// edge is non-welded so each quad keeps its own UV half, and texel centers keep
        /// the floor sample origins strictly z &lt; 0 (off the coplanar edge). The mesh
        /// self-occludes: the 1-unit-tall wall blocks ~half the cosine-weighted hemisphere
        /// of the corner texels (baked AO ~= 0.5 there) while the far floor stays open.
        /// </summary>
        private static Mesh CreateOpenBoxMeshAsset(string path)
        {
            Mesh mesh = new Mesh { name = "NamerAOBakeTestOpenBox" };
            mesh.vertices = new[]
            {
                // Floor (verts 0-3): z from 0 (at the wall) to -2 (open far edge).
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, -2f),
                new Vector3(0f, 0f, -2f),
                // Wall (verts 4-7): stands on the floor's z=0 edge. Triangle winding is
                // verified against the explicit normals (cross(b-a, c-a) = +Y for the
                // floor, -Z for the wall); the normals drive the bake hemisphere + cage
                // offset and must be exact.
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up,
                Vector3.back, Vector3.back, Vector3.back, Vector3.back,
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 0.5f),
                new Vector2(0f, 0.5f),
                new Vector2(0f, 0.5f),
                new Vector2(1f, 0.5f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 4, 6, 5, 4, 7, 6 };
            mesh.RecalculateBounds();
            AssetDatabase.CreateAsset(mesh, path);
            return AssetDatabase.LoadAssetAtPath<Mesh>(path);
        }

        private static Texture2D CreateImportedSolidBaseMap(string path, int size)
        {
            Texture2D source = new Texture2D(size, size, TextureFormat.RGBA32, false, true);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
            }

            source.SetPixels(pixels);
            source.Apply(false, false);
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.sRGBTexture = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static Material CreateSourceMaterial(string folder, string name, Texture2D baseMap)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.IsNotNull(shader, "URP Lit shader not found");

            // No _OcclusionMap on purpose: the test pins the SYNTHETIC source (the
            // geometry bake), which the D-07 gate only reaches without an authored map.
            Material material = new Material(shader) { name = name };
            material.SetTexture("_BaseMap", baseMap);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0f);
            AssetDatabase.CreateAsset(material, folder + "/" + name + ".mat");
            return material;
        }

        private static GameObject CreateSceneObject(Mesh mesh, Material material, string name)
        {
            GameObject go = new GameObject(name);
            MeshFilter filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            return go;
        }

        private static void EnsureTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }

            AssetDatabase.CreateFolder("Assets", "NAMER_AO_Tests_Temp");
        }

        private static void DecodeSurfacePng(string path, out Color32[] pixels, out int width, out int height)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            bool loaded = ImageConversion.LoadImage(texture, bytes);
            Assert.IsTrue(loaded, "failed to decode PNG at " + path);
            pixels = texture.GetPixels32();
            width = texture.width;
            height = texture.height;
            Destroy(texture);
        }

        private static float BlueBandMean(Color32[] pixels, int width, int yFrom, int yToExclusive)
        {
            long sum = 0;
            long count = 0;
            for (int y = yFrom; y < yToExclusive; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    sum += pixels[y * width + x].b;
                    count++;
                }
            }

            Assert.Greater(count, 0, "the pixel band must contain at least one row");
            return (float)sum / count;
        }

        // -- EditorPrefs isolation (AO-priming E2E) -------------------------
        // NamerProcessorSettings setters persist to EditorPrefs, so the settings object
        // initializer mutates live editor state. Snapshot the eight keys the test writes
        // and restore them in its finally (the NamerDipSwitchTests snapshot idiom).

        private sealed class SettingsSnapshot
        {
            public string Destination;
            public bool DecompositionEnabled;
            public bool AoStageEnabled;
            public float AoStrength;
            public float AoContrast;
            public float AoBlurRadius;
            public bool MetallicContributionEnabled;
            public bool EmissiveContributionEnabled;
            public bool HadDestination;
            public bool HadDecompositionEnabled;
            public bool HadAoStageEnabled;
            public bool HadAoStrength;
            public bool HadAoContrast;
            public bool HadAoBlurRadius;
            public bool HadMetallicContributionEnabled;
            public bool HadEmissiveContributionEnabled;
        }

        private static SettingsSnapshot CaptureSettings()
        {
            return new SettingsSnapshot
            {
                Destination = EditorPrefs.GetString(DestinationKey, string.Empty),
                DecompositionEnabled = EditorPrefs.GetBool(DecompositionEnabledKey, false),
                AoStageEnabled = EditorPrefs.GetBool(AoStageEnabledKey, true),
                AoStrength = EditorPrefs.GetFloat(AoStrengthKey, 1f),
                AoContrast = EditorPrefs.GetFloat(AoContrastKey, 1f),
                AoBlurRadius = EditorPrefs.GetFloat(AoBlurRadiusKey, 0f),
                MetallicContributionEnabled = EditorPrefs.GetBool(MetallicContributionEnabledKey, true),
                EmissiveContributionEnabled = EditorPrefs.GetBool(EmissiveContributionEnabledKey, true),
                HadDestination = EditorPrefs.HasKey(DestinationKey),
                HadDecompositionEnabled = EditorPrefs.HasKey(DecompositionEnabledKey),
                HadAoStageEnabled = EditorPrefs.HasKey(AoStageEnabledKey),
                HadAoStrength = EditorPrefs.HasKey(AoStrengthKey),
                HadAoContrast = EditorPrefs.HasKey(AoContrastKey),
                HadAoBlurRadius = EditorPrefs.HasKey(AoBlurRadiusKey),
                HadMetallicContributionEnabled = EditorPrefs.HasKey(MetallicContributionEnabledKey),
                HadEmissiveContributionEnabled = EditorPrefs.HasKey(EmissiveContributionEnabledKey),
            };
        }

        private static void RestoreSettings(SettingsSnapshot snapshot)
        {
            if (snapshot.HadDestination) { EditorPrefs.SetString(DestinationKey, snapshot.Destination); }
            else { EditorPrefs.DeleteKey(DestinationKey); }

            if (snapshot.HadDecompositionEnabled) { EditorPrefs.SetBool(DecompositionEnabledKey, snapshot.DecompositionEnabled); }
            else { EditorPrefs.DeleteKey(DecompositionEnabledKey); }

            if (snapshot.HadAoStageEnabled) { EditorPrefs.SetBool(AoStageEnabledKey, snapshot.AoStageEnabled); }
            else { EditorPrefs.DeleteKey(AoStageEnabledKey); }

            if (snapshot.HadAoStrength) { EditorPrefs.SetFloat(AoStrengthKey, snapshot.AoStrength); }
            else { EditorPrefs.DeleteKey(AoStrengthKey); }

            if (snapshot.HadAoContrast) { EditorPrefs.SetFloat(AoContrastKey, snapshot.AoContrast); }
            else { EditorPrefs.DeleteKey(AoContrastKey); }

            if (snapshot.HadAoBlurRadius) { EditorPrefs.SetFloat(AoBlurRadiusKey, snapshot.AoBlurRadius); }
            else { EditorPrefs.DeleteKey(AoBlurRadiusKey); }

            if (snapshot.HadMetallicContributionEnabled) { EditorPrefs.SetBool(MetallicContributionEnabledKey, snapshot.MetallicContributionEnabled); }
            else { EditorPrefs.DeleteKey(MetallicContributionEnabledKey); }

            if (snapshot.HadEmissiveContributionEnabled) { EditorPrefs.SetBool(EmissiveContributionEnabledKey, snapshot.EmissiveContributionEnabled); }
            else { EditorPrefs.DeleteKey(EmissiveContributionEnabledKey); }
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

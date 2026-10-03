using System;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// In-memory result of a geometry AO bake. <see cref="Ao"/> is a flat row-major
    /// <c>Width * Height</c> array of AO values in [0,1] for UV-covered texels and
    /// <c>-1</c> for empty (uncovered) texels that the later jump-flood seam dilation fills.
    /// The caller owns <see cref="Ao"/> and must <see cref="NativeArray{T}.Dispose"/> it.
    /// </summary>
    public readonly struct NamerAOBakeResult
    {
        public readonly NativeArray<float> Ao;
        public readonly int Width;
        public readonly int Height;

        /// <summary>
        /// True when the bake was cancelled before completing. <see cref="Ao"/> is then
        /// <c>default</c> (not created); the caller must not read it and must treat the
        /// bake as never having run (no partial result may be cached).
        /// </summary>
        public readonly bool Cancelled;

        public NamerAOBakeResult(NativeArray<float> ao, int width, int height, bool cancelled = false)
        {
            Ao = ao;
            Width = width;
            Height = height;
            Cancelled = cancelled;
        }
    }

    /// <summary>
    /// Deterministic CPU Burst geometry bake (Phase 03.1, plan 02). Replicates the Blender
    /// Cycles AO defaults (D-05) without Cycles: 64 ray directions, a 0.01 cage offset along
    /// the interpolated normal, a max ray distance of 10% of the largest bounds dimension,
    /// and a reduced-resolution output capped at 512². The occluder is the BVH; the low mesh
    /// supplies ray origins + output UVs (D-04: no UV match required). A null/invalid
    /// occluder falls back to the selected mesh (D-06), never a hard failure.
    ///
    /// Pure CPU and deterministic (fixed direction table + fixed float math), so it is
    /// testable headless. This is a mesh-geometry bake over a capped 512² resolution,
    /// Burst-parallelized — not a per-pixel C# texture loop and not on the 300 ms debounce.
    /// </summary>
    public static class NamerAOBaker
    {
        public const int kBakeResolutionCap = 512;
        public const float kCageOffset = 0.01f;
        public const float kMaxDistanceFactor = 0.1f;
        public const int kRayCount = 64;

        private const float kGoldenRatio = 0.61803398875f;
        private const float kTwoPi = 6.28318530718f;
        private const float kBarycentricAreaEps = 1e-8f;
        private const float kBarycentricInsideEps = 1e-4f;
        private const float kNormalBasisEps = 0.999f;
        private const int kInnerLoopBatchCount = 64;
        private const int kRowsPerSlab = 16;

        /// <summary>
        /// Fixed 64 cosine-weighted hemisphere directions around +Z, built once with a
        /// deterministic golden-ratio sequence (never <see cref="System.Random"/>). Each
        /// direction is rotated into the surface tangent frame per texel in the bake job.
        /// </summary>
        private static readonly float3[] DirectionTable = BuildDirectionTable();

        /// <summary>Interactive progress-bar title for the synchronous bake path.</summary>
        private const string kProgressTitle = "Baking NAMER AO";

        /// <summary>
        /// Bakes per-texel AO. Reads <paramref name="lowMesh"/>/<paramref name="occluderMesh"/>
        /// on the main thread into <see cref="NativeArray{T}"/>s, builds a BVH over the
        /// occluder, and runs a Burst-parallel job that casts <paramref name="rayCount"/>
        /// directions from each UV-covered texel. <paramref name="resolution"/> is capped to
        /// <see cref="kBakeResolutionCap"/>. The returned <see cref="NamerAOBakeResult.Ao"/> is
        /// owned by the caller; every intermediate allocation is disposed here.
        /// <paramref name="shouldCancel"/> is polled between row-slabs; when it returns
        /// <c>true</c> the bake aborts and the returned result is marked
        /// <see cref="NamerAOBakeResult.Cancelled"/>. When null (the interactive-editor path),
        /// a <see cref="EditorUtility.DisplayCancelableProgressBar"/> is shown and its
        /// cancel-return drives the same abort.
        /// </summary>
        public static NamerAOBakeResult Bake(Mesh lowMesh, Mesh occluderMesh, int resolution, float cageOffset, float maxDistanceFactor, int rayCount, Func<bool> shouldCancel = null)
        {
            BakeScratch scratch = default;
            try
            {
                scratch = GatherBakeScratch(lowMesh, occluderMesh, resolution, maxDistanceFactor, rayCount);
                return BakeCore(
                    scratch,
                    cageOffset,
                    (progress, message) => shouldCancel != null
                        ? shouldCancel()
                        : EditorUtility.DisplayCancelableProgressBar(kProgressTitle, message, progress));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                DisposeScratchExceptAo(scratch);
            }
        }

        /// <summary>
        /// Awaitable mirror of <see cref="Bake"/> (05.3 threading): the same
        /// main-thread gather, then the BVH build + slab loop on a worker thread via
        /// <see cref="Task.Run"/> while the editor loop keeps pumping (the TempJob
        /// scratch already crosses threads — the Burst jobs read these exact arrays
        /// from job workers today). Deterministic: identical ray math, slab batching,
        /// and cancellation semantics to <see cref="Bake"/>. Shows NO progress bar
        /// itself — the caller paints through <paramref name="reportProgress"/>
        /// (per-slab progress + phase message) and cancels through
        /// <paramref name="shouldCancel"/>. Unlike <see cref="Bake"/> this overload
        /// never clears the progress bar: the caller owns it.
        /// </summary>
        public static async Task<NamerAOBakeResult> BakeAsync(Mesh lowMesh, Mesh occluderMesh, int resolution, float cageOffset, float maxDistanceFactor, int rayCount, Func<bool> shouldCancel = null, Action<float, string> reportProgress = null)
        {
            BakeScratch scratch = default;
            try
            {
                scratch = GatherBakeScratch(lowMesh, occluderMesh, resolution, maxDistanceFactor, rayCount);
                return await Task.Run(() => BakeCore(
                    scratch,
                    cageOffset,
                    (progress, message) =>
                    {
                        if (reportProgress != null)
                        {
                            reportProgress(progress, message);
                        }

                        return shouldCancel != null && shouldCancel();
                    }));
            }
            finally
            {
                // No ClearProgressBar here — the async caller owns the bar (05.3 threading).
                DisposeScratchExceptAo(scratch);
            }
        }

        /// <summary>
        /// Main-thread gather shared by <see cref="Bake"/> and <see cref="BakeAsync"/>:
        /// argument validation, the D-06 occluder fallback, Unity mesh reads, the
        /// smooth-normal fallback, the NativeArray conversions, the fixed direction
        /// table, and the persistent AO buffer — everything the worker-side core
        /// needs, with the clamped resolution/ray count and derived max distance.
        /// </summary>
        private static BakeScratch GatherBakeScratch(Mesh lowMesh, Mesh occluderMesh, int resolution, float maxDistanceFactor, int rayCount)
        {
            if (lowMesh == null)
            {
                throw new ArgumentNullException(nameof(lowMesh));
            }

            if (occluderMesh == null || occluderMesh.vertexCount == 0)
            {
                occluderMesh = lowMesh; // D-06 fallback: never a hard failure.
            }

            int res = max(min(resolution, kBakeResolutionCap), 1);
            int clampedRays = clamp(rayCount, 1, kRayCount);

            // Unity Mesh reads happen on the main thread.
            Vector3[] lv = lowMesh.vertices;
            Vector3[] ln = lowMesh.normals;
            Vector2[] lu = lowMesh.uv;
            int[] lt = lowMesh.triangles;
            Vector3[] ov = occluderMesh.vertices;
            int[] ot = occluderMesh.triangles;

            if (ln == null || ln.Length == 0)
            {
                ln = ComputeSmoothNormals(lv, lt);
            }

            Vector3 boundsSize = lowMesh.bounds.size;
            float maxDim = max(max(boundsSize.x, boundsSize.y), boundsSize.z);

            BakeScratch scratch = new BakeScratch
            {
                LowVertices = ToFloat3(lv),
                LowNormals = ToFloat3(ln),
                LowUvs = ToFloat2(lu),
                LowTriangles = ToInt3(lt),
                OccluderVertices = ToFloat3(ov),
                OccluderTriangles = ToInt3(ot),
                Resolution = res,
                RayCount = clampedRays,
                MaxDistance = maxDistanceFactor * maxDim,
            };

            scratch.Directions = new NativeArray<float3>(kRayCount, Allocator.TempJob);
            for (int i = 0; i < kRayCount; i++)
            {
                scratch.Directions[i] = DirectionTable[i];
            }

            scratch.Ao = new NativeArray<float>(res * res, Allocator.Persistent);
            return scratch;
        }

        /// <summary>
        /// Worker-safe bake core shared by <see cref="Bake"/> and
        /// <see cref="BakeAsync"/>: builds the BVH over the occluder and runs the
        /// Burst slab loop, polling <paramref name="progressCancel"/> (progress,
        /// phase message → cancelled) between slabs. On cancel the AO buffer is
        /// disposed here and the cancelled result is returned (no partial result
        /// may be cached); on success the AO buffer ownership transfers to the
        /// caller. The BVH is always disposed.
        /// </summary>
        private static NamerAOBakeResult BakeCore(BakeScratch scratch, float cageOffset, Func<float, string, bool> progressCancel)
        {
            NamerAOBvh bvh = NamerAOBvh.Build(scratch.OccluderVertices, scratch.OccluderTriangles);
            try
            {
                int res = scratch.Resolution;
                // Per-texel work is fully independent: the direction table is fixed and
                // each texel reads only its own UV/position/normal plus the shared
                // read-only BVH, so batching the monolithic dispatch into row-slabs is
                // deterministic (identical results to a single full dispatch) and lets a
                // progress bar + cancellation poll interleave between slabs.
                for (int rowStart = 0; rowStart < res; rowStart += kRowsPerSlab)
                {
                    int rowCount = min(kRowsPerSlab, res - rowStart);
                    int startIndex = rowStart * res;
                    int indexCount = rowCount * res;

                    var job = new RayTriangleJob
                    {
                        LowVertices = scratch.LowVertices,
                        LowNormals = scratch.LowNormals,
                        LowUvs = scratch.LowUvs,
                        LowTriangles = scratch.LowTriangles,
                        BvhNodes = bvh.Nodes,
                        BvhPrimitives = bvh.PrimitiveIndices,
                        OccluderVertices = bvh.Vertices,
                        OccluderTriangles = bvh.Triangles,
                        Directions = scratch.Directions,
                        Resolution = res,
                        CageOffset = cageOffset,
                        MaxDistance = scratch.MaxDistance,
                        RayCount = scratch.RayCount,
                        StartIndex = startIndex,
                        AoOut = scratch.Ao,
                    };
                    job.Schedule(indexCount, kInnerLoopBatchCount).Complete();

                    if (progressCancel(
                            (float)(rowStart + rowCount) / res,
                            "Casting occlusion rays — row " + (rowStart + rowCount) + "/" + res))
                    {
                        if (scratch.Ao.IsCreated)
                        {
                            scratch.Ao.Dispose();
                        }

                        return new NamerAOBakeResult(default, res, res, cancelled: true);
                    }
                }
            }
            finally
            {
                bvh.Dispose();
            }

            return new NamerAOBakeResult(scratch.Ao, scratch.Resolution, scratch.Resolution);
        }

        /// <summary>
        /// Disposes every scratch buffer EXCEPT <see cref="BakeScratch.Ao"/> — the AO
        /// array is returned to the caller by <see cref="BakeCore"/> (or already
        /// disposed on the cancel path).
        /// </summary>
        private static void DisposeScratchExceptAo(BakeScratch scratch)
        {
            if (scratch.LowVertices.IsCreated) scratch.LowVertices.Dispose();
            if (scratch.LowNormals.IsCreated) scratch.LowNormals.Dispose();
            if (scratch.LowUvs.IsCreated) scratch.LowUvs.Dispose();
            if (scratch.LowTriangles.IsCreated) scratch.LowTriangles.Dispose();
            if (scratch.OccluderVertices.IsCreated) scratch.OccluderVertices.Dispose();
            if (scratch.OccluderTriangles.IsCreated) scratch.OccluderTriangles.Dispose();
            if (scratch.Directions.IsCreated) scratch.Directions.Dispose();
        }

        /// <summary>
        /// The bake's cross-thread buffers (05.3 threading): gathered on the main
        /// thread by <see cref="GatherBakeScratch"/>, consumed on the worker by
        /// <see cref="BakeCore"/> — the same TempJob/Persistent arrays the Burst
        /// jobs already access from job workers.
        /// </summary>
        private struct BakeScratch
        {
            public NativeArray<float3> LowVertices;
            public NativeArray<float3> LowNormals;
            public NativeArray<float2> LowUvs;
            public NativeArray<int3> LowTriangles;
            public NativeArray<float3> OccluderVertices;
            public NativeArray<int3> OccluderTriangles;
            public NativeArray<float3> Directions;
            public NativeArray<float> Ao;
            public int Resolution;
            public int RayCount;
            public float MaxDistance;
        }

        private static float3[] BuildDirectionTable()
        {
            var dirs = new float3[kRayCount];
            for (int i = 0; i < kRayCount; i++)
            {
                float u1 = (i + 0.5f) / kRayCount;
                float u2 = frac(i * kGoldenRatio);
                float r = sqrt(u1);
                float theta = kTwoPi * u2;
                dirs[i] = new float3(r * cos(theta), r * sin(theta), sqrt(1f - u1));
            }

            return dirs;
        }

        private static Vector3[] ComputeSmoothNormals(Vector3[] verts, int[] tris)
        {
            Vector3[] normals = new Vector3[verts.Length];
            for (int t = 0; t + 2 < tris.Length; t += 3)
            {
                int a = tris[t];
                int b = tris[t + 1];
                int c = tris[t + 2];
                Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                normals[a] += n;
                normals[b] += n;
                normals[c] += n;
            }

            for (int i = 0; i < normals.Length; i++)
            {
                normals[i] = normals[i].normalized;
            }

            return normals;
        }

        private static NativeArray<float3> ToFloat3(Vector3[] src)
        {
            var arr = new NativeArray<float3>(src.Length, Allocator.TempJob);
            for (int i = 0; i < src.Length; i++)
            {
                arr[i] = new float3(src[i].x, src[i].y, src[i].z);
            }

            return arr;
        }

        private static NativeArray<float2> ToFloat2(Vector2[] src)
        {
            var arr = new NativeArray<float2>(src.Length, Allocator.TempJob);
            for (int i = 0; i < src.Length; i++)
            {
                arr[i] = new float2(src[i].x, src[i].y);
            }

            return arr;
        }

        private static NativeArray<int3> ToInt3(int[] src)
        {
            var arr = new NativeArray<int3>(src.Length / 3, Allocator.TempJob);
            for (int i = 0; i < arr.Length; i++)
            {
                arr[i] = new int3(src[i * 3], src[i * 3 + 1], src[i * 3 + 2]);
            }

            return arr;
        }

        [BurstCompile]
        private struct RayTriangleJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float3> LowVertices;
            [ReadOnly] public NativeArray<float3> LowNormals;
            [ReadOnly] public NativeArray<float2> LowUvs;
            [ReadOnly] public NativeArray<int3> LowTriangles;
            [ReadOnly] public NativeArray<NamerAOBvh.BvhNode> BvhNodes;
            [ReadOnly] public NativeArray<int> BvhPrimitives;
            [ReadOnly] public NativeArray<float3> OccluderVertices;
            [ReadOnly] public NativeArray<int3> OccluderTriangles;
            [ReadOnly] public NativeArray<float3> Directions;
            public int Resolution;
            public float CageOffset;
            public float MaxDistance;
            public int RayCount;
            public int StartIndex;
            // Slab scheduling writes AoOut[i] for i = index + StartIndex (disjoint per
            // slab, each completed before the next), so the parallel-for index restriction
            // is lifted — no two in-flight jobs ever touch the same element.
            [WriteOnly]
            [NativeDisableParallelForRestriction]
            public NativeArray<float> AoOut;

            public void Execute(int index)
            {
                int i = index + StartIndex;
                int x = i % Resolution;
                int y = i / Resolution;
                float2 uv = new float2((x + 0.5f) / Resolution, (y + 0.5f) / Resolution);

                if (!TryReconstruct(uv, out float3 worldPos, out float3 normal))
                {
                    AoOut[i] = -1f; // empty texel — filled later by jump-flood dilation.
                    return;
                }

                float3 origin = worldPos + normal * CageOffset;

                // Deterministic orthonormal basis: align +Z to the interpolated normal.
                float3 helper = abs(normal.z) < kNormalBasisEps ? new float3(0f, 0f, 1f) : new float3(1f, 0f, 0f);
                float3 tangent = normalize(cross(helper, normal));
                float3 bitangent = cross(normal, tangent);

                int unoccluded = 0;
                for (int r = 0; r < RayCount; r++)
                {
                    float3 d = Directions[r];
                    float3 worldDir = tangent * d.x + bitangent * d.y + normal * d.z;
                    if (!NamerAOBvh.TraceBvh(BvhNodes, BvhPrimitives, OccluderVertices, OccluderTriangles, origin, worldDir, MaxDistance, out float _))
                    {
                        unoccluded++;
                    }
                }

                AoOut[i] = (float)unoccluded / RayCount;
            }

            private bool TryReconstruct(float2 uv, out float3 pos, out float3 normal)
            {
                // Texel-space barycentric test: denom = (2*Area_texels)^2, so the area
                // epsilon rejects only true zero-area triangles at any bake resolution.
                // In [0,1] UV space denom shrinks quadratically with resolution and
                // silently discarded real triangles (debug: residual-missing-triangles).
                float2 p = uv * Resolution;
                float2 texels = new float2(Resolution);
                for (int t = 0; t < LowTriangles.Length; t++)
                {
                    int3 tri = LowTriangles[t];
                    float2 a = LowUvs[tri.x] * texels;
                    float2 b = LowUvs[tri.y] * texels;
                    float2 c = LowUvs[tri.z] * texels;

                    float2 v0 = b - a;
                    float2 v1 = c - a;
                    float2 v2 = p - a;
                    float d00 = dot(v0, v0);
                    float d01 = dot(v0, v1);
                    float d11 = dot(v1, v1);
                    float d20 = dot(v2, v0);
                    float d21 = dot(v2, v1);
                    float denom = d00 * d11 - d01 * d01;
                    if (abs(denom) < kBarycentricAreaEps)
                    {
                        continue;
                    }

                    float v = (d11 * d20 - d01 * d21) / denom;
                    float w = (d00 * d21 - d01 * d20) / denom;
                    float u = 1f - v - w;
                    if (u < -kBarycentricInsideEps || v < -kBarycentricInsideEps || w < -kBarycentricInsideEps)
                    {
                        continue;
                    }

                    pos = LowVertices[tri.x] * u + LowVertices[tri.y] * v + LowVertices[tri.z] * w;
                    normal = normalize(LowNormals[tri.x] * u + LowNormals[tri.y] * v + LowNormals[tri.z] * w);
                    return true;
                }

                pos = 0f;
                normal = new float3(0f, 0f, 1f);
                return false;
            }
        }
    }
}

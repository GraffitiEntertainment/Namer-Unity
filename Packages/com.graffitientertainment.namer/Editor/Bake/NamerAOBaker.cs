using System;
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

            NativeArray<float3> lowVerts = default;
            NativeArray<float3> lowNormals = default;
            NativeArray<float2> lowUvs = default;
            NativeArray<int3> lowTris = default;
            NativeArray<float3> occVerts = default;
            NativeArray<int3> occTris = default;
            NativeArray<float3> directions = default;
            NativeArray<float> ao = default;

            try
            {
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

                lowVerts = ToFloat3(lv);
                lowNormals = ToFloat3(ln);
                lowUvs = ToFloat2(lu);
                lowTris = ToInt3(lt);
                occVerts = ToFloat3(ov);
                occTris = ToInt3(ot);

                directions = new NativeArray<float3>(kRayCount, Allocator.TempJob);
                for (int i = 0; i < kRayCount; i++)
                {
                    directions[i] = DirectionTable[i];
                }

                ao = new NativeArray<float>(res * res, Allocator.Persistent);

                Vector3 boundsSize = lowMesh.bounds.size;
                float maxDim = max(max(boundsSize.x, boundsSize.y), boundsSize.z);
                float maxDistance = maxDistanceFactor * maxDim;

                NamerAOBvh bvh = NamerAOBvh.Build(occVerts, occTris);
                try
                {
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
                            LowVertices = lowVerts,
                            LowNormals = lowNormals,
                            LowUvs = lowUvs,
                            LowTriangles = lowTris,
                            BvhNodes = bvh.Nodes,
                            BvhPrimitives = bvh.PrimitiveIndices,
                            OccluderVertices = bvh.Vertices,
                            OccluderTriangles = bvh.Triangles,
                            Directions = directions,
                            Resolution = res,
                            CageOffset = cageOffset,
                            MaxDistance = maxDistance,
                            RayCount = clampedRays,
                            StartIndex = startIndex,
                            AoOut = ao,
                        };
                        job.Schedule(indexCount, kInnerLoopBatchCount).Complete();

                        float progress = (float)(rowStart + rowCount) / res;
                        bool cancelled;
                        if (shouldCancel != null)
                        {
                            // Injectable headless path (tests): no editor UI.
                            cancelled = shouldCancel();
                        }
                        else
                        {
                            // Interactive-editor path: visible, cancellable progress.
                            cancelled = EditorUtility.DisplayCancelableProgressBar(
                                "Baking NAMER AO",
                                "Casting occlusion rays — row " + (rowStart + rowCount) + "/" + res,
                                progress);
                        }

                        if (cancelled)
                        {
                            if (ao.IsCreated)
                            {
                                ao.Dispose();
                            }

                            return new NamerAOBakeResult(default, res, res, cancelled: true);
                        }
                    }
                }
                finally
                {
                    bvh.Dispose();
                }

                return new NamerAOBakeResult(ao, res, res);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                if (lowVerts.IsCreated) lowVerts.Dispose();
                if (lowNormals.IsCreated) lowNormals.Dispose();
                if (lowUvs.IsCreated) lowUvs.Dispose();
                if (lowTris.IsCreated) lowTris.Dispose();
                if (occVerts.IsCreated) occVerts.Dispose();
                if (occTris.IsCreated) occTris.Dispose();
                if (directions.IsCreated) directions.Dispose();
                // ao is intentionally NOT disposed — it is returned to the caller.
            }
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
                for (int t = 0; t < LowTriangles.Length; t++)
                {
                    int3 tri = LowTriangles[t];
                    float2 a = LowUvs[tri.x];
                    float2 b = LowUvs[tri.y];
                    float2 c = LowUvs[tri.z];

                    float2 v0 = b - a;
                    float2 v1 = c - a;
                    float2 v2 = uv - a;
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

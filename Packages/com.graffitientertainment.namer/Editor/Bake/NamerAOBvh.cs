using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Median-split bounding volume hierarchy over an occluder mesh's triangles, used by
    /// <see cref="NamerAOBaker"/> to answer per-texel visibility raycasts (Phase 03.1,
    /// plan 02). The occluder is the BVH; the low mesh supplies ray origins + output UVs.
    ///
    /// Pure value-type math over <see cref="NativeArray{T}"/> — no <see cref="UnityEngine.Object"/>
    /// fields — so traversal (<see cref="RayCast"/> and the static
    /// <see cref="TraceBvh"/>/<see cref="RayTriangle"/> helpers) is Burst-compatible.
    /// <see cref="Build"/> runs on the main thread (it may use managed collections) and
    /// returns a hierarchy that owns its node + primitive-index arrays; the caller owns and
    /// disposes the <paramref name="vertices"/>/<paramref name="triangles"/> inputs after the
    /// bake completes. An all-triangles linear scan is intentionally avoided: 268 M rays ×
    /// O(n) is intractable, so every ray traverses the hierarchy in O(log n).
    /// </summary>
    public struct NamerAOBvh
    {
        private const float kRayEps = 1e-8f;
        private const int kLeafSize = 4;
        private const int kMaxTraversalStack = 256;

        /// <summary>
        /// One BVH node. <see cref="IsLeaf"/> selects the encoding: a leaf stores a
        /// [<see cref="Left"/>, <see cref="Right"/>) range into the primitive-index array; an
        /// internal node stores the left/right child indices. <c>IsLeaf</c> is an <c>int</c>
        /// (not <c>bool</c>) so the node stays a blittable value type for <see cref="NativeArray{T}"/>.
        /// </summary>
        public struct BvhNode
        {
            public float3 Min;
            public float3 Max;
            public int Left;
            public int Right;
            public int IsLeaf;
        }

        private NativeArray<BvhNode> _nodes;
        private NativeArray<int> _primitiveIndices;
        private NativeArray<float3> _vertices;
        private NativeArray<int3> _triangles;

        /// <summary>Flat node array (owned by this hierarchy).</summary>
        internal NativeArray<BvhNode> Nodes => _nodes;

        /// <summary>Reordered triangle indices referenced by leaf nodes (owned by this hierarchy).</summary>
        internal NativeArray<int> PrimitiveIndices => _primitiveIndices;

        /// <summary>Occluder vertices (caller-owned, referenced for Möller–Trumbore tests).</summary>
        internal NativeArray<float3> Vertices => _vertices;

        /// <summary>Occluder triangles (caller-owned, referenced for Möller–Trumbore tests).</summary>
        internal NativeArray<int3> Triangles => _triangles;

        /// <summary>
        /// Builds a median-split BVH over <paramref name="triangles"/>. Deterministic: the
        /// partition uses a fixed Lomuto quickselect on triangle centroids, never
        /// <see cref="System.Random"/>. The returned hierarchy owns its node + primitive-index
        /// arrays (dispose via <see cref="Dispose"/>); the input arrays are borrowed, not owned.
        /// </summary>
        public static NamerAOBvh Build(NativeArray<float3> vertices, NativeArray<int3> triangles)
        {
            int triCount = triangles.Length;

            var nodes = new List<BvhNode>(triCount * 2 + 1);
            var primitives = new List<int>(triCount);

            var centroids = new float3[triCount];
            for (int i = 0; i < triCount; i++)
            {
                int3 tri = triangles[i];
                centroids[i] = (vertices[tri.x] + vertices[tri.y] + vertices[tri.z]) / 3.0f;
            }

            var indices = new int[triCount];
            for (int i = 0; i < triCount; i++)
            {
                indices[i] = i;
            }

            if (triCount > 0)
            {
                BuildRecursive(vertices, triangles, centroids, indices, 0, triCount, nodes, primitives);
            }

            NativeArray<BvhNode> nodeArray = new NativeArray<BvhNode>(nodes.Count, Allocator.Persistent);
            NativeArray<int> primArray = new NativeArray<int>(primitives.Count, Allocator.Persistent);
            for (int i = 0; i < nodes.Count; i++)
            {
                nodeArray[i] = nodes[i];
            }

            for (int i = 0; i < primitives.Count; i++)
            {
                primArray[i] = primitives[i];
            }

            return new NamerAOBvh
            {
                _nodes = nodeArray,
                _primitiveIndices = primArray,
                _vertices = vertices,
                _triangles = triangles,
            };
        }

        /// <summary>
        /// Casts a ray against the hierarchy and reports the nearest triangle hit. Returns
        /// <c>false</c> and leaves <paramref name="t"/> at 0 when nothing is hit within
        /// <paramref name="maxDistance"/>. Hits at <c>t &lt;= kRayEps</c> are ignored so the
        /// origin surface never self-occludes (Pitfall 4).
        /// </summary>
        public bool RayCast(float3 origin, float3 dir, float maxDistance, out float t)
        {
            return TraceBvh(_nodes, _primitiveIndices, _vertices, _triangles, origin, dir, maxDistance, out t);
        }

        /// <summary>Disposes the hierarchy-owned arrays (node + primitive-index). Inputs are caller-owned.</summary>
        public void Dispose()
        {
            if (_nodes.IsCreated)
            {
                _nodes.Dispose();
            }

            if (_primitiveIndices.IsCreated)
            {
                _primitiveIndices.Dispose();
            }
        }

        private static int BuildRecursive(
            in NativeArray<float3> vertices,
            in NativeArray<int3> triangles,
            float3[] centroids,
            int[] indices,
            int start,
            int count,
            List<BvhNode> nodes,
            List<int> primitives)
        {
            float3 aabbMin = new float3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
            float3 aabbMax = new float3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);
            float3 centroidMin = aabbMin;
            float3 centroidMax = aabbMax;

            for (int i = start; i < start + count; i++)
            {
                int3 tri = triangles[indices[i]];
                float3 v0 = vertices[tri.x];
                float3 v1 = vertices[tri.y];
                float3 v2 = vertices[tri.z];
                aabbMin = min(aabbMin, min(min(v0, v1), v2));
                aabbMax = max(aabbMax, max(max(v0, v1), v2));

                float3 c = centroids[indices[i]];
                centroidMin = min(centroidMin, c);
                centroidMax = max(centroidMax, c);
            }

            if (count <= kLeafSize)
            {
                int leafIndex = nodes.Count;
                nodes.Add(new BvhNode
                {
                    Min = aabbMin,
                    Max = aabbMax,
                    Left = primitives.Count,
                    Right = primitives.Count + count,
                    IsLeaf = 1,
                });
                for (int i = start; i < start + count; i++)
                {
                    primitives.Add(indices[i]);
                }

                return leafIndex;
            }

            float3 extent = centroidMax - centroidMin;
            int axis = extent.x > extent.y
                ? (extent.x > extent.z ? 0 : 2)
                : (extent.y > extent.z ? 1 : 2);

            int mid = SelectMedian(centroids, indices, start, count, axis);
            int leftCount = mid - start;

            int leftChild = BuildRecursive(vertices, triangles, centroids, indices, start, leftCount, nodes, primitives);
            int rightChild = BuildRecursive(vertices, triangles, centroids, indices, mid, count - leftCount, nodes, primitives);

            int nodeIndex = nodes.Count;
            nodes.Add(new BvhNode
            {
                Min = aabbMin,
                Max = aabbMax,
                Left = leftChild,
                Right = rightChild,
                IsLeaf = 0,
            });

            return nodeIndex;
        }

        private static int SelectMedian(float3[] centroids, int[] indices, int start, int count, int axis)
        {
            int left = start;
            int right = start + count - 1;
            int k = start + count / 2;

            while (left < right)
            {
                int pivot = Partition(centroids, indices, left, right, axis);
                if (pivot == k)
                {
                    return k;
                }

                if (k < pivot)
                {
                    right = pivot - 1;
                }
                else
                {
                    left = pivot + 1;
                }
            }

            return k;
        }

        private static int Partition(float3[] centroids, int[] indices, int left, int right, int axis)
        {
            float pivot = CentroidsAxis(centroids[indices[right]], axis);
            int store = left;
            for (int i = left; i < right; i++)
            {
                if (CentroidsAxis(centroids[indices[i]], axis) < pivot)
                {
                    Swap(indices, i, store);
                    store++;
                }
            }

            Swap(indices, store, right);
            return store;
        }

        private static float CentroidsAxis(float3 c, int axis)
        {
            return axis == 0 ? c.x : (axis == 1 ? c.y : c.z);
        }

        private static void Swap(int[] array, int a, int b)
        {
            int tmp = array[a];
            array[a] = array[b];
            array[b] = tmp;
        }

        internal static bool TraceBvh(
            NativeArray<BvhNode> nodes,
            NativeArray<int> primitives,
            NativeArray<float3> vertices,
            NativeArray<int3> triangles,
            float3 origin,
            float3 dir,
            float maxDistance,
            out float t)
        {
            t = 0f;
            if (nodes.Length == 0)
            {
                return false;
            }

            Span<int> stack = stackalloc int[kMaxTraversalStack];
            int sp = 0;
            // Build constructs the hierarchy post-order (children are appended before
            // their parent), so the root is the LAST node — not node 0. The empty-tree
            // case is already guarded by the nodes.Length == 0 early return above.
            stack[sp++] = nodes.Length - 1;

            float bestT = maxDistance;
            bool hit = false;

            while (sp > 0)
            {
                int nodeIndex = stack[--sp];

                BvhNode node = nodes[nodeIndex];
                if (!RayAabbHit(origin, dir, bestT, node.Min, node.Max))
                {
                    continue;
                }

                if (node.IsLeaf != 0)
                {
                    for (int p = node.Left; p < node.Right; p++)
                    {
                        int3 tri = triangles[primitives[p]];
                        if (RayTriangle(origin, dir, vertices[tri.x], vertices[tri.y], vertices[tri.z], out float tt) && tt < bestT)
                        {
                            bestT = tt;
                            hit = true;
                        }
                    }
                }
                else
                {
                    if (sp + 2 <= kMaxTraversalStack)
                    {
                        stack[sp++] = node.Left;
                        stack[sp++] = node.Right;
                    }
                }
            }

            if (hit)
            {
                t = bestT;
                return true;
            }

            return false;
        }

        internal static bool RayTriangle(float3 origin, float3 dir, float3 v0, float3 v1, float3 v2, out float t)
        {
            float3 e1 = v1 - v0;
            float3 e2 = v2 - v0;
            float3 p = cross(dir, e2);
            float det = dot(e1, p);
            if (abs(det) < kRayEps)
            {
                t = 0f;
                return false;
            }

            float inv = 1f / det;
            float3 s = origin - v0;
            float u = dot(s, p) * inv;
            if (u < 0f || u > 1f)
            {
                t = 0f;
                return false;
            }

            float3 q = cross(s, e1);
            float v = dot(dir, q) * inv;
            if (v < 0f || u + v > 1f)
            {
                t = 0f;
                return false;
            }

            t = dot(e2, q) * inv;
            return t >= kRayEps;
        }

        private static bool RayAabbHit(float3 origin, float3 dir, float maxT, float3 boxMin, float3 boxMax)
        {
            float3 invDir = 1f / dir;
            float3 t0 = (boxMin - origin) * invDir;
            float3 t1 = (boxMax - origin) * invDir;
            float3 tmin = min(t0, t1);
            float3 tmax = max(t0, t1);
            float tEnter = max(max(tmin.x, tmin.y), tmin.z);
            float tExit = min(min(tmax.x, tmax.y), tmax.z);
            return tExit >= max(tEnter, 0f) && tEnter <= maxT;
        }
    }
}

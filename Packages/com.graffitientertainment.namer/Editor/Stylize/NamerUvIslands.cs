using System;
using System.Collections.Generic;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Result of the UV-island union-find (D-03): one compact island id per triangle (in
    /// flattened sub-mesh order), the true island count, and the binary16 clamp flag.
    /// <see cref="TriIsland"/> is null when any UV falls outside the
    /// [0-eps, 1+eps] atlas range — the kUvBoundsEpsilon guard mirrors
    /// NamerDecompPipeline.GenerateResidual's honest fallback, and callers treat null as
    /// "no islands available" (the bilateral stage skips; T-05-08).
    /// </summary>
    public sealed class NamerUvIslandResult
    {
        /// <summary>
        /// One island id per triangle in flattened sub-mesh order; ids are consecutive
        /// [0, <see cref="IslandCount"/>) before any clamp. Null when the out-of-range
        /// guard tripped (tiled/mis-mapped UVs).
        /// </summary>
        public int[] TriIsland;

        /// <summary>Number of distinct UV islands found (the pre-clamp TRUE count).</summary>
        public int IslandCount;

        /// <summary>
        /// True when <see cref="IslandCount"/> exceeded
        /// <see cref="NamerUvIslands.kMaxIslandIdFloat16"/> and island ids above the cap
        /// alias to the cap value in the binary16 island texture — flagged so the aliasing
        /// is never silent (Pitfall 3 / T-05-08).
        /// </summary>
        public bool ClampedToFloat16Max;
    }

    /// <summary>
    /// Deterministic CPU union-find over UV-adjacent triangles of a split mesh (D-03).
    /// Two triangles join the same island when they share a QUANTIZED UV-space edge — the
    /// edge key is the ordered pair of quantized UV endpoints, never vertex indices,
    /// because position-split vertices share UVs but carry distinct indices (Pitfall 2:
    /// the 04.1 position weld keyed on positions; an index-keyed island walk would split
    /// every UV-seam-shared edge back apart). Quantization uses the splitter's
    /// kUvQuantize grid, the union-find uses path compression + union-by-size, and the
    /// final ids compact to consecutive [0, N) in fixed triangle order, so the same input
    /// always produces element-wise identical output.
    /// </summary>
    public static class NamerUvIslands
    {
        /// <summary>
        /// UV quantization grid matching <c>MeshVertexSplitter.kUvQuantize</c> — island
        /// edge keys must land on the same grid the splitter's UV welds use.
        /// </summary>
        private const float kUvQuantize = 1e-4f;

        /// <summary>
        /// UV atlas bounds tolerance mirroring
        /// <c>NamerDecompPipeline.GenerateResidual.kUvBoundsEpsilon</c>: UVs beyond
        /// [0-eps, 1+eps] mean a tiled/mis-mapped layout the island rasterizer cannot
        /// represent, so the honest result is no islands (null TriIsland).
        /// </summary>
        private const float kUvBoundsEpsilon = 1e-4f;

        /// <summary>
        /// Largest island id exactly representable in the binary16 (half-float)
        /// R16G16B16A16_SFloat island texture (Pitfall 3). Ids above this value would
        /// silently alias when stored as half floats; instead BuildIslandIds clamps them
        /// to this cap and raises <c>ClampedToFloat16Max</c> (T-05-08).
        /// </summary>
        public const int kMaxIslandIdFloat16 = 2048;

        private const int kVertsPerTriangle = 3;
        private const int kHashPrime = 397;

        /// <summary>
        /// Flattens sub-mesh triangle lists into one index buffer, sub-mesh order
        /// (mirrors <c>NamerDecompPipeline.FlattenTriangles</c> — the rasterizer upload
        /// contract the stylize pipeline reuses).
        /// </summary>
        public static int[] FlattenTriangles(int[][] subMeshTriangles)
        {
            if (subMeshTriangles == null)
            {
                return Array.Empty<int>();
            }

            int total = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub != null)
                {
                    total += sub.Length;
                }
            }

            int[] tris = new int[total];
            int offset = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub == null)
                {
                    continue;
                }

                Array.Copy(sub, 0, tris, offset, sub.Length);
                offset += sub.Length;
            }

            return tris;
        }

        /// <summary>
        /// Builds one compact island id per triangle via union-find over quantized
        /// UV-space edges. Deterministic: the same <paramref name="split"/> always yields
        /// element-wise identical <see cref="NamerUvIslandResult.TriIsland"/> arrays.
        /// The split is only read, never mutated.
        /// </summary>
        /// <param name="split">Split mesh (any UV-space split; positions are ignored).</param>
        /// <returns>Island result; TriIsland is null when UVs fall outside the
        /// kUvBoundsEpsilon atlas range.</returns>
        public static NamerUvIslandResult BuildIslandIds(NamerSplitResult split)
        {
            if (split == null)
            {
                throw new ArgumentNullException(nameof(split));
            }

            if (split.Uvs == null || split.SubMeshTriangles == null)
            {
                throw new ArgumentNullException(split.Uvs == null ? nameof(split.Uvs) : nameof(split.SubMeshTriangles));
            }

            NamerUvIslandResult result = new NamerUvIslandResult();

            // Out-of-range guard (mirror GenerateResidual's kUvBoundsEpsilon handling):
            // a tiled layout cannot be island-rasterized into a [0,1] atlas texture, so
            // return the honest no-islands result instead of silently wrong islands.
            int vertexCount = split.VertexCount;
            for (int v = 0; v < vertexCount; v++)
            {
                Vector2 uv = split.Uvs[v];
                if (uv.x < -kUvBoundsEpsilon || uv.x > 1f + kUvBoundsEpsilon ||
                    uv.y < -kUvBoundsEpsilon || uv.y > 1f + kUvBoundsEpsilon)
                {
                    result.TriIsland = null;
                    result.IslandCount = 0;
                    return result;
                }
            }

            int[] triangles = FlattenTriangles(split.SubMeshTriangles);
            int triCount = triangles.Length / kVertsPerTriangle;

            // Union-find over triangle indices: parent chain + subtree sizes.
            int[] parent = new int[triCount];
            int[] sizes = new int[triCount];
            for (int t = 0; t < triCount; t++)
            {
                parent[t] = t;
                sizes[t] = 1;
            }

            // First triangle owner per quantized UV edge; a repeated key unions its
            // owners (position-split shared edges carry distinct indices but identical
            // quantized UVs, so they hit the same key — Pitfall 2).
            var edgeOwner = new Dictionary<UvEdgeKey, int>();
            for (int t = 0; t < triCount; t++)
            {
                int i0 = triangles[t * kVertsPerTriangle];
                int i1 = triangles[t * kVertsPerTriangle + 1];
                int i2 = triangles[t * kVertsPerTriangle + 2];
                UnionEdge(edgeOwner, parent, sizes, Quantize(split.Uvs[i0]), Quantize(split.Uvs[i1]), t);
                UnionEdge(edgeOwner, parent, sizes, Quantize(split.Uvs[i1]), Quantize(split.Uvs[i2]), t);
                UnionEdge(edgeOwner, parent, sizes, Quantize(split.Uvs[i2]), Quantize(split.Uvs[i0]), t);
            }

            // Compact roots to consecutive ids in fixed triangle order (determinism).
            int[] triIsland = new int[triCount];
            var rootToId = new Dictionary<int, int>();
            int nextId = 0;
            for (int t = 0; t < triCount; t++)
            {
                int root = Find(parent, t);
                if (!rootToId.TryGetValue(root, out int id))
                {
                    id = nextId++;
                    rootToId[root] = id;
                }

                triIsland[t] = id;
            }

            // Binary16 clamp (Pitfall 3): ids above the cap alias to the cap — flagged,
            // never silent (T-05-08).
            if (nextId > kMaxIslandIdFloat16)
            {
                result.ClampedToFloat16Max = true;
                for (int t = 0; t < triCount; t++)
                {
                    if (triIsland[t] > kMaxIslandIdFloat16)
                    {
                        triIsland[t] = kMaxIslandIdFloat16;
                    }
                }
            }

            result.TriIsland = triIsland;
            result.IslandCount = nextId;
            return result;
        }

        private static void UnionEdge(
            Dictionary<UvEdgeKey, int> edgeOwner,
            int[] parent,
            int[] sizes,
            QuantizedUv a,
            QuantizedUv b,
            int triangle)
        {
            var key = new UvEdgeKey(a, b);
            if (edgeOwner.TryGetValue(key, out int owner))
            {
                Union(parent, sizes, owner, triangle);
            }
            else
            {
                edgeOwner[key] = triangle;
            }
        }

        private static QuantizedUv Quantize(Vector2 uv)
        {
            return new QuantizedUv(
                Mathf.RoundToInt(uv.x / kUvQuantize),
                Mathf.RoundToInt(uv.y / kUvQuantize));
        }

        /// <summary>Find with path compression (iterative — no recursion).</summary>
        private static int Find(int[] parent, int i)
        {
            int root = i;
            while (parent[root] != root)
            {
                root = parent[root];
            }

            while (parent[i] != root)
            {
                int next = parent[i];
                parent[i] = root;
                i = next;
            }

            return root;
        }

        /// <summary>Union by size: the smaller root attaches under the larger root.</summary>
        private static void Union(int[] parent, int[] sizes, int a, int b)
        {
            int ra = Find(parent, a);
            int rb = Find(parent, b);
            if (ra == rb)
            {
                return;
            }

            if (sizes[ra] < sizes[rb])
            {
                int temp = ra;
                ra = rb;
                rb = temp;
            }

            parent[rb] = ra;
            sizes[ra] += sizes[rb];
        }

        /// <summary>Quantized UV grid coordinate (the splitter's weld grid).</summary>
        private readonly struct QuantizedUv
        {
            public readonly int X;
            public readonly int Y;

            public QuantizedUv(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        /// <summary>
        /// Stable integer edge key: the ordered (lexicographic) pair of quantized UV
        /// endpoints, so either traversal direction of a shared edge yields the same key.
        /// Lexicographic endpoint ordering (not component-wise min/max, which would
        /// collide distinct edges like {(1,5),(2,3)} and {(1,3),(2,5)}).
        /// </summary>
        private readonly struct UvEdgeKey : IEquatable<UvEdgeKey>
        {
            private readonly int _ax;
            private readonly int _ay;
            private readonly int _bx;
            private readonly int _by;

            public UvEdgeKey(QuantizedUv a, QuantizedUv b)
            {
                if (a.X > b.X || (a.X == b.X && a.Y > b.Y))
                {
                    QuantizedUv temp = a;
                    a = b;
                    b = temp;
                }

                _ax = a.X;
                _ay = a.Y;
                _bx = b.X;
                _by = b.Y;
            }

            public bool Equals(UvEdgeKey other)
            {
                return _ax == other._ax && _ay == other._ay && _bx == other._bx && _by == other._by;
            }

            public override bool Equals(object obj)
            {
                return obj is UvEdgeKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _ax;
                    hash = (hash * kHashPrime) ^ _ay;
                    hash = (hash * kHashPrime) ^ _bx;
                    hash = (hash * kHashPrime) ^ _by;
                    return hash;
                }
            }
        }
    }
}

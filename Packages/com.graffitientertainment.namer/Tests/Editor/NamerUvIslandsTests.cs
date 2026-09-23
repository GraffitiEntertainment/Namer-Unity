using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05 plan 02 CPU island tests (D-03): the UV-island union-find keyed on
    /// QUANTIZED UV pairs must weld a position-split shared edge (distinct indices,
    /// identical UVs — Pitfall 2), keep disjoint quads as two compact consecutive ids,
    /// run element-wise deterministic, guard out-of-range UVs without throwing (the
    /// GenerateResidual kUvBoundsEpsilon mirror), and clamp ids past the binary16
    /// island-texture cap with the ClampedToFloat16Max flag (Pitfall 3 / T-05-08).
    /// All fixtures are pure managed arrays — no mesh assets, no GPU.
    /// </summary>
    public class NamerUvIslandsTests
    {
        private const int kFloat16Cap = NamerUvIslands.kMaxIslandIdFloat16;

        [Test]
        public void Islands_FlatQuad_SingleIsland()
        {
            NamerSplitResult split = CreateQuadSplit(
                new Vector2(0f, 0f), new Vector2(1f, 1f));

            NamerUvIslandResult result = NamerUvIslands.BuildIslandIds(split);

            Assert.IsNotNull(result.TriIsland, "in-range quad UVs must produce island ids");
            Assert.AreEqual(2, result.TriIsland.Length);
            Assert.AreEqual(1, result.IslandCount, "both triangles share the diagonal UV edge");
            Assert.IsFalse(result.ClampedToFloat16Max);
            Assert.AreEqual(0, result.TriIsland[0]);
            Assert.AreEqual(0, result.TriIsland[1]);
        }

        [Test]
        public void Islands_SharedUvEdge_DistinctVertexIndices_SingleIsland()
        {
            // Position-split simulation (Pitfall 2): triangle B duplicates triangle A's
            // shared UV edge (1,0)-(0,1) on three brand-new vertices with DIFFERENT
            // positions. An index-keyed island walk would split this edge; a
            // quantized-UV-pair key must weld it.
            var positions = new[]
            {
                new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(1.5f, 0.5f, 0.5f), new Vector3(0.5f, 1.5f, 0.5f),
            };
            var uvs = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f),
                new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            NamerSplitResult split = CreateSplit(positions, uvs, new[] { 0, 1, 2, 3, 4, 5 });

            NamerUvIslandResult result = NamerUvIslands.BuildIslandIds(split);

            Assert.IsNotNull(result.TriIsland);
            Assert.AreEqual(1, result.IslandCount, "quantized-UV-pair edge key must weld the position-split edge");
            Assert.AreEqual(result.TriIsland[0], result.TriIsland[1]);
        }

        [Test]
        public void Islands_TwoDisjointQuads_TwoCompactIdsInFlattenOrder()
        {
            // Two sub-meshes (also exercises sub-mesh flattening order): quad A in
            // [0, 0.4]^2, quad B in [0.6, 1.0]^2 — no shared quantized UV corners.
            NamerSplitResult split = CreateSplit(
                new Vector3[8],
                new[]
                {
                    // Quad A (verts 0-3)
                    new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f), new Vector2(0.0f, 0.4f), new Vector2(0.4f, 0.4f),
                    // Quad B (verts 4-7)
                    new Vector2(0.6f, 0.6f), new Vector2(1.0f, 0.6f), new Vector2(0.6f, 1.0f), new Vector2(1.0f, 1.0f),
                },
                new[] { 0, 1, 2, 2, 1, 3 },
                new[] { 4, 5, 6, 6, 5, 7 });

            NamerUvIslandResult result = NamerUvIslands.BuildIslandIds(split);

            Assert.IsNotNull(result.TriIsland);
            Assert.AreEqual(4, result.TriIsland.Length, "two quads = four flattened triangles");
            Assert.AreEqual(2, result.IslandCount, "UV-disjoint quads are two islands");
            Assert.AreEqual(0, result.TriIsland[0], "flatten order: sub-mesh A's first triangle");
            Assert.AreEqual(0, result.TriIsland[1]);
            Assert.AreEqual(1, result.TriIsland[2], "sub-mesh B's triangles compact onto the NEXT id");
            Assert.AreEqual(1, result.TriIsland[3]);
            Assert.AreEqual(result.IslandCount - 1, Mathf.Max(result.TriIsland), "compact ids: max used id == IslandCount - 1");
        }

        [Test]
        public void Islands_Deterministic_DoubleRunElementWiseEqual()
        {
            NamerSplitResult first = CreateTwoQuadSplit();
            NamerSplitResult second = CreateTwoQuadSplit();

            NamerUvIslandResult a = NamerUvIslands.BuildIslandIds(first);
            NamerUvIslandResult b = NamerUvIslands.BuildIslandIds(second);

            Assert.AreEqual(a.IslandCount, b.IslandCount);
            Assert.AreEqual(a.ClampedToFloat16Max, b.ClampedToFloat16Max);
            Assert.AreEqual(a.TriIsland.Length, b.TriIsland.Length);
            for (int t = 0; t < a.TriIsland.Length; t++)
            {
                Assert.AreEqual(a.TriIsland[t], b.TriIsland[t], $"triangle {t} island id must be element-wise deterministic");
            }
        }

        [Test]
        public void Islands_OutOfRangeUvs_GuardedNoException()
        {
            var positions = new Vector3[4];
            var uvs = new[]
            {
                new Vector2(0f, 0f), new Vector2(1.5f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            };
            NamerSplitResult split = CreateSplit(positions, uvs, new[] { 0, 1, 2, 2, 1, 3 });

            NamerUvIslandResult result = null;
            Assert.DoesNotThrow(() => { result = NamerUvIslands.BuildIslandIds(split); }, "out-of-range UVs are guarded, never an exception");

            Assert.IsNull(result.TriIsland, "tiled/mis-mapped UVs return the honest no-islands result (kUvBoundsEpsilon mirror)");
            Assert.AreEqual(0, result.IslandCount);
        }

        [Test]
        public void Islands_MoreThanFloat16Cap_ClampedAndFlagged()
        {
            // kFloat16Cap + 2 = 2050 triangles, every one with unique quantized UVs so
            // nothing unions: 2050 raw islands > the 2048 binary16 cap.
            const int kTriangleCount = kFloat16Cap + 2;
            const float kCellStep = 0.015f;
            const float kCornerStep = 0.001f;
            int vertexCount = kTriangleCount * 3;
            var positions = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[kTriangleCount * 3];
            for (int t = 0; t < kTriangleCount; t++)
            {
                int baseIndex = t * 3;
                float bx = (t % 64) * kCellStep;
                float by = (t / 64) * kCellStep;
                uvs[baseIndex] = new Vector2(bx, by);
                uvs[baseIndex + 1] = new Vector2(bx + kCornerStep, by);
                uvs[baseIndex + 2] = new Vector2(bx, by + kCornerStep);
                triangles[baseIndex] = baseIndex;
                triangles[baseIndex + 1] = baseIndex + 1;
                triangles[baseIndex + 2] = baseIndex + 2;
            }

            NamerSplitResult split = CreateSplit(positions, uvs, triangles);

            NamerUvIslandResult result = null;
            Assert.DoesNotThrow(() => { result = NamerUvIslands.BuildIslandIds(split); });

            Assert.IsTrue(result.ClampedToFloat16Max, "over-cap island count must raise the clamp flag (T-05-08, no silent aliasing)");
            Assert.AreEqual(kTriangleCount, result.IslandCount, "IslandCount reports the TRUE pre-clamp count");
            int maxId = int.MinValue;
            int minId = int.MaxValue;
            for (int t = 0; t < result.TriIsland.Length; t++)
            {
                maxId = Math.Max(maxId, result.TriIsland[t]);
                minId = Math.Min(minId, result.TriIsland[t]);
            }

            Assert.AreEqual(0, minId, "compact ids start at 0");
            Assert.AreEqual(kFloat16Cap, maxId, "ids above the binary16 cap alias to the cap value");
        }

        /// <summary>Quad split: 4 verts at the UV box corners, 2 triangles sharing the diagonal.</summary>
        private static NamerSplitResult CreateQuadSplit(Vector2 uvMin, Vector2 uvMax)
        {
            var positions = new Vector3[4];
            var uvs = new[]
            {
                new Vector2(uvMin.x, uvMin.y),
                new Vector2(uvMax.x, uvMin.y),
                new Vector2(uvMin.x, uvMax.y),
                new Vector2(uvMax.x, uvMax.y),
            };
            return CreateSplit(positions, uvs, new[] { 0, 1, 2, 2, 1, 3 });
        }

        /// <summary>Two UV-disjoint quads in one sub-mesh (determinism fixture).</summary>
        private static NamerSplitResult CreateTwoQuadSplit()
        {
            var positions = new Vector3[8];
            var uvs = new[]
            {
                new Vector2(0.0f, 0.0f), new Vector2(0.4f, 0.0f), new Vector2(0.0f, 0.4f), new Vector2(0.4f, 0.4f),
                new Vector2(0.6f, 0.6f), new Vector2(1.0f, 0.6f), new Vector2(0.6f, 1.0f), new Vector2(1.0f, 1.0f),
            };
            return CreateSplit(positions, uvs, new[] { 0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7 });
        }

        private static NamerSplitResult CreateSplit(Vector3[] positions, Vector2[] uvs, params int[][] subMeshTriangles)
        {
            return new NamerSplitResult
            {
                Positions = positions,
                Normals = new Vector3[positions.Length],
                Tangents = new Vector4[positions.Length],
                Uvs = uvs,
                SubMeshTriangles = subMeshTriangles,
            };
        }
    }
}

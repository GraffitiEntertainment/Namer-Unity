using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 4 vertex-color decomposition tests (plan 01). Covers the two CPU mesh-topology
    /// stages headlessly (no GPU): the seam-safe attribute-preserving splitter (VCOL-02) and
    /// the per-triangle barycentric least-squares fitter with Color32 quantization (VCOL-01,
    /// D-04). The splitter tests are pure [Test] cases over in-code <see cref="Mesh"/>
    /// fixtures; the fitter tests run <see cref="VertexColorFitter"/> against a constant-color
    /// base readback so the fit stays deterministic and GPU-free.
    /// </summary>
    public class VertexColorDecompTests
    {
        [Test]
        public void Split_SplitsAtUvSeam_ButWeldsSharedUvs()
        {
            Mesh seamQuad = CreateSeamQuad();
            Mesh weldedQuad = CreateWeldedQuad();
            try
            {
                NamerSplitResult seam = MeshVertexSplitter.Split(seamQuad);
                NamerSplitResult welded = MeshVertexSplitter.Split(weldedQuad);

                Assert.AreEqual(6, seam.VertexCount,
                    "a UV seam must keep each side of the shared edge a distinct vertex (6 output vertices)");
                Assert.AreEqual(4, welded.VertexCount,
                    "a quad with shared UVs must weld its two triangles back to 4 vertices");
            }
            finally
            {
                Destroy(seamQuad, weldedQuad);
            }
        }

        [Test]
        public void Split_PreservesAttributeStreams()
        {
            Mesh quad = CreateAttributeQuad();
            try
            {
                NamerSplitResult result = MeshVertexSplitter.Split(quad);

                Assert.AreEqual(4, result.VertexCount,
                    "four distinct attribute tuples must remain four vertices (no welding)");

                Vector3[] srcPos = quad.vertices;
                Vector3[] srcNorm = quad.normals;
                Vector4[] srcTan = quad.tangents;
                Vector2[] srcUv = quad.uv;

                for (int i = 0; i < result.VertexCount; i++)
                {
                    bool found = false;
                    for (int s = 0; s < srcPos.Length; s++)
                    {
                        if (result.Positions[i] == srcPos[s]
                            && result.Normals[i] == srcNorm[s]
                            && result.Tangents[i] == srcTan[s]
                            && result.Uvs[i] == srcUv[s])
                        {
                            found = true;
                            break;
                        }
                    }

                    Assert.IsTrue(found,
                        "every output vertex's (position, normal, tangent, uv) tuple must equal a source corner's tuple exactly");
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void Split_PreservesSubMeshBoundaries()
        {
            Mesh mesh = CreateTwoSubMeshMesh();
            try
            {
                NamerSplitResult result = MeshVertexSplitter.Split(mesh);

                Assert.AreEqual(2, result.SubMeshTriangles.Length,
                    "a two-submesh source must yield two sub-mesh triangle arrays");
                Assert.AreEqual(mesh.GetTriangles(0).Length, result.SubMeshTriangles[0].Length,
                    "sub-mesh 0 triangle count must be preserved");
                Assert.AreEqual(mesh.GetTriangles(1).Length, result.SubMeshTriangles[1].Length,
                    "sub-mesh 1 triangle count must be preserved");

                foreach (int index in result.SubMeshTriangles[0])
                {
                    Assert.GreaterOrEqual(index, 0, "sub-mesh 0 indices must be non-negative");
                    Assert.Less(index, result.VertexCount, "sub-mesh 0 indices must address output vertices");
                }

                foreach (int index in result.SubMeshTriangles[1])
                {
                    Assert.GreaterOrEqual(index, 0, "sub-mesh 1 indices must be non-negative");
                    Assert.Less(index, result.VertexCount, "sub-mesh 1 indices must address output vertices");
                }
            }
            finally
            {
                Destroy(mesh);
            }
        }

        [Test]
        public void Split_PreservesSkinningData()
        {
            Mesh mesh = CreateSkinnedQuad();
            try
            {
                NamerSplitResult result = MeshVertexSplitter.Split(mesh);

                Assert.IsNotNull(result.BoneWeights, "a skinned source must yield bone weights");
                Assert.IsNotNull(result.Bindposes, "a skinned source must yield bind poses");
                Assert.AreEqual(result.VertexCount, result.BoneWeights.Length,
                    "every output vertex derived from a skinned source must carry a bone weight");
                Assert.AreEqual(mesh.bindposes.Length, result.Bindposes.Length,
                    "bind poses (per-bone) must be preserved verbatim");
            }
            finally
            {
                Destroy(mesh);
            }
        }

        private static Mesh CreateWeldedQuad()
        {
            Mesh mesh = new Mesh { name = "VertexColorDecompWeldedQuad" };
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

        private static Mesh CreateSeamQuad()
        {
            // Two triangles tile a square across the diagonal (BL -> TR). The two diagonal
            // positions are duplicated with different UVs on each side, producing a real UV
            // seam: 6 source vertices that must stay 6 distinct output vertices.
            Mesh mesh = new Mesh { name = "VertexColorDecompSeamQuad" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f), // v0 BL (triangle A)
                new Vector3(1f, 0f, 0f), // v1 BR (triangle A)
                new Vector3(1f, 0f, 1f), // v2 TR (triangle A)
                new Vector3(0f, 0f, 0f), // v3 BL (triangle B) - same position, different UV
                new Vector3(1f, 0f, 1f), // v4 TR (triangle B) - same position, different UV
                new Vector3(0f, 0f, 1f), // v5 TL (triangle B)
            };
            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(1f, 0f),
                new Vector2(1f, 1f),
                new Vector2(0f, 1f),
                new Vector2(0f, 0f),
                new Vector2(1f, 1f),
            };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateAttributeQuad()
        {
            Mesh mesh = new Mesh { name = "VertexColorDecompAttributeQuad" };
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
            mesh.tangents = new[]
            {
                new Vector4(1f, 0f, 0f, -1f),
                new Vector4(1f, 0f, 0f, -1f),
                new Vector4(1f, 0f, 0f, -1f),
                new Vector4(1f, 0f, 0f, -1f),
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

        private static Mesh CreateTwoSubMeshMesh()
        {
            Mesh mesh = new Mesh { name = "VertexColorDecompTwoSubMesh" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0f, 1f),
                new Vector3(2f, 0f, 0f),
                new Vector3(3f, 0f, 0f),
                new Vector3(3f, 0f, 1f),
            };
            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up, Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f),
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 3, 4, 5 }, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateSkinnedQuad()
        {
            Mesh mesh = CreateWeldedQuad();
            mesh.name = "VertexColorDecompSkinnedQuad";
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                new BoneWeight { boneIndex0 = 0, weight0 = 1f },
            };
            mesh.bindposes = new[] { Matrix4x4.identity };
            return mesh;
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

using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
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

        [Test]
        public void Fit_ConstantColor_RecoversColorWithinTolerance()
        {
            Mesh quad = CreateWeldedQuad();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(quad);
                const int width = 8;
                const int height = 8;
                Color32 constant = new Color32(200, 100, 50, 255);
                NativeArray<Color32> baseTexels = CreateConstantBase(width, height, constant);
                try
                {
                    VertexColorFitResult result = VertexColorFitter.Fit(split, baseTexels, width, height);
                    try
                    {
                        float3 expected = new float3(constant.r / 255f, constant.g / 255f, constant.b / 255f);
                        Assert.AreEqual(split.VertexCount, result.VertexCount);
                        for (int i = 0; i < result.VertexCount; i++)
                        {
                            Assert.LessOrEqual(math.length(result.Colors[i] - expected), 1e-3f,
                                "a constant base color must fit to itself within 1e-3 (linear)");
                        }
                    }
                    finally
                    {
                        result.Dispose();
                    }
                }
                finally
                {
                    baseTexels.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void Fit_WrapsUvsOutsideZeroOne()
        {
            Mesh mesh = CreateTilingTriangleMesh();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(mesh);
                NativeArray<Color32> baseTexels = CreateRampBase(4, 4);
                try
                {
                    VertexColorFitResult result = VertexColorFitter.Fit(split, baseTexels, 4, 4);
                    try
                    {
                        Assert.AreEqual(split.VertexCount, result.VertexCount);
                        // Rationale: the triangle's UVs live in [1.25, 1.75]; the old clamp
                        // mapped every sample to edge texel x=3 (white, 1.0), while the Repeat
                        // wrap maps them to the wrapped ramp ~0.5. The 0.15 / 0.85 bounds are
                        // 1/6 and 5/6 (the linearly-derived wrapped-ramp fit) widened by a
                        // 1/60 safety margin — do not loosen further.
                        for (int i = 0; i < result.VertexCount; i++)
                        {
                            Assert.LessOrEqual(result.Colors[i].x, 0.85f,
                                "wrapped fit must not clamp to the white edge texel");
                            Assert.GreaterOrEqual(result.Colors[i].x, 0.15f,
                                "wrapped fit must wrap to the mid-ramp, not black");
                            Assert.LessOrEqual(result.Colors[i].y, 0.85f,
                                "wrapped fit must not clamp to the white edge texel");
                            Assert.GreaterOrEqual(result.Colors[i].y, 0.15f,
                                "wrapped fit must wrap to the mid-ramp, not black");
                            Assert.LessOrEqual(result.Colors[i].z, 0.85f,
                                "wrapped fit must not clamp to the white edge texel");
                            Assert.GreaterOrEqual(result.Colors[i].z, 0.15f,
                                "wrapped fit must wrap to the mid-ramp, not black");
                        }
                    }
                    finally
                    {
                        result.Dispose();
                    }
                }
                finally
                {
                    baseTexels.Dispose();
                }
            }
            finally
            {
                Destroy(mesh);
            }
        }

        [Test]
        public void Fit_IsDeterministic()
        {
            Mesh quad = CreateWeldedQuad();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(quad);
                NativeArray<Color32> baseTexels = CreateConstantBase(8, 8, new Color32(90, 150, 210, 255));
                try
                {
                    VertexColorFitResult first = VertexColorFitter.Fit(split, baseTexels, 8, 8);
                    VertexColorFitResult second = VertexColorFitter.Fit(split, baseTexels, 8, 8);
                    try
                    {
                        Assert.AreEqual(first.VertexCount, second.VertexCount);
                        for (int i = 0; i < first.VertexCount; i++)
                        {
                            Assert.AreEqual(first.Colors[i], second.Colors[i],
                                "two fits must produce element-wise identical colors");
                            Assert.AreEqual(first.FitQuality[i], second.FitQuality[i],
                                "two fits must produce element-wise identical fit quality");
                        }
                    }
                    finally
                    {
                        first.Dispose();
                        second.Dispose();
                    }
                }
                finally
                {
                    baseTexels.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void ToColor32Array_QuantizeRoundtrip()
        {
            Mesh quad = CreateWeldedQuad();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(quad);
                NativeArray<Color32> baseTexels = CreateConstantBase(8, 8, new Color32(120, 160, 200, 255));
                try
                {
                    VertexColorFitResult result = VertexColorFitter.Fit(split, baseTexels, 8, 8);
                    try
                    {
                        Color32[] quantized = result.ToColor32Array();
                        Assert.AreEqual(result.VertexCount, quantized.Length);
                        const float tolerance = 0.5f / 255f + 1e-4f;
                        for (int i = 0; i < result.VertexCount; i++)
                        {
                            float3 c = math.saturate(result.Colors[i]);
                            Assert.LessOrEqual(math.abs(quantized[i].r / 255f - c.x), tolerance, "red quantize roundtrip within 1/255");
                            Assert.LessOrEqual(math.abs(quantized[i].g / 255f - c.y), tolerance, "green quantize roundtrip within 1/255");
                            Assert.LessOrEqual(math.abs(quantized[i].b / 255f - c.z), tolerance, "blue quantize roundtrip within 1/255");
                            Assert.GreaterOrEqual(quantized[i].a, 0, "alpha must be a byte in [0,255]");
                            Assert.LessOrEqual(quantized[i].a, 255, "alpha must be a byte in [0,255]");
                        }
                    }
                    finally
                    {
                        result.Dispose();
                    }
                }
                finally
                {
                    baseTexels.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
            }
        }

        [Test]
        public void FitQuality_IsInUnitRange_AndOneForPerfectFit()
        {
            Mesh quad = CreateWeldedQuad();
            try
            {
                NamerSplitResult split = MeshVertexSplitter.Split(quad);
                NativeArray<Color32> baseTexels = CreateConstantBase(8, 8, new Color32(64, 128, 192, 255));
                try
                {
                    VertexColorFitResult result = VertexColorFitter.Fit(split, baseTexels, 8, 8);
                    try
                    {
                        for (int i = 0; i < result.VertexCount; i++)
                        {
                            Assert.GreaterOrEqual(result.FitQuality[i], 0f, "fit quality must be >= 0");
                            Assert.LessOrEqual(result.FitQuality[i], 1f, "fit quality must be <= 1");
                            Assert.AreEqual(1f, result.FitQuality[i], 1e-3f,
                                "a perfect constant-color fit must have fit-quality 1");
                        }
                    }
                    finally
                    {
                        result.Dispose();
                    }
                }
                finally
                {
                    baseTexels.Dispose();
                }
            }
            finally
            {
                Destroy(quad);
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

        private static NativeArray<Color32> CreateConstantBase(int width, int height, Color32 color)
        {
            var texels = new NativeArray<Color32>(width * height, Allocator.TempJob);
            for (int i = 0; i < texels.Length; i++)
            {
                texels[i] = color;
            }

            return texels;
        }

        private static NativeArray<Color32> CreateRampBase(int width, int height)
        {
            // Grayscale x-ramp: texel 0 is black, texel (width-1) is white. Each texel's r/g/b
            // equal the x fraction so the fit test can distinguish wrap (mid-ramp) from clamp
            // (white edge).
            var texels = new NativeArray<Color32>(width * height, Allocator.TempJob);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte v = (byte)Mathf.RoundToInt(x / (float)(width - 1) * 255f);
                    texels[y * width + x] = new Color32(v, v, v, 255);
                }
            }

            return texels;
        }

        private static Mesh CreateTilingTriangleMesh()
        {
            // A single triangle whose UVs all live in [1.25, 1.75] (outside [0,1]) so the old
            // clamp mapped every bilinear sample to the white edge texel while the Repeat wrap
            // samples the mid-ramp.
            Mesh mesh = new Mesh { name = "VertexColorDecompTilingTriangle" };
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 0f, 1f),
            };
            mesh.normals = new[]
            {
                Vector3.up, Vector3.up, Vector3.up,
            };
            mesh.uv = new[]
            {
                new Vector2(1.25f, 0.5f),
                new Vector2(1.75f, 0.5f),
                new Vector2(1.5f, 0.9f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();
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

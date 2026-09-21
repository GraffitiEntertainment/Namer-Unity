using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// The in-memory result of a seam-safe vertex split. One output vertex exists per
    /// unique weld key — the tuple <c>(position, normal, tangent, uv)</c> — so every
    /// UV-seam side, hard-normal edge, tangent break, and split position becomes a
    /// distinct vertex (VCOL-02). All attribute streams are re-emitted at the output
    /// indices and each sub-mesh's triangles are rewritten against the new indices,
    /// preserving sub-mesh boundaries (material slots) and, for skinned meshes, the
    /// <see cref="BoneWeights"/> / <see cref="Bindposes"/> streams.
    ///
    /// Pure in-memory: the splitter returns managed arrays only and never writes to disk
    /// (<see cref="AssetGenerator"/> remains the sole disk writer for plan 04-03).
    /// </summary>
    public sealed class NamerSplitResult
    {
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Vector2[] Uvs;

        /// <summary>Static lightmap UVs (uv2 / TEXCOORD1); <c>null</c> when the source mesh has none.</summary>
        public Vector2[] Uv2;

        /// <summary>Dynamic lightmap UVs (uv3 / TEXCOORD2); <c>null</c> when the source mesh has none.</summary>
        public Vector2[] Uv3;

        /// <summary>One index array per sub-mesh; indices address <see cref="Positions"/>.</summary>
        public int[][] SubMeshTriangles;
        /// <summary>Per-output-vertex influence counts; <c>null</c> when the source mesh is not skinned.</summary>
        public byte[] BonesPerVertex;

        /// <summary>Variable-count influence stream — <see cref="BoneWeight1"/> runs indexed by
        /// <see cref="BonesPerVertex"/> — so meshes with more than four influences per vertex
        /// survive the split (the legacy <c>boneWeights</c> accessor truncates to four).
        /// <c>null</c> when the source mesh is not skinned.</summary>
        public BoneWeight1[] BoneWeights;
        /// <summary><c>null</c> when the source mesh is not skinned.</summary>
        public Matrix4x4[] Bindposes;

        public int VertexCount => Positions != null ? Positions.Length : 0;
    }

    /// <summary>
    /// Seam-safe, attribute-preserving vertex split (Phase 4, plan 01 — VCOL-02). Reads a
    /// source mesh on the main thread exactly like <see cref="NamerAOBaker.Bake"/>, welds
    /// corners by a quantized <c>(position, normal, tangent, uv)</c> integer key (never
    /// floating-point equality), and returns a <see cref="NamerSplitResult"/> whose output
    /// vertices carry the source attributes verbatim. Missing normals fall back to a copy of
    /// <see cref="NamerAOBaker"/>'s smooth-normal computation; blend shapes are intentionally
    /// skipped for MVP (out of scope, never a hard failure).
    /// </summary>
    public static class MeshVertexSplitter
    {
        // Quantization epsilons turn the continuous (position, normal, tangent, uv) tuple into
        // a stable integer weld key. Near-equal attributes on a shared edge weld; genuinely
        // different attributes (a UV seam, a hard normal, a tangent break) split. These are
        // deliberately coarser than the fitter's barycentric epsilons — they answer "are these
        // the same corner" rather than "is this triangle degenerate".
        private const float kPositionQuantize = 1e-4f;
        private const float kNormalQuantize = 1e-3f;
        private const float kTangentQuantize = 1e-3f;
        private const float kUvQuantize = 1e-4f;

        /// <summary>
        /// Splits <paramref name="sourceMesh"/> into seam-safe vertices. The source mesh is
        /// only read, never mutated. Returns a new <see cref="NamerSplitResult"/>; the caller
        /// owns the returned managed arrays (no <see cref="Unity.Collections.NativeArray{T}"/>
        /// lifetime coupling, so both the fitter and 04-03's generator can consume it).
        /// </summary>
        public static NamerSplitResult Split(Mesh sourceMesh)
        {
            if (sourceMesh == null)
            {
                throw new ArgumentNullException(nameof(sourceMesh));
            }

            Vector3[] positions = sourceMesh.vertices;
            if (positions == null)
            {
                throw new ArgumentException("Source mesh exposes no vertex positions.", nameof(sourceMesh));
            }

            Vector3[] normals = sourceMesh.normals;
            if (normals == null || normals.Length == 0)
            {
                normals = ComputeSmoothNormals(positions, sourceMesh.triangles);
            }

            Vector4[] tangents = sourceMesh.tangents;
            if (tangents == null || tangents.Length == 0)
            {
                tangents = new Vector4[positions.Length];
            }

            Vector2[] uvs = sourceMesh.uv;
            if (uvs == null || uvs.Length == 0)
            {
                uvs = new Vector2[positions.Length];
            }

            // Lightmap UV channels carried verbatim (uv2 -> TEXCOORD1 static, uv3 ->
            // TEXCOORD2 dynamic). The same source vertex always produces the same weld
            // key, so no key change is needed. NAMER.shader samples these under
            // LIGHTMAP_ON / DYNAMICLIGHTMAP_ON; dropping them corrupted baked lighting
            // on lightmapped renderers whose mesh got swapped (Codex PR #1 review).
            Vector2[] lightmapUvs = sourceMesh.uv2;
            Vector2[] dynamicLightmapUvs = sourceMesh.uv3;
            bool hasLightmapUvs = lightmapUvs != null && lightmapUvs.Length == positions.Length;
            bool hasDynamicLightmapUvs = dynamicLightmapUvs != null && dynamicLightmapUvs.Length == positions.Length;

            // Variable-count influence stream: the legacy boneWeights accessor truncates to
            // four influences per vertex, silently deforming meshes authored with more
            // (Codex PR #1 review). The native reads are copied to managed arrays and
            // disposed immediately — the result stays free of native lifetime coupling.
            byte[] srcBonesPerVertex;
            BoneWeight1[] srcBoneWeights;
            using (NativeArray<byte> nativeBonesPerVertex = sourceMesh.GetBonesPerVertex(Allocator.Temp))
            using (NativeArray<BoneWeight1> nativeBoneWeights = sourceMesh.GetAllBoneWeights(Allocator.Temp))
            {
                srcBonesPerVertex = nativeBonesPerVertex.ToArray();
                srcBoneWeights = nativeBoneWeights.ToArray();
            }

            Matrix4x4[] bindposes = sourceMesh.bindposes;
            bool skinned = srcBonesPerVertex.Length == positions.Length && srcBoneWeights.Length > 0;

            // Influence runs are indexed by cumulative per-vertex offsets; the weld key is
            // unchanged because a source vertex's full influence run is constant.
            int[] influenceOffsets = new int[positions.Length + 1];
            for (int v = 0; v < positions.Length; v++)
            {
                influenceOffsets[v + 1] = influenceOffsets[v] + (v < srcBonesPerVertex.Length ? srcBonesPerVertex[v] : 0);
            }

            int subMeshCount = sourceMesh.subMeshCount;

            // Weld by a quantized attribute key: one output vertex per unique tuple. The
            // dictionary maps a source corner's attribute tuple to its output index; because
            // every source vertex carries a single (position, normal, tangent, uv), two corners
            // that reference the same source vertex always produce the same key.
            var weldMap = new Dictionary<WeldKey, int>();
            var outPositions = new List<Vector3>();
            var outNormals = new List<Vector3>();
            var outTangents = new List<Vector4>();
            var outUvs = new List<Vector2>();
            List<Vector2> outLightmapUvs = hasLightmapUvs ? new List<Vector2>() : null;
            List<Vector2> outDynamicLightmapUvs = hasDynamicLightmapUvs ? new List<Vector2>() : null;
            var outBoneWeights = skinned ? new List<BoneWeight1>() : null;
            var outBonesPerVertex = skinned ? new List<byte>() : null;

            var subMeshes = new int[subMeshCount][];
            for (int sub = 0; sub < subMeshCount; sub++)
            {
                int[] srcTriangles = sourceMesh.GetTriangles(sub);
                int[] outTriangles = new int[srcTriangles.Length];
                for (int i = 0; i < srcTriangles.Length; i++)
                {
                    int srcVertex = srcTriangles[i];
                    var key = new WeldKey(positions[srcVertex], normals[srcVertex], tangents[srcVertex], uvs[srcVertex]);
                    if (!weldMap.TryGetValue(key, out int outVertex))
                    {
                        outVertex = outPositions.Count;
                        weldMap.Add(key, outVertex);
                        outPositions.Add(positions[srcVertex]);
                        outNormals.Add(normals[srcVertex]);
                        outTangents.Add(tangents[srcVertex]);
                        outUvs.Add(uvs[srcVertex]);
                        if (hasLightmapUvs)
                        {
                            outLightmapUvs.Add(lightmapUvs[srcVertex]);
                        }

                        if (hasDynamicLightmapUvs)
                        {
                            outDynamicLightmapUvs.Add(dynamicLightmapUvs[srcVertex]);
                        }

                        if (skinned)
                        {
                            outBonesPerVertex.Add(srcBonesPerVertex[srcVertex]);
                            for (int b = influenceOffsets[srcVertex]; b < influenceOffsets[srcVertex + 1]; b++)
                            {
                                outBoneWeights.Add(srcBoneWeights[b]);
                            }
                        }
                    }

                    outTriangles[i] = outVertex;
                }

                subMeshes[sub] = outTriangles;
            }

            return new NamerSplitResult
            {
                Positions = outPositions.ToArray(),
                Normals = outNormals.ToArray(),
                Tangents = outTangents.ToArray(),
                Uvs = outUvs.ToArray(),
                Uv2 = hasLightmapUvs ? outLightmapUvs.ToArray() : null,
                Uv3 = hasDynamicLightmapUvs ? outDynamicLightmapUvs.ToArray() : null,
                SubMeshTriangles = subMeshes,
                BonesPerVertex = skinned ? outBonesPerVertex.ToArray() : null,
                BoneWeights = skinned ? outBoneWeights.ToArray() : null,
                Bindposes = skinned ? bindposes : null,
            };
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

        private static int Quantize(float value, float epsilon)
        {
            return Mathf.RoundToInt(value / epsilon);
        }

        /// <summary>
        /// Stable integer weld key over <c>(position, normal, tangent, uv)</c>. Quantization
        /// makes near-equal floats compare equal without any floating-point tolerance in
        /// <see cref="Equals"/>, mirroring the deterministic-float philosophy of
        /// <see cref="NamerAOBaker"/>.
        /// </summary>
        private readonly struct WeldKey : IEquatable<WeldKey>
        {
            private readonly int _px;
            private readonly int _py;
            private readonly int _pz;
            private readonly int _nx;
            private readonly int _ny;
            private readonly int _nz;
            private readonly int _tx;
            private readonly int _ty;
            private readonly int _tz;
            private readonly int _tw;
            private readonly int _ux;
            private readonly int _uy;

            public WeldKey(Vector3 position, Vector3 normal, Vector4 tangent, Vector2 uv)
            {
                _px = Quantize(position.x, kPositionQuantize);
                _py = Quantize(position.y, kPositionQuantize);
                _pz = Quantize(position.z, kPositionQuantize);
                _nx = Quantize(normal.x, kNormalQuantize);
                _ny = Quantize(normal.y, kNormalQuantize);
                _nz = Quantize(normal.z, kNormalQuantize);
                _tx = Quantize(tangent.x, kTangentQuantize);
                _ty = Quantize(tangent.y, kTangentQuantize);
                _tz = Quantize(tangent.z, kTangentQuantize);
                _tw = Quantize(tangent.w, kTangentQuantize);
                _ux = Quantize(uv.x, kUvQuantize);
                _uy = Quantize(uv.y, kUvQuantize);
            }

            public bool Equals(WeldKey other)
            {
                return _px == other._px
                    && _py == other._py
                    && _pz == other._pz
                    && _nx == other._nx
                    && _ny == other._ny
                    && _nz == other._nz
                    && _tx == other._tx
                    && _ty == other._ty
                    && _tz == other._tz
                    && _tw == other._tw
                    && _ux == other._ux
                    && _uy == other._uy;
            }

            public override bool Equals(object obj)
            {
                return obj is WeldKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = _px;
                    hash = (hash * 397) ^ _py;
                    hash = (hash * 397) ^ _pz;
                    hash = (hash * 397) ^ _nx;
                    hash = (hash * 397) ^ _ny;
                    hash = (hash * 397) ^ _nz;
                    hash = (hash * 397) ^ _tx;
                    hash = (hash * 397) ^ _ty;
                    hash = (hash * 397) ^ _tz;
                    hash = (hash * 397) ^ _tw;
                    hash = (hash * 397) ^ _ux;
                    hash = (hash * 397) ^ _uy;
                    return hash;
                }
            }
        }
    }
}

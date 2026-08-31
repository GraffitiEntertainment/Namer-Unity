using System;
using System.Collections.Generic;
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
        /// <summary>One index array per sub-mesh; indices address <see cref="Positions"/>.</summary>
        public int[][] SubMeshTriangles;
        /// <summary><c>null</c> when the source mesh is not skinned.</summary>
        public BoneWeight[] BoneWeights;
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

            BoneWeight[] boneWeights = sourceMesh.boneWeights;
            Matrix4x4[] bindposes = sourceMesh.bindposes;
            bool skinned = boneWeights != null && boneWeights.Length > 0;

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
            var outBoneWeights = skinned ? new List<BoneWeight>() : null;

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
                        if (skinned)
                        {
                            outBoneWeights.Add(boneWeights[srcVertex]);
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
                SubMeshTriangles = subMeshes,
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

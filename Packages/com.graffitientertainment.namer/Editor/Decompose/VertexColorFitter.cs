using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// In-memory result of the vertex-color least-squares fit. <see cref="Colors"/> is a
    /// per-vertex float3 fit in linear RGB and <see cref="FitQuality"/> a per-vertex [0,1]
    /// fit-confidence signal (D-04). The caller owns both and must <see cref="Dispose"/> them
    /// (mirrors <see cref="NamerAOBakeResult"/> ownership). <see cref="ToColor32Array"/> is
    /// the quantized representation the residual quotient (plan 04-02) and the mesh
    /// <c>colors32</c> write (plan 04-03) consume — the residual MUST derive from these
    /// quantized values, not the float fit (Pitfall 2).
    /// </summary>
    public sealed class VertexColorFitResult : IDisposable
    {
        public NativeArray<float3> Colors;
        public NativeArray<float> FitQuality;
        public int VertexCount;

        private bool _disposed;

        public VertexColorFitResult(NativeArray<float3> colors, NativeArray<float> fitQuality, int vertexCount)
        {
            Colors = colors;
            FitQuality = fitQuality;
            VertexCount = vertexCount;
        }

        /// <summary>
        /// Quantizes the fit to <see cref="Color32"/>: RGB = <c>round(saturate(color) * 255)</c>
        /// and A = <c>round(saturate(fitQuality) * 255)</c>. This is the exact byte stream the
        /// runtime interpolates, so the residual quotient must be derived from it.
        /// </summary>
        public Color32[] ToColor32Array()
        {
            if (!Colors.IsCreated || Colors.Length == 0)
            {
                return Array.Empty<Color32>();
            }

            bool hasQuality = FitQuality.IsCreated && FitQuality.Length == Colors.Length;
            var result = new Color32[Colors.Length];
            for (int i = 0; i < Colors.Length; i++)
            {
                float3 c = saturate(Colors[i]);
                float q = hasQuality ? saturate(FitQuality[i]) : 0f;
                result[i] = new Color32(
                    (byte)round(c.x * 255f),
                    (byte)round(c.y * 255f),
                    (byte)round(c.z * 255f),
                    (byte)round(q * 255f));
            }

            return result;
        }

        /// <summary>
        /// Releases <see cref="Colors"/> and <see cref="FitQuality"/>. Safe to call more than
        /// once (idempotent).
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (Colors.IsCreated)
            {
                Colors.Dispose();
            }

            if (FitQuality.IsCreated)
            {
                FitQuality.Dispose();
            }
        }
    }

    /// <summary>
    /// Deterministic CPU Burst per-triangle barycentric least-squares fitter (Phase 4, plan 01 —
    /// VCOL-01 / D-04). For each triangle, a fixed interior barycentric grid
    /// (<see cref="kGrid"/> samples) samples the linear base texture by UV, accumulates the
    /// triangle's symmetric 3x3 Gram matrix (6 unique entries) and 3 per-corner right-hand
    /// sides, and solves the 3-corner system with a closed-form Cramer rule (jittered for
    /// degenerate triangles). Per-vertex results are the weighted combination of every incident
    /// triangle's solve; <see cref="VertexColorFitResult.FitQuality"/> is the per-vertex mean
    /// reconstruction residual mapped through a named reference error.
    ///
    /// Pure CPU, deterministic (fixed grid + fixed float math) and headless — no GPU. Mirrors the
    /// <see cref="NamerAOBaker"/> Burst + Unity.Collections + Unity.Mathematics discipline and
    /// reuses its exact barycentric epsilon constants.
    /// </summary>
    public static class VertexColorFitter
    {
        /// <summary>Interior barycentric samples per triangle (RESEARCH A3).</summary>
        public const int kGrid = 16;

        private const float kBarycentricAreaEps = 1e-8f;
        private const float kBarycentricInsideEps = 1e-4f;
        private const float kFitQualityRef = 0.05f;
        private const float kSolveJitter = 1e-8f;
        private const float kColorByteToUnit = 1f / 255f;
        private const float kMinAccumWeight = 1e-6f;
        private const int kInnerLoopBatchCount = 64;

        /// <summary>
        /// Fits a per-vertex color to <paramref name="baseTexels"/> (linear RGBA32, row-major)
        /// against <paramref name="split"/>. The returned <see cref="VertexColorFitResult"/> owns
        /// its buffers; every intermediate allocation is disposed here.
        /// </summary>
        public static VertexColorFitResult Fit(NamerSplitResult split, NativeArray<Color32> baseTexels, int baseWidth, int baseHeight)
        {
            if (split == null)
            {
                throw new ArgumentNullException(nameof(split));
            }

            if (split.Positions == null || split.Positions.Length == 0)
            {
                throw new ArgumentException("Split result has no vertices to fit.", nameof(split));
            }

            if (baseWidth < 1 || baseHeight < 1)
            {
                throw new ArgumentException("Base texture dimensions must be positive.", nameof(baseWidth));
            }

            if (!baseTexels.IsCreated || baseTexels.Length != baseWidth * baseHeight)
            {
                throw new ArgumentException("Base texel buffer length must equal baseWidth * baseHeight.", nameof(baseTexels));
            }

            int vertexCount = split.VertexCount;

            NativeArray<float2> uvs = default;
            NativeArray<int3> tris = default;
            NativeArray<float> atA = default;
            NativeArray<float3> atB = default;
            NativeArray<float3> colorSum = default;
            NativeArray<float> weightSum = default;
            NativeArray<float3> colors = default;
            NativeArray<float> triError = default;
            NativeArray<float> fitQuality = default;

            try
            {
                // The fit is UV-space only: positions/normals/tangents are not sampled, so only
                // Uvs and the flattened sub-mesh triangles are converted (mirrors NamerAOBaker's
                // ToFloat2 / ToInt3 helpers).
                uvs = ToFloat2(split.Uvs);
                tris = FlattenTriangles(split.SubMeshTriangles);
                int triangleCount = tris.Length;

                atA = new NativeArray<float>(triangleCount * 6, Allocator.TempJob);
                atB = new NativeArray<float3>(triangleCount * 3, Allocator.TempJob);
                colorSum = new NativeArray<float3>(vertexCount, Allocator.TempJob);
                weightSum = new NativeArray<float>(vertexCount, Allocator.TempJob);
                colors = new NativeArray<float3>(vertexCount, Allocator.Persistent);
                triError = new NativeArray<float>(triangleCount, Allocator.TempJob);
                fitQuality = new NativeArray<float>(vertexCount, Allocator.Persistent);

                var accumulate = new AccumulateJob
                {
                    Uvs = uvs,
                    Tris = tris,
                    BaseTexels = baseTexels,
                    BaseWidth = baseWidth,
                    BaseHeight = baseHeight,
                    AtA = atA,
                    AtB = atB,
                };
                accumulate.Schedule(triangleCount, kInnerLoopBatchCount).Complete();

                SolveAndAccumulate(atA, atB, tris, colorSum, weightSum);

                for (int v = 0; v < vertexCount; v++)
                {
                    colors[v] = weightSum[v] > kMinAccumWeight ? colorSum[v] / weightSum[v] : 0f;
                }

                var error = new ErrorJob
                {
                    Uvs = uvs,
                    Tris = tris,
                    BaseTexels = baseTexels,
                    Colors = colors,
                    BaseWidth = baseWidth,
                    BaseHeight = baseHeight,
                    TriError = triError,
                };
                error.Schedule(triangleCount, kInnerLoopBatchCount).Complete();

                // Accumulate per-triangle reconstruction error into per-vertex bins on the main
                // thread (avoids a cross-thread read-modify-write race on shared vertices).
                float[] errorSum = new float[vertexCount];
                int[] errorCount = new int[vertexCount];
                for (int t = 0; t < triangleCount; t++)
                {
                    int3 tri = tris[t];
                    float e = triError[t];
                    errorSum[tri.x] += e;
                    errorSum[tri.y] += e;
                    errorSum[tri.z] += e;
                    errorCount[tri.x]++;
                    errorCount[tri.y]++;
                    errorCount[tri.z]++;
                }

                for (int v = 0; v < vertexCount; v++)
                {
                    float meanErr = errorCount[v] > 0 ? errorSum[v] / errorCount[v] : kFitQualityRef;
                    fitQuality[v] = saturate(1f - meanErr / kFitQualityRef);
                }

                return new VertexColorFitResult(colors, fitQuality, vertexCount);
            }
            finally
            {
                if (uvs.IsCreated) uvs.Dispose();
                if (tris.IsCreated) tris.Dispose();
                if (atA.IsCreated) atA.Dispose();
                if (atB.IsCreated) atB.Dispose();
                if (colorSum.IsCreated) colorSum.Dispose();
                if (weightSum.IsCreated) weightSum.Dispose();
                if (triError.IsCreated) triError.Dispose();
                // colors and fitQuality are intentionally NOT disposed — returned to the caller.
            }
        }

        private static void SolveAndAccumulate(
            NativeArray<float> atA,
            NativeArray<float3> atB,
            NativeArray<int3> tris,
            NativeArray<float3> colorSum,
            NativeArray<float> weightSum)
        {
            for (int t = 0; t < tris.Length; t++)
            {
                int3 tri = tris[t];
                int b6 = t * 6;
                float g00 = atA[b6 + 0];
                float g01 = atA[b6 + 1];
                float g02 = atA[b6 + 2];
                float g11 = atA[b6 + 3];
                float g12 = atA[b6 + 4];
                float g22 = atA[b6 + 5];

                int b3 = t * 3;
                float3 r0 = atB[b3 + 0];
                float3 r1 = atB[b3 + 1];
                float3 r2 = atB[b3 + 2];

                Solve3x3(g00, g01, g02, g11, g12, g22, r0.x, r1.x, r2.x, out float c0r, out float c1r, out float c2r);
                Solve3x3(g00, g01, g02, g11, g12, g22, r0.y, r1.y, r2.y, out float c0g, out float c1g, out float c2g);
                Solve3x3(g00, g01, g02, g11, g12, g22, r0.z, r1.z, r2.z, out float c0b, out float c1b, out float c2b);

                colorSum[tri.x] += new float3(c0r, c0g, c0b);
                colorSum[tri.y] += new float3(c1r, c1g, c1b);
                colorSum[tri.z] += new float3(c2r, c2g, c2b);
                weightSum[tri.x] += 1f;
                weightSum[tri.y] += 1f;
                weightSum[tri.z] += 1f;
            }
        }

        private static void Solve3x3(
            float g00, float g01, float g02, float g11, float g12, float g22,
            float b0, float b1, float b2,
            out float x0, out float x1, out float x2)
        {
            float det = g00 * (g11 * g22 - g12 * g12)
                      - g01 * (g01 * g22 - g12 * g02)
                      + g02 * (g01 * g12 - g11 * g02);

            if (abs(det) < kBarycentricAreaEps)
            {
                // Under-sampled / degenerate triangle: jitter the diagonal to keep the solve
                // non-singular (RESEARCH A6 sanctions the closed-form/Cramer fallback).
                g00 += kSolveJitter;
                g11 += kSolveJitter;
                g22 += kSolveJitter;
                det = g00 * (g11 * g22 - g12 * g12)
                    - g01 * (g01 * g22 - g12 * g02)
                    + g02 * (g01 * g12 - g11 * g02);
            }

            float invDet = 1f / det;

            x0 = (b0 * (g11 * g22 - g12 * g12)
                - g01 * (b1 * g22 - g12 * b2)
                + g02 * (b1 * g12 - g11 * b2)) * invDet;

            x1 = (g00 * (b1 * g22 - g12 * b2)
                - b0 * (g01 * g22 - g12 * g02)
                + g02 * (g01 * b2 - b1 * g02)) * invDet;

            x2 = (g00 * (g11 * b2 - g12 * b1)
                - g01 * (g01 * b2 - b1 * g02)
                + b0 * (g01 * g12 - g11 * g02)) * invDet;
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

        private static NativeArray<int3> FlattenTriangles(int[][] subMeshTriangles)
        {
            if (subMeshTriangles == null)
            {
                return new NativeArray<int3>(0, Allocator.TempJob);
            }

            int totalTriangles = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub != null)
                {
                    totalTriangles += sub.Length / 3;
                }
            }

            var tris = new NativeArray<int3>(totalTriangles, Allocator.TempJob);
            int t = 0;
            foreach (int[] sub in subMeshTriangles)
            {
                if (sub == null)
                {
                    continue;
                }

                for (int i = 0; i + 2 < sub.Length; i += 3)
                {
                    tris[t++] = new int3(sub[i], sub[i + 1], sub[i + 2]);
                }
            }

            return tris;
        }

        [BurstCompile]
        private struct AccumulateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Uvs;
            [ReadOnly] public NativeArray<int3> Tris;
            [ReadOnly] public NativeArray<Color32> BaseTexels;
            public int BaseWidth;
            public int BaseHeight;
            // Each triangle writes only its own 6 + 3 slots (disjoint per index), so the
            // parallel-for restriction is lifted for the non-trivial triIndex*N indexing.
            [WriteOnly]
            [NativeDisableParallelForRestriction]
            public NativeArray<float> AtA;   // triangleCount * 6
            [WriteOnly]
            [NativeDisableParallelForRestriction]
            public NativeArray<float3> AtB;  // triangleCount * 3

            public void Execute(int triIndex)
            {
                int3 tri = Tris[triIndex];
                float2 a = Uvs[tri.x];
                float2 b = Uvs[tri.y];
                float2 c = Uvs[tri.z];

                float g00 = 0f;
                float g01 = 0f;
                float g02 = 0f;
                float g11 = 0f;
                float g12 = 0f;
                float g22 = 0f;
                float3 r0 = 0f;
                float3 r1 = 0f;
                float3 r2 = 0f;

                for (int i = 1; i <= kGrid; i++)
                {
                    float wa = (float)i / kGrid;
                    for (int j = 1; j <= kGrid - i; j++)
                    {
                        float wb = (float)j / kGrid;
                        float wc = 1f - wa - wb;
                        if (wc < kBarycentricInsideEps)
                        {
                            continue;
                        }

                        float2 uv = a * wa + b * wb + c * wc;
                        float3 col = SampleBase(uv);

                        g00 += wa * wa;
                        g01 += wa * wb;
                        g02 += wa * wc;
                        g11 += wb * wb;
                        g12 += wb * wc;
                        g22 += wc * wc;

                        r0 += wa * col;
                        r1 += wb * col;
                        r2 += wc * col;
                    }
                }

                int b6 = triIndex * 6;
                AtA[b6 + 0] = g00;
                AtA[b6 + 1] = g01;
                AtA[b6 + 2] = g02;
                AtA[b6 + 3] = g11;
                AtA[b6 + 4] = g12;
                AtA[b6 + 5] = g22;

                int b3 = triIndex * 3;
                AtB[b3 + 0] = r0;
                AtB[b3 + 1] = r1;
                AtB[b3 + 2] = r2;
            }

            private float3 SampleBase(float2 uv)
            {
                float u = uv.x * BaseWidth - 0.5f;
                float v = uv.y * BaseHeight - 0.5f;
                u = clamp(u, 0f, BaseWidth - 1f);
                v = clamp(v, 0f, BaseHeight - 1f);

                int x0 = (int)u;
                int y0 = (int)v;
                int x1 = min(x0 + 1, BaseWidth - 1);
                int y1 = min(y0 + 1, BaseHeight - 1);

                float fu = u - x0;
                float fv = v - y0;

                float3 c00 = ToLinear(BaseTexels[y0 * BaseWidth + x0]);
                float3 c10 = ToLinear(BaseTexels[y0 * BaseWidth + x1]);
                float3 c01 = ToLinear(BaseTexels[y1 * BaseWidth + x0]);
                float3 c11 = ToLinear(BaseTexels[y1 * BaseWidth + x1]);

                float3 top = lerp(c00, c10, fu);
                float3 bottom = lerp(c01, c11, fu);
                return lerp(top, bottom, fv);
            }

            private static float3 ToLinear(Color32 c)
            {
                return new float3(c.r * kColorByteToUnit, c.g * kColorByteToUnit, c.b * kColorByteToUnit);
            }
        }

        [BurstCompile]
        private struct ErrorJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> Uvs;
            [ReadOnly] public NativeArray<int3> Tris;
            [ReadOnly] public NativeArray<Color32> BaseTexels;
            [ReadOnly] public NativeArray<float3> Colors;
            public int BaseWidth;
            public int BaseHeight;
            // Writes only TriError[triIndex] (the Execute index), so no restriction lift needed.
            [WriteOnly] public NativeArray<float> TriError;

            public void Execute(int triIndex)
            {
                int3 tri = Tris[triIndex];
                float2 a = Uvs[tri.x];
                float2 b = Uvs[tri.y];
                float2 c = Uvs[tri.z];
                float3 ca = Colors[tri.x];
                float3 cb = Colors[tri.y];
                float3 cc = Colors[tri.z];

                float errSum = 0f;
                int sampleCount = 0;
                for (int i = 1; i <= kGrid; i++)
                {
                    float wa = (float)i / kGrid;
                    for (int j = 1; j <= kGrid - i; j++)
                    {
                        float wb = (float)j / kGrid;
                        float wc = 1f - wa - wb;
                        if (wc < kBarycentricInsideEps)
                        {
                            continue;
                        }

                        float2 uv = a * wa + b * wb + c * wc;
                        float3 baseCol = SampleBase(uv);
                        float3 interp = ca * wa + cb * wb + cc * wc;
                        float3 diff = abs(interp - baseCol);
                        errSum += (diff.x + diff.y + diff.z) / 3f;
                        sampleCount++;
                    }
                }

                TriError[triIndex] = sampleCount > 0 ? errSum / sampleCount : 0f;
            }

            private float3 SampleBase(float2 uv)
            {
                float u = uv.x * BaseWidth - 0.5f;
                float v = uv.y * BaseHeight - 0.5f;
                u = clamp(u, 0f, BaseWidth - 1f);
                v = clamp(v, 0f, BaseHeight - 1f);

                int x0 = (int)u;
                int y0 = (int)v;
                int x1 = min(x0 + 1, BaseWidth - 1);
                int y1 = min(y0 + 1, BaseHeight - 1);

                float fu = u - x0;
                float fv = v - y0;

                float3 c00 = ToLinear(BaseTexels[y0 * BaseWidth + x0]);
                float3 c10 = ToLinear(BaseTexels[y0 * BaseWidth + x1]);
                float3 c01 = ToLinear(BaseTexels[y1 * BaseWidth + x0]);
                float3 c11 = ToLinear(BaseTexels[y1 * BaseWidth + x1]);

                float3 top = lerp(c00, c10, fu);
                float3 bottom = lerp(c01, c11, fu);
                return lerp(top, bottom, fv);
            }

            private static float3 ToLinear(Color32 c)
            {
                return new float3(c.r * kColorByteToUnit, c.g * kColorByteToUnit, c.b * kColorByteToUnit);
            }
        }
    }
}

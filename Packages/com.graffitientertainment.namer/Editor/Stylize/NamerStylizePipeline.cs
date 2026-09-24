using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Per-Process stylization context (D-01/D-03): the frozen snapshot of one
    /// <see cref="NamerStyleProfile"/> plus the mesh-derived island ids the stylize
    /// kernels need. Built via <see cref="Create"/> (which sanitizes the profile with
    /// ClampToCaps and rejects zero-weight/NaN anchors before upload, T-05-06) or
    /// hand-assembled by tests. <see cref="Active"/> is the caller's zero-dispatch
    /// identity gate: false means <c>NamerComputePipeline.Process</c> must take the
    /// byte-identical null path — no extra leases, no extra dispatches.
    /// </summary>
    public sealed class NamerStyleStylization
    {
        /// <summary>
        /// True when any effect is non-identity (smoothing &gt; 0 with an available mesh,
        /// or palette pull with strength &gt; 0 and at least one valid anchor). The
        /// identity case (strength 0 + smoothing 0, or smoothing with no mesh data)
        /// stays false so the caller skips the stage entirely.
        /// </summary>
        public bool Active;

        /// <summary>
        /// Anchor records to upload (LinearColor + hue/sat/luma/weight per anchor;
        /// filtered at Create — weight &gt; 0 and no NaN fields, T-05-06).
        /// </summary>
        public NamerStyleAnchor[] Anchors;

        /// <summary>Number of valid anchors in <see cref="Anchors"/>.</summary>
        public int AnchorCount;

        /// <summary>Style strength [0,1]: 0 = original, 1 = full palette snap (D-04).</summary>
        public float StyleStrength;

        /// <summary>Island-gated bilateral smoothing strength [0,1] (D-03).</summary>
        public float Smoothing;

        /// <summary>Hue mode index: 0 = Preserve (f = strength), 1 = Replace (f = 1) (D-05/A5).</summary>
        public int HueReplaceMode;

        /// <summary>Split-mesh UVs (rasterize upload contract; null when no mesh resolved).</summary>
        public Vector2[] Uvs;

        /// <summary>Flattened sub-mesh triangles in fixed order (rasterize upload contract).</summary>
        public int[] FlattenedTriangles;

        /// <summary>
        /// Per-triangle island ids from <see cref="NamerUvIslands.BuildIslandIds"/> —
        /// null when no mesh resolved or the kUvBoundsEpsilon out-of-range guard tripped.
        /// </summary>
        public int[] TriIsland;

        /// <summary>True island count before any binary16 clamp (diagnostics).</summary>
        public int IslandCount;

        /// <summary>True when island ids were clamped at the binary16 cap (T-05-08, surfaced by 05-03).</summary>
        public bool IslandClampedToFloat16Max;

        /// <summary>True when the profile is extracted and carries at least one valid anchor.</summary>
        public bool PaletteAvailable;

        /// <summary>
        /// True when smoothing was requested but no island data is available (no mesh
        /// resolved, or out-of-range UVs) — the bilateral stage skips and the 05-03
        /// Processor window surfaces the Info note (OQ-3).
        /// </summary>
        public bool SmoothingSkippedNoMesh;

        /// <summary>
        /// Builds the stylization context for one Process call. The profile is only
        /// read (plus its ClampToCaps sanitizer), the split is only read. Anchors are
        /// copied (weight &gt; 0, non-NaN only), and TriIsland comes from the CPU
        /// union-find — null when the mesh is null or its UVs fall outside the atlas
        /// bounds epsilon, in which case smoothing skips with
        /// <see cref="SmoothingSkippedNoMesh"/> set.
        /// </summary>
        public static NamerStyleStylization Create(NamerStyleProfile profile, NamerSplitResult split)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            profile.ClampToCaps();

            NamerStyleStylization stylization = new NamerStyleStylization
            {
                StyleStrength = Mathf.Clamp01(profile.StyleStrength),
                Smoothing = Mathf.Clamp01(profile.Smoothing),
                HueReplaceMode = profile.HueMode,
            };

            if (profile.IsExtracted && profile.PaletteAnchors != null && profile.PaletteAnchors.Count > 0)
            {
                // T-05-06: reject zero-weight and NaN anchors before upload — a NaN
                // anchor must never reach the StructuredBuffer.
                var anchors = new List<NamerStyleAnchor>(profile.PaletteAnchors.Count);
                foreach (NamerStyleAnchor anchor in profile.PaletteAnchors)
                {
                    if (anchor.Weight > 0f && !IsNaN(anchor))
                    {
                        anchors.Add(anchor);
                    }
                }

                stylization.Anchors = anchors.ToArray();
                stylization.AnchorCount = anchors.Count;
                stylization.PaletteAvailable = anchors.Count > 0;
            }

            if (split != null)
            {
                stylization.Uvs = split.Uvs;
                stylization.FlattenedTriangles = NamerUvIslands.FlattenTriangles(split.SubMeshTriangles);
                NamerUvIslandResult islands = NamerUvIslands.BuildIslandIds(split);
                stylization.TriIsland = islands.TriIsland;
                stylization.IslandCount = islands.IslandCount;
                stylization.IslandClampedToFloat16Max = islands.ClampedToFloat16Max;
            }

            if (stylization.Smoothing > 0f && stylization.TriIsland == null)
            {
                stylization.SmoothingSkippedNoMesh = true;
            }

            stylization.Active = (stylization.Smoothing > 0f && stylization.TriIsland != null)
                || (stylization.PaletteAvailable && stylization.StyleStrength > 0f);
            return stylization;
        }

        private static bool IsNaN(NamerStyleAnchor anchor)
        {
            return float.IsNaN(anchor.Hue) || float.IsNaN(anchor.Saturation) || float.IsNaN(anchor.Luma)
                || float.IsNaN(anchor.Weight)
                || float.IsNaN(anchor.LinearColor.r) || float.IsNaN(anchor.LinearColor.g)
                || float.IsNaN(anchor.LinearColor.b) || float.IsNaN(anchor.LinearColor.a);
        }
    }

    /// <summary>
    /// GPU dispatch harness for the NAMER stylization stage (Phase 5, plan 02 —
    /// D-01/D-03): the island-id rasterizer, the island-gated bilateral, and the
    /// hue-anchored palette transfer in <c>Compute/NAMERStylize.compute</c>.
    ///
    /// Cross-pool law (Pitfall 1): EVERY render texture is CALLER-OWNED —
    /// <see cref="Run"/> receives src/dst/island/scratch targets leased from
    /// NamerComputePipeline's pool and never creates, leases, or releases a render
    /// texture itself. The pipeline owns only transient ComputeBuffers, disposed in a
    /// finally block (T-05-07) so nothing escapes even on a mid-stage throw.
    ///
    /// Stage order (Pattern 5): (1) rasterize island ids when TriIsland is present,
    /// (2) island-gated bilateral ping-pong over the caller's two scratch targets for
    /// iterations = 1 + round(2 * smoothing) (skipped when smoothing &lt;= 0 or
    /// TriIsland is null — OQ-3), (3) palette transfer, which ALWAYS runs so the
    /// caller-owned dst is fully written (it is a byte-exact passthrough copy when
    /// there are no anchors or zero strength). All targets are linear
    /// R16G16B16A16_SFloat — compute never writes sRGB.
    /// </summary>
    public sealed class NamerStylizePipeline : IDisposable
    {
        private const string ComputeShaderPath = "Packages/com.graffitientertainment.namer/Compute/NAMERStylize.compute";

        // Pattern 3 slider mapping (plan Decisions): iterations = 1 + round(2*s) bound
        // [1,3]; radius fixed at 2 texels; sigmaS = radius * 0.5; sigmaR spans
        // [0.05, 0.40] with s. s = 0 skips the bilateral dispatches entirely.
        private const int kSmoothingRadius = 2;
        private const float kSigmaScaleFactor = 0.5f;
        private const float kSigmaRMin = 0.05f;
        private const float kSigmaRMax = 0.40f;
        private const int kMinSmoothingIterations = 1;
        private const int kMaxSmoothingIterations = 3;
        private const int kIterationsPerUnitStrength = 2;
        private const float kMinSigma = 1e-3f;

        // Achromatic anchor flag threshold (mirrors NAMER_STYLIZE_ACHROMATIC_CUTOFF).
        private const float kAchromaticSaturationCutoff = 0.15f;

        private readonly ComputeShader _compute;
        private readonly int _kernelRasterizeIslands;
        private readonly int _kernelIslandBilateral;
        private readonly int _kernelPaletteTransfer;

        public NamerStylizePipeline()
        {
            _compute = AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputeShaderPath);
            if (_compute == null)
            {
                throw new InvalidOperationException("NAMER stylize compute shader not found at " + ComputeShaderPath);
            }

            _kernelRasterizeIslands = _compute.FindKernel("CSRasterizeIslandIds");
            _kernelIslandBilateral = _compute.FindKernel("CSIslandBilateral");
            _kernelPaletteTransfer = _compute.FindKernel("CSPaletteTransfer");
        }

        /// <summary>
        /// Runs the stylize stage from <paramref name="src"/> into <paramref name="dst"/>
        /// (never the same texture). All five render textures are caller-owned pool
        /// leases; this pipeline only creates/disposes its own ComputeBuffers. The
        /// island/scratch targets are optional when the context carries no mesh — they
        /// are only bound by the stages that use them.
        /// </summary>
        public void Run(
            RenderTexture src,
            RenderTexture dst,
            RenderTexture islandIds,
            RenderTexture scratchA,
            RenderTexture scratchB,
            NamerStyleStylization stylization,
            int w,
            int h)
        {
            if (src == null)
            {
                throw new ArgumentNullException(nameof(src));
            }

            if (dst == null)
            {
                throw new ArgumentNullException(nameof(dst));
            }

            if (stylization == null)
            {
                throw new ArgumentNullException(nameof(stylization));
            }

            ComputeBuffer uvBuffer = null;
            ComputeBuffer triBuffer = null;
            ComputeBuffer islandBuffer = null;
            ComputeBuffer anchorBuffer = null;
            try
            {
                _compute.SetInts("_Size", new[] { w, h });
                RenderTexture paletteSource = src;

                bool smoothingActive = stylization.Smoothing > 0f && stylization.TriIsland != null;
                if (smoothingActive)
                {
                    // Stage 1: rasterize the CPU union-find island ids (the NAMERDecomp
                    // upload contract: ToFloat2 uvs + flattened tris + per-tri ids).
                    int triCount = stylization.TriIsland.Length;
                    float[] uvs = ToFloat2(stylization.Uvs);
                    uvBuffer = CreateBuffer(uvs, stylization.Uvs.Length, sizeof(float) * 2);
                    triBuffer = CreateBuffer(stylization.FlattenedTriangles, triCount, sizeof(int) * 3);
                    islandBuffer = CreateBuffer(stylization.TriIsland, triCount, sizeof(int));
                    _compute.SetInt("_TriCount", triCount);
                    _compute.SetBuffer(_kernelRasterizeIslands, "_Uvs", uvBuffer);
                    _compute.SetBuffer(_kernelRasterizeIslands, "_Tris", triBuffer);
                    _compute.SetBuffer(_kernelRasterizeIslands, "_TriIsland", islandBuffer);
                    _compute.SetTexture(_kernelRasterizeIslands, "_IslandOut", islandIds);
                    Dispatch(_kernelRasterizeIslands, w, h);

                    // Stage 2: island-gated bilateral ping-pong (never in place).
                    int iterations = Mathf.Clamp(
                        kMinSmoothingIterations + Mathf.RoundToInt(kIterationsPerUnitStrength * stylization.Smoothing),
                        kMinSmoothingIterations,
                        kMaxSmoothingIterations);
                    float sigmaS = Mathf.Max(kSmoothingRadius * kSigmaScaleFactor, kMinSigma);
                    float sigmaR = Mathf.Max(Mathf.Lerp(kSigmaRMin, kSigmaRMax, stylization.Smoothing), kMinSigma);
                    _compute.SetInt("_Radius", kSmoothingRadius);
                    _compute.SetFloat("_SigmaS", sigmaS);
                    _compute.SetFloat("_SigmaR", sigmaR);
                    RenderTexture read = src;
                    RenderTexture write = scratchA;
                    for (int i = 0; i < iterations; i++)
                    {
                        _compute.SetTexture(_kernelIslandBilateral, "_Src", read);
                        _compute.SetTexture(_kernelIslandBilateral, "_Dst", write);
                        _compute.SetTexture(_kernelIslandBilateral, "_IslandOut", islandIds);
                        Dispatch(_kernelIslandBilateral, w, h);
                        paletteSource = write;
                        read = write;
                        write = (write == scratchA) ? scratchB : scratchA;
                    }
                }

                // Stage 3: palette transfer ALWAYS runs so the caller-owned dst is fully
                // written — passthrough copy when no anchors / zero strength. Unity 6 on
                // Metal SKIPS the dispatch when ANY property a dispatched kernel
                // references is unbound — constants ("requires a Compute ConstantBuffer
                // ... Skipping dispatch to avoid crashing") and StructuredBuffers
                // ("Property (_AnchorParams) at kernel index (2) is not set") alike. So
                // EVERY argument of this kernel is set UNCONDITIONALLY, on the no-anchor
                // path too; only the anchor DATA is conditional. The no-anchor buffer
                // carries a single dummy record the kernel never reads (_AnchorCount is
                // 0 and it returns before the first _AnchorParams access) because
                // zero-length ComputeBuffers are rejected on some backends
                // (2026-09-23 gate, rounds 1-2).
                int anchorCount = stylization.PaletteAvailable && stylization.Anchors != null
                    && stylization.AnchorCount > 0
                        ? stylization.AnchorCount
                        : 0;
                _compute.SetInt("_AnchorCount", anchorCount);
                _compute.SetFloat("_StyleStrength", Mathf.Clamp01(stylization.StyleStrength));
                _compute.SetInt("_HueReplaceMode", stylization.HueReplaceMode);
                anchorBuffer = CreateBuffer(BuildAnchorParams(stylization, anchorCount), Mathf.Max(anchorCount, 1), sizeof(float) * 4);
                _compute.SetBuffer(_kernelPaletteTransfer, "_AnchorParams", anchorBuffer);

                _compute.SetTexture(_kernelPaletteTransfer, "_PaletteSrc", paletteSource);
                _compute.SetTexture(_kernelPaletteTransfer, "_PaletteOut", dst);
                Dispatch(_kernelPaletteTransfer, w, h);
            }
            finally
            {
                DisposeBuffer(uvBuffer);
                DisposeBuffer(triBuffer);
                DisposeBuffer(islandBuffer);
                DisposeBuffer(anchorBuffer);
            }
        }

        /// <summary>
        /// No pool and no persistent buffers: nothing outlives <see cref="Run"/> (the
        /// compute shader itself is a project asset).
        /// </summary>
        public void Dispose()
        {
        }

        private static Vector4[] BuildAnchorParams(NamerStyleStylization stylization, int anchorCount)
        {
            // One dummy record when anchorCount == 0 (zero-length ComputeBuffers are
            // rejected on some backends); the passthrough kernel never reads it.
            var anchorParams = new Vector4[Mathf.Max(anchorCount, 1)];
            for (int i = 0; i < anchorCount; i++)
            {
                NamerStyleAnchor anchor = stylization.Anchors[i];
                float achromaticFlag = anchor.Saturation < kAchromaticSaturationCutoff ? 1f : 0f;
                anchorParams[i] = new Vector4(anchor.Hue, anchor.Saturation, anchor.Luma, achromaticFlag);
            }

            return anchorParams;
        }

        private static ComputeBuffer CreateBuffer<T>(T[] data, int count, int stride) where T : struct
        {
            ComputeBuffer buffer = new ComputeBuffer(count, stride);
            buffer.SetData(data);
            return buffer;
        }

        private static void DisposeBuffer(ComputeBuffer buffer)
        {
            if (buffer != null)
            {
                buffer.Dispose();
            }
        }

        private static float[] ToFloat2(Vector2[] uvs)
        {
            float[] result = new float[uvs.Length * 2];
            for (int i = 0; i < uvs.Length; i++)
            {
                result[i * 2] = uvs[i].x;
                result[i * 2 + 1] = uvs[i].y;
            }

            return result;
        }

        private void Dispatch(int kernel, int w, int h)
        {
            _compute.Dispatch(kernel, (w + 7) / 8, (h + 7) / 8, 1);
        }
    }
}

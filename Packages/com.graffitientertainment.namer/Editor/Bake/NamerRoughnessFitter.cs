using System;
using UnityEditor;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// In-memory result of a fit-driven roughness strength search. <see cref="Strength"/> is
    /// the selected (first-passing) strength; <see cref="MaxError"/> the post-refit residual
    /// MaxError that strength produced; <see cref="Passed"/> is true only when that MaxError
    /// collapsed within the threshold; <see cref="Cancelled"/> is true when the injectable
    /// cancel poll aborted the scan (no partial result may be consumed).
    /// </summary>
    public readonly struct NamerRoughnessFitResult
    {
        public readonly float Strength;
        public readonly float MaxError;
        public readonly bool Passed;
        public readonly bool Cancelled;

        public NamerRoughnessFitResult(float strength, float maxError, bool passed, bool cancelled)
        {
            Strength = strength;
            MaxError = maxError;
            Passed = passed;
            Cancelled = cancelled;
        }
    }

    /// <summary>
    /// Deterministic CPU strength-ladder minimizer for the fit-driven roughness estimator
    /// (Phase 04.1, plan 02 — D-04). Walks a fixed ascending <see cref="StrengthLadder"/> and
    /// selects the FIRST (minimal) strength whose post-refit residual MaxError is within the
    /// threshold — ChooseResolution-style, mirroring
    /// <see cref="NamerDecompPipeline.ChooseResolution"/> — so minimal sufficient extraction
    /// protects surviving albedo detail (a global argmin would erode more than needed).
    ///
    /// The ladder scan is a thin MANAGED loop, deliberately not a <c>[BurstCompile]</c>
    /// <c>IJobParallelFor</c>: the per-step work is a GPU dispatch + <c>VertexColorFitter.Fit</c>
    /// + <c>NamerDecompPipeline.GenerateResidual</c>, none of which can run inside a Burst job
    /// (the documented D-04 vehicle deviation). Per-pixel texture math stays on the GPU
    /// (NORM-03); this class owns only the deterministic ascending search and the injectable
    /// cancellation poll, mirroring <see cref="NamerAOBaker"/>'s deterministic discipline
    /// (fixed tables, never <see cref="System.Random"/>, caller-owned results, injectable
    /// <c>shouldCancel</c>).
    ///
    /// The <c>evaluate</c> callback is COMPOSED BY <c>NamerProcessor</c> and passed IN: it
    /// drives the per-strength sharp-removal, readback, vertex-color refit, and residual
    /// generation, and returns the post-refit MaxError. This class owns no native buffers —
    /// every allocation lives and dies inside the callback, which is the T-04.1-04 mitigation
    /// (no search-buffer overrun surface exists here).
    /// </summary>
    public static class NamerRoughnessFitter
    {
        /// <summary>
        /// Fixed ascending strength ladder (D-04 — deterministic, never
        /// <see cref="System.Random"/>). <c>0.0</c> (identity) is first so an
        /// already-within-threshold fit costs zero albedo detail.
        /// </summary>
        public static readonly float[] StrengthLadder = { 0.0f, 0.15f, 0.3f, 0.5f, 0.7f, 0.9f, 1.0f };

        /// <summary>
        /// Walks <see cref="StrengthLadder"/> in ascending order and returns the FIRST
        /// strength whose post-refit residual MaxError (<paramref name="evaluate"/>) is
        /// <c>&lt;= <paramref name="maxErrorThreshold"/></c> — first-passing minimal strength,
        /// not a global argmin (the ascending walk makes the tie-break deterministic and never
        /// erodes more albedo detail than needed). If no step passes, returns the max strength
        /// (<c>1.0f</c>) with <see cref="NamerRoughnessFitResult.Passed"/> <c>false</c> (the
        /// residual stays required — honest gate). <paramref name="shouldCancel"/> is polled
        /// between steps; when null, a <see cref="EditorUtility.DisplayCancelableProgressBar"/>
        /// drives the same poll (the interactive-editor path).
        /// </summary>
        public static NamerRoughnessFitResult Fit(Func<float, float> evaluate, Func<bool> shouldCancel, float maxErrorThreshold)
        {
            if (evaluate == null)
            {
                throw new ArgumentNullException(nameof(evaluate));
            }

            float lastMaxError = 0f;
            // WR-01 (04.1 review): only the interactive fallback below displays a progress
            // bar, so only that branch may clear it — a non-interactive shouldCancel caller
            // (tests, batch, debounced preview recomputes) never shows one, and clearing
            // here would dismiss whatever progress bar the host had up.
            bool interactive = shouldCancel == null;
            try
            {
                for (int i = 0; i < StrengthLadder.Length; i++)
                {
                    float strength = StrengthLadder[i];
                    bool cancelled = shouldCancel != null
                        ? shouldCancel()
                        : EditorUtility.DisplayCancelableProgressBar(
                            "Fitting NAMER roughness",
                            "Trying strength " + (i + 1) + "/" + StrengthLadder.Length,
                            (float)(i + 1) / StrengthLadder.Length);

                    if (cancelled)
                    {
                        return new NamerRoughnessFitResult(0f, 0f, passed: false, cancelled: true);
                    }

                    lastMaxError = evaluate(strength);
                    if (lastMaxError <= maxErrorThreshold)
                    {
                        return new NamerRoughnessFitResult(strength, lastMaxError, passed: true, cancelled: false);
                    }
                }

                return new NamerRoughnessFitResult(1.0f, lastMaxError, passed: false, cancelled: false);
            }
            finally
            {
                if (interactive)
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }
    }
}

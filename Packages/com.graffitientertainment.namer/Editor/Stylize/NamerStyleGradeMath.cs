using UnityEngine;
using UnityEngine.Rendering;
using Unity.Mathematics;
using static Unity.Mathematics.math;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// CPU twin of the pinned URP 17.0.4 LDR grading chain and its inverse
    /// (STYL-13/STYL-14 groundwork, phase 05.3 D-02/D-04). The forward twins
    /// transcribe Shaders/NamerPostPreview.hlsl:55-112 stage-for-stage (itself the
    /// verified mirror of URP 17.0.4 LutBuilderLdr.shader FragLutBuilderLdr:41-141 +
    /// PostProcessing/Common.hlsl ApplyColorGrading/ApplyTonemap, installed PackageCache
    /// c7abd84d7030); every numeric literal is a named constant with a file:line citation
    /// so the twin cannot drift from the HLSL it mirrors (same contract as
    /// <see cref="NamerStylePostMath"/> twinning the core RP color math).
    /// D-02 contract: the inverse operators invert the <see cref="NamerStylePostFit"/>
    /// estimator family's own chain — never a second estimator family; the neutral
    /// source constructor exists so Fit(neutral, reference) IS the reference grade estimate.
    /// Pinned by Tests/Editor/NamerStyleGradeMathTests.cs (goldens + two-direction
    /// round-trips at worst-case knobs).
    /// Determinism: every numeric solve is fixed-count with strict comparisons (the
    /// kExposureBisectionIterations = 48 law, NamerStylePostFit.cs:206); no RNG, no
    /// engine object state, no texture access — pure C# on Unity.Mathematics with
    /// Color/AnimationCurve inputs.
    /// Neutral-anchor provenance (RESEARCH Open Question 1, resolved): the synthetic
    /// neutral source's anchors (mean luma, LogC p90-p10 spread, mean chroma) are
    /// DERIVED from the named deterministic fixture through the shipped accumulator —
    /// never hand-picked floats. Any anchor choice is round-trip-consistent (RESEARCH
    /// A1); revisit-with-evidence only (e.g. a neutral NAMER render fixture whose
    /// measured spread diverges visibly).
    /// </summary>
    public static class NamerStyleGradeMath
    {
        // -------- shared chain constants (transcribed) --------

        /// <summary>YRGB curve pre-offset: half a texel of the 128-texel curve strip
        /// (1/128)/2 = 1/256, added before the master pass and again before the R/G/B
        /// pass — NamerPostPreview.hlsl:33-35 kNamerPostCurveHalfPixel (URP
        /// LutBuilderLdr.shader:122 kHalfPixel law).</summary>
        public const float kCurveHalfPixel = (1f / 128f) / 2f;

        /// <summary>Percent-knob scale of the URP grading knobs (contrast/saturation
        /// map z = knob/100 + 1) — NamerPostPreview.hlsl:79-92 CPU precompute law and
        /// NamerStylePostFit.cs:203 kParamRangeLimit (the fit's percent scale/clamp).</summary>
        public const float kPercentKnobScale = 100f;

        /// <summary>Fixed iteration count of the radial tonemap inverse bisection —
        /// mirrors NamerStylePostFit.cs:206 kExposureBisectionIterations (fixed count
        /// keeps the determinism contract; float32 brackets collapse well before 48).</summary>
        public const int kTonemapInverseIterations = 48;

        /// <summary>Upper bracket of the radial tonemap inverse, in pre-tonemap luma:
        /// TonemapLdr is saturated to ~1 well below this, so the bracket covers every
        /// unclipped display target plus +10 EV of raw headroom above display 1.0
        /// (kEffectiveExposureLimit · raw scale; 05.3-01 plan constant).</summary>
        public const float kTonemapInverseBracketMax = 64f;

        /// <summary>Fixed iteration count of the saturation fixed-point inverse
        /// (05.3-01 plan constant; see InverseSaturation — the map preserves luma, so
        /// the iteration is exact after one step in float and the count is headroom).</summary>
        public const int kSatInverseIterations = 3;

        /// <summary>Fixed iteration count of the tonemap inverse's Newton refinement
        /// (Rule-1 fix of the plan's radial-only sketch): 4 finite-difference Newton
        /// steps converge the worst-case probe (chroma 0.5, luma 0.9 shoulder) from
        /// the radial guess to the forward-difference floor ~7e-6; a 5th step measures
        /// no further improvement (harness measurement, 05.3-01). Fixed count keeps
        /// the determinism contract (the 48-law discipline).</summary>
        public const int kTonemapNewtonIterations = 4;

        /// <summary>Forward-difference step of the tonemap inverse's Jacobian,
        /// relative to the current solve magnitude with an absolute floor of the same
        /// value: step = |u|max/1024 + 1/1024. Sets the refinement's convergence floor
        /// (measured ~7e-6 output-space — two orders under the chain round-trip pin;
        /// smaller steps lose the difference to float cancellation in TonemapLdr).</summary>
        public const float kTonemapJacobianStep = 1f / 1024f;

        /// <summary>Determinant magnitude under which the tonemap Jacobian is treated
        /// as numerically singular (flat saturate region at the clip boundary): the
        /// refinement stops and keeps the current iterate — deterministic exit, no
        /// division by noise (best-effort class documented on the method).</summary>
        private const float kTonemapSingularDeterminant = 1e-12f;

        // -------- effective-cap mirrors (STYL-14: invert what the render applies) --------

        /// <summary>Effective white-balance knob cap — mirrors the FIT-side clamp
        /// NamerStylePostFit.cs:207 kFittedWhiteBalanceLimit applied at :394-395
        /// (the fit now clamps at URP's full ±100, so the render's effective knobs
        /// span the same range; asset-side ClampToCaps allows ±100 identically).</summary>
        private const float kEffectiveWhiteBalanceLimit = 100f;

        /// <summary>Effective post-exposure cap — mirrors NamerStylePostFit.cs:205
        /// kMaxFittedPostExposureEv applied at :363 (plan 02 pins
        /// MaxFittedPostExposureEv to the same value).</summary>
        private const float kEffectiveExposureLimit = 10f;

        /// <summary>Effective contrast/saturation percent-knob cap — mirrors
        /// NamerStylePostFit.cs:203 kParamRangeLimit (URP's own slider range ±100).</summary>
        private const float kEffectiveKnobRangeLimit = 100f;

        /// <summary>Uniform key-grid resolution the curve twins sample — MUST match
        /// NamerStylePostFit.cs:196 kCurveKeyCount because the reused MasterInverse
        /// maps grid indices to times through (kCurveKeyCount - 1) internally
        /// (NamerStylePostFit.cs:598-619).</summary>
        private const int kCurveKeyGridResolution = 9;

        // -------- neutral-source fixture anchors (RESEARCH Pattern 1 / A1) --------

        /// <summary>Neutral fixture grid side: 16 columns of EV-ladder luma x 16 rows
        /// (half achromatic, half canonical-chroma hue cycle) — 05.3-01 plan constant
        /// (256 texels: ample mass for the p10/p90 percentile cuts).</summary>
        public const int kNeutralGridResolution = 16;

        /// <summary>Neutral anchor: raw linear mid-gray — ACES 18% gray, same
        /// provenance as kAcesCcMidGray (NamerStylePostMath.cs:39-41: log2(0.18)
        /// enters the ACEScc mid-gray encode); with srcRawMean = 0.18 the shipped EV
        /// bisection (NamerStylePostFit.cs:345-361) makes the neutral EV absolute.</summary>
        public const float kNeutralMidGrayLinear = 0.18f;

        /// <summary>Neutral anchor: canonical chroma of the chromatic fixture half,
        /// measured with the stats' own chroma metric (max-min)/max — 05.3-01 plan
        /// constant (a mid-saturation albedo-grade chroma; the mean over the grid is
        /// exactly half of this because the metric is scale-invariant).</summary>
        public const float kNeutralCanonicalChroma = 0.25f;

        /// <summary>EV step per column of the neutral fixture's geometric luma ladder:
        /// column luma = kNeutralMidGrayLinear * 2^((col - center) * step) — 05.3-01
        /// plan constant giving a +/-3.75 EV raw range (a plausible un-lit neutral
        /// scene spread; wide enough that the contrast spread ratio's denominator
        /// never floors at kContrastDenominatorFloor).</summary>
        private const float kNeutralRampEvStep = 0.5f;

        /// <summary>Column index of the ladder's mid-gray anchor = (kNeutralGridResolution - 1) / 2
        /// — derived, kept named so the ladder's centering is explicit.</summary>
        private const float kNeutralRampCenter = 7.5f;

        /// <summary>Hue-wheel stops of the fixture's deterministic hue cycle (six
        /// primary/secondary stops; hue fraction in [0,1) maps to stop f*6 — no RNG,
        /// no trig).</summary>
        private const int kNeutralHueCycleStops = 6;

        // ================= FORWARD TWINS (NamerPostPreview.hlsl:55-112) =================

        /// <summary>
        /// Forward steps 2-3 (NamerPostPreview.hlsl:64-70): multiply by 2^EV, then
        /// saturate(AcesTonemap(unity_to_ACES(c))) via <see cref="NamerStylePostMath.TonemapLdr"/>
        /// (the exact LDR display stage URP bakes into the LUT). rawLinear is
        /// pre-tonemap HDR linear; the output is display-referred linear in [0,1].
        /// </summary>
        public static float3 ForwardExposureTonemap(float3 rawLinear, float postExposureEv)
        {
            float3 scaled = rawLinear * exp2(postExposureEv);
            return NamerStylePostMath.TonemapLdr(scaled);
        }

        /// <summary>
        /// Forward step 4a (NamerPostPreview.hlsl:72-77): LMS multiply by the exact
        /// knob->coefficient map the CPU side pre-computes for the shader —
        /// <see cref="ColorUtils.ColorBalanceToLMSCoeffs"/> is called DIRECTLY (already
        /// an Editor-asmdef dependency, NamerStylePostFit.cs:781); it is never
        /// transcribed (transcription law applies to pinned HLSL, not to engine C#
        /// the shader already consumes).
        /// </summary>
        public static float3 ForwardWhiteBalance(float3 c, float temperature, float tint)
        {
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(temperature, tint);
            return NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(c) * new float3(coeffs.x, coeffs.y, coeffs.z));
        }

        /// <summary>
        /// Forward step 4b (NamerPostPreview.hlsl:79-85): LogC deviation scaling
        /// around the ACEScc mid-gray pivot, z = contrast/100 + 1 (the CPU precompute
        /// of _PostContrastZ; URP LutBuilderLdr.shader:50-53).
        /// </summary>
        public static float3 ForwardContrast(float3 c, float contrast)
        {
            float z = contrast / kPercentKnobScale + 1f;
            float3 colorLog = NamerStylePostMath.LinearToLogC(c);
            colorLog = (colorLog - NamerStylePostMath.kAcesCcMidGray) * z + NamerStylePostMath.kAcesCcMidGray;
            return NamerStylePostMath.LogCToLinear(colorLog);
        }

        /// <summary>
        /// Forward step 4e (NamerPostPreview.hlsl:87-92): linear saturation around the
        /// INPUT Rec.709 luma pivot, s = saturation/100 + 1 (URP
        /// LutBuilderLdr.shader:116-118 GetLuminance/satMult path). The map preserves
        /// the input luma exactly (Luma709 is linear), which the inverse exploits.
        /// </summary>
        public static float3 ForwardSaturation(float3 c, float saturation)
        {
            float s = saturation / kPercentKnobScale + 1f;
            float luma = NamerStylePostMath.Luma709(c);
            return luma + s * (c - luma);
        }

        /// <summary>
        /// Forward step 4f (NamerPostPreview.hlsl:94-108): YRGB curves — add the 1/256
        /// pre-offset, apply the MASTER curve per channel, add 1/256 again, apply the
        /// R/G/B curves at the (already master-mapped, re-offset) channel values.
        /// Curves are evaluated exactly the way the window bakes the shader's 128x1
        /// strip (NamerEditorWindow.cs EvaluatePostCurveChannel: clamp01(curve.Evaluate(t)),
        /// null -> identity diagonal) — the strip is 128 samples of this same function,
        /// so the twin IS the shader's curve stage up to strip discretization.
        /// </summary>
        public static float3 ForwardCurves(float3 c, AnimationCurve master, AnimationCurve red, AnimationCurve green, AnimationCurve blue)
        {
            float3 curved = c + kCurveHalfPixel;
            float3 masterMapped = new float3(
                EvaluateCurveChannel(master, curved.x),
                EvaluateCurveChannel(master, curved.y),
                EvaluateCurveChannel(master, curved.z));
            float3 offset = masterMapped + kCurveHalfPixel;
            return new float3(
                EvaluateCurveChannel(red, offset.x),
                EvaluateCurveChannel(green, offset.y),
                EvaluateCurveChannel(blue, offset.z));
        }

        /// <summary>
        /// The whole pinned forward chain in order (NamerPostPreview.hlsl:55-112):
        /// exposure*tonemap -> white balance -> contrast -> saturation -> curves ->
        /// final saturate. Scalars come from <paramref name="p"/> with the master curve
        /// built from p.MasterKeys (the plan's NamerStylePostFitParams sketch carried an
        /// AnimationCurve MasterCurve; the shipped class stores MasterKeys — reconciled
        /// here, NamerStylePostFit.cs:24); red/green/blue are explicit curves (pass
        /// null for the identity diagonal — the shipped fit's R/G/B identity keys
        /// evaluated through a null curve ARE the identity). Knobs are clamped to the
        /// effective caps first so the twin applies exactly what the render applies
        /// (STYL-14; see <see cref="DeGrade"/>).
        /// </summary>
        public static float3 ForwardGrade(float3 rawLinear, NamerStylePostFitParams p, AnimationCurve red, AnimationCurve green, AnimationCurve blue)
        {
            ClampEffectiveKnobs(p, out float temperature, out float tint, out float exposureEv, out float contrast, out float saturation);
            float3 c = ForwardExposureTonemap(rawLinear, exposureEv);
            c = ForwardWhiteBalance(c, temperature, tint);
            c = ForwardContrast(c, contrast);
            c = ForwardSaturation(c, saturation);
            c = ForwardCurves(c, ResolveMasterCurve(p), red, green, blue);
            return saturate(c);
        }

        // ================= INVERSE OPERATORS (de-grade chain) =================

        /// <summary>
        /// Inverse of ForwardCurves: channel inverse first (applied last forward),
        /// then master inverse, subtracting the same 1/256 pre-offsets. Curve inversion
        /// reuses <see cref="NamerStylePostFit.MasterInverse"/> verbatim (monotone-key
        /// map, flat segments resolve to the flat region's start) over each curve's
        /// uniform 9-key grid samples — single implementation, no drift. Identity
        /// diagonals pass through unchanged (MasterInverse of value==time keys returns
        /// the value itself). Table-exact for curves whose evaluation is linear between
        /// the 9 grid keys (linear-tangent keys, the round-trip fixture class); fitted
        /// default-tangent keys evaluate Hermite-eased in the strip bake, so their
        /// inverse carries a second-order interpolation residual the de-grade accepts
        /// (the fit family's own curve algebra — FitCurveValues/MasterInverse — is the
        /// same uniform-grid linear discipline, NamerStylePostFit.cs:572-619).
        /// Determinism: closed form, no iteration.
        /// </summary>
        public static float3 InverseCurves(float3 c, AnimationCurve master, AnimationCurve red, AnimationCurve green, AnimationCurve blue)
        {
            float3 channelPre = new float3(
                InverseCurveChannel(red, c.x),
                InverseCurveChannel(green, c.y),
                InverseCurveChannel(blue, c.z));
            float3 masterOut = channelPre - kCurveHalfPixel;
            float3 masterPre = new float3(
                InverseCurveChannel(master, masterOut.x),
                InverseCurveChannel(master, masterOut.y),
                InverseCurveChannel(master, masterOut.z));
            return masterPre - kCurveHalfPixel;
        }

        /// <summary>
        /// Inverse of ForwardSaturation: fixed-point solve of x = Luma709(x) + (o - Luma709(x))/s
        /// for exactly <see cref="kSatInverseIterations"/> iterations starting at x = o.
        /// Derivation: the forward map preserves its input luma exactly (Luma709(o) ==
        /// Luma709(x)), so the first iteration already lands on the fixed point in
        /// exact arithmetic and the remaining iterations are float/convergence headroom
        /// for composed-chain inputs that are not exactly on the map (RESEARCH Pitfall
        /// 4: the pivot is the input luma, which the map does preserve). Never divides
        /// by zero: s &lt;= 0 (saturation &lt;= -100) is a luma-collapsing degenerate map
        /// with nothing to invert — it passes o through unchanged; the shipped fit
        /// floors Saturation at 0 (NamerStylePostFit.cs:406), so the inverse only ever
        /// undoes boosts on the fitted path. Determinism: fixed iteration count.
        /// </summary>
        public static float3 InverseSaturation(float3 o, float saturation)
        {
            float s = saturation / kPercentKnobScale + 1f;
            if (s <= 0f)
            {
                return o;
            }

            float3 x = o;
            for (int i = 0; i < kSatInverseIterations; i++)
            {
                float luma = NamerStylePostMath.Luma709(x);
                x = luma + (o - luma) / s;
            }

            return x;
        }

        /// <summary>
        /// Inverse of ForwardContrast: closed form (logC - pivot)/z + pivot with
        /// z = contrast/100 + 1. z == 0 at contrast == -100 collapses the forward map
        /// onto the pivot (a constant — nothing to invert), so it passes c through
        /// unchanged instead of dividing by zero; the knob range clamps at -100, so
        /// z &lt; 0 is unreachable on the effective path. Determinism: closed form.
        /// </summary>
        public static float3 InverseContrast(float3 c, float contrast)
        {
            float z = contrast / kPercentKnobScale + 1f;
            if (z <= 0f)
            {
                return c;
            }

            float3 colorLog = NamerStylePostMath.LinearToLogC(c);
            colorLog = (colorLog - NamerStylePostMath.kAcesCcMidGray) / z + NamerStylePostMath.kAcesCcMidGray;
            return NamerStylePostMath.LogCToLinear(colorLog);
        }

        /// <summary>
        /// Inverse of ForwardWhiteBalance: per-channel LMS division by the forward
        /// coefficients — exact algebra. NEVER negates the knobs: the knob->
        /// chromaticity map is asymmetric (t1 &lt; 0 ? 0.1 : 0.05 at core
        /// ColorUtils.cs:75), so a sign-flipped temperature knob is NOT the inverse
        /// of the original knob pair
        /// (RESEARCH anti-pattern); dividing by ColorBalanceToLMSCoeffs(T, Tint) is.
        /// Determinism: closed form, no iteration.
        /// </summary>
        public static float3 InverseWhiteBalance(float3 c, float temperature, float tint)
        {
            Vector3 coeffs = ColorUtils.ColorBalanceToLMSCoeffs(temperature, tint);
            return NamerStylePostMath.LmsToLinear(NamerStylePostMath.LinearToLms(c) / new float3(coeffs.x, coeffs.y, coeffs.z));
        }

        /// <summary>
        /// Inverse of ForwardExposureTonemap: radial bisection seed + fixed-count
        /// Newton refinement, both over the SHARED forward TonemapLdr (black box —
        /// no second transcription of the ACES constants, D-02 discipline).
        /// Stage 1 (the plan's radial sketch): TonemapLdr is monotone in uniform
        /// scale — saturate(AcesTonemap(UnityToAces(c*s))) is nondecreasing in s
        /// (shipped comment NamerStylePostFit.cs:341-344) — so a bisection on the
        /// radial scale finds lambda with Luma709(TonemapLdr(lambda * c_hat)) matching
        /// Luma709(y), c_hat = y / Luma709(y), over [0, <see cref="kTonemapInverseBracketMax"/>]
        /// for exactly <see cref="kTonemapInverseIterations"/> iterations with strict
        /// &lt; comparisons (the 48-law).
        /// Stage 2 (Rule-1 fix, measured): the ACES chain is cross-channel (RRT/ODT
        /// desaturation, glow), so the true pre-tonemap point does NOT lie on y's ray —
        /// the radial-only residual measured 0.74 at chroma 0.5 (RESEARCH A3 made
        /// concrete). Exactly <see cref="kTonemapNewtonIterations"/> finite-difference
        /// Newton steps on the output-space residual y - TonemapLdr(u) converge every
        /// probe to the ~7e-6 forward-difference floor (3x3 Jacobian by forward
        /// differences at <see cref="kTonemapJacobianStep"/>, Cramer solve, iterate
        /// clamped to the bisection bracket; a singular Jacobian stops the refinement
        /// deterministically). Return u / 2^EV.
        /// Black or NaN input luma returns black (TonemapLdr(0) == 0 — only black maps
        /// from black; the negated comparison catches NaN, NamerStylePostFit.cs:305
        /// discipline). Where y is clipped (all channels >= 1 - 1/255) the result is
        /// best-effort: the bracket rails and the saturate region flattens the
        /// Jacobian — the CALLER masks those texels (D-07 "clipping" class, exactly
        /// the non-invertible set). Determinism: fixed counts, no data exit.
        /// </summary>
        public static float3 InverseExposureTonemap(float3 y, float postExposureEv)
        {
            float lumaY = NamerStylePostMath.Luma709(y);
            if (!(lumaY > 0f))
            {
                return new float3(0f, 0f, 0f);
            }

            float3 direction = y / lumaY;
            float lo = 0f;
            float hi = kTonemapInverseBracketMax;
            for (int i = 0; i < kTonemapInverseIterations; i++)
            {
                float mid = (lo + hi) * 0.5f; // bisection midpoint, NamerStylePostFit.cs:351 form
                float predicted = NamerStylePostMath.Luma709(NamerStylePostMath.TonemapLdr(mid * direction));
                if (predicted < lumaY)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            float3 u = ((lo + hi) * 0.5f) * direction;
            for (int k = 0; k < kTonemapNewtonIterations; k++)
            {
                float3 f0 = NamerStylePostMath.TonemapLdr(u);
                float3 residual = y - f0;
                float step = cmax(math.abs(u)) * kTonemapJacobianStep + kTonemapJacobianStep;
                float3 column0 = (NamerStylePostMath.TonemapLdr(u + new float3(step, 0f, 0f)) - f0) / step;
                float3 column1 = (NamerStylePostMath.TonemapLdr(u + new float3(0f, step, 0f)) - f0) / step;
                float3 column2 = (NamerStylePostMath.TonemapLdr(u + new float3(0f, 0f, step)) - f0) / step;
                float determinant = column0.x * (column1.y * column2.z - column1.z * column2.y)
                    - column0.y * (column1.x * column2.z - column1.z * column2.x)
                    + column0.z * (column1.x * column2.y - column1.y * column2.x);
                if (math.abs(determinant) < kTonemapSingularDeterminant)
                {
                    break;
                }

                float deltaX = (residual.x * (column1.y * column2.z - column1.z * column2.y)
                    - residual.y * (column1.x * column2.z - column1.z * column2.x)
                    + residual.z * (column1.x * column2.y - column1.y * column2.x)) / determinant;
                float deltaY = (column0.x * (residual.y * column2.z - residual.z * column2.y)
                    - column0.y * (residual.x * column2.z - residual.z * column2.x)
                    + column0.z * (residual.x * column2.y - residual.y * column2.x)) / determinant;
                float deltaZ = (column0.x * (column1.y * residual.z - column1.z * residual.y)
                    - column0.y * (column1.x * residual.z - column1.z * residual.x)
                    + column0.z * (column1.x * residual.y - column1.y * residual.x)) / determinant;
                u = clamp(u + new float3(deltaX, deltaY, deltaZ), 0f, kTonemapInverseBracketMax);
            }

            return u / exp2(postExposureEv);
        }

        /// <summary>
        /// The whole de-grade: composes the inverses in REVERSE chain order
        /// (InverseCurves -> InverseSaturation -> InverseContrast ->
        /// InverseWhiteBalance -> InverseExposureTonemap) over the CLAMPED effective
        /// knobs — the inverse must invert the clamped values the render applies
        /// (STYL-14 law, RESEARCH Pattern 3: the fitted WB clamps to +/-50 and EV to
        /// +/-10, so out-of-cap params invert identically to their capped values).
        /// displayLinear is display-referred linear in [0,1]; the output is pre-tonemap
        /// raw linear (may exceed 1). Curve/master resolution matches
        /// <see cref="ForwardGrade"/>. Determinism: all member solves fixed-count.
        /// </summary>
        public static float3 DeGrade(float3 displayLinear, NamerStylePostFitParams p, AnimationCurve red, AnimationCurve green, AnimationCurve blue)
        {
            ClampEffectiveKnobs(p, out float temperature, out float tint, out float exposureEv, out float contrast, out float saturation);
            float3 c = InverseCurves(displayLinear, ResolveMasterCurve(p), red, green, blue);
            c = InverseSaturation(c, saturation);
            c = InverseContrast(c, contrast);
            c = InverseWhiteBalance(c, temperature, tint);
            return InverseExposureTonemap(c, exposureEv);
        }

        // ================= NEUTRAL SOURCE (RESEARCH Pattern 1) =================

        /// <summary>
        /// The deterministic synthetic neutral raw-linear fixture: a 16x16 grid whose
        /// columns are a geometric (EV-ladder) luma ramp anchored at
        /// <see cref="kNeutralMidGrayLinear"/> (column kNeutralRampCenter IS mid-gray)
        /// and whose rows are split — half achromatic, half at
        /// <see cref="kNeutralCanonicalChroma"/> with a fixed hue cycle (row-index
        /// fractions over the hue wheel, no RNG) — then normalized so the raw luma
        /// mean of the whole grid is exactly kNeutralMidGrayLinear (divide by the
        /// computed mean over kNeutralMidGrayLinear, double-accumulated in fixed
        /// row/column order). The chroma metric is scale-invariant, so normalization
        /// preserves the canonical chroma.
        /// Determinism: fixed order, fixed grids, no RNG (run-twice identical).
        /// </summary>
        public static Color[] BuildNeutralSourcePixels()
        {
            int pixelCount = kNeutralGridResolution * kNeutralGridResolution;
            float3[] raw = new float3[pixelCount];
            double lumaSum = 0.0;
            int index = 0;
            for (int row = 0; row < kNeutralGridResolution; row++)
            {
                for (int col = 0; col < kNeutralGridResolution; col++)
                {
                    float columnLuma = kNeutralMidGrayLinear * exp2((col - kNeutralRampCenter) * kNeutralRampEvStep);
                    float3 value;
                    if (row < kNeutralGridResolution / 2)
                    {
                        value = new float3(columnLuma, columnLuma, columnLuma);
                    }
                    else
                    {
                        int chromaticRow = row - kNeutralGridResolution / 2;
                        float hueFraction = chromaticRow / (float)(kNeutralGridResolution / 2);
                        value = ChromaticNeutralPixel(columnLuma, hueFraction);
                    }

                    raw[index] = value;
                    lumaSum += NamerStylePostMath.Luma709(value);
                    index++;
                }
            }

            float meanLuma = (float)(lumaSum / pixelCount);
            float normalizationScale = meanLuma / kNeutralMidGrayLinear;
            Color[] pixels = new Color[pixelCount];
            for (int i = 0; i < pixelCount; i++)
            {
                float3 normalized = raw[i] / normalizationScale;
                pixels[i] = new Color(normalized.x, normalized.y, normalized.z, 1f);
            }

            return pixels;
        }

        /// <summary>
        /// The neutral source stats: <see cref="NamerStylePostFit.AccumulateSource"/>
        /// run verbatim over <see cref="BuildNeutralSourcePixels"/> — the neutral
        /// anchors (mean luma, LogC p90-p10 spread, mean chroma) are therefore derived
        /// by the shipped accumulator from the named fixture, never hand-picked
        /// (RESEARCH Open Question 1 resolution). Pass these as the SOURCE side of
        /// <see cref="NamerStylePostFit.Fit"/> to obtain the reference grade estimate
        /// (D-02: the same code path, only the source-stats constructor is new).
        /// </summary>
        public static NamerStylePostStats BuildNeutralSourceStats()
        {
            return NamerStylePostFit.AccumulateSource(BuildNeutralSourcePixels());
        }

        /// <summary>
        /// Curve evaluation of one channel exactly as the window bakes the shader
        /// strip: clamp01(curve.Evaluate(t)), null -> identity diagonal
        /// (NamerEditorWindow.cs EvaluatePostCurveChannel discipline).
        /// </summary>
        private static float EvaluateCurveChannel(AnimationCurve curve, float t)
        {
            if (curve == null)
            {
                return t;
            }

            return clamp(curve.Evaluate(t), 0f, 1f);
        }

        /// <summary>
        /// Inverse of one channel's curve map via the reused
        /// <see cref="NamerStylePostFit.MasterInverse"/> over the curve's uniform
        /// 9-key grid samples (the fit family's own curve algebra). y is clamped to
        /// [0,1] first (the forward evaluation's own clamp01); a null curve is the
        /// identity diagonal and passes through unchanged.
        /// </summary>
        private static float InverseCurveChannel(AnimationCurve curve, float y)
        {
            if (curve == null)
            {
                return y;
            }

            return NamerStylePostFit.MasterInverse(CurveGridValues(curve), clamp(y, 0f, 1f));
        }

        /// <summary>
        /// A curve's values on the uniform kCurveKeyGridResolution key grid (the
        /// MasterInverse domain). Allocated per call — editor-time de-grade over
        /// <=128-cap grids, not a hot path.
        /// </summary>
        private static float[] CurveGridValues(AnimationCurve curve)
        {
            float[] values = new float[kCurveKeyGridResolution];
            for (int i = 0; i < kCurveKeyGridResolution; i++)
            {
                values[i] = EvaluateCurveChannel(curve, i / (float)(kCurveKeyGridResolution - 1));
            }

            return values;
        }

        /// <summary>
        /// Clamps the fit params to the effective caps both operators apply — the
        /// STYL-14 law: the render applies clamped knobs, so the twin's forward and
        /// inverse both operate on the clamped values (mirrors: WB NamerStylePostFit.cs:204/:326-327,
        /// EV :205/:363, percent knobs :203). NaN falls through clamp into lo/hi rails
        /// deterministically (only the shipped fit writes p in production; its values
        /// are already in-cap, so this is defensive symmetry for hand-built params).
        /// </summary>
        private static void ClampEffectiveKnobs(NamerStylePostFitParams p, out float temperature, out float tint, out float exposureEv, out float contrast, out float saturation)
        {
            temperature = clamp(p.Temperature, -kEffectiveWhiteBalanceLimit, kEffectiveWhiteBalanceLimit);
            tint = clamp(p.Tint, -kEffectiveWhiteBalanceLimit, kEffectiveWhiteBalanceLimit);
            exposureEv = clamp(p.PostExposureEv, -kEffectiveExposureLimit, kEffectiveExposureLimit);
            contrast = clamp(p.Contrast, -kEffectiveKnobRangeLimit, kEffectiveKnobRangeLimit);
            saturation = clamp(p.Saturation, -kEffectiveKnobRangeLimit, kEffectiveKnobRangeLimit);
        }

        /// <summary>
        /// The master curve of a fit-params object: built from p.MasterKeys (the
        /// shipped class stores keys, not a curve — reconciled here); a null/empty
        /// key array resolves to null, which the curve stages treat as the identity
        /// diagonal.
        /// </summary>
        private static AnimationCurve ResolveMasterCurve(NamerStylePostFitParams p)
        {
            if (p == null || p.MasterKeys == null || p.MasterKeys.Length == 0)
            {
                return null;
            }

            return new AnimationCurve(p.MasterKeys);
        }

        /// <summary>
        /// One neutral-fixture chromatic pixel at the target raw luma, canonical
        /// chroma and a hue-wheel fraction: color = (1-c)*M*white + c*M*pureHue with
        /// pureHue on the RGB unit wheel (max 1, min 0). The stats' chroma metric
        /// (max-min)/max of the mix is exactly c = kNeutralCanonicalChroma, and the
        /// luma equation M*((1-c) + c*Luma709(pureHue)) = targetLuma closes the
        /// construction deterministically (no RNG, no trig).
        /// </summary>
        private static float3 ChromaticNeutralPixel(float targetLuma, float hueFraction)
        {
            float3 pureHue = PureHueDirection(hueFraction);
            float pureHueLuma = NamerStylePostMath.Luma709(pureHue);
            float mixDenominator = (1f - kNeutralCanonicalChroma) + kNeutralCanonicalChroma * pureHueLuma;
            float m = targetLuma / mixDenominator;
            float achromaticWeight = (1f - kNeutralCanonicalChroma) * m;
            float chromaticWeight = kNeutralCanonicalChroma * m;
            return achromaticWeight + chromaticWeight * pureHue;
        }

        /// <summary>
        /// Pure hue direction on the RGB wheel for a fraction in [0,1): six stops
        /// (red -> yellow -> green -> cyan -> blue -> magenta), linear within each
        /// stop segment. All components stay in [0,1] with max 1 and min 0.
        /// </summary>
        private static float3 PureHueDirection(float hueFraction)
        {
            float wheel = hueFraction * kNeutralHueCycleStops;
            int stop = (int)wheel;
            float fraction = wheel - stop;
            switch (stop)
            {
                case 0: return new float3(1f, fraction, 0f);
                case 1: return new float3(1f - fraction, 1f, 0f);
                case 2: return new float3(0f, 1f, fraction);
                case 3: return new float3(0f, 1f - fraction, 1f);
                case 4: return new float3(fraction, 0f, 1f);
                default: return new float3(1f, 0f, 1f - fraction);
            }
        }
    }
}

using System;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Unity.Mathematics;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05.3 plan 03 decomposer tests (STYL-13/STYL-14/STYL-17, D-02/D-04/D-07):
    /// the run-twice element-wise determinism bar over anchors + grade + fractions,
    /// ForwardGrade-based self-consistent recovery through the FULL decompose
    /// pipeline (sRGB 8-bit round trip + percentile cuts + Pattern 4 mask + shipped
    /// Fit), exact per-class classification pins at the pure ClassifyTexel seam
    /// (cues built FROM the classifier's own named constants — A5 revisits retarget
    /// the fixtures), decompose-level fraction ordering across hash-noise fixtures,
    /// prereq honesty (min-3 gate with named unreadable texture, injected cancel,
    /// null asset), the grade-override path, the STYL-17 validation metric's both
    /// boundary cases against kValidationMismatchThreshold, and the mask-in-both-
    /// palette-modes pin. Knob-recovery tolerances are NAMED and harness-measured
    /// (Temp/gsd-05303-harness, 2026-09-30): the shipped estimator family (D-02,
    /// frozen) is a mean/ratio heuristic with cross-knob biases on the wide neutral
    /// ladder — white balance recovers tightly at small knobs (measured |err| &lt;= 0.5),
    /// exposure within 0.04, saturation within 5 (floor), while contrast carries a
    /// large mask-induced spread bias (measured −45.6 at known +5: the classifier
    /// correctly zeroes the ladder's 44% deep-shadow mass, compressing the reference
    /// p10/p90 spread the contrast solve reads) and is pinned BOUNDED, not exact.
    /// Fixtures: in-memory textures (extractor-test builder conventions); hash-noise
    /// grain never luminance ramps (MEMORY: ramps flatten to zero gradient); the
    /// fog/veil and flat-mid classes are exactly the classes a ramp mispredicts.
    /// All decomposes run headless via injected shouldCancel — no editor progress UI.
    /// </summary>
    public class NamerStyleDecomposerTests
    {
        private const string TempFolder = "Assets/NAMER_StyleDecomposer_Tests_Temp";

        // -------- self-consistency tolerances (harness-measured, Temp/gsd-05303-harness) --------

        /// <summary>WB recovery at small knobs measured |T err| 0.06–0.50 and
        /// |Tint err| 0.27–0.40 across masked/unmasked and with/without a master
        /// curve; 1.0 doubles the worst case.</summary>
        private const float kWhiteBalanceRecoveryTolerance = 1.0f;

        /// <summary>EV recovery measured |err| 0.02–0.22 across scenarios (the
        /// unmasked-with-master case is the worst); 0.25 covers it with margin.</summary>
        private const float kExposureRecoveryTolerance = 0.25f;

        /// <summary>Saturation recovered 0 (the fit's chroma deadband) against known
        /// +5 — measured |err| 5; 6 covers the deadband with margin.</summary>
        private const float kSaturationRecoveryTolerance = 6.0f;

        /// <summary>Contrast through the masked pipeline measured −45.6 at known +5
        /// (no curve) / −40.6 (gentle master) — the spread-compression bias of the
        /// shadow mask on the neutral ladder. Pinned bounded: above −60 with margin.</summary>
        private const float kContrastRecoveryFloor = -60f;

        /// <summary>Texel-level de-graded hue error over chromatic texels measured
        /// mean 0.014–0.034 / max 0.148 across scenarios; anchors average texels, so
        /// 0.10 mean / 0.30 max cover clustering plus 8-bit quantization (1/255).</summary>
        private const float kDeGradedHueMeanTolerance = 0.10f;
        private const float kDeGradedHueMaxTolerance = 0.30f;

        /// <summary>Chroma floor for a hue to be meaningful (extraction-test
        /// kChromaticSaturationFloor precedent; the neutral fixture's canonical
        /// chroma is 0.25 raw).</summary>
        private const float kChromaticChromaFloor = 0.15f;

        /// <summary>Hue comparison epsilon for the local hue helper.</summary>
        private const float kHueEpsilon = 1e-6f;

        /// <summary>Clean hash-noise fixture: mid-gray sRGB base with per-channel
        /// grain — the majority must classify clean (confident).</summary>
        private const float kCleanMidBase = 0.45f;
        private const float kCleanMidGrain = 0.05f;

        /// <summary>Deterministic hash-noise constants (fract(sin(i·k1)·k2) grain —
        /// fixed, no RNG; the extractor determinism discipline).</summary>
        private const float kHashNoiseScale = 12.9898f;
        private const float kHashNoiseSeed = 43758.5453f;

        /// <summary>Confident-fraction floor for the clean grain fixture (measured
        /// behavior: mid-bright grain classifies overwhelmingly clean).</summary>
        private const float kCleanConfidentFractionFloor = 0.5f;

        /// <summary>Veil fixture base: 0.65 sRGB decodes to ~0.39 linear — above the
        /// classifier's kFogMidLumaFloor (1/3 linear) so the flat veil patch actually
        /// lands in the fog class (0.55 sRGB would decode BELOW the floor and read
        /// clean instead).</summary>
        private const float kVeilBase = 0.65f;

        // ================= determinism =================

        [Test]
        public void Decompose_IsDeterministic_RunTwiceElementWiseIdentical()
        {
            NamerStyleAsset asset = CreateDecomposableAsset("Determinism", BuildSmallKnobParams());
            try
            {
                NamerStyleDecomposeResult first = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                NamerStyleDecomposeResult second = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(first.PaletteAnchors, "three readable refs must decompose successfully");
                Assert.IsNotNull(second.PaletteAnchors);
                Assert.IsFalse(first.Cancelled);
                Assert.IsFalse(first.TooFewReferences);
                Assert.IsNull(first.ErrorTextureName);

                Assert.AreEqual(first.PaletteAnchors.Length, second.PaletteAnchors.Length,
                    "anchor count must be identical across two decomposes (fixed order + fixed float math)");
                for (int i = 0; i < first.PaletteAnchors.Length; i++)
                {
                    Assert.AreEqual(first.PaletteAnchors[i].LinearColor, second.PaletteAnchors[i].LinearColor,
                        "anchor colors must be element-wise identical across two decomposes");
                    Assert.AreEqual(first.PaletteAnchors[i].Hue, second.PaletteAnchors[i].Hue);
                    Assert.AreEqual(first.PaletteAnchors[i].Saturation, second.PaletteAnchors[i].Saturation);
                    Assert.AreEqual(first.PaletteAnchors[i].Luma, second.PaletteAnchors[i].Luma);
                    Assert.AreEqual(first.PaletteAnchors[i].Weight, second.PaletteAnchors[i].Weight);
                }

                Assert.AreEqual(first.Grade.Temperature, second.Grade.Temperature,
                    "grade knobs must be exactly equal across two decomposes");
                Assert.AreEqual(first.Grade.Tint, second.Grade.Tint);
                Assert.AreEqual(first.Grade.PostExposureEv, second.Grade.PostExposureEv);
                Assert.AreEqual(first.Grade.Contrast, second.Grade.Contrast);
                Assert.AreEqual(first.Grade.Saturation, second.Grade.Saturation);
                Assert.AreEqual(first.Grade.BloomThreshold, second.Grade.BloomThreshold);
                Assert.AreEqual(first.ConfidentAlbedoFraction, second.ConfidentAlbedoFraction);
                Assert.AreEqual(first.ContaminatedFraction, second.ContaminatedFraction);
                Assert.AreEqual(first.DecompositionFingerprint, second.DecompositionFingerprint);

                // The bare-stamp pin: the fingerprint is the EXACT ComputeFingerprint
                // return, no suffix — writing it to the asset leaves IsDecompositionStale
                // false immediately after Decompose (05.3-02 state-model contract).
                Assert.AreEqual(asset.ComputeFingerprint(), first.DecompositionFingerprint,
                    "DecompositionFingerprint must be the bare ComputeFingerprint return");
                asset.DecompositionFingerprint = first.DecompositionFingerprint;
                Assert.IsFalse(asset.IsDecompositionStale,
                    "writing the returned fingerprint must clear decomposition staleness");
            }
            finally
            {
                DestroyAssetAndTextures(asset);
            }
        }

        // ================= self-consistent recovery (STYL-13/STYL-14 core) =================

        [Test]
        public void Decompose_SelfConsistentRecovery_SmallKnobsNoCurve_RecoversWhiteBalanceExposureAndHue()
        {
            NamerStylePostFitParams known = BuildSmallKnobParams();
            NamerStyleAsset asset = CreateDecomposableAsset("SelfConsistentNoCurve", known, withMasterCurve: false);
            try
            {
                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(result.PaletteAnchors, "graded neutral fixture must decompose successfully");

                Assert.AreEqual(known.Temperature, result.Grade.Temperature, kWhiteBalanceRecoveryTolerance,
                    "recovered temperature must match the known grade within the harness-measured tolerance");
                Assert.AreEqual(known.Tint, result.Grade.Tint, kWhiteBalanceRecoveryTolerance,
                    "recovered tint must match the known grade within the harness-measured tolerance");
                Assert.AreEqual(known.PostExposureEv, result.Grade.PostExposureEv, kExposureRecoveryTolerance,
                    "recovered post-exposure must match the known grade within the harness-measured tolerance");
                Assert.AreEqual(known.Saturation, result.Grade.Saturation, kSaturationRecoveryTolerance,
                    "recovered saturation must match the known grade within the deadband-covering tolerance");
                Assert.GreaterOrEqual(result.Grade.Contrast, kContrastRecoveryFloor,
                    "contrast carries the measured mask-induced spread bias but must stay bounded (no railing)");

                AssertDeGradedAnchorsTrackNeutralHues(result);
            }
            finally
            {
                DestroyAssetAndTextures(asset);
            }
        }

        [Test]
        public void Decompose_SelfConsistentRecovery_WithMasterCurve_MaskAppliesInBothPaletteModesAndRawDiffers()
        {
            NamerStylePostFitParams known = BuildSmallKnobParams();
            NamerStyleAsset asset = CreateDecomposableAsset("SelfConsistentMasterCurve", known, withMasterCurve: true);
            try
            {
                asset.PaletteSource = NamerEditorConstants.kPaletteSourceDeGraded;
                NamerStyleDecomposeResult deGraded = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(deGraded.PaletteAnchors);
                Assert.AreEqual(known.Temperature, deGraded.Grade.Temperature, kWhiteBalanceRecoveryTolerance);
                Assert.AreEqual(known.PostExposureEv, deGraded.Grade.PostExposureEv, kExposureRecoveryTolerance);
                Assert.AreEqual(9, deGraded.Grade.MasterKeys.Length,
                    "the fit emits the fixed 9-key master grid");
                Assert.IsTrue(IsMonotone(deGraded.Grade.MasterKeys),
                    "fitted master keys are monotone by the fit's running-max law");
                AssertDeGradedAnchorsTrackNeutralHues(deGraded);

                // D-04 switch pin: raw mode skips ONLY the DeGrade transform — the
                // confidence mask multiplies histogram weights in BOTH modes.
                asset.PaletteSource = NamerEditorConstants.kPaletteSourceRaw;
                NamerStyleDecomposeResult raw = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(raw.PaletteAnchors);
                Assert.AreEqual(deGraded.ConfidentAlbedoFraction, raw.ConfidentAlbedoFraction,
                    "the confidence mask applies identically in both palette modes");
                Assert.AreEqual(deGraded.ContaminatedFraction, raw.ContaminatedFraction,
                    "the confidence mask applies identically in both palette modes");
                Assert.AreNotEqual(DescribeAnchors(deGraded.PaletteAnchors), DescribeAnchors(raw.PaletteAnchors),
                    "raw mode keeps the grade baked — the palette must differ from the de-graded mode");
                Assert.AreEqual(deGraded.Grade.Temperature, raw.Grade.Temperature,
                    "the palette-source switch never re-fits the grade");
            }
            finally
            {
                DestroyAssetAndTextures(asset);
            }
        }

        // ================= classification (RESEARCH Pattern 4) =================

        [Test]
        public void ClassifyTexel_ClippedTexel_CarriesZeroWeight()
        {
            float weight = NamerStyleDecomposer.ClassifyTexel(
                NamerStyleDecomposer.kClipThresholdSrgb, 0.9f * NamerStyleDecomposer.kClipThresholdSrgb,
                decodedLuma: 0.4f, localLumaGradient: 0.1f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f);
            Assert.AreEqual(0f, weight,
                "a texel at the clipping threshold (254/255) is the tonemap inverse's non-invertible set — weight 0");
        }

        [Test]
        public void ClassifyTexel_DesaturatedBrightAboveCut_CarriesZeroWeight()
        {
            float rawMax = 0.99f;
            float chromaBelowCut = NamerStyleDecomposer.kSpecularChromaMax * 0.5f;
            float rawMin = rawMax * (1f - chromaBelowCut);
            float weight = NamerStyleDecomposer.ClassifyTexel(
                rawMax, rawMin, decodedLuma: 0.5f, localLumaGradient: 0.1f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f);
            Assert.AreEqual(0f, weight,
                "desaturated-bright above the p95 cut is specular/bloom — weight 0");
        }

        [Test]
        public void ClassifyTexel_SaturatedBrightAboveCut_CarriesZeroWeight()
        {
            float weight = NamerStyleDecomposer.ClassifyTexel(
                rawMaxChannel: 0.99f, rawMinChannel: 0.2f, decodedLuma: 0.4f, localLumaGradient: 0.1f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f);
            Assert.AreEqual(0f, weight,
                "saturated-bright above the p95 cut is emissive — weight 0");
        }

        [Test]
        public void ClassifyTexel_DeepShadowTexel_CarriesZeroWeight()
        {
            float belowFloor = NamerStyleDecomposer.kShadowLumaFloor * 0.5f;
            Assert.AreEqual(0f, NamerStyleDecomposer.ClassifyTexel(
                rawMaxChannel: 0.08f, rawMinChannel: 0.05f, decodedLuma: belowFloor, localLumaGradient: 0.1f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f),
                "a texel below the absolute shadow floor is noise-dominated — weight 0");
            Assert.AreEqual(0f, NamerStyleDecomposer.ClassifyTexel(
                rawMaxChannel: 0.1f, rawMinChannel: 0.06f, decodedLuma: 0.01f, localLumaGradient: 0.1f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.015f),
                "a texel below the fixture's p5 luma cut is the deep-shadow class — weight 0");
        }

        [Test]
        public void ClassifyTexel_FlatMidBrightLowChromaTexel_CarriesFogWeight()
        {
            float rawMax = 0.6f;
            float rawMin = rawMax * (1f - NamerStyleDecomposer.kSpecularChromaMax * 0.5f);
            float weight = NamerStyleDecomposer.ClassifyTexel(
                rawMax, rawMin,
                decodedLuma: NamerStyleDecomposer.kFogMidLumaFloor * 1.2f,
                localLumaGradient: NamerStyleDecomposer.kFogGradientThreshold * 0.5f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f);
            Assert.AreEqual(NamerStyleDecomposer.kFogWeight, weight,
                "flat mid-bright desaturated texels are fog/veil — the conservative LOW weight, not zero");
        }

        [Test]
        public void ClassifyTexel_CleanMidTexel_CarriesFullConfidence()
        {
            float weight = NamerStyleDecomposer.ClassifyTexel(
                rawMaxChannel: 0.5f, rawMinChannel: 0.35f, decodedLuma: 0.2f,
                localLumaGradient: NamerStyleDecomposer.kFogGradientThreshold * 2f,
                p95Max3Cut: 0.95f, p5LumaCut: 0.001f);
            Assert.AreEqual(1f, weight,
                "a textured mid-gray texel away from every contaminant cue is clean albedo — full confidence");
        }

        [Test]
        public void Decompose_ClassificationFractions_OrderAcrossContaminantFixtures()
        {
            // Hash-noise grain, never ramps (MEMORY: ramps flatten to zero gradient —
            // exactly the fog cue, the class a ramp mispredicts).
            Texture2D clean = CreateFilledTexture("FractionClean", 64, i =>
                HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D clipped = CreateFilledTexture("FractionClipped", 64, i =>
                (i % 9 == 0)
                    ? new Color(1f, 1f, 1f, 1f)
                    : HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D black = CreateFilledTexture("FractionBlack", 64, i =>
                (i % 7 == 0)
                    ? new Color(0.005f, 0.005f, 0.005f, 1f)
                    : HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D veil = CreateFilledTexture("FractionVeil", 64, i =>
                HashNoiseTexel(i, kVeilBase, kCleanMidGrain * 0.1f));
            try
            {
                NamerStyleDecomposeResult cleanResult = DecomposeSingleFixture("Clean", clean);
                NamerStyleDecomposeResult clippedResult = DecomposeSingleFixture("Clipped", clipped);
                NamerStyleDecomposeResult blackResult = DecomposeSingleFixture("Black", black);
                NamerStyleDecomposeResult veilResult = DecomposeSingleFixture("Veil", veil);

                Assert.GreaterOrEqual(cleanResult.ConfidentAlbedoFraction, kCleanConfidentFractionFloor,
                    "mid-bright hash-noise grain must classify majority-clean");
                Assert.Greater(clippedResult.ContaminatedFraction, cleanResult.ContaminatedFraction,
                    "a clipped-white patch must raise the contaminated fraction above clean grain");
                Assert.Greater(blackResult.ContaminatedFraction, cleanResult.ContaminatedFraction,
                    "a deep-black patch must raise the contaminated fraction above clean grain");
                Assert.Greater(veilResult.ContaminatedFraction, cleanResult.ContaminatedFraction,
                    "a flat low-gradient veil must raise the contaminated fraction (fog low-weight class)");
                Assert.Less(veilResult.ConfidentAlbedoFraction, cleanResult.ConfidentAlbedoFraction,
                    "the veil fixture must lose confident mass relative to clean grain");
            }
            finally
            {
                Destroy(clean, clipped, black, veil);
            }
        }

        // ================= prereq honesty / failure contract =================

        [Test]
        public void Decompose_TwoPositiveWeightRefs_ReportTooFewReferences()
        {
            Texture2D first = CreateFilledTexture("PrereqA", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D second = CreateFilledTexture("PrereqB", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D third = CreateFilledTexture("PrereqC", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // A null slot plus a zero-weight slot are both "not there" (S4/A4).
                asset.References.Add(first);
                asset.References.Add(second);
                asset.References.Add(third);
                asset.References.Add(null);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(0f);
                asset.ReferenceWeights.Add(1f);

                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsTrue(result.TooFewReferences,
                    "two positive-weight refs are below MinReferences — honest refusal, never a partial result");
                Assert.IsNull(result.Grade);
                Assert.IsNull(result.PaletteAnchors);
            }
            finally
            {
                Destroy(asset, first, second, third);
            }
        }

        [Test]
        public void Decompose_UnreadableReference_FailsNamingTheTexture()
        {
            Texture2D readableA = CreateFilledTexture("ReadableA", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D readableB = CreateFilledTexture("ReadableB", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D unreadable = CreateUnreadableTexture("UnreadableRef");
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                asset.References.Add(readableA);
                asset.References.Add(readableB);
                asset.References.Add(unreadable);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(1f);

                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsFalse(result.TooFewReferences);
                Assert.AreEqual("UnreadableRef", result.ErrorTextureName,
                    "the unreadable texture must be named in the failure result — never a raw exception");
                Assert.IsNull(result.Grade, "no partial result escapes a failed read");
            }
            finally
            {
                Destroy(asset, readableA, readableB);
                DeleteTempFolder();
            }
        }

        [Test]
        public void Decompose_CancelInjected_ReturnsCancelledWithNoPartialResult()
        {
            NamerStyleAsset asset = CreateDecomposableAsset("Cancel", BuildSmallKnobParams());
            try
            {
                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: AlwaysCancel);
                Assert.IsTrue(result.Cancelled, "an injected cancel must return the cancelled shape");
                Assert.IsNull(result.Grade, "no partial result escapes a cancel");
                Assert.IsNull(result.PaletteAnchors);
                Assert.AreEqual(-1f, result.ConfidentAlbedoFraction);
            }
            finally
            {
                DestroyAssetAndTextures(asset);
            }
        }

        [Test]
        public void Decompose_NullAsset_ThrowsArgumentNullException()
        {
            Assert.Throws<ArgumentNullException>(
                () => NamerStyleDecomposer.DecomposeStyle(null, shouldCancel: NeverCancel));
        }

        [Test]
        public void Decompose_NonFiniteTexels_AreSkippedNotFatal()
        {
            // RGBAFloat keeps NaN/Inf through GetPixels (RGBA32 would quantize them
            // away) — the pipeline must skip them (T-05.3-03-01), never poison.
            const int Size = 32;
            Color[] pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain);
            }

            pixels[0] = new Color(float.NaN, 0.5f, 0.5f, 1f);
            pixels[1] = new Color(0.5f, float.PositiveInfinity, 0.5f, 1f);
            pixels[2] = new Color(0.5f, 0.5f, float.NegativeInfinity, 1f);
            Texture2D poisoned = CreateFloatTexture("NonFinite", Size, pixels);
            Texture2D cleanA = CreateFilledTexture("NonFiniteCleanA", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            Texture2D cleanB = CreateFilledTexture("NonFiniteCleanB", 32, i => HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain));
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                asset.References.Add(poisoned);
                asset.References.Add(cleanA);
                asset.References.Add(cleanB);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(1f);

                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(result.PaletteAnchors, "non-finite texels must not abort the decompose");
                Assert.IsFalse(float.IsNaN(result.ConfidentAlbedoFraction),
                    "fractions are computed over finite mass only");
                Assert.IsFalse(float.IsNaN(result.ContaminatedFraction));
                Assert.IsFalse(float.IsNaN(result.Grade.PostExposureEv),
                    "non-finite texels must never reach the estimator inputs");
            }
            finally
            {
                Destroy(asset, poisoned, cleanA, cleanB);
            }
        }

        // ================= grade override (D-02 complement) =================

        [Test]
        public void Decompose_GradeOverride_CarriesExemplarNameAndBareFingerprint_NeverSilent()
        {
            NamerStyleAsset asset = CreateDecomposableAsset("Override", BuildSmallKnobParams());
            try
            {
                NamerStylePostFitParams overrideParams = new NamerStylePostFitParams
                {
                    Temperature = 7f,
                    Tint = -3f,
                    PostExposureEv = 0.4f,
                    Contrast = 10f,
                    Saturation = 8f,
                };
                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(
                    asset, shouldCancel: NeverCancel, gradeOverride: overrideParams, gradeOverrideExemplarName: "Suite Exemplar");
                Assert.AreSame(overrideParams, result.Grade,
                    "the override path carries the caller's grade object verbatim");
                Assert.AreEqual("Suite Exemplar", result.GradeSourceExemplarName,
                    "the exemplar name records where the grade came from (S6 honesty)");
                Assert.AreEqual(asset.ComputeFingerprint(), result.DecompositionFingerprint,
                    "the override path still stamps the bare shared-list fingerprint");
                Assert.AreEqual(7f, result.Grade.Temperature,
                    "no re-fit happens on the override path");

                NamerStyleDecomposeResult neutral = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.AreEqual(string.Empty, neutral.GradeSourceExemplarName,
                    "the neutral (model-free) path leaves the exemplar name empty — the complement is engaged only when supplied");
                Assert.AreNotEqual(7f, neutral.Grade.Temperature,
                    "the neutral path fits its own grade over the neutral source");
            }
            finally
            {
                DestroyAssetAndTextures(asset);
            }
        }

        // ================= validation metric (STYL-17) =================

        [Test]
        public void Validation_IdentityParams_MeasureZeroWithinThreshold()
        {
            NamerStylePostFitParams identity = new NamerStylePostFitParams();
            NamerStyleValidationMeasure measure = NamerStyleDecomposer.ComputeValidationMeasure(identity);
            Assert.AreEqual(0f, measure.Measure, 1e-6f,
                "identity params (all knobs zero, no curve) deviate nothing from identity");
            Assert.IsTrue(measure.Measure < NamerEditorConstants.kValidationMismatchThreshold,
                "the identity case sits within the mismatch threshold by construction");
            Assert.AreEqual("max knob deviation 0% (Temperature)", NamerStyleDecomposer.BuildValidationMeasureString(identity),
                "the measure string renders integer percent with the worst knob's name");
        }

        [Test]
        public void Validation_SingleKnobAtThirtyPercentOfRange_ExceedsMismatchThreshold()
        {
            NamerStylePostFitParams mismatch = new NamerStylePostFitParams { Contrast = 30f };
            NamerStyleValidationMeasure measure = NamerStyleDecomposer.ComputeValidationMeasure(mismatch);
            Assert.AreEqual(0.30f, measure.Measure, 1e-6f,
                "contrast 30 of its ±100 range normalizes to exactly 0.30");
            Assert.GreaterOrEqual(measure.Measure, NamerEditorConstants.kValidationMismatchThreshold,
                "the mismatch case must exceed the pinned 0.10 threshold (the STYL-17 Warning boundary)");
            Assert.AreEqual("Contrast", measure.KnobName);
            Assert.AreEqual("max knob deviation 30% (Contrast)", NamerStyleDecomposer.BuildValidationMeasureString(mismatch));
        }

        [Test]
        public void Validation_BoundaryAndMasterCurveCases_PinThresholdAndKnobNaming()
        {
            NamerStylePostFitParams justInside = new NamerStylePostFitParams { Temperature = 4f };
            NamerStyleValidationMeasure inside = NamerStyleDecomposer.ComputeValidationMeasure(justInside);
            Assert.AreEqual(0.08f, inside.Measure, 1e-6f,
                "temperature 4 of its ±50 range normalizes to 0.08");
            Assert.Less(inside.Measure, NamerEditorConstants.kValidationMismatchThreshold,
                "0.08 hugging the boundary from below stays within tolerance");

            // A master curve lifted 0.3 at mid-grid deviates 0.3 on the 9-point grid —
            // the curve axis dominates and names itself.
            Keyframe[] lifted = new Keyframe[9];
            for (int i = 0; i < lifted.Length; i++)
            {
                float t = i / (float)(lifted.Length - 1);
                lifted[i] = new Keyframe(t, t + 0.3f * 4f * t * (1f - t));
            }

            NamerStylePostFitParams curved = new NamerStylePostFitParams { MasterKeys = lifted };
            NamerStyleValidationMeasure curveMeasure = NamerStyleDecomposer.ComputeValidationMeasure(curved);
            Assert.AreEqual("MasterCurve", curveMeasure.KnobName,
                "the master curve is a validation axis — a 0.3 mid-grid lift must dominate and be named");
            Assert.Greater(curveMeasure.Measure, 0.2f,
                "the 0.3 lift measures above 0.2 on the curve axis");
        }

        // ================= fixture builders (extractor-test conventions) =================

        /// <summary>Small known knobs — the harness-measured regime where the frozen
        /// estimator family recovers white balance/exposure/saturation tightly
        /// (contrast stays spread-biased through the shadow mask; see class doc).</summary>
        private static NamerStylePostFitParams BuildSmallKnobParams()
        {
            return new NamerStylePostFitParams
            {
                Temperature = 5f,
                Tint = -2f,
                PostExposureEv = 0.2f,
                Contrast = 5f,
                Saturation = 5f,
            };
        }

        /// <summary>An asset with three graded-neutral-fixture references (the
        /// self-consistency premise: reference = ForwardGrade(neutral, known) — the
        /// D-02 source constructor fixes the fixture). The textures are destroyed by
        /// the caller via DestroyAssetAndTextures.</summary>
        private static NamerStyleAsset CreateDecomposableAsset(string namePrefix, NamerStylePostFitParams known,
            bool withMasterCurve = false)
        {
            NamerStylePostFitParams gradeParams = known;
            if (withMasterCurve)
            {
                gradeParams = new NamerStylePostFitParams
                {
                    Temperature = known.Temperature,
                    Tint = known.Tint,
                    PostExposureEv = known.PostExposureEv,
                    Contrast = known.Contrast,
                    Saturation = known.Saturation,
                    MasterKeys = BuildLiftedMasterKeys(0.04f),
                };
            }

            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            for (int i = 0; i < NamerEditorConstants.MinReferences; i++)
            {
                asset.References.Add(CreateGradedNeutralTexture(namePrefix + "Ref" + i, gradeParams));
                asset.ReferenceWeights.Add(1f);
            }

            return asset;
        }

        /// <summary>The graded neutral fixture as a 16x16 RGBA32 texture: each texel
        /// is the sRGB ENCODING of ForwardGrade's display-linear output over the
        /// neutral grid texel (the texture stores what a graded screenshot would
        /// store; AccumulateReference's decode recovers the display-linear values).</summary>
        private static Texture2D CreateGradedNeutralTexture(string textureName, NamerStylePostFitParams known)
        {
            Color[] neutral = NamerStyleGradeMath.BuildNeutralSourcePixels();
            Color[] graded = new Color[neutral.Length];
            for (int i = 0; i < neutral.Length; i++)
            {
                float3 displayLinear = NamerStyleGradeMath.ForwardGrade(
                    new float3(neutral[i].r, neutral[i].g, neutral[i].b), known, null, null, null);
                graded[i] = new Color(
                    LinearToSrgb(displayLinear.x), LinearToSrgb(displayLinear.y), LinearToSrgb(displayLinear.z), 1f);
            }

            return CreateFilledTexture(textureName, NamerStyleGradeMath.kNeutralGridResolution, i => graded[i]);
        }

        /// <summary>Lifted-diagonal master keys with LINEAR tangents — the documented
        /// round-trip fixture class (NamerStyleGradeMath InverseCurves doc: linear
        /// between the 9 grid keys, so the de-grade is table-exact).</summary>
        private static Keyframe[] BuildLiftedMasterKeys(float lift)
        {
            const int KeyCount = 9;
            Keyframe[] keys = new Keyframe[KeyCount];
            for (int i = 0; i < KeyCount; i++)
            {
                float t = i / (float)(KeyCount - 1);
                keys[i] = new Keyframe(t, t + lift * 4f * t * (1f - t));
            }

            for (int i = 0; i < KeyCount; i++)
            {
                float slope = i == 0
                    ? keys[1].value - keys[0].value
                    : (i == KeyCount - 1
                        ? keys[i].value - keys[i - 1].value
                        : (keys[i + 1].value - keys[i - 1].value) * 0.5f);
                keys[i].inTangent = slope;
                keys[i].outTangent = slope;
            }

            return keys;
        }

        /// <summary>The sRGB transfer encode — the exact inverse of
        /// NamerStyleExtractor.SrgbToLinear's constants (the decode side's own
        /// thresholds; the fixture builder must round-trip through the same law).</summary>
        private static float LinearToSrgb(float linear)
        {
            const float kSegmentCutoff = 0.0031308f;
            const float kLinearDivisor = 12.92f;
            const float kSegmentOffset = 0.055f;
            const float kSegmentScale = 1.055f;
            const float kEncodeExponent = 1f / 2.4f;
            if (linear <= kSegmentCutoff)
            {
                return linear / kLinearDivisor;
            }

            return Mathf.Pow(linear, kEncodeExponent) * kSegmentScale - kSegmentOffset;
        }

        /// <summary>Deterministic per-texel hash-noise grain around a base sRGB level
        /// (no RNG — the extractor determinism discipline; independent per channel so
        /// the chroma stays above the fog chroma cut and the luma box-gradient above
        /// the fog gradient threshold — the classes a luminance ramp would blur).</summary>
        private static Color HashNoiseTexel(int index, float baseLevel, float grain)
        {
            return new Color(
                baseLevel + (HashNoise(index * 3) * 2f - 1f) * grain,
                baseLevel + (HashNoise(index * 3 + 1) * 2f - 1f) * grain,
                baseLevel + (HashNoise(index * 3 + 2) * 2f - 1f) * grain,
                1f);
        }

        private static float HashNoise(int seed)
        {
            float s = Mathf.Sin(seed * kHashNoiseScale) * kHashNoiseSeed;
            return s - Mathf.Floor(s);
        }

        private static NamerStyleDecomposeResult DecomposeSingleFixture(string name, Texture2D fixture)
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                for (int i = 0; i < NamerEditorConstants.MinReferences; i++)
                {
                    asset.References.Add(fixture);
                    asset.ReferenceWeights.Add(1f);
                }

                NamerStyleDecomposeResult result = NamerStyleDecomposer.DecomposeStyle(asset, shouldCancel: NeverCancel);
                Assert.IsNotNull(result.PaletteAnchors, "fixture " + name + " must decompose successfully");
                return result;
            }
            finally
            {
                Destroy(asset);
            }
        }

        /// <summary>Asserts the de-graded palette's chromatic anchors sit near the
        /// neutral fixture's own hue set (the STYL-14 de-grade pin at anchor level;
        /// tolerances are harness-measured at texel level plus clustering margin).</summary>
        private static void AssertDeGradedAnchorsTrackNeutralHues(NamerStyleDecomposeResult result)
        {
            Color[] neutral = NamerStyleGradeMath.BuildNeutralSourcePixels();
            float hueSum = 0f;
            float hueMax = 0f;
            int chromaticAnchors = 0;
            foreach (NamerStyleAnchor anchor in result.PaletteAnchors)
            {
                if (anchor.Saturation < kChromaticChromaFloor)
                {
                    continue;
                }

                float nearestNeutralHueDistance = 1f;
                foreach (Color texel in neutral)
                {
                    float3 rgb = new float3(texel.r, texel.g, texel.b);
                    if (Chroma01(rgb) < kChromaticChromaFloor)
                    {
                        continue;
                    }

                    nearestNeutralHueDistance = Mathf.Min(
                        nearestNeutralHueDistance, HueDistance(anchor.Hue, Hue01(rgb)));
                }

                Assert.LessOrEqual(nearestNeutralHueDistance, kDeGradedHueMaxTolerance,
                    "every chromatic de-graded anchor must sit near a neutral fixture hue");
                hueSum += nearestNeutralHueDistance;
                hueMax = Mathf.Max(hueMax, nearestNeutralHueDistance);
                chromaticAnchors++;
            }

            Assert.Greater(chromaticAnchors, 0,
                "the chromatic half of the fixture must produce at least one chromatic anchor");
            Assert.LessOrEqual(hueSum / chromaticAnchors, kDeGradedHueMeanTolerance,
                "mean de-graded hue error over chromatic anchors must stay within the measured tolerance (max was "
                + hueMax.ToString("F3") + ")");
        }

        private static bool IsMonotone(Keyframe[] keys)
        {
            for (int i = 1; i < keys.Length; i++)
            {
                if (keys[i].value < keys[i - 1].value)
                {
                    return false;
                }
            }

            return true;
        }

        private static string DescribeAnchors(NamerStyleAnchor[] anchors)
        {
            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            foreach (NamerStyleAnchor anchor in anchors)
            {
                builder.Append(anchor.LinearColor.ToString("F6")).Append(';');
            }

            return builder.ToString();
        }

        private static float Chroma01(float3 c)
        {
            float max = math.cmax(c);
            if (max <= kHueEpsilon)
            {
                return 0f;
            }

            return (max - math.cmin(c)) / max;
        }

        private static float Hue01(float3 c)
        {
            float max = math.cmax(c);
            float min = math.cmin(c);
            float chroma = max - min;
            if (max <= kHueEpsilon || chroma <= kHueEpsilon)
            {
                return 0f;
            }

            float hue;
            if (max == c.x)
            {
                hue = (c.y - c.z) / chroma;
            }
            else if (max == c.y)
            {
                hue = 2f + (c.z - c.x) / chroma;
            }
            else
            {
                hue = 4f + (c.x - c.y) / chroma;
            }

            hue /= 6f;
            return hue < 0f ? hue + 1f : hue;
        }

        private static float HueDistance(float a, float b)
        {
            float d = Mathf.Abs(a - b);
            return d > 0.5f ? 1f - d : d;
        }

        private static bool NeverCancel()
        {
            return false;
        }

        private static bool AlwaysCancel()
        {
            return true;
        }

        private static Texture2D CreateFilledTexture(string textureName, int size, Func<int, Color> fill)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = textureName };
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill(i);
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static Texture2D CreateFloatTexture(string textureName, int size, Color[] pixels)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBAFloat, false) { name = textureName };
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        /// <summary>A PNG imported with Read/Write disabled — the only way to hold an
        /// isReadable == false texture in an EditMode test (extractor-test /
        /// NamerAOBakeTests TempFolder precedent; folder removed in the caller's
        /// finally).</summary>
        private static Texture2D CreateUnreadableTexture(string textureName)
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", System.IO.Path.GetFileName(TempFolder));
            }
            const int Size = 8;
            Texture2D source = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[Size * Size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = HashNoiseTexel(i, kCleanMidBase, kCleanMidGrain);
            }

            source.SetPixels(pixels);
            source.Apply(false, false);
            string path = TempFolder + "/" + textureName + ".png";
            System.IO.File.WriteAllBytes(path, source.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(source);
            AssetDatabase.ImportAsset(path);
            TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.isReadable = false;
            importer.SaveAndReimport();
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        private static void DeleteTempFolder()
        {
            if (AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        private static void DestroyAssetAndTextures(NamerStyleAsset asset)
        {
            if (asset == null)
            {
                return;
            }

            foreach (Texture2D reference in asset.References)
            {
                DestroyImmediate(reference);
            }

            DestroyImmediate(asset);
        }

        private static void Destroy(params UnityEngine.Object[] assets)
        {
            foreach (UnityEngine.Object asset in assets)
            {
                DestroyImmediate(asset);
            }
        }

        private static void DestroyImmediate(UnityEngine.Object asset)
        {
            if (asset != null)
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}

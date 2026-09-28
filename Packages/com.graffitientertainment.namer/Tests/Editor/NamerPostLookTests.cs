using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.2 plan 01 post-look asset tests (STYL-10): headless pins for the standalone
    /// <see cref="NamerPostLook"/> model — fresh-instance neutrality (UI-SPEC section C
    /// defaults law: unfitted ≡ neutral ≡ no post), ClampToCaps sanitization (the
    /// re-homed NaN-then-clamp discipline plus the bloom-intensity artist knob and the
    /// reference-list cap), reference-fingerprint identity over the asset's OWN list
    /// (determinism, null-slot skipping, weight sensitivity), the fit-staleness seam,
    /// and the A13 volume-staleness seam over <c>ComputeParamsFingerprint</c>. Pure
    /// EditMode tests — no GPU, no asset writes.
    /// </summary>
    public class NamerPostLookTests
    {
        [Test]
        public void Look_CreateInstance_HasNeutralIdentityDefaults()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                // An unfitted/neutral asset is identity post (05.2 UI-SPEC section C):
                // zero white balance, zero EV, unity contrast/saturation, URP-default
                // bloom threshold AND intensity, the neutral curve diagonal.
                Assert.AreEqual(0f, look.PostTemperature, "neutral fit = zero temperature");
                Assert.AreEqual(0f, look.PostTint, "neutral fit = zero tint");
                Assert.AreEqual(0f, look.PostExposureEv, "neutral fit = zero exposure");
                Assert.AreEqual(0f, look.PostContrast, "neutral fit = unity contrast reads 0 (URP 0 = identity)");
                Assert.AreEqual(0f, look.PostSaturation, "neutral fit = unity saturation reads 0 (URP 0 = identity)");
                Assert.AreEqual(NamerEditorConstants.DefaultBloomThreshold, look.PostBloomThreshold,
                    "bloom threshold defaults to URP's own 0.9 (STYL-08)");
                Assert.AreEqual(NamerEditorConstants.DefaultBloomIntensity, look.PostBloomIntensity,
                    "bloom intensity is the artist knob moved home (A9) — default 1, never fitted");

                AssertNeutralDiagonal(look.PostMasterCurve);
                AssertNeutralDiagonal(look.PostRedCurve);
                AssertNeutralDiagonal(look.PostGreenCurve);
                AssertNeutralDiagonal(look.PostBlueCurve);

                Assert.IsEmpty(look.References, "the asset's own reference list starts empty (D-05)");
                Assert.IsEmpty(look.ReferenceWeights);
                Assert.AreEqual(0, look.AssignedReferenceCount);
                Assert.IsTrue(string.IsNullOrEmpty(look.PostFitFingerprint));
                Assert.IsTrue(string.IsNullOrEmpty(look.PostFitExemplarName));
                Assert.IsFalse(look.HasFit, "a fresh asset carries no fitted post look");
                Assert.IsFalse(look.IsFitStale, "fit staleness is only meaningful once fitted");
                Assert.IsTrue(string.IsNullOrEmpty(look.VolumeWriteFingerprint));
                Assert.IsFalse(look.IsVolumeStale, "volume staleness is only meaningful once fitted");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        [Test]
        public void Look_ClampToCaps_SanitizesFittedBlock()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                look.PostTemperature = 150f;
                look.PostTint = -250f;
                look.PostExposureEv = -42f;
                look.PostContrast = float.NaN;
                look.PostSaturation = 2f;
                look.PostBloomThreshold = 12f;
                look.PostBloomIntensity = 25f;

                look.ClampToCaps();

                Assert.AreEqual(100f, look.PostTemperature, "temperature clamps to the URP [-100,100] range");
                Assert.AreEqual(-100f, look.PostTint, "tint clamps to the URP [-100,100] range");
                Assert.AreEqual(-NamerEditorConstants.MaxFittedPostExposureEv, look.PostExposureEv,
                    "post exposure clamps to the fitted-EV cap");
                Assert.AreEqual(0f, look.PostContrast,
                    "a non-numeric contrast falls back to neutral 0 — Mathf.Clamp propagates NaN, so the fallback must run first");
                Assert.AreEqual(2f, look.PostSaturation, "saturation 2 is already inside [-100,100] and stays put");
                Assert.AreEqual(NamerEditorConstants.MaxBloomThreshold, look.PostBloomThreshold, "bloom threshold clamps to its cap");
                Assert.AreEqual(NamerEditorConstants.MaxBloomIntensity, look.PostBloomIntensity,
                    "the bloom-intensity artist knob clamps to its own cap (A9)");

                // NaN bloom floats fall back to their URP-default constants, not 0.
                look.PostBloomThreshold = float.NaN;
                look.PostBloomIntensity = float.NaN;
                look.ClampToCaps();
                Assert.AreEqual(NamerEditorConstants.DefaultBloomThreshold, look.PostBloomThreshold,
                    "a non-numeric bloom threshold falls back to the URP default");
                Assert.AreEqual(NamerEditorConstants.DefaultBloomIntensity, look.PostBloomIntensity,
                    "a non-numeric bloom intensity falls back to its default (the identity knob value)");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        [Test]
        public void Look_ClampToCaps_ResetsDegenerateCurvesToNeutralDiagonal()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                // Null and zero-key curves reset to the neutral diagonal.
                look.PostMasterCurve = null;
                look.PostRedCurve = new AnimationCurve();
                look.ClampToCaps();
                AssertNeutralDiagonal(look.PostMasterCurve);
                AssertNeutralDiagonal(look.PostRedCurve);

                // Out-of-range key times/values clamp into [0,1]; a NaN key value
                // resets the whole curve to the neutral diagonal.
                look.PostGreenCurve = new AnimationCurve(
                    new Keyframe(-0.25f, 0.1f), new Keyframe(0.5f, 0.6f), new Keyframe(1.5f, 1.4f));
                look.PostBlueCurve = new AnimationCurve(
                    new Keyframe(0f, float.NaN), new Keyframe(1f, 1f));
                look.ClampToCaps();

                Assert.AreEqual(3, look.PostGreenCurve.length, "in-range key count is preserved — only the values clamp");
                Assert.AreEqual(0f, look.PostGreenCurve.keys[0].time, "key times clamp up to 0");
                Assert.AreEqual(0.1f, look.PostGreenCurve.keys[0].value);
                Assert.AreEqual(1f, look.PostGreenCurve.keys[2].time, "key times clamp down to 1");
                Assert.AreEqual(1f, look.PostGreenCurve.keys[2].value, "key values clamp down to 1");
                AssertNeutralDiagonal(look.PostBlueCurve,
                    "a non-numeric key value resets the whole curve to the neutral diagonal");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        [Test]
        public void Look_ClampToCaps_TruncatesReferenceListAndSyncsWeights()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                for (int i = 0; i < NamerEditorConstants.MaxReferences + 3; i++)
                {
                    look.References.Add(null);
                }

                // No weights at all: the sync fills the whole list with defaults.
                look.ClampToCaps();

                Assert.AreEqual(NamerEditorConstants.MaxReferences, look.References.Count,
                    "the asset's own reference list truncates at MaxReferences (D-05 list-shape law)");
                Assert.AreEqual(NamerEditorConstants.MaxReferences, look.ReferenceWeights.Count,
                    "weights stay parallel to the truncated list");
                for (int i = 0; i < look.ReferenceWeights.Count; i++)
                {
                    Assert.AreEqual(NamerEditorConstants.DefaultReferenceWeight, look.ReferenceWeights[i],
                        "synced weights default to DefaultReferenceWeight");
                }

                // Overlong weight lists trim; NaN weights fall back to the default
                // before the [0,1] clamp; out-of-range weights clamp.
                look.ReferenceWeights.Add(4f);
                look.ReferenceWeights[1] = float.NaN;
                look.ReferenceWeights[2] = -0.5f;
                look.ClampToCaps();
                Assert.AreEqual(NamerEditorConstants.MaxReferences, look.ReferenceWeights.Count,
                    "an overlong weight list trims back to the reference count");
                Assert.AreEqual(NamerEditorConstants.DefaultReferenceWeight, look.ReferenceWeights[1],
                    "a non-numeric weight falls back to the default");
                Assert.AreEqual(0f, look.ReferenceWeights[2], "a negative weight clamps up to 0");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        [Test]
        public void Look_ComputeFingerprint_IsDeterministicAndSkipsNullSlots()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "LookFingerprintFirst" };
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                look.References.Add(null);
                look.References.Add(first);
                look.ReferenceWeights.Add(1f);
                look.ReferenceWeights.Add(0.5f);

                Assert.AreEqual(look.ComputeFingerprint(), look.ComputeFingerprint(),
                    "the fingerprint is deterministic — two calls on the same state agree");

                // Null slots are skipped: [null, first@0.5] identities equal [first@0.5].
                NamerPostLook withoutNull = ScriptableObject.CreateInstance<NamerPostLook>();
                try
                {
                    withoutNull.References.Add(first);
                    withoutNull.ReferenceWeights.Add(0.5f);
                    Assert.AreEqual(withoutNull.ComputeFingerprint(), look.ComputeFingerprint(),
                        "null slots contribute nothing to the fingerprint");
                }
                finally
                {
                    DestroyImmediate(withoutNull);
                }

                Assert.AreEqual(1, look.AssignedReferenceCount, "null slots are not counted as assigned");
            }
            finally
            {
                DestroyImmediate(look);
                DestroyImmediate(first);
            }
        }

        [Test]
        public void Look_ComputeFingerprint_TracksWeightChanges()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "LookWeightFirst" };
            Texture2D second = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "LookWeightSecond" };
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                look.References.Add(first);
                look.References.Add(second);
                look.ReferenceWeights.Add(1f);
                look.ReferenceWeights.Add(0.5f);
                string before = look.ComputeFingerprint();

                look.ReferenceWeights[1] = 0.75f;
                Assert.AreNotEqual(before, look.ComputeFingerprint(),
                    "any weight edit changes the fingerprint — weights apply at fit time (A10)");
            }
            finally
            {
                DestroyImmediate(look);
                DestroyImmediate(first);
                DestroyImmediate(second);
            }
        }

        [Test]
        public void Look_HasFitAndIsFitStale_TrackTheFitFingerprint()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "LookStaleFirst" };
            Texture2D second = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "LookStaleSecond" };
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                look.References.Add(first);
                look.References.Add(second);
                look.ReferenceWeights.Add(1f);
                look.ReferenceWeights.Add(0.5f);

                // Simulate a successful Fit Look write: the fit-time fingerprint
                // equals the current refs + weights state.
                look.PostFitFingerprint = look.ComputeFingerprint();
                look.PostFitExemplarName = "TestExemplar";

                Assert.IsTrue(look.HasFit, "a captured fit fingerprint marks the asset fitted");
                Assert.IsFalse(look.IsFitStale, "unchanged refs + weights must not be fit-stale");

                look.ReferenceWeights[1] = 0.75f;
                Assert.IsTrue(look.IsFitStale,
                    "any refs + weights change must flip the asset fit-stale (the A10 warning fires)");

                look.ReferenceWeights[1] = 0.5f;
                Assert.IsFalse(look.IsFitStale,
                    "reverting the weight restores the fit-fingerprint match — fit staleness is pure comparison, no hidden state");
            }
            finally
            {
                DestroyImmediate(look);
                DestroyImmediate(first);
                DestroyImmediate(second);
            }
        }

        [Test]
        public void Look_ComputeParamsFingerprint_CoversEveryParameterAndCurveKey()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                string baseline = look.ComputeParamsFingerprint();
                Assert.AreEqual(baseline, look.ComputeParamsFingerprint(),
                    "the params fingerprint is deterministic");

                // Every fitted float plus the artist knob participates.
                look.PostTemperature = 1.5f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "temperature participates");
                look.PostTemperature = 0f;

                look.PostTint = -2.5f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "tint participates");
                look.PostTint = 0f;

                look.PostExposureEv = 0.25f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "post exposure participates");
                look.PostExposureEv = 0f;

                look.PostContrast = 3f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "contrast participates");
                look.PostContrast = 0f;

                look.PostSaturation = -1f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "saturation participates");
                look.PostSaturation = 0f;

                look.PostBloomThreshold = 0.95f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "bloom threshold participates");
                look.PostBloomThreshold = NamerEditorConstants.DefaultBloomThreshold;

                look.PostBloomIntensity = 2.5f;
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(),
                    "the bloom-intensity artist knob participates (A13 marks the Volume stale on knob edits too)");
                look.PostBloomIntensity = NamerEditorConstants.DefaultBloomIntensity;

                // Every curve's time/value keys participate.
                look.PostMasterCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0.9f));
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "master curve key values participate");
                look.PostMasterCurve = CreateNeutralCurve();

                look.PostRedCurve = new AnimationCurve(new Keyframe(0.1f, 0f), new Keyframe(1f, 1f));
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "red curve key times participate");
                look.PostRedCurve = CreateNeutralCurve();

                look.PostGreenCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0.8f));
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "green curve keys participate");
                look.PostGreenCurve = CreateNeutralCurve();

                look.PostBlueCurve = new AnimationCurve(new Keyframe(0f, 0.1f), new Keyframe(1f, 1f));
                Assert.AreNotEqual(baseline, look.ComputeParamsFingerprint(), "blue curve keys participate");
                look.PostBlueCurve = CreateNeutralCurve();

                Assert.AreEqual(baseline, look.ComputeParamsFingerprint(),
                    "reverting every parameter restores the baseline fingerprint");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        [Test]
        public void Look_IsVolumeStale_FlipsOnParameterDrift()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                // Simulate a fit followed by a Volume write: both fingerprints captured.
                look.PostFitFingerprint = "test-fit-fingerprint";
                look.VolumeWriteFingerprint = look.ComputeParamsFingerprint();
                Assert.IsFalse(look.IsVolumeStale,
                    "immediately after a write the recorded fingerprint matches — not stale (A13 starts honest)");

                look.PostSaturation = 5f;
                Assert.IsTrue(look.IsVolumeStale,
                    "a parameter tweak after the write flips the volume-stale warning on");

                look.PostSaturation = 0f;
                look.PostMasterCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0.75f));
                Assert.IsTrue(look.IsVolumeStale,
                    "a curve-key tweak after the write also flips the volume-stale warning on");

                look.PostMasterCurve = CreateNeutralCurve();
                Assert.IsFalse(look.IsVolumeStale,
                    "reverting the drift restores the match — pure comparison, no hidden state");

                // An unfitted asset never reports volume staleness, even with a stale
                // recorded fingerprint left over from a hypothetical earlier state.
                look.PostFitFingerprint = "";
                look.VolumeWriteFingerprint = "stale-leftover";
                Assert.IsFalse(look.IsVolumeStale,
                    "volume staleness is only meaningful once fitted");
            }
            finally
            {
                DestroyImmediate(look);
            }
        }

        /// <summary>
        /// Asserts the two-key neutral diagonal (0,0)→(1,1) that every fresh and every
        /// sanitized fitted-post curve must carry (re-homed 05.1 plan 02 pin).
        /// </summary>
        private static void AssertNeutralDiagonal(AnimationCurve curve, string message = "")
        {
            Assert.IsNotNull(curve, "curve must never stay null after sanitization " + message);
            Assert.AreEqual(2, curve.length, "neutral curve is exactly the two-key diagonal " + message);
            Assert.AreEqual(0f, curve.keys[0].time);
            Assert.AreEqual(0f, curve.keys[0].value);
            Assert.AreEqual(1f, curve.keys[1].time);
            Assert.AreEqual(1f, curve.keys[1].value);
        }

        private static AnimationCurve CreateNeutralCurve()
        {
            return new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 1f));
        }

        private static void DestroyImmediate(Object asset)
        {
            if (asset != null)
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}

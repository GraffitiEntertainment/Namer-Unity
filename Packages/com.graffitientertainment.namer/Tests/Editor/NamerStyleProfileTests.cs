using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05 plan 01 profile-model tests: creation defaults from the nine 05-UI-SPEC
    /// section C constants, fingerprint determinism + weight sensitivity + null-slot
    /// skipping (OQ-4), ClampToCaps serialization-abuse sanitization (T-05-03), the
    /// Assets/Create/NAMER/Style Profile menu registration, and the inspector couplings
    /// (staleness flip after a weight/reference change post-extract; the fresh-asset
    /// not-extracted state that drives the Statistics em-dash placeholder rows).
    /// 05.2-06 (R4): the fitted-post fixtures retired with the profile's post fields —
    /// the post look lives on <see cref="NamerPostLook"/> (see NamerPostLookTests).
    /// </summary>
    public class NamerStyleProfileTests
    {
        [Test]
        public void Profile_CreateInstance_HasUiSpecDefaults()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                Assert.AreEqual(0, profile.References.Count, "reference list must start empty (user-locked list model, UI-SPEC P2)");
                Assert.AreEqual(0, profile.ReferenceWeights.Count);
                Assert.AreEqual(NamerEditorConstants.DefaultStyleStrength, profile.StyleStrength);
                Assert.AreEqual(NamerEditorConstants.DefaultSmoothingStrength, profile.Smoothing);
                Assert.AreEqual(NamerEditorConstants.DefaultCavityFloor, profile.CavityFloor);
                Assert.AreEqual(NamerEditorConstants.DefaultNormalDetailReduction, profile.NormalDetailReduction);
                Assert.AreEqual(NamerEditorConstants.DefaultRoughnessSimplify, profile.RoughnessSimplify);
                Assert.AreEqual(NamerEditorConstants.DefaultHueMode, profile.HueMode);
                Assert.AreEqual(0, profile.PaletteAnchors.Count, "extracted anchors must start empty");
                Assert.IsNotNull(profile.Stats);
                Assert.AreEqual(0, profile.Stats.AnchorCount);
                Assert.IsTrue(string.IsNullOrEmpty(profile.ReferenceFingerprint));
                Assert.IsFalse(profile.IsExtracted, "a fresh asset is not extracted (Statistics rows render the em-dash placeholder)");
            }
            finally
            {
                DestroyImmediate(profile);
            }
        }

        [Test]
        public void Profile_ComputeFingerprint_IsDeterministicAndWeightSensitive()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "FingerprintFirst" };
            Texture2D second = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "FingerprintSecond" };
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(first);
                profile.ReferenceWeights.Add(1f);
                profile.References.Add(second);
                profile.ReferenceWeights.Add(0.5f);

                string firstCall = profile.ComputeFingerprint();
                string secondCall = profile.ComputeFingerprint();
                Assert.AreEqual(firstCall, secondCall, "fingerprint must be deterministic across two calls (OQ-4)");

                profile.ReferenceWeights[1] = 0.75f;
                Assert.AreNotEqual(firstCall, profile.ComputeFingerprint(),
                    "a weight change must change the fingerprint: weights apply at extract time, so any weight edit marks the profile stale (OQ-4/A4)");

                // Null slots are skipped and never break GUID/weight alignment.
                profile.References.Insert(1, null);
                profile.ReferenceWeights.Insert(1, 0.9f);
                NamerStyleProfile noNull = ScriptableObject.CreateInstance<NamerStyleProfile>();
                try
                {
                    noNull.References.Add(first);
                    noNull.ReferenceWeights.Add(1f);
                    noNull.References.Add(second);
                    noNull.ReferenceWeights.Add(0.75f);
                    Assert.AreEqual(noNull.ComputeFingerprint(), profile.ComputeFingerprint(),
                        "null slots must be skipped from the fingerprint (null slots never count as references)");
                }
                finally
                {
                    DestroyImmediate(noNull);
                }
            }
            finally
            {
                DestroyImmediate(profile);
                DestroyImmediate(first);
                DestroyImmediate(second);
            }
        }

        [Test]
        public void Profile_ClampToCaps_TruncatesReferencesAndClampsWeights()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                const int EditedSlotCount = 15;
                for (int i = 0; i < EditedSlotCount; i++)
                {
                    profile.References.Add(null);
                    profile.ReferenceWeights.Add(0.5f);
                }

                profile.ReferenceWeights[0] = -1f;
                profile.ReferenceWeights[1] = 2f;
                profile.ReferenceWeights[2] = float.NaN;

                profile.ClampToCaps();

                Assert.AreEqual(NamerEditorConstants.MaxReferences, profile.References.Count,
                    "a hand-edited asset with more than MaxReferences slots must truncate (T-05-03)");
                Assert.AreEqual(NamerEditorConstants.MaxReferences, profile.ReferenceWeights.Count,
                    "weights must stay length-synced with references");
                Assert.AreEqual(0f, profile.ReferenceWeights[0], "weight -1 clamps to 0");
                Assert.AreEqual(1f, profile.ReferenceWeights[1], "weight 2 clamps to 1");
                Assert.AreEqual(NamerEditorConstants.DefaultReferenceWeight, profile.ReferenceWeights[2],
                    "a non-numeric weight falls back to the default weight");
            }
            finally
            {
                DestroyImmediate(profile);
            }
        }


        [Test]
        public void Profile_MenuItem_IsRegistered()
        {
            MethodInfo method = typeof(NamerStyleProfile).GetMethod(
                "CreateStyleProfileAsset", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "the profile must expose the creation entry point");
            // Unity's attribute class is named MenuItem (no Attribute suffix), so the
            // generic lookup must name the attribute type exactly as declared.
            MenuItem attribute = method.GetCustomAttribute<MenuItem>();
            Assert.IsNotNull(attribute, "the creation entry point must carry a MenuItem attribute");
            Assert.AreEqual("Assets/Create/NAMER/Style Profile", attribute.menuItem,
                "the creation menu must live under the house NAMER root (STYL-01)");
        }

        [Test]
        public void Profile_IsStale_FlipsAfterReferenceOrWeightChangePostExtract()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StaleFirst" };
            Texture2D second = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StaleSecond" };
            Texture2D third = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StaleThird" };
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(first);
                profile.References.Add(second);
                profile.References.Add(third);
                profile.ReferenceWeights.Add(1f);
                profile.ReferenceWeights.Add(0.75f);
                profile.ReferenceWeights.Add(0.5f);

                // Simulate a successful Extract write: the fingerprint captured at
                // extract time equals the current refs + weights state (OQ-4).
                profile.ReferenceFingerprint = profile.ComputeFingerprint();
                Assert.IsTrue(profile.IsExtracted, "a captured fingerprint marks the profile extracted");
                Assert.IsFalse(profile.IsStale,
                    "unchanged refs + weights must not be stale — the Statistics staleness warning stays hidden");

                profile.ReferenceWeights[1] = 0.8f;
                Assert.IsTrue(profile.IsStale,
                    "any weight edit must flip the profile stale (OQ-4/A4 — weights apply at extract time)");

                profile.ReferenceWeights[1] = 0.75f;
                Assert.IsFalse(profile.IsStale,
                    "reverting the weight restores the fingerprint match — staleness is pure comparison, no hidden state");

                profile.References[2] = null;
                Assert.IsTrue(profile.IsStale,
                    "clearing an assigned reference slot must flip the profile stale (null slots are skipped from the fingerprint)");
            }
            finally
            {
                DestroyImmediate(profile);
                DestroyImmediate(first);
                DestroyImmediate(second);
                DestroyImmediate(third);
            }
        }

        [Test]
        public void Profile_FreshAsset_RendersStatisticsPlaceholderState()
        {
            NamerStyleProfile fresh = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                Assert.IsFalse(fresh.IsExtracted,
                    "a fresh asset must take the Statistics em-dash placeholder path (UI-SPEC P6)");
                Assert.AreEqual(0, fresh.Stats.AnchorCount,
                    "zeroed stats back the placeholder rows until the first Extract writes real values");
                Assert.IsFalse(fresh.IsStale,
                    "staleness is only meaningful once extracted — a fresh asset never shows the warning");
            }
            finally
            {
                DestroyImmediate(fresh);
            }
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

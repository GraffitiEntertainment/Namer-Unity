using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.3 plan 02 merged style-asset tests (STYL-15, D-05): headless pins for the
    /// <see cref="NamerStyleAsset"/> state model — fresh-instance neutrality, the
    /// THREE-INDEPENDENT-FLAG law (a migrated asset keeps palette AND fit while
    /// honestly reading not-decomposed — the M3/no-conflation pin), the
    /// decomposition/palette-source/volume staleness seams (the
    /// NamerPostLook IsFitStale idiom: DecompositionFingerprint stores the bare
    /// <see cref="NamerStyleAsset.ComputeFingerprint"/> return), ClampToCaps
    /// sanitization including the new palette-source ints and classification
    /// fractions, fingerprint determinism/sensitivity, and the dedicated
    /// StyleAssetGuid pref resolution seam (<see cref="NamerProcessor.ResolveStyleAsset"/>
    /// — never the legacy StyleProfileGuid pref, A9). Pure EditMode tests — no GPU,
    /// no Volume writes; the GUID-resolution tests use a temp folder deleted in
    /// <c>finally</c> with the EditorPrefs-backed pref snapshotted/restored in
    /// <c>finally</c> (NamerStylePostFitLifecycleTests isolation discipline).
    /// </summary>
    public class NamerStyleAssetTests
    {
        private const string TempFolder = "Assets/NAMER_StyleAsset_Tests_Temp";

        [Test]
        public void Asset_CreateInstance_HasNeutralIdentityDefaults()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // The shared list starts empty (D-05: ONE list, 0-10 entries).
                Assert.IsEmpty(asset.References, "the ONE shared reference list starts empty (D-05)");
                Assert.IsEmpty(asset.ReferenceWeights);
                Assert.AreEqual(0, asset.AssignedReferenceCount);

                // The post block carries the neutral identity defaults (05.2 section C law).
                Assert.AreEqual(0f, asset.PostTemperature);
                Assert.AreEqual(0f, asset.PostTint);
                Assert.AreEqual(0f, asset.PostExposureEv);
                Assert.AreEqual(0f, asset.PostContrast);
                Assert.AreEqual(0f, asset.PostSaturation);
                Assert.AreEqual(NamerEditorConstants.DefaultBloomThreshold, asset.PostBloomThreshold);
                Assert.AreEqual(NamerEditorConstants.DefaultBloomIntensity, asset.PostBloomIntensity);
                AssertNeutralDiagonal(asset.PostMasterCurve);
                AssertNeutralDiagonal(asset.PostRedCurve);
                AssertNeutralDiagonal(asset.PostGreenCurve);
                AssertNeutralDiagonal(asset.PostBlueCurve);
                Assert.IsTrue(string.IsNullOrEmpty(asset.PostFitFingerprint));
                Assert.IsTrue(string.IsNullOrEmpty(asset.VolumeWriteFingerprint));

                // The palette block starts empty; both palette-source ints default de-graded.
                Assert.IsEmpty(asset.PaletteAnchors);
                Assert.IsNotNull(asset.Stats);
                Assert.IsTrue(string.IsNullOrEmpty(asset.ReferenceFingerprint));
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceDeGraded, asset.PaletteSourceAtExtract,
                    "the AtExtract twin defaults to the de-graded source");
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceDeGraded, asset.PaletteSource,
                    "the S12 input flag defaults to the de-graded source");

                // The sliders carry the NamerStyleProfile defaults.
                Assert.AreEqual(NamerEditorConstants.DefaultStyleStrength, asset.StyleStrength);
                Assert.AreEqual(NamerEditorConstants.DefaultSmoothingStrength, asset.Smoothing);
                Assert.AreEqual(NamerEditorConstants.DefaultCavityFloor, asset.CavityFloor);
                Assert.AreEqual(NamerEditorConstants.DefaultNormalDetailReduction, asset.NormalDetailReduction);
                Assert.AreEqual(NamerEditorConstants.DefaultRoughnessSimplify, asset.RoughnessSimplify);
                Assert.AreEqual(NamerEditorConstants.DefaultHueMode, asset.HueMode);

                // Decomposition + validation state start never/empty.
                Assert.IsTrue(string.IsNullOrEmpty(asset.DecompositionFingerprint));
                Assert.IsTrue(string.IsNullOrEmpty(asset.GradeSourceExemplarName));
                Assert.AreEqual(-1f, asset.ConfidentAlbedoFraction, "-1 = never decomposed (the em-dash placeholder state)");
                Assert.AreEqual(-1f, asset.ContaminatedFraction, "-1 = never decomposed (the em-dash placeholder state)");
                Assert.IsTrue(string.IsNullOrEmpty(asset.ValidationResult));
                Assert.IsTrue(string.IsNullOrEmpty(asset.ValidationFingerprint));

                // All staleness seams start silent.
                Assert.IsFalse(asset.IsDecomposed, "a fresh asset is not decomposed (M3 — decomposition never auto-runs)");
                Assert.IsFalse(asset.HasFit, "a fresh asset carries no fitted post look");
                Assert.IsFalse(asset.IsDecompositionStale, "decomposition staleness is only meaningful once decomposed");
                Assert.IsFalse(asset.IsPaletteSourceStale, "palette-source staleness is only meaningful once decomposed");
                Assert.IsFalse(asset.IsVolumeStale, "volume staleness is only meaningful once fitted");
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_MigratedState_KeepsPaletteAndFitWhileNotDecomposed()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // Simulate the M3 merge carry (plan 04): palette + fit carried from
                // the split sources, NO decomposition run.
                asset.PaletteAnchors.Add(new NamerStyleAnchor { LinearColor = Color.red, Hue = 0f, Saturation = 1f, Luma = 0.2126f, Weight = 1f });
                asset.PaletteAnchors.Add(new NamerStyleAnchor { LinearColor = Color.green, Hue = 0.3333f, Saturation = 1f, Luma = 0.7152f, Weight = 1f });
                asset.PostFitFingerprint = "migrated-fit-fingerprint";
                asset.PostFitExemplarName = "MigratedExemplar";

                // THE no-conflation pin (RESEARCH Pattern 5): three INDEPENDENT
                // truths hold simultaneously — palette-present AND fitted AND
                // honestly not-decomposed. A conflated flag would silently lose one.
                Assert.Greater(asset.PaletteAnchors.Count, 0, "palette-present = PaletteAnchors.Count > 0, no separate bool");
                Assert.IsTrue(asset.HasFit, "the migrated fit survives the merge");
                Assert.IsFalse(asset.IsDecomposed, "the migrated asset honestly reads not-decomposed (M3 law)");

                // Staleness seams stay silent for the not-decomposed dimensions.
                Assert.IsFalse(asset.IsDecompositionStale);
                Assert.IsFalse(asset.IsPaletteSourceStale);

                // But the volume seam is honest: a fit without a recorded merged-asset
                // Volume write reports stale until its first Regenerate Volume.
                Assert.IsTrue(asset.IsVolumeStale,
                    "a migrated fit with no merged-asset Volume write reports volume-stale");
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_IsDecompositionStale_NotStaleAfterBareFingerprintWrite()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StyleAssetStaleFirst" };
            Texture2D second = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StyleAssetStaleSecond" };
            Texture2D third = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StyleAssetStaleThird" };
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                asset.References.Add(first);
                asset.References.Add(second);
                asset.ReferenceWeights.Add(1f);
                asset.ReferenceWeights.Add(0.5f);

                // Decompose writes the BARE ComputeFingerprint() return (plan 03 —
                // the NamerPostLook PostFitFingerprint idiom), so staleness must
                // read false immediately after the write.
                asset.DecompositionFingerprint = asset.ComputeFingerprint();
                Assert.IsTrue(asset.IsDecomposed, "a captured decomposition fingerprint marks the asset decomposed");
                Assert.IsFalse(asset.IsDecompositionStale,
                    "the bare-fingerprint write leaves the asset not stale immediately (no phantom warning)");

                asset.ReferenceWeights[1] = 0.75f;
                Assert.IsTrue(asset.IsDecompositionStale,
                    "any weight change flips the decomposition-stale warning on");

                asset.ReferenceWeights[1] = 0.5f;
                Assert.IsFalse(asset.IsDecompositionStale,
                    "reverting the weight restores the match — pure comparison, no hidden state");

                asset.References.Add(third);
                asset.ReferenceWeights.Add(1f);
                Assert.IsTrue(asset.IsDecompositionStale,
                    "an ASSIGNED reference change also flips the warning (null slots are skipped by the fingerprint, assigned slots are not)");

                asset.References.RemoveAt(asset.References.Count - 1);
                asset.ReferenceWeights.RemoveAt(asset.ReferenceWeights.Count - 1);
                Assert.IsFalse(asset.IsDecompositionStale,
                    "removing the added reference restores the match");
            }
            finally
            {
                DestroyImmediate(asset);
                DestroyImmediate(first);
                DestroyImmediate(second);
                DestroyImmediate(third);
            }
        }

        [Test]
        public void Asset_IsPaletteSourceStale_FlipsOnlyOnSourceMismatch()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StyleAssetPaletteSourceFirst" };
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // Before any decomposition the seam stays silent even if the popup moved.
                asset.PaletteSource = NamerEditorConstants.kPaletteSourceRaw;
                Assert.IsFalse(asset.IsPaletteSourceStale,
                    "palette-source staleness is only meaningful once decomposed");

                // Simulate a decomposition that ran against the de-graded source
                // (plan 03 shape): the shared list carries an assigned reference so
                // the bare ComputeFingerprint() write is NON-EMPTY (the empty string
                // is IsDecomposed's never-decomposed sentinel — an empty-list
                // fingerprint would leave the asset honestly not-decomposed), and
                // Decompose captures the source it used in the AtExtract twin.
                asset.References.Add(first);
                asset.ReferenceWeights.Add(1f);
                asset.PaletteSource = NamerEditorConstants.kPaletteSourceDeGraded;
                asset.PaletteSourceAtExtract = NamerEditorConstants.kPaletteSourceDeGraded;
                asset.DecompositionFingerprint = asset.ComputeFingerprint();
                Assert.IsTrue(asset.IsDecomposed,
                    "sanity: the simulated decomposition wrote a non-empty fingerprint");
                Assert.IsFalse(asset.IsPaletteSourceStale,
                    "matching sources are not stale");

                asset.PaletteSource = NamerEditorConstants.kPaletteSourceRaw;
                Assert.IsTrue(asset.IsPaletteSourceStale,
                    "a popup flip after the decomposition marks the pending-switch state (S12 — input for the NEXT decomposition)");

                asset.PaletteSource = NamerEditorConstants.kPaletteSourceDeGraded;
                Assert.IsFalse(asset.IsPaletteSourceStale,
                    "reverting the popup clears the mismatch");
            }
            finally
            {
                DestroyImmediate(asset);
                DestroyImmediate(first);
            }
        }

        [Test]
        public void Asset_ClampToCaps_SanitizesPostBlockSlidersAndWeights()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                for (int i = 0; i < NamerEditorConstants.MaxReferences + 3; i++)
                {
                    asset.References.Add(null);
                }

                asset.PostTemperature = 150f;
                asset.PostContrast = float.NaN;
                asset.PostBloomIntensity = 25f;
                asset.PostMasterCurve = null;
                asset.PostRedCurve = new AnimationCurve();
                asset.StyleStrength = 3f;
                asset.Smoothing = float.NaN;
                asset.CavityFloor = 0.9f;

                asset.ClampToCaps();

                // NaN falls back to the identity BEFORE clamping — Mathf.Clamp
                // propagates NaN, so the fallback must run first.
                Assert.AreEqual(0f, asset.PostContrast,
                    "a non-numeric post float falls back to neutral 0 first, then clamps");
                Assert.AreEqual(100f, asset.PostTemperature, "out-of-range post floats clamp after the NaN fallback");
                Assert.AreEqual(NamerEditorConstants.MaxBloomIntensity, asset.PostBloomIntensity, "the bloom knob clamps to its cap");

                AssertNeutralDiagonal(asset.PostMasterCurve, "a null curve resets to the neutral diagonal");
                AssertNeutralDiagonal(asset.PostRedCurve, "a degenerate curve resets to the neutral diagonal");

                // Sliders clamp to their Range bounds; NaN falls back to the default.
                Assert.AreEqual(1f, asset.StyleStrength, "style strength clamps to [0,1]");
                Assert.AreEqual(NamerEditorConstants.DefaultSmoothingStrength, asset.Smoothing,
                    "a non-numeric slider falls back to its default before clamping");
                Assert.AreEqual(0.5f, asset.CavityFloor, "cavity floor clamps to [0,0.5]");

                // The shared list truncates at MaxReferences; weights stay parallel.
                Assert.AreEqual(NamerEditorConstants.MaxReferences, asset.References.Count,
                    "the ONE shared reference list truncates at MaxReferences");
                Assert.AreEqual(NamerEditorConstants.MaxReferences, asset.ReferenceWeights.Count);
                for (int i = 0; i < asset.ReferenceWeights.Count; i++)
                {
                    Assert.AreEqual(NamerEditorConstants.DefaultReferenceWeight, asset.ReferenceWeights[i],
                        "synced weights default to DefaultReferenceWeight");
                }
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_ClampToCaps_ClampsPaletteSourceAndConfidenceFractions()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                asset.PaletteSourceAtExtract = -3;
                asset.PaletteSource = 7;
                asset.ConfidentAlbedoFraction = 1.5f;
                asset.ContaminatedFraction = -2f;

                asset.ClampToCaps();

                Assert.AreEqual(NamerEditorConstants.kPaletteSourceDeGraded, asset.PaletteSourceAtExtract,
                    "an out-of-range AtExtract clamps up to the de-graded index");
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceRaw, asset.PaletteSource,
                    "an out-of-range popup index clamps down to the raw index");
                Assert.AreEqual(1f, asset.ConfidentAlbedoFraction, "confidences clamp to [-1,1] at the top");
                Assert.AreEqual(-1f, asset.ContaminatedFraction, "confidences clamp to [-1,1] at the bottom");

                // NaN fractions fall back to the -1 never-decomposed sentinel first.
                asset.ConfidentAlbedoFraction = float.NaN;
                asset.ContaminatedFraction = float.NaN;
                asset.ClampToCaps();
                Assert.AreEqual(-1f, asset.ConfidentAlbedoFraction,
                    "a non-numeric confidence falls back to the never-decomposed sentinel, then clamps");
                Assert.AreEqual(-1f, asset.ContaminatedFraction);
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_ComputeFingerprint_IsDeterministicAndWeightSensitive()
        {
            Texture2D first = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "StyleAssetFingerprintFirst" };
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                asset.References.Add(first);
                asset.ReferenceWeights.Add(1f);

                Assert.AreEqual(asset.ComputeFingerprint(), asset.ComputeFingerprint(),
                    "the fingerprint is deterministic — two calls on the same state agree");

                string before = asset.ComputeFingerprint();
                asset.ReferenceWeights[0] = 0.5f;
                Assert.AreNotEqual(before, asset.ComputeFingerprint(),
                    "a weight edit changes the fingerprint — weights apply at decomposition time");
            }
            finally
            {
                DestroyImmediate(asset);
                DestroyImmediate(first);
            }
        }

        [Test]
        public void Asset_ComputeParamsFingerprint_IsDeterministicAndParameterSensitive()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                string baseline = asset.ComputeParamsFingerprint();
                Assert.AreEqual(baseline, asset.ComputeParamsFingerprint(),
                    "the params fingerprint is deterministic");

                asset.PostSaturation = 5f;
                Assert.AreNotEqual(baseline, asset.ComputeParamsFingerprint(), "saturation participates");
                asset.PostSaturation = 0f;

                asset.PostMasterCurve = new AnimationCurve(new Keyframe(0f, 0f), new Keyframe(1f, 0.75f));
                Assert.AreNotEqual(baseline, asset.ComputeParamsFingerprint(), "curve keys participate");
                asset.PostMasterCurve = CreateNeutralCurve();

                Assert.AreEqual(baseline, asset.ComputeParamsFingerprint(),
                    "reverting the drift restores the baseline fingerprint");
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void Asset_IsVolumeStale_FlipsOnParameterDrift()
        {
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                // A fit followed by a merged-asset Volume write: both fingerprints captured.
                asset.PostFitFingerprint = "test-fit-fingerprint";
                asset.VolumeWriteFingerprint = asset.ComputeParamsFingerprint();
                Assert.IsFalse(asset.IsVolumeStale,
                    "immediately after a write the recorded fingerprint matches (the A13 seam starts honest)");

                asset.PostSaturation = 5f;
                Assert.IsTrue(asset.IsVolumeStale,
                    "a parameter tweak after the write flips the volume-stale warning on");

                asset.PostSaturation = 0f;
                Assert.IsFalse(asset.IsVolumeStale,
                    "reverting the drift restores the match — pure comparison, no hidden state");

                // An unfitted asset never reports volume staleness, even with a stale
                // recorded fingerprint left over from a hypothetical earlier state.
                asset.PostFitFingerprint = "";
                asset.VolumeWriteFingerprint = "stale-leftover";
                Assert.IsFalse(asset.IsVolumeStale,
                    "volume staleness is only meaningful once fitted");
            }
            finally
            {
                DestroyImmediate(asset);
            }
        }

        [Test]
        public void ResolveStyleAsset_EmptyGuid_YieldsNull()
        {
            string savedGuid = new NamerProcessorSettings().StyleAssetGuid;
            try
            {
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleAssetGuid = string.Empty;
                Assert.IsNull(NamerProcessor.ResolveStyleAsset(settings),
                    "an empty GUID detaches silently — no asset, no throw");
            }
            finally
            {
                new NamerProcessorSettings().StyleAssetGuid = savedGuid;
            }
        }

        [Test]
        public void ResolveStyleAsset_ResolvesAssetByGuid()
        {
            string savedGuid = new NamerProcessorSettings().StyleAssetGuid;
            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                EnsureTempFolder();
                AssetDatabase.CreateAsset(asset, TempFolder + "/ResolveStyleAsset.asset");

                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleAssetGuid = AssetDatabase.AssetPathToGUID(TempFolder + "/ResolveStyleAsset.asset");
                NamerStyleAsset resolved = NamerProcessor.ResolveStyleAsset(settings);
                Assert.IsNotNull(resolved, "a GUID created from a real style asset resolves back to it");
                Assert.AreSame(asset, resolved);
            }
            finally
            {
                new NamerProcessorSettings().StyleAssetGuid = savedGuid;
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        [Test]
        public void ResolveStyleAsset_WrongTypePath_YieldsNull()
        {
            string savedGuid = new NamerProcessorSettings().StyleAssetGuid;
            NamerStyleProfile wrongType = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                EnsureTempFolder();
                AssetDatabase.CreateAsset(wrongType, TempFolder + "/ResolveStyleAssetWrongType.asset");

                // A live but wrong-type asset at the GUID's path resolves null —
                // the silent detach (A9), never a cast or a throw.
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleAssetGuid = AssetDatabase.AssetPathToGUID(TempFolder + "/ResolveStyleAssetWrongType.asset");
                Assert.IsNull(NamerProcessor.ResolveStyleAsset(settings),
                    "a GUID pointing at a non-style-asset resolves null (wrong type = silent detach)");
            }
            finally
            {
                new NamerProcessorSettings().StyleAssetGuid = savedGuid;
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// Asserts the two-key neutral diagonal (0,0)→(1,1) that every fresh and every
        /// sanitized fitted-post curve must carry (NamerPostLookTests helper copy).
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

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
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

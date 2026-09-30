using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.3 plan 04 merge tests (STYL-15/D-05, M3/M4/M5): pins for the
    /// <see cref="NamerStyleAssetMergerCore"/> pure carry core — no-silent-loss
    /// equality (palette anchors element-wise, stats deep-copied, every post
    /// float "R"-format, curve time/value keys, fingerprints), the deterministic
    /// reference union (profile-first, first-wins GUID dedup keeping the PROFILE
    /// slot's weight, null slots skipped), cap honesty evaluated at the
    /// <see cref="NamerStyleAssetMergerCore.CountUnionReferences"/> seam BEFORE
    /// any asset exists, the honest migrated state (palette + fit carried while
    /// not decomposed, raw palette source), and source immutability (GEN-02
    /// spirit — serialized field dumps byte-identical across
    /// <see cref="NamerStyleAssetMergerCore.BuildMerged"/>). GUID-dependent tests
    /// create real imported texture assets under a temp folder deleted in
    /// <c>finally</c> (NamerStylePostFitLifecycleTests isolation discipline);
    /// no sleeps anywhere.
    /// </summary>
    public class NamerStyleAssetMergerTests
    {
        private const string TempFolder = "Assets/NAMER_Style_Merge_Tests_Temp";

        /// <summary>
        /// No-silent-loss (05.2 D-07): every carried value survives BuildMerged
        /// exactly — anchors element-wise, stats field-for-field (deep copy, not
        /// an aliased class instance), every post float, every curve's time/value
        /// keys (tangents excluded — the ComputeParamsFingerprint precedent),
        /// and all three fingerprint strings. Awkward floats (0.1f + 0.2f) pin
        /// that the carry is verbatim, not round-tripped through a lossy format.
        /// </summary>
        [Test]
        public void Merge_NoSilentLoss_PaletteStatsPostAndSlidersCarryExactly()
        {
            float awkward = 0.1f + 0.2f;
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            NamerStyleAsset merged = null;
            try
            {
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = new Color(0.31f, 0.42f, 0.135f, 0.87f),
                    Hue = 0.083f,
                    Saturation = 0.66f,
                    Luma = 0.3729f,
                    Weight = 0.17f
                });
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = new Color(0.02f, 0.5f, 0.77f, 1f),
                    Hue = 0.55f,
                    Saturation = 0.974f,
                    Luma = awkward,
                    Weight = 0.5f
                });
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = new Color(0.9f, 0.05f, 0.5f, 0.33f),
                    Hue = 0.921f,
                    Saturation = awkward,
                    Luma = 0.25f,
                    Weight = 3.1f
                });
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = new Color(awkward, 0.11f, 0.98f, 0.44f),
                    Hue = 0.6671f,
                    Saturation = 0.12f,
                    Luma = 0.9f,
                    Weight = awkward
                });
                profile.Stats = new NamerStyleStats
                {
                    AnchorCount = 4,
                    HueFamilyCount = 3,
                    MeanSaturation = 0.4711f,
                    ShadowFraction = 0.19f,
                    MidtoneFraction = awkward,
                    HighlightFraction = 0.4f,
                    ShadowColor = new Color(0.05f, 0.06f, 0.07f, 1f),
                    MidtoneColor = new Color(0.4f, 0.41f, awkward, 1f),
                    HighlightColor = new Color(0.9f, 0.92f, 0.95f, 1f),
                    WarmCoolBias = -0.35f,
                    Contrast = 2.75f,
                    PaletteDensity = 0.0808f,
                    ReferenceCount = 7
                };
                profile.ReferenceFingerprint = "profile-extract-fingerprint";
                profile.StyleStrength = 0.85f;
                profile.Smoothing = 0.3f;
                profile.CavityFloor = 0.15f;
                profile.NormalDetailReduction = 0.65f;
                profile.RoughnessSimplify = 0.45f;
                profile.HueMode = 1;

                look.PostTemperature = 12.5f;
                look.PostTint = -8.25f;
                look.PostExposureEv = 1.75f;
                look.PostContrast = 33f;
                look.PostSaturation = -21f;
                look.PostBloomThreshold = 0.77f;
                look.PostBloomIntensity = 2.5f;
                look.PostMasterCurve = new AnimationCurve(
                    new Keyframe(0f, 0.05f), new Keyframe(0.4f, 0.45f), new Keyframe(1f, 0.9f));
                look.PostRedCurve = new AnimationCurve(new Keyframe(0f, 0.1f), new Keyframe(1f, 0.85f));
                look.PostGreenCurve = new AnimationCurve(
                    new Keyframe(0f, 0.02f), new Keyframe(0.7f, 0.5f), new Keyframe(1f, 0.95f));
                look.PostBlueCurve = new AnimationCurve(new Keyframe(0f, 0.2f), new Keyframe(1f, 0.7f));
                look.PostFitFingerprint = "look-fit-fingerprint";
                look.PostFitExemplarName = "MergeExemplar";
                look.VolumeWriteFingerprint = "look-volume-fingerprint";

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, look);

                // Palette anchors element-wise, "R" per field (the fingerprint
                // formatting law — a lossy carry cannot hide behind tolerance).
                Assert.AreEqual(profile.PaletteAnchors.Count, merged.PaletteAnchors.Count, "every anchor survives");
                for (int i = 0; i < profile.PaletteAnchors.Count; i++)
                {
                    AssertCarriedFloat(profile.PaletteAnchors[i].Hue, merged.PaletteAnchors[i].Hue, "anchor " + i + " hue");
                    AssertCarriedFloat(profile.PaletteAnchors[i].Saturation, merged.PaletteAnchors[i].Saturation, "anchor " + i + " saturation");
                    AssertCarriedFloat(profile.PaletteAnchors[i].Luma, merged.PaletteAnchors[i].Luma, "anchor " + i + " luma");
                    AssertCarriedFloat(profile.PaletteAnchors[i].Weight, merged.PaletteAnchors[i].Weight, "anchor " + i + " weight");
                    AssertCarriedColor(profile.PaletteAnchors[i].LinearColor, merged.PaletteAnchors[i].LinearColor, "anchor " + i + " linear color");
                }

                // Stats: deep copy — a distinct instance with identical fields.
                Assert.AreNotSame(profile.Stats, merged.Stats, "stats must be a deep copy, never the source instance");
                Assert.AreEqual(profile.Stats.AnchorCount, merged.Stats.AnchorCount);
                Assert.AreEqual(profile.Stats.HueFamilyCount, merged.Stats.HueFamilyCount);
                AssertCarriedFloat(profile.Stats.MeanSaturation, merged.Stats.MeanSaturation, "stats mean saturation");
                AssertCarriedFloat(profile.Stats.ShadowFraction, merged.Stats.ShadowFraction, "stats shadow fraction");
                AssertCarriedFloat(profile.Stats.MidtoneFraction, merged.Stats.MidtoneFraction, "stats midtone fraction");
                AssertCarriedFloat(profile.Stats.HighlightFraction, merged.Stats.HighlightFraction, "stats highlight fraction");
                AssertCarriedColor(profile.Stats.ShadowColor, merged.Stats.ShadowColor, "stats shadow color");
                AssertCarriedColor(profile.Stats.MidtoneColor, merged.Stats.MidtoneColor, "stats midtone color");
                AssertCarriedColor(profile.Stats.HighlightColor, merged.Stats.HighlightColor, "stats highlight color");
                AssertCarriedFloat(profile.Stats.WarmCoolBias, merged.Stats.WarmCoolBias, "stats warm-cool bias");
                AssertCarriedFloat(profile.Stats.Contrast, merged.Stats.Contrast, "stats contrast");
                AssertCarriedFloat(profile.Stats.PaletteDensity, merged.Stats.PaletteDensity, "stats palette density");
                Assert.AreEqual(profile.Stats.ReferenceCount, merged.Stats.ReferenceCount);
                Assert.AreEqual(profile.ReferenceFingerprint, merged.ReferenceFingerprint, "the extraction fingerprint carries verbatim");

                // Every post float "R"-equal.
                AssertCarriedFloat(look.PostTemperature, merged.PostTemperature, "post temperature");
                AssertCarriedFloat(look.PostTint, merged.PostTint, "post tint");
                AssertCarriedFloat(look.PostExposureEv, merged.PostExposureEv, "post exposure");
                AssertCarriedFloat(look.PostContrast, merged.PostContrast, "post contrast");
                AssertCarriedFloat(look.PostSaturation, merged.PostSaturation, "post saturation");
                AssertCarriedFloat(look.PostBloomThreshold, merged.PostBloomThreshold, "post bloom threshold");
                AssertCarriedFloat(look.PostBloomIntensity, merged.PostBloomIntensity, "post bloom intensity");

                // Curve time/value keys equal (tangents excluded — the
                // ComputeParamsFingerprint precedent); instances never shared.
                AssertCurveKeysCarried(look.PostMasterCurve, merged.PostMasterCurve, "master curve");
                AssertCurveKeysCarried(look.PostRedCurve, merged.PostRedCurve, "red curve");
                AssertCurveKeysCarried(look.PostGreenCurve, merged.PostGreenCurve, "green curve");
                AssertCurveKeysCarried(look.PostBlueCurve, merged.PostBlueCurve, "blue curve");

                // Fit provenance strings carry verbatim.
                Assert.AreEqual(look.PostFitFingerprint, merged.PostFitFingerprint, "the fit fingerprint carries");
                Assert.AreEqual(look.PostFitExemplarName, merged.PostFitExemplarName, "the exemplar name carries");
                Assert.AreEqual(look.VolumeWriteFingerprint, merged.VolumeWriteFingerprint, "the volume-write fingerprint carries");

                // Palette-application sliders + hue mode carry from the profile.
                AssertCarriedFloat(profile.StyleStrength, merged.StyleStrength, "style strength");
                AssertCarriedFloat(profile.Smoothing, merged.Smoothing, "smoothing");
                AssertCarriedFloat(profile.CavityFloor, merged.CavityFloor, "cavity floor");
                AssertCarriedFloat(profile.NormalDetailReduction, merged.NormalDetailReduction, "normal detail reduction");
                AssertCarriedFloat(profile.RoughnessSimplify, merged.RoughnessSimplify, "roughness simplify");
                Assert.AreEqual(profile.HueMode, merged.HueMode, "hue mode carries");
            }
            finally
            {
                Destroy(merged);
                Destroy(look);
                Destroy(profile);
            }
        }

        /// <summary>
        /// Union order + duplicates (T-05.3-04-04): profile [A, null, B] + look
        /// [B(duplicate), null, C] merges to [A, B, C] — profile-first, the
        /// duplicate B keeps the PROFILE slot's weight (first-wins), the look-only
        /// C appends in order, and null slots vanish entirely. Real imported
        /// textures so GUIDs exist and the dedup is exercised for real.
        /// </summary>
        [Test]
        public void Merge_Union_ProfileFirstDuplicateKeepsProfileWeight()
        {
            EnsureTempFolder();
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            NamerStyleAsset merged = null;
            try
            {
                Texture2D a = CreateImportedReference(TempFolder + "/RefA.png", new Color(0.8f, 0.1f, 0.1f));
                Texture2D b = CreateImportedReference(TempFolder + "/RefB.png", new Color(0.1f, 0.8f, 0.1f));
                Texture2D c = CreateImportedReference(TempFolder + "/RefC.png", new Color(0.1f, 0.1f, 0.8f));

                profile.References.Add(a);
                profile.References.Add(null);
                profile.References.Add(b);
                profile.ReferenceWeights.Add(1f);
                profile.ReferenceWeights.Add(0.9f);
                profile.ReferenceWeights.Add(0.25f);
                look.References.Add(b);
                look.References.Add(null);
                look.References.Add(c);
                look.ReferenceWeights.Add(0.9f);
                look.ReferenceWeights.Add(0.8f);
                look.ReferenceWeights.Add(0.6f);

                Assert.AreEqual(3, NamerStyleAssetMergerCore.CountUnionReferences(profile, look),
                    "the seam counts the would-be union before any asset exists");

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, look);

                Assert.AreEqual(3, merged.References.Count, "[A,B] + [B,C] unions to exactly [A,B,C]");
                Assert.AreSame(a, merged.References[0], "the profile's first reference leads the union");
                Assert.AreSame(b, merged.References[1], "the duplicate keeps its PROFILE slot position — carried once, not twice");
                Assert.AreSame(c, merged.References[2], "the look-only reference appends after the profile block");
                AssertCarriedFloat(1f, merged.ReferenceWeights[0], "profile weight A carries verbatim");
                AssertCarriedFloat(0.25f, merged.ReferenceWeights[1],
                    "first-wins: the PROFILE weight keeps the duplicate slot (the look's 0.9 is discarded)");
                AssertCarriedFloat(0.6f, merged.ReferenceWeights[2], "the look-only reference keeps the look's weight");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(merged);
                Destroy(look);
                Destroy(profile);
            }
        }

        /// <summary>
        /// Look-only merge (profile refs empty): the look's references append in
        /// order with their weights preserved — the palette-only + fitted-post
        /// merge shape still carries the look's list.
        /// </summary>
        [Test]
        public void Merge_UnionLookOnly_LookReferencesAppendInOrder()
        {
            EnsureTempFolder();
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            NamerStyleAsset merged = null;
            try
            {
                Texture2D d = CreateImportedReference(TempFolder + "/RefD.png", new Color(0.7f, 0.7f, 0.1f));
                Texture2D e = CreateImportedReference(TempFolder + "/RefE.png", new Color(0.1f, 0.7f, 0.7f));

                look.References.Add(d);
                look.References.Add(e);
                look.ReferenceWeights.Add(0.4f);
                look.ReferenceWeights.Add(0.7f);

                Assert.AreEqual(2, NamerStyleAssetMergerCore.CountUnionReferences(profile, look));

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, look);

                Assert.AreEqual(2, merged.References.Count, "an empty profile list appends the look's refs in order");
                Assert.AreSame(d, merged.References[0]);
                Assert.AreSame(e, merged.References[1]);
                AssertCarriedFloat(0.4f, merged.ReferenceWeights[0], "look weights carry verbatim");
                AssertCarriedFloat(0.7f, merged.ReferenceWeights[1], "look weights carry verbatim");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(merged);
                Destroy(look);
                Destroy(profile);
            }
        }

        /// <summary>
        /// Cap honesty (M3/T-05.3-04-03): 6 + 6 assigned references count to 12 —
        /// above the 10-slot cap — via the SEAM alone; the dialog's disabled
        /// condition reads this value and BuildMerged is never reached in that
        /// state (nothing is created before the gate). The dedup boundary is
        /// pinned too: two duplicates bring the union to exactly
        /// <see cref="NamerEditorConstants.MaxReferences"/>, which is AT the cap
        /// and must NOT trip the strict greater-than gate.
        /// </summary>
        [Test]
        public void Merge_CapHonesty_UnionCountExceedsMaxBeforeAnyAssetExists()
        {
            EnsureTempFolder();
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                for (int i = 0; i < 6; i++)
                {
                    profile.References.Add(CreateImportedReference(
                        TempFolder + "/CapP" + i + ".png", new Color(0.1f, 0.1f, i / 8f)));
                    profile.ReferenceWeights.Add(1f);
                    look.References.Add(CreateImportedReference(
                        TempFolder + "/CapL" + i + ".png", new Color(i / 8f, 0.1f, 0.1f)));
                    look.ReferenceWeights.Add(1f);
                }

                int unionCount = NamerStyleAssetMergerCore.CountUnionReferences(profile, look);
                Assert.AreEqual(12, unionCount, "6 + 6 distinct references count to 12");
                Assert.Greater(unionCount, NamerEditorConstants.MaxReferences,
                    "the dialog-level disabled condition (the button gate is UI; the seam value is the proof)");
                // BuildMerged is deliberately NOT called while the cap is
                // exceeded — the dialog never reaches asset creation in this
                // state (M3: the Error shows BEFORE anything is created).

                // Boundary: replace the look's first two slots with duplicates
                // of profile refs — the union drops to exactly the cap, which is
                // legal (strict >) and merges 10 without silent loss.
                look.References[0] = profile.References[0];
                look.References[1] = profile.References[1];
                int dedupedCount = NamerStyleAssetMergerCore.CountUnionReferences(profile, look);
                Assert.AreEqual(NamerEditorConstants.MaxReferences, dedupedCount,
                    "first-wins dedup drops the two duplicates from the count");
                Assert.IsFalse(dedupedCount > NamerEditorConstants.MaxReferences,
                    "a union AT the cap does not trip the strict greater-than gate");
            }
            finally
            {
                AssetDatabase.DeleteAsset(TempFolder);
                Destroy(look);
                Destroy(profile);
            }
        }

        /// <summary>
        /// State honesty with a look (M3): the migrated asset carries the palette
        /// AND the fit while honestly reading not-decomposed, the palette source
        /// flags read raw (the migrated palette came from raw-pixel extraction),
        /// and the decomposition/validation fields stay empty — decomposition
        /// does NOT auto-run.
        /// </summary>
        [Test]
        public void Merge_StateHonesty_WithLook_PaletteAndFitCarriedNotDecomposed()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            NamerStyleAsset merged = null;
            try
            {
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = Color.red, Hue = 0f, Saturation = 1f, Luma = 0.2126f, Weight = 1f
                });
                profile.ReferenceFingerprint = "state-profile-fingerprint";
                look.PostFitFingerprint = "state-look-fingerprint";

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, look);

                Assert.IsFalse(merged.IsDecomposed, "the migrated asset honestly reads not-decomposed (M3)");
                Assert.Greater(merged.PaletteAnchors.Count, 0, "the palette survives the merge (no-silent-loss)");
                Assert.IsTrue(merged.HasFit, "the fitted post look survives the merge");
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceRaw, merged.PaletteSourceAtExtract,
                    "the migrated palette came from raw-pixel extraction — the honesty flag");
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceRaw, merged.PaletteSource,
                    "the input flag starts at the same raw source (no pending-switch staleness)");
                Assert.IsEmpty(merged.DecompositionFingerprint, "decomposition does NOT auto-run");
                Assert.IsEmpty(merged.GradeSourceExemplarName);
                Assert.IsEmpty(merged.ValidationResult);
                Assert.IsEmpty(merged.ValidationFingerprint);
            }
            finally
            {
                Destroy(merged);
                Destroy(look);
                Destroy(profile);
            }
        }

        /// <summary>
        /// State honesty, look-less variant (M3 copy path): the palette still
        /// carries, the post block stays at its neutral identity (no fit
        /// fabricated), and the raw palette-source flag still applies.
        /// </summary>
        [Test]
        public void Merge_StateHonesty_LookLess_PaletteCarriedWithoutFit()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerStyleAsset merged = null;
            try
            {
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = Color.green, Hue = 0.3333f, Saturation = 1f, Luma = 0.7152f, Weight = 1f
                });
                profile.ReferenceFingerprint = "state-lookless-fingerprint";

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, null);

                Assert.IsFalse(merged.IsDecomposed, "not decomposed — no look, no decomposition");
                Assert.Greater(merged.PaletteAnchors.Count, 0, "the palette survives the look-less merge");
                Assert.IsFalse(merged.HasFit, "no look means no fit — nothing fabricated");
                Assert.IsEmpty(merged.PostFitFingerprint);
                Assert.IsEmpty(merged.PostFitExemplarName);
                Assert.IsEmpty(merged.VolumeWriteFingerprint);
                Assert.AreEqual(0f, merged.PostTemperature, "the post block stays at the neutral identity");
                Assert.AreEqual(0f, merged.PostContrast);
                Assert.AreEqual(NamerEditorConstants.kPaletteSourceRaw, merged.PaletteSourceAtExtract,
                    "the raw-source honesty flag applies to the look-less variant too");
            }
            finally
            {
                Destroy(merged);
                Destroy(profile);
            }
        }

        /// <summary>
        /// Sources untouched (M5/GEN-02 spirit): the serialized state of both
        /// carry sources — fingerprint strings plus full field dumps — is
        /// byte-identical before and after BuildMerged. The merge reads; it never
        /// writes back.
        /// </summary>
        [Test]
        public void Merge_SourcesUntouched_SerializedStateIdenticalBeforeAfter()
        {
            Texture2D first = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "MergeSourceFirst" };
            Texture2D second = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "MergeSourceSecond" };
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            NamerStyleAsset merged = null;
            try
            {
                profile.References.Add(first);
                profile.References.Add(second);
                profile.ReferenceWeights.Add(0.8f);
                profile.ReferenceWeights.Add(0.2f);
                profile.PaletteAnchors.Add(new NamerStyleAnchor
                {
                    LinearColor = Color.cyan, Hue = 0.5f, Saturation = 1f, Luma = 0.7f, Weight = 0.9f
                });
                profile.ReferenceFingerprint = profile.ComputeFingerprint();
                profile.StyleStrength = 0.6f;
                look.References.Add(second);
                look.ReferenceWeights.Add(0.5f);
                look.PostTemperature = 5f;
                look.PostFitFingerprint = "untouched-fit-fingerprint";
                look.VolumeWriteFingerprint = look.ComputeParamsFingerprint();

                string profileBefore = DumpProfileState(profile);
                string lookBefore = DumpLookState(look);

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, look);

                Assert.AreEqual(profileBefore, DumpProfileState(profile),
                    "the merge never modifies the source profile (M5) — field dump identical");
                Assert.AreEqual(lookBefore, DumpLookState(look),
                    "the merge never modifies the source look (M5) — field dump identical");
            }
            finally
            {
                Destroy(merged);
                Destroy(look);
                Destroy(profile);
                Destroy(first);
                Destroy(second);
            }
        }

        /// <summary>
        /// ClampToCaps runs inside BuildMerged (T-05.3-04-01): a hand-edited
        /// profile with an over-cap reference count and out-of-range/NaN weights
        /// survives the merge with the merged lists truncated and sanitized.
        /// </summary>
        [Test]
        public void Merge_ClampToCaps_TruncatesOverCapCountAndSanitizesWeights()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerStyleAsset merged = null;
            List<Texture2D> fixtures = new List<Texture2D>();
            try
            {
                for (int i = 0; i < NamerEditorConstants.MaxReferences + 2; i++)
                {
                    Texture2D fixture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "MergeClamp" + i };
                    fixtures.Add(fixture);
                    profile.References.Add(fixture);
                }

                profile.ReferenceWeights.Add(2.5f);
                profile.ReferenceWeights.Add(float.NaN);
                profile.ReferenceWeights.Add(-0.5f);

                merged = NamerStyleAssetMergerCore.BuildMerged(profile, null);

                Assert.AreEqual(NamerEditorConstants.MaxReferences, merged.References.Count,
                    "an over-cap source count truncates to MaxReferences on merge");
                Assert.AreEqual(NamerEditorConstants.MaxReferences, merged.ReferenceWeights.Count,
                    "weights stay parallel after the truncation");
                AssertCarriedFloat(1f, merged.ReferenceWeights[0], "an above-range weight clamps to 1");
                AssertCarriedFloat(
                    NamerEditorConstants.DefaultReferenceWeight, merged.ReferenceWeights[1],
                    "a non-numeric weight falls back to the default before clamping");
                AssertCarriedFloat(0f, merged.ReferenceWeights[2], "a below-range weight clamps to 0");
            }
            finally
            {
                Destroy(merged);
                Destroy(profile);
                for (int i = 0; i < fixtures.Count; i++)
                {
                    Destroy(fixtures[i]);
                }
            }
        }

        /// <summary>
        /// Float carry assertion: invariant round-trip string equality — the
        /// ComputeParamsFingerprint formatting law, so a lossy intermediate
        /// (double cast, truncated format, re-encoded byte) fails loudly.
        /// </summary>
        private static void AssertCarriedFloat(float expected, float actual, string field)
        {
            Assert.AreEqual(
                expected.ToString("R", CultureInfo.InvariantCulture),
                actual.ToString("R", CultureInfo.InvariantCulture),
                field + " must survive the merge bit-for-bit");
        }

        /// <summary>Color carry assertion: per-channel round-trip string equality.</summary>
        private static void AssertCarriedColor(Color expected, Color actual, string field)
        {
            AssertCarriedFloat(expected.r, actual.r, field + " r");
            AssertCarriedFloat(expected.g, actual.g, field + " g");
            AssertCarriedFloat(expected.b, actual.b, field + " b");
            AssertCarriedFloat(expected.a, actual.a, field + " a");
        }

        /// <summary>
        /// Curve carry assertion: same key count, every key's time and value
        /// round-trip-equal (tangents excluded — the ComputeParamsFingerprint
        /// precedent), and never the same instance (deep-copy law).
        /// </summary>
        private static void AssertCurveKeysCarried(AnimationCurve expected, AnimationCurve actual, string name)
        {
            Assert.IsNotNull(actual, name + " must never be null after the merge (ClampToCaps guarantees a curve)");
            Assert.AreNotSame(expected, actual, name + " must be a deep copy — never a shared instance across assets");
            Assert.AreEqual(expected.length, actual.length, name + " key count");
            for (int i = 0; i < expected.length; i++)
            {
                AssertCarriedFloat(expected.keys[i].time, actual.keys[i].time, name + " key " + i + " time");
                AssertCarriedFloat(expected.keys[i].value, actual.keys[i].value, name + " key " + i + " value");
            }
        }

        /// <summary>
        /// Full serialized dump of one profile: reference fingerprint, palette
        /// anchors, stats, sliders, hue mode, extraction fingerprint — the
        /// source-immutability comparison payload.
        /// </summary>
        private static string DumpProfileState(NamerStyleProfile profile)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("refs=").Append(profile.ComputeFingerprint());
            builder.Append("|extractFp=").Append(profile.ReferenceFingerprint);
            builder.Append("|anchors=");
            for (int i = 0; i < profile.PaletteAnchors.Count; i++)
            {
                NamerStyleAnchor anchor = profile.PaletteAnchors[i];
                builder.Append('(')
                    .Append(anchor.Hue.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.Saturation.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.Luma.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.Weight.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.LinearColor.r.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.LinearColor.g.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                    .Append(anchor.LinearColor.b.ToString("R", CultureInfo.InvariantCulture)).Append(')');
            }

            builder.Append("|stats=").Append(DumpStats(profile.Stats));
            builder.Append("|sliders=")
                .Append(profile.StyleStrength.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(profile.Smoothing.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(profile.CavityFloor.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(profile.NormalDetailReduction.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(profile.RoughnessSimplify.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(profile.HueMode);
            return builder.ToString();
        }

        /// <summary>
        /// Full serialized dump of one look: reference fingerprint, the params
        /// fingerprint (every post float + all curve keys), and the three
        /// provenance strings.
        /// </summary>
        private static string DumpLookState(NamerPostLook look)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append("refs=").Append(look.ComputeFingerprint());
            builder.Append("|params=").Append(look.ComputeParamsFingerprint());
            builder.Append("|fitFp=").Append(look.PostFitFingerprint);
            builder.Append("|exemplar=").Append(look.PostFitExemplarName);
            builder.Append("|volumeFp=").Append(look.VolumeWriteFingerprint);
            return builder.ToString();
        }

        /// <summary>Field dump of one stats block (int + float + color fields, round-trip formatted).</summary>
        private static string DumpStats(NamerStyleStats stats)
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(stats.AnchorCount).Append(',')
                .Append(stats.HueFamilyCount).Append(',')
                .Append(stats.MeanSaturation.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.ShadowFraction.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.MidtoneFraction.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.HighlightFraction.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.ShadowColor.r.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.ShadowColor.g.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.ShadowColor.b.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.MidtoneColor.r.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.MidtoneColor.g.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.MidtoneColor.b.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.HighlightColor.r.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.HighlightColor.g.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.HighlightColor.b.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.WarmCoolBias.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.Contrast.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.PaletteDensity.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                .Append(stats.ReferenceCount);
            return builder.ToString();
        }

        /// <summary>
        /// Creates a real imported texture asset so its GUID exists (the
        /// CreateImportedBaseMap donor: PNG bytes + ImportAsset + load; the color
        /// content is irrelevant here — the texture is an identity token for the
        /// GUID dedup).
        /// </summary>
        private static Texture2D CreateImportedReference(string path, Color color)
        {
            Texture2D source = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
            source.SetPixel(0, 0, color);
            source.SetPixel(1, 1, color);
            source.Apply();
            File.WriteAllBytes(path, source.EncodeToPNG());
            Destroy(source);
            AssetDatabase.ImportAsset(path);
            Texture2D loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Assert.IsNotNull(loaded, "the imported reference fixture must load back at " + path);
            return loaded;
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            }
        }

        private static void Destroy(UnityEngine.Object asset)
        {
            if (asset != null)
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}

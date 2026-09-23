using System.IO;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Phase 05 plan 01 extraction tests (STYL-02, D-04): the Bake_IsDeterministic
    /// element-wise bar replicated for palette anchors and every Stats field, the
    /// stat oracles on synthetic readable references (pure-red hue, half-black/
    /// half-white contrast + bands, warm vs cool bias, the weight-0 exclusion oracle
    /// A4), the unreadable-reference error contract (OQ-1 — named texture, no
    /// exception), and cancellation with no partial result. All synthetic references
    /// are in-memory RGBA32 textures (always readable, no importer, treated as
    /// linear); every Extract runs headless via the injectable shouldCancel path, so
    /// no editor progress UI appears. The unreadable path uses a PNG imported under a
    /// Temp folder with Read/Write disabled (NamerAOBakeTests TempFolder precedent),
    /// cleaned up in finally.
    /// </summary>
    public class NamerStyleExtractionTests
    {
        private const string TempFolder = "Assets/NAMER_Style_Tests_Temp";

        // Oracle thresholds — named local constants, never inline numbers.
        private const float kRedHueTolerance = 1e-3f;
        private const float kSaturatedMeanFloor = 0.9f;
        private const float kChromaticSaturationFloor = 0.15f;
        private const float kContrastOracleMinimum = 8f;
        private const float kBandFractionMinimum = 0.3f;
        private const float kWarmBiasMinimum = 0.5f;
        private const float kCoolBiasMaximum = -0.5f;
        private const float kWeightOracleTolerance = 1e-6f;

        [Test]
        public void Extract_AnchorsAndStats_AreElementWiseIdenticalAcrossTwoExtracts()
        {
            Texture2D red = CreateSolidTexture("DeterminismRed", new Color(1f, 0f, 0f, 1f));
            Texture2D checker = CreateCheckerTexture("DeterminismChecker");
            Texture2D split = CreateHalfSplitTexture("DeterminismSplit", 64);
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(red);
                profile.References.Add(checker);
                profile.References.Add(split);
                profile.ReferenceWeights.Add(1f);
                profile.ReferenceWeights.Add(0.75f);
                profile.ReferenceWeights.Add(0.5f);

                NamerStyleExtractResult first = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                NamerStyleExtractResult second = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.IsNotNull(first.Anchors);
                Assert.IsNotNull(second.Anchors);
                Assert.IsNotNull(first.Stats);
                Assert.IsNotNull(second.Stats);

                Assert.AreEqual(first.Anchors.Length, second.Anchors.Length);
                for (int i = 0; i < first.Anchors.Length; i++)
                {
                    Assert.AreEqual(first.Anchors[i].LinearColor, second.Anchors[i].LinearColor,
                        "anchor colors must be element-wise identical across two extracts (fixed order + fixed float math, D-04)");
                    Assert.AreEqual(first.Anchors[i].Hue, second.Anchors[i].Hue,
                        "anchor hue must be element-wise identical across two extracts");
                    Assert.AreEqual(first.Anchors[i].Saturation, second.Anchors[i].Saturation,
                        "anchor saturation must be element-wise identical across two extracts");
                    Assert.AreEqual(first.Anchors[i].Luma, second.Anchors[i].Luma,
                        "anchor luma must be element-wise identical across two extracts");
                    Assert.AreEqual(first.Anchors[i].Weight, second.Anchors[i].Weight,
                        "anchor weight must be element-wise identical across two extracts");
                }

                Assert.AreEqual(first.Stats.AnchorCount, second.Stats.AnchorCount,
                    "stats must be element-wise identical across two extracts");
                Assert.AreEqual(first.Stats.HueFamilyCount, second.Stats.HueFamilyCount);
                Assert.AreEqual(first.Stats.MeanSaturation, second.Stats.MeanSaturation);
                Assert.AreEqual(first.Stats.ShadowFraction, second.Stats.ShadowFraction);
                Assert.AreEqual(first.Stats.MidtoneFraction, second.Stats.MidtoneFraction);
                Assert.AreEqual(first.Stats.HighlightFraction, second.Stats.HighlightFraction);
                Assert.AreEqual(first.Stats.ShadowColor, second.Stats.ShadowColor);
                Assert.AreEqual(first.Stats.MidtoneColor, second.Stats.MidtoneColor);
                Assert.AreEqual(first.Stats.HighlightColor, second.Stats.HighlightColor);
                Assert.AreEqual(first.Stats.WarmCoolBias, second.Stats.WarmCoolBias);
                Assert.AreEqual(first.Stats.Contrast, second.Stats.Contrast);
                Assert.AreEqual(first.Stats.PaletteDensity, second.Stats.PaletteDensity);
                Assert.AreEqual(first.Stats.ReferenceCount, second.Stats.ReferenceCount);
                Assert.AreEqual(first.Fingerprint, second.Fingerprint);
            }
            finally
            {
                Destroy(profile, red, checker, split);
            }
        }

        [Test]
        public void Extract_PureRedRef_ProducesRedChromaticAnchorAndSaturatedMean()
        {
            Texture2D red = CreateSolidTexture("OracleRed", new Color(1f, 0f, 0f, 1f));
            Texture2D darkRed = CreateSolidTexture("OracleDarkRed", new Color(0.5f, 0f, 0f, 1f));
            Texture2D orange = CreateSolidTexture("OracleOrange", new Color(1f, 0.5f, 0f, 1f));
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(red);
                profile.References.Add(darkRed);
                profile.References.Add(orange);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.IsNotNull(result.Anchors, "three readable refs must extract successfully");
                bool hasRedAnchor = false;
                for (int i = 0; i < result.Anchors.Length; i++)
                {
                    NamerStyleAnchor anchor = result.Anchors[i];
                    if (anchor.Saturation >= kChromaticSaturationFloor && Mathf.Abs(anchor.Hue) <= kRedHueTolerance)
                    {
                        hasRedAnchor = true;
                        break;
                    }
                }

                Assert.IsTrue(hasRedAnchor,
                    "a chromatic anchor with hue within 1e-3 of 0 (red) must exist for a pure-red reference");
                Assert.Greater(result.Stats.MeanSaturation, kSaturatedMeanFloor,
                    "an all-chromatic saturated reference set must report MeanSaturation > 0.9");
            }
            finally
            {
                Destroy(profile, red, darkRed, orange);
            }
        }

        [Test]
        public void Extract_BlackWhiteRef_HighContrastWithShadowAndHighlightMass()
        {
            Texture2D first = CreateHalfSplitTexture("OracleSplitA", 64);
            Texture2D second = CreateHalfSplitTexture("OracleSplitB", 64);
            Texture2D third = CreateHalfSplitTexture("OracleSplitC", 64);
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(first);
                profile.References.Add(second);
                profile.References.Add(third);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.IsNotNull(result.Anchors);
                Assert.Greater(result.Stats.Contrast, kContrastOracleMinimum,
                    "half-black/half-white references must report luma p90/p10 contrast > 8");
                Assert.Greater(result.Stats.HighlightFraction, kBandFractionMinimum,
                    "the white half must put > 0.3 of the mass in the highlight band");
                Assert.Greater(result.Stats.ShadowFraction, kBandFractionMinimum,
                    "the black half must put > 0.3 of the mass in the shadow band");
            }
            finally
            {
                Destroy(profile, first, second, third);
            }
        }

        [Test]
        public void Extract_WarmAndCoolRefs_ReportOppositeBiases()
        {
            Texture2D orangeA = CreateSolidTexture("WarmOrangeA", new Color(1f, 0.5f, 0f, 1f));
            Texture2D orangeB = CreateSolidTexture("WarmOrangeB", new Color(1f, 0.6f, 0f, 1f));
            Texture2D orangeC = CreateSolidTexture("WarmOrangeC", new Color(1f, 0.4f, 0f, 1f));
            Texture2D cyanA = CreateSolidTexture("CoolCyanA", new Color(0f, 1f, 1f, 1f));
            Texture2D cyanB = CreateSolidTexture("CoolCyanB", new Color(0f, 0.9f, 1f, 1f));
            Texture2D cyanC = CreateSolidTexture("CoolCyanC", new Color(0f, 1f, 0.9f, 1f));
            NamerStyleProfile warmProfile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerStyleProfile coolProfile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                warmProfile.References.Add(orangeA);
                warmProfile.References.Add(orangeB);
                warmProfile.References.Add(orangeC);
                coolProfile.References.Add(cyanA);
                coolProfile.References.Add(cyanB);
                coolProfile.References.Add(cyanC);

                NamerStyleExtractResult warm = NamerStyleExtractor.Extract(warmProfile, shouldCancel: NeverCancel);
                NamerStyleExtractResult cool = NamerStyleExtractor.Extract(coolProfile, shouldCancel: NeverCancel);
                Assert.IsNotNull(warm.Anchors);
                Assert.IsNotNull(cool.Anchors);
                Assert.Greater(warm.Stats.WarmCoolBias, kWarmBiasMinimum,
                    "solid orange references must report WarmCoolBias > 0.5 (warm hue sector)");
                Assert.Less(cool.Stats.WarmCoolBias, kCoolBiasMaximum,
                    "solid cyan-blue references must report WarmCoolBias < -0.5 (cool hue sector)");
            }
            finally
            {
                Destroy(warmProfile, orangeA, orangeB, orangeC);
                Destroy(coolProfile, cyanA, cyanB, cyanC);
            }
        }

        [Test]
        public void Extract_ZeroWeightReference_ContributesNothing()
        {
            Texture2D first = CreateSolidTexture("WeightFirst", new Color(1f, 0f, 0f, 1f));
            Texture2D second = CreateHalfSplitTexture("WeightSecond", 32);
            Texture2D third = CreateCheckerTexture("WeightThird");
            Texture2D ignored = CreateSolidTexture("WeightIgnored", new Color(1f, 0f, 1f, 1f));
            NamerStyleProfile weighted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerStyleProfile control = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                // A4 as planned (a two-ref extract vs a single-ref control) is
                // unsatisfiable: MinReferences = 3 honestly fails any extract with
                // fewer than three assigned refs. Content-independence form instead:
                // both sides carry three assigned refs with weights [1, 1, 0] and
                // differ ONLY in the weight-0 slot's texture (checker vs magenta).
                // Element-wise equal anchors and stats prove the weight-0 reference's
                // content contributes nothing — the same A4 claim, stronger form.
                weighted.References.Add(first);
                weighted.References.Add(second);
                weighted.References.Add(third);
                weighted.ReferenceWeights.Add(1f);
                weighted.ReferenceWeights.Add(1f);
                weighted.ReferenceWeights.Add(0f);
                control.References.Add(first);
                control.References.Add(second);
                control.References.Add(ignored);
                control.ReferenceWeights.Add(1f);
                control.ReferenceWeights.Add(1f);
                control.ReferenceWeights.Add(0f);

                NamerStyleExtractResult withZeroWeight = NamerStyleExtractor.Extract(weighted, shouldCancel: NeverCancel);
                NamerStyleExtractResult without = NamerStyleExtractor.Extract(control, shouldCancel: NeverCancel);
                Assert.IsNotNull(withZeroWeight.Anchors);
                Assert.IsNotNull(without.Anchors);
                Assert.AreEqual(withZeroWeight.Anchors.Length, without.Anchors.Length);
                for (int i = 0; i < without.Anchors.Length; i++)
                {
                    Assert.AreEqual(without.Anchors[i].LinearColor.r, withZeroWeight.Anchors[i].LinearColor.r, kWeightOracleTolerance,
                        "a weight-0 reference must not move any anchor (R)");
                    Assert.AreEqual(without.Anchors[i].LinearColor.g, withZeroWeight.Anchors[i].LinearColor.g, kWeightOracleTolerance,
                        "a weight-0 reference must not move any anchor (G)");
                    Assert.AreEqual(without.Anchors[i].LinearColor.b, withZeroWeight.Anchors[i].LinearColor.b, kWeightOracleTolerance,
                        "a weight-0 reference must not move any anchor (B)");
                    Assert.AreEqual(without.Anchors[i].Hue, withZeroWeight.Anchors[i].Hue, kWeightOracleTolerance);
                    Assert.AreEqual(without.Anchors[i].Saturation, withZeroWeight.Anchors[i].Saturation, kWeightOracleTolerance);
                    Assert.AreEqual(without.Anchors[i].Luma, withZeroWeight.Anchors[i].Luma, kWeightOracleTolerance);
                    Assert.AreEqual(without.Anchors[i].Weight, withZeroWeight.Anchors[i].Weight, kWeightOracleTolerance);
                }

                Assert.AreEqual(without.Stats.AnchorCount, withZeroWeight.Stats.AnchorCount);
                Assert.AreEqual(without.Stats.HueFamilyCount, withZeroWeight.Stats.HueFamilyCount);
                Assert.AreEqual(without.Stats.MeanSaturation, withZeroWeight.Stats.MeanSaturation, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.ShadowFraction, withZeroWeight.Stats.ShadowFraction, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.MidtoneFraction, withZeroWeight.Stats.MidtoneFraction, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.HighlightFraction, withZeroWeight.Stats.HighlightFraction, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.ShadowColor, withZeroWeight.Stats.ShadowColor);
                Assert.AreEqual(without.Stats.MidtoneColor, withZeroWeight.Stats.MidtoneColor);
                Assert.AreEqual(without.Stats.HighlightColor, withZeroWeight.Stats.HighlightColor);
                Assert.AreEqual(without.Stats.WarmCoolBias, withZeroWeight.Stats.WarmCoolBias, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.Contrast, withZeroWeight.Stats.Contrast, kWeightOracleTolerance);
                Assert.AreEqual(without.Stats.PaletteDensity, withZeroWeight.Stats.PaletteDensity, kWeightOracleTolerance);
                // ReferenceCount honestly reports how many references were read —
                // assigned refs are counted, weight-0 included, on both sides.
                Assert.AreEqual(3, without.Stats.ReferenceCount);
                Assert.AreEqual(3, withZeroWeight.Stats.ReferenceCount);
            }
            finally
            {
                Destroy(weighted, control, first, second, third, ignored);
            }
        }

        [Test]
        public void Extract_UnreadableReference_ReturnsErrorTextureNameWithoutThrowing()
        {
            Texture2D readableA = CreateSolidTexture("ReadableA", new Color(1f, 0f, 0f, 1f));
            Texture2D readableB = CreateSolidTexture("ReadableB", new Color(0f, 1f, 0f, 1f));
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                Directory.CreateDirectory(TempFolder);
                Texture2D pngSource = CreateSolidTexture("UnreadableSource", new Color(0.5f, 0.5f, 0.5f, 1f));
                byte[] png = pngSource.EncodeToPNG();
                DestroyImmediate(pngSource);
                string assetPath = TempFolder + "/UnreadableRef.png";
                File.WriteAllBytes(assetPath, png);
                AssetDatabase.ImportAsset(assetPath);
                TextureImporter importer = (TextureImporter)AssetImporter.GetAtPath(assetPath);
                Assert.IsNotNull(importer, "the temp PNG must import as a TextureImporter asset");
                importer.isReadable = false;
                importer.SaveAndReimport();
                Texture2D unreadable = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                Assert.IsNotNull(unreadable, "the reimported texture must load");
                Assert.IsFalse(unreadable.isReadable, "the fixture must be imported with Read/Write disabled");

                profile.References.Add(readableA);
                profile.References.Add(unreadable);
                profile.References.Add(readableB);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.AreEqual("UnreadableRef", result.ErrorTextureName,
                    "the unreadable texture must be named in the error result (OQ-1), never a raw exception");
                Assert.IsNull(result.Anchors, "a failed extract must return no partial anchors");
                Assert.IsNull(result.Stats);
                Assert.IsFalse(result.Cancelled);
            }
            finally
            {
                if (Directory.Exists(TempFolder))
                {
                    AssetDatabase.DeleteAsset(TempFolder);
                }

                Destroy(profile, readableA, readableB);
            }
        }

        [Test]
        public void Extract_CancelledBeforeCompletion_ReturnsNoPartialResult()
        {
            Texture2D first = CreateSolidTexture("CancelA", new Color(1f, 0f, 0f, 1f));
            Texture2D second = CreateSolidTexture("CancelB", new Color(0f, 1f, 0f, 1f));
            Texture2D third = CreateSolidTexture("CancelC", new Color(0f, 0f, 1f, 1f));
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(first);
                profile.References.Add(second);
                profile.References.Add(third);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: AlwaysCancel);
                Assert.IsTrue(result.Cancelled, "an immediate cancel must return the cancelled result");
                Assert.IsNull(result.Anchors, "no partial anchors may be returned");
                Assert.IsNull(result.Stats);
                Assert.IsTrue(string.IsNullOrEmpty(result.Fingerprint), "no fingerprint may be returned");
            }
            finally
            {
                Destroy(profile, first, second, third);
            }
        }

        [Test]
        public void Extract_TooFewReferences_ReturnsHonestFailure()
        {
            Texture2D only = CreateSolidTexture("OnlyRef", new Color(1f, 0f, 0f, 1f));
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.References.Add(only);

                NamerStyleExtractResult result = NamerStyleExtractor.Extract(profile, shouldCancel: NeverCancel);
                Assert.IsTrue(result.TooFewReferences,
                    "below MinReferences the API must report the failure honestly (the UI prevents this via the disabled Extract button)");
                Assert.IsNull(result.Anchors);
                Assert.IsNull(result.ErrorTextureName);
            }
            finally
            {
                Destroy(profile, only);
            }
        }

        private static bool NeverCancel()
        {
            return false;
        }

        private static bool AlwaysCancel()
        {
            return true;
        }

        private static Texture2D CreateSolidTexture(string textureName, Color color)
        {
            const int SolidSize = 32;
            return CreateFilledTexture(textureName, SolidSize, _ => color);
        }

        private static Texture2D CreateHalfSplitTexture(string textureName, int size)
        {
            Color white = new Color(1f, 1f, 1f, 1f);
            Color black = new Color(0f, 0f, 0f, 1f);
            return CreateFilledTexture(textureName, size, index =>
            {
                int y = index / size;
                return y < size / 2 ? white : black;
            });
        }

        private static Texture2D CreateCheckerTexture(string textureName)
        {
            const int CheckerSize = 32;
            const int CheckerCell = 8;
            Color[] palette =
            {
                new Color(1f, 0f, 0f, 1f),
                new Color(0f, 1f, 0f, 1f),
                new Color(0f, 0f, 1f, 1f),
                new Color(1f, 1f, 1f, 1f),
            };
            return CreateFilledTexture(textureName, CheckerSize, index =>
            {
                int x = index % CheckerSize;
                int y = index / CheckerSize;
                int cell = (x / CheckerCell + y / CheckerCell) % palette.Length;
                return palette[cell];
            });
        }

        private static Texture2D CreateFilledTexture(string textureName, int size, System.Func<int, Color> fill)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = textureName };
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = fill(i);
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void Destroy(params Object[] assets)
        {
            foreach (Object asset in assets)
            {
                DestroyImmediate(asset);
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

using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 D-04 (STYL-09) / 05.2 plan 04 (W10) Stylized + Post lens tests, pinned
    /// against the ATTACHED merged <see cref="NamerStyleAsset"/>'s fitted post
    /// look (05.3-05 retarget; the legacy <see cref="NamerPostLook"/> resolver
    /// test below stays as the migration-source pin): the popup option
    /// table, the caption seam, the ApplyPostLook material-uniform contract
    /// (full-strength direct asset reads), the bloom-threshold conversion, the
    /// GUID round-trip of the selection row (W9), and the pane-bloom renderer
    /// state. Mirrors <see cref="NamerStylizationIntegrationTests"/> pure-seam
    /// patterns and <see cref="NamerDipSwitchTests"/> material-uniform idioms.
    /// </summary>
    public class NamerPostPreviewTests
    {
        private const string TempFolder = "Assets/NAMER_PostPreview_Tests_Temp";

        /// <summary>
        /// The After View popup offers exactly three entries, index-parallel to
        /// kAfterViewNamer/kAfterViewStylized/kAfterViewPost (W5).
        /// </summary>
        [Test]
        public void AfterViewPopup_HasThreeEntries_InOrder()
        {
            Assert.AreEqual(2, NamerEditorWindow.kAfterViewPost,
                "the post lens is the third popup entry (index 2)");
            CollectionAssert.AreEqual(
                new[] { "NAMER", "Stylized NAMER", "Stylized + Post" },
                NamerEditorWindow.AfterViewOptions,
                "popup options must stay index-parallel to the kAfterView* constants");
        }

        /// <summary>
        /// Caption seam (W7): the post state returns the locked post caption and
        /// suppresses the Before/After flipper; the stylized state keeps its caption;
        /// only the un-stylized state draws the flipper. W13: the caption is
        /// UNCHANGED by the 05.2 asset rewire.
        /// </summary>
        [Test]
        public void ResolveAlbedoPreviewCaption_PostState_ReturnsPostCaptionAndNoFlipper()
        {
            string postCaption = NamerEditorWindow.ResolveAlbedoPreviewCaption(
                NamerEditorWindow.kAfterViewPost, out bool postFlipper);
            Assert.AreEqual(NamerEditorWindow.AlbedoPreviewPostCaption, postCaption,
                "the post lens must show the locked W7 post caption");
            Assert.IsFalse(postFlipper, "the post state must not draw the View Stylized flipper");

            string stylizedCaption = NamerEditorWindow.ResolveAlbedoPreviewCaption(
                NamerEditorWindow.kAfterViewStylized, out bool stylizedFlipper);
            Assert.AreEqual(NamerEditorWindow.AlbedoPreviewStylizedCaption, stylizedCaption);
            Assert.IsFalse(stylizedFlipper, "the stylized state also replaces the flipper");

            string namerCaption = NamerEditorWindow.ResolveAlbedoPreviewCaption(
                NamerEditorWindow.kAfterViewNamer, out bool namerFlipper);
            Assert.AreEqual(NamerEditorWindow.AlbedoPreviewUnstylizedCaption, namerCaption);
            Assert.IsTrue(namerFlipper, "only the un-stylized state draws the flipper");
        }

        /// <summary>
        /// W9 selection round-trip: the GUID the row persists resolves back to the
        /// SAME asset instance (domain-reload-safe attach mechanism, the style-row
        /// mirror), and an empty GUID resolves null (the silent detach — the W10
        /// fallback to plain Stylized NAMER). The EditorPrefs-backed pref is saved
        /// and restored in finally so the test never leaks the selection.
        /// </summary>
        [Test]
        public void ResolvePostLook_RoundTrip_ResolvesSelectedAssetAndNullOnEmptyGuid()
        {
            string savedGuid = new NamerProcessorSettings().PostLookGuid;
            EnsureTempFolder();
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            try
            {
                AssetDatabase.CreateAsset(look, TempFolder + "/PostPreviewLook.asset");
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.PostLookGuid = string.Empty;
                Assert.IsNull(NamerProcessor.ResolvePostLook(settings),
                    "an empty GUID is the silent detach — resolve must return null");

                string guid = AssetDatabase.AssetPathToGUID(TempFolder + "/PostPreviewLook.asset");
                Assert.IsFalse(string.IsNullOrEmpty(guid), "the temp asset must have a real GUID");
                settings.PostLookGuid = guid;
                NamerPostLook resolved = NamerProcessor.ResolvePostLook(settings);
                Assert.AreSame(look, resolved,
                    "the persisted GUID must resolve back to the same asset instance (survives domain reload)");
            }
            finally
            {
                new NamerProcessorSettings().PostLookGuid = savedGuid;
                AssetDatabase.DeleteAsset(TempFolder);
            }
        }

        /// <summary>
        /// Full-table law (Metal silent-skip discipline): a FITTED post look at the
        /// post lens writes enable 1 plus the CPU-side twins of URP's own grading
        /// passes — LMS via the exact ColorBalanceToLMSCoeffs the LUT pass calls,
        /// z = contrast/100 + 1, s = saturation/100 + 1, raw EV — with every value a
        /// DIRECT unscaled asset-field read (R7 full strength), and binds the
        /// 128x1 RGBA float curve strip.
        /// </summary>
        [Test]
        public void ApplyPostLook_FittedLook_SetsFullTable()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            NamerStyleAsset asset = ScriptableObject.CreateInstance<NamerStyleAsset>();
            Material material = new Material(shader);
            try
            {
                asset.PostFitFingerprint = "synthetic-fit-fingerprint";
                asset.PostTemperature = 20f;
                asset.PostTint = -10f;
                asset.PostExposureEv = 1.5f;
                asset.PostContrast = 25f;
                asset.PostSaturation = -40f;

                NamerEditorWindow.ApplyPostLook(material, asset, true);

                Assert.AreEqual(1f, material.GetFloat("_PostEnable"),
                    "fitted look at the post lens must enable the in-shader grade");
                Vector4 expectedLms = ColorUtils.ColorBalanceToLMSCoeffs(20f, -10f);
                Assert.AreEqual(expectedLms, material.GetVector("_PostLmsCoeffs"),
                    "LMS coefficients must be the exact function URP's LUT pass evaluates");
                Assert.AreEqual(1.5f, material.GetFloat("_PostExposureEv"));
                Assert.AreEqual(1.25f, material.GetFloat("_PostContrastZ"),
                    "z must be contrast/100 + 1 (25 -> 1.25)");
                Assert.AreEqual(0.6f, material.GetFloat("_PostSaturationS"),
                    "s must be saturation/100 + 1 (-40 -> 0.6)");

                Texture2D curve = material.GetTexture("_PostCurveTex") as Texture2D;
                Assert.IsNotNull(curve, "the fitted case must bind the curve strip");
                Assert.AreEqual(128, curve.width, "URP-matching 128-texel curve strip");
                Assert.AreEqual(1, curve.height);
                Assert.AreEqual(TextureFormat.RGBAFloat, curve.format,
                    "float strip so curve values survive unquantized");
                Assert.AreEqual(FilterMode.Bilinear, curve.filterMode,
                    "LinearClamp sampling like URP's LUT-builder curve fetches");
                Assert.AreEqual(TextureWrapMode.Clamp, curve.wrapMode);
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(asset);
            }
        }

        /// <summary>
        /// Inactive table law: unfitted look, non-post lens, or null look all
        /// write the COMPLETE neutral table (enable 0, identity LMS/z/s/EV, null
        /// curve) — never a half-written table (Metal skips unbound dispatches
        /// silently).
        /// </summary>
        [Test]
        public void ApplyPostLook_InactiveOrUnfitted_IsNeutral()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            NamerStyleAsset fitted = ScriptableObject.CreateInstance<NamerStyleAsset>();
            NamerStyleAsset unfitted = ScriptableObject.CreateInstance<NamerStyleAsset>();
            Material material = new Material(shader);
            try
            {
                fitted.PostFitFingerprint = "synthetic-fit-fingerprint";
                fitted.PostTemperature = 80f;
                fitted.PostContrast = 60f;
                fitted.PostSaturation = 50f;
                fitted.PostExposureEv = 2f;

                // Null look (post lens, but nothing selected): full neutral table.
                NamerEditorWindow.ApplyPostLook(material, null, true);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"));
                Assert.AreEqual(Vector4.one, material.GetVector("_PostLmsCoeffs"),
                    "a null look must still write the neutral LMS row");
                Assert.AreEqual(0f, material.GetFloat("_PostExposureEv"));
                Assert.AreEqual(1f, material.GetFloat("_PostContrastZ"));
                Assert.AreEqual(1f, material.GetFloat("_PostSaturationS"));
                Assert.IsNull(material.GetTexture("_PostCurveTex"));

                // Fitted look but a NON-post lens: enable 0 (identity grade) — the
                // other uniforms may ride along, the gate is what the shader reads.
                NamerEditorWindow.ApplyPostLook(material, fitted, false);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"),
                    "the NAMER/Stylized lenses must keep the in-shader grade off");

                // Post lens but an UNFITTED look: enable 0 (the W10 fallback to
                // plain Stylized NAMER).
                NamerEditorWindow.ApplyPostLook(material, unfitted, true);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"),
                    "an unfitted look must fall back to the un-graded stylized view");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(unfitted);
                Object.DestroyImmediate(fitted);
            }
        }

        /// <summary>
        /// Bloom threshold conversion (05.1 D-04 / 05.2 W10): the pure helper applies
        /// URP's exact render-time gamma-to-linear conversion to the selected look's
        /// fitted threshold and returns the disabled sentinel 0 for null/unfitted
        /// looks.
        /// </summary>
        [Test]
        public void BloomThresholdLinear_MatchesUrpRenderTimeConversion()
        {
            Assert.AreEqual(0f, NamerEditorWindow.BloomThresholdLinear(null),
                "no selected look must disable the pane bloom chain (0 sentinel)");

            NamerStyleAsset unfitted = ScriptableObject.CreateInstance<NamerStyleAsset>();
            NamerStyleAsset fitted = ScriptableObject.CreateInstance<NamerStyleAsset>();
            try
            {
                Assert.AreEqual(0f, NamerEditorWindow.BloomThresholdLinear(unfitted),
                    "an unfitted look must disable the pane bloom chain (0 sentinel)");

                fitted.PostFitFingerprint = "synthetic-fit-fingerprint";
                fitted.PostBloomThreshold = 0.9f;
                Assert.AreEqual(
                    Mathf.GammaToLinearSpace(0.9f),
                    NamerEditorWindow.BloomThresholdLinear(fitted),
                    "the fitted threshold must convert through the exact call URP's PostProcessPass applies at render time");
            }
            finally
            {
                Object.DestroyImmediate(unfitted);
                Object.DestroyImmediate(fitted);
            }
        }

        /// <summary>
        /// Renderer state (05.1 D-04): SetPostBloom(0, 0) — and the never-set state —
        /// keep the renderer on the disabled path (the Enabled probe stays false and
        /// the lazily-created material is never created), so Render stays
        /// byte-identical to the pre-05.1 renderer: zero extra RTs, zero blits.
        /// </summary>
        [Test]
        public void SetPostBloom_Zero_DisablesPaneBloomChain()
        {
            NamerPreviewRenderer renderer = new NamerPreviewRenderer();
            try
            {
                Assert.IsFalse(renderer.PostBloomEnabled,
                    "a fresh renderer must sit on the disabled (byte-identical) path");
                renderer.SetPostBloom(0f, 0f);
                Assert.IsFalse(renderer.PostBloomEnabled,
                    "SetPostBloom(0, 0) must leave the disabled path — no material, no RTs");

                renderer.SetPostBloom(0.5f, 1f);
                Assert.IsTrue(renderer.PostBloomEnabled, "arming the chain flips the probe");

                renderer.SetPostBloom(0f, 0f);
                Assert.IsFalse(renderer.PostBloomEnabled,
                    "re-disabling must return to the disabled path (temporaries released)");
            }
            finally
            {
                renderer.Dispose();
            }
        }

        private static void EnsureTempFolder()
        {
            if (!AssetDatabase.IsValidFolder(TempFolder))
            {
                AssetDatabase.CreateFolder("Assets", TempFolder.Substring("Assets/".Length));
            }
        }
    }
}

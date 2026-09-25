using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 D-04 (STYL-09) Stylized + Post lens tests: the preview-composition gate
    /// (the post view composes stylization), the popup option table, the caption
    /// seam, and the ApplyPostLook material-uniform contract. Mirrors
    /// <see cref="NamerStylizationIntegrationTests"/> pure-seam patterns and
    /// <see cref="NamerDipSwitchTests"/> material-uniform idioms.
    /// </summary>
    public class NamerPostPreviewTests
    {
        /// <summary>
        /// The post lens must compose stylization exactly like the Stylized NAMER
        /// lens — the fitted post grade rides ON TOP of the stylized base, it never
        /// replaces it (W5 widening of ComposePreviewStylization).
        /// </summary>
        [Test]
        public void ComposePreviewStylization_PostView_ComposesStylization()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                profile.Smoothing = 0.5f;

                NamerStyleStylization composed = NamerEditorWindow.ComposePreviewStylization(
                    profile, NamerEditorWindow.kAfterViewPost, null);
                Assert.IsNotNull(composed,
                    "After View = Stylized + Post must compose the stylized preview context");
                Assert.IsTrue(composed.SmoothingSkippedNoMesh,
                    "no split handed to the composition must set SmoothingSkippedNoMesh (OQ-3, shared with the stylized lens)");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

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
        /// only the un-stylized state draws the flipper.
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
        /// Full-table law (Metal silent-skip discipline): a FITTED profile at the
        /// post lens writes enable 1 plus the CPU-side twins of URP's own grading
        /// passes — LMS via the exact ColorBalanceToLMSCoeffs the LUT pass calls,
        /// z = contrast/100 + 1, s = saturation/100 + 1, raw EV — and binds the
        /// 128x1 RGBA float curve strip.
        /// </summary>
        [Test]
        public void ApplyPostLook_FittedProfile_SetsFullTable()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            Material material = new Material(shader);
            try
            {
                profile.PostFitFingerprint = "synthetic-fit-fingerprint";
                profile.PostTemperature = 20f;
                profile.PostTint = -10f;
                profile.PostExposureEv = 1.5f;
                profile.PostContrast = 25f;
                profile.PostSaturation = -40f;

                NamerEditorWindow.ApplyPostLook(material, profile, true);

                Assert.AreEqual(1f, material.GetFloat("_PostEnable"),
                    "fitted profile at the post lens must enable the in-shader grade");
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
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// Inactive table law: unfitted profile, non-post lens, or null profile all
        /// write the COMPLETE neutral table (enable 0, identity LMS/z/s/EV, null
        /// curve) — never a half-written table (Metal skips unbound dispatches
        /// silently).
        /// </summary>
        [Test]
        public void ApplyPostLook_InactiveOrUnfitted_IsNeutral()
        {
            Shader shader = Shader.Find("GraffitiEntertainment.Namer/NAMER");
            Assert.IsNotNull(shader, "NAMER shader must load");

            NamerStyleProfile fitted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerStyleProfile unfitted = ScriptableObject.CreateInstance<NamerStyleProfile>();
            Material material = new Material(shader);
            try
            {
                fitted.PostFitFingerprint = "synthetic-fit-fingerprint";
                fitted.PostTemperature = 80f;
                fitted.PostContrast = 60f;
                fitted.PostSaturation = 50f;
                fitted.PostExposureEv = 2f;

                // Null profile (post lens, but nothing attached): full neutral table.
                NamerEditorWindow.ApplyPostLook(material, null, true);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"));
                Assert.AreEqual(Vector4.one, material.GetVector("_PostLmsCoeffs"),
                    "null profile must still write the neutral LMS row");
                Assert.AreEqual(0f, material.GetFloat("_PostExposureEv"));
                Assert.AreEqual(1f, material.GetFloat("_PostContrastZ"));
                Assert.AreEqual(1f, material.GetFloat("_PostSaturationS"));
                Assert.IsNull(material.GetTexture("_PostCurveTex"));

                // Fitted profile but a NON-post lens: enable 0 (identity grade) — the
                // other uniforms may ride along, the gate is what the shader reads.
                NamerEditorWindow.ApplyPostLook(material, fitted, false);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"),
                    "the NAMER/Stylized lenses must keep the in-shader grade off");

                // Post lens but an UNFITTED profile: enable 0 (W6 fallback to plain
                // Stylized NAMER).
                NamerEditorWindow.ApplyPostLook(material, unfitted, true);
                Assert.AreEqual(0f, material.GetFloat("_PostEnable"),
                    "an unfitted profile must fall back to the un-graded stylized view");
            }
            finally
            {
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(unfitted);
                Object.DestroyImmediate(fitted);
            }
        }
    }
}

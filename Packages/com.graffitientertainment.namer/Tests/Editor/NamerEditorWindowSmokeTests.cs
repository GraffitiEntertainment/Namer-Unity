using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// Headless EditMode smoke for the window facts that do not require window rendering:
    /// the <see cref="NamerProcessorSettings"/> foldout defaults (Source open, others collapsed)
    /// and the <see cref="NamerEditorWindow.ShaderInputToggleLabels"/> contents/order (the seven
    /// neutral-default shaded-view toggles, read via reflection). Also pins the persistent
    /// Source pick (a global editor selection change must not clobber the window's explicit
    /// pick), the albedo preview caption copy (locked, em-dashes verbatim), the neutral-state
    /// reason differentiation (no-source vs no-result), the embedded
    /// profile-inspector cache contract (created for the attached profile, reused, null when
    /// detached), and the stylize preview base selection (the styled SOURCE base wins over
    /// the projected base). The render-only claims (window fits a small screen with all
    /// sections collapsed; every control reachable by scrolling) are manual-verification
    /// checklist items, not automated tests.
    /// </summary>
    public class NamerEditorWindowSmokeTests
    {
        private const string FoldoutSourceKey = "NamerProcessor.FoldoutSource";
        private const string FoldoutPreviewKey = "NamerProcessor.FoldoutPreview";
        private const string FoldoutRoughnessExtractionKey = "NamerProcessor.FoldoutRoughnessExtraction";
        private const string FoldoutAoKey = "NamerProcessor.FoldoutAo";
        private const string FoldoutDecompositionKey = "NamerProcessor.FoldoutDecomposition";
        private const string FoldoutOutputKey = "NamerProcessor.FoldoutOutput";

        [Test]
        public void FoldoutDefaults_SourceOpenOthersCollapsed()
        {
            PrefsSnapshot prefs = CaptureFoldoutPrefs();
            try
            {
                DeleteFoldoutKeys();
                var settings = new NamerProcessorSettings();

                Assert.IsTrue(settings.FoldoutSource, "Source must default open so the selection is visible");
                Assert.IsFalse(settings.FoldoutPreview, "Preview/Debug must default collapsed");
                Assert.IsFalse(settings.FoldoutRoughnessExtraction, "Roughness Extraction must default collapsed");
                Assert.IsFalse(settings.FoldoutAo, "AO must default collapsed");
                Assert.IsFalse(settings.FoldoutDecomposition, "Decomposition must default collapsed");
                Assert.IsFalse(settings.FoldoutOutput, "Output must default collapsed");
            }
            finally
            {
                RestoreFoldoutPrefs(prefs);
            }
        }

        [Test]
        public void ShaderInputToggleLabels_AreFiveNeutralToggles()
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                "ShaderInputToggleLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "ShaderInputToggleLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])field.GetValue(null);
            Assert.IsNotNull(labels, "ShaderInputToggleLabels must resolve to a string array");

            string[] expected = { "Base/Residual", "Roughness", "AO", "Metallic", "Emissive", "Vertex Color", "Normal" };
            CollectionAssert.AreEqual(expected, labels,
                "ShaderInputToggleLabels must be the seven neutral-default shaded-view input toggles");
        }

        [Test]
        public void ChannelPaneLabels_AreSixChannelPanes()
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                "ChannelPaneLabels", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "ChannelPaneLabels field must exist on NamerEditorWindow");
            string[] labels = (string[])field.GetValue(null);
            Assert.IsNotNull(labels, "ChannelPaneLabels must resolve to a string array");

            string[] expected = { "Base", "Roughness", "AO", "Metallic", "Emissive", "Normal" };
            CollectionAssert.AreEqual(expected, labels,
                "ChannelPaneLabels must be the six channel panes decoded from the After material's packed textures");
        }

        /// <summary>
        /// Behavioral pin for the persistent Source pick: the window no longer subscribes to
        /// <c>Selection.selectionChanged</c>, so a global editor selection change must leave the
        /// window's explicit <c>_selection</c> untouched. The window instance runs OnEnable (null
        /// selection — the production default, no GPU work) and OnDisable (via DestroyImmediate)
        /// inside the test; Tick never fires in a synchronous test body.
        /// </summary>
        [Test]
        public void SourceSelection_PersistsAcrossEditorSelectionChange()
        {
            UnityEngine.Object previousSelection = Selection.activeObject;
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            ScriptableObject tokenA = ScriptableObject.CreateInstance<ScriptableObject>();
            ScriptableObject tokenB = ScriptableObject.CreateInstance<ScriptableObject>();
            tokenA.name = "NamerSourceTokenA";
            tokenB.name = "NamerSourceTokenB";
            try
            {
                FieldInfo selectionField = typeof(NamerEditorWindow).GetField(
                    "_selection", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(selectionField, "_selection field must exist on NamerEditorWindow");

                selectionField.SetValue(window, tokenA);
                Selection.activeObject = tokenB;

                Assert.AreSame(tokenA, selectionField.GetValue(window),
                    "A global editor selection change must not clobber the window's explicit Source pick");
            }
            finally
            {
                Selection.activeObject = previousSelection;
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(tokenA);
                Object.DestroyImmediate(tokenB);
            }
        }

        /// <summary>
        /// The albedo preview caption/button strings are locked copy — the caption must state
        /// honestly which lens produced the image, and the neutral-state reason lines must name
        /// the missing prerequisite. Pinned verbatim (em-dashes included) via the same
        /// reflection seam as the other label fields.
        /// </summary>
        [Test]
        public void AlbedoPreviewCaptions_ExactLockedCopy()
        {
            AssertLockedConstString("AlbedoPreviewStylizedCaption", "Stylized albedo — palette transfer applied");
            AssertLockedConstString("AlbedoPreviewUnstylizedCaption", "Un-stylized albedo — After View (Preview/Debug) is NAMER");
            AssertLockedConstString("AlbedoPreviewNoSourceCaption", "select a Source above to preview");
            AssertLockedConstString("AlbedoPreviewNoResultCaption", "no preview yet — waiting for recompute");
            AssertLockedConstString("AlbedoPreviewViewStylizedButton", "View Stylized");
        }

        /// <summary>
        /// The neutral-state reason line differentiates WHAT is missing: no Source (e.g. a
        /// domain reload wiped a scene-object pick — accepted Task 1 limitation) reads the
        /// no-source line, a picked source without a computed result reads the no-result
        /// line, and both-present resolves null (no line). Pure seam — no OnGUI, no GPU.
        /// </summary>
        [Test]
        public void AlbedoPreviewEmptyReason_Differentiates()
        {
            MethodInfo resolve = typeof(NamerEditorWindow).GetMethod(
                "ResolveAlbedoPreviewEmptyReason", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(resolve, "ResolveAlbedoPreviewEmptyReason method must exist on NamerEditorWindow");

            Assert.AreEqual("select a Source above to preview", resolve.Invoke(null, new object[] { false, true }),
                "No Source picked must read the no-source reason");
            Assert.AreEqual("select a Source above to preview", resolve.Invoke(null, new object[] { false, false }),
                "No Source dominates — the no-source reason wins even without a live result");

            Assert.AreEqual("no preview yet — waiting for recompute", resolve.Invoke(null, new object[] { true, false }),
                "Source picked but no recompute yet must read the no-result reason");

            Assert.IsNull(resolve.Invoke(null, new object[] { true, true }),
                "Both prerequisites hold — no reason line renders (null)");
        }

        private static void AssertLockedConstString(string fieldName, string expected)
        {
            FieldInfo field = typeof(NamerEditorWindow).GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, fieldName + " field must exist on NamerEditorWindow");
            Assert.AreEqual(expected, (string)field.GetValue(null),
                fieldName + " must equal the locked copy verbatim (em-dashes included)");
        }

        /// <summary>
        /// The Stylization section embeds the attached profile's full inspector: the cached
        /// UnityEditor.Editor is created for the attached profile, reused on the second call,
        /// and absent when no profile is attached. Drives EnsureProfileEditor via reflection
        /// with no OnGUI pass (creating an editor is safe outside OnGUI; only OnInspectorGUI
        /// is OnGUI-only). The profile is an in-memory ScriptableObject — CreateEditor needs
        /// no asset on disk.
        /// </summary>
        [Test]
        public void ProfileEditor_EmbedsForAttachedProfile()
        {
            NamerStyleProfile profile = ScriptableObject.CreateInstance<NamerStyleProfile>();
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            try
            {
                MethodInfo ensure = typeof(NamerEditorWindow).GetMethod(
                    "EnsureProfileEditor", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.IsNotNull(ensure, "EnsureProfileEditor method must exist on NamerEditorWindow");

                UnityEditor.Editor embedded = ensure.Invoke(window, new object[] { profile }) as UnityEditor.Editor;
                Assert.IsNotNull(embedded, "EnsureProfileEditor must create an editor for the attached profile");
                Assert.AreSame(profile, embedded.target, "The embedded editor must target the attached profile");

                UnityEditor.Editor second = ensure.Invoke(window, new object[] { profile }) as UnityEditor.Editor;
                Assert.AreSame(embedded, second, "EnsureProfileEditor must reuse the cached editor instance");

                Assert.IsNull(ensure.Invoke(window, new object[] { null }),
                    "EnsureProfileEditor must return null when no profile is attached");
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        /// The stylize albedo preview binds the styled SOURCE base (pre-projection), never
        /// the projected/packed texture the After material samples: SourceBaseColor wins when
        /// set, NormalizedBaseColor is the fallback (it IS the styled base when no projection
        /// ran), and a null result resolves null. Pure reference-selection seam — the throwaway
        /// render targets are never bound, so no GPU work happens.
        /// </summary>
        [Test]
        public void StylizePreviewBase_PrefersSourceOverProjected()
        {
            RenderTexture sourceBase = new RenderTexture(1, 1, 0);
            RenderTexture projectedBase = new RenderTexture(1, 1, 0);
            try
            {
                MethodInfo resolve = typeof(NamerEditorWindow).GetMethod(
                    "ResolveStylizePreviewBase", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(resolve, "ResolveStylizePreviewBase method must exist on NamerEditorWindow");

                var withSource = new NamerComputeResult
                {
                    SourceBaseColor = sourceBase,
                    NormalizedBaseColor = projectedBase,
                };
                Assert.AreSame(sourceBase, resolve.Invoke(null, new object[] { withSource }),
                    "The stylize preview must prefer the styled SOURCE base (pre-projection) when the projection ran");

                var withoutSource = new NamerComputeResult
                {
                    SourceBaseColor = null,
                    NormalizedBaseColor = projectedBase,
                };
                Assert.AreSame(projectedBase, resolve.Invoke(null, new object[] { withoutSource }),
                    "Without a projection the styled base IS NormalizedBaseColor — it must be the fallback");

                Assert.IsNull(resolve.Invoke(null, new object[] { null }),
                    "A null result must resolve a null preview base");
            }
            finally
            {
                Object.DestroyImmediate(sourceBase);
                Object.DestroyImmediate(projectedBase);
            }
        }

        /// <summary>
        /// 260926-mtv: the preview quad's horizontal center is the VISIBLE band center
        /// (scroll.x + viewWidth/2), never the reserved row's center — the expand row
        /// stretches to the scroll CONTENT width (900 here), and centering on the row
        /// was the pre-fix behavior. The row is intentionally below the vertical band
        /// here, so the vertical clip inverts and is irrelevant to the horizontal claim.
        /// </summary>
        [Test]
        public void CentersInViewWidth_NotRowWidth()
        {
            // fitScale = min(488/1024, 512/1024) = 61/128 -> drawWidth == drawHeight == 488;
            // unclipped fit x = 0 + (488 - 488) * 0.5 = 0, so the clipped center x == 244.
            NamerEditorWindow.ComputeAlbedoPreviewGeometry(
                new Rect(0f, 1000f, 900f, 512f), 1024f, 1024f, 488f, 400f, new Vector2(0f, 0f),
                out Rect drawRect, out Rect uvRect);

            Assert.AreEqual(488f * 0.5f, drawRect.center.x, 0.0001f,
                "The quad must center on the visible band (scroll.x + viewWidth/2 == 244), not the 900px row center (450) — the pre-fix behavior");
        }

        /// <summary>
        /// 260926-mtv: a wide texture can never draw wider than the visible band — the
        /// fit is width-bound (drawWidth == viewWidth exactly) and the x-extent is
        /// unclipped, so the full texture width stays visible (uvRect.width == 1).
        /// </summary>
        [Test]
        public void NeverWiderThanView_WideTexture()
        {
            // fitScale = 488/4096 = 61/512 -> drawWidth == 488 exact, drawHeight == 61;
            // unclipped x-range [0, 488] == band x-range [0, 488] — no horizontal cut.
            NamerEditorWindow.ComputeAlbedoPreviewGeometry(
                new Rect(0f, 0f, 900f, 512f), 4096f, 512f, 488f, 400f, new Vector2(0f, 0f),
                out Rect drawRect, out Rect uvRect);

            Assert.AreEqual(488f, drawRect.width, 0.0001f,
                "A width-bound fit pins drawWidth at exactly the view width");
            Assert.AreEqual(1f, uvRect.width, 0.0001f,
                "An uncut x-extent samples the full texture width (uvRect.width == 1)");
        }

        /// <summary>
        /// 260926-mtv: a quad extending above the band top is cut at the band edge and
        /// uvRect reports the matching visible fraction — the clipped-off region is
        /// removed from sampling, not stretched to fill the clipped rect. The quad
        /// bottom (2012 + 488 == 2500) aligns exactly with the band bottom
        /// (2200 + 300 == 2500), so only the top is cut: the visible v-range is
        /// [0, 300/488] — uvRect.y == 0 (v == 0 is the quad BOTTOM, probe-verified
        /// 2026-09-26) and height == 300/488 &lt; 1.
        /// </summary>
        [Test]
        public void BandClip_CutsQuadAndReportsUvSubRect()
        {
            // fit 488x488 at y = 2000 + (512 - 488) * 0.5 = 2012; band y [2200, 2500] ->
            // top cut at 2200: visible v [0, 300/488], clipped-off top 188/488.
            NamerEditorWindow.ComputeAlbedoPreviewGeometry(
                new Rect(0f, 2000f, 900f, 512f), 1024f, 1024f, 488f, 300f, new Vector2(0f, 2200f),
                out Rect drawRect, out Rect uvRect);

            Assert.AreEqual(2200f, drawRect.yMin, 0.0001f,
                "The quad must be cut at the band top edge");
            Assert.AreEqual(0f, uvRect.y, 0.0001f,
                "The quad bottom aligns exactly with the band bottom — v starts at 0 exactly (bottom-origin window)");
            Assert.AreEqual(300f / 488f, uvRect.height, 0.0001f,
                "uvRect.height must report the visible fraction (300/488 < 1)");
            Assert.AreEqual(300f / 488f, uvRect.yMax, 0.0001f,
                "The clipped-off top (188/488) is removed from sampling — yMax < 1");
        }

        /// <summary>
        /// 260926-mtv: a fully visible row (scroll.y == rowRect.y here) is never cut —
        /// the helper returns the uncut fit rect and an exact identity uvRect. 0/488 and
        /// 488/488 are exact binary floats, so the uvRect assertions carry NO tolerance.
        /// </summary>
        [Test]
        public void FullyVisible_PassesIdentityUv()
        {
            // fit 488x488 at y 1012..1500 inside band [1000, 1600]; x [0, 488] inside
            // band [0, 488] — uncut, so uvRect == (0, 0, 1, 1) exactly.
            NamerEditorWindow.ComputeAlbedoPreviewGeometry(
                new Rect(0f, 1000f, 900f, 512f), 1024f, 1024f, 488f, 600f, new Vector2(0f, 1000f),
                out Rect drawRect, out Rect uvRect);

            Assert.AreEqual(0f, uvRect.x, "A fully visible quad starts sampling at u == 0 exactly");
            Assert.AreEqual(0f, uvRect.y, "A fully visible quad starts sampling at v == 0 exactly");
            Assert.AreEqual(1f, uvRect.width, "A fully visible quad samples the full texture width exactly");
            Assert.AreEqual(1f, uvRect.height, "A fully visible quad samples the full texture height exactly");
            Assert.AreEqual(1012f, drawRect.yMin, 0.0001f,
                "Vertical centering within the row is unchanged: y = 1000 + (512 - 488) * 0.5 == 1012");
            Assert.AreEqual(488f, drawRect.height, 0.0001f,
                "The uncut fit keeps its full square height");
        }

        /// <summary>
        /// W14: a pane fully inside the visible band is returned unchanged with an
        /// exact identity uvRect — the common case (the strip is a mid-content row
        /// fully on screen) must behave exactly like the pre-clip draw. All values
        /// are exact binary floats, so the assertions carry NO tolerance.
        /// </summary>
        [Test]
        public void ChannelPaneClip_FullContainment_ReturnsPaneAndIdentityUv()
        {
            // Pane (100, 200, 48, 48) sits strictly inside band (0, 0, 600, 500).
            Rect clipped = NamerEditorWindow.ComputeChannelPaneClip(
                new Rect(100f, 200f, 48f, 48f), new Rect(0f, 0f, 600f, 500f), out Rect uvRect);

            Assert.AreEqual(100f, clipped.xMin, "A contained pane keeps its left edge");
            Assert.AreEqual(200f, clipped.yMin, "A contained pane keeps its top edge");
            Assert.AreEqual(48f, clipped.width, "A contained pane keeps its full width");
            Assert.AreEqual(48f, clipped.height, "A contained pane keeps its full height");
            Assert.AreEqual(0f, uvRect.x, "A fully visible pane starts sampling at u == 0 exactly");
            Assert.AreEqual(0f, uvRect.y, "A fully visible pane starts sampling at v == 0 exactly");
            Assert.AreEqual(1f, uvRect.width, "A fully visible pane samples the full texture width exactly");
            Assert.AreEqual(1f, uvRect.height, "A fully visible pane samples the full texture height exactly");
        }

        /// <summary>
        /// W14: a pane crossing the band's RIGHT edge (the vertical-scrollbar side)
        /// is trimmed to the covered side and uvRect windows the surviving half —
        /// the clipped-off right half is removed from sampling, not squeezed into
        /// the trimmed rect. 24/48 == 0.5 is an exact binary float.
        /// </summary>
        [Test]
        public void ChannelPaneClip_PartialOverlap_TrimsRightEdgeExactly()
        {
            // Pane (100, 200, 48, 48) vs band (0, 0, 124, 500): x trimmed to
            // [100, 124] — 24 px visible; y untouched.
            Rect clipped = NamerEditorWindow.ComputeChannelPaneClip(
                new Rect(100f, 200f, 48f, 48f), new Rect(0f, 0f, 124f, 500f), out Rect uvRect);

            Assert.AreEqual(124f, clipped.xMax, "The pane is cut at the band right edge");
            Assert.AreEqual(24f, clipped.width, "Exactly the covered width survives (48 - 24 == 24)");
            Assert.AreEqual(48f, clipped.height, "The vertical extent is untouched");
            Assert.AreEqual(0f, uvRect.x, "The left edge is uncut — u starts at 0 exactly");
            Assert.AreEqual(0.5f, uvRect.width, "The visible fraction is exactly half the pane");
            Assert.AreEqual(1f, uvRect.height, "The full vertical texture extent stays sampled");
        }

        /// <summary>
        /// W14: a pane crossing the band's BOTTOM edge (the horizontal-scrollbar
        /// strip) is trimmed to the covered top portion; uvRect's v offset is
        /// measured from the quad BOTTOM (probe-verified mtv lesson), so a
        /// 30/48-px survivor reads v == [1 - 30/48, 1] — the clipped-off bottom is
        /// removed, never stretched. 30/48 == 5/8 == 0.625 is an exact binary float.
        /// </summary>
        [Test]
        public void ChannelPaneClip_PartialOverlap_TrimsBottomEdgeExactly()
        {
            // Pane (100, 200, 48, 48) vs band (0, 0, 600, 230): y trimmed to
            // [200, 230] — 30 px visible; x untouched.
            Rect clipped = NamerEditorWindow.ComputeChannelPaneClip(
                new Rect(100f, 200f, 48f, 48f), new Rect(0f, 0f, 600f, 230f), out Rect uvRect);

            Assert.AreEqual(230f, clipped.yMax, "The pane is cut at the band bottom edge (the horizontal scrollbar strip)");
            Assert.AreEqual(30f, clipped.height, "Exactly the covered height survives (48 - 18 == 30)");
            Assert.AreEqual(48f, clipped.width, "The horizontal extent is untouched");
            Assert.AreEqual(0.375f, uvRect.y, "Bottom-origin window: v starts at 1 - 30/48 == 0.375 exactly");
            Assert.AreEqual(0.625f, uvRect.height, "The visible fraction is exactly 30/48 of the pane");
            Assert.AreEqual(1f, uvRect.yMax, "The quad top (v == 1) is the visible edge — nothing is mirrored");
            Assert.AreEqual(1f, uvRect.width, "The full horizontal texture extent stays sampled");
        }

        /// <summary>
        /// W14: a pane fully outside the band returns a zero-area rect (draw,
        /// label, cursor, and hit-test all skipped by the caller) — never an
        /// inverted rect that could sneak a draw through a negative-size check.
        /// </summary>
        [Test]
        public void ChannelPaneClip_ZeroIntersection_ReturnsZeroArea()
        {
            // Pane (100, 200, 48, 48) lies entirely below band (0, 300, 600, 200).
            Rect clipped = NamerEditorWindow.ComputeChannelPaneClip(
                new Rect(100f, 200f, 48f, 48f), new Rect(0f, 300f, 600f, 200f), out Rect uvRect);

            Assert.AreEqual(Rect.zero, clipped,
                "A fully out-of-band pane must resolve to the exact zero rect (scrolled-out strip draws nothing)");
            Assert.AreEqual(Rect.zero, uvRect,
                "A skipped pane reports the zero window, never a sampling fraction");
        }

        /// <summary>
        /// W14: a degenerate band — the first-frame default before the Repaint
        /// capture fills <c>_scrollViewRect</c> — is a miss like any other
        /// (zero-area clip, draw skipped): the mtv first-frame guard class. The
        /// pane never renders before the band is known.
        /// </summary>
        [Test]
        public void ChannelPaneClip_DegenerateBand_SkipsDraw()
        {
            Rect clipped = NamerEditorWindow.ComputeChannelPaneClip(
                new Rect(100f, 200f, 48f, 48f), new Rect(0f, 0f, 0f, 0f), out Rect uvRect);

            Assert.AreEqual(Rect.zero, clipped,
                "A degenerate (first-frame) band must skip the pane draw entirely");
            Assert.AreEqual(Rect.zero, uvRect,
                "A skipped pane reports the zero window");
        }

        // -- EditorPrefs isolation for the foldout keys -----------------------

        private sealed class PrefsSnapshot
        {
            public bool Source;
            public bool Preview;
            public bool RoughnessExtraction;
            public bool Ao;
            public bool Decomposition;
            public bool Output;
            public bool HadSource;
            public bool HadPreview;
            public bool HadRoughnessExtraction;
            public bool HadAo;
            public bool HadDecomposition;
            public bool HadOutput;
        }

        private static PrefsSnapshot CaptureFoldoutPrefs()
        {
            return new PrefsSnapshot
            {
                Source = EditorPrefs.GetBool(FoldoutSourceKey, true),
                Preview = EditorPrefs.GetBool(FoldoutPreviewKey, false),
                RoughnessExtraction = EditorPrefs.GetBool(FoldoutRoughnessExtractionKey, false),
                Ao = EditorPrefs.GetBool(FoldoutAoKey, false),
                Decomposition = EditorPrefs.GetBool(FoldoutDecompositionKey, false),
                Output = EditorPrefs.GetBool(FoldoutOutputKey, false),
                HadSource = EditorPrefs.HasKey(FoldoutSourceKey),
                HadPreview = EditorPrefs.HasKey(FoldoutPreviewKey),
                HadRoughnessExtraction = EditorPrefs.HasKey(FoldoutRoughnessExtractionKey),
                HadAo = EditorPrefs.HasKey(FoldoutAoKey),
                HadDecomposition = EditorPrefs.HasKey(FoldoutDecompositionKey),
                HadOutput = EditorPrefs.HasKey(FoldoutOutputKey),
            };
        }

        private static void RestoreFoldoutPrefs(PrefsSnapshot snapshot)
        {
            if (snapshot.HadSource) { EditorPrefs.SetBool(FoldoutSourceKey, snapshot.Source); }
            else { EditorPrefs.DeleteKey(FoldoutSourceKey); }

            if (snapshot.HadPreview) { EditorPrefs.SetBool(FoldoutPreviewKey, snapshot.Preview); }
            else { EditorPrefs.DeleteKey(FoldoutPreviewKey); }

            if (snapshot.HadRoughnessExtraction) { EditorPrefs.SetBool(FoldoutRoughnessExtractionKey, snapshot.RoughnessExtraction); }
            else { EditorPrefs.DeleteKey(FoldoutRoughnessExtractionKey); }

            if (snapshot.HadAo) { EditorPrefs.SetBool(FoldoutAoKey, snapshot.Ao); }
            else { EditorPrefs.DeleteKey(FoldoutAoKey); }

            if (snapshot.HadDecomposition) { EditorPrefs.SetBool(FoldoutDecompositionKey, snapshot.Decomposition); }
            else { EditorPrefs.DeleteKey(FoldoutDecompositionKey); }

            if (snapshot.HadOutput) { EditorPrefs.SetBool(FoldoutOutputKey, snapshot.Output); }
            else { EditorPrefs.DeleteKey(FoldoutOutputKey); }
        }

        private static void DeleteFoldoutKeys()
        {
            EditorPrefs.DeleteKey(FoldoutSourceKey);
            EditorPrefs.DeleteKey(FoldoutPreviewKey);
            EditorPrefs.DeleteKey(FoldoutRoughnessExtractionKey);
            EditorPrefs.DeleteKey(FoldoutAoKey);
            EditorPrefs.DeleteKey(FoldoutDecompositionKey);
            EditorPrefs.DeleteKey(FoldoutOutputKey);
        }
    }
}

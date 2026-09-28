using System.Reflection;
using GraffitiEntertainment.Namer.Editor;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Tests
{
    /// <summary>
    /// 05.1 plan 03 lifecycle tests (STYL-08 tail) / 05.2 plan 04 (R3) / 05.2 plan 05:
    /// the Fit Look prerequisite seam lives on the ASSET editor —
    /// <see cref="NamerPostLookEditor.FitButtonState"/> with its LOCKED three-reason
    /// chain (positive-weight reference → exemplar window with a loadable source →
    /// stylized output). Pure EditMode logic — no GPU, no Process run (the Volume
    /// regen gate itself is covered by the volume-writer tests; the fit write lands
    /// on the asset via <see cref="NamerStylePostFit.ApplyToPostLook"/> — see
    /// NamerStylePostFitTests / NamerPostLookTests — and regression is covered by the
    /// full suite). 05.2-06 (STYL-10/STYL-11): the profile-write, load-time
    /// migration, and W8 fit-clause fixtures retired with the deleted profile-side
    /// surface — the migration window closed in 05.2 plan 05 and the post look now
    /// exists exactly once, on the asset. Asset-writing tests use a temp folder
    /// deleted in <c>finally</c> so fingerprints are real GUID-based identity
    /// strings; the EditorPrefs-backed Style Profile pref is snapshotted and
    /// restored in <c>finally</c> so stylized-output pinning never leaks the
    /// selection.
    /// </summary>
    public class NamerStylePostFitLifecycleTests
    {
        private const string TempFolder = "Assets/NAMER_PostFit_Lifecycle_Tests_Temp";

        private const string ReasonNoPositiveReference =
            "Fit Look needs at least one reference with weight above 0 — all slots empty or every weight 0 leaves nothing to fit.";

        private const string ReasonNoExemplar =
            "Fit Look uses the object loaded in the Processor window (Tools > NAMER/Processor) — load a Source there first.";

        private const string ReasonNoStylizedOutput =
            "Fit Look fits the Processor window's stylized render — attach a Style Profile in the Processor window first.";

        /// <summary>
        /// 05.2-05 prereq 1: an assigned reference whose only weight is 0 leaves
        /// nothing to fit — the button disables with the WR-01 mirror reason even
        /// before the window/prereq-2 question is asked (LOCKED reason order). The
        /// seam is pure — no window, no EditorPrefs reads for this reason.
        /// </summary>
        [Test]
        public void FitButtonState_NoPositiveReferenceWeight_DisabledWithReason1()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            look.ReferenceWeights.Add(0f);
            try
            {
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, null, new NamerProcessorSettings());
                Assert.IsTrue(state.Disabled, "zero-mass references disable the button");
                Assert.AreEqual(ReasonNoPositiveReference, state.ReasonMiniLabel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 prereq 2: references with positive weight but no exemplar window
        /// (null window — the seam's injectable-optional shape) disables with the
        /// Processor-window reason. The reason's menu-path slash form is copy-locked.
        /// </summary>
        [Test]
        public void FitButtonState_NoExemplarWindow_DisabledWithReason2()
        {
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            try
            {
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, null, new NamerProcessorSettings());
                Assert.IsTrue(state.Disabled, "no exemplar window disables the button");
                Assert.AreEqual(ReasonNoExemplar, state.ReasonMiniLabel);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 prereq 3 (D-04 LOCKED): a window WITH a loadable source (reflection
        /// seam — _selection + _model, the smoke-test precedent) but no resolvable
        /// Style Profile in settings disables with the stylized-output reason. The
        /// EditorPrefs-backed pref is snapshotted and restored in finally.
        /// </summary>
        [Test]
        public void FitButtonState_NoStylizedOutput_DisabledWithReason3()
        {
            string savedGuid = new NamerProcessorSettings().StyleProfileGuid;
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            ScriptableObject token = ScriptableObject.CreateInstance<ScriptableObject>();
            token.name = "LifecycleSourceToken";
            try
            {
                InjectWindowSource(window, token);
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleProfileGuid = string.Empty;
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, window, settings);
                Assert.IsTrue(state.Disabled, "no stylized output disables the button");
                Assert.AreEqual(ReasonNoStylizedOutput, state.ReasonMiniLabel);
            }
            finally
            {
                new NamerProcessorSettings().StyleProfileGuid = savedGuid;
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(token);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// 05.2-05 ready state: positive-weight reference + a window with a loadable
        /// source + a resolvable attached Style Profile → enabled, no reason line.
        /// The Style Profile pref is pinned to a real temp asset GUID (round-trip
        /// discipline) and restored in finally.
        /// </summary>
        [Test]
        public void FitButtonState_AllPrereqsSatisfied_EnabledWithNoReason()
        {
            string savedGuid = new NamerProcessorSettings().StyleProfileGuid;
            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.References.Add(new Texture2D(2, 2));
            NamerEditorWindow window = ScriptableObject.CreateInstance<NamerEditorWindow>();
            ScriptableObject token = ScriptableObject.CreateInstance<ScriptableObject>();
            token.name = "LifecycleSourceToken";
            NamerStyleProfile attached = ScriptableObject.CreateInstance<NamerStyleProfile>();
            try
            {
                EnsureTempFolder();
                AssetDatabase.CreateAsset(attached, TempFolder + "/LifecycleStyleProfile.asset");
                InjectWindowSource(window, token);
                NamerProcessorSettings settings = new NamerProcessorSettings();
                settings.StyleProfileGuid = AssetDatabase.AssetPathToGUID(TempFolder + "/LifecycleStyleProfile.asset");
                NamerPostLookEditor.FitLookButtonState state =
                    NamerPostLookEditor.FitButtonState(look, window, settings);
                Assert.IsFalse(state.Disabled, "all prereqs met — the button is enabled");
                Assert.IsNull(state.ReasonMiniLabel, "an enabled button renders no reason line");
            }
            finally
            {
                new NamerProcessorSettings().StyleProfileGuid = savedGuid;
                AssetDatabase.DeleteAsset(TempFolder);
                UnityEngine.Object.DestroyImmediate(window);
                UnityEngine.Object.DestroyImmediate(token);
                UnityEngine.Object.DestroyImmediate(look);
            }
        }

        /// <summary>
        /// Reflection seam (the smoke-test precedent): injects a non-null
        /// <c>_selection</c> plus a <see cref="NamerSourceModel"/> holding one
        /// material inspection so the window reports HasLoadableSource without any
        /// GPU or Process work.
        /// </summary>
        private static void InjectWindowSource(NamerEditorWindow window, UnityEngine.Object token)
        {
            FieldInfo selectionField = typeof(NamerEditorWindow).GetField(
                "_selection", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(selectionField, "_selection field must exist on NamerEditorWindow");
            selectionField.SetValue(window, token);

            FieldInfo modelField = typeof(NamerEditorWindow).GetField(
                "_model", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(modelField, "_model field must exist on NamerEditorWindow");
            NamerSourceModel model = new NamerSourceModel();
            model.Materials.Add(new NamerMaterialInspection());
            modelField.SetValue(window, model);
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

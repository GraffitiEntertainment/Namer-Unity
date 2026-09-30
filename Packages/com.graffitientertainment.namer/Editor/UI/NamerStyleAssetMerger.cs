using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Pure carry core of the 05.3 M-series migration (STYL-15/D-05; 05.2 D-07
    /// no-silent-loss applies to the merge): merges a
    /// <see cref="NamerStyleProfile"/> and an optional <see cref="NamerPostLook"/>
    /// into a NEW <see cref="NamerStyleAsset"/> with deterministic union order,
    /// deep copies, and honest state flags. Headless-testable: creates NO assets
    /// on disk — persistence is the dialog's job
    /// (<see cref="NamerStyleAssetMerger"/>); every method here is pure apart
    /// from the <see cref="ScriptableObject.CreateInstance{T}"/> allocation.
    /// </summary>
    internal static class NamerStyleAssetMergerCore
    {
        /// <summary>
        /// Builds the merged Style Asset in memory from the carry sources. Copy
        /// order (deterministic): (1) the reference union — the profile's
        /// non-null references first with their weights preserved per slot, then
        /// the look's non-null references whose GUID is not already present
        /// (first-wins duplicate resolution — the profile slot's weight keeps
        /// the slot); null slots are skipped entirely. (2) The palette block —
        /// anchors element-wise, stats deep-copied, the extraction fingerprint
        /// carried verbatim, and both palette-source ints honestly set to
        /// <see cref="NamerEditorConstants.kPaletteSourceRaw"/> (the migrated
        /// palette came from raw-pixel extraction — S12 display rule). (3) The
        /// fitted-post block, only when the look is non-null — every
        /// <c>Post*</c> float, deep curve copies, and the fit/exemplar/volume
        /// fingerprints. (4) The palette-application sliders + hue mode from the
        /// profile. Decomposition/validation fields stay at their neutral
        /// defaults (M3: decomposition does NOT auto-run — the merged asset
        /// honestly reads not-decomposed). Finishes with
        /// <see cref="NamerStyleAsset.ClampToCaps"/> (T-05.3-04-01: a
        /// hand-edited source is sanitized on merge).
        /// </summary>
        /// <param name="profile">Required carry source (palette + sliders + hue mode).</param>
        /// <param name="look">Optional carry source (fitted post block); null = palette-only merge.</param>
        /// <returns>An unsaved merged asset — the caller owns persistence.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="profile"/> is null.</exception>
        internal static NamerStyleAsset BuildMerged(NamerStyleProfile profile, NamerPostLook look)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            NamerStyleAsset merged = ScriptableObject.CreateInstance<NamerStyleAsset>();

            // (1) Reference union: profile-first, look-appended, first-wins GUID
            // dedup, null slots skipped (one home — AppendUnionReferences).
            AppendUnionReferences(profile, look, merged.References, merged.ReferenceWeights);

            // (2) Palette block: anchors element-wise (NamerStyleAnchor is plain
            // data — the struct copy IS the deep copy), stats deep-copied so the
            // merged asset never shares the class instance with the source
            // profile, fingerprint carried verbatim (honest provenance of the
            // extraction the migrated palette came from).
            for (int i = 0; i < profile.PaletteAnchors.Count; i++)
            {
                merged.PaletteAnchors.Add(profile.PaletteAnchors[i]);
            }

            merged.Stats = CopyStats(profile.Stats);
            merged.ReferenceFingerprint = profile.ReferenceFingerprint;
            merged.PaletteSourceAtExtract = NamerEditorConstants.kPaletteSourceRaw;
            merged.PaletteSource = NamerEditorConstants.kPaletteSourceRaw;

            // (3) Fitted-post block (look present): floats verbatim, curves as
            // deep copies (never a shared AnimationCurve instance across
            // assets), fingerprints + exemplar name carried so HasFit and the
            // volume-staleness seam stay honest after the migration.
            if (look != null)
            {
                merged.PostTemperature = look.PostTemperature;
                merged.PostTint = look.PostTint;
                merged.PostExposureEv = look.PostExposureEv;
                merged.PostContrast = look.PostContrast;
                merged.PostSaturation = look.PostSaturation;
                merged.PostBloomThreshold = look.PostBloomThreshold;
                merged.PostBloomIntensity = look.PostBloomIntensity;
                merged.PostMasterCurve = CopyCurve(look.PostMasterCurve);
                merged.PostRedCurve = CopyCurve(look.PostRedCurve);
                merged.PostGreenCurve = CopyCurve(look.PostGreenCurve);
                merged.PostBlueCurve = CopyCurve(look.PostBlueCurve);
                merged.PostFitFingerprint = look.PostFitFingerprint;
                merged.PostFitExemplarName = look.PostFitExemplarName;
                merged.VolumeWriteFingerprint = look.VolumeWriteFingerprint;
            }

            // (4) Palette-application sliders + hue mode from the profile.
            merged.StyleStrength = profile.StyleStrength;
            merged.Smoothing = profile.Smoothing;
            merged.CavityFloor = profile.CavityFloor;
            merged.NormalDetailReduction = profile.NormalDetailReduction;
            merged.RoughnessSimplify = profile.RoughnessSimplify;
            merged.HueMode = profile.HueMode;

            // Decomposition/validation fields keep their CreateInstance neutral
            // defaults — nothing to carry (honest not-decomposed, M3).

            merged.ClampToCaps();
            return merged;
        }

        /// <summary>
        /// The would-be union reference count — the SAME walk
        /// <see cref="BuildMerged"/> performs, exposed so the dialog AND the
        /// tests can evaluate the <see cref="NamerEditorConstants.MaxReferences"/>
        /// cap BEFORE any asset exists (M3/T-05.3-04-03 cap honesty).
        /// </summary>
        /// <param name="profile">Required carry source; null yields 0 (no merge is possible).</param>
        /// <param name="look">Optional carry source; null counts the profile side alone.</param>
        /// <returns>Number of non-null references the merge would carry, after first-wins GUID dedup.</returns>
        internal static int CountUnionReferences(NamerStyleProfile profile, NamerPostLook look)
        {
            if (profile == null)
            {
                return 0;
            }

            List<Texture2D> references = new List<Texture2D>();
            List<float> weights = new List<float>();
            AppendUnionReferences(profile, look, references, weights);
            return references.Count;
        }

        /// <summary>
        /// Appends the deterministic reference union into the caller's lists:
        /// the profile's non-null references first (weights preserved per slot),
        /// then the look's non-null references whose GUID is not already present
        /// (first-wins — the profile slot's weight keeps the slot). Null slots
        /// are skipped entirely. Empty GUIDs (textures not persisted as assets)
        /// never dedup: two distinct unverifiable textures must not be silently
        /// conflated (D-07 no-silent-loss).
        /// </summary>
        private static void AppendUnionReferences(
            NamerStyleProfile profile,
            NamerPostLook look,
            List<Texture2D> references,
            List<float> weights)
        {
            HashSet<string> seenGuids = new HashSet<string>();
            for (int i = 0; i < profile.References.Count; i++)
            {
                Texture2D reference = profile.References[i];
                if (reference == null)
                {
                    continue;
                }

                references.Add(reference);
                weights.Add(WeightAt(profile.ReferenceWeights, i));
                TrackGuid(seenGuids, reference);
            }

            if (look == null)
            {
                return;
            }

            for (int i = 0; i < look.References.Count; i++)
            {
                Texture2D reference = look.References[i];
                if (reference == null)
                {
                    continue;
                }

                // First-wins GUID dedup (T-05.3-04-04): a texture already carried
                // from the profile keeps the PROFILE slot's weight; the look's
                // duplicate copy is skipped.
                string guid = ReferenceGuid(reference);
                if (!string.IsNullOrEmpty(guid) && seenGuids.Contains(guid))
                {
                    continue;
                }

                references.Add(reference);
                weights.Add(WeightAt(look.ReferenceWeights, i));
                if (!string.IsNullOrEmpty(guid))
                {
                    seenGuids.Add(guid);
                }
            }
        }

        /// <summary>
        /// Deep-copies the merged aggregate statistics —
        /// <see cref="NamerStyleStats"/> is a class, so a field assignment would
        /// alias the source profile's instance; the merged asset must own its
        /// own. A null source (hand-edited asset) maps to a fresh neutral stats.
        /// </summary>
        private static NamerStyleStats CopyStats(NamerStyleStats stats)
        {
            if (stats == null)
            {
                return new NamerStyleStats();
            }

            return new NamerStyleStats
            {
                AnchorCount = stats.AnchorCount,
                HueFamilyCount = stats.HueFamilyCount,
                MeanSaturation = stats.MeanSaturation,
                ShadowFraction = stats.ShadowFraction,
                MidtoneFraction = stats.MidtoneFraction,
                HighlightFraction = stats.HighlightFraction,
                ShadowColor = stats.ShadowColor,
                MidtoneColor = stats.MidtoneColor,
                HighlightColor = stats.HighlightColor,
                WarmCoolBias = stats.WarmCoolBias,
                Contrast = stats.Contrast,
                PaletteDensity = stats.PaletteDensity,
                ReferenceCount = stats.ReferenceCount
            };
        }

        /// <summary>
        /// Deep-copies one fitted-post curve over its key array — a shared
        /// AnimationCurve instance would couple the merged asset's serialized
        /// state to the source look's. Null passes through: the closing
        /// ClampToCaps maps a null curve to the neutral diagonal.
        /// </summary>
        private static AnimationCurve CopyCurve(AnimationCurve curve)
        {
            return curve != null ? new AnimationCurve(curve.keys) : null;
        }

        /// <summary>Reads one slot's weight, defaulting the missing-slot case (the fingerprint idiom).</summary>
        private static float WeightAt(List<float> weights, int index)
        {
            return index < weights.Count ? weights[index] : NamerEditorConstants.DefaultReferenceWeight;
        }

        /// <summary>Identity GUID of one reference texture (the ComputeFingerprint expression).</summary>
        private static string ReferenceGuid(Texture2D reference)
        {
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(reference));
        }

        /// <summary>Registers a carried reference's GUID for the first-wins dedup (empty GUIDs are not trackable).</summary>
        private static void TrackGuid(HashSet<string> seenGuids, Texture2D reference)
        {
            string guid = ReferenceGuid(reference);
            if (!string.IsNullOrEmpty(guid))
            {
                seenGuids.Add(guid);
            }
        }
    }

    /// <summary>
    /// The 05.3 M-series merge dialog (Tools/NAMER/Merge Style Assets…,
    /// STYL-15/D-05): migrates an existing <see cref="NamerStyleProfile"/>
    /// (required) and an optional <see cref="NamerPostLook"/> into a NEW
    /// <see cref="NamerStyleAsset"/> losing neither extracted palettes nor
    /// fitted looks (05.2 D-07 no-silent-loss). The sources are NEVER deleted,
    /// moved, or modified (M5 — zero delete/move/SetDirty calls on them; only
    /// <see cref="AssetDatabase.CreateAsset"/> on the user-chosen path runs).
    /// A union above <see cref="NamerEditorConstants.MaxReferences"/> shows the
    /// cap-honesty Error with the button disabled BEFORE any asset is created
    /// (M3). Creation is never silent (M4): the created asset is pinged and a
    /// console line names it. Volume consequence: the merged asset's Volume is
    /// keyed on the MERGED asset's name, so the source look's generated Volume
    /// orphans in place — accepted, never deleted silently (05.2 R8 precedent).
    /// All user-visible strings live in <see cref="NamerEditorConstants"/>
    /// (merge-dialog strings block — constants law).
    /// </summary>
    public sealed class NamerStyleAssetMerger : EditorWindow
    {
        private NamerStyleProfile _profile;
        private NamerPostLook _look;

        /// <summary>Opens the merge dialog via the Tools/NAMER menu (M3).</summary>
        [MenuItem("Tools/NAMER/Merge Style Assets…")]
        public static void Open()
        {
            GetWindow<NamerStyleAssetMerger>(NamerEditorConstants.MergeWindowTitle);
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField(NamerEditorConstants.MergeWindowTitle, EditorStyles.boldLabel);

            _profile = (NamerStyleProfile)EditorGUILayout.ObjectField(
                NamerEditorConstants.MergeProfileFieldLabel, _profile, typeof(NamerStyleProfile), false);
            _look = (NamerPostLook)EditorGUILayout.ObjectField(
                NamerEditorConstants.MergeLookFieldLabel, _look, typeof(NamerPostLook), false);

            // Cap honesty before creation (M3): the seam counts the would-be
            // union WITHOUT building an asset; the Error and the button gate
            // both read it.
            int unionCount = NamerStyleAssetMergerCore.CountUnionReferences(_profile, _look);
            bool capExceeded = unionCount > NamerEditorConstants.MaxReferences;

            if (_profile != null)
            {
                EditorGUILayout.LabelField(BuildPreviewLine(), EditorStyles.miniLabel);
            }

            if (capExceeded)
            {
                EditorGUILayout.HelpBox(
                    string.Format(
                        CultureInfo.InvariantCulture,
                        NamerEditorConstants.MergeCapErrorFormat,
                        unionCount,
                        NamerEditorConstants.MaxReferences),
                    MessageType.Error);
            }

            using (new EditorGUI.DisabledScope(_profile == null || capExceeded))
            {
                if (GUILayout.Button(NamerEditorConstants.MergeButtonLabel))
                {
                    RunMerge();
                }
            }
        }

        /// <summary>
        /// The merge-preview miniLabel line — the with-look variant names both
        /// sources, the look-less variant names the profile alone (05.3 locked
        /// copy; counts are assigned references, what the merge actually carries).
        /// </summary>
        private string BuildPreviewLine()
        {
            if (_look != null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    NamerEditorConstants.MergePreviewLineWithLookFormat,
                    _profile.name,
                    _profile.AssignedReferenceCount,
                    _look.name,
                    _look.AssignedReferenceCount);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                NamerEditorConstants.MergePreviewLineLookLessFormat,
                _profile.name,
                _profile.AssignedReferenceCount);
        }

        /// <summary>
        /// Runs the merge: save dialog (default name "{profileName} Style") →
        /// BuildMerged → CreateAsset on the chosen path → SaveAssets → ping →
        /// announcement console line (M4 — never silent). A cancelled save dialog
        /// is a no-op (the CreateAndAttachStyleProfile donor idiom).
        /// </summary>
        private void RunMerge()
        {
            if (_profile == null)
            {
                return;
            }

            string path = EditorUtility.SaveFilePanelInProject(
                NamerEditorConstants.MergeWindowTitle,
                _profile.name + NamerEditorConstants.MergeSaveDefaultNameSuffix,
                NamerEditorConstants.MergeSaveDialogExtension,
                NamerEditorConstants.MergeSaveDialogMessage);
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            NamerStyleAsset merged = NamerStyleAssetMergerCore.BuildMerged(_profile, _look);
            AssetDatabase.CreateAsset(merged, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(merged);
            Debug.Log(BuildAnnouncement(path));
        }

        /// <summary>The never-silent announcement console line — with-look and look-less variants (M4 locked copy).</summary>
        private string BuildAnnouncement(string assetPath)
        {
            if (_look != null)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    NamerEditorConstants.MergeAnnouncementWithLookFormat,
                    _profile.name,
                    _look.name,
                    assetPath);
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                NamerEditorConstants.MergeAnnouncementLookLessFormat,
                _profile.name,
                assetPath);
        }
    }
}

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Load-time migration of 5.1-era fitted <see cref="NamerStyleProfile"/> assets to
    /// standalone <see cref="NamerPostLook"/> assets (D-07 option a, 05.2 UI-SPEC
    /// section C M1-M3). Runs once per domain reload after the AssetDatabase is ready
    /// (<see cref="EditorApplication.delayCall"/>): scans for profiles whose fit block
    /// is populated (<see cref="NamerStyleProfile.HasPostFit"/>), copies the fit
    /// VERBATIM to a "{profile.name} Post.asset" beside the profile, then clears the
    /// source fit so the filter never re-selects it (idempotent). Never silent (M2):
    /// every real conversion pings the created asset and logs the locked console line
    /// naming source and destination. Never overwrites (GEN-03): an existing target
    /// file skips the profile entirely. Writes NO Volume (M3): the 5.1 companion Volume
    /// is left untouched on disk, and the migrated asset regenerates its own Volume on
    /// demand via the inspector's Regenerate Volume (A12). This class — and the 5.1
    /// post fields on the profile it drains — are DELETED in plan 05.2-06 once the
    /// migration window has served every project.
    /// </summary>
    [InitializeOnLoad]
    internal static class NamerPostLookMigration
    {
        static NamerPostLookMigration()
        {
            EditorApplication.delayCall += RunLoadedMigration;
        }

        /// <summary>
        /// M1: the load-time scan. Finds every <see cref="NamerStyleProfile"/> in the
        /// project, loads it, and migrates it when its fit block is populated. The
        /// delayCall scheduling keeps all AssetDatabase access off the static ctor
        /// (serialization-order safety); the HasPostFit filter plus MigrateProfile's
        /// own guards make the pass idempotent — a profile drained by a previous run
        /// is never re-selected, and a failed/skipped conversion is retried on the
        /// next domain reload (never destructive: see the existing-target check).
        /// </summary>
        private static void RunLoadedMigration()
        {
            string[] guids = AssetDatabase.FindAssets("t:NamerStyleProfile");
            for (int i = 0; i < guids.Length; i++)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[i]);
                NamerStyleProfile profile = AssetDatabase.LoadAssetAtPath<NamerStyleProfile>(path);
                if (profile == null || !profile.HasPostFit)
                {
                    continue;
                }

                MigrateProfile(profile);
            }
        }

        /// <summary>
        /// M1 core: copies one fitted profile's post look onto a new
        /// <see cref="NamerPostLook"/> asset at "{profile.name} Post.asset" beside the
        /// profile, then clears the source fit. Returns true when a conversion
        /// happened; false for every skip (null/unfitted profile, in-memory profile
        /// with no asset path, or an EXISTING target — never overwrite, GEN-03; the
        /// 5.1 writer's overwrite opt-in is not consulted because migration never
        /// passes an overwrite). The copy is verbatim: the six fitted floats (the
        /// profile never stored Bloom Intensity — the artist knob moves home defaulted,
        /// never fitted), the four curves as fresh key clones (no shared curve
        /// instances between source and target), the fit fingerprint and exemplar
        /// name, and the reference lists with null slots and weights preserved. Writes
        /// NO Volume (M3) — the old companion Volume stays on disk and the new asset's
        /// Volume is regenerated on demand via A12. M2: the conversion is never
        /// silent — the created asset is pinged and the locked console line names the
        /// source and destination.
        /// </summary>
        internal static bool MigrateProfile(NamerStyleProfile profile)
        {
            if (profile == null || !profile.HasPostFit)
            {
                return false;
            }

            string sourcePath = AssetDatabase.GetAssetPath(profile);
            if (string.IsNullOrEmpty(sourcePath))
            {
                // In-memory profile (never persisted) — nothing to migrate beside.
                return false;
            }

            string directory = Path.GetDirectoryName(sourcePath);
            string targetPath = string.IsNullOrEmpty(directory)
                ? profile.name + " Post.asset"
                : directory + "/" + profile.name + " Post.asset";

            // GEN-03 discipline: an existing asset/file at the target path is NEVER
            // overwritten — the profile is skipped whole (fit left intact so the
            // next load retries if the user resolves the collision).
            if (File.Exists(targetPath) || AssetDatabase.LoadAssetAtPath<NamerPostLook>(targetPath) != null)
            {
                return false;
            }

            NamerPostLook look = ScriptableObject.CreateInstance<NamerPostLook>();
            look.name = profile.name + " Post";
            look.PostTemperature = profile.PostTemperature;
            look.PostTint = profile.PostTint;
            look.PostExposureEv = profile.PostExposureEv;
            look.PostContrast = profile.PostContrast;
            look.PostSaturation = profile.PostSaturation;
            look.PostBloomThreshold = profile.PostBloomThreshold;
            look.PostMasterCurve = new AnimationCurve(profile.PostMasterCurve.keys);
            look.PostRedCurve = new AnimationCurve(profile.PostRedCurve.keys);
            look.PostGreenCurve = new AnimationCurve(profile.PostGreenCurve.keys);
            look.PostBlueCurve = new AnimationCurve(profile.PostBlueCurve.keys);
            look.PostFitFingerprint = profile.PostFitFingerprint;
            look.PostFitExemplarName = profile.PostFitExemplarName;
            look.References = profile.References != null
                ? new List<Texture2D>(profile.References)
                : new List<Texture2D>();
            look.ReferenceWeights = profile.ReferenceWeights != null
                ? new List<float>(profile.ReferenceWeights)
                : new List<float>();
            look.ClampToCaps();
            AssetDatabase.CreateAsset(look, targetPath);

            // Drain the source: a plain write (no Undo — the fit has MOVED, not been
            // user-edited) so HasPostFit reads false after and the scan filter never
            // re-selects this profile.
            profile.PostFitFingerprint = "";
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // M2: never silent — ping + the locked console line.
            EditorGUIUtility.PingObject(look);
            Debug.Log("NAMER: migrated fitted post look from " + profile.name + " to " + targetPath + ".");
            return true;
        }
    }
}

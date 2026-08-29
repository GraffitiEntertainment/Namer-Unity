using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Result of one <see cref="NamerProcessor.Process"/> run: the written generated
    /// assets, any non-blocking warnings, and a blocking error message (null/empty on
    /// success). <see cref="Error"/> is set — never thrown — for overwrite refusals,
    /// readback errors, destination escapes, or a missing NAMER shader so the window and
    /// tests can surface it.
    /// </summary>
    public sealed class NamerProcessResult
    {
        public List<NamerGeneratedAsset> GeneratedAssets = new List<NamerGeneratedAsset>();
        public List<string> Warnings = new List<string>();
        public string Error;

        /// <summary>Number of generated material sets (one per unique source material).</summary>
        public int MaterialCount => GeneratedAssets.Count;
    }

    /// <summary>
    /// The shared "Process with NAMER" entry point (D-13): validate -&gt; inspect -&gt;
    /// process -&gt; generate. Orchestrates <see cref="SourceInspector"/>,
    /// <see cref="NamerComputePipeline"/>, and <see cref="AssetGenerator"/> end-to-end,
    /// releasing every pooled compute result and disposing the pipeline so a batch never
    /// accumulates live render targets (T-03-05).
    /// </summary>
    public static class NamerProcessor
    {
        /// <summary>
        /// Processes the given selection into a NAMER material + textures under
        /// <c>{destination}/{source}/</c>. Returns a <see cref="NamerProcessResult"/> with
        /// written paths, warnings, and any blocking error rather than throwing.
        /// </summary>
        public static NamerProcessResult Process(UnityEngine.Object selection, NamerProcessorSettings settings)
        {
            var result = new NamerProcessResult();

            if (selection == null)
            {
                result.Warnings.Add("No selection provided.");
                return result;
            }

            if (settings == null)
            {
                result.Error = "No processor settings provided.";
                return result;
            }

            // D-01 / T-03-01: destination must be non-empty, under Assets/, and confined
            // to the project folder (Path.GetFullPath rejects '..'/absolute escapes).
            string destination = settings.Destination;
            if (string.IsNullOrEmpty(destination) || !destination.StartsWith("Assets/", StringComparison.Ordinal))
            {
                result.Error = "Destination must be non-empty and start with 'Assets/'.";
                return result;
            }

            if (!IsUnderProjectAssets(destination))
            {
                result.Error = "Destination '" + destination + "' must resolve under the project Assets/ folder.";
                return result;
            }

            NamerSourceModel model = SourceInspector.Inspect(selection);
            foreach (string warning in model.Warnings)
            {
                result.Warnings.Add(warning);
            }

            if (model.Materials.Count == 0)
            {
                return result;
            }

            string destinationFolder = AssetGenerator.ComposeDestinationFolder(destination, selection.name);
            EnsureFolder(destinationFolder);

            // D-04 idempotent reprocess: snapshot each scene renderer's per-slot source
            // identity BEFORE generation, because overwriting the generated material in
            // place invalidates the live renderer reference (the slot reads null after).
            Dictionary<Renderer, int[]> slotSources = null;
            if (selection is GameObject sceneObject
                && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(sceneObject)))
            {
                slotSources = CaptureSlotSources(sceneObject);
            }

            NamerComputePipeline pipeline = null;
            try
            {
                AssetGenerator.PreflightTargets(model, settings, destinationFolder);

                pipeline = new NamerComputePipeline();
                AssetGenerator generator = new AssetGenerator();

                foreach (NamerMaterialInspection inspection in model.Materials)
                {
                    inspection.AoUnmultiplyStrength = settings.AoUnmultiplyStrength;
                    inspection.AoBlurRadius = settings.AoBlurRadius;
                    inspection.AoStrength = settings.AoStrength;
                    inspection.AoContrast = settings.AoContrast;
                    NamerComputeResult computeResult = pipeline.Process(inspection);
                    try
                    {
                        NamerGeneratedAsset asset = generator.Generate(computeResult, inspection, settings, destinationFolder);
                        result.GeneratedAssets.Add(asset);
                    }
                    catch (InvalidOperationException ex)
                    {
                        result.Error = ex.Message;
                        return result;
                    }
                    finally
                    {
                        pipeline.ReleaseResult(computeResult);
                    }
                }
            }
            catch (InvalidOperationException ex)
            {
                result.Error = ex.Message;
                return result;
            }
            finally
            {
                if (pipeline != null)
                {
                    pipeline.Dispose();
                }
            }

            if (string.IsNullOrEmpty(result.Error))
            {
                BindGeneratedMaterials(selection, slotSources, model, result);
            }

            Debug.Log("[NAMER] Processed '" + selection.name + "': " + result.MaterialCount
                + " material(s) generated in '" + destinationFolder + "'"
                + (result.Warnings.Count > 0 ? " (" + result.Warnings.Count + " warning(s))" : string.Empty));

            return result;
        }

        /// <summary>
        /// Swaps each scene renderer's per-sub-mesh material slot to its index-aligned
        /// generated material after a successful Process. The only mutation is the live
        /// scene renderer's <c>sharedMaterials</c> array — no source material, texture,
        /// importer, FBX, or <c>.meta</c> is written, imported, or modified.
        /// </summary>
        private static void BindGeneratedMaterials(
            UnityEngine.Object selection,
            Dictionary<Renderer, int[]> slotSources,
            NamerSourceModel model,
            NamerProcessResult result)
        {
            if (!(selection is GameObject gameObject))
            {
                return;
            }

            if (!string.IsNullOrEmpty(AssetDatabase.GetAssetPath(gameObject)))
            {
                return;
            }

            var generatedBySourceId = new Dictionary<int, Material>();
            int count = Math.Min(model.Materials.Count, result.GeneratedAssets.Count);
            for (int i = 0; i < count; i++)
            {
                Material source = model.Materials[i].Material;
                Material generated = AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[i].MaterialPath);
                if (source != null && generated != null)
                {
                    generatedBySourceId[source.GetInstanceID()] = generated;
                }
            }

            foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                if (slotSources == null || !slotSources.TryGetValue(renderer, out int[] sourceIds))
                {
                    continue;
                }

                Material[] shared = renderer.sharedMaterials;
                bool changed = false;
                for (int i = 0; i < sourceIds.Length && i < shared.Length; i++)
                {
                    int sourceId = sourceIds[i];
                    if (sourceId != 0 && generatedBySourceId.TryGetValue(sourceId, out Material generated))
                    {
                        shared[i] = generated;
                        changed = true;
                    }
                }

                if (changed)
                {
                    renderer.sharedMaterials = shared;
                }
            }
        }

        /// <summary>
        /// Captures each scene renderer's per-slot ORIGINAL source instance ID before
        /// generation. A slot wearing a previous generated material resolves through its
        /// <c>NamerSource</c> tag to the original source, so the post-generation bind swaps
        /// it (and any slot whose live reference the in-place overwrite invalidated) to the
        /// fresh generated material by index.
        /// </summary>
        private static Dictionary<Renderer, int[]> CaptureSlotSources(GameObject gameObject)
        {
            var slotSources = new Dictionary<Renderer, int[]>();
            foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                Material[] shared = renderer.sharedMaterials;
                int[] sourceIds = new int[shared.Length];
                for (int i = 0; i < shared.Length; i++)
                {
                    sourceIds[i] = ResolveSourceInstanceId(shared[i]);
                }

                slotSources[renderer] = sourceIds;
            }

            return slotSources;
        }

        /// <summary>
        /// Maps a renderer slot material to the instance ID of its ORIGINAL source so a
        /// slot wearing a previous generated material (tagged <c>NamerSource</c>) swaps to
        /// the fresh generated material bound to that same original — not just slots still
        /// wearing the raw source instance (D-04 idempotent reprocess).
        /// </summary>
        private static int ResolveSourceInstanceId(Material material)
        {
            if (material == null)
            {
                return 0;
            }

            Material original = SourceInspector.ResolveOriginalFromSourceTag(
                material.GetTag(NamerEditorConstants.SourceTag, false, string.Empty));
            return original != null ? original.GetInstanceID() : material.GetInstanceID();
        }

        private static bool IsUnderProjectAssets(string destination)
        {
            string fullDestination = Path.GetFullPath(destination);
            string assetsFullPath = Path.GetFullPath(Application.dataPath);

            return fullDestination.Equals(assetsFullPath, StringComparison.OrdinalIgnoreCase)
                || fullDestination.StartsWith(assetsFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureFolder(string folder)
        {
            string normalized = folder.TrimEnd('/', '\\');
            if (string.IsNullOrEmpty(normalized) || normalized == "Assets")
            {
                return;
            }

            if (AssetDatabase.IsValidFolder(normalized))
            {
                return;
            }

            string parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
            string folderName = Path.GetFileName(normalized);

            if (!string.IsNullOrEmpty(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}

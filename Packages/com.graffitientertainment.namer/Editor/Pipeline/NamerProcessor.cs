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

            string destinationFolder = destination.TrimEnd('/', '\\') + "/"
                + AssetGenerator.SanitizeFileName(selection.name) + "/";
            EnsureFolder(destinationFolder);

            NamerComputePipeline pipeline = null;
            try
            {
                AssetGenerator.PreflightTargets(model, settings, destinationFolder);

                pipeline = new NamerComputePipeline();
                AssetGenerator generator = new AssetGenerator();

                foreach (NamerMaterialInspection inspection in model.Materials)
                {
                    inspection.AoUnmultiplyStrength = settings.AoUnmultiplyStrength;
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

            Debug.Log("[NAMER] Processed '" + selection.name + "': " + result.MaterialCount
                + " material(s) generated in '" + destinationFolder + "'"
                + (result.Warnings.Count > 0 ? " (" + result.Warnings.Count + " warning(s))" : string.Empty));

            return result;
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

            string parent = Path.GetDirectoryName(normalized);
            string folderName = Path.GetFileName(normalized);

            if (!string.IsNullOrEmpty(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, folderName);
        }
    }
}

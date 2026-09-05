using System;
using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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
                AssetGenerator.PreflightTargets(model, settings, destinationFolder, settings.DecompositionEnabled);

                // D-05 decomposition needs the source mesh the inspection wears. Resolve it
                // once from the selection (a scene object, prefab, or model) so the split and
                // the later renderer sharedMesh swap agree on the same source.
                Mesh decomposeSourceMesh = settings.DecompositionEnabled ? ResolveSourceMesh(selection) : null;

                // CR-01: one vertex-color stream per source mesh cannot carry N materials'
                // fits simultaneously, and a multi-mesh selection resolves only its FIRST
                // mesh (FindMeshInObject/FindMeshSubAsset) while BindGeneratedMaterials swaps
                // each renderer by its OWN mesh (ResolveRendererMesh) — so the un-resolved
                // second mesh stays un-split yet its material slot still binds the residual,
                // silently rendering wrong colors even though model.Materials.Count == 1
                // (SourceInspector.AddUnique dedupes the shared material by instance ID).
                // Setting decomposeSourceMesh = null makes every material take the existing
                // decomp == null Phase-3 path below, so both disjuncts produce correct
                // non-decomposed output plus a warning instead of silent garbage.
                int distinctSourceMeshes = decomposeSourceMesh != null ? CountDistinctSourceMeshes(selection) : 0;
                if (decomposeSourceMesh != null && (model.Materials.Count > 1 || distinctSourceMeshes > 1))
                {
                    result.Warnings.Add("Vertex-color decomposition skipped for '" + selection.name
                        + "': the selection maps " + model.Materials.Count + " material(s) to "
                        + distinctSourceMeshes + " source mesh(es) — generating the non-decomposed Phase-3 shape instead.");
                    decomposeSourceMesh = null;
                }

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
                        NamerDecompData decomp = null;
                        VertexColorFitResult fit = null;
                        NamerDecompPipeline decompPipeline = null;
                        NamerDecompOutput decompOutput = null;
                        NativeArray<Color32> baseTexels = default;
                        try
                        {
                            if (settings.DecompositionEnabled)
                            {
                                inspection.BakeSourceMesh = decomposeSourceMesh;
                                if (decomposeSourceMesh == null)
                                {
                                    result.Warnings.Add("No mesh to decompose for material '"
                                        + (inspection.Material != null ? inspection.Material.name : "(null)")
                                        + "' — generating the Phase-3 shape instead.");
                                }
                                else
                                {
                                    baseTexels = ReadBackBase(computeResult.NormalizedBaseColor);
                                    NamerSplitResult split = MeshVertexSplitter.Split(decomposeSourceMesh);
                                    fit = VertexColorFitter.Fit(split, baseTexels, computeResult.Width, computeResult.Height);
                                    Color32[] colors = fit.ToColor32Array();
                                    decompPipeline = new NamerDecompPipeline();
                                    decompOutput = decompPipeline.GenerateResidual(
                                        split, colors, computeResult.NormalizedBaseColor,
                                        computeResult.Width, computeResult.Height,
                                        settings.ErrorThreshold, settings.ResidualResolution);
                                    if (decompOutput.Stats.CannotDecompose)
                                    {
                                        // CR-03 fallback: near-zero rasterizer coverage means the
                                        // fit was never validated — leave decomp null so the Phase-3
                                        // shape is generated instead of a bogus residual.
                                        result.Warnings.Add("Vertex-color decomposition skipped for material '"
                                            + (inspection.Material != null ? inspection.Material.name : "(null)")
                                            + "': UV coverage near zero (tiling/out-of-range UVs) — generating the non-decomposed Phase-3 shape instead.");
                                    }
                                    else
                                    {
                                        decomp = new NamerDecompData
                                        {
                                            Split = split,
                                            Colors = colors,
                                            Residual = decompOutput.Residual,
                                            Stats = decompOutput.Stats,
                                        };
                                    }
                                }
                            }

                            NamerGeneratedAsset asset = generator.Generate(computeResult, inspection, settings, destinationFolder, decomp);
                            result.GeneratedAssets.Add(asset);
                        }
                        finally
                        {
                            // The residual RT is read back synchronously inside Generate, so
                            // release it (and the pool) only after Generate returns.
                            if (decompOutput != null)
                            {
                                decompOutput.Dispose();
                            }

                            if (decompPipeline != null)
                            {
                                decompPipeline.Dispose();
                            }

                            if (fit != null)
                            {
                                fit.Dispose();
                            }

                            if (baseTexels.IsCreated)
                            {
                                baseTexels.Dispose();
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
            var generatedMeshBySourceMeshId = new Dictionary<int, Mesh>();
            int count = Math.Min(model.Materials.Count, result.GeneratedAssets.Count);
            for (int i = 0; i < count; i++)
            {
                Material source = model.Materials[i].Material;
                Material generated = AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[i].MaterialPath);
                if (source != null && generated != null)
                {
                    generatedBySourceId[source.GetInstanceID()] = generated;
                }

                Mesh sourceMesh = model.Materials[i].BakeSourceMesh;
                string meshPath = result.GeneratedAssets[i].MeshPath;
                if (sourceMesh != null && !string.IsNullOrEmpty(meshPath))
                {
                    Mesh generatedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                    if (generatedMesh != null)
                    {
                        generatedMeshBySourceMeshId[sourceMesh.GetInstanceID()] = generatedMesh;
                    }
                }
            }

            foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
            {
                // D-07 / Pitfall 3: swap the renderer's mesh to the generated split mesh so
                // the fitted vertex colors render. Guarded on the renderer still wearing the
                // split source mesh (T-04-09) — never swap an unrelated renderer.
                Mesh currentMesh = ResolveRendererMesh(renderer);
                if (currentMesh != null && generatedMeshBySourceMeshId.TryGetValue(currentMesh.GetInstanceID(), out Mesh generatedMesh))
                {
                    SetRendererMesh(renderer, generatedMesh);
                }

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

        private static Mesh ResolveRendererMesh(Renderer renderer)
        {
            // A MeshFilter is a Component sibling of Renderer (not a subclass), so read it
            // via GetComponent; a SkinnedMeshRenderer IS the Renderer and owns its own mesh.
            if (renderer is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh;
            }

            MeshFilter mf = renderer.GetComponent<MeshFilter>();
            return mf != null ? mf.sharedMesh : null;
        }

        private static void SetRendererMesh(Renderer renderer, Mesh mesh)
        {
            if (renderer is SkinnedMeshRenderer smr)
            {
                smr.sharedMesh = mesh;
                return;
            }

            MeshFilter mf = renderer.GetComponent<MeshFilter>();
            if (mf != null)
            {
                mf.sharedMesh = mesh;
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

        /// <summary>
        /// Reads the linear base-color render target back as RGBA32 texels for the
        /// vertex-color fit. Throws on a failed readback so no files are written on a
        /// GPU error (the same contract as <see cref="AssetGenerator"/>).
        /// </summary>
        private static NativeArray<Color32> ReadBackBase(RenderTexture source)
        {
            AsyncGPUReadbackRequest request = NamerComputePipeline.RequestReadback(source, 0, TextureFormat.RGBA32);
            request.forcePlayerLoopUpdate = true;
            request.WaitForCompletion();

            if (request.hasError)
            {
                throw new InvalidOperationException("GPU readback failed while decomposing; no files were written.");
            }

            return request.GetData<Color32>();
        }

        /// <summary>
        /// Resolves the single source mesh a selection wears (D-05 decomposition input).
        /// Mirrors the processor window's preview-mesh resolution: scene renderers first,
        /// then prefab contents, then model/FBX sub-assets. Null when the selection has no
        /// mesh (e.g. a bare material), which decomposition treats as a non-blocking skip.
        /// </summary>
        private static Mesh ResolveSourceMesh(UnityEngine.Object selection)
        {
            if (selection == null)
            {
                return null;
            }

            if (selection is GameObject gameObject)
            {
                string assetPath = AssetDatabase.GetAssetPath(gameObject);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return FindMeshInObject(gameObject);
                }

                PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(gameObject);
                if (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant)
                {
                    Mesh subAsset = FindMeshSubAsset(assetPath);
                    if (subAsset != null)
                    {
                        return subAsset;
                    }

                    GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                    try
                    {
                        Mesh mesh = contents != null ? FindMeshInObject(contents) : null;
                        return mesh != null && AssetDatabase.Contains(mesh) ? mesh : null;
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                }

                return FindMeshSubAsset(assetPath);
            }

            string path = AssetDatabase.GetAssetPath(selection);
            return string.IsNullOrEmpty(path) ? null : FindMeshSubAsset(path);
        }

        private static Mesh FindMeshInObject(GameObject gameObject)
        {
            MeshFilter filter = gameObject.GetComponentInChildren<MeshFilter>(true);
            if (filter != null && filter.sharedMesh != null)
            {
                return filter.sharedMesh;
            }

            SkinnedMeshRenderer skinned = gameObject.GetComponentInChildren<SkinnedMeshRenderer>(true);
            if (skinned != null && skinned.sharedMesh != null)
            {
                return skinned.sharedMesh;
            }

            return null;
        }

        private static Mesh FindMeshSubAsset(string assetPath)
        {
            foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (subAsset is Mesh mesh)
                {
                    return mesh;
                }
            }

            return null;
        }

        /// <summary>
        /// Counts the DISTINCT source meshes a selection resolves to, mirroring
        /// <see cref="ResolveSourceMesh"/>'s per-case resolution but collecting a
        /// <see cref="HashSet{T}"/> of mesh instance IDs instead of stopping at the first
        /// mesh (CR-01 guard input). Scene renderers, then prefab contents, then model/FBX
        /// sub-assets — the same order the single-mesh resolution uses.
        /// </summary>
        private static int CountDistinctSourceMeshes(UnityEngine.Object selection)
        {
            if (selection == null)
            {
                return 0;
            }

            var distinctIds = new HashSet<int>();

            if (selection is GameObject gameObject)
            {
                string assetPath = AssetDatabase.GetAssetPath(gameObject);
                if (string.IsNullOrEmpty(assetPath))
                {
                    foreach (Renderer renderer in gameObject.GetComponentsInChildren<Renderer>(true))
                    {
                        Mesh mesh = ResolveRendererMesh(renderer);
                        if (mesh != null)
                        {
                            distinctIds.Add(mesh.GetInstanceID());
                        }
                    }

                    return distinctIds.Count;
                }

                PrefabAssetType prefabType = PrefabUtility.GetPrefabAssetType(gameObject);
                if (prefabType == PrefabAssetType.Regular || prefabType == PrefabAssetType.Variant)
                {
                    GameObject contents = PrefabUtility.LoadPrefabContents(assetPath);
                    try
                    {
                        if (contents != null)
                        {
                            foreach (Renderer renderer in contents.GetComponentsInChildren<Renderer>(true))
                            {
                                Mesh mesh = ResolveRendererMesh(renderer);
                                if (mesh != null)
                                {
                                    distinctIds.Add(mesh.GetInstanceID());
                                }
                            }
                        }
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }

                    return distinctIds.Count;
                }

                foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (subAsset is Mesh mesh)
                    {
                        distinctIds.Add(mesh.GetInstanceID());
                    }
                }

                return distinctIds.Count;
            }

            string path = AssetDatabase.GetAssetPath(selection);
            if (string.IsNullOrEmpty(path))
            {
                return 0;
            }

            foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (subAsset is Mesh mesh)
                {
                    distinctIds.Add(mesh.GetInstanceID());
                }
            }

            return distinctIds.Count;
        }
    }
}

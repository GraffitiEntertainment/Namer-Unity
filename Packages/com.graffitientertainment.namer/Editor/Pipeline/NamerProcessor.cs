using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
            // Completed-task contract (05.3 threading): the core's ONLY awaits are on
            // the seams injected by CreateSyncSeams (already-completed tasks around
            // the blocking calls), so the core never yields, never posts a
            // continuation to Unity's synchronization context, and the task below is
            // complete when it returns — GetResult cannot deadlock. DO NOT add any
            // other await to ProcessCoreAsync — that breaks this guarantee.
            return ProcessCoreAsync(selection, settings, null, null, CreateSyncSeams())
                .GetAwaiter().GetResult();
        }

        /// <summary>
        /// Awaitable mirror of <see cref="Process"/> (05.3 threading): the identical
        /// validate -&gt; inspect -&gt; process -&gt; generate orchestration with the AO bake
        /// (Burst worker thread), the projection readback/fit/residual, the
        /// legacy-branch readbacks, and the generation readbacks released to the
        /// editor loop instead of blocking it. Continuations resume on the main
        /// thread (Unity's synchronization context), so every GPU dispatch and
        /// AssetDatabase call stays main-thread; results are identical to
        /// <see cref="Process"/> on the same inputs. A user cancel surfaces as a
        /// warning (cancelled between materials — completed materials keep their
        /// assets) or as <see cref="NamerProcessResult.Error"/> (cancelled
        /// mid-material, thrown by the pipeline as
        /// <see cref="InvalidOperationException"/> and caught here) — never as
        /// partially-written assets. <paramref name="reportProgress"/> receives the
        /// overall fraction and phase message for a cancellable progress bar.
        /// </summary>
        public static Task<NamerProcessResult> ProcessAsync(
            UnityEngine.Object selection,
            NamerProcessorSettings settings,
            Func<bool> shouldCancel = null,
            Action<float, string> reportProgress = null)
        {
            return ProcessCoreAsync(selection, settings, shouldCancel, reportProgress, CreateAsyncSeams(shouldCancel, reportProgress));
        }

        /// <summary>
        /// The single processing implementation shared by <see cref="Process"/> and
        /// <see cref="ProcessAsync"/>. <paramref name="seams"/> carries the ONLY await
        /// points (see <see cref="ProcessSeams"/>): the sync entry point injects
        /// already-completed tasks so the whole core runs inline (the wrapper's
        /// completed-task contract), the awaitable entry point injects the awaitable
        /// mirrors. Everything else is synchronous main-thread orchestration
        /// (inspect, preflight, mesh resolution, stylization plumbing, warnings,
        /// binding, disposal). <paramref name="shouldCancel"/> is polled between
        /// materials — a cancel stops the batch gracefully (warning + break);
        /// <paramref name="reportProgress"/> receives the per-material fraction and
        /// the pipeline's phase messages.
        /// </summary>
        private static async Task<NamerProcessResult> ProcessCoreAsync(
            UnityEngine.Object selection,
            NamerProcessorSettings settings,
            Func<bool> shouldCancel,
            Action<float, string> reportProgress,
            ProcessSeams seams)
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

                // 05.3-05 (STYL-16): resolve the attached MERGED style asset ONCE from
                // the dedicated StyleAssetGuid pref (empty GUID -> null, the
                // byte-identical baseline; deleted asset -> silent detach). The legacy
                // StyleProfileGuid pref no longer applies on the bake side (A9) — a
                // stale legacy profile GUID must not style the bake. Stylization with
                // Smoothing > 0 needs the source mesh for island ids, so it joins the
                // priming disjunct below (palette-only assets never resolve a mesh).
                NamerStyleAsset styleAsset = ResolveStyleAsset(settings);

                // D-05 decomposition needs the source mesh the inspection wears. Resolve it
                // once from the selection (a scene object, prefab, or model) so the split and
                // the later renderer sharedMesh swap agree on the same source. The 2026-09-21
                // AO contract follow-up also resolves it for the AO stage alone: the bake is
                // the synthetic source whenever the stage is on, decomposition or not.
                Mesh decomposeSourceMesh = (settings.DecompositionEnabled || settings.AoStageEnabled
                    || StylizationNeedsMesh(styleAsset)) ? ResolveSourceMesh(selection) : null;

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
                bool decompGuardTripped = false;
                string skipReason = DecompositionSkipReason(selection, model, decomposeSourceMesh, distinctSourceMeshes);
                if (skipReason != null)
                {
                    result.Warnings.Add("Vertex-color decomposition skipped for '" + selection.name
                        + "': " + skipReason + " — generating the non-decomposed Phase-3 shape instead.");
                    decomposeSourceMesh = null;
                    decompGuardTripped = true;
                }

                pipeline = new NamerComputePipeline();
                AssetGenerator generator = new AssetGenerator();

                // 05-03: when stylization needs a mesh but decomposition does NOT run,
                // split the (already CR-01-vetted) source mesh ONCE for the whole batch —
                // island ids are mesh-intrinsic, so every material shares the split. When
                // decomposition runs, the per-material fitSplit below is reused instead.
                NamerSplitResult stylizeSplitShared = styleAsset != null
                    && !settings.DecompositionEnabled
                    && decomposeSourceMesh != null
                    ? MeshVertexSplitter.Split(decomposeSourceMesh)
                    : null;

                int totalMaterials = model.Materials.Count;
                int materialIndex = 0;
                foreach (NamerMaterialInspection inspection in model.Materials)
                {
                    // 05.3 threading: cancel BETWEEN materials is a graceful stop, not
                    // an error — completed materials keep their generated assets and
                    // the caller sees exactly where the run stopped. Mid-material
                    // cancels surface as pipeline InvalidOperationExceptions instead.
                    if (shouldCancel != null && shouldCancel())
                    {
                        result.Warnings.Add("Cancelled after " + result.MaterialCount
                            + " material(s); remaining materials were not processed.");
                        break;
                    }

                    if (reportProgress != null)
                    {
                        reportProgress(
                            (float)materialIndex / totalMaterials,
                            "Processing material '" + (inspection.Material != null ? inspection.Material.name : "(null)")
                                + "' (" + (materialIndex + 1) + "/" + totalMaterials + ")...");
                    }

                    // 2026-09-21 contract: the AO stage checkbox gates SYNTHESIS only
                    // (bake vs white fill, in the D-07 gate) — never the authored-map
                    // transfer — so the un-multiply strength passes through unchanged:
                    // with the stage off and no authored map, surface B is white and the
                    // divide is the identity whatever the strength.
                    inspection.AoUnmultiplyStrength = settings.AoUnmultiplyStrength;
                    inspection.AoStageEnabled = settings.AoStageEnabled;
                    inspection.AoBlurRadius = settings.AoBlurRadius;
                    inspection.AoStrength = settings.AoStrength;
                    inspection.AoContrast = settings.AoContrast;
                    inspection.RoughnessExtractStrength = settings.RoughnessStageEnabled ? settings.RoughnessExtractStrength : 0f;
                    inspection.DipSource = (NamerDipSource)settings.DipSource;

                    // 05-03 D-07/D-08: plumb the attached style asset's bake-side controls
                    // into the inspection (shared with the preview window so the saved bake
                    // and the stylized preview compose identically — phase boundary
                    // invariant).
                    ApplyStyleControls(inspection, styleAsset);

                    // ResolveSourceMesh ordering: attach the source mesh BEFORE Process so the
                    // projection/transfer and the AO three-way gate can reference it. (The
                    // decomp block's old assignment here moved up.) Primed for the AO stage
                    // too (2026-09-21 contract follow-up): AO on + decomposition off bakes
                    // instead of packing white. The CR-01 guard above still nulls the mesh
                    // for multi-material/multi-mesh selections, so those keep the safe
                    // white fill.
                    if (settings.DecompositionEnabled || settings.AoStageEnabled)
                    {
                        inspection.BakeSourceMesh = decomposeSourceMesh;
                    }

                    // 04.2: build the projection context when decomposition runs with the
                    // RemovedDetail dip source. The context's Run drives the projection
                    // write-back + residual inside pipeline.Process; the legacy SobelEdge
                    // branch (decomposition on, no projection) keeps the post-Process
                    // readback -> fit -> GenerateResidual flow with mode from
                    // settings.WriteResidual (AlwaysKeep/NeverKeep — Gate retires from call
                    // sites).
                    int baseW = inspection.BaseMap != null ? inspection.BaseMap.width : NamerComputePipeline.DefaultBaseResolution;
                    int baseH = inspection.BaseMap != null ? inspection.BaseMap.height : NamerComputePipeline.DefaultBaseResolution;
                    bool decomposeWillRun = settings.DecompositionEnabled && decomposeSourceMesh != null;
                    bool projectionPath = decomposeWillRun && inspection.DipSource == NamerDipSource.RemovedDetail;
                    NamerSplitResult fitSplit = null;
                    NamerDecompPipeline decompPipeline = null;
                    NamerProjectionContext projection = null;
                    if (decomposeWillRun)
                    {
                        fitSplit = MeshVertexSplitter.Split(decomposeSourceMesh);
                        decompPipeline = new NamerDecompPipeline();
                        if (projectionPath)
                        {
                            projection = seams.CreateProjection(
                                fitSplit, decompPipeline, baseW, baseH,
                                settings.ErrorThreshold, settings.ResidualResolution, settings.WriteResidual,
                                settings.CoverageTarget);
                        }
                    }

                    // 05-03: compose the stylization context (05-02 NamerStyleStylization).
                    // The split is the decomposition fitSplit when decomposition runs, the
                    // pre-split shared mesh otherwise; null mesh (bare material, CR-01
                    // guard, or unresolvable selection) degrades to palette-only with
                    // SmoothingSkippedNoMesh set. asset == null keeps stylization null —
                    // zero stylize dispatches, byte-identical.
                    // 05.2-03 (STYL-11): the palette transfer is hue/vibrance-only — it
                    // never pulls luma, fitted or not; tonality is owned exclusively by
                    // the post look (the NamerPostLook asset's scene Volume).
                    NamerStyleStylization stylization = null;
                    if (styleAsset != null)
                    {
                        stylization = NamerStyleStylization.Create(
                            styleAsset, fitSplit != null ? fitSplit : stylizeSplitShared);
                    }

                    try
                    {
                    // 05.3 threading: the pipeline reports its internal (bake) fraction;
                    // compose it into the overall per-material fraction so the bar stays
                    // monotone across materials.
                    Action<float, string> materialProgress = reportProgress != null
                        ? (fraction, phase) => reportProgress((materialIndex + fraction) / totalMaterials, phase)
                        : null;
                    NamerComputeResult computeResult = await seams.RunCompute(
                        pipeline, inspection, projection, stylization, materialProgress);
                    try
                    {
                        NamerDecompData decomp = null;
                        VertexColorFitResult fit = null;
                        NamerDecompOutput decompOutput = null;
                        NativeArray<Color32> baseTexels = default;
                        try
                        {
                            if (settings.DecompositionEnabled)
                            {
                                // WR-02: the CR-01 guard above already explained why the mesh
                                // is null (with the accurate material/mesh counts) — only a
                                // genuine resolution failure (no mesh was ever found) adds
                                // this per-material warning, so a guard trip emits exactly
                                // ONE accurate warning instead of N+1 contradictory ones.
                                if (decomposeSourceMesh == null && !decompGuardTripped)
                                {
                                    result.Warnings.Add("No mesh to decompose for material '"
                                        + (inspection.Material != null ? inspection.Material.name : "(null)")
                                        + "' — generating the Phase-3 shape instead.");
                                }

                                // The null-gate is deliberately separate from the warning
                                // above: a guard trip (mesh null, guard tripped) must take
                                // NEITHER branch — it skips the split path exactly as it did
                                // before WR-02, where the old if/else coupled the two.
                                if (decomposeSourceMesh != null)
                                {
                                    if (projection != null)
                                    {
                                        // 04.2 projection path: the context already produced
                                        // Split/Colors/Decomp inside Process.
                                        NamerProjectionContext ctx = projection;
                                        if (ctx.Decomp != null && ctx.Decomp.Stats.CannotDecompose)
                                        {
                                            // CR-03 fallback: near-zero rasterizer coverage means the
                                            // fit was never validated — leave decomp null so the Phase-3
                                            // shape is generated instead of a bogus residual.
                                            result.Warnings.Add("Vertex-color decomposition skipped for material '"
                                                + (inspection.Material != null ? inspection.Material.name : "(null)")
                                                + "': UV coverage near zero (tiling/out-of-range UVs) — generating the non-decomposed Phase-3 shape instead.");
                                        }
                                        else if (ctx.Decomp != null && ctx.Decomp.Stats != null)
                                        {
                                            decomp = new NamerDecompData
                                            {
                                                Split = ctx.Split,
                                                Colors = ctx.Colors,
                                                Residual = ctx.Decomp.Residual,
                                                Stats = ctx.Decomp.Stats,
                                            };
                                        }
                                    }
                                    else
                                    {
                                        // Legacy SobelEdge branch: readback -> fit ->
                                        // GenerateResidual, mode from settings.WriteResidual.
                                        baseTexels = await seams.ReadBase(computeResult.NormalizedBaseColor);
                                        NamerSplitResult split = fitSplit;
                                        fit = await seams.Fit(split, baseTexels, computeResult.Width, computeResult.Height);
                                        Color32[] colors = fit.ToColor32Array();
                                        decompOutput = await seams.GenerateResidual(
                                            decompPipeline, split, colors, computeResult.NormalizedBaseColor,
                                            computeResult.Width, computeResult.Height,
                                            settings.ErrorThreshold, settings.ResidualResolution,
                                            settings.WriteResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep,
                                            settings.CoverageTarget);
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
                            }

                            NamerGeneratedAsset asset = await seams.Generate(generator, computeResult, inspection, settings, destinationFolder, decomp);
                            result.GeneratedAssets.Add(asset);

                            // Surface the chosen residual resolution so a residual-resolution
                            // choice is never silent (04.2 residual-smeared-reconstruction
                            // investigation: a manual 128 popup produced a smeared residual
                            // that read like an adaptive-ladder metric bug). Mirror the
                            // AssetGenerator writeResidual gate so this logs exactly when a
                            // residual was actually written.
                            if (decomp != null && decomp.Stats != null
                                && decomp.Stats.ResidualRequired && decomp.Residual != null)
                            {
                                Debug.Log("[NAMER] Residual written for '"
                                    + (inspection.Material != null ? inspection.Material.name : "(null)")
                                    + "' at " + decomp.Stats.ChosenResolution + "px long edge ("
                                    + decomp.Residual.width + "x" + decomp.Residual.height + " texels).");
                            }
                        }
                        finally
                        {
                            // The residual RT is read back inside Generate/GenerateAsync,
                            // so release it (and the pool) only after that returns.
                            if (projection != null && projection.Decomp != null)
                            {
                                projection.Decomp.Dispose();
                            }

                            if (decompOutput != null)
                            {
                                decompOutput.Dispose();
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
                    finally
                    {
                        if (decompPipeline != null)
                        {
                            decompPipeline.Dispose();
                        }
                    }

                    materialIndex++;
                }

                // 05.2-03 R8: the Process-time companion-Volume regeneration block is
                // RETIRED — the Volume lifecycle belongs to the post-look asset's
                // inspector (Fit Look / Regenerate Volume). Process writes/refreshes no
                // Volume; orphaned 5.1 companion Volumes are left in place, never
                // auto-deleted.
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
        /// The await seams of <see cref="ProcessCoreAsync"/> (05.3 threading): one
        /// delegate per blocking point, so the single core serves both the
        /// synchronous <see cref="Process"/> (completed-task contract —
        /// <see cref="CreateSyncSeams"/> injects already-completed tasks, the core
        /// never yields, and the wrapper blocks safely) and the awaitable
        /// <see cref="ProcessAsync"/> (<see cref="CreateAsyncSeams"/> injects the
        /// awaitable mirrors, releasing the editor loop). Adding a new await to the
        /// core REQUIRES a matching seam field here plus both factory assignments.
        /// </summary>
        private sealed class ProcessSeams
        {
            /// <summary>Projection-context factory: wires <c>Run</c> (sync) or <c>RunAsync</c>.</summary>
            public Func<NamerSplitResult, NamerDecompPipeline, int, int, float, int, bool, float, NamerProjectionContext> CreateProjection;

            /// <summary>Compute entry: <c>Process</c> (sync) or <c>ProcessAsync</c> (awaitable).</summary>
            public Func<NamerComputePipeline, NamerMaterialInspection, NamerProjectionContext, NamerStyleStylization, Action<float, string>, Task<NamerComputeResult>> RunCompute;

            /// <summary>Linear base-color readback: blocking (sync) or awaitable.</summary>
            public Func<RenderTexture, Task<NativeArray<Color32>>> ReadBase;

            /// <summary>Vertex-color fit: inline (sync) or Burst worker thread (awaitable).</summary>
            public Func<NamerSplitResult, NativeArray<Color32>, int, int, Task<VertexColorFitResult>> Fit;

            /// <summary>Legacy-branch residual generation: blocking (sync) or awaitable.</summary>
            public Func<NamerDecompPipeline, NamerSplitResult, Color32[], RenderTexture, int, int, float, int, NamerResidualMode, float, Task<NamerDecompOutput>> GenerateResidual;

            /// <summary>Asset generation: blocking (sync) or awaitable readbacks.</summary>
            public Func<AssetGenerator, NamerComputeResult, NamerMaterialInspection, NamerProcessorSettings, string, NamerDecompData, Task<NamerGeneratedAsset>> Generate;
        }

        /// <summary>
        /// The synchronous seam set injected by <see cref="Process"/>: every delegate
        /// runs the original blocking call and wraps it in an already-completed task
        /// (the completed-task contract), so <see cref="ProcessCoreAsync"/> runs
        /// inline and its returned task is complete.
        /// </summary>
        private static ProcessSeams CreateSyncSeams()
        {
            return new ProcessSeams
            {
                CreateProjection = CreateProjectionContext,
                RunCompute = (compute, insp, projection, stylization, _) =>
                    Task.FromResult(compute.Process(insp, projection, stylization)),
                ReadBase = source => Task.FromResult(ReadBackBase(source)),
                Fit = (split, texels, w, h) => Task.FromResult(VertexColorFitter.Fit(split, texels, w, h)),
                GenerateResidual = (decomp, split, colors, baseLinear, w, h, errorThreshold, manualResolution, mode, coverageTarget) =>
                    Task.FromResult(decomp.GenerateResidual(
                        split, colors, baseLinear, w, h, errorThreshold, manualResolution,
                        projectedOut: null, mode: mode, coverageTarget: coverageTarget)),
                Generate = (generator, computeResult, insp, settings, destinationFolder, decomp) =>
                    Task.FromResult(generator.Generate(computeResult, insp, settings, destinationFolder, decomp)),
            };
        }

        /// <summary>
        /// The awaitable seam set injected by <see cref="ProcessAsync"/>: the
        /// projection fit/residual, the legacy-branch readback/fit, and the
        /// generation readbacks all release the editor loop, with cancellation and
        /// progress threaded through to <see cref="NamerComputePipeline.ProcessAsync"/>.
        /// </summary>
        private static ProcessSeams CreateAsyncSeams(Func<bool> shouldCancel, Action<float, string> reportProgress)
        {
            return new ProcessSeams
            {
                CreateProjection = CreateProjectionContextAsync,
                RunCompute = (compute, insp, projection, stylization, progress) =>
                    compute.ProcessAsync(insp, projection, stylization, shouldCancel, progress),
                ReadBase = ReadBackBaseAsync,
                Fit = FitOnWorkerAsync,
                GenerateResidual = (decomp, split, colors, baseLinear, w, h, errorThreshold, manualResolution, mode, coverageTarget) =>
                    decomp.GenerateResidualAsync(
                        split, colors, baseLinear, w, h, errorThreshold, manualResolution,
                        projectedOut: null, mode: mode, coverageTarget: coverageTarget),
                Generate = (generator, computeResult, insp, settings, destinationFolder, decomp) =>
                    generator.GenerateAsync(computeResult, insp, settings, destinationFolder, decomp),
            };
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
                        if (generatedMesh.bounds.size.sqrMagnitude > 0f)
                        {
                            generatedMeshBySourceMeshId[sourceMesh.GetInstanceID()] = generatedMesh;
                        }
                        else
                        {
                            result.Warnings.Add("Generated mesh for material '" + (source != null ? source.name : "(null)") + "' has degenerate bounds and was not bound to the scene instance — the source mesh stays.");
                        }
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

        /// <summary>Blocking error message for a failed decomposition base readback.</summary>
        private const string kDecomposeReadbackFailedMessage = "GPU readback failed while decomposing; no files were written.";

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

            return FinishReadBackBase(request);
        }

        /// <summary>
        /// Awaitable mirror of <see cref="ReadBackBase"/> (05.3 threading): the same
        /// RGBA32 request released to the editor loop via
        /// <see cref="NamerComputePipeline.RequestReadbackAsync"/> instead of blocking
        /// in <see cref="AsyncGPUReadbackRequest.WaitForCompletion"/>. Unlike the sync
        /// twin (which consumes the request's buffer view in the same tick), this
        /// returns a Persistent copy: the view is main-thread/frame scoped, and the
        /// fit worker (<see cref="FitOnWorkerAsync"/>) schedules jobs with it after
        /// this continuation resumes on a later tick — racing the main thread's frame
        /// flush, which invalidated the view mid-fit ("ErrorJob.BaseTexels has been
        /// deallocated"). The copy is byte-identical, so fit results are unchanged.
        /// </summary>
        private static async Task<NativeArray<Color32>> ReadBackBaseAsync(RenderTexture source)
        {
            AsyncGPUReadbackRequest request = await NamerComputePipeline.RequestReadbackAsync(source, 0, TextureFormat.RGBA32);
            NativeArray<Color32> view = FinishReadBackBase(request);

            return new NativeArray<Color32>(view, Allocator.Persistent);
        }

        /// <summary>
        /// Shared tail of <see cref="ReadBackBase"/> and <see cref="ReadBackBaseAsync"/>:
        /// error gate + RGBA32 texel view. Throws on a failed readback so no files are
        /// written on a GPU error.
        /// </summary>
        private static NativeArray<Color32> FinishReadBackBase(AsyncGPUReadbackRequest request)
        {
            if (request.hasError)
            {
                throw new InvalidOperationException(kDecomposeReadbackFailedMessage);
            }

            return request.GetData<Color32>();
        }

        /// <summary>
        /// Runs the Burst vertex-color fit on a worker thread (05.3 threading): the
        /// per-triangle least-squares is pure CPU Burst math over the seam-safe split
        /// and the read-back texels with no main-thread-only API (the fit's jobs
        /// already read these arrays from job workers synchronously). Shared by the
        /// async projection callback and the async legacy Sobel branch.
        /// </summary>
        private static Task<VertexColorFitResult> FitOnWorkerAsync(NamerSplitResult split, NativeArray<Color32> texels, int w, int h)
        {
            return Task.Run(() => VertexColorFitter.Fit(split, texels, w, h));
        }

        /// <summary>
        /// Builds the 04.2 projection context for the removed-detail dip path. The returned
        /// <see cref="NamerProjectionContext.Run"/> callback (invoked by
        /// <see cref="NamerComputePipeline.Process"/> after normalize) reads the source base
        /// back, runs the vertex-color fit, and generates the residual against the SOURCE base
        /// while writing the Gouraud projection into <c>projectedOut</c>. Phase 5 D-02:
        /// with a style profile attached, the SOURCE base reads as the STYLED source —
        /// the operator is unchanged. With
        /// <paramref name="writeResidual"/> the residual is forced kept (AlwaysKeep); otherwise
        /// forced dropped (NeverKeep) — the 04.2 checkbox, replacing the retired D-13 Gate.
        /// <paramref name="coverageTarget"/> is the percentile-gate target threaded into
        /// <c>GenerateResidual</c> (04.3 D-05; defaults to
        /// <see cref="NamerEditorConstants.DefaultCoverageTarget"/>).
        /// </summary>
        internal static NamerProjectionContext CreateProjectionContext(
            NamerSplitResult split,
            NamerDecompPipeline decomp,
            int w,
            int h,
            float errorThreshold,
            int residualResolution,
            bool writeResidual,
            float coverageTarget = NamerEditorConstants.DefaultCoverageTarget)
        {
            var context = new NamerProjectionContext
            {
                Split = split,
                ErrorThreshold = errorThreshold,
                CoverageTarget = coverageTarget,
                ManualResolution = residualResolution,
                WriteResidual = writeResidual,
            };

            context.Run = (sourceBase, projectedOut) =>
            {
                NativeArray<Color32> texels = ReadBackBase(sourceBase);
                try
                {
                    using (VertexColorFitResult fit = VertexColorFitter.Fit(split, texels, w, h))
                    {
                        context.Colors = fit.ToColor32Array();
                        context.Decomp = decomp.GenerateResidual(
                            split, context.Colors, sourceBase, w, h,
                            context.ErrorThreshold, context.ManualResolution, projectedOut,
                            context.WriteResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep,
                            context.CoverageTarget);
                    }
                }
                finally
                {
                    texels.Dispose();
                }
            };

            return context;
        }

        /// <summary>
        /// Awaitable mirror of <see cref="CreateProjectionContext"/> (05.3 threading),
        /// injected by <see cref="ProcessAsync"/>: builds the SAME context (identical
        /// fields, mode mapping, and residual semantics) but wires only
        /// <see cref="NamerProjectionContext.RunAsync"/> — the readback released to
        /// the editor loop (<see cref="ReadBackBaseAsync"/>), the fit on a Burst
        /// worker thread (<see cref="FitOnWorkerAsync"/>), and the awaitable residual
        /// (<see cref="NamerDecompPipeline.GenerateResidualAsync"/>). Continuations
        /// resume on the main thread; results are identical to the sync callback's.
        /// </summary>
        internal static NamerProjectionContext CreateProjectionContextAsync(
            NamerSplitResult split,
            NamerDecompPipeline decomp,
            int w,
            int h,
            float errorThreshold,
            int residualResolution,
            bool writeResidual,
            float coverageTarget = NamerEditorConstants.DefaultCoverageTarget)
        {
            var context = new NamerProjectionContext
            {
                Split = split,
                ErrorThreshold = errorThreshold,
                CoverageTarget = coverageTarget,
                ManualResolution = residualResolution,
                WriteResidual = writeResidual,
            };

            context.RunAsync = async (sourceBase, projectedOut) =>
            {
                NativeArray<Color32> texels = await ReadBackBaseAsync(sourceBase);
                try
                {
                    using (VertexColorFitResult fit = await FitOnWorkerAsync(split, texels, w, h))
                    {
                        context.Colors = fit.ToColor32Array();
                        context.Decomp = await decomp.GenerateResidualAsync(
                            split, context.Colors, sourceBase, w, h,
                            context.ErrorThreshold, context.ManualResolution, projectedOut,
                            context.WriteResidual ? NamerResidualMode.AlwaysKeep : NamerResidualMode.NeverKeep,
                            context.CoverageTarget);
                    }
                }
                finally
                {
                    texels.Dispose();
                }
            };

            return context;
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

        // 05-03 D-07/D-08: the profile's CavityFloor slider is authored in [0, 0.5]
        // (mirrors the NamerStyleProfile slider cap); the plumb clamps rather than
        // trusting serialized data (T-05-12).
        private const float kMaxCavityFloor = 0.5f;

        /// <summary>
        /// Resolves the attached <see cref="NamerStyleProfile"/> from the settings'
        /// persisted GUID (05-03 W2 bake side). Empty GUID, a deleted/unresolvable asset,
        /// or a GUID that no longer points at a profile all return null — the silent
        /// detach (T-05-11) — so Process degrades to the byte-identical 04.3 baseline
        /// instead of failing.
        /// </summary>
        internal static NamerStyleProfile ResolveStyleProfile(NamerProcessorSettings settings)
        {
            string guid = settings.StyleProfileGuid;
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<NamerStyleProfile>(path);
        }

        /// <summary>
        /// Resolves the selected <see cref="NamerPostLook"/> from the settings'
        /// persisted GUID (05.2 plan 04 W9 — mirrors <see cref="ResolveStyleProfile"/>
        /// field-for-field). Empty GUID, a deleted/unresolvable asset, or a GUID that
        /// no longer points at a post look all return null — the silent detach
        /// (T-05.2-04-01) — so the Stylized + Post lens degrades to plain Stylized
        /// NAMER with the locked fallback reason instead of throwing.
        /// </summary>
        internal static NamerPostLook ResolvePostLook(NamerProcessorSettings settings)
        {
            string guid = settings.PostLookGuid;
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<NamerPostLook>(path);
        }

        /// <summary>
        /// Resolves the attached <see cref="NamerStyleAsset"/> from the settings'
        /// persisted DEDICATED StyleAssetGuid pref (05.3 STYL-15, A9 — mirrors
        /// <see cref="ResolvePostLook"/> field-for-field). Empty GUID, a
        /// deleted/unresolvable asset, or a GUID that no longer points at a style
        /// asset all return null — the silent detach — so callers degrade to the
        /// unstyled baseline instead of failing. The legacy
        /// <see cref="NamerProcessorSettings.StyleProfileGuid"/> pref is NEVER read
        /// here (A9: a stale legacy profile GUID must not silently resolve to a
        /// split-shape profile as the merged asset). Process's stylization source
        /// rewires to this seam in plan 05 — this plan lands the seam only.
        /// </summary>
        internal static NamerStyleAsset ResolveStyleAsset(NamerProcessorSettings settings)
        {
            string guid = settings.StyleAssetGuid;
            if (string.IsNullOrEmpty(guid))
            {
                return null;
            }

            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            return AssetDatabase.LoadAssetAtPath<NamerStyleAsset>(path);
        }

        /// <summary>
        /// True when the attached profile requests island-gated smoothing and therefore
        /// needs the source mesh split (05-03): profile != null &amp;&amp; Smoothing &gt; 0.
        /// A null profile (or palette-only) never primes a mesh — the identity path adds
        /// zero mesh resolutions.
        /// </summary>
        internal static bool StylizationNeedsMesh(NamerStyleProfile profile)
        {
            return profile != null && profile.Smoothing > 0f;
        }

        /// <summary>
        /// Merged-asset mirror of the profile overload (05.3 STYL-16): the attached
        /// <see cref="NamerStyleAsset"/> requests island-gated smoothing and therefore
        /// needs the source mesh split. A null asset (or palette-only) never primes a
        /// mesh — the identity path adds zero mesh resolutions.
        /// </summary>
        internal static bool StylizationNeedsMesh(NamerStyleAsset asset)
        {
            return asset != null && asset.Smoothing > 0f;
        }

        /// <summary>NaN-guarded clamp: a NaN serialized slider reads as identity 0
        /// instead of poisoning the kernels (05-03 T-05-12 plumb-side guard).</summary>
        private static float SanitizedClamp(float value, float min, float max)
        {
            return float.IsNaN(value) ? 0f : Mathf.Clamp(value, min, max);
        }

        /// <summary>NaN-guarded <see cref="Mathf.Clamp01"/> (identity 0 on NaN).</summary>
        private static float SanitizedClamp01(float value)
        {
            return float.IsNaN(value) ? 0f : Mathf.Clamp01(value);
        }

        /// <summary>
        /// Plumbs the attached profile's bake-side controls (CavityFloor /
        /// NormalDetailReduction / RoughnessSimplify, 05-03 D-07/D-08) onto an
        /// inspection — clamped and NaN-guarded (ClampToCaps sanitizes anchors, not
        /// sliders, T-05-12). A null profile writes identity 0 on all three: the
        /// byte-identical 04.3 baseline. Shared by the bake path and the preview
        /// window so the saved output equals the stylized preview.
        /// </summary>
        internal static void ApplyStyleControls(NamerMaterialInspection inspection, NamerStyleProfile profile)
        {
            inspection.CavityFloor = profile != null ? SanitizedClamp(profile.CavityFloor, 0f, kMaxCavityFloor) : 0f;
            inspection.NormalDetailReduction = profile != null ? SanitizedClamp01(profile.NormalDetailReduction) : 0f;
            inspection.RoughnessSimplify = profile != null ? SanitizedClamp01(profile.RoughnessSimplify) : 0f;
        }

        /// <summary>
        /// Merged-asset mirror of the profile overload (05.3 STYL-16): plumbs the
        /// attached <see cref="NamerStyleAsset"/>'s bake-side controls onto an
        /// inspection — same clamps, same NaN guards, same null-identity (a null
        /// asset writes identity 0 on all three). Shared by the bake path and the
        /// preview window so the saved output equals the stylized preview.
        /// </summary>
        internal static void ApplyStyleControls(NamerMaterialInspection inspection, NamerStyleAsset asset)
        {
            inspection.CavityFloor = asset != null ? SanitizedClamp(asset.CavityFloor, 0f, kMaxCavityFloor) : 0f;
            inspection.NormalDetailReduction = asset != null ? SanitizedClamp01(asset.NormalDetailReduction) : 0f;
            inspection.RoughnessSimplify = asset != null ? SanitizedClamp01(asset.RoughnessSimplify) : 0f;
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
        /// The CR-01 predicate shared by <see cref="Process"/> and the processor window's
        /// preview: the reason vertex-color decomposition cannot run for this selection
        /// (one vertex-color stream per source mesh cannot carry N materials' fits), or
        /// null when decomposition may run. A null <paramref name="resolvedSourceMesh"/>
        /// (nothing resolvable) is NOT a skip reason here — Process reports the missing
        /// mesh separately and the window's preview null-gates on it — so the return
        /// only covers the multi-material/multi-mesh guard.
        /// </summary>
        internal static string DecompositionSkipReason(
            UnityEngine.Object selection, NamerSourceModel model, Mesh resolvedSourceMesh, int distinctSourceMeshes = -1)
        {
            if (resolvedSourceMesh == null)
            {
                return null;
            }

            if (distinctSourceMeshes < 0)
            {
                distinctSourceMeshes = CountDistinctSourceMeshes(selection);
            }

            if (model.Materials.Count > 1 || distinctSourceMeshes > 1)
            {
                return "the selection maps " + model.Materials.Count + " material(s) to "
                    + distinctSourceMeshes + " source mesh(es)";
            }

            return null;
        }

        /// <summary>
        /// Counts the DISTINCT source meshes a selection involves, mirroring
        /// <see cref="ResolveSourceMesh"/>'s per-case resolution — scene renderers, then
        /// prefab sub-assets + prefab contents renderers, then model/FBX sub-assets — but
        /// collecting a <see cref="HashSet{T}"/> of mesh instance IDs instead of stopping
        /// at the first mesh (CR-01 guard input). In the Regular/Variant prefab case the
        /// resolver returns the <see cref="FindMeshSubAsset"/> mesh FIRST without ever
        /// looking at renderers, so the counter collects the mesh sub-assets AND the
        /// contents renderers' meshes (WR-03): when those differ, decomposition would run
        /// on the sub-asset while <see cref="BindGeneratedMaterials"/> keys the swap on
        /// what the renderers actually wear — the CR-01 silent-wrong-render failure mode —
        /// so the mismatch itself must trip the guard.
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
                    // WR-03: mirror the resolver's priority — FindMeshSubAsset is checked
                    // FIRST — and count the sub-asset meshes alongside the contents
                    // renderers' meshes. For a normal prefab these are the same set (or the
                    // sub-asset walk is empty), so the count is unchanged; only a prefab
                    // whose sub-asset differs from its renderers' meshes counts 2.
                    foreach (UnityEngine.Object subAsset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                    {
                        if (subAsset is Mesh mesh)
                        {
                            distinctIds.Add(mesh.GetInstanceID());
                        }
                    }

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

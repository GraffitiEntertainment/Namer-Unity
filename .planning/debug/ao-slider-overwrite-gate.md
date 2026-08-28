---
status: diagnosed
trigger: "UAT Phase 3, Test 1, gap 1: dragging AO un-multiply strength slider in NAMER editor window errors with 'process is blocked refusing to overwrite non-generated asset' instead of silently recomputing preview in memory"
created: 2026-08-27T00:00:00Z
updated: 2026-08-27T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED - The AO slider path never reaches the overwrite gate (D-10 holds in code). The gate error the user saw during slider work was produced by a "Process with NAMER" invocation (window button or context menu) that the user performed because the in-memory slider recompute has no visible affordance; that Process run was refused by AssetGenerator.EnsureWritableTarget on files left by the session's earlier successful Process run, with OverwriteGenerated at its default false (gate refuses even stamped assets and the message mislabels them "non-generated"), and/or the gap-5 stamp-detection defect.
test: Full static call-graph trace of every code path reachable from the slider callback, plus reverse trace of every producer of the exact error string
expecting: If any slider-reachable code contained a disk write or AssetGenerator call, the original gap interpretation ("slider recompute hits the gate") would be confirmed; otherwise the error must originate from a Process invocation
next_action: Root cause found - return diagnosis (goal: find_root_cause_only). No fix to apply.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: Dragging the AO un-multiply slider recomputes the preview in ~300 ms in memory, with no asset written to disk and no error.
actual: "If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset."
errors: Editor error stating the processor refuses to overwrite a non-generated asset (exact message paraphrased by user).
reproduction: Test 1 in UAT - open Tools > NAMER > Processor, select a textured FBX (user used "Neo-T-Pose"), drag AO Un-multiply Strength slider.
started: Discovered during UAT after Phase 3 completion.

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: RecomputePreview (the debounced slider recompute) calls the disk-write path (NamerProcessor.Process or AssetGenerator)
  evidence: NamerEditorWindow.cs:198-249 - RecomputePreview calls only NamerComputePipeline.Process + Material.SetTexture/SetColor/SetFloat on in-memory materials; grep confirms zero references to NamerProcessor.Process or AssetGenerator anywhere in the slider/debounce/recompute path
  timestamp: 2026-08-27

- hypothesis: NamerComputePipeline.Process performs hidden disk writes or AssetDatabase mutations
  evidence: Full read of NamerComputePipeline.cs:69-127 - leases pooled render targets, Graphics.Blit inputs, dispatches 3 kernels, returns RenderTextures; grep for File./AssetDatabase write APIs across NamerComputePipeline.cs, NamerPreviewRenderer.cs, NamerDebugChannelMaterial.cs, SourceInspector.cs, NamerSourceModel.cs, ComputeTexturePool.cs returned zero matches
  timestamp: 2026-08-27

- hypothesis: An automatic trigger (InitializeOnLoad, AssetPostprocessor, selection callback) runs NamerProcessor.Process when the slider moves or assets import
  evidence: grep for InitializeOnLoad/AssetPostprocessor/DidReloadScripts across the package: zero hits. Selection.selectionChanged handler only calls RebuildInspection (SourceInspector.Inspect + MarkDirty, compute-only). RunProcess has exactly one caller: the GUI button (NamerEditorWindow.cs:503); ProcessSelection's callers are the two context-menu MenuItems (:67, :73)
  timestamp: 2026-08-27

- hypothesis: A different error message (e.g. a shader/compute failure in RecomputePreview) was misread by the user as the overwrite refusal
  evidence: The user's paraphrase matches the distinctive fragments of exactly one string in the codebase: AssetGenerator.cs:431 "Refusing to overwrite non-generated asset '...'" prefixed by "Process blocked: " (NamerEditorWindow.cs:554-558, DescribeResult, only reachable from RunProcess) or "[NAMER] Process with NAMER blocked: " (LogResult, only reachable from the context menus). Gap 5's companion quote ("says only NamerGenerated-stampped assets trying to overwrite the surface.png") matches the second sentence of the same message, confirming both sightings are this one gate message
  timestamp: 2026-08-27

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-27
  checked: NamerEditorWindow.cs slider -> debounce -> recompute path (DrawProcessingSection :445-451, Tick :182-196, RecomputePreview :198-249)
  found: Slider drag sets _aoStrength + EditorPrefs-backed _settings.AoUnmultiplyStrength, MarkDirty(); after 0.3 s debounce (NamerEditorConstants.DebounceSeconds) Tick calls RecomputePreview which runs NamerComputePipeline.Process(inspection) and assigns the resulting RenderTextures directly to in-memory _namerMaterial/_debugMaterial. No readback, no disk write - D-10 contract is honored in code.
  implication: The preview slider path cannot itself produce any overwrite-gate error.

- timestamp: 2026-08-27
  checked: Origin of the exact error string (grep "Refusing to overwrite" package-wide)
  found: Sole producer is AssetGenerator.EnsureWritableTarget (:419-434), called only from Generate (:71-73), WriteSurfaceTexture (:282), WriteBaseTexture (:308), WriteMaterial (:339), and PreflightTargets (:157-165) - all inside AssetGenerator, whose only caller is NamerProcessor.Process (NamerProcessor.cs:89 preflight, :100 generate)
  implication: The gate is reachable ONLY through NamerProcessor.Process.

- timestamp: 2026-08-27
  checked: All callers of NamerProcessor.Process
  found: Exactly two production callers: NamerEditorWindow.ProcessSelection (:81, Assets/ and GameObject/ "Process with NAMER" context menus, uses new NamerProcessorSettings() -> OverwriteGenerated defaults false) and NamerEditorWindow.RunProcess (:528, the window's Process button, bound to _settings). Remainder are tests (AssetGeneratorTests, SourceImmutabilityTests).
  implication: The user's gate error came from a Process button click or a context-menu invocation - a deliberate user action, not the slider.

- timestamp: 2026-08-27
  checked: Gate refusal conditions (AssetGenerator.EnsureWritableTarget :419-434)
  found: Throws when `!stamped || !overwriteGenerated`. With the EditorPrefs default OverwriteGenerated=false (NamerProcessorSettings.cs:44), the gate refuses even correctly-stamped generated assets, and the message always says "Refusing to overwrite non-generated asset" regardless of actual stamp state - a misleading message for the overwrite-off case.
  implication: After the session's first successful Process run (UAT test 2 first run created Assets/NAMERGenerated/Neo-T-Pose/), ANY subsequent Process attempt without Overwrite enabled is blocked with the exact message the user quoted - no stamp bug required. With Overwrite enabled, gap 5's stamp-detection defect blocks it too; either way the user's Process attempt during slider work was refused.

- timestamp: 2026-08-27
  checked: Why the user invoked Process during slider work at all (UAT report, same test 1)
  found: User's companion complaints: "I don't see a AO recompute button?" and confusion that moving the slider produced no visible response. The window offers no recompute indicator, no status change on slider recompute (status is cleared silently on success, RecomputePreview :235), and no hint that the slider is preview-only. The only visible commit action is the "Process with NAMER" button (D-13 shared entry point), which is the disk-write path.
  implication: Workflow misattribution: the user pressed Process to apply the AO change because the in-memory recompute is invisible; the resulting gate refusal was then reported as "moving the slider gives an error".

- timestamp: 2026-08-27
  checked: Status persistence path (could a stale Process-blocked status masquerade as a slider error?)
  found: _status is cleared on selection change (RebuildInspection :176) and on successful RecomputePreview (:235). If RecomputePreview fails it shows "Processing failed: ..." (not "Process blocked: ..."). A "Process blocked" status can therefore coexist with slider dragging only until the next successful recompute (~300 ms after drag stops) or if recompute keeps failing/early-returns.
  implication: Even the transient-visibility variant requires a prior Process-button click as the error's origin; the slider is never the producer.

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: The AO-slider preview path never touches the overwrite gate - the D-10 no-disk-write contract holds in code (slider -> Tick debounce -> RecomputePreview -> NamerComputePipeline.Process only; verified zero AssetDatabase/File write calls in every file on that path). The gate error the user experienced "when moving the AO slider" was actually produced by a separate "Process with NAMER" invocation (window button or context menu - the only two production callers of NamerProcessor.Process, the only route to AssetGenerator.EnsureWritableTarget, the sole producer of that message). The user invoked Process during slider work because the debounced in-memory recompute has no visible affordance or feedback in the window (their own companion report: "I don't see a AO recompute button?"), making the disk-writing Process button the only apparent way to apply the AO change. That Process run was then refused because the session's earlier successful Process run (UAT test 2) had already created Assets/NAMERGenerated/Neo-T-Pose/…, and EnsureWritableTarget throws whenever `!stamped || !overwriteGenerated` - with the default OverwriteGenerated=false it refuses even correctly-stamped assets while misleadingly calling them "non-generated" (and with Overwrite=true, the independently-broken stamp detection from gap 5 also refuses). So the defect is (a) a preview-recompute discoverability/feedback gap in the window that steers users to the gated disk path, plus (b) a misleading gate message, not a preview path that writes to disk.

fix: (for plan-phase --gaps; not applied - find_root_cause_only) 1) Surface the slider recompute in the UI: a visible "recomputing/updated" indicator or explicit "Recompute Preview" button next to the AO slider, and label the section as in-memory preview-only (nothing written to disk). 2) Split the gate message by refusal reason: "asset exists and Overwrite generated is off" vs "asset is not NamerGenerated-stamped" so the message stops mislabeling stamped assets as non-generated. 3) Coordinate with gap 5's fix for stamp detection so Overwrite-enabled re-runs of stamped assets succeed.

verification: Static call-graph analysis: every file on the slider path read in full (NamerEditorWindow.cs, NamerComputePipeline.cs) and grep-verified for disk/AssetDatabase write APIs (zero hits in NamerPreviewRenderer.cs, NamerDebugChannelMaterial.cs, SourceInspector.cs, NamerSourceModel.cs, ComputeTexturePool.cs); reverse call-graph from the error string to its only producers and their only two production callers; gate logic read directly (:419-434); message text matched fragment-for-fragment to the user's and gap 5's paraphrases. Human verification in Unity (drag slider, watch Console/status) still available to the user.

files_changed: []

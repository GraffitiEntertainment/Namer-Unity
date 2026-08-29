---
status: resolved
trigger: "Running Process with NAMER a second time on a scene object re-inspects the generated NAMER material as the source, producing garbage output (_Namer_Namer 256x256 fallback textures) and binding it; scene renders untextured while the NAMER window preview looks correct; moving the AO un-multiply slider turns AO pure white. User-directed desired behavior: idempotent regeneration - re-processing must resolve back to the ORIGINAL source material and regenerate the same output paths in place, overwriting via the existing OverwriteGenerated gate."
created: 2026-08-29T00:00:00Z
updated: 2026-08-29T16:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: RESOLVED - source recovery implemented, verified, and shipped. A re-process now resolves the worn generated NAMER material back to its ORIGINAL source via a NamerSource override tag, regenerates the same output paths in place, and swaps the renderer slot to the fresh material. Full EditMode suite is green (82/0).
next_action: None - fix is complete. Remaining user action: save the (already-dirty) NamerSmoke.unity scene so the SkinnedMeshRenderer keeps the repaired material binding.

## Resolution
<!-- APPEND only - how the confirmed root cause was fixed -->

- timestamp: 2026-08-29
  code: AssetGenerator.WriteMaterial now stamps `material.SetOverrideTag("NamerSource", "<guid>|<localFileId>")` on the generated .mat before CreateAsset. SetOverrideTag is serialized into the .mat `stringTagMap` by CreateAsset itself, so no SaveAssets call is needed (a SaveAssets call was tried first but re-serialized other dirty source assets, breaking SourceImmutabilityTests — removed).
- timestamp: 2026-08-29
  code: SourceInspector.Inspect remaps any collected material carrying the NamerGenerated label OR a NamerSource tag back to its recorded original (GUIDToAssetPath + LoadAllAssetsAtPath matching the localFileId). Unresolvable/untagged legacy generated materials are skipped with a warning; remap chains (resolved source is itself generated) are treated as unresolvable. Added `ResolveOriginalFromSourceTag` (public, shared with NamerProcessor).
- timestamp: 2026-08-29
  code: NamerProcessor now snapshots each scene renderer's per-slot source instance ID BEFORE generation (CaptureSlotSources) and binds by that snapshot. This was required because overwriting the generated material in place via CreateAsset preserves the GUID but invalidates the LIVE renderer reference (the slot reads null after overwrite — verified by probe), so post-generation slots cannot be resolved from their current value.
- timestamp: 2026-08-29
  code: NamerEditorConstants gained `SourceTag = "NamerSource"`.
- timestamp: 2026-08-29
  test: Added NamerReprocessTests (remap for .mat and FBX-like sub-asset sources, double-process idempotency with no `_Namer_Namer` stacking, and old-generated-instance bind swap). Full EditMode suite: 82 passed / 0 failed.
- timestamp: 2026-08-29
  scene: Live repair completed - bound Assets/NAMERGenerated/Neo-T-Pose/tripo_mat_d83278e6_Namer.mat onto SkinnedMeshRenderer 'tripo_node_d83278e6' slot 0 (scene marked dirty for the user to save); deleted the six tripo_mat_d83278e6_Namer_Namer.{mat,_Base.png,_Surface.png} files (+ .meta) ; backfilled NamerSource=d0691ba7e72264bfe9112baabba16338|2100000 on the legacy generated material.
- timestamp: 2026-08-29
  deviation: The approved design assumed the source was an FBX sub-asset material, but Assets/Models/Neo/Neo-T-Pose.fbx no longer embeds any Material sub-assets (verified via LoadAllAssetsAtPath) — the material was extracted on import to the standalone Assets/Models/Neo/tripo_mat_d83278e6.mat (guid d0691ba7e72264bfe9112baabba16338, localId 2100000). The backfill resolves that standalone material instead, which is what the source renderer and the extracted FBX material reference.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: Running Process with NAMER again on an already-processed object regenerates the same generated files in place from the original source material (overwrite honored by the OverwriteGenerated toggle, default on), the scene keeps rendering textured, and AO tweaks keep affecting real AO.
actual: Second run created tripo_mat_d83278e6_Namer_Namer.{mat,_Base.png,_Surface.png} (256x256 fallback content) and bound the junk material to the character; scene view renders untextured while the NAMER window preview looks correct; dragging the AO un-multiply strength slider makes AO go completely white (1.0). User separately re-applied a material manually but it landed on the root MeshRenderer, not the SkinnedMeshRenderer that carries the visible geometry.
errors: None in console (verified: errorCount 0; 23 benign warnings from earlier test runs). No shader compile errors in Editor.log. The NAMER shader resolves and is supported on the live material.
reproduction: Select the scene object Neo-T-Pose (child SkinnedMeshRenderer tripo_node_d83278e6 wearing a generated NAMER material), click Process with NAMER in the NAMER processor window.
started: Became reachable with commit c4445d5 (BindGeneratedMaterials) on 2026-08-29; observed by user immediately after processing twice.

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: The NAMER shader fails to compile, explaining the untextured scene
  evidence: Editor.log contains no shader compile errors; live editor query shows shader=GraffitiEntertainment.Namer/NAMER resolving with isSupported true on the worn material; console errorCount 0
  timestamp: 2026-08-29

- hypothesis: GUID churn from delete+recreate on overwrite broke texture references ("missing material")
  evidence: Every generated .mat's texture GUID references match the .meta GUIDs on disk (run-1 mat refs 7974d991.../fbbcb644... == PNG meta guids; run-2 mat refs f58b9087.../7657da28... == PNG meta guids); run-1 PNG data was overwritten in place with Aug-28 metas preserved; no null material slots exist in the live scene
  timestamp: 2026-08-29

- hypothesis: The window preview and scene differ because the generated material is broken
  evidence: The window preview renders with its own correct material assignment; the scene is untextured only because the SkinnedMeshRenderer wears the junk _Namer_Namer material while the user's manual re-apply landed on the root MeshRenderer 'Neo-T-Pose'
  timestamp: 2026-08-29

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-29
  checked: Assets/NAMERGenerated/Neo-T-Pose/ file listing with mtimes and sizes
  found: Run 1 (14:39): tripo_mat_d83278e6_Namer.mat + _Namer_Base.png 5.4 MB + _Namer_Surface.png 3.9 MB (full-res, good). Run 2 (14:40): tripo_mat_d83278e6_Namer_Namer.mat + _Namer_Namer_Base.png 1670 bytes + _Namer_Namer_Surface.png 1673 bytes (256x256 fallback content). Scene NamerSmoke.unity saved 14:37:48 with the skinned mesh on the FBX sub-asset material (fileID -4025976620153653997, guid 8291cccf... = Neo-T-Pose.fbx).
  implication: Both Process runs happened after the scene save; run 1 was correct, run 2 generated junk from a wrong source.

- timestamp: 2026-08-29
  checked: Live editor renderer/material state (Unity RunCommand, twice)
  found: SkinnedMeshRenderer 'tripo_node_d83278e6' wears 'tripo_mat_d83278e6_Namer_Namer' (NAMER shader, 256x256 surface/base textures); root MeshRenderer 'Neo-T-Pose' wears URP/Lit 'tripo_mat_d83278e6' with neo-character_glb_basecolor (user's manual re-apply hit the wrong node - no visible geometry on it).
  implication: The visible character geometry is the SkinnedMeshRenderer; the untextured look is the junk 256x256 material, not a shader failure.

- timestamp: 2026-08-29
  checked: Material YAML + meta GUID cross-references for both generated sets
  found: Both mats reference the NAMER shader guid 2d0387b5... and their PNG guids correctly; all resolve on disk.
  implication: No broken asset references; the damage is purely the junk content and the rebind.

- timestamp: 2026-08-29
  checked: SourceInspector.cs collection + ReadGeneric paths; NamerProcessor.cs BindGeneratedMaterials; NamerEditorWindow.cs:177 inspection and AO slider MarkTweaking/RecomputePreview path
  found: Scene-GameObject collection walks renderer.sharedMaterials with no NamerGenerated filter (AddSharedMaterials :146-152); ReadGeneric (:305) does _BaseMap ?? _MainTex -> both null on a NAMER material -> bare-material neutral defaults warning path; BindGeneratedMaterials (NamerProcessor.cs:147-189) maps only source->generated by instance ID; the window inspects once per selection change (:177) so the worn NAMER material poisons the live AO recompute.
  implication: Single insertion points exist for source recovery (Inspect loop), tag stamping (WriteMaterial), and dual-map binding (BindGeneratedMaterials).

- timestamp: 2026-08-29
  checked: Unity console (Error/Warning) and Editor.log shader errors
  found: errorCount 0; warnings are benign test-run RenderTexture release notices plus a Unity AI toolkit network warning; Editor.log has no shader compile failures.
  implication: "Shader not compiling" symptom was actually the junk-textured render; no shader work needed.

- timestamp: 2026-08-29
  checked: SetOverrideTag persistence through CreateAsset (live-editor probe writing Assets/TagProbe.mat)
  found: SetOverrideTag BEFORE AssetDatabase.CreateAsset serializes the tag into the .mat YAML `stringTagMap` with no SaveAssets call; a .mat main object resolves TryGetGUIDAndLocalFileIdentifier as localId 2100000 (not 0).
  implication: The tag stamping does not need (and must not add) an AssetDatabase.SaveAssets call, which was re-serializing the source material and breaking SourceImmutabilityTests.

- timestamp: 2026-08-29
  checked: Overwrite-in-place behavior of AssetDatabase.CreateAsset at an existing .mat path (live-editor probe)
  found: CreateAsset preserves the .meta GUID (guidChanged=False) but the LIVE renderer reference to the old material instance reads null after the overwrite — the slot must be rebound by a pre-generation snapshot, not by its post-generation value.
  implication: BindGeneratedMaterials cannot resolve null slots; CaptureSlotSources must run before generation.

- timestamp: 2026-08-29
  checked: FBX material sub-asset presence (AssetDatabase.LoadAllAssetsAtPath on Assets/Models/Neo/Neo-T-Pose.fbx)
  found: The FBX exposes only GameObject/Transform (mixamorig rig), Animator, and Avatar — no Material sub-assets. The material was extracted on import to the standalone Assets/Models/Neo/tripo_mat_d83278e6.mat (guid d0691ba7e72264bfe9112baabba16338, localId 2100000), referenced from the FBX .meta externalObjects.
  implication: The approved design's "FBX sub-asset source" assumption was stale; the NamerSource backfill resolves the standalone extracted material instead.

- timestamp: 2026-08-29
  checked: Full EditMode suite (GraffitiEntertainment.Namer.Tests.Editor) after the fix
  found: 82 passed / 0 failed / 0 skipped (first run 79/3; the three failures were the remap Assert.AreSame reference-equality test, the double-process null-slot bind, and SourceImmutability's source-material byte change — all fixed).
  implication: Fix verified end-to-end; no regressions across SourceInspector/AssetGenerator/NamerProcessor tests.

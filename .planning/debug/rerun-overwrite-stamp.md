---
status: diagnosed
trigger: "UAT Phase 3, Test 2, gap 5: re-run with Overwrite generated enabled refused - 're-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png'"
created: 2026-08-27T00:00:00Z
updated: 2026-08-28T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED - Not a stamp-detection failure. On-disk evidence proves run 1's Stamp() persisted (all three .meta files under Assets/NAMERGenerated/Neo-T-Pose/ carry `labels: [NamerGenerated]`), so at re-run time `stamped == true`. The only remaining arm of `EnsureWritableTarget`'s `!stamped || !overwriteGenerated` is `!overwriteGenerated`: the re-run executed with the Overwrite toggle off, and the gate's single conflated message then claimed the stamped asset was "non-generated" - a false statement that never mentions the toggle.
test: Read live .meta files from the user's actual UAT run (on-disk ground truth for whether Stamp persisted); read EnsureWritableTarget/Stamp, NamerProcessor.Process, NamerProcessorSettings, window toggle binding, and the overwrite test.
expecting: If the metas lacked labels, a persistence defect in Stamp/SetLabels; if they carry labels, the refusal must be the overwrite-off arm with a misleading message.
next_action: Root cause found - return diagnosis (find_root_cause_only). No fix applied.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: Re-running Process with Overwrite generated enabled replaces the assets the first run itself generated (NamerGenerated-stamped).
actual: "re-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png"
errors: Overwrite gate refusal naming surface.png as non-generated.
reproduction: Test 2 in UAT - first Process on FBX "Neo-T-Pose" succeeded (console: "[NAMER] Processed 'Neo-T-Pose': 1 material(s) generated in 'Assets/NAMERGenerated/Neo-T-Pose/'"); immediate re-run refused on the surface PNG.
started: Discovered during UAT after Phase 3 completion (first live use of the flow; 53/53 tests passed headless).

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: Run 1's Stamp() never persisted, so GetLabels on re-run found no label
  evidence: On-disk ground truth - tripo_mat_d83278e6_Namer.mat.meta, _Base.png.meta, and _Surface.png.meta under Assets/NAMERGenerated/Neo-T-Pose/ all contain `labels:\n- NamerGenerated` (inspected 2026-08-28; files timestamped 2026-08-27 18:11, the UAT session). Stamp() -> AssetDatabase.SetLabels persisted to the .meta files.
  timestamp: 2026-08-28

- hypothesis: The window's Overwrite toggle never reaches the settings instance Process reads (binding/wiring bug)
  evidence: NamerEditorWindow.cs:482-486 - toggle writes `_settings.OverwriteGenerated` (EditorPrefs-backed property, setter writes through immediately, NamerProcessorSettings.cs:42-46); RunProcess passes that same `_settings` instance to NamerProcessor.Process (:528). The context-menu path's `new NamerProcessorSettings()` reads the same EditorPrefs key. No wiring defect exists.
  timestamp: 2026-08-28

- hypothesis: A settings mutation between runs (test leakage, domain reload) cleared the toggle
  evidence: No code path resets OverwriteGenerated; the test suite snapshots/restores the EditorPrefs key (AssetGeneratorTests PrefsSnapshot) and runs headless in a clone; the user's re-run happened seconds after run 1 in the same window with no script recompile between.
  timestamp: 2026-08-28

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-28
  checked: Live .meta files from the user's UAT run (Assets/NAMERGenerated/Neo-T-Pose/)
  found: All three generated assets carry `labels: [NamerGenerated]`. The surface meta also carries the intended import settings (sRGBTexture: 0, mipMapEnabled: 0, filterMode: 0, uncompressed default platform) - run 1's write+import+stamp completed fully.
  implication: `stamped == true` at re-run time; the refusal cannot be the stamp arm.

- timestamp: 2026-08-28
  checked: EnsureWritableTarget refusal logic (AssetGenerator.cs:419-434)
  found: Single condition `if (!stamped || !overwriteGenerated)` throws ONE message: "Refusing to overwrite non-generated asset '<path>'. Only NamerGenerated-stamped assets in the destination folder can be overwritten." - identical text for both arms. The second sentence is what the user quoted.
  implication: With stamped=true and the toggle off, the gate refuses while factually asserting the asset is non-generated and never naming the real reason (Overwrite generated is disabled) or its location (Output section).

- timestamp: 2026-08-28
  checked: Why the toggle was off on the re-run
  found: EditorPrefs default is false (NamerProcessorSettings.cs:44); the toggle lives in the Output section with no hint it gates re-runs; the error message does not reference it. Whether the user toggled it before re-running is indistinguishable from the message - which is itself the defect. The pasted stack from test 1's identical error (NamerProcessor.Process <- NamerEditorWindow.RunProcess <- DrawActionSection <- OnGUI) confirms these refusals come from Process button presses.
  implication: Any user journey - toggle missed, toggled after pressing, or context-menu re-run before toggling - produces an error that misdiagnoses itself as a stamping problem.

- timestamp: 2026-08-28
  checked: Why the test suite never caught this (coverage hole)
  found: AssetGeneratorTests.OverwriteGating_RefusesNonStampedTarget (:185-247) DELETES run 1's surface.png, writes an unstamped collision PNG, asserts the refusal, then MANUALLY stamps via AssetDatabase.SetLabels (:232-234) before the success run. It never verifies (a) that run 1's own Stamp is detected on re-run, nor (b) the message for the stamped-but-overwrite-off arm.
  implication: The exact UAT scenario (re-run against the first run's untouched output) was never encoded as a test; 53/53 green was consistent with this defect.

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: The re-run refusal is a false-attribution defect in AssetGenerator.EnsureWritableTarget's error message, not a stamp-detection failure. Run 1 stamps correctly (on-disk .meta files prove `labels: [NamerGenerated]` persisted), so on re-run `stamped == true` and the only live refusal arm is `!overwriteGenerated` - the re-run executed with the Overwrite toggle off (EditorPrefs default false; the error message never mentions the toggle, so a user who missed or mistimed the toggle cannot self-diagnose). The gate's single conflated message then asserts the stamped asset is "non-generated" and quotes the stamp policy - a factually wrong explanation for this arm. The user (correctly seeing their assets were stamped) reported it as "stamp not detected". A test coverage hole let this ship: the existing overwrite test manually re-stamps the target, so "first run's own stamp round-trips into a successful overwrite re-run" was never asserted.

fix: (for plan-phase --gaps; not applied - find_root_cause_only) 1) Split EnsureWritableTarget's refusal by actual reason: keep the current message only for genuinely unstamped targets; for stamped targets with the toggle off, throw "'<path>' already exists. Enable the Output > Overwrite generated toggle to replace NamerGenerated assets." 2) Extend AssetGeneratorTests: after a fresh successful run, re-run with overwrite=false must fail with the toggle message (also proving stamped==true detection on run 1's own output), and re-run with overwrite=true against the UNTOUCHED output must succeed with no manual SetLabels - encoding the exact UAT scenario.

verification: On-disk .meta inspection (ground truth for stamp persistence); full reads of AssetGenerator gate/stamp code, NamerProcessor.Process, NamerProcessorSettings (EditorPrefs round-trip), NamerEditorWindow toggle binding and RunProcess; line-level read of the existing overwrite test identifying the manual-stamp bypass. Live-editor re-run after the fix is the final UAT confirmation.

files_changed: []

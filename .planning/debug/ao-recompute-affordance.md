---
status: diagnosed
trigger: "ao-recompute-affordance (UAT Phase 3, Test 1, gap 3): user cannot tell AO un-multiply changes recompute automatically (~300 ms debounce); looked for explicit 'AO recompute' button that doesn't exist ('I don't see a AO recompute button?')"
created: 2026-08-27T00:00:00Z
updated: 2026-08-27T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED — The automatic 300 ms debounced AO recompute is present and functional, but nothing in the UI communicates its existence or activity. The UI spec itself contracted no affordance (no tooltip, no helper copy, no "recomputing…" state text; the Recomputing state is defined as visually inert), and the implementation faithfully matches: on success the status string is cleared and only the preview pixels silently change; `_recomputing` is set and cleared within one synchronous editor-update callback so even the spec's single cue (Process button disabled) can never render.
test: Full read of NamerEditorWindow.cs (649 lines) + 03-UI-SPEC.md + 03-CONTEXT.md (D-10/D-14) + 03-VERIFICATION.md; grep for tooltip/miniLabel/recomputing affordances and for the origin of the user's overwrite error.
expecting: If the "invisible automatic path" hypothesis is right, the slider row has no GUIContent/tooltip/hint, `_dirty`/`_recomputing` have no OnGUI representation on the success path, and "Refusing to overwrite" cannot originate from RecomputePreview. All confirmed.
next_action: None — root cause established; return diagnosis (goal: find_root_cause_only).

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: The AO recompute path is discoverable in the window UI (automatic ~300 ms debounced recompute is apparent, or an explicit affordance exists).
actual: "I don't see a AO recompute button?"
errors: None reported.
reproduction: Test 1 in UAT — open Tools > NAMER > Processor, look for how to apply an AO change.
started: Discovered during UAT after Phase 3 completion (design-level gap, not a regression).

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: The debounce is not wired — nothing actually happens when the slider changes (dead control).
  evidence: Tick() (NamerEditorWindow.cs:182-196) is subscribed to EditorApplication.update (:113) and gates on NamerEditorConstants.DebounceSeconds = 0.3f (NamerEditorConstants.cs:25); RecomputePreview (:198-249) re-runs NamerComputePipeline.Process, reassigns textures, and calls Repaint(). 03-VERIFICATION.md item 16 independently verified this wiring. The mechanism works.
  timestamp: 2026-08-27

- hypothesis: A recompute button was specified in the UI contract but never implemented.
  evidence: 03-UI-SPEC.md Component #6 contracts a bare EditorGUILayout.Slider (+"value readout", i.e. the slider's own numeric field); no AO-apply button exists anywhere in the spec or code. By design: D-10 ("control changes trigger a debounced re-dispatch of the compute pipeline") — the no-button design is intentional and correct.
  timestamp: 2026-08-27

- hypothesis: The slider's own debounce path throws the overwrite error, making it look dead.
  evidence: "Refusing to overwrite" is produced only by AssetGenerator.cs:431 (disk-write path). RecomputePreview calls only NamerComputePipeline (no AssetGenerator/NamerProcessor — confirmed by reading the method and 03-VERIFICATION item 9). The error the user attributed to slider movement could only come from the "Process with NAMER" button — the natural "apply" action for someone who saw no slider feedback (that defect is gap 1/gap 4's session, not this one).
  timestamp: 2026-08-27

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-27
  checked: 03-UI-SPEC.md Copywriting Contract + State Contract + Component #6
  found: No copy exists for any AO-recompute affordance — no tooltip text, no helper line, no "recomputing…" status. The Recomputing state row's contracted visible behavior is "preview shows previous frame until readback completes" (i.e., deliberately nothing) plus "Process disabled". The only planned signal that a recompute happens was the preview's pixels changing (03-VERIFICATION.md human item 1: "confirm the preview re-renders after ~300 ms").
  implication: The discoverability gap is a spec-level omission first — the automatic path was contracted to be silent.

- timestamp: 2026-08-27
  checked: NamerEditorWindow.cs DrawProcessingSection (:440-456) and whole-file grep for GUIContent/tooltip/miniLabel
  found: The AO control is `EditorGUILayout.Slider("AO Un-multiply Strength", ...)` with a plain string label — no GUIContent, no tooltip, no helper miniLabel anywhere in the file (only GUIContent.none in GUI.Box calls). The "Processing" section contains exactly a bold header and the slider.
  implication: The implementation gives the user zero textual/visual indication that slider changes auto-apply after a debounce.

- timestamp: 2026-08-27
  checked: Debounce-wait UI representation (_dirty/_lastChange consumers, OnGUI paths)
  found: `_dirty`/`_lastChange` are consumed only by Tick() (:182-196); no OnGUI code reads them. During the 300 ms wait the window renders identically to idle — slider stays enabled, no status, no spinner.
  implication: The debounce interval is completely invisible.

- timestamp: 2026-08-27
  checked: RecomputePreview success path (:235-236) and _recomputing lifecycle (:51, :206, :245, :497)
  found: On success `_status` is set to string.Empty — the code actively CLEARS all communication — and the only observable effect is the preview texture silently repainting. `_recomputing` is set true and false inside the single synchronous RecomputePreview callback, so OnGUI can never observe it true; its sole consumer is `canProcess` (:497), meaning even the spec's one cue ("Process disabled while Recomputing") is structurally unreachable.
  implication: There is no transient "recomputing" indication possible in the current implementation, even though a `_recomputing` field exists.

- timestamp: 2026-08-27
  checked: Origin of the error the user saw ("Refusing to overwrite") — grep across package
  found: String exists only in AssetGenerator.cs:431 (disk-write path). The slider path never calls the generator. The user's reported sequence (move slider → blocked error) is only consistent with pressing "Process with NAMER" — the sole button — to apply the AO change, which then hit the overwrite gate (gap 1/gap 4 defects).
  implication: The affordance gap actively redirects users to the destructive-looking path: seeing no feedback from the slider, they press the only button, get blocked, and conclude a dedicated recompute control is missing.

- timestamp: 2026-08-27
  checked: 03-CONTEXT.md D-14 text
  found: D-14 explicitly states "UX polish (spacing, states, visual detail) matters — the user treats UI quality as equal to functional correctness."
  implication: The missing recompute state/affordance falls short of the phase's own stated UX bar; the spec's silent-Recomputing state contradicts D-14's emphasis on states.

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: The AO recompute path is automatic and functional (debounce pump verified: Tick at NamerEditorWindow.cs:182-196, 0.3 s constant at NamerEditorConstants.cs:25), but its automaticness is communicated nowhere — a spec-level omission faithfully implemented. 03-UI-SPEC.md contracted a bare slider with no tooltip/helper copy and defined the Recomputing state as visually inert ("preview shows previous frame"); the implementation matches exactly: no GUIContent/tooltip on the slider (NamerEditorWindow.cs:445), no OnGUI representation of `_dirty` during the 300 ms wait, `_status` cleared to empty on recompute success (:235), and `_recomputing` set/cleared within one synchronous callback (:206/:245) so even the spec's single cue (Process disabled, :497) can never render. The design relied entirely on the user noticing the preview pixels change ~300 ms after release; for a subtle AO delta that signal is imperceptible, and with gap 1's blocking error from the "Process with NAMER" button (the only button a user seeking to "apply" the change would press), the working automatic path is indistinguishable from a dead control — so the user looked for an "AO recompute" button that the no-button design (D-10) intentionally omits.
fix: (find_root_cause_only — not applied) Add an affordance while keeping the no-button design: e.g. a GUIContent tooltip and/or EditorStyles.miniLabel helper under the slider stating changes recompute the preview automatically (~300 ms), plus a transient "Recomputing…" status visible while dirty/recomputing (requires _recomputing to span frames or OnGUI to read _dirty). Coordinate with gap 1/4 fixes so the error the user attributes to the slider no longer appears.
verification: (not applicable in diagnose-only mode)
files_changed: []

---
phase: quick-fcx-namer-debug-dip-switches
plan: 01
subsystem: editor-ui
tags: [unity, urp, hlsl, editorprefs, shader, namer, debug-instrumentation]

# Dependency graph
requires: []
provides:
  - 5 neutral-default shader gates (_DbgEnableResidual/Roughness/AO/Metallic/Emissive) plus _DbgRoughnessNeutral in the single NAMER surface shader
  - 4 EditorPrefs-persisted step-gate bools (Roughness/AO stage, Metallic/Emissive contribution)
  - window step dip-switch row + shaded-view input toggle row + preview horizontal-scrollbar fix
affects:
  - 04.2-gouraud-projection-one-texture-with-roughness-transfer-resid (residual-smeared-reconstruction bisect instrument)

# Tech tracking
tech-stack:
  added: []
  patterns:
    - neutral-default float gates written via Shader.PropertyToID from editor UI onto preview/generated materials
    - EditorPrefs-backed opt-out bool gates mirroring NamerProcessorSettings.DecompositionEnabled

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs.meta
  modified:
    - Packages/com.graffitientertainment.namer/Shaders/NAMER.shader
    - Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl
    - Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
    - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs

key-decisions:
  - "VC + Residual step gate reuses the existing NamerProcessorSettings.DecompositionEnabled (no duplicate control)"
  - "Roughness/AO step gates hard-gate the pipeline via a (enabled ? value : 0f) ternary so the persisted strength-slider values are never overwritten"
  - "Metallic/Emissive gate the shader only (scalar passthrough into packed bits has no pipeline stage)"
  - "Shaded-view toggles are transient (not persisted); ApplyDebugGates does not SetDirty so the on-disk generated .mat keeps neutral 1.0 defaults"

patterns-established:
  - "hidden [HideInInspector] _Dbg* float shader properties with __-prefixed display strings"
  - "internal static readonly string[] ShaderInputToggleLabels as the reflection-pinned label array"

requirements-completed: [DIP-01, DIP-02, DIP-03]

# Metrics
duration: 5min
completed: 2026-09-18
---

# Phase quick-fcx-namer-debug-dip-switches Plan 01: NAMER Debug Dip-Switches Summary

**Five neutral-default shader gates plus a persistent step dip-switch row and shaded-view toggle row, replacing the per-texture debug channel toolbar with a bisect instrument for the 04.2 residual-smeared reconstruction**

## Performance

- **Duration:** 5 min
- **Started:** 2026-09-18T18:15:52Z
- **Completed:** 2026-09-18T18:20:48Z
- **Tasks:** 3
- **Files modified/created:** 9

## Accomplishments
- Single NAMER surface shader now declares `_DbgEnableResidual/Roughness/AO/Metallic/Emissive` (all default 1.0) and `_DbgRoughnessNeutral` (default 0.5); `InitializeNamerSurfaceData` neutralizes residual→white, AO→1, roughness→neutral, metallic→0, emissive→0 when the matching gate is off, and is byte-identical when all gates are 1.
- Four EditorPrefs-persisted step gates (`RoughnessStageEnabled`, `AoStageEnabled`, `MetallicContributionEnabled`, `EmissiveContributionEnabled`, all default true) with `NamerProcessor.Process` hard-gating roughness extraction and AO un-multiply via `enabled ? value : 0f` so strength sliders are untouched.
- Window shows the step row above the foldouts, the shaded-view toggle row in place of the debug channel toolbar, and the preview rect now subtracts `GUI.skin.verticalScrollbar.fixedWidth` (no permanent horizontal scrollbar).

## Task Commits

1. **Task 1: Add neutral-default shader gates** - `125537c` (feat)
2. **Task 2: Persist step-gate settings and hard-gate pipeline** - `c85592f` (feat)
3. **Task 3: Window rewiring + scrollbar fix + tests** - `817b5eb` (feat)

## Files Created/Modified
- `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader` - 6 hidden neutral-default debug gate properties
- `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl` - CBUFFER gate vars + neutralization inside `InitializeNamerSurfaceData`
- `Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs` - 4 true defaults
- `Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs` - 4 EditorPrefs-backed bool properties
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` - hard-gated roughness/AO stage assignments
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` - step row, shaded-view toggles, `ApplyDebugGates`, scrollbar fix, debug-channel removal
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs` - `ShaderInputToggleLabels_AreFiveNeutralToggles`
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs` (+ `.meta`) - step-gate persistence/defaults + shader-gate neutrality

## Decisions Made
- VC + Residual reuses `DecompositionEnabled` (no duplicate key), staying in sync with the Decomposition foldout toggle through `_decompositionEnabled`/`_settings`.
- Roughness/AO are independent hard gates; the persisted slider values are never overwritten.
- Metallic/Emissive are shader-only gates (ANDed with their step-gate contribution flag inside `ApplyDebugGates`).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Cleanup] Removed three dead generated-texture fields**
- **Found during:** Task 3 (debug-channel machinery removal)
- **Issue:** Removing the `_debugMaterial` blocks left `_generatedSurface`, `_generatedBase`, and `_generatedResidual` assigned but never read (CS0414), since those textures only fed the deleted debug material.
- **Fix:** Removed the three fields and their `LoadAssetAtPath`/null-reset assignments in `ResolveGeneratedPreview` (kept `_generatedMaterial` and `_generatedMesh`).
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs`
- **Committed in:** `817b5eb` (Task 3 commit)

---

**Total deviations:** 1 auto-fixed (cleanup)
**Impact on plan:** Necessary to avoid dead fields/warnings after the plan's own debug-block removal; no scope creep beyond the plan's intent.

## Issues Encountered

- **EditMode test run blocked (not skipped silently).** The plan requires running `NamerDipSwitchTests` and `NamerEditorWindowSmokeTests` in the live editor via the unity-mcp `Unity_RunCommand` relay, but no `mcp__unity-mcp__*` tools were available in this executor. The fallback (headless `-batchmode -runTests`) is blocked because the interactive Unity Editor (PID 54091) holds the project lock (a second instance exits 134), and no pre-configured batch script exists in the repo. Per the testing protocol, this is reported as blocked rather than silently skipped. Each task's grep-based `<verify>` passed (`SHADER_GATES_OK`, `STEP_GATES_OK`, `WINDOW_GATES_OK`).

## Known Stubs

None — no placeholder values, TODO/FIXME markers, or un-wired data sources introduced.

## Next Phase Readiness

- The dip-switch bisect instrument is ready for the 04.2 residual-smeared-reconstruction investigation: toggle residual/roughness/AO/metallic/emissive in the full shaded After view, or hard-gate the roughness/AO pipeline stages, to isolate which input carries the smear.
- The uncommitted debug-session hunks (`NamerProcessor.cs` residual-resolution `Debug.Log`, `ResidualPipelineTests.cs` +150-line E2E test) remain intact — `NamerProcessor.cs`'s hunk rode along into `c85592f`, and `ResidualPipelineTests.cs` is still uncommitted as intended.

---
*Phase: quick-fcx-namer-debug-dip-switches*
*Completed: 2026-09-18*

## Self-Check: PASSED

- All 6 key files verified present on disk (NamerDipSwitchTests.cs + .meta, NAMER.shader, NamerSurface.hlsl, NamerEditorWindow.cs, SUMMARY.md).
- All 3 task commits verified present: `125537c`, `c85592f`, `817b5eb`.

## Orchestrator Verification Addendum (post-executor)

- **Live-editor full EditMode suite: 156 pass / 0 fail / 0 skip** (TestRunnerApi via unity-mcp RunCommand, marker `Temp/namer-dipswitch-results2.txt`). The executor's blocked test run was completed by the orchestrator after compile verification via UTF-16 literal probe (`tr -d '\0'` — plain ASCII `strings` cannot see .NET string literals).
- First full-suite run surfaced 2 failures, both diagnosed and fixed in commit `cfe83b7`:
  1. `NamerAOControlsTests.AoUnmultiplyStrength_RemainsTheOnlyGate` — superseded spec: DIP-01's `AoStageEnabled` is the legitimate 5th AO control; count assertion updated 4→5, single-unmultiply invariant kept.
  2. `NamerOneTextureTests.OneTexture_ResidualDropped_LeavesBaseResidualMapUnbound` — pre-existing isolation hole (NOT a quick-task regression): the live editor's `WriteResidual=ON` pref (left from the 2026-09-17 smear investigation) leaked into the test's unpinned setting and flipped the dropped-residual path to AlwaysKeep. Fixed by pinning `WriteResidual = false` in the test's settings initializer.
- Final commits: `125537c`, `c85592f`, `817b5eb`, `cfe83b7`.

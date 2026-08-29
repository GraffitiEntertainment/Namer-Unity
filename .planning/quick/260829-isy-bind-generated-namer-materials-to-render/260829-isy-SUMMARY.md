---
phase: quick-isy-bind-generated-namer-materials
plan: 01
subsystem: editor-workflow
tags: [renderer-binding, generated-preview, after-panel, debug-binding, editmode-tests]
requires:
  - GEN-01
  - UI-02
  - UI-03
provides:
  - renderer material binding after generation
  - generated/live After-panel flip state
  - generated-texture debug binding
affects:
  - NamerProcessor
  - AssetGenerator
  - NamerEditorWindow
tech-stack:
  added:
    - NamerAfterPanelState (C# state machine)
  patterns:
    - index-aligned source-to-generated material mapping by GetInstanceID
    - single shared ComposeDestinationFolder/ComposePath folder convention
    - persistent AssetDatabase loads (never DestroyImmediate generated assets)
key-files:
  created:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerRendererBindingTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerAfterPanelStateTests.cs
  modified:
    - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
decisions:
  - Made ComposePath public and added ComposeDestinationFolder as the single source of truth for the generated-asset folder convention (DRY seam shared by NamerProcessor and the window).
  - Generated materials/textures are persistent AssetDatabase assets loaded via LoadAssetAtPath; they are never DestroyImmediate'd (including OnDisable).
  - ResolveGeneratedPreview resets GeneratedAvailable=false on entry so the flag accurately reflects "a generated material exists" (defensive correctness, not a plan-required behavior change).
metrics:
  duration: ~20 min
  completed_date: "2026-08-29"
---

# Phase quick-isy Plan 01: Bind Generated NAMER Materials to Renderers Summary

## One-liner

Close the last mile of "Process with NAMER": the processor now swaps each scene
renderer's sub-mesh slot to its index-aligned generated material, and the window's
After panel + debug channels flip between the generated assets and the live in-memory
preview based on a small state machine.

## What Changed

**Task 1 — Shared path helpers + renderer binding**
- `AssetGenerator.ComposePath` is now `public`; added `public static ComposeDestinationFolder`.
- `NamerProcessor.Process` uses `ComposeDestinationFolder` (DRY seam, identical behavior).
- Added `BindGeneratedMaterials`: after a successful Process, maps source material
  `GetInstanceID()` -> generated material (index-aligned) and swaps each live scene
  renderer's `sharedMaterials` slot. Non-GameObject and asset-backed selections are
  no-ops; source assets are never written or mutated.

**Task 2 — After-panel flip state + generated debug binding**
- New `NamerAfterPanelState` (generated/live flip contract: `GeneratedAvailable`,
  `Tweaking`, `PreferGenerated`, `MarkTweaking`, `Reset`).
- `ResolveGeneratedPreview()` loads the generated `.mat`/`_Surface.png`/`_Base.png`
  via the shared `ComposeDestinationFolder`/`ComposePath` convention, sets
  `GeneratedAvailable`, and binds generated textures to the debug material.
- Called on selection rebuild and process success; AO sliders + occluder change call
  `MarkTweaking()` (flip to live); `DrawPreviewSection` and `RecomputePreview` prefer
  generated assets when `PreferGenerated` holds.

**Task 3 — EditMode tests**
- `NamerRendererBindingTests`: per-slot index-aligned binding + source immutability.
- `NamerAfterPanelStateTests`: flip logic + path-convention parity (D-16).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Correctness] Reset `GeneratedAvailable = false` at the top of `ResolveGeneratedPreview`**
- **Found during:** Task 2
- **Issue:** The plan reset `Tweaking` (via `Reset()`) and nulled the generated
  material/texture fields, but left `GeneratedAvailable` untouched, so a stale `true`
  could persist across selection changes.
- **Fix:** Set `_afterPanelState.GeneratedAvailable = false;` right after `Reset()` so
  the flag accurately reflects "a generated material exists for the selection". The
  window already guards `PreferGenerated` with `_generatedMaterial != null` /
  `_generatedSurface/_generatedBase != null`, so this is a defensive accuracy fix, not
  a behavior change.
- **Files modified:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs`
- **Commit:** 958f6a3

No other deviations — the plan executed as written otherwise.

## Tests

EditMode tests written but not executed in worktree — deferred to main-tree run by
orchestrator. Do not claim tests passed in this environment.

- `GraffitiEntertainment.Namer.Tests.NamerAfterPanelStateTests` (pure logic, `[Test]`)
- `GraffitiEntertainment.Namer.Tests.NamerRendererBindingTests` (GPU-gated `[UnityTest]`,
  `Assert.Ignore` when `!ComputeAvailable`)

## Known Stubs

None.

## Threat Flags

None — no new network endpoints, auth paths, or trust-boundary file-access patterns
introduced beyond the plan's `<threat_model>` (T-quick-01/T-quick-02 accepted).

## Self-Check

- Commits present: cf6849f, 958f6a3, 1a27131 (verified via `git log --oneline -5`).
- Files present on disk (created): `NamerAfterPanelState.cs`, `NamerRendererBindingTests.cs`, `NamerAfterPanelStateTests.cs` (+ `.meta` files).
- Grep verification: `ComposeDestinationFolder` in both `NamerProcessor.cs` and `AssetGenerator.cs`; `MarkTweaking` in all five tweak blocks; no `DestroyImmediate(_generated` anywhere.

## Self-Check: PASSED

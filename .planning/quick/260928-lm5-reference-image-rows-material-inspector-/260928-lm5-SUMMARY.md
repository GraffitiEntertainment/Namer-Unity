---
phase: quick-260928-lm5
plan: 01
subsystem: namer-post-look-editor-ui
tags: [im gui, editor-ui, namer-post-look, reference-images, material-inspector]
requires:
  - "05.2 NamerPostLook asset + NamerPostLookEditor A1-A5 reference section"
provides:
  - "Material-inspector reference rows (label left + square 64px thumbnail ObjectField) in NamerPostLookEditor.DrawReferenceSlots"
  - "kThumbnailSize = 64 file-private const in NamerPostLookEditor (5.1 sibling convention)"
affects:
  - "Packages/com.graffitientertainment.namer/Editor/UI/NamerPostLookEditor.cs (DrawReferenceSlots layout only)"
tech-stack:
  added: []
  patterns:
    - "Label-less EditorGUILayout.ObjectField(obj, type, allowSceneObjects, params GUILayoutOption[]) overload renders the square thumbnail picker; GUILayout.Width/Height pin it to kThumbnailSize"
key-files:
  created: []
  modified:
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerPostLookEditor.cs
decisions:
  - "Row shape per plan verbatim: BeginHorizontal + GUILayout.Label(\"Reference Texture #N\") + label-less square ObjectField + EndHorizontal, with EndHorizontal BEFORE the `assigned == current` continue-guard so the horizontal group is always closed inside the loop"
  - "kThumbnailSize stays file-private in NamerPostLookEditor (mirrors the 5.1 NamerStyleProfileEditor file-private const); NamerEditorConstants untouched"
metrics:
  duration: 1min
  completed: 2026-09-28
---

# Quick Task 260928-lm5: Material-Inspector Reference Rows Summary

**One-liner:** NamerPostLook reference slot rows restyled to material-inspector texture-slot layout — "Reference Texture #N" label left, square 64px thumbnail ObjectField right — pure IMGUI relayout with the assign/undo/RaiseChanged write block byte-identical.

## What Was Built

Three surgical edits to `NamerPostLookEditor.cs` (15 insertions, 7 deletions, one file):

1. **`kThumbnailSize` const** added after the field block (`private const int kThumbnailSize = 64;`), mirroring the 5.1 `NamerStyleProfileEditor` file-private convention, with the amended doc comment "(material-inspector row style; 2026-09-28 user amendment)".
2. **`DrawReferenceSlots` row relayout** — the single labeled-ObjectField widget line became a material-inspector row:
   - `EditorGUILayout.BeginHorizontal()`
   - `GUILayout.Label(new GUIContent("Reference Texture #" + slot))`
   - label-less square picker: `(Texture2D)EditorGUILayout.ObjectField(current, typeof(Texture2D), false, GUILayout.Width(kThumbnailSize), GUILayout.Height(kThumbnailSize))`
   - `EditorGUILayout.EndHorizontal()` — placed BEFORE the `if (assigned == current) { continue; }` guard so the horizontal group is always closed inside the loop (no IMGUI layout exception possible).

   The guard and the entire write block (`Undo.RecordObject("Assign Reference Image")` → `SyncWeights()` → `look.References[i] = assigned` → `EditorUtility.SetDirty` → `NamerPostLook.RaiseChanged`) are untouched by the diff — byte-identical, verified via `git diff`.
3. **Two doc comments amended** to the 2026-09-28 amendment: the class doc's A1-A4 parenthetical now states slot rows render material-inspector style (label left, square thumbnail ObjectField right), superseding the 2026-09-27 no-thumbnail call; the `DrawReferenceSlots` doc now describes the label-left + square `kThumbnailSize` ObjectField row.

The empty-state block, +/– footer, count-honesty line, and Advanced foldout are unchanged. `NamerStyleProfileEditor.cs`, `NamerEditorConstants.cs`, and all test files untouched.

## Verification

**Static grep gates (all PASS):**
- `kThumbnailSize` >= 3 occurrences (actual: 4 — const, Width, Height, DrawReferenceSlots doc)
- `GUILayout.Label(new GUIContent("Reference Texture #" + slot))` == 1
- `EditorGUILayout.BeginHorizontal()` == 2 and `EndHorizontal()` == 2 (footer pair + new row pair)
- old inline-label form `new GUIContent("Reference Texture #" + slot), current` == 0
- `Undo.RecordObject(look, "Assign Reference Image")` == 1 (write block intact)
- `GUILayout.Width(kThumbnailSize), GUILayout.Height(kThumbnailSize)` == 1
- superseded phrases `no per-row thumbnail` / `only image` == 0
- Brace balance 79/79, paren balance 293/293

**Live verification (orchestrator-owned):** No Unity MCP / relay access in this executor session, so the live-editor script refresh (zero new console/Editor.log errors) and the visual checkpoint were NOT run here — the orchestrator runs the live-editor visual/compile verification after this summary per the spawn constraints. The Roslyn compile-equivalence gate was also intentionally skipped per the same constraint (static gates + brace balance deemed sufficient for this mechanical IMGUI relayout; no new types, no API surface risk — all widgets used are existing UnityEditor overloads).

## Checkpoint (Task 2)

`checkpoint:human-verify` (visual verification of the restyled rows) — **auto-approved** under `workflow.auto_advance = true` (not a package-legitimacy gate). Verification steps for the orchestrator's live run:

1. Select a NamerPostLook asset (e.g. `Assets/Models/Neo/Neo-T-Pose Post.asset`) — Reference Images section shows each slot as label-left + square thumbnail row.
2. Click a square, pick a Texture2D: thumbnail appears in the square; count line updates.
3. Drag a different texture onto a square: it swaps.
4. Cmd+Z: assignment reverts (undo name "Assign Reference Image").
5. +/– footer, count line, and Advanced weight sliders render as before; no console errors.

## Deviations from Plan

None — plan executed exactly as written.

## Known Stubs

None — no data wiring touched; this is a pure layout change on a fully wired inspector.

## Self-Check: PASSED

- File exists: `Packages/com.graffitientertainment.namer/Editor/UI/NamerPostLookEditor.cs` — FOUND
- Commit exists: `693a85d` — FOUND
- Commit contains exactly one file; zero file deletions; unrelated working-tree changes (Assets/, tests, .gitignore) not staged.

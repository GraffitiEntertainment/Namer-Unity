# Capture List — 260921-l5s (docs USER_GUIDE section images)

Ordered screenshot pass for the NAMER Processor window (open it via **Tools > NAMER > Processor**; window title is "NAMER Processor"). One row per image referenced by docs/USER_GUIDE.md, in guide order. Save each capture to the exact target path in the last column (paths are relative to docs/). Captures happen in the orchestrator's live-editor RunCommand pass — no image files were fabricated by the executor.

| # | Section | UI state needed to expose it | Target file (relative to docs/) |
|---|---------|------------------------------|---------------------------------|
| 1 | Overview | Window freshly opened with a processed selection loaded (Before/After panes visible and rendered) | images/NAMER-preview.png (EXISTS — verify only, do not recapture) |
| 2 | Stage & Shader Gates | Top of the window, no scrolling needed; all five checkboxes visible (VC + Residual, Roughness, AO, Metallic, Emissive) | images/stage-shader-gates.png |
| 3 | Source | Source foldout expanded with a selection loaded showing the material list (name — ShaderName rows) | images/source-section.png |
| 4 | Preview/Debug | Preview/Debug foldout expanded; Before/After panes rendered, shaded-view toggle row and six channel panes visible (scroll the foldout content as needed) | images/preview-before-after.png |
| 5 | Channel popup (OPTIONAL) | Click a channel pane to open the 384 px popup — needs an interactive click or scripted MouseDown; skip this row if automation fails (the guide tolerates the missing file) | images/channel-popup.png |
| 6 | Roughness Extraction | Foldout expanded, Dip Source popup + Roughness Dip Depth slider visible | images/roughness-extraction.png |
| 7 | AO | Foldout expanded, all four sliders visible (AO Un-multiply Strength, AO Blur Radius, AO Strength, AO Contrast) | images/ao-section.png |
| 8 | Decomposition | Foldout expanded with Vertex Color Decomposition ON so Write Residual, Error Threshold, Residual Resolution, and the Statistics rows are live | images/decomposition-section.png |
| 9 | Output | Foldout expanded, Destination / Prefix / Suffix / Overwrite generated visible | images/output-section.png |
| 10 | Action | Bottom of the window (pinned outside the scroll view): Process with NAMER button + status box | images/action-process.png |

Notes for the capture pass:

- All foldouts default to expanded in a fresh window; the sections scroll inside the window's scroll view — set the scroll position so the target section is fully on-screen before capturing.
- Row 8 requires toggling Vertex Color Decomposition on first; the stage-gate row (row 2 area) must show its checkbox checked.
- Row 5 is optional: it requires a synthetic MouseDown inside a channel pane rect; skip without failing the pass.
- Row 1 already exists on disk — do not overwrite it; verify only.
- After each capture, confirm the saved filename matches the target column exactly (lowercase, hyphens, .png).

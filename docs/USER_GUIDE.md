# NAMER Processor — User Guide

The NAMER Processor converts ordinary Unity PBR materials into compact NAMER materials entirely inside the Unity Editor. Baked ambient occlusion is un-multiplied from the base color, removed detail can be re-expressed as roughness gloss, surface data (roughness, AO, metallic, emissive, and the packed normal) is compressed into a single surface texture, and the base color can optionally be decomposed into mesh vertex colors for a one-texture material. A live before/after preview lets you tune every parameter before committing to a Process run. Your source assets are never modified — all generated output is written to a separate folder.

## Getting Started

There are three entry points:

- **Tools > NAMER > Processor** opens the NAMER Processor window described in this guide.
- **Assets > Process with NAMER** (Project window context menu) runs Process immediately on the selected asset using default settings and logs the result to the console.
- **GameObject > Process with NAMER** (Hierarchy context menu) does the same for the selected GameObject.

The context-menu commands are the fast path: select an asset, right-click, and the NAMER material is generated without opening the window. The result line reads `[NAMER] Process with NAMER: N material(s) generated`, with any warnings logged below it. Use the full window when you want the preview, the debug views, or non-default settings.

You can select a GameObject (in the scene or a prefab), a prefab, a model (for example an FBX), a single material, or a folder — every inspectable PBR material found in the selection is processed. The window tracks the current Editor selection automatically, so whatever you select appears in the Source section and the preview.

## Contents

- [Stage & Shader Gates](#stage--shader-gates)
- [Source](#source)
- [Preview/Debug](#previewdebug)
- [Roughness Extraction](#roughness-extraction)
- [AO](#ao)
- [Decomposition](#decomposition)
- [Output](#output)
- [Action](#action)

The sections below appear in the same order as the window, top to bottom.

## Stage & Shader Gates

![Stage & Shader Gates row](images/stage-shader-gates.png)

This checkbox row sits above everything else — it is always visible, not folded into the scroll content. It holds two different kinds of gates, and telling them apart matters:

- **Pipeline-stage gates** — `VC + Residual`, `Roughness`, and `AO`. These gate actual processing stages, so they take effect on the next Process run (and on the debounced live preview recompute).
- **Shader-only gates** — `Metallic` and `Emissive`. These gate the shader contribution only and are live: the preview updates the moment you click them, no re-Process needed.

The five checkboxes:

| Checkbox | Kind | Effect | Default |
|----------|------|--------|---------|
| VC + Residual | Pipeline stage | Hard gate for vertex-color decomposition + residual (same setting as the `Vertex Color Decomposition` toggle in the Decomposition section). Independent of the strength sliders. | Off |
| Roughness | Pipeline stage | Hard gate for roughness extraction — skips extraction without touching the dip-depth slider. | On |
| AO | Pipeline stage | Gates AO synthesis only (see [AO](#ao)). | On |
| Metallic | Shader only | Gates the metallic contribution in the shader. | On |
| Emissive | Shader only | Gates the emissive contribution in the shader. | On |

Every toggle is persisted with your processor settings, so the window comes back in the state you left it. The whole row is disabled while a Process run is in flight.

Do not confuse this row with the similar-looking shaded-view toggle row inside Preview/Debug: that row is seven shader-only debug neutralizers for the After view — live, not persisted, and they never change what Process generates.

## Source

![Source foldout](images/source-section.png)

The Source foldout shows exactly what will be processed before you commit. It lists the selection name and its type, then one line per material in the form `name — ShaderName`, plus warning boxes for anything suspicious the inspector found (for example missing maps).

If nothing is selected, the section shows **No source selected** and reminds you to select a GameObject, prefab, model, material, or folder in the Project or Hierarchy window and press Process with NAMER. If the selection contains no inspectable PBR materials, it shows **No materials found** with the same guidance.

## Preview/Debug

![Preview/Debug: Before/After panes, shaded-view toggles, and channel panes](images/preview-before-after.png)

This foldout is the heart of the window: a combined before/after render of the actual mesh, plus per-channel debug views. Until a mesh is selected it shows an info box asking you to select a GameObject or model.

### Before / After panes

The **Before** pane renders the source material; the **After** pane renders the NAMER material the current settings would produce. Both panes share one orthographic camera with a synced rotation — orbit one side and the other side follows, so differences you see are material differences, never camera ones. When decomposition is enabled the After pane draws the seam-split mesh with its fitted vertex colors, which is the mesh Process writes.

Camera controls over the preview:

- **Drag** to orbit.
- **Ctrl+drag** to pan the camera.
- **Shift+scroll, Ctrl+scroll, or Alt+scroll** to zoom (wheel up zooms in).
- Plain scroll scrolls the dialog as usual.

### Shaded-view toggle row
![Shaded View Toggle](images/shaded-view-toggle.png)

Below the preview sit seven checkboxes: `Base/Residual`, `Roughness`, `AO`, `Metallic`, `Emissive`, `Vertex Color`, and `Normal`. Each one neutralizes the matching input in the full shaded After view — uncheck `Roughness`, for instance, and the After pane shades with a neutral roughness so you can see what that channel contributes. These are shader-only debug toggles: they are live, they trigger no GPU recompute, and they are not persisted. Metallic and Emissive here are additionally ANDed with their Stage & Shader Gates counterparts, so turning a contribution off at the gates also disables it here.

### Channel panes

Under the toggle row sit six small (48 px) panes labeled `Base`, `Roughness`, `AO`, `Metallic`, `Emissive`, and `Normal`. Each pane decodes one channel of the After material's packed textures through the exact NAMER decode the runtime material uses — what you see is what the shipped material samples, with no drift. `Base` reads `_BaseResidualMap`; the other five read `_SurfaceMap`. Before any generated textures exist, the panes render as neutral gray boxes.

Click any pane to open a large (384 px) popup of that channel:

![Channel popup](images/channel-popup.png)

The popup anchors below the clicked pane and closes when you click away (or when you open another channel — popups replace each other).

### Triangles

![Triangles toggle](images/toggle-triangles.png)

The `Triangles` toggle overlays a cyan wireframe on the After pane as a second render pass. It is a visual debug aid only — it never affects processing.

## Roughness Extraction

![Roughness Extraction foldout](images/roughness-extraction.png)

This foldout controls how gloss is extracted from the source and re-expressed as roughness. The whole stage can be hard-gated off with the `Roughness` checkbox in Stage & Shader Gates, independent of these controls.

- **Dip Source** — which signal dips roughness toward gloss:
  - **Removed Detail** (default) re-expresses the luminance the Gouraud projection removed as gloss: bright speckle dips toward gloss, dark occlusion raises toward matte. This path requires Vertex Color Decomposition to be enabled.
  - **Sobel Edge** is the fallback Blender-parity edge signal, used when decomposition is off or skipped.
- **Roughness Dip Depth** — slider from 0 to 1, default 0.25. A taste control for how strongly removed-detail luminance becomes gloss; 0 keeps the authored roughness scalar unchanged. Luminance carries roughly half of the removed signal's energy; the discarded chroma grain is an accepted loss because a scalar gloss channel has no home for color.

When the effective dip source falls back to Sobel Edge — either because you chose it or because Removed Detail is unavailable (decomposition off or skipped for this selection) — an info box tells you so.

Changing either control recomputes the preview in memory 0.3 s after the change; nothing is written to disk until you press Process with NAMER.

## AO

![AO foldout](images/ao-section.png)

The AO contract is simple and important:

- An authored `_OcclusionMap` on the source material **always transfers**, regardless of any checkbox.
- The `AO` checkbox in Stage & Shader Gates gates **synthesis only**: OFF with no authored map packs the surface B channel white (no synthetic AO at all); ON with no authored map runs the geometry bake as the synthetic source.
- The `AO Un-multiply Strength` slider is independent of the AO gate — when surface B is white, the un-multiply divide is an identity whatever the strength.

Four sliders, all live:

| Slider | Range | Default | Effect |
|--------|-------|---------|--------|
| AO Un-multiply Strength | 0–1 | 1 | How strongly baked AO is divided out of the base color before packing. |
| AO Blur Radius | 0–16 texels | 0 | Blurs the AO output; 0 = off. |
| AO Strength | 0–1 | 1 | Blend of the AO map in the decode; 1 = full AO, 0 = white/no AO. |
| AO Contrast | 0–4 | 1 | Contrast around a 0.5 pivot; 1 = identity. |

All four sliders respond live without re-Process: each change recomputes the preview in memory after a 0.3 s debounce — an in-memory GPU recompute with nothing written to disk. The settings are baked in on the next Process run.

## Decomposition

![Decomposition foldout and statistics](images/decomposition-section.png)

This foldout controls the vertex-color decomposition stage, which fits the base color into mesh vertex colors so the material can ship with fewer (or no) color textures.

- **Vertex Color Decomposition** — the master toggle, off by default (opt-in). When enabled, the preview shows the decomposed reconstruction and Process writes the seam-split mesh (plus a residual EXR when enabled below). This is the same setting as the `VC + Residual` stage gate.

When decomposition is enabled but will be skipped for the current selection — a multi-material or multi-mesh source — a warning box explains why, and both the preview and Process show/generate the non-decomposed result instead of silently promising an extraction that will not happen.

- **Write Residual Texture** — off by default, which is the one-texture outcome: no residual EXR is written, because the Gouraud projection already makes the base divided by the vertex-color interpolation white by construction. Turn it on to additionally write the removed detail as an EXR (including its luminance, which the gloss transfer also re-expresses). On stacked-UV assets the EXR reads honestly but cannot attribute overlap blending between triangles covering the same texel.

Enabled only when Write Residual is on:

- **Error Threshold** — slider from 0 to 0.10, default 0.02. The maximum acceptable reconstruction error for the adaptive residual-resolution search.
- **Residual Resolution** — popup with `Auto` (default), 2048, 1024, 512, 256, 128. Auto adaptively halves from the source resolution while error stays within the threshold; the manual options snap to the same halving steps.

When Write Residual is off, the two controls render disabled with the hint `Only applies when Write Residual is on.`

### Statistics

A read-only block (shown when decomposition is enabled) reporting the quality of the current fit. It reads `—` until the first fit completes:

| Row | Meaning |
|-----|---------|
| Coverage | Fraction of UV-covered texels reconstructed within the error threshold. |
| Avg Error | Average removed-detail reconstruction error over covered texels. |
| Max Error | Maximum removed-detail reconstruction error over covered texels. |
| Removed-detail max error | The source-vs-reconstruction error of the removed detail — what the residual-ON EXR encodes and what the gloss transfer re-expresses. |
| Residual | `not written (one-texture)` or `written @ Npx`, reflecting the Write Residual checkbox. |

A status line underneath reads `Recomputing preview…` while the debounced recompute is pending or running, and `Preview up to date` once it settles.

## Output

![Output foldout](images/output-section.png)

Where generated assets go and what they are called:

- **Destination** — root folder for everything NAMER generates, default `Assets/NAMERGenerated/`. Each processed selection gets its own subfolder named after it, so assets never collide across sources.
- **Prefix** / **Suffix** — prepended/appended to every generated file name. Prefix defaults to empty; suffix defaults to `_Namer`. A material named `tripo_mat` with defaults becomes `tripo_mat_Namer` assets.
- **Overwrite generated** — whether a Process run may replace previously generated assets at the same paths.

Source assets are never touched, imported, or re-imported by NAMER; generated output lives only inside the destination folder.

## Action

![Action section with the Process button](images/action-process.png)

Pinned outside the scroll view at the bottom of the window, the Action section is always reachable no matter how far you have scrolled.

- **Process with NAMER** — runs the pipeline on the current selection with the current settings and writes the generated assets. While a run is in flight the label reads `Processing…` and the button is disabled; it is also disabled while a preview recompute is pending, so Process always bakes in what you see.

After the run a status box reports the outcome — for example `Generated 2 material(s) under Assets/NAMERGenerated/.` on success, or the blocking reason (a destination conflict, an invalid selection, and so on) as an error. Warnings from the run are logged to the console.

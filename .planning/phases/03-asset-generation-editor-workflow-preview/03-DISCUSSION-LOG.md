# Phase 3: Asset Generation + Editor Workflow + Preview - Discussion Log

> **Audit trail only.** Do not use as input to planning, research, or execution agents.
> Decisions are captured in CONTEXT.md — this log preserves the alternatives considered.

**Date:** 2026-08-27
**Phase:** 3-Asset Generation + Editor Workflow + Preview
**Mode:** `--auto` (recommended defaults auto-selected; no interactive prompts)
**Areas discussed:** Output layout & naming, Scene integration scope, Asset writing & import stamping, Preview approach, Debug channel views, Command surface & controls, Interactive update behavior, Test mechanics, MVP package assembly

---

## Output layout & naming

| Option | Description | Selected |
|--------|-------------|----------|
| Per-selection subfolder under `Assets/NAMERGenerated/` | `NAMERGenerated/{sourceName}/`, per-material files, configurable prefix/suffix (default `_Namer`), EditorPrefs persistence | ✓ |
| Flat folder | All outputs directly under `NAMERGenerated/` — name-collision prone | |
| Mirror full source path | Replicates source tree — deepest organization, most surprising deletes | |

**Auto-selection:** recommended default. Grounded in GEN-01/GEN-03 and Phase 2 D-03 (unique-material dedup).

## Scene integration scope

| Option | Description | Selected |
|--------|-------------|----------|
| Material + textures only | Preview applies material in-memory; no renderer/prefab mutation | ✓ |
| Also re-point renderers | Convenience but touches user scenes/prefabs | |
| Prefab variant | Creates new assets users didn't ask for in v1 | |

**Auto-selection:** recommended default — matches success criterion wording ("receives a NAMER material + textures") and the non-destructive core value.

## Asset writing & import stamping

| Option | Description | Selected |
|--------|-------------|----------|
| PNG + importer stamping with label | Base = sRGB PNG; surface = linear/uncompressed/point/no-mips PNG; `NamerGenerated` asset label gates overwrite | ✓ |
| EXR for base | Unnecessary — base is LDR after quantization | |
| Raw asset copies | Cannot stamp importer settings reliably | |

**Auto-selection:** recommended default. Point filtering on the packed texture verified against Blender reference by researcher (D-06).

## Preview approach

| Option | Description | Selected |
|--------|-------------|----------|
| PreviewRenderUtility mesh before/after | Actual mesh, side-by-side, synced camera | ✓ |
| Texture-only 2D view | Cheaper but fails UI-04 ("actual selected mesh") | |
| Separate preview scene | Heavier; PreviewRenderUtility is the Unity-native pattern | |

**Auto-selection:** recommended default. Researcher must resolve the STATE.md blocker (PreviewRenderUtility docs 404).

## Debug channel views

| Option | Description | Selected |
|--------|-------------|----------|
| Current-output channels only | Base, AO, normal, roughness, metallic, emissive on the preview mesh | ✓ |
| All UI-05 channels with placeholders | Dead toggles for Phase 4 channels — rejected (no fake UI) | |
| Texture-space flat views | Supplement, not replacement | |

**Auto-selection:** recommended default (D-11/D-12).

## Command surface & controls

| Option | Description | Selected |
|--------|-------------|----------|
| Context menus + window button, single entry | `Assets/…` + `GameObject/…` context menus and window Process button share one entry point | ✓ |
| Menu bar only | Least discoverable for the FBX right-click workflow | |
| Window button only | Fails the "select FBX → run command" core value | |

**Auto-selection:** recommended default (D-13/D-14). Window ships only functional controls; Phase 4/5 controls land with their phases.

## Interactive update behavior

| Option | Description | Selected |
|--------|-------------|----------|
| Debounced full re-dispatch | Re-run staged kernels on control change; preview-only, never writes | ✓ |
| Staged caching | Optimization — discretion, not a locked decision | |
| Manual refresh only | Fails UI-06 | |

**Auto-selection:** recommended default (D-10).

## Test mechanics

| Option | Description | Selected |
|--------|-------------|----------|
| File-hash before/after | SHA over source files + .meta around a full Process run; temp destination | ✓ |
| Metadata comparison | Misses file-content corruption | |
| Timestamp checks | Trivially false-positive/negative | |

**Auto-selection:** recommended default (D-15/D-16).

## MVP package assembly

| Option | Description | Selected |
|--------|-------------|----------|
| Metadata + README + test sweep | package.json complete, package README, headless suites green | ✓ |
| Include Samples~ | Deferred — scope | |
| Unity package validation suite | Deferred — tooling | |

**Auto-selection:** recommended default (D-17).

---

## Claude's Discretion

Class/file names, window layout details, debounce interval and staging mechanics, hash algorithm, fixture construction, debug-view mechanism (keyword variant vs editor-only debug shader), PNG/readback plumbing.

## Deferred Ideas

- Auto-assign generated material to renderers / prefab-variant generation
- Shared team-settings `NamerProcessorSettings` ScriptableObject (v1: EditorPrefs)
- `Samples~` smoke content in the package
- EXR/HDR base-color export

---
phase: quick-fcx-namer-debug-dip-switches
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Shaders/NAMER.shader
  - Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl
  - Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
  - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs
autonomous: true
requirements: [DIP-01, DIP-02, DIP-03]

must_haves:
  truths:
    - "A horizontal step-checkbox row [VC + Residual] [Roughness] [AO] [Metallic] [Emissive] sits above the foldouts; unchecking Roughness skips roughness extraction and unchecking AO skips the AO un-multiply without touching the strength sliders; VC + Residual drives the existing decomposition toggle"
    - "The per-texture debug channel toolbar is gone, replaced by [Residual] [Roughness] [AO] [Metallic] [Emissive] checkboxes that neutralize the matching input in the full shaded After view, with all gates neutral-default (1.0) so nothing changes unless toggled"
    - "The NAMER window no longer shows a permanent horizontal scrollbar (preview width subtracts the vertical scrollbar width)"
    - "Step-row toggle state persists across window reopen via EditorPrefs; shaded-view toggles reset to neutral on reopen"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Shaders/NAMER.shader"
      provides: "5 neutral-default shader gate properties + roughness-neutral property"
      contains: "_DbgEnableResidual"
    - path: "Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl"
      provides: "shader-side neutralization of residual/roughness/AO/metallic/emissive inputs"
      contains: "_DbgEnableMetallic"
    - path: "Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs"
      provides: "4 persisted step-gate bools"
      contains: "RoughnessStageEnabled"
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
      provides: "step row + shaded-view toggle row + gate application + scrollbar fix"
      contains: "ApplyDebugGates"
    - path: "Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs"
      provides: "settings persistence/defaults + shader neutral-default tests"
      contains: "ShaderDebugGates_DefaultNeutral"
  key_links:
    - from: "NamerEditorWindow.cs"
      to: "NamerSurface.hlsl _DbgEnable* floats"
      via: "Material.SetFloat on preview + generated After materials"
      pattern: "ApplyDebugGates"
    - from: "NamerEditorWindow.cs"
      to: "NamerProcessorSettings bools"
      via: "EditorPrefs step-gate persistence"
      pattern: "_settings\\.RoughnessStageEnabled"
    - from: "NamerProcessor.cs"
      to: "inspection strength fields"
      via: "hard gate (enabled ? value : 0)"
      pattern: "settings\\.AoStageEnabled"
    - from: "NamerSurface.hlsl"
      to: "baseResidual.rgb"
      via: "residual neutralization to white"
      pattern: "lerp(half3(1.0, 1.0, 1.0)"
---

<objective>
Add NAMER debug dip-switches to the processor window: (1) a horizontal step-checkbox row
gating pipeline stages, (2) shaded-view input toggles driven by neutral-default float gates in
NamerSurface.hlsl that replace the per-texture debug channel toolbar, and (3) the permanent
horizontal-scrollbar fix.

Purpose: The 04.2 residual-smear bug is REOPENED — the render still reads fit-only-smooth even
with a 2048 residual. The residual ON/OFF shaded toggle plus per-stage hard gates are the bisect
instrument to isolate which input (residual, roughness, AO, metallic, emissive) carries the smear.
Output: A single NamerSurface shader (no forked debug shader) with 5 neutral-default float gates
(`_DbgEnableResidual/Roughness/AO/Metallic/Emissive`) plus `_DbgRoughnessNeutral`; 4 persisted
step-gate settings; the window's step row + shaded-view toggle row + gate application; the
scrollbar fix; and EditMode tests pinning persistence, defaults, and shader-gate neutrality.

Locked decisions implemented here (traceability):
- DIP-01 = step dip-switch row (pipeline hard gates) — VC+Residual maps to the existing
  `DecompositionEnabled`; Roughness/AO are new hard gates; Metallic/Emissive gate the shader only.
- DIP-02 = shaded-view input toggles replacing the debug channel toolbar.
- DIP-03 = scrollbar fix (subtract `GUI.skin.verticalScrollbar.fixedWidth`, keep the 256f guard).
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/STATE.md
@.planning/debug/resolved/residual-smeared-reconstruction.md

# The source files this plan edits (read first — the plan references their exact symbols):
@Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
@Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
@Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
@Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl
@Packages/com.graffitientertainment.namer/Shaders/NAMER.shader
@Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
@Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerOneTextureTests.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerUIControlsTests.cs

<interfaces>
<!-- Key contracts the executor uses — no codebase exploration needed. -->

NamerProcessorSettings (Editor/Settings/NamerProcessorSettings.cs): EditorPrefs-backed get/set
pattern. Existing keys are `const string ...Key = "NamerProcessor.X";` and each property is
`get { return EditorPrefs.GetBool(Key, NamerEditorConstants.DefaultX); } set { EditorPrefs.SetBool(Key, value); }`.
`DecompositionEnabled` already exists (key "NamerProcessor.DecompositionEnabled") and is REUSED as
the step row's "VC + Residual" switch — do NOT add a duplicate.

NamerEditorConstants (Editor/NamerEditorConstants.cs): `public const bool DefaultDecompositionEnabled = false;`
and `public const float DefaultRoughnessExtractStrength = 0.25f;` etc. New defaults follow the same shape.

NamerProcessor.Process (Editor/Pipeline/NamerProcessor.cs ~lines 133-138): currently
`inspection.AoUnmultiplyStrength = settings.AoUnmultiplyStrength;` and
`inspection.RoughnessExtractStrength = settings.RoughnessExtractStrength;` (and AoBlurRadius/AoStrength/
AoContrast/DipSource). These two assignments become hard-gated. NOTE: this file carries UNCOMMITTED hunks
(the residual-resolution Debug.Log after generator.Generate, ~+15 lines) that must RIDE ALONG — do not revert.

NamerComputePipeline.Process (Editor/Pipeline/NamerComputePipeline.cs line 120):
`bool shouldExtract = inspection.MetallicGlossMap == null && inspection.RoughnessExtractStrength > 0f;`
— forcing `RoughnessExtractStrength = 0` skips the roughness extraction stage (both Sobel and
removed-detail transfer). Line 351: `_compute.SetFloat("_AoUnmultiplyStrength", inspection.AoUnmultiplyStrength);`
with CSNormalize (Compute/NAMERPack.compute line 61) `base / lerp(1.0, max(ao, _AoUnmultiplyFloor), _AoUnmultiplyStrength)`
— forcing `AoUnmultiplyStrength = 0` skips the AO un-multiply (division by 1.0). Metallic/emissive are scalar
passthrough (NAMERPack.compute lines 75/79) — no pipeline stage exists; they gate the shader only.

NamerSurface.hlsl: `CBUFFER_START(UnityPerMaterial)` holds `_SurfaceMap_ST, _BaseResidualMap_ST, _BaseColor,
_EmissionColor, _OcclusionStrength, _Cutoff, _Surface`. `InitializeNamerSurfaceData` decodes via
`NAMER_DECODE_SURFACE` then assembles SurfaceData. The D-06 roughness offset is applied inside
InitializeNamerSurfaceData (NOT in NAMER_DECODE_SURFACE) so the Meta-pass call site is untouched — add the
debug gates the same way (inside InitializeNamerSurfaceData only; leave NAMER_DECODE_SURFACE and the Meta pass alone).

NamerEditorWindow.cs symbols being removed: `DebugChannelLabels` (lines 26-30), `_debugChannel` (94),
`_debugMaterialFactory` (59), `_debugMaterial` (68), the toolbar block (856-869), the `_debugChannel == 0`
after-material branch (806-816), and the `_debugMaterialFactory.SetTextures/SetDebugBaseMap/SetChannel/
SetExtractedRoughness` + `_debugMaterial.SetFloat` block (422-438, 295-300). Keep `DecompStatLabels` and
`ResidualNotWrittenLabel`. The `NamerDebugChannelMaterial.cs` and `NamerDebugView.shader` files remain on disk
(no test references them) — do NOT edit or delete them.

Test conventions: NamerEditorWindowSmokeTests reflects static label arrays via
`typeof(NamerEditorWindow).GetField(..., BindingFlags.NonPublic | BindingFlags.Static)`.
NamerUIControlsTests snapshots/restores EditorPrefs keys in try/finally.
NamerOneTextureTests (lines 132-159) pins a shader property default via
`Shader.Find("GraffitiEntertainment.Namer/NAMER")` + `shader.FindPropertyIndex(...)` + `new Material(shader)` +
`material.GetFloat(...)` + `DestroyImmediate`.
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Add neutral-default shader gates to NamerSurface.hlsl</name>
  <files>Packages/com.graffitientertainment.namer/Shaders/NAMER.shader, Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl</files>
  <action>
Add five neutral-default float input gates plus one roughness-neutral float to the single NAMER surface
shader (per DIP-02 — no forked debug shader, no variant drift).

In `Shaders/NAMER.shader`, in the `Properties` block after the `_Cutoff("Alpha Cutoff", ...)` line and
before the `// Keyword-setting toggles` comment, add six hidden float properties following the existing
`[HideInInspector] __srcA("__srcA", Float) = 1.0` hidden-property convention (first token is the property
name the window writes via `Shader.PropertyToID`; the quoted `__`-prefixed display string keeps the default
inspector clean):

- `[HideInInspector] _DbgEnableResidual("__dbgEnableResidual", Float) = 1.0`
- `[HideInInspector] _DbgEnableRoughness("__dbgEnableRoughness", Float) = 1.0`
- `[HideInInspector] _DbgEnableAO("__dbgEnableAO", Float) = 1.0`
- `[HideInInspector] _DbgEnableMetallic("__dbgEnableMetallic", Float) = 1.0`
- `[HideInInspector] _DbgEnableEmissive("__dbgEnableEmissive", Float) = 1.0`
- `[HideInInspector] _DbgRoughnessNeutral("__dbgRoughnessNeutral", Float) = 0.5`

In `Shaders/NamerSurface.hlsl`, add the matching `half` variables to `CBUFFER_START(UnityPerMaterial)` (keep
the SRP-batcher layout stable — no ifdefs): `_DbgEnableResidual`, `_DbgEnableRoughness`, `_DbgEnableAO`,
`_DbgEnableMetallic`, `_DbgEnableEmissive`, `_DbgRoughnessNeutral`.

In `InitializeNamerSurfaceData`, apply the neutralization (all gates default 1.0, so the decode stays
byte-identical to today when nothing is toggled). Make ONLY these edits, all inside
`InitializeNamerSurfaceData` — do NOT touch `NAMER_DECODE_SURFACE` or the Meta pass:

1. Immediately after sampling `baseResidual`, gate the color but preserve alpha (AlphaDiscard reads `.a`):
   `baseResidual.rgb = lerp(half3(1.0, 1.0, 1.0), baseResidual.rgb, _DbgEnableResidual);`
2. After the D-06 roughness-offset line (`roughness = saturate(roughness + SAMPLE_TEXTURE2D(...).r);`), gate
   roughness then recompute smoothness:
   `roughness = lerp(_DbgRoughnessNeutral, roughness, _DbgEnableRoughness);` (the existing
   `smoothness = 1.0 - roughness;` on the next line already follows).
3. After `NAMER_DECODE_SURFACE` populates `ao`, gate it: `ao = lerp(1.0, ao, _DbgEnableAO);`.
4. Change `surfaceData.metallic = metallic ? 1.0 : 0.0;` to
   `surfaceData.metallic = (metallic ? 1.0 : 0.0) * _DbgEnableMetallic;`.
5. Inside the `#ifdef _EMISSION` branch, change the emission line to
   `surfaceData.emission = _EmissionColor.rgb * (emissive ? 1.0 : 0.0) * _DbgEnableEmissive;`
   (the `#else` branch stays `half3(0.0, 0.0, 0.0)`).
</action>
  <verify>
    <automated>test $(grep -o "_DbgEnable" Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl | wc -l | tr -d ' ') -ge 5 && test $(grep -o "_DbgRoughnessNeutral" Packages/com.graffitientertainment.namer/Shaders/NAMER.shader | wc -l | tr -d ' ') -ge 1 && echo SHADER_GATES_OK</automated>
  </verify>
  <done>NAMER.shader declares the 6 hidden float properties with 1.0/0.5 defaults; NamerSurface.hlsl neutralizes residual→white(1), AO→1, roughness→_DbgRoughnessNeutral, metallic→0, emissive→0 when the matching gate is 0, and is byte-identical when all gates are 1.</done>
</task>

<task type="auto">
  <name>Task 2: Persist step-gate settings and hard-gate the pipeline stages</name>
  <files>Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs, Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs</files>
  <action>
Add the four persisted step-gate bools (per DIP-01; VC+Residual reuses the existing
`DecompositionEnabled` — no duplicate control) and apply the hard gates in Process.

In `Editor/NamerEditorConstants.cs`, after `DefaultDecompositionEnabled`, add (all true — the gates are
opt-OUT, so the default pipeline is unchanged):

- `public const bool DefaultRoughnessStageEnabled = true;`
- `public const bool DefaultAoStageEnabled = true;`
- `public const bool DefaultMetallicContributionEnabled = true;`
- `public const bool DefaultEmissiveContributionEnabled = true;`

In `Editor/Settings/NamerProcessorSettings.cs`, add four private key consts and four public bool properties
following the EXACT existing EditorPrefs pattern (e.g. mirror `DecompositionEnabled`):
`RoughnessStageEnabled` (key "NamerProcessor.RoughnessStageEnabled"), `AoStageEnabled`
("NamerProcessor.AoStageEnabled"), `MetallicContributionEnabled` ("NamerProcessor.MetallicContributionEnabled"),
`EmissiveContributionEnabled` ("NamerProcessor.EmissiveContributionEnabled"), each defaulting to its
`NamerEditorConstants.Default*` constant.

In `Editor/Pipeline/NamerProcessor.cs` (~lines 133-138), change exactly two assignments to hard-gate the
stages WITHOUT modifying the slider settings (the gate is independent of the sliders):
- `inspection.RoughnessExtractStrength = settings.RoughnessStageEnabled ? settings.RoughnessExtractStrength : 0f;`
- `inspection.AoUnmultiplyStrength = settings.AoStageEnabled ? settings.AoUnmultiplyStrength : 0f;`
Leave `inspection.AoBlurRadius/AoStrength/AoContrast/DipSource` unchanged. Do NOT add any Metallic/Emissive
pipeline change — they are scalar passthrough into packed bits (NamerComputePipeline.cs lines 353-358) and
their checkboxes gate the SHADER only (Task 3).

CRITICAL: NamerProcessor.cs carries UNCOMMITTED hunks (the residual-resolution Debug.Log after
`generator.Generate`, ~+15 lines) that must ride along into this commit. Do NOT `git checkout`, revert, or
"clean" NamerProcessor.cs or ResidualPipelineTests.cs; make only the two targeted edits above. Do not touch
ResidualPipelineTests.cs at all.
</action>
  <verify>
    <automated>test $(grep -o "RoughnessStageEnabled" Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs | wc -l | tr -d ' ') -ge 2 && test $(grep -o "DefaultAoStageEnabled" Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs | wc -l | tr -d ' ') -ge 1 && test $(grep -o "settings.RoughnessStageEnabled\|settings.AoStageEnabled" Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs | wc -l | tr -d ' ') -ge 2 && echo STEP_GATES_OK</automated>
  </verify>
  <done>Four new settings bools persist via EditorPrefs with true defaults; NamerProcessor.Process skips roughness extraction when RoughnessStageEnabled is false and skips AO un-multiply when AoStageEnabled is false, without touching the strength sliders; the uncommitted residual-resolution Debug.Log hunk is preserved.</done>
</task>

<task type="auto">
  <name>Task 3: Wire the step row + shaded-view toggles into the window, fix the scrollbar, and add tests</name>
  <files>Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerEditorWindowSmokeTests.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs</files>
  <action>
Rewire NamerEditorWindow.cs and add tests (per DIP-01/DIP-02/DIP-03). Do this in order:

A. REMOVE the per-channel debug machinery:
- Delete `DebugChannelLabels` (lines 26-30) and the `/// <see cref="DebugChannelLabels"/>` mention in the
  `DecompStatLabels` doc comment (line 36). KEEP `DecompStatLabels` and `ResidualNotWrittenLabel`.
- Delete fields `_debugMaterialFactory` (59), `_debugMaterial` (68), `_debugChannel` (94).
- Delete `_debugMaterialFactory = new NamerDebugChannelMaterial();` in OnEnable (164).
- Delete `_debugChannel = 0;` in RebuildInspection (245).
- Delete the `_debugMaterial` destroy block in OnDisable (199-203).
- Delete the `_debugMaterial` creation block in EnsureMaterials (478-482).
- Delete the `_debugMaterialFactory.SetTextures/SetDebugBaseMap` block in ResolveGeneratedPreview (295-300).
- Delete the whole `_debugMaterialFactory.SetTextures/SetDebugBaseMap/SetChannel/SetExtractedRoughness` +
  `_debugMaterial.SetFloat(OcclusionStrengthId, ...)` block in RecomputePreview (422-438).
- Replace the after-material selection (806-816) with a single always-shaded selection (keep the null
  fallback): `Material afterMaterial = (_afterPanelState.PreferGenerated && _generatedMaterial != null) ? _generatedMaterial : _namerMaterial;` then keep the existing `if (afterMaterial == null) afterMaterial = beforeMaterial;`.
- Delete the `EditorGUI.BeginDisabledGroup(_busy); int newChannel = GUILayout.Toolbar(...); ... EndDisabledGroup();`
  toolbar block (856-869).
- Leave `NamerDebugChannelMaterial.cs` and `NamerDebugView.shader` untouched on disk.

B. ADD new window state + labels:
- Cached IDs next to the existing `SurfaceMapId` statics:
  `DbgEnableResidualId = Shader.PropertyToID("_DbgEnableResidual")`, and the same for
  `_DbgEnableRoughness`, `_DbgEnableAO`, `_DbgEnableMetallic`, `_DbgEnableEmissive`, `_DbgRoughnessNeutral`.
- `internal static readonly string[] ShaderInputToggleLabels = { "Residual", "Roughness", "AO", "Metallic", "Emissive" };`
  (replaces the removed `DebugChannelLabels` as the reflection-pinned label array).
- Fields: `_roughnessStageEnabled`, `_aoStageEnabled`, `_metallicContributionEnabled`,
  `_emissiveContributionEnabled` (persisted, loaded from settings in RebuildInspection), and
  `_dbgResidualEnabled`, `_dbgRoughnessEnabled`, `_dbgAoEnabled`, `_dbgMetallicEnabled`, `_dbgEmissiveEnabled`
  (all default true — neutral; NOT persisted).

C. Load the four step-gate bools in RebuildInspection next to the existing settings loads (e.g.
  `_roughnessStageEnabled = _settings.RoughnessStageEnabled;`).

D. Hard-gate the preview stages in RecomputePreview exactly like Process (replace the two existing lines):
  `inspection.RoughnessExtractStrength = _roughnessStageEnabled ? _roughnessExtractStrength : 0f;` and
  `inspection.AoUnmultiplyStrength = _aoStageEnabled ? _aoUnmultiplyStrength : 0f;`. After the
  `_namerMaterial` property assignments, call `ApplyDebugGates();` so a freshly created preview material
  receives the current toggles.

E. Add `private void ApplyDebugGates()`: for each of `_namerMaterial` and `_generatedMaterial` (when non-null),
  SetFloat:
  - `_DbgEnableResidual`  = `_dbgResidualEnabled ? 1f : 0f`
  - `_DbgEnableRoughness` = `_dbgRoughnessEnabled ? 1f : 0f`
  - `_DbgEnableAO`        = `_dbgAoEnabled ? 1f : 0f`
  - `_DbgEnableMetallic`  = `(_metallicContributionEnabled && _dbgMetallicEnabled) ? 1f : 0f`
  - `_DbgEnableEmissive`  = `(_emissiveContributionEnabled && _dbgEmissiveEnabled) ? 1f : 0f`
  - `_DbgRoughnessNeutral` = `PrimaryInspection != null ? PrimaryInspection.Roughness : 0.5f`
  Do NOT call EditorUtility.SetDirty — these are transient preview toggles; the on-disk generated .mat keeps
  its neutral 1.0 defaults. Call `ApplyDebugGates()` from the end of RecomputePreview, from
  ResolveGeneratedPreview (after `_generatedMaterial` is loaded), and from each checkbox change handler.

F. Draw the step row: add `private void DrawStepSwitches()` and call it in OnGUI inside the scroll view,
  immediately after `EditorGUILayout.BeginScrollView(_scrollPosition);` and BEFORE `DrawSourceSection()`
  (i.e., above the foldouts). One `EditorGUILayout.BeginHorizontal()` of 5 toggles, disabled when `_busy`
  (`EditorGUI.BeginDisabledGroup(_busy)`), each with a tooltip explaining it is a hard stage gate independent
  of the strength sliders. Handlers:
  - "VC + Residual" -> `_decompositionEnabled` (write `_settings.DecompositionEnabled`, `_afterPanelState.MarkTweaking()`, `MarkDirty()`).
  - "Roughness" -> `_roughnessStageEnabled` (write `_settings.RoughnessStageEnabled`, `MarkDirty()`).
  - "AO" -> `_aoStageEnabled` (write `_settings.AoStageEnabled`, `MarkDirty()`).
  - "Metallic" -> `_metallicContributionEnabled` (write `_settings.MetallicContributionEnabled`, then `ApplyDebugGates()` + `Repaint()` — shader-only, no recompute).
  - "Emissive" -> `_emissiveContributionEnabled` (write `_settings.EmissiveContributionEnabled`, then `ApplyDebugGates()` + `Repaint()`).
  The Decomposition foldout keeps its existing full Toggle for the same `DecompositionEnabled` setting (they
  stay in sync through `_decompositionEnabled`/`_settings`).

G. Draw the shaded-view toggle row: in DrawPreviewSection, replace the deleted toolbar block with a
  `EditorGUILayout.BeginHorizontal()` of 5 `EditorGUILayout.Toggle` bound to the 5 `_dbg*` fields using
  `ShaderInputToggleLabels`, disabled when `_busy`. On any change: assign the field, call `ApplyDebugGates()`,
  `Repaint()` (no MarkDirty — shader gates need no GPU recompute). Add a one-line mini-label/tooltip before or
  after the row explaining these toggles gate inputs in the full shaded After view. `EditorGUILayout.EndHorizontal()`.

H. Scrollbar fix (line 801): change to
  `float previewWidth = Mathf.Max(EditorGUIUtility.currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth, 256f);`
  (keep the 256f min guard — DIP-03).

I. Tests:
- In `Tests/Editor/NamerEditorWindowSmokeTests.cs`: DELETE `DebugChannelLabels_AppendsExtractedRoughnessAtIndex10`
  (the array is gone) and replace it with `ShaderInputToggleLabels_AreFiveNeutralToggles()` that reflects
  `ShaderInputToggleLabels` and asserts `{ "Residual", "Roughness", "AO", "Metallic", "Emissive" }`. Keep
  `FoldoutDefaults_SourceOpenOthersCollapsed` untouched.
- Create `Tests/Editor/NamerDipSwitchTests.cs` (EditMode asmdef; mirror NamerUIControlsTests prefs
  snapshot/restore and NamerOneTextureTests shader-default patterns) with:
  - `StepGates_PersistViaEditorPrefs` — snapshot the 4 keys, write all 4 step-gate settings false through one
    instance, assert a NEW instance reads them back false, restore in finally.
  - `StepGates_FreshPrefsDefaults` — delete the 4 keys, assert all default true, restore in finally.
  - `ShaderDebugGates_DefaultNeutral` — `Shader.Find("GraffitiEntertainment.Namer/NAMER")` non-null;
    `shader.FindPropertyIndex("_DbgEnableResidual") >= 0` for all 5 gates plus `_DbgRoughnessNeutral`;
    `new Material(shader)` then `GetFloat` == 1.0f for the 5 gates and 0.5f for `_DbgRoughnessNeutral`;
    `DestroyImmediate(material)` in finally.
  Create the matching `.meta` file; if Unity is not running, `git add -f` the .cs and .meta under Packages/
  (see the Packages/ git quirk in STATE.md).

Run the touched fixtures in the LIVE editor (per project memory: TestRunnerApi via unity-mcp RunCommand,
marker file, no AssetDatabase.Refresh): `NamerDipSwitchTests` and `NamerEditorWindowSmokeTests`.
</action>
  <verify>
    <automated>test $(grep -o "ApplyDebugGates" Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | wc -l | tr -d ' ') -ge 3 && test $(grep -o "ShaderInputToggleLabels" Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | wc -l | tr -d ' ') -ge 1 && grep -n "currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth" Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs | grep -q . && grep -q "ShaderDebugGates_DefaultNeutral" Packages/com.graffitientertainment.namer/Tests/Editor/NamerDipSwitchTests.cs && echo WINDOW_GATES_OK</automated>
  </verify>
  <done>The window shows a step dip-switch row above the foldouts (Roughness/AO/VC+Residual hard-gate the pipeline, Metallic/Emissive gate the shader), the per-channel toolbar is replaced by the shaded-view input toggle row, toggles write the `_DbgEnable*` floats onto the preview and generated After materials, the preview width subtracts the vertical scrollbar width (no permanent horizontal scrollbar), and the new tests pass in the live editor.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| editor UI -> material shader floats | Debug toggle values written by the editor window onto preview/generated materials; editor-authoritative, no untrusted input |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-quick-01 | Tampering | `_DbgEnable*` material floats | accept | Editor-only debug instrument; values are editor-authored booleans (0/1) with neutral 1.0 defaults, no external/PII input, no persistence to disk of the toggles. |
| T-quick-SC | Tampering | npm/pip/cargo installs | N/A | No package-manager installs in this plan. |
</threat_model>

<verification>
- `_DbgEnable` appears >= 5 times in Shaders/NamerSurface.hlsl (declared + applied) and `_DbgRoughnessNeutral` is present in NAMER.shader.
- `RoughnessStageEnabled` appears >= 2 times in NamerProcessorSettings.cs and the Process gating greps pass.
- `NamerEditorWindow.cs` shows `currentViewWidth - GUI.skin.verticalScrollbar.fixedWidth` (scrollbar fix).
- Live-editor EditMode run: `NamerDipSwitchTests` and `NamerEditorWindowSmokeTests` pass (marker file, no Refresh).
- The uncommitted NamerProcessor.cs residual-resolution Debug.Log hunk and ResidualPipelineTests.cs additions remain (git diff still shows them as modifications, not reverted).
</verification>

<success_criteria>
- The five `_DbgEnable*` gates default to 1.0 and neutralize residual→white, AO→1, roughness→`_DbgRoughnessNeutral`, metallic→0, emissive→0 when off.
- Four step-gate bools persist via EditorPrefs with true defaults; Process and the preview skip roughness extraction / AO un-multiply when their gates are unchecked.
- The window shows the step row above the foldouts and the shaded-view toggle row in place of the toolbar; the preview rect subtracts the vertical scrollbar width.
- All new/touched tests pass in the live editor and the plan's uncommitted-hunk preservation holds.
</success_criteria>

<output>
Create `.planning/quick/260918-fcx-namer-debug-dip-switches/260918-fcx-SUMMARY.md` when done
</output>

---
phase: quick-isy-bind-generated-namer-materials
plan: 01
type: execute
wave: 1
depends_on: []
files_modified:
  - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerRendererBindingTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/NamerAfterPanelStateTests.cs
autonomous: true
requirements: [UI-02, UI-03, GEN-01]

must_haves:
  truths:
    - "Running Process on a scene object swaps each renderer sub-mesh slot's source material for its index-aligned generated material, leaving source assets untouched"
    - "The After panel shows the generated .mat when it exists, flips to the live in-memory preview on any AO slider/occluder change, and flips back after a successful Process"
    - "Debug channels sample the generated _Surface.png/_Base.png textures in generated mode, and the live _liveResult textures in live mode"
  artifacts:
    - path: "Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs"
      provides: "renderer binding after generation"
      contains: "BindGeneratedMaterials"
    - path: "Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs"
      provides: "shared generated-path composition"
      contains: "ComposeDestinationFolder"
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs"
      provides: "generated/live flip state"
      contains: "class NamerAfterPanelState"
    - path: "Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs"
      provides: "generated-material detection + debug texture binding"
      contains: "ResolveGeneratedPreview"
  key_links:
    - from: "NamerProcessor.cs"
      to: "AssetGenerator.ComposeDestinationFolder"
      via: "destination folder composition"
      pattern: "ComposeDestinationFolder"
    - from: "NamerEditorWindow.cs"
      to: "AssetGenerator.ComposePath"
      via: "generated material/texture path resolution"
      pattern: "ComposePath"
    - from: "NamerEditorWindow.cs"
      to: "NamerDebugChannelMaterial.SetTextures"
      via: "generated vs live debug texture binding"
      pattern: "SetTextures"
---

<objective>
Bind generated NAMER materials to scene renderers on process, and make the After
panel + debug views prefer the generated material/textures when they exist — flipping
to the live in-memory preview the moment the user tweaks an AO slider or the occluder.

Purpose: Close the last mile of the "Process with NAMER" workflow — today the processor
writes generated assets under `NAMERGenerated/` but the scene keeps rendering the source
materials, and the window always shows the live preview even after a successful run.
Output: `NamerProcessor` binds per-sub-mesh-slot; the window gains a generated/live flip
state machine and generated-texture debug binding; two EditMode test files prove the
binding and the flip logic.
</objective>

<execution_context>
@$HOME/.claude/get-shit-done/workflows/execute-plan.md
@$HOME/.claude/get-shit-done/templates/summary.md
</execution_context>

<context>
@.planning/PROJECT.md
@.planning/ROADMAP.md
@.planning/STATE.md

# The three touched source files (read these first — the plan references their exact symbols):
@Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
@Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
@Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
@Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs
@Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs

# Existing test conventions to mirror (fixture helpers, prefs snapshot, compute gate):
@Packages/com.graffitientertainment.namer/Tests/Editor/SourceImmutabilityTests.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/AssetGeneratorTests.cs
@Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOControlsTests.cs

<interfaces>
<!-- Key contracts the executor builds against. Extracted from the current codebase. -->

From AssetGenerator.cs (private today; becomes public in Task 1):
```csharp
private static string ComposePath(NamerMaterialInspection inspection, NamerProcessorSettings settings, string destinationFolder, string extension)
// returns destinationFolder + settings.Prefix + SanitizeFileName(inspection.Material.name) + settings.Suffix + extension
public static string SanitizeFileName(string name)
```

From NamerProcessor.Process (the index-alignment contract):
```csharp
// model.Materials[i].Material (source) is index-aligned with result.GeneratedAssets[i].MaterialPath
// one Generate() call per material, appended in order
public sealed class NamerGeneratedAsset { public string MaterialPath; public string BaseTexturePath; public string SurfaceTexturePath; }
public sealed class NamerProcessResult { public List<NamerGeneratedAsset> GeneratedAssets; public List<string> Warnings; public string Error; }
public sealed class NamerSourceModel { public List<NamerMaterialInspection> Materials; public List<string> Warnings; }
public sealed class NamerMaterialInspection { public Material Material; /* ... */ }
```

From NamerDebugChannelMaterial.cs:
```csharp
public Material Create()
public void SetTextures(Material material, Texture surface, Texture baseResidual)  // _SurfaceMap, _BaseResidualMap
public void SetChannel(Material material, int channel)
```

From NamerEditorWindow.cs (fields to extend):
```csharp
private Material _namerMaterial;   // live in-memory NAMER material (HideAndDontSave)
private Material _debugMaterial;   // debug channel material (HideAndDontSave)
private NamerComputeResult _liveResult;   // live pooled result
private NamerProcessorSettings _settings;
private NamerSourceModel _model;
private NamerMaterialInspection PrimaryInspection { get; }  // _model.Materials[0]
private void RebuildInspection();   // selection-change path
private void RecomputePreview();    // debounced live recompute + texture binding
private void RunProcess();          // Process button
private void DrawPreviewSection(NamerMaterialInspection inspection);
private void DrawProcessingSection(NamerMaterialInspection inspection);
```
</interfaces>
</context>

<tasks>

<task type="auto">
  <name>Task 1: Expose shared path helpers and bind generated materials to scene renderers</name>
  <files>Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs</files>
  <action>
  In `AssetGenerator.cs`:
  1. Change the existing `private static string ComposePath(...)` to `public static string ComposePath(...)`. Keep its XML doc (it already describes D-02). Do not rename it — callers in `Generate`/`PreflightTargets` keep working.
  2. Add a new public static method `ComposeDestinationFolder` that returns `destination.TrimEnd('/', '\\') + "/" + SanitizeFileName(selectionName) + "/"` with an XML doc stating it is shared by `NamerProcessor` and the processor window so both agree on where generated assets live. Reuse `SanitizeFileName` (do not duplicate its logic).

  In `NamerProcessor.cs`:
  3. Replace the inline destination-folder computation in `Process` (the two lines building `destinationFolder` from `destination` + `selection.name`) with a single call `AssetGenerator.ComposeDestinationFolder(destination, selection.name)`. Behavior is identical — this is the DRY seam, not a behavior change.
  4. After the `try`/`finally` block completes (the pipeline is disposed) and guarded by `if (string.IsNullOrEmpty(result.Error))`, call a new `private static void BindGeneratedMaterials(UnityEngine.Object selection, NamerSourceModel model, NamerProcessResult result)`.
  5. Implement `BindGeneratedMaterials` with exactly this algorithm:
     - Return immediately when `selection` is not a `GameObject` (material/folder/model selections have no scene renderer) OR when `AssetDatabase.GetAssetPath(gameObject)` is non-empty (a prefab/model asset selected in the Project has no live scene renderer — matching the "model-asset selections are a no-op" lock and the existing empty-path = scene-object convention in `ResolvePreviewMesh`).
     - Build `Dictionary<int, Material> generatedBySourceId`; for each index `i` over `model.Materials` and `result.GeneratedAssets` (bounded by `Math.Min` of their counts), load `AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[i].MaterialPath)` and map `model.Materials[i].Material.GetInstanceID()` to it when both source and generated are non-null. This is the index-aligned source→generated contract.
     - For each `Renderer renderer` in `gameObject.GetComponentsInChildren<Renderer>(true)`: read `Material[] shared = renderer.sharedMaterials;` (a copy), iterate each slot, and when a slot is non-null and its `GetInstanceID()` is in the map, replace it with the generated material and set a `changed` flag. Assign `renderer.sharedMaterials = shared` only when `changed` is true. This is per-slot multi-material mapping.
     - Never write to, import, or otherwise mutate any source material, texture, importer, FBX, or `.meta` — the only mutation is the live scene renderer's material array. Do not load or modify `model.Materials[i].Material` itself.
  Follow project conventions: explicit braces on every `if`/`for` (even single statements), XML doc on the public `ComposeDestinationFolder`, no magic numbers.
  </action>
  <verify>
    <automated>grep -n "public static string ComposePath\|public static string ComposeDestinationFolder" Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs (both must be present)</automated>
    <automated>grep -n "BindGeneratedMaterials" Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs (must show one call site and one method declaration)</automated>
  </verify>
  <done>ComposePath is public; ComposeDestinationFolder exists and Process uses it; BindGeneratedMaterials swaps per-slot scene renderer materials only after a successful run and never touches source assets. Full behavioral proof lands in Task 3.</done>
</task>

<task type="auto">
  <name>Task 2: After-panel generated/live flip state + generated-texture debug binding</name>
  <files>Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs, Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs</files>
  <action>
  Create new file `Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs` (namespace `GraffitiEntertainment.Namer.Editor`) with a `public sealed class NamerAfterPanelState` containing exactly:
    - `public bool GeneratedAvailable { get; set; }` — true when a generated material exists for the selection.
    - `public bool Tweaking { get; private set; }` — true after an AO slider/occluder change until the next successful Process.
    - `public bool PreferGenerated => GeneratedAvailable && !Tweaking;` — the After panel should show the generated material.
    - `public void MarkTweaking()` setting `Tweaking = true`.
    - `public void Reset()` setting `Tweaking = false`.
    Add an XML doc explaining the generated/live flip contract (piece 2).

  In `NamerEditorWindow.cs`:
  1. Add three private fields near the existing material fields: `NamerAfterPanelState _afterPanelState = new NamerAfterPanelState();`, `Material _generatedMaterial;`, `Texture2D _generatedSurface;`, `Texture2D _generatedBase;`.
  2. Add a private method `ResolveGeneratedPreview()` that, on every call:
     - calls `_afterPanelState.Reset()` (start not-tweaking), then nulls `_generatedMaterial`/`_generatedSurface`/`_generatedBase`;
     - returns early when `PrimaryInspection` is null or `_selection` is null;
     - computes `string folder = AssetGenerator.ComposeDestinationFolder(_settings.Destination, _selection.name);` then loads `AssetDatabase.LoadAssetAtPath<Material>(AssetGenerator.ComposePath(inspection, _settings, folder, ".mat"))` into `_generatedMaterial`, and loads the `_Surface.png`/`_Base.png` textures (same `ComposePath` with those extensions) into `_generatedSurface`/`_generatedBase`;
     - if `_generatedMaterial` is null, returns (generated mode unavailable);
     - sets `_afterPanelState.GeneratedAvailable = true`;
     - if `_debugMaterial != null`, binds the generated textures now via `_debugMaterialFactory.SetTextures(_debugMaterial, _generatedSurface, _generatedBase)`.
     The loaded generated material/textures are persistent assets from `AssetDatabase` — never `DestroyImmediate` them anywhere (including `OnDisable`).
  3. Call `ResolveGeneratedPreview()` in `RebuildInspection()` immediately before its final `MarkDirty()` (after `_previewMesh`/`PrimaryInspection` are set), and in `RunProcess()` inside the success path (when `string.IsNullOrEmpty(result.Error)`) right after the status is set, so a successful Process flips back to generated.
  4. In `DrawProcessingSection`, inside each of the four AO-slider `if` blocks and the occluder `ObjectField` change `if` block, add `_afterPanelState.MarkTweaking();` alongside the existing `MarkDirty();` (flip to live on any tweak).
  5. In `DrawPreviewSection`, replace the single-line `afterMaterial` assignment with: when `_debugChannel == 0`, use `(_afterPanelState.PreferGenerated && _generatedMaterial != null) ? _generatedMaterial : _namerMaterial`; otherwise use `_debugMaterial`. Keep the existing null-fallback to `beforeMaterial`.
  6. In `RecomputePreview`, replace the single `_debugMaterialFactory.SetTextures(_debugMaterial, _liveResult.PackedSurface, previewBaseMap);` line with a branch: when `_afterPanelState.PreferGenerated && _generatedSurface != null && _generatedBase != null`, bind `_generatedSurface`/`_generatedBase`; otherwise bind `_liveResult.PackedSurface`/`previewBaseMap` (today's live behavior). Leave the `_namerMaterial` live binding and the rest of `RecomputePreview` unchanged.
  Do not add any new UI buttons, do not change `NamerDebugChannelMaterial`, do not modify the bake path.
  </action>
  <verify>
    <automated>grep -n "class NamerAfterPanelState\|PreferGenerated\|MarkTweaking\|ResolveGeneratedPreview" Packages/com.graffitientertainment.namer/Editor/UI/NamerAfterPanelState.cs Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs (all must be present)</automated>
  </verify>
  <done>The window holds a `NamerAfterPanelState`, resolves the generated material/textures on selection change and process success, flips to live on any AO/occluder tweak, and binds debug channels to generated textures in generated mode. Behavioral proof lands in Task 3.</done>
</task>

<task type="auto">
  <name>Task 3: EditMode tests for renderer binding and after-panel flip logic</name>
  <files>Packages/com.graffitientertainment.namer/Tests/Editor/NamerRendererBindingTests.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAfterPanelStateTests.cs</files>
  <action>
  Create two test files in namespace `GraffitiEntertainment.Namer.Tests` (mirror the `using` set and conventions of `SourceImmutabilityTests`/`NamerAOControlsTests`).

  File A — `NamerRendererBindingTests.cs`:
    - `private const string TempFolder = "Assets/NAMER_Tests_Temp";` and the `ComputeAvailable => SystemInfo.supportsComputeShaders && SystemInfo.supportsAsyncGPUReadback;` gate.
    - One `[UnityTest]` `Process_BindsGeneratedMaterialsToSceneRenderersPerSlot()`: `Assert.Ignore` when `!ComputeAvailable` (D-15); build two URP Lit source materials (each with a distinct 1x1 base-map PNG, via a local `CreateSourceMaterial` helper copied from `SourceImmutabilityTests`), create a plain scene `GameObject` (NOT saved as a prefab — it must stay a scene object so `AssetDatabase.GetAssetPath` is empty), add a `MeshRenderer`, assign `renderer.sharedMaterials = new[] { matA, matB }`, run `NamerProcessor.Process(gameObject, settings)` with `Destination = TempFolder + "/Out"` and default prefix/suffix, then assert:
        - `result.Error` is null and `result.GeneratedAssets.Count == 2`;
        - `renderer.sharedMaterials[0]` equals `AssetDatabase.LoadAssetAtPath<Material>(result.GeneratedAssets[0].MaterialPath)` and `[1]` equals the index-1 generated material (index-aligned, per-slot), and both differ from `matA`/`matB` (the swap happened);
        - source assets are unmodified: `matA` and `matB` still load from their original `AssetDatabase.GetAssetPath` and their `shader.name == "Universal Render Pipeline/Lit"` (binding never overwrote the source).
      `finally`: `Object.DestroyImmediate(gameObject)`, `AssetDatabase.DeleteAsset(TempFolder)`, restore the `NamerProcessor.*` EditorPrefs snapshot (copy the `PrefsSnapshot` helper from `SourceImmutabilityTests`). End with `yield return null;`.

  File B — `NamerAfterPanelStateTests.cs`:
    - `[Test]` `AfterPanelState_FlipsBetweenGeneratedAndLive()`: construct `NamerAfterPanelState`, set `GeneratedAvailable = true`, `Reset()`, assert `PreferGenerated` is true (generated mode when assets exist); call `MarkTweaking()`, assert `PreferGenerated` false (flip to live on slider change); call `Reset()`, assert true (flip back after process); set `GeneratedAvailable = false`, assert `PreferGenerated` false (live until generated exists).
    - `[Test]` `GeneratedPaths_MatchProcessConvention()`: create a runtime `Material` named `MyMat` (HideAndDontSave) and `new NamerMaterialInspection { Material = thatMaterial }`; snapshot/restore the `NamerProcessor.Prefix`/`Suffix` keys, set `Prefix = "P_"`, `Suffix = "_N"`; assert `AssetGenerator.ComposeDestinationFolder("Assets/NAMERGenerated/", "My:Model/01") == "Assets/NAMERGenerated/MyModel01/"` (sanitizer strips `:` and `/`); assert `ComposePath(inspection, settings, folder, ".mat") == "Assets/NAMERGenerated/MyModel01/P_MyMat_N.mat"` and the `_Surface.png`/`_Base.png` paths match the same convention. `finally` destroy the material and restore prefs. This proves the window's detection uses the same folder/file convention `NamerProcessor.Process` writes (D-16).
  Every GPU-path test is `[UnityTest]` with the compute gate; pure-logic tests are `[Test]`. No `std::this_thread`-style sleeps; follow the wait-condition/hand-rolled determinism conventions of the existing suite.
  </action>
  <verify>
    <automated>Unity -batchmode -projectPath /Users/Shared/SSDevelopment/Development/Namer-Unity -runTests -testPlatform EditMode -testFilter GraffitiEntertainment.Namer.Tests.NamerAfterPanelStateTests -testResults Logs/namer-afterpanel.xml (no -quit; see STATE.md — the test framework controls exit)</automated>
    <automated>Unity -batchmode -projectPath /Users/Shared/SSDevelopment/Development/Namer-Unity -runTests -testPlatform EditMode -testFilter GraffitiEntertainment.Namer.Tests.NamerRendererBindingTests -testResults Logs/namer-binding.xml (requires a GPU — run in-editor if batchmode is -nographics, per PITFALLS.md)</automated>
  </verify>
  <done>Both test classes compile and pass: renderer binding is proven per-slot with source immutability, and the generated/live flip + path-convention are proven deterministically.</done>
</task>

</tasks>

<threat_model>
## Trust Boundaries

| Boundary | Description |
|----------|-------------|
| Operator → editor tooling | Local, trusted Unity editor operator driving `Process with NAMER` and the window; no network or external input. |
| Generated path composition | `ComposePath`/`ComposeDestinationFolder` feed `AssetDatabase.LoadAssetAtPath` in the window; path-confinement for writes is already enforced by `AssetGenerator.ValidateDestinationFolder`/`ValidateComposedPath` (T-03-01) and is unchanged. |

## STRIDE Threat Register

| Threat ID | Category | Component | Disposition | Mitigation Plan |
|-----------|----------|-----------|-------------|-----------------|
| T-quick-01 | Tampering | `BindGeneratedMaterials` (NamerProcessor) | accept | Scene-only, operator-trusted, index-aligned swap; no source asset is written — no new input surface beyond the already-validated Process selection. |
| T-quick-02 | Spoofing/Information disclosure | `ResolveGeneratedPreview` (NamerEditorWindow) | accept | Reads only via `AssetDatabase.LoadAssetAtPath` on composed paths under the configured destination; no secrets, no cross-asset writes. |
| T-quick-SC | Tampering | npm/pip/cargo installs | accept | No new packages added; no supply-chain surface introduced by this plan. |
</threat_model>

<verification>
1. `grep -n "ComposeDestinationFolder"` appears in both `NamerProcessor.cs` and `AssetGenerator.cs` (single source of truth for the folder convention).
2. `grep -n "MarkTweaking"` appears in all four AO-slider change blocks and the occluder change block in `NamerEditorWindow.cs`.
3. Task 3's EditMode tests pass for `NamerAfterPanelStateTests` and (on a GPU) `NamerRendererBindingTests`.
4. `grep -rn "DestroyImmediate(_generated"` returns nothing — generated materials/textures are persistent assets and are never destroyed.
</verification>

<success_criteria>
1. `Process` on a two-material scene object swaps both renderer slots to their index-aligned generated materials, and the source material assets are byte-unchanged.
2. The After panel shows the generated `.mat` when present, flips to the live preview on any AO slider/occluder tweak, and flips back after the next successful Process.
3. Debug channels read the generated `_Surface.png`/`_Base.png` in generated mode and `_liveResult` in live mode.
4. `NamerAfterPanelStateTests` and `NamerRendererBindingTests` pass via the project's EditMode batchmode convention.
</success_criteria>

<output>
Create `.planning/quick/260829-isy-bind-generated-namer-materials-to-render/260829-isy-SUMMARY.md` when done.

Note: new `.cs` files (`NamerAfterPanelState.cs`, the two test files) get a `.meta` on next Unity import; if committing headless, add matching `.meta` files (`fileFormatVersion: 2` + a fresh 32-hex `guid`) so the UPM package stays import-consistent.
</output>

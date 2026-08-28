---
phase: 03-asset-generation-editor-workflow-preview
reviewed: 2026-08-27T23:48:52Z
depth: standard
files_reviewed: 12
files_reviewed_list:
  - Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs
  - Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs
  - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
  - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerDebugChannelMaterial.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
  - Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs
  - Packages/com.graffitientertainment.namer/README.md
  - Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader
  - Packages/com.graffitientertainment.namer/Tests/Editor/AssetGeneratorTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/SourceImmutabilityTests.cs
  - Packages/com.graffitientertainment.namer/package.json
findings:
  critical: 2
  warning: 6
  info: 7
  total: 15
status: issues_found
---

# Phase 3: Code Review Report

**Reviewed:** 2026-08-27T23:48:52Z
**Depth:** standard
**Files Reviewed:** 12
**Status:** issues_found

## Summary

Reviewed all 12 changed files plus the load-bearing cross-file contracts they depend on (`NamerComputePipeline`, `NamerSourceModel`, `SourceInspector`, `NamerFormat`, `NAMER.shader`, `NamerSurface.hlsl`). The core safety architecture is genuinely good: AssetGenerator is the sole disk writer, destination confinement exists, the overwrite gate is label-based, and the SHA-256 immutability test is a real content-hash test. However, two blockers exist: (1) the confinement and overwrite-gate model is defeated by unsanitized prefix/suffix settings, which allow the composed write path to escape the destination folder and bypass the label gate entirely; (2) the debug-channel toolbar never applies the selected channel to the debug material, so the feature visibly does not work. Six further warnings cover a UI lockout on unexpected exceptions, preview/output AO divergence, non-atomic multi-file writes, a Windows path-separator defect in folder creation, transparent materials rendered in the geometry queue, and a use-after-unload for embedded prefab meshes.

## Narrative Findings (AI reviewer)

## Critical Issues

### CR-01: Unsanitized prefix/suffix defeat destination confinement and the overwrite gate (path traversal)

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:59-64` (compose), `:325-340` (gate), `:193`/`:219` (writes)
**Issue:** `SanitizeFileName` is applied only to the material name. `settings.Prefix` and `settings.Suffix` (free-text `EditorPrefs`-persisted values typed into the window at `NamerEditorWindow.cs:465-475`) are concatenated raw into the write path:

```csharp
string basePath = destinationFolder + prefix + safeName + suffix + "_Base.png";
```

`ValidateDestinationFolder` (`AssetGenerator.cs:166-182`) validates only `destinationFolder`, never the composed final path. A prefix of `"../"` produces `Assets/NAMERGenerated/{src}/../Foo_Base.png` (writes into the parent of the destination); enough `../` segments (or an absolute-ish prefix) writes outside `Assets/` entirely, violating the T-03-01 confinement contract this phase built and the project hard rule that generated output stays confined. Worse, the overwrite gate is bypassed on any escaping path: `EnsureWritableTarget` uses `AssetDatabase.LoadAssetAtPath`, which returns `null` for paths outside the imported asset store, so the "unstamped asset" check is skipped and `File.WriteAllBytes` runs ungated — an arbitrary-file overwrite with no NamerGenerated check possible. (The gate is also bypassed for on-disk-but-unimported files inside Assets — see IN-03.)
**Fix:** Sanitize prefix/suffix through the same sanitizer, and re-validate the composed paths before any write:

```csharp
string safePrefix = SanitizeFileName(settings.Prefix);
string safeSuffix = SanitizeFileName(settings.Suffix);
string basePath = destinationFolder + safePrefix + safeName + safeSuffix + "_Base.png";
// Defense in depth: re-check the composed path, not just the folder.
string assetsRoot = Path.GetFullPath(Application.dataPath);
string composedRoot = Path.GetFullPath(Path.GetDirectoryName(basePath));
if (!composedRoot.StartsWith(assetsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("Generated path escapes the project Assets/ folder: " + basePath);
}
```

### CR-02: Debug-channel toolbar selection never reaches the debug material

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:401-409` (toolbar), `:232-233` (only `SetChannel` call site)
**Issue:** When the user clicks a channel in the toolbar, the handler only does `_debugChannel = newChannel; Repaint();`. `_debugMaterialFactory.SetChannel(...)` is called exclusively inside `RecomputePreview` (line 233), which runs only on selection change or after an AO-slider debounce. So after the initial recompute (which sets `_DebugChannel = 0` = Base Color), clicking "AO", "Normal", "Roughness", "Metallic", or "Emissive" swaps the material but leaves the stale uniform — the preview keeps showing Base Color under every toolbar label until an unrelated recompute happens. The phase's debug-channel toolbar deliverable is functionally broken.
**Fix:** Apply the channel immediately on toolbar change:

```csharp
int newChannel = GUILayout.Toolbar(_debugChannel, DebugChannelLabels);
if (newChannel != _debugChannel)
{
    _debugChannel = newChannel;
    if (_debugMaterial != null)
    {
        _debugMaterialFactory.SetChannel(_debugMaterial, Mathf.Max(0, _debugChannel - 1));
    }
    Repaint();
}
```

## Warnings

### WR-01: No exception safety around `_busy` — any non-`InvalidOperationException` permanently disables the window

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:509-533`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:100,111`
**Issue:** `RunProcess` sets `_busy = true` and only resets it on the success path. `NamerProcessor.Process` catches only `InvalidOperationException`; any other exception (e.g., `IOException`/`UnauthorizedAccessException` from `File.WriteAllBytes` at `AssetGenerator.cs:193/219` — disk full, folder deleted mid-batch, file locked by a VCS/AV) propagates through `RunProcess`, so `_busy = false` never executes. `_busy` disables the Process button AND the Destination/Prefix/Suffix/Overwrite fields (`NamerEditorWindow.cs:457`), locking the whole window until it is closed and reopened. The two context-menu entry points (`ProcessSelectedAsset`/`ProcessSelectedGameObject`, lines 67-83) have no exception handling at all.
**Fix:** Wrap the body of `RunProcess` in `try { ... } catch (Exception ex) { _status = "Processing failed: " + ex.Message; _statusIsError = true; } finally { _busy = false; Repaint(); }`.

### WR-02: Previewed AO un-multiply strength is silently discarded by Process — output does not match the preview

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:214`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:71`
**Issue:** The window mutates `inspection.AoUnmultiplyStrength = _aoStrength` on its cached `_model` for the live preview. But `RunProcess` calls `NamerProcessor.Process(_selection, _settings)`, which calls `SourceInspector.Inspect(selection)` fresh — `AoUnmultiplyStrength` resets to the `1f` default (`SourceInspector.cs:222`) and there is no parameter on `NamerProcessorSettings` to carry the value. An artist who previews at AO 0.3 and presses Process gets an AO 1.0 output; the advertised tweak-then-process workflow does not survive the round trip. `NamerSourceModel.cs:17` even documents "Phase 3 UI overrides it," but the override never reaches generation.
**Fix:** Thread the value through: add `float AoUnmultiplyStrength` to `NamerProcessorSettings` (persisted like the others) and have `NamerProcessor.Process` assign it to each inspection before `pipeline.Process(inspection)`; the window writes the setting alongside `_aoStrength`.

### WR-03: Multi-file generation is non-atomic — mid-batch failure leaves a partial stamped asset set

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:112-117`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:92-109`
**Issue:** `Generate` writes surface, then base, then material, with no rollback. If `WriteBaseTexture` or `WriteMaterial` throws (e.g., overwrite refusal on the base path while the surface path was stamped, or missing shader), the surface PNG has already been overwritten on disk and remains stamped. Similarly, `NamerProcessor.Process` aborts mid-loop on the first material error, leaving earlier materials of the batch generated while reporting a blocking `Error`. This contradicts the class contract "never writes garbage to disk" (`AssetGenerator.cs:33`) and leaves the user with an inconsistent set that a re-run may or may not repair depending on the label state.
**Fix:** At minimum, validate all three target paths (a pre-flight `EnsureWritableTarget` for surface, base, AND material before the first `File.WriteAllBytes`) and all per-material targets for the whole batch before writing anything; that converts the common refusal cases into clean no-op failures. Document the remaining crash window.

### WR-04: `EnsureFolder` feeds OS-separator paths into AssetDatabase — folder creation can fail on Windows editors

**File:** `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:153-161`
**Issue:** `Path.GetDirectoryName(normalized)` returns backslash-separated paths on Windows (`Assets\NAMERGenerated`). `AssetDatabase.CreateFolder`/`IsValidFolder` require forward-slash asset paths; backslashes are treated as invalid asset-path characters. The default destination (`Assets/NAMERGenerated/`) composed with a source name is two levels deep, so on Windows editors the nested `{source}` folder creation may fail (CreateFolder logs an error and returns ""), after which `File.WriteAllBytes` throws `DirectoryNotFoundException` — surfacing as WR-01's lockout. Metal/macOS is unaffected, which is why 52/52 passes hide it.
**Fix:** Normalize before calling AssetDatabase:

```csharp
string parent = Path.GetDirectoryName(normalized)?.Replace('\\', '/');
string folderName = Path.GetFileName(normalized);
```

### WR-05: Generated transparent materials keep the opaque render queue — broken transparency

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:278-285`
**Issue:** When `SurfaceType > 0`, `WriteMaterial` sets `_Surface = 1`, `_SURFACE_TYPE_TRANSPARENT`, alpha blending, and `ZWrite = 0`, but never changes `material.renderQueue` (the NAMER shader's queue stays Geometry/2000) and the shader's static `RenderType` tag stays "Opaque". A ZWrite-off, alpha-blended material rendered in the geometry queue produces depth/sorting artifacts (transparent surfaces rendered before/interleaved with opaque ones, per-material sorting undefined). URP Lit's transparent mode sets queue 3000.
**Fix:** After enabling the transparent path, add `material.renderQueue = (int)RenderQueue.Transparent;` (and consider `SetOverrideTag("RenderType", "Transparent")` for replacement-tag consumers).

### WR-06: `FindMeshInPrefab` returns a mesh owned by unloaded prefab contents

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:596-607`
**Issue:** `PrefabUtility.LoadPrefabContents(assetPath)` loads an isolated copy; `FindMeshInGameObject(contents)` returns `filter.sharedMesh`. For prefabs whose meshes are external assets (FBX/model sub-assets) this is safe, but for prefabs with embedded mesh sub-assets the returned `Mesh` is destroyed by `UnloadPrefabContents` in the `finally`. The stored `_previewMesh` then compares Unity-fake-null in `DrawPreviewSection` (`_previewMesh == null`), so the preview silently falls back to "No mesh to preview" for exactly the embedded-mesh prefab case the load was performed for. (`SourceInspector.AddPrefabContentsMaterials` uses the same load/unload pattern but only extracts persistent Material references, so it is unaffected.)
**Fix:** Either resolve the persistent sub-asset instead — `FindMeshSubAsset(assetPath)` already does this and handles embedded meshes — or verify the found mesh is a persistent asset (`AssetDatabase.Contains(mesh)`) and only fall back to prefab contents otherwise, keeping contents alive while that mesh is framed.

## Info

### IN-01: Magic numbers in the new UI code violate the phase's named-constants rule

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:374-375`; `Packages/com.graffitientertainment.namer/Editor/UI/NamerPreviewRenderer.cs:33,65-66,99`
**Issue:** `256f` and `0.5f` (preview min width / aspect), the light rotations `50f,-30f,0f` / `340f,218f,177f`, the `radius = 1f` fallback in `Frame`, and the `_distance = 4f` field initializer are unnamed literals. `NamerPreviewRenderer` otherwise models the convention well (`FieldOfView`, `HalfSeparation`, `MinCameraDistance`, ...), and `NamerEditorConstants.DebounceSeconds` exists precisely for this — these literals bypass it.
**Fix:** Promote to named `const` fields (`MinPreviewWidth = 256f`, `PreviewAspect = 0.5f`, `DefaultFramingDistance = 4f`, light-rotation constants).

### IN-02: Destination-confinement logic implemented twice — drift risk

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:166-182`; `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs:131-138`
**Issue:** `AssetGenerator.ValidateDestinationFolder` and `NamerProcessor.IsUnderProjectAssets` are byte-for-byte the same algorithm in two private copies. CR-01's fix (validating composed paths) will need a third use; keep one shared helper (e.g., on `NamerEditorConstants` or an internal static utility) so the rule cannot diverge between caller and writer.
**Fix:** Extract a single `IsConfinedToAssets(string path)` used by both (and by the composed-path check).

### IN-03: Overwrite gate is blind to on-disk-but-unimported files

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:325-340`
**Issue:** `EnsureWritableTarget` treats `LoadAssetAtPath == null` as "nothing to protect." A file that exists on disk at the target path but has no import state (`.meta` missing, not yet refreshed, or written by an external tool) is overwritten without any gate. This is the same root pattern as CR-01's escape case.
**Fix:** Add `File.Exists(path)` as a second condition: if the file exists on disk but is not a NamerGenerated-stamped imported asset, refuse (or require explicit overwrite) rather than silently replacing it.

### IN-04: `Graphics.ConvertTexture` → `EncodeToPNG` relies on synchronous CPU-side update

**File:** `Packages/com.graffitientertainment.namer/Editor/Generation/AssetGenerator.cs:109-110`
**Issue:** `baseSrgb` is never `Apply`'d or read back explicitly; correctness of `EncodeToPNG(baseSrgb)` at line 218 depends on `Graphics.ConvertTexture` having updated the destination texture's CPU copy by the time encoding reads it. Verified working on the Metal target (the `BasePng_EncodesLinearToSrgb` test proves it), but the timing guarantee varies by graphics API; on a backend that defers the GPU→CPU copy the base PNG could encode stale/empty data.
**Fix:** Note the verified-platform assumption next to the call, or make it explicit and portable with a tiny linear→sRGB conversion inside the existing compute pipeline (the project already avoids CPU per-pixel loops; a `ConvertTexture`-replacement kernel keeps that guarantee).

### IN-05: Test-suite duplication and hygiene

**File:** `Packages/com.graffitientertainment.namer/Tests/Editor/AssetGeneratorTests.cs:331-464`; `Packages/com.graffitientertainment.namer/Tests/Editor/SourceImmutabilityTests.cs:118-241`
**Issue:** `CreateSourceMaterial`, `CreateImportedBaseMap`, `EnsureTempFolder`, `Destroy`, `PrefsSnapshot`, `CapturePrefs`, and `RestorePrefs` (~130 lines) are copy-pasted between the two new test files — any fix to the prefs-restore logic must be made twice. Also: `SHA256.Create()` in `ComputeFileHash` is `IDisposable` and never disposed; `SourceImmutabilityTests.cs:49` assigns `baseMap` and never uses it.
**Fix:** Extract a shared `NamerTestHelpers` static class (same test assembly); `using (var sha = SHA256.Create()) { return sha.ComputeHash(...); }`; drop the unused local.

### IN-06: `#pragma target 2.0` alongside uint bitwise ops in the shared decode

**File:** `Packages/com.graffitientertainment.namer/Shaders/NamerDebugView.shader:42` (and the same pattern in the phase-2 `NAMER.shader:65`); `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl:57-74`
**Issue:** `NAMER_DECODE_SURFACE` uses `uint` bitwise AND (`a & 0x80u`), which nominally requires integer-op shader-model support that target 2.0 does not guarantee. It compiles on the editor backends in use (Metal/DX compile at ≥ SM4 regardless), so this is a documentation/portability nit, not a live break — but the declared target is misleading. Also, `float _DebugChannel` (shader line 50) sits outside the shared `UnityPerMaterial` CBUFFER, so the debug material is SRP-batcher-incompatible (editor-only; harmless, but the shader comment claims the layout "stays complete").
**Fix:** Bump to `#pragma target 3.5` (or add a comment stating the effective SM floor) and note the `_DebugChannel` CBUFFER exemption.

### IN-07: `DescribeResult` classifies outcomes by brittle substring matching

**File:** `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs:535-547`
**Issue:** "Blocked" vs "failed" is decided by `IndexOf("Refusing to overwrite")`, `"Destination"`, `"selection"`, `"settings"` over the error text. Any error message that merely contains one of those words (e.g., a readback failure mentioning the destination folder) is misclassified, and rewording a message in `AssetGenerator`/`NamerProcessor` silently changes UI semantics.
**Fix:** Add an enum or `Blocked` flag to `NamerProcessResult` set at the throw site instead of string sniffing.

---

_Reviewed: 2026-08-27T23:48:52Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: standard_

## Fix Log

_Applied by Claude (gsd-code-fixer)._

| ID | Fix | Commit |
|----|-----|--------|
| CR-01 | Reject `..` and path separators in prefix/suffix; re-validate composed paths are confined under the destination before any write | `aa6a6b7` |
| CR-02 | Apply the selected debug channel to the debug material immediately on toolbar change | `fbbae52` |
| WR-01 | Wrap `RunProcess` in try/catch/finally so `_busy` is always released | `edbadf9` |
| WR-02 | Thread `AoUnmultiplyStrength` through `NamerProcessorSettings` into generation | `123c3ab` |
| WR-03 | Pre-flight all target paths (per-material and whole batch) before any write | `368c1b8` |
| WR-04 | Normalize folder path separators to forward slashes before AssetDatabase calls | `2da8b7a` |
| WR-05 | Set transparent render queue and RenderType override on transparent materials | `3fbf0b3` |
| WR-06 | Resolve a persistent prefab mesh (or verify persistence) before unloading prefab contents | `506a3c4` |

_Regression test for CR-01 path-confinement was added in the WR-03 commit (`368c1b8`), where the pre-GPU preflight path made it a clean non-GPU `[Test]`._

---
phase: 01-core-format-contract-runtime-decode
reviewed: 2026-08-26T19:58:28Z
depth: deep
files_reviewed: 17
files_reviewed_list:
  - Packages/com.graffitientertainment.namer/package.json
  - Packages/com.graffitientertainment.namer/README.md
  - Packages/com.graffitientertainment.namer/Core/GraffitiEntertainment.Namer.Core.asmdef
  - Packages/com.graffitientertainment.namer/Core/NamerConstants.cs
  - Packages/com.graffitientertainment.namer/Core/NamerFormat.cs
  - Packages/com.graffitientertainment.namer/Editor/GraffitiEntertainment.Namer.Editor.asmdef
  - Packages/com.graffitientertainment.namer/Editor/NamerSmokeSetup.cs
  - Packages/com.graffitientertainment.namer/Runtime/GraffitiEntertainment.Namer.Runtime.asmdef
  - Packages/com.graffitientertainment.namer/Shaders/NAMER.shader
  - Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl
  - Packages/com.graffitientertainment.namer/Tests/Editor/BitPackingTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/BlenderGoldenVectorTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef
  - Packages/com.graffitientertainment.namer/Tests/Editor/OctahedralRoundTripTests.cs
  - Packages/com.graffitientertainment.namer/Tests/Runtime/GraffitiEntertainment.Namer.Tests.Runtime.asmdef
  - Packages/com.graffitientertainment.namer/Tests/Runtime/NamerRoundTripSmokeTests.cs
  - Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute
findings:
  critical: 2
  warning: 7
  info: 6
  total: 15
status: issues_found
---

# Phase 01: Code Review Report (core-format-contract-runtime-decode)

**Reviewed:** 2026-08-26T19:58:28Z
**Depth:** deep (per-file + cross-file: URP 17.0.4 package sources traced, git index state verified)
**Files Reviewed:** 17 (all tracked files under `Packages/com.graffitientertainment.namer/`)
**Status:** issues_found

## Summary

The core format contract is mathematically sound. I re-derived the quadratic inverse by hand: for a DirectX texel `v = ((m.x+1)/2, (1-m.y)/2, (m.z+1)/2)` of a unit normal `m`, the encode produces `p = v/|v|_1`, the larger root `S = [1+sqrt(1-2|p|^2)]/|p|^2` equals `2*|v|_1` exactly (verified algebraically: `|p|^2*S^2 - 2S + 2 = |m|^2 - 1 = 0`), and `n = (p.x*S-1, 1-p.y*S, p.z*S-1)` recovers `m` exactly. The C# Core and the HLSL mirror are line-for-line equivalent (`max(1-2*dotP,0)` discriminant clamp, `max(dotP,1e-6)` divide guard, `+0.5` alpha-byte rounding on both sides). The 30 EditMode tests' golden bytes (31/191/223/64/0) and oct values (0.625/0.625, 1.0/0.5) all check out by independent arithmetic. `AlphaDiscard`/`AlphaModulate`/`OutputAlpha` usage matches URP 17.0.4 semantics (all three self-gate on keywords, so the unconditional calls in `NamerSurface.hlsl` are correct).

The defects cluster elsewhere: an editor tool that silently discards unsaved scene work, a git state in which the package's `.meta` files can never be committed (proven via `git check-ignore`), a ForwardLit keyword set missing five multi_compiles that URP 17.0.4 `Lit.shader` declares, inconsistent `_EMISSION` gating between the ForwardLit and Meta passes, material UI toggles that cannot set keywords or blend state, a smoke tool that permanently rewrites global graphics settings, a wrong `testables` entry in package.json, and a PlayMode test whose material leg asserts nothing about the material.

Known deviations documented in 01-02-SUMMARY.md (property-driven blend state, CustomEditor removal, asmdef URP refs, OUTPUT_SH fix, git-index sweep) were not re-reported except where they produce a concrete downstream defect (WR-03 is the functional consequence of the CustomEditor removal).

## Critical Issues

### CR-01: Smoke-scene tool silently discards unsaved scene work

**File:** `Packages/com.graffitientertainment.namer/Editor/NamerSmokeSetup.cs:112`
**Issue:** `EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single)` closes the user's currently open scene and **silently discards any unsaved modifications** — it never prompts. This is a menu-item tool (`Tools/NAMER/Create Smoke Scene`) that an artist can invoke mid-editing; unsaved scene work is destroyed with no confirmation and no undo path. Data-loss risk in a shipped editor tool.
**Fix:** Guard the scene switch with the documented prompt (and bail out if the user cancels):
```csharp
private static void BuildSmokeScene(Material namerMaterial)
{
    if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsToContinue())
    {
        return; // user declined to discard/save their open scene
    }
    Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
    ...
}
```

### CR-02: Package committed without `.meta` files, and the staged `.gitignore` rule blocks the planned fix

**File:** `.gitignore:3` (affects `Packages/com.graffitientertainment.namer/**.meta`); evidence in git index
**Issue:** `git ls-files Packages/com.graffitientertainment.namer/` lists 17 source files and **zero** `.meta` files, while 25 exist on disk. `git check-ignore -v` proves why: the staged `.gitignore` rule `/packages/` (lowercase) matches `Packages/...` case-insensitively on this machine, so every package `.meta` is silently ignored. Consequently the remediation promised in 01-02-SUMMARY.md ("a follow-up commit should capture them") **cannot ever happen** — `git add` will skip them. On any fresh clone Unity will regenerate all 25 metas with new random GUIDs; every GUID-based reference made this phase (the saved `NamerSmoke_Material.mat` → texture assets, the smoke scene → material/shader) breaks, and Phase 3's generated-asset references become unstable across machines. The missing metas are acknowledged in the summary; the fact that the ignore rule prevents the fix is not, and is a concrete defect.
**Fix:** Remove or case-correct the `/packages/` rule in `.gitignore` (this is the user's pre-existing staged file — report, do not modify without consent), then `git add -f Packages/com.graffitientertainment.namer/**/*.meta` (or plain add after the ignore fix) and commit. Recommended `.gitignore` for a Unity project additionally ignores `Library/`, `Logs/`, `Temp/`, `obj/`.

## Warnings

### WR-01: ForwardLit pass missing five multi_compiles declared by URP 17.0.4 Lit

**File:** `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:73-90` (vs `Library/PackageCache/com.unity.render-pipelines.universal@c7abd84d7030/Shaders/Lit.shader:144-148`)
**Issue:** URP 17.0.4 `Lit.shader` ForwardLit additionally declares `#pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION`, `_DBUFFER_MRT1/2/3`, `_LIGHT_COOKIES`, `#pragma multi_compile _ _LIGHT_LAYERS`, and `#pragma multi_compile _ _FORWARD_PLUS`. NAMER declares none of them. Consequences when the renderer uses these features (Forward+ is a common Unity 6 configuration, SSAO is a stock renderer feature, light cookies/layers are project settings): NAMER materials silently skip SSAO, screen-space decals, cookies, and light-layer filtering, and additional-light evaluation under Forward+ deviates from URP Lit — breaking the SHDR-03 "renders like Lit" parity goal outside the smoke-scene defaults.
**Fix:** Add the five pragmas to the ForwardLit pass (copy lines 144-148 of URP's Lit.shader). Verify no compile regression for the `_FORWARD_PLUS` variant, which requires the cluster-light path in `Lighting.hlsl`.

### WR-02: `_EMISSION` gated in ForwardLit but not in the Meta pass

**File:** `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:562` (vs `NamerSurface.hlsl:102-106`, `NAMER.shader:518`)
**Issue:** `InitializeNamerSurfaceData` wraps emission in `#ifdef _EMISSION`; the Meta fragment sets `metaInput.Emission = _EmissionColor.rgb * (emissive ? 1.0 : 0.0);` unconditionally, even though the Meta pass declares the `_EMISSION` keyword at line 518 (unused there). URP Lit routes Meta emission through the keyword-gated `surfaceData.emission`. Result: a material whose `_EMISSION` keyword is off but whose `_EmissionColor` is non-black (exactly the state after toggling emission off — the HDR color persists) bakes emissive light into lightmaps while rendering no emission at runtime. The declared-but-unused keyword also generates dead Meta variants.
**Fix:**
```hlsl
#ifdef _EMISSION
    metaInput.Emission = _EmissionColor.rgb * (emissive ? 1.0 : 0.0);
#else
    metaInput.Emission = half3(0.0, 0.0, 0.0);
#endif
```

### WR-03: Emission / transparency / alpha-clip toggles are dead in the material inspector

**File:** `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:14-16, 19-27`
**Issue:** The toggles use `[ToggleUI]`, which writes only the float — it never sets shader keywords. URP wires `_ALPHATEST_ON` / `_SURFACE_TYPE_TRANSPARENT` / `_EMISSION` keywords and the `_Surface` / `_SrcBlend` / `_DstBlend` / `_ZWrite` render-state properties through `BaseShaderGUI`/`LitShader` (URP Lit's own `[ToggleUI] _AlphaClip` at Lit.shader:51 is likewise GUI-driven). The CustomEditor was removed (accepted deviation), but nothing replaced that wiring: with the default inspector, checking "Transparent"/"Emission"/"Alpha Clipping" flips floats the shader ignores, keyword-dependent code (`#ifdef _EMISSION`, `#ifdef _SURFACE_TYPE_TRANSPARENT`, `AlphaDiscard`'s `_ALPHATEST_ON` gate) is unreachable, and blend state stays opaque (`_SrcBlend=1,_DstBlend=0`). SHDR-04's emissive/transparency carry-through is only reachable via material scripting. This is the functional cost of the documented deviation, not a re-report of it.
**Fix:** Add a minimal `ShaderGUI` (mirror of `BaseShaderGUI.SetupBaseShaderKeywordsAndPass` for the NAMER property set) and re-add `CustomEditor`, or — the low-tech path — switch to keyword-setting attributes (`[Toggle(_ALPHATEST_ON)]`, `[Toggle(_EMISSION)]`, `[Toggle(_SURFACE_TYPE_TRANSPARENT)]`) and expose `_SrcBlend/_DstBlend/_ZWrite/_Surface` controls or a Phase 3 material-setup API that sets them.

### WR-04: Tiling/offset UI lies — `_SurfaceMap_ST` declared but never used; `_BaseResidualMap` is `[NoScaleOffset]` yet drives all UVs

**File:** `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl:22-23`, `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:5-6, 202`
**Issue:** Every pass transforms UVs with `_BaseResidualMap`'s ST (`TRANSFORM_TEX(input.texcoord, _BaseResidualMap)` at NAMER.shader:202, 326, 396, 466, 543), but `_BaseResidualMap` is declared `[NoScaleOffset]` so its tiling is hidden from the inspector — the one ST that works is invisible. Meanwhile `_SurfaceMap` (not `[NoScaleOffset]`) shows tiling/offset controls in the UI that do absolutely nothing (`_SurfaceMap_ST` sits in the CBUFFER and is never read). Both maps are sampled with the same UVs, so sharing one ST is the right design — the property exposure is inverted.
**Fix:** Add `[NoScaleOffset]` to `_SurfaceMap` (line 5) so no map shows fake tiling, and document that tiling (when exposed later) comes from the base map. Alternatively drop `_SurfaceMap_ST` from the CBUFFER.

### WR-05: Smoke tool permanently rewrites global graphics settings with no confirmation or restore

**File:** `Packages/com.graffitientertainment.namer/Editor/NamerSmokeSetup.cs:54-69`
**Issue:** `ConfigureURP` assigns `GraphicsSettings.defaultRenderPipeline` and `QualitySettings.renderPipeline` to the smoke asset and never restores them. This is not hypothetical: `ProjectSettings/GraphicsSettings.asset:40` and one `QualitySettings.asset` level (line 310) now point at `Assets/NAMER/NamerSmokeURP.asset` (guid `d1494954c81d842e0ae26e6a5f532aae`) — the tool already rewired the user's project, writing ProjectSettings files as a side effect of a "smoke scene" menu action. It also only checks `GraphicsSettings.defaultRenderPipeline`; a per-quality-level URP override (the skip-path) leaves inconsistent state.
**Fix:** Prompt before changing global settings (or generate the scene-only assets and instruct the user to assign the pipeline manually); at minimum, cache and document the previous values so the smoke setup is reversible, and check `QualitySettings.renderPipeline` for the active quality level too.

### WR-06: `testables` lists the wrong package — tests will not be discovered when installed as a dependency

**File:** `Packages/com.graffitientertainment.namer/package.json:11`
**Issue:** `"testables": ["com.unity.test-framework"]` names the test framework, not this package. Unity's documented pattern for shipping package tests is to list the package under test (`"testables": ["com.graffitientertainment.namer"]`); the framework comes in via the project manifest. As written, a project consuming this package from a registry/embedded cache will not include the 30 EditMode tests or the PlayMode smoke test in test runs (they only run now because of the embedded-package dev setup).
**Fix:**
```json
"testables": ["com.graffitientertainment.namer"]
```

### WR-07: PlayMode smoke test's "material" leg asserts nothing about the material

**File:** `Packages/com.graffitientertainment.namer/Tests/Runtime/NamerRoundTripSmokeTests.cs:26-44`
**Issue:** The test is named `NeutralNormalRoundTripsThroughCoreAndMaterial`, and steps 3-4 build a `Texture2D` + `Material` and bind `_SurfaceMap` — but nothing ever reads back from the GPU. The only shader-adjacent assertions are `Shader.Find` non-null and `material.shader.name`. Every step from line 26 onward duplicates the EditMode suite except the name check; the shader decode (`NamerOctahedralDecode`, alpha unpack, `InitializeNamerSurfaceData`) can regress completely and this test stays green, giving false confidence on top of a test that has also never been run headless. Also, `Destroy` calls are only reached on the success path — an assert failure leaks the material/texture into the domain.
**Fix:** Make the GPU leg real (render the material once and read pixels back):
```csharp
// after creating the material:
RenderTexture rt = RenderTexture.GetTemporary(4, 4, 0, GraphicsFormat.R8G8B8A8_SFloat);
Graphics.Blit(Texture2D.whiteTexture, rt, material); // or draw a quad via CommandBuffer
// AsyncGPUReadback / ReadPixels, then assert the emissive/alpha decode path changed the output
RenderTexture.ReleaseTemporary(rt);
```
At minimum: rename to drop "Material", move cleanup into a `finally`/`IDisposable`, and add a standalone CPU golden test `OctahedralDecode((0.625, 0.625)) == (0, 0, 1)` with an exact direction assert.

## Info

### IN-01: Magic numbers in Core; declared constant is dead

**File:** `Packages/com.graffitientertainment.namer/Core/NamerFormat.cs:22,39,57,68,78,90`
**Issue:** `63.0f` appears twice (PackAlphaBits/UnpackAlphaBits) while `NamerConstants.RoughnessStep` (`NamerConstants.cs:27`) is never referenced anywhere — the one named roughness constant is dead and the literals it was meant to name are in use. Also unnamed: `255.0f` (two places), `1e-6f` (two places). Project convention (review focus 5) requires named constants; more importantly, Phase 2's compute kernel will re-derive these literals a third time, and a silent mismatch there breaks ENCD-05 equivalence.
**Fix:** Add `public const float RoughnessLevels = 63.0f;` (and `AlphaByteScale = 255.0f`, `Epsilon = 1e-6f`) to `NamerConstants`, use them in `NamerFormat`, and delete or use `RoughnessStep`.

### IN-02: Dead `#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR`

**File:** `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:100`
**Issue:** The define is emitted after all includes and nothing in NAMER.shader tests it — the Varyings struct carries `tangentWS` unconditionally, so the macro gates nothing. URP's Lit uses this macro inside its own pass headers; here it is cargo-culted dead code.
**Fix:** Delete the line (the struct is already correct).

### IN-03: Golden vectors are self-derived, not imported from Blender

**File:** `Packages/com.graffitientertainment.namer/Tests/Editor/BlenderGoldenVectorTests.cs:7-22`
**Issue:** The header admits the expectations are "derived from the exact reference formula" — i.e., computed by the same math the implementation uses. If the C# port misreads `namer_core.py`, both implementation and expectations misread it identically and the ENCD-05 test passes while diverging from Blender. Decode also has no standalone golden test (decode of `(0.625, 0.625)` → `+Z` is only exercised through round-trips and the PlayMode test).
**Fix:** When the Blender plugin is reachable, capture literal texel→byte outputs from `namer_core.py` and pin them as opaque constants; add a direct `OctahedralDecode` golden-direction test.

### IN-04: Exception types and dead computation in Meta pass

**File:** `Packages/com.graffitientertainment.namer/Editor/NamerSmokeSetup.cs:76,124`; `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader:552-558`
**Issue:** `throw new System.Exception` twice — use `InvalidOperationException` for clearer failure semantics. The Meta fragment decodes `roughness`/`smoothness`/`normalTS`/`ao` it never reads (only albedo/emission/`emissive` are used) — harmless but noise.
**Fix:** Specific exception types; call `NAMER_DECODE_SURFACE` for just the emissive bit, or sample and mask directly.

### IN-05: No GBuffer pass — deferred renderer renders nothing

**File:** `Packages/com.graffitientertainment.namer/Shaders/NAMER.shader` (absence; cf. URP Lit's `UniversalGBuffer` pass)
**Issue:** With a Deferred-mode URP renderer the material has no GBuffer pass to execute. Acceptable for Phase 1 (URP-forward-first scope, project's smoke asset is Forward), but it should be a tracked gap before Phase 2 GPU work claims pipeline coverage.
**Fix:** Note in the plan; add a GBuffer pass (or an explicit "deferred unsupported" statement) when pipeline coverage matters.

### IN-06: Nothing enforces point sampling of the packed alpha bits

**File:** `Packages/com.graffitientertainment.namer/Shaders/NamerSurface.hlsl:57-71`
**Issue:** `NAMER_SAMPLE_ALPHA_BYTE` rounds a filtered UNorm alpha back to a byte. The smoke textures set `FilterMode.Point` by hand, but neither the shader nor any shared setup constrains the sampler — if Phase 3's importer emits the packed surface with bilinear filtering or mips, interpolated alpha corrupts the metallic/emissive/roughness bits and filtered RG degrades the octahedral decode. This will bite Phase 3 exactly where ENCD-05 equivalence is checked.
**Fix:** In the Phase 3 texture-import path, force point filtering + no mips (and uncompressed linear, per constraint) for `_SurfaceMap`; consider asserting it in the material setup API.

---

## Counts

| Severity | Count |
|----------|-------|
| Critical | 2 |
| Warning | 7 |
| Info | 6 |
| **Total** | **15** |

## Verdict

**DO NOT SHIP as-is.** The format contract (Core C#, HLSL mirror, golden vectors) is correct and well-tested — that risk is retired. The blockers are around it: an editor menu tool that can silently destroy unsaved scene work (CR-01), and a git/`.gitignore` state that guarantees Unity GUID instability for every asset this phase produced (CR-02). The seven warnings all erode the shader's stated goal of "renders like URP Lit" (missing Forward+/SSAO/decal/cookie keywords, dead emission/transparency UI, ungated Meta emission) or the test story's credibility (PlayMode test asserts nothing about the material, `testables` misconfigured). Fix CR-01/CR-02 and WR-01..WR-03 before Phase 2 builds on this; the rest can ride the next pass.

_Reviewed: 2026-08-26T19:58:28Z_
_Reviewer: Claude (gsd-code-reviewer)_
_Depth: deep_

## Fix Log

_Applied 2026-08-26 by gsd-code-fixer (per-finding atomic commits on main, `git commit --only` preserving the user's pre-existing staged index). Severity classifications above are unchanged._

| Finding | File(s) | Commit | Note |
|---------|---------|--------|------|
| CR-01 | Editor/NamerSmokeSetup.cs | `79a1d01` | `BuildSmokeScene` now returns bool; guarded by `EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsToContinue()` (bails without the false "scene created" log when the user cancels). |
| WR-01 | Shaders/NAMER.shader | `1b672e4` | Added the five multi_compiles copied verbatim from URP 17.0.4 `Lit.shader:144-148` (`_SCREEN_SPACE_OCCLUSION`, `_DBUFFER_MRT1/2/3`, `_LIGHT_COOKIES`, `_LIGHT_LAYERS`, `_FORWARD_PLUS`). Include chain verified to satisfy `_LIGHT_LAYERS` (`IsMatchingLightLayer` via Lighting.hlsl → GlobalIllumination.hlsl → ImageBasedLighting.hlsl → CommonLighting.hlsl; `GetMeshRenderingLayer()` reads the always-declared `unity_RenderingLayer` global). Known limitation kept in scope: DBuffer decal *application* remains Lit-pass code in URP, so decals are still skipped; only keyword parity was added. |
| WR-02 | Shaders/NAMER.shader | `4260f91` | Meta `metaInput.Emission` now `#ifdef _EMISSION`-gated (zero when off), backed by the pass's existing pragma. |
| WR-03 | Shaders/NAMER.shader | `2b07434` | Low-tech path per scope: `[Toggle(_ALPHATEST_ON)]`/`[Toggle(_EMISSION)]`, and the transparent keyword moved onto `[Toggle(_SURFACE_TYPE_TRANSPARENT)] _Surface` so one checkbox sets the float (drives `OutputAlpha` via `IsSurfaceTypeTransparent`) AND the keyword. `_SrcBlend`/`_DstBlend` exposed as `[Enum(UnityEngine.Rendering.BlendMode)]`, `_ZWrite` as `[Enum(Off,0,On,1)]` — defaults (One/Zero/On) keep opaque rendering byte-identical, so SHDR-03 parity and existing materials are unaffected. Dead GUI-only `_Blend` property removed. Transparent setup from the inspector is now possible (toggle + blend dropdowns); a one-click ShaderGUI remains a future option. |
| WR-04 | Shaders/NAMER.shader | `4790dde` | `[NoScaleOffset]` added to `_SurfaceMap` (comment documents that tiling comes from `_BaseResidualMap`). |
| WR-05 | Editor/NamerSmokeSetup.cs | `c799e8e` | `ConfigureURP` now treats URP as active only when the project default is URP AND the active quality override is URP-or-null, and asks via `EditorUtility.DisplayDialog` (buttons "Assign URP Asset" / "Skip"; Esc = Skip) before writing `GraphicsSettings.defaultRenderPipeline` / `QualitySettings.renderPipeline`; declines skip gracefully with a log. Deviation from the suggested "default No": Unity's 2-button dialog cannot set the focused button, and conventional OK-first ordering was chosen so Esc (abort key) maps to Skip rather than to the assignment. |
| WR-06 | package.json | `e50cbfc` | `testables` = `["com.graffitientertainment.namer"]` (JSON re-parsed, matches the package's own name). |
| WR-07 | Tests/Runtime/NamerRoundTripSmokeTests.cs, Tests/Editor/BlenderGoldenVectorTests.cs | `eba8c21` | PlayMode test renamed to `NeutralNormalRoundTripsThroughCoreAndShaderBinding` (dropped the "Material" claim), `Destroy` moved to `finally` with null guards so asserts can't leak assets; added `GoldenDecode_NeutralOctahedralTexelRecoversPlusZ` CPU golden (exact component asserts x=0,y=0,z=1 at 1e-4 + direction dot ≥ 1-1e-3; hand-derived p=(0.25,0.25,0.5), S=4 → (0,0,1)) in the Editor golden suite. EditMode count is now 31. |
| IN-01 | Core/NamerConstants.cs, Core/NamerFormat.cs | `55449ed` | Added `RoughnessLevels = 63.0f`, `AlphaByteScale = 255.0f`, `Epsilon = 1e-6f`; used at all six literal sites in `NamerFormat`; dead `RoughnessStep` deleted (grep-verified zero references, incl. tests). Values identical, so golden bytes unchanged. |
| IN-02 | Shaders/NAMER.shader | `ef6d414` | Dead `#define REQUIRES_WORLD_SPACE_TANGENT_INTERPOLATOR` deleted (explanatory comment kept as struct doc). |
| IN-04 (partial, per scope) | Editor/NamerSmokeSetup.cs | `0da81f0` | Both `System.Exception` throws → `System.InvalidOperationException`. Meta-pass dead-computation cleanup intentionally skipped (out of scope for this pass). |

**Not fixed (out of scope per instructions):** CR-02 (.gitignore is user-owned), IN-03 (needs live Blender plugin), IN-05 (GBuffer pass — tracked gap), IN-06 (Phase 3 import path).

**Verification:** headless EditMode run attempted as instructed and aborted — the user's interactive editor (PID 32120) holds `Temp/UnityLockfile` ("Multiple Unity instances cannot open the same project"); no results XML was produced and the editor was NOT touched. Fallback compile-level review performed: full re-read of every changed C# file (brace/paren balance checked; all symbols resolve under existing usings — `SaveCurrentModifiedScenesIfUserWantsToContinue`, `DisplayDialog`, `QualitySettings.renderPipeline`, iterator `try/finally` with `yield` outside the block, `[Test]`/`math.dot` in the Editor suite), JSON parse of package.json, and URP-source verification of every include/keyword dependency introduced by WR-01. The 30/30 (now 31/31) EditMode suite must be re-run once the interactive editor is closed.

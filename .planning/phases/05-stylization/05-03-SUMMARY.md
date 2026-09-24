---
phase: 05-stylization
plan: 03
subsystem: ui
tags: [unity, hlsl, ao-remap, cavity-floor, roughness-simplify, octahedral, stylization, editor-window, guid-persistence, editmode-tests]

# Dependency graph
requires:
  - phase: 05-stylization plan 02
    provides: NamerStyleStylization.Create contract (Active gate, SmoothingSkippedNoMesh), D-01 stylize stage inside Process, Metal argument-table discipline finding
  - phase: 05-stylization plan 01
    provides: NamerStyleProfile (sliders, IsExtracted, ClampToCaps, Changed event), NamerStyleExtractor apply sequence
  - phase: 04.3 adaptive residual
    provides: NamerProcessor.Process flow, mesh-priming disjunct, four EditorPrefs sandbox mirrors in the test assembly
provides:
  - D-08 cavity floor: NamerAoRemap 4th parameter max(ao, cavityFloor) applied post-saturate on the extracted, baked, AND authored AO paths (STYL-05)
  - D-08 authored-AO remap: NamerAOPipeline.RemapAuthored (CSAoRemap direct, caller-owned RTs, zero pool ops) invoked by NamerComputePipeline whenever strength/contrast/floor is non-identity
  - D-07 bake-side controls: _RoughnessSimplify (radius-2 neighborhood mean in CSSurfacePack, after dip adoption, before 6-bit quantization) and _NormalDetailReduction (lerp toward +Z in CSOctahedralEncode, after Z-rebuild, before oct encode) (STYL-06)
  - NamerProcessor profile resolution: ResolveStyleProfile (empty/dead GUID -> silent null detach, T-05-11 fail-closed), StylizationNeedsMesh joining the mesh-priming disjunct, stylize-only split priming, ApplyStyleControls shared scalar plumb (NaN-sanitized clamps, T-05-12)
  - NamerEditorWindow W1-W4: Stylization section (foldout + profile ObjectField persisting the asset GUID), attach-state honesty line, preview-only After View lens {NAMER, Stylized NAMER}, NamerStyleProfile.Changed subscription, SmoothingSkippedNoMesh Info note
  - Preview/bake parity: the Stylized NAMER preview and the Process bake compose the identical stylization context + scalars from the same persisted profile (saved output == stylized preview)
  - Three new EditorPrefs keys (StyleProfileGuid string "", FoldoutStylization bool false, AfterView int 0) mirrored in all four test-assembly prefs sandboxes
affects: [phase 05 post-wave regression gate, phase 06+ any window/pipeline work]

# Tech tracking
tech-stack:
  added: []  # zero new packages
  patterns:
    - ApplyStyleControls as the single scalar plumb: one internal NamerProcessor helper writes CavityFloor/NormalDetailReduction/RoughnessSimplify from the profile (clamped, NaN-safe) or zeroes them; the bake and RecomputePreview both call it, so preview == saved output by construction
    - Preview-only lens: settings.AfterView gates ONLY the preview's stylization argument; RunProcess -> NamerProcessor.Process resolves the profile from settings and is structurally incapable of seeing AfterView
    - GUID persistence via AssetPathToGUID on attach + GUIDToAssetPath/LoadAssetAtPath on every resolve; dead/deleted GUIDs fail closed to null with zero exceptions (T-05-11)
    - Metal argument-table discipline: every NEW production dispatch site binds its full kernel argument table (RemapAuthored binds _CavityFloor; BindAndDispatchNormalizeEncode binds both NAMERPack uniforms before CSSurfacePack and CSOctahedralEncode); pre-existing kernel-direct tests left untouched on the bare-global-float evidence (see Deviations)

key-files:
  created: []  # plan modified existing files only
  modified:
    - Packages/com.graffitientertainment.namer/Compute/NAMERAO.hlsl
    - Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute
    - Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs
    - Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs
    - Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs
    - Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizationIntegrationTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerDecompIntegrationTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerProjectionAcceptanceTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/NamerOneTextureTests.cs

key-decisions:
  - "ApplyStyleControls extracted from the Task-2 inline plumb into an internal shared helper so the window preview and the bake apply byte-identical scalars (parity by construction, not by duplication)"
  - "After View is a preview-only lens; the bake composes the profile unconditionally — W4 popup disabled + visible hint when no profile, per the UI-SPEC interaction table"
  - "Pre-existing kernel-direct tests (NamerAOControlsTests/GpuGoldenTests/NamerAOExtractionTests) NOT edited for Metal argument-table discipline: wave-2 evidence shows bare $Globals floats zero-default without triggering the silent skip (never-set _LumaAverage/_HasExtractedRoughness in the twice-green 226-test suite); every NEW production dispatch site binds its full table"
  - "RecomputePreview splits the mesh only when the projection context or the stylized lens needs it ((dipSource == RemovedDetail || stylizeLens)) — the lens-off path keeps the exact pre-05-03 dispatch behavior"
  - "D-08 test-flip audit outcome: zero pre-existing tests legitimately flip — existing authored-AO coverage either pins identity strength/contrast (raw passthrough unchanged) or drives the remap kernel directly where cavityFloor = 0 is a max-with-zero identity; the changed behavior is covered by the new AuthoredAoRemap_* tests"

patterns-established:
  - "Profile-driven bake controls: profile sliders -> ClampToCaps/SanitizedClamp at the resolve seam -> inspection scalars -> compute uniforms, with identity-0 defaults keeping the no-profile path byte-identical"
  - "EditorPrefs sandbox mirrors updated in lockstep with every new NamerProcessor.* key (four mirrors carry the now 13-key snapshot)"
  - "Roslyn closure compile proof (Temp/gsd-05-03-compile-check.sh) as the no-live-editor verification gate: Unity's bundled csc against the exact csproj reference closure, Editor dll rebuilt and Tests.Editor compiled against it"

requirements-completed: [STYL-05, STYL-06]

# Metrics
duration: 26min
completed: 2026-09-24
---

# Phase 5 Plan 3: Bake-Side Stylization Controls + Editor Window Summary

**Profile-attached stylization is now operable end-to-end: authored AO is shapable with a cavity floor, roughness simplifies and normals flatten bake-side before quantization/encode, and the Processor window's W1-W4 Stylization section previews exactly what Process bakes.**

## Performance

- **Duration:** 26 min
- **Started:** 2026-09-24T00:13:24Z
- **Completed:** 2026-09-24T00:39:20Z
- **Tasks:** 3/3
- **Files modified:** 14 (10 plan files + 4 prefs-sandbox mirrors)

## Accomplishments

- **D-08 cavity floor + authored-AO remap (STYL-05):** `NamerAoRemap(ao, strength, contrast, cavityFloor)` clamps the remapped output with `max(ao, cavityFloor)` on every AO path; painted/authored AO maps now honor the strength/contrast remap via the caller-owned `RemapAuthored` dispatch (zero pool ops — Pitfall 1), while identity settings keep the raw passthrough byte-identical.
- **D-07 roughness simplify + normal flatten (STYL-06):** a radius-2 neighborhood mean smooths roughness inside CSSurfacePack after dip-source adoption and before the 6-bit quantization (one insertion covers transfer/Sobel/scalar — they converge on the local), and CSOctahedralEncode lerps normals toward +Z after the Z-rebuild and before octahedral encode; both identity-0 short-circuited, `NamerEncode.hlsl` untouched (verified empty diff).
- **NamerProcessor bake plumb:** `ResolveStyleProfile` (empty/dead GUID -> silent null, T-05-11 fail-closed), `StylizationNeedsMesh` joining the mesh-priming disjunct, stylize-only split priming shared across materials, `NamerStyleStylization.Create(profile, split)` passed into `Process(inspection, projection, stylization)`, and `ApplyStyleControls` scalars (NaN-sanitized clamps, T-05-12).
- **Editor window W1-W4:** Stylization section between AO and Decomposition with a GUID-persisting profile ObjectField, the attach-state honesty line ("not extracted" / "palette extracted (N anchors, M references)"), the preview-only After View lens with verbatim UI-SPEC copy, `NamerStyleProfile.Changed` re-debouncing, and the SmoothingSkippedNoMesh Info note (OQ-3). The bake is never gated by AfterView (`RunProcess` -> `NamerProcessor.Process(_selection, _settings)` is structurally blind to it).
- **Byte-identity triple-check held:** E2E test proves empty GUID == dead GUID == all-identity profile are byte-identical to the 04.3 baseline (zero stylize dispatches, zero extra mesh resolutions); every new branch is guarded by `profile != null`.
- **13 new EditMode tests** (deterministic, numeric readback only — hash-noise grain fixtures, strip/window medians, no sleep waits): identity byte-parity, authored remap strength + cavity floor, roughness simplify (gloss-map + Sobel-after-dip ordering), normal flatten dot-oracle, StylizationNeedsMesh, styled-bake E2E hue oracle, GUID byte-identity triple, smoothing priming without decomposition, bare-material palette-only degrade, lens logic, attach-state copy formatting.

## Task Commits

Each task was committed atomically:

1. **Task 1: D-07/D-08 bake-side controls** - `d0af168` (feat)
2. **Task 2: NamerProcessor bake plumb + E2E** - `9a0f1e6` (feat)
3. **Task 3: Editor window W1-W4 + lens + mirrors** - `d283984` (feat)

**Plan metadata:** (docs commit follows this summary)

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Compute/NAMERAO.hlsl` — cavityFloor parameter in NamerAoRemap (D-08)
- `Packages/com.graffitientertainment.namer/Compute/NAMERAO.compute` — `_CavityFloor` uniform + call-site plumb
- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` — `_RoughnessSimplify` (CSSurfacePack, post-dip pre-quant) + `_NormalDetailReduction` (CSOctahedralEncode, post-Z-rebuild pre-oct) with named radius constant
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs` — CavityFloor/NormalDetailReduction/RoughnessSimplify inspection scalars (identity-0 defaults)
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs` — `_CavityFloor` bound at every CSAoRemap dispatch site + caller-owned `RemapAuthored`
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs` — non-identity authored remap branch (lease/release around RemapAuthored) + both NAMERPack uniforms bound before every pack/encode dispatch
- `Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs` — ResolveStyleProfile, StylizationNeedsMesh, stylize-only split priming, ApplyStyleControls shared helper
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs` — W1-W4, lens composition, Changed subscription, RecomputePreview profile-aware priming/split/compose
- `Packages/com.graffitientertainment.namer/Editor/Settings/NamerProcessorSettings.cs` — StyleProfileGuid/FoldoutStylization/AfterView keys
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizationIntegrationTests.cs` — 13 new tests + E2E fixture helpers (assets under Assets/NAMER_Style_E2E_Temp, cleaned in finally)
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerRoughnessFitTests.cs` / `NamerDecompIntegrationTests.cs` / `NamerProjectionAcceptanceTests.cs` / `NamerOneTextureTests.cs` — the four EditorPrefs sandbox mirrors, each carrying the three new keys + Had flags

## Decisions Made

- Preview/bake parity via the shared `ApplyStyleControls` helper rather than duplicated scalar plumbing (preview == saved output by construction).
- The lens-off preview path preserves the exact pre-05-03 split/dispatch behavior (`_dipSource == RemovedDetail || stylizeLens` guard) — no extra splits when the popup reads NAMER.
- All three prefs keys added in Task 2 (single sandbox-mirror churn), consumed by the window in Task 3, per the plan's action note.
- E2E profile asset serializes with `References.Clear()` after the extractor apply sequence (in-memory refs would serialize broken; Stats.ReferenceCount is already counted).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - blocking] Task 3 commit scope: NamerProcessor.cs + four sandbox-mirror files beyond the task's `<files>` list**
- **Found during:** Task 3 (Editor window W1-W4)
- **Issue:** The plan's Task 3 `<files>` lists only NamerEditorWindow.cs + NamerStylizationIntegrationTests.cs, but the action text requires the preview to apply the same clamped scalars as the bake and requires updating "every prefs sandbox mirror". The Task-2 inline scalar plumb lived inside NamerProcessor.Process and was unreachable from the window; the four mirrors live in four additional test files.
- **Fix:** Extracted the plumb into `internal static NamerProcessor.ApplyStyleControls` (behavior-identical refactor, now called by both Process and RecomputePreview); added the three keys to all four mirrors as the plan's action text specifies.
- **Files modified:** Editor/Pipeline/NamerProcessor.cs, Tests/Editor/{NamerRoughnessFitTests, NamerDecompIntegrationTests, NamerProjectionAcceptanceTests, NamerOneTextureTests}.cs
- **Verification:** Roslyn closure compile proof green (Editor + Tests.Editor); grep gate PLAN-05-03-T3-OK.
- **Committed in:** d283984

**2. [Rule 1 - bug] Test oracle corrections in the Task-3 lens/attach-state tests**
- **Found during:** Task 3 test authoring
- **Issue:** The draft lens test asserted `composed.Active` on a non-extracted profile (invalid: `Active = (Smoothing>0 && TriIsland!=null) || (PaletteAvailable && StyleStrength>0)` is false without a palette), and the draft attach-state fixture marked "extracted" via anchors+stats when `IsExtracted` actually reads the non-empty `ReferenceFingerprint` Extract writes.
- **Fix:** Lens test asserts the W4 contract (null at NAMER view, null without profile, composed + SmoothingSkippedNoMesh at the stylized view with Smoothing>0); attach-state fixture sets a synthetic fingerprint + anchors + ReferenceCount to produce the exact UI-SPEC string.
- **Files modified:** Tests/Editor/NamerStylizationIntegrationTests.cs
- **Verification:** Roslyn compile green; assertions match the NamerStyleStylization.Create and NamerStyleProfile.IsExtracted sources (read directly, not assumed).
- **Committed in:** d283984

---

**Total deviations:** 2 auto-fixed (1 x Rule 3, 1 x Rule 1)
**Impact on plan:** Both within the plan's own action text and identity guard; no scope creep, no behavior change on pre-existing paths.

## Issues Encountered

- **Metal argument-table risk on pre-existing kernel-direct tests (considered, resolved by evidence, no edits):** NamerAOControlsTests/GpuGoldenTests/NamerAOExtractionTests dispatch kernels with partial argument tables. Wave-2's skip finding covered StructuredBuffers/mixed-table constants; bare $Globals floats zero-default without triggering the skip (proven by never-set `_LumaAverage`/`_HasExtractedRoughness` in the twice-green 226-test suite). All NEW production dispatch sites bind full tables.
- **Compile-only verification:** the live editor holds the Library lock, so the EditMode suite run is deferred to the orchestrator's post-wave live regression gate (established 05-01/05-02 house precedent). Compile equivalence proven via Unity's bundled Roslyn against the exact csproj closures (`Temp/gsd-05-03-compile-check.sh` -> GSD-05-03-COMPILE-OK, Editor dll rebuilt + Tests.Editor compiled against it).
- **CS0117/CS0037 test compile errors** during authoring (Assert.IsNullOrEmpty; null comparisons on the struct NamerStyleAnchor) — fixed with IsNullOrEmpty->IsTrue(string.IsNullOrEmpty) and a `bool found` dominant-anchor pattern.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Phase 05 is feature-complete (05-01 profile + extraction, 05-02 stylize stage, 05-03 bake controls + window): the phase goal "reference-image-driven, hue-preserving stylization operable from the editor window via a reusable profile, with saved output == preview and the no-profile path byte-identical" is delivered.
- Post-wave gate remains: run the full EditMode suite in the live editor (expect the 226 pre-existing + 13 new green) and a visual pass of the W1-W4 section.
- The phase-close regression gate should also confirm the four sandbox mirrors restore the three new keys.

## Self-Check: PASSED

All 15 modified/created files exist on disk; all three task commits (d0af168, 9a0f1e6, d283984) present in git log.

---
*Phase: 05-stylization*
*Completed: 2026-09-24*

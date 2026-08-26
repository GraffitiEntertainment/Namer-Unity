---
phase: 01-core-format-contract-runtime-decode
plan: 03
subsystem: infra
tags: [unity, upm, urp, asmdef, csharp, hlsl, compute-shader, test-framework]

# Dependency graph
requires: []
provides:
  - Unity 6000.0 project skeleton at the repo root (Assets/, ProjectSettings/, Packages/)
  - UPM package com.graffitientertainment.namer with Core/Runtime/Editor/Tests asmdef contract
  - Round-trip PlayMode smoke test harness (walking-skeleton end-to-end slice)
affects:
  - 01-01 (Core math compiles against GraffitiEntertainment.Namer.Core asmdef)
  - 01-02 (URP shader + PlayMode smoke test run)

# Tech tracking
tech-stack:
  added: [Unity 6000.0.82f1, URP 17.0.4, Unity.Mathematics 1.3.2, Unity.Burst 1.8.0, Unity.Collections 2.5.0, Unity Test Framework (testables)]
  patterns: [asmdef-per-folder, Core/Runtime/Editor/Tests compilation split, linear R8G8B8A8_UNorm packed-texture contract]

key-files:
  created:
    - Packages/manifest.json
    - Packages/com.graffitientertainment.namer/package.json
    - ProjectSettings/ProjectVersion.txt
    - Packages/com.graffitientertainment.namer/Core/GraffitiEntertainment.Namer.Core.asmdef
    - Packages/com.graffitientertainment.namer/Runtime/GraffitiEntertainment.Namer.Runtime.asmdef
    - Packages/com.graffitientertainment.namer/Editor/GraffitiEntertainment.Namer.Editor.asmdef
    - Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef
    - Packages/com.graffitientertainment.namer/Tests/Runtime/GraffitiEntertainment.Namer.Tests.Runtime.asmdef
    - Packages/com.graffitientertainment.namer/README.md
    - Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute
    - Packages/com.graffitientertainment.namer/Tests/Runtime/NamerRoundTripSmokeTests.cs
  modified: []

key-decisions:
  - "Used the plan's hand-authored fallback (no -createProject) since the repo root is non-empty (PRD, .planning/, .sln) and running Unity would emit unignored Library/Temp artifacts the pre-existing .gitignore does not cover"
  - "Skipped the batchmode open (fallback step 4); acceptance criteria are file-based and Unity generates ProjectSettings.asset on first manual open or in 01-01/01-02"
  - "Authored the smoke test against the actual Unity 6 Texture2D API (TextureFormat.RGBA32 + linear=true + graphicsFormat assertion) instead of a non-existent GraphicsFormat constructor"

patterns-established:
  - "Five asmdefs with exact name/reference contract from the plan <interfaces> block (Core -> Unity.Mathematics, Runtime -> Core, Editor -> Core+Runtime, Tests -> Core + testAssemblies)"
  - "Packed surface texture is linear R8G8B8A8_UNorm; base/residual is sRGB; documented in README"

requirements-completed: [PKG-01]

# Metrics
duration: 12min
completed: 2026-08-26
---

# Phase 1 Plan 03: UPM Package Skeleton + Round-Trip Test Harness Summary

**Scaffold Unity 6000.0 project and UPM package `com.graffitientertainment.namer` with five asmdefs (Core/Runtime/Editor/Tests) and the round-trip PlayMode smoke test**

## Performance

- **Duration:** 12 min
- **Started:** 2026-08-26T01:44:35Z
- **Completed:** 2026-08-26T01:56:55Z
- **Tasks:** 3
- **Files modified:** 11 (all new)

## Accomplishments

- Unity 6000.0 project skeleton at the repo root (`Assets/`, `ProjectSettings/ProjectVersion.txt`, `Packages/manifest.json` with URP 17.0.4, no `com.unity.render-pipelines.core` pin)
- Valid UPM package `com.graffitientertainment.namer` (`"unity": "6000.0"`, four dependencies + `testables: [com.unity.test-framework]`)
- Five assembly definitions establishing the Core/Runtime/Editor/Tests compile contract with exact reference wiring from the plan `<interfaces>` block
- `README.md` with the packed-format summary; empty `Compute/NAMERPack.compute` placeholder (Phase 2)
- Round-trip `NamerRoundTripSmokeTests` PlayMode test referencing `NamerFormat` (01-01) and `Shader.Find("GraffitiEntertainment.Namer/NAMER")` (01-02) — the intended failing end-to-end test

## Task Commits

Each task was committed atomically:

1. **Task 1: Create Unity project, install URP, write package.json + project manifest** - `b75f8df` (chore)
2. **Task 2: Create package directory layout + five assembly definitions** - `1cb6ca3` (feat)
3. **Task 3: Author the round-trip PlayMode smoke test (failing end-to-end test)** - `7e96e9a` (test)

## Files Created/Modified

- `Packages/manifest.json` - Project manifest declaring URP 17.0.4 (Unity resolves the rest)
- `Packages/com.graffitientertainment.namer/package.json` - Package identity (`com.graffitientertainment.namer`, `"unity": "6000.0"`, 4 deps + testables)
- `ProjectSettings/ProjectVersion.txt` - `m_EditorVersion: 6000.0.82f1`
- `Packages/com.graffitientertainment.namer/Core/GraffitiEntertainment.Namer.Core.asmdef` - Core assembly (`Unity.Mathematics`, autoReferenced)
- `Packages/com.graffitientertainment.namer/Runtime/GraffitiEntertainment.Namer.Runtime.asmdef` - Runtime assembly (`Core`, autoReferenced)
- `Packages/com.graffitientertainment.namer/Editor/GraffitiEntertainment.Namer.Editor.asmdef` - Editor assembly (`Core` + `Runtime`, Editor-only)
- `Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef` - EditMode test assembly (`Core`, testAssemblies, Editor-only)
- `Packages/com.graffitientertainment.namer/Tests/Runtime/GraffitiEntertainment.Namer.Tests.Runtime.asmdef` - PlayMode test assembly (`Core` + `Runtime`, testAssemblies)
- `Packages/com.graffitientertainment.namer/README.md` - Package description + packed-format summary
- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` - Empty compute placeholder (Phase 2)
- `Packages/com.graffitientertainment.namer/Tests/Runtime/NamerRoundTripSmokeTests.cs` - Walking-skeleton round-trip smoke test

## Decisions Made

- Hand-authored the Unity project skeleton (fallback path) rather than invoking `-createProject`, because the repo root is non-empty and `-createProject` would refuse it. This also avoided a Unity batchmode license gate.
- Skipped the fallback's batchmode "open once to generate ProjectSettings.asset" step: acceptance criteria are file-based, and a batchmode open would emit `Library/`/`Temp/` artifacts not covered by the pre-existing `.gitignore` (which I was instructed not to modify). Unity generates `ProjectSettings.asset` on first open.
- Authored the smoke test against Unity 6's actual `Texture2D` API (see deviations) while preserving the `R8G8B8A8_UNorm` linear-format contract via a `graphicsFormat` assertion.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] `.gitignore` `/packages/` entry ignored Unity's `Packages/` directory**

- **Found during:** Task 1 (and all subsequent `Packages/` adds)
- **Issue:** The pre-existing `.gitignore` line `/packages/` (intended for NuGet) matches Unity's `Packages/` on macOS's case-insensitive filesystem (`core.ignorecase=true`), so `git add Packages/...` failed with "paths are ignored".
- **Fix:** Force-added the specific files with `git add -f` (did NOT modify `.gitignore`, per the explicit "do not modify" instruction). Tracked files are no longer subject to `.gitignore`, so future edits to already-added files commit normally; new files under `Packages/` will also need `-f`.
- **Files modified:** none (`.gitignore` left untouched); `Packages/...` files force-added.
- **Verification:** All three task commits landed (`b75f8df`, `1cb6ca3`, `7e96e9a`); `git status` confirms pre-existing staged files remain untouched.
- **Committed in:** `b75f8df` (Task 1), `1cb6ca3` (Task 2), `7e96e9a` (Task 3)

**2. [Rule 1 - Bug / plan inaccuracy] Unity 6 `Texture2D` has no `GraphicsFormat` constructor**

- **Found during:** Task 3 (authoring the smoke test)
- **Issue:** The plan assumed `new Texture2D(1, 1, GraphicsFormat.R8G8B8A8_UNorm, TextureCreationFlags.None)`. Verified against the local Unity 6000.0.82f1 `UnityEngine.xml` + `UnityEngine.CoreModule.dll`: `GraphicsFormat` lives in `UnityEngine.Experimental.Rendering` (not `UnityEngine`), there is no `TextureCreationFlags` type (only `RenderTextureCreationFlags`), and `Texture2D` exposes no `GraphicsFormat`-based constructor (only legacy `TextureFormat` constructors).
- **Fix:** Constructed with `new Texture2D(1, 1, TextureFormat.RGBA32, false, true)` (`mipChain=false`, `linear=true` — `RGBA32` linear maps to `R8G8B8A8_UNorm`) and asserted `surfaceMap.graphicsFormat == GraphicsFormat.R8G8B8A8_UNorm` to keep the linear-format contract explicit and satisfy the plan's `R8G8B8A8_UNorm` grep.
- **Files modified:** `Packages/com.graffitientertainment.namer/Tests/Runtime/NamerRoundTripSmokeTests.cs`
- **Verification:** `grep` for `NamerFormat`, `Shader.Find`, `R8G8B8A8_UNorm`, `0.625`, `31` all present; enum members + `Texture.graphicsFormat` property confirmed in Unity 6 API docs.
- **Committed in:** `7e96e9a` (Task 3)

---

**Total deviations:** 2 auto-fixed (1 blocking, 1 plan/API inaccuracy)
**Impact on plan:** Both fixes necessary for correctness and to keep the linear packed-texture contract explicit. No scope creep — all changes within the plan's stated file set.

## Issues Encountered

- Unity license status is unverified (no `.ulf` license file found) and the pre-existing `.gitignore` lacks Unity entries (`Library/`, `Temp/`, `UserSettings/`). These do not block this plan (file-based acceptance), but will need resolution before 01-01/01-02 run tests in-editor: add Unity entries to `.gitignore` (or confirm license) before the first batchmode invocation.

## User Setup Required

None - no external service configuration required. (Unity 6000.0.82f1 + URP 17.0.4 already installed; license activation is a one-time concern for the next plan's in-editor test run, not this plan.)

## Known Stubs / Intentional Placeholders

- `Packages/com.graffitientertainment.namer/Compute/NAMERPack.compute` - empty (kernels land in Phase 2), per plan.
- `Packages/com.graffitientertainment.namer/Shaders/` - empty directory (plan 01-02 fills it; git does not track empty directories).
- `NamerRoundTripSmokeTests.cs` references `NamerFormat` (plan 01-01) and `Shader.Find("GraffitiEntertainment.Namer/NAMER")` (plan 01-02) which do not exist yet — this is the intended "failing end-to-end test" state, not an omission.

## Next Phase Readiness

- Core (`GraffitiEntertainment.Namer.Core`) and Runtime/Editor/Tests asmdefs are in place with the exact reference contract 01-01 and 01-02 compile against.
- The round-trip smoke test is wired to the `NamerFormat` API and the shader identifier; it turns green only after 01-01 (Core math) and 01-02 (URP shader) land.
- Blockers for 01-02's in-editor test run: add Unity `.gitignore` entries (`Library/`, `Temp/`, `Logs/`, `UserSettings/`, `obj/`) and confirm Unity license activation before batchmode.

## Self-Check: PASSED

All 11 created files and all 3 task commits verified present.

---

*Phase: 01-core-format-contract-runtime-decode*
*Completed: 2026-08-26*

---
phase: 01-core-format-contract-runtime-decode
plan: 01
subsystem: core
tags: [csharp, unity, octahedral, bit-packing, format-contract, nunit, editmode]

# Dependency graph
requires: [01-03]
provides:
  - NamerFormat octahedral encode/decode + alpha bit packing (pure C#, Core asmdef)
  - Headless EditMode test suite proving the format contract (30 tests)
affects:
  - 01-02 (URP runtime shader mirrors this contract in HLSL)
  - 01-03 (Tests/Editor asmdef modernized; manifest test-framework dependency added)

# Tech tracking
tech-stack:
  added: []
  patterns: [Core-purity (Unity.Mathematics only), golden-vector NUnit [TestCase] fixtures, strict-inequality bit thresholds]

key-files:
  created:
    - Packages/com.graffitientertainment.namer/Core/NamerConstants.cs
    - Packages/com.graffitientertainment.namer/Core/NamerFormat.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/OctahedralRoundTripTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/BitPackingTests.cs
    - Packages/com.graffitientertainment.namer/Tests/Editor/BlenderGoldenVectorTests.cs
  modified:
    - Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef
    - Packages/manifest.json

key-decisions:
  - "Corrected the round-trip test: the plan's dot(decode(encode(v)), normalize(v)) compared against the texel direction, not the recovered tangent normal (Pitfall 3); tests now map a unit normal -> DirectX texel -> encode -> decode and assert dot(decoded, normal) >= 1-1e-3"
  - "Removed -quit from the test command: -quit shuts the editor down before the test framework's async (EditorApplication.update) run starts; the test framework controls exit itself"
  - "Modernized the Tests/Editor asmdef from legacy optionalUnityReferences:['TestAssemblies'] to explicit UnityEngine.TestRunner/UnityEditor.TestRunner references (matches UTF 1.6.0 sample)"
  - "Added com.unity.test-framework as a direct manifest dependency so the UnityEditor.TestRunner assembly loads and -runTests actually runs"

requirements-completed: [ENCD-01, ENCD-02, ENCD-03, ENCD-05, TEST-01]

# Metrics
duration: 26min
completed: 2026-08-26
---

# Phase 1 Plan 01: Core Format Contract + Runtime Decode Summary

**Lock the NAMER packed surface format in a pure-C# Core assembly and prove it headless against the Blender reference — 30/30 EditMode tests pass**

## Performance

- **Duration:** 26 min
- **Started:** 2026-08-26T02:18:14Z
- **Tasks:** 2
- **Files modified:** 7 (5 created, 2 modified)

## Accomplishments

- `Core/NamerConstants.cs` — metallic/emissive/roughness bit masks (0x80/0x40/0x3F) and strict thresholds (0.5f / 0.1f), the single source of truth (D-02, D-03).
- `Core/NamerFormat.cs` — `OctahedralEncode` (barycentric projection, R/G ∈ [0.5, 1.0]), `OctahedralDecode` (full quadratic inverse, recovers unit tangent normal + DirectX green un-flip), `PackAlphaBits`/`UnpackAlphaBits` (strict `>` thresholds, linear 6-bit `floor`), `PackSurface`/`UnpackSurface` (AO = B channel, no transform). Pure C# — `Unity.Mathematics` only, zero `UnityEngine` references (D-03).
- Three headless EditMode test classes (`OctahedralRoundTripTests`, `BitPackingTests`, `BlenderGoldenVectorTests`).
- **Test run: 30/30 passed, 0 failed** (`/tmp/namer-editmode.xml`): BitPackingTests (12), BlenderGoldenVectorTests (5), OctahedralRoundTripTests (13). Golden vectors A–E match (float 1e-4, integer bits exact), including boundary cases C (0.5/0.1 → bit CLEAR) and B/E (bits 7/6 set).

## Task Commits

1. **Task 1: Core NamerConstants + NamerFormat** - `34a8e53` (feat) — committed cleanly (2 files).
2. **Task 2: Headless EditMode tests** - `c1ac414` (test) — committed (5 files: 3 test classes + asmdef + manifest).

## Files Created/Modified

- `Packages/com.graffitientertainment.namer/Core/NamerConstants.cs` — bit masks + thresholds (created)
- `Packages/com.graffitientertainment.namer/Core/NamerFormat.cs` — encode/decode/pack/unpack (created)
- `Packages/com.graffitientertainment.namer/Tests/Editor/OctahedralRoundTripTests.cs` — ENCD-01 round-trip (created)
- `Packages/com.graffitientertainment.namer/Tests/Editor/BitPackingTests.cs` — ENCD-02/03 bit + AO (created)
- `Packages/com.graffitientertainment.namer/Tests/Editor/BlenderGoldenVectorTests.cs` — ENCD-05 golden vectors (created)
- `Packages/com.graffitientertainment.namer/Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef` — modern test-assembly references (modified)
- `Packages/manifest.json` — `com.unity.test-framework` direct dependency (modified)

## Decisions Made

- Corrected the round-trip test semantics (see Deviations #1) — the plan's literal assertion compared against the texel direction, which is mathematically wrong per the research's own Pitfall 3.
- Adopted unit-normal → DirectX-texel → encode → decode → compare-against-normal as the round-trip oracle.
- Removed `-quit` from the batchmode test command (the plan's command quits before the async test run).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Round-trip test asserted against the wrong reference vector**

- **Found during:** Task 2 (authoring OctahedralRoundTripTests)
- **Issue:** The plan specified `dot(OctahedralDecode(OctahedralEncode(v)), normalize(v)) >= 1-1e-3` for `[0,1]` DirectX texels `v`. But `normalize(v)` is the direction of the *texel*, not the tangent normal the decode recovers (Pitfall 3). For the neutral texel this gives `dot((0,0,1),(0.408,0.408,0.816))=0.816`, which fails. The plan's "+X (1,0,0)" and "+Y (0,1,0)" texels are also not valid texels of any unit normal.
- **Fix:** Rewrote the round-trip to map a unit tangent normal → DirectX texel → encode → decode and assert `dot(decoded, normal) >= 1-1e-3`. Encode-invariant (R/G ∈ [0.5,1.0]) and neutral→(0.625,0.625) checks retained. All 13 OctahedralRoundTrip tests pass.
- **Files modified:** `Packages/.../Tests/Editor/OctahedralRoundTripTests.cs`

**2. [Rule 3 - Blocking] `-quit` quits before the async test run starts**

- **Found during:** Task 2 (running the suite)
- **Issue:** The plan's verify command ended in `-quit`. The UTF `TestStarter` registers the run on `EditorApplication.update` (async); `-quit` shuts the editor down before that callback fires, so `-runTests` produced no run and no results file.
- **Fix:** Removed `-quit` (the test framework calls `EditorApplication.Exit` itself on completion). Tests then ran and reported 30/30 passed.
- **Files modified:** none (command-line only)

**3. [Rule 3 - Blocking] Legacy test-assembly asmdef format not recognized in Unity 6**

- **Found during:** Task 2
- **Issue:** The 01-03 `Tests/Editor` asmdef used `optionalUnityReferences: ["TestAssemblies"]` (pre-2019.3 legacy) and omitted `UnityEngine.TestRunner`/`UnityEditor.TestRunner` references.
- **Fix:** Modernized to explicit `references: ["UnityEngine.TestRunner","UnityEditor.TestRunner","GraffitiEntertainment.Namer.Core","Unity.Mathematics"]`, `autoReferenced: true`, removed the legacy field — matching the UTF 1.6.0 sample.
- **Files modified:** `Packages/.../Tests/Editor/GraffitiEntertainment.Namer.Tests.Editor.asmdef`

**4. [Rule 3 - Blocking] Test framework not a direct dependency**

- **Found during:** Task 2
- **Issue:** `com.unity.test-framework` was only a transitive dependency (via collections), so its `UnityEditor.TestRunner` assembly (which processes `-runTests`) was not reliably loaded.
- **Fix:** Added `com.unity.test-framework: 1.6.0` to `Packages/manifest.json` `dependencies` (plus `testables`).
- **Files modified:** `Packages/manifest.json`

---

**Total deviations:** 4 auto-fixed (1 plan math bug, 3 blocking test-infrastructure fixes)

## Issues Encountered

### RESOLVED: 1Password SSH commit signing agent failing

- **Status:** RESOLVED — the user restarted 1Password (its SSH signing agent re-initialized), and commit signing was verified working before this continuation.
- **Symptom:** `git commit` failed with `error: 1Password: agent returned an error` / `failed to fill whole buffer` / `fatal: failed to write commit object`.
- **Root cause:** `git config` has `commit.gpgsign=true`, `gpg.format=ssh`, and `gpg.ssh.program=/Applications/1Password.app/Contents/MacOS/op-ssh-sign`. The 1Password main process was stuck in a `--just-updated --should-restart` state, so `op-ssh-sign` could not produce signatures. This surfaced mid-session (Task 1's commit `34a8e53` succeeded before the agent broke).
- **Resolution:** Human-action checkpoint (restart 1Password) was completed; the Task 2 commit (`c1ac414`) and this final metadata commit now sign cleanly.

### Non-blocking

- Unity generated project/URP files on first batchmode open (`Library/`, `Logs/`, `UserSettings/`, `ProjectSettings/*.asset`, `Assets/DefaultVolumeProfile.asset`, `Assets/UniversalRenderPipelineGlobalSettings.asset`, `Packages/packages-lock.json`, and a `ProjectSettings/ProjectVersion.txt` edit). Left untracked/uncommitted (not part of this plan). The pre-existing `.gitignore` lacks Unity entries (`Library/`, `Temp/`, `Logs/`, `UserSettings/`); 01-03 already flagged this for a follow-up.
- Unity license is active (Unity Personal, via Unity Hub IPC) — the "no .ulf" concern from 01-03 did not block this plan.

## Known Stubs / Intentional Placeholders

None in this plan's files. Core and tests are fully implemented (no TODO/FIXME/placeholder or empty-data paths).

## Next Phase Readiness

- The format contract is locked and proven; plan 01-02 mirrors `NamerFormat` in HLSL.
- `Tests/Runtime/GraffitiEntertainment.Namer.Tests.Runtime.asmdef` still uses the legacy `optionalUnityReferences: ["TestAssemblies"]` format — plan 01-02 should modernize it (same fix as Deviations #3) before its PlayMode smoke test runs.

## Self-Check: PASSED

- Task 1 commit `34a8e53` verified present (Core files, 2 files, 123 insertions).
- Task 2 commit `c1ac414` verified present (3 test classes + asmdef + manifest, 5 files, 194 insertions / 6 deletions, no deletions of tracked files).
- Test run verified 30/30 passed, 0 failed (`/tmp/namer-editmode.xml`).
- Pre-existing staged files (`.gitignore`, `.idea/**`, `NAMER_UNITY_PLUGIN_PRD.md`, `Namer-Unity.sln`) remain staged and were NOT swept into the Task 2 commit.

---

*Phase: 01-core-format-contract-runtime-decode*
*Completed: 2026-08-26 (code + tests committed; 30/30 pass)*

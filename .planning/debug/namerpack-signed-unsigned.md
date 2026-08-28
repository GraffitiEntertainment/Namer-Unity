---
status: diagnosed
trigger: "UAT Phase 3, Test 1, gap 4: 6 Metal shader warnings - 'Shader warning in NAMERPack: signed/unsigned mismatch, unsigned assumed at kernel CSNormalize/CSOctahedralEncode/CSSurfacePack at NAMERPack.compute(80) and (92)'"
created: 2026-08-27T00:00:00Z
updated: 2026-08-28T00:00:00Z
---

## Current Focus
<!-- OVERWRITE on each update - reflects NOW -->

hypothesis: CONFIRMED - NAMERPack.compute declares `int2 _Size;` (:43) but every kernel's bounds guard compares its members against the unsigned `uint3 id : SV_DispatchThreadID` components: `if (id.x >= _Size.x || id.y >= _Size.y)` at :48 (CSNormalize), :80 (CSOctahedralEncode), :92 (CSSurfacePack). HLSL promotes the int operands to unsigned for the comparison; Metal's compiler flags each site - 2 sites x 3 kernels = the user's 6 warnings. Purely diagnostic noise: w/h are always positive so guard math is identical either way (all 53 tests green).
test: Read NAMERPack.compute + NamerEncode.hlsl in full (all _Size uses); read NamerComputePipeline.BindAndDispatch for how _Size is bound.
expecting: A signed/unsigned comparison in shared guard code emitted into all three kernels, and a C# binding that is agnostic to the member's signedness.
next_action: Root cause found - return diagnosis (find_root_cause_only). No fix applied.

## Symptoms
<!-- Written during gathering, then IMMUTABLE -->

expected: NAMERPack.compute compiles without warnings on Metal.
actual: 6 shader warnings: "Shader warning in 'NAMERPack': signed/unsigned mismatch, unsigned assumed at kernel CSNormalize/CSOctahedralEncode/CSSurfacePack at NAMERPack.compute(80)" and the same three at (92), on metal.
errors: Warning-level only; no functional failure. Packed output is correct (GPU golden tests pass).
reproduction: Dispatch any NAMERPack kernel on Metal - every preview recompute (AO slider, selection change) and every Process run logs the warnings.
started: Discovered during UAT after Phase 3 completion. (The warnings appear per-kernel-per-site; the exact reported line numbers depend on how the Metal backend attributes inlined guard sites across kernels.)

## Eliminated
<!-- APPEND only - prevents re-investigating -->

- hypothesis: The mismatch is inside NamerEncode.hlsl helpers shared by the kernels
  evidence: Full read of NamerEncode.hlsl (67 lines) - `_Size` is not referenced; all constants are already `uint`/`float`; the pack helpers take floats and one `uint` return. The comparison sites exist only in NAMERPack.compute's kernel bodies.
  timestamp: 2026-08-28

- hypothesis: The warnings come from URP Color.hlsl includes
  evidence: The user's warnings all name kernel-local lines (80/92) inside NAMERPack.compute, not the include; SRGBToLinear (the only Color.hlsl call, :54) takes/returns floats.
  timestamp: 2026-08-28

- hypothesis: `_Size` signedness matters to behavior (guard could mis-evaluate)
  evidence: C# binds `_compute.SetInts("_Size", new[] { w, h })` (NamerComputePipeline.cs:173) with w/h >= 1 always (DefaultResolution 256 fallback); comparing positive values as unsigned vs signed is bit-identical, and all 53 EditMode tests incl. GPU golden vectors pass. No behavioral defect - warning noise only.
  timestamp: 2026-08-28

## Evidence
<!-- APPEND only - facts discovered -->

- timestamp: 2026-08-28
  checked: NAMERPack.compute - all `_Size` references
  found: Declaration `int2 _Size;` (:43) and exactly three uses, all the identical guard pattern `if (id.x >= _Size.x || id.y >= _Size.y)` at :48, :80, :92. `id` is `uint3 SV_DispatchThreadID` in all three kernels. No other uses of `_Size` in the file (or in NamerEncode.hlsl).
  implication: One declaration change removes every mismatch site.

- timestamp: 2026-08-28
  checked: C# binding of `_Size` (NamerComputePipeline.BindAndDispatch :173)
  found: `_compute.SetInts("_Size", new[] { w, h })` - SetInts marshals raw 32-bit values into the constant buffer; the HLSL member's declared signedness does not change the bytes on the wire, and the values are always positive.
  implication: Changing `int2 _Size;` to `uint2 _Size;` requires NO C# change; both sides of the comparison become unsigned and Metal compiles clean.

- timestamp: 2026-08-28
  checked: Blast radius of the declaration change
  found: `_Size` feeds only the out-of-bounds early-return guards. Texture indexing uses `id.xy` directly. Existing EditMode tests (GpuGoldenTests, ComputeSmokeTests, OctahedralRoundTripTests, AssetGeneratorTests) dispatch all three kernels and assert packed-output values - a full regression net for the change.
  implication: Single-word fix, fully covered by existing tests.

## Resolution
<!-- OVERWRITE as understanding evolves -->

root_cause: Signed/unsigned comparison mismatch in NAMERPack.compute's kernel bounds guards: `int2 _Size` members compared against `uint3 SV_DispatchThreadID` components at :48/:80/:92 (2 comparison sites per guard x 3 kernels = 6 Metal warnings). The C# side always uploads positive width/height via ComputeShader.SetInts, so behavior is unaffected - the warnings are pure console noise that also reinforced gap 1's slider-to-error misattribution (warnings appear exactly when the debounced preview recompute dispatches, during slider interaction).

fix: (for plan-phase --gaps; not applied - find_root_cause_only) Change the declaration `int2 _Size;` -> `uint2 _Size;` (NAMERPack.compute:43). One word; comparisons become uint-vs-uint; no C# change needed (SetInts uploads bit-identical positive values).

verification: Full reads of NAMERPack.compute and NamerEncode.hlsl confirming _Size's complete use set; NamerComputePipeline.cs:173 confirming the binding is signedness-agnostic; existing GPU golden/smoke tests as the regression net; warning-free compilation confirmed in the live editor (or clone-runner log) after the fix.

files_changed: []

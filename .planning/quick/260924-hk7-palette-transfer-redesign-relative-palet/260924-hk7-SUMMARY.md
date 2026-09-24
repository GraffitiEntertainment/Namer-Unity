---
quick: 260924-hk7
title: Palette transfer redesign — relative ramp + Tone Match
status: complete
mode: quick
commits:
  - 2582b3b
  - 2a7bd3e
verification: grep-gates-passed, roslyn-compile-equivalence-passed, live-editmode-deferred
---

# Quick 260924-hk7: Palette Transfer Redesign — Relative Palette Ramp + Tone Match

## What Changed

CSPaletteTransfer is no longer an absolute-snap operator. The redesign (quick
260924-hk7) fixes two user-reported defects:

1. **Absolute luma flattening** — the old kernel lerped texel luma toward the
   matched anchor's ABSOLUTE luma via `_StyleStrength`, so dark browns styled
   toward a light anchor lost their tonality. Luma is now governed by an
   independent `_ToneMatch` control: `outLuma = lerp(srcLuma, anchor.z,
   toneMatch)` — 0 (the default) preserves the source's own luma exactly.
2. **Achromatic lifting/tinting** — with real extracted anchors (k-means
   centroids all sat >= 0.20, above the 0.15 achromatic cutoff), achromatic
   texels fell into the chromatic fallback and got lifted/tinted (black eyes
   turned gray). Achromatic texels now keep their saturation verbatim and are
   matched by luma, never tinted.

Saturation is **boost-only**: `outSat = max(srcSat, lerp(srcSat, anchor.y,
strength))` — a palette can add vibrance but never desaturate the source. Hue
ramping is gated on `strength > 0` in BOTH hue modes (tone-only dispatches
never snap hue, even under Replace). Passthrough fires when `_AnchorCount ==
0` OR `(strength <= 0 && toneMatch <= 0)`.

`_ToneMatch` joins the unconditional Stage 3 property set (Unity 6/Metal
skips dispatches on unbound kernel properties — the exact-zero-output pitfall
in project memory). `NamerStyleProfile.ToneMatch` defaults to 0 via
`NamerEditorConstants.DefaultToneMatch`, so old profile assets deserialize to
the safe no-migration default automatically. `ClampToCaps` sanitizes NaN ->
default before clamping (T-hk7-01: `Mathf.Clamp` propagates NaN and the
Create()/Run Clamp01 chain would ship it to the GPU). The inspector gains a
"Tone Match" slider; Style Strength's tooltip now scopes it to hue + vibrance.

## Task 1 — Kernel + ToneMatch wiring end-to-end (2582b3b)

Files (5):

- `Packages/com.graffitientertainment.namer/Compute/NAMERStylize.compute` —
  operator redesign (relative luma via `_ToneMatch`, boost-only sat, gated hue
  ramp, extended passthrough); anchor matching verbatim.
- `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStylizePipeline.cs` —
  `NamerStyleStylization.ToneMatch` field + `Create()` clamp; `Active` includes
  tone-only dispatches; Stage 3 sets `_ToneMatch` unconditionally next to
  `_StyleStrength`.
- `Packages/com.graffitientertainment.namer/Editor/NamerEditorConstants.cs` —
  `DefaultToneMatch = 0f`.
- `Packages/com.graffitientertainment.namer/Editor/Stylize/NamerStyleProfile.cs` —
  serialized `ToneMatch` slider field + `ClampToCaps` NaN/clamp sanitization.
- `Packages/com.graffitientertainment.namer/Editor/UI/NamerStyleProfileEditor.cs` —
  Tone Match slider; Style Strength tooltip semantics amendment.

## Task 2 — Pinned test suite re-spec (2a7bd3e)

Files (3):

- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizeKernelTests.cs` —
  all palette pins re-specced to the new operator with no-clip fixtures
  re-derived through documented HSV unit-luma K/V math (boost-only sat means
  sat can no longer drop, so the old sat-1 fixtures clipped and bent hue/luma
  readbacks): hue preserved at mid strength; boost-only sat reaches toward
  higher anchor sat / never desaturates below source; Replace and Preserve
  full-strength hue convergence; toneMatch-full luma snap; achromatic texel
  matched by luma not hue (dual toneMatch 0/1); achromatic texel never tinted
  against all-chromatic anchors; zero-controls passthrough copy; identity
  context extended with tone-only cases. New `CreatePaletteContext` toneMatch
  overload + `HsvUnit`/`HsvColor`/`MaxAbsChannelDelta` oracle helpers.
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStylizationIntegrationTests.cs` —
  anchor fixture moved to the no-clip 0.2/0.6/0.4 green-yellow family;
  `Stylization_ActiveReplace_StylingFlowsIntoProjectionAndResult` now runs a
  null-stylization control (with its own projection context — SourceBaseColor
  only exists when projection ran) and asserts boost-only sat 1, luma-median
  preservation vs control, and the fitted-VC anchor hue family; NEW
  `Stylization_ToneMatchZero_PreservesAlbedoLumaStructure` two-half fixture
  test (per-texel styled luma preservation + projected strip-median ordering
  and control match, justified by the linearity of the chroma swap through
  the LSQ fit); E2E saturation oracle re-derived through the clipped magenta
  rebuild (V = luma/K(5/6,1) >= 1.6 -> every texel (1,0,1) -> bytes (255,0,255)
  -> byte saturation exactly 1). Dead helpers `TieWinningChromaticAnchor` and
  `ToColor32` removed; `AnchorLinearColor` kept (profile fixture still uses it).
- `Packages/com.graffitientertainment.namer/Tests/Editor/NamerStyleProfileTests.cs` —
  `DefaultToneMatch` creation-default pin + new
  `Profile_ClampToCaps_ClampsToneMatch` (-1 -> 0, 2 -> 1, NaN -> default).

## Verification

- Task 1 grep gates: all 7 passed (`_ToneMatch` uniform/pipeline/profile/
  constants/editor/tooltip/Active-tone-only each exactly 1+).
- Task 2 grep gates passed: kernel pins 3 (`NeverTinted|ToneMatchFull|
  LumaPreserved`), profile `DefaultToneMatch` 2, integration `toneMatch` 9.
- Roslyn compile-equivalence (04.2/04.3 precedent; Unity's bundled csc at
  6000.0.82f1 via the generated csproj reference closures, real assembly
  names): `GraffitiEntertainment.Namer.Core` rc=0,
  `GraffitiEntertainment.Namer.Editor` rc=0,
  `GraffitiEntertainment.Namer.Tests.Editor` rc=0. Pre-existing CS0436
  warnings in the Tests build (csproj double-includes Editor sources beside
  the reference dll) are unchanged and out of scope.
- **Live EditMode run deferred to the orchestrator's post-wave regression
  gate** — the Unity Editor holds the project lock; batch mode banned for
  this executor. The pinned suites above are the re-spec the live run must
  execute: `NamerStylizeKernelTests`, `NamerStylizationIntegrationTests`,
  `NamerStyleProfileTests`.

## Threat Register Dispositions

- **T-hk7-01 (applied):** `ClampToCaps` NaN fallback + [0,1] clamp on
  ToneMatch, pinned by `Profile_ClampToCaps_ClampsToneMatch`.
- **T-hk7-02 (applied):** every dispatch path sets `_ToneMatch`
  unconditionally (Stage 3), documented beside the Metal unbound-property
  comments; kernel tests cover tone-only dispatches.
- **T-hk7-03 (applied):** defaults (ToneMatch 0, boost-only sat, gated hue)
  are the safe behavior for legacy assets; creation-default pinned in
  `Profile_CreateInstance_HasUiSpecDefaults`.

## Notes for the Live Run

- Fixture math is documented inline at each pin (K = luma(HsvUnit(h,s)),
  V = luma/K < 1 required). If a hue/luma pin fails by a small consistent
  bias, re-check the V < 1 no-clip condition first — clipping bends both
  hue and luma readbacks (project memory: NAMER test fixtures vs AO
  un-multiply has the analogous lesson).
- The E2E byte-saturation oracle is now exactly 1.0 (clipped magenta), not
  the old tie-winning-anchor derivation.

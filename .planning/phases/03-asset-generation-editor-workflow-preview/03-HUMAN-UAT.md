---
status: verified
phase: 03-asset-generation-editor-workflow-preview
source: [03-VERIFICATION.md]
started: 2026-08-27
updated: 2026-08-28
---

## Current Test

[testing complete]

## Tests

### 1. Interactive window flow (preview + debug channels + AO recompute)

Open `Tools > NAMER > Processor`, select a textured FBX or material, drag the AO un-multiply slider, orbit/zoom the preview, and click through all 7 toolbar modes.

expected: Before pane shows the source material; after pane shows NAMER (Shaded) or the clicked debug channel immediately (CR-02); no magenta panes; preview updates after ~300 ms without any asset written to disk.
result: pass
reported: "If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset. Also when I rotate the images, the before and after should move or be in the scene to rotate with the object. And I don't see a AO recompute button? Plus 6 signed/unsigned mismatch shader warnings from NAMERPack.compute (lines 80, 92) on metal."
severity: blocker
verified: "2026-08-28 — user confirmed the fixed preview shades correctly on the Neo T-pose after the normal-layout fix (9628fe5): 'everything looks correct now' / 'it is shaded correctly'. Covers the five original gaps (6062b19, 3b46ce8, 2bbd2ea, 191a9fb) plus gaps 6-7 below; no further preview complaints in two re-verification rounds."

### 2. End-to-end Process from the live window

Press `Process with NAMER` in the window on a real textured FBX; inspect the Project window; re-run with Overwrite generated enabled.

expected: Material + `_Base.png` + `_Surface.png` under `NAMERGenerated/{source}/` labeled `NamerGenerated`; status reads "Generated 1 material(s) under Assets/NAMERGenerated/"; source shows no modification; stamped re-run replaces cleanly.
result: pass
reported: "re-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png"
severity: major
verified: "2026-08-28 — user re-ran Process with Overwrite generated enabled and confirmed the stamped re-run replaces cleanly and tripo_mat_d83278e6_Namer_Base.png is now the real base color (28b0371); decoded PNG stats: surface oct avg (159,159) with spatial variation, B=255, A=63."

## Summary

total: 2
passed: 2
issues: 0
pending: 0
skipped: 0
blocked: 0

## Gaps

<!-- YAML format for plan-phase --gaps consumption -->
- truth: "Dragging the AO un-multiply slider recomputes the preview in ~300 ms in memory, with no asset written to disk and no error"
  status: fix_applied
  reason: "User reported: 'If I move AO down ao unmultiply-strength, it gives an error that the process is block refusing to overwrite non-generated asset' — the slider recompute path hits the generator's overwrite gate instead of staying preview-only (D-10)"
  severity: blocker
  test: 1
  root_cause: "The slider path never touches the gate — D-10 holds in code (slider → Tick debounce → RecomputePreview → NamerComputePipeline.Process only; zero AssetDatabase/File write calls on the path, verified by full call-graph trace). The error came from a separate 'Process with NAMER' button press (stack in the user's console proves it: NamerProcessor.Process ← RunProcess ← DrawActionSection) made during slider work because the debounced in-memory recompute has no visible affordance (gap 3); that press was refused by EnsureWritableTarget because the session's earlier successful run had already written Assets/NAMERGenerated/Neo-T-Pose/ and OverwriteGenerated defaults to false, with a message that mislabels stamped assets as 'non-generated' (gap 5 root cause)"
  artifacts: [".planning/debug/ao-slider-overwrite-gate.md", ".planning/debug/ao-recompute-affordance.md", ".planning/debug/rerun-overwrite-stamp.md"]
  missing: []
  debug_session: "ao-slider-overwrite-gate"
  fix_commit: "6062b19"
- truth: "Orbit interaction matches the standard Unity object-preview expectation (drag rotates the object/scene; before/after panes track it)"
  status: fix_applied
  reason: "User reported: 'when I rotate the images, the before and after should move or be in the scene to rotate with the object'"
  severity: minor
  test: 1
  root_cause: "Rotation-ownership mismatch in NamerPreviewRenderer: orbit state drives the CAMERA around the world-origin pivot (ApplyCamera: position = rotation * (0,0,-distance)) while both mesh instances are drawn with Quaternion.identity at fixed ±0.7 translations — a drag swings the fixed-orientation pair through the frame (objects arc across panes without rotating) instead of spinning each object in place as Unity's Inspector preview does. The mouse-event chain (hit-test → Orbit → Use → Repaint, NamerEditorWindow:418-438) is fully functional; the camera-orbit model was a deliberate-but-mismatched design choice documented in the class header"
  artifacts: [".planning/debug/preview-orbit-expectation.md"]
  missing: []
  debug_session: "preview-orbit-expectation"
  fix_commit: "3b46ce8"
- truth: "The AO recompute path is discoverable in the window UI (automatic ~300 ms debounced recompute is apparent, or an explicit affordance exists)"
  status: fix_applied
  reason: "User reported: 'I don't see a AO recompute button?'"
  severity: minor
  test: 1
  root_cause: "The 300 ms debounce works (Tick wired to EditorApplication.update; DebounceSeconds = 0.3) but the UI communicates nothing: the slider is a plain label with no tooltip, successful recompute silently clears _status, and _recomputing is set/cleared synchronously around the compute call so the disabled-Process cue never renders — the recompute is functionally happening but completely invisible, which steered the user to the disk-writing Process button (gap 1's misattribution)"
  artifacts: [".planning/debug/ao-recompute-affordance.md"]
  missing: []
  debug_session: "ao-recompute-affordance"
  fix_commit: "2bbd2ea"
- truth: "Re-running Process with Overwrite generated enabled replaces the assets the first run itself generated (NamerGenerated-stamped)"
  status: fix_applied
  reason: "User reported: 're-run doesn't allow overwriting the generated assets says only NamerGenerated-stampped assets trying to overwrite the surface.png' — same gate message as the AO-slider error, suggesting the stamp applied at generation is not detected on the next run"
  severity: major
  test: 2
  root_cause: "NOT a stamp-detection failure: on-disk .meta files from the user's run all carry 'labels: [NamerGenerated]' — run 1's Stamp() persisted, so stamped == true at re-run. The refusal is the OTHER arm of EnsureWritableTarget's '!stamped || !overwriteGenerated': the re-run executed with the Overwrite toggle off (EditorPrefs default false; nothing near the error names the toggle), and the gate's single conflated message then asserted the stamped asset was 'non-generated' — a false explanation the user (correctly seeing their assets stamped) reported as 'stamp not detected'. A test coverage hole let it ship: OverwriteGating_RefusesNonStampedTarget manually re-stamps the target via AssetDatabase.SetLabels, so 'first run's own stamp round-trips into a successful overwrite re-run' was never asserted"
  artifacts: [".planning/debug/rerun-overwrite-stamp.md", ".planning/debug/ao-slider-overwrite-gate.md"]
  missing: []
  debug_session: "rerun-overwrite-stamp"
  fix_commit: "6062b19"
- truth: "NAMERPack.compute compiles without warnings on Metal"
  status: fix_applied
  reason: "User reported 6 shader warnings: 'Shader warning in NAMERPack: signed/unsigned mismatch, unsigned assumed' at NAMERPack.compute(80) and (92) in kernels CSNormalize, CSOctahedralEncode, CSSurfacePack (on metal)"
  severity: minor
  test: 1
  root_cause: "NAMERPack.compute declares 'int2 _Size' (line 43) but each kernel's bounds guard compares its members against the unsigned 'uint3 id : SV_DispatchThreadID' components — 'if (id.x >= _Size.x || id.y >= _Size.y)' at lines 48/80/92; 2 comparison sites × 3 kernels = 6 Metal 'signed/unsigned mismatch, unsigned assumed' warnings. Behavior unaffected (C# uploads positive w/h via SetInts; all 53 tests green) — pure console noise that fires on every preview recompute, reinforcing gap 1's slider-to-error misattribution"
  artifacts: [".planning/debug/namerpack-signed-unsigned.md"]
  missing: []
  debug_session: "namerpack-signed-unsigned"
  fix_commit: "191a9fb"
- truth: "Packed octahedral normals decode back to the authored per-texel directions and the NAMER material lights correctly against a moving light"
  status: fix_applied
  reason: "User reported during re-verification: 'Normals aren't working correct and do not looks like a normal map texture at all and isn't lighting correctly with the normal map part' — broken surface PNG had oct R avg 192 (expected ~159 for near-neutral normals)"
  severity: blocker
  test: 1
  root_cause: "Unity's NormalMap importer delivers DXT5nm/AG-swizzled texels on the GPU (X in ALPHA, R forced white, B unreliable — measured live: delivered R=255 const, authored R 43-218), while CSOctahedralEncode fed the raw [0,1] texel straight to the Blender-mirrored encode with no layout handling. Proven numerically: normalize(1.0, 0.494, 0.494) encodes to oct (191.7, 158.2) = exactly the observed broken PNG (avg 191.8/158.9). Fix: unpack X via the UnpackNormalmapRGorAG trick (texel.x *= texel.w) and rebuild Z from the signed XY so the encode receives the authored raw DirectX texel; the format contract (Blender golden vectors, neutral byte 159) is untouched. Added AG-vs-RGB layout-equivalence tests at kernel and full-Process level."
  artifacts: []
  missing: []
  fix_commit: "9628fe5"
  verified: "2026-08-28 — live dispatch on the real 2048px DXT5 texture produced oct avg (159,159) ranges [135-181]; regenerated surface PNG matches; user confirmed lighting correct"
- truth: "The generated _Base.png contains the sRGB-encoded normalized base color with alpha 255"
  status: fix_applied
  reason: "User-reported during re-verification: saved _Namer_Base.png uniform (205,205,205,205) despite the preview shading correctly — corruption confined to the save path"
  severity: major
  test: 2
  root_cause: "Graphics.ConvertTexture converts on the GPU only and never updates the destination Texture2D's CPU-side pixel data, which ImageConversion.EncodeToPNG encodes — the PNG carried uninitialized new-Texture2D memory (uniform 0xCD = 205 in all four channels, alpha included despite the kernel writing a=1.0, which would honestly save as 255). The surface PNG was unaffected (its CPU data came straight from LoadRawTextureData and never round-tripped through the GPU) and the preview was unaffected (binds the result RTs directly). Fix: blit NormalizedBaseColor through the existing NamerRawCopy material with _REENCODE_SRGB (explicit IEC 61966-2-1 encode, color-space independent) into a linear R8G8B8A8_UNorm temp RT, read that back, and load it into the sRGB-declared texture. The regression test oracle was strengthened from a weak byte-greater assertion (which uniform garbage 205 satisfied) to the exact expected byte (authored 128 linear -> 188 +/-1) with alpha 255."
  artifacts: []
  missing: []
  fix_commit: "28b0371"
  verified: "2026-08-28 — user re-processed and confirmed tripo_mat_d83278e6_Namer_Base.png is created correctly; clone EditMode suite 59/59"

## Notes

- AO extraction: user reported 'the AO extraction is always fully white' — verified by-design for this source (no _OcclusionMap on the material, no AO texture on disk; white 1.0 fill packs as B=255; the un-multiply slider is a mathematical no-op when ao=1). Extracting AO baked into the base color (PRD pipeline steps 5-6) is NOT implemented — routed to inserted Phase 03.1 rather than closed as a Phase 3 gap.
- Clone EditMode suite after all fixes: 59/59 (was 55 at first re-verification).

# Phase 1 Discussion Log — 2026-08-25

**Mode:** `--auto` (auto-selected recommended options; no interactive questions)

## Areas & Auto-Selections

### Octahedral Encoding & Blender Equivalence
- Q: "Which octahedral variant?" — Options: mirror Blender reference / standard oct+fix (Meyer et al.) / Claude's choice
- Selected: **Mirror Blender reference** (decode-equivalence is success criterion #2; researcher must extract exact algorithm)

### Core Assembly Purity
- Q: "How pure should Core be?" — Options: pure C# no UnityEngine.Object / allow UnityEngine math types / Unity-dependent
- Selected: **Pure C# asmdef, no UnityEngine.Object dependencies, manually mirrored in HLSL**

### Shader Authoring
- Q: "Shader Graph or hand-written HLSL?" — Options: hand-written HLSL / Shader Graph / hybrid
- Selected: **Hand-written URP HLSL** (bit-unpacking infeasible in Shader Graph; locked in stack research)

### Round-Trip Testing Strategy
- Q: "How to verify?" — Options: CPU golden vectors with fixed tolerances / pixel-diff GPU tests / property-based only
- Selected: **CPU golden vectors + fixed tolerances; GPU-vs-CPU deferred to Phase 2; Blender-derived fixtures for equivalence**

### Unity Baseline & Package Layout
- Q: "Unity version and layout?" — Options: Unity 6000.0 LTS / 6000.3 / 2022.3
- Selected: **Unity 6000.0 LTS minimum, URP 17.x, PRD package layout with asmdefs per folder**

## Deferred Ideas
None raised.

## Notes
- All decisions were auto-selected recommended defaults per `--auto`. User should review 01-CONTEXT.md before execution if they want to override any decision.

# Phase 1: Core Format Contract + Runtime Decode - Context

**Gathered:** 2026-08-25
**Status:** Ready for planning
**Mode:** auto (recommended defaults selected; review before execution if desired)

<domain>
## Phase Boundary

Define the NAMER packed surface format once in a pure-C# `Core` assembly (octahedral normal encode/decode, AO channel, alpha bit packing: bit 7 metallic, bit 6 emissive, bits 0–5 six-bit roughness), decode it in a hand-written URP runtime shader (including base/residual + vertex-color reconstruction path and emissive-metadata/transparency support), and scaffold the UPM package (`com.graffitientertainment.namer`) with assembly definitions and headless tests. GPU kernels, source material inspection, asset generation, editor UI, and stylization are OUT of this phase — this phase locks the format contract and proves the runtime decode.

</domain>

<decisions>
## Implementation Decisions

### Octahedral Encoding & Blender Equivalence
- **D-01:** The octahedral normal encoding/decoding MUST mirror the algorithm used by `GraffitiEntertainment/BlenderNamerPlugin` (develop branch) exactly — decode-equivalence with the Blender reference is a hard success criterion (ENCD-05), not a nicety. Research the Blender implementation's exact variant (fold handling, sign encoding, normalization) before implementing Core.
- **D-02:** Roughness quantization (6-bit) and metallic/emissive bit placement are fixed by the PRD: A bit 7 = metallic, A bit 6 = emissive, A bits 0–5 = roughness (64 values). No remapping freedom unless the Blender reference does it — if Blender remaps (e.g., perceptual sqrt before quantize), mirror that.

### Core Assembly
- **D-03:** `Core` is a pure-C# assembly (net-standard-compatible asmdef) with NO `UnityEngine.Object` dependencies (plain math types only). It is the single source of truth for the format, mirrored manually in HLSL. All bit-packing/octahedral/color math lives here so it is headless-testable.

### Shader
- **D-04:** Runtime NAMER shader is hand-written URP HLSL (ShaderLab + HLSLPROGRAM), NOT Shader Graph. Rationale: bit-unpacking from the alpha channel and octahedral decode are awkward/impossible in Shader Graph, and a comparison path against URP Lit is required for development.
- **D-05:** The shader includes the vertex-color reconstruction path (`BaseColor ≈ VertexColorInterpolation × ResidualColor`) from day one even though decomposition lands in Phase 4, so the decode contract is complete.

### Testing
- **D-06:** Headless CPU tests are the primary verification: round-trip tests for octahedral normals, bit packing (metallic/emissive/roughness), AO preservation, with fixed tolerances (normal decode within ~1/255 per component; roughness within 1 quantization step; metallic/emissive exact bits).
- **D-07:** Blender-equivalence is verified via golden vectors: encode known inputs in Core and compare against expected values derived from the Blender reference implementation (either documented expected outputs or vectors produced by running the Blender plugin once and committing the fixtures).
- **D-08:** GPU-vs-CPU comparison tests belong to Phase 2 (kernels); Phase 1 shader verification is visual/comparison against URP Lit on the same source material plus a play-mode smoke test if practical.

### Baseline & Package
- **D-09:** Unity 6000.0 LTS minimum (`"unity": "6000.0"` in package.json), URP 17.x. Hand-written HLSL compute (.compute files are scaffolded but kernels are Phase 2).
- **D-10:** Package layout per PRD: `com.graffitientertainment.namer/` with Runtime/, Editor/, Shaders/, Compute/, Tests/ (Editor + Runtime), each with asmdefs. Package lives in `Packages/` of a Unity project for development.

### Claude's Discretion
- Exact class/file names within Core and Shaders (PRD lists suggested names; treat as non-binding).
- Test framework scaffolding details (EditMode vs PlayMode split for shader smoke tests).
- Whether Blender golden vectors are committed as JSON/fixtures — planner's choice with researcher input.

</decisions>

<canonical_refs>
## Canonical References

**Downstream agents MUST read these before planning or implementing.**

### Project definition
- `NAMER_UNITY_PLUGIN_PRD.md` — authoritative PRD: NAMER runtime representation (bit layout), source material support, validation list, key design rules
- `.planning/PROJECT.md` — project context, constraints, out-of-scope boundaries
- `.planning/REQUIREMENTS.md` — ENCD-01..06, SHDR-01..04, TEST-01, PKG-01 are this phase's requirements

### Research
- `.planning/research/STACK.md` — Unity 6 LTS/URP 17.x baseline, Unity.Mathematics constraints (no SVD), GraphicsFormat guidance
- `.planning/research/ARCHITECTURE.md` — Core/Runtime/Editor assembly split, one-way data flow, component boundaries
- `.planning/research/PITFALLS.md` — sRGB/linear contract (pitfall #1), compression corruption (#7), untestable-GPU (#9)

### External reference (not in repo)
- `GraffitiEntertainment/BlenderNamerPlugin` (GitHub, develop branch) — the reference implementation for NAMER encoding. Researcher must locate and extract the exact octahedral/packing algorithm in Phase 1 research. NOT vendored into this repo — used as reference only.

</canonical_refs>

<code_context>
## Existing Code Insights

### Reusable Assets
- None — greenfield. The repo currently contains only the PRD, solution stub, and planning docs.

### Established Patterns
- None yet; this phase ESTABLISHES the patterns (Core purity, asmdef layout, test harness) that later phases follow.

### Integration Points
- Unity Editor + URP project (to be created/scaffolded as part of PKG-01). Package will be developed under `Packages/com.graffitientertainment.namer/`.

</code_context>

<specifics>
## Specific Ideas

- Format contract details come from the PRD's "NAMER Runtime Representation" section — R/G octahedral normal, B AO, A packed bits, emissive color as material metadata, 64 roughness values.
- Shader should allow side-by-side comparison against URP Lit during development (SHDR-03).

</specifics>

<deferred>
## Deferred Ideas

None — discussion stayed within phase scope

</deferred>

---

*Phase: 1-Core Format Contract + Runtime Decode*
*Context gathered: 2026-08-25*

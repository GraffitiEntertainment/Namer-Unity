# Phase 1: Core Format Contract + Runtime Decode - Research

**Researched:** 2026-08-25
**Domain:** Unity UPM editor plugin — NAMER packed-texture format contract + URP runtime decode shader
**Confidence:** HIGH (Blender reference algorithm, URP shader structure, and Unity package facts verified directly against primary sources — actual Blender source, actual Unity 6000.0.82f1 install, actual URP 17.0.4 package)

## Summary

Phase 1 locks the NAMER packed surface-texture format and proves it can be decoded correctly in a URP runtime shader, verified headless against the Blender reference. The single most important finding of this research is the **exact Blender encoding algorithm**, which I extracted from `GraffitiEntertainment/BlenderNamerPlugin` (develop branch, default branch, pushed 2025-05-10) and numerically validated.

The Blender reference (`namer_core.py`) encodes the packed surface texture with three non-obvious properties that MUST be mirrored for decode equivalence (ENCD-05):

1. **Octahedral encode** operates on the **raw `[0,1]` DirectX normal-map texel** (NOT the `[-1,1]` tangent normal). It normalizes, then divides `xy` by the L1 norm. The normalize and the L1-divide **cancel exactly for non-negative inputs**, so the encode is algebraically identical to a simple barycentric projection: `oct.x = 0.5 + 0.5 * v.x/(v.x+v.y+v.z)` (same for `y`). There is **no corner-fold** (irrelevant — inputs are always in the `+z` hemisphere because they are `[0,1]`-biased). This produces NAMER R/G values that are **always in `[0.5, 1.0]`** — a strong test invariant.
2. **Roughness quantization is LINEAR** — `floor(roughness * 63)`, no perceptual/sqrt remap (answers D-02: no remap freedom, mirror linear). **Metallic bit 7 = `metallic > 0.5`**, **emissive bit 6 = `emissive > 0.1`** (both strict inequalities), bits 0–5 = roughness.
3. **The normalize discards the normal's magnitude**, so the only lossless round-trip is *directional*. The decode that recovers the actual tangent normal requires solving a small quadratic (`|p|²S² − 2S + 2 = 0`) — I derived and verified it (neutral normal `(0,0,1)` → oct `(0.625, 0.625)` → decode → `(0,0,1)`). This full inverse also un-flips the DirectX green channel, landing on Unity's tangent convention.

The rest of the phase is well-trodden: a URP 17.x hand-written shader that reuses the URP ShaderLibrary lighting model but replaces texture sampling with NAMER decode; a pure-C# `Core` asmdef (Unity.Mathematics, no `UnityEngine.Object`); a standard UPM package layout with `testAssemblies` asmdefs.

**Primary recommendation:** Mirror the Blender encode byte-for-byte in `Core` (barycentric projection + linear 6-bit roughness + `>0.5`/`>0.1` bit thresholds), implement the full quadratic decode (recovers the true tangent normal, un-flips DirectX green) in both `Core` and HLSL, and verify ENCD-05 with float-level golden vectors derived from the reference formula. Flag the tangent-space handedness match (SHDR-03) as the one MEDIUM-confidence item needing visual validation.

<user_constraints>
## User Constraints (from CONTEXT.md)

### Locked Decisions
- **D-01:** The octahedral normal encoding/decoding MUST mirror `GraffitiEntertainment/BlenderNamerPlugin` (develop branch) exactly — decode-equivalence with the Blender reference is a hard success criterion (ENCD-05).
- **D-02:** Roughness quantization (6-bit) and metallic/emissive bit placement fixed by PRD: A bit 7 = metallic, A bit 6 = emissive, A bits 0–5 = roughness (64 values). No remapping freedom unless Blender remaps.
- **D-03:** `Core` is a pure-C# assembly (net-standard-compatible asmdef) with NO `UnityEngine.Object` dependencies (plain math types only). Single source of truth for the format, mirrored manually in HLSL. All bit-packing/octahedral/color math lives here.
- **D-04:** Runtime NAMER shader is hand-written URP HLSL (ShaderLab + HLSLPROGRAM), NOT Shader Graph.
- **D-05:** Shader includes the vertex-color reconstruction path (`BaseColor ≈ VertexColorInterpolation × ResidualColor`) from day one.
- **D-06:** Headless CPU tests are primary verification: round-trip for octahedral normals, bit packing, AO preservation. Tolerances: normal decode ~1/255 per component; roughness within 1 quantization step; metallic/emissive exact bits.
- **D-07:** Blender-equivalence verified via golden vectors (known inputs → expected values derived from the Blender reference).
- **D-08:** GPU-vs-CPU comparison tests are Phase 2; Phase 1 shader verification is visual/comparison against URP Lit + play-mode smoke test.
- **D-09:** Unity 6000.0 LTS minimum (`"unity": "6000.0"`), URP 17.x. `.compute` files scaffolded but kernels are Phase 2.
- **D-10:** Package layout per PRD: `com.graffitientertainment.namer/` with Runtime/, Editor/, Shaders/, Compute/, Tests/ (Editor + Runtime), each with asmdefs. Package lives in `Packages/` of a Unity project.

### Claude's Discretion
- Exact class/file names within Core and Shaders (PRD names non-binding).
- Test framework scaffolding details (EditMode vs PlayMode split for shader smoke tests).
- Whether Blender golden vectors are committed as JSON/fixtures.

### Deferred Ideas (OUT OF SCOPE)
- None — discussion stayed within phase scope.
</user_constraints>

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|------------------|
| ENCD-01 | Source normals → octahedral RG, round-trip within tolerance | `Core.OctahedralEncode/Decode` (§ Code Examples). Round-trip is *directional* (normalize discards magnitude). Full quadratic decode recovers the true normal. Test: `dot(n_dec, n_src) >= 1−ε`. |
| ENCD-02 | Pack surface texture: R/G oct normal, B AO, A bit7 metallic / bit6 emissive / bits0-5 roughness | `Core.PackSurface` / `Core.PackAlphaBits` (§ Code Examples). Exact thresholds and linear 6-bit quantize from `namer_core.py`. |
| ENCD-03 | AO preserved through packing | B channel = AO raw (no transform), verified in reference. Test: B == AO within quantization. |
| ENCD-04 | Packed texture written linear + uncompressed (no sRGB / BC / ETC / ASTC) | Texture import must be Linear + no compression. Shader samples R/G/B as data. Documented in § Common Pitfalls #1. Saving is Phase 3; Phase 1 locks the contract + shader samples linear. |
| ENCD-05 | Decode equivalent to Blender reference | Golden vectors derived from the exact reference formula (§ Code Examples). No reference decode exists — equivalence = our encode matches Blender encode (golden), our decode inverts it. |
| ENCD-06 | Emissive color stored as material metadata | Reference stores avg emissive in PNG metadata (`emissive_color`). Unity equivalent = material `_EmissionColor` (HDR) property; emissive flag gates it in shader. |
| SHDR-01 | Shader decodes oct normal, AO, metallic, emissive flag, 6-bit roughness | HLSL `NamerDecodeSurface()` (§ Code Examples) — mirrors Core decode + unpack. |
| SHDR-02 | Base/residual + vertex-color reconstruction | `albedo = baseResidual.rgb × vertexColor.rgb` (§ Shader surface-data map). Residual==base in Phase 1; decomposition is Phase 4. |
| SHDR-03 | Renders comparably to URP Lit | Reuse URP `UniversalFragmentPBR` lighting; map NAMER → `SurfaceData` identically to Lit (§ URP shader structure). Validate visually + PlayMode smoke (D-08). |
| SHDR-04 | Emissive color + transparency | `_EMISSION` keyword + `_EmissionColor`; `_SURFACE_TYPE_TRANSPARENT` keyword + Lit blend state; alpha from base texture. |
| TEST-01 | Automated tests: oct encode/decode, bit packing, AO | `Tests/Editor` asmdef (`testAssemblies`), NUnit `[Test]`, golden vectors + round-trip. |
| PKG-01 | UPM package with Runtime/Editor/Shader/Compute/Test asmdefs, builds + headless tests | `package.json` `"unity": "6000.0"`, asmdef layout (§ Architecture Patterns). |
</phase_requirements>

## Architectural Responsibility Map

| Capability | Primary Tier | Secondary Tier | Rationale |
|------------|-------------|----------------|-----------|
| Octahedral encode/decode math | API/Backend (Core asmdef, pure C#) | Shader (manual HLSL mirror) | Core is single source of truth (D-03); HLSL is a hand-port, verified against Core in Phase 2. |
| Bit packing (metallic/emissive/roughness, AO) | Core asmdef | — | Pure integer/float math, headless-testable. |
| Golden-vector fixtures (Blender equivalence) | Core tests (EditMode) | — | Fixtures are plain JSON; no Unity scene needed. |
| Runtime surface decode + shading | Frontend (GPU shader, URP) | Core (algorithm mirror) | Lighting lives in URP ShaderLibrary; NAMER only replaces surface-data assembly. |
| Base/residual × vertex-color reconstruction | Frontend shader | Core (contract) | Vertex color comes from mesh; multiply in fragment. |
| Emissive/transparency metadata | Frontend shader + material | Core (contract) | Emissive color is a material property; flag gates it. |
| UPM package layout + asmdefs | Build/packaging | — | Static files; no runtime logic. |

## Standard Stack

### Core (Unity UPM packages — NOT npm; verified against local editor install)
| Package | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| Unity Editor | **6000.0.82f1** (installed locally) | Editor host | Unity 6 LTS baseline (D-09). Verified present at `/Applications/Unity/Hub/Editor/6000.0.82f1`. |
| `com.unity.render-pipelines.universal` (URP) | **17.0.4** | Renderer the NAMER shader targets | Verified: built-in package `package.json` says `17.0.4`, `"unity": "6000.0"`. |
| `com.unity.render-pipelines.core` | **17.0.4** | URP ShaderLibrary (`UniversalFragmentPBR`, `GetVertexNormalInputs`, `UnpackNormalScale`) | Auto dependency of URP. Verified built-in. |
| `com.unity.mathematics` | **1.3.2** (project baseline; resolve via UPM) | `float2/float3/math` in Core | STACK.md decision. `Unity.Mathematics.asmdef` verified on GitHub: `{"name":"Unity.Mathematics","allowUnsafeCode":true}` — no engine refs, pure math. |
| `com.unity.test-framework` (UTF) | **1.6.0** (built-in) | NUnit EditMode/PlayMode tests | Verified: built-in package `package.json` says `1.6.0`, `"unity": "6000.0"`. |
| `com.unity.burst` / `com.unity.collections` | bundled Unity 6 (~1.8.x / ~2.5.x) | Burst-compile Core hot loops (Phase 2+ verification) | STACK.md decision; not required by Phase 1 itself. |
| `com.unity.ext.nunit` | **2.0.3** | NUnit bridge | Auto dependency of UTF (verified in UTF package.json). |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `Unity.Mathematics` `float3`/`float2`/`math` | 1.3.x | Core math types (no `UnityEngine.Object`, Burst-friendly) | All Core encode/decode/pack math. |
| `UnityEngine.TestRunner` / `UnityEditor.TestRunner` + `nunit.framework.dll` | via UTF | EditMode test assemblies | `testAssemblies` asmdef references (§ Architecture Patterns). |
| `UnityEngine.JsonUtility` | built-in | (Optional) serialize golden-vector fixtures | If fixtures are `.json` assets; else plain `.cs` constant arrays. |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Unity.Mathematics` in Core | Hand-rolled `struct float2/float3` | Hand-rolled = zero deps + truly engine-free, but loses Burst + SIMD; use Unity.Mathematics (STACK.md already chose it). |
| Standard (folded) octahedral encode | Blender reference barycentric encode | **Standard octahedral would NOT match Blender** (D-01/ENCD-05). Must use the reference's exact (non-folded, `[0,1]`-input) projection. |
| `System.Numerics.Vector3` | `Unity.Mathematics` | `System.Numerics` unreliable/not Burst-compatible in Unity — avoid (STACK.md). |
| URP Lit copy-paste with `#include` hacks | Custom shader including URP ShaderLibrary | Reuse `LitInput.hlsl`-style structure but own `SurfaceData` assembly; do not patch the URP package. |

**Version verification (this session):**
```
URP package.json            → "version": "17.0.4", "unity": "6000.0"   [VERIFIED: local editor install]
render-pipelines.core       → "version": "17.0.4"                      [VERIFIED: local editor install]
com.unity.test-framework    → "version": "1.6.0", "unity": "6000.0"    [VERIFIED: local editor install]
Unity.Mathematics.asmdef    → {"name":"Unity.Mathematics","allowUnsafeCode":true}  [CITED: github.com/Unity-Technologies/Unity.Mathematics]
```

## Package Legitimacy Audit

> This phase installs **zero npm/PyPI/crates packages**. Its "packages" are Unity UPM packages, which come from Unity's own registry (not npm) and are not subject to npm slopcheck. Every recommended package is Unity-official and was verified by direct inspection of the locally installed editor's `BuiltInPackages` directory.

| Package | Registry | Age | Downloads | Source Repo | slopcheck | Disposition |
|---------|----------|-----|-----------|-------------|-----------|-------------|
| com.unity.render-pipelines.universal 17.0.4 | Unity UPM | Unity-official | n/a | Unity-Technologies/Graphics | n/a (not npm) | Approved — [VERIFIED: local editor install] |
| com.unity.render-pipelines.core 17.0.4 | Unity UPM | Unity-official | n/a | Unity-Technologies/Graphics | n/a | Approved — [VERIFIED: local editor install] |
| com.unity.test-framework 1.6.0 | Unity UPM | Unity-official | n/a | Unity-Technologies | n/a | Approved — [VERIFIED: local editor install] |
| com.unity.ext.nunit 2.0.3 | Unity UPM | Unity-official | n/a | Unity-Technologies | n/a | Approved — [VERIFIED: local editor install] |
| com.unity.mathematics 1.3.x | Unity UPM | Unity-official | n/a | Unity-Technologies/Unity.Mathematics | n/a | Approved — [CITED: GitHub asmdef + STACK.md] |
| com.unity.burst / com.unity.collections (bundled) | Unity UPM | Unity-official | n/a | Unity-Technologies | n/a | Approved (not required by Phase 1) |

**Packages removed due to slopcheck [SLOP] verdict:** none (no npm/PyPI/crates packages in scope)
**Packages flagged as suspicious [SUS]:** none

*slopcheck is N/A for Unity UPM packages; they are resolved from Unity's registry, not npm. The "package name provenance" rule (npm `[ASSUMED]` tagging) does not apply because no npm packages are recommended. All Unity packages above were confirmed either by local file inspection or by official GitHub source, not by registry-name guesswork.*

## Architecture Patterns

### System Architecture Diagram

```
                    ┌─────────────────────────────────────────────────────────────┐
                    │                    Core asmdef (pure C#, .NET Std 2.1)       │
                    │   NamerFormatEncode / Decode / PackAlphaBits / UnpackAlpha    │
                    │   (Unity.Mathematics only — no UnityEngine.Object)           │
                    └───────────────▲───────────────────────────┬──────────────────┘
                                    │ golden-vector fixtures     │ manual HLSL mirror (same math)
                    ┌───────────────┴─────────────┐   ┌─────────▼──────────────────┐
                    │ Tests/Editor (testAssemblies)│   │ Shaders/NAMER.shader       │
                    │ NUnit EditMode, headless     │   │  HLSLPROGRAM               │
                    │  - round-trip octahedral     │   │  NamerDecodeSurface()      │
                    │  - bit pack/unpack           │   │  + UniversalFragmentPBR    │
                    │  - AO preservation           │   │  + TransformTangentToWorld │
                    │  - golden-vector vs Blender  │   └────────────────────────────┘
                    └─────────────────────────────┘
```

Data flow (Phase 1 scope): known input vectors → `Core` encode/pack (verified against Blender golden vectors) → same math ported to HLSL → URP shading compared to URP Lit on the same source (SHDR-03). Phase 1 does **not** generate textures, inspect materials, or run compute — those enter Phase 2/3.

### Recommended Project Structure

```
Packages/com.graffitientertainment.namer/
├── package.json                      # "unity": "6000.0", URP + mathematics deps
├── Runtime/
│   ├── GraffitiEntertainment.Namer.Runtime.asmdef
│   └── Core/                         # Core lives in Runtime (games need it? no — see note)
├── Core/                             # RECOMMENDED: separate Core folder + asmdef
│   ├── GraffitiEntertainment.Namer.Core.asmdef   # references "Unity.Mathematics"
│   ├── NamerFormat.cs                # octahedral encode/decode, pack/unpack
│   └── NamerConstants.cs             # bit masks, thresholds
├── Shaders/
│   ├── GraffitiEntertainment.Namer.Shaders.asmdef (or none — shaders are assets)
│   ├── NAMER.shader                  # ShaderLab + HLSLPROGRAM
│   └── NamerSurface.hlsl             # shared decode + surface-data assembly
├── Compute/                          # Phase 2 kernels (scaffold .compute only)
│   └── (empty / .compute placeholder)
├── Editor/
│   └── GraffitiEntertainment.Namer.Editor.asmdef   # Phase 2+ tooling
└── Tests/
    ├── Editor/
    │   ├── GraffitiEntertainment.Namer.Tests.Editor.asmdef  # testAssemblies, references Core
    │   ├── OctahedralRoundTripTests.cs
    │   ├── BitPackingTests.cs
    │   └── BlenderGoldenVectorTests.cs
    └── Runtime/
        └── GraffitiEntertainment.Namer.Tests.Runtime.asmdef  # PlayMode smoke (D-08)
```

Note on `Core` location: because `Core` must be referenced by both `Runtime` and `Tests`, put it in a top-level `Core/` folder with its own asmdef (`"autoReferenced": true`) and `"references": ["Unity.Mathematics"]`. `Runtime` (the runtime shader-supporting scripts, if any) references Core. If Phase 1 has no runtime C# beyond the shader, Core is still a standalone asmdef so tests can reference it directly.

### Pattern 1: Pure-C# Core asmdef
**What:** An assembly definition with no `UnityEngine.Object` usage; math via `Unity.Mathematics`; `noEngineReferences` left at default (false) so `float3` etc. work, but *by convention* no `UnityEngine` types are used.
**When to use:** Core format math that must be headless-testable and mirrored in HLSL.
**Example:**
```json
{
  "name": "GraffitiEntertainment.Namer.Core",
  "rootNamespace": "GraffitiEntertainment.Namer.Core",
  "references": ["Unity.Mathematics"],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": false,
  "precompiledReferences": [],
  "autoReferenced": true,
  "defineConstraints": [],
  "versionDefines": [],
  "noEngineReferences": false
}
```
Key point: `noEngineReferences: false` is intentional. D-03 means "no `UnityEngine.Object` / scene / asset types", not "no `UnityEngine` at all". `Unity.Mathematics` is a pure math library (verified asmdef has no engine references) so Core stays logically pure.

### Pattern 2: Test asmdef (`testAssemblies`)
**What:** A test assembly with `"optionalUnityReferences": ["TestAssemblies"]` and references to the assembly under test; `"includePlatforms": ["Editor"]` for EditMode tests.
**When to use:** UTF EditMode tests (D-06). Confirmed by UTF docs: tests live in `Tests/Editor` or `Tests/Runtime`, each with an asmdef, and `optionalUnityReferences` must include `"TestAssemblies"`.
**Example (Editor tests):**
```json
{
  "name": "GraffitiEntertainment.Namer.Tests.Editor",
  "rootNamespace": "GraffitiEntertainment.Namer.Tests",
  "references": ["Unity.Mathematics", "GraffitiEntertainment.Namer.Core"],
  "optionalUnityReferences": ["TestAssemblies"],
  "includePlatforms": ["Editor"],
  "excludePlatforms": [],
  "allowUnsafeCode": false,
  "overrideReferences": true,
  "precompiledReferences": ["nunit.framework.dll"],
  "autoReferenced": false,
  "defineConstraints": ["UNITY_INCLUDE_TESTS"],
  "versionDefines": [],
  "noEngineReferences": false
}
```

### Pattern 3: URP hand-written lit shader structure
**What:** A shader with the same pass skeleton as `URP/Shaders/Lit.shader`, reusing the URP ShaderLibrary for lighting but with NAMER decode replacing texture sampling.
**When to use:** SHDR-01..04. Verified against `Lit.shader` (URP 17.0.4) — required passes and LightMode tags:
| Pass | LightMode tag | Purpose |
|------|---------------|---------|
| ForwardLit | `UniversalForward` | Main lit pass (always) |
| ShadowCaster | `ShadowCaster` | Cast shadows |
| DepthOnly | `DepthOnly` | Depth prepass |
| DepthNormals | `DepthNormals` | SSAO/depth-normals |
| Meta | `Meta` | Lightmap/reflection baking |
| GBuffer | `UniversalGBuffer` | Deferred (stub optional; URP 17.0.4 uses `UniversalGBuffer` tag) |
| Universal2D | `Universal2D` | 2D renderer (optional) |
| MotionVectors / XRMotionVectors | `MotionVectors` | Motion vectors |

Material keywords to replicate from `Lit.shader` ForwardLit (verified): `_SURFACE_TYPE_TRANSPARENT`, `_ALPHATEST_ON`, `_ALPHAPREMULTIPLY_ON`, `_ALPHAMODULATE_ON`, `_EMISSION`, `_RECEIVE_SHADOWS_OFF`, `_SPECULARHIGHLIGHTS_OFF`, `_ENVIRONMENTREFLECTIONS_OFF`, plus the URP pipeline `multi_compile` keywords (main/additional lights, shadows, SH, fog, instancing). NAMER does **not** need `_NORMALMAP`/`_METALLICSPECGLOSSMAP`/`_OCCLUSIONMAP` — those data come from the packed surface texture.

**Vertex-color reconstruction (D-05, SHDR-02):** `albedo = SAMPLE(baseResidual).rgb * input.vertexColor.rgb`. In Phase 1 residual==base and vertex color is optional (default white). The multiply path is present and tested now so Phase 4 plugs in without shader changes.

**SurfaceData mapping (NAMER → URP):** verified against `LitForwardPass.hlsl`'s `InitializeSurfaceData`:
```
albedo      = baseResidual.rgb * vertexColor.rgb
normalTS    = OctahedralDecode(surface.rg)
metallic    = (surface.a bit 7) ? 1 : 0
smoothness  = 1.0 - (surface.a bits 0-5)/63.0        // NAMER stores roughness
occlusion   = surface.b * _OcclusionStrength
emission    = _EmissionColor.rgb * (bit 6 ? 1 : 0)
alpha       = baseResidual.a (or material _BaseColor.a)
```
Then feed to URP `UniversalFragmentPBR(inputData, surfaceData)` — identical path to Lit, so SHDR-03 comparison is apples-to-apples.

### Anti-Patterns to Avoid
- **Using the standard folded octahedral encode** (Meyer et al. with the `if (z<0)` corner fold): would NOT match Blender (D-01). Use the reference's exact barycentric projection.
- **Setting `noEngineReferences: true` on Core** while also referencing `Unity.Mathematics`: over-restricts; D-03 only forbids `UnityEngine.Object`, not the engine math module. Keep default (false) + convention.
- **Copying `LitInput.hlsl` wholesale into the package**: forks URP internals and breaks on URP upgrades. Include the public `ShaderLibrary/*.hlsl` headers; only own the surface-decode function.
- **Treating roughness as smoothness in the bits**: NAMER stores *roughness*; shader must compute `smoothness = 1 − roughness`.
- **Sampling the packed texture as sRGB**: corrupts the octahedral R/G and AO. The texture must be imported Linear; alpha (packed bits) is always linear regardless.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| URP PBR lighting (BRDF, shadows, GI) | Custom BRDF | URP `ShaderLibrary/Lighting.hlsl` + `UniversalFragmentPBR` | Matches URP Lit (SHDR-03) and gets shadows/GI/light cookies free. |
| Tangent→world transform / TBN | Hand-built TBN | URP `GetVertexNormalInputs` + `TransformTangentToWorld` | mikkTSpace-compliant, handles negative scale (`tangent.w`). |
| Normal-map green unpacking (if needed elsewhere) | Hand-rolled | Core RP `UnpackNormalScale` (in `Packing.hlsl`) | Already correct + DXT5nm/ASTC-aware. |
| Test harness / assertion | Custom test runner | Unity Test Framework (NUnit `[Test]`/`[TestCase]`) | EditMode/PlayMode, CI support, `Assert.That`. |
| Octahedral math library | Generic "standard octahedral" package | **Hand-roll the reference mirror in Core** | No library reproduces the Blender-specific `[0,1]`-input barycentric variant; this is the one thing we MUST hand-roll (to spec D-01). |
| JSON fixture serialization | Custom parser | `UnityEngine.JsonUtility` or `.cs` constant arrays | Golden vectors are tiny; avoid a JSON dependency. |

**Key insight:** the only thing Phase 1 deliberately hand-rolls is the *format math itself* (because it must mirror a specific external reference exactly, not because it's complex). Everything around it — lighting, TBN, tests, packaging — uses the existing URP/Unity stack.

## Runtime State Inventory

Greenfield phase — no runtime state exists. This section is normally for rename/refactor/migration phases and is not applicable. Explicitly: **nothing found in any category** (no stored data, no live service config, no OS-registered state, no secrets, no build artifacts) — the repo contains only the PRD, a `.sln` stub, and planning docs.

## Common Pitfalls

### Pitfall 1: sRGB/linear contract on the packed texture
**What goes wrong:** If the packed surface texture is imported sRGB, Unity linearizes R/G/B on sample, corrupting the octahedral coordinates (which are already linear data) and AO. Shading breaks subtly.
**Why it happens:** Unity auto-imports color textures as sRGB; a packed data texture is not color.
**How to avoid:** Mark the packed surface texture Linear + uncompressed (ENCD-04). The alpha channel is never sRGB-converted, so the packed bits are safe regardless; only R/G/B are at risk. Base/residual texture *is* sRGB (it is color) and stays sRGB.
**Warning signs:** Normals look "washed out" toward `(0,0,1)`; AO too bright; compare decoded normal to source normal map.

### Pitfall 2: Confusing "decode equivalence" with "byte-identical output"
**What goes wrong:** Assuming ENCD-05 means our *encode* must be byte-identical to Blender's PNG output, then getting blocked on float-vs-`uint8` rounding.
**Why it happens:** The reference's on-disk bytes come from `(pixels * 255).astype(np.uint8)` — numpy **truncation** (floor), not round. Unity's `Texture2D`/`Color32` path rounds differently. Phase 1 golden vectors should compare at **float level** (`[0,1]` oct/alpha), not bytes; byte-level PNG equivalence is Phase 3's concern.
**How to avoid:** Define golden vectors as float triples + integer alpha bit-sum (see § Code Examples), with tolerance `1e-4`. Note the floor-vs-round nuance for Phase 3.
**Warning signs:** Golden-vector tests failing by exactly 1 LSB.

### Pitfall 3: The normalize-in-encode discards magnitude → naive decode returns a biased normal
**What goes wrong:** A "simple" inverse decode (just undo the L1-divide and `*0.5+0.5`) returns `v/|v|` — the *direction of the `[0,1]` normal-map value*, not the tangent normal. For a neutral normal this yields `(0.408, 0.408, 0.816)` — a ~35° error that will fail SHDR-03.
**Why it happens:** The reference normalizes `v` before projecting; that magnitude is not recoverable from the 2-channel output alone.
**How to avoid:** Use the full decode (solve `|p|²S² − 2S + 2 = 0`, take the larger root) which recovers the true unit tangent normal AND un-flips the DirectX green channel. Verified numerically (neutral → `(0,0,1)`).
**Warning signs:** Neutral normals rendering as `(0.408,0.408,0.816)`; SHDR-03 comparison off by a constant normal tilt.

### Pitfall 4: Tangent-space handedness (DirectX green-flip) mismatch
**What goes wrong:** Baked normal detail appears inverted (bumps look like dents) if the decoded tangent normal's Y sign doesn't match Unity's TBN convention.
**Why it happens:** The Blender reference bakes DirectX normal maps (`green = 1 − green`, verified in `normal_operators.py` / `properties.py` "DIRECTX (Unity/Unreal): Y+ is down"). Unity's `UnpackNormal` does NOT flip green (verified in `Packing.hlsl`). Our full decode's `n.y = 1 − p.y*S` un-flips back to the OpenGL-style (+Y up) convention, which is what Unity's TBN expects — but this is the one MEDIUM-confidence item.
**How to avoid:** Implement the decode as specified; validate with a normal-mapped test mesh under URP Lit comparison (SHDR-03). If detail is inverted, apply a single `normalTS.y = -normalTS.y` (or negate bitangent).
**Warning signs:** Normal detail lighting from the wrong side in the SHDR-03 comparison.

### Pitfall 5: Strict-inequality thresholds are load-bearing
**What goes wrong:** Using `>=` instead of `>` for the metallic (`>0.5`) and emissive (`>0.1`) thresholds shifts the bit boundary and breaks golden-vector equivalence.
**Why it happens:** The reference uses strict `>` in `(metallic > 0.5)` and `(emissive > 0.1)`.
**How to avoid:** Replicate strict `>` exactly. Add boundary test cases (`0.5`, `0.1` exactly → bit CLEAR).
**Warning signs:** Golden vectors at exact thresholds disagree.

## Code Examples

Verified against `GraffitiEntertainment/BlenderNamerPlugin` `namer_core.py` (develop) and numerically validated.

### 1. Blender reference algorithm (verbatim, for provenance)
```python
# Source: raw.githubusercontent.com/GraffitiEntertainment/BlenderNamerPlugin/develop/namer_core.py
def octahedral_encode(normal):                       # normal = [0,1] DirectX normal-map texel
    normal = normal / np.linalg.norm(normal, axis=-1, keepdims=True)          # (1) normalize
    normal_xy = normal[:, :, :2] / (abs(normal[:,:,0]) + abs(normal[:,:,1]) + abs(normal[:,:,2]))[:, :, None]  # (2) L1 divide
    octahedral = (normal_xy + 1.0) * 0.5             # (3) map to [0,1]
    return octahedral

def pack_alpha(metallic, emissive, roughness):
    metallic_bit  = (metallic  > 0.5).astype(np.uint8) * 128   # bit 7
    emissive_bit  = (emissive  > 0.1).astype(np.uint8) * 64    # bit 6
    roughness_bits= np.clip(np.floor(roughness * 63), 0, 63).astype(np.uint8)  # bits 0-5
    alpha = (metallic_bit + emissive_bit + roughness_bits) / 255.0
    return alpha
```
**Key equivalence (proven):** for non-negative `v` (all normal-map texels), step (1)+(2) cancel, so the encode is exactly:
```
oct.x = 0.5 + 0.5 * v.x / (v.x + v.y + v.z)
oct.y = 0.5 + 0.5 * v.y / (v.x + v.y + v.z)
```
No corner-fold occurs because `v` is always in the `+z` hemisphere (`v.z >= 0`). Consequence: NAMER R/G ∈ `[0.5, 1.0]` always.

### 2. Core encode + pack (C# / Unity.Mathematics)
```csharp
// GraffitiEntertainment.Namer.Core.NamerFormat
using Unity.Mathematics;
using static Unity.Mathematics.math;

public static class NamerFormat
{
    // Mirrors Blender octahedral_encode EXACTLY (barycentric projection of the [0,1] DirectX normal-map texel).
    public static float2 OctahedralEncode(float3 v)
    {
        float sum = v.x + v.y + v.z;
        // Degenerate guard: reference normalizes (NaN on zero); valid normal maps always have v.z >= 0.5.
        sum = max(sum, 1e-6f);
        return new float2(0.5f + 0.5f * v.x / sum, 0.5f + 0.5f * v.y / sum);
    }

    // Pack metallic/emissive/roughness into one 8-bit value (bits 7 / 6 / 0-5).
    public static byte PackAlphaBits(float metallic, float emissive, float roughness)
    {
        byte metallicBit  = metallic  > 0.5f ? (byte)0x80 : (byte)0x00;
        byte emissiveBit  = emissive  > 0.1f ? (byte)0x40 : (byte)0x00;
        byte roughnessBits = (byte)clamp((int)floor(roughness * 63.0f), 0, 63);
        return (byte)(metallicBit | emissiveBit | roughnessBits);
    }

    public static void UnpackAlphaBits(byte a, out bool metallic, out bool emissive, out float roughness)
    {
        metallic  = (a & 0x80) != 0;
        emissive  = (a & 0x40) != 0;
        roughness = (a & 0x3F) / 63.0f;
    }
}
```

### 3. Core decode (full inverse — recovers true tangent normal)
```csharp
    // Inverts OctahedralEncode AND recovers the unit tangent normal (undoes the [0,1] bias and the DirectX green flip).
    public static float3 OctahedralDecode(float2 oct)
    {
        float2 pxy = oct * 2.0f - 1.0f;         // = v.xy / (v.x+v.y+v.z)
        float pz   = 1.0f - pxy.x - pxy.y;      // = v.z  / (v.x+v.y+v.z)
        float3 p   = new float3(pxy.x, pxy.y, pz);   // p.x + p.y + p.z == 1

        float dotP = dot(p, p);                 // dotP in [1/3, 1/2] for valid normals
        // Recover S = nx - ny + nz + 3 from |n| == 1:  |p|^2 S^2 - 2 S + 2 = 0  →  larger root.
        float disc = max(1.0f - 2.0f * dotP, 0.0f);
        float S    = (1.0f + sqrt(disc)) / max(dotP, 1e-6f);

        float3 n;
        n.x = p.x * S - 1.0f;   // (nx+1)/S  →  nx
        n.y = 1.0f - p.y * S;   // (1-ny)/S  →  ny   (DirectX green un-flip)
        n.z = p.z * S - 1.0f;   // (nz+1)/S  →  nz
        return normalize(n);    // safety; already unit for valid input
    }
}
```
**Validation (neutral normal):** `v=(0.5,0.5,1.0)` → `oct=(0.625,0.625)` → decode: `p=(0.25,0.25,0.5)`, `|p|²=0.375`, `S=(1+0.5)/0.375=4`, `n=(0.25·4−1, 1−0.25·4, 0.5·4−1)=(0,0,1)`. ✓

### 4. HLSL shader decode (mirror of Core)
```hlsl
// Shaders/NamerSurface.hlsl  — sampled surface is a LINEAR (non-sRGB) UNorm texture
float4 surface = SAMPLE_TEXTURE2D(_SurfaceMap, sampler_SurfaceMap, uv); // RG=oct, B=AO, A=packed

float3 NamerOctahedralDecode(float2 oct)
{
    float2 pxy = oct * 2.0 - 1.0;
    float  pz  = 1.0 - pxy.x - pxy.y;
    float3 p   = float3(pxy.x, pxy.y, pz);
    float dotP = dot(p, p);
    float disc = max(1.0 - 2.0 * dotP, 0.0);
    float S    = (1.0 + sqrt(disc)) / max(dotP, 1e-6);
    float3 n;
    n.x = p.x * S - 1.0;
    n.y = 1.0 - p.y * S;
    n.z = p.z * S - 1.0;
    return normalize(n);
}

// Packed alpha: texture alpha is UNorm [0,1]; recover the byte and unpack bits.
uint   a         = (uint)(surface.a * 255.0 + 0.5);
bool   metallic  = (a & 0x80u) != 0;
bool   emissive  = (a & 0x40u) != 0;
float  roughness = (float)(a & 0x3Fu) / 63.0;
float  smoothness = 1.0 - roughness;

float3 normalTS = NamerOctahedralDecode(surface.rg);
float  ao       = surface.b * _OcclusionStrength;
float3 albedo   = SAMPLE_TEXTURE2D(_BaseResidualMap, sampler_BaseResidualMap, uv).rgb * input.vertexColor.rgb;
float  alpha    = _BaseColor.a; // or base-residual alpha, gated by _SURFACE_TYPE_TRANSPARENT
float3 emission = _EmissionColor.rgb * (emissive ? 1.0 : 0.0);
```

### 5. Golden vectors (float-level, derived from the reference formula)
| # | Input (`v` = DirectX normal texel) | metallic | emissive | roughness | AO | oct.x | oct.y | B | alpha bits |
|---|-----------------------------------|----------|----------|-----------|-----|-------|-------|---|-----------|
| A | (0.5, 0.5, 1.0) neutral | 0.0 | 0.0 | 0.5 | 1.0 | 0.625 | 0.625 | 1.0 | 31 |
| B | (0.5, 0.5, 1.0) neutral | 1.0 | 0.0 | 1.0 | 1.0 | 0.625 | 0.625 | 1.0 | 128+63=191 |
| C | (0.5, 0.5, 1.0) neutral | 0.5 (not >0.5) | 0.1 (not >0.1) | 0.0 | 1.0 | 0.625 | 0.625 | 1.0 | 0 |
| D | (1.0, 0.0, 0.0) +X extreme | 0.0 | 0.0 | 0.5 | 1.0 | 1.0 | 0.5 | 1.0 | 31 |
| E | (0.5, 0.5, 1.0) neutral | 0.6 | 0.2 | 0.5 | 0.25 | 0.625 | 0.625 | 0.25 | 128+64+31=223 |

Golden-vector fixture strategy (D-07): commit as a `[TestCase]`-driven constant array in `BlenderGoldenVectorTests.cs` (simplest, no JSON dependency), or a `TextAsset` JSON if the discuss-phase prefers data-driven. The ideal long-term fixture is a set produced by running the Blender plugin once; until then, these hand-derived values (from the exact reference formula, validated above) are authoritative. Tolerance `1e-4` float, exact for integer bits.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Full-octahedron folded encoding (Meyer 2014, `if z<0` fold, full `[0,1]²` square) | Blender NAMER non-folded barycentric projection of the `[0,1]`-biased normal (R/G ∈ `[0.5,1.0]`) | Reference is the 2025 Blender plugin | We MUST use the non-folded variant for ENCD-05; note the wasted corner quadrants are a known property, not a bug to "fix". |
| Legacy `RenderTextureFormat` enum | `GraphicsFormat` (`R8G8B8A8_UNorm`, `R8G8B8A8_SRGB`) | Unity 6 | STACK.md; used when the processor writes textures (Phase 3). |
| `CommandBuffer`/RenderGraph for offscreen processing | Direct `ComputeShader.Dispatch` + `AsyncGPUReadback` | Unity 6 | Phase 2+; Phase 1 shader is in-pipeline URP. |

**Deprecated/outdated:**
- `RenderTextureFormat.DepthAuto`/`ShadowAuto` — obsolete in Unity 6; use `GraphicsFormat` (STACK.md).
- Standard octahedral libraries/GLSL snippets (e.g., `octahedron` from Khronos/three.js) — wrong for this project's equivalence target.

## Assumptions Log

| # | Claim | Section | Risk if Wrong |
|---|-------|---------|---------------|
| A1 | The reference has **no decode** — "decode equivalence" (ENCD-05) is defined as our encode matching Blender's encode (golden vectors) and our decode inverting it. Verified: repo contains only `.py` files, no shader/GLSL. | Code Examples / Pitfall #2 | Low — if a reference decode exists elsewhere, golden-vector source changes. |
| A2 | Unity's TBN expects the OpenGL-style (+Y up) tangent normal, which our full decode produces (via `n.y = 1 − p.y*S`). | Pitfall #4 | Medium — if Unity's convention differs, one-line `normalTS.y` flip fixes it; must validate via SHDR-03. |
| A3 | `Unity.Mathematics` 1.3.x has no `UnityEngine` runtime dependency (asmdef `{"name":"Unity.Mathematics","allowUnsafeCode":true}` — no `references`). Resolved version should be pinned in `package.json`. | Standard Stack | Low — pure math library; worst case Core references UnityEngine.CoreModule implicitly, which is fine. |
| A4 | Golden vectors may be committed as `.cs` `[TestCase]` constants (Claude's discretion). | Code Examples | None — planner can switch to JSON; format is the same numbers. |
| A5 | "Headless" (D-06) means UTF EditMode tests (in-editor, no scene/render), not `dotnet test` outside Unity. | Architecture Patterns | Low — if truly-outside-Unity tests are wanted, Core needs `noEngineReferences:true` + no `Unity.Mathematics`; a larger change. |
| A6 | Emissive "flag" bit corresponds to the reference's per-pixel `emissive_red > 0.1`; the emissive *color* is the reference's average (luminance `0.299R+0.587G+0.114B > 0.01` non-black average). Unity stores this as `_EmissionColor`. | ENCD-06 | Low — the exact emissive-color computation belongs to Phase 2/3 material inspection; Phase 1 only carries the metadata slot + flag gate. |

## Open Questions (RESOLVED)

> All three questions below were open at research time and are now resolved by the phase plan and SKELETON.md. Each is annotated with its resolution.

1. **Exact `_EmissionColor` value source in Unity** — RESOLVED
   - What we know: reference stores *average* emissive RGB (non-black pixels) as metadata; shader gates by flag bit.
   - What's unclear: whether Phase 1 shader should default `_EmissionColor` to a placeholder and defer the average computation to Phase 2/3.
   - Recommendation: Phase 1 exposes `_EmissionColor` (HDR) + `_EMISSION` keyword and gates on the bit; the average-emissive *calculation* is a Phase 2/3 concern.
   - Resolution: plan 01-02 Task 1 exposes `_EmissionColor` (HDR) + `_EMISSION` keyword gated on the bit; the average-emissive computation is deferred to Phase 2/3.

2. **Tangent-space handedness confirmation** — RESOLVED
   - What we know: decode un-flips DirectX green to OpenGL-style; Unity `UnpackNormal` does not flip green; reference bakes DirectX.
   - What's unclear: whether the resulting tangent normal sign is byte-correct against URP Lit on all meshes (negative-scale/mirrored-UV edge cases).
   - Recommendation: implement per spec; validate via SHDR-03 with a normal-mapped mesh; one-line flip if inverted.
   - Resolution: plan 01-02 Task 3 (human checkpoint) performs the SHDR-03 side-by-side visual comparison and documents the one-line `normalTS.y` flip if detail is inverted.

3. **Golden-vector provenance for ENCD-05** — RESOLVED
   - What we know: hand-derived vectors match the reference formula (validated); ideal is real Blender plugin output.
   - What's unclear: whether the team wants to run the Blender plugin once to capture authoritative fixtures before Phase 1 sign-off.
   - Recommendation: ship hand-derived vectors now (they ARE the reference formula); optionally regenerate from Blender as a follow-up. Low urgency — the formula is exact.
   - Resolution: SKELETON.md + plan 01-01 Task 2 ship the hand-derived golden vectors as `[TestCase]` constants in `BlenderGoldenVectorTests.cs` (they ARE the reference formula); regenerating from Blender is an optional follow-up.

## Environment Availability

| Dependency | Required By | Available | Version | Fallback |
|------------|------------|-----------|---------|----------|
| Unity Editor | PKG-01 scaffolding, shader import, EditMode tests | ✓ | 6000.0.82f1 (`/Applications/Unity/Hub/Editor/6000.0.82f1`) | — |
| URP | SHDR-01..04 | ✓ | 17.0.4 (built-in) | — |
| render-pipelines.core | URP ShaderLibrary | ✓ | 17.0.4 (built-in) | — |
| Unity Test Framework | TEST-01 | ✓ | 1.6.0 (built-in) | — |
| Unity.Mathematics | Core math | ✓ (via UPM) | 1.3.x (`Unity.Mathematics.dll` present in template libcache) | Hand-rolled float2/3 structs |
| Unity.Burst / Unity.Collections | Phase 2+ verification | ✓ (bundled) | ~1.8.x / ~2.5.x | — |
| Node.js / npm | optional tooling | ✓ | v24.13.0 / 11.6.2 | — |
| ctx7 CLI | Context7 doc lookup | ✗ | — | WebFetch official docs (used this session) |
| **A Unity *project* with URP** | Everything (shader must compile, tests must run) | ✗ | — | **Must be scaffolded as part of PKG-01** |

**Missing dependencies with no fallback:**
- A Unity project (with URP configured) that hosts `Packages/com.graffitientertainment.namer`. None exists today (repo is PRD + `.sln` stub only). This is not a blocker for *planning* — PKG-01 IS the scaffolding task that creates it — but the plan must sequence project creation before any shader/test execution.

**Missing dependencies with fallback:**
- ctx7 CLI (absent) → WebFetch on official Unity docs / local package source (sufficient this session).

## Validation Approach

*(Informational — `workflow.nyquist_validation` is explicitly `false` in `.planning/config.json`, so no formal test-framework gate is enforced. This map exists so the planner can attach concrete verification to each requirement.)*

| Req ID | Behavior | Test Type | Automated Command (runs in Editor) | Notes |
|--------|----------|-----------|-----------------------------------|-------|
| ENCD-01 | Octahedral encode→decode round-trip recovers normal | EditMode unit | UTF EditMode `OctahedralRoundTripTests` | Assert `dot(nDec, nSrc) >= 1 − 1e-3` (directional; magnitude is intentionally discarded) |
| ENCD-02 | Pack R/G/B/A bits correct | EditMode unit | `BitPackingTests` | Exact bit equality (integer) |
| ENCD-03 | AO == B channel | EditMode unit | `BitPackingTests` | B == AO within 1e-4 |
| ENCD-04 | Linear + uncompressed contract | Manual + import-setting test | — | Phase 3 writes it; Phase 1 documents + shader samples linear |
| ENCD-05 | Golden vectors match reference formula | EditMode unit | `BlenderGoldenVectorTests` | Float tolerance 1e-4; int bits exact |
| ENCD-06 | `_EmissionColor` + `_EMISSION` keyword present | Shader compile + material inspection | — | Manual/smoke in Phase 1 |
| SHDR-01 | Shader decodes all 5 packed values | Shader compile + visual | PlayMode smoke (D-08) | Compares decoded vs known source |
| SHDR-02 | `albedo = baseResidual × vertexColor` | Shader compile + visual | PlayMode smoke | Path present; residual==base in Phase 1 |
| SHDR-03 | Renders comparably to URP Lit | Visual comparison | Manual side-by-side scene | Primary SHDR-03 acceptance |
| SHDR-04 | Emissive + transparency carry through | Visual + PlayMode smoke | PlayMode smoke | Transparent + emissive test material |
| TEST-01 | Above encode/pack/AO tests exist | EditMode unit | Full EditMode suite | Headless (no scene) |
| PKG-01 | Package builds; asmdefs compile; tests run | Build + test | `EditMode` run + `Library/PackageCache` compile | Package present under `Packages/` |

## Security Domain

*(`security_enforcement` is not explicitly disabled in config, so this is included.)* The phase is **editor-side asset-processing tooling** — no network I/O, no authentication, no session, no cryptography. Relevant ASVS categories are minimal:

| ASVS Category | Applies | Standard Control |
|---------------|---------|-----------------|
| V2 Authentication | no | n/a |
| V3 Session Management | no | n/a |
| V4 Access Control | no | n/a |
| V5 Input Validation | yes (light) | Validate texture dims / degenerate vectors before encode (guard `sum≈0`, NaN); clamp roughness/metallic/emissive to `[0,1]`. |
| V6 Cryptography | no | n/a |

**Known threat patterns for this stack:** none of the classic injection classes apply (no SQL, no command shells, no deserialization of untrusted input). The realistic concern is **robustness to malformed source textures** (NaN/Inf from corrupt normal maps, zero-length normals) — handled by the `max(sum, 1e-6)` / `max(dotP, 1e-6)` / `normalize` guards already in the decode/encode, plus `math.clamp` on quantized inputs. The reference's own `octahedral_encode` produces NaN on an all-zero texel; Core should not.

## Sources

### Primary (HIGH confidence)
- **Blender reference source** — `raw.githubusercontent.com/GraffitiEntertainment/BlenderNamerPlugin/develop/namer_core.py` (fetched this session; `octahedral_encode`, `pack_alpha`, `compute_average_emissive_color`, `create_namer_png` channel packing). `normal_operators.py` (DirectX bake + `invert_normal_y_channel`), `properties.py` (normal-map format enum "DIRECTX (Unity/Unreal)"). Repo default branch `develop`, pushed 2025-05-10.
- **URP 17.0.4** — local install `/Applications/Unity/Hub/Editor/6000.0.82f1/Unity.app/Contents/Resources/PackageManager/BuiltInPackages/com.unity.render-pipelines.universal/` (`Shaders/Lit.shader` pass list + ForwardLit keyword block + Properties; `ShaderLibrary/ShaderVariablesFunctions.hlsl` `GetVertexNormalInputs` TBN).
- **Core RP 17.0.4** — local install `.../com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl` (`UnpackNormalScale` — no green flip).
- **UTF 1.6.0 / core 17.0.4 / URP 17.0.4** — `package.json` version + `"unity": "6000.0"` (read directly).
- **Unity.Mathematics** — `github.com/Unity-Technologies/Unity.Mathematics` `src/Unity.Mathematics/Unity.Mathematics.asmdef` = `{"name":"Unity.Mathematics","allowUnsafeCode":true}`.

### Secondary (MEDIUM confidence)
- UTF test-folder layout — `docs.unity3d.com/6000.0/Documentation/Manual/cus-tests.html` (Tests/Editor + Tests/Runtime asmdefs, `optionalUnityReferences: ["TestAssemblies"]`).
- Unity 6000.0 / URP 17.x / Mathematics 1.3.x / Burst / Collections versioning — carried from `.planning/research/STACK.md` (prior Context7-backed research) and `CLAUDE.md`.

### Tertiary (LOW confidence)
- None — all load-bearing claims were verified against primary sources this session.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — versions verified against the locally installed editor and package.json files.
- Architecture: HIGH — URP shader pass/keyword structure read from actual `Lit.shader`; asmdef conventions from UTF docs + asmdef source.
- Pitfalls: HIGH — each pitfall traced to a specific line of the reference or URP source; the one exception (tangent-space handedness) is explicitly flagged MEDIUM (A2).

**Research date:** 2026-08-25
**Valid until:** 2026-08-25 + 30 days (stable domain; URP 17.x is an LTS line and the Blender reference is a static 2025 snapshot).

# Stack Research

**Domain:** Unity editor plugin (UPM package) — GPU texture processing + material conversion
**Researched:** 2026-08-25
**Confidence:** HIGH (core stack verified against official Unity docs/Context7; minor version numbers MEDIUM)

## Recommended Stack

### Core Technologies

| Technology | Version | Purpose | Why Recommended |
|------------|---------|---------|-----------------|
| Unity Editor | **6000.0.x LTS** (baseline) and **6000.3.x LTS** (latest) | Editor host for the plugin | Unity 6 is the current LTS generation. `6000.0.x` and `6000.3.x` are the two active LTS streams (stable as of March 2026). Target `6000.0+`; declare minimum `6000.0.0f1` in `package.json`. |
| Universal Render Pipeline (URP) | **17.x** (`com.unity.render-pipelines.universal`) | Runtime renderer for the NAMER material | URP 17 is the Unity 6 line (URP 14 = 2022.3 LTS). URP-first per the project decision. This is the renderer the runtime NAMER shader targets. |
| Compute Shaders (HLSL, `.compute`) | Unity 6 built-in | GPU texture processing (packing, normalizing, stylization filters) | Mandated by project constraints. Metal/Vulkan/DX11+DX12 all supported. No external dependency — part of Unity. |
| C# (editor + runtime code) | .NET Standard 2.1 / C# 9 (Unity 6 profile) | Asset inspection, UI, mesh processing, serialization | Unity 6 uses C# 9 with .NET Standard 2.1 API level. All editor tooling and CPU-side work is C#. |

### Texture & GPU APIs (Unity-native, no third-party deps)

| API | Purpose | Key Constraint |
|-----|---------|----------------|
| `GraphicsFormat` (not legacy `RenderTextureFormat`) | Declare precise texture pixel formats | Use `R8G8B8A8_UNorm` for the packed surface texture (data, linear), `R8G8B8A8_SRGB` for base color, `R16G16B16A16_SFloat` for HDR residuals. `GraphicsFormatUtility`/`SystemInfo.IsFormatSupported(format, FormatUsage.*)` for capability checks. |
| `RenderTexture` + `enableRandomWrite = true` | Compute-shader write target | Random-access (UAV) write requires this flag. **sRGB formats are NOT writable from compute** — always compute into linear formats and sRGB-encode on sample/save. |
| `ComputeShader` (`SetTexture` / `SetBuffer` / `Dispatch`) | Run GPU kernels | Editor-time dispatch is direct; no `CommandBuffer`/RenderGraph needed for offscreen processing. |
| `AsyncGPUReadback` | Non-blocking GPU→CPU readback | Use for editor preview and pipeline stages. Sync `Texture2D.ReadPixels` only for one-shot small reads (it stalls the render thread). |
| `Texture2D.EncodeToPNG()` / `EncodeToEXR()` | Save generated assets | PNG for RGBA8 (base/surface), EXR for HDR residual. |
| `Graphics.Blit` | Simple copy / format conversion | Fine for editor-time full-frame copies; heavy per-pixel work stays in compute. |

### Math Libraries

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| **Unity.Mathematics** (`com.unity.mathematics`) | **1.3.2** | Vector/matrix math (`float3`, `float4x4`, `math` namespace), SIMD/Burst-friendly | Everywhere CPU math is needed — vertex fitting, palette work, UV/mesh math. Confirmed: NO SVD/eigen/least-squares built in (only elementary ops). |
| **Unity.Burst** (`com.unity.burst`) | bundled with Unity 6 (~1.8.x) | Burst-compile the CPU hot loops | The vertex-color decomposition (per-triangle 3×3 least-squares over tens of thousands of triangles) and palette extraction (k-means) are exactly the kind of loops Burst accelerates 10–50×. |
| **Unity.Collections** (`com.unity.collections`) | bundled with Unity 6 (~2.5.x) | `NativeArray`/`NativeList` for Burst jobs | Mandatory partner to Burst for allocator-controlled, job-safe buffers during mesh processing. |
| **Hand-rolled 3×3 Jacobi eigen / SVD** | — (own code) | Barycentric least-squares fitting (`AᵀA x = Aᵀb` per triangle) | The systems are 3×3. Solve normal equations with a small symmetric Jacobi eigensolver (or closed-form 3×3 inverse). No library needed; avoids a heavy dependency. |

### Testing

| Tool | Version | Purpose | Notes |
|------|---------|---------|-------|
| Unity Test Framework (`com.unity.test-framework`) | **1.4.x** (1.4.6 latest in line) | NUnit-based EditMode/PlayMode tests | Bundled with Unity 6. Tests are asmdef-gated: a test assembly references `nunit.framework.dll` + `UnityEngine.TestRunner` (+ `UnityEditor.TestRunner` for EditMode) and targets `"Editor"` platform. |
| NUnit | via UTF | Assertions, `[Test]`/`[UnityTest]` | EditMode tests run in-editor with `UnityEditor` access (perfect for asset-path and immutability tests). |

### Development Tools

| Tool | Purpose | Notes |
|------|---------|-------|
| Assembly Definitions (`.asmdef`) | Package modularity + test isolation | `Runtime.asmdef`, `Editor.asmdef`, `Runtime.Tests.asmdef`, `Editor.Tests.asmdef`. Prevents accidental editor/runtime coupling and is required for UPM packages. |
| Hand-written ShaderLab + HLSL (not Shader Graph) | Runtime NAMER shader + compute kernels | Bit-packing (octahedral decode, metallic/emissive/roughness bits) and integer bit ops are awkward/impossible in Shader Graph. Compute shaders are HLSL-only anyway. Keep a single HLSL code style. |
| `#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"` | URP shader integration | Use URP's `TEXTURE2D`/`SAMPLER` macros and `UnityPerMaterial` CBUFFER so the shader is SRP-batched. |

## Installation

UPM package manifest (`package.json` in the package root). Dependencies resolve via Unity's package manager — nothing is `npm install`ed; this is a Unity package.

```json
{
  "name": "com.graffitientertainment.namer",
  "version": "0.1.0",
  "displayName": "NAMER",
  "unity": "6000.0",
  "dependencies": {
    "com.unity.mathematics": "1.3.2",
    "com.unity.render-pipelines.universal": "17.0.0",
    "com.unity.burst": "1.8.0",
    "com.unity.collections": "2.5.0"
  },
  "testables": ["com.unity.test-framework"]
}
```

`com.unity.render-pipelines.core` is pulled automatically as a URP dependency (do not pin it yourself). `com.unity.test-framework` is bundled with the editor; add under `testables` so consumers can run the package's tests.

## Alternatives Considered

| Recommended | Alternative | When to Use Alternative |
|-------------|-------------|-------------------------|
| Unity 6 LTS (6000.0/6000.3) | Unity 2022.3 LTS + URP 14 | Only if a specific customer is frozen on 2022.3. Costs you URP 17 APIs and the older pipeline tooling. For a greenfield package, do not start here. |
| URP 17 hand-written HLSL shader | Shader Graph | Only for visually-authored stylization variants with no bit manipulation. The NAMER packed format requires explicit bit ops — use HLSL. |
| Hand-rolled 3×3 least-squares | MathNet.Numerics | Only if a future feature needs arbitrary-size SVD (e.g., global color transfer). Not worth the ~5 MB dependency and AOT friction for 3×3 systems. |
| `AsyncGPUReadback` | `Texture2D.ReadPixels` (sync) | `ReadPixels` is acceptable for tiny one-shot reads, but stalls; use async for 2K/4K preview and pipeline. |
| Editor-only UPM package | Runtime-included package | The runtime NAMER shader + material must ship in `Runtime/` (games need it at runtime). The processor/tooling lives in `Editor/`. Both in one package, split by asmdef. |

## What NOT to Use

| Avoid | Why | Use Instead |
|-------|-----|-------------|
| `RenderTextureFormat` (legacy enum) for new code | Deprecated in favor of `GraphicsFormat`; `DepthAuto`/`ShadowAuto` are already obsolete in Unity 6 | `GraphicsFormat` + `GraphicsFormatUtility` |
| sRGB `GraphicsFormat` (e.g. `R8G8B8A8_SRGB`) as a compute write target | sRGB formats cannot be randomly written from compute; writes are implicitly treated as linear | Compute into `R8G8B8A8_UNorm`, store the texture as linear data, mark sRGB only where color is intended to be sampled sRGB |
| Shader Graph for the packed-format shader | Bit packing / octahedral decode / integer ops are not expressible cleanly | Hand-written ShaderLab + HLSL |
| MathNet.Numerics / Accord.NET | Heavy, IL2CPP/AOT and linker friction, overkill for 3×3 systems | Unity.Mathematics + hand-rolled 3×3 solver (Burst-compiled) |
| `CommandBuffer`/RenderGraph for offscreen editor processing | RenderGraph is for in-pipeline frame rendering; editor offscreen work doesn't need it | Direct `ComputeShader.Dispatch` + `AsyncGPUReadback` |
| Native C++ plugins | Explicitly out of scope; breaks UPM portability | C# + HLSL compute (project decision) |
| `System.Numerics` | Not available/reliable in Unity; not Burst-compatible | `Unity.Mathematics` |

## Version Compatibility

| Package | Compatible With | Notes |
|---------|-----------------|-------|
| Unity 6000.0 / 6000.3 LTS | URP 17.x, Mathematics 1.3.x, Burst 1.8.x, Collections 2.5.x, UTF 1.4.x | All are the Unity 6 package set; co-versioned |
| URP 17.x | `com.unity.render-pipelines.core` 17.x (auto) | Never pin core RP manually |
| Unity 2022.3 LTS | URP 14.x, Mathematics 1.2.x | Legacy fallback only; not the primary target |
| Metal (macOS/iOS) | Compute shaders supported; threadgroup ≤ 1024 threads; memoryless RTs on Apple silicon (macOS 11+) | Use linear formats for RW textures; `half`/`min16float` honored via `UNITY_DEVICE_SUPPORTS_NATIVE_16BIT` |
| Vulkan / DX11 / DX12 | Compute shaders supported; DX requires Shader Model 5.0 GPU | Same HLSL kernels work across all; no per-API branching in source |

## Key Domain-Specific Notes (for roadmap)

1. **Packed surface texture must be linear.** The RGBA-packed surface map (octahedral normal RG, AO B, metallic/emissive/roughness in A) stores *data*, not color. Author and sample it as linear (`R8G8B8A8_UNorm`, sRGB disabled) or the gamma curve corrupts the encoded values on decode. This is the single most likely source of a "decode mismatch with Blender" bug.
2. **Compute writes are always linear.** Do all GPU processing in linear space, and only apply sRGB conversion at the final sample (shader) or save (encoder) boundary.
3. **Editor processing ≠ runtime rendering.** Offscreen compute (texture packing, stylization) uses plain `ComputeShader.Dispatch` + `AsyncGPUReadback`, not RenderGraph or `CommandBuffer`. RenderGraph only matters if/when you add runtime effects to the pipeline.
4. **Burst the vertex-color fit.** The barycentric least-squares decomposition over many triangles is the CPU hotspot. Design it as Burst-compilable static functions on `NativeArray` from the start (using `Unity.Mathematics` types so it compiles under Burst).

## Sources

- Context7 `/websites/unity3d_manual` — compute shader basics, `enableRandomWrite`, `RWTexture2D`, Metal/Vulkan/DX support, UTF asmdef setup, `GraphicsFormat`/`IsFormatSupported` — HIGH
- Context7 `/unity-technologies/graphics` — URP 17.x = Unity 6 (CHANGELOG maps 17→6000.x, 14→2022.3) — HIGH
- `docs.unity3d.com/Packages/com.unity.mathematics@1.3` — Mathematics 1.3.2 changelog; confirms no SVD/eigen/decomposition — HIGH
- `docs.unity3d.com/Packages/com.unity.test-framework@1.4` — UTF 1.4.6 latest in line — HIGH
- `docs.unity3d.com/Manual/metal-optimize.html` — memoryless render targets on Apple silicon (macOS 11+) — HIGH
- Wikipedia "Unity (game engine)" — 6000.0.x and 6000.3.x marked LTS (March 2026) — MEDIUM
- Raw `package.json` for URP (GitHub Unity-Technologies/Graphics master) — URP 17.6.0 in dev branch — MEDIUM (dev-branch, not released)

---
*Stack research for: NAMER Unity editor texture/material processing plugin*
*Researched: 2026-08-25*

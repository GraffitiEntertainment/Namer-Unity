<!-- GSD:project-start source:PROJECT.md -->
## Project

**NAMER Unity Plugin**

A Unity-native editor plugin (UPM package, C# + HLSL) that converts ordinary Unity PBR materials into compact NAMER materials entirely inside Unity — generating, processing, compressing, previewing, and stylizing textures without round-tripping through Blender. It replaces the Blender-only NAMER workflow for artists and developers building Unity games who want reduced texture counts/memory and optional stylization driven by reference images.

**Core Value:** A user can select a textured FBX in Unity, run `Process with NAMER`, and get a correctly rendering, source-compatible NAMER material without ever modifying the imported source assets or leaving the Unity Editor.

### Constraints

- **Tech stack**: C# for editor/runtime code, Unity compute shaders (HLSL) for GPU image processing — project requirement
- **Render pipeline**: URP first; Unity Standard where available; HDRP only if practical without harming the first milestone
- **Compatibility**: NAMER packed textures must decode equivalently to the Blender NAMER implementation
- **Asset safety**: Source assets must never be modified; generated output lives in a separate directory
- **Performance**: No repeated per-pixel C# loops on large textures; GPU compute for high-resolution work
- **Precision**: Roughness limited to 64 values (6 bits); vertex colors Color32 where sufficient
- **Package**: Must ship as a reusable Unity Package Manager package
<!-- GSD:project-end -->

<!-- GSD:stack-start source:research/STACK.md -->
## Technology Stack

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
## Sources
- Context7 `/websites/unity3d_manual` — compute shader basics, `enableRandomWrite`, `RWTexture2D`, Metal/Vulkan/DX support, UTF asmdef setup, `GraphicsFormat`/`IsFormatSupported` — HIGH
- Context7 `/unity-technologies/graphics` — URP 17.x = Unity 6 (CHANGELOG maps 17→6000.x, 14→2022.3) — HIGH
- `docs.unity3d.com/Packages/com.unity.mathematics@1.3` — Mathematics 1.3.2 changelog; confirms no SVD/eigen/decomposition — HIGH
- `docs.unity3d.com/Packages/com.unity.test-framework@1.4` — UTF 1.4.6 latest in line — HIGH
- `docs.unity3d.com/Manual/metal-optimize.html` — memoryless render targets on Apple silicon (macOS 11+) — HIGH
- Wikipedia "Unity (game engine)" — 6000.0.x and 6000.3.x marked LTS (March 2026) — MEDIUM
- Raw `package.json` for URP (GitHub Unity-Technologies/Graphics master) — URP 17.6.0 in dev branch — MEDIUM (dev-branch, not released)
<!-- GSD:stack-end -->

<!-- GSD:conventions-start source:CONVENTIONS.md -->
## Conventions

Conventions not yet established. Will populate as patterns emerge during development.
<!-- GSD:conventions-end -->

## C# Engineering Constraints

These are design constraints, not a checklist.

This section is the C# counterpart to the GNUS C++ Coding Standards and is **authoritative** for C# syntax, naming, layout, language use, class design, error handling, file layout, dependency management, platform/GPU abstraction, and tooling in this repository. Do not override it with a general design principle or a local preference.

When two design principles conflict, choose the option that creates the lowest future cost **in this repository** while preserving correctness, clarity, and the existing architecture.

### Core rule: refactor first, then change behavior

When existing structure prevents a clean change:

1. Make the smallest behavior-preserving refactor needed.
2. Run the relevant tests and verification.
3. Only then change behavior.

Do not mix a structural rewrite and a behavioral change into one diff when they can be separated.

A refactor must preserve observable behavior, including error results, ordering, rounding, limits, ownership, lifetime, serialization, shader output, and determinism.

---

### Design principles

1. **Separation of concerns**
   Give each module one kind of work. Keep domain rules (packing/encoding math, fitting, palette operators), asset persistence, GPU pipeline plumbing, editor UI, serialization, and infrastructure separate where practical.

2. **Encapsulation and information hiding**
   Expose the smallest complete public interface. Hide storage, caches, internal containers, implementation types, synchronization, pooled resources, and other details behind that interface.

3. **High cohesion, loose coupling**
   Code that changes for the same reason belongs together. Independent parts communicate through narrow, explicit contracts (asmdefs enforce this at the assembly level).

4. **DRY means one source of truth**
   Keep one authoritative representation of each rule, formula, threshold, constant, bit layout, encoding rule, or other piece of knowledge.
   Do not abstract code merely because two blocks look similar.

5. **KISS**
   Prefer the simplest design that fully solves the current problem. Avoid extra layers, indirection, factories, wrappers, generics, or abstractions unless they remove real duplication or isolate a real dependency.

6. **Single responsibility**
   A class, module, or function should have one clear reason to change. If its purpose requires unrelated responsibilities joined by "and", split it.

7. **Depend on contracts, not implementation details**
   Higher-level policy must not reach through another component's internals. Use the existing public interface or introduce the smallest suitable interface when a real boundary exists.

8. **YAGNI**
   Do not add speculative features, extension points, configuration flags, abstractions, or "future use" APIs without a current requirement.

9. **Composition over implementation inheritance**
   Prefer composition when combining behavior. Use inheritance where the relationship is genuinely "is-a" or where an abstract interface defines the required contract.

10. **Open/Closed, with evidence**
    Do not create extension frameworks in anticipation of change. Introduce an extension boundary when repeated real changes show that a stable boundary exists.

11. **Law of Demeter**
    Do not reach through chains of objects to manipulate distant internals. Use named intermediate values and the owning object's public contract.

12. **Fail early and explicitly**
    Validate input and invariants at boundaries (pre-flight gates before mutation). Return errors through the project's established error mechanism. Never silently swallow failures.

13. **Make invalid states hard to represent**
    Use types, enums, constructors/factories, validation, and sanitizers (e.g. `ClampToCaps`-style) to prevent invalid combinations where doing so keeps the code simpler and clearer.

14. **Optimize for deletion**
    Prefer code that can later be removed without affecting unrelated components. Avoid hidden dependencies and unnecessary framework code.

---

## Hard invariants

### 1. One owning module per domain rule

Every domain rule must have one clear owner.

Examples include:

- packed-surface encoding/decoding (the `NamerFormat` ⇄ `NamerSurface.hlsl` contract)
- vertex-color decomposition and residual fitting
- palette extraction and the palette-transfer operator
- post-look fit math (`NamerStylePostFit`)
- Volume generation (`NamerStyleVolumeWriter`)
- asset-path composition and overwrite policy (`AssetGenerator`)
- EditorPrefs persistence keys and settings plumbing

Before adding a rule, find its existing owner.

If the rule already exists elsewhere, extend that owner rather than adding another implementation beside it.

If related logic is scattered, consolidate it before extending it where doing so can be done safely and independently.

Creating a new module requires a clear reason why the existing owner cannot own the rule.

A local helper, callback, static field, or dependency inversion used only to avoid an architectural cycle usually means the responsibility is in the wrong place. Fix the ownership rather than hiding the cycle.

### 2. Never duplicate domain knowledge

Before writing new business or pipeline logic, search the repository for:

- the formula
- constant or threshold
- enum or classification
- validation rule
- serialization rule
- format string
- state transition
- EditorPrefs key or fingerprint recipe
- error mapping
- shader uniform/kernel constant
- equivalent helper

On the second real use of the same knowledge:

1. move the existing implementation to its proper owner;
2. switch existing callers to it;
3. verify behavior is unchanged;
4. then add the new caller.

Do not create a shared helper while leaving old copies behind.

When consolidating logic, preserve all existing behavior: guards, integer semantics, precision, rounding, overflow handling, ordering, limits, error values, and side effects.

A behavior change belongs in a separate change.

### 3. No domain logic in adapters or presentation layers

Editor windows, custom inspectors/property drawers, menu items, context-menu handlers, asset import hooks, serializers, and other boundary code should translate data and delegate work.

They must not independently implement domain rules such as encoding, validation, classification, fitting decisions, or asset-path policy.

Compute domain results in the owning module and pass the result outward.

### 4. Ownership and lifetime must be explicit

Make resource ownership explicit. Unity native resources (`RenderTexture`, `ComputeBuffer`, `NativeArray`, `PreviewRenderUtility`, GPU readback requests) must be released by their owner — scope them with `using`/`try-finally` or route them through the pool that owns them.

Use `ScriptableObject.CreateInstance` for ScriptableObjects and the appropriate Unity creation API for engine objects; never leave a created native object untracked or leaked.

A field, parameter, or return value should make ownership and mutability clear from the interface.

Do not expose handles to private mutable internals.

### 5. Prefer the smallest C# abstraction that fits

For stateless operations that do not require instance state, prefer `static` methods on a `static` class.

Use a class when state, invariants, ownership, or encapsulation require one.

Use an interface when callers need to depend on a contract rather than an implementation.

Do not create a class merely to hold unrelated helper functions.

Do not create an interface for a single implementation unless it represents a real architectural boundary (Runtime↔Editor asmdef split), test seam, platform/GPU boundary, or dependency that callers must not own directly.

### 6. Keep public contracts small and stable

Public types in `Runtime/` are shipped contracts; public Editor types are contracts across asmdefs.

Do not expose internal collections, synchronization primitives, pooled resources, implementation-only types, or third-party details without need.

Minimize asmdef references and public surface.

A caller should not need to understand implementation details to use a component correctly.

Changes to public types (especially `Runtime/`) deserve more scrutiny than equivalent internal changes because they increase coupling and break downstream users.

### 7. Errors are part of the contract

Use the project's established error mechanism for fallible operations: `InvalidOperationException` for editor-time gate refusals (pre-flight validation), and the established error-value pattern (nullable returns, `bool` + message, `fitError`-style surfaced messages) where the surrounding code uses one.

Do not introduce a second informal error channel beside the established one.

Do not convert errors into success, defaults, logs, or ignored return values unless the contract explicitly requires that behavior.

Use `Debug.Assert` for programmer errors and invariants, not for expected runtime failures.

Finalizers and `Dispose` paths must never throw.

### 8. Preserve portability

Keep platform/GPU-specific behavior behind capability checks and Unity APIs (`SystemInfo.IsFormatSupported`, `GraphicsFormatUtility`), not scattered `#if` platform branches or per-graphics-API assumptions.

The same C# and HLSL must hold across Metal/Vulkan/DX11/DX12; branch on capability, not platform, and isolate any true platform branch behind one seam.

Do not add compiler-specific or editor-version-specific behavior unless it is isolated behind the same kind of boundary.

---

## C# implementation rules

For every new or modified C# file:

- Target **C# 9 / .NET Standard 2.1 only** (Unity 6 profile). Do not introduce C# 10+ or newer .NET features.
- Follow the repository's existing formatting: match the surrounding file's style and apply the IDE formatter — do not hand-reformat a file against its existing style.
- Run IDE/analyser inspections on changed files and clear errors and warnings.
- Follow the existing codebase naming conventions for new code.
- Use Allman braces and always brace control statements, even single statements.
- Initialize fields and variables at declaration.
- Use `null` for absent references and validate at boundaries.
- Use explicit casts where conversion is required; do not rely on hidden conversions.
- Preserve immutability where practical: `const`, `static readonly`, `readonly` fields, and read-only collections.
- Prefer strongly-typed enums over bool flags, magic strings, or magic numbers.
- Do not use `goto`.
- Do not compare floating-point values directly for equality; use a tolerance (`Mathf.Approximately`, epsilon).
- Prefer named constants over magic numbers, following the file's existing constant naming style.
- Keep one primary type per file, with the file name matching the type.
- Give every C# file a Doxygen-compatible file header.
- Keep public functions and interfaces documented with Doxygen-style comments.
- Prefer standard library/collection APIs and `foreach` when they make the code clearer; avoid LINQ, closures, and boxing in hot paths and Burst-compiled jobs.
- Keep functions focused; roughly 100 lines is the upper guideline, not a target.
- Include only the `using` directives a file directly needs; remove unused ones.
- Keep class member ordering consistent with the file's existing layout (constants, fields, properties, constructors, Unity messages, methods).

Do not invent a competing local C# style.

---

## Change discipline

Before implementing a non-trivial change:

1. Find the domain owner.
2. Search for existing implementations of the same rule or knowledge.
3. Identify the public contract that should own the behavior.
4. Refactor existing code first if the new behavior would otherwise create duplication or cross-domain coupling.
5. Keep that refactor behavior-preserving.
6. Implement the behavioral change.
7. Add or update tests at the owning module's boundary.

Do not use local convenience types, static mutable state, singletons, events/callbacks, over-broad public exposure, inheritance, generics, or new abstraction layers merely to route around a bad dependency. Fix the dependency when practical.

---

## Verification is a gate, not a promise

A rule that matters should be enforced by tests, the compiler, analyser inspections, asmdef boundaries, CI, or another automated check whenever practical.

Before declaring C# work complete:

1. apply the IDE formatter to changed files;
2. run analyser inspections on changed files and clear errors/warnings;
3. compile in Unity without errors or warnings;
4. run the relevant EditMode/PlayMode tests;
5. confirm that a refactor-only change did not alter behavior.

Never claim a check passed unless it was actually run.

If a required check cannot be run, state exactly which check was not run and why.

<!-- GSD:architecture-start source:ARCHITECTURE.md -->
## Architecture

Architecture not yet mapped. Follow existing patterns found in the codebase.
<!-- GSD:architecture-end -->

<!-- GSD:skills-start source:skills/ -->
## Project Skills

No project skills found. Add skills to any of: `.claude/skills/`, `.agents/skills/`, `.cursor/skills/`, `.github/skills/`, or `.codex/skills/` with a `SKILL.md` index file.
<!-- GSD:skills-end -->

<!-- GSD:workflow-start source:GSD defaults -->
## GSD Workflow Enforcement

Before using Edit, Write, or other file-changing tools, start work through a GSD command so planning artifacts and execution context stay in sync.

Use these entry points:
- `/gsd-quick` for small fixes, doc updates, and ad-hoc tasks
- `/gsd-debug` for investigation and bug fixing
- `/gsd-execute-phase` for planned phase work

Do not make direct repo edits outside a GSD workflow unless the user explicitly asks to bypass it.
<!-- GSD:workflow-end -->



<!-- GSD:profile-start -->
## Developer Profile

> Profile not yet configured. Run `/gsd-profile-user` to generate your developer profile.
> This section is managed by `generate-claude-profile` -- do not edit manually.
<!-- GSD:profile-end -->

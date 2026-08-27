---
phase: 02
slug: source-inspection-gpu-compute-pipeline
status: verified
threats_open: 0
asvs_level: 1
created: 2026-08-27
---

# Phase 02 — Security

> Per-phase security contract: threat register, accepted risks, and audit trail.

---

## Trust Boundaries

| Boundary | Description | Data Crossing |
|----------|-------------|---------------|
| selection → SourceInspector | Untrusted Unity `Object` inputs (any selection kind, null, unsupported shaders) cross into editor code | Unity Object references / shader property values |
| inspection model → NamerComputePipeline | In-memory map refs + scalar flags cross from editor CPU code into GPU dispatch | Texture2D refs, floats/ints, sRGB flags |
| GPU kernels → RenderTextures | Untrusted per-texel inputs (artist-authored textures) flow through compute write targets | RGBA texel data (arbitrary bit patterns) |
| test inputs → GPU kernels | Deterministic procedural inputs cross into compute dispatch; readback crosses back into CPU assertions | RGBA texel data + AsyncGPUReadback results |

---

## Threat Register

| Threat ID | Category | Component | Disposition | Mitigation | Status |
|-----------|----------|-----------|-------------|------------|--------|
| T-02-01 | Tampering | source asset importer flags | mitigate | Read-only discipline: `IsSrgb` only reads `TextureImporter.sRGBTexture` / `GraphicsFormatUtility` (`SourceInspector.cs:386-405`); no `SetDirty`/`SaveAssets`; `SourceImmutability_InspectionDoesNotChangeTextureFlags` (`SourceInspectorTests.cs:394-417`) asserts flags unchanged | closed |
| T-02-02 | Input Validation (V5) | selection resolution + property reads | mitigate | `HasProperty` guards on all reads (`SourceInspector.cs:381-420`); null/unsupported selections and null materials produce warnings, never exceptions (`:44-48`, `:143`, `:192-195`) | closed |
| T-02-03 | DoS | folder/model traversal | accept | Bounded by `AssetDatabase.FindAssets`/`LoadAllAssetsAtPath`; instance-ID dedupe caps per-material work; no network/untrusted parser | closed |
| T-02-04 | DoS | GPU resource exhaustion | mitigate | `ComputeTexturePool` sole RT allocator; `Release`/`Dispose` in `finally` (`NamerComputePipeline.cs:118-126`); `LiveCount` watchdog returns to baseline; bounded `(w+7)/8` dispatch (`:205`) | closed |
| T-02-05 | Tampering | unintended asset write | mitigate | In-memory only: no `SetDirty`/`SaveAssets`/`AssetDatabase.CreateAsset` in pipeline/pool; outputs are RenderTextures (`NamerComputeResult`) handed to Phase 3's single write path | closed |
| T-02-06 | Info Disclosure / Integrity | editor-only code in runtime assembly | mitigate | `GraffitiEntertainment.Namer.Editor.asmdef` `includePlatforms: ["Editor"]`; harness + pool in `Editor/Pipeline/`; `Core/NamerFormat.cs` pure (no `UnityEngine.Object`) | closed |
| T-02-07 | Input Validation (V5) | sRGB/channel handling | mitigate | sRGB decode gated on `_SourceIsSrgb` for base only (`NAMERPack.compute:54`, set from `BaseMapIsSrgb` at `NamerComputePipeline.cs:175`); AO from `.g`; `max(ao, NAMER_EPSILON)` (`NamerEncode.hlsl:21`); `_SmoothnessTextureChannel`/`_HasMetallicGlossMap` guarded branches | closed |
| T-02-08 | Tampering | flaky/false-green GPU tests | mitigate | Capability gate + `Assert.Ignore` (skip-with-report); `WaitForCompletion`/`WaitUntil(req.done)` + `Assert.IsFalse(req.hasError)` before `GetData`; EXACT alpha-byte and explicit 1/255 + 1e-3 tolerances | closed |
| T-02-09 | DoS | RenderTexture leak in tests | mitigate | try/finally + `RenderTexture.Release()`/`DestroyImmediate` in `GpuGoldenTests.cs` and `ComputeSmokeTests.cs`; `LiveCount` leak watchdog asserts baseline return | closed |
| T-02-10 | Info Disclosure / Integrity | editor-only test code in runtime assembly | accept | Tests live in `Tests/Editor/` (`includePlatforms: ["Editor"]`); no runtime exposure; no production-code change | closed |
| T-02-SC | Tampering | package installs | accept | Zero new packages this phase; `Packages/manifest.json` contains only `com.unity.*` registry deps; no npm/pip/crates surface | closed |

*Status: open · closed*
*Disposition: mitigate (implementation required) · accept (documented risk) · transfer (third-party)*

---

## Accepted Risks Log

| Risk ID | Threat Ref | Rationale | Accepted By | Date |
|---------|------------|-----------|-------------|------|
| AR-02-01 | T-02-03 | Folder/model traversal bounded by `AssetDatabase` APIs + instance-ID dedupe; no network or untrusted parser surface; worst case is editor-time work proportional to project size | Plan-time register (02-01-PLAN.md) | 2026-08-27 |
| AR-02-02 | T-02-10 | Editor-only test assembly (`includePlatforms: ["Editor"]`) — no runtime exposure possible; no production code changed by tests | Plan-time register (02-03-PLAN.md) | 2026-08-27 |
| AR-02-03 | T-02-SC | No new packages introduced in Phase 2; Unity registry-only manifest; supply-chain surface unchanged from Phase 1 audit | Plan-time register (all three plans) | 2026-08-27 |

*Accepted risks do not resurface in future audit runs.*

---

## Security Audit Trail

| Audit Date | Threats Total | Closed | Open | Run By |
|------------|---------------|--------|------|--------|
| 2026-08-27 | 11 | 11 | 0 | gsd-security-auditor (verify-mitigations mode, register_authored_at_plan_time) |

Audit basis: all three PLAN `<threat_model>` blocks parsed; all three SUMMARYs checked (no `## Threat Flags` sections — zero unregistered flags); each mitigate-dispositioned threat verified against implementation code with file:line evidence (see Threat Register). Register authored at plan time, so no retroactive STRIDE scan was required.

---

## Sign-Off

- [x] All threats have a disposition (mitigate / accept / transfer)
- [x] Accepted risks documented in Accepted Risks Log
- [x] `threats_open: 0` confirmed
- [x] `status: verified` set in frontmatter

**Approval:** verified 2026-08-27

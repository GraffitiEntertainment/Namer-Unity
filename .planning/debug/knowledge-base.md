# GSD Debug Knowledge Base

Resolved debug sessions. Used by `gsd-debugger` to surface known-pattern hypotheses at the start of new investigations.

---

## ao-luma-false-occlusion — synthetic AO tracked albedo luma, not geometry (dark clothing read as occluded)
- **Date:** 2026-09-21
- **Error patterns:** AO looks wrong, dark regions ambient-darkened, occlusion from albedo, luminance extraction conflates albedo, surface.b tracks base luma, bake unreachable chicken-and-egg HasCachedBake, stage gate packs white, authored occlusion map ignored when checkbox off
- **Root cause:** The D-07 luminance-extraction AO (the automatic synthetic source with no authored _OcclusionMap) normalized every texel by ONE global scalar mean (_LumaAverage) and consumed no geometric input, so AO ≈ saturate(blur(albedo luma)/global mean luma) — r(luma, ao)=+0.4346 on Neo; dark clothing darkened ambient. Simultaneously the correct geometry bake was unreachable in production since 307b305: the bake cache is in-memory per NamerComputePipeline, Process constructs a fresh pipeline, and nothing primed it (HasCachedBake always false).
- **Fix:** Rewrote the D-07 gate in NamerComputePipeline.Process: authored _OcclusionMap transfers FIRST, checkbox-independent; AO stage ON + no authored map calls BakeAndUpload directly (bakes fresh on cache miss); stage OFF or no bake source packs white via WhiteFill (un-multiply divide becomes the identity at any strength, so the strength slider passes through unchanged). Retired the luminance Extract() from the automatic path. Prime inspection.BakeSourceMesh whenever DecompositionEnabled || AoStageEnabled in NamerProcessor and the window preview (AO works with decomposition off). Commit b75d925.
- **Files changed:** Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerSourceModel.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerComputePipeline.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerProcessor.cs, Packages/com.graffitientertainment.namer/Editor/Pipeline/NamerAOPipeline.cs, Packages/com.graffitientertainment.namer/Editor/UI/NamerEditorWindow.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOExtractionTests.cs, Packages/com.graffitientertainment.namer/Tests/Editor/NamerAOBakeTests.cs
---

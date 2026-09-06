using System.Collections.Generic;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Roughness-extraction estimator selector (D-03). <see cref="FitDriven"/> (0) is the
    /// default: the strength search picks the minimal strength whose post-refit residual
    /// MaxError collapses within threshold (D-04). <see cref="Sobel"/> (1) is the explicit
    /// standalone parity route (plan 01) — it does NOT sharp-remove the base, so Sobel-mode
    /// assets do not reach the one-texture outcome (parity-only per D-03).
    /// </summary>
    public enum NamerRoughnessEstimator
    {
        FitDriven = 0,
        Sobel = 1,
    }

    /// <summary>
    /// Serializable inspection result for a single unique source material (D-03).
    /// Holds map references, per-map sRGB/channel metadata, scalar fallbacks, and any
    /// warnings produced during discovery. This is the CPU-side data model that plans
    /// 02-02 (compute pipeline) and 03-01 (asset generation) consume.
    ///
    /// Field derivations (kept here so consumers read one contract):
    ///   Roughness           = 1 - Smoothness (D-05)
    ///   Emissive            = max(EmissionColor.r, .g, .b) when emission is present
    ///                         (an emission map exists OR _EmissionColor is non-black),
    ///                         else 0
    ///   AoUnmultiplyStrength = 1.0f default (D-08 cleaning strength; Phase 3 UI
    ///                          overrides it — distinct from OcclusionStrength)
    ///   Cutoff              = _Cutoff
    ///   SurfaceType         = _Surface (URP: 0 opaque / 1 transparent; Standard:
    ///                         0 opaque unless a transparent blend mode is detected)
    ///
    /// AO is recorded as the raw <c>_OcclusionMap</c> green-channel semantics: the map
    /// reference plus <see cref="OcclusionStrength"/> metadata. The strength blend is
    /// applied at decode (Phase 1's NamerSurface.hlsl), never baked here, so Phase 3
    /// cannot double-apply it.
    ///
    /// Per-map sRGB metadata: only <see cref="BaseMapIsSrgb"/> feeds a conversion (the
    /// normalize kernel's sRGB decode). Normal/AO/metallicGloss maps are consumed as
    /// raw texel data by the compute kernels regardless of their import flag; their
    /// *IsSrgb fields record that flag and <c>SourceInspector</c> warns when it is set,
    /// since an sRGB-authored data map contradicts the raw-data assumption.
    /// </summary>
    public sealed class NamerMaterialInspection
    {
        public Material Material;
        public string ShaderName;
        public bool IsUrpLit;
        public bool IsStandard;

        public Texture2D BaseMap;
        public bool BaseMapIsSrgb;
        public Color BaseColor;

        public Texture2D NormalMap;
        public bool NormalMapIsSrgb;
        public float BumpScale;

        public Texture2D MetallicGlossMap;
        public bool MetallicGlossMapIsSrgb;
        public float Metallic;
        public float Smoothness;
        public int SmoothnessTextureChannel;

        public Texture2D OcclusionMap;
        public bool OcclusionMapIsSrgb;
        public float OcclusionStrength;

        public Mesh BakeSourceMesh; // default geometry-bake source = selected mesh (D-03); null = no bake possible
        public Mesh OccluderMesh;   // optional high-res occluder (D-04); null/invalid falls back to BakeSourceMesh (D-06)

        // Processing-time AO tweak controls (D-11). These shape the AO output regardless of
        // source (extracted or baked) and are DISTINCT from the decode-time OcclusionStrength
        // metadata (RESEARCH Pitfall 6 — the strength here is not the runtime _OcclusionStrength).
        // Inline identity defaults keep every construction path (window, NamerProcessor, tests)
        // behavior-identical until a caller overrides them.
        public float AoBlurRadius = 0f; // texels; 0 = off (the user blur is a second pass distinct from the internal low-pass)
        public float AoStrength = 1f;   // 1 = full AO, 0 = white/no AO
        public float AoContrast = 1f;   // 1 = identity, pivot 0.5

        public Texture2D EmissionMap;
        public Color EmissionColor;
        public float Emissive;

        public float Roughness;
        public float AoUnmultiplyStrength;

        // Roughness extraction controls (Phase 04.1). RoughnessExtractStrength is the
        // D-02 user override (strength 0 = off); it has NO inline default (matching
        // AoUnmultiplyStrength) so the identity 0 = off preserves the legacy scalar path for
        // direct NamerComputePipeline.Process callers and existing no-map fixtures — the shipped
        // default-on 1f arrives via settings/constants in plan 02. RoughnessEstimator is the
        // D-03 selector, retyped to the NamerRoughnessEstimator enum in plan 02 (FitDriven = 0
        // is the default; Sobel = 1 the parity alternative).
        public float RoughnessExtractStrength;
        public NamerRoughnessEstimator RoughnessEstimator = NamerRoughnessEstimator.FitDriven;

        /// <summary>
        /// D-06 optional per-material roughness-offset input (null default = no offset). A
        /// user-assigned <see cref="Texture2D"/> (no generated path, so no
        /// <c>AssetDatabase.LoadAssetAtPath</c>) surfaced as the shader's "Roughness Offset"
        /// slot; <see cref="AssetGenerator.WriteMaterial"/> rebinds it when non-null. The
        /// shader's neutral <c>"black" {}</c> default means an unassigned slot decodes
        /// identically.
        /// </summary>
        public Texture2D RoughnessOffsetMap;

        public float Cutoff;
        public float SurfaceType;

        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// The full inspection result for one <c>SourceInspector.Inspect</c> call:
    /// one entry per deduplicated unique material, plus top-level warnings for
    /// unsupported selections or traversal problems.
    /// </summary>
    public sealed class NamerSourceModel
    {
        public List<NamerMaterialInspection> Materials = new List<NamerMaterialInspection>();
        public List<string> Warnings = new List<string>();
    }
}

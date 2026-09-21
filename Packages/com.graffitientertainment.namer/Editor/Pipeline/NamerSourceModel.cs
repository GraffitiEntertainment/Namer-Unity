using System.Collections.Generic;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
    /// <summary>
    /// Roughness dip-source selector (04.2: the dip source replaces the retired 04.1
    /// estimator split, UI-03). <see cref="RemovedDetail"/> (0) is the default: the signed
    /// Rec.601 luminance of the albedo the Gouraud projection removed is re-expressed as
    /// gloss through the taste dip slider, and requires decomposition to run (the projected
    /// base is the dividend). <see cref="SobelEdge"/> (1) is the explicit alternate dip
    /// source — the surviving Blender-parity Sobel edge signal — used when decomposition is
    /// off (or CR-01-guarded) and as the fallback signal; it does NOT project the base, so
    /// Sobel-only assets do not reach the by-construction one-texture outcome.
    /// </summary>
    public enum NamerDipSource
    {
        RemovedDetail = 0,
        SobelEdge = 1,
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

        // AO stage gate (2026-09-21 contract). Gates SYNTHESIS only: false (the inline
        // default, matching AoUnmultiplyStrength's no-default convention so direct
        // NamerComputePipeline.Process callers and fixtures keep the identity path)
        // skips the synthetic stage entirely — surface B packs white via the WhiteFill
        // upload and the un-multiply divides by 1 regardless of strength; true with no
        // authored _OcclusionMap makes the GEOMETRY BAKE the synthetic source (the
        // albedo-driven luminance extraction is retired from the automatic path). An
        // authored _OcclusionMap always transfers regardless of this flag — authored
        // data is not gated. The shipped default-on arrives via settings/constants.
        public bool AoStageEnabled;

        public Texture2D EmissionMap;
        public Color EmissionColor;
        public float Emissive;

        public float Roughness;
        public float AoUnmultiplyStrength;

        // Roughness extraction controls (Phase 04.2). RoughnessExtractStrength is the
        // D-02 user override re-expressed as the pure-taste dip-depth slider (strength 0 =
        // keep the authored scalar); it has NO inline default (matching AoUnmultiplyStrength)
        // so the identity 0 = off preserves the legacy scalar path for direct
        // NamerComputePipeline.Process callers and existing no-map fixtures — the shipped
        // default-on 0.25 arrives via settings/constants. DipSource is the 04.2 selector
        // (RemovedDetail = 0 is the default; SobelEdge = 1 the fallback alternate).
        public float RoughnessExtractStrength;
        public NamerDipSource DipSource = NamerDipSource.RemovedDetail;

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

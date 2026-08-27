using System.Collections.Generic;
using UnityEngine;

namespace GraffitiEntertainment.Namer.Editor
{
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

        public Texture2D EmissionMap;
        public Color EmissionColor;
        public float Emissive;

        public float Roughness;
        public float AoUnmultiplyStrength;
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

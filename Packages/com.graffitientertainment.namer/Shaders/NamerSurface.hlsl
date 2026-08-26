#ifndef GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED
#define GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED

// NAMER runtime decode + surface assembly.
//
// Hand-port of GraffitiEntertainment.Namer.Core.NamerFormat (plan 01-01) and the
// BlenderNamerPlugin reference (namer_core.py). The packed surface texture is
// LINEAR data (ENCD-04): R/G are octahedral normal coordinates, B is AO, A is the
// packed alpha byte (bit 7 = metallic, bit 6 = emissive, bits 0-5 = roughness).
//
// Reuses URP ShaderLibrary lighting (UniversalFragmentPBR) and TBN helpers; only
// owns the NAMER decode and the SurfaceData assembly.

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

// ------------------------------------------------------------------
// NAMER material properties (SRP-batcher compatible: one CBUFFER).
// Do not ifdef properties here — the SRP batcher needs a stable layout.
// ------------------------------------------------------------------
CBUFFER_START(UnityPerMaterial)
    float4 _SurfaceMap_ST;
    float4 _BaseResidualMap_ST;
    half4  _BaseColor;
    half4  _EmissionColor;
    half   _OcclusionStrength;
    half   _Cutoff;
    half   _Surface;
CBUFFER_END

// _SurfaceMap:  LINEAR (non-sRGB) R8G8B8A8_UNorm packed surface texture.
// _BaseResidualMap: sRGB base/residual color (residual == base in Phase 1).
TEXTURE2D(_SurfaceMap);        SAMPLER(sampler_SurfaceMap);
TEXTURE2D(_BaseResidualMap);   SAMPLER(sampler_BaseResidualMap);

// ------------------------------------------------------------------
// NAMER octahedral decode — mirrors NamerFormat.OctahedralDecode EXACTLY.
// Full quadratic inverse of the barycentric encode: recovers the unit tangent
// normal and un-flips the DirectX green channel (n.y = 1 - p.y * S).
// ------------------------------------------------------------------
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

// Recover the packed alpha byte from a UNorm [0,1] alpha channel value.
uint NAMER_SAMPLE_ALPHA_BYTE(float4 surface)
{
    return (uint)(surface.a * 255.0 + 0.5);
}

// Decode all five packed values from one linear packed surface texel.
void NAMER_DECODE_SURFACE(float4 surface, out bool metallic, out bool emissive,
                          out float roughness, out float smoothness,
                          out float3 normalTS, out float ao)
{
    uint a = NAMER_SAMPLE_ALPHA_BYTE(surface);
    metallic   = (a & 0x80u) != 0;
    emissive   = (a & 0x40u) != 0;
    roughness  = (float)(a & 0x3Fu) / 63.0;
    smoothness = 1.0 - roughness;   // NAMER stores ROUGHNESS, not smoothness.
    normalTS   = NamerOctahedralDecode(surface.rg);
    ao         = surface.b * _OcclusionStrength;
}

// Assemble the URP SurfaceData from NAMER textures + per-vertex color.
// SHDR-02 / D-05: albedo = baseResidual.rgb * vertexColor.rgb (residual == base in Phase 1).
void InitializeNamerSurfaceData(float2 uv, float4 vertexColor, out SurfaceData surfaceData)
{
    float4 surface      = SAMPLE_TEXTURE2D(_SurfaceMap, sampler_SurfaceMap, uv);
    float4 baseResidual = SAMPLE_TEXTURE2D(_BaseResidualMap, sampler_BaseResidualMap, uv);

    bool metallic;
    bool emissive;
    float roughness;
    float smoothness;
    float ao;
    float3 normalTS;
    NAMER_DECODE_SURFACE(surface, metallic, emissive, roughness, smoothness, normalTS, ao);

    half alpha = baseResidual.a * _BaseColor.a;
    alpha = AlphaDiscard(alpha, _Cutoff);

    surfaceData.albedo = baseResidual.rgb * _BaseColor.rgb * vertexColor.rgb;
    surfaceData.albedo = AlphaModulate(surfaceData.albedo, alpha);

    surfaceData.metallic   = metallic ? 1.0 : 0.0;
    surfaceData.specular   = half3(0.0, 0.0, 0.0);
    surfaceData.smoothness = smoothness;
    surfaceData.normalTS   = normalTS;
    surfaceData.occlusion  = ao;
#ifdef _EMISSION
    surfaceData.emission   = _EmissionColor.rgb * (emissive ? 1.0 : 0.0);
#else
    surfaceData.emission   = half3(0.0, 0.0, 0.0);
#endif
    surfaceData.alpha              = alpha;
    surfaceData.clearCoatMask      = half(0.0);
    surfaceData.clearCoatSmoothness = half(0.0);
}

#endif // GRAFFITI_ENTERTAINMENT_NAMER_SURFACE_INCLUDED

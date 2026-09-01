Shader "GraffitiEntertainment.Namer/NamerDebugView"
{
    Properties
    {
        [NoScaleOffset] _SurfaceMap("Surface (Packed)", 2D) = "white" {}
        [NoScaleOffset] _BaseResidualMap("Base/Residual", 2D) = "white" {}
        [NoScaleOffset] _DebugBaseMap("Debug Base", 2D) = "white" {}

        _DebugChannel("Debug Channel", Float) = 0
        _OcclusionStrength("Occlusion Strength", Range(0.0, 1.0)) = 1.0

        // The shared CBUFFER in NamerSurface.hlsl also carries these; declare them
        // (hidden) so the SRP-batcher material layout stays complete.
        [HideInInspector] _BaseColor("Color", Color) = (1,1,1,1)
        [HideInInspector] _EmissionColor("Emission", Color) = (0,0,0)
        [HideInInspector] _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        [HideInInspector] _Surface("Transparent Surface", Float) = 0.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }
        LOD 100

        Pass
        {
            Name "NamerDebugView"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Cull Back
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex DebugVert
            #pragma fragment DebugFrag

            // Reuses the shared decode so debug views cannot drift from runtime (D-12).
            #include "NamerSurface.hlsl"
            // The error-heatmap ramp (channel 8) lives with the decomp kernels (D-11).
            #include "../Compute/NAMERDecomp.hlsl"

            float _DebugChannel;
            TEXTURE2D(_DebugBaseMap);
            SAMPLER(sampler_DebugBaseMap);

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 texcoord   : TEXCOORD0;
                float4 color      : COLOR;
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float4 vertexColor : TEXCOORD1;
                float4 positionCS : SV_POSITION;
            };

            Varyings DebugVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.texcoord;
                output.vertexColor = input.color;
                return output;
            }

            half4 DebugFrag(Varyings input) : SV_Target
            {
                float4 surface      = SAMPLE_TEXTURE2D(_SurfaceMap, sampler_SurfaceMap, input.uv);
                float4 baseResidual = SAMPLE_TEXTURE2D(_BaseResidualMap, sampler_BaseResidualMap, input.uv);

                bool metallic;
                bool emissive;
                float roughness;
                float smoothness;
                float ao;
                float3 normalTS;
                NAMER_DECODE_SURFACE(surface, metallic, emissive, roughness, smoothness, normalTS, ao);

                float metallicFlag = metallic ? 1.0 : 0.0;
                float emissiveFlag = emissive ? 1.0 : 0.0;

                float3 channel;
                if (_DebugChannel < 0.5)
                {
                    channel = baseResidual.rgb;    // 0: Base Color
                }
                else if (_DebugChannel < 1.5)
                {
                    channel = ao.rrr;              // 1: AO
                }
                else if (_DebugChannel < 2.5)
                {
                    channel = normalTS * 0.5 + 0.5; // 2: Decoded normal
                }
                else if (_DebugChannel < 3.5)
                {
                    channel = roughness.rrr;        // 3: Roughness
                }
                else if (_DebugChannel < 4.5)
                {
                    channel = metallicFlag.rrr;     // 4: Metallic
                }
                else if (_DebugChannel < 5.5)
                {
                    channel = emissiveFlag.rrr;     // 5: Emissive
                }
                else if (_DebugChannel < 6.5)
                {
                    channel = input.vertexColor.rgb; // 6: Vertex Colors (white where no stream)
                }
                else if (_DebugChannel < 7.5)
                {
                    channel = baseResidual.rgb;     // 7: Residual (== base when non-decomposed)
                }
                else
                {
                    // 8: Error Heatmap — the mean-channel MAE between the reconstruction
                    // (residual * vertex color) and the debug base map, mapped through the
                    // viridis ramp. Violet/zero when non-decomposed (rec == base, color == white).
                    float3 rec = baseResidual.rgb * input.vertexColor.rgb;
                    float3 db = SAMPLE_TEXTURE2D(_DebugBaseMap, sampler_DebugBaseMap, input.uv).rgb;
                    float err = (abs(rec.r - db.r) + abs(rec.g - db.g) + abs(rec.b - db.b)) / 3.0;
                    channel = NAMER_DECOMP_HEATMAP(err / 0.25);
                }

                return half4(channel, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

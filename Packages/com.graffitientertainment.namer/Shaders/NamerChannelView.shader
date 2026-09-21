Shader "GraffitiEntertainment.Namer/NamerChannelView"
{
    Properties
    {
        [NoScaleOffset] _SurfaceMap("Surface (Packed)", 2D) = "white" {}
        [NoScaleOffset] _BaseResidualMap("Base/Residual", 2D) = "white" {}
        // D-06: NamerSurface.hlsl declares this texture; keep the channel-view
        // material's layout complete + neutral ("black" {} => .r == 0). Not sampled
        // by ChannelFrag.
        [HideInInspector] _RoughnessOffsetMap("Roughness Offset", 2D) = "black" {}

        _Channel("Channel", Float) = 0
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
            Name "NamerChannelView"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            Cull Off
            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex ChannelVert
            #pragma fragment ChannelFrag

            // Reuses the shared decode so channel views cannot drift from runtime (D-12).
            #include "NamerSurface.hlsl"

            float _Channel;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float2 uv         : TEXCOORD0;
                float4 positionCS : SV_POSITION;
            };

            // Standard Graphics.Blit quad vertex — raw UVs, no TRANSFORM_TEX (the panes
            // show the raw texture).
            Varyings ChannelVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                return output;
            }

            half4 ChannelFrag(Varyings input) : SV_Target
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
                if (_Channel < 0.5)
                {
                    channel = baseResidual.rgb;       // 0: Base/Residual color
                }
                else if (_Channel < 1.5)
                {
                    channel = roughness.rrr;          // 1: Roughness (alpha bits 0-5 / 63)
                }
                else if (_Channel < 2.5)
                {
                    channel = ao.rrr;                 // 2: AO (surface.b * _OcclusionStrength)
                }
                else if (_Channel < 3.5)
                {
                    channel = metallicFlag.rrr;       // 3: Metallic flag (alpha bit 7)
                }
                else if (_Channel < 4.5)
                {
                    channel = emissiveFlag.rrr;       // 4: Emissive flag (alpha bit 6)
                }
                else
                {
                    channel = normalTS * 0.5 + 0.5;   // 5: decoded tangent-space normal
                }

                return half4(channel, 1.0);
            }
            ENDHLSL
        }
    }

    FallBack Off
}

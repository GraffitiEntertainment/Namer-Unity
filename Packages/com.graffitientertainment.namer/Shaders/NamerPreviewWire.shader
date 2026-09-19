Shader "GraffitiEntertainment.Namer/NamerPreviewWire"
{
    Properties
    {
        _WireColor("Wire Color", Color) = (0, 1, 1, 1)
    }

    SubShader
    {
        // Geometry+100 draws after the opaque preview mesh so the X-ray look is
        // preserved; ZTest Always + ZWrite Off below keep back-facing edges visible.
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry+100"
        }
        LOD 100

        Pass
        {
            Name "NamerPreviewWire"
            Tags
            {
                "LightMode" = "UniversalForward"
            }

            ZWrite Off
            ZTest Always

            HLSLPROGRAM
            #pragma target 2.0

            #pragma vertex WireVert
            #pragma fragment WireFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _WireColor;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            // Minimal unlit color pass: the wire submesh is line topology sharing the
            // preview mesh's vertex buffer, so only the position is needed.
            Varyings WireVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 WireFrag(Varyings input) : SV_Target
            {
                return _WireColor;
            }
            ENDHLSL
        }
    }

    FallBack Off
}

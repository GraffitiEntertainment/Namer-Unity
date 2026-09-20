Shader "GraffitiEntertainment.Namer/NamerPreviewWire"
{
    Properties
    {
        _WireColor("Wire Color", Color) = (0, 1, 1, 1)
    }

    SubShader
    {
        // Geometry+100 draws after the opaque preview mesh so on-surface edges land
        // on top of the shaded surface. ZTest LEqual + ZWrite Off below depth-occlude
        // the wire against that solid pass: edges geometrically behind the visible
        // surface (back faces, and fronts hidden behind other geometry) are dropped.
        // Lines have no winding, so GPU face culling cannot do this — depth
        // occlusion by the solid pass is the mechanism.
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
            ZTest LEqual

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
            static const float kWireDepthBias = 1e-5;

            Varyings WireVert(Attributes input)
            {
                Varyings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                // Edges that lie exactly ON the opaque surface have depth equal to the
                // solid pass's; float rounding in the two interpolation paths can push a
                // line fragment a few ulps farther and drop it (dashed edges). Pull the
                // wire a hair toward the camera — direction flips with reversed-Z.
                #if UNITY_REVERSED_Z
                output.positionCS.z += kWireDepthBias;
                #else
                output.positionCS.z -= kWireDepthBias;
                #endif
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

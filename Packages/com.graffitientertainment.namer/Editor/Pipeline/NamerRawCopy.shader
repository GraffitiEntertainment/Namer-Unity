// NamerRawCopy.shader
// Editor-only raw upload material for NamerComputePipeline.Upload.
//
// Graphics.Blit samples its source, so under a Linear-color-space project an
// sRGB-declared texture is hardware-decoded on upload and the compute kernels'
// conditional _SourceIsSrgb decode would double-decode it. This pass cancels that
// decode: when _REENCODE_SRGB is set (source is sRGB-declared AND the project is
// Linear), the fragment shader applies the exact IEC 61966-2-1 linear -> sRGB
// encode. Decode and re-encode both run in float inside this one pass with a
// single quantization at the write, so the target receives the source bytes
// bit-exactly — the same guarantee Graphics.CopyTexture gives, without its
// equal-dimension / equal-mip-count / equal-block-size restrictions (imported
// sources are routinely RGB24 or mipmapped).
//
// In a Gamma project the keyword is off: sampling never decodes there, so the
// pass is a plain copy. UnityCG built-in style is deliberate — this is a direct
// full-screen draw via Graphics.Blit(source, target, material), never a camera
// render, so it has no render-pipeline dependency.

Shader "Hidden/Namer/NamerRawCopy"
{
    Properties
    {
        _MainTex("Source", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _REENCODE_SRGB
            #include "UnityCG.cginc"

            sampler2D _MainTex;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            // Exact IEC 61966-2-1 linear -> sRGB encode (same curve the kernels'
            // SRGBToLinear inverts), not UnityCG's approximate LinearToGammaSpace.
            float3 NamerLinearToSRGB(float3 c)
            {
                c = max(c, 0.0.xxx);
                return c <= 0.0031308.xxx ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 c = tex2D(_MainTex, i.uv);
                #ifdef _REENCODE_SRGB
                c.rgb = NamerLinearToSRGB(c.rgb);
                #endif
                return c;
            }
            ENDCG
        }
    }
}

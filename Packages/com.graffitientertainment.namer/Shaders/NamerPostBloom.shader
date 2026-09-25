// NAMER After-pane bloom approximation (05.1 D-04, STYL-09).
//
// A three-pass Hidden/ blit shader used ONLY by the processor window's
// Stylized + Post lens: it approximates the URP Bloom volume the fitted
// profile generates, so the After pane approves the look without play mode.
// It never runs at runtime and never touches any saved asset — the scene
// Volume remains the runtime truth.
//
//   Pass 0 BrightPass   — URP Bloom.shader's soft-knee prefilter: quadratic
//                         knee around the threshold (knee = threshold * 0.5),
//                         brightness = Max3(r, g, b). The threshold uniform is
//                         already LINEAR — the window converts the profile's
//                         gamma-space value with Mathf.GammaToLinearSpace,
//                         exactly the conversion URP's PostProcessPass applies
//                         at render time.
//   Pass 1 BlurPass     — separable 9-tap gaussian along _BlurDirection
//                         (horizontal then vertical dispatch), unit-texel
//                         offsets, URP's 9-tap weights.
//   Pass 2 CompositePass — source + bloom * _BloomIntensity.
//
// All half math; named constants for the knee scale/epsilons and tap weights.
Shader "Hidden/NamerPostBloom"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _BloomTex ("Bloom", 2D) = "black" {}
        _BloomThreshold ("Threshold (linear)", Float) = 0.9
        _BloomIntensity ("Intensity", Float) = 1.0
        _BlurDirection ("Blur Direction", Vector) = (1, 0, 0, 0)
    }

    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" "RenderType" = "Opaque" }
        Cull Off
        ZWrite Off
        ZTest Always

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_MainTex);   SAMPLER(sampler_MainTex);
        TEXTURE2D(_BloomTex);  SAMPLER(sampler_BloomTex);

        float4 _MainTex_TexelSize;
        float4 _BlurDirection;
        half   _BloomThreshold;
        half   _BloomIntensity;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float2 uv         : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 uv         : TEXCOORD0;
        };

        Varyings NamerPostBloomVert(Attributes input)
        {
            Varyings output;
            output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
            output.uv = input.uv;
            return output;
        }
        ENDHLSL

        Pass
        {
            Name "BrightPass"

            HLSLPROGRAM
            #pragma vertex NamerPostBloomVert
            #pragma fragment FragBright

            // URP Bloom knee: knee = threshold * 0.5 (PostProcessPass.cs).
            static const half kBloomKneeScale = 0.5;
            // URP Bloom.shader's own 1e-4 denominators (bright-pass division guards).
            static const half kBloomEpsilon = 1e-4;

            half4 FragBright(Varyings input) : SV_Target
            {
                half3 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;
                half brightness = Max3(color.r, color.g, color.b);
                half knee = _BloomThreshold * kBloomKneeScale;

                // Quadratic soft knee (URP Bloom.shader Prefilter): a smooth ramp
                // into the threshold instead of a hard cut.
                half softness = clamp(brightness - _BloomThreshold + knee, 0.0, 2.0 * knee);
                softness = softness * softness / (4.0 * knee + kBloomEpsilon);
                half multiplier = max(brightness - _BloomThreshold, softness)
                    / max(brightness, kBloomEpsilon);
                return half4(color * multiplier, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlurPass"

            HLSLPROGRAM
            #pragma vertex NamerPostBloomVert
            #pragma fragment FragBlur

            // 9-tap gaussian: center weight + four mirrored taps, unit-texel
            // offsets. Weights sum to 1 (URP's 9-tap set).
            static const half kBlurTapWeights[5] =
            {
                0.22702703, 0.19459459, 0.12162162, 0.05405405, 0.01621622
            };
            static const int kBlurTapCount = 5;

            half4 FragBlur(Varyings input) : SV_Target
            {
                half4 color = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv)
                    * kBlurTapWeights[0];
                [unroll]
                for (int tap = 1; tap < kBlurTapCount; tap++)
                {
                    float2 offset = _BlurDirection.xy * _MainTex_TexelSize.xy * tap;
                    color += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv + offset)
                        * kBlurTapWeights[tap];
                    color += SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv - offset)
                        * kBlurTapWeights[tap];
                }

                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "CompositePass"

            HLSLPROGRAM
            #pragma vertex NamerPostBloomVert
            #pragma fragment FragComposite

            half4 FragComposite(Varyings input) : SV_Target
            {
                half3 source = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv).rgb;
                half3 bloom = SAMPLE_TEXTURE2D(_BloomTex, sampler_BloomTex, input.uv).rgb;
                return half4(source + bloom * _BloomIntensity, 1.0);
            }
            ENDHLSL
        }
    }
}

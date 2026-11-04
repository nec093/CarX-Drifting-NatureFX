// NatureFX: in place of HDRP's final pass (Hidden/HDRP/FinalPass) while DLSS upscales (Dlaa.cs). HDRP renders at the
// lower resolution with its own TAA off and a plain upscale filter; PostPass has already made the picture at the
// display resolution (DLSS, then the own exposure, bloom and tone curve: _NFX_UpscaledFinal), so this pass takes that,
// pixel for pixel, where HDRP's final pass would upscale its own. Everything else HDRP's final pass does is kept as
// it does it (HDRP 2023.2's FinalPass.shader): the flip and the viewport (_UVTransform, _ViewPortSize), film grain,
// dithering, the after post-process objects, the alpha channel, HDR output with the UI composited.
Shader "Hidden/NatureFX/UpscaledFinal"
{
    HLSLINCLUDE

        #pragma target 4.5
        #pragma only_renderers d3d11 vulkan metal

        #pragma multi_compile_fragment _ SCREEN_COORD_OVERRIDE
        #pragma multi_compile_local_fragment _ GRAIN
        #pragma multi_compile_local_fragment _ DITHER
        #pragma multi_compile_local_fragment _ ENABLE_ALPHA
        #pragma multi_compile_local_fragment _ APPLY_AFTER_POST
        #pragma multi_compile_local_fragment _ HDR_INPUT HDR_ENCODING

        #ifdef HDR_ENCODING
        #define HDR_INPUT 1
        #endif

        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Color.hlsl"
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/ScreenCoordOverride.hlsl"
        #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
#if defined(HDR_ENCODING)
        #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/HDROutput.hlsl"
#endif

        TEXTURE2D_X(_NFX_UpscaledFinal); // PostPass's picture at the display resolution
        TEXTURE2D(_GrainTexture);
        TEXTURE2D_X(_AfterPostProcessTexture);
        TEXTURE2D_ARRAY(_BlueNoiseTexture);
        TEXTURE2D_X(_AlphaTexture);
        TEXTURE2D_X(_UITexture);

        float2 _GrainParams;
        float4 _GrainTextureParams;
        float3 _DitherParams;
        float4 _UVTransform;
        float4 _ViewPortSize;
        float  _KeepAlpha;

        float4 _HDROutputParams;
        float4 _HDROutputParams2;
        #define _MinNits            _HDROutputParams.x
        #define _MaxNits            _HDROutputParams.y
        #define _PaperWhite         _HDROutputParams.z
        #define _OneOverPaperWhite  _HDROutputParams.w
        #define _RangeReductionMode (int)_HDROutputParams2.x

        struct Attributes
        {
            uint vertexID : SV_VertexID;
            UNITY_VERTEX_INPUT_INSTANCE_ID
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float2 texcoord   : TEXCOORD0;
            UNITY_VERTEX_OUTPUT_STEREO
        };

        Varyings Vert(Attributes input)
        {
            Varyings output;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
            output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
            output.texcoord = GetFullScreenTriangleTexCoord(input.vertexID);
            return output;
        }

        float4 Frag(Varyings input) : SV_Target0
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

            float2 positionNDC = input.texcoord;
            uint2 positionSS = input.texcoord * _ScreenSize.xy;
            // the display's pixel (the viewport's size, flipped as HDRP flips)
            uint2 scaledPositionSS = ((input.texcoord.xy * _UVTransform.xy) + _UVTransform.zw) * _ViewPortSize.xy;
            positionSS = positionSS * _UVTransform.xy + _UVTransform.zw * (_ScreenSize.xy - 1.0);
            positionNDC = positionNDC * _UVTransform.xy + _UVTransform.zw;

            float3 outColor = LOAD_TEXTURE2D_X(_NFX_UpscaledFinal, scaledPositionSS).rgb;
            #ifdef HDR_INPUT
            // the own picture is display referred (0..1): HDR output wants nits, white at the paper white
            outColor *= _PaperWhite;
            #endif

            float outAlpha = (_KeepAlpha == 1.0) ? LOAD_TEXTURE2D_X(_AlphaTexture, positionSS).x : 1.0;

            #if (defined(GRAIN) || defined(DITHER)) && !defined(HDR_INPUT)
            outColor = saturate(outColor);
            #endif

            #if GRAIN
            {
                float grain = SAMPLE_TEXTURE2D(_GrainTexture, s_linear_repeat_sampler, (SCREEN_COORD_APPLY_SCALEBIAS(positionNDC) * _GrainTextureParams.xy) + _GrainTextureParams.zw).w;
                grain = (grain - 0.5) * 2.0;
                float lum = Luminance(outColor);
                #ifdef HDR_INPUT
                lum *= _OneOverPaperWhite;
                #endif
                lum = 1.0 - sqrt(lum);
                lum = lerp(1.0, lum, _GrainParams.y);
                outColor += outColor * grain * _GrainParams.x * lum;
            }
            #endif

            #if DITHER
            {
                float3 ditherParams = _DitherParams;
                float noise = SAMPLE_TEXTURE2D_ARRAY(_BlueNoiseTexture, s_linear_repeat_sampler, positionNDC * ditherParams.xy, ditherParams.z).a;
                #ifdef HDR_INPUT
                float3 sRGBColor = LinearToSRGB(outColor * _OneOverPaperWhite);
                #else
                float3 sRGBColor = LinearToSRGB(outColor);
                #endif
                noise = noise * 2.0 - 1.0;
                noise = FastSign(noise) * (1.0 - sqrt(1.0 - abs(noise)));
                #ifdef HDR_INPUT
                outColor = SRGBToLinear(sRGBColor + noise / 255.0) * _PaperWhite;
                #else
                outColor = SRGBToLinear(sRGBColor + noise / 255.0);
                #endif
            }
            #endif

            #if APPLY_AFTER_POST
            float4 afterPostColor = SAMPLE_TEXTURE2D_X_LOD(_AfterPostProcessTexture, s_point_clamp_sampler, positionNDC.xy * _RTHandleScale.xy, 0);
            #ifdef HDR_ENCODING
                afterPostColor.rgb = ProcessUIForHDR(afterPostColor.rgb, _PaperWhite, _MaxNits);
            #endif
            outColor = afterPostColor.a * outColor + afterPostColor.rgb;
            #endif

            #ifdef HDR_ENCODING
            {
                float4 uiValue = SAMPLE_TEXTURE2D_X_LOD(_UITexture, s_point_clamp_sampler, positionNDC.xy * _RTHandleScale.xy, 0);
                outColor = SceneUIComposition(uiValue, outColor, _PaperWhite, _MaxNits);
                outColor = OETF(outColor, _MaxNits);
            }
            #endif

            return float4(outColor, outAlpha);
        }

    ENDHLSL

    SubShader
    {
        Tags{ "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Cull Off ZWrite Off ZTest Always

            HLSLPROGRAM
                #pragma vertex Vert
                #pragma fragment Frag
            ENDHLSL
        }
    }
}

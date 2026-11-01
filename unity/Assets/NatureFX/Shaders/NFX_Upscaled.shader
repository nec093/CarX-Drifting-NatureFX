// NatureFX: in place of HDRP's TemporalAA shader while DLSS upscales (Dlaa.cs). HDRP renders at the lower resolution
// with its TAAU upscaler chosen, so everything after it (depth of field, motion blur, the final pass) runs at the
// display resolution; its TAAU pass draws this instead, which hands over the picture PostPass already made at the
// display resolution (DLSS, then the own exposure, bloom and tone curve): _NFX_UpscaledFinal, pixel for pixel.
// HDRP's second TAA after a physically based depth of field (its anti-flicker at the maximum, 3.5; the first pass's
// is set to 0 meanwhile) passes its input through. The pass names are HDRP's (HDRenderPipeline.PostProcess.cs).
Shader "Hidden/NatureFX/Upscaled"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D_X(_NFX_UpscaledFinal);
    TEXTURE2D_X(_InputTexture);
    float4 _TaaPostParameters;

    struct Attributes
    {
        uint vertexID : SV_VertexID;
        UNITY_VERTEX_INPUT_INSTANCE_ID
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float2 texcoord : TEXCOORD0;
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

    float4 Upscaled(Varyings input) : SV_Target0
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        if (_TaaPostParameters.y > 1.0)
            return float4(SAMPLE_TEXTURE2D_X_LOD(_InputTexture, s_linear_clamp_sampler, input.texcoord * _RTHandleScale.xy, 0).rgb, 1.0);
        return float4(LOAD_TEXTURE2D_X(_NFX_UpscaledFinal, (uint2)input.positionCS.xy).rgb, 1.0);
    }

    float4 Nothing(Varyings input) : SV_Target0
    {
        return 0.0;
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass { Name "TAA" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Upscaled
            ENDHLSL }
        Pass { Name "Excluded From TAA" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Upscaled
            ENDHLSL }
        Pass { Name "TAAU" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Upscaled
            ENDHLSL }
        Pass { Name "Copy History" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Nothing
            ENDHLSL }
    }
    Fallback Off
}

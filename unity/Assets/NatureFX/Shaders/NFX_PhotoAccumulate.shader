// NatureFX photo mode (PhotoMode.cs): the running average of the finished HDR frames while the camera rests, before
// post-processing (so the jitter of the anti-aliasing averages out too). Pass 0 blends the current frame into the
// average with weight 1 / n (Blend SrcAlpha OneMinusSrcAlpha); pass 1 puts the average back into the camera colour.
Shader "Hidden/NatureFX/PhotoAccumulate"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_NFX_PhotoSource);
    float _NFX_PhotoWeight;

    float4 Accumulate(Varyings v) : SV_Target
    {
        float3 c = LOAD_TEXTURE2D_X(_NFX_PhotoSource, uint2(v.positionCS.xy)).rgb;
        // a NaN or infinity would stay in the average for good
        c = any(!isfinite(c)) ? 0.0 : c;
        return float4(c, _NFX_PhotoWeight);
    }

    float4 Show(Varyings v) : SV_Target
    {
        return float4(LOAD_TEXTURE2D_X(_NFX_PhotoSource, uint2(v.positionCS.xy)).rgb, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Accumulate"
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Accumulate
            ENDHLSL
        }

        Pass
        {
            Name "Show"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Show
            ENDHLSL
        }
    }
    Fallback Off
}

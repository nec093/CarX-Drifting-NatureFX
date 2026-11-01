// Draws nothing: OwnGlass.cs puts it in place of the game's glass materials while the own engine draws the glass
// (Glass.hlsl, roadmap E). Its only pass has a light mode HDRP never renders.
Shader "Hidden/NatureFX/Invisible"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Never"
            Tags { "LightMode" = "NFXNever" }
            ColorMask 0
            ZWrite Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag
            float4 Vert(float4 p : POSITION) : SV_POSITION { return 0.0; }
            float4 Frag() : SV_Target { return 0.0; }
            ENDHLSL
        }
    }
    Fallback Off
}

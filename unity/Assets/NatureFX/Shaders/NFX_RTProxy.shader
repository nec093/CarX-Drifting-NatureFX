// Material values of a game material for the own ray tracing engine (NFXNative): RayTracingScene.cs makes one copy
// of this material per game material, carrying its base colour map, tint, alpha test and cutoff, metalness,
// smoothness, clear coat and emission, and NativeEngine reads them from it. Never drawn: the pass below only makes it a valid
// shader.
Shader "Hidden/NatureFX/RTProxy"
{
    Properties
    {
        _BaseColorMap("Base Color Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1, 1, 1, 1)
        _AlphaCutoff("Alpha Cutoff", Range(0, 1)) = 0.5
        _NFX_AlphaTest("Alpha Test", Float) = 0
        _Metallic("Metallic", Range(0, 1)) = 0
        _Smoothness("Smoothness", Range(0, 1)) = 0.5
        _NFX_Coat("Clear Coat", Float) = 0
        [HDR] _EmissiveColor("Emissive Color", Color) = (0, 0, 0, 1)
        _EmissiveColorMap("Emissive Color Map", 2D) = "white" {}
    }

    SubShader
    {
        Pass
        {
            Name "Values"
            ColorMask 0
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Vert(float4 position : POSITION) : SV_POSITION
            {
                return 0.0;
            }

            float4 Frag() : SV_Target
            {
                return 0.0;
            }
            ENDHLSL
        }
    }
    Fallback Off
}

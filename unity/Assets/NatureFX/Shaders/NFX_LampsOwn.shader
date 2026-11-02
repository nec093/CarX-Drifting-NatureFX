// NatureFX lamp light with the own BSDF (HDRP hand-over C1): the lamp masks' pass of NFX_SunExact (LampsExact) without
// HDRP's G-buffer, so RTShadowPass need not draw the opaque renderers' GBuffer pass again. HDRP's punctual lights
// (their attenuation, spot cones and cookies: the headlights' beam patterns) on the surface trace's colour and metal and
// HDRP's normal buffer's normal and roughness, lit by NFX_OwnBrdf (NFX_LampsExact.hlsl). Same targets as LampsExact:
// the light the lamps' shadows take away, the masks' history, all of the lamps' light.
// A shader of its own: if it does not compile, only the own BSDF mode is missing (RTShadowPass keeps the G-buffer).
Shader "Hidden/NatureFX/LampsOwn"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "LampsOwn"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment FragLamps

            #define LIGHTLOOP_DISABLE_TILE_AND_CLUSTER
            #define USE_FPTL_LIGHTLIST
            // shadow filtering defines HDRP's light loop needs to compile (no shadow map is sampled)
            #define PUNCTUAL_SHADOW_MEDIUM
            #define DIRECTIONAL_SHADOW_MEDIUM
            #define AREA_SHADOW_MEDIUM
            #define SCREEN_SPACE_SHADOWS_OFF

            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPass.cs.hlsl"
            #define SHADERPASS SHADERPASS_DEFERRED_LIGHTING
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Material.hlsl"
            #define HAS_LIGHTLOOP
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoopDef.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.hlsl"
            #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Lighting/LightLoop/LightLoop.hlsl"

            float4 _NFX_RTSunDir;
            #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

            #define NFX_OWN_BRDF
            #include "Assets/NatureFX/ShaderLibrary/NFX_LampsExact.hlsl"
            ENDHLSL
        }
    }
}

// NatureFX exact sun light: the direct sun light HDRP added to each pixel, worked out the way HDRP's deferred lighting
// does it (Hidden/HDRP/Deferred), so the ray traced shadows (NFX_RTShadow.shader, Composite) take away exactly that.
// HDRP's own G-buffer is gone by the time the custom pass runs (its memory is reused), so RTShadowPass draws the
// opaque renderers' GBuffer pass again into its own targets and binds them as _GBufferTexture0..3. Per pixel:
// HDRP's G-buffer decode, the sun's DirectionalLightData (cookies, clouds, the atmosphere's tint, the sun disc's
// roughness clamp, micro shadows) and Lit's BSDF, without its shadow maps (HDRP's sun shadows are off while the ray
// traced ones run). Output: rgb the sun light, pre-exposed as the camera colour; a 1 where the pixel was drawn
// (0: no GBuffer pass there, e.g. forward only materials; the composite falls back to its estimate).
Shader "Hidden/NatureFX/SunExact"
{
    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "SunExact"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag

            #define LIGHTLOOP_DISABLE_TILE_AND_CLUSTER
            #define USE_FPTL_LIGHTLIST
            // shadow filtering defines HDRP's light loop needs to compile (no shadow map is sampled: the sun has none)
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

            float4 _NFX_RTSunDir; // xyz: direction towards the sun (picks HDRP's light), w: 1 when the sun is up
            float _NFX_OwnSun;    // 1: HDRP's sun is switched off for HDRP's own lighting (affects neither diffuse nor
                                  // specular); this pass works it out at full strength all the same (OwnLighting.Sun)

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                uint2 pixel = (uint2)input.positionCS.xy;
                float depth = LoadCameraDepth(pixel);
                if (depth == UNITY_RAW_FAR_CLIP_VALUE || _NFX_RTSunDir.w <= 0.0)
                    return 0.0;
                // RTShadowPass clears the base colour to magenta with alpha 0 (a value no material writes in practice)
                float4 g0 = LOAD_TEXTURE2D_X(_GBufferTexture0, pixel);
                if (g0.r > 0.999 && g0.g < 0.001 && g0.b > 0.999 && g0.a < 0.001)
                    return 0.0;

                PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V, pixel / GetTileSize());
                float3 V = GetWorldSpaceNormalizeViewDir(posInput.positionWS);
                BSDFData bsdfData;
                BuiltinData builtinData;
                DECODE_FROM_GBUFFER(posInput.positionSS, UINT_MAX, bsdfData, builtinData);
                PreLightData preLightData = GetPreLightData(V, posInput, bsdfData);

                // as LightLoop sets it up, but no contact shadows: the ray traced shadows cover them, and HDRP's
                // contact shadow texture is not this frame's any more either
                LightLoopContext context;
                context.shadowContext = InitShadowContext();
                context.shadowValue = 1;
                context.sampleReflection = 0;
            #ifdef APPLY_FOG_ON_SKY_REFLECTIONS
                context.positionWS = posInput.positionWS;
            #endif
                context.contactShadow = 0;
                context.contactShadowFade = 0.0;

                float3 diffuse = 0.0;
                float3 specular = 0.0;
                for (uint i = 0; i < _DirectionalLightCount; ++i)
                {
                    DirectionalLightData light = _DirectionalLightDatas[i];
                    // the sun the ray traced shadows are for (a moon or a fill light keeps HDRP's own handling)
                    if (dot(-light.forward, _NFX_RTSunDir.xyz) < 0.9995)
                        continue;
                    if (_NFX_OwnSun > 0.5)
                    {
                        light.diffuseDimmer = 1.0;
                        light.specularDimmer = 1.0;
                    }
                    DirectLighting lighting = ShadeSurface_Directional(context, posInput, builtinData, preLightData, light, bsdfData, V);
                    diffuse += lighting.diffuse;
                    specular += lighting.specular;
                }
                // Lit's PostEvaluateBSDF: the diffuse light on the surface's (subsurface modified) diffuse colour, the
                // specular light rescaled for multiple scattering
                specular *= 1.0 + bsdfData.fresnel0 * preLightData.energyCompensation;
                float3 sun = (diffuse * GetModifiedDiffuseColorForSSS(bsdfData) + specular) * GetCurrentExposureMultiplier();
                return float4(sun, 1.0);
            }
            ENDHLSL
        }
        Pass
        {
            Name "LampsExact"
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
            // shadow filtering defines HDRP's light loop needs to compile (no shadow map is sampled: the sun has none)
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

            float4 _NFX_RTSunDir; // xyz: direction towards the sun (picks HDRP's light), w: 1 when the sun is up
            #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

            #include "Assets/NatureFX/ShaderLibrary/NFX_LampsExact.hlsl"
            ENDHLSL
        }
        Pass
        {
            // DLSS Ray Reconstruction's guides (Dlaa, NativeEngine.Dlss): the surface's diffuse albedo and its
            // specular albedo (HDRP's directional albedo, specularFGD), from the G-buffer drawn again
            Name "DlssGuides"
            ZWrite Off
            ZTest Always
            Blend Off
            Cull Off

            HLSLPROGRAM
            #pragma target 4.5
            #pragma only_renderers d3d11 vulkan metal
            #pragma vertex Vert
            #pragma fragment Frag

            #define LIGHTLOOP_DISABLE_TILE_AND_CLUSTER
            #define USE_FPTL_LIGHTLIST
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

            struct Attributes
            {
                uint vertexID : SV_VertexID;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            struct Guides
            {
                float4 diffuse : SV_Target0;  // rgb diffuse albedo
                float4 specular : SV_Target1; // rgb specular albedo
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
                return output;
            }

            Guides Frag(Varyings input)
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                Guides o;
                // the sky and pixels without a GBuffer pass (glass, particles): grey, no specular
                o.diffuse = float4(0.5, 0.5, 0.5, 1.0);
                o.specular = 0.0;
                uint2 pixel = (uint2)input.positionCS.xy;
                float depth = LoadCameraDepth(pixel);
                if (depth == UNITY_RAW_FAR_CLIP_VALUE)
                    return o;
                float4 g0 = LOAD_TEXTURE2D_X(_GBufferTexture0, pixel);
                if (g0.r > 0.999 && g0.g < 0.001 && g0.b > 0.999 && g0.a < 0.001)
                    return o;
                PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V, pixel / GetTileSize());
                float3 V = GetWorldSpaceNormalizeViewDir(posInput.positionWS);
                BSDFData bsdfData;
                BuiltinData builtinData;
                DECODE_FROM_GBUFFER(posInput.positionSS, UINT_MAX, bsdfData, builtinData);
                PreLightData preLightData = GetPreLightData(V, posInput, bsdfData);
                o.diffuse = float4(GetModifiedDiffuseColorForSSS(bsdfData), 1.0);
                o.specular = float4(preLightData.specularFGD, 1.0);
                return o;
            }
            ENDHLSL
        }
    }
    Fallback Off
}

// NatureFX: the own engine's 3D fog on what HDRP draws see-through (TransparentFogPass.cs). The fog goes onto the opaque
// picture in the composite (NFX_Atmosphere, after the opaque passes); HDRP's own fog is off, so its transparent
// materials (fences of planks on a card, foliage cards drawn blended, signs, the garage's car paint) came out unfogged:
// dark and sharp in front of a fogged forest.
//   pass 0 Coverage: drawn over the transparent renderers in place of their own shader, keeping their material's values
//     (RendererListDesc.overrideShader): r the nearest one's device depth (blend max: reversed Z), a their coverage
//     (1 - the product of what each lets through), against the camera's depth
//   pass 1 Apply: the picture before the transparent objects (B, already fogged) and after them (C): what they added,
//     C - (1 - a) B, gets the fog in front of the nearest one, and its share a of the air's light
Shader "Hidden/NatureFX/TransparentFog"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"
    #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }

        Pass
        {
            Name "Coverage"
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend One One, One OneMinusSrcAlpha
            BlendOp Max, Add

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            // the renderer's own material's values, by name (HDRP Lit and Unlit, the built-in names); not in a
            // UnityPerMaterial buffer: the materials' layouts differ, each draw gets its own values
            TEXTURE2D(_BaseColorMap);
            TEXTURE2D(_UnlitColorMap);
            TEXTURE2D(_MainTex);
            float4 _BaseColor;
            float4 _BaseColorMap_ST;
            float4 _UnlitColor;
            float4 _UnlitColorMap_ST;
            float4 _Color;
            float4 _MainTex_ST;
            float _AlphaCutoffEnable;
            float _AlphaCutoff;
            float _BlendMode;      // HDRP: 0 alpha, 1 additive, 4 premultiplied
            float4 _NFX_PTiles;    // NatureFX/Particle's own (y >= 1): those fog themselves
            float _NFX_SelfFog;    // the NatureFX/* lit shaders' (MaterialProfiles): their forward pass fogs them

            struct Attributes
            {
                float3 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
            };

            Varyings Vert(Attributes a)
            {
                Varyings o;
                float3 positionRWS = mul(UNITY_MATRIX_M, float4(a.positionOS, 1.0)).xyz;
                o.positionCS = mul(UNITY_MATRIX_VP, float4(positionRWS, 1.0));
                o.uv = a.uv;
                o.color = a.color;
                return o;
            }

            float4 Frag(Varyings v) : SV_Target
            {
                if (_NFX_PTiles.y > 0.5 || _NFX_SelfFog > 0.5)
                    discard;
                // whichever of the names the material has (an unset colour is 0: that name contributes nothing)
                float lit = _BaseColor.a * SAMPLE_TEXTURE2D(_BaseColorMap, s_linear_repeat_sampler, v.uv * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw).a;
                float unlit = _UnlitColor.a * SAMPLE_TEXTURE2D(_UnlitColorMap, s_linear_repeat_sampler, v.uv * _UnlitColorMap_ST.xy + _UnlitColorMap_ST.zw).a;
                float legacy = _Color.a * SAMPLE_TEXTURE2D(_MainTex, s_linear_repeat_sampler, v.uv * _MainTex_ST.xy + _MainTex_ST.zw).a;
                float alpha = saturate(max(lit, max(unlit, legacy)));
                if (_AlphaCutoffEnable > 0.5 && alpha < _AlphaCutoff)
                    discard;
                // additive: covers nothing (what is behind shows whole), only dimmed by the fog in front of it
                if (abs(_BlendMode - 1.0) < 0.5)
                    alpha = 0.0;
                if (alpha < 0.004 && abs(_BlendMode - 1.0) >= 0.5)
                    discard;
                return float4(v.positionCS.z, 0.0, 0.0, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Apply"
            Cull Off ZWrite Off ZTest Always Blend Off

            HLSLPROGRAM
            #pragma vertex VertFull
            #pragma fragment Apply

            TEXTURE2D_X(_NFX_TFColor);  // after the transparent objects
            TEXTURE2D_X(_NFX_TFBack);   // before them (the composite's fogged picture)
            TEXTURE2D_X(_NFX_TFCover);  // pass 0

            struct VaryingsFull
            {
                float4 positionCS : SV_POSITION;
            };

            VaryingsFull VertFull(uint id : SV_VertexID)
            {
                VaryingsFull o;
                o.positionCS = GetFullScreenTriangleVertexPosition(id);
                return o;
            }

            float4 Apply(VaryingsFull v) : SV_Target
            {
                uint2 pixel = (uint2)v.positionCS.xy;
                float3 color = LOAD_TEXTURE2D_X(_NFX_TFColor, pixel).rgb;
                float4 cover = LOAD_TEXTURE2D_X(_NFX_TFCover, pixel);
                if (cover.r <= 0.0 || _NFX_FogVolumeParams.x < 0.5)
                    return float4(color, 1.0);
                float3 back = LOAD_TEXTURE2D_X(_NFX_TFBack, pixel).rgb;
                float a = saturate(cover.a);
                float2 uv = (pixel + 0.5) * _ScreenSize.zw;
                float3 rel = ComputeWorldSpacePosition(uv, cover.r, UNITY_MATRIX_I_VP);
                float distance = length(rel);
                float3 dir = rel / max(distance, 1e-4);
                float viewDepth = distance * dot(dir, -UNITY_MATRIX_V[2].xyz);
                float4 fog = NFX_Fog(uv, viewDepth, distance, dir);
                // what the transparent objects added over what was behind them (signed: a dark pane takes light away)
                float3 added = color - (1.0 - a) * back;
                float3 result = (1.0 - a) * back + added * fog.a + a * fog.rgb * GetCurrentExposureMultiplier();
                if (any(!isfinite(result)))
                    return float4(color, 1.0);
                return float4(max(result, 0.0), 1.0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}

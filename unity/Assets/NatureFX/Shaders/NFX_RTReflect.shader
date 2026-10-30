// NatureFX ray traced reflections, composite (full resolution). The reflection is traced by the own engine
// (NFXNative Reflection.hlsl) at trace resolution and accumulated and denoised by the Temporal / Denoise passes below; it is
// upsampled with depth weights and blended over the camera colour by smoothness, replacing the Fresnel share of
// the image HDRP gave to its own (probe / screen space) reflection. Where nothing was hit, HDRP's stays.
Shader "Hidden/NatureFX/RTReflect"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Builtin/BuiltinData.hlsl"
    #include "Assets/NatureFX/ShaderLibrary/NFX_RayTracing.hlsl"

    TEXTURE2D_X(_NFX_ColorTex);     // copy of the camera colour (full res)
    TEXTURE2D_X(_NFX_SourceTex);    // denoised reflection: rgb radiance, a = traced share
    float4 _NFX_LowSize;            // trace resolution: xy size, zw 1 / size
    float4 _NFX_ReflCompose;        // x: intensity, y: roughness cutoff
    float4 _NFX_TargetSize;         // size of the target being drawn (denoise: trace resolution)
    float _NFX_DenoiseStep;         // a-trous step in trace-res pixels
    TEXTURE2D_X(_NFX_TraceTex);     // new frame of the reflection (trace resolution)
    TEXTURE2D_X(_NFX_HistoryTex);   // accumulated reflection of the previous frame
    TEXTURE2D_X(_NFX_DistTex);      // distance from the surface to what it reflects (0: nothing)
    float4 _NFX_ComposeParams;      // z: share of the new frame

    TEXTURE2D_X(_NFX_SurfaceAlbedo);  // RTSurfaceTrace: rgb albedo, a metalness and clear coat
    TEXTURE2D_X(_NFX_SurfaceNormal);  // w != 0: the trace found the surface the pixel shows

    // Roughness of what the reflection shows at a pixel: a clear coated surface (car paint) mirrors through its
    // varnish whatever roughness HDRP's normal buffer holds for the base layer
    float ReflRoughness(uint2 pixel, float baseRoughness)
    {
        bool coated = LOAD_TEXTURE2D_X(_NFX_SurfaceNormal, pixel).w != 0.0 && LOAD_TEXTURE2D_X(_NFX_SurfaceAlbedo, pixel).a > 0.495;
        return coated ? min(baseRoughness, NFX_COAT_ROUGHNESS) : baseRoughness;
    }

    #define SampleRT(tex, uv) SAMPLE_TEXTURE2D_X_LOD(tex, s_linear_clamp_sampler, (uv) * _RTHandleScale.xy, 0)

    // Glass reflections (RTGlassPass): the change Glass.hlsl worked out (rgb, signed: the sky HDRP mirrored swapped for
    // what the ray found), never taking away more than the glass reflects (a: its Fresnel)
    TEXTURE2D_X(_NFX_GlassTex);
    float4 _NFX_GlassCompose;       // x: intensity

    float4 GlassComposite(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float3 color = LOAD_TEXTURE2D_X(_NFX_ColorTex, pixel).rgb;
        float4 glass = LOAD_TEXTURE2D_X(_NFX_GlassTex, pixel);
        if (glass.a <= 0.0 || any(!isfinite(glass)))
            return float4(color, 1.0);
        float3 change = glass.rgb * _NFX_GlassCompose.x;
        // a reflection takes at most its Fresnel share away; the refraction (in the same change) may move any colour
        // there, so the floor is low
        return float4(max(color + change, color * 0.05), 1.0);
    }

    float4 Composite(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float2 uv = (pixel + 0.5) * _ScreenSize.zw;

        float3 color = LOAD_TEXTURE2D_X(_NFX_ColorTex, pixel).rgb;
        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return float4(color, 1);

        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        // full weight up to three quarters of the cutoff, fading to none at it: the Fresnel term of the trace
        // (refl.a) already makes rough surfaces reflect weakly (squaring the smoothness hid rough reflections)
        float cutoff = max(_NFX_ReflCompose.y, 1e-3);
        float roughness = ReflRoughness(pixel, nd.perceptualRoughness);
        float smooth = saturate((cutoff - roughness) / (cutoff * 0.25));
        if (smooth <= 0.0)
            return float4(color, 1);

        // joint bilateral upsample: the 4 nearest trace texels weighted by depth similarity
        float centerDepth = LinearEyeDepth(depth, _ZBufferParams);
        float2 lowPos = uv * _NFX_LowSize.xy - 0.5;
        float2 base = floor(lowPos);
        float2 f = lowPos - base;
        float4 refl = 0;
        float wsum = 0;
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            float2 o = float2(i & 1, i >> 1);
            float2 luv = (base + o + 0.5) * _NFX_LowSize.zw;
            uint2 fp = min((uint2)(saturate(luv) * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
            float d = LinearEyeDepth(LoadCameraDepth(fp), _ZBufferParams);
            float bil = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
            float w = bil * exp(-abs(d - centerDepth) / max(centerDepth * 0.03, 0.05)) + 1e-4;
            refl += SampleRT(_NFX_SourceTex, luv) * w;
            wsum += w;
        }
        refl /= wsum;
        if (refl.a <= 1e-4)
            return float4(color, 1); // nothing traced here (sky or rough): HDRP's reflection stays

        // refl.rgb = Fresnel * reflected light, refl.a = Fresnel (from the Reflection trace). Swap that share of the
        // image (HDRP's own probe / screen space reflection) for the traced one.
        float weight = smooth * _NFX_ReflCompose.x;
        float3 result = color * (1.0 - saturate(refl.a * weight)) + refl.rgb * weight;
        return float4(max(result, 0.0), 1);
    }

    // A-trous blur at trace resolution whose footprint follows the surface roughness: glossy reflections get
    // smoothed, a mirror keeps its detail (blurring it is what made the mirror soft and its edges grainy).
    float4 Denoise(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        float2 uv = v.positionCS.xy * _NFX_TargetSize.zw;
        uint2 pixel = min((uint2)(uv * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
        float4 center = SampleRT(_NFX_SourceTex, uv);

        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return center;
        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        // footprint grows with the roughness, up to three times the a-trous step for the roughest surfaces traced
        float roughness = ReflRoughness(pixel, nd.perceptualRoughness);
        float spread = min(roughness / 0.3, 3.0);
        if (spread < 0.05)
            return center;

        float centerDepth = LinearEyeDepth(depth, _ZBufferParams);
        float4 sum = center;
        float weights = 1.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                if (x == 0 && y == 0)
                    continue;
                float2 suv = uv + float2(x, y) * _NFX_DenoiseStep * spread * _NFX_TargetSize.zw;
                uint2 sp = min((uint2)(saturate(suv) * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
                float sd = LinearEyeDepth(LoadCameraDepth(sp), _ZBufferParams);
                NormalData snd;
                DecodeFromNormalBuffer(sp, snd);
                float w = exp(-abs(sd - centerDepth) / max(centerDepth * 0.02, 0.05))
                          * pow(saturate(dot(nd.normalWS, snd.normalWS)), 16.0)
                          * exp(-abs(ReflRoughness(sp, snd.perceptualRoughness) - roughness) * 8.0)
                          * 0.6 * saturate(spread);
                sum += SampleRT(_NFX_SourceTex, suv) * w;
                weights += w;
            }
        }
        return sum / weights;
    }

    // Temporal accumulation for reflections. A reflection does not move with the surface that shows it: a flat
    // mirror shows a virtual image lying "behind" it, at the hit distance along the view ray. Reprojecting that
    // virtual point keeps the history aligned on planar reflections (following the surface's own motion made
    // their edges shimmer while driving); surfaces with nothing reflected fall back to the motion vectors.
    float4 Temporal(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        float2 uv = v.positionCS.xy * _NFX_TargetSize.zw;
        uint2 pixel = min((uint2)(uv * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
        float4 current = SampleRT(_NFX_TraceTex, uv);

        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return current;

        float hitDist = LOAD_TEXTURE2D_X(_NFX_DistTex, (uint2)v.positionCS.xy).r;
        float2 prevUV;
        if (hitDist > 0.0)
        {
            PositionInputs pos = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
            float3 V = GetWorldSpaceNormalizeViewDir(pos.positionWS);
            float3 virtualRWS = pos.positionWS - V * hitDist;
            prevUV = ComputeNormalizedDeviceCoordinates(virtualRWS, UNITY_MATRIX_PREV_VP);
        }
        else
        {
            float2 motion;
            DecodeMotionVector(LOAD_TEXTURE2D_X(_CameraMotionVectorsTexture, pixel), motion);
            prevUV = uv - motion;
        }
        if (any(prevUV < 0.0) || any(prevUV > 1.0))
            return current;

        // variance clipping against the new frame's neighbourhood
        float4 m1 = 0, m2 = 0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float4 c = SampleRT(_NFX_TraceTex, uv + float2(x, y) * _NFX_TargetSize.zw);
                m1 += c;
                m2 += c * c;
            }
        }
        m1 /= 9.0;
        float4 sigma = sqrt(max(m2 / 9.0 - m1 * m1, 0.0));
        float4 history = clamp(SampleRT(_NFX_HistoryTex, prevUV), m1 - sigma * 1.25, m1 + sigma * 1.25);

        // a mirror's ray is deterministic: take mostly the new frame (no noise to average, no smearing); rough
        // surfaces sample random directions and lean on the history (the set blend) to average them out
        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        float roughness = ReflRoughness(pixel, nd.perceptualRoughness);
        // z = 1: the history was dropped (camera cut, reset)
        float blend = _NFX_ComposeParams.z >= 0.999 ? 1.0
            : lerp(lerp(0.7, _NFX_ComposeParams.z, saturate(roughness / 0.2)), _NFX_ComposeParams.z * 0.5,
                   saturate((roughness - 0.4) / 0.3));
        return lerp(history, current, blend);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }

        Pass
        {
            Name "Denoise"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Denoise
            ENDHLSL
        }

        Pass
        {
            Name "Temporal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Temporal
            ENDHLSL
        }

        Pass
        {
            Name "GlassComposite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment GlassComposite
            ENDHLSL
        }
    }
}

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
    #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

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
    // HDRP's sky as it mirrors it (the own engine's sky cube, in nits) and x 1 when HDRP uses it: the composite takes
    // that mirrored sky away where the traced reflection replaces it
    TEXTURECUBE(_NFX_HdrpSkyCube);
    SAMPLER(sampler_NFX_HdrpSkyCube);
    float4 _NFX_HdrpSky;

    TEXTURE2D_X(_NFX_SurfaceAlbedo);  // RTSurfaceTrace: rgb albedo, a metalness and clear coat
    TEXTURE2D_X(_NFX_SurfaceNormal);  // w != 0: the trace found the surface the pixel shows

    // Roughness of what the reflection shows at a pixel: a clear coated surface (car paint) mirrors through its
    // varnish whatever roughness HDRP's normal buffer holds for the base layer
    float ReflRoughness(uint2 pixel, float baseRoughness)
    {
        bool exact = LOAD_TEXTURE2D_X(_NFX_SurfaceNormal, pixel).w != 0.0;
        float a = LOAD_TEXTURE2D_X(_NFX_SurfaceAlbedo, pixel).a;
        bool coated = exact && a > 0.495;
        // chrome: a smooth metal is a mirror (NFX_NativeBridge PrepareReflection)
        bool chrome = exact && !coated && saturate(a / 0.49) >= 0.85 && baseRoughness <= 0.25;
        return coated ? min(baseRoughness, NFX_COAT_ROUGHNESS) : chrome ? min(baseRoughness, 0.03) : baseRoughness;
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
        if (any(!isfinite(glass)))
            return float4(color, 1.0);
        if (glass.a < -0.5)
        {
            // roadmap E, the own glass (Glass.hlsl with NFX_FLAG_OWN_GLASS): the colour is what is behind the glass
            // (HDRP's glass is hidden); it lets the transmission through and adds what it mirrors, which gets the 3D
            // fog between the camera and the glass
            uint packed = (uint)(-glass.a - 1.0);
            float3 transmission = float3(packed & 31u, (packed >> 5) & 31u, (packed >> 10) & 31u) / 31.0;
            float ownMetres = (float)(packed >> 15);
            float3 mirrored = glass.rgb * _NFX_GlassCompose.x;
            float3 own = color * transmission;
            float mirrorShare = saturate(1.0 - max(transmission.r, max(transmission.g, transmission.b)));
            if (_NFX_FogVolumeParams.x > 0.5 && ownMetres > 0.0)
            {
                float2 uv = (pixel + 0.5) * _ScreenSize.zw;
                float3 dir = normalize(ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP));
                float4 fog = NFX_Fog(uv, ownMetres * dot(dir, -UNITY_MATRIX_V[2].xyz), ownMetres, dir);
                mirrored = mirrored * fog.a + fog.rgb * GetCurrentExposureMultiplier() * mirrorShare;
            }
            return float4(max(own + mirrored, 0.0), 1.0);
        }
        if (glass.a <= 0.0)
            return float4(color, 1.0);
        float3 change = glass.rgb * _NFX_GlassCompose.x;
        // a reflection takes at most its Fresnel share away; the refraction (in the same change) may move any colour
        // there, so the floor is low
        color = max(color + change, color * 0.05);
        // the 3D fog in front of the glass (Glass.hlsl packs its distance: w = Fresnel share + 2 x whole metres). What
        // is seen through it was fogged with the surface behind it; what it mirrors was not: that share gets the fog
        // between the camera and the glass
        float metres = floor(glass.a * 0.5);
        float fresnel = saturate(glass.a - 2.0 * metres);
        if (_NFX_FogVolumeParams.x > 0.5 && metres > 0.0)
        {
            float2 uv = (pixel + 0.5) * _ScreenSize.zw;
            float3 dir = normalize(ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP));
            float viewDepth = metres * dot(dir, -UNITY_MATRIX_V[2].xyz);
            float4 fog = NFX_Fog(uv, viewDepth, metres, dir);
            color = color * (1.0 - fresnel * (1.0 - fog.a)) + fog.rgb * GetCurrentExposureMultiplier() * fresnel;
        }
        return float4(color, 1.0);
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
        float share = saturate(refl.a * weight);
        // what to take away: the Fresnel share of the image, or at least HDRP's own mirrored sky in that share (taking
        // only a share of the whole image left nearly all of HDRP's sky reflection on a car's paint, whose colour is
        // mostly its diffuse red: indoors the open sky still showed in the doors, under the traced garage)
        float3 removed = color * share;
        if (_NFX_HdrpSky.x > 0.5)
        {
            float3 posWS = ComputeWorldSpacePosition(uv, depth, UNITY_MATRIX_I_VP); // camera relative
            float3 R = reflect(normalize(posWS), nd.normalWS);
            // the sky as HDRP mirrors it: through the scene's reflection multiplier (y), and with HDRP's own Fresnel of the
            // surface (a clear coat or a dielectric 0.04, a metal its colour), not the traced share, which counts what the
            // trace found as a whole; never more than nine tenths of the pixel (the paint went black)
            float3 sky = SAMPLE_TEXTURECUBE_LOD(_NFX_HdrpSkyCube, sampler_NFX_HdrpSkyCube, R, 0).rgb * GetCurrentExposureMultiplier() * _NFX_HdrpSky.y;
            float4 surf = LOAD_TEXTURE2D_X(_NFX_SurfaceAlbedo, pixel);
            float metal = saturate((surf.a - (surf.a > 0.495 ? 0.5 : 0.0)) / 0.49);
            float3 f0 = lerp(0.04, surf.rgb, metal);
            float nv = saturate(dot(nd.normalWS, -normalize(posWS)));
            float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - nv, 5.0);
            removed = min(max(removed, sky * fresnel * weight), color * 0.9);
        }
        float3 result = color - removed + refl.rgb * weight;
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
        float3 centerPos = ComputeWorldSpacePosition((pixel + 0.5) * _ScreenSize.zw, depth, UNITY_MATRIX_I_VP);
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
                // off the centre's surface plane (a depth difference blurred grazing floors only sideways: streaks)
                float sDepth = LoadCameraDepth(sp);
                float3 sPos = ComputeWorldSpacePosition((sp + 0.5) * _ScreenSize.zw, sDepth, UNITY_MATRIX_I_VP);
                float off = sDepth == UNITY_RAW_FAR_CLIP_VALUE ? 1e4 : abs(dot(nd.normalWS, sPos - centerPos));
                NormalData snd;
                DecodeFromNormalBuffer(sp, snd);
                float w = exp(-off / max(centerDepth * 0.02, 0.05))
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

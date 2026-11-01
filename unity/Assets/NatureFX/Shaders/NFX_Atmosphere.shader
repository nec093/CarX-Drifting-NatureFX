// NatureFX: the own engine's sky and fog over the finished opaque image (Rendering/AtmospherePass.cs, the last pass
// of the AfterOpaqueAndSky volume). Sky pixels: the sky view table (NFXNative Sky.hlsli) looked up by direction,
// the sun's or the moon's disc dimmed by the air, the star field (MapNight) dimmed by the air, the clouds in front
// of all of it. Every pixel, the sky too: the 3D fog in front (NFX_FogCommon.hlsl).
Shader "Hidden/NatureFX/Atmosphere"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

    TEXTURE2D_X(_NFX_ColorTex);
    TEXTURE2D_X(_NFX_SkyView);
    TEXTURE2D_X(_NFX_SkyTransmittance);
    TEXTURE2D_X(_NFX_CloudTex);      // rgb radiance, a transmittance (quarter resolution)
    TEXTURECUBE(_NFX_Stars);
    SAMPLER(sampler_NFX_Stars);
    float4 _NFX_SkyParams;           // x 1 = sky, 2 = with clouds; y the light above the air (lux); z camera height (m); w its radius
    float4 _NFX_SkyLightDir;         // xyz towards the sun or the moon, w 1 = the moon
    float4 _NFX_StarParams;          // x how much of the star field (night)
    float4 _NFX_StarRowX, _NFX_StarRowY, _NFX_StarRowZ; // the star field's rotation

    // the same parameterisations as NFXNative's Sky.hlsli (keep them in step)
    static const float SkyGround = 6360000.0, SkyTop = 6460000.0;

    float3 SkyTransmittanceAlong(float r, float mu)
    {
        float H = sqrt(SkyTop * SkyTop - SkyGround * SkyGround);
        float rho = sqrt(max(r * r - SkyGround * SkyGround, 0.0));
        float disc = r * r * (mu * mu - 1.0) + SkyTop * SkyTop;
        float d = max(-r * mu + sqrt(max(disc, 0.0)), 0.0);
        float dMin = SkyTop - r, dMax = rho + H;
        float2 uv = float2((d - dMin) / max(dMax - dMin, 1e-3), rho / H);
        return SAMPLE_TEXTURE2D_X_LOD(_NFX_SkyTransmittance, s_linear_clamp_sampler, uv, 0).rgb;
    }

    float3 NativeSky(float3 dir, float2 screenUv)
    {
        float r = SkyGround + max(_NFX_SkyParams.z, 1.0);
        float cosBeta = sqrt(max(r * r - SkyGround * SkyGround, 0.0)) / r;
        float beta = acos(cosBeta);
        float zenithHorizon = PI - beta;
        float viewZenith = acos(clamp(dir.y, -1.0, 1.0));
        bool aboveHorizon = dir.y >= -cosBeta;
        float2 uv;
        if (aboveHorizon)
            uv.y = (1.0 - sqrt(1.0 - saturate(viewZenith / zenithHorizon))) * 0.5;
        else
            uv.y = sqrt(saturate((viewZenith - zenithHorizon) / beta)) * 0.5 + 0.5;
        float3 light = normalize(_NFX_SkyLightDir.xyz);
        float2 dh = dir.xz, lh = light.xz;
        float azimuthCos = dot(dh, dh) > 1e-8 && dot(lh, lh) > 1e-8 ? dot(normalize(dh), normalize(lh)) : 1.0;
        uv.x = sqrt(saturate(-azimuthCos * 0.5 + 0.5));
        float3 sky = SAMPLE_TEXTURE2D_X_LOD(_NFX_SkyView, s_linear_clamp_sampler, uv, 0).rgb;
        float4 clouds = _NFX_SkyParams.x > 1.5 ? SAMPLE_TEXTURE2D_X_LOD(_NFX_CloudTex, s_linear_clamp_sampler, screenUv, 0) : float4(0.0, 0.0, 0.0, 1.0);
        float3 air = aboveHorizon ? SkyTransmittanceAlong(r, dir.y) : 0.0;

        // the disc: its illuminance over its solid angle, darker towards the limb (the sun; the moon a flat grey)
        float radius = max(_NFX_SkyParams.w, 0.0047);
        float cosDisc = dot(dir, light);
        float cosR = cos(radius);
        if (cosDisc > cosR && aboveHorizon)
        {
            float edge = saturate((1.0 - cosDisc) / (1.0 - cosR));
            float limb = _NFX_SkyLightDir.w > 0.5 ? 1.0 : 1.0 - 0.6 * (1.0 - sqrt(1.0 - edge));
            sky += air * _NFX_SkyParams.y / (PI * radius * radius) * limb;
        }
        // the stars (nits), dimmed by the air in front of them
        if (_NFX_StarParams.x > 0.0 && aboveHorizon)
        {
            float3 sd = float3(dot(_NFX_StarRowX.xyz, dir), dot(_NFX_StarRowY.xyz, dir), dot(_NFX_StarRowZ.xyz, dir));
            sky += SAMPLE_TEXTURECUBE_LOD(_NFX_Stars, sampler_NFX_Stars, sd, 0).rgb * air * _NFX_StarParams.x;
        }
        sky = sky * clouds.a + clouds.rgb;
        return min(sky * GetCurrentExposureMultiplier(), 60000.0);
    }

    float4 Composite(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float2 uv = (pixel + 0.5) * _ScreenSize.zw;
        float3 color = LOAD_TEXTURE2D_X(_NFX_ColorTex, pixel).rgb;
        float depth = LoadCameraDepth(pixel);
        bool sky = depth == UNITY_RAW_FAR_CLIP_VALUE;
        float3 rel = ComputeWorldSpacePosition(uv, sky ? UNITY_NEAR_CLIP_VALUE : depth, UNITY_MATRIX_I_VP);
        float3 dir = normalize(rel);
        if (sky && _NFX_SkyParams.x > 0.5)
            color = NativeSky(dir, uv);
        if (_NFX_FogVolumeParams.x > 0.5)
        {
            // over the sky only the mist near the camera (the grid, 3 km): the engine's sky already has the air's
            // haze in it, and 20 km more of it hid the distant clouds (only those overhead were left: "small clouds")
            // and the stars
            float distance = sky ? NFX_FOG_FAR : length(rel);
            float viewDepth = distance * dot(dir, -UNITY_MATRIX_V[2].xyz);
            color = NFX_ApplyFog(color, sky ? NFX_FogAt(uv, viewDepth) : NFX_Fog(uv, viewDepth, distance, dir));
        }
        return float4(color, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Pass
        {
            Name "Composite"
            ZWrite Off ZTest Always Blend Off Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }
    }
    Fallback Off
}

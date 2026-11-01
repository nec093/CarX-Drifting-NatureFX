// NFXNative sky: the radiance of the whole sky in any direction, clouds included, shared by SkyAmbient.hlsl (the
// ambient probe) and SkyCube.hlsl (HDRP's sky). The sky view table is in Probe and the sky data in Screen;
// AmbientParams and ReflParams as Clouds.hlsl (the clouds' cover and motion).
#include "NFXCommon.hlsli"
#include "Sky.hlsli"
#include "CloudsCommon.hlsli"

static const uint AmbientW = 32, AmbientH = 16;

float3 SkyViewRadiance(float3 dir, float r)
{
    float cosBeta = sqrt(max(r * r - GroundRadius * GroundRadius, 0.0)) / r;
    float beta = acos(cosBeta);
    float zenithHorizon = PI - beta;
    float viewZenith = acos(clamp(dir.y, -1.0, 1.0));
    float2 uv;
    if (dir.y >= -cosBeta)
        uv.y = (1.0 - sqrt(1.0 - saturate(viewZenith / zenithHorizon))) * 0.5;
    else
        uv.y = sqrt(saturate((viewZenith - zenithHorizon) / beta)) * 0.5 + 0.5;
    float2 dh = dir.xz, sh = SunDir.xz;
    float azimuthCos = dot(dh, dh) > 1e-8 && dot(sh, sh) > 1e-8 ? dot(normalize(dh), normalize(sh)) : 1.0;
    uv.x = sqrt(saturate(-azimuthCos * 0.5 + 0.5));
    float4 t;
    NFX_LUT_SAMPLE(Probe, SkyViewW, SkyViewH, uv, t);
    return t.rgb;
}

// the sky's radiance in a direction: the sky view table, the clouds in front (a short march, simple light),
// below the horizon the ground lit by the sun and the sky
float3 SkyRadiance(float3 dir)
{
    float r = GroundRadius + max(SkyParams.w, 1.0);
    float3 sunAbove = SkySun();
    float3 sunGround = sunAbove * SkyTransmittance(r, SunDir.y);
    float3 L;
    if (dir.y > 0.0)
    {
        L = SkyViewRadiance(dir, r);
        if ((AmbientParams.x + AmbientParams.y + AmbientParams.z) > 0.0)
        {
            // through the cloud layer: what of the sky gets through, and the light the clouds send down (lit by the
            // sun from above, so their undersides are greyer than their tops)
            float up = max(dir.y, 0.08);
            float t0 = CloudBottom / up, t1 = min(CloudTop / up, t0 + 30000.0);
            const int steps = 16;
            float dt = (t1 - t0) / steps;
            float T = 1.0;
            float3 cloudLight = 0.0;
            float3 ambient = SkyViewRadiance(normalize(float3(dir.x, 0.3, dir.z)), r);
            for (int i = 0; i < steps && T > 0.01; ++i)
            {
                float t = t0 + (i + 0.5) * dt;
                float h = t * up;
                float density = CloudDensity(Camera.xz + dir.xz * t, h, true);
                if (density <= 0.0)
                    continue;
                float sigmaT = density * CloudExtinction;
                float hf = saturate((h - CloudBottom) / (CloudTop - CloudBottom));
                float3 S = CloudAlbedo * sigmaT * (sunGround * (0.02 + 0.06 * hf) * max(SunDir.y, 0.0) + ambient * lerp(0.3, 0.8, hf));
                float sampleT = exp(-sigmaT * dt);
                cloudLight += T * (S - S * sampleT) / sigmaT;
                T *= sampleT;
            }
            L = L * T + cloudLight;
        }
    }
    else
    {
        // the ground below the horizon: grey-green, lit by the sun and half the sky
        float3 skyDown = SkyViewRadiance(float3(dir.x, 0.5, dir.z), r);
        L = 0.15 * (sunGround * max(SunDir.y, 0.0) + PI * skyDown * 0.5) / PI;
    }
    return L;
}

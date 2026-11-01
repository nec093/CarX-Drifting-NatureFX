// NFXNative fog (phase 2): the air between the camera and each surface (or out to the haze's reach over the sky),
// at the light shafts' resolution, in place of HDRP's fog and the shafts. Surface: float4 per pixel (view
// direction, distance in m; 0 nothing). Output: float4 per pixel (the light the air scatters towards the camera,
// nits, and the transmittance). The density falls off exponentially above a base height (MapFog: the map's fog,
// the distant haze or the track's own fog, thickened by the weather). Light: the sun (its illuminance at the
// ground, SunColor), with traced shadows every fourth step (light shafts through trees and gaps), and the sky's
// light from the probe (the engine's own sky, clouds included). AmbientParams: x extinction at the base (1/m),
// y base height (world y), z scale height (m), w anisotropy. Compiled into Fog.h.
#include "NFXCommon.hlsli"

float FogSigma(float y)
{
    return AmbientParams.x * exp(-max(y - AmbientParams.y, 0.0) / max(AmbientParams.z, 1.0));
}

float FogPhase(float c, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
}

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 ray = asfloat(Surface.Load4(index * 16));
    float4 result = float4(0.0, 0.0, 0.0, 1.0);
    if (AmbientParams.x > 0.0 && ray.w > 0.0)
    {
        float3 dir = normalize(ray.xyz);
        const int steps = 16;
        float dt = ray.w / steps;
        float jitter = IGN(float2(id.xy) + 5.588238 * (FrameIndex % 64u));
        // mostly forward (the glow around the sun and lamps in mist), a little back
        float c = dot(dir, SunDir);
        float phase = lerp(FogPhase(c, AmbientParams.w), FogPhase(c, -0.2), 0.2);
        // the sky's light from every side (isotropic share): up and down averaged
        float3 sky = 0.5 * (SampleSH9(float3(0.0, 1.0, 0.0)) + SampleSH9(float3(0.0, -1.0, 0.0)));
        float3 L = 0.0;
        float T = 1.0;
        float visible = 1.0;
        for (int i = 0; i < steps && T > 0.002; ++i)
        {
            float t = (i + jitter) * dt;
            float3 p = Camera + dir * t;
            float sigma = FogSigma(p.y);
            if ((i & 3) == 0)
            {
                visible = 0.0;
                if (SunUp != 0)
                {
                    float3 toSun = SampleSunCone(SunDir, SunRadius, Hash22(id.xy, FrameIndex, 20 + i));
                    visible = Blocked(p, toSun, 2000.0, t > CoarseDistance) ? 0.0 : 1.0;
                }
            }
            float3 S = sigma * (SunColor * phase * visible + sky);
            float sampleT = exp(-sigma * dt);
            L += T * (sigma > 1e-9 ? S * (1.0 - sampleT) / sigma : S * dt);
            T *= sampleT;
        }
        result = float4(min(L, 1e7), T);
    }
    Output.Store4(index * 16, asuint(result));
}

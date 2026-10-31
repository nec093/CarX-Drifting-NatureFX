// NFXNative sky (Sky.hlsli): the sky as seen from the camera, SkyViewW x SkyViewH, into Output (Hillaire 2020, 5.3):
// x the azimuth from the sun (squared for detail around it), y the view zenith, split at the horizon with more rows
// near it. Each texel marches its view ray to the top of the air or the ground, adding the sun light scattered once
// (Rayleigh and Mie phase) and the multiple scattering table's share, both dimmed by the air between. rgb radiance
// for SkySun() (nits), a the luminance transmittance along the ray. Transmittance table in Screen, multiple
// scattering table in Surface. Compiled into SkyView.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= SkyViewW || id.y >= SkyViewH)
        return;
    float2 uv = (id.xy + 0.5) / float2(SkyViewW, SkyViewH);
    float r = GroundRadius + max(SkyParams.w, 1.0);
    // view direction from the texel: the camera's up is +y, the sun in the x-y plane
    float vHorizon = sqrt(max(r * r - GroundRadius * GroundRadius, 0.0));
    float beta = acos(vHorizon / r);
    float zenithHorizon = PI - beta;
    float viewZenithCos;
    if (uv.y < 0.5)
    {
        float c = 1.0 - 2.0 * uv.y;
        c = 1.0 - c * c;
        viewZenithCos = cos(zenithHorizon * c);
    }
    else
    {
        float c = uv.y * 2.0 - 1.0;
        viewZenithCos = cos(zenithHorizon + beta * c * c);
    }
    float azimuthCos = -((uv.x * uv.x) * 2.0 - 1.0);
    float viewSin = sqrt(saturate(1.0 - viewZenithCos * viewZenithCos));
    float3 dir = float3(viewSin * azimuthCos, viewZenithCos, viewSin * sqrt(saturate(1.0 - azimuthCos * azimuthCos)));
    float sunMu = clamp(SunDir.y, -1.0, 1.0);
    float3 sun = float3(sqrt(saturate(1.0 - sunMu * sunMu)), sunMu, 0.0);

    bool ground = HitsGround(r, viewZenithCos);
    float rayLength = ground ? DistanceToGround(r, viewZenithCos) : max(DistanceToSphere(r, viewZenithCos, TopRadius), 0.0);
    float c = dot(dir, sun);
    float phaseR = RayleighPhase(c), phaseM = MiePhase(c);
    const int steps = 32;
    float3 throughput = 1.0, L = 0.0;
    float previous = 0.0;
    for (int i = 0; i < steps; ++i)
    {
        // more samples near the camera, where the air is thick
        float x = (i + 0.3) / steps;
        float t = rayLength * x * x;
        float dt = t - previous;
        previous = t;
        float3 p = float3(0.0, r, 0.0) + dir * t;
        float rp = length(p);
        float3 up = p / rp;
        float sunCos = dot(up, sun);
        Medium m = MediumAt(rp - GroundRadius);
        float3 sampleT = exp(-m.extinction * dt);
        float3 sunT = SkyTransmittance(rp, sunCos);
        float3 S = sunT * (m.rayleigh * phaseR + m.mie * phaseM) + SkyMultiScatter(rp, sunCos) * m.scattering;
        float3 extinction = max(m.extinction, 1e-12);
        L += throughput * (S - S * sampleT) / extinction;
        throughput *= sampleT;
    }
    float3 radiance = min(L * SkySun(), 1e9);
    Output.Store4((id.y * SkyViewW + id.x) * 16, asuint(float4(radiance, dot(throughput, float3(0.2126, 0.7152, 0.0722)))));
}

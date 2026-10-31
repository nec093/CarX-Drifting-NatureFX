// NFXNative sky (Sky.hlsli): the multiple scattering table, MultiScatterSize², into Output (Hillaire 2020, 5.5).
// Each texel is a height and a sun zenith cosine. From there, light scattered once (a unit sun) is gathered from 64
// directions with an isotropic phase (L2), with the share the air sends back each time (fms); every further order
// is that share again, so all orders together are L2 / (1 - fms). The transmittance table is in Screen.
// Compiled into SkyMultiScatter.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= MultiScatterSize || id.y >= MultiScatterSize)
        return;
    float muSun = ((id.x + 0.5) / MultiScatterSize) * 2.0 - 1.0;
    float r = GroundRadius + max((id.y + 0.5) / MultiScatterSize * (TopRadius - GroundRadius), 50.0);
    float3 sun = float3(sqrt(saturate(1.0 - muSun * muSun)), muSun, 0.0);
    const float isotropic = 1.0 / (4.0 * PI);
    const int directions = 64, steps = 20;
    float3 L2 = 0.0, fms = 0.0;
    for (int k = 0; k < directions; ++k)
    {
        // spherical Fibonacci: even directions over the sphere
        float z = 1.0 - (2.0 * k + 1.0) / directions;
        float phi = k * 2.39996323;
        float s = sqrt(saturate(1.0 - z * z));
        float3 dir = float3(s * cos(phi), z, s * sin(phi));
        float mu = dir.y;
        bool ground = HitsGround(r, mu);
        float rayLength = ground ? DistanceToGround(r, mu) : max(DistanceToSphere(r, mu, TopRadius), 0.0);
        float dt = rayLength / steps;
        float3 throughput = 1.0, L = 0.0, f = 0.0;
        for (int i = 0; i < steps; ++i)
        {
            float t = (i + 0.5) * dt;
            float3 p = float3(0.0, r, 0.0) + dir * t;
            float rp = length(p);
            float3 up = p / rp;
            Medium m = MediumAt(rp - GroundRadius);
            float3 sampleT = exp(-m.extinction * dt);
            float3 sunT = SkyTransmittance(rp, dot(up, sun));
            float3 S = m.scattering * isotropic * sunT;
            float3 extinction = max(m.extinction, 1e-12);
            L += throughput * (S - S * sampleT) / extinction;
            f += throughput * (m.scattering - m.scattering * sampleT) / extinction;
            throughput *= sampleT;
        }
        if (ground)
        {
            // the ground lit by the sun reflects some back (Lambertian)
            float3 p = float3(0.0, r, 0.0) + dir * rayLength;
            float3 up = normalize(p);
            L += throughput * SkyTransmittance(GroundRadius, dot(up, sun)) * saturate(dot(up, sun)) * GroundAlbedo / PI;
        }
        L2 += L / directions;
        fms += f / directions;
    }
    float3 psi = L2 / max(1.0 - fms, 1e-3);
    Output.Store4((id.y * MultiScatterSize + id.x) * 16, asuint(float4(psi, 1.0)));
}

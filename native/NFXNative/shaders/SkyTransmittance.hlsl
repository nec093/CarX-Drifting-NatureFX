// NFXNative sky (Sky.hlsli): the transmittance table, TransmittanceW x TransmittanceH, into Output. Each texel is a
// height and a zenith cosine; the optical depth to the top of the air (the ground does not count: SkyTransmittance
// returns 0 for rays into it). Compiled into SkyTransmittance.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= TransmittanceW || id.y >= TransmittanceH)
        return;
    float r, mu;
    TransmittanceFromUv((id.xy + 0.5) / float2(TransmittanceW, TransmittanceH), r, mu);
    float rayLength = max(DistanceToSphere(r, mu, TopRadius), 0.0);
    const int steps = 40;
    float dt = rayLength / steps;
    float3 depth = 0.0;
    for (int i = 0; i < steps; ++i)
    {
        float t = (i + 0.5) * dt;
        float h = sqrt(r * r + t * t + 2.0 * r * mu * t) - GroundRadius;
        depth += MediumAt(h).extinction * dt;
    }
    Output.Store4((id.y * TransmittanceW + id.x) * 16, asuint(float4(exp(-depth), 1.0)));
}

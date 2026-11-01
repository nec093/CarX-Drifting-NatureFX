// NFXNative sky: the light of the whole sky, clouds included, for the ambient light (phase 1c): 512 directions
// (32 x 16, equal area) of SkyRadiance (SkyRadiance.hlsli). Output: float4 per direction (radiance, nits). Unity
// projects them onto spherical harmonics (NFX_NativeBridge SkyAmbientSH). Compiled into SkyAmbient.h.
#include "SkyRadiance.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= AmbientW || id.y >= AmbientH)
        return;
    float phi = (id.x + 0.5) / AmbientW * 2.0 * PI;
    float cosTheta = 1.0 - 2.0 * (id.y + 0.5) / AmbientH;
    float sinTheta = sqrt(saturate(1.0 - cosTheta * cosTheta));
    float3 L = SkyRadiance(float3(sinTheta * cos(phi), cosTheta, sinTheta * sin(phi)));
    Output.Store4((id.y * AmbientW + id.x) * 16, asuint(float4(L, 1.0)));
}

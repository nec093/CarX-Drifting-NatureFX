// NFXNative clouds: their shadows, as a map seen from above (CloudShadowSize², CloudShadowArea metres around the
// camera, snapped to its texels). Each texel is a point on the ground (world y 0); a ray from there towards the sun
// through the cloud layer gives the share of sun light that gets through. The composite (NFX_RTShadow) moves each
// surface point along the sun to y 0 and multiplies its sun visibility by the map there. Screen: the sky data
// (noise, cloud type map). Output: float4 per texel (x transmittance). AmbientParams, ReflParams as Clouds.hlsl.
// Compiled into CloudShadow.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"
#include "CloudsCommon.hlsli"

static const uint CloudShadowSize = 512;
static const float CloudShadowArea = 16000.0; // NativeEngine.CloudShadowArea

NFX_ENTRY(id)
{
    if (id.x >= CloudShadowSize || id.y >= CloudShadowSize)
        return;
    float texel = CloudShadowArea / CloudShadowSize;
    float2 center = floor(Camera.xz / texel) * texel;
    float2 xz = center + ((id.xy + 0.5) / CloudShadowSize - 0.5) * CloudShadowArea;
    float3 sun = SunDir;
    float T = 1.0;
    if (sun.y > 0.01 && (AmbientParams.x + AmbientParams.y + AmbientParams.z) > 0.0)
    {
        float up = max(sun.y, 0.05);
        float t0 = CloudBottom / up, t1 = min(CloudTop / up, t0 + 20000.0);
        const int steps = 24;
        float dt = (t1 - t0) / steps;
        float od = 0.0;
        for (int i = 0; i < steps; ++i)
        {
            float t = t0 + (i + 0.5) * dt;
            od += CloudDensity(xz + sun.xz * t, t * up, true) * CloudExtinction * dt;
        }
        // light scattered forward through a cloud still arrives: a shadowed ground is never pitch black under it
        T = max(exp(-od), exp(-od * 0.25) * 0.25);
    }
    Output.Store4((id.y * CloudShadowSize + id.x) * 16, asuint(float4(T, 0.0, 0.0, 1.0)));
}

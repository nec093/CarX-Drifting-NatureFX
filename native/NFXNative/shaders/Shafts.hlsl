// NFXNative light shafts, Unity (NFX_NativeBridge
// PrepareShafts) picks one random point along each quarter resolution view ray and works out how much sun light
// the air there would scatter towards the camera; here a ray towards the sun says whether that point is lit.
// Surface: 2 float4 per pixel (sample point xyz, scattering weight (pre-exposed, 0: nothing); packed full
// resolution pixel, 1 = coarse). Output: float4 per pixel (scattered light rgb). Compiled into Shafts.h.
#include "NFXCommon.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Surface.Load4(index * 32));
    float4 b = asfloat(Surface.Load4(index * 32 + 16));
    float3 inscatter = 0;
    if (a.w > 0.0 && SunUp != 0)
    {
        float3 dir = SampleSunCone(SunDir, SunRadius, Hash22(UnpackPixel(b.x), FrameIndex, 11));
        if (!Blocked(a.xyz, dir, 2000.0, b.y > 0.5))
            inscatter = SunColor * a.w;
    }
    Output.Store4(index * 16, asuint(float4(inscatter, 1.0)));
}

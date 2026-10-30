// NFXNative: REBLUR_SPECULAR's denoised reflection back into the trace's result buffer, as UnpackReflection reads it:
// the radiance times the pixel's Fresnel again (taken off by NrdSpecPack.hlsl), the Fresnel luminance and the
// distance left as the trace wrote them. Pixels not traced keep what the trace wrote.
// Root: t0 a table (OUT_SPEC_RADIANCE_HITDIST), t1 surfaces (3 float4 per pixel), u0 result (2 float4 per pixel),
// b0 { width, height }. Compiled into NrdSpecUnpack.h.
#include "NRD.hlsli"

Texture2D<float4> Signal : register(t0);
ByteAddressBuffer TraceSurface : register(t1);
RWByteAddressBuffer TraceResult : register(u0);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(TraceSurface.Load4(index * 48));
    if (a.w < 0.0)
        return;
    float3 fresnel = asfloat(TraceSurface.Load3(index * 48 + 32));
    float3 radiance = max(REBLUR_BackEnd_UnpackRadianceAndNormHitDist(Signal[id.xy]).rgb, 0.0);
    float4 result = asfloat(TraceResult.Load4(index * 32));
    TraceResult.Store4(index * 32, asuint(float4(radiance * max(fresnel, 1e-3), result.a)));
}

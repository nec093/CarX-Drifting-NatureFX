// NFXNative: REBLUR's denoised ambient light or GI back into the trace's result buffer, as Unpack4 and the shadow
// pass read it (GI: light bounced in rgb, sky seen a; ambient light: rgb, 1; lamp shadows: blocked less added, 1).
// Pixels without a valid surface keep what the trace wrote.
// Root: t0..t1 a table (OUT_DIFF_RADIANCE_HITDIST of the first and the second REBLUR_DIFFUSE), t2 surfaces (3 float4
// per pixel), u0 result (float4 per pixel), b0 { width, height, mode }. Compiled into NrdReblurUnpack.h.
#include "NRD.hlsli"

Texture2D<float4> Signal0 : register(t0);
Texture2D<float4> Signal1 : register(t1);
ByteAddressBuffer TraceSurface : register(t2);
RWByteAddressBuffer TraceResult : register(u0);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    uint Mode;
    uint Pad;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    if (asfloat(TraceSurface.Load(index * 48 + 12)) < 0.5)
        return;
    float3 light = max(REBLUR_BackEnd_UnpackRadianceAndNormHitDist(Signal0[id.xy]).rgb, 0.0);
    float3 second = max(REBLUR_BackEnd_UnpackRadianceAndNormHitDist(Signal1[id.xy]).rgb, 0.0);
    if (Mode == 2u)
        light -= second;
    float sky = Mode == 1u ? saturate(second.r) : 1.0;
    TraceResult.Store4(index * 16, asuint(float4(light, sky)));
}

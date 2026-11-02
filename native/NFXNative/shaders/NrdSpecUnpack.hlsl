// NFXNative: REBLUR_SPECULAR's denoised reflection back into the trace's result buffer, as UnpackReflection reads it:
// the radiance times the pixel's Fresnel again (taken off by NrdSpecPack.hlsl), the Fresnel luminance and the
// distance left as the trace wrote them. Pixels not traced keep what the trace wrote; mirrors (roughness below 0.05)
// keep the trace's own result.
// Root: t0 a table (OUT_SPEC_RADIANCE_HITDIST), t1 surfaces (2 uint4 per pixel, NfxReflSurface), u0 result (a uint4
// per pixel, NfxPackReflResult: only its first 8 bytes, the colour, are written back),
// b0 { width, height }. Compiled into NrdSpecUnpack.h.
#include "NRD.hlsli"
#include "NfxPack.hlsli"

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
    NfxReflSurface surface = NfxUnpackReflSurface(TraceSurface.Load4(index * 32), TraceSurface.Load4(index * 32 + 16));
    if (surface.alpha < 0.0)
        return;
    float3 fresnel = surface.fresnel;
    float3 radiance = max(REBLUR_BackEnd_UnpackRadianceAndNormHitDist(Signal[id.xy]).rgb, 0.0);
    float4 result = NfxUnpackReflResult(TraceResult.Load4(index * 16));
    // a mirror (car paint's varnish, a mirror, still water): one ray is all it takes and REBLUR's blur only took the
    // sharpness off the paint's reflection; the trace's own result stays there, the denoised one comes in by 0.1
    float roughness = sqrt(max(surface.alpha, 0.0));
    float denoised = saturate((roughness - 0.05) / 0.05);
    float3 mixed = lerp(result.rgb, radiance * max(fresnel, 0.02), denoised);
    uint4 packed = NfxPackReflResult(float4(mixed, result.a), 0.0);
    TraceResult.Store2(index * 16, packed.xy);
}

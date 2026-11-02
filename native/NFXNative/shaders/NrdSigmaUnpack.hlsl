// NFXNative: SIGMA_SHADOW_TRANSLUCENCY's denoised sun shadow (OUT_SHADOW_TRANSLUCENCY: x unoccluded, yzw what gets
// through glass) back into the sun trace's result buffer, as the shadow pass reads it (visibility: 1 unoccluded, the
// glass's share where only glass is in the way; blocker distance 0: the pass's own penumbra blur is skipped). Pixels
// facing away from the sun stay lit there, as without SIGMA (HDRP gave them no sun to take away).
// Root: t0 the shadow (a table), t1 sun surfaces (a uint4 per pixel: w 0xFFFFFFFF not traced), u0 sun result (2 halves per
// pixel),
// b0 { width, height }. Compiled into NrdSigmaUnpack.h.
#include "NRD.hlsli"
#include "NfxPack.hlsli"

Texture2D<float4> Shadow : register(t0);
ByteAddressBuffer SunSurface : register(t1);
RWByteAddressBuffer SunResult : register(u0);

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
    float traced = SunSurface.Load(index * 16 + 12) != 0xFFFFFFFFu ? 1.0 : 0.0;
    float4 s = SIGMA_BackEnd_UnpackShadow(Shadow[id.xy]);
    float through = dot(saturate(s.yzw), 1.0 / 3.0);
    float visibility = traced < 0.5 ? 1.0 : saturate(lerp(through, 1.0, saturate(s.x)));
    SunResult.Store(index * 4, NfxPackHalf2(float2(visibility, 0.0)));
}

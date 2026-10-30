// NFXNative: SIGMA's inputs from one sun trace (Nrd.inl), per trace pixel. Reads the guides Unity prepared
// (NFX_NativeBridge PrepareNrdGuide: world normal and linear roughness; view depth, motion previous - current in uv),
// the sun trace's surfaces (origin w: traced, i.e. facing the sun) and its result (visibility, blocker distance), and
// writes NRD's input textures. A lit pixel has no occluder (NRD_FP16_MAX); one facing away from the sun is marked
// back facing (0); a shadowed one gets the penumbra its blocker distance makes with the sun's disc. Glass lets the sun
// through: SIGMA_SHADOW has no translucency, so a pixel behind glass counts as lit.
// Root: t0 guides (2 float4 per pixel), t1 sun surfaces (2 float4 per pixel), t2 sun result (float2 per pixel),
// u0..u3 a table (normal and roughness, view Z, motion, penumbra), b0 { width, height, tan of the sun's angular radius }.
// Compiled into NrdSigmaPack.h (NRD's Shaders folder on the include path).
#include "NRD.hlsli"

ByteAddressBuffer Guide : register(t0);
ByteAddressBuffer SunSurface : register(t1);
ByteAddressBuffer SunResult : register(t2);
RWTexture2D<float4> OutNormalRoughness : register(u0);
RWTexture2D<float> OutViewZ : register(u1);
RWTexture2D<float2> OutMotion : register(u2);
RWTexture2D<float> OutPenumbra : register(u3);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    float TanSunRadius;
    float Pad;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Guide.Load4(index * 32));
    float4 b = asfloat(Guide.Load4(index * 32 + 16));
    float traced = asfloat(SunSurface.Load(index * 32 + 12));
    float2 sun = asfloat(SunResult.Load2(index * 8));
    OutNormalRoughness[id.xy] = NRD_FrontEnd_PackNormalAndRoughness(normalize(a.xyz), saturate(a.w), 0.0);
    OutViewZ[id.xy] = b.x;
    OutMotion[id.xy] = b.yz;
    float occluder = traced < 0.5 ? 0.0 : sun.x > 0.5 ? NRD_FP16_MAX : max(sun.y, 0.01);
    OutPenumbra[id.xy] = occluder <= 0.0 ? 0.0 : SIGMA_FrontEnd_PackPenumbra(occluder, TanSunRadius);
}

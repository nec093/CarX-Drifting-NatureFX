// NFXNative: REBLUR's inputs from one ambient light or GI trace (Nrd.inl), per trace pixel. Reads the guides Unity
// prepared (PrepareNrdGuide at the ambient resolution), the trace's surfaces (origin w: valid; the last float, where
// Ambient.hlsl / GI.hlsl leave the distance their ray went) and its result, and writes NRD's input textures.
// GI (Mode 1): the light bounced in (rgb) goes to the first REBLUR_DIFFUSE, the share of the sky seen (a, as grey) to
// the second. Ambient light (Mode 0): its rgb (relative to HDRP's ambient light) to the first only. Lamp shadows
// (Mode 2, LocalShadows.hlsl's blocked light on the ambient light's surfaces): the blocked light to the first, the
// light added by emissive stand ins (negative) to the second, both without a hit distance (soft, wide). A pixel
// without a valid surface is put out of NRD's range (view Z 1e6).
// Root: t0 guides (2 float4 per pixel), t1 surfaces (3 float4 per pixel), t2 result (float4 per pixel), u0..u4 a table
// (normal and roughness, view Z, motion, radiance and hit distance 0 and 1), b0 { width, height, mode, scale }.
// Scale shrinks light before exposure (GI's bounce, the lamps' lux: tens of thousands in the sun) into what REBLUR's
// half float moments hold (their squares overflowed: infinities spread over the whole image, white).
// Compiled into NrdReblurPack.h (NRD's Shaders folder on the include path).
#include "NRD.hlsli"

ByteAddressBuffer Guide : register(t0);
ByteAddressBuffer TraceSurface : register(t1);
ByteAddressBuffer TraceResult : register(t2);
RWTexture2D<float4> OutNormalRoughness : register(u0);
RWTexture2D<float> OutViewZ : register(u1);
RWTexture2D<float2> OutMotion : register(u2);
RWTexture2D<float4> OutSignal0 : register(u3);
RWTexture2D<float4> OutSignal1 : register(u4);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    uint Mode;
    float Scale;
};

// ReblurHitDistanceParameters' defaults (NRDSettings.h): the settings Nrd.inl sends leave them so
static const float3 HitDistParams = float3(3.0, 0.1, 20.0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Guide.Load4(index * 32));
    float4 b = asfloat(Guide.Load4(index * 32 + 16));
    float valid = asfloat(TraceSurface.Load(index * 48 + 12));
    float hitDist = asfloat(TraceSurface.Load(index * 48 + 44));
    float4 result = asfloat(TraceResult.Load4(index * 16));
    float viewZ = valid > 0.5 ? b.x : 1e6;
    OutNormalRoughness[id.xy] = NRD_FrontEnd_PackNormalAndRoughness(normalize(a.xyz), 1.0, 0.0);
    OutViewZ[id.xy] = viewZ;
    OutMotion[id.xy] = b.yz;
    float norm = Mode == 2u ? 1.0 : REBLUR_FrontEnd_GetNormHitDist(max(hitDist, 0.0), viewZ, HitDistParams, 1.0);
    float3 first = (Mode == 2u ? max(result.rgb, 0.0) : result.rgb) * Scale;
    float3 second = Mode == 1u ? result.aaa : Mode == 2u ? max(-result.rgb, 0.0) * Scale : 0.0;
    OutSignal0[id.xy] = REBLUR_FrontEnd_PackRadianceAndNormHitDist(first, norm, true);
    OutSignal1[id.xy] = REBLUR_FrontEnd_PackRadianceAndNormHitDist(second, norm, true);
}

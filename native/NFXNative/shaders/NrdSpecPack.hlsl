// NFXNative: REBLUR_SPECULAR's inputs from one reflection trace (Nrd.inl), per trace pixel. Reads the guides Unity
// prepared (PrepareNrdGuide: view Z and motion), the trace's surfaces (Reflection.hlsl: origin, GGX alpha (< 0 not
// traced); the normal it mirrored on; Fresnel rgb) and its result (Fresnel * radiance, Fresnel luminance; distance
// to the reflected point, 0 none), and writes NRD's input textures. The normal and roughness are the trace's own
// (a clear coat mirrors on its smooth varnish, not on HDRP's rough paint); the radiance goes in without its Fresnel,
// which NrdSpecUnpack.hlsl puts back. A pixel not traced is put out of NRD's range (view Z 1e6).
// Root: t0 guides (a packed uint4 per pixel, NfxLoadNrdGuide), t1 surfaces (2 uint4 per pixel, NfxReflSurface), t2 result (a uint4 per pixel, NfxPackReflResult), u0..u3 a
// table (normal and roughness, view Z, motion, radiance and hit distance), b0 { width, height }.
// Compiled into NrdSpecPack.h (NRD's Shaders folder on the include path).
#include "NRD.hlsli"
#include "NfxPack.hlsli"

ByteAddressBuffer Guide : register(t0);
ByteAddressBuffer TraceSurface : register(t1);
ByteAddressBuffer TraceResult : register(t2);
RWTexture2D<float4> OutNormalRoughness : register(u0);
RWTexture2D<float> OutViewZ : register(u1);
RWTexture2D<float2> OutMotion : register(u2);
RWTexture2D<float4> OutSignal : register(u3);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
};

// ReblurHitDistanceParameters' defaults (NRDSettings.h): the settings Nrd.inl sends leave them so
static const float3 HitDistParams = float3(3.0, 0.1, 20.0);

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    NfxNrdGuide guide = NfxLoadNrdGuide(Guide, index);
    NfxReflSurface surface = NfxUnpackReflSurface(TraceSurface.Load4(index * 32), TraceSurface.Load4(index * 32 + 16));
    uint4 packed = TraceResult.Load4(index * 16);
    float4 result = NfxUnpackReflResult(packed);
    float dist = asfloat(packed.z);
    bool traced = surface.alpha >= 0.0;
    float roughness = sqrt(max(surface.alpha, 0.0));
    float viewZ = traced ? guide.viewZ : 1e6;
    // (a pixel not traced has no normal: out of NRD's range anyway)
    float3 n = traced ? surface.normal : float3(0.0, 1.0, 0.0);
    OutNormalRoughness[id.xy] = NRD_FrontEnd_PackNormalAndRoughness(n, saturate(roughness), 0.0);
    OutViewZ[id.xy] = viewZ;
    OutMotion[id.xy] = guide.motion;
    // (pre-exposed; a sun glint capped so REBLUR's half float moments do not overflow, the Fresnel floored at a
    // dielectric's so dividing by it cannot blow a dark pixel up)
    float3 radiance = traced ? min(result.rgb / max(surface.fresnel, 0.02), 64.0) : 0.0;
    float norm = dist > 0.0 ? REBLUR_FrontEnd_GetNormHitDist(dist, viewZ, HitDistParams, roughness) : 1.0;
    OutSignal[id.xy] = REBLUR_FrontEnd_PackRadianceAndNormHitDist(radiance, norm, true);
}

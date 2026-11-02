// NFXNative: the packing of the trace buffers, shared by the trace shaders
// (NFXCommon.hlsli includes it) and the NRD pack / unpack shaders. NFX_NativeBridge.compute has the same formulas
// (keep them in step): the layouts are an interface between the bridge, the engine's shaders and the C# strides.
#ifndef NFX_PACK_HLSLI
#define NFX_PACK_HLSLI

// Packing of the trace buffers, the same formulas as NFX_NativeBridge.compute:
// a unit vector as an octahedron (HDRP's PackNormalOctQuadEncode) in two unorm16, rounded (about 3e-5 rad off)
uint NfxPackOct16(float3 n)
{
    n *= rcp(max(dot(abs(n), 1.0), 1e-6));
    float t = saturate(-n.z);
    float2 f = n.xy + select(n.xy >= 0.0, t, -t);
    uint2 q = (uint2)round(saturate(f * 0.5 + 0.5) * 65535.0);
    return q.x | (q.y << 16);
}

float3 NfxUnpackOct16(uint p)
{
    float2 f = float2(p & 0xFFFFu, p >> 16) / 65535.0 * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-n.z, 0.0);
    n.xy += select(n.xy >= 0.0, -t, t);
    return normalize(n);
}

uint NfxPackHalf2(float2 v)
{
    return f32tof16(v.x) | (f32tof16(v.y) << 16);
}

float2 NfxUnpackHalf2(uint p)
{
    return float2(f16tof32(p), f16tof32(p >> 16));
}

// four values 0..1 in 8 bit each, rounded (truncating moved 0.5 to 0.498: the clear coat flag of the surface trace)
uint NfxPackUnorm4x8(float4 v)
{
    uint4 q = (uint4)round(saturate(v) * 255.0);
    return q.x | (q.y << 8) | (q.z << 16) | (q.w << 24);
}

float4 NfxUnpackUnorm4x8(uint p)
{
    return float4(p & 0xFFu, (p >> 8) & 0xFFu, (p >> 16) & 0xFFu, p >> 24) / 255.0;
}

// NRD's guides (the bridge's PrepareNrdGuide), one uint4 per trace pixel
struct NfxNrdGuide
{
    float3 normal;
    float viewZ;
    float2 motion; // previous - current, uv
    float roughness;
};

NfxNrdGuide NfxLoadNrdGuide(ByteAddressBuffer buffer, uint index)
{
    uint4 g = buffer.Load4(index * 16);
    NfxNrdGuide r;
    r.normal = NfxUnpackOct16(g.x);
    r.viewZ = asfloat(g.y);
    r.motion = NfxUnpackHalf2(g.z);
    r.roughness = g.w / 65535.0;
    return r;
}

// The reflection trace's surface (the bridge's PrepareReflection), 2 uint4 per trace pixel:
//   a: origin xyz (absolute, float), the packed full resolution pixel
//   b: normal (NfxPackOct16), Fresnel r g (halves), Fresnel b and the GGX alpha (halves; < 0: nothing to trace),
//      the exposure multiplier (float: a day's 2.5e-5 is below a half's normal range)
struct NfxReflSurface
{
    float3 origin;
    uint pixel;
    float3 normal;
    float3 fresnel;
    float alpha;
    float exposure;
};

NfxReflSurface NfxUnpackReflSurface(uint4 a, uint4 b)
{
    NfxReflSurface r;
    r.origin = asfloat(a.xyz);
    r.pixel = a.w;
    r.normal = NfxUnpackOct16(b.x);
    float2 rg = NfxUnpackHalf2(b.y);
    float2 bAlpha = NfxUnpackHalf2(b.z);
    r.fresnel = float3(rg, bAlpha.x);
    r.alpha = bAlpha.y;
    r.exposure = asfloat(b.w);
    return r;
}

// The reflection trace's result (Reflection.hlsl), a uint4 per trace pixel: Fresnel * radiance r g, b and the
// Fresnel's luminance (halves: the trace texture is RGBA16F anyway), the distance to the reflected point (float: it
// goes to an R32 texture; 0 nothing reflected), 0
uint4 NfxPackReflResult(float4 result, float dist)
{
    float4 r = min(result, 60000.0);
    return uint4(NfxPackHalf2(r.rg), NfxPackHalf2(r.ba), asuint(dist), 0u);
}

float4 NfxUnpackReflResult(uint4 p)
{
    return float4(NfxUnpackHalf2(p.x), NfxUnpackHalf2(p.y));
}

// The ambient light's surfaces (the bridge's PrepareAmbient; Ambient, GI, LocalShadows, LampMask), 2 uint4 per pixel:
//   a: origin xyz (absolute, float), the packed full resolution pixel (0xFFFFFFFF: no valid surface)
//   b: shading normal and geometric normal (NfxPackOct16), view depth (float), the distance the ambient / GI ray went
//      (float, written by Ambient.hlsl / GI.hlsl at byte 28 for NRD's REBLUR: NfxAmbientTravelledOffset)
static const uint NfxAmbientStride = 32;
static const uint NfxAmbientTravelledOffset = 28;

struct NfxAmbientSurface
{
    bool valid;
    float3 origin;
    uint pixel;
    float3 normal;
    float3 geomNormal;
    float viewDepth;
};

NfxAmbientSurface NfxUnpackAmbientSurface(uint4 a, uint4 b)
{
    NfxAmbientSurface r;
    r.valid = a.w != 0xFFFFFFFFu;
    r.origin = asfloat(a.xyz);
    r.pixel = a.w;
    r.normal = NfxUnpackOct16(b.x);
    r.geomNormal = NfxUnpackOct16(b.y);
    r.viewDepth = asfloat(b.z);
    return r;
}

#endif

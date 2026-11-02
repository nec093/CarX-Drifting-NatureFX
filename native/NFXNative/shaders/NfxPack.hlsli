// NFXNative: the packing of the trace buffers (docs/plan-review-followups.md 7), shared by the trace shaders
// (NFXCommon.hlsli includes it) and the NRD pack / unpack shaders. NFX_NativeBridge.compute has the same formulas
// (keep them in step): the layouts are an interface between the bridge, the engine's shaders and the C# strides.
#ifndef NFX_PACK_HLSLI
#define NFX_PACK_HLSLI

// Packing of the trace buffers (docs/plan-review-followups.md 7), the same formulas as NFX_NativeBridge.compute:
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

#endif

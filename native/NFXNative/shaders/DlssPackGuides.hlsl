// NFXNative: DLSS Ray Reconstruction's guides (Dlss.inl) from the buffers Unity packed (NFX_NativeBridge PrepareDlss):
// the guide buffer, a uint4 per pixel (diffuse albedo rgb and specular albedo rgb as 6 halves, the world normal
// octahedron encoded as 2 halves), and the colour buffer, whose alpha half holds the perceptual roughness.
// Root: t0 the guide buffer, t1 the colour buffer, (t2 unused), u0..u2 a table (diffuse albedo, specular albedo,
// normal with the roughness in w), b0 { width, height }. Compiled into DlssPackGuides.h.
ByteAddressBuffer Guides : register(t0);
ByteAddressBuffer Packed : register(t1);
RWTexture2D<float4> OutDiffuse : register(u0);
RWTexture2D<float4> OutSpecular : register(u1);
RWTexture2D<float4> OutNormals : register(u2);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
};

// HDRP's PackNormalOctQuadEncode undone
float3 UnpackOct(float2 f)
{
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = max(-n.z, 0.0);
    n.xy += select(n.xy >= 0.0, -t, t);
    return normalize(n);
}

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    uint4 g = Guides.Load4(index * 16);
    uint colorBA = Packed.Load(index * 16 + 4);
    float roughness = f16tof32(colorBA >> 16);
    OutDiffuse[id.xy] = float4(f16tof32(g.x), f16tof32(g.x >> 16), f16tof32(g.y), 1.0);
    OutSpecular[id.xy] = float4(f16tof32(g.y >> 16), f16tof32(g.z), f16tof32(g.z >> 16), 1.0);
    OutNormals[id.xy] = float4(UnpackOct(float2(f16tof32(g.w), f16tof32(g.w >> 16))), saturate(roughness));
}

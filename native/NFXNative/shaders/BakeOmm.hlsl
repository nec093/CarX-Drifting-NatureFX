// NFXNative: opacity micromaps (DXR 1.2) from an alpha tested geometry's byte mask (BakeAlpha.hlsl), on the DXR 1.2
// device. Every triangle gets a 4 state micromap at Level: each micro-triangle is fully opaque (no shader runs for
// it), fully transparent (rays pass), or unknown opaque (the any-hit test decides, as before). One thread per
// micro-triangle; it samples the mask at 19 points of its micro-triangle (the corners and the centres of its 16
// sub-triangles) through the triangle's UVs and decides from those.
// Micro-triangles are numbered along the space filling curve of the Vulkan / DXR specs (BarycentricsToIndex, the
// reference code of VK_EXT_opacity_micromap); 2 bits per micro-triangle, micro-triangle 0 in the lowest bits.
// Root: t0 indices (3 x uint per triangle), t1 UVs (float2 per vertex), t2 the byte mask, u0 the micromap data
// (zeroed), b0 the constants below. Compiled into BakeOmm.h.
ByteAddressBuffer Indices : register(t0);
ByteAddressBuffer Uvs : register(t1);
ByteAddressBuffer Mask : register(t2);
RWByteAddressBuffer Output : register(u0);

cbuffer Constants : register(b0)
{
    uint Triangles;
    uint Level;
    uint MaskWidth;
    uint MaskHeight;
    float4 ST;          // texture scale (xy) and offset (zw)
    float Cutoff;
    float AlphaScale;
    uint GroupsX;       // dispatch width in groups (the dispatch is 2D: too many micro-triangles for one row)
    uint Unused;
};

// The reference code of VK_EXT_opacity_micromap: hit barycentrics (u: vertex 1's weight, v: vertex 2's) to the
// micro-triangle's index at a subdivision level
uint BarycentricsToIndex(float u, float v, uint level)
{
    u = saturate(u);
    v = saturate(v);
    float fu = u * (1u << level);
    float fv = v * (1u << level);
    uint iu = (uint)fu;
    uint iv = (uint)fv;
    float uf = fu - float(iu);
    float vf = fv - float(iv);
    if (iu >= (1u << level)) iu = (1u << level) - 1u;
    if (iv >= (1u << level)) iv = (1u << level) - 1u;
    uint iuv = iu + iv;
    if (iuv >= (1u << level))
        iu -= iuv - (1u << level) + 1u;
    uint iw = ~(iu + iv);
    if (uf + vf >= 1.0f && iuv < (1u << level) - 1u)
        --iw;
    uint b0 = ~(iu ^ iw);
    b0 &= ((1u << level) - 1u);
    uint t = (iu ^ iv) & b0;
    uint f = t;
    f ^= f >> 1u;
    f ^= f >> 2u;
    f ^= f >> 4u;
    f ^= f >> 8u;
    uint b1 = ((f ^ iu) & ~b0) | t;
    b0 = (b0 | (b0 << 8u)) & 0x00ff00ffu;
    b0 = (b0 | (b0 << 4u)) & 0x0f0f0f0fu;
    b0 = (b0 | (b0 << 2u)) & 0x33333333u;
    b0 = (b0 | (b0 << 1u)) & 0x55555555u;
    b1 = (b1 | (b1 << 8u)) & 0x00ff00ffu;
    b1 = (b1 | (b1 << 4u)) & 0x0f0f0f0fu;
    b1 = (b1 | (b1 << 2u)) & 0x33333333u;
    b1 = (b1 | (b1 << 1u)) & 0x55555555u;
    return b0 | (b1 << 1u);
}

// As AlphaSolid with NFX_ALPHA_MASK (NFXCommon.hlsli): nearest texel of the byte mask
bool Solid(float2 uv)
{
    uint2 size = uint2(MaskWidth, MaskHeight);
    uint2 texel = min((uint2)(frac(uv * ST.xy + ST.zw) * size), size - 1);
    uint index = texel.y * size.x + texel.x;
    float alpha = ((Mask.Load(index & ~3u) >> ((index & 3u) * 8u)) & 0xFFu) / 255.0;
    return alpha * AlphaScale >= Cutoff;
}

[numthreads(64, 1, 1)]
void main(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint perTriangle = 1u << (2u * Level);
    uint id = (group.y * GroupsX + group.x) * 64u + thread.x;
    uint triIndex = id >> (2u * Level);
    uint local = id & (perTriangle - 1u);
    if (triIndex >= Triangles)
        return;

    // the micro-triangle's place in the barycentric grid: row r (v = r .. r + 1) holds 2 (n - r) - 1 triangles,
    // up and down in turn
    uint n = 1u << Level;
    uint row = 0;
    uint k = local;
    while (row < n && k >= 2u * (n - row) - 1u)
    {
        k -= 2u * (n - row) - 1u;
        ++row;
    }
    bool up = (k & 1u) == 0u;
    float column = (float)(k >> 1);
    float scale = 1.0 / n;
    // corners in (u, v), the barycentric weights of vertex 1 and vertex 2
    float2 c0, c1, c2;
    if (up)
    {
        c0 = float2(column, row) * scale;
        c1 = float2(column + 1.0, row) * scale;
        c2 = float2(column, row + 1.0) * scale;
    }
    else
    {
        c0 = float2(column + 1.0, row) * scale;
        c1 = float2(column, row + 1.0) * scale;
        c2 = float2(column + 1.0, row + 1.0) * scale;
    }
    float2 centre = (c0 + c1 + c2) / 3.0;
    uint index = BarycentricsToIndex(centre.x, centre.y, Level);

    uint3 tri = Indices.Load3(triIndex * 12u);
    float2 uv0 = asfloat(Uvs.Load2(tri.x * 8u));
    float2 uv1 = asfloat(Uvs.Load2(tri.y * 8u));
    float2 uv2 = asfloat(Uvs.Load2(tri.z * 8u));

    // 19 samples: the 3 corners and the centres of the 16 sub-triangles two levels down
    uint solid = 0, total = 0;
    [unroll]
    for (uint s = 0; s < 19u; ++s)
    {
        float2 b;
        if (s < 3u)
        {
            // corners pulled in slightly: exactly on an edge belongs to the neighbour as much as to this one
            b = (s == 0u ? c0 : s == 1u ? c1 : c2) * 0.9 + centre * 0.1;
        }
        else
        {
            uint q = s - 3u;
            uint qr = q < 7u ? 0u : q < 12u ? 1u : q < 15u ? 2u : 3u;
            uint qk = q - (qr == 0u ? 0u : qr == 1u ? 7u : qr == 2u ? 12u : 15u);
            bool qup = (qk & 1u) == 0u;
            float qc = (float)(qk >> 1);
            float2 local2 = (qup ? float2(qc + 1.0 / 3.0, qr + 1.0 / 3.0) : float2(qc + 2.0 / 3.0, qr + 2.0 / 3.0)) * 0.25;
            // map from the unit triangle onto this micro-triangle (an up one; a down one is the mirror)
            b = c0 + (c1 - c0) * local2.x + (c2 - c0) * local2.y;
        }
        float2 uv = uv0 * (1.0 - b.x - b.y) + uv1 * b.x + uv2 * b.y;
        solid += Solid(uv) ? 1u : 0u;
        ++total;
    }
    // 1 opaque, 0 transparent, 3 unknown opaque (the any-hit test decides)
    uint state = solid == total ? 1u : solid == 0u ? 0u : 3u;
    if (state == 0u)
        return;  // zeroed already
    uint bit = (index & 15u) * 2u;
    uint word = triIndex * (perTriangle / 16u) + (index >> 4);
    Output.InterlockedOr(word * 4u, state << bit);
}

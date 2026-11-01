// NFXNative: DLSS's inputs (Dlss.inl) from the buffer Unity packed (NFX_NativeBridge PrepareDlss), a uint4 per pixel:
// x, y the pre-exposed colour as 4 halves, z the motion to the previous frame in pixels as 2 halves, w the device depth.
// Root: t0 the buffer, (t1, t2 unused), u0..u2 a table (colour, motion, depth), b0 { width, height }. Compiled into
// DlssPack.h.
ByteAddressBuffer Packed : register(t0);
RWTexture2D<float4> OutColor : register(u0);
RWTexture2D<float2> OutMotion : register(u1);
RWTexture2D<float> OutDepth : register(u2);

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
    uint4 p = Packed.Load4((id.y * Width + id.x) * 16);
    OutColor[id.xy] = float4(f16tof32(p.x), f16tof32(p.x >> 16), f16tof32(p.y), f16tof32(p.y >> 16));
    OutMotion[id.xy] = float2(f16tof32(p.z), f16tof32(p.z >> 16));
    OutDepth[id.xy] = asfloat(p.w);
}

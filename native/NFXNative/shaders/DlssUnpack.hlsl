// NFXNative: DLAA's output (Dlss.inl) back over the colour in Unity's buffer (the x, y halves of each uint4), which
// NFX_NativeBridge UnpackDlss puts into the camera's colour.
// Root: t0 a table (the output), (t1 unused), u0 the buffer, b0 { width, height }. Compiled into DlssUnpack.h.
Texture2D<float4> Output : register(t0);
RWByteAddressBuffer Packed : register(u0);

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
    float4 c = max(Output[id.xy], 0.0);
    c = min(c, 65000.0);
    uint2 halves = uint2(f32tof16(c.r) | (f32tof16(c.g) << 16), f32tof16(c.b) | (f32tof16(c.a) << 16));
    Packed.Store2((id.y * Width + id.x) * 16, halves);
}

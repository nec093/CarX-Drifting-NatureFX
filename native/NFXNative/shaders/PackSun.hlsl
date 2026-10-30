// NFXNative: the sun trace's hand over to and from the DXR 1.2 device, packed to cut the copying (RtDevice.inl).
// PackIn: Prepare's 2 float4 per pixel (origin xyz, 1 = trace; normal xyz, packed pixel) into 16 bytes: origin xyz
// and the packed pixel, 0xFFFFFFFF where nothing is traced (SunShadow.hlsl with NFX_PACKED_SUN reads that).
// UnpackOut: the trace's half2 per pixel (visibility, blocker distance) back into the float2 Unpack expects.
// Root: t0 source (raw), u0 target (raw), b0 { pixels, unused x 3 }. Compiled into PackSunIn.h / PackSunOut.h.
ByteAddressBuffer Source : register(t0);
RWByteAddressBuffer Target : register(u0);

cbuffer Constants : register(b0)
{
    uint Pixels;
    uint3 Unused;
};

[numthreads(64, 1, 1)]
void PackIn(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Pixels)
        return;
    uint4 a = Source.Load4(id.x * 32);
    uint packedPixel = Source.Load(id.x * 32 + 28);
    Target.Store4(id.x * 16, uint4(a.xyz, asfloat(a.w) > 0.5 ? packedPixel : 0xFFFFFFFFu));
}

[numthreads(64, 1, 1)]
void UnpackOut(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Pixels)
        return;
    uint h = Source.Load(id.x * 4);
    Target.Store2(id.x * 8, asuint(float2(f16tof32(h & 0xFFFFu), f16tof32(h >> 16))));
}

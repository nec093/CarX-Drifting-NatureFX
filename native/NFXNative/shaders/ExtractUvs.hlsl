// NFXNative: copies one vertex attribute out of Unity's vertex buffer into the engine's own buffer, one thread per
// vertex: the first UV set (float2 per vertex) for the alpha test and the hit colour, or the position (float3 per
// vertex) for the hit normal. Compiled into ExtractUvs.h.
RWByteAddressBuffer Output : register(u0);
ByteAddressBuffer Source : register(t1);  // Unity's vertex buffer, bound from a 4 byte aligned address

cbuffer Params : register(b0)
{
    uint Vertices;
    uint Shift;    // bytes from the bound address to the first vertex's attribute
    uint Stride;   // bytes between vertices
    uint Format;   // 1: two 32 bit floats, 2: two 16 bit floats, 3: three 32 bit floats, 4: four 16 bit floats (xyz)
};

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Vertices)
        return;
    uint address = Shift + id.x * Stride;
    if (Format == 3)
    {
        Output.Store3(id.x * 12, Source.Load3(address));
    }
    else if (Format == 4)
    {
        uint2 packed = Source.Load2(address);
        float3 p = float3(f16tof32(packed.x & 0xFFFFu), f16tof32(packed.x >> 16), f16tof32(packed.y & 0xFFFFu));
        Output.Store3(id.x * 12, asuint(p));
    }
    else if (Format == 2)
    {
        uint packed = Source.Load(address);
        Output.Store2(id.x * 8, asuint(float2(f16tof32(packed & 0xFFFFu), f16tof32(packed >> 16))));
    }
    else
    {
        Output.Store2(id.x * 8, Source.Load2(address));
    }
}

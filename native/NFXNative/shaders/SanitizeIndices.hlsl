// NFXNative: copies a mesh's indices out of Unity's index buffer into the engine's own (32 bit), one thread per
// triangle. A triangle with an index past the vertex count becomes degenerate: building a BLAS from such indices
// hung the GPU (device removed) when a buffer held something other than the mesh expected. Compiled into
// SanitizeIndices.h.
RWByteAddressBuffer Output : register(u0);
ByteAddressBuffer Source : register(t1);  // Unity's index buffer, bound from a 4 byte aligned address

cbuffer Params : register(b0)
{
    uint Triangles;
    uint Shift;        // bytes from the bound address to the first index (0 or 2)
    uint Is16;         // 1: 16 bit indices
    uint VertexCount;
};

uint LoadIndex(uint i)
{
    if (Is16 != 0)
    {
        uint address = Shift + i * 2;
        uint word = Source.Load(address & ~3u);
        return (address & 2u) != 0 ? word >> 16 : word & 0xFFFFu;
    }
    return Source.Load(Shift + i * 4);
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Triangles)
        return;
    uint3 t = uint3(LoadIndex(id.x * 3), LoadIndex(id.x * 3 + 1), LoadIndex(id.x * 3 + 2));
    if (any(t >= VertexCount))
        t = 0;
    Output.Store3(id.x * 12, t);
}

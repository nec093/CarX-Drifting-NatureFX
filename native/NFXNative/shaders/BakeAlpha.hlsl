// NFXNative: a material's alpha as a small byte mask for the DXR 1.2 device, which cannot read Unity's textures.
// Samples the colour texture (the engine's descriptor heap on Unity's device) at a coarse mip, as the shadow rays'
// alpha test does, and packs four texels per word into a shared buffer. Root: u0 target (raw), b0 { texture
// descriptor, width (a multiple of 4), height, target byte offset, mip }, a linear wrap sampler. Compiled into
// BakeAlpha.h.
RWByteAddressBuffer Target : register(u0);
SamplerState LinearWrap : register(s0);

cbuffer Constants : register(b0)
{
    uint Descriptor;
    uint Width;
    uint Height;
    uint TargetOffset;
    float Mip;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    uint x = id.x * 4;
    if (x >= Width || id.y >= Height)
        return;
    Texture2D texture = ResourceDescriptorHeap[Descriptor];
    uint packed = 0;
    [unroll]
    for (uint i = 0; i < 4; ++i)
    {
        float2 uv = (float2(x + i, id.y) + 0.5) / float2(Width, Height);
        float a = saturate(texture.SampleLevel(LinearWrap, uv, Mip).a);
        packed |= (uint)round(a * 255.0) << (i * 8);
    }
    Target.Store(TargetOffset + (id.y * Width + x), packed);
}

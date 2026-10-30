// NFXNative: copies whole 32 bit words from one buffer to another with unordered access, so neither changes state
// (Unity's buffers stay as Unity left them). Used to hand data to and from the DXR 1.2 device (shared buffers).
// Root: t0 source (raw), u0 target (raw), b0 { words, source byte offset, target byte offset, unused }.
// Compiled into CopyRaw.h.
ByteAddressBuffer Source : register(t0);
RWByteAddressBuffer Target : register(u0);

cbuffer Constants : register(b0)
{
    uint Words;
    uint SourceOffset;
    uint TargetOffset;
    uint Unused;
};

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    // four words per thread
    uint first = id.x * 4;
    if (first >= Words)
        return;
    if (first + 4 <= Words)
    {
        Target.Store4(TargetOffset + first * 4, Source.Load4(SourceOffset + first * 4));
        return;
    }
    for (uint w = first; w < Words; ++w)
        Target.Store(TargetOffset + w * 4, Source.Load(SourceOffset + w * 4));
}

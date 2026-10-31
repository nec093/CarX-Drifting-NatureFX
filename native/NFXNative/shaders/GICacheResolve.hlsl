// NFXNative radiance cache resolve (GI): after each frame's GI trace, every cell's new samples are averaged into
// what it holds (a running mean over its first frames, then an exponential one that follows changing light), and
// the sums are cleared. Cells unused for a while are freed. One thread per entry: Width x Height = CacheEntries.
// Compiled into GICacheResolve.h.
#include "NFXCommon.hlsli"

static const uint CacheMaxFrames = 45;  // the running mean turns into an exponential one of about this length
                                        // (30 wobbled enough for REBLUR's anti-lag to see changes of light)
static const uint CacheKeepFrames = 600;

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint e = id.y * Width + id.x;
    if (e >= CacheEntries)
        return;
    uint address = e * CacheEntryBytes;
    uint key = Cache.Load(address);
    if (key == 0u)
        return;
    uint count = Cache.Load(address + 4);
    if (count == 0u)
    {
        uint last = Cache.Load(address + 32);
        if (FrameIndex - last > CacheKeepFrames)
        {
            // freed: the whole entry (key, count, sums, resolved, last used, frames). With the resolved colour and
            // frames left, the next cell to take the slot answered lookups with the old owner's light at once
            Cache.Store4(address, uint4(0u, 0u, 0u, 0u));
            Cache.Store4(address + 16, uint4(0u, 0u, 0u, 0u));
            Cache.Store4(address + 32, uint4(0u, 0u, 0u, 0u));
        }
        return;
    }
    uint samples = min(count, CacheMaxSamples);
    float3 mean = (float3)Cache.Load3(address + 8) / (samples * CacheScale());
    float3 resolved = asfloat(Cache.Load3(address + 20));
    uint frames = Cache.Load(address + 36);
    float weight = 1.0 / (float)(min(frames, CacheMaxFrames) + 1u);
    resolved = lerp(resolved, mean, weight);
    if (any(!isfinite(resolved)))
        resolved = 0.0;
    Cache.Store(address + 4, 0u);
    Cache.Store3(address + 8, uint3(0u, 0u, 0u));
    Cache.Store3(address + 20, asuint(resolved));
    Cache.Store(address + 36, frames + 1u);
}

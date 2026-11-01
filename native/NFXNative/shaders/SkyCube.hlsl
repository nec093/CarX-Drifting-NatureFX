// NFXNative sky (roadmap A3): the engine's sky as a cube map for HDRP (its sky, sky reflections, reflection
// probes and ambient probe become the engine's): SkyCubeSize² texels per face, the faces after one another (Unity's
// order +X -X +Y -Y +Z -Z, rows top down), of SkyRadiance (SkyRadiance.hlsli: sky, clouds, ground; no sun or moon
// disc, which HDRP's lights already shine as). Output: float4 per texel. Unity copies it into the cube
// (NFX_NativeBridge SkyCubeUnpack). Compiled into SkyCube.h.
#include "SkyRadiance.hlsli"

static const uint SkyCubeSize = 64;

NFX_ENTRY(id)
{
    if (id.x >= SkyCubeSize || id.y >= SkyCubeSize * 6)
        return;
    uint face = id.y / SkyCubeSize, y = id.y % SkyCubeSize;
    float u = (id.x + 0.5) / SkyCubeSize * 2.0 - 1.0;
    float v = (y + 0.5) / SkyCubeSize * 2.0 - 1.0;
    float3 dir = face == 0 ? float3(1.0, -v, -u) : face == 1 ? float3(-1.0, -v, u) : face == 2 ? float3(u, 1.0, v)
               : face == 3 ? float3(u, -1.0, -v) : face == 4 ? float3(u, -v, 1.0) : float3(-u, -v, -1.0);
    float3 L = SkyRadiance(normalize(dir));
    Output.Store4((id.y * SkyCubeSize + id.x) * 16, asuint(float4(min(L, 60000.0), 1.0)));
}

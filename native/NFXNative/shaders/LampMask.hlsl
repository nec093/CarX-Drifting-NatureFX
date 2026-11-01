// NFXNative lamp shadow masks (roadmap A4), per half resolution pixel: for the four lamps that matter most there, each
// lamp's own visibility, so the composite can shadow each of HDRP's lamps exactly (its diffuse light and its shine:
// NFX_SunExact LampsExact multiplies every lamp by its own mask), instead of taking one blocked share off all of them.
// The four are the lamps HDRP draws (LightColor.w 1) with the most weight on the point (NFXCommon LampWeight, its
// shine included: a far lamp's long reflection on a wet road counts); each gets one ray per frame towards a random
// point of the light's shape (soft shadows), the temporal accumulation is LampsExact's. Lamps after the four are left
// unshadowed (the weakest); lights only NatureFX has stay with LocalShadows.hlsl.
// Surface: as Ambient.hlsl. Output: float4 per pixel, as floats that hold small integers exactly: x lamp 0 + 256 x
// lamp 1, y lamp 2 + 256 x lamp 3 (255: none), z visibility 0 + 256 x visibility 1, w visibility 2 + 256 x
// visibility 3 (0..255). Compiled into LampMask.h.
#include "NFXCommon.hlsli"

static const int Slots = 4;

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Surface.Load4(index * 48));
    float4 b = asfloat(Surface.Load4(index * 48 + 16));
    int lamp[Slots] = { 255, 255, 255, 255 };
    float weight[Slots] = { 0.0, 0.0, 0.0, 0.0 };
    float3 dirTo[Slots] = { (float3)0, (float3)0, (float3)0, (float3)0 };
    float distTo[Slots] = { 0.0, 0.0, 0.0, 0.0 };
    float vis[Slots] = { 1.0, 1.0, 1.0, 1.0 };
    if (a.w > 0.5)
    {
        float3 p = a.xyz;
        float3 n = b.xyz;
        float3 toView = normalize(Camera - p);
        int count = min((int)LightCount.x, NFX_MAX_LOCAL_LIGHTS);
        // the four heaviest, kept in order by insertion
        for (int i = 0; i < count; ++i)
        {
            if (LightColor[i].w < 0.5 || LightColor[i].w > 1.5)
                continue;
            float3 toLight;
            float dist;
            float3 e = LocalLightIlluminance(i, p, toLight, dist);
            float w = Luminance(e) * LampWeight(n, toLight, toView, true);
            if (w <= 1e-5 || w <= weight[Slots - 1])
                continue;
            int at = Slots - 1;
            [unroll]
            for (int s = Slots - 1; s > 0; --s)
            {
                if (w > weight[s - 1])
                {
                    lamp[s] = lamp[s - 1];
                    weight[s] = weight[s - 1];
                    dirTo[s] = dirTo[s - 1];
                    distTo[s] = distTo[s - 1];
                    at = s - 1;
                }
            }
            lamp[at] = i;
            weight[at] = w;
            dirTo[at] = toLight;
            distTo[at] = dist;
        }
        [unroll]
        for (int k = 0; k < Slots; ++k)
        {
            if (lamp[k] == 255)
                continue;
            float2 u = Hash22(id.xy, FrameIndex, 211u + (uint)k * 5u);
            float3 dir = dirTo[k];
            float len = distTo[k];
            float radius = LightSpot[lamp[k]].y;
            if (radius > 0.0)
            {
                float3 tangent = normalize(abs(dir.y) < 0.99 ? cross(dir, float3(0.0, 1.0, 0.0)) : cross(dir, float3(1.0, 0.0, 0.0)));
                float3 bitangent = cross(dir, tangent);
                float r = radius * sqrt(u.x);
                float phi = 2.0 * PI * u.y;
                float3 d = LightPos[lamp[k]].xyz + (tangent * cos(phi) + bitangent * sin(phi)) * r - p;
                len = length(d);
                dir = d / max(len, 1e-4);
            }
            // the last 0.6 m before the light are not tested: car lamps sit inside their housing and glass
            vis[k] = Blocked(p, dir, max(len - 0.6, 0.0), false) ? 0.0 : 1.0;
        }
    }
    float4 o;
    o.x = (float)lamp[0] + 256.0 * (float)lamp[1];
    o.y = (float)lamp[2] + 256.0 * (float)lamp[3];
    o.z = round(vis[0] * 255.0) + 256.0 * round(vis[1] * 255.0);
    o.w = round(vis[2] * 255.0) + 256.0 * round(vis[3] * 255.0);
    Output.Store4(index * 16, asuint(o));
}

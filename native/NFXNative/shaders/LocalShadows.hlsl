// NFXNative local light shadows, per half resolution pixel: how much of the light of the lamps near it is blocked.
// Many lamps can reach a pixel, so not every one gets a ray: two lights are picked per pixel and frame in proportion
// to the light each would put there (resampled importance sampling, as the first stage of ReSTIR), each towards a
// random point of the light's shape (soft shadows), and weighted so the sum over frames is the full blocked light;
// the shadow pass accumulates and blurs it. Lamps HDRP draws come off where blocked; lights HDRP does not have
// (emissive surfaces standing in as lamps, LightColor.w 2) go on where they get through (a negative blocked share).
// Surface: as Ambient.hlsl. Output: float4 per pixel (blocked illuminance rgb, before the surface's diffuse colour
// and the exposure). Compiled into LocalShadows.h.
#include "NFXCommon.hlsli"

static const int Samples = 2;

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Surface.Load4(index * 48));
    float4 b = asfloat(Surface.Load4(index * 48 + 16));
    float3 blocked = 0;
    if (a.w > 0.5)
    {
        int count = min((int)LightCount.x, NFX_MAX_LOCAL_LIGHTS);
        // the candidates' weights: the luminance of the light each puts on the point, unshadowed
        float total = 0.0;
        for (int i = 0; i < count; ++i)
        {
            if (LightColor[i].w < 0.5)
                continue; // HDRP shadows this one
            float3 toLight;
            float dist;
            float3 e = LocalLightIlluminance(i, a.xyz, toLight, dist);
            total += Luminance(e) * saturate(dot(b.xyz, toLight));
        }
        if (total > 1e-4)
        {
            for (int k = 0; k < Samples; ++k)
            {
                // pick a light by its weight (a walk over the running sum)
                float2 u = Hash22(id.xy, FrameIndex, 11u + (uint)k * 2u);
                float target = u.x * total;
                float running = 0.0;
                int chosen = -1;
                float chosenWeight = 0.0;
                float3 chosenLight = 0;
                float3 chosenDir = 0;
                float chosenDist = 0.0;
                for (int j = 0; j < count && chosen < 0; ++j)
                {
                    if (LightColor[j].w < 0.5)
                        continue;
                    float3 toLight;
                    float dist;
                    float3 e = LocalLightIlluminance(j, a.xyz, toLight, dist);
                    float ndl = saturate(dot(b.xyz, toLight));
                    float w = Luminance(e) * ndl;
                    running += w;
                    if (w > 0.0 && running >= target)
                    {
                        chosen = j;
                        chosenWeight = w;
                        chosenLight = e * ndl;
                        chosenDir = toLight;
                        chosenDist = dist;
                    }
                }
                if (chosen < 0)
                    continue;
                // towards a random point of the light's shape: a disc facing the point
                float3 dir = chosenDir;
                float len = chosenDist;
                float radius = LightSpot[chosen].y;
                if (radius > 0.0)
                {
                    float3 tangent = normalize(abs(dir.y) < 0.99 ? cross(dir, float3(0.0, 1.0, 0.0)) : cross(dir, float3(1.0, 0.0, 0.0)));
                    float3 bitangent = cross(dir, tangent);
                    float r = radius * sqrt(u.y);
                    float phi = 2.0 * PI * frac(u.y * 7.31 + 0.5 * k);
                    float3 d = LightPos[chosen].xyz + (tangent * cos(phi) + bitangent * sin(phi)) * r - a.xyz;
                    len = length(d);
                    dir = d / max(len, 1e-4);
                }
                // the sample stands for all the light: its own over the chance it was picked, over the samples
                float3 share = chosenLight * (total / chosenWeight) / Samples;
                // the last 0.6 m before the light are not tested: car lamps sit inside their housing and glass
                bool hidden = Blocked(a.xyz, dir, max(len - 0.6, 0.0), false);
                if (LightColor[chosen].w > 1.5)
                    blocked -= hidden ? 0.0 : share; // a light only NatureFX has: added where it gets through
                else if (hidden)
                    blocked += share;
            }
        }
    }
    // half float targets: a headlight blocked right in front of it puts far more lux than they hold (an infinity,
    // then NaN in the composite, spreading through the history as dots)
    Output.Store4(index * 16, asuint(float4(clamp(blocked, -60000.0, 60000.0), 1.0)));
}

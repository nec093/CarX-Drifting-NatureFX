// NFXNative local light shadows, per half resolution pixel: how much of the light of the lamps near it is blocked.
// Many lamps can reach a pixel (up to NFX_MAX_LOCAL_LIGHTS: a lit mountain pass, a town), so not every one gets a ray:
// two lights are picked per pixel and frame in proportion to the light each would put there, each traced towards a
// random point of the light's shape (soft shadows), and weighted so the sum over frames is the full blocked light;
// the shadow pass (and NRD) accumulate and denoise it.
// The pick is ReSTIR's first stage: with few lights every one is a candidate (resampled importance sampling over all
// of them), with many a streaming reservoir looks at a fixed number of candidates drawn at random, so the cost per
// pixel stays the same however many lamps there are. (The reservoirs are not reused across frames or neighbours:
// the temporal accumulation and the denoiser do that part.)
// Lamps HDRP draws come off where blocked; lights HDRP does not have (emissive surfaces standing in as lamps,
// LightColor.w 2) go on where they get through (a negative blocked share).
// Surface: as Ambient.hlsl. Output: float4 per pixel (blocked illuminance rgb, before the surface's diffuse colour
// and the exposure; with NFX_FLAG_LAMP_MASK only the lights NatureFX adds). Compiled into LocalShadows.h.
#include "NFXCommon.hlsli"

static const int Samples = 2;
static const int ExactUpTo = 16;  // every light a candidate up to this many
static const int Candidates = 8;  // else this many per sample, drawn uniformly

// the candidate's target weight: the luminance of the light it puts on the point, unshadowed (0: HDRP shadows it)
float TargetWeight(int i, float3 p, float3 n, out float3 light, out float3 toLight, out float dist)
{
    light = 0;
    toLight = 0;
    dist = 0;
    if (LightColor[i].w < 0.5)
        return 0.0;
    // HDRP's lamps have masks of their own (LampMask.hlsl)
    if ((Flags & NFX_FLAG_LAMP_MASK) != 0u && LightColor[i].w < 1.5)
        return 0.0;
    float3 e = LocalLightIlluminance(i, p, toLight, dist);
    light = e * saturate(dot(n, toLight));
    return Luminance(light);
}

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
        bool exact = count <= ExactUpTo;
        // exact: the sum of every light's weight, for the picks below
        float total = 0.0;
        if (exact)
        {
            for (int i = 0; i < count; ++i)
            {
                float3 l, t;
                float d;
                total += TargetWeight(i, a.xyz, b.xyz, l, t, d);
            }
        }
        if (count > 0 && (!exact || total > 1e-4))
        {
            for (int k = 0; k < Samples; ++k)
            {
                float2 u = Hash22(id.xy, FrameIndex, 11u + (uint)k * 2u);
                int chosen = -1;
                float chosenWeight = 0.0;
                float3 chosenLight = 0;
                float3 chosenDir = 0;
                float chosenDist = 0.0;
                float share = 0.0; // what the pick stands for: the sum of weights over the pick's weight
                if (exact)
                {
                    // a walk over the running sum
                    float target = u.x * total;
                    float running = 0.0;
                    for (int j = 0; j < count && chosen < 0; ++j)
                    {
                        float3 l, t;
                        float d;
                        float w = TargetWeight(j, a.xyz, b.xyz, l, t, d);
                        running += w;
                        if (w > 0.0 && running >= target)
                        {
                            chosen = j;
                            chosenWeight = w;
                            chosenLight = l;
                            chosenDir = t;
                            chosenDist = d;
                        }
                    }
                    share = total;
                }
                else
                {
                    // a streaming reservoir over uniformly drawn candidates: each kept with its weight over the
                    // running sum; the resampling weight is target / source chance = target * count
                    float sum = 0.0;
                    for (int c = 0; c < Candidates; ++c)
                    {
                        float2 r = Hash22(id.xy, FrameIndex, 101u + (uint)(k * Candidates + c) * 3u);
                        int j = min((int)(r.x * count), count - 1);
                        float3 l, t;
                        float d;
                        float w = TargetWeight(j, a.xyz, b.xyz, l, t, d) * count;
                        sum += w;
                        if (w > 0.0 && r.y * sum < w)
                        {
                            chosen = j;
                            chosenWeight = w / count;
                            chosenLight = l;
                            chosenDir = t;
                            chosenDist = d;
                        }
                    }
                    share = sum / Candidates;
                }
                if (chosen < 0 || chosenWeight <= 0.0)
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
                float3 light = chosenLight * (share / chosenWeight) / Samples;
                // the last 0.6 m before the light are not tested: car lamps sit inside their housing and glass
                bool hidden = Blocked(a.xyz, dir, max(len - 0.6, 0.0), false);
                if (LightColor[chosen].w > 1.5)
                    blocked -= hidden ? 0.0 : light; // a light only NatureFX has: added where it gets through
                else if (hidden)
                    blocked += light;
            }
        }
    }
    // half float targets: a headlight blocked right in front of it puts far more lux than they hold (an infinity,
    // then NaN in the composite, spreading through the history as dots)
    Output.Store4(index * 16, asuint(float4(clamp(blocked, -60000.0, 60000.0), 1.0)));
}

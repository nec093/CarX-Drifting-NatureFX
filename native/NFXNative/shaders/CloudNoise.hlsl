// NFXNative clouds (Sky.hlsli): makes the clouds' noise and cloud type map once, into Output after the transmittance
// table. Dispatched over 4096 x 64 threads: each fills one texel of the shape noise (64³), the first 32768 one of the
// detail noise (32³), the first 65536 one of the map (256²). All tile, so the sky repeats without seams.
// Compiled into CloudNoise.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"

float3 Hash3(int3 c, int period, uint seed)
{
    c = ((c % period) + period) % period;
    uint3 v = uint3(c) * uint3(1597334673u, 3812015801u, 2798796415u) + seed * 747796405u;
    uint n = (v.x ^ v.y ^ v.z) * 1597334673u;
    uint3 r = uint3(n, n * 16807u, n * 48271u);
    r = (r ^ (r >> 15)) * 2246822519u;
    return float3(r & 0xffffu) / 65535.0;
}

// tileable gradient noise, 0..1 (period cells over the unit cube)
float Gradient3(float3 p, int period, uint seed)
{
    p *= period;
    int3 i = (int3)floor(p);
    float3 f = p - i;
    float3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float result = 0.0;
    [unroll] for (int z = 0; z < 2; ++z)
    [unroll] for (int y = 0; y < 2; ++y)
    [unroll] for (int x = 0; x < 2; ++x)
    {
        int3 o = int3(x, y, z);
        float3 g = Hash3(i + o, period, seed) * 2.0 - 1.0;
        float w = (x ? u.x : 1.0 - u.x) * (y ? u.y : 1.0 - u.y) * (z ? u.z : 1.0 - u.z);
        result += w * dot(g, f - o);
    }
    return saturate(result * 0.7 + 0.5);
}

// tileable Worley noise, inverted: 1 at a cell's feature point, 0 a cell away
float Worley3(float3 p, int period, uint seed)
{
    p *= period;
    int3 i = (int3)floor(p);
    float best = 9.0;
    for (int z = -1; z <= 1; ++z)
    for (int y = -1; y <= 1; ++y)
    for (int x = -1; x <= 1; ++x)
    {
        int3 c = i + int3(x, y, z);
        float3 d = c + Hash3(c, period, seed) - p;
        best = min(best, dot(d, d));
    }
    return saturate(1.0 - sqrt(best));
}

float Value2(float2 p, int period, uint seed)
{
    p *= period;
    int2 i = (int2)floor(p);
    float2 f = p - i;
    f = f * f * (3.0 - 2.0 * f);
    float a = Hash3(int3(i, 0), period, seed).x, b = Hash3(int3(i + int2(1, 0), 0), period, seed).x;
    float c = Hash3(int3(i + int2(0, 1), 0), period, seed).x, d = Hash3(int3(i + int2(1, 1), 0), period, seed).x;
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float Fbm2(float2 p, int period, int octaves, uint seed)
{
    float sum = 0.0, amp = 0.5, norm = 0.0;
    for (int o = 0; o < octaves; ++o)
    {
        sum += Value2(p, period << o, seed + o * 17) * amp;
        norm += amp;
        amp *= 0.5;
    }
    return sum / norm;
}

float Cells2(float2 p, int period, uint seed)
{
    return Worley3(float3(p, 0.5 / period), period, seed);
}

NFX_ENTRY(id)
{
    uint thread = id.y * 4096 + id.x;
    // shape: Perlin-Worley (billowing heaps), as Schneider's Nubis
    if (id.x < 4096 && id.y < CloudShapeSize)
    {
        float3 p = (float3(id.x % CloudShapeSize, id.x / CloudShapeSize, id.y) + 0.5) / CloudShapeSize;
        float perlin = Gradient3(p, 4, 1) * 0.5 + Gradient3(p, 8, 2) * 0.3 + Gradient3(p, 16, 3) * 0.2;
        float worley = Worley3(p, 4, 4) * 0.625 + Worley3(p, 8, 5) * 0.25 + Worley3(p, 16, 6) * 0.125;
        float shape = saturate((perlin - (worley - 1.0)) / (2.0 - worley) * 1.0);
        shape = saturate((shape - 0.35) / 0.65) * 0.6 + worley * 0.4;
        Output.Store(CloudShapeOffset + thread * 4, asuint(shape));
    }
    // detail: Worley at three scales, for the cauliflower edges
    if (thread < CloudDetailSize * CloudDetailSize * CloudDetailSize)
    {
        float3 p = (float3(thread % CloudDetailSize, (thread / CloudDetailSize) % CloudDetailSize, thread / (CloudDetailSize * CloudDetailSize)) + 0.5) / CloudDetailSize;
        float detail = Worley3(p, 4, 7) * 0.625 + Worley3(p, 8, 8) * 0.25 + Worley3(p, 16, 9) * 0.125;
        Output.Store(CloudDetailOffset + thread * 4, asuint(detail));
    }
    // where each cloud type may form: cumulus in fields of separate heaps, a broad altostratus sheet, a few
    // cumulonimbus cells (where it rains)
    if (thread < CloudMapSize * CloudMapSize)
    {
        float2 p = (float2(thread % CloudMapSize, thread / CloudMapSize) + 0.5) / CloudMapSize;
        float field = Fbm2(p, 4, 3, 11);
        float heaps = Cells2(p, 18, 23) * 0.65 + Fbm2(p, 16, 3, 5) * 0.35;
        float cumulus = smoothstep(0.25, 0.85, heaps) * smoothstep(0.25, 0.7, field);
        float alto = smoothstep(0.3, 0.75, Fbm2(p, 3, 5, 31)) * 0.85 + 0.15;
        float tower = smoothstep(0.62, 0.85, Fbm2(p, 3, 3, 47) * 0.7 + Cells2(p, 5, 53) * 0.3);
        float rain = max(tower, smoothstep(0.75, 1.0, alto) * 0.5);
        Output.Store4(CloudMapOffset + thread * 16, asuint(float4(cumulus, alto, tower, rain)));
    }
}

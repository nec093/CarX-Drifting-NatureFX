// NFXNative 3D fog (phase 2b): the light the air scatters at each cell of a camera aligned grid (froxels:
// FogW x FogH columns, FogD slices spread exponentially from FogNear to FogFar metres of view depth), so that any
// depth can look up the fog in front of it: opaque surfaces, glass and particles alike (NFX_NativeBridge
// FogReproject / FogIntegrate, the composite, NFX_Particle). Per cell: the extinction (MapFog's height fog) and the
// light scattered towards the camera from the sun (one traced shadow ray, a new point in the cell each frame: the
// history smooths it), the sky (the probe: the engine's own sky) and the lamps and headlights (the frame's local
// lights, unshadowed). Surface: float4 per column (the view ray at view depth 1, camera relative). Output: float4
// per cell (scattered radiance per metre, rgb, for a unit sun... in nits/m; extinction 1/m). AmbientParams as Fog.hlsl
// (x extinction at the base, y base height, z scale height, w anisotropy). Compiled into FogVolume.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"
#include "CloudsCommon.hlsli"

static const uint FogW = 160, FogH = 90, FogD = 64;
static const float FogNear = 0.5, FogFar = 3000.0;

float FogSigma(float y)
{
    return AmbientParams.x * exp(-max(y - AmbientParams.y, 0.0) / max(AmbientParams.z, 1.0));
}

// Roadmap C: the map's local fog and the tyre smoke (HDRP's local volumetric fog boxes, AtmospherePass gathers them):
// ReflParams.x boxes after the columns in Surface, 5 float4 each (centre xyz, extinction; the three axes over the
// half sizes, w albedo r g b; x the fade share, y the noise size (m), z 1 = round (HDRP's exponential falloff: a puff
// of smoke, an ellipsoid), 0 = a box). The density fades in over the fade share from the outside and is broken up by
// the clouds' Worley noise (Screen: the sky data) at the box's own scale, rising slowly. Returns the extinction,
// the albedo mix in tint.
float BoxesSigma(float3 p, out float3 tint)
{
    tint = 0.0;
    float sum = 0.0;
    uint count = min((uint)ReflParams.x, 64u);
    uint base = FogW * FogH * 3;
    for (uint i = 0; i < count; ++i)
    {
        float4 c = asfloat(Surface.Load4((base + i * 5) * 16));
        float4 ax = asfloat(Surface.Load4((base + i * 5 + 1) * 16));
        float4 ay = asfloat(Surface.Load4((base + i * 5 + 2) * 16));
        float4 az = asfloat(Surface.Load4((base + i * 5 + 3) * 16));
        float4 look = asfloat(Surface.Load4((base + i * 5 + 4) * 16));
        float3 d = p - c.xyz;
        float3 local = float3(dot(d, ax.xyz), dot(d, ay.xyz), dot(d, az.xyz));
        float edge = look.z > 0.5 ? length(local) : max(abs(local.x), max(abs(local.y), abs(local.z)));
        if (edge >= 1.0)
            continue;
        float fade = saturate((1.0 - edge) / max(look.x, 0.05));
        fade = look.z > 0.5 ? fade * fade * (3.0 - 2.0 * fade) : fade;
        // the noise at the box's own size, rising: billows in smoke, patches in a fog bank
        float3 q = p / max(look.y, 0.2) + float3(0.0, -ReflParams.y * 0.25, 0.0);
        float noise = DetailNoise(q * DetailTile / CloudDetailSize * 8.0); // a Worley cell about the noise size
        float sigma = c.w * fade * saturate(noise * 1.8 - 0.35);
        sum += sigma;
        tint += sigma * float3(ax.w, ay.w, az.w);
    }
    tint = sum > 0.0 ? tint / sum : 1.0;
    return sum;
}

// The tyre smoke's grid (TireSmoke.UpdateSmokeVolume, after the boxes, ReflParams.z = 1): 3 float4 of header (the
// grid's lowest corner and 1 / cell size; the smoke's box, its min and max), then the 48 x 24 x 48 cells' extinction
// (1/m), four to a float4. The light of the sun and the lamps through it on its way to the mist: four steps along the
// way, where it crosses the smoke's box.
static const uint SmokeX = 48, SmokeY = 24, SmokeZ = 48;

float SmokeAt(uint base, float3 p, float4 origin)
{
    int3 cell = (int3)floor((p - origin.xyz) * origin.w);
    if (any(cell < 0) || cell.x >= (int)SmokeX || cell.y >= (int)SmokeY || cell.z >= (int)SmokeZ)
        return 0.0;
    uint i = ((uint)cell.z * SmokeY + (uint)cell.y) * SmokeX + (uint)cell.x;
    float4 v = asfloat(Surface.Load4((base + 3 + i / 4) * 16));
    uint k = i & 3u;
    return k == 0 ? v.x : k == 1 ? v.y : k == 2 ? v.z : v.w;
}

float SmokeTransmittance(float3 from, float3 to)
{
    if (ReflParams.z < 0.5)
        return 1.0;
    uint base = FogW * FogH * 3 + 64 * 5;
    float4 origin = asfloat(Surface.Load4(base * 16));
    float3 lo = asfloat(Surface.Load3((base + 1) * 16));
    float3 hi = asfloat(Surface.Load3((base + 2) * 16));
    float3 dir = to - from;
    float3 inv = 1.0 / select(abs(dir) > 1e-5, dir, (float3)1e-5);
    float3 t0 = (lo - from) * inv, t1 = (hi - from) * inv;
    float3 a = min(t0, t1), b = max(t0, t1);
    float enter = saturate(max(max(a.x, a.y), a.z)), leave = saturate(min(min(b.x, b.y), b.z));
    if (leave <= enter)
        return 1.0;
    float tau = 0.0;
    [unroll]
    for (int k = 0; k < 4; ++k)
        tau += SmokeAt(base, from + dir * lerp(enter, leave, (k + 0.5) / 4.0), origin);
    return exp(-tau * length(dir) * (leave - enter) / 4.0);
}

float FogPhase(float c, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
}

float FogPhaseMix(float c)
{
    return lerp(FogPhase(c, AmbientParams.w), FogPhase(c, -0.2), 0.2);
}

NFX_ENTRY(id)
{
    if (id.x >= FogW || id.y >= FogH * FogD)
        return;
    uint x = id.x, y = id.y % FogH, s = id.y / FogH;
    uint index = (s * FogH + y) * FogW + x;
    uint c = (y * FogW + x) * 3;
    float3 column = asfloat(Surface.Load3(c * 16));
    float3 stepX = asfloat(Surface.Load3((c + 1) * 16));
    float3 stepY = asfloat(Surface.Load3((c + 2) * 16));
    // a new point inside the cell each frame, across as well as in depth: a lamp's glow far away is smaller than a
    // cell, and sampled at the cells' centres it showed as a square (the history averages the points)
    float2 xi = Hash22(uint2(x, y + s * FogH), FrameIndex, 7);
    float jitter = IGN(float2(x + s * 13u, y) + 5.588238 * (FrameIndex % 64u));
    float z = FogNear * pow(FogFar / FogNear, (s + jitter) / FogD);
    float3 rel = (column + stepX * (xi.x - 0.5) + stepY * (xi.y - 0.5)) * z;
    float3 p = Camera + rel;
    float distance = length(rel);
    float3 view = rel / max(distance, 1e-4); // from the camera
    float3 tint;
    float boxes = BoxesSigma(p, tint);
    float baseSigma = FogSigma(p.y);
    float sigma = baseSigma + boxes;
    // the boxes' albedo tints what they scatter (smoke a little grey), the air's is white
    float3 albedo = sigma > 0.0 ? (baseSigma + boxes * tint) / sigma : 1.0;
    float4 result = 0.0;
    if (sigma > 0.0)
    {
        float3 light = 0.0;
        if (SunUp != 0)
        {
            float3 toSun = SampleSunCone(SunDir, SunRadius, Hash22(uint2(x, y + s * FogH), FrameIndex, 31));
            if (!Blocked(p, toSun, 2000.0, distance > CoarseDistance))
                light += SunColor * FogPhaseMix(dot(SunDir, view)) * SmokeTransmittance(p, p + toSun * 30.0);
        }
        light += 0.5 * (SampleSH9(float3(0.0, 1.0, 0.0)) + SampleSH9(float3(0.0, -1.0, 0.0)));
        // lamps and headlights: their beams in the mist (HDRP's punctual light falloff, the spot's cone)
        uint count = min((uint)LightCount.x, (uint)NFX_MAX_LOCAL_LIGHTS);
        for (uint i = 0; i < count; ++i)
        {
            float3 d = LightPos[i].xyz - p;
            float range = LightPos[i].w;
            float raw = dot(d, d);
            if (raw > range * range)
                continue;
            float3 L = d * rsqrt(max(raw, 1e-6)); // towards the lamp
            // no closer than the lamp's own size or this cell's (an eighth of its depth): a lamp inside a cell lit it
            // hundreds of times over (1 / 0.1 m squared), and the grid's smooth lookup spread that over the screen:
            // the night sky turned white under the eye's night adaptation
            float cell = z * 0.13;
            float d2 = max(raw, max(LightSpot[i].y * LightSpot[i].y, cell * cell));
            float window = saturate(1.0 - d2 * d2 / (range * range * range * range));
            float spot = 1.0;
            if (LightDir[i].w > 0.0)
            {
                float a = saturate(dot(-L, LightDir[i].xyz) * LightDir[i].w + LightSpot[i].x);
                // a headlight's cut-off in the mist too (its beam does not light the air above the road)
                spot = a * a * NFX_BeamPattern(LightSpot[i].z, LightDir[i].xyz, -L, LightSpot[i].w);
            }
            // the tyre smoke between the mist and the lamp: the headlights' beams cut by a cloud of smoke
            spot *= SmokeTransmittance(p, LightPos[i].xyz);
            light += LightColor[i].rgb / d2 * window * window * spot * FogPhaseMix(dot(L, view));
        }
        result = float4(min(light * sigma * albedo, 1e7), sigma);
    }
    Output.Store4(index * 16, asuint(result));
}

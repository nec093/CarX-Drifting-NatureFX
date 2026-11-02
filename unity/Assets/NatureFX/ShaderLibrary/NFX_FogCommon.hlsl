// NatureFX 3D fog lookup (phase 2b): the own engine's froxel fog (NFXNative FogVolume.hlsl, integrated by
// NFX_NativeBridge FogIntegrate) for anything at any depth: the opaque composite (NFX_RTShadow), glass (NFX_RTReflect)
// and particles (NFX_Particle). Globals set by RTShadowPass each frame. Beyond the grid's far end (FOG_FAR) the haze
// goes on analytically (no shadows that far). Radiance in nits, not exposed.
#ifndef NFX_FOG_COMMON
#define NFX_FOG_COMMON

#define NFX_FOG_D 64
#define NFX_FOG_NEAR 0.5
#define NFX_FOG_FAR 3000.0

TEXTURE3D(_NFX_FogVolume);           // xyz: screen uv and slice; rgb light scattered towards the camera, a transmittance
SAMPLER(sampler_NFX_FogVolume);
float4 _NFX_FogVolumeParams;         // x 1 = on
float4 _NFX_FogHeight;               // MapFog.NativeParams: x extinction at the base (1/m), y base height, z scale height, w anisotropy
float4 _NFX_FogSun;                  // rgb the sun's illuminance at the ground (lux); w 1 when it is up
float4 _NFX_FogSunDir;               // xyz towards the sun
StructuredBuffer<float4> _NFX_SkyLight; // the engine's sky probe (7 float4) and [7] the sky's mean radiance

// The tyre smoke's shadow (TireSmoke.UpdateSmokeShadow): the smoke's particles splatted along the sun onto a grid
// across it (128 x 128 cells around the camera); per cell the optical depth of all the smoke in that column, and the
// nearest and farthest smoke along the sun. A point lies under the share of the column that is between it and the
// sun (the smoke spread evenly over its depth): the ground and the cars under a cloud of smoke, and the smoke's own far
// side, get that much less sun.
TEXTURE2D(_NFX_SmokeShadowTex);      // r optical depth, g nearest smoke along the sun (m, from the centre), b farthest
float4 _NFX_SmokeShadowCenter;       // xyz the grid's centre (absolute), w 1 / cell size (1/m)
float4 _NFX_SmokeShadowU;            // xyz across the sun (grid x), w cells across
float4 _NFX_SmokeShadowV;            // xyz across the sun (grid y)
float4 _NFX_SmokeShadowS;            // xyz towards the sun, w 1 = on

float NFX_SmokeTransmittance(float3 positionAWS)
{
    if (_NFX_SmokeShadowS.w < 0.5)
        return 1.0;
    float3 d = positionAWS - _NFX_SmokeShadowCenter.xyz;
    float n = _NFX_SmokeShadowU.w;
    float2 cell = float2(dot(d, _NFX_SmokeShadowU.xyz), dot(d, _NFX_SmokeShadowV.xyz)) * _NFX_SmokeShadowCenter.w + n * 0.5 - 0.5;
    if (any(cell < 0.0) || any(cell >= n - 1.0))
        return 1.0;
    float depth = dot(d, _NFX_SmokeShadowS.xyz);
    int2 base = (int2)floor(cell);
    float2 f = cell - base;
    float sum = 0.0;
    [unroll]
    for (int i = 0; i < 4; ++i)
    {
        int2 o = int2(i & 1, i >> 1);
        float4 m = LOAD_TEXTURE2D(_NFX_SmokeShadowTex, (uint2)(base + o));
        float between = m.r > 0.0 ? saturate((m.b - depth) / max(m.b - m.g, 0.3)) : 0.0;
        float w = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
        sum += w * exp(-m.r * between);
    }
    return sum;
}

// A headlight's beam as HeadlightController's cookies draw it (relative to its peak), for what does not see HDRP's
// cookies: kind 1 the low beam for driving on the left (Japan: the cut-off 0.6° below the axis, rising 15° on the left
// to 1° up, the hot spot under it a little to the left, a wide fill, 1.5 % above), 3 the same mirrored (driving on the
// right), 2 the high beam (gathered on the axis). forward: the lamp's axis; dir: from the lamp
// to the point; roll: the lamp's turn about its axis from level (radians: a car leaning in a drift or on a bank).
float NFX_BeamPattern(float kind, float3 forward, float3 dir, float roll)
{
    if (kind < 0.5)
        return 1.0;
    float3 level = abs(forward.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    float3 right0 = normalize(cross(level, forward));
    float3 up0 = cross(forward, right0);
    float cr = cos(roll), sr = sin(roll);
    float3 up = up0 * cr - right0 * sr;
    float3 right = cross(up, forward);
    float z = max(dot(dir, forward), 1e-3);
    float h = degrees(atan2(dot(dir, right), z));
    float a = degrees(atan2(dot(dir, up), z));
    // kind 3: the low beam for driving on the right (Europe / US), the pattern mirrored
    if (kind > 2.5)
        h = -h;
    if (kind > 1.5 && kind < 2.5)
    {
        float hot = exp(-(h / 5.0) * (h / 5.0) - (a / 2.5) * (a / 2.5));
        float glow = exp(-(h / 20.0) * (h / 20.0) - (a / 7.0) * (a / 7.0)) * 0.25;
        float nearRoad = a < -2.0 ? exp(-(h / 28.0) * (h / 28.0)) * 0.12 : 0.0;
        return min(1.0, hot + glow + nearRoad);
    }
    float cut = -0.6 + (h < 0.0 ? min(-h * 0.267949, 1.6) : 0.0);
    float below = cut - a;
    float edge = saturate(below / 0.5 + 0.5);
    float spread = a < -1.2 ? 2.2 : 1.2; // the peak 1.2° down: slower below it than above
    float hotSpot = exp(-((h + 1.5) / 9.0) * ((h + 1.5) / 9.0) - ((a + 1.2) / spread) * ((a + 1.2) / spread));
    float fill = exp(-(h / 22.0) * (h / 22.0)) * exp(-max(0.0, below - 1.0) / 2.0) * 0.25 * saturate((38.0 - sqrt(h * h + a * a)) / 8.0);
    return lerp(0.015, min(1.0, hotSpot + fill), edge);
}

// The tyre smoke's density in 3D (TireSmoke.UpdateSmokeVolume): extinction (1/m) on a 48 x 24 x 48 grid of 1 m cells
// round the camera, and the box the smoke fills. The light of a lamp or a headlight reaching a point through the smoke:
// a few steps along the way from the point to the lamp, skipped where the way misses the smoke's box.
TEXTURE3D(_NFX_SmokeVolume);
SAMPLER(sampler_NFX_SmokeVolume);
float4 _NFX_SmokeVolumeOrigin;  // xyz the grid's lowest corner (absolute), w 1 / cell size
float4 _NFX_SmokeVolumeSize;    // xyz cells, w 1 = on
float4 _NFX_SmokeBoundsMin;     // xyz the smoke's box (absolute)
float4 _NFX_SmokeBoundsMax;

float NFX_SmokeLampTransmittance(float3 fromAWS, float3 lampAWS)
{
    if (_NFX_SmokeVolumeSize.w < 0.5)
        return 1.0;
    // the way against the smoke's box (slabs)
    float3 dir = lampAWS - fromAWS;
    float3 inv = 1.0 / (abs(dir) > 1e-5 ? dir : 1e-5);
    float3 t0 = (_NFX_SmokeBoundsMin.xyz - fromAWS) * inv, t1 = (_NFX_SmokeBoundsMax.xyz - fromAWS) * inv;
    float3 lo = min(t0, t1), hi = max(t0, t1);
    float enter = saturate(max(max(lo.x, lo.y), lo.z)), leave = saturate(min(min(hi.x, hi.y), hi.z));
    if (leave <= enter)
        return 1.0;
    float len = length(dir) * (leave - enter);
    float tau = 0.0;
    [unroll]
    for (int k = 0; k < 6; ++k)
    {
        float3 p = fromAWS + dir * lerp(enter, leave, (k + 0.5) / 6.0);
        float3 uvw = (p - _NFX_SmokeVolumeOrigin.xyz) * _NFX_SmokeVolumeOrigin.w / _NFX_SmokeVolumeSize.xyz;
        tau += SAMPLE_TEXTURE3D_LOD(_NFX_SmokeVolume, sampler_NFX_SmokeVolume, uvw, 0).r;
    }
    return exp(-tau * len / 6.0);
}

float NFX_FogPhase(float c, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
}

// the fog between the camera and a point at a view depth (m) on the screen
float4 NFX_FogAt(float2 uv, float viewDepth)
{
    float slice = log(max(viewDepth, NFX_FOG_NEAR) / NFX_FOG_NEAR) / log(NFX_FOG_FAR / NFX_FOG_NEAR);
    // each texel holds the far end of its slice: lerp from no fog at the camera up to the first
    float coord = slice - 0.5 / NFX_FOG_D;
    float4 fog = SAMPLE_TEXTURE3D_LOD(_NFX_FogVolume, sampler_NFX_FogVolume, float3(uv, saturate(coord)), 0);
    if (coord < 0.0)
        fog = lerp(float4(0.0, 0.0, 0.0, 1.0), fog, saturate(slice * NFX_FOG_D));
    return fog;
}

// the haze past the grid: from FOG_FAR (as ray length) to distance along a normalized view direction
float4 NFX_FogTail(float3 dir, float from, float to)
{
    if (to <= from || _NFX_FogHeight.x <= 0.0)
        return float4(0.0, 0.0, 0.0, 1.0);
    float camY = GetAbsolutePositionWS(float3(0.0, 0.0, 0.0)).y;
    float c = dot(dir, _NFX_FogSunDir.xyz);
    float phase = lerp(NFX_FogPhase(c, _NFX_FogHeight.w), NFX_FogPhase(c, -0.2), 0.2);
    float3 source = _NFX_FogSun.rgb * phase * _NFX_FogSun.w + _NFX_SkyLight[7].rgb;
    const int steps = 8;
    float dt = (to - from) / steps;
    float3 L = 0.0;
    float T = 1.0;
    for (int i = 0; i < steps; ++i)
    {
        float y = camY + dir.y * (from + (i + 0.5) * dt);
        float sigma = _NFX_FogHeight.x * exp(-max(y - _NFX_FogHeight.y, 0.0) / max(_NFX_FogHeight.z, 1.0));
        float sampleT = exp(-sigma * dt);
        L += T * source * (1.0 - sampleT);
        T *= sampleT;
    }
    return float4(L, T);
}

// the whole fog in front of a point: grid, then the tail. viewDepth and distance of the point, dir normalized
float4 NFX_Fog(float2 uv, float viewDepth, float distance, float3 dir)
{
    float4 near = NFX_FogAt(uv, min(viewDepth, NFX_FOG_FAR));
    if (viewDepth <= NFX_FOG_FAR)
        return near;
    float gridEnd = distance * NFX_FOG_FAR / max(viewDepth, 1e-3);
    float4 tail = NFX_FogTail(dir, gridEnd, distance);
    return float4(near.rgb + near.a * tail.rgb, near.a * tail.a);
}

// apply to a colour (pre-exposed): what the air lets through, plus what it adds
float3 NFX_ApplyFog(float3 color, float4 fog)
{
    return color * fog.a + fog.rgb * GetCurrentExposureMultiplier();
}

#endif

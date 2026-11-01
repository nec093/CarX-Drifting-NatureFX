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

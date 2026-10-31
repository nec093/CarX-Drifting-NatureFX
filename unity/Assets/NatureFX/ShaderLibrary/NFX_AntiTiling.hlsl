// NatureFX anti-tiling: HDRP's texture sampling for the Lit family (core ShaderLibrary/Sampling/SampleUVMapping.hlsl)
// with its regular sampling function swapped for hex tiling (Mikkelsen, "Practical Real-Time Hex-Tiling", JCGT 2022,
// without the rotation): the texture plane is cut into hexagons, each takes the texture from its own random offset,
// and the three nearest are blended with sharpened weights, so no tile repeats and no tile edge shows. Every map the
// Lit data reads goes through it (base colour, mask, normal, detail), each with the same offsets, so they stay
// aligned. A material turns it on with _NFX_Surface.x ([materials] antitiling=), _NFX_AntiTiling scales it for all
// (F8 > Shader rebuild); off it costs one uniform branch.
// Included by NFX_LitData.hlsl before HDRP's LitData.hlsl, which then finds the include guard set and keeps this one.

#ifndef __SAMPLEUVMAPPING_HLSL__
#define __SAMPLEUVMAPPING_HLSL__
#define NFX_ANTITILING_HOOKED

// What LitData.hlsl defines before it includes HDRP's SampleUVMapping.hlsl, here too, since this file is read first:
// the normal map samplers below compile as surface gradients only under it, and LitData's normal resolve treats what
// they return as a gradient. Without it every normal mapped surface came out tilted (a flat map resolved to a 45
// degree normal).
#ifndef SHADER_STAGE_RAY_TRACING
#define SURFACE_GRADIENT
#endif

#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/NormalSurfaceGradient.hlsl"

float _NFX_AntiTiling; // global: 0 off for all, 1 as the materials ask (MaterialProfiles / NatureFXMod)

#define UV_MAPPING_UVSET 0
#define UV_MAPPING_PLANAR 1
#define UV_MAPPING_TRIPLANAR 2

struct UVMapping
{
    int mappingType;
    float2 uv;

    float2 uvZY;
    float2 uvXZ;
    float2 uvXY;

    float3 normalWS;
    float3 triplanarWeights;

    // HDRP's has these only under SURFACE_GRADIENT, which LitData.hlsl defines after this file is read: always here
    float3 tangentWS;
    float3 bitangentWS;
};

float2 NFX_HexHash(int2 v)
{
    uint2 q = uint2(v) * uint2(1597334673u, 3812015801u);
    uint n = (q.x ^ q.y) * 1597334673u;
    return float2(n & 0xffffu, n >> 16) * (1.0 / 65535.0);
}

// The texture sampled through hex tiles: a hex is about 0.6 of a texture tile across, so the repeat of the texture
// itself never lines up. Gradients come from the plain uv, so the offsets do not break the mip choice at hex edges.
float4 NFX_SampleHex(TEXTURE2D_PARAM(textureName, samplerName), float2 uv)
{
    float strength = _NFX_Surface.x * _NFX_AntiTiling;
    if (strength <= 0.0)
        return SAMPLE_TEXTURE2D(textureName, samplerName, uv);
    float2 dx = ddx(uv), dy = ddy(uv);
    // triangle grid: the skewed coordinates of uv (Mikkelsen's TriangleGrid; row major, as his reference code:
    // transposed, the triangles were not equilateral and the hexes stretched along one axis)
    const float2x2 gridToSkewed = float2x2(1.0, -0.57735027, 0.0, 1.15470054);
    float2 skewed = mul(gridToSkewed, uv * 3.4641016);
    int2 base = int2(floor(skewed));
    float3 t = float3(frac(skewed), 0.0);
    t.z = 1.0 - t.x - t.y;
    float s = step(0.0, -t.z);
    float s2 = 2.0 * s - 1.0;
    float3 w = float3(-t.z * s2, s - t.y * s2, s - t.x * s2);
    int2 v1 = base + int2(s, s);
    int2 v2 = base + int2(s, 1.0 - s);
    int2 v3 = base + int2(1.0 - s, s);
    // sharpened weights: each hex mostly its own sample, blended only near its edges (keeps the texture's contrast;
    // a plain linear blend of three samples washed the detail out). Linear, so normal maps blend correctly too.
    float3 W = w * w * w;
    W = W * W;
    W /= dot(W, 1.0);
    // the samples with explicit gradients, so the two that weigh nothing over most of a hex can be skipped
    float4 hex = W.x * SAMPLE_TEXTURE2D_GRAD(textureName, samplerName, uv + NFX_HexHash(v1), dx, dy);
    if (W.y > 0.001)
        hex += W.y * SAMPLE_TEXTURE2D_GRAD(textureName, samplerName, uv + NFX_HexHash(v2), dx, dy);
    if (W.z > 0.001)
        hex += W.z * SAMPLE_TEXTURE2D_GRAD(textureName, samplerName, uv + NFX_HexHash(v3), dx, dy);
    if (strength >= 0.999)
        return hex;
    return lerp(SAMPLE_TEXTURE2D(textureName, samplerName, uv), hex, strength);
}

// Regular sampling functions: hex tiled
#define ADD_FUNC_SUFFIX(Name) Name
#define SAMPLE_TEXTURE_FUNC(textureName, samplerName, uvMapping, unused) NFX_SampleHex(TEXTURE2D_ARGS(textureName, samplerName), uvMapping)
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMappingInternal.hlsl"
#undef ADD_FUNC_SUFFIX
#undef SAMPLE_TEXTURE_FUNC

// Lod sampling functions (vertex displacement, tessellation): as HDRP has them
#define ADD_FUNC_SUFFIX(Name) MERGE_NAME(Name, Lod)
#define SAMPLE_TEXTURE_FUNC(textureName, samplerName, uvMapping, lod) SAMPLE_TEXTURE2D_LOD(textureName, samplerName, uvMapping, lod)
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMappingInternal.hlsl"
#undef ADD_FUNC_SUFFIX
#undef SAMPLE_TEXTURE_FUNC

// Bias sampling functions
#define ADD_FUNC_SUFFIX(Name) MERGE_NAME(Name, Bias)
#define SAMPLE_TEXTURE_FUNC(textureName, samplerName, uvMapping, bias) SAMPLE_TEXTURE2D_BIAS(textureName, samplerName, uvMapping, bias)
#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Sampling/SampleUVMappingInternal.hlsl"
#undef ADD_FUNC_SUFFIX
#undef SAMPLE_TEXTURE_FUNC

#define SAMPLE_UVMAPPING_TEXTURE2D(textureName, samplerName, uvMapping)             SampleUVMapping(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, 0.0)
#define SAMPLE_UVMAPPING_TEXTURE2D_LOD(textureName, samplerName, uvMapping, lod)    SampleUVMappingLod(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, lod)
#define SAMPLE_UVMAPPING_TEXTURE2D_BIAS(textureName, samplerName, uvMapping, bias)  SampleUVMappingBias(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, bias)

#define SAMPLE_UVMAPPING_NORMALMAP(textureName, samplerName, uvMapping, scale)              SampleUVMappingNormal(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, 0.0)
#define SAMPLE_UVMAPPING_NORMALMAP_LOD(textureName, samplerName, uvMapping, scale, lod)     SampleUVMappingNormalLod(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, lod)
#define SAMPLE_UVMAPPING_NORMALMAP_BIAS(textureName, samplerName, uvMapping, scale, bias)   SampleUVMappingNormalBias(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, bias)

#define SAMPLE_UVMAPPING_NORMALMAP_AG(textureName, samplerName, uvMapping, scale)              SampleUVMappingNormalAG(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, 0.0)
#define SAMPLE_UVMAPPING_NORMALMAP_AG_LOD(textureName, samplerName, uvMapping, scale, lod)     SampleUVMappingNormalAGLod(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, lod)
#define SAMPLE_UVMAPPING_NORMALMAP_AG_BIAS(textureName, samplerName, uvMapping, scale, bias)   SampleUVMappingNormalAGBias(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, bias)

#define SAMPLE_UVMAPPING_NORMALMAP_RGB(textureName, samplerName, uvMapping, scale)              SampleUVMappingNormalRGB(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, 0.0)
#define SAMPLE_UVMAPPING_NORMALMAP_RGB_LOD(textureName, samplerName, uvMapping, scale, lod)     SampleUVMappingNormalRGBLod(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, lod)
#define SAMPLE_UVMAPPING_NORMALMAP_RGB_BIAS(textureName, samplerName, uvMapping, scale, bias)   SampleUVMappingNormalRGBBias(TEXTURE2D_ARGS(textureName, samplerName), uvMapping, scale, bias)

#endif // __SAMPLEUVMAPPING_HLSL__

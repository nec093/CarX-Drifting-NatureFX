#ifndef NFX_TRANSPARENT_SHADOW_INCLUDED
#define NFX_TRANSPARENT_SHADOW_INCLUDED

// The sun's ray traced shadow on transparent surfaces (HDRP's forward pass, NatureFX/* shaders only: the generated
// NFX_ShaderPassForward.hlsl calls NFX_TransparentSunShadow). Transparent surfaces have no depth on screen, so the
// screen space traced shadows of the opaque ones cannot reach them; the own engine keeps a ray traced shadow map
// instead (SunShadowMap.cs, NFXNative SunMap.hlsl): a grid across the sun's direction around the camera, each cell the
// distance from a plane far up the sun's direction down to the first thing the sun meets. A surface is in shadow
// where that thing is nearer the sun than the surface. HDRP's sun (its shadow maps are off with the traced shadows)
// lit the transparent surface fully; the share of it the map says is blocked is taken off again.

StructuredBuffer<float> _NFX_SunMap;
float4 _NFX_SunMapOrigin;   // xyz the grid's absolute centre, w the cell size (m); w 0: no map yet
float4 _NFX_SunMapDir;      // xyz towards the sun as the map was traced, w cells per side
float4 _NFX_SunMapParams;   // x how far up the sun's direction the rays start (m), y bias (m)

// 1 lit .. 0 in shadow: the four nearest cells' tests, bilinear (soft over a cell)
float NFX_SunMapVisibility(float3 positionRWS)
{
    if (_NFX_SunMapOrigin.w <= 0.0)
        return 1.0;
    float3 p = GetAbsolutePositionWS(positionRWS) - _NFX_SunMapOrigin.xyz;
    float3 dir = _NFX_SunMapDir.xyz;
    float3 helper = abs(dir.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 right = normalize(cross(helper, dir));
    float3 up = cross(dir, right);
    int n = (int)_NFX_SunMapDir.w;
    float2 cell = float2(dot(p, right), dot(p, up)) / _NFX_SunMapOrigin.w + n * 0.5 - 0.5;
    float height = dot(p, dir);
    int2 c0 = (int2)floor(cell);
    float2 f = cell - c0;
    float visibility = 0.0;
    [unroll]
    for (int k = 0; k < 4; ++k)
    {
        int2 c = c0 + int2(k & 1, k >> 1);
        float w = ((k & 1) ? f.x : 1.0 - f.x) * ((k >> 1) ? f.y : 1.0 - f.y);
        float lit = 1.0;
        if (all(c >= 0) && all(c < n))
        {
            float t = _NFX_SunMap[c.y * n + c.x];
            // the blocker's height up the sun's direction above the grid's plane
            float blocker = _NFX_SunMapParams.x - t;
            lit = t < 1e8 && height < blocker - _NFX_SunMapParams.y ? 0.0 : 1.0;
        }
        visibility += lit * w;
    }
    return visibility;
}

#ifndef NFX_SUNMAP_ONLY // (particles: the map alone, without HDRP's lit types)
void NFX_TransparentSunShadow(float3 V, PositionInputs posInput, PreLightData preLightData, BSDFData bsdfData, BuiltinData builtinData,
                              inout float3 diffuseLighting, inout float3 specularLighting)
{
    float shade = 1.0 - NFX_SunMapVisibility(posInput.positionWS);
    if (shade <= 0.0)
        return;
    LightLoopContext context;
    context.shadowContext = InitShadowContext();
    context.shadowValue = 1;
    context.sampleReflection = 0;
#ifdef APPLY_FOG_ON_SKY_REFLECTIONS
    context.positionWS = posInput.positionWS;
#endif
    context.contactShadow = 0;
    context.contactShadowFade = 0.0;
    for (uint i = 0; i < _DirectionalLightCount; ++i)
    {
        DirectionalLightData light = _DirectionalLightDatas[i];
        // the sun the map was traced for (a moon or a fill light keeps HDRP's own handling)
        if (dot(-light.forward, _NFX_SunMapDir.xyz) < 0.999)
            continue;
        DirectLighting lighting = ShadeSurface_Directional(context, posInput, builtinData, preLightData, light, bsdfData, V);
        // as Lit's PostEvaluateBSDF puts them together
        float3 sunDiffuse = lighting.diffuse * GetModifiedDiffuseColorForSSS(bsdfData);
        float3 sunSpecular = lighting.specular * (1.0 + bsdfData.fresnel0 * preLightData.energyCompensation);
        diffuseLighting = max(diffuseLighting - sunDiffuse * shade, 0.0);
        specularLighting = max(specularLighting - sunSpecular * shade, 0.0);
    }
}

#endif

#endif

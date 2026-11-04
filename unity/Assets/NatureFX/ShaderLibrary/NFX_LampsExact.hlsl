// The lamp masks' pass (NFX_SunExact LampsExact): each of HDRP's lamps shadowed by its own traced mask, shaded from
// the G-buffer drawn again with HDRP's BSDF, with HDRP's attenuation and cookies.
// Included after HDRP's light loop and NFX_FogCommon.hlsl.
#ifndef NFX_LAMPS_EXACT_INCLUDED
#define NFX_LAMPS_EXACT_INCLUDED

struct Attributes
{
    uint vertexID : SV_VertexID;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct Varyings
{
    float4 positionCS : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

Varyings Vert(Attributes input)
{
    Varyings output;
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
    output.positionCS = GetFullScreenTriangleVertexPosition(input.vertexID);
    return output;
}

// Roadmap A4: each lamp's own shadow (NFXNative LampMask.hlsl: the four heaviest lamps per trace pixel, one
// ray each a frame), gathered over time here per lamp: the history keeps each slot's lamp and its
// visibility, a slot takes its lamp's history wherever the pixel was last frame
TEXTURE2D_X(_NFX_LampMask);        // trace resolution: x lamps 0 1, y lamps 2 3 (255 none), z w visibility (0..255)
TEXTURE2D_X(_NFX_LampMaskHistory); // full resolution: x y lamps as the mask, z w visibility (0..4095)
float4 _NFX_LampMaskSize;          // xy the mask's size
float4 _NFX_LampMaskParams;        // x the new frame's share, y 1 = lamp masks in use
float4 _NFX_LampPos[128];          // the engine's lamps (LocalLights): xyz absolute position
float _NFX_LampCount;
float _NFX_OwnLamps;   // 1: HDRP's lamps light nothing for HDRP (OwnLighting.Lamps); worked out at full strength here

struct LampsOut
{
    float4 blocked : SV_Target0; // rgb the light of HDRP's lamps their shadows take away, a 1 = drawn
    float4 history : SV_Target1;
    float4 total : SV_Target2;   // rgb all of HDRP's lamp light, unshadowed (the full relight's split)
};

void UnpackPair(float f, uint bits, out uint a, out uint b)
{
    uint u = (uint)f;
    a = u & ((1u << bits) - 1u);
    b = u >> bits;
}

// the light HDRP's lamps and headlights (punctual lights) put on each pixel without their shadow maps (off
// while the own engine traces their shadows), times what each lamp's own mask finds blocked: the composite
// takes it away (diffuse and shine alike, lamp by lamp)
LampsOut FragLamps(Varyings input)
{
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
    LampsOut o;
    o.blocked = 0.0;
    o.history = float4(255.0 + 256.0 * 255.0, 255.0 + 256.0 * 255.0, 0.0, 0.0);
    o.total = 0.0;
    uint2 pixel = (uint2)input.positionCS.xy;
    float depth = LoadCameraDepth(pixel);
    if (depth == UNITY_RAW_FAR_CLIP_VALUE || _NFX_LampMaskParams.y < 0.5)
        return o;
    float4 g0 = LOAD_TEXTURE2D_X(_GBufferTexture0, pixel);
    if (g0.r > 0.999 && g0.g < 0.001 && g0.b > 0.999 && g0.a < 0.001)
        return o;

    // this frame's masks, and the history where the pixel was
    float2 uv = (pixel + 0.5) * _ScreenSize.zw;
    // of the four trace pixels around, the one on this surface: the nearest in depth. The nearest one alone
    // gave an object's edge the background's mask (lit where the background was: white outlines)
    float viewDepth = LinearEyeDepth(depth, _ZBufferParams);
    float2 maskPos = uv * _NFX_LampMaskSize.xy - 0.5;
    int2 maskBase = (int2)floor(maskPos);
    uint2 maskPixel = 0;
    float bestDiff = 1e9;
    [unroll]
    for (int c = 0; c < 4; ++c)
    {
        int2 cand = clamp(maskBase + int2(c & 1, c >> 1), 0, (int2)_NFX_LampMaskSize.xy - 1);
        uint2 at = min((uint2)((cand + 0.5) / _NFX_LampMaskSize.xy * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
        float diff = abs(LinearEyeDepth(LoadCameraDepth(at), _ZBufferParams) - viewDepth);
        if (diff < bestDiff)
        {
            bestDiff = diff;
            maskPixel = (uint2)cand;
        }
    }
    float4 m = LOAD_TEXTURE2D_X(_NFX_LampMask, maskPixel);
    // all zero (lamp 0 twice) is no mask: the buffer as an engine without LampMask leaves it
    if (m.x == 0.0 && m.y == 0.0)
        m = float4(255.0 + 256.0 * 255.0, 255.0 + 256.0 * 255.0, 0.0, 0.0);
    uint lamp[4], vis8[4];
    UnpackPair(m.x, 8, lamp[0], lamp[1]);
    UnpackPair(m.y, 8, lamp[2], lamp[3]);
    UnpackPair(m.z, 8, vis8[0], vis8[1]);
    UnpackPair(m.w, 8, vis8[2], vis8[3]);
    float2 motion;
    DecodeMotionVector(LOAD_TEXTURE2D_X(_CameraMotionVectorsTexture, pixel), motion);
    float2 prevUV = uv - motion;
    bool hasHistory = _NFX_LampMaskParams.x < 0.999 && all(prevUV >= 0.0) && all(prevUV <= 1.0);
    uint hLamp[4] = { 255u, 255u, 255u, 255u };
    uint hVis[4] = { 0u, 0u, 0u, 0u };
    if (hasHistory)
    {
        uint2 prevPixel = min((uint2)(prevUV * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
        float4 h = LOAD_TEXTURE2D_X(_NFX_LampMaskHistory, prevPixel);
        UnpackPair(h.x, 8, hLamp[0], hLamp[1]);
        UnpackPair(h.y, 8, hLamp[2], hLamp[3]);
        UnpackPair(h.z, 12, hVis[0], hVis[1]);
        UnpackPair(h.w, 12, hVis[2], hVis[3]);
    }
    // the range each lamp's visibility has around here this frame (3 x 3 trace pixels): the history is held
    // inside it, so a lit patch that moved on (a headlight with its car) leaves no trail behind
    float lo[4] = { 1.0, 1.0, 1.0, 1.0 };
    float hi[4] = { 0.0, 0.0, 0.0, 0.0 };
    [unroll]
    for (int ny = -1; ny <= 1; ++ny)
    {
        [unroll]
        for (int nx = -1; nx <= 1; ++nx)
        {
            uint2 np = (uint2)clamp((int2)maskPixel + int2(nx, ny), 0, (int2)_NFX_LampMaskSize.xy - 1);
            float4 nm = LOAD_TEXTURE2D_X(_NFX_LampMask, np);
            uint nl[4], nv[4];
            UnpackPair(nm.x, 8, nl[0], nl[1]);
            UnpackPair(nm.y, 8, nl[2], nl[3]);
            UnpackPair(nm.z, 8, nv[0], nv[1]);
            UnpackPair(nm.w, 8, nv[2], nv[3]);
            [unroll]
            for (int a = 0; a < 4; ++a)
            {
                [unroll]
                for (int b = 0; b < 4; ++b)
                {
                    if (nl[b] == lamp[a] && lamp[a] != 255u)
                    {
                        lo[a] = min(lo[a], nv[b] / 255.0);
                        hi[a] = max(hi[a], nv[b] / 255.0);
                    }
                }
            }
        }
    }
    float vis[4];
    [unroll]
    for (int k = 0; k < 4; ++k)
    {
        float current = vis8[k] / 255.0;
        vis[k] = current;
        if (lamp[k] == 255u)
        {
            vis[k] = 1.0;
            continue;
        }
        [unroll]
        for (int j = 0; j < 4; ++j)
        {
            if (hLamp[j] == lamp[k])
                vis[k] = lerp(clamp(hVis[j] / 4095.0, lo[k], hi[k]), current, _NFX_LampMaskParams.x);
        }
    }
    uint q[4];
    [unroll]
    for (int k2 = 0; k2 < 4; ++k2)
        q[k2] = (uint)round(saturate(vis[k2]) * 4095.0);
    o.history = float4(lamp[0] + 256.0 * lamp[1], lamp[2] + 256.0 * lamp[3], q[0] + 4096.0 * q[1], q[2] + 4096.0 * q[3]);

    PositionInputs posInput = GetPositionInput(input.positionCS.xy, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V, pixel / GetTileSize());
    float3 V = GetWorldSpaceNormalizeViewDir(posInput.positionWS);
    BSDFData bsdfData;
    BuiltinData builtinData;
    DECODE_FROM_GBUFFER(posInput.positionSS, UINT_MAX, bsdfData, builtinData);
    PreLightData preLightData = GetPreLightData(V, posInput, bsdfData);
    LightLoopContext context;
    context.shadowContext = InitShadowContext();
    context.shadowValue = 1;
    context.sampleReflection = 0;
#ifdef APPLY_FOG_ON_SKY_REFLECTIONS
    context.positionWS = posInput.positionWS;
#endif
    context.contactShadow = 0;
    context.contactShadowFade = 0.0;
    float3 diffuse = 0.0;
    float3 specular = 0.0;
    float3 allDiffuse = 0.0;
    float3 allSpecular = 0.0;
    int engineLamps = min((int)_NFX_LampCount, 128);
    for (uint i = 0; i < _PunctualLightCount; ++i)
    {
        LightData light = _LightDatas[i];
        // out of the light's range: nothing to add or take away (HDRP's light loop never reaches such a
        // light through its tiles; this loop over every lamp evaluated the full BSDF for each)
        float3 toLight = light.positionRWS - posInput.positionWS;
        if (dot(toLight, toLight) > light.range * light.range)
            continue;
        // the engine's lamp of this light: the same position (LocalLights takes it from the same Light)
        float3 at = GetAbsolutePositionWS(light.positionRWS);
        float visible = 1.0;
        [unroll]
        for (int s = 0; s < 4; ++s)
        {
            if (lamp[s] != 255u && (int)lamp[s] < engineLamps)
            {
                float3 d = _NFX_LampPos[lamp[s]].xyz - at;
                if (dot(d, d) < 0.04)
                    visible = vis[s];
            }
        }
        // and the tyre smoke on the way to the lamp
        visible *= NFX_SmokeLampTransmittance(GetAbsolutePositionWS(posInput.positionWS), at);
        float blocked = 1.0 - visible;
        light.shadowIndex = -1;
        light.contactShadowMask = 0;
        if (_NFX_OwnLamps > 0.5)
        {
            light.diffuseDimmer = 1.0;
            light.specularDimmer = 1.0;
        }
        DirectLighting lighting = ShadeSurface_Punctual(context, posInput, builtinData, preLightData, light, bsdfData, V);
        allDiffuse += lighting.diffuse;
        allSpecular += lighting.specular;
        diffuse += lighting.diffuse * blocked;
        specular += lighting.specular * blocked;
    }
    float energy = 1.0 + bsdfData.fresnel0 * preLightData.energyCompensation;
    float3 diffuseColor = GetModifiedDiffuseColorForSSS(bsdfData);
    float exposure = GetCurrentExposureMultiplier();
    o.blocked = float4((diffuse * diffuseColor + specular * energy) * exposure, 1.0);
    o.total = float4((allDiffuse * diffuseColor + allSpecular * energy) * exposure, 1.0);
    return o;
}

#endif

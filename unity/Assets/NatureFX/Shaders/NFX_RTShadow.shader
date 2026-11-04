// NatureFX ray traced sun shadows and ambient light: accumulation, denoise and composite (RTShadowPass).
// The own engine's sun trace (NFXNative SunShadow.hlsl) writes whether one point of the sun's disc is visible (r)
// and, where it is not, how far the blocker is (g); its ambient / GI trace writes the ambient light relative to HDRP's (rgb: blocked sky darkens,
// bounce light tints). Both are accumulated over frames and blurred along surfaces, the sun by the width of its
// penumbra (sharp where the blocker is close, soft further away). The composite works out per full resolution
// pixel the sun light HDRP added there (HDRP's own sun shadows are off), takes the blocked share of it away and
// scales the rest of the light by the ambient term.
Shader "Hidden/NatureFX/RTShadow"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/NormalBuffer.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Builtin/BuiltinData.hlsl"
    #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"

    TEXTURE2D_X(_NFX_ColorTex);       // camera colour (full res)
    TEXTURE2D_X(_NFX_ShadowTex);      // new frame of the input being accumulated
    TEXTURE2D_X(_NFX_HistoryTex);     // accumulated input of the previous frame
    TEXTURE2D_X(_NFX_SourceTex);      // input of the denoise passes
    TEXTURE2D_X(_NFX_SunTex);         // composite: filtered sun visibility (r)
    TEXTURE2D_X(_NFX_AmbientTex);     // composite: filtered ambient light (rgb)
    TEXTURE2D_X(_NFX_ShaftTex);       // composite: filtered light shafts (rgb, pre-exposed)
    TEXTURE2D_X(_NFX_LocalTex);       // composite: filtered blocked local illuminance (rgb, before exposure and albedo)
    TEXTURE2D_X(_NFX_SurfaceAlbedo);  // Surface trace: rgb albedo, a metallic (full res)
    TEXTURE2D_X(_NFX_SunExactTex);    // NFX_SunExact.shader: rgb the sun light HDRP added, a 1 where known (full res)
    TEXTURE2D_X(_NFX_SurfaceNormal);  // Surface trace: w > 0 where the albedo is the pixel's own
    float4 _NFX_TargetSize;           // size of the target being drawn
    float4 _NFX_SunSize;              // sun trace resolution: xy size, zw 1 / size
    float4 _NFX_AmbientSize;          // ambient trace resolution
    float4 _NFX_ShaftSize;            // light shaft trace resolution
    float4 _NFX_ShaftParams;          // x scattering of the air (1/m), y anisotropy, z max distance (m)
    float4 _NFX_LocalSize;            // local light shadow trace resolution
    float4 _NFX_ShadowCompose;        // x: shadow strength (0: not traced), y: 1 = debug view, 2 = GI debug view, 3 = exact sun light, z: share of the new frame, w: ambient strength
                                      // (_NFX_LeafParams w: own engine GI bounce strength, 0 = one bounce ambient light)
    float4 _NFX_SunFilter;            // x: 2 tan(sun angular radius), y: angle one sun trace pixel covers (radians)
    float4 _NFX_RTSunDir;             // xyz: direction towards the sun, w: 1 when the sun is up
    float4 _NFX_RTSunColor;           // rgb: sun illuminance at the ground (lux)
    // the own engine's sky (NFXNative Sky.hlsli, NativeEngine.TraceSky): the sky view and transmittance tables
    TEXTURE2D_X(_NFX_SkyView);
    TEXTURE2D_X(_NFX_SkyTransmittance);
    float4 _NFX_SkyParams;            // x 1 = draw it, 2 = with clouds, y the sun above the air (lux), z camera height (m), w sun radius
    TEXTURE2D_X(_NFX_CloudTex);       // the own engine's clouds (Clouds.hlsl), quarter resolution: rgb radiance, a transmittance
    TEXTURE2D_X(_NFX_CloudShadowTex); // their shadows seen from above (CloudShadow.hlsl): x the share of sun light through
    float4 _NFX_CloudShadowParams;    // xy the map's centre (world xz), z its size (m), w 1 = use it
    // step A: the light HDRP's lamps added (NFX_SunExact LampsExact) and the lamps as the engine sees them (LocalLights)
    TEXTURE2D_X(_NFX_LampsExactTex);
    // A4 full relight: all of HDRP's lamp light unshadowed (LampsExact), and the G-buffer's baked light (the sky light
    // HDRP's G-buffer pass put on the surface, and its glow)
    TEXTURE2D_X(_NFX_LampsTotalTex);
    TEXTURE2D_X(_NFX_GBuffer3Tex);
    TEXTURE2D_X(_NFX_GBuffer0Tex);  // the G-buffer drawn again (exact sun light): rgb the base colour; magenta, a 0: not drawn
    float _NFX_GBuffer0On;

    // the pixel's colour for the sky's and the lamps' light: the G-buffer's base colour where it was drawn (what is on
    // screen), else the surface trace's, else a grey guess. The trace's colour alone missed grass and small plants the
    // ray tracing scene leaves out (grey, white under the blue sky) and read a leaf card's empty, white texels where the
    // card it hit was not the one on screen (white leaves)
    float3 PixelAlbedo(uint2 pixel, bool known, float3 traced)
    {
        if (_NFX_GBuffer0On > 0.5)
        {
            float4 g0 = LOAD_TEXTURE2D_X(_NFX_GBuffer0Tex, pixel);
            if (!(g0.r > 0.999 && g0.g < 0.001 && g0.b > 0.999 && g0.a < 0.001))
                return saturate(g0.rgb);
        }
        return known ? traced : 0.3;
    }
    float4 _NFX_LampPos[128];   // xyz absolute position, w range
    float4 _NFX_LampColor[128]; // rgb intensity (cd), w 1 traced shadows, 2 a light HDRP does not have
    float4 _NFX_LampDir[128];   // xyz spot direction, w angle scale (0 point)
    float4 _NFX_LampSpot[128];  // x angle offset
    float _NFX_LampCount;

    // the unshadowed light of HDRP's lamps on a point, as the engine weighs it (NFXCommon LocalLightIlluminance)
    float LampsLuminance(float3 pointAWS, float3 n)
    {
        float sum = 0.0;
        int count = min((int)_NFX_LampCount, 128);
        for (int i = 0; i < count; ++i)
        {
            if (_NFX_LampColor[i].w < 0.5 || _NFX_LampColor[i].w > 1.5)
                continue;
            float3 d = _NFX_LampPos[i].xyz - pointAWS;
            float dist = length(d);
            float3 toLight = d / max(dist, 1e-4);
            float range = _NFX_LampPos[i].w;
            float ratio = dist * dist / (range * range);
            float window = saturate(1.0 - ratio * ratio);
            float attenuation = window * window / max(dist * dist, 1e-4);
            if (_NFX_LampDir[i].w > 0.0)
            {
                float cone = saturate(dot(-toLight, _NFX_LampDir[i].xyz) * _NFX_LampDir[i].w + _NFX_LampSpot[i].x);
                attenuation *= cone * cone;
            }
            sum += Luminance(_NFX_LampColor[i].rgb) * attenuation * saturate(dot(n, toLight));
        }
        return sum;
    }
    // OwnLighting.Lamps where the lamps' exact light is not known (no G-buffer pass there): HDRP's lamps' diffuse light
    // on the surface's colour, unshadowed (pre-exposed)
    float3 OwnLampsLight(float3 pointAWS, float3 n, float3 albedoD)
    {
        float3 sum = 0.0;
        int count = min((int)_NFX_LampCount, 128);
        for (int i = 0; i < count; ++i)
        {
            if (_NFX_LampColor[i].w < 0.5 || _NFX_LampColor[i].w > 1.5)
                continue;
            float3 d = _NFX_LampPos[i].xyz - pointAWS;
            float dist = length(d);
            float3 toLight = d / max(dist, 1e-4);
            float range = _NFX_LampPos[i].w;
            float ratio = dist * dist / (range * range);
            float window = saturate(1.0 - ratio * ratio);
            float attenuation = window * window / max(dist * dist, 1e-4);
            if (_NFX_LampDir[i].w > 0.0)
            {
                float cone = saturate(dot(-toLight, _NFX_LampDir[i].xyz) * _NFX_LampDir[i].w + _NFX_LampSpot[i].x);
                attenuation *= cone * cone;
            }
            sum += _NFX_LampColor[i].rgb * attenuation * saturate(dot(n, toLight));
        }
        return sum * albedoD * INV_PI * GetCurrentExposureMultiplier();
    }
    float _NFX_OwnLamps;                    // 1: HDRP's lamps light nothing; the composite adds their lit share

    StructuredBuffer<float4> _NFX_SkyProbe; // the own engine's sky as an ambient probe (HDRP's packing), phase 1c
    float _NFX_SkyAmbientOn;

    // the clouds' shadow on a point: along the sun down to the ground plane (y 0) where the map was made
    float CloudShadowAt(float3 positionAWS)
    {
        float3 sun = _NFX_RTSunDir.xyz;
        float2 xz = positionAWS.xz - sun.xz * positionAWS.y / max(sun.y, 0.05);
        float2 uv = (xz - _NFX_CloudShadowParams.xy) / _NFX_CloudShadowParams.z + 0.5;
        float shade = SAMPLE_TEXTURE2D_X_LOD(_NFX_CloudShadowTex, s_linear_clamp_sampler, saturate(uv), 0).r;
        // beyond the map (16 km): fade to the average of its edge rather than stopping at a line
        float edge = saturate(max(abs(uv.x - 0.5), abs(uv.y - 0.5)) * 2.0 * 8.0 - 7.0);
        return lerp(shade, 1.0, edge * 0.5);
    }

    float3 SkyProbeSH9(float3 n)
    {
        float4 vA = float4(n, 1.0);
        float3 x1 = float3(dot(_NFX_SkyProbe[0], vA), dot(_NFX_SkyProbe[1], vA), dot(_NFX_SkyProbe[2], vA));
        float4 vB = n.xyzz * n.yzzx;
        float3 x2 = float3(dot(_NFX_SkyProbe[3], vB), dot(_NFX_SkyProbe[4], vB), dot(_NFX_SkyProbe[5], vB));
        float vC = n.x * n.x - n.y * n.y;
        return max(x1 + x2 + _NFX_SkyProbe[6].rgb * vC, 0.0);
    }
    StructuredBuffer<float4> _NFX_AmbientProbe; // the sky's diffuse probe (7 float4, the SH9 constants): HDRP's, or the
                                                // own engine's once it replaces HDRP's sky light (OwnLighting)
    float _NFX_OwnAmbient;                      // 1: HDRP's sky light is off; the composite adds the own sky's light
    float _NFX_OwnBrdf;                         // 1: no G-buffer; what the trace does not know HDRP lit itself (OwnLighting.UntracedLit)
    float _NFX_OwnSun;                          // 1: HDRP's sun lights nothing; the composite adds the sun's light
    float _NFX_OwnSkyStrength;                  // the own sky's light on surfaces (Settings.SkyLightStrength)
    float4 _NFX_LeafParams;           // x: share of the sun leaves let through, y: 1 = light shafts, z: 1 = local light shadows,
                                      // w: own engine GI bounce strength (0: the ambient texture is the one bounce factor)
    float4 _NFX_SunExact;             // x: 1 when _NFX_SunExactTex holds this frame's exact sun light
    float _NFX_DenoiseStep;
    float _NFX_DenoiseMode;           // 1: variance guided (the luminance spread of the 3 x 3 decides how much to blur)

    #define SampleRT(tex, uv) SAMPLE_TEXTURE2D_X_LOD(tex, s_linear_clamp_sampler, (uv) * _RTHandleScale.xy, 0)

    // full resolution pixel under a uv
    uint2 FullPixel(float2 uv)
    {
        return min((uint2)(saturate(uv) * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
    }

    float4 Temporal(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        float2 uv = v.positionCS.xy * _NFX_TargetSize.zw;
        uint2 pixel = FullPixel(uv);
        float2 motion;
        DecodeMotionVector(LOAD_TEXTURE2D_X(_CameraMotionVectorsTexture, pixel), motion);
        float2 prevUV = uv - motion;
        // _NFX_ShadowCompose.x > 0.5: the new frame is one frame old (the DXR 1.2 device's sun shadows): it is read
        // where the pixel was then, like the history (newly uncovered pixels take what is there for a frame)
        float2 currentUV = _NFX_ShadowCompose.x > 0.5 && all(prevUV >= 0.0) && all(prevUV <= 1.0) ? prevUV : uv;
        float4 current = SampleRT(_NFX_ShadowTex, currentUV);
        if (_NFX_ShadowCompose.z >= 0.999 || any(prevUV < 0.0) || any(prevUV > 1.0))
            return current;

        // clamp the history to what the new frame's neighbourhood allows (no ghost shadows when things move)
        float4 lo = 1e4, hi = 0.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float4 c = SampleRT(_NFX_ShadowTex, currentUV + float2(x, y) * _NFX_TargetSize.zw);
                lo = min(lo, c);
                hi = max(hi, c);
            }
        }
        float4 history = clamp(SampleRT(_NFX_HistoryTex, prevUV), lo, hi);
        // where nothing moves on screen the history is as good as new: it builds up over more frames there
        // (_NFX_ShadowCompose.w the share of the blend kept when still; 1 for the sun, whose shadows move with cars
        // over still ground)
        float moved = length(motion * _ScreenSize.xy);
        float blend = _NFX_ShadowCompose.z * lerp(_NFX_ShadowCompose.w, 1.0, saturate(moved * 0.5));
        return lerp(history, current, blend);
    }

    // How far a neighbour lies off the centre's surface plane, against a tolerance growing with distance: 1 on the
    // same surface, towards 0 off it. A plain depth difference rejected every neighbour above and below on a floor
    // seen at a grazing angle (its depth changes fast down the screen) and blurred only sideways: streaks.
    float PlaneWeight(float3 centerPos, float3 centerNormal, float centerDepth, uint2 sp)
    {
        float deviceDepth = LoadCameraDepth(sp);
        if (deviceDepth == UNITY_RAW_FAR_CLIP_VALUE)
            return 0.0;
        float3 p = ComputeWorldSpacePosition((sp + 0.5) * _ScreenSize.zw, deviceDepth, UNITY_MATRIX_I_VP);
        float off = abs(dot(centerNormal, p - centerPos));
        return exp(-off / max(centerDepth * 0.01, 0.02));
    }

    // a-trous blur along surfaces (plane and normal weights): smooths the ambient light
    float4 Denoise(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        float2 uv = v.positionCS.xy * _NFX_TargetSize.zw;
        uint2 pixel = FullPixel(uv);
        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return 1.0;
        float centerDepth = LinearEyeDepth(depth, _ZBufferParams);
        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        float3 centerPos = ComputeWorldSpacePosition((pixel + 0.5) * _ScreenSize.zw, depth, UNITY_MATRIX_I_VP);

        // the 3 x 3 taps first: their luminance spread says how noisy the signal is here (SVGF's edge stopping on
        // luminance, with the variance taken from the neighbourhood instead of a temporal moment buffer)
        float4 taps[9];
        float lumSum = 0.0, lumSq = 0.0;
        [unroll]
        for (int t = 0; t < 9; ++t)
        {
            float2 suv = uv + float2(t % 3 - 1, t / 3 - 1) * _NFX_DenoiseStep * _NFX_TargetSize.zw;
            taps[t] = SampleRT(_NFX_SourceTex, suv);
            float l = dot(taps[t].rgb, float3(0.2126, 0.7152, 0.0722));
            lumSum += l;
            lumSq += l * l;
        }
        float mean = lumSum / 9.0;
        float sigma = sqrt(max(lumSq / 9.0 - mean * mean, 0.0));
        float centerLum = dot(taps[4].rgb, float3(0.2126, 0.7152, 0.0722));
        bool guided = _NFX_DenoiseMode > 0.5;

        float4 sum = 0.0;
        float weights = 0.0;
        [unroll]
        for (int i = 0; i < 9; ++i)
        {
            int x = i % 3 - 1;
            int y = i / 3 - 1;
            float2 suv = uv + float2(x, y) * _NFX_DenoiseStep * _NFX_TargetSize.zw;
            uint2 sp = FullPixel(suv);
            NormalData snd;
            DecodeFromNormalBuffer(sp, snd);
            float w = PlaneWeight(centerPos, nd.normalWS, centerDepth, sp)
                      * pow(saturate(dot(nd.normalWS, snd.normalWS)), 32.0)
                      * ((x == 0 && y == 0) ? 1.0 : !guided ? 0.6 : (x == 0 || y == 0) ? 0.5 : 0.25); // guided: B-spline like
            if (guided && _NFX_DenoiseStep < 2.5)
            {
                // values far from the centre in units of the local noise belong to another feature (a contact
                // shadow's edge): kept apart; where the rays agree the blur is light, where they scatter it is full.
                // Only in the first, narrow steps: the wider ones see an already smoothed signal whose small spread
                // would stop them everywhere (the noise then stayed).
                float lum = dot(taps[i].rgb, float3(0.2126, 0.7152, 0.0722));
                w *= exp(-abs(lum - centerLum) / (4.0 * sigma + 1e-3 + 0.2 * abs(centerLum)));
            }
            sum += taps[i] * w;
            weights += w;
        }
        return sum / max(weights, 1e-4);
    }

    // Sun visibility blur sized by the penumbra: a blocker at distance d widens the shadow edge by about
    // d * 2 tan(sun radius). Near a contact the edge stays sharp (a fixed blur made every shadow soft and
    // blotchy), further away it softens as much as the real sun does.
    float4 DenoiseSun(Varyings v) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        float2 uv = v.positionCS.xy * _NFX_TargetSize.zw;
        uint2 pixel = FullPixel(uv);
        float4 center = SampleRT(_NFX_SourceTex, uv);
        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
            return center;
        float centerDepth = LinearEyeDepth(depth, _ZBufferParams);

        // mean blocker distance of the blocked samples around (g holds the distance times the blocked share)
        float blocked = 0.0, distanceSum = 0.0;
        [unroll]
        for (int j = -1; j <= 1; ++j)
        {
            [unroll]
            for (int i = -1; i <= 1; ++i)
            {
                float4 c = SampleRT(_NFX_SourceTex, uv + float2(i, j) * _NFX_DenoiseStep * _NFX_TargetSize.zw);
                blocked += 1.0 - saturate(c.r);
                distanceSum += c.g;
            }
        }
        if (blocked < 1e-3)
            return center; // lit all around: nothing to soften
        float blocker = distanceSum / blocked;
        float penumbraPixels = blocker * _NFX_SunFilter.x / max(centerDepth * _NFX_SunFilter.y, 1e-6);
        float sigma = clamp(penumbraPixels * 0.5, 0.3, 6.0);

        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        float3 centerPos = ComputeWorldSpacePosition((pixel + 0.5) * _ScreenSize.zw, depth, UNITY_MATRIX_I_VP);
        float4 sum = 0.0;
        float weights = 0.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float2 offset = float2(x, y) * _NFX_DenoiseStep;
                float2 suv = uv + offset * _NFX_TargetSize.zw;
                uint2 sp = FullPixel(suv);
                NormalData snd;
                DecodeFromNormalBuffer(sp, snd);
                float w = PlaneWeight(centerPos, nd.normalWS, centerDepth, sp)
                          * pow(saturate(dot(nd.normalWS, snd.normalWS)), 32.0)
                          * exp(-dot(offset, offset) / (2.0 * sigma * sigma));
                sum += SampleRT(_NFX_SourceTex, suv) * w;
                weights += w;
            }
        }
        return sum / max(weights, 1e-4);
    }

    // joint bilateral upsample: the 4 nearest trace texels weighted by depth similarity
    float4 Upsample(int which, float4 lowSize, float2 uv, float centerDepth)
    {
        float2 lowPos = uv * lowSize.xy - 0.5;
        float2 base = floor(lowPos);
        float2 f = lowPos - base;
        float4 sum = 0.0;
        float wsum = 0.0;
        [unroll]
        for (int i = 0; i < 4; ++i)
        {
            float2 o = float2(i & 1, i >> 1);
            float2 luv = (base + o + 0.5) * lowSize.zw;
            float d = LinearEyeDepth(LoadCameraDepth(FullPixel(luv)), _ZBufferParams);
            float bil = (o.x ? f.x : 1.0 - f.x) * (o.y ? f.y : 1.0 - f.y);
            float w = bil * exp(-abs(d - centerDepth) / max(centerDepth * 0.02, 0.03)) + 1e-4;
            float4 value = which == 0 ? SampleRT(_NFX_SunTex, luv) : which == 1 ? SampleRT(_NFX_AmbientTex, luv)
                         : which == 2 ? SampleRT(_NFX_ShaftTex, luv) : SampleRT(_NFX_LocalTex, luv);
            sum += value * w;
            wsum += w;
        }
        // the lamp shadows (3) carry the light NatureFX's own lights add as a negative blocked share: clamped at zero,
        // a neon tube never lit the floor
        return which == 3 ? sum / wsum : max(sum / wsum, 0.0);
    }

    float4 CompositeSurface(Varyings v);
    float3 LeafGlow(uint2 pixel, float depth, bool sunLit, float surfaceW, float ndl, float3 L, float4 surface, float sunVisible);

    // HDRP's diffuse sky light for a normal (the probe's SH9 constants, as NFXNative's SampleSH9)
    float3 ProbeSH9(float3 n)
    {
        float4 vA = float4(n, 1.0);
        float3 x1 = float3(dot(_NFX_AmbientProbe[0], vA), dot(_NFX_AmbientProbe[1], vA), dot(_NFX_AmbientProbe[2], vA));
        float4 vB = n.xyzz * n.yzzx;
        float3 x2 = float3(dot(_NFX_AmbientProbe[3], vB), dot(_NFX_AmbientProbe[4], vB), dot(_NFX_AmbientProbe[5], vB));
        float vC = n.x * n.x - n.y * n.y;
        return max(x1 + x2 + _NFX_AmbientProbe[6].rgb * vC, 0.0);
    }

    // the shadowed and ambient-scaled image, plus the light the air scatters towards the camera
    float3 EstimateSunPart(uint2 pixel, float depth, float3 color, float3 n, float perceptualRoughness, float3 L, float ndl, float4 surface, bool known);

    // OwnLighting.Sun, where the exact sun light is not known (no G-buffer pass there: forward only materials): the
    // sun's light from the surface trace's colour, metalness and clear coat and the normal buffer's roughness: Lambert
    // and GGX (a clear coat's own sharp lobe over the base), at the sun's illuminance, pre-exposed
    float3 OwnSunLight(float3 n, float3 V, float3 L, float perceptualRoughness, float4 surface, bool known)
    {
        float ndl = saturate(dot(n, L));
        if (ndl <= 0.0)
            return 0.0;
        float nv = max(saturate(dot(n, V)), 1e-3);
        float3 H = normalize(L + V);
        float nh = saturate(dot(n, H));
        float vh = saturate(dot(V, H));
        bool coated = known && surface.a > 0.495;
        float metal = known ? saturate((surface.a - (coated ? 0.5 : 0.0)) / 0.49) : 0.0;
        float3 albedo = known ? surface.rgb : 0.3;
        float3 f0 = lerp(0.04, albedo, metal);
        float3 diffuse = albedo * (1.0 - metal) * INV_PI;
        float3 specular = 0.0;
        [unroll]
        for (int layer = 0; layer < 2; ++layer)
        {
            if (layer == 1 && !coated)
                break;
            // the sun's disc keeps the sharpest lobe from a point (HDRP clamps its roughness the same way)
            // the coat: NFX_RayTracing.hlsl's NFX_COAT_ROUGHNESS (0.04, not included here) and the disc's spread
            float r = layer == 0 ? max(perceptualRoughness, 0.05) : 0.07;
            float a = r * r;
            float a2 = a * a;
            float d = nh * nh * (a2 - 1.0) + 1.0;
            float D = a2 / (PI * d * d);
            float vis = 0.5 / max(ndl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(ndl * ndl * (1.0 - a2) + a2), 1e-4);
            float3 F = layer == 0 ? f0 + (1.0 - f0) * pow(1.0 - vh, 5.0) : 0.04 + 0.96 * pow(1.0 - vh, 5.0);
            if (layer == 1)
            {
                // the varnish's reflection over a base lit through it
                float fc = 0.04 + 0.96 * pow(1.0 - nv, 5.0);
                specular *= 1.0 - fc;
                diffuse *= 1.0 - fc;
            }
            specular += F * min(D * vis, 100.0);
        }
        return (diffuse + specular) * _NFX_RTSunColor.rgb * ndl * GetCurrentExposureMultiplier();
    }

    float4 Composite(Varyings v) : SV_Target
    {
        float4 result = CompositeSurface(v);
        // the own engine's 3D fog and sky: AtmospherePass, after this pass
        if (_NFX_LeafParams.y > 0.5 && _NFX_ShadowCompose.y < 0.5)
        {
            uint2 pixel = (uint2)v.positionCS.xy;
            float2 uv = (pixel + 0.5) * _ScreenSize.zw;
            float depth = LoadCameraDepth(pixel);
            // the sky already holds the light its air scatters (HDRP's sky and fog): adding 120 m more of it turned
            // the sky white, by night too. On a surface the light the air adds in front of it comes with the light the
            // same air takes away from it, or the whole picture gets a white veil.
            if (_NFX_ShaftParams.w > 0.5)
            {
                // the own engine's fog (Fog.hlsl): the air's light and transmittance over every pixel, the sky too
                // (its haze); rgb in nits, not yet exposed
                float linearDepth = depth != UNITY_RAW_FAR_CLIP_VALUE ? LinearEyeDepth(depth, _ZBufferParams) : 1e6;
                float4 fog = Upsample(2, _NFX_ShaftSize, uv, linearDepth);
                result.rgb = result.rgb * saturate(fog.a) + fog.rgb * GetCurrentExposureMultiplier();
            }
            else if (depth != UNITY_RAW_FAR_CLIP_VALUE)
            {
                float linearDepth = LinearEyeDepth(depth, _ZBufferParams);
                float transmittance = exp(-_NFX_ShaftParams.x * min(linearDepth, _NFX_ShaftParams.z));
                result.rgb = result.rgb * transmittance + Upsample(2, _NFX_ShaftSize, uv, linearDepth).rgb;
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ the own engine's sky
    // The same parameterisations as NFXNative's Sky.hlsli (keep them in step): Bruneton's for transmittance,
    // Hillaire's for the sky view (zenith split at the horizon, azimuth from the sun squared).
    static const float SkyGround = 6360000.0, SkyTop = 6460000.0;

    float3 SkySunTransmittance(float r, float mu)
    {
        float H = sqrt(SkyTop * SkyTop - SkyGround * SkyGround);
        float rho = sqrt(max(r * r - SkyGround * SkyGround, 0.0));
        float disc = r * r * (mu * mu - 1.0) + SkyTop * SkyTop;
        float d = max(-r * mu + sqrt(max(disc, 0.0)), 0.0);
        float dMin = SkyTop - r, dMax = rho + H;
        float2 uv = float2((d - dMin) / max(dMax - dMin, 1e-3), rho / H);
        return SAMPLE_TEXTURE2D_X_LOD(_NFX_SkyTransmittance, s_linear_clamp_sampler, uv, 0).rgb;
    }

    // dir: towards the sky; hdrpSky: what HDRP drew there (its stars and moon lit sky take over at night)
    float3 NativeSky(float3 dir, float3 hdrpSky, float2 uv)
    {
        float r = SkyGround + max(_NFX_SkyParams.z, 1.0);
        float cosBeta = sqrt(max(r * r - SkyGround * SkyGround, 0.0)) / r;
        float beta = acos(cosBeta);
        float zenithHorizon = PI - beta;
        float viewZenith = acos(clamp(dir.y, -1.0, 1.0));
        float2 screenUv = uv;
        bool aboveHorizon = dir.y >= -cosBeta;
        if (aboveHorizon)
        {
            float c = 1.0 - saturate(viewZenith / zenithHorizon);
            uv.y = (1.0 - sqrt(c)) * 0.5;
        }
        else
        {
            uv.y = sqrt(saturate((viewZenith - zenithHorizon) / beta)) * 0.5 + 0.5;
        }
        float3 sun = normalize(_NFX_RTSunDir.xyz);
        float2 dh = dir.xz, sh = sun.xz;
        float azimuthCos = dot(dh, dh) > 1e-8 && dot(sh, sh) > 1e-8 ? dot(normalize(dh), normalize(sh)) : 1.0;
        uv.x = sqrt(saturate(-azimuthCos * 0.5 + 0.5));
        float2 skyUv = uv;
        uv = screenUv;
        float3 sky = SAMPLE_TEXTURE2D_X_LOD(_NFX_SkyView, s_linear_clamp_sampler, skyUv, 0).rgb;
        // the clouds in front of the sky (and the sun)
        float4 clouds = _NFX_SkyParams.x > 1.5 ? SAMPLE_TEXTURE2D_X_LOD(_NFX_CloudTex, s_linear_clamp_sampler, uv, 0) : float4(0.0, 0.0, 0.0, 1.0);

        // the sun's disc: its illuminance over its solid angle, dimmed by the air, darker towards the limb
        float radius = max(_NFX_SkyParams.w, 0.0047);
        float cosSun = dot(dir, sun);
        float cosR = cos(radius);
        if (cosSun > cosR && aboveHorizon)
        {
            float edge = saturate((1.0 - cosSun) / (1.0 - cosR));
            float mu = sqrt(1.0 - edge);
            float limb = 1.0 - 0.6 * (1.0 - mu);
            sky += SkySunTransmittance(r, sun.y) * _NFX_SkyParams.y / (PI * radius * radius) * limb;
        }
        sky = sky * clouds.a + clouds.rgb;
        float3 color = sky * GetCurrentExposureMultiplier();
        // after dusk the stars and the moon lit sky of HDRP's own (MapNight) come through
        float night = smoothstep(-0.03, -0.12, sun.y);
        return min(color + hdrpSky * night * clouds.a, 60000.0);
    }

    float4 CompositeSurface(Varyings v)
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float2 uv = (pixel + 0.5) * _ScreenSize.zw;
        float3 color = LOAD_TEXTURE2D_X(_NFX_ColorTex, pixel).rgb;
        bool debug = _NFX_ShadowCompose.y > 0.5;
        float shadowStrength = _NFX_ShadowCompose.x;
        float ambientStrength = _NFX_ShadowCompose.w;

        float depth = LoadCameraDepth(pixel);
        if (depth == UNITY_RAW_FAR_CLIP_VALUE)
        {
            if (debug)
                return float4(0.25, 0.3, 0.4, 1.0); // debug: sky blue-grey
            if (_NFX_SkyParams.x > 0.5)
            {
                float3 dir = normalize(ComputeWorldSpacePosition(uv, UNITY_NEAR_CLIP_VALUE, UNITY_MATRIX_I_VP));
                color = NativeSky(dir, color, uv);
            }
            return float4(color, 1.0);
        }
        float linearDepth = LinearEyeDepth(depth, _ZBufferParams);
        float3 hdrpColor = color;   // what HDRP drew, before any of the changes below
        float3 nativeAdded = 0.0;   // light of the lamps only NatureFX has, where it gets through
        // local lights HDRP casts no shadows for: their blocked light comes off first (not part of the sun / ambient
        // split below)
        float4 surface = LOAD_TEXTURE2D_X(_NFX_SurfaceAlbedo, pixel);
        float surfaceW = LOAD_TEXTURE2D_X(_NFX_SurfaceNormal, pixel).w;
        bool known = surfaceW != 0.0;
        float3 pixelAlbedo = PixelAlbedo(pixel, known, surface.rgb);
        // the own BSDF (no G-buffer): what the own engine does not trace (the distant forest's cards, grass) is on
        // HDRP's lit layer (OwnLighting.UntracedLit) and holds HDRP's sun, lamps and sky light; with HDRP's sky light
        // off everything else is black but its glow. Such a pixel keeps HDRP's light (the sun's share shadowed by the
        // estimate) instead of the own light on a grey guess of its colour
        bool hdrpLit = _NFX_OwnBrdf > 0.5 && _NFX_OwnAmbient > 0.5 && !known && Luminance(hdrpColor) > 1e-4;
        if (_NFX_LeafParams.z > 0.5 && !debug)
        {
            float3 localDiffuse = pixelAlbedo * (1.0 - (known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0)) * INV_PI;
            // never more than 95 % of the pixel's brightness, scaled as a whole: clamping each channel took the warm
            // lamp light's red and green away and left blue speckles where a noisy sample overshot
            float3 blockedLight = Upsample(3, _NFX_LocalSize, uv, linearDepth).rgb;
            float3 removed = blockedLight * localDiffuse * GetCurrentExposureMultiplier();
            // step A: with HDRP's own lamp light known exactly, the blocked share of it comes off (its shine too), not
            // the blocked light on an estimated diffuse colour; lights only NatureFX has (a negative share) are added
            // as before
            // (the lamp masks' pass also runs without the G-buffer: the own BSDF, NFX_LampsOwn)
            float4 lampsExact = _NFX_SunExact.x > 0.5 || _NFX_SunExact.y > 0.5 ? LOAD_TEXTURE2D_X(_NFX_LampsExactTex, pixel) : 0.0;
            if (_NFX_SunExact.y > 0.5)
            {
                // A4: each of HDRP's lamps shadowed by its own mask (NFX_SunExact LampsExact: the light it takes away);
                // the traced lamp shadows then hold only the lights NatureFX adds (a negative blocked share)
                nativeAdded = max(-removed, 0.0);
                removed = (lampsExact.a > 0.5 ? max(lampsExact.rgb, 0.0) : 0.0) + min(removed, 0.0);
            }
            else if (lampsExact.a > 0.5 && Luminance(blockedLight) > 0.0)
            {
                NormalData lampNormal;
                DecodeFromNormalBuffer(pixel, lampNormal);
                PositionInputs lp = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
                float total = LampsLuminance(GetAbsolutePositionWS(lp.positionWS), lampNormal.normalWS);
                float share = total > 1e-6 ? saturate(Luminance(blockedLight) / total) : 0.0;
                removed = max(lampsExact.rgb, 0.0) * share;
            }
            if (any(!isfinite(removed)))
                removed = 0.0;
            if (_NFX_OwnLamps > 0.5)
            {
                // OwnLighting.Lamps: HDRP's lamps added nothing; their lit share (the exact total less the blocked) is
                // added, or where the G-buffer has no pixel their unshadowed diffuse light; the lights only NatureFX has
                // (nativeAdded) as before
                float4 lampsAllExact = LOAD_TEXTURE2D_X(_NFX_LampsTotalTex, pixel);
                float3 lampsLitHere;
                if (lampsAllExact.a > 0.5)
                {
                    lampsLitHere = max(lampsAllExact.rgb - (lampsExact.a > 0.5 ? max(lampsExact.rgb, 0.0) : 0.0), 0.0);
                }
                else
                {
                    NormalData ln;
                    DecodeFromNormalBuffer(pixel, ln);
                    PositionInputs lq = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
                    float lampMetal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
                    lampsLitHere = OwnLampsLight(GetAbsolutePositionWS(lq.positionWS), ln.normalWS, pixelAlbedo * (1.0 - lampMetal));
                }
                color = color + (hdrpLit ? (float3)0.0 : lampsLitHere) + nativeAdded;
                removed = 0.0;
            }
            float removedLum = Luminance(removed);
            if (removedLum > 0.0)
                removed *= min(1.0, 0.95 * Luminance(color) / removedLum);
            color = max(color - removed, 0.0);
        }

        // the sun at full resolution is read as is: an upsample would soften its sharp edges again
        float sunVisible = 1.0;
        if (shadowStrength > 0.0 || _NFX_LeafParams.x > 0.0)
        {
            sunVisible = all(_NFX_SunSize.xy >= _ScreenSize.xy - 0.5)
                ? SampleRT(_NFX_SunTex, uv).r
                : Upsample(0, _NFX_SunSize, uv, linearDepth).r;
            sunVisible = saturate(sunVisible);
        }
        if (_NFX_CloudShadowParams.w > 0.5 && _NFX_RTSunDir.w > 0.0)
        {
            PositionInputs cp = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
            sunVisible *= CloudShadowAt(GetAbsolutePositionWS(cp.positionWS));
        }
        if (_NFX_SmokeShadowS.w > 0.5 && _NFX_RTSunDir.w > 0.0)
        {
            // the tyre smoke between the surface and the sun
            PositionInputs sp = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
            sunVisible *= NFX_SmokeTransmittance(GetAbsolutePositionWS(sp.positionWS));
        }
        // the ambient texture: one bounce ambient light, rgb relative to HDRP's; or the own engine's GI
        // (_NFX_LeafParams.w > 0, its strength): rgb the light bounced in, a the share of the sky seen. HDRP's
        // ambient light is then only darkened by the sky's share, and the bounced light added on the surface's colour.
        float3 ambientFactor = 1.0;
        float3 bounceLight = 0.0;
        if (ambientStrength > 0.0)
        {
            float4 ambient = Upsample(1, _NFX_AmbientSize, uv, linearDepth);
            if (_NFX_LeafParams.w > 0.0)
            {
                ambientFactor = lerp(1.0, saturate(ambient.a), ambientStrength);
                float diffuseMetal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
                float3 diffuseAlbedo = pixelAlbedo * (1.0 - diffuseMetal);
                bounceLight = diffuseAlbedo * max(ambient.rgb, 0.0) * GetCurrentExposureMultiplier() * _NFX_LeafParams.w * ambientStrength;
            }
            else
            {
                ambientFactor = lerp(1.0, ambient.rgb, ambientStrength);
            }
        }

        float3 L = _NFX_RTSunDir.xyz;
        NormalData nd;
        DecodeFromNormalBuffer(pixel, nd);
        float3 n = nd.normalWS;
        float ndl = dot(n, L);
        bool sunLit = _NFX_RTSunDir.w > 0.0 && ndl > 0.0;
        // phase 1c: the sky's light on this surface from the own engine's sky (clouds included) in place of HDRP's
        // probe; the difference, scaled by how much of the sky the surface sees like the rest of its sky light
        float3 skyDelta = 0.0;
        if (_NFX_SkyAmbientOn > 0.0)
        {
            float skyMetal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
            float3 skyAlbedo = pixelAlbedo * (1.0 - skyMetal);
            skyDelta = skyAlbedo * (SkyProbeSH9(n) - ProbeSH9(n)) * GetCurrentExposureMultiplier() * ambientFactor * _NFX_SkyAmbientOn;
        }
        // OwnLighting: HDRP lit nothing with its sky; the own sky's light on the surface's colour, through the GI's sky
        // share (ambientFactor), is added in each of the ways below instead of darkening HDRP's
        bool ownAmbient = _NFX_OwnAmbient > 0.5;
        float3 ownSky = 0.0;
        if (ownAmbient && !hdrpLit)
        {
            float ownMetal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
            float3 ownAlbedo = pixelAlbedo * (1.0 - ownMetal);
            ownSky = ownAlbedo * ProbeSH9(n) * GetCurrentExposureMultiplier() * ambientFactor * _NFX_OwnSkyStrength;
            skyDelta = 0.0;
        }
        // the sun light HDRP added here, worked out from the G-buffer as HDRP does (a = 0 where it is not known)
        float4 exact = _NFX_SunExact.x > 0.5 ? LOAD_TEXTURE2D_X(_NFX_SunExactTex, pixel) : 0.0;
        bool exactKnown = exact.a > 0.5;
        if (_NFX_ShadowCompose.y > 3.5)
        {
            // check: the albedo (the G-buffer's base colour) as flat grey-scaled colour, lit evenly, against what real
            // materials reflect: red over 0.8 (only fresh snow and white paint), blue under 0.03 (charcoal, fresh
            // asphalt at most), magenta where a bright colour is more saturated than nature's (leaves, soil and stone
            // keep well under it); the rest as it is
            float3 a = pixelAlbedo;
            float y = dot(a, float3(0.2126, 0.7152, 0.0722));
            float hi = max(a.r, max(a.g, a.b)), lo = min(a.r, min(a.g, a.b));
            float chroma = hi > 1e-4 ? (hi - lo) / hi : 0.0;
            float3 flag = y > 0.8 ? float3(1.0, 0.1, 0.1) : y < 0.03 ? float3(0.1, 0.3, 1.0) : chroma > 0.85 && y > 0.05 ? float3(1.0, 0.1, 1.0) : -1.0;
            float3 shown = flag.x >= 0.0 ? lerp(a, flag, 0.7) : a;
            // the picture is pre-exposed: the albedo itself shows about as it is (0.18 as mid grey)
            return float4(shown, 1.0);
        }
        if (_NFX_ShadowCompose.y > 2.5)
        {
            // check: the exact sun light alone, dark magenta where it is not known (the estimate is used there)
            return float4(exactKnown ? exact.rgb : float3(0.3, 0.0, 0.3), 1.0);
        }
        if (_NFX_ShadowCompose.y > 1.5)
        {
            // GI alone: the ambient light relative to HDRP's, mid grey where it is unchanged; darker where the
            // surroundings block the sky, brighter and tinted where light bounces in (a red car tints the road red)
            return float4(ambientFactor * 0.5 + bounceLight, 1.0);
        }
        if (debug)
        {
            // white lit, black in the traced shadow, darker where the ambient light is blocked and coloured
            // by bounce light; tinted blue where the surface faces away from the sun
            float3 g = lerp(0.05, 0.9, saturate((sunLit ? sunVisible : 1.0) * ambientFactor));
            return float4(sunLit ? g : g * float3(0.75, 0.85, 1.0), 1.0);
        }

        // A4, the full relight: the pixel taken apart exactly and put together again from the own engine's light.
        // HDRP's image is its G-buffer's baked light (the sky light on the surface and its glow), the sun, the lamps
        // and the rest (reflections, mostly); the sun and the lamps (known exactly) get the traced shadows, the sky
        // light the traced sky share and GI and the own sky, the glow stays, the rest takes the sky share as its
        // occlusion where GI does not run (the reflections pass replaces what it traces).
        if (_NFX_SunExact.z > 0.5 && exactKnown)
        {
            float3 g3 = LOAD_TEXTURE2D_X(_NFX_GBuffer3Tex, pixel).rgb;
            // HDRP's tag for "no baked light, AO stored here" (Lit.hlsl AO_IN_GBUFFER3_TAG)
            if (g3.x > 2047.0 && g3.z > 1023.0)
                g3 = 0.0;
            float metal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
            float3 albedoD = pixelAlbedo * (1.0 - metal);
            // the sky light HDRP baked in, as its probe on the surface's colour, never more than G3 holds; the rest
            // of G3 is the glow
            float3 bakeEstimate = albedoD * ProbeSH9(n) * GetCurrentExposureMultiplier();
            // (none with OwnLighting: HDRP's indirect diffuse multiplier, which scales the baked light too, is 0, and
            // all of G3 is the glow)
            float3 bake = ownAmbient ? (float3)0.0 : bakeEstimate * min(1.0, Luminance(g3) / max(Luminance(bakeEstimate), 1e-6));
            float3 glow = max(g3 - bake, 0.0);
            float4 lampsTotal = _NFX_SunExact.y > 0.5 ? LOAD_TEXTURE2D_X(_NFX_LampsTotalTex, pixel) : 0.0;
            float4 lampsBlocked = _NFX_SunExact.y > 0.5 ? LOAD_TEXTURE2D_X(_NFX_LampsExactTex, pixel) : 0.0;
            float3 lampsAll = lampsTotal.a > 0.5 ? max(lampsTotal.rgb, 0.0) : 0.0;
            float3 lampsLit = max(lampsAll - (lampsBlocked.a > 0.5 ? max(lampsBlocked.rgb, 0.0) : 0.0), 0.0);
            float3 sunAll = max(exact.rgb, 0.0);
            // (with OwnLighting.Sun HDRP added no sun: nothing of it to take away)
            float3 restLight = max(hdrpColor - g3 - (_NFX_OwnSun > 0.5 ? (float3)0.0 : sunAll) - (_NFX_OwnLamps > 0.5 ? (float3)0.0 : lampsAll), 0.0);
            float sunShade = 1.0 - (1.0 - sunVisible) * shadowStrength;
            // the sky's light from the sky as it is now (HDRP's probe is the own engine's sky, A3), not what the G-buffer
            // baked: a track's lightmaps were baked under its own weak sun (Units) and left the shade black
            float3 skyLight = albedoD * ProbeSH9(n) * GetCurrentExposureMultiplier() * ambientFactor * (ownAmbient ? _NFX_OwnSkyStrength : 1.0);
            float3 relit = glow + skyLight + skyDelta + sunAll * sunShade + lampsLit + nativeAdded
                         // the rest is mostly HDRP's reflection of the sky, which it mirrors under any roof (its probes
                         // know no garage): only as much as the sky is seen from here (with the sun at full strength
                         // a covered floor shone with the blue sky)
                         + restLight * ambientFactor + bounceLight;
            return float4(max(relit, 0.0) + LeafGlow(pixel, depth, sunLit, surfaceW, ndl, L, surface, sunVisible), 1.0);
        }

        // Without the ray traced shadows HDRP's shadow maps already darken the sun light, so the sun cannot be told
        // apart from the rest: the ambient light scales all of it, a little less to spare the sunlit share.
        // Facing away from the sun, all of the pixel's light is ambient.
        // GI: the sky's share scales only the sky light HDRP gave (its probe on the surface's colour), not the lamps,
        // glows and reflections in the rest (scaling all of it put out the lamps of a garage, whose sky share is ~0)
        bool gi = _NFX_LeafParams.w > 0.0 && ambientStrength > 0.0;
        float3 skyPart = 0.0;
        if (gi)
        {
            float diffuseMetal = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
            float3 diffuseAlbedo = pixelAlbedo * (1.0 - diffuseMetal);
            skyPart = diffuseAlbedo * ProbeSH9(n) * GetCurrentExposureMultiplier();
        }
        // OwnLighting.Sun: HDRP's pixel holds no sun light; the exact one (or the own BSDF where the G-buffer has no
        // pixel) is added under the traced shadow instead of taking the shadowed share away
        bool ownSun = _NFX_OwnSun > 0.5 && !hdrpLit;
        float3 ownSunLight = 0.0;
        if (ownSun && _NFX_RTSunDir.w > 0.0)
        {
            PositionInputs ownSunPos = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
            float3 ownSunView = GetWorldSpaceNormalizeViewDir(ownSunPos.positionWS);
            ownSunLight = exactKnown ? max(exact.rgb, (float3)0.0) : OwnSunLight(n, ownSunView, L, nd.perceptualRoughness, surface, known);
        }
        if (shadowStrength <= 0.0)
        {
            if (ownSun)
                color += ownSunLight;
            if (ownAmbient)
                return float4(max(color + ownSky + bounceLight, 0.0), 1.0);
            if (gi)
            {
                skyPart *= min(1.0, Luminance(color) / max(Luminance(skyPart), 1e-6));
                // not below zero: the sky share is clamped by brightness, a channel of it can still exceed the pixel's
                return float4(max(max(color - skyPart * (1.0 - ambientFactor), 0.0) + bounceLight + skyDelta, 0.0), 1.0);
            }
            return float4(max(color * lerp(1.0, ambientFactor, 0.6) + bounceLight + skyDelta, 0.0), 1.0);
        }

        // split the pixel into the sun's share and the rest (sky, bounce, reflections); shadow the first, scale the
        // second. Facing away from the sun the share is zero, except where HDRP lets the sun through (transmission).
        float3 sunPart = 0.0;
        if (ownSun)
        {
            sunPart = ownSunLight; // added below under the shadow; the rest is all of HDRP's pixel
        }
        else if (exactKnown)
        {
            // exact: what HDRP added, never more than the pixel has (local light shadows came off first). Scaled as a
            // whole, like the lamp light above: clamped per channel, a sun brighter in one channel than the pixel
            // (HDRP's contact shadows and direct AO are not in the exact pass) lost that channel and shifted the hue
            float3 exactSun = max(exact.rgb, 0.0);
            float exactLum = Luminance(exactSun);
            sunPart = exactLum > 0.0 ? exactSun * min(1.0, 0.95 * Luminance(color) / exactLum) : 0.0;
        }
        else if (sunLit)
        {
            sunPart = EstimateSunPart(pixel, depth, color, n, nd.perceptualRoughness, L, ndl, surface, known);
        }
        float sunFactor = 1.0 - (1.0 - sunVisible) * shadowStrength;
        float3 rest = ownSun ? color : max(color - sunPart, 0.0);
        float3 result;
        if (ownAmbient)
        {
            // HDRP's pixel holds no sky light: the rest (lamps, glow, highlights) as it is, the own sky added
            result = sunPart * sunFactor + rest + ownSky + bounceLight;
        }
        else if (gi)
        {
            skyPart *= min(1.0, Luminance(rest) / max(Luminance(skyPart), 1e-6));
            result = sunPart * sunFactor + max(rest - skyPart * (1.0 - ambientFactor), 0.0) + bounceLight;
        }
        else
        {
            result = sunPart * sunFactor + rest * ambientFactor + bounceLight;
        }
        result = max(result + skyDelta, 0.0);
        return float4(result + LeafGlow(pixel, depth, sunLit, surfaceW, ndl, L, surface, sunVisible), 1.0);
    }

    // A leaf lit from behind glows: sunlight passes through the thin blade, tinted twice by its colour, brightest when
    // looking towards the sun. HDRP lights it from the front only.
    float3 LeafGlow(uint2 pixel, float depth, bool sunLit, float surfaceW, float ndl, float3 L, float4 surface, float sunVisible)
    {
        if (sunLit || surfaceW >= 0.0 || _NFX_RTSunDir.w <= 0.0 || _NFX_LeafParams.x <= 0.0)
            return 0.0;
        PositionInputs lp = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float3 lv = GetWorldSpaceNormalizeViewDir(lp.positionWS);
        float forward = pow(saturate(dot(lv, -L)), 6.0);
        float3 tint = surface.rgb * saturate(surface.rgb * 2.0);
        float3 through = tint * _NFX_RTSunColor.rgb * INV_PI * (0.3 + 1.7 * forward) * saturate(-ndl * 0.7 + 0.3);
        return through * sunVisible * _NFX_LeafParams.x * GetCurrentExposureMultiplier();
    }

    // The sun's share of a pixel where the exact sun light is not known (no G-buffer there): the direct sun light
    // HDRP added (Lambert diffuse plus GGX specular, as close to HDRP/Lit as this gets), with the colour and metalness
    // of the surface from the camera ray of the Surface trace. The share is one fraction of the pixel's level, not per
    // channel: where the estimate overshot one channel (red paint under HDRP's bright sky reflection) clamping per
    // channel took the red away and left the shaded paint green.
    float3 EstimateSunPart(uint2 pixel, float depth, float3 color, float3 n, float perceptualRoughness, float3 L, float ndl, float4 surface, bool known)
    {
        float3 albedo = known ? surface.rgb : 0.3;
        // metalness in the low 7 bits of alpha (metallic * 0.49), the clear coat flag above
        float metallic = known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0;
        float3 f0 = lerp(0.04, albedo, metallic);

        PositionInputs pos = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
        float3 V = GetWorldSpaceNormalizeViewDir(pos.positionWS);
        float nv = saturate(dot(n, V));
        float alpha = max(perceptualRoughness * perceptualRoughness, 0.002);
        float a2 = alpha * alpha;
        float3 H = normalize(L + V);
        float nh = saturate(dot(n, H));
        float d = nh * nh * (a2 - 1.0) + 1.0;
        float distribution = a2 / (PI * d * d);
        float visibilityTerm = 0.5 / max(ndl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(ndl * ndl * (1.0 - a2) + a2), 1e-4);
        float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(V, H)), 5.0);
        float3 diffuse = albedo * (1.0 - metallic) * INV_PI;
        float3 direct = (diffuse + fresnel * distribution * visibilityTerm) * _NFX_RTSunColor.rgb * ndl * GetCurrentExposureMultiplier();

        float level = dot(color, float3(0.2126, 0.7152, 0.0722));
        return color * min(dot(direct, float3(0.2126, 0.7152, 0.0722)) / max(level, 1e-6), 0.95);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Temporal"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Temporal
            ENDHLSL
        }

        Pass
        {
            Name "Denoise"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Denoise
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Composite
            ENDHLSL
        }

        Pass
        {
            Name "DenoiseSun"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DenoiseSun
            ENDHLSL
        }
    }
}

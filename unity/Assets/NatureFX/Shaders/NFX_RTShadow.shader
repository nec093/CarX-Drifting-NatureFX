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
    float4 _NFX_LocalSize;            // local light shadow trace resolution
    float4 _NFX_ShadowCompose;        // x: shadow strength (0: not traced), y: 1 = debug view, 2 = GI debug view, 3 = exact sun light, z: share of the new frame, w: ambient strength
                                      // (_NFX_LeafParams w: own engine GI bounce strength, 0 = one bounce ambient light)
    float4 _NFX_SunFilter;            // x: 2 tan(sun angular radius), y: angle one sun trace pixel covers (radians)
    float4 _NFX_RTSunDir;             // xyz: direction towards the sun, w: 1 when the sun is up
    float4 _NFX_RTSunColor;           // rgb: sun illuminance at the ground (lux)
    StructuredBuffer<float4> _NFX_AmbientProbe; // HDRP's diffuse sky probe (7 float4, the SH9 constants)
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
        return max(sum / wsum, 0.0);
    }

    float4 CompositeSurface(Varyings v);

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

    float4 Composite(Varyings v) : SV_Target
    {
        float4 result = CompositeSurface(v);
        if (_NFX_LeafParams.y > 0.5 && _NFX_ShadowCompose.y < 0.5)
        {
            uint2 pixel = (uint2)v.positionCS.xy;
            float2 uv = (pixel + 0.5) * _ScreenSize.zw;
            float depth = LoadCameraDepth(pixel);
            float linearDepth = depth == UNITY_RAW_FAR_CLIP_VALUE ? 1e6 : LinearEyeDepth(depth, _ZBufferParams);
            result.rgb += Upsample(2, _NFX_ShaftSize, uv, linearDepth).rgb;
        }
        return result;
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
            return float4(debug ? float3(0.25, 0.3, 0.4) : color, 1.0); // debug: sky blue-grey
        float linearDepth = LinearEyeDepth(depth, _ZBufferParams);
        // local lights HDRP casts no shadows for: their blocked light comes off first (not part of the sun / ambient
        // split below)
        float4 surface = LOAD_TEXTURE2D_X(_NFX_SurfaceAlbedo, pixel);
        float surfaceW = LOAD_TEXTURE2D_X(_NFX_SurfaceNormal, pixel).w;
        bool known = surfaceW != 0.0;
        if (_NFX_LeafParams.z > 0.5 && !debug)
        {
            float3 localDiffuse = (known ? surface.rgb : 0.3) * (1.0 - (known ? saturate((surface.a - (surface.a > 0.495 ? 0.5 : 0.0)) / 0.49) : 0.0)) * INV_PI;
            // never more than 95 % of the pixel's brightness, scaled as a whole: clamping each channel took the warm
            // lamp light's red and green away and left blue speckles where a noisy sample overshot
            float3 removed = Upsample(3, _NFX_LocalSize, uv, linearDepth).rgb * localDiffuse * GetCurrentExposureMultiplier();
            if (any(!isfinite(removed)))
                removed = 0.0;
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
                float3 diffuseAlbedo = (known ? surface.rgb : 0.3) * (1.0 - diffuseMetal);
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
        // the sun light HDRP added here, worked out from the G-buffer as HDRP does (a = 0 where it is not known)
        float4 exact = _NFX_SunExact.x > 0.5 ? LOAD_TEXTURE2D_X(_NFX_SunExactTex, pixel) : 0.0;
        bool exactKnown = exact.a > 0.5;
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
            float3 diffuseAlbedo = (known ? surface.rgb : 0.3) * (1.0 - diffuseMetal);
            skyPart = diffuseAlbedo * ProbeSH9(n) * GetCurrentExposureMultiplier();
        }
        if (shadowStrength <= 0.0)
        {
            if (gi)
            {
                skyPart *= min(1.0, Luminance(color) / max(Luminance(skyPart), 1e-6));
                return float4(color - skyPart * (1.0 - ambientFactor) + bounceLight, 1.0);
            }
            return float4(color * lerp(1.0, ambientFactor, 0.6) + bounceLight, 1.0);
        }

        // split the pixel into the sun's share and the rest (sky, bounce, reflections); shadow the first, scale the
        // second. Facing away from the sun the share is zero, except where HDRP lets the sun through (transmission).
        float3 sunPart = 0.0;
        if (exactKnown)
        {
            // exact: what HDRP added, per channel (never more than the pixel has: local light shadows came off first)
            sunPart = min(exact.rgb, color);
        }
        else if (sunLit)
        {
            sunPart = EstimateSunPart(pixel, depth, color, n, nd.perceptualRoughness, L, ndl, surface, known);
        }
        float sunFactor = 1.0 - (1.0 - sunVisible) * shadowStrength;
        float3 rest = max(color - sunPart, 0.0);
        float3 result;
        if (gi)
        {
            skyPart *= min(1.0, Luminance(rest) / max(Luminance(skyPart), 1e-6));
            result = sunPart * sunFactor + rest - skyPart * (1.0 - ambientFactor) + bounceLight;
        }
        else
        {
            result = sunPart * sunFactor + rest * ambientFactor + bounceLight;
        }
        // A leaf lit from behind glows: sunlight passes through the thin blade, tinted twice by its colour,
        // brightest when looking towards the sun. HDRP lights it from the front only.
        if (!sunLit && surfaceW < 0.0 && _NFX_RTSunDir.w > 0.0 && _NFX_LeafParams.x > 0.0)
        {
            PositionInputs lp = GetPositionInput(pixel, _ScreenSize.zw, depth, UNITY_MATRIX_I_VP, UNITY_MATRIX_V);
            float3 lv = GetWorldSpaceNormalizeViewDir(lp.positionWS);
            float forward = pow(saturate(dot(lv, -L)), 6.0);
            float3 tint = surface.rgb * saturate(surface.rgb * 2.0);
            float3 through = tint * _NFX_RTSunColor.rgb * INV_PI * (0.3 + 1.7 * forward) * saturate(-ndl * 0.7 + 0.3);
            result += through * sunVisible * _NFX_LeafParams.x * GetCurrentExposureMultiplier();
        }
        return float4(result, 1.0);
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

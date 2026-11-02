// NatureFX post-processing (roadmap D, PostProcess.cs): what HDRP's exposure, bloom, tonemapping and colour
// adjustments did, NatureFX's own, on the HDR colour before HDRP's post-processing (which then only does TAA, depth of
// field, motion blur and lens flares, on the finished picture: its tonemapping is off).
//   Meter: the scene's luminance (cd/m², the colour over the exposure) as log2 on a 64 x 36 grid; PostProcess.cs reads
//   it back and sets the exposure (eye adaptation) as HDRP's fixed exposure.
//   Bloom: the light a real lens and eye scatter around bright things: a mip chain of the image (13 tap down, tent up;
//   the first down averages by brightness so single bright pixels do not flicker), blended in as a small share of all
//   the light (energy kept, no threshold: a lamp spreads its own light, a grey wall almost none).
//   Flare (in Final): the lens's ghosts of the bright lights in view, the sun or the moon and the nearest lamps
//   (ParticleLighting's list): each a few faint, nearly neutral images of the lens's aperture (mostly round and soft,
//   one hexagon, their rims split by dispersion) along the line from the light through the picture's centre, as bright
//   as the light shows on screen (hidden behind a wall: none) and fewer towards the picture's edge (vignetting); a soft
//   glow close round the light, and short soft diffraction rays.
//   Final: bloom, the flare, the exposure compensation, the tone curve (ACES fitted, Khronos PBR Neutral, AgX, none), then contrast
//   and saturation on the display picture.
//   MotionBlur: the light gathered along the path each pixel moved while the shutter was open (its motion to the previous
//   frame times the shutter's share of a frame), a nearer surface that hardly moves (the chased car over a streaming
//   road) kept out of what moves behind it.
//   DepthOfField: a thin lens: each pixel's circle of confusion from its distance, the focus distance, the focal length
//   (from the field of view on a 24 mm high sensor) and the f-number, gathered over a disc; a blurred nearer surface
//   spreads over what is behind it, a blurred farther one never over a sharper nearer one.
Shader "Hidden/NatureFX/Post"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_NFX_PostSource);   // what is read (the colour, or the bloom level above)
    TEXTURE2D_X(_NFX_PostLow);      // bloom up: the smaller level's result
    TEXTURE2D_X(_NFX_PostBloom);    // final: the bloom (half resolution)
    float4 _NFX_PostSourceSize;     // the texture read: xy size, zw 1 / size
    float4 _NFX_PostTargetSize;     // the target: xy size, zw 1 / size
    float4 _NFX_PostParams;         // x bloom share, y exposure compensation (EV), z contrast (-1..1), w saturation (-1..1)
    float4 _NFX_PostMode;           // x tone curve: 0 ACES, 1 Neutral, 2 AgX, 3 none; y bloom levels
    float4 _NFX_PostWhite;          // rgb the eye's white balance (gains on the scene's light, luminance kept)

    float _NFX_PostPlain;           // 1: the textures are the display resolution's own (DLSS upscaling), not scaled RTHandles

    #define SampleRT(tex, uv) SAMPLE_TEXTURE2D_X_LOD(tex, s_linear_clamp_sampler, (uv) * (_NFX_PostPlain > 0.5 ? float2(1.0, 1.0) : _RTHandleScale.xy), 0)

    float2 TargetUV(Varyings v)
    {
        return v.positionCS.xy * _NFX_PostTargetSize.zw;
    }

    // --- meter: each grid cell the mean of 4 x 4 samples' log2 luminance (cd/m²)
    // x the cell's mean log2 luminance; yzw the mean colour of what is not sky (cd/m²), 0 where all is sky: the light the
    // surfaces return, which the eye's white balance adapts to (PostProcess.WhiteBalance)
    float4 Meter(Varyings v) : SV_Target
    {
        float2 uv0 = (floor(v.positionCS.xy)) * _NFX_PostTargetSize.zw;
        float exposure = max(GetCurrentExposureMultiplier(), 1e-10);
        float sum = 0.0;
        float3 surfaces = 0.0;
        float count = 0.0;
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                float2 uv = uv0 + (float2(x, y) + 0.5) * 0.25 * _NFX_PostTargetSize.zw;
                float3 c = SampleRT(_NFX_PostSource, uv).rgb;
                float L = dot(c, float3(0.2126, 0.7152, 0.0722)) / exposure;
                sum += log2(max(L, 1e-5));
                uint2 px = min((uint2)(saturate(uv) * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
                if (LoadCameraDepth(px) != UNITY_RAW_FAR_CLIP_VALUE && all(isfinite(c)))
                {
                    surfaces += max(c, 0.0) / exposure;
                    count += 1.0;
                }
            }
        }
        return float4(sum / 16.0, count > 0.0 ? surfaces / count : (float3)0.0);
    }

    // --- bloom: 13 tap down (Jimenez 2014); the first level weights by 1 / (1 + luma) (Karis) against fireflies
    float Karis(float3 c)
    {
        return 1.0 / (1.0 + dot(c, float3(0.2126, 0.7152, 0.0722)));
    }

    float3 Down13(float2 uv, bool karis)
    {
        float2 t = _NFX_PostSourceSize.zw;
        float3 a = SampleRT(_NFX_PostSource, uv + t * float2(-2, -2)).rgb;
        float3 b = SampleRT(_NFX_PostSource, uv + t * float2(0, -2)).rgb;
        float3 c = SampleRT(_NFX_PostSource, uv + t * float2(2, -2)).rgb;
        float3 d = SampleRT(_NFX_PostSource, uv + t * float2(-1, -1)).rgb;
        float3 e = SampleRT(_NFX_PostSource, uv + t * float2(1, -1)).rgb;
        float3 f = SampleRT(_NFX_PostSource, uv + t * float2(-2, 0)).rgb;
        float3 g = SampleRT(_NFX_PostSource, uv).rgb;
        float3 h = SampleRT(_NFX_PostSource, uv + t * float2(2, 0)).rgb;
        float3 i = SampleRT(_NFX_PostSource, uv + t * float2(-1, 1)).rgb;
        float3 j = SampleRT(_NFX_PostSource, uv + t * float2(1, 1)).rgb;
        float3 k = SampleRT(_NFX_PostSource, uv + t * float2(-2, 2)).rgb;
        float3 l = SampleRT(_NFX_PostSource, uv + t * float2(0, 2)).rgb;
        float3 m = SampleRT(_NFX_PostSource, uv + t * float2(2, 2)).rgb;
        if (!karis)
            return (d + e + i + j) * 0.125 + (a + b + f + g) * 0.03125 + (b + c + g + h) * 0.03125
                 + (f + g + k + l) * 0.03125 + (g + h + l + m) * 0.03125;
        // the five boxes, each weighted by its brightness
        float3 b0 = (d + e + i + j) * 0.25, b1 = (a + b + f + g) * 0.25, b2 = (b + c + g + h) * 0.25;
        float3 b3 = (f + g + k + l) * 0.25, b4 = (g + h + l + m) * 0.25;
        float w0 = Karis(b0) * 0.5, w1 = Karis(b1) * 0.125, w2 = Karis(b2) * 0.125, w3 = Karis(b3) * 0.125, w4 = Karis(b4) * 0.125;
        return (b0 * w0 + b1 * w1 + b2 * w2 + b3 * w3 + b4 * w4) / max(w0 + w1 + w2 + w3 + w4, 1e-6);
    }

    float4 DownFirst(Varyings v) : SV_Target
    {
        float3 c = Down13(TargetUV(v), true);
        return float4(any(!isfinite(c)) ? 0.0 : c, 1.0);
    }

    float4 Down(Varyings v) : SV_Target
    {
        return float4(Down13(TargetUV(v), false), 1.0);
    }

    // --- bloom up: this level's down result plus the smaller level's, through a 3 x 3 tent
    static const float BloomFalloff = 0.55; // each wider level's share against the one inside it

    float4 Up(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        float2 t = _NFX_PostSourceSize.zw; // the smaller level's texel
        float3 s = SampleRT(_NFX_PostLow, uv + t * float2(-1, -1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(1, -1)).rgb
                 + SampleRT(_NFX_PostLow, uv + t * float2(-1, 1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(1, 1)).rgb
                 + 2.0 * (SampleRT(_NFX_PostLow, uv + t * float2(0, -1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(-1, 0)).rgb
                        + SampleRT(_NFX_PostLow, uv + t * float2(1, 0)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(0, 1)).rgb)
                 + 4.0 * SampleRT(_NFX_PostLow, uv).rgb;
        // the wider levels count for less: a lens's and an eye's scatter falls off fast away from a light (summed evenly,
        // a neon sign at night spread a cloud over half the picture)
        return float4(SampleRT(_NFX_PostSource, uv).rgb + s / 16.0 * BloomFalloff, 1.0);
    }

    // --- lens flare
    TEXTURE2D_X(_NFX_PostFlareTex); // the bloom's quarter resolution level: how bright a light shows on screen
    float4 _NFX_PostFlare;          // x ghosts, y spikes, z threshold (exposed), w 1 = on
    // the lights (ParticleLighting): xyz absolute position, the sun's direction
    float4 _NFX_PLights[8];
    float4 _NFX_PLightColors[8]; // w: cosine of a spot's outer cone (-2 a point light)
    float4 _NFX_PLightDirs[8];   // xyz: a spot's direction, w: cosine of its inner cone
    float4 _NFX_PLightBeams[8];  // x: a headlight's beam (1 low, 2 high), y its roll
    float _NFX_PLightCount;
    float4 _NFX_PSunDir;

    // a headlight's beam (NFX_FogCommon's, the same pattern): how much of its light comes the camera's way
    float FlareBeam(float kind, float3 forward, float3 dir, float roll)
    {
        if (kind < 0.5)
            return 1.0;
        float3 level = abs(forward.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
        float3 right0 = normalize(cross(level, forward));
        float3 up0 = cross(forward, right0);
        float3 up = up0 * cos(roll) - right0 * sin(roll);
        float3 right = cross(up, forward);
        float z = max(dot(dir, forward), 1e-3);
        float h = degrees(atan2(dot(dir, right), z));
        float a = degrees(atan2(dot(dir, up), z));
        if (kind > 1.5)
            return min(1.0, exp(-(h / 5.0) * (h / 5.0) - (a / 2.5) * (a / 2.5)) + exp(-(h / 20.0) * (h / 20.0) - (a / 7.0) * (a / 7.0)) * 0.25);
        float cut = -0.6 + (h < 0.0 ? min(-h * 0.267949, 1.6) : 0.0);
        float below = cut - a;
        float edge = saturate(below / 0.5 + 0.5);
        float spread = a < -1.2 ? 2.2 : 1.2; // the peak 1.2° down: slower below it than above
        float hotSpot = exp(-(h / 9.0) * (h / 9.0) - ((a + 1.2) / spread) * ((a + 1.2) / spread));
        float fill = exp(-(h / 22.0) * (h / 22.0)) * exp(-max(0.0, below - 1.0) / 2.0) * 0.25 * saturate((38.0 - sqrt(h * h + a * a)) / 8.0);
        return lerp(0.015, min(1.0, hotSpot + fill), edge);
    }



    // signed distance to a regular hexagon of inradius r (negative inside)
    float Hexagon(float2 p, float r)
    {
        const float3 k = float3(-0.866025404, 0.5, 0.577350269);
        p = abs(p);
        p -= 2.0 * min(dot(k.xy, p), 0.0) * k.xy;
        p -= float2(clamp(p.x, -k.z * r, k.z * r), r);
        return length(p) * sign(p.y);
    }

    // a ghost: the aperture's image out of focus. Most are round with a soft edge (the blades' polygon blurs away
    // that far out of focus), one keeps the hexagon; a faint brighter rim, as real ghosts show
    float GhostShape(float2 d, float r, bool hexagon)
    {
        float dist = hexagon ? Hexagon(d, r) : length(d) - r;
        float inside = saturate(-dist / max(r * 0.3, 1e-4) + 0.5);
        float rim = 0.75 + 0.25 * saturate((dist + r * 0.3) / (r * 0.3));
        return inside * rim;
    }

    float3 FlareFrom(float2 q, float2 lightUV, float aspect, float facing)
    {
        if (facing <= 0.01)
            return 0.0;
        float3 src = SampleRT(_NFX_PostFlareTex, lightUV).rgb;
        float l = dot(src, float3(0.2126, 0.7152, 0.0722));
        float bright = max(l - _NFX_PostFlare.z, 0.0);
        if (bright <= 0.0)
            return 0.0;
        float3 colour = src / max(l, 1e-4) * bright * facing;
        float2 lq = (lightUV - 0.5) * float2(aspect, 1.0);
        // the lens's vignetting: a light near the edge of the picture sends fewer ghosts through
        float vignette = saturate(1.0 - length(lq) / 0.95);
        vignette *= vignette;
        const float scales[5] = { -0.3, -0.62, 0.38, -1.05, 0.72 };
        const float radii[5] = { 0.016, 0.04, 0.022, 0.075, 0.03 };
        // nearly neutral: the coatings tint ghosts only faintly (amber, violet, green)
        const float3 tints[5] = { float3(1.0, 0.9, 0.8), float3(0.85, 0.85, 1.0), float3(0.9, 1.0, 0.92),
                                  float3(0.92, 0.9, 1.0), float3(1.0, 0.95, 0.85) };
        float3 sum = 0.0;
        [unroll]
        for (int i = 0; i < 5; ++i)
        {
            float2 d = q - lq * scales[i];
            float r = radii[i];
            bool hexagon = i == 1;
            // dispersion splits the rim only: red a touch bigger than blue
            float3 shape = float3(GhostShape(d, r * 1.02, hexagon), GhostShape(d, r, hexagon), GhostShape(d, r * 0.98, hexagon));
            // a lens's coatings leave about a twenty thousandth of the light in each ghost, spread over its area
            sum += shape * tints[i] * (6e-5 * Sq(0.02 / r));
        }
        sum *= _NFX_PostFlare.x * vignette;
        float2 d0 = q - lq;
        float len = length(d0);
        // the glow round a bright light: the lens's and the eye's own scatter, close in and soft
        sum += float3(1.0, 0.95, 0.9) * 6e-5 / (1.0 + Sq(len / 0.006)) * _NFX_PostFlare.x;
        if (_NFX_PostFlare.y > 0.0)
        {
            // diffraction: six soft rays of a little different lengths (the blades are not perfect), short
            float angle = atan2(d0.y, d0.x);
            float ray = pow(abs(cos(3.0 * angle)), 40.0);
            float reach = 0.06 * (0.75 + 0.25 * cos(angle * 7.0 + 1.3));
            sum += ray * 1.2e-4 / (1.0 + Sq(len / 0.008)) * saturate(1.0 - len / reach) * _NFX_PostFlare.y;
        }
        return colour * sum;
    }

    float3 LensFlare(float2 uv)
    {
        float aspect = _ScreenSize.x * _ScreenSize.w;
        float2 q = (uv - 0.5) * float2(aspect, 1.0);
        float3 sum = 0.0;
        // the sun (or the moon) far along its direction
        float3 camFwd = -UNITY_MATRIX_V[2].xyz;
        if (dot(_NFX_PSunDir.xyz, camFwd) > 0.05)
        {
            float2 sunUV = ComputeNormalizedDeviceCoordinates(_NFX_PSunDir.xyz * 5000.0, UNITY_MATRIX_VP);
            if (all(sunUV > 0.0) && all(sunUV < 1.0))
                sum += FlareFrom(q, sunUV, aspect, 1.0);
        }
        int count = min((int)_NFX_PLightCount, 8);
        [loop]
        for (int i = 0; i < count; ++i)
        {
            float3 rws = GetCameraRelativePositionWS(_NFX_PLights[i].xyz);
            if (dot(rws, camFwd) <= 0.3)
                continue;
            float2 luv = ComputeNormalizedDeviceCoordinates(rws, UNITY_MATRIX_VP);
            if (any(luv <= 0.0) || any(luv >= 1.0))
                continue;
            // only as much as the lamp sends the camera's way: a headlight seen from the side or behind makes no
            // flare (its beam goes ahead), whatever is bright round it
            float facing = 1.0;
            if (_NFX_PLightColors[i].w > -1.5)
            {
                float3 toCamera = -normalize(rws);
                float c = dot(toCamera, _NFX_PLightDirs[i].xyz);
                facing = smoothstep(_NFX_PLightColors[i].w, _NFX_PLightDirs[i].w, c)
                       * FlareBeam(_NFX_PLightBeams[i].x, _NFX_PLightDirs[i].xyz, toCamera, _NFX_PLightBeams[i].y);
            }
            sum += FlareFrom(q, luv, aspect, facing);
        }
        return sum;
    }

    // --- tone curves (scene linear, exposed -> display linear 0..1)
    float3 AcesFitted(float3 x)
    {
        // Stephen Hill's fit of the ACES RRT + ODT, with its input and output matrices
        const float3x3 inM = { 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 };
        const float3x3 outM = { 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 };
        x = mul(inM, x);
        float3 a = x * (x + 0.0245786) - 0.000090537;
        float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
        return saturate(mul(outM, a / b));
    }

    float3 PbrNeutral(float3 c)
    {
        // Khronos PBR Neutral: base colours stay as they are up to the shoulder, highlights roll off towards white
        const float startCompression = 0.8 - 0.04;
        const float desaturation = 0.15;
        float x = min(c.r, min(c.g, c.b));
        float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
        c -= offset;
        float peak = max(c.r, max(c.g, c.b));
        if (peak < startCompression)
            return c;
        const float d = 1.0 - startCompression;
        float newPeak = 1.0 - d * d / (peak + d - startCompression);
        c *= newPeak / peak;
        float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
        return lerp(c, newPeak, g);
    }

    float3 AgX(float3 c)
    {
        // AgX (Troy Sobotka), the base look: the inset matrix, a log2 encoding over 16.5 stops, the sigmoid as a
        // polynomial fit, the outset matrix; display linear out
        const float3x3 inset = { 0.856627153315983, 0.0951212405381588, 0.0482516061458583,
                                 0.137318972929847, 0.761241990602591, 0.101439036467562,
                                 0.11189821299995, 0.0767994186031903, 0.811302368396859 };
        const float3x3 outset = { 1.1271005818144368, -0.11060664309660323, -0.016493938717834573,
                                  -0.1413297634984383, 1.157823702216272, -0.016493938717834257,
                                  -0.14132976349843826, -0.11060664309660294, 1.2519364065950405 };
        const float minEv = -12.47393, maxEv = 4.026069;
        c = mul(inset, max(c, 1e-10));
        c = saturate((log2(c) - minEv) / (maxEv - minEv));
        float3 x2 = c * c, x4 = x2 * x2;
        c = 15.5 * x4 * x2 - 40.14 * x4 * c + 31.96 * x4 - 6.868 * x2 * c + 0.4298 * x2 + 0.1191 * c - 0.00232;
        c = mul(outset, c);
        // the curve's output is display encoded (sRGB-ish 2.2): back to display linear for HDRP's output
        return pow(saturate(c), 2.2);
    }

    float4 Final(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float3 c = LOAD_TEXTURE2D_X(_NFX_PostSource, pixel).rgb;
        if (_NFX_PostParams.x > 0.0)
        {
            // the bloom chain sums its levels, each wider one at BloomFalloff of the last: over their total weight it is
            // the blurred picture
            float levels = max(_NFX_PostMode.y, 1.0);
            float3 bloom = SampleRT(_NFX_PostBloom, uv).rgb * (1.0 - BloomFalloff) / (1.0 - pow(BloomFalloff, levels));
            c = lerp(c, bloom, _NFX_PostParams.x);
        }
        if (_NFX_PostFlare.w > 0.5)
            c += LensFlare(uv);
        c = max(c, 0.0) * exp2(_NFX_PostParams.y);
        // the eye's white balance: the light around taken partly as white (sunset, shade under a blue sky, lamps)
        if (any(_NFX_PostWhite.rgb > 0.0))
            c *= _NFX_PostWhite.rgb;
        int mode = (int)_NFX_PostMode.x;
        float3 d = mode == 0 ? AcesFitted(c) : mode == 1 ? PbrNeutral(c) : mode == 2 ? AgX(c) : saturate(c);
        // contrast about mid grey (in a perceptual space), then saturation
        float3 p = pow(max(d, 0.0), 1.0 / 2.2);
        p = saturate((p - 0.5) * (1.0 + _NFX_PostParams.z) + 0.5);
        float luma = dot(p, float3(0.2126, 0.7152, 0.0722));
        p = saturate(lerp(luma, p, 1.0 + _NFX_PostParams.w));
        return float4(pow(p, 2.2), 1.0);
    }

    // --- motion blur and depth of field (PostPass, before the meter and the bloom: on the scene's light). Depth and
    // motion are HDRP's, at the render resolution; the target may be the display's (DLSS upscaling).
    TEXTURE2D_X(_NFX_PostMotion);   // motion to the previous frame in uv (previous - current), render resolution
    float4 _NFX_PostBlur;           // x the shutter's share of a frame, y longest blur (target pixels), w largest circle of confusion (target pixels)
    float4 _NFX_PostLens;           // x focal length (mm), y f-number, z focus distance (m), w sensor height (mm)

    uint2 RenderPixel(float2 uv)
    {
        return min((uint2)(saturate(uv) * _ScreenSize.xy), (uint2)_ScreenSize.xy - 1);
    }

    float2 MotionAt(float2 uv)
    {
        return LOAD_TEXTURE2D_X(_NFX_PostMotion, RenderPixel(uv)).xy;
    }

    float DistanceAt(float2 uv)
    {
        float d = LoadCameraDepth(RenderPixel(uv));
        return d == UNITY_RAW_FAR_CLIP_VALUE ? 1e5 : LinearEyeDepth(d, _ZBufferParams);
    }

    float4 MotionBlur(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        float3 c0 = SampleRT(_NFX_PostSource, uv).rgb;
        float2 m = MotionAt(uv) * _NFX_PostBlur.x;
        float len = length(m * _NFX_PostTargetSize.xy);
        if (len < 0.5)
            return float4(c0, 1.0);
        if (len > _NFX_PostBlur.y)
        {
            m *= _NFX_PostBlur.y / len;
            len = _NFX_PostBlur.y;
        }
        float d0 = DistanceAt(uv);
        float3 sum = c0;
        float w = 1.0;
        const int N = 16;
        [loop]
        for (int i = 0; i < N; ++i)
        {
            // along the path, centred on the pixel (the shutter open around the frame's moment)
            float2 suv = uv + m * ((i + 0.5) / N - 0.5);
            float ds = DistanceAt(suv);
            float ls = length(MotionAt(suv) * _NFX_PostBlur.x * _NFX_PostTargetSize.xy);
            if (ds < d0 * 0.95 && ls < len * 0.5)
                continue;
            sum += SampleRT(_NFX_PostSource, suv).rgb;
            w += 1.0;
        }
        return float4(sum / w, 1.0);
    }

    // a thin lens's circle of confusion for a surface this far (m), in target pixels
    float CocPixels(float distance)
    {
        float f = _NFX_PostLens.x;
        float focus = max(_NFX_PostLens.z * 1000.0, f * 1.05);
        float d = max(distance * 1000.0, f * 1.05);
        float coc = f * f / _NFX_PostLens.y * abs(d - focus) / (d * (focus - f));
        return min(coc / _NFX_PostLens.w * _NFX_PostTargetSize.y, _NFX_PostBlur.w);
    }

    float4 DepthOfField(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        float3 c0 = SampleRT(_NFX_PostSource, uv).rgb;
        float d0 = DistanceAt(uv);
        float coc0 = CocPixels(d0);
        // how far to look: this pixel's own blur, or a blurred nearer surface around it that reaches over it
        float radius = coc0;
        float widest = _NFX_PostBlur.w;
        [unroll]
        for (int k = 0; k < 8; ++k)
        {
            float a = k * 0.7853982;
            float2 o = float2(cos(a), sin(a)) * widest * _NFX_PostTargetSize.zw;
            float ds = DistanceAt(uv + o);
            if (ds < d0)
                radius = max(radius, CocPixels(ds));
        }
        if (radius < 0.75)
            return float4(c0, 1.0);
        float3 sum = 0.0;
        float w = 0.0;
        const int N = 40;
        [loop]
        for (int i = 0; i < N; ++i)
        {
            float r = sqrt((i + 0.5) / N) * radius;
            float a = i * 2.3999632;
            float2 suv = uv + float2(cos(a), sin(a)) * r * _NFX_PostTargetSize.zw;
            float ds = DistanceAt(suv);
            float cs = CocPixels(ds);
            // a farther surface does not spread over a nearer, sharper one
            if (ds > d0)
                cs = min(cs, coc0);
            float wi = saturate(cs - r + 1.0);
            sum += SampleRT(_NFX_PostSource, suv).rgb * wi;
            w += wi;
        }
        return w > 1e-3 ? float4(sum / w, 1.0) : float4(c0, 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass { Name "Meter" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Meter
            ENDHLSL }
        Pass { Name "DownFirst" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DownFirst
            ENDHLSL }
        Pass { Name "Down" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Down
            ENDHLSL }
        Pass { Name "Up" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Up
            ENDHLSL }
        Pass { Name "Final" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Final
            ENDHLSL }
        Pass { Name "MotionBlur" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment MotionBlur
            ENDHLSL }
        Pass { Name "DepthOfField" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DepthOfField
            ENDHLSL }

    }
    Fallback Off
}

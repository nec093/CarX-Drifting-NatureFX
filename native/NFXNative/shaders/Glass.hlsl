// NFXNative glass reflections (RTGlassPass). Glass is drawn transparent, after the opaque scene the other traces
// work on, so it is in the scene only for this trace (instance mask NFX_GLASS_MASK). Per pixel the camera ray looks
// for the closest glass in front of the opaque surface; where there is one, the ray mirrored on it is traced through
// the scene and what it hits is shaded as a reflection (camera colour where the hit is on screen, else shaded). HDRP
// already reflects its sky / probe in the glass: the result is the change to that, the hit's radiance less the sky
// light guess HDRP had there, times the glass's Fresnel. A ray that reaches the sky changes nothing.
// Surface (NFX_NativeBridge PrepareGlass): 2 float4 per pixel (view direction xyz, distance to the opaque surface
// (<= 0: nothing to trace); packed full resolution pixel, exposure multiplier). Output: float4 per pixel (change to
// the pixel's colour rgb, pre-exposed and signed; the glass's Fresnel). Compiled into Glass.h.
// NFX_FLAG_OWN_GLASS (roadmap E): HDRP does not draw the glass (OwnGlass.cs hides it), the camera colour is what is
// behind it, and the glass is drawn here whole: what it lets through (1 - Fresnel, tinted by its colour as far as it
// is opaque: HDRP's alpha), what it mirrors (the traced ray, the sky past it or on rough glass), the sun's glint, and
// the bend of solid glass. Output then: rgb the mirrored light, glint and the refraction's change (pre-exposed), w
// negative: -(1 + transmission rgb at 5 bits each + 32768 x whole metres to the glass).
#include "NFXReflect.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Surface.Load4(index * 32));
    float4 b = asfloat(Surface.Load4(index * 32 + 16));
    float4 result = 0;
    Hit glass;
    glass.t = 0.0;
    bool found = a.w > 0.0 && ClosestGlass(Camera, a.xyz, a.w, glass);
    bool own = (Flags & NFX_FLAG_OWN_GLASS) != 0u;
    float3 transmission = 1.0;
    if (found)
    {
        float3 viewDir = a.xyz;
        uint2 pixel = UnpackPixel(b.x);
        float exposure = b.y;
        HitSurface g = SurfaceAt(glass, viewDir, 0.0);
        float3 n = g.normal; // facing the camera
        float nv = saturate(dot(n, -viewDir));
        if (own)
        {
            float smoothness = saturate(g.smoothness);
            float fresnel = 0.04 + (max(smoothness, 0.04) - 0.04) * pow(1.0 - nv, 5.0);
            float opacity = GlassOpacity(glass);
            transmission = (1.0 - fresnel) * lerp(1.0, g.albedo, opacity);
            float3 dir = reflect(viewDir, n);
            float3 glassAWS = Camera + viewDir * glass.t;
            float3 origin = glassAWS + n * 0.02;
            // what it mirrors: the traced ray on smooth glass, else (and past the ray's reach) the sky's light
            float3 mirrored = SampleSH9(dir) * exposure;
            if (smoothness > 0.5)
            {
                Hit hit;
                if (Closest(origin, dir, ReflParams.x, false, hit))
                {
                    float3 hitAWS = origin + dir * hit.t;
                    HitSurface s = SurfaceAt(hit, dir, -1.0);
                    float3 screenColor;
                    float onScreen = ScreenVisibility(hitAWS, screenColor);
                    onScreen *= 1.0 - smoothstep(0.4, 0.6, s.smoothness);
                    float3 radiance = screenColor;
                    if (onScreen < 0.999)
                    {
                        float hitSky;
                        float3 shaded = max(ShadeReflectedHit(s, hitAWS, dir, Hash22(pixel, FrameIndex, 5), hitSky), 0.0);
                        if (any(!isfinite(shaded)))
                            shaded = 0.0;
                        radiance = lerp(shaded * exposure, screenColor, onScreen);
                    }
                    float reach = 1.0 - smoothstep(0.7, 1.0, hit.t / ReflParams.x);
                    mirrored = lerp(mirrored, radiance, reach);
                }
            }
            // the sun's glint (GGX, the sun disc's size as the least roughness)
            float3 glint = 0.0;
            float nl = dot(n, SunDir);
            if (SunUp != 0 && nl > 0.0 && SunVisible(origin))
            {
                float alpha = max((1.0 - smoothness) * (1.0 - smoothness), 0.02);
                float a2 = alpha * alpha;
                float3 h = normalize(SunDir - viewDir);
                float nh = saturate(dot(n, h));
                float t = nh * nh * (a2 - 1.0) + 1.0;
                float D = a2 / (PI * t * t);
                float sunFresnel = 0.04 + 0.96 * pow(1.0 - saturate(dot(h, -viewDir)), 5.0);
                glint = min(sunFresnel * D / (4.0 * max(nv, 0.05)), 200.0) * SunColor * exposure;
            }
            result = float4(mirrored * fresnel + glint, 0.0);
        }
        else
        {
        // frosted glass blurs what it mirrors past recognition: HDRP's probe reflection is as good there
        float smoothness = saturate(g.smoothness);
        if (smoothness > 0.5)
        {
            // Schlick with the smoothness capping the grazing rise (rough glass mirrors less at grazing angles)
            float fresnel = 0.04 + (max(smoothness, 0.04) - 0.04) * pow(1.0 - nv, 5.0);
            float3 dir = reflect(viewDir, n);
            float3 glassAWS = Camera + viewDir * glass.t;
            float3 origin = glassAWS + n * 0.02;
            float maxDistance = ReflParams.x;
            Hit hit;
            if (Closest(origin, dir, maxDistance, false, hit))
            {
                float3 hitAWS = origin + dir * hit.t;
                HitSurface s = SurfaceAt(hit, dir, -1.0);
                float3 screenColor;
                float onScreen = ScreenVisibility(hitAWS, screenColor);
                onScreen *= 1.0 - smoothstep(0.4, 0.6, s.smoothness);
                float3 radiance = screenColor;
                if (onScreen < 0.999)
                {
                    float hitSky;
                    float3 shaded = ShadeReflectedHit(s, hitAWS, dir, Hash22(pixel, FrameIndex, 5), hitSky);
                    shaded = max(shaded, 0.0);
                    if (any(!isfinite(shaded)))
                        shaded = 0.0;
                    radiance = lerp(shaded * exposure, screenColor, onScreen);
                }
                // what HDRP mirrored there instead: its sky light from that direction
                float3 skyGuess = SampleSH9(dir) * exposure;
                // fade into HDRP's own reflection over the last 30 % of the ray length
                float reach = 1.0 - smoothstep(0.7, 1.0, hit.t / maxDistance);
                result = float4(fresnel * (radiance - skyGuess) * reach, fresnel * reach);
            }
        }
        }
        // refraction: what is behind solid glass (a ball, a lens, a block) seen bent through it, where the ray leaves
        // the same piece again. A car's windows, and glass made as single sheets, bend nothing: a real pane's two faces
        // are parallel and give the view back unbent, while a sheet took the far window for the way out and bent the
        // view wildly. ReflParams.z scales the bend.
        float strength = saturate(ReflParams.z);
        float3 bent = refract(viewDir, n, 1.0 / 1.5);
        bool solid = false;
        float3 origin = 0;
        if (strength > 0.0 && !g.moving && dot(bent, bent) > 0.0)
        {
            origin = Camera + viewDir * glass.t - n * 0.02;
            Hit exitHit;
            if (ClosestGlass(origin, bent, 10.0, exitHit) && exitHit.instance == glass.instance)
            {
                HitSurface e = SurfaceAt(exitHit, bent, 0.0); // its normal faces the ray: into the glass
                float3 leaving = refract(bent, e.normal, 1.5);
                if (dot(leaving, leaving) > 0.0) // else totally reflected inside: left as it is
                {
                    origin += bent * exitHit.t - e.normal * 0.02;
                    bent = normalize(leaving);
                    solid = true;
                }
            }
        }
        {
            if (solid)
            {
                bent = normalize(lerp(viewDir, bent, strength));
                // what the bent ray finds: the camera colour where it is on screen, else shaded here (inside a ball's
                // outline the screen holds the ball, whose depth never matched: nothing was bent there), the sky if
                // nothing. It replaces what the pixel shows now.
                float3 refracted;
                Hit behind;
                if (Closest(origin, bent, 100.0, false, behind))
                {
                    float3 hitAWS = origin + bent * behind.t;
                    float seen = ScreenVisibility(hitAWS, refracted);
                    if (seen < 0.999)
                    {
                        float hitSky;
                        float3 shaded = max(ShadeReflectedHit(SurfaceAt(behind, bent, -1.0), hitAWS, bent, Hash22(pixel, FrameIndex, 9), hitSky), 0.0);
                        if (any(!isfinite(shaded)))
                            shaded = 0.0;
                        refracted = lerp(shaded * exposure, refracted, seen);
                    }
                }
                else
                {
                    refracted = SampleSH9(bent) * exposure;
                }
                uint3 own = Screen.Load3((pixel.y * (uint)ScreenSize.x + pixel.x) * 12);
                float3 straight = float3(f16tof32(own.x & 0xFFFFu), f16tof32(own.x >> 16), f16tof32(own.y & 0xFFFFu));
                float3 through = select(own, transmission, (float3)(1.0 - result.w));
                result.rgb += (refracted - straight) * through;
                result.w = max(result.w, 1e-3); // something to compose even where nothing is reflected
            }
        }
    }
    // the glass's distance with its Fresnel share (w: share + 2 x whole metres; RTGlassPass keeps it in 32 bit floats):
    // the composite puts the 3D fog in front of the glass
    if (found && own)
    {
        uint3 t5 = (uint3)round(saturate(transmission) * 31.0);
        float metres = min(floor(max(glass.t, 0.0)), 511.0);
        result.w = -(1.0 + (float)(t5.r | (t5.g << 5) | (t5.b << 10)) + 32768.0 * metres);
    }
    else if (found)
        result.w = saturate(result.w) + 2.0 * floor(max(glass.t, 0.0));
    Output.Store4(index * 16, asuint(result));
}

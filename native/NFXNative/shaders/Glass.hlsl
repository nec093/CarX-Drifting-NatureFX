// NFXNative glass reflections (RTGlassPass). Glass is drawn transparent, after the opaque scene the other traces
// work on, so it is in the scene only for this trace (instance mask NFX_GLASS_MASK). Per pixel the camera ray looks
// for the closest glass in front of the opaque surface; where there is one, the ray mirrored on it is traced through
// the scene and what it hits is shaded as a reflection (camera colour where the hit is on screen, else shaded). HDRP
// already reflects its sky / probe in the glass: the result is the change to that, the hit's radiance less the sky
// light guess HDRP had there, times the glass's Fresnel. A ray that reaches the sky changes nothing.
// Surface (NFX_NativeBridge PrepareGlass): 2 float4 per pixel (view direction xyz, distance to the opaque surface
// (<= 0: nothing to trace); packed full resolution pixel, exposure multiplier). Output: float4 per pixel (change to
// the pixel's colour rgb, pre-exposed and signed; the glass's Fresnel). Compiled into Glass.h.
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
    if (a.w > 0.0 && ClosestGlass(Camera, a.xyz, a.w, glass))
    {
        float3 viewDir = a.xyz;
        uint2 pixel = UnpackPixel(b.x);
        float exposure = b.y;
        HitSurface g = SurfaceAt(glass, viewDir, 0.0);
        float3 n = g.normal; // facing the camera
        float nv = saturate(dot(n, -viewDir));
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
        // refraction: what is behind bent glass (lenses, curved screens, thick panes) seen through it. The camera colour
        // already shows what is straight behind; the change is what the bent ray finds there instead. A flat pane
        // shifts the view too little to see, so the bend is scaled down (ReflParams.z) rather than leaving the glass.
        float strength = saturate(ReflParams.z);
        if (strength > 0.0)
        {
            float3 bent = refract(viewDir, n, 1.0 / 1.5);
            if (dot(bent, bent) > 0.0)
            {
                bent = normalize(lerp(viewDir, bent, strength));
                float3 glassAWS = Camera + viewDir * glass.t;
                float3 origin = glassAWS - n * 0.02;
                Hit behind;
                if (Closest(origin, bent, 100.0, false, behind))
                {
                    float3 refracted;
                    float3 straight;
                    float seen = ScreenVisibility(origin + bent * behind.t, refracted);
                    seen = min(seen, ScreenVisibility(Camera + viewDir * a.w, straight));
                    float through = 1.0 - result.w;
                    result.rgb += (refracted - straight) * seen * through;
                    result.w = max(result.w, 1e-3); // something to compose even where nothing is reflected
                }
            }
        }
    }
    Output.Store4(index * 16, asuint(result));
}

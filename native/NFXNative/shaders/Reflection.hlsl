// NFXNative reflections, one GGX sampled ray per trace
// pixel. Unity (NFX_NativeBridge PrepareReflection) works out per pixel what does not need the scene: the ray
// origin above the exact surface, its normal, roughness and the reflected share (Fresnel, clear coat included);
// here the ray is traced and what it hits is shaded: from the camera colour where the hit is on screen and not
// glossy, else with sky (occluded), sun (with a GGX highlight), the local lights (shadowed) and, for smooth hits,
// one more bounce.
// Surface: 2 uint4 per pixel (NfxReflSurface in NfxPack.hlsli: origin, pixel; normal, Fresnel, GGX alpha (< 0:
// nothing to trace), exposure multiplier). Output: a uint4 per pixel (NfxPackReflResult: Fresnel * radiance rgb
// (pre-exposed) and the luminance of the Fresnel as halves; the distance to the reflected point (0: nothing reflected)).
// Compiled into Reflection.h.
#include "NFXReflect.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    NfxReflSurface surface = NfxUnpackReflSurface(Surface.Load4(index * 32), Surface.Load4(index * 32 + 16));
    float4 result = 0;
    float dist = 0.0;
    if (surface.alpha >= 0.0)
    {
        float3 origin = surface.origin;
        float3 n = surface.normal;
        uint2 pixel = UnpackPixel(asfloat(surface.pixel));
        float3 fresnel = surface.fresnel;
        float exposure = surface.exposure;
        float3 V = normalize(Camera - origin);
        float alpha = max(surface.alpha, 0.002);
        float3 H;
        // a wet road: the water film over the bumps of the asphalt stretches every reflection along the view (the
        // long streaks of lamps and headlights at night), and keeps it narrow across
        // a mirror (car paint's varnish, chrome, glass-smooth): the exact mirror direction. Its GGX lobe is narrower
        // than a pixel; sampling it anyway only shook the reflection by a fraction of a pixel every frame, which with
        // no history to average it (NrdSpecUnpack keeps a mirror's own trace) read as grain, orange peel
        bool mirror = alpha <= 0.003;
        float wet = n.y > 0.7 && !mirror ? saturate(ReflParams.w) : 0.0;
        float3 along = V - n * dot(V, n);
        if (wet > 0.0 && dot(along, along) > 1e-4)
        {
            float3 t = normalize(along);
            float stretch = alpha + wet * 0.35;
            H = SampleGGXHalfVectorAniso(n, t, max(stretch, 0.002), max(alpha * (1.0 - 0.6 * wet), 0.002), Hash22(pixel, FrameIndex, 0));
        }
        else
        {
            H = SampleGGXHalfVector(n, alpha, Hash22(pixel, FrameIndex, 0));
        }
        float3 dir = reflect(-V, H);
        if (mirror || dot(dir, n) <= 0.0)
            dir = reflect(-V, n);

        float maxDistance = ReflParams.x;
        Hit hit;
        if (!Closest(origin, dir, maxDistance, false, hit))
        {
            // the sky: HDRP's reflection stays; reproject like a very distant reflected point
            dist = maxDistance;
        }
        else
        {
            float3 hitAWS = origin + dir * hit.t;
            HitSurface s = SurfaceAt(hit, dir, -1.0);
            float3 screenColor;
            float onScreen = ScreenVisibility(hitAWS, screenColor);
            // the camera colour holds a glossy surface's reflection from the wrong angle: glossy hits are shaded
            onScreen *= 1.0 - smoothstep(0.4, 0.6, s.smoothness);

            float3 radiance = screenColor;
            if (onScreen < 0.999)
            {
                float hitSky;
                // a mirror's hit: its sky share from four rays, not one (one binary ray a frame grained the paint)
                float3 shaded = mirror ? ShadeReflectedHitSmooth(s, hitAWS, dir, pixel, hitSky)
                                       : ShadeReflectedHit(s, hitAWS, dir, Hash22(pixel, FrameIndex, 7), hitSky);
                // a smooth hit (mirror, chrome, wet floor, glossy paint) shows what it faces: one more ray replaces
                // the sky probe guess, weighted by the hit's own Fresnel
                if (ReflParams.z > 0.5 && s.smoothness > 0.6)
                {
                    float3 hn = s.normal;
                    float3 dir2 = reflect(dir, hn);
                    float3 origin2 = hitAWS + hn * 0.02;
                    Hit second;
                    float3 seen = SampleSH9(dir2);
                    if (Closest(origin2, dir2, maxDistance, false, second))
                        seen = ShadeHit(SurfaceAt(second, dir2, 4.0), origin2 + dir2 * second.t, dir2, 1.0);
                    shaded += HitFresnel(s, dir) * (seen - SampleSH9(dir2) * hitSky);
                }
                // never negative or NaN: one bad sample spreads through the temporal filter as coloured speckles
                shaded = max(shaded, 0.0);
                if (any(!isfinite(shaded)))
                    shaded = 0.0;
                radiance = lerp(shaded * exposure, screenColor, onScreen);
            }
            // fade into HDRP's own reflection over the last 30 % of the ray length
            float reach = 1.0 - smoothstep(0.7, 1.0, hit.t / maxDistance);
            result = float4(fresnel * radiance, Luminance(fresnel)) * reach;
            dist = hit.t;
        }
    }
    Output.Store4(index * 16, NfxPackReflResult(result, dist));
}

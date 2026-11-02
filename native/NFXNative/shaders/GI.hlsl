// NFXNative global illumination: the ambient light (relative to the sky light
// the pixel would get unblocked, which the shadow pass scales HDRP's ambient light by), with every bounce instead
// of one. One cosine ray per half resolution pixel within the GI distance; what it hits sends back its direct light
// with the light it receives itself from the radiance cache (direct and every bounce before, averaged over many
// pixels and frames, so a single shadow ray at the hit no longer decides its brightness), plus what it gives off.
// The hit feeds the cache with one sample of what it receives: its direct light (sun and lamps with their shadows)
// and one more ray for the light bounced to it, so the bounces build up over frames.
// Unlike the one bounce ambient light it is not a factor on HDRP's ambient light: the sky light HDRP already gives is
// only darkened by what blocks the sky, and the light bounced in is added as it is (a factor relative to the open
// sky stayed small indoors, where HDRP's own ambient light is dark too, and the colour bleeding barely showed).
// Surface (NFX_NativeBridge PrepareAmbient): 2 uint4 per pixel, as Ambient.hlsl. Output: float4 per pixel (light
// bounced in: the average radiance the hits send back, before exposure, rgb; share of the sky seen, a). The distance
// the ray went goes into the surface's last float (NfxAmbientTravelledOffset) for NRD's REBLUR (NrdReblurPack.hlsl).
// Compiled into GI.h.
#include "NFXCommon.hlsli"

#define NFX_SKY_UNDERSIDE 0.3

float FarSky(float3 originAWS, float3 dir, float giDistance)
{
    float skyDistance = AmbientParams.z;
    if (skyDistance <= giDistance)
        return 1.0;
    return Blocked(originAWS + dir * giDistance, dir, skyDistance - giDistance, true) ? NFX_SKY_UNDERSIDE : 1.0;
}

// The direct light a surface receives (irradiance / pi, before exposure): the sun and the local lights, with their
// shadows (unshadowed lamps lit the far side of walls and spattered the image with bright dots)
float3 DirectLight(HitSurface s, float3 hitAWS)
{
    float3 e = LocalLightsAtHit(hitAWS, s.normal, true);
    float ndl = saturate(dot(s.normal, SunDir));
    if (SunUp != 0 && ndl > 0.0 && SunVisible(hitAWS + s.normal * 0.05))
        e += SunColor * ndl * INV_PI;
    return e;
}

// All the light a surface receives: the cache's average, or (a cell nothing has fed yet) its direct light and the sky.
// A car's surface is in other cells every frame: barely fed, it read a different few samples each frame (the ground
// under and behind a moving car flickered), so a moving hit is always lit directly.
float3 ReceivedLight(HitSurface s, float3 hitAWS)
{
    float3 cached;
    return !s.moving && CacheLookup(hitAWS, s.normal, cached) ? cached : DirectLight(s, hitAWS) + HitSkyLight(s.normal);
}

// The radiance a surface sends back along the ray (before exposure)
float3 Outgoing(HitSurface s, float3 hitAWS, float3 rayDir)
{
    float3 diffuse = s.albedo * ReceivedLight(s, hitAWS) * (1.0 - s.metallic);
    float3 specular = HitFresnel(s, rayDir) * SampleSH9(reflect(rayDir, s.normal));
    return diffuse + specular + s.emission;
}

// Caps a pixel's ambient light, keeping its colour: one ray onto a lamp or a sunlit patch is a bright dot the
// filters cannot spread out
float3 ClampBright(float3 v, float limit)
{
    float l = Luminance(v);
    return l > limit ? v * (limit / l) : v;
}

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    NfxAmbientSurface surface = NfxUnpackAmbientSurface(Surface.Load4(index * NfxAmbientStride), Surface.Load4(index * NfxAmbientStride + 16));
    float3 bounce = 0.0;
    float sky = 1.0;
    float giDistance = AmbientParams.x;
    if (surface.valid && giDistance > 0.0)
    {
        float3 origin = surface.origin;
        float3 n = surface.normal;
        float3 geomN = surface.geomNormal;
        uint2 pixel = UnpackPixel(asfloat(surface.pixel));
        bool coarse = surface.viewDepth > 50.0;
        float3 dir = CosineSampleHemisphere(n, Hash22(pixel, FrameIndex, 5));
        if (dot(dir, geomN) < 0.05)
            dir = normalize(dir + geomN * (0.05 - dot(dir, geomN)));
        // a bright dot (one ray onto a lamp or a sunlit patch) is capped at about a sunlit (or lamp lit) white surface
        float limit = 2.0 * CacheReference();

        Hit hit;
        bool hitSomething = Closest(origin, dir, giDistance, coarse, hit);
        Surface.Store(index * NfxAmbientStride + NfxAmbientTravelledOffset, asuint(hitSomething ? hit.t : giDistance));
        if (!hitSomething)
        {
            sky = FarSky(origin, dir, giDistance);
        }
        else
        {
            float3 hitAWS = origin + dir * hit.t;
            HitSurface s = SurfaceAt(hit, dir, 4.0);
            // what this pixel sees there; hits near the end of the ray fade into the sky beyond (no popping)
            float fade = smoothstep(0.7, 1.0, hit.t / giDistance);
            bounce = ClampBright(Outgoing(s, hitAWS, dir), limit) * (1.0 - fade);
            sky = fade > 0.0 ? FarSky(origin, dir, giDistance) * fade : 0.0;

            // a sample of what the hit receives, for the cache: its direct light, and one more ray for the light
            // bounced to it (what that ray hits sends back its own cached light: the bounces before)
            // (not from a car: its cells would follow it through the world, fed a frame each)
            if (!coarse && !s.moving)
            {
                float3 hn = s.normal;
                float3 dir2 = CosineSampleHemisphere(hn, Hash22(pixel, FrameIndex, 9));
                float3 origin2 = hitAWS + hn * 0.03;
                Hit hit2;
                float3 bounced;
                if (Closest(origin2, dir2, giDistance, true, hit2))
                {
                    float3 hitAWS2 = origin2 + dir2 * hit2.t;
                    HitSurface s2 = SurfaceAt(hit2, dir2, 4.0);
                    bounced = s2.albedo * ReceivedLight(s2, hitAWS2) * (1.0 - s2.metallic) + s2.emission;
                }
                else
                {
                    bounced = SampleSH9(dir2) * FarSky(origin2, dir2, giDistance);
                }
                CacheAdd(hitAWS, hn, DirectLight(s, hitAWS) + bounced);
            }
        }

        float contact = AmbientParams.w;
        if (contact > 0.0 && !coarse)
        {
            float3 dir2 = CosineSampleHemisphere(n, Hash22(pixel, FrameIndex, 6));
            if (dot(dir2, geomN) < 0.05)
                dir2 = normalize(dir2 + geomN * (0.05 - dot(dir2, geomN)));
            if (Blocked(origin, dir2, min(0.4, giDistance), false))
            {
                sky *= 1.0 - contact;
                bounce *= 1.0 - contact;
            }
        }
    }
    // half float targets: keep within their range
    Output.Store4(index * 16, asuint(float4(min(bounce, 60000.0), sky)));
}

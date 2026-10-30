// NFXNative ambient light, one cosine ray per half
// resolution pixel within the ambient distance. A ray that gets out keeps HDRP's ambient light (1, or the sky's
// share past the distance when an overhang takes it); one that hits brings the light that surface sends back
// relative to the sky light the pixel would get (bounce light), or blocks (plain AO without bounce). A short
// second ray darkens creases (contact shadows).
// Surface (NFX_NativeBridge PrepareAmbient): 3 float4 per pixel (origin xyz, 1 = valid; shading normal xyz, packed
// full resolution pixel; geometric normal xyz, view depth). Output: float4 per pixel (ambient rgb relative to
// HDRP's). Compiled into Ambient.h.
#include "NFXCommon.hlsli"

#define NFX_SKY_UNDERSIDE 0.3

float FarSky(float3 originAWS, float3 dir, float ambientDistance)
{
    float skyDistance = AmbientParams.z;
    if (skyDistance <= ambientDistance)
        return 1.0;
    return Blocked(originAWS + dir * ambientDistance, dir, skyDistance - ambientDistance, true) ? NFX_SKY_UNDERSIDE : 1.0;
}

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 a = asfloat(Surface.Load4(index * 48));
    float4 b = asfloat(Surface.Load4(index * 48 + 16));
    float4 c = asfloat(Surface.Load4(index * 48 + 32));
    float3 ambient = 1.0;
    float ambientDistance = AmbientParams.x;
    if (a.w > 0.5 && ambientDistance > 0.0)
    {
        float3 origin = a.xyz;
        float3 n = b.xyz;
        float3 geomN = c.xyz;
        uint2 pixel = UnpackPixel(b.w);
        bool coarse = c.w > 50.0;
        float3 dir = CosineSampleHemisphere(n, Hash22(pixel, FrameIndex, 5));
        if (dot(dir, geomN) < 0.05)
            dir = normalize(dir + geomN * (0.05 - dot(dir, geomN)));
        if (AmbientParams.y > 0.5)
        {
            Hit hit;
            bool hitSomething = Closest(origin, dir, ambientDistance, coarse, hit);
            float far = !hitSomething || hit.t > ambientDistance * 0.7 ? FarSky(origin, dir, ambientDistance) : 1.0;
            ambient = far;
            if (hitSomething)
            {
                // the hit's radiance over the sky's level (not its colour: a blue sky turned grey bounces green)
                float3 hitAWS = origin + dir * hit.t;
                HitSurface s = SurfaceAt(hit, dir, 4.0);
                float hitSky = 1.0;
                if (!coarse)
                {
                    float skyReach = AmbientParams.z > 0.0 ? AmbientParams.z : 4.0;
                    float3 skyDir = CosineSampleHemisphere(s.normal, Hash22(pixel, FrameIndex, 8));
                    hitSky = Blocked(hitAWS + s.normal * 0.03, skyDir, skyReach, true) ? NFX_SKY_UNDERSIDE : 1.0;
                }
                float reference = max(Luminance(SampleSH9(n)), 1e-3);
                float3 bounce = min(ShadeHit(s, hitAWS, dir, hitSky) / reference, 8.0);
                ambient = lerp(bounce, far, smoothstep(0.7, 1.0, hit.t / ambientDistance));
            }
        }
        else
        {
            ambient = Blocked(origin, dir, ambientDistance, coarse) ? 0.0 : FarSky(origin, dir, ambientDistance);
        }

        float contact = AmbientParams.w;
        if (contact > 0.0 && !coarse)
        {
            float3 dir2 = CosineSampleHemisphere(n, Hash22(pixel, FrameIndex, 6));
            if (dot(dir2, geomN) < 0.05)
                dir2 = normalize(dir2 + geomN * (0.05 - dot(dir2, geomN)));
            if (Blocked(origin, dir2, min(0.4, ambientDistance), false))
                ambient *= 1.0 - contact;
        }
    }
    Output.Store4(index * 16, asuint(float4(ambient, 1.0)));
}

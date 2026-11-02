// NFXNative: shading what a reflection shows, shared by Reflection.hlsl and Glass.hlsl
#include "NFXCommon.hlsli"

#define NFX_SKY_COVERED 0.25
// how far the sky test of a reflected hit looks: 4 m saw out of any room larger than that (a garage's walls in the
// car's side were lit by the open sky); the GI's own far sky distance when it is longer
#define NFX_REFLECT_SKY_TEST max(AmbientParams.z, 30.0)

// Hybrid shading: where the hit is what the camera sees at its screen position, the camera colour there is HDRP's
// full lighting. Returns how much to trust it (fades out near the screen edges, 0 off screen or hidden).
float ScreenVisibility(float3 hitAWS, out float3 color)
{
    color = 0;
    float4 clip = mul(ViewProj, float4(hitAWS - Camera, 1.0));
    if (clip.w <= 0.0)
        return 0.0;
    float2 ndc = clip.xy / clip.w;
    float2 suv = float2(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    if (any(suv < 0.0) || any(suv > 1.0))
        return 0.0;
    uint2 sp = min((uint2)(suv * ScreenSize.xy), (uint2)ScreenSize.xy - 1);
    // the bridge's PrepareScreen: colour rgb and linear depth as 4 halves
    uint2 s = Screen.Load2((sp.y * (uint)ScreenSize.x + sp.x) * 8);
    float sceneDepth = NfxUnpackHalf2(s.y).y;
    // soft depth match with a tolerance growing with distance (a hard cut flipped grain on the horizon)
    float tolerance = 0.1 + clip.w * 0.03;
    float match = saturate(2.0 - abs(sceneDepth - clip.w) / tolerance);
    if (match <= 0.0)
        return 0.0;
    color = float3(NfxUnpackHalf2(s.x), NfxUnpackHalf2(s.y).x);
    float edge = min(min(suv.x, 1.0 - suv.x), min(suv.y, 1.0 - suv.y));
    return saturate(edge * 20.0) * match;
}

// ShadeHit for what a reflection shows directly: the sky light blocked by what is around the hit (one short ray),
// a sun highlight as sharp as the hit's smoothness, and the local lights with their shadows.
float3 ShadeReflectedHitWithSky(HitSurface s, float3 hitAWS, float3 rayDir, float sky);

float3 ShadeReflectedHit(HitSurface s, float3 hitAWS, float3 rayDir, float2 xi, out float sky)
{
    float3 hn = s.normal;
    float3 origin = hitAWS + hn * 0.03;
    sky = Blocked(origin, CosineSampleHemisphere(hn, xi), NFX_REFLECT_SKY_TEST, true) ? NFX_SKY_COVERED : 1.0;
    return ShadeReflectedHitWithSky(s, hitAWS, rayDir, sky);
}

// the same with the sky share from four stratified rays: for mirrors, whose one ray per pixel and frame has no
// history to average it (Reflection.hlsl)
float3 ShadeReflectedHitSmooth(HitSurface s, float3 hitAWS, float3 rayDir, uint2 pixel, out float sky)
{
    float3 hn = s.normal;
    float3 origin = hitAWS + hn * 0.03;
    float2 jitter = Hash22(pixel, FrameIndex, 31);
    sky = 0.0;
    [unroll]
    for (int k = 0; k < 4; ++k)
    {
        float2 xi = frac((float2(k & 1, k >> 1) + jitter) * 0.5);
        sky += Blocked(origin, CosineSampleHemisphere(hn, xi), NFX_REFLECT_SKY_TEST, true) ? NFX_SKY_COVERED : 1.0;
    }
    sky *= 0.25;
    return ShadeReflectedHitWithSky(s, hitAWS, rayDir, sky);
}

float3 ShadeReflectedHitWithSky(HitSurface s, float3 hitAWS, float3 rayDir, float sky)
{
    float3 hn = s.normal;
    float3 origin = hitAWS + hn * 0.03;
    // what a still surface receives, from the GI's radiance cache where it has the cell: the sun, the lamps and every
    // bounce with what blocks the sky (a room's walls lit only by what comes in); else the sky share of the test rays
    float3 cached;
    bool fromCache = !s.moving && CacheLookup(hitAWS, hn, cached);
    float3 irradiance = fromCache ? cached : HitSkyLight(hn) * sky;
    float3 L = SunDir;
    float ndl = saturate(dot(hn, L));
    float3 sunSpecular = 0;
    if (SunUp != 0 && ndl > 0.0 && SunVisible(origin))
    {
        if (!fromCache)
            irradiance += SunColor * ndl * INV_PI;
        float3 V = -rayDir;
        float roughness = 1.0 - s.smoothness;
        float alpha = max(roughness * roughness, 0.002);
        float a2 = alpha * alpha;
        float3 H = normalize(L + V);
        float nh = saturate(dot(hn, H));
        float nv = saturate(dot(hn, V));
        float d = nh * nh * (a2 - 1.0) + 1.0;
        float distribution = a2 / (PI * d * d);
        float visibilityTerm = 0.5 / max(ndl * sqrt(nv * nv * (1.0 - a2) + a2) + nv * sqrt(ndl * ndl * (1.0 - a2) + a2), 1e-4);
        float3 f0 = lerp(0.04, s.albedo, s.metallic);
        float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(V, H)), 5.0);
        sunSpecular = fresnel * min(distribution * visibilityTerm, 100.0) * SunColor * ndl;
    }
    if (!fromCache)
        irradiance += LocalLightsAtHit(hitAWS, hn, true);
    float3 diffuse = s.albedo * irradiance * (1.0 - s.metallic);
    float3 specular = HitFresnel(s, rayDir) * SampleSH9(reflect(rayDir, hn)) * sky;
    return diffuse + specular + sunSpecular + s.emission;
}

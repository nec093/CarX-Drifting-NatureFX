// NFXNative clouds (Sky.hlsli): the cloud layer seen through each sky pixel, at a quarter of the screen's
// resolution. Surface: float4 per pixel (view direction, w 1 where the pixel shows sky). Screen: the sky data buffer
// (transmittance, noise, cloud type map). Probe: the sky view table (the sky's light on the clouds). Output: float4
// per pixel (the clouds' radiance in nits for the sun SkyParams gives, and the transmittance through them).
// AmbientParams: x cumulus, y altostratus, z cumulonimbus (how much of each the weather has, 0..1), w density.
// ReflParams: xy the wind's offset (m, how far the air has moved), z the clouds' age (s), w the aerial perspective
// distance (m). Compiled into Clouds.h.
#include "NFXCommon.hlsli"
#include "Sky.hlsli"
#include "CloudsCommon.hlsli"

float HenyeyGreenstein(float c, float g)
{
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5));
}

// the sky's light on the clouds: the sky view table (in Probe) looked up a little above the horizon, all round
float3 SkyAmbient()
{
    float4 a, b;
    NFX_LUT_SAMPLE(Probe, SkyViewW, SkyViewH, float2(0.25, 0.25), a);
    NFX_LUT_SAMPLE(Probe, SkyViewW, SkyViewH, float2(0.75, 0.25), b);
    return (a.rgb + b.rgb) * 0.5;
}

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 ray = asfloat(Surface.Load4(index * 16));
    float4 result = float4(0.0, 0.0, 0.0, 1.0);
    if (ray.w > 0.5 && (AmbientParams.x + AmbientParams.y + AmbientParams.z) > 0.0)
    {
        float3 dir = normalize(ray.xyz);
        float r = GroundRadius + max(SkyParams.w, 1.0);
        float rBottom = GroundRadius + CloudBottom, rTop = GroundRadius + CloudTop;
        // from the camera (below the layer) to where the view ray leaves the layer, at most 80 km
        float t0 = DistanceToSphere(r, dir.y, rBottom);
        float t1 = DistanceToSphere(r, dir.y, rTop);
        if (r > rBottom)
            t0 = 0.0;
        if (t0 >= 0.0 && t1 > t0 && !HitsGround(r, dir.y))
        {
            t1 = min(t1, t0 + 80000.0);
            const int steps = 64;
            float dt = (t1 - t0) / steps;
            float jitter = IGN(float2(id.xy) + 5.588238 * (FrameIndex % 64u));
            float3 sun = SunDir;
            float c = dot(dir, sun);
            // a bright forward lobe (silver linings) and a dim backward one
            float phase = lerp(HenyeyGreenstein(c, 0.75), HenyeyGreenstein(c, -0.25), 0.25);
            float3 ambient = SkyAmbient();
            float3 sunAbove = SkySun();
            float3 L = 0.0;
            float T = 1.0;
            float weightedT = 0.0, weight = 0.0;
            for (int i = 0; i < steps && T > 0.01; ++i)
            {
                float t = t0 + (i + jitter) * dt;
                float3 p = float3(0.0, r, 0.0) + dir * t;
                float rp = length(p);
                float h = rp - GroundRadius;
                float2 xz = Camera.xz + dir.xz * t;
                float density = CloudDensity(xz, h, false);
                if (density <= 0.0)
                    continue;
                float sigmaT = density * CloudExtinction;
                // towards the sun: the optical depth of the cloud above, in growing steps
                float3 up = p / rp;
                float od = 0.0;
                float ls = 40.0, lt = 0.0;
                [unroll] for (int k = 0; k < 6; ++k)
                {
                    lt += ls;
                    float3 q = p + sun * lt;
                    float rq = length(q);
                    od += CloudDensity(xz + sun.xz * lt, rq - GroundRadius, true) * CloudExtinction * ls;
                    ls *= 2.0;
                }
                // multiple scattering as octaves (Wrenninge): each order sees less of the cloud and scatters wider
                float sunLight = 0.0, a = 1.0, b = 1.0, g = 1.0;
                [unroll] for (int o = 0; o < 3; ++o)
                {
                    sunLight += a * exp(-od * b) * lerp(HenyeyGreenstein(c, 0.75 * g), HenyeyGreenstein(c, -0.25 * g), 0.25);
                    a *= 0.5; b *= 0.4; g *= 0.5;
                }
                // dark rims where the light only just entered (the "powder" look of a heap's sunlit side)
                float powder = 1.0 - exp(-sigmaT * 60.0);
                sunLight *= lerp(1.0, powder, 0.5);
                float3 sunAtSample = sunAbove * SkyTransmittance(rp, dot(up, sun));
                float hf = saturate((h - CloudBottom) / (CloudTop - CloudBottom));
                float3 S = CloudAlbedo * sigmaT * (sunAtSample * sunLight + ambient * lerp(0.35, 1.0, hf));
                float sampleT = exp(-sigmaT * dt);
                L += T * (S - S * sampleT) / sigmaT;
                weightedT += t * T * (1.0 - sampleT);
                weight += T * (1.0 - sampleT);
                T *= sampleT;
            }
            // far clouds fade into the sky's haze
            float distance = weight > 0.0 ? weightedT / weight : t0;
            float fade = exp(-distance / max(ReflParams.w, 1000.0));
            // within half range: the target and its history are 16 bit floats, and a thin cloud across the sun went
            // past 65504 (inf, then NaN spreading through the temporal history as holes around the sun)
            result = float4(min(L * fade, 60000.0), lerp(1.0, T, fade));
        }
    }
    Output.Store4(index * 16, asuint(result));
}

// NFXNative sky: the Earth's atmosphere as HDRP's physically based sky models it, after Hillaire, "A Scalable and
// Production Ready Sky and Atmosphere Rendering Technique" (EGSR 2020). Three look-up tables, all float4 texels in
// raw buffers (the engine shares no textures with Unity):
//   transmittance (SkyTransmittance.hlsl): how much sun light reaches a height from a direction, by height and
//     zenith cosine (Bruneton's parameterisation), computed when the air changes;
//   multiple scattering (SkyMultiScatter.hlsl): the light scattered twice and more, as an isotropic source, by
//     height and sun zenith cosine;
//   sky view (SkyView.hlsl): the sky's radiance from the camera, by view zenith and azimuth from the sun, every
//     frame; Unity unpacks it (NativeEngine.TraceSky) and the composite (NFX_RTShadow) draws the sky pixels with it.
// Units: metres; radiance in nits for the sun's illuminance in lux (SkyParams.xyz, above the air).

static const float GroundRadius = 6360000.0;
static const float TopRadius = 6460000.0;
static const float3 RayleighScattering = float3(5.802e-6, 13.558e-6, 33.1e-6);
static const float RayleighHeight = 8000.0;
static const float MieScattering = 3.996e-6;
static const float MieAbsorption = 4.40e-6;
static const float MieHeight = 1200.0;
static const float MieG = 0.8;
static const float3 OzoneAbsorption = float3(0.650e-6, 1.881e-6, 0.085e-6);
static const float3 GroundAlbedo = float3(0.3, 0.3, 0.3);

static const uint TransmittanceW = 256, TransmittanceH = 64;
static const uint MultiScatterSize = 32;
static const uint SkyViewW = 192, SkyViewH = 108;

// SkyParams: xyz the sun's illuminance above the air (lux), w the camera's height above the ground (m);
// LightCount.y aerosols (Mie) relative to a clear day, .z ozone relative to normal
float3 SkySun() { return SkyParams.xyz; }
float AerosolScale() { return max(LightCount.y, 0.0); }

struct Medium
{
    float3 scattering;     // total
    float3 extinction;
    float3 rayleigh;       // scattering of each, for the phase functions
    float mie;
};

Medium MediumAt(float height)
{
    Medium m;
    float rd = exp(-max(height, 0.0) / RayleighHeight);
    float md = exp(-max(height, 0.0) / MieHeight) * AerosolScale();
    float od = max(0.0, 1.0 - abs(height - 25000.0) / 15000.0);
    m.rayleigh = RayleighScattering * rd;
    m.mie = MieScattering * md;
    m.scattering = m.rayleigh + m.mie;
    m.extinction = m.scattering + MieAbsorption * md + OzoneAbsorption * od;
    return m;
}

float RayleighPhase(float c)
{
    return 3.0 / (16.0 * PI) * (1.0 + c * c);
}

// Cornette-Shanks, as HDRP and Hillaire use for the Mie lobe
float MiePhase(float c)
{
    float g = MieG, g2 = g * g;
    float k = 3.0 / (8.0 * PI) * (1.0 - g2) / (2.0 + g2);
    return k * (1.0 + c * c) / pow(max(1.0 + g2 - 2.0 * g * c, 1e-4), 1.5);
}

// distance from radius r along zenith cosine mu to the sphere of radius R (the far hit), -1 for none
float DistanceToSphere(float r, float mu, float R)
{
    float d = r * r * (mu * mu - 1.0) + R * R;
    return d < 0.0 ? -1.0 : -r * mu + sqrt(d);
}

bool HitsGround(float r, float mu)
{
    return mu < 0.0 && r * r * (mu * mu - 1.0) + GroundRadius * GroundRadius >= 0.0;
}

float DistanceToGround(float r, float mu)
{
    return -r * mu - sqrt(max(r * r * (mu * mu - 1.0) + GroundRadius * GroundRadius, 0.0));
}

// Bruneton's transmittance parameterisation: uv from height and zenith cosine, and back
float2 TransmittanceUv(float r, float mu)
{
    float H = sqrt(TopRadius * TopRadius - GroundRadius * GroundRadius);
    float rho = sqrt(max(r * r - GroundRadius * GroundRadius, 0.0));
    float d = max(DistanceToSphere(r, mu, TopRadius), 0.0);
    float dMin = TopRadius - r, dMax = rho + H;
    return float2((d - dMin) / max(dMax - dMin, 1e-3), rho / H);
}

void TransmittanceFromUv(float2 uv, out float r, out float mu)
{
    float H = sqrt(TopRadius * TopRadius - GroundRadius * GroundRadius);
    float rho = H * uv.y;
    r = sqrt(rho * rho + GroundRadius * GroundRadius);
    float dMin = TopRadius - r, dMax = rho + H;
    float d = dMin + uv.x * (dMax - dMin);
    mu = d == 0.0 ? 1.0 : clamp((H * H - rho * rho - d * d) / (2.0 * r * d), -1.0, 1.0);
}

// bilinear fetch of a float4 table in a raw buffer, clamped at the edges
#define NFX_LUT_SAMPLE(buffer, w, h, uv, result)                                          \
    {                                                                                        \
        float2 p_ = saturate(uv) * float2(w, h) - 0.5;                                       \
        int2 i_ = (int2)floor(p_);                                                           \
        float2 f_ = p_ - i_;                                                                 \
        int2 a_ = clamp(i_, 0, int2(w, h) - 1), b_ = clamp(i_ + 1, 0, int2(w, h) - 1);       \
        float4 t00_ = asfloat(buffer.Load4((a_.y * w + a_.x) * 16));                        \
        float4 t10_ = asfloat(buffer.Load4((a_.y * w + b_.x) * 16));                        \
        float4 t01_ = asfloat(buffer.Load4((b_.y * w + a_.x) * 16));                        \
        float4 t11_ = asfloat(buffer.Load4((b_.y * w + b_.x) * 16));                        \
        result = lerp(lerp(t00_, t10_, f_.x), lerp(t01_, t11_, f_.x), f_.y);                 \
    }

// the transmittance table is in Screen (t4) for the passes that read it
float3 SkyTransmittance(float r, float mu)
{
    if (HitsGround(r, mu))
        return 0.0;
    float4 t;
    NFX_LUT_SAMPLE(Screen, TransmittanceW, TransmittanceH, TransmittanceUv(r, mu), t);
    return t.rgb;
}

// the multiple scattering table is in Surface (u1) for the sky view
float3 SkyMultiScatter(float r, float muSun)
{
    float2 uv = float2(muSun * 0.5 + 0.5, saturate((r - GroundRadius) / (TopRadius - GroundRadius)));
    float4 t;
    NFX_LUT_SAMPLE(Surface, MultiScatterSize, MultiScatterSize, uv, t);
    return t.rgb;
}

// ------------------------------------------------------------------ clouds (CloudNoise.hlsl, Clouds.hlsl)
// The clouds' noise and cloud type map live in the same buffer as the transmittance table, after it (the passes
// that read the sky's tables read them from Screen as well): a 64³ Perlin-Worley shape noise, a 32³ Worley detail
// noise (one float each), and a 256² map of where each cloud type may form (float4: cumulus, altostratus,
// cumulonimbus, rain).
static const uint CloudShapeSize = 64, CloudDetailSize = 32, CloudMapSize = 256;
static const uint CloudShapeOffset = TransmittanceW * TransmittanceH * 16;
static const uint CloudDetailOffset = CloudShapeOffset + CloudShapeSize * CloudShapeSize * CloudShapeSize * 4;
static const uint CloudMapOffset = CloudDetailOffset + CloudDetailSize * CloudDetailSize * CloudDetailSize * 4;
static const uint SkyDataBytes = CloudMapOffset + CloudMapSize * CloudMapSize * 16; // NativeEngine.SkyDataFloat4s * 16

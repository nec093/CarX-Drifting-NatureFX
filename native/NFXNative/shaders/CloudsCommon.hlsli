// NFXNative clouds: the cloud layer's density, shared by the clouds seen (Clouds.hlsl) and their shadows
// (CloudShadow.hlsl). Heights are above the ground (world y 0). AmbientParams and ReflParams as Clouds.hlsl says.
static const float CloudBottom = 1500.0;   // above the ground (m)
static const float CloudTop = 5500.0;
static const float MapTile = 32000.0;      // the cloud type map repeats every 32 km
static const float ShapeTile = 4000.0;
static const float DetailTile = 700.0;
static const float CloudExtinction = 0.05; // 1/m at density 1 (a cumulus' interior)
static const float CloudAlbedo = 0.99;

float ShapeNoise(float3 p)
{
    float3 q = frac(p / ShapeTile) * CloudShapeSize - 0.5;
    int3 i = (int3)floor(q);
    float3 f = q - i;
    float result = 0.0;
    [unroll] for (int z = 0; z < 2; ++z)
    [unroll] for (int y = 0; y < 2; ++y)
    [unroll] for (int x = 0; x < 2; ++x)
    {
        int3 c = (i + int3(x, y, z) + (int)CloudShapeSize) % (int)CloudShapeSize;
        float v = asfloat(Screen.Load(CloudShapeOffset + ((c.z * CloudShapeSize + c.y) * CloudShapeSize + c.x) * 4));
        result += v * (x ? f.x : 1.0 - f.x) * (y ? f.y : 1.0 - f.y) * (z ? f.z : 1.0 - f.z);
    }
    return result;
}

float DetailNoise(float3 p)
{
    float3 q = frac(p / DetailTile) * CloudDetailSize - 0.5;
    int3 i = (int3)floor(q);
    float3 f = q - i;
    float result = 0.0;
    [unroll] for (int z = 0; z < 2; ++z)
    [unroll] for (int y = 0; y < 2; ++y)
    [unroll] for (int x = 0; x < 2; ++x)
    {
        int3 c = (i + int3(x, y, z) + (int)CloudDetailSize) % (int)CloudDetailSize;
        float v = asfloat(Screen.Load(CloudDetailOffset + ((c.z * CloudDetailSize + c.y) * CloudDetailSize + c.x) * 4));
        result += v * (x ? f.x : 1.0 - f.x) * (y ? f.y : 1.0 - f.y) * (z ? f.z : 1.0 - f.z);
    }
    return result;
}

float4 CloudMap(float2 p)
{
    float2 q = frac(p / MapTile) * CloudMapSize - 0.5;
    int2 i = (int2)floor(q);
    float2 f = q - i;
    int2 a = (i + (int)CloudMapSize) % (int)CloudMapSize, b = (i + 1 + (int)CloudMapSize) % (int)CloudMapSize;
    float4 t00 = asfloat(Screen.Load4(CloudMapOffset + (a.y * CloudMapSize + a.x) * 16));
    float4 t10 = asfloat(Screen.Load4(CloudMapOffset + (a.y * CloudMapSize + b.x) * 16));
    float4 t01 = asfloat(Screen.Load4(CloudMapOffset + (b.y * CloudMapSize + a.x) * 16));
    float4 t11 = asfloat(Screen.Load4(CloudMapOffset + (b.y * CloudMapSize + b.x) * 16));
    return lerp(lerp(t00, t10, f.x), lerp(t01, t11, f.x), f.y);
}

// density 0..1 at a world position (xz absolute, h above the ground). Each cloud type has its own height profile:
// cumulus flat bottomed heaps whose tops rise with the cover, altostratus a thin high sheet, cumulonimbus the whole
// layer. cheap: no detail erosion (the sun's light march)
float CloudDensity(float2 xz, float h, bool cheap)
{
    float hf = (h - CloudBottom) / (CloudTop - CloudBottom);
    if (hf <= 0.0 || hf >= 1.0)
        return 0.0;
    float2 wind = ReflParams.xy;
    float4 map = CloudMap(xz + wind);
    float cumulus = map.x * AmbientParams.x, alto = map.y * AmbientParams.y, tower = map.z * AmbientParams.z;
    float cumulusTop = 0.12 + 0.3 * cumulus;
    float coverage = max(max(
        cumulus * smoothstep(0.0, 0.05, hf) * smoothstep(cumulusTop, cumulusTop * 0.55, hf),
        alto * smoothstep(0.62, 0.66, hf) * smoothstep(0.76, 0.7, hf)),
        tower * smoothstep(0.0, 0.03, hf) * smoothstep(1.0, 0.8, hf));
    if (coverage < 0.01)
        return 0.0;
    // the shapes travel with the wind, faster higher up (shear), and change slowly as they go
    float3 p = float3(xz + wind * (1.0 + 0.3 * hf), h + ReflParams.z * 0.6);
    float shape = ShapeNoise(p);
    float base = saturate((shape - (1.0 - coverage)) / max(coverage, 1e-3));
    if (base <= 0.0)
        return 0.0;
    if (!cheap)
    {
        // the edges eaten away into billows, more at the bottom (wispy bases) than the tops
        float detail = DetailNoise(p + float3(0.0, ReflParams.z * 1.5, 0.0));
        float erosion = detail * lerp(0.35, 0.2, hf);
        base = saturate((base - erosion) / (1.0 - erosion));
    }
    return base * AmbientParams.w;
}


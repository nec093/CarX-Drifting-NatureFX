// NFXNative: what every trace shader of the engine shares. Root signature (NFXNative.cpp, CreateTracePipelines):
// t0 the scene, u0 the result, b0 the frame constants, u1 the surface Unity prepared (NFX_NativeBridge.compute),
// t2 the instance records, t3 HDRP's ambient probe, t4 the screen (camera colour and depth, for reflections); the
// descriptor heap is indexed directly (shader model 6.6).
// Helpers shared by every trace kind.
#include "NfxPack.hlsli"
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);
RWByteAddressBuffer Surface : register(u1);
ByteAddressBuffer Records : register(t2);  // RecordSize bytes per instance (InstanceID), see NFXNative.cpp
ByteAddressBuffer Probe : register(t3);    // HDRP's diffuse ambient probe: 7 float4 (SampleSH9)
ByteAddressBuffer Screen : register(t4);   // per full resolution pixel: colour (4 halves, pre-exposed), linear depth
RWByteAddressBuffer Cache : register(u2); // world space radiance cache (GI), see the cache section below
SamplerState LinearWrap : register(s0);

#define NFX_MAX_LOCAL_LIGHTS 128
static const uint RecordSize = 128;

cbuffer Frame : register(b0)
{
    float3 SunDir;          // towards the sun
    float SunRadius;        // angular radius (radians)
    float3 SunColor;        // illuminance at the ground (lux)
    float CoarseDistance;   // beyond it from the camera, alpha tested surfaces count as solid
    float3 Camera;          // absolute world position
    uint FrameIndex;
    uint Width;
    uint Height;
    uint SunUp;
    uint Flags;             // NFX_FLAG_*
    float4 AmbientParams;   // x ambient ray length, y 1 = bounce light, z sky occlusion length (0 off), w contact strength
    float4 SkyParams;       // xyz the sun above the air (lux), w the camera above the ground (m): Sky.hlsli
    float4 LightCount;      // x lights in use; y aerosols, z ozone (Sky.hlsli), w the brightest lamp's level (lux, CacheReference)
    float4x4 ViewProj;      // camera relative world to clip (reflections: what of a hit is on screen)
    float4 ScreenSize;      // full resolution: xy size, zw 1 / size
    float4 ReflParams;      // x max distance (m), y roughness cutoff, z 1 = second bounce (glass: refraction strength),
                            // w wetness of the ground (reflections stretch along the view, as on a wet road)
    // the local lights last, so their number changes nothing before them
    float4 LightPos[NFX_MAX_LOCAL_LIGHTS];    // xyz absolute position, w range (m)
    float4 LightColor[NFX_MAX_LOCAL_LIGHTS];  // rgb intensity (cd), w 1 = ray traced shadows, 2 = also added (a light HDRP
                                              // does not have: an emissive surface standing in as a lamp)
    float4 LightDir[NFX_MAX_LOCAL_LIGHTS];    // xyz spot direction, w angle scale (0: point light)
    float4 LightSpot[NFX_MAX_LOCAL_LIGHTS];   // x angle offset, y radius of the light's shape (m: soft shadows),
                                              // z a headlight's beam (1 low, 2 high: NFX_BeamPattern), w its roll
};

static const float PI = 3.14159265359;
static const float INV_PI = 0.31830988618;
static const uint NoDescriptor = 0xFFFFFFFFu;
// Flags bits
#define NFX_FLAG_GLASS_SHADOWS 1u  // sun shadow rays pass glass dimmed (tinted windows darker) instead of untouched
#define NFX_FLAG_OWN_GLASS 4u      // Glass.hlsl draws the glass itself (HDRP's glass hidden, roadmap E): see there
#define NFX_FLAG_FOG_LISTS 16u     // FogVolume.hlsl: the lamps' light lists per tile (the bridge's PrepareFogVolume) are
                                   // written (bits 8..10 hold the sun's samples)
#define NFX_FLAG_LAMP_MASK 2u      // HDRP's lamps are shadowed one by one (LampMask.hlsl, roadmap A4): LocalShadows
                                   // then only traces the lights NatureFX adds
// instance masks (NFX_Native_SetInstances): the scene every trace sees, and glass, which only Glass.hlsl looks for
#define NFX_SCENE_MASK 0x01
#define NFX_GLASS_MASK 0x02
#define NFX_GROUND_MASK 0x04  // the ground (terrain, wide flat meshes): GroundMap.hlsl; also in NFX_SCENE_MASK's instances
#define NFX_ROAD_MASK 0x08    // the road among them
#define NFX_FOLIAGE_MASK 0x10 // plants (alpha tested or swaying): the ground map's cover

// ------------------------------------------------------------------ sampling

float IGN(float2 p)
{
    return frac(52.9829189 * frac(dot(p, float2(0.06711056, 0.00583715))));
}

float2 Hash22(uint2 p, uint frame, uint i)
{
    float2 offset = float2(0.7548776662, 0.5698402910) * (frame * 4u + i) * 37.0;
    return float2(IGN(p + offset), IGN(p.yx + offset * 1.7 + 11.3));
}

float3 CosineSampleHemisphere(float3 n, float2 xi)
{
    float phi = 2.0 * PI * xi.x;
    float r = sqrt(xi.y);
    float3 t = normalize(abs(n.y) < 0.999 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(1.0 - xi.y));
}

float3 SampleSunCone(float3 toSun, float radius, float2 xi)
{
    float3 t = normalize(abs(toSun.y) < 0.999 ? cross(toSun, float3(0, 1, 0)) : cross(toSun, float3(1, 0, 0)));
    float3 b = cross(toSun, t);
    float r = radius * sqrt(xi.x);
    float phi = 2.0 * PI * xi.y;
    return normalize(toSun + (t * cos(phi) + b * sin(phi)) * tan(r));
}

// GGX (Trowbridge-Reitz) half vector around n
// GGX half vector with different roughness along t and across it (b = n x t): wet asphalt stretches reflections
// along the view
float3 SampleGGXHalfVectorAniso(float3 n, float3 t, float alphaT, float alphaB, float2 xi)
{
    float3 b = cross(n, t);
    float phi = 2.0 * PI * xi.y;
    float3 h = float3(alphaT * cos(phi), alphaB * sin(phi), 0.0) * sqrt(xi.x / max(1.0 - xi.x, 1e-4));
    h.z = 1.0;
    h = normalize(h);
    return normalize(t * h.x + b * h.y + n * h.z);
}

float3 SampleGGXHalfVector(float3 n, float alpha, float2 xi)
{
    float a2 = alpha * alpha;
    float cosTheta = sqrt((1.0 - xi.x) / (1.0 + (a2 - 1.0) * xi.x));
    float sinTheta = sqrt(max(1.0 - cosTheta * cosTheta, 0.0));
    float phi = 2.0 * PI * xi.y;
    float3 t = normalize(abs(n.y) < 0.999 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    return normalize(t * (sinTheta * cos(phi)) + b * (sinTheta * sin(phi)) + n * cosTheta);
}

uint2 UnpackPixel(float packed)
{
    uint p = asuint(packed);
    return uint2(p & 0xFFFFu, p >> 16);
}

// ------------------------------------------------------------------ instance records
// 0 index buffer, 1 UV buffer, 2 colour texture, 3 cutoff, 4 alpha scale, 5 position buffer, 6 normal buffer,
// 7 metallic, 8-11 texture scale / offset, 12-14 base colour, 15 smoothness, 16 mask map, 17 flags (1 clear coat,
// 2 thin card), 18-21 mask remap (metallic min / max, smoothness min / max), 22 emission map, 24-26 emission colour
// (linear, intensity included: the light the surface gives off, as HDRP's _EmissiveColor), 27 alpha mask size
// (DXR 1.2 device: width | height << 16)

struct Record
{
    uint indices;
    uint uvs;
    uint texture;
    float cutoff;
    float alphaScale;
    uint positions;
    uint normals;
    float metallic;
    float4 st;
    float3 color;
    float smoothness;
    uint mask;
    uint flags;
    float4 remap;
    uint emissionMap;
    float3 emission;
    uint maskSize;
};

Record LoadRecord(uint instance)
{
    uint base = instance * RecordSize;
    uint4 a = Records.Load4(base);
    uint4 b = Records.Load4(base + 16);
    uint4 c = Records.Load4(base + 32);
    uint4 d = Records.Load4(base + 48);
    uint4 e = Records.Load4(base + 64);
    uint4 f = Records.Load4(base + 80);
    uint4 g = Records.Load4(base + 96);
    Record r;
    r.indices = a.x;
    r.uvs = a.y;
    r.texture = a.z;
    r.cutoff = asfloat(a.w);
    r.alphaScale = asfloat(b.x);
    r.positions = b.y;
    r.normals = b.z;
    r.metallic = asfloat(b.w);
    r.st = asfloat(c);
    r.color = asfloat(d.xyz);
    r.smoothness = asfloat(d.w);
    r.mask = e.x;
    r.flags = e.y;
    r.remap = float4(asfloat(e.zw), asfloat(f.xy));
    r.emissionMap = f.z;
    r.emission = asfloat(g.xyz);
    r.maskSize = g.w;
    return r;
}

uint3 LoadTriangle(Record r, uint primitive)
{
    ByteAddressBuffer indices = ResourceDescriptorHeap[r.indices];
    return indices.Load3(primitive * 12);
}

float2 HitUv(Record r, uint3 tri, float2 barycentrics)
{
    if (r.uvs == NoDescriptor)
        return 0.0;
    ByteAddressBuffer uvs = ResourceDescriptorHeap[r.uvs];
    float2 uv0 = asfloat(uvs.Load2(tri.x * 8));
    float2 uv1 = asfloat(uvs.Load2(tri.y * 8));
    float2 uv2 = asfloat(uvs.Load2(tri.z * 8));
    float2 uv = uv0 * (1.0 - barycentrics.x - barycentrics.y) + uv1 * barycentrics.x + uv2 * barycentrics.y;
    return uv * r.st.xy + r.st.zw;
}

// Whether an alpha tested triangle is solid where the ray touched it
bool AlphaSolid(uint instance, uint primitive, float2 barycentrics)
{
    Record r = LoadRecord(instance);
    if (r.texture == NoDescriptor || r.indices == NoDescriptor || r.uvs == NoDescriptor)
        return true;
    float2 uv = HitUv(r, LoadTriangle(r, primitive), barycentrics);
#ifdef NFX_ALPHA_MASK
    // the DXR 1.2 device: the alpha baked into a byte mask (BakeAlpha.hlsl), its size in record word 27
    ByteAddressBuffer mask = ResourceDescriptorHeap[r.texture];
    uint2 size = uint2(r.maskSize & 0xFFFFu, r.maskSize >> 16);
    uint2 texel = min((uint2)(frac(uv) * size), size - 1);
    uint index = texel.y * size.x + texel.x;
    float alpha = ((mask.Load(index & ~3u) >> ((index & 3u) * 8u)) & 0xFFu) / 255.0;
    return alpha * r.alphaScale >= r.cutoff;
#else
    Texture2D texture = ResourceDescriptorHeap[r.texture];
    // a coarse mip: the shadow of a mesh fence blurs anyway, and it keeps the texture reads cheap
    return texture.SampleLevel(LinearWrap, uv, 2.0).a * r.alphaScale >= r.cutoff;
#endif
}

// ------------------------------------------------------------------ rays
// Two ways to trace the same rays: inline ray queries (DXR 1.1, compute shaders: the default) or, with NFX_PIPELINE,
// TraceRay in a ray tracing pipeline (DXR 1.0: each trace kind compiled as a library with a ray generation shader,
// one hit group whose any hit shader runs the alpha test, and a miss shader). The traces only use these helpers, and
// NFX_ENTRY for their entry point, so they compile either way.

bool CoarseAt(float3 pointAWS)
{
    return distance(pointAWS, Camera) > CoarseDistance;
}

// What a ray hit, closest first
struct Hit
{
    float t;
    uint instance;
    uint primitive;
    float2 barycentrics;
    float3x4 objectToWorld;
};

RayDesc MakeRay(float3 origin, float3 direction, float maxDist)
{
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = direction;
    ray.TMin = 0.0;
    ray.TMax = maxDist;
    return ray;
}

#ifdef NFX_PIPELINE

// TraceRay's payload: what the closest hit shader saw (t < 0: missed)
struct NfxPayload
{
    float t;
    uint instance;
    uint primitive;
    float2 barycentrics;
    float4 row0;
    float4 row1;
    float4 row2;
};

[shader("anyhit")]
void NfxAnyHit(inout NfxPayload p, BuiltInTriangleIntersectionAttributes attributes)
{
    // only non opaque (alpha tested) instances get here
    if (!AlphaSolid(InstanceID(), PrimitiveIndex(), attributes.barycentrics))
        IgnoreHit();
}

[shader("closesthit")]
void NfxClosestHit(inout NfxPayload p, BuiltInTriangleIntersectionAttributes attributes)
{
    p.t = RayTCurrent();
    p.instance = InstanceID();
    p.primitive = PrimitiveIndex();
    p.barycentrics = attributes.barycentrics;
    float3x4 m = ObjectToWorld3x4();
    p.row0 = m[0];
    p.row1 = m[1];
    p.row2 = m[2];
}

[shader("miss")]
void NfxMiss(inout NfxPayload p)
{
    p.t = -1.0;
}

NfxPayload NfxTrace(float3 origin, float3 direction, float maxDist, uint flags, uint mask)
{
    NfxPayload p = (NfxPayload)0;
    p.t = -1.0;
    TraceRay(Scene, flags | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES, mask, 0, 0, 0, MakeRay(origin, direction, maxDist), p);
    return p;
}

bool NfxToHit(NfxPayload p, out Hit hit)
{
    hit = (Hit)0;
    if (p.t < 0.0)
        return false;
    hit.t = p.t;
    hit.instance = p.instance;
    hit.primitive = p.primitive;
    hit.barycentrics = p.barycentrics;
    hit.objectToWorld = float3x4(p.row0, p.row1, p.row2);
    return true;
}

// Whether anything lies within maxDist along the ray; alpha tested surfaces cut their holes unless coarse
bool Blocked(float3 origin, float3 direction, float maxDist, bool coarse)
{
    NfxPayload p = (NfxPayload)0;
    p.t = 1.0;  // the miss shader clears it; with the closest hit shader skipped a hit leaves it
    TraceRay(Scene, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_CLOSEST_HIT_SHADER | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES |
             (coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE), NFX_SCENE_MASK, 0, 0, 0, MakeRay(origin, direction, maxDist), p);
    return p.t >= 0.0;
}

// The distance to the first blocker found (any, not the closest: the sun's penumbra), < 0 when none
float FirstBlocker(float3 origin, float3 direction, float maxDist, bool coarse)
{
    return NfxTrace(origin, direction, maxDist, RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | (coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE),
                    NFX_SCENE_MASK).t;
}

bool Closest(float3 origin, float3 direction, float maxDist, bool coarse, out Hit hit)
{
    return NfxToHit(NfxTrace(origin, direction, maxDist, coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE, NFX_SCENE_MASK), hit);
}

// the closest of the instances in mask (alpha tested)
bool ClosestMasked(float3 origin, float3 direction, float maxDist, uint mask, out Hit hit)
{
    return NfxToHit(NfxTrace(origin, direction, maxDist, RAY_FLAG_NONE, mask), hit);
}

// The closest glass along the ray (glass is opaque to this query: no alpha test)
bool ClosestGlass(float3 origin, float3 direction, float maxDist, out Hit hit)
{
    return NfxToHit(NfxTrace(origin, direction, maxDist, RAY_FLAG_FORCE_OPAQUE, NFX_GLASS_MASK), hit);
}

// the trace's entry point: a ray generation shader over the dispatch's width x height
#define NFX_ENTRY(id) void NfxTraceMain(uint3 id); \
    [shader("raygeneration")] void NfxRayGen() { NfxTraceMain(DispatchRaysIndex()); } \
    void NfxTraceMain(uint3 id)

#else

// Whether anything lies within maxDist along the ray; alpha tested surfaces cut their holes unless coarse
bool Blocked(float3 origin, float3 direction, float maxDist, bool coarse)
{
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE, NFX_SCENE_MASK, MakeRay(origin, direction, maxDist));
    while (q.Proceed())
    {
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            AlphaSolid(q.CandidateInstanceID(), q.CandidatePrimitiveIndex(), q.CandidateTriangleBarycentrics()))
            q.CommitNonOpaqueTriangleHit();
    }
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
}

// The distance to the first blocker found (any, not the closest: the sun's penumbra), < 0 when none. With NFX_OMM
// (the DXR 1.2 device) opacity micromaps decide most alpha tested hits without the alpha test.
float FirstBlocker(float3 origin, float3 direction, float maxDist, bool coarse)
{
#ifdef NFX_OMM
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES, RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS> q;
#else
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
#endif
    q.TraceRayInline(Scene, coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE, NFX_SCENE_MASK, MakeRay(origin, direction, maxDist));
    while (q.Proceed())
    {
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            AlphaSolid(q.CandidateInstanceID(), q.CandidatePrimitiveIndex(), q.CandidateTriangleBarycentrics()))
            q.CommitNonOpaqueTriangleHit();
    }
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? q.CommittedRayT() : -1.0;
}

bool Closest(float3 origin, float3 direction, float maxDist, bool coarse, out Hit hit)
{
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, coarse ? RAY_FLAG_FORCE_OPAQUE : RAY_FLAG_NONE, NFX_SCENE_MASK, MakeRay(origin, direction, maxDist));
    while (q.Proceed())
    {
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            AlphaSolid(q.CandidateInstanceID(), q.CandidatePrimitiveIndex(), q.CandidateTriangleBarycentrics()))
            q.CommitNonOpaqueTriangleHit();
    }
    hit = (Hit)0;
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return false;
    hit.t = q.CommittedRayT();
    hit.instance = q.CommittedInstanceID();
    hit.primitive = q.CommittedPrimitiveIndex();
    hit.barycentrics = q.CommittedTriangleBarycentrics();
    hit.objectToWorld = q.CommittedObjectToWorld3x4();
    return true;
}

// the closest of the instances in mask (alpha tested)
bool ClosestMasked(float3 origin, float3 direction, float maxDist, uint mask, out Hit hit)
{
    RayQuery<RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, mask, MakeRay(origin, direction, maxDist));
    while (q.Proceed())
    {
        if (q.CandidateType() == CANDIDATE_NON_OPAQUE_TRIANGLE &&
            AlphaSolid(q.CandidateInstanceID(), q.CandidatePrimitiveIndex(), q.CandidateTriangleBarycentrics()))
            q.CommitNonOpaqueTriangleHit();
    }
    hit = (Hit)0;
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return false;
    hit.t = q.CommittedRayT();
    hit.instance = q.CommittedInstanceID();
    hit.primitive = q.CommittedPrimitiveIndex();
    hit.barycentrics = q.CommittedTriangleBarycentrics();
    hit.objectToWorld = q.CommittedObjectToWorld3x4();
    return true;
}

// The closest glass along the ray (glass is opaque to this query: no alpha test)
bool ClosestGlass(float3 origin, float3 direction, float maxDist, out Hit hit)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, NFX_GLASS_MASK, MakeRay(origin, direction, maxDist));
    q.Proceed();
    hit = (Hit)0;
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT)
        return false;
    hit.t = q.CommittedRayT();
    hit.instance = q.CommittedInstanceID();
    hit.primitive = q.CommittedPrimitiveIndex();
    hit.barycentrics = q.CommittedTriangleBarycentrics();
    hit.objectToWorld = q.CommittedObjectToWorld3x4();
    return true;
}

// the trace's entry point: a compute shader in 8 x 8 groups
#define NFX_ENTRY(id) [numthreads(8, 8, 1)] void main(uint3 id : SV_DispatchThreadID)

#endif

bool SunVisible(float3 originAWS)
{
    return !Blocked(originAWS, SunDir, 2000.0, CoarseAt(originAWS));
}

// ------------------------------------------------------------------ hit shading

struct HitSurface
{
    float3 albedo;
    float3 normal;   // world space, facing the ray: interpolated vertex normals where the mesh has them
    float metallic;
    float smoothness;
    bool coat;       // clear coated (car paint)
    bool thin;       // alpha tested card (leaf, grass, fence)
    bool moving;     // a car's part (instance flag bit 3)
    float3 emission; // light the surface gives off (radiance, before exposure)
};

// The colour texture's mip at a hit, as the rasteriser picks it (a ray cone): the triangle's texels per metre from its
// UV and world areas, the footprint of one pixel's ray at the hit's distance, stretched where the surface is seen at a
// grazing angle (partly: the rasteriser's anisotropic filtering keeps such surfaces sharper than a plain cone).
// Was "a texture spans about 4 m": a building's 2048 texture over 20 m came out four mips too coarse, which blurred
// what the composite rebuilds from this colour (walls and windows in the shade).
static const float NfxPixelAngle = 0.0009; // radians a pixel spans (about 1080 lines over a 55 degree view)
float HitMip(Record r, uint3 tri, Hit hit, float3 rayDir, float3 normal, Texture2D texture)
{
    uint w, h;
    texture.GetDimensions(w, h);
    ByteAddressBuffer positions = ResourceDescriptorHeap[r.positions];
    ByteAddressBuffer uvs = ResourceDescriptorHeap[r.uvs];
    float3 p0 = mul((float3x3)hit.objectToWorld, asfloat(positions.Load3(tri.x * 12)));
    float3 p1 = mul((float3x3)hit.objectToWorld, asfloat(positions.Load3(tri.y * 12)));
    float3 p2 = mul((float3x3)hit.objectToWorld, asfloat(positions.Load3(tri.z * 12)));
    float2 t0 = asfloat(uvs.Load2(tri.x * 8)) * r.st.xy;
    float2 t1 = asfloat(uvs.Load2(tri.y * 8)) * r.st.xy;
    float2 t2 = asfloat(uvs.Load2(tri.z * 8)) * r.st.xy;
    float worldArea = length(cross(p1 - p0, p2 - p0));
    float2 e1 = (t1 - t0) * float2(w, h);
    float2 e2 = (t2 - t0) * float2(w, h);
    float texelArea = abs(e1.x * e2.y - e1.y * e2.x);
    if (worldArea < 1e-12 || texelArea < 1e-12)
        return 0.0;
    float texelsPerMetre = sqrt(texelArea / worldArea);
    float facing = sqrt(max(abs(dot(normal, rayDir)), 0.0625));
    float footprint = hit.t * NfxPixelAngle / facing;
    return clamp(log2(max(footprint * texelsPerMetre, 1e-4)), 0.0, 12.0);
}

// The surface where a ray hit: colour, normal, metalness and smoothness from the instance's mesh and material.
// lod: the colour texture's mip (-1: from the hit distance, as the ray cone of NFX_RTLitHit.hlsl)
HitSurface SurfaceAt(Hit hit, float3 rayDir, float lod)
{
    Record r = LoadRecord(hit.instance);
    HitSurface s;
    s.albedo = r.color;
    s.metallic = r.metallic;
    s.smoothness = r.smoothness;
    s.coat = (r.flags & 1u) != 0;
    s.thin = (r.flags & 2u) != 0;
    s.moving = (r.flags & 4u) != 0;
    s.emission = r.emission;
    s.normal = -rayDir;
    if (r.indices == NoDescriptor || r.positions == NoDescriptor)
        return s;
    uint3 tri = LoadTriangle(r, hit.primitive);
    float3 n;
    if (r.normals != NoDescriptor)
    {
        ByteAddressBuffer normals = ResourceDescriptorHeap[r.normals];
        float3 n0 = asfloat(normals.Load3(tri.x * 12));
        float3 n1 = asfloat(normals.Load3(tri.y * 12));
        float3 n2 = asfloat(normals.Load3(tri.z * 12));
        float2 b = hit.barycentrics;
        n = n0 * (1.0 - b.x - b.y) + n1 * b.x + n2 * b.y;
        // normals go to world space with the inverse transpose; for the rotations and near uniform scales of the
        // game's objects the matrix itself, renormalised, is close enough
    }
    else
    {
        ByteAddressBuffer positions = ResourceDescriptorHeap[r.positions];
        float3 p0 = asfloat(positions.Load3(tri.x * 12));
        float3 p1 = asfloat(positions.Load3(tri.y * 12));
        float3 p2 = asfloat(positions.Load3(tri.z * 12));
        n = cross(p1 - p0, p2 - p0);
    }
    n = mul((float3x3)hit.objectToWorld, n);
    float len = length(n);
    if (len > 1e-12)
    {
        n /= len;
        s.normal = dot(n, rayDir) > 0.0 ? -n : n;
    }
    if (r.uvs != NoDescriptor)
    {
        float2 uv = HitUv(r, tri, hit.barycentrics);
        if (r.texture != NoDescriptor)
        {
            Texture2D texture = ResourceDescriptorHeap[r.texture];
            float mip = lod;
            if (mip < 0.0)
                mip = HitMip(r, tri, hit, rayDir, s.normal, texture);
            s.albedo *= texture.SampleLevel(LinearWrap, uv, mip).rgb;
        }
        if (r.emissionMap != NoDescriptor && any(r.emission > 0.0))
        {
            Texture2D emissionMap = ResourceDescriptorHeap[r.emissionMap];
            s.emission *= emissionMap.SampleLevel(LinearWrap, uv, lod < 0.0 ? 2.0 : lod).rgb;
        }
        if (r.mask != NoDescriptor)
        {
            // HDRP mask map: r metallic, a smoothness, both remapped
            Texture2D mask = ResourceDescriptorHeap[r.mask];
            float4 m = mask.SampleLevel(LinearWrap, uv, lod < 0.0 ? 2.0 : lod);
            s.metallic = lerp(r.remap.x, r.remap.y, m.r);
            s.smoothness = lerp(r.remap.z, r.remap.w, m.a);
        }
    }
    s.albedo = saturate(s.albedo);
    return s;
}

// The glass's opacity at a hit: its material's base alpha times its colour map's (HDRP's transparent glass blends by it)
float GlassOpacity(Hit hit)
{
    Record r = LoadRecord(hit.instance);
    float alpha = r.alphaScale;
    if (r.texture != NoDescriptor && r.indices != NoDescriptor && r.uvs != NoDescriptor)
    {
        Texture2D texture = ResourceDescriptorHeap[r.texture];
        alpha *= texture.SampleLevel(LinearWrap, HitUv(r, LoadTriangle(r, hit.primitive), hit.barycentrics), 0.0).a;
    }
    return saturate(alpha);
}

// Share of the light a pane of glass lets through: clear glass about 90 %, tinted (dark base colour) less
float GlassTransmission(HitSurface g)
{
    float clarity = max(g.albedo.r, max(g.albedo.g, g.albedo.b));
    return 0.9 * lerp(0.25, 1.0, saturate(clarity));
}

float3 SampleSH9(float3 n)
{
    float4 shAr = asfloat(Probe.Load4(0));
    float4 shAg = asfloat(Probe.Load4(16));
    float4 shAb = asfloat(Probe.Load4(32));
    float4 shBr = asfloat(Probe.Load4(48));
    float4 shBg = asfloat(Probe.Load4(64));
    float4 shBb = asfloat(Probe.Load4(80));
    float4 shCr = asfloat(Probe.Load4(96));
    float4 vA = float4(n, 1.0);
    float3 x1 = float3(dot(shAr, vA), dot(shAg, vA), dot(shAb, vA));
    float4 vB = n.xyzz * n.yzzx;
    float3 x2 = float3(dot(shBr, vB), dot(shBg, vB), dot(shBb, vB));
    float vC = n.x * n.x - n.y * n.y;
    return max(x1 + x2 + shCr.rgb * vC, 0.0);
}

float Luminance(float3 c)
{
    return dot(c, float3(0.2126, 0.7152, 0.0722));
}

// (the trace buffers' packing helpers, Nfx*: NfxPack.hlsli, included at the top)

// The sky probe's lower half is the open ground around the map: light from below keeps only its level
float3 HitSkyLight(float3 hn)
{
    float3 sh = SampleSH9(hn);
    return lerp(sh, Luminance(sh), saturate(-hn.y * 2.0));
}

float3 EnvBRDF(float3 f0, float perceptualRoughness, float nv)
{
    const float4 c0 = float4(-1.0, -0.0275, -0.572, 0.022);
    const float4 c1 = float4(1.0, 0.0425, 1.04, -0.04);
    float4 r = perceptualRoughness * c0 + c1;
    float a004 = min(r.x * r.x, exp2(-9.28 * nv)) * r.x + r.y;
    float2 ab = float2(-1.04, 1.04) * a004 + r.zw;
    return f0 * ab.x + ab.y;
}

// Share of light a hit surface reflects towards the ray (its own F0 and roughness)
float3 HitFresnel(HitSurface s, float3 rayDir)
{
    float3 f0 = lerp(0.04, s.albedo, s.metallic);
    return EnvBRDF(f0, 1.0 - s.smoothness, saturate(dot(s.normal, -rayDir)));
}

// A lamp's weight on a point for the blocked share of its light: the diffuse (n.l) and, with shine, a moderately
// rough dielectric lobe (GGX, alpha 0.3, F0 0.04) towards the view. A far lamp puts little diffuse light on a road but
// a long bright reflection on it when wet: weighed by n.l alone, blocking it hardly changed the share, and its
// reflection showed through walls. LampMask.hlsl picks each pixel's lamps by it.
float LampWeight(float3 n, float3 toLight, float3 toView, bool shine)
{
    float ndl = saturate(dot(n, toLight));
    if (!shine || ndl <= 0.0)
        return ndl;
    float3 h = normalize(toLight + toView);
    float nh = saturate(dot(n, h));
    const float a2 = 0.09;
    float t = nh * nh * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * t * t);
    float nv = max(dot(n, toView), 0.05);
    return ndl + PI * 0.04 * D / (4.0 * nv);
}

// A headlight's beam as HeadlightController's cookies draw it (relative to its peak), for what does not see HDRP's
// cookies: kind 1 the low beam for driving on the left (Japan: the cut-off 0.6° below the axis, rising 15° on the left
// to 1° up, the hot spot under it a little to the left, a wide fill, 1.5 % above), 3 the same mirrored (driving on the
// right), 2 the high beam (gathered on the axis). forward: the lamp's axis; dir: from the lamp
// to the point; roll: the lamp's turn about its axis from level (radians: a car leaning in a drift or on a bank).
// soft: degrees the cut-off is softened by (the 3D fog: its cells are wider than the cut-off's half degree, and a
// sharp one stepped from cell to cell, a staircase along the beam's edge)
float NFX_BeamPatternSoft(float kind, float3 forward, float3 dir, float roll, float soft)
{
    if (kind < 0.5)
        return 1.0;
    float3 level = abs(forward.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(0.0, 0.0, 1.0);
    float3 right0 = normalize(cross(level, forward));
    float3 up0 = cross(forward, right0);
    float cr = cos(roll), sr = sin(roll);
    float3 up = up0 * cr - right0 * sr;
    float3 right = cross(up, forward);
    float z = max(dot(dir, forward), 1e-3);
    float h = degrees(atan2(dot(dir, right), z));
    float a = degrees(atan2(dot(dir, up), z));
    // kind 3: the low beam for driving on the right (Europe / US), the pattern mirrored
    if (kind > 2.5)
        h = -h;
    if (kind > 1.5 && kind < 2.5)
    {
        float hot = exp(-(h / 5.0) * (h / 5.0) - (a / 2.5) * (a / 2.5));
        float glow = exp(-(h / 20.0) * (h / 20.0) - (a / 7.0) * (a / 7.0)) * 0.25;
        float nearRoad = a < -2.0 ? exp(-(h / 28.0) * (h / 28.0)) * 0.12 : 0.0;
        return min(1.0, hot + glow + nearRoad);
    }
    float cut = -0.6 + (h < 0.0 ? min(-h * 0.267949, 1.6) : 0.0);
    float below = cut - a;
    float edge = saturate(below / (0.5 + soft) + 0.5);
    float spread = a < -1.2 ? 2.2 : 1.2; // the peak 1.2° down: slower below it than above
    float hotSpot = exp(-((h + 1.5) / 9.0) * ((h + 1.5) / 9.0) - ((a + 1.2) / spread) * ((a + 1.2) / spread));
    float fill = exp(-(h / 22.0) * (h / 22.0)) * exp(-max(0.0, below - 1.0) / 2.0) * 0.25 * saturate((38.0 - sqrt(h * h + a * a)) / 8.0);
    return lerp(0.015, min(1.0, hotSpot + fill), edge);
}

float NFX_BeamPattern(float kind, float3 forward, float3 dir, float roll)
{
    return NFX_BeamPatternSoft(kind, forward, dir, roll, 0.0);
}

float3 LocalLightIlluminance(int i, float3 pointAWS, out float3 toLight, out float dist)
{
    float3 d = LightPos[i].xyz - pointAWS;
    dist = length(d);
    toLight = d / max(dist, 1e-4);
    float range = LightPos[i].w;
    float ratio = dist * dist / (range * range);
    float window = saturate(1.0 - ratio * ratio);
    float attenuation = window * window / max(dist * dist, 1e-4);
    float scale = LightDir[i].w;
    if (scale > 0.0)
    {
        float cone = saturate(dot(-toLight, LightDir[i].xyz) * scale + LightSpot[i].x);
        attenuation *= cone * cone * NFX_BeamPattern(LightSpot[i].z, LightDir[i].xyz, -toLight, LightSpot[i].w);
    }
    return LightColor[i].rgb * attenuation;
}

// Irradiance (lux / pi) the local lights put on a hit; shadowed ones only when traceShadows (each costs a ray, the
// first few lights only: the nearer ones). The last 0.6 m before a light are not tested: car lamps sit inside their
// housing and glass.
float3 LocalLightsAtHit(float3 hitAWS, float3 hn, bool traceShadows)
{
    float3 irradiance = 0;
    int count = min((int)LightCount.x, NFX_MAX_LOCAL_LIGHTS);
    int rays = 0;
    for (int i = 0; i < count; ++i)
    {
        float3 toLight;
        float dist;
        float3 e = LocalLightIlluminance(i, hitAWS, toLight, dist);
        float ndl = saturate(dot(hn, toLight));
        if (ndl <= 0.0 || dot(e, 1.0) <= 1e-3)
            continue;
        if (traceShadows && rays < 4)
        {
            ++rays;
            if (Blocked(hitAWS + hn * 0.03, toLight, max(dist - 0.6, 0.0), false))
                continue;
        }
        irradiance += e * ndl * INV_PI;
    }
    return irradiance;
}

// The light a hit surface sends back along the ray (before exposure): sky (sky = the share that reaches it), sun
// with its shadow, the local lights unshadowed, and the sky's reflection
float3 ShadeHit(HitSurface s, float3 hitAWS, float3 rayDir, float sky)
{
    float3 hn = s.normal;
    float3 irradiance = HitSkyLight(hn) * sky;
    float ndl = saturate(dot(hn, SunDir));
    if (SunUp != 0 && ndl > 0.0 && SunVisible(hitAWS + hn * 0.05))
        irradiance += SunColor * ndl * INV_PI;
    irradiance += LocalLightsAtHit(hitAWS, hn, false);
    float3 diffuse = s.albedo * irradiance * (1.0 - s.metallic);
    float3 specular = HitFresnel(s, rayDir) * SampleSH9(reflect(rayDir, hn)) * sky;
    return diffuse + specular + s.emission;
}

// ------------------------------------------------------------------ radiance cache (GI)
// A world space hash grid of the light surfaces receive (irradiance / pi, the units SampleSH9 gives, before
// exposure): GI rays read it where they hit for the bounces before, and add to it what a second ray from the hit
// finds; GICacheResolve.hlsl averages each frame's samples into it. Cells grow with the distance from the camera,
// and are kept apart by the dominant axis of the surface normal (the two sides of a wall differ).
// Entry (48 bytes): key, sample count, accumulated rgb (fixed point), resolved rgb, frame last used, frames
// accumulated, unused.

static const uint CacheEntries = 1u << 19;
static const uint CacheEntryBytes = 48;
static const uint CacheProbes = 8;
static const uint CacheMaxSamples = 64;

uint HashMix(uint x)
{
    x ^= x >> 16;
    x *= 0x7feb352du;
    x ^= x >> 15;
    x *= 0x846ca68bu;
    x ^= x >> 16;
    return x;
}

// The key and first slot of the cell holding a point with that normal; key is never 0 (an empty slot)
uint CacheKey(float3 pointAWS, float3 normal, out uint slot)
{
    float d = max(distance(pointAWS, Camera), 1.0);
    uint level = (uint)clamp(floor(log2(d / 8.0)) + 1.0, 0.0, 12.0);
    float size = 0.25 * exp2((float)level);
    int3 cell = (int3)floor(pointAWS / size);
    float3 an = abs(normal);
    uint axis = an.x > an.y && an.x > an.z ? 0u : an.y > an.z ? 1u : 2u;
    uint face = axis * 2u + (normal[axis] < 0.0 ? 1u : 0u);
    uint h = HashMix((uint)cell.x * 73856093u ^ HashMix((uint)cell.y * 19349663u ^ HashMix((uint)cell.z * 83492791u ^ (level << 3) ^ face)));
    slot = h % CacheEntries;
    return HashMix(h ^ 0x9e3779b9u) | 1u;
}

// The brightest light level of the scene the GI expects: the sun and the sky on a white surface, or the strongest
// local light at half its range (LightCount.w, LocalLights.LampReference, lux) with a bounce of up to half of it (a
// white wall: albedo 0.8 / pi = 0.25). With the sun's alone, at night (the moon, 0.1 lux) a headlight's bounce off the
// road was cut to a few hundredth.
float CacheReference()
{
    float sky = Luminance(SunColor) * INV_PI + Luminance(SampleSH9(float3(0.0, 1.0, 0.0)));
    return max(sky, max(LightCount.w, 0.0) * 0.5);
}

// The scale the accumulated sums are stored with: about 1000 steps for the brightest level of the scene. The sums
// live one frame (GICacheResolve clears them every frame), so a reference changing between frames is fine.
float CacheScale()
{
    return 1024.0 / max(CacheReference(), 1e-3);
}

// The cached light a point receives; false when the cell has none yet
bool CacheLookup(float3 pointAWS, float3 normal, out float3 irradiance)
{
    irradiance = 0;
    uint slot;
    uint key = CacheKey(pointAWS, normal, slot);
    for (uint i = 0; i < CacheProbes; ++i)
    {
        uint e = (slot + i) % CacheEntries;
        if (Cache.Load(e * CacheEntryBytes) == key)
        {
            irradiance = asfloat(Cache.Load3(e * CacheEntryBytes + 20));
            return Cache.Load(e * CacheEntryBytes + 36) > 0u; // resolved at least once
        }
    }
    return false;
}

// Adds one sample of the light a point receives to its cell (taking a free slot for a new cell)
void CacheAdd(float3 pointAWS, float3 normal, float3 irradiance)
{
    uint slot;
    uint key = CacheKey(pointAWS, normal, slot);
    for (uint i = 0; i < CacheProbes; ++i)
    {
        uint e = (slot + i) % CacheEntries;
        uint original;
        Cache.InterlockedCompareExchange(e * CacheEntryBytes, 0u, key, original);
        if (original != 0u && original != key)
            continue;
        uint count;
        Cache.InterlockedAdd(e * CacheEntryBytes + 4, 1u, count);
        Cache.Store(e * CacheEntryBytes + 32, FrameIndex);
        if (count >= CacheMaxSamples)
            return;
        float scale = CacheScale();
        // a single sample is capped (a hot pixel of sun would overflow the sums)
        uint3 v = (uint3)(min(max(irradiance, 0.0) * scale, 65535.0));
        Cache.InterlockedAdd(e * CacheEntryBytes + 8, v.x);
        Cache.InterlockedAdd(e * CacheEntryBytes + 12, v.y);
        Cache.InterlockedAdd(e * CacheEntryBytes + 16, v.z);
        return;
    }
}

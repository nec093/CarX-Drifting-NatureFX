// NFXNative: the ground seen from above around the camera (GroundMap.cs): what the foot of the plants' cards reads
// (NFX_Ground.hlsl NFX_ApplyFoliageFoot). Each cell one ray straight down onto the ground only (instances flagged ground:
// terrain, wide flat meshes; NFX_GROUND_MASK), its colour, height, normal and kind (road or not: a second ray onto the
// road instances, NFX_ROAD_MASK, hitting at the same place). Traced a band of rows at a time: the dispatch covers
// Width (cells per side) x Height (rows this time) from the first row on.
// Constants: AmbientParams xyz the grid's absolute centre (y: the camera's height), w the cell size (m); ReflParams x
// the first row, y how far above the centre the rays start (m), z cells per side.
// Output: two float4 per cell: albedo rgb and the height (absolute y; -1e9 where no ground), the normal and the kind
// (0 none, 1 ground, 2 road; + 0.4 where plants cover it). Compiled into GroundMap.h.
#include "NFXCommon.hlsli"

// How much a hit face looks up, by the mesh's own normals (not turned to the ray): 1 a floor, 0 a wall, -1 a ceiling
float FaceUp(Hit hit)
{
    Record r = LoadRecord(hit.instance);
    if (r.indices == NoDescriptor || r.positions == NoDescriptor)
        return 1.0;
    uint3 tri = LoadTriangle(r, hit.primitive);
    float3 n;
    if (r.normals != NoDescriptor)
    {
        ByteAddressBuffer normals = ResourceDescriptorHeap[r.normals];
        float2 b = hit.barycentrics;
        n = asfloat(normals.Load3(tri.x * 12)) * (1.0 - b.x - b.y) + asfloat(normals.Load3(tri.y * 12)) * b.x + asfloat(normals.Load3(tri.z * 12)) * b.y;
    }
    else
    {
        // no normals: the winding's face (Unity's front faces wind clockwise seen from the front)
        ByteAddressBuffer positions = ResourceDescriptorHeap[r.positions];
        float3 p0 = asfloat(positions.Load3(tri.x * 12));
        float3 p1 = asfloat(positions.Load3(tri.y * 12));
        float3 p2 = asfloat(positions.Load3(tri.z * 12));
        n = cross(p1 - p0, p2 - p0);
    }
    n = mul((float3x3)hit.objectToWorld, n);
    float len = length(n);
    return len > 1e-12 ? n.y / len : 1.0;
}

NFX_ENTRY(id)
{
    uint n = (uint)ReflParams.z;
    uint row = (uint)ReflParams.x + id.y;
    if (id.x >= Width || id.y >= Height || row >= n)
        return;
    float2 cell = (float2(id.x, row) + 0.5 - n * 0.5) * AmbientParams.w;
    float start = ReflParams.y;
    float3 origin = float3(AmbientParams.x + cell.x, AmbientParams.y + start, AmbientParams.z + cell.y);
    float3 down = float3(0.0, -1.0, 0.0);
    float4 a = float4(0.0, 0.0, 0.0, -1e9);
    float4 b = float4(0.0, 1.0, 0.0, 0.0);
    // down through the ground instances to the floor: only faces that look up count (a garage's ceiling and walls are
    // one flat wide mesh each and passed as ground: the debris lay on the ceiling's height), and of those the first
    // at or below the camera's height wins (indoors, under a bridge), else the first one up there
    Hit hit = (Hit)0;
    bool found = false;
    float travelled = 0.0;
    float camY = AmbientParams.y;
    [loop]
    for (int k = 0; k < 6; ++k)
    {
        Hit h;
        float3 from = origin + down * travelled;
        if (!ClosestMasked(from, down, start * 2.0 - travelled, NFX_GROUND_MASK, h))
            break;
        h.t += travelled;
        travelled = h.t + 0.02;
        if (FaceUp(h) < 0.35)
            continue;
        if (!found)
        {
            hit = h;
            found = true;
        }
        if (origin.y - h.t <= camY + 1.5)
        {
            hit = h;
            break;
        }
    }
    if (found)
    {
        HitSurface s = SurfaceAt(hit, down, 2.0);
        a = float4(s.albedo, origin.y - hit.t);
        Hit road;
        bool onRoad = ClosestMasked(origin + down * max(hit.t - 0.1, 0.0), down, 0.2, NFX_ROAD_MASK, road) && abs(road.t - 0.1) < 0.05;
        // plants over the ground (grass, bushes, low branches up to 3 m): the cover, packed into the kind's
        // fraction (kind + cover * 0.4, so the kind's tests keep working)
        Hit plant;
        float cover = ClosestMasked(origin, down, hit.t, NFX_FOLIAGE_MASK, plant) && hit.t - plant.t < 3.0 ? 1.0 : 0.0;
        b = float4(s.normal, (onRoad ? 2.0 : 1.0) + cover * 0.4);
    }
    uint index = (row * n + id.x) * 2;
    Output.Store4(index * 16, asuint(a));
    Output.Store4(index * 16 + 16, asuint(b));
}

// NFXNative: the ground seen from above around the camera (GroundMap.cs): what the map's ground blend, painted dirt
// and debris read (NFX_Ground.hlsl). Each cell one ray straight down onto the ground only (instances flagged ground:
// terrain, wide flat meshes; NFX_GROUND_MASK), its colour, height, normal and kind (road or not: a second ray onto the
// road instances, NFX_ROAD_MASK, hitting at the same place). Traced a band of rows at a time: the dispatch covers
// Width (cells per side) x Height (rows this time) from the first row on.
// Constants: AmbientParams xyz the grid's absolute centre (y: the camera's height), w the cell size (m); ReflParams x
// the first row, y how far above the centre the rays start (m), z cells per side.
// Output: two float4 per cell: albedo rgb and the height (absolute y; -1e9 where no ground), the normal and the kind
// (0 none, 1 ground, 2 road; + 0.4 where plants cover it). Compiled into GroundMap.h.
#include "NFXCommon.hlsli"

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
    Hit hit;
    if (ClosestMasked(origin, down, start * 2.0, NFX_GROUND_MASK, hit))
    {
        HitSurface s = SurfaceAt(hit, down, 2.0);
        a = float4(s.albedo, origin.y - hit.t);
        Hit road;
        bool onRoad = ClosestMasked(origin, down, start * 2.0, NFX_ROAD_MASK, road) && abs(road.t - hit.t) < 0.05;
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

// NFXNative: the sun's ray traced shadow map for transparent surfaces (SunShadowMap.cs, NFX_TransparentShadow.hlsl).
// A grid across the sun's direction around the camera; each cell one ray from far up the sun's direction down to the
// first thing the sun meets (alpha tested: leaves let it through between them), its distance stored. Traced a band of
// rows at a time: the dispatch covers Width (cells per side) x Height (rows this time) from the first row on.
// Constants: AmbientParams xyz the grid's absolute centre, w the cell size (m); ReflParams x the first row, y how far
// up the sun's direction the rays start (m), z cells per side. Output: a float per cell (1e9 where nothing).
// Compiled into SunMap.h.
#include "NFXCommon.hlsli"

NFX_ENTRY(id)
{
    uint n = (uint)ReflParams.z;
    uint row = (uint)ReflParams.x + id.y;
    if (id.x >= Width || id.y >= Height || row >= n)
        return;
    float3 dir = SunDir;
    float3 helper = abs(dir.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 right = normalize(cross(helper, dir));
    float3 up = cross(dir, right);
    float2 cell = (float2(id.x, row) + 0.5 - n * 0.5) * AmbientParams.w;
    float start = ReflParams.y;
    float3 origin = AmbientParams.xyz + right * cell.x + up * cell.y + dir * start;
    Hit hit;
    float t = Closest(origin, -dir, start * 2.0, false, hit) ? hit.t : 1e9;
    Output.Store((row * n + id.x) * 4, asuint(t));
}

// DXR 1.2 probe: checks micromaps baked by BakeOmm.hlsl. A grid of Size x Size rays straight onto a unit quad at
// z = 0 (uv = xy); per ray what the micromap decided: 1 opaque (a committed hit without any candidate), 0 transparent
// (no hit, no candidate), 2 unknown (a candidate the query had to decide; none is committed here).
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);

static const uint Size = 64;

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Size || id.y >= Size)
        return;
    RayDesc ray;
    ray.Origin = float3((id.x + 0.5) / Size, (id.y + 0.5) / Size, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.0;
    ray.TMax = 10.0;
    RayQuery<RAY_FLAG_NONE, RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    uint candidates = 0;
    while (q.Proceed())
        ++candidates;
    uint state = candidates > 0 ? 2u : q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 1u : 0u;
    Output.Store((id.y * Size + id.x) * 4, state);
}

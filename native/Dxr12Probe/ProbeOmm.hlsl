// DXR 1.2 probe: opacity micromaps with inline ray tracing. Two triangles, both non-opaque: the first one's micromap
// says transparent, the second one's special index fully opaque. Per ray: committed distance (-1: none) and how many
// candidates the query had to decide (an any hit's worth of work; 0 when the micromap decided).
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);

[numthreads(2, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    RayDesc ray;
    ray.Origin = float3(id.x == 0 ? -1.25 : 1.25, -0.3, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.0;
    ray.TMax = 10.0;
    RayQuery<RAY_FLAG_NONE, RAYQUERY_FLAG_ALLOW_OPACITY_MICROMAPS> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    uint candidates = 0;
    while (q.Proceed())
        ++candidates;
    float t = q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? q.CommittedRayT() : -1.0;
    Output.Store2(id.x * 8, uint2(asuint(t), candidates));
}

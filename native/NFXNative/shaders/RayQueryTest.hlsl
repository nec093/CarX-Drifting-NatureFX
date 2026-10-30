// NFXNative self test: one inline ray (DXR 1.1) straight down onto the test triangle at y = 0.
// Output: 1 the ray missed, 2 it hit. Compiled by tools/build_native.ps1 (dxc, cs_6_5) into RayQueryTest.h.
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);

[numthreads(1, 1, 1)]
void main()
{
    RayDesc ray;
    ray.Origin = float3(0.0, 1.0, 0.0);
    ray.Direction = float3(0.0, -1.0, 0.0);
    ray.TMin = 0.0;
    ray.TMax = 10.0;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    Output.Store(0, q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 2u : 1u);
}

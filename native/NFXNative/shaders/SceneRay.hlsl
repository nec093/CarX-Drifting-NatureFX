// NFXNative scene check: one inline ray (DXR 1.1) against the scene the engine built from Unity's meshes.
// Output: the distance to the closest opaque hit, or -1 when nothing was hit. Compiled into SceneRay.h.
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);

cbuffer Ray : register(b0)
{
    float3 Origin;
    float MaxDistance;
    float3 Direction;
    float Pad;
};

[numthreads(1, 1, 1)]
void main()
{
    RayDesc ray;
    ray.Origin = Origin;
    ray.Direction = Direction;
    ray.TMin = 0.0;
    ray.TMax = MaxDistance;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    float t = q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? q.CommittedRayT() : -1.0;
    Output.Store(0, asuint(t));
}

// NFXNative scene check: many inline rays (DXR 1.1) at once against the engine's scene, one per thread.
// Rays: 8 floats each (origin xyz, max distance, direction xyz, unused). Output: 2 words per ray, the distance to
// the closest hit (-1: none) and the index of the instance hit (InstanceID, 0xFFFFFFFF: none). Compiled into
// SceneRays.h.
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);
ByteAddressBuffer Rays : register(t1);

cbuffer Params : register(b0)
{
    uint Count;
};

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Count)
        return;
    float4 a = asfloat(Rays.Load4(id.x * 32));
    float4 b = asfloat(Rays.Load4(id.x * 32 + 16));
    RayDesc ray;
    ray.Origin = a.xyz;
    ray.Direction = b.xyz;
    ray.TMin = 0.0;
    ray.TMax = a.w;
    RayQuery<RAY_FLAG_FORCE_OPAQUE | RAY_FLAG_SKIP_PROCEDURAL_PRIMITIVES> q;
    q.TraceRayInline(Scene, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    bool hit = q.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
    Output.Store2(id.x * 8, uint2(asuint(hit ? q.CommittedRayT() : -1.0), hit ? q.CommittedInstanceID() : 0xFFFFFFFFu));
}

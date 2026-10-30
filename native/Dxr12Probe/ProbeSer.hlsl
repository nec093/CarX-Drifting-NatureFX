// DXR 1.2 probe: shader execution reordering. A ray generation shader traces into a HitObject, lets the GPU
// regroup the threads by what they hit (MaybeReorderThread) and only then runs the closest hit / miss shader.
// Per ray: the distance the closest hit shader saw (-2: miss shader ran).
RaytracingAccelerationStructure Scene : register(t0);
RWByteAddressBuffer Output : register(u0);

struct [raypayload] Payload
{
    float t : write(caller, closesthit, miss) : read(caller);
};

[shader("raygeneration")]
void RayGen()
{
    uint i = DispatchRaysIndex().x;
    RayDesc ray;
    ray.Origin = float3(i == 0 ? -1.25 : (i == 1 ? 1.25 : 5.0), -0.3, -1.0);
    ray.Direction = float3(0.0, 0.0, 1.0);
    ray.TMin = 0.0;
    ray.TMax = 10.0;
    Payload p;
    p.t = -1.0;
    dx::HitObject hit = dx::HitObject::TraceRay(Scene, RAY_FLAG_FORCE_OPAQUE, 0xFF, 0, 1, 0, ray, p);
    dx::MaybeReorderThread(hit);
    dx::HitObject::Invoke(hit, p);
    Output.Store(i * 4, asuint(p.t));
}

[shader("closesthit")]
void Hit(inout Payload p, BuiltInTriangleIntersectionAttributes a)
{
    p.t = RayTCurrent();
}

[shader("miss")]
void Miss(inout Payload p)
{
    p.t = -2.0;
}

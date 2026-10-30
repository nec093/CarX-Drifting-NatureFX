// NFXNative surface trace, one camera ray per full
// resolution pixel to the point the depth buffer gives, read where it hits: colour, metalness and clear coat
// (HDRP's G-buffer holds neither for a custom pass), the exact normal and distance (the G-buffer normal is
// quantised), and whether it is a thin card (leaf). The shadow, ambient and reflection passes read this.
// Surface (NFX_NativeBridge PrepareSurface): float4 per pixel (the point xyz, 1 = something there).
// Output: 2 float4 per pixel (albedo rgb, metallic * 0.49 + 0.5 when clear coated; normal xyz, distance
// from the camera: 0 not found, negative a thin card). Compiled into Surface.h.
#include "NFXCommon.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    float4 p = asfloat(Surface.Load4(index * 16));
    float4 albedo = 0;
    float4 normal = 0;
    if (p.w > 0.5)
    {
        float3 toPixel = p.xyz - Camera;
        float dist = length(toPixel);
        float3 dir = toPixel / max(dist, 1e-4);
        Hit hit;
        if (Closest(Camera, dir, dist * 1.02 + 0.1, false, hit) && abs(hit.t - dist) < max(0.1, dist * 0.02))
        {
            // the camera ray of a pixel: the texture's own mip for its footprint, as the rasteriser picks it
            HitSurface s = SurfaceAt(hit, dir, -1.0);
            float coat = s.coat ? 0.5 : 0.0;
            albedo = float4(s.albedo, saturate(s.metallic) * 0.49 + coat);
            normal = float4(s.normal, s.thin ? -hit.t : hit.t);
        }
        else
        {
            albedo = float4(0.3, 0.3, 0.3, 0.0);
        }
    }
    Output.Store4(index * 32, asuint(albedo));
    Output.Store4(index * 32 + 16, asuint(normal));
}

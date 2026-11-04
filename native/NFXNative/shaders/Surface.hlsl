// NFXNative surface trace, one camera ray per full
// resolution pixel to the point the depth buffer gives, read where it hits: colour, metalness and clear coat
// (HDRP's G-buffer holds neither for a custom pass), the exact normal and distance (the G-buffer normal is
// quantised), and whether it is a thin card (leaf). The shadow, ambient and reflection passes read this.
// Surface (NFX_NativeBridge PrepareSurface): float4 per pixel (the point xyz, 1 = something there).
// Output: uint4 per pixel (albedo rgb and metallic * 0.49 + 0.5 when clear coated in unorm8 (NfxPackUnorm4x8); the
// normal as an octahedron (NfxPackOct16); the distance from the camera as a float: 0 not found, negative a thin card
// (it rebuilds the ray origins: no half); 0). The bridge's UnpackSurface. Compiled into Surface.h.
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
        // only the stretch of the ray where the surface can be: a hit is taken within this window of the depth
        // buffer's distance anyway, and the whole ray from the camera walked most of the scene's BVH for it (the
        // costliest full resolution trace). Something in front of the window (a leaf the depth buffer did not have)
        // made the pixel fall back to grey before; now its surface is found behind it.
        float window = max(0.1, dist * 0.02);
        float start = max(dist - window, 0.0);
        Hit hit;
        if (Closest(Camera + dir * start, dir, dist + window - start, false, hit) && abs(start + hit.t - dist) < window)
        {
            hit.t += start; // from the camera again (the texture's mip goes by it)
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
    Output.Store4(index * 16, uint4(NfxPackUnorm4x8(albedo), NfxPackOct16(normal.xyz), asuint(normal.w), 0u));
}

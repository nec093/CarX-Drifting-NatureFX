// NFXNative sun shadows: one inline ray (DXR 1.1) per trace pixel towards a random point of the sun's disc, or a ray
// tracing pipeline ray (DXR 1.0). NFX_NativeBridge.compute (Prepare) fills Surface with the ray
// origin of every pixel the sun reaches, Unpack copies Output into the shadow pass's sun texture.
// Surface: 2 float4 per pixel (origin xyz, 1 = trace; shading normal xyz, full resolution pixel x | y << 16).
// Output: float2 per pixel (sun visibility, blocker distance where blocked; through glass: the share it lets through
// and the distance times the blocked share). Compiled into SunShadow.h.
#include "NFXCommon.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
#ifdef NFX_PACKED_SUN
    // the DXR 1.2 device: 16 bytes per pixel (PackSun.hlsl), origin xyz and the packed pixel (0xFFFFFFFF: no trace)
    uint4 packed = Surface.Load4(index * 16);
    float4 a = float4(asfloat(packed.xyz), packed.w != 0xFFFFFFFFu ? 1.0 : 0.0);
    float4 b = float4(0.0, 0.0, 0.0, asfloat(packed.w));
#else
    float4 a = asfloat(Surface.Load4(index * 32));
    float4 b = asfloat(Surface.Load4(index * 32 + 16));
#endif
    float2 result = float2(1.0, 0.0);
    if (a.w > 0.5)
    {
        // the blocker distance sets the penumbra: the first blocker found is enough
        float3 dir = SampleSunCone(SunDir, SunRadius, Hash22(UnpackPixel(b.w), FrameIndex, 3));
        float blocker = FirstBlocker(a.xyz, dir, 2000.0, CoarseAt(a.xyz));
        if (blocker >= 0.0)
            result = float2(0.0, max(blocker, 0.01));
#ifndef NFX_PACKED_SUN
        // glass (only in the scene for its own queries) lets most of the sun through: up to two panes (a car's
        // windows on both sides) dim it, tinted ones more. g carries the distance times the blocked share.
        else if ((Flags & NFX_FLAG_GLASS_SHADOWS) != 0u)
        {
            float transmitted = 1.0;
            float3 o = a.xyz;
            float travelled = 0.0;
            float first = 0.0;
            for (int pane = 0; pane < 2; ++pane)
            {
                Hit glass;
                if (!ClosestGlass(o, dir, 2000.0, glass))
                    break;
                HitSurface g = SurfaceAt(glass, dir, 0.0);
                transmitted *= GlassTransmission(g);
                travelled += glass.t;
                if (pane == 0)
                    first = travelled;
                o += dir * (glass.t + 0.01);
                travelled += 0.01;
            }
            if (transmitted < 0.999)
                result = float2(transmitted, max(first, 0.01) * (1.0 - transmitted));
        }
#endif
    }
#ifdef NFX_PACKED_SUN
    Output.Store(index * 4, f32tof16(result.x) | (f32tof16(result.y) << 16));
#else
    Output.Store2(index * 8, asuint(result));
#endif
}

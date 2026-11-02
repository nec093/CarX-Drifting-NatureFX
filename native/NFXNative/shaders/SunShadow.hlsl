// NFXNative sun shadows: one inline ray (DXR 1.1) per trace pixel towards a random point of the sun's disc, or a ray
// tracing pipeline ray (DXR 1.0). NFX_NativeBridge.compute (Prepare) fills Surface with the ray
// origin of every pixel the sun reaches, Unpack copies Output into the shadow pass's sun texture.
// Surface: a uint4 per pixel (origin xyz as floats, the full resolution pixel x | y << 16; 0xFFFFFFFF: no trace).
// Output: 2 halves per pixel (sun visibility, blocker distance where blocked; through glass: the share it lets through
// and the distance times the blocked share). The same 16 / 4 bytes are handed to the DXR 1.2 device as they are
// (RtDevice.inl copies them; its build has no glass in the scene: NFX_NO_GLASS_SHADOWS). Compiled into SunShadow.h.
#include "NFXCommon.hlsli"

NFX_ENTRY(id)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    // the bridge's Prepare: origin xyz and the packed pixel (0xFFFFFFFF: no trace)
    uint4 packed = Surface.Load4(index * 16);
    float4 a = float4(asfloat(packed.xyz), 0.0);
    float4 b = float4(0.0, 0.0, 0.0, asfloat(packed.w));
    float2 result = float2(1.0, 0.0);
    if (packed.w != 0xFFFFFFFFu)
    {
        // rays per pixel: 1, more while DLSS upscales (Flags bits 8..10: count - 1), so the render resolution's
        // fewer pixels keep about the rays of the display's (a moving shadow over still ground has no history to
        // lean on, and its noise was upscaled with it). Averaged: x the share of the sun seen, y the blocker
        // distance times the blocked share, as one ray gives them.
        uint samples = ((Flags >> 8) & 7u) + 1u;
        float2 sum = 0.0;
        for (uint s = 0; s < samples; ++s)
        {
            float2 one = float2(1.0, 0.0);
            // the blocker distance sets the penumbra: the first blocker found is enough
            float3 dir = SampleSunCone(SunDir, SunRadius, Hash22(UnpackPixel(b.w), FrameIndex, 3 + s * 7));
            float blocker = FirstBlocker(a.xyz, dir, 2000.0, CoarseAt(a.xyz));
            if (blocker >= 0.0)
                one = float2(0.0, max(blocker, 0.01));
#ifndef NFX_NO_GLASS_SHADOWS
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
                    one = float2(transmitted, max(first, 0.01) * (1.0 - transmitted));
            }
#endif
            sum += one;
        }
        result = sum / samples;
    }
    Output.Store(index * 4, NfxPackHalf2(result));
}

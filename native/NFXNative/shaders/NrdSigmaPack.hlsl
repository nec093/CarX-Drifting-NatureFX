// NFXNative: SIGMA_SHADOW_TRANSLUCENCY's inputs from one sun trace (Nrd.inl), per trace pixel. Reads the guides Unity
// prepared (NFX_NativeBridge PrepareNrdGuide: world normal and linear roughness; view depth, motion previous - current
// in uv), the sun trace's surfaces (origin w: traced, i.e. facing the sun) and its result (SunShadow.hlsl: lit (1, 0);
// blocked (0, blocker distance); through glass only (the share let through, the first pane's distance times the
// blocked share)), and writes NRD's input textures. Penumbra: none when lit (NRD_FP16_MAX), back facing (0) when the
// pixel faces away from the sun, else from the first occluder's distance and the sun's disc. Translucency: what gets
// through that occluder (glass: its share, grey; anything solid: 0).
// Root: t0 guides (a packed uint4 per pixel, NfxLoadNrdGuide), t1 sun surfaces (a uint4 per pixel: w 0xFFFFFFFF not traced), t2 sun result (2 halves per pixel),
// u0..u4 a table (normal and roughness, view Z, motion, penumbra, translucency), b0 { width, height, tan of the sun's
// angular radius }. Compiled into NrdSigmaPack.h (NRD's Shaders folder on the include path).
#include "NRD.hlsli"
#include "NfxPack.hlsli"

ByteAddressBuffer Guide : register(t0);
ByteAddressBuffer SunSurface : register(t1);
ByteAddressBuffer SunResult : register(t2);
RWTexture2D<float4> OutNormalRoughness : register(u0);
RWTexture2D<float> OutViewZ : register(u1);
RWTexture2D<float2> OutMotion : register(u2);
RWTexture2D<float> OutPenumbra : register(u3);
RWTexture2D<float4> OutTranslucency : register(u4);

cbuffer Params : register(b0)
{
    uint Width;
    uint Height;
    float TanSunRadius;
    float Pad;
};

[numthreads(8, 8, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Width || id.y >= Height)
        return;
    uint index = id.y * Width + id.x;
    NfxNrdGuide guide = NfxLoadNrdGuide(Guide, index);
    float traced = SunSurface.Load(index * 16 + 12) != 0xFFFFFFFFu ? 1.0 : 0.0;
    float2 sun = NfxUnpackHalf2(SunResult.Load(index * 4));
    OutNormalRoughness[id.xy] = NRD_FrontEnd_PackNormalAndRoughness(guide.normal, saturate(guide.roughness), 0.0);
    OutViewZ[id.xy] = guide.viewZ;
    OutMotion[id.xy] = guide.motion;

    float occluder;
    float through;
    if (traced < 0.5)
    {
        occluder = 0.0; // back facing
        through = 0.0;
    }
    else if (sun.x >= 0.999)
    {
        occluder = NRD_FP16_MAX; // lit
        through = 1.0;
    }
    else if (sun.x > 0.0)
    {
        occluder = max(sun.y / max(1.0 - sun.x, 1e-3), 0.01); // glass: the first pane
        through = sun.x;
    }
    else
    {
        occluder = max(sun.y, 0.01); // solid
        through = 0.0;
    }
    OutPenumbra[id.xy] = occluder <= 0.0 ? 0.0 : SIGMA_FrontEnd_PackPenumbra(occluder, TanSunRadius);
    OutTranslucency[id.xy] = SIGMA_FrontEnd_PackTranslucency(occluder <= 0.0 ? 0.0 : occluder, through.xxx);
}

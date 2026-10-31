// NatureFX night vision (NightVision.cs): how the eye sees in the dark, applied to the HDR colour before
// post-processing. Below about 3 cd/m² the rods take over from the cones (mesopic, then scotopic below 0.01 cd/m²):
// colours fade, the picture shifts towards blue-green (the Purkinje shift: rods see 507 nm best, reds go dark first),
// and the dimmest parts get a fine grain. Bright lamps, headlights and lit signs stay in full colour.
Shader "Hidden/NatureFX/NightVision"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_NFX_NightSource);
    float4 _NFX_Night;   // x: strength, y: time, z: grain, w: 1 = show where the rods see (debug)

    float Hash(float2 p)
    {
        p = frac(p * float2(443.897, 441.423));
        p += dot(p, p.yx + 19.19);
        return frac((p.x + p.y) * p.x);
    }

    float4 Night(Varyings v) : SV_Target
    {
        uint2 pixel = uint2(v.positionCS.xy);
        float3 c = LOAD_TEXTURE2D_X(_NFX_NightSource, pixel).rgb;
        float exposure = GetCurrentExposureMultiplier();
        float3 L = c / max(exposure, 1e-8); // cd/m²
        float photopic = dot(L, float3(0.2126, 0.7152, 0.0722));
        // rods: most sensitive in the blue-green, blind to red
        float scotopic = dot(L, float3(0.04, 0.55, 0.62));
        // 0 scotopic (< 0.01 cd/m²) .. 1 photopic (> 3 cd/m²), on a log scale
        float cones = saturate((log10(max(photopic, 1e-6)) + 2.0) / 2.48);
        float rods = (1.0 - smoothstep(0.0, 1.0, cones)) * _NFX_Night.x;
        if (_NFX_Night.w > 0.5)
        {
            return float4(lerp(c, float3(0.0, rods, 0.0) * 0.5 + c * 0.5, 0.8), 1.0);
        }
        if (rods <= 0.001)
        {
            return float4(c, 1.0);
        }
        // what the rods see: no colour, a cool tint, as bright as the rods' response
        float3 rodColor = scotopic * float3(0.62, 0.82, 1.1);
        float3 result = lerp(L, rodColor, rods);
        // the eye's noise in the dark: fine grain, strongest where it is darkest
        float grain = (Hash(float2(pixel) + frac(_NFX_Night.y * 13.7) * 1000.0) - 0.5) * _NFX_Night.z * rods * (1.0 - cones);
        result *= 1.0 + grain * 0.6;
        return float4(max(result * exposure, 0.0), 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            Name "Night"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Night
            ENDHLSL
        }
    }
    Fallback Off
}

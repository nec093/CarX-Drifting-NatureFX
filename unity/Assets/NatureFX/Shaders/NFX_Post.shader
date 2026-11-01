// NatureFX post-processing (roadmap D, PostProcess.cs): what HDRP's exposure, bloom, tonemapping and colour
// adjustments did, NatureFX's own, on the HDR colour before HDRP's post-processing (which then only does TAA, depth of
// field, motion blur and lens flares, on the finished picture: its tonemapping is off).
//   Meter: the scene's luminance (cd/m², the colour over the exposure) as log2 on a 64 x 36 grid; PostProcess.cs reads
//   it back and sets the exposure (eye adaptation) as HDRP's fixed exposure.
//   Bloom: the light a real lens and eye scatter around bright things: a mip chain of the image (13 tap down, tent up;
//   the first down averages by brightness so single bright pixels do not flicker), blended in as a small share of all
//   the light (energy kept, no threshold: a lamp spreads its own light, a grey wall almost none).
//   Final: bloom, the exposure compensation, the tone curve (ACES fitted, Khronos PBR Neutral, AgX, none), then contrast
//   and saturation on the display picture.
Shader "Hidden/NatureFX/Post"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/RenderPass/CustomPass/CustomPassCommon.hlsl"

    TEXTURE2D_X(_NFX_PostSource);   // what is read (the colour, or the bloom level above)
    TEXTURE2D_X(_NFX_PostLow);      // bloom up: the smaller level's result
    TEXTURE2D_X(_NFX_PostBloom);    // final: the bloom (half resolution)
    float4 _NFX_PostSourceSize;     // the texture read: xy size, zw 1 / size
    float4 _NFX_PostTargetSize;     // the target: xy size, zw 1 / size
    float4 _NFX_PostParams;         // x bloom share, y exposure compensation (EV), z contrast (-1..1), w saturation (-1..1)
    float4 _NFX_PostMode;           // x tone curve: 0 ACES, 1 Neutral, 2 AgX, 3 none; y bloom levels

    #define SampleRT(tex, uv) SAMPLE_TEXTURE2D_X_LOD(tex, s_linear_clamp_sampler, (uv) * _RTHandleScale.xy, 0)

    float2 TargetUV(Varyings v)
    {
        return v.positionCS.xy * _NFX_PostTargetSize.zw;
    }

    // --- meter: each grid cell the mean of 4 x 4 samples' log2 luminance (cd/m²)
    float4 Meter(Varyings v) : SV_Target
    {
        float2 uv0 = (floor(v.positionCS.xy)) * _NFX_PostTargetSize.zw;
        float exposure = max(GetCurrentExposureMultiplier(), 1e-10);
        float sum = 0.0;
        for (int y = 0; y < 4; ++y)
        {
            for (int x = 0; x < 4; ++x)
            {
                float2 uv = uv0 + (float2(x, y) + 0.5) * 0.25 * _NFX_PostTargetSize.zw;
                float3 c = SampleRT(_NFX_PostSource, uv).rgb;
                float L = dot(c, float3(0.2126, 0.7152, 0.0722)) / exposure;
                sum += log2(max(L, 1e-5));
            }
        }
        return float4(sum / 16.0, 0.0, 0.0, 1.0);
    }

    // --- bloom: 13 tap down (Jimenez 2014); the first level weights by 1 / (1 + luma) (Karis) against fireflies
    float Karis(float3 c)
    {
        return 1.0 / (1.0 + dot(c, float3(0.2126, 0.7152, 0.0722)));
    }

    float3 Down13(float2 uv, bool karis)
    {
        float2 t = _NFX_PostSourceSize.zw;
        float3 a = SampleRT(_NFX_PostSource, uv + t * float2(-2, -2)).rgb;
        float3 b = SampleRT(_NFX_PostSource, uv + t * float2(0, -2)).rgb;
        float3 c = SampleRT(_NFX_PostSource, uv + t * float2(2, -2)).rgb;
        float3 d = SampleRT(_NFX_PostSource, uv + t * float2(-1, -1)).rgb;
        float3 e = SampleRT(_NFX_PostSource, uv + t * float2(1, -1)).rgb;
        float3 f = SampleRT(_NFX_PostSource, uv + t * float2(-2, 0)).rgb;
        float3 g = SampleRT(_NFX_PostSource, uv).rgb;
        float3 h = SampleRT(_NFX_PostSource, uv + t * float2(2, 0)).rgb;
        float3 i = SampleRT(_NFX_PostSource, uv + t * float2(-1, 1)).rgb;
        float3 j = SampleRT(_NFX_PostSource, uv + t * float2(1, 1)).rgb;
        float3 k = SampleRT(_NFX_PostSource, uv + t * float2(-2, 2)).rgb;
        float3 l = SampleRT(_NFX_PostSource, uv + t * float2(0, 2)).rgb;
        float3 m = SampleRT(_NFX_PostSource, uv + t * float2(2, 2)).rgb;
        if (!karis)
            return (d + e + i + j) * 0.125 + (a + b + f + g) * 0.03125 + (b + c + g + h) * 0.03125
                 + (f + g + k + l) * 0.03125 + (g + h + l + m) * 0.03125;
        // the five boxes, each weighted by its brightness
        float3 b0 = (d + e + i + j) * 0.25, b1 = (a + b + f + g) * 0.25, b2 = (b + c + g + h) * 0.25;
        float3 b3 = (f + g + k + l) * 0.25, b4 = (g + h + l + m) * 0.25;
        float w0 = Karis(b0) * 0.5, w1 = Karis(b1) * 0.125, w2 = Karis(b2) * 0.125, w3 = Karis(b3) * 0.125, w4 = Karis(b4) * 0.125;
        return (b0 * w0 + b1 * w1 + b2 * w2 + b3 * w3 + b4 * w4) / max(w0 + w1 + w2 + w3 + w4, 1e-6);
    }

    float4 DownFirst(Varyings v) : SV_Target
    {
        float3 c = Down13(TargetUV(v), true);
        return float4(any(!isfinite(c)) ? 0.0 : c, 1.0);
    }

    float4 Down(Varyings v) : SV_Target
    {
        return float4(Down13(TargetUV(v), false), 1.0);
    }

    // --- bloom up: this level's down result plus the smaller level's, through a 3 x 3 tent
    float4 Up(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        float2 t = _NFX_PostSourceSize.zw; // the smaller level's texel
        float3 s = SampleRT(_NFX_PostLow, uv + t * float2(-1, -1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(1, -1)).rgb
                 + SampleRT(_NFX_PostLow, uv + t * float2(-1, 1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(1, 1)).rgb
                 + 2.0 * (SampleRT(_NFX_PostLow, uv + t * float2(0, -1)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(-1, 0)).rgb
                        + SampleRT(_NFX_PostLow, uv + t * float2(1, 0)).rgb + SampleRT(_NFX_PostLow, uv + t * float2(0, 1)).rgb)
                 + 4.0 * SampleRT(_NFX_PostLow, uv).rgb;
        return float4(SampleRT(_NFX_PostSource, uv).rgb + s / 16.0, 1.0);
    }

    // --- tone curves (scene linear, exposed -> display linear 0..1)
    float3 AcesFitted(float3 x)
    {
        // Stephen Hill's fit of the ACES RRT + ODT, with its input and output matrices
        const float3x3 inM = { 0.59719, 0.35458, 0.04823, 0.07600, 0.90834, 0.01566, 0.02840, 0.13383, 0.83777 };
        const float3x3 outM = { 1.60475, -0.53108, -0.07367, -0.10208, 1.10813, -0.00605, -0.00327, -0.07276, 1.07602 };
        x = mul(inM, x);
        float3 a = x * (x + 0.0245786) - 0.000090537;
        float3 b = x * (0.983729 * x + 0.4329510) + 0.238081;
        return saturate(mul(outM, a / b));
    }

    float3 PbrNeutral(float3 c)
    {
        // Khronos PBR Neutral: base colours stay as they are up to the shoulder, highlights roll off towards white
        const float startCompression = 0.8 - 0.04;
        const float desaturation = 0.15;
        float x = min(c.r, min(c.g, c.b));
        float offset = x < 0.08 ? x - 6.25 * x * x : 0.04;
        c -= offset;
        float peak = max(c.r, max(c.g, c.b));
        if (peak < startCompression)
            return c;
        const float d = 1.0 - startCompression;
        float newPeak = 1.0 - d * d / (peak + d - startCompression);
        c *= newPeak / peak;
        float g = 1.0 - 1.0 / (desaturation * (peak - newPeak) + 1.0);
        return lerp(c, newPeak, g);
    }

    float3 AgX(float3 c)
    {
        // AgX (Troy Sobotka), the base look: the inset matrix, a log2 encoding over 16.5 stops, the sigmoid as a
        // polynomial fit, the outset matrix; display linear out
        const float3x3 inset = { 0.856627153315983, 0.0951212405381588, 0.0482516061458583,
                                 0.137318972929847, 0.761241990602591, 0.101439036467562,
                                 0.11189821299995, 0.0767994186031903, 0.811302368396859 };
        const float3x3 outset = { 1.1271005818144368, -0.11060664309660323, -0.016493938717834573,
                                  -0.1413297634984383, 1.157823702216272, -0.016493938717834257,
                                  -0.14132976349843826, -0.11060664309660294, 1.2519364065950405 };
        const float minEv = -12.47393, maxEv = 4.026069;
        c = mul(inset, max(c, 1e-10));
        c = saturate((log2(c) - minEv) / (maxEv - minEv));
        float3 x2 = c * c, x4 = x2 * x2;
        c = 15.5 * x4 * x2 - 40.14 * x4 * c + 31.96 * x4 - 6.868 * x2 * c + 0.4298 * x2 + 0.1191 * c - 0.00232;
        c = mul(outset, c);
        // the curve's output is display encoded (sRGB-ish 2.2): back to display linear for HDRP's output
        return pow(saturate(c), 2.2);
    }

    float4 Final(Varyings v) : SV_Target
    {
        float2 uv = TargetUV(v);
        uint2 pixel = (uint2)v.positionCS.xy;
        float3 c = LOAD_TEXTURE2D_X(_NFX_PostSource, pixel).rgb;
        if (_NFX_PostParams.x > 0.0)
        {
            // the bloom chain sums its levels: their mean is the blurred picture
            float3 bloom = SampleRT(_NFX_PostBloom, uv).rgb / max(_NFX_PostMode.y, 1.0);
            c = lerp(c, bloom, _NFX_PostParams.x);
        }
        c = max(c, 0.0) * exp2(_NFX_PostParams.y);
        int mode = (int)_NFX_PostMode.x;
        float3 d = mode == 0 ? AcesFitted(c) : mode == 1 ? PbrNeutral(c) : mode == 2 ? AgX(c) : saturate(c);
        // contrast about mid grey (in a perceptual space), then saturation
        float3 p = pow(max(d, 0.0), 1.0 / 2.2);
        p = saturate((p - 0.5) * (1.0 + _NFX_PostParams.z) + 0.5);
        float luma = dot(p, float3(0.2126, 0.7152, 0.0722));
        p = saturate(lerp(luma, p, 1.0 + _NFX_PostParams.w));
        return float4(pow(p, 2.2), 1.0);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" }
        Cull Off ZWrite Off ZTest Always Blend Off

        Pass { Name "Meter" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Meter
            ENDHLSL }
        Pass { Name "DownFirst" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment DownFirst
            ENDHLSL }
        Pass { Name "Down" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Down
            ENDHLSL }
        Pass { Name "Up" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Up
            ENDHLSL }
        Pass { Name "Final" HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Final
            ENDHLSL }
    }
    Fallback Off
}

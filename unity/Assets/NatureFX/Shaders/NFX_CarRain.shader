// NatureFX rain on car bodies: drawn over the car's own (unchanged) materials by CarRain.cs.
// Beaded drops on upward faces, water running down the sides, and an oily thin-film sheen on wet panels.
Shader "Hidden/NatureFX/CarRain"
{
    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D_X(_NFX_ColorTex);
    float4 _NFX_CarRain;    // x: drops, y: oil film, z: wetness (0..1), w: time
    float4 _NFX_SunDirWS;   // xyz: direction towards the sun

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float3 positionOS : TEXCOORD0;
        float3 normalOS : TEXCOORD1;
        float3 normalWS : TEXCOORD2;
        float3 positionRWS : TEXCOORD3;
        float3 positionAWS : TEXCOORD4;
    };

    float Hash21(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return frac(p.x * p.y);
    }

    float Noise(float2 p)
    {
        float2 i = floor(p);
        float2 f = frac(p);
        f = f * f * (3.0 - 2.0 * f);
        return lerp(lerp(Hash21(i), Hash21(i + float2(1, 0)), f.x), lerp(Hash21(i + float2(0, 1)), Hash21(i + float2(1, 1)), f.x), f.y);
    }

    Varyings Vert(Attributes a)
    {
        Varyings o;
        // UNITY_MATRIX_M is already camera relative in HDRP
        float4x4 m = UNITY_MATRIX_M;
        o.positionRWS = mul(m, float4(a.positionOS, 1.0)).xyz;
        o.positionAWS = GetAbsolutePositionWS(o.positionRWS);
        o.positionCS = mul(UNITY_MATRIX_VP, float4(o.positionRWS, 1.0));
        o.positionOS = a.positionOS;
        o.normalOS = a.normalOS;
        o.normalWS = normalize(mul((float3x3)m, a.normalOS));
        return o;
    }

    // Beads in a 2D plane: returns coverage and the bead's normal offset in that plane.
    float Beads(float2 p, out float2 tilt)
    {
        float2 cell = floor(p);
        float2 f = frac(p);
        float best = 0;
        tilt = 0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float2 c = cell + float2(x, y);
                float h = Hash21(c);
                if (h > 0.75)
                    continue;
                float2 center = float2(x, y) + float2(Hash21(c + 3.7), Hash21(c + 9.1));
                float radius = 0.12 + 0.28 * Hash21(c + 5.3) * Hash21(c + 1.9);
                float2 d = f - center;
                float dist = length(d);
                float m = smoothstep(radius, radius * 0.75, dist);
                if (m > best)
                {
                    best = m;
                    tilt = d / radius;
                }
            }
        }
        return best;
    }

    float4 Frag(Varyings i) : SV_Target
    {
        float3 n = normalize(i.normalWS);
        float3 V = normalize(-i.positionRWS);
        float wet = _NFX_CarRain.z;
        float time = _NFX_CarRain.w;
        float up = saturate(n.y);
        float side = 1.0 - abs(n.y);

        // object space planar projection by dominant axis, so drops stick to the panels while driving
        float3 an = abs(i.normalOS);
        float2 plane = an.y > an.x && an.y > an.z ? i.positionOS.xz : (an.x > an.z ? i.positionOS.zy : i.positionOS.xy);

        float2 tilt;
        float beads = Beads(plane * 22.0, tilt) * _NFX_CarRain.x * wet * saturate(up * 1.5 + 0.25);

        // water running down the sides in world space
        float2 runUV = float2(dot(i.positionAWS.xz, float2(0.7, 0.7)) * 14.0, i.positionAWS.y * 2.0 + time * 0.8);
        float run = smoothstep(0.62, 0.9, Noise(float2(runUV.x, runUV.y * 0.2)) * Noise(runUV * float2(1.0, 0.1) + 7.0) * 2.2);
        run *= side * wet * _NFX_CarRain.x * 0.8;

        float coverage = saturate(max(beads, run));

        // refract the car behind the water, then add a sharp sun glint and a soft sky reflection
        float2 screenUV = i.positionCS.xy * _ScreenSize.zw;
        float2 refractUV = saturate(screenUV - tilt * 0.006 * beads);
        float3 behind = SAMPLE_TEXTURE2D_X_LOD(_NFX_ColorTex, s_linear_clamp_sampler, refractUV * _RTHandleScale.xy, 0).rgb;
        float3 dn = normalize(n + float3(tilt.x, 0.0, tilt.y) * 0.8 * beads);
        float3 r = reflect(-V, dn);
        float glint = pow(saturate(dot(r, _NFX_SunDirWS.xyz)), 400.0) * beads;
        float fresnel = pow(1.0 - saturate(dot(dn, V)), 5.0);
        float luma = dot(behind, float3(0.2126, 0.7152, 0.0722));
        float3 water = behind * (0.8 + 0.2 * (1.0 - fresnel)) + (luma * 0.5 + 0.02) * fresnel + glint * (luma * 20.0 + 0.5);

        // oily thin film on wet horizontal panels: interference colours shift with view angle and thickness
        float3 color = water;
        float alpha = coverage;
        float oil = _NFX_CarRain.y * wet * smoothstep(0.5, 0.9, up);
        if (oil > 0.001)
        {
            float thickness = Noise(i.positionAWS.xz * 3.0 + time * 0.02) * 0.7 + Noise(i.positionAWS.xz * 11.0) * 0.3;
            float phase = thickness * 2.5 + (1.0 - saturate(dot(n, V))) * 1.5;
            float3 film = 0.5 + 0.5 * cos(6.2832 * (phase + float3(0.0, 0.33, 0.67)));
            float filmMask = oil * smoothstep(0.35, 0.75, thickness) * (0.3 + fresnel);
            color = lerp(color, behind * 0.85 + film * (luma * 0.35 + 0.01), filmMask * (1.0 - coverage));
            alpha = max(alpha, filmMask * 0.6);
        }

        return float4(color, saturate(alpha));
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "CarRain"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
    Fallback Off
}

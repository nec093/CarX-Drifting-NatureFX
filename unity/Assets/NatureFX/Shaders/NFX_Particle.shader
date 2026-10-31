// NatureFX particles: falling leaves and petals, rain and its splashes, snow, and the small glowing or floating things
// of the map's fx boxes (src/NatureFX/Map/MapParticles.cs, src/NatureFX/Rendering/Precipitation.cs). The look comes
// from a painted atlas (ParticleAtlas.cs, 4 x 4 tiles); each particle picks its tile from its stable random number.
// Lit here from NatureFX's own light list (ParticleLighting.cs): the sun or the moon, the sky, and the nearest lamps and
// headlights, so rain shows in a headlight beam and under a street light as it does for real.
//   _NFX_PMode  0 leaf / petal: alpha tested, two sided, sunlight shining through it from behind
//               1 rain: a thin refracting streak, bright towards a light behind it, faint against the sky
//               2 snow: a soft white flake
//               3 glow: emissive (fireflies), additive
//               4 mote: dust or pollen lit by the sun (bright in a sunbeam), soft
//   _NFX_PTiles x: first tile, y: number of tiles to pick from
Shader "NatureFX/Particle"
{
    Properties
    {
        _MainTex ("Atlas", 2D) = "white" {}
        _BaseColor ("Tint", Color) = (1, 1, 1, 1)
        _NFX_PMode ("Mode", Float) = 0
        _NFX_PTiles ("Tiles", Vector) = (0, 1, 0, 0)
        _Emission ("Emission (nits)", Float) = 0
        _Cutoff ("Alpha cutoff", Float) = 0.5
        _SrcBlend ("Src blend", Float) = 5
        _DstBlend ("Dst blend", Float) = 10
        _ZWrite ("Z write", Float) = 0
    }

    HLSLINCLUDE

    #pragma target 4.5
    #pragma only_renderers d3d11 vulkan metal

    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    #include "Packages/com.unity.render-pipelines.high-definition/Runtime/ShaderLibrary/ShaderVariables.hlsl"

    TEXTURE2D(_MainTex);
    SAMPLER(sampler_MainTex);

    float4 _BaseColor;
    float _NFX_PMode;
    float4 _NFX_PTiles;
    float _Emission;
    float _Cutoff;

    // ParticleLighting.cs
    float4 _NFX_PSun;            // rgb: sun (or moon) illuminance on a facing surface (lux)
    float4 _NFX_PSunDir;         // xyz: towards the sun / moon
    float4 _NFX_PSky;            // rgb: sky illuminance from above (lux); w: extinction of the air per metre (fades far particles)
    float4 _NFX_PLights[8];      // xyz: absolute world position, w: range (m)
    float4 _NFX_PLightColors[8]; // rgb: intensity (cd), w: cosine of the outer cone (-2 for a point light)
    float4 _NFX_PLightDirs[8];   // xyz: the spot's direction, w: cosine of the inner cone
    float _NFX_PLightCount;

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 color : COLOR;
        float4 uv : TEXCOORD0; // xy: uv, z: stable random (ParticleSystemVertexStream.StableRandomX)
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 color : COLOR;
        float3 uvTile : TEXCOORD0; // xy: atlas uv, z: random
        float3 normalWS : TEXCOORD1;
        float3 positionRWS : TEXCOORD2;
        float2 uvLocal : TEXCOORD3;
    };

    Varyings Vert(Attributes a)
    {
        Varyings o;
        // particles are in world space; UNITY_MATRIX_M is camera relative in HDRP
        o.positionRWS = mul(UNITY_MATRIX_M, float4(a.positionOS, 1.0)).xyz;
        o.positionCS = mul(UNITY_MATRIX_VP, float4(o.positionRWS, 1.0));
        o.normalWS = normalize(mul((float3x3)UNITY_MATRIX_M, a.normalOS));
        o.color = a.color;
        float tile = _NFX_PTiles.x + min(floor(a.uv.z * _NFX_PTiles.y), _NFX_PTiles.y - 1.0);
        float2 cell = float2(fmod(tile, 4.0), 3.0 - floor(tile / 4.0));
        o.uvTile = float3((cell + saturate(a.uv.xy)) * 0.25, a.uv.z);
        o.uvLocal = a.uv.xy;
        return o;
    }

    // light arriving at p from the lamps: rgb illuminance on a surface facing each lamp, and the share of it that comes
    // from behind the particle as seen from the camera (forward scattering through a drop or a leaf)
    void LampLight(float3 positionRWS, float3 V, float3 n, bool twoSided, out float3 facing, out float3 diffuse, out float3 behind)
    {
        facing = 0.0;
        diffuse = 0.0;
        behind = 0.0;
        float3 positionAWS = GetAbsolutePositionWS(positionRWS);
        int count = (int)_NFX_PLightCount;
        [loop]
        for (int i = 0; i < 8; ++i)
        {
            if (i >= count)
                break;
            float3 d = _NFX_PLights[i].xyz - positionAWS;
            float dist2 = max(dot(d, d), 0.04);
            float3 L = d * rsqrt(dist2);
            float range = _NFX_PLights[i].w;
            float window = Sq(saturate(1.0 - Sq(dist2 / (range * range))));
            float spot = 1.0;
            if (_NFX_PLightColors[i].w > -1.5)
            {
                float c = dot(-L, _NFX_PLightDirs[i].xyz);
                spot = smoothstep(_NFX_PLightColors[i].w, _NFX_PLightDirs[i].w, c);
            }
            float3 E = _NFX_PLightColors[i].rgb / dist2 * window * spot;
            float nl = dot(n, L);
            facing += E;
            diffuse += E * (twoSided ? abs(nl) : saturate(nl));
            behind += E * pow(saturate(dot(-V, L)), 6.0);
        }
    }

    float DistanceFade(float3 positionRWS)
    {
        float dist = length(positionRWS);
        // the air's extinction, and nothing right in front of the lens
        return exp(-_NFX_PSky.w * dist) * saturate((dist - 0.3) / 1.2);
    }

    float4 Frag(Varyings i, bool frontFace : SV_IsFrontFace) : SV_Target
    {
        int mode = (int)(_NFX_PMode + 0.5);
        float4 tex = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, i.uvTile.xy);
        float3 V = normalize(-i.positionRWS);
        float exposure = GetCurrentExposureMultiplier();
        float3 tint = _BaseColor.rgb * i.color.rgb;
        float fade = DistanceFade(i.positionRWS);

        if (mode == 0)
        {
            // leaf or petal: cut out, both sides lit; the sun through it from behind makes it glow warmer and deeper
            clip(tex.a - _Cutoff);
            float3 n = normalize(i.normalWS);
            n = dot(n, V) < 0.0 ? -n : n;
            float3 albedo = tex.rgb * tint;
            float3 sunL = _NFX_PSunDir.xyz;
            float front = saturate(dot(n, sunL));
            float back = saturate(dot(-n, sunL));
            float3 through = albedo * sqrt(max(albedo, 1e-4)) * 1.6; // light passing through is filtered twice
            float3 lit = _NFX_PSun.rgb * (albedo * front + through * back * 0.6);
            lit += _NFX_PSky.rgb * albedo * (0.55 + 0.35 * n.y);
            float3 facing, diffuse, behind;
            LampLight(i.positionRWS, V, n, true, facing, diffuse, behind);
            lit += diffuse * albedo + behind * through * 0.5;
            float3 color = lit * INV_PI * exposure;
            return float4(color, 1.0);
        }
        if (mode == 1)
        {
            // rain: the drop refracts the bright world around it into a thin streak. Against the sky it is faint; with a
            // lamp or the sun behind it, it lights up (forward scattering); along the streak it thins out at the ends
            float shape = tex.a;
            float3 facing, diffuse, behind;
            LampLight(i.positionRWS, V, V, true, facing, diffuse, behind);
            float sunBehind = pow(saturate(dot(-V, _NFX_PSunDir.xyz)), 8.0);
            float3 radiance = _NFX_PSky.rgb * 0.5 + _NFX_PSun.rgb * (0.04 + sunBehind * 0.6) + facing * 0.15 + behind * 1.2;
            float3 color = radiance * INV_PI * exposure * tint;
            float alpha = shape * _BaseColor.a * i.color.a * fade;
            return float4(color, alpha);
        }
        if (mode == 2)
        {
            // snow: a soft, very white flake lit from all round
            float3 facing, diffuse, behind;
            LampLight(i.positionRWS, V, V, true, facing, diffuse, behind);
            float3 lit = _NFX_PSun.rgb * (0.55 + 0.45 * saturate(dot(V, _NFX_PSunDir.xyz))) + _NFX_PSky.rgb + facing * 0.8 + behind * 0.6;
            float3 color = lit * 0.9 * tint * INV_PI * exposure;
            return float4(color, tex.a * _BaseColor.a * i.color.a * fade);
        }
        if (mode == 3)
        {
            // glow: emissive, added
            float3 color = _Emission * tint * tex.a * i.color.a * exposure;
            return float4(color, 1.0);
        }
        // mote: dust or pollen; small and bright where the light is behind it (a sunbeam)
        float3 facing, diffuse, behind;
        LampLight(i.positionRWS, V, V, true, facing, diffuse, behind);
        float sunBehind = pow(saturate(dot(-V, _NFX_PSunDir.xyz)), 4.0);
        float3 lit = _NFX_PSun.rgb * (0.3 + sunBehind * 2.5) + _NFX_PSky.rgb * 0.6 + facing * 0.3 + behind * 2.0;
        float3 color = lit * tex.rgb * tint * INV_PI * exposure;
        return float4(color, tex.a * _BaseColor.a * i.color.a * fade);
    }

    ENDHLSL

    SubShader
    {
        Tags { "RenderPipeline" = "HDRenderPipeline" "Queue" = "Transparent" "RenderType" = "Transparent" }
        Pass
        {
            Name "ForwardOnly"
            Tags { "LightMode" = "ForwardOnly" }
            Blend [_SrcBlend] [_DstBlend]
            ZWrite [_ZWrite]
            ZTest LEqual
            Cull Off
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            ENDHLSL
        }
    }
    Fallback Off
}

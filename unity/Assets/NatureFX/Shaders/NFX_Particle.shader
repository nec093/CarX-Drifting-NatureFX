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
//               5 smoke: NatureFX's tyre smoke (a noise puff)
//               6 textured: the game's own particles (GameParticles.cs, roadmap E) with their own texture and colour,
//                 lit as a soft ball of scattering particles and fogged like the rest
//               7 textured glow: the game's own additive particles (exhaust flames, sparks, glows) with their own
//                 texture and colour, glowing at _Emission cd/m² (real units, as the own engine's lamps), added
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
    #include "Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl"
    #define NFX_SUNMAP_ONLY
    #include "Assets/NatureFX/ShaderLibrary/NFX_TransparentShadow.hlsl"
    TEXTURE2D(_NFX_NoiseTex); // ShaderGlobals.EnsureNoiseTexture: r value noise, 256 cells over the texture

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
    float4 _NFX_PLightBeams[8];  // x: a headlight's beam (1 low, 2 high: NFX_BeamPattern), y its roll (radians)

    // the sky's light from the own engine's sky (phase 1c probe, y of the fog's flags) in place of ParticleLighting's
    // estimate; under the 3D fog the air's dimming is in the fog, not in the distance fade
    float4 NFX_ParticleSky()
    {
        float4 v = _NFX_PSky;
        if (_NFX_FogVolumeParams.y > 0.5)
        {
            float3 up = float3(_NFX_SkyLight[0].y + _NFX_SkyLight[0].w, _NFX_SkyLight[1].y + _NFX_SkyLight[1].w,
                               _NFX_SkyLight[2].y + _NFX_SkyLight[2].w) - _NFX_SkyLight[6].rgb;
            v.rgb = PI * max(up, 0.0);
        }
        if (_NFX_FogVolumeParams.x > 0.5)
            v.w = 0.0;
        return v;
    }
    #define _NFX_PSky NFX_ParticleSky()

    struct Attributes
    {
        float3 positionOS : POSITION;
        float3 normalOS : NORMAL;
        float4 color : COLOR;
        float4 uv : TEXCOORD0; // xy: uv, z: stable random (ParticleSystemVertexStream.StableRandomX), w: smoke: 1 = in a shadow (Custom1.x)
        float uv1 : TEXCOORD1; // smoke: the share of the sun its own cloud takes away at the particle (Custom1.y)
    };

    struct Varyings
    {
        float4 positionCS : SV_POSITION;
        float4 color : COLOR;
        float3 uvTile : TEXCOORD0; // xy: atlas uv, z: random
        float3 normalWS : TEXCOORD1;
        float3 positionRWS : TEXCOORD2;
        float2 uvLocal : TEXCOORD3;
        float sunShadow : TEXCOORD4; // smoke: the share of the sun blocked at the particle (TireSmoke.UpdateShadows)
        float smokeShadow : TEXCOORD5; // smoke: the share its own cloud blocks (TireSmoke.UpdateSmokeShadow)
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
        // the game's own texture (mode 6): the particle's uv as it is (its own sheet animation included)
        if (_NFX_PMode > 5.5)
            o.uvTile.xy = a.uv.xy;
        o.uvLocal = a.uv.xy;
        o.sunShadow = _NFX_PMode > 4.5 && _NFX_PMode < 5.5 ? saturate(a.uv.w) : 0.0;
        o.smokeShadow = _NFX_PMode > 4.5 && _NFX_PMode < 5.5 ? saturate(a.uv1) : 0.0;
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
                spot = smoothstep(_NFX_PLightColors[i].w, _NFX_PLightDirs[i].w, c) * NFX_BeamPattern(_NFX_PLightBeams[i].x, _NFX_PLightDirs[i].xyz, -L, _NFX_PLightBeams[i].y);
            }
            // through the smoke on the way (a puff in a cloud of smoke sees the lamp dimmed)
            spot *= NFX_SmokeLampTransmittance(positionAWS, _NFX_PLights[i].xyz);
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

    float4 FragLit(Varyings i, bool frontFace);

    // the own engine's 3D fog over the particle (phase 2b)
    float4 Frag(Varyings i, bool frontFace : SV_IsFrontFace) : SV_Target
    {
        float4 c = FragLit(i, frontFace);
        if (_NFX_FogVolumeParams.x > 0.5)
        {
            float2 uv = i.positionCS.xy * _ScreenSize.zw;
            float distance = length(i.positionRWS);
            float viewDepth = -mul(UNITY_MATRIX_V, float4(i.positionRWS, 1.0)).z;
            float4 fog = NFX_Fog(uv, viewDepth, distance, i.positionRWS / max(distance, 1e-4));
            int mode = (int)(_NFX_PMode + 0.5);
            // glows add their light: only dimmed. The rest as HDRP fogs transparents: the colour seen through the air
            c.rgb = mode == 3 || mode == 7 ? c.rgb * fog.a : NFX_ApplyFog(c.rgb, fog);
        }
        return c;
    }

    float4 FragLit(Varyings i, bool frontFace)
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
            // rain: a drop is a tiny lens: it shows the whole sky above it squeezed into a streak (about the sky's mean
            // radiance, a little less), so against a dark background it shows grey, against the sky hardly at all; a
            // lamp or the sun in front of the eye behind it lights it up (forward scattering, the bright streaks in a
            // headlight's beam). In the shade of a roof, a bridge or the car the sun's part is gone (the traced sun map)
            float shape = tex.a;
            float3 facing, diffuse, behind;
            LampLight(i.positionRWS, V, V, true, facing, diffuse, behind);
            float sunVisible = NFX_SunMapVisibility(i.positionRWS);
            float sunBehind = pow(saturate(dot(-V, _NFX_PSunDir.xyz)), 12.0);
            float3 radiance = _NFX_PSky.rgb * 0.4 + _NFX_PSun.rgb * sunVisible * (0.015 + sunBehind * 0.5) + facing * 0.08 + behind * 1.0;
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
        if (mode == 5)
        {
            // smoke: a soft round puff whose billows are noise, lit as a ball of scattering particles (bright towards
            // the sun and the lamps, much of it from all round by multiple scattering), its edges soft against
            // whatever it touches
            float2 q = i.uvLocal * 2.0 - 1.0;
            float r = length(q);
            if (r >= 1.0)
                discard;
            float2 nuv = i.uvLocal * 5.0 + i.uvTile.z * 37.0 + float2(0.0, _Time.y * 0.15);
            float billows = SAMPLE_TEXTURE2D_LOD(_NFX_NoiseTex, s_linear_repeat_sampler, nuv / 256.0, 0).r * 0.5
                          + SAMPLE_TEXTURE2D_LOD(_NFX_NoiseTex, s_linear_repeat_sampler, nuv * 2.3 / 256.0, 0).r * 0.3
                          + SAMPLE_TEXTURE2D_LOD(_NFX_NoiseTex, s_linear_repeat_sampler, nuv * 5.1 / 256.0, 0).r * 0.2;
            float falloff = saturate(1.0 - r);
            float density = saturate(falloff * falloff * (0.6 + 1.1 * billows) - 0.08);
            // soft against the scene behind (it would cut a hard line into the road and the car)
            float sceneDepth = LinearEyeDepth(LOAD_TEXTURE2D_X(_CameraDepthTexture, (uint2)i.positionCS.xy).r, _ZBufferParams);
            float ownDepth = -mul(UNITY_MATRIX_V, float4(i.positionRWS, 1.0)).z;
            float soft = saturate((sceneDepth - ownDepth) / 0.8);
            // a sphere's normal across the billboard
            float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz, back = UNITY_MATRIX_V[2].xyz;
            float3 n = normalize(q.x * right + q.y * up + sqrt(saturate(1.0 - r * r)) * back);
            float3 sunL = _NFX_PSunDir.xyz;
            float front = saturate(dot(n, sunL) * 0.5 + 0.5);
            float sunBehind = pow(saturate(dot(-V, sunL)), 6.0);
            float3 facing, diffuse, behind;
            LampLight(i.positionRWS, V, n, true, facing, diffuse, behind);
            float3 albedo = tint;
            // the sun's light where it reaches the particle (in a building's or a car's shadow only the sky lights it)
            // and the smoke itself: what of the cloud lies between this puff and the sun (its far side is darker), one
            // value for the whole puff (looked up per pixel of the billboard it drew the map's cells as streaks)
            float smokeSun = 1.0 - i.smokeShadow;
            float3 lit = _NFX_PSun.rgb * (1.0 - i.sunShadow) * smokeSun * (0.25 + 0.75 * front * front + sunBehind * 1.5)
                       + _NFX_PSky.rgb * lerp(0.8, 1.1, smokeSun) + diffuse * 0.7 + behind * 1.2;
            float3 color = lit * albedo * INV_PI * exposure;
            return float4(color, density * _BaseColor.a * i.color.a * soft * fade);
        }
        if (mode == 7)
        {
            // the game's glow: its texture's light (an additive texture: its colour is how bright it is) times the
            // particle's colour and fade, at a real luminance; soft against the scene
            float sceneDepth = LinearEyeDepth(LOAD_TEXTURE2D_X(_CameraDepthTexture, (uint2)i.positionCS.xy).r, _ZBufferParams);
            float ownDepth = -mul(UNITY_MATRIX_V, float4(i.positionRWS, 1.0)).z;
            float soft = saturate((sceneDepth - ownDepth) / 0.3);
            float coverage = _NFX_PTiles.z > 0.5 ? 1.0 : tex.a;
            float3 color = tex.rgb * coverage * tint * i.color.a * _BaseColor.a * _Emission * soft * fade * exposure;
            return float4(color, 1.0);
        }
        if (mode == 6)
        {
            // the game's particle (dust, spray, smoke of its own): its texture and colour as the albedo of a soft ball
            // of scattering particles, lit by the own engine's sun or moon, sky and lamps; soft against the scene
            float2 q = i.uvLocal * 2.0 - 1.0;
            float r2 = saturate(dot(q, q));
            float3 right = UNITY_MATRIX_V[0].xyz, up = UNITY_MATRIX_V[1].xyz, back = UNITY_MATRIX_V[2].xyz;
            float3 n = normalize(q.x * right + q.y * up + sqrt(1.0 - r2) * back);
            float3 sunL = _NFX_PSunDir.xyz;
            float front = saturate(dot(n, sunL) * 0.5 + 0.5);
            float sunBehind = pow(saturate(dot(-V, sunL)), 6.0);
            float3 facingT, diffuseT, behindT;
            LampLight(i.positionRWS, V, n, true, facingT, diffuseT, behindT);
            float3 lit = _NFX_PSun.rgb * (0.25 + 0.75 * front * front + sunBehind * 1.2) + _NFX_PSky.rgb + diffuseT * 0.7 + behindT;
            float sceneDepth = LinearEyeDepth(LOAD_TEXTURE2D_X(_CameraDepthTexture, (uint2)i.positionCS.xy).r, _ZBufferParams);
            float ownDepth = -mul(UNITY_MATRIX_V, float4(i.positionRWS, 1.0)).z;
            float soft = saturate((sceneDepth - ownDepth) / 0.5);
            float3 color = lit * tex.rgb * tint * INV_PI * exposure;
            // a texture without alpha (_NFX_PTiles.z = 1): its brightness is the coverage (a soft white dot on black)
            float coverage = _NFX_PTiles.z > 0.5 ? saturate(max(tex.r, max(tex.g, tex.b))) : tex.a;
            if (_NFX_PTiles.z > 0.5)
                color = lit * tint * INV_PI * exposure;
            return float4(color, coverage * _BaseColor.a * i.color.a * soft * fade);
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

// NatureFX rain on car bodies and windows: drawn over the car's own (unchanged) materials and over the own engine's
// glass by CarRain.cs. Water as water behaves on a panel: small drops land, grow and dry; where the panel slopes, big
// drops gather, slide down as teardrops weaving a little, leave a trail of droplets and wipe the small ones off their
// path; the way down is gravity along the panel plus the air the car drives through (its square: at speed the drops
// on a windscreen run up and back). Each drop is a lens: the scene behind it seen bent and upside down, a dark rim
// where the light is reflected inside it, the sky and the sun's glint on its dome. Oily sheen and dirt as before.
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
    float4 _NFX_CarDirt;    // x: mud on the lower body, y: dust, z: snow on top, w: the car's ground height (absolute world)
    float4 _NFX_CarLight;   // x: sun illuminance on a facing panel (lux), y: sky illuminance (lux); for the dirt's own shading
    float4 _NFX_CarAir;     // xyz: the car's velocity (m/s, world): the air it drives through blows the drops (set per car)

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

    // --- water drops on a panel, in its plane (metres; +y the way down the flow)
    // A drop's dome: its coverage, and in tilt the slope of its surface (the lens's normal, -1..1 at the rim)
    float Dome(float2 d, float2 radius, out float2 tilt)
    {
        float2 q = d / radius;
        float r2 = dot(q, q);
        tilt = r2 < 1.0 ? q : 0.0;
        return smoothstep(1.0, 0.85, r2);
    }

    // Small drops landing: each 2.5 cm cell holds one that lands at its moment, grows over a while and dries (or is
    // wiped). size: their scale with the rain
    float Droplets(float2 p, float time, float rate, out float2 tilt)
    {
        const float scale = 40.0;
        float2 q = p * scale;
        float2 cell = floor(q);
        float best = 0.0;
        tilt = 0.0;
        [unroll]
        for (int y = -1; y <= 1; ++y)
        {
            [unroll]
            for (int x = -1; x <= 1; ++x)
            {
                float2 c = cell + float2(x, y);
                float h = Hash21(c);
                // its life: lands, grows, lingers, dries
                float life = frac(time * rate * (0.6 + 0.8 * Hash21(c + 4.4)) + h);
                float grow = saturate(life * 6.0) * saturate((1.0 - life) * 3.0);
                if (grow <= 0.0)
                    continue;
                float2 centre = c + float2(Hash21(c + 3.7), Hash21(c + 9.1));
                float size = (0.08 + 0.22 * Hash21(c + 5.3) * Hash21(c + 1.9)) * sqrt(grow);
                // not round: a drop sits where the panel let it, a little wider than tall or the other way
                float2 radius = size * float2(1.0 + 0.25 * (Hash21(c + 2.2) - 0.5), 1.0 - 0.15 * Hash21(c + 6.6));
                float2 t;
                float m = Dome(q - centre, radius, t);
                if (m > best)
                {
                    best = m;
                    tilt = t;
                }
            }
        }
        return best;
    }

    // Big drops sliding down the flow in lanes 7 cm apart: a teardrop (its tail up the flow), weaving a little, faster
    // when steeper; behind it a trail of droplets that dry, and the path it wiped. speed: how fast they run (m/s)
    float Sliders(float2 p, float time, float speed, out float2 tilt, out float wiped)
    {
        const float lane = 0.07;
        tilt = 0.0;
        wiped = 0.0;
        float best = 0.0;
        float column = floor(p.x / lane);
        [unroll]
        for (int k = -1; k <= 1; ++k)
        {
            float c = column + k;
            float h = Hash21(float2(c, 1.7));
            if (h < 0.35)
                continue; // not every lane has one
            // one drop a lane at a time, starting again higher up when it has run 0.8 m
            float run = time * speed * (0.6 + 0.8 * h) + h * 10.0;
            float trip = floor(run / 0.8);
            float y = frac(run / 0.8) * 0.8;
            float y0 = floor(p.y / 0.8) * 0.8 + y;
            // it weaves as it finds its way between the small drops
            float weave = sin((p.y + h * 7.0) * 18.0) * 0.006 + sin((p.y + h * 3.0) * 47.0) * 0.003;
            float x0 = (c + 0.3 + 0.4 * Hash21(float2(c, trip + 3.1))) * lane + weave;
            float size = 0.004 + 0.003 * Hash21(float2(c, trip + 8.8));
            float2 d = float2(p.x - x0, p.y - y0);
            // the teardrop: round at the front (down the flow), drawn out behind
            float2 radius = float2(size, d.y < 0.0 ? size * 2.2 : size);
            float2 t;
            float m = Dome(d, radius, t);
            if (m > best)
            {
                best = m;
                tilt = t;
            }
            // behind it: the path it wiped (a few centimetres wide) and droplets left on it, drying
            float behind = y0 - p.y;
            if (behind > 0.0 && behind < 0.6 && abs(d.x) < size * 1.6)
            {
                wiped = max(wiped, saturate(1.0 - behind / 0.6));
                float2 tc = float2(floor(p.y / 0.012), c);
                if (Hash21(tc) > 0.55)
                {
                    float2 td = float2(d.x - (Hash21(tc + 1.3) - 0.5) * size, frac(p.y / 0.012) * 0.012 - 0.006);
                    float2 tt;
                    float tm = Dome(td, (0.0012 + 0.001 * Hash21(tc + 2.9)) * (1.0 - behind / 0.6).xx, tt) ;
                    if (tm > best)
                    {
                        best = tm;
                        tilt = tt;
                    }
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
        float3 axisU, axisV;
        if (an.y > an.x && an.y > an.z) { axisU = float3(1, 0, 0); axisV = float3(0, 0, 1); }
        else if (an.x > an.z) { axisU = float3(0, 0, 1); axisV = float3(0, 1, 0); }
        else { axisU = float3(1, 0, 0); axisV = float3(0, 1, 0); }
        float2 plane = float2(dot(i.positionOS, axisU), dot(i.positionOS, axisV));

        // the way down: gravity along the panel plus the air blowing past it (drag grows with the square of the speed;
        // at about 70 km/h it holds a drop against gravity on a windscreen)
        float3 gravityOS = mul((float3x3)UNITY_MATRIX_I_M, float3(0.0, -1.0, 0.0));
        float3 airOS = mul((float3x3)UNITY_MATRIX_I_M, -_NFX_CarAir.xyz);
        float airSpeed = length(_NFX_CarAir.xyz);
        float3 nOS = normalize(i.normalOS);
        float3 pull = gravityOS - nOS * dot(gravityOS, nOS);
        float3 push = (airOS - nOS * dot(airOS, nOS)) * (airSpeed / 20.0) * (1.0 / 20.0);
        float3 flow3 = pull + push;
        float2 flow = float2(dot(flow3, axisU), dot(flow3, axisV));
        float flowStrength = length(flow);
        float2 down = flowStrength > 1e-3 ? flow / flowStrength : float2(0.0, -1.0);
        float2 across = float2(-down.y, down.x);
        // the plane turned so +y runs down the flow
        float2 fp = float2(dot(plane, across), -dot(plane, down));

        float2 tiltS, tiltD;
        float wiped;
        // big drops run where the panel slopes or the air pushes (on a level roof they stay beads)
        float sliding = saturate((flowStrength - 0.15) * 2.0);
        float sliders = sliding > 0.0 ? Sliders(fp, time, 0.08 + 0.35 * flowStrength, tiltS, wiped) * sliding : 0.0;
        // small drops: more in heavier rain; blown off at speed; wiped where a big one ran
        float rate = 0.05 + 0.15 * wet;
        float droplets = Droplets(fp, time, rate, tiltD) * (1.0 - wiped * sliding) * saturate(1.3 - airSpeed / 35.0);
        float drops = max(sliders, droplets) * _NFX_CarRain.x * wet;
        float2 tilt = sliders > droplets ? tiltS : tiltD;
        // the tilt back from the flow's frame into the panel's, then to the world
        float2 tiltPlane = across * tilt.x - down * tilt.y;
        float3 tiltOS = axisU * tiltPlane.x + axisV * tiltPlane.y;
        float3 tiltWS = normalize(mul((float3x3)UNITY_MATRIX_M, tiltOS + nOS * 1e-4));

        // water running down the sides in world space (the film between the drops)
        float2 runUV = float2(dot(i.positionAWS.xz, float2(0.7, 0.7)) * 14.0, i.positionAWS.y * 2.0 + time * 0.8);
        float run = smoothstep(0.62, 0.9, Noise(float2(runUV.x, runUV.y * 0.2)) * Noise(runUV * float2(1.0, 0.1) + 7.0) * 2.2);
        run *= side * wet * _NFX_CarRain.x * 0.5 * (1.0 - sliding * 0.5);

        float coverage = saturate(max(drops, run));
        float beads = drops;

        // the drop as a lens: its dome's normal, the scene behind it bent through it (a short drop flips it: the view
        // through it comes from the other side), a dark rim where the light reflects inside, the sky and the sun on top
        float rim = length(tilt);
        float3 dn = normalize(n + tiltWS * 1.2 * beads * saturate(rim + 0.05));
        float2 screenUV = i.positionCS.xy * _ScreenSize.zw;
        float3 bent = refract(-V, dn, 1.0 / 1.33);
        float3 throughRWS = i.positionRWS + bent * 0.6;
        float2 refractUV = ComputeNormalizedDeviceCoordinates(throughRWS, UNITY_MATRIX_VP);
        refractUV = lerp(screenUV, refractUV, saturate(beads * 1.5));
        float3 behind = SAMPLE_TEXTURE2D_X_LOD(_NFX_ColorTex, s_linear_clamp_sampler, saturate(refractUV) * _RTHandleScale.xy, 0).rgb;
        float3 r = reflect(-V, dn);
        float glint = pow(saturate(dot(r, _NFX_SunDirWS.xyz)), 600.0) * beads;
        float fresnel = 0.02 + 0.98 * pow(1.0 - saturate(dot(dn, V)), 5.0);
        float luma = dot(behind, float3(0.2126, 0.7152, 0.0722));
        // what it mirrors: the sky above brighter than the ground below (the scene's own level as the scale)
        float sky = luma * (0.4 + 0.8 * saturate(r.y * 0.5 + 0.5)) + 0.01;
        float edgeDark = lerp(1.0, 0.35, smoothstep(0.75, 1.0, rim) * saturate(beads * 2.0));
        float3 water = behind * (1.0 - fresnel) * edgeDark + sky * fresnel + glint * (luma * 25.0 + 0.5);

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

        // dirt: drawn over the lit car, so it is shaded here from the sun and the sky (it is matte), in the colour
        // buffer's exposure. Mud splashed up from the wheels on the lower body, dust settling everywhere (more on top),
        // snow lying on the roof and the bonnet.
        float dirtAmount = _NFX_CarDirt.x + _NFX_CarDirt.y + _NFX_CarDirt.z;
        if (dirtAmount > 0.001)
        {
            float h = i.positionAWS.y - _NFX_CarDirt.w;
            float splat = Noise(plane * 6.0) * 0.6 + Noise(plane * 25.0) * 0.4;
            float mud = _NFX_CarDirt.x * smoothstep(0.8, 0.2, h + (splat - 0.5) * 0.35) * (1.0 - up * 0.6);
            float dust = _NFX_CarDirt.y * (0.35 + 0.65 * up) * smoothstep(0.25, 0.75, splat * 0.6 + Noise(plane * 3.0) * 0.4);
            float snow = _NFX_CarDirt.z * smoothstep(0.55, 0.85, up) * smoothstep(0.3, 0.5, Noise(plane * 1.5) * 0.7 + splat * 0.3);
            float lit = (_NFX_CarLight.x * saturate(dot(n, _NFX_SunDirWS.xyz)) + _NFX_CarLight.y * (0.6 + 0.4 * n.y)) * INV_PI * GetCurrentExposureMultiplier();
            float3 dirtColor = float3(0.0, 0.0, 0.0);
            float dirtMask = 0.0;
            if (mud > 0.001)
            {
                dirtColor = float3(0.16, 0.12, 0.08) * lit;
                dirtMask = saturate(mud * 1.2);
            }
            if (dust > 0.001)
            {
                float3 d = float3(0.42, 0.37, 0.3) * lit;
                float m = saturate(dust);
                dirtColor = lerp(dirtColor, d, m * (1.0 - dirtMask * 0.5));
                dirtMask = max(dirtMask, m * 0.85);
            }
            if (snow > 0.001)
            {
                float m = saturate(snow * 1.5);
                dirtColor = lerp(dirtColor, float3(0.85, 0.87, 0.9) * lit, m);
                dirtMask = max(dirtMask, m);
            }
            color = lerp(color, dirtColor, dirtMask * (1.0 - coverage * 0.5));
            alpha = max(alpha, dirtMask);
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

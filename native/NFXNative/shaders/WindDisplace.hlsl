// NFXNative: foliage swayed by the wind for the ray traced scene, the same displacement NatureFX/Foliage applies when
// it draws (NFX_LitData.hlsl ApplyVertexModification with NFX_WindOffsetWS of NFX_Wind.hlsl, kept in step with
// them): every vertex of one wind instance moved in world space and written back in its object space, then the
// instance's BLAS is updated from them.
// Root: t0 the geometry's positions (float3), u0 the swayed positions (float3), t1 the wind frame (raw, below),
// b0 { vertices, object to world 3x4 (12), world to object 3x3 rows (9) }. Compiled into WindDisplace.h.
// Wind frame: float4 wind (xyz direction, w strength), float4 params (gust freq, gust strength, flutter freq,
// flutter strength), float4 (time, foliage bend, interactor count, unused), float4 interactors[8] (xyz, radius).
ByteAddressBuffer Source : register(t0);
RWByteAddressBuffer Target : register(u0);
ByteAddressBuffer Frame : register(t1);

cbuffer Instance : register(b0)
{
    uint Vertices;
    float3 Pad;
    float4 ObjectToWorld[3];
    float3 WorldToObject0;
    float Pad0;
    float3 WorldToObject1;
    float Pad1;
    float3 WorldToObject2;
    float Pad2;
};

// NFX_Common.hlsl
float NFX_Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

float NFX_Hash13(float3 p3)
{
    p3 = frac(p3 * 0.1031);
    p3 += dot(p3, p3.zyx + 31.32);
    return frac((p3.x + p3.y) * p3.z);
}

float NFX_ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = NFX_Hash12(i);
    float b = NFX_Hash12(i + float2(1, 0));
    float c = NFX_Hash12(i + float2(0, 1));
    float d = NFX_Hash12(i + float2(1, 1));
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

// NFX_Wind.hlsl NFX_WindOffsetWS
float3 WindOffsetWS(float3 positionAWS, float3 originAWS, float weight, float stiffness, float time)
{
    float4 wind = asfloat(Frame.Load4(0));
    float4 params = asfloat(Frame.Load4(16));
    float4 misc = asfloat(Frame.Load4(32));
    float3 dir = wind.xyz;
    float3 side = float3(-dir.z, 0.0, dir.x);
    float strength = wind.w * (1.0 - saturate(stiffness));
    float w = weight * weight;

    float along = dot(positionAWS.xz, dir.xz);
    float across = dot(positionAWS.xz, side.xz);
    float gust = NFX_ValueNoise(float2(along * 0.04 - time * params.x, across * 0.02));
    gust = 0.3 + gust * gust * params.y * 2.5;

    float objPhase = NFX_Hash13(floor(originAWS * 2.0)) * 6.2832;
    float sway = sin(time * 1.7 + objPhase) * 0.5 + sin(time * 2.9 + objPhase * 1.3) * 0.25;

    float3 disp = dir * strength * w * (gust + sway * 0.25 * strength);

    float flutterPhase = NFX_Hash13(floor(positionAWS * 4.0)) * 6.2832;
    float flutter = sin(time * params.z + flutterPhase) * params.w * 0.04 * weight * (0.3 + strength);
    disp += (side + float3(0.0, 0.6, 0.0)) * flutter;

    int count = (int)misc.z;
    [loop]
    for (int i = 0; i < 8; ++i)
    {
        if (i >= count)
            break;
        float4 it = asfloat(Frame.Load4(48 + i * 16));
        float3 d = positionAWS - it.xyz;
        float dist = length(d.xz);
        float f = saturate(1.0 - dist / it.w) * saturate(1.0 - abs(d.y) / 3.0);
        float2 push = d.xz / max(dist, 1e-3);
        disp.xz += push * f * 0.9 * weight;
        disp.y -= f * 0.5 * weight;
    }

    disp.y -= length(disp.xz) * 0.4 * weight;
    return disp;
}

[numthreads(64, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= Vertices)
        return;
    float3 positionOS = asfloat(Source.Load3(id.x * 12));
    float3 positionAWS = float3(dot(ObjectToWorld[0].xyz, positionOS) + ObjectToWorld[0].w,
                                dot(ObjectToWorld[1].xyz, positionOS) + ObjectToWorld[1].w,
                                dot(ObjectToWorld[2].xyz, positionOS) + ObjectToWorld[2].w);
    float3 originAWS = float3(ObjectToWorld[0].w, ObjectToWorld[1].w, ObjectToWorld[2].w);
    float4 misc = asfloat(Frame.Load4(32));
    // NFX_LitData.hlsl: no per-vertex weight on game meshes, the height above the object origin instead
    float weight = saturate(0.35 + positionOS.y * 0.12);
    float3 disp = WindOffsetWS(positionAWS, originAWS, weight, 0.0, misc.x) * misc.y;
    float3 moved = positionOS + float3(dot(WorldToObject0, disp), dot(WorldToObject1, disp), dot(WorldToObject2, disp));
    Target.Store3(id.x * 12, asuint(moved));
}

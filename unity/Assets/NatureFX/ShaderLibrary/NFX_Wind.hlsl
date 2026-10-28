#ifndef NFX_WIND_INCLUDED
#define NFX_WIND_INCLUDED

#include "NFX_Common.hlsl"

// Shader Graph "Custom Function" node (Type: File, Name: NFX_Wind), connected to Vertex > Position.
//   PositionAWS : Position node, Space = Absolute World
//   PositionOS  : Position node, Space = Object
//   Weight      : 0 at the root, 1 at the tip (vertex color R for trees/bushes, UV0.y for grass cards)
//   Stiffness   : 0 = grass, ~0.7 = tree trunk
void NFX_Wind_float(float3 PositionAWS, float3 PositionOS, float Weight, float Stiffness, out float3 OutPositionOS) {
	float3 dir = _NFX_Wind.xyz;
	float3 side = float3(-dir.z, 0.0, dir.x);
	float strength = _NFX_Wind.w * (1.0 - saturate(Stiffness));
	float t = _NFX_WindTime;
	float w = Weight * Weight;

	// Gust waves rolling across the field along the wind direction
	float along = dot(PositionAWS.xz, dir.xz);
	float across = dot(PositionAWS.xz, side.xz);
	float gust = NFX_ValueNoise(float2(along * 0.04 - t * _NFX_WindParams.x, across * 0.02));
	gust = 0.3 + gust * gust * _NFX_WindParams.y * 2.5;

	// Per-object sway with its own phase so neighbouring plants don't move in lockstep
	float3 originAWS = GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23);
	float objPhase = NFX_Hash13(floor(originAWS * 2.0)) * 6.2832;
	float sway = sin(t * 1.7 + objPhase) * 0.5 + sin(t * 2.9 + objPhase * 1.3) * 0.25;

	float3 disp = dir * strength * w * (gust + sway * 0.25 * strength);

	// High frequency leaf flutter
	float flutterPhase = NFX_Hash13(floor(PositionAWS * 4.0)) * 6.2832;
	float flutter = sin(t * _NFX_WindParams.z + flutterPhase) * _NFX_WindParams.w * 0.04 * Weight * (0.3 + strength);
	disp += (side + float3(0.0, 0.6, 0.0)) * flutter;

	// Cars pushing the vegetation away
	int count = (int)_NFX_InteractorCount;
	[loop]
	for (int i = 0; i < 8; ++i) {
		if (i >= count) {
			break;
		}
		float4 it = _NFX_Interactors[i];
		float3 d = PositionAWS - it.xyz;
		float dist = length(d.xz);
		float f = saturate(1.0 - dist / it.w) * saturate(1.0 - abs(d.y) / 3.0);
		float2 push = d.xz / max(dist, 1e-3);
		disp.xz += push * f * 0.9 * Weight;
		disp.y -= f * 0.5 * Weight;
	}

	// Bend instead of stretch: lower the tip as it moves sideways
	disp.y -= length(disp.xz) * 0.4 * Weight;

	OutPositionOS = PositionOS + mul((float3x3)GetWorldToObjectMatrix(), disp);
}

#endif

#ifndef NFX_WIND_INCLUDED
#define NFX_WIND_INCLUDED

#include "NFX_Common.hlsl"

// World-space wind offset for one vertex.
//   positionAWS : absolute world position of the vertex
//   weight      : 0 at the root, 1 at the tip
//   stiffness   : 0 = grass, ~0.7 = tree trunk
//   time        : seconds; use the pass time (_TimeParameters.x / timeParameters.x) so motion vectors stay correct
//   objPhase    : the sway's phase in radians (NFX_WindOffsetWS: from the object's origin; the map's vertex colour G)
float3 NFX_WindOffsetPhaseWS(float3 positionAWS, float objPhase, float weight, float stiffness, float time) {
	float3 dir = _NFX_Wind.xyz;
	float3 side = float3(-dir.z, 0.0, dir.x);
	float strength = _NFX_Wind.w * (1.0 - saturate(stiffness));
	float w = weight * weight;

	// Gust waves rolling across the field along the wind direction
	float along = dot(positionAWS.xz, dir.xz);
	float across = dot(positionAWS.xz, side.xz);
	float gust = NFX_ValueNoise(float2(along * 0.04 - time * _NFX_WindParams.x, across * 0.02));
	gust = 0.3 + gust * gust * _NFX_WindParams.y * 2.5;

	float sway = sin(time * 1.7 + objPhase) * 0.5 + sin(time * 2.9 + objPhase * 1.3) * 0.25;

	float3 disp = dir * strength * w * (gust + sway * 0.25 * strength);

	// High frequency leaf flutter
	float flutterPhase = NFX_Hash13(floor(positionAWS * 4.0)) * 6.2832;
	float flutter = sin(time * _NFX_WindParams.z + flutterPhase) * _NFX_WindParams.w * 0.04 * weight * (0.3 + strength);
	disp += (side + float3(0.0, 0.6, 0.0)) * flutter;

	// Cars pushing the vegetation away
	int count = (int)_NFX_InteractorCount;
	[loop]
	for (int i = 0; i < 8; ++i) {
		if (i >= count) {
			break;
		}
		float4 it = _NFX_Interactors[i];
		float3 d = positionAWS - it.xyz;
		float dist = length(d.xz);
		// it.xyz is the car's centre of mass (about 0.5 m up): only what the body can touch, from the ground to
		// just over the roof, is pushed, so the leaves of a tree the car stops under stay where they are
		float reach = smoothstep(1.4, 0.6, d.y) * smoothstep(-2.5, -1.5, d.y);
		float f = smoothstep(0.0, 1.0, saturate(1.0 - dist / it.w)) * reach;
		// away from the car, fading to nothing right under it so the direction does not flip from one side to the other
		float2 push = d.xz / max(dist, it.w * 0.3);
		disp.xz += push * f * 0.9 * weight;
		disp.y -= f * 0.5 * weight;
	}

	// Bend instead of stretch: lower the tip as it moves sideways
	disp.y -= length(disp.xz) * 0.4 * weight;
	return disp;
}

float3 NFX_WindOffsetWS(float3 positionAWS, float3 originAWS, float weight, float stiffness, float time) {
	// Per-object sway with its own phase so neighbouring plants don't move in lockstep
	return NFX_WindOffsetPhaseWS(positionAWS, NFX_Hash13(floor(originAWS * 2.0)) * 6.2832, weight, stiffness, time);
}

// Shader Graph "Custom Function" node (Type: File, Name: NFX_Wind), connected to Vertex > Position.
//   PositionAWS : Position node, Space = Absolute World
//   PositionOS  : Position node, Space = Object
//   Weight      : 0 at the root, 1 at the tip (vertex color R for trees/bushes, UV0.y for grass cards)
//   Stiffness   : 0 = grass, ~0.7 = tree trunk
void NFX_Wind_float(float3 PositionAWS, float3 PositionOS, float Weight, float Stiffness, out float3 OutPositionOS) {
	float3 originAWS = GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23);
	float3 disp = NFX_WindOffsetWS(PositionAWS, originAWS, Weight, Stiffness, _NFX_WindTime);
	OutPositionOS = PositionOS + mul((float3x3)GetWorldToObjectMatrix(), disp);
}

#endif

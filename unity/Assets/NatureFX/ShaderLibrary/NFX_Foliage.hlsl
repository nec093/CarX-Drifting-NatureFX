#ifndef NFX_FOLIAGE_INCLUDED
#define NFX_FOLIAGE_INCLUDED

// Leaves and grass from the map profile's [foliage] (docs/map-authoring.md 5.2 / 5.3, src/NatureFX/Map/MaterialProfiles.cs)
// and the generated 3D grass (5.1, src/NatureFX/Nature/GrassRenderer.cs). Only for NatureFX/Foliage:
//   _NFX_Foliage  x: type (0 none, 1 conifer, 2 deciduous, 3 bamboo / sasa, 4 grass), y: translucency, z: variation,
//                 w: unused (0; was the autumn colour)
//   _NFX_Foliage2 x: 1 = generated grass blades, y: hue shift (degrees), z: unused (0; was the susuki plumes), w: 1 =
//                 the mesh's vertex colours drive the sway as the map's would (forest cards)
// Static batching merges trees, so "per tree" is a 6 m cell of world space, as for the lamps.

#include "NFX_Common.hlsl"

float3 NFX_HueShift(float3 c, float degrees) {
	float a = radians(degrees);
	const float3 k = float3(0.57735, 0.57735, 0.57735);
	float cosA = cos(a);
	return c * cosA + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - cosA);
}

// Colour variation and back lit leaves
void NFX_ApplyFoliage(inout float3 baseColor, inout float3 normalWS, float3 positionAWS, float3 V) {
	float3 cell = floor(positionAWS / 6.0);
	float variation = _NFX_Foliage.z;
	if (variation > 0.0) {
		float h2 = NFX_Hash13(cell + 11.3);
		float h3 = NFX_Hash13(cell + 29.1);
		baseColor *= 1.0 + (h2 - 0.5) * variation * 0.6;
		baseColor = NFX_HueShift(baseColor, (h3 - 0.5) * variation * 40.0);
	}
	if (_NFX_Foliage2.y != 0.0) {
		baseColor = NFX_HueShift(baseColor, _NFX_Foliage2.y);
	}

	// back lit: the sun through the leaf. The normal leans a little towards the sun, so the far side catches some of
	// it (with its shadows); the light that comes through is the shadow composite's (LeafGlow), not brighter colour
	// here: with both, and the normal turned fully, back lit crowns went pale white (a real leaf lets some 5 to 15 %
	// of the sun through, needles less)
	float translucency = _NFX_Foliage.y;
	if (translucency > 0.0 && _NFX_SunDir.w > 0.0) {
		float backlit = Sq(saturate(dot(-V, _NFX_SunDir.xyz))) * _NFX_SunDir.w;
		normalWS = normalize(normalWS + _NFX_SunDir.xyz * translucency * backlit * 0.5);
	}
}

// Generated grass blades (GrassRenderer): vertex colour R height share, G phase, B stiffness, A brightness;
// uv1 x: height above the blade's root (m), y: the blade's height (m). Tyres press them down (the track map's
// compression).
float3 NFX_GrassBladeOffset(float3 positionAWS, float heightAbove, float bladeHeight, float seed) {
	float3 offset = 0.0;
	float press = NFX_Track(positionAWS.xz).b;
	if (press > 0.01) {
		float a = seed * 6.2832;
		offset.xz += float2(cos(a), sin(a)) * heightAbove * press * 0.6;
		offset.y -= heightAbove * press * 0.85;
	}
	return offset;
}

#endif

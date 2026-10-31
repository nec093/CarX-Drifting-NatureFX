#ifndef NFX_FOLIAGE_INCLUDED
#define NFX_FOLIAGE_INCLUDED

// Leaves and grass from the map profile's [foliage] (docs/map-authoring.md 5.2 / 5.3, src/NatureFX/Map/MaterialProfiles.cs)
// and the generated 3D grass (5.1, src/NatureFX/Nature/GrassRenderer.cs). Only for NatureFX/Foliage:
//   _NFX_Foliage  x: type (0 none, 1 conifer, 2 deciduous, 3 bamboo / sasa, 4 grass), y: translucency, z: variation,
//                 w: autumn colour (0 mixed, 1 red, 2 yellow, 3 brown)
//   _NFX_Foliage2 x: 1 = generated grass blades, y: hue shift (degrees), z: 1 = plumes (susuki), w: 1 = the mesh's
//                 vertex colours drive the sway as the map's would (forest cards)
// Static batching merges trees, so "per tree" is a 6 m cell of world space, as for the lamps.

#include "NFX_Common.hlsl"

// rgb: the colour the leaves move to in this season, a: how far (0 keeps the texture's own)
float4 NFX_SeasonLook(int type, int season, int autumn, float h) {
	if (type == 2) {
		if (season == 0) return float4(0.42, 0.62, 0.16, 0.45); // young leaves
		if (season == 1) return float4(0.16, 0.32, 0.07, 0.3);  // deep summer green
		if (season == 2) {
			int kind = autumn == 0 ? (h < 0.4 ? 1 : (h < 0.75 ? 2 : 4)) : autumn; // mixed: red, yellow, orange
			float3 c = kind == 1 ? float3(0.55, 0.08, 0.03) : (kind == 2 ? float3(0.7, 0.52, 0.05) : (kind == 3 ? float3(0.36, 0.2, 0.07) : float3(0.7, 0.28, 0.04)));
			return float4(c, 0.9);
		}
		return float4(0.33, 0.22, 0.1, 0.9); // the few dead leaves left in winter
	}
	if (type == 1) {
		if (season == 2) return float4(0.22, 0.26, 0.09, 0.25);
		if (season == 3) return float4(0.08, 0.16, 0.07, 0.4);
		return float4(0.12, 0.25, 0.08, 0.1);
	}
	if (type == 3) {
		return float4(0.18, 0.36, 0.08, 0.05); // evergreen; winter whitens the edges instead (NFX_ApplyFoliage)
	}
	if (type == 4) {
		if (season == 0) return float4(0.4, 0.6, 0.15, 0.4);
		if (season == 1) return float4(0.2, 0.4, 0.08, 0.2);
		if (season == 2) return float4(0.58, 0.46, 0.2, 0.75);
		return float4(0.5, 0.43, 0.3, 0.85);
	}
	return 0.0;
}

// share of a deciduous tree's leaves still on it
float NFX_LeafShare(int season) {
	return season == 3 ? 0.12 : (season == 2 ? 0.85 : 1.0);
}

float3 NFX_Recolour(float3 base, float4 look) {
	float lum = dot(base, float3(0.2126, 0.7152, 0.0722));
	float target = max(dot(look.rgb, float3(0.2126, 0.7152, 0.0722)), 1e-3);
	return lerp(base, look.rgb * (lum / target), look.a);
}

float3 NFX_HueShift(float3 c, float degrees) {
	float a = radians(degrees);
	const float3 k = float3(0.57735, 0.57735, 0.57735);
	float cosA = cos(a);
	return c * cosA + cross(k, c) * sin(a) + k * dot(k, c) * (1.0 - cosA);
}

// The season this place is in: the global season (_NFX_Season.x, 0 spring .. 3 winter, cyclic; < 0 none) moved a
// little per 6 m cell, so trees change one after another. Returns the two seasons and the blend between them.
bool NFX_LocalSeason(float h, out int a, out int b, out float t) {
	a = 0;
	b = 0;
	t = 0.0;
	if (_NFX_Season.x < 0.0) {
		return false;
	}
	float s = _NFX_Season.x + (h - 0.5) * 0.3;
	s = s - floor(s / 4.0) * 4.0;
	a = (int)floor(s);
	b = (a + 1) % 4;
	t = smoothstep(0.65, 1.0, frac(s)); // a season holds most of its span, then turns
	return true;
}

// Season colour, colour variation and back lit leaves. 'opacity' and 'cutoff' are the alpha test's (the winter
// bamboo's white edges are where the leaf thins out). Returns the share of leaves kept (deciduous trees in
// autumn / winter), for the alpha clip.
float NFX_ApplyFoliage(inout float3 baseColor, inout float3 normalWS, float3 positionAWS, float3 V, float opacity, float cutoff) {
	int type = (int)(_NFX_Foliage.x + 0.5);
	float3 cell = floor(positionAWS / 6.0);
	float h = NFX_Hash13(cell + 3.7);
	float keep = 1.0;

	int sa, sb;
	float st;
	if (type > 0 && NFX_LocalSeason(h, sa, sb, st)) {
		int autumn = (int)(_NFX_Foliage.w + 0.5);
		float3 a = NFX_Recolour(baseColor, NFX_SeasonLook(type, sa, autumn, h));
		float3 b = NFX_Recolour(baseColor, NFX_SeasonLook(type, sb, autumn, h));
		baseColor = lerp(a, b, st);
		if (type == 2) {
			keep = lerp(NFX_LeafShare(sa), NFX_LeafShare(sb), st);
		}
		if (type == 3) {
			float winter = (sa == 3 ? 1.0 - st : 0.0) + (sb == 3 ? st : 0.0);
			float edge = 1.0 - smoothstep(cutoff, cutoff + 0.3, opacity);
			float lum = dot(baseColor, float3(0.2126, 0.7152, 0.0722));
			baseColor = lerp(baseColor, float3(0.8, 0.77, 0.62) * max(lum * 2.0, 0.25), edge * winter * 0.85);
		}
	}

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

	// back lit: the sun through the leaf. The normal leans towards the sun, so HDRP lights the far side too
	// (with its shadows), and the light that comes through is warmer and more saturated
	float translucency = _NFX_Foliage.y;
	if (translucency > 0.0 && _NFX_SunDir.w > 0.0) {
		float backlit = Sq(saturate(dot(-V, _NFX_SunDir.xyz))) * _NFX_SunDir.w;
		normalWS = normalize(normalWS + _NFX_SunDir.xyz * translucency * backlit * 2.0);
		baseColor *= 1.0 + translucency * backlit * float3(0.7, 0.8, 0.3);
	}
	return keep;
}

// Generated grass blades (GrassRenderer): vertex colour R height share, G phase, B stiffness, A brightness;
// uv1 x: height above the blade's root (m), y: the blade's height (m). Tyres press them down (the track map's
// compression) and snow deeper than a blade buries it.
float3 NFX_GrassBladeOffset(float3 positionAWS, float heightAbove, float bladeHeight, float seed) {
	float3 offset = 0.0;
	float press = NFX_Track(positionAWS.xz).b;
	if (press > 0.01) {
		float a = seed * 6.2832;
		offset.xz += float2(cos(a), sin(a)) * heightAbove * press * 0.6;
		offset.y -= heightAbove * press * 0.85;
	}
	float snowDepth = _NFX_Weather.y * _NFX_Accumulation.x;
	if (snowDepth > bladeHeight * 0.9) {
		offset.y -= heightAbove; // flat at the root, under the snow
	}
	return offset;
}

#endif

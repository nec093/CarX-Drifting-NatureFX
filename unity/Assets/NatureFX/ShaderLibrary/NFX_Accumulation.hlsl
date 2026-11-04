#ifndef NFX_ACCUMULATION_INCLUDED
#define NFX_ACCUMULATION_INCLUDED

#include "NFX_Common.hlsl"

struct NFXSurface {
	float3 baseColor;
	float smoothness;
	float metallic;
	float3 normalWS;
	float occlusion;
	float height;    // 0 low .. 1 high on the surface (its height map, else its brightness): where the water film stands
};

float3 NFX_PerturbNormal(float3 n, float3 gradient, float strength) {
	// gradient is in world xz; tilt the normal against it
	return normalize(n - float3(gradient.x, 0.0, gradient.z) * strength);
}

// Expanding rain drop rings on standing water; returns a world xz normal offset. Heavy rain lands hundreds of drops on
// a square metre each second: four layers of cells 10 to 25 cm across, each cell a ring every second or so, as many of
// the cells as the rain is heavy
float2 NFX_Ripples(float2 xz, float time, float rain) {
	float2 offset = 0.0;
	[loop]
	for (int layer = 0; layer < 4; ++layer) {
		float2 p = xz * (4.0 + layer * 2.3) + layer * 17.0;
		float2 cell = floor(p);
		float2 f = frac(p) - 0.5;
		float h = NFX_Hash12(cell + layer * 3.0);
		if (h > rain) {
			continue; // fewer drops when it rains lightly
		}
		float t = frac(time * (0.9 + h * 0.6) + h * 7.0);
		float2 d = f - (float2(NFX_Hash12(cell + 1.7), NFX_Hash12(cell + 4.2)) - 0.5) * 0.6;
		float dist = length(d);
		float ring = sin((dist - t * 0.45) * 60.0) * smoothstep(0.08, 0.0, abs(dist - t * 0.45)) * (1.0 - t);
		offset += d / max(dist, 1e-3) * ring;
	}
	return offset * 0.6;
}

// porosity : how much the surface darkens when wet (asphalt ~1, metal ~0)
// exposure : 1 = open sky, 0 = sheltered
void NFX_ApplyAccumulation(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float3 V, float porosity, float exposure, float time, float viewDistance) {
	// how wet the ground has got (AccumulationController: it wets over a minute or so as the rain sets in, as fast as
	// the rain is heavy, and dries slowly), not the rain itself (a road soaked the instant the first drop fell)
	float wet = _NFX_Accumulation.w;
	float rain = _NFX_Weather.x;

	float up = saturate(geomNormalWS.y);
	float2 xz = positionAWS.xz;
	float large = NFX_Fbm(xz * 0.15);
	float detail = NFX_Fbm(xz * 1.7);
	exposure = saturate(exposure);
	// the dry line the tyres squeeze out of the wet road (TrackMap: the track map's g)
	float dryLine = NFX_Track(xz).g;

	// --- asphalt / ground micro detail: fine grain in the normal, close to the camera only
	float grain = _NFX_Weather.z * up * (1.0 - smoothstep(8.0, 40.0, viewDistance));
	if (grain > 0.001) {
		const float ge = 0.02;
		float g0 = NFX_ValueNoise(xz * 40.0);
		float3 gg = float3(NFX_ValueNoise(xz * 40.0 + float2(ge * 40.0, 0.0)) - g0, 0.0, NFX_ValueNoise(xz * 40.0 + float2(0.0, ge * 40.0)) - g0);
		s.normalWS = NFX_PerturbNormal(s.normalWS, gg * 2.5, grain * 0.2);
		s.occlusion *= 1.0 - (1.0 - g0) * grain * 0.08;
	}

	// --- wetness (Lagarde, "Water drop"): a porous surface first soaks the water up, darker (the water in its pores
	// keeps light in) and only a little glossier; once soaked, water stands in its low spots, a film that fills the
	// relief (flat normal, mirror smooth). Uneven drying; tyres squeeze the water out of the racing line.
	float wetMask = wet * exposure * saturate(0.65 + (large - 0.5) * 0.9 + up * 0.25) * (1.0 - dryLine * 0.8);
	float soaked = saturate(wetMask * 1.6);
	s.baseColor *= lerp(1.0, 0.35, soaked * saturate(porosity));
	// glossier mostly where water can lie (steep faces shed it): soaked asphalt is a blurred sheen, not a mirror
	s.smoothness = lerp(s.smoothness, lerp(0.55, 0.75, 1.0 - saturate(porosity)), soaked * lerp(0.4, 1.0, up));
	// the water film: it stands only while the rain is heavy enough to outrun the drainage (a road's camber and its
	// texture carry the water off; it drains within minutes after the rain), in the relief's low parts first. A road's
	// stones still poke through a film a millimetre thick: a sheen over its texture, not a mirror (the whole road at
	// mirror gloss looked like a flooded car park). Relief: the surface's height, or noise where it has no height map
	float relief = _NFX_Surface2.x > 0.5 ? s.height : lerp(s.height, detail * 0.7 + large * 0.3, 0.7);
	float level = saturate((wetMask - 0.7) / 0.3) * saturate(rain * 1.5) * pow(up, 4.0);
	float film = saturate((level * 0.9 - relief * 0.8) * 4.0);
	if (film > 0.001) {
		s.smoothness = lerp(s.smoothness, 0.86, film);
		s.normalWS = normalize(lerp(s.normalWS, geomNormalWS, film * 0.5));
		if (rain > 0.0) {
			float2 rip = NFX_Ripples(xz, time, rain);
			s.normalWS = normalize(s.normalWS + float3(rip.x, 0.0, rip.y) * film * 0.6);
		}
	}

	// drips running down steep faces while it rains
	float steep = 1.0 - up;
	if (rain > 0.0 && steep > 0.4) {
		float2 sp = float2(dot(xz, float2(0.7, 0.7)) * 6.0, positionAWS.y * 0.8 + time * 0.6 * rain);
		float streak = smoothstep(0.55, 0.85, NFX_ValueNoise(float2(sp.x, sp.y * 0.15)) * NFX_ValueNoise(sp * float2(1.0, 0.05) + 3.0) * 2.0);
		float dm = streak * rain * exposure * smoothstep(0.4, 0.8, steep);
		s.baseColor *= lerp(1.0, 0.7, dm * porosity);
		s.smoothness = lerp(s.smoothness, 0.95, dm);
	}

	// puddles in the ground's dips (the large scale noise as its unevenness), as the water rises: mirror smooth, flat
	// normal, rain rings on top
	float puddle = smoothstep(0.55, 0.42, large + (1.0 - level) * 0.6 + detail * 0.08) * pow(up, 8.0) * saturate(wetMask * 2.0 - 1.0);
	if (puddle > 0.001) {
		s.baseColor = lerp(s.baseColor, s.baseColor * 0.55, puddle);
		s.smoothness = lerp(s.smoothness, 0.98, puddle);
		float3 n = normalize(lerp(s.normalWS, geomNormalWS, puddle));
		float2 rip = NFX_Ripples(xz, time, rain);
		n = normalize(n + float3(rip.x, 0.0, rip.y) * puddle * rain);
		s.normalWS = n;
		s.occlusion = lerp(s.occlusion, 1.0, puddle);
	}
}

// Shader Graph "Custom Function" node (Type: File, Name: NFX_Accumulation)
void NFX_Accumulation_float(
	float3 PositionAWS, float3 NormalWS, float3 BaseColor, float Smoothness, float Metallic, float Exposure, float Porosity,
	out float3 OutBaseColor, out float OutSmoothness, out float OutMetallic, out float NormalFlatten) {
	NFXSurface s;
	s.height = 0.5;
	s.baseColor = BaseColor;
	s.smoothness = Smoothness;
	s.metallic = Metallic;
	s.normalWS = NormalWS;
	s.occlusion = 1.0;
	NFX_ApplyAccumulation(s, PositionAWS, NormalWS, float3(0.0, 1.0, 0.0), Porosity, Exposure, _NFX_WindTime, 100.0);
	OutBaseColor = s.baseColor;
	OutSmoothness = s.smoothness;
	OutMetallic = s.metallic;
	NormalFlatten = 0.0;
}

#endif

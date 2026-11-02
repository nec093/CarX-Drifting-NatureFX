#ifndef NFX_ACCUMULATION_INCLUDED
#define NFX_ACCUMULATION_INCLUDED

#include "NFX_Common.hlsl"

static const float3 NFX_SnowColor = float3(0.88, 0.90, 0.94);
static const float3 NFX_SnowShadowTint = float3(0.80, 0.86, 0.97);
static const float3 NFX_DustColor = float3(0.52, 0.45, 0.36);
static const float3 NFX_LeafColorA = float3(0.55, 0.20, 0.05);
static const float3 NFX_LeafColorB = float3(0.72, 0.46, 0.08);
static const float3 NFX_LeafColorC = float3(0.32, 0.19, 0.08);
static const float3 NFX_LeafColorD = float3(0.45, 0.42, 0.12);

struct NFXSurface {
	float3 baseColor;
	float smoothness;
	float metallic;
	float3 normalWS;
	float occlusion;
	float height;    // 0 low .. 1 high on the surface (its height map, else its brightness): the height fill
};

// a cover from 0 (none) to 1 (whole) laid over height h (0 low .. 1 high): the low parts take it first, the high ones
// last, the amount on the whole about the same (dirt and soil settle in the gaps, not on the stones)
float NFX_HeightFill(float c, float h) {
	return saturate(c + (0.5 - h) * 0.9 * (1.0 - abs(2.0 * c - 1.0)));
}

// Snow / ground height field used for both the vertex displacement and the surface normal
float NFX_SnowHeight(float2 xz) {
	return NFX_Fbm(xz * 0.35) * 0.7 + NFX_ValueNoise(xz * 3.1) * 0.3;
}

// Coverage mask shared by the vertex and pixel stages so the displaced shape matches the shading
float NFX_SnowMask(float up, float2 xz, float snow) {
	float threshold = 1.0 - snow * 1.35;
	float n = NFX_SnowHeight(xz);
	return smoothstep(threshold, threshold + 0.25, up + (n - 0.5) * 0.4) * saturate(snow * 4.0);
}

float3 NFX_PerturbNormal(float3 n, float3 gradient, float strength) {
	// gradient is in world xz; tilt the normal against it
	return normalize(n - float3(gradient.x, 0.0, gradient.z) * strength);
}

// One layer of fallen leaves. Returns coverage; outputs colour, a tilted normal offset and a soft shadow term.
float NFX_LeafLayer(float2 p, float seed, out float3 color, out float2 tilt, out float shadow) {
	float2 cell = floor(p);
	float2 f = frac(p);
	float best = 0.0;
	shadow = 0.0;
	color = NFX_LeafColorA;
	tilt = 0.0;

	// real loops, not unrolled: unrolled, the nine cells' maths compiled for seconds per shader variant
	[loop]
	for (int y = -1; y <= 1; ++y) {
		[loop]
		for (int x = -1; x <= 1; ++x) {
			float2 c = cell + float2(x, y) + seed;
			float h = NFX_Hash12(c);
			float2 center = float2(x, y) + float2(h, NFX_Hash12(c + 7.3));
			float2 d = f - center;
			float a = h * 6.2832;
			float2 r = float2(d.x * cos(a) - d.y * sin(a), d.x * sin(a) + d.y * cos(a));

			// pointed leaf outline with a midrib vein
			float along = r.x * 1.9;
			float width = 0.30 * (1.0 - pow(abs(along) / 0.42, 2.0));
			float dist = abs(r.y) - max(width, 0.0);
			float m = smoothstep(0.02, -0.02, dist) * step(abs(along), 0.42);
			float vein = smoothstep(0.012, 0.0, abs(r.y)) * 0.25;
			// soft drop shadow offset towards -y so leaves look lifted off the ground
			float2 rs = r + float2(0.0, 0.05);
			float sd = abs(rs.y) - max(0.30 * (1.0 - pow(abs(rs.x * 1.9) / 0.42, 2.0)), 0.0);
			shadow = max(shadow, smoothstep(0.10, -0.02, sd) * step(abs(rs.x * 1.9), 0.5));

			if (m > best) {
				best = m;
				float k = NFX_Hash12(c + 3.1);
				color = k < 0.3 ? NFX_LeafColorA : (k < 0.6 ? NFX_LeafColorB : (k < 0.85 ? NFX_LeafColorC : NFX_LeafColorD));
				color *= (0.75 + 0.45 * NFX_Hash12(c + 11.7)) * (1.0 - vein);
				// curled leaf: tilt along its length, random amount
				tilt = float2(cos(a), sin(a)) * (r.x * 2.0) * (0.5 + h) + (float2(NFX_Hash12(c + 5.1), NFX_Hash12(c + 9.4)) - 0.5) * 0.6;
			}
		}
	}
	return best;
}

// Expanding rain drop rings in puddles; returns a world xz normal offset
float2 NFX_Ripples(float2 xz, float time, float rain) {
	float2 offset = 0.0;
	[loop]
	for (int layer = 0; layer < 2; ++layer) {
		float2 p = xz * (2.2 + layer * 1.3) + layer * 17.0;
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

// How much snow the plough and the tyres pushed here: the driven line within about 3 m, but not on it
float NFX_SnowBank(float2 xz, float driven) {
	if (_NFX_TrackParams.w <= 0.0) {
		return 0.0;
	}
	float near = max(max(NFX_Track(xz + float2(3.0, 0.0)).b, NFX_Track(xz - float2(3.0, 0.0)).b),
	                 max(NFX_Track(xz + float2(0.0, 3.0)).b, NFX_Track(xz - float2(0.0, 3.0)).b));
	return saturate(near * 1.5 - driven * 3.0);
}

// porosity : how much the surface darkens when wet (asphalt ~1, metal ~0)
// exposure : 1 = open sky, 0 = sheltered
void NFX_ApplyAccumulation(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float3 V, float porosity, float exposure, float time, float viewDistance) {
	float snow = _NFX_Accumulation.x;
	float leaves = _NFX_Accumulation.y;
	float dust = _NFX_Accumulation.z;
	float wet = max(_NFX_Accumulation.w, _NFX_Weather.x);
	float rain = _NFX_Weather.x;

	float up = saturate(geomNormalWS.y);
	float2 xz = positionAWS.xz;
	float large = NFX_Fbm(xz * 0.15);
	float detail = NFX_Fbm(xz * 1.7);
	exposure = saturate(exposure);
	float4 track = NFX_Track(xz);

	// --- asphalt / ground micro detail: fine grain in the normal, close to the camera only
	float grain = _NFX_Weather.z * up * (1.0 - smoothstep(8.0, 40.0, viewDistance));
	if (grain > 0.001) {
		const float ge = 0.02;
		float g0 = NFX_ValueNoise(xz * 40.0);
		float3 gg = float3(NFX_ValueNoise(xz * 40.0 + float2(ge * 40.0, 0.0)) - g0, 0.0, NFX_ValueNoise(xz * 40.0 + float2(0.0, ge * 40.0)) - g0);
		s.normalWS = NFX_PerturbNormal(s.normalWS, gg * 2.5, grain * 0.2);
		s.occlusion *= 1.0 - (1.0 - g0) * grain * 0.08;
	}

	// --- the driven surface: years of rubber laid down in long sweeping streaks, polished where it is thickest,
	// and a road that is not equally rough everywhere. Only on dark, grey, level ground at the height the cars
	// drive on (not on roofs, trailers or the stands' steps).
	float roadWear = _NFX_Road.x;
	if (roadWear > 0.001 && _NFX_Road.z > 0.5) {
		float lum = dot(s.baseColor, float3(0.2126, 0.7152, 0.0722));
		float chroma = max(s.baseColor.r, max(s.baseColor.g, s.baseColor.b)) - min(s.baseColor.r, min(s.baseColor.g, s.baseColor.b));
		float asphalt = (1.0 - smoothstep(0.18, 0.3, lum)) * (1.0 - smoothstep(0.04, 0.1, chroma));
		float level = (1.0 - smoothstep(0.4, 1.2, abs(positionAWS.y - _NFX_Road.y))) * smoothstep(0.95, 0.99, up);
		float road = asphalt * level * roadWear;
		if (road > 0.001) {
			// streaks follow arcs: the direction turns slowly over the ground and the domain is warped
			float2 warped = xz + float2(NFX_Fbm(xz * 0.015), NFX_Fbm(xz * 0.015 + 7.3)) * 45.0;
			float angle = NFX_Fbm(xz * 0.008 + 3.1) * 6.2832;
			float2 dir = float2(cos(angle), sin(angle));
			float along = dot(warped, dir);
			float across = dot(warped, float2(-dir.y, dir.x));
			float streaks = smoothstep(0.55, 0.85, NFX_ValueNoise(float2(across * 1.4, along * 0.04)));
			// driven zones and untouched corners
			float zone = smoothstep(0.4, 0.65, NFX_Fbm(xz * 0.025 + 11.0));
			float laid = saturate(streaks * zone + zone * 0.25) * road;
			s.baseColor = lerp(s.baseColor, s.baseColor * 0.35 + 0.006, laid * 0.75);
			s.smoothness = lerp(s.smoothness, 0.42, laid * 0.5);
			// worn smooth here, open and dusty there
			float rough = NFX_ValueNoise(xz * 0.9) * 0.6 + NFX_ValueNoise(xz * 6.0) * 0.4;
			s.smoothness = saturate(s.smoothness * lerp(1.0, lerp(0.75, 1.2, rough), road));
		}
	}

	// --- rubber laid down by sliding tyres: dark, slightly polished
	float rubber = saturate(track.r) * up;
	s.baseColor = lerp(s.baseColor, s.baseColor * 0.15 + 0.008, rubber * 0.9);
	s.smoothness = lerp(s.smoothness, 0.5, rubber * 0.6);

	// --- dust: settles on upward faces, breaks up with noise
	float dustMask = NFX_HeightFill(saturate(dust * (0.4 + up * 0.8) * (0.6 + detail * 0.8)), s.height);
	s.baseColor = lerp(s.baseColor, NFX_DustColor * (0.85 + detail * 0.3), dustMask * 0.75);
	s.smoothness = lerp(s.smoothness, 0.12, dustMask);
	s.metallic = lerp(s.metallic, 0.0, dustMask);

	// --- wetness: uneven drying, darker porous surfaces, glossy film
	// tyres squeeze the water out: the racing line dries while it keeps raining around it
	float wetMask = wet * exposure * saturate(0.65 + (large - 0.5) * 0.9 + up * 0.25) * (1.0 - track.g * 0.8);
	s.baseColor *= lerp(1.0, 0.5, wetMask * saturate(porosity));
	s.smoothness = lerp(s.smoothness, 0.86, wetMask);

	// drips running down steep faces while it rains
	float steep = 1.0 - up;
	if (rain > 0.0 && steep > 0.4) {
		float2 sp = float2(dot(xz, float2(0.7, 0.7)) * 6.0, positionAWS.y * 0.8 + time * 0.6 * rain);
		float streak = smoothstep(0.55, 0.85, NFX_ValueNoise(float2(sp.x, sp.y * 0.15)) * NFX_ValueNoise(sp * float2(1.0, 0.05) + 3.0) * 2.0);
		float dm = streak * rain * exposure * smoothstep(0.4, 0.8, steep);
		s.baseColor *= lerp(1.0, 0.7, dm * porosity);
		s.smoothness = lerp(s.smoothness, 0.95, dm);
	}

	// puddles in flat, low spots: mirror smooth, flat normal, rain rings on top
	float puddle = smoothstep(0.55, 0.42, large + (1.0 - wet) * 0.6 + detail * 0.08) * pow(up, 8.0) * wetMask;
	if (puddle > 0.001) {
		s.baseColor = lerp(s.baseColor, s.baseColor * 0.55, puddle);
		s.smoothness = lerp(s.smoothness, 0.98, puddle);
		float3 n = normalize(lerp(s.normalWS, geomNormalWS, puddle));
		float2 rip = NFX_Ripples(xz, time, rain);
		n = normalize(n + float3(rip.x, 0.0, rip.y) * puddle * rain);
		s.normalWS = n;
		s.occlusion = lerp(s.occlusion, 1.0, puddle);
	}

	// --- fallen leaves: two overlapping layers, lifted with a soft shadow
	if (leaves > 0.001) {
		// tyres sweep leaves out of the driven line
		float patch = NFX_HeightFill(smoothstep(1.0 - leaves, 1.0 - leaves + 0.3, large + detail * 0.25) * pow(up, 2.0) * saturate(leaves * 3.0) * exposure * (1.0 - track.b * 0.85), s.height);
		[loop]
		for (int layer = 0; layer < 2; ++layer) {
			float3 leafColor;
			float2 tilt;
			float shadow;
			float m = NFX_LeafLayer(xz * (3.4 + layer * 1.7), layer * 31.0, leafColor, tilt, shadow) * patch;
			// ground under and around leaves gets darker (contact shadow), then the leaf sits on top
			s.occlusion *= 1.0 - shadow * patch * 0.45;
			s.baseColor *= 1.0 - shadow * patch * 0.25 * (1.0 - m);
			// leaves pick up the wet look too
			leafColor *= lerp(1.0, 0.65, wetMask);
			s.baseColor = lerp(s.baseColor, leafColor, m);
			s.smoothness = lerp(s.smoothness, lerp(0.28, 0.75, wetMask), m);
			s.metallic = lerp(s.metallic, 0.0, m);
			s.normalWS = normalize(lerp(s.normalWS, normalize(geomNormalWS + float3(tilt.x, 0.0, tilt.y) * 0.6), m));
		}
	}

	// --- snow: soft drifts with bumps, covering everything below it, glints in the light
	float snowMask = NFX_HeightFill(NFX_SnowMask(up, xz, snow) * exposure, s.height);
	// ruts: compressed snow is thinner, greyer slush with the ground showing through
	float rut = track.b * snowMask;
	snowMask *= 1.0 - track.b * 0.75;
	// banks: snow pushed aside piles up just beside where the cars run
	snowMask = saturate(snowMask + NFX_SnowBank(xz, track.b) * snow * exposure);
	if (snowMask > 0.001) {
		const float e = 0.15;
		float h0 = NFX_SnowHeight(xz);
		float3 grad = float3(NFX_SnowHeight(xz + float2(e, 0.0)) - h0, 0.0, NFX_SnowHeight(xz + float2(0.0, e)) - h0) / e;
		float3 snowN = NFX_PerturbNormal(geomNormalWS, grad, 0.35);
		// melt edge: thin, slightly darker and wet where coverage fades out
		float edge = smoothstep(0.0, 0.35, snowMask);
		float3 snowColor = lerp(NFX_SnowColor * 0.8, NFX_SnowColor, edge) * lerp(NFX_SnowShadowTint, 1.0, saturate(dot(snowN, V) * 0.5 + 0.5));

		// glints: sparse tiny facets that turn mirror-like towards the viewer
		float glint = step(0.997, NFX_Hash13(floor(positionAWS * 90.0))) * pow(saturate(dot(snowN, V)), 4.0);

		s.baseColor = lerp(s.baseColor, snowColor, snowMask);
		s.smoothness = lerp(s.smoothness, lerp(0.55, 0.35, edge) + glint * 0.6, snowMask);
		s.metallic = lerp(s.metallic, 0.0, snowMask);
		s.normalWS = normalize(lerp(s.normalWS, snowN, snowMask));
		s.occlusion = lerp(s.occlusion, 1.0, snowMask * edge);
	}
	if (rut > 0.001) {
		s.baseColor = lerp(s.baseColor, s.baseColor * 0.6 + float3(0.12, 0.12, 0.13), rut * 0.5);
		s.smoothness = lerp(s.smoothness, 0.7, rut * 0.6); // packed, glazed slush
	}

	// black ice: a cold, wet road freezes into a clear glaze, in patches, polished most on the driven line
	float ice = _NFX_Ice.x * saturate(wet * 1.5) * pow(up, 4.0) * exposure * (1.0 - snowMask) * smoothstep(0.3, 0.55, large + track.b * 0.3);
	if (ice > 0.001) {
		s.baseColor *= lerp(1.0, 0.8, ice);
		s.smoothness = lerp(s.smoothness, 0.98, ice);
		s.normalWS = normalize(lerp(s.normalWS, geomNormalWS, ice));
	}
}

// Vertex stage: lift snow-covered, upward-facing geometry so drifts have real thickness.
float3 NFX_SnowDisplacementWS(float3 positionAWS, float3 normalWS) {
	float snow = _NFX_Accumulation.x;
	if (snow <= 0.001 || _NFX_Weather.y <= 0.0) {
		return 0.0;
	}
	float up = saturate(normalWS.y);
	float driven = NFX_Track(positionAWS.xz).b;
	float mask = NFX_SnowMask(up, positionAWS.xz, snow) * (1.0 - driven * 0.8);
	float bank = NFX_SnowBank(positionAWS.xz, driven) * snow * up;
	float h = NFX_SnowHeight(positionAWS.xz);
	return normalWS * (mask * (0.5 + h) + bank * 1.8) * _NFX_Weather.y;
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

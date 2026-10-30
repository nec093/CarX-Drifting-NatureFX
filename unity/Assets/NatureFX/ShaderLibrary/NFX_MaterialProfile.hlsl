#ifndef NFX_MATERIAL_PROFILE_INCLUDED
#define NFX_MATERIAL_PROFILE_INCLUDED

// Per-material looks set from a map profile ([materials] / [emissive] in kino/config/NatureFX/maps/*.nfx,
// see docs/map-authoring.md and src/NatureFX/Map/MaterialProfiles.cs). Only for the generated Lit copies:
// _NFX_Material* live in their UnityPerMaterial cbuffer (Editor/GenerateNatureFXShaders.cs).

#include "NFX_Accumulation.hlsl"

static const float3 NFX_MossColor = float3(0.16, 0.22, 0.07);
static const float3 NFX_RustColor = float3(0.36, 0.16, 0.06);

float NFX_Porosity(float shaderDefault) {
	return _NFX_Material.x >= 0.0 ? _NFX_Material.x : shaderDefault;
}

// World position projected on the plane the surface mostly faces, so noise is not stretched on walls
float2 NFX_PlanarUV(float3 p, float3 n) {
	float3 a = abs(n);
	return a.y > max(a.x, a.z) ? p.xz : (a.x > a.z ? p.zy : p.xy);
}

// Thin dark lines along the borders of random cells
float NFX_Cracks(float2 p) {
	float2 c = floor(p);
	float2 f = frac(p);
	float d1 = 8.0;
	float d2 = 8.0;
	[unroll]
	for (int y = -1; y <= 1; ++y) {
		[unroll]
		for (int x = -1; x <= 1; ++x) {
			float2 cell = c + float2(x, y);
			float2 o = float2(x, y) + float2(NFX_Hash12(cell), NFX_Hash12(cell + 5.3));
			float d = length(o - f);
			if (d < d1) {
				d2 = d1;
				d1 = d;
			} else if (d < d2) {
				d2 = d;
			}
		}
	}
	return smoothstep(0.05, 0.0, d2 - d1);
}

// Moss, rust, rain stains, wear and dirt: the age of the surface, applied before the weather
void NFX_ApplyAgeing(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS) {
	float moss = _NFX_Material.y;
	float rust = _NFX_Material.z;
	float stain = _NFX_Material.w;
	float wear = _NFX_Material2.x;
	float dirt = _NFX_Material2.y;
	if (moss + rust + stain + wear + dirt <= 0.0) {
		return;
	}

	float up = saturate(geomNormalWS.y);
	float2 uv = NFX_PlanarUV(positionAWS, geomNormalWS);
	float big = NFX_Fbm(uv * 0.5);
	float fine = NFX_Fbm(uv * 4.0);

	// moss: upward faces and crevices (low occlusion), in patches
	if (moss > 0.0) {
		float crevice = 1.0 - s.occlusion;
		float m = saturate((big * 0.7 + fine * 0.3 + up * 0.35 + crevice * 0.8 - (1.0 - moss)) * 2.5);
		s.baseColor = lerp(s.baseColor, NFX_MossColor * (0.7 + fine * 0.6), m * 0.9);
		s.smoothness = lerp(s.smoothness, 0.15, m);
		s.metallic = lerp(s.metallic, 0.0, m);
	}

	// rust: blotches that eat the metal, more on faces that hold water
	if (rust > 0.0) {
		float m = saturate((fine * 0.6 + big * 0.4 + up * 0.1 - (1.0 - rust)) * 3.0);
		s.baseColor = lerp(s.baseColor, NFX_RustColor * (0.6 + fine * 0.7), m);
		s.smoothness = lerp(s.smoothness, 0.2, m);
		s.metallic = lerp(s.metallic, 0.0, m);
	}

	// rain stains: dark vertical streaks on walls
	if (stain > 0.0) {
		float steep = 1.0 - up;
		float streak = NFX_ValueNoise(float2(dot(positionAWS.xz, float2(0.7, 0.7)) * 5.0, positionAWS.y * 0.12));
		float m = smoothstep(0.45, 0.8, streak) * stain * smoothstep(0.3, 0.7, steep) * (0.6 + big * 0.8);
		s.baseColor *= 1.0 - m * 0.55;
		s.smoothness = lerp(s.smoothness, 0.1, m);
	}

	// wear: sun-bleached, greyer surface with cracks on the ground
	if (wear > 0.0) {
		float lum = dot(s.baseColor, float3(0.2126, 0.7152, 0.0722));
		float faded = wear * (0.4 + big * 0.6);
		s.baseColor = lerp(s.baseColor, lum * 1.25 + 0.03, faded * 0.5);
		float crack = NFX_Cracks(uv * 0.8 + fine * 0.6) * wear * up * smoothstep(0.35, 0.6, big);
		s.baseColor *= 1.0 - crack * 0.7;
		s.occlusion *= 1.0 - crack * 0.5;
		s.smoothness = lerp(s.smoothness, 0.05, crack);
	}

	// dirt: dull, darker grime in patches
	if (dirt > 0.0) {
		float m = dirt * (0.4 + big * 0.6);
		s.baseColor *= lerp(1.0, (0.75 + fine * 0.25) * float3(0.9, 0.85, 0.78), m);
		s.smoothness *= 1.0 - m * 0.5;
	}
}

// Water beading on water-repellent surfaces (paint, metal, glass): drops instead of a darkened film
void NFX_ApplyBeading(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float wet) {
	float bead = _NFX_Material2.z * wet;
	if (bead <= 0.001) {
		return;
	}

	float2 uv = NFX_PlanarUV(positionAWS, geomNormalWS) * 30.0;
	float2 c = floor(uv);
	float2 f = frac(uv) - 0.5;
	float h = NFX_Hash12(c);
	float r = 0.15 + 0.25 * h;
	float2 d = f - (float2(NFX_Hash12(c + 3.1), NFX_Hash12(c + 7.7)) - 0.5) * 0.4;
	float m = step(h, bead * 0.8) * smoothstep(r, r * 0.7, length(d));
	if (m <= 0.0) {
		return;
	}

	float3 n = geomNormalWS;
	float3 t = normalize(cross(n, abs(n.y) < 0.99 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0)));
	float3 b = cross(n, t);
	s.normalWS = normalize(s.normalWS + (t * d.x + b * d.y) / r * m * 0.8);
	s.smoothness = lerp(s.smoothness, 0.97, m);
	s.baseColor *= lerp(1.0, 0.9, m);
}

// Emitted light in nits. Lamps switch on by sun elevation, staggered by place so they do not all
// flip at once; 'share lit' turns some places off (house windows). Static batching merges objects,
// so places are 6 m cells of world space rather than objects.
float3 NFX_MaterialEmission(float3 baseColor, float alpha, float3 positionAWS) {
	float when = _NFX_Emission.w;
	if (when < 0.5) {
		return 0.0;
	}

	float3 cell = floor(positionAWS / 6.0);
	float h = NFX_Hash13(cell);
	float on = 1.0;
	if (when < 2.5) {
		float threshold = (when < 1.5 ? -1.0 : 6.0) + h * 4.0; // degrees of sun elevation
		on = smoothstep(threshold + 0.4, threshold - 0.4, _NFX_Lights.x);
	}
	on *= step(NFX_Hash13(cell + 17.0), _NFX_EmissionParams.y);
	if (on <= 0.0) {
		return 0.0;
	}

	float flicker = _NFX_EmissionParams.z;
	if (flicker > 0.0) {
		float n = NFX_ValueNoise(float2(_NFX_Lights.y * 9.0 + h * 50.0, h * 13.0));
		on *= 1.0 - flicker * smoothstep(0.65, 0.75, n) * 0.85;
	}

	int mode = (int)_NFX_EmissionParams.x;
	if (mode == 2) {
		return _NFX_Emission.rgb * on;
	}
	float lum = dot(baseColor, float3(0.2126, 0.7152, 0.0722));
	float mask = mode == 1 ? alpha : smoothstep(0.25, 0.7, lum);
	float3 tint = baseColor / max(max(baseColor.r, max(baseColor.g, baseColor.b)), 1e-3);
	return _NFX_Emission.rgb * tint * mask * on;
}

#endif

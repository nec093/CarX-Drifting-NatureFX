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
	[loop]
	for (int y = -1; y <= 1; ++y) {
		[loop]
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
		float m = NFX_HeightFill(saturate((big * 0.7 + fine * 0.3 + up * 0.35 + crevice * 0.8 - (1.0 - moss)) * 2.5), s.height);
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
		float m = NFX_HeightFill(dirt * (0.4 + big * 0.6), s.height);
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

// Cliffs and cut slopes ([terrain], docs/map-authoring.md 7.5). Triplanar: the material's colour map is laid on
// from three sides by world position, so it does not stretch down a steep face. Slope blending: steep faces take the
// steep map (another material's colour map, or a procedural rock when none is named), flat ground the flat map,
// with a noisy border. Only the colour: the normal map stays as the mesh has it.
TEXTURE2D(_NFX_SteepMap);
TEXTURE2D(_NFX_FlatMap);

float3 NFX_Triplanar(TEXTURE2D_PARAM(tex, smp), float3 p, float3 n, float tile) {
	float3 w = pow(abs(n), 4.0);
	w /= max(w.x + w.y + w.z, 1e-4);
	float3 x = SAMPLE_TEXTURE2D(tex, smp, p.zy / tile).rgb;
	float3 y = SAMPLE_TEXTURE2D(tex, smp, p.xz / tile).rgb;
	float3 z = SAMPLE_TEXTURE2D(tex, smp, p.xy / tile).rgb;
	return x * w.x + y * w.y + z * w.z;
}

void NFX_ApplyTerrain(inout float3 baseColor, float3 positionAWS, float3 geomNormalWS) {
	if (_NFX_Terrain.y <= 0.0) {
		return; // no [terrain] entry
	}
	if (_NFX_Terrain.x > 0.5) {
		baseColor = NFX_Triplanar(TEXTURE2D_ARGS(_BaseColorMap, sampler_BaseColorMap), positionAWS, geomNormalWS, max(_NFX_Terrain.y, 0.1)) * _BaseColor.rgb;
	}
	float slope = 1.0 - saturate(geomNormalWS.y);
	float noise = (NFX_Fbm(positionAWS.xz * 0.25 + positionAWS.y * 0.1) - 0.5) * _NFX_Terrain2.z;
	float steep = smoothstep(_NFX_Terrain.z - _NFX_Terrain.w, _NFX_Terrain.z + _NFX_Terrain.w, slope + noise);
	float tile = max(_NFX_Terrain2.w, 0.1);
	float3 rock;
	if (_NFX_Terrain2.x > 0.5) {
		rock = NFX_Triplanar(TEXTURE2D_ARGS(_NFX_SteepMap, sampler_BaseColorMap), positionAWS, geomNormalWS, tile);
	} else {
		// procedural rock: the surface's own colour greyed and darkened in layered strata
		float lum = dot(baseColor, float3(0.2126, 0.7152, 0.0722));
		float strata = NFX_ValueNoise(float2(positionAWS.y * 1.5, dot(positionAWS.xz, float2(0.05, 0.05))));
		rock = lerp(lum.xxx, baseColor, 0.25) * (0.65 + strata * 0.5) * float3(1.0, 0.97, 0.92);
	}
	baseColor = lerp(baseColor, rock, steep);
	if (_NFX_Terrain2.y > 0.5) {
		float flat = (1.0 - steep) * smoothstep(0.25, 0.05, slope + noise * 0.5);
		float3 top = SAMPLE_TEXTURE2D(_NFX_FlatMap, sampler_BaseColorMap, positionAWS.xz / tile).rgb;
		baseColor = lerp(baseColor, top, flat);
	}
}

// Decal markers (Map/MapDecals.cs; the game's HDRP has decals off, so the ground shaders lay them on): boxes projected
// from above, turned by yaw. _NFX_Decals[i] xyz centre, w = preset slice * 4 + opacity; _NFX_DecalSizes[i] xyz half
// size, w yaw (radians). Slices: 0 crack, 1 patch, 2 oil, 3 leaves_pile, 4 sand, 5 skid, 6 moss_edge.
float4 _NFX_Decals[32];
float4 _NFX_DecalSizes[32];
float _NFX_DecalCount;
TEXTURE2D_ARRAY(_NFX_DecalAtlas);

void NFX_ApplyDecals(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float3 V) {
	int count = (int)_NFX_DecalCount;
	if (count <= 0 || geomNormalWS.y < 0.3) {
		return;
	}
	float wet = max(_NFX_Accumulation.w, _NFX_Weather.x);
	[loop]
	for (int i = 0; i < 32; ++i) {
		if (i >= count) {
			break;
		}
		float4 d = _NFX_Decals[i];
		float4 size = _NFX_DecalSizes[i];
		float3 rel = positionAWS - d.xyz;
		if (abs(rel.y) > size.y || dot(rel.xz, rel.xz) > dot(size.xz, size.xz)) {
			continue;
		}
		float c = cos(size.w);
		float sn = sin(size.w);
		float2 local = float2(rel.x * c - rel.z * sn, rel.x * sn + rel.z * c);
		float2 uv = local / max(size.xz, 0.01) * 0.5 + 0.5;
		if (any(uv < 0.0) || any(uv > 1.0)) {
			continue;
		}
		float slice = floor(d.w / 4.0);
		float opacity = d.w - slice * 4.0;
		float4 tex = SAMPLE_TEXTURE2D_ARRAY(_NFX_DecalAtlas, s_trilinear_clamp_sampler, uv, slice);
		float a = tex.a * opacity;
		if (a <= 0.001) {
			continue;
		}
		s.baseColor = lerp(s.baseColor, tex.rgb, a);
		if (slice < 0.5) {
			s.occlusion *= 1.0 - a * 0.5; // cracks hold shadow
		} else if (abs(slice - 2.0) < 0.5) {
			// oil: smooth, and a thin film of rainbow colours on a wet road
			s.smoothness = lerp(s.smoothness, 0.9, a);
			float film = dot(V, geomNormalWS) * 6.0 + NFX_ValueNoise(positionAWS.xz * 3.0) * 4.0;
			float3 rainbow = 0.5 + 0.5 * cos(6.2832 * (film + float3(0.0, 0.33, 0.67)));
			s.baseColor += rainbow * 0.08 * a * saturate(wet * 2.0);
		} else if (abs(slice - 5.0) < 0.5) {
			s.smoothness = lerp(s.smoothness, 0.45, a * 0.5); // rubber polished by the tyres
		} else if (abs(slice - 3.0) < 0.5) {
			s.smoothness = lerp(s.smoothness, 0.25, a);
		}
	}
}

// Water surfaces (docs/map-authoring.md 7.3, Map/MapWater.cs). _NFX_Water x: 1 = water, y: flow direction (radians,
// 0 = +Z), z: flow speed (m/s), w: ripples from the wind. Dark and mirror smooth, so the reflections make it water;
// two layers of noise slide along the flow, rain drops ring on top.
void NFX_ApplyWater(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float time) {
	float2 dir = float2(sin(_NFX_Water.y), cos(_NFX_Water.y));
	float2 xz = positionAWS.xz;
	float2 flow = dir * _NFX_Water.z * time;
	const float e = 0.05;
	float2 p1 = xz * 1.3 - flow;
	float2 p2 = xz * 3.1 - flow * 1.7 + 5.0;
	float h0 = NFX_ValueNoise(p1) + NFX_ValueNoise(p2) * 0.5;
	float hx = NFX_ValueNoise(p1 + float2(e, 0.0)) + NFX_ValueNoise(p2 + float2(e, 0.0)) * 0.5;
	float hz = NFX_ValueNoise(p1 + float2(0.0, e)) + NFX_ValueNoise(p2 + float2(0.0, e)) * 0.5;
	float strength = 0.08 + _NFX_Water.w * (0.1 + _NFX_Wind.w * 0.15) + _NFX_Water.z * 0.1;
	float3 n = geomNormalWS + float3(h0 - hx, 0.0, h0 - hz) / e * strength;
	float2 rip = NFX_Ripples(xz, time, _NFX_Weather.x);
	n += float3(rip.x, 0.0, rip.y) * _NFX_Weather.x;
	s.normalWS = normalize(n);
	s.baseColor = float3(0.015, 0.022, 0.022);
	s.smoothness = 0.97;
	s.metallic = 0.0;
	s.occlusion = 1.0;
}

// Puddle markers (nfx_puddle, Map/MapWater.cs): boxes where rain water gathers. The low spots of the ground noise
// fill first and the water spreads as _NFX_Puddles[i].w rises with the rain (and shrinks again as it dries).
void NFX_ApplyPuddleMarkers(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS, float exposure, float time) {
	int count = (int)_NFX_PuddleCount;
	if (count <= 0) {
		return;
	}
	float up = saturate(geomNormalWS.y);
	if (up < 0.9) {
		return;
	}
	float water = 0.0;
	[loop]
	for (int i = 0; i < 16; ++i) {
		if (i >= count) {
			break;
		}
		float4 p = _NFX_Puddles[i];
		float4 size = _NFX_PuddleSizes[i];
		if (p.w <= 0.001) {
			continue;
		}
		float3 d = positionAWS - p.xyz;
		float c = cos(size.w);
		float sn = sin(size.w);
		float2 local = float2(d.x * c - d.z * sn, d.x * sn + d.z * c);
		float2 edge = size.xz - abs(local);
		if (edge.x <= 0.0 || edge.y <= 0.0 || abs(d.y) > size.y) {
			continue;
		}
		// the box's middle is its lowest point; noise breaks up the shore
		float2 rel = local / max(size.xz, 0.1);
		float depth = 1.0 - saturate(length(rel)) + (NFX_Fbm(positionAWS.xz * 0.6) - 0.5) * 0.5;
		water = max(water, smoothstep(1.0 - p.w, 1.0 - p.w + 0.12, depth) * saturate(min(edge.x, edge.y) * 2.0));
	}
	water *= exposure;
	if (water <= 0.001) {
		return;
	}
	s.baseColor = lerp(s.baseColor, s.baseColor * 0.45, water);
	s.smoothness = lerp(s.smoothness, 0.98, water);
	float2 rip = NFX_Ripples(positionAWS.xz, time, _NFX_Weather.x);
	s.normalWS = normalize(lerp(s.normalWS, geomNormalWS, water) + float3(rip.x, 0.0, rip.y) * water * _NFX_Weather.x);
	s.occlusion = lerp(s.occlusion, 1.0, water);
}

// Retroreflection (docs/map-authoring.md 6.3), in nits: retroreflective paint and sheeting send light back towards
// where it came from, so a headlight's light comes back to an eye close to it. The coefficient of retroreflection
// (cd per lux per m², of the illuminance facing the light) peaks within a few degrees of the light: retro = 1 is
// sign sheeting (about 300), 0.6 road markings (about 5). The observation angle between headlight and eye decides how
// much comes back: from the driver's seat nearly all, from a chase camera a few metres above the car much less, and
// another car's headlights far from the camera nothing. Water on the ground drowns the beads of flat markings.
float3 NFX_Retroreflection(float3 baseColor, float3 positionAWS, float3 geomNormalWS, float wet) {
	float retro = _NFX_Material2.w;
	if (retro <= 0.0) {
		return 0.0;
	}
	float3 eye = GetAbsolutePositionWS(GetCameraPositionWS());
	float3 toEye = normalize(eye - positionAWS);
	float coefficient = 300.0 * pow(saturate(retro), 8.0);
	float received = 0.0;
	[loop]
	for (int i = 0; i < 4; ++i) {
		float4 h = _NFX_Headlights[i];
		if (h.w <= 0.0) {
			continue;
		}
		float3 d = h.xyz - positionAWS;
		float dist2 = max(dot(d, d), 1.0);
		float3 toLight = d * rsqrt(dist2);
		float4 cone = _NFX_HeadlightDirs[i];
		float beam = smoothstep(cone.w, lerp(cone.w, 1.0, 0.4), dot(-toLight, cone.xyz));
		float facing = saturate(dot(geomNormalWS, toLight));
		float observation = acos(saturate(dot(toLight, toEye)));
		float r = exp(-Sq(observation / 0.035)) + 0.15 * exp(-Sq(observation / 0.12));
		received += h.w * beam * facing / dist2 * r;
	}
	if (received <= 0.0) {
		return 0.0;
	}
	float drowned = 1.0 - 0.8 * saturate(wet) * saturate(geomNormalWS.y);
	return baseColor * coefficient * received * drowned;
}

#endif

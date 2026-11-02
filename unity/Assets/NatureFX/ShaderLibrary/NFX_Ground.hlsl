#ifndef NFX_GROUND_INCLUDED
#define NFX_GROUND_INCLUDED

// What the ground map gives the map's surfaces (GroundMap.cs, NFXNative GroundMap.hlsl): the ground seen from above
// around the camera, each cell its colour, height, normal and kind.
//   Ground blend: where an object stands in the ground (a rock, a retaining wall, a kerb, a guard rail's post, a
//   tree's foot) its lowest part takes the ground's colour, more the lower, broken up by noise (the idea of Unreal's
//   runtime virtual texture blend and Far Cry's mesh to terrain blend). _NFX_Surface.w per material: 0 the map's
//   default height (_NFX_GroundBlendDefault), > 0 its own (m), < 0 off.
//   Painted dirt ([paint] in the map profile, MapPaint.cs): mud splashed up the verges, water stains down the foot of
//   walls, dark ruts, from a map the profile's lines are drawn into around the camera.
//   Height fill: overlays (moss, dirt, dust, leaves, snow) settle in the low parts of a surface first, by its height
//   map or, without one, its brightness (cracks and gaps are dark).

StructuredBuffer<float4> _NFX_GroundMap;  // two per cell: albedo rgb + height (absolute; < -1e8 none), normal xyz + kind
float4 _NFX_GroundMapParams;              // x, y the grid's centre (absolute x, z), z the cell size (m), w cells per side (0: none)
float _NFX_GroundBlendDefault;            // m

TEXTURE2D(_NFX_PaintMap);                 // r mud, g water stains, b ruts, a the top they reach (absolute y)
float4 _NFX_PaintParams;                  // x, y the map's corner (absolute x, z), z its size (m), w 1 = on

// The ground under xz: false outside the map or where there is none
bool NFX_Ground(float2 xz, out float3 albedo, out float height, out float3 normal, out float kind) {
	albedo = 0.0;
	height = 0.0;
	normal = float3(0.0, 1.0, 0.0);
	kind = 0.0;
	int n = (int)_NFX_GroundMapParams.w;
	if (n <= 0) {
		return false;
	}
	float2 cell = (xz - _NFX_GroundMapParams.xy) / _NFX_GroundMapParams.z + n * 0.5 - 0.5;
	int2 c0 = (int2)floor(cell);
	float2 f = cell - c0;
	float weight = 0.0;
	[unroll]
	for (int k = 0; k < 4; ++k) {
		int2 c = clamp(c0 + int2(k & 1, k >> 1), 0, n - 1);
		float w = ((k & 1) ? f.x : 1.0 - f.x) * ((k >> 1) ? f.y : 1.0 - f.y);
		uint i = (uint)(c.y * n + c.x) * 2u;
		float4 a = _NFX_GroundMap[i];
		float4 b = _NFX_GroundMap[i + 1u];
		if (a.w < -1e8) {
			continue;
		}
		albedo += a.rgb * w;
		height += a.w * w;
		normal += b.xyz * w;
		kind = max(kind, b.w * step(0.25, w));
		weight += w;
	}
	if (weight < 0.5 || any(c0 < 0) || any(c0 >= n - 1)) {
		return false;
	}
	albedo /= weight;
	height /= weight;
	normal = normalize(normal);
	return true;
}

void NFX_ApplyGroundBlend(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS) {
	float reach = _NFX_Surface.w;
	if (reach < 0.0) {
		return;
	}
	if (reach == 0.0) {
		reach = _NFX_GroundBlendDefault;
	}
	if (reach <= 0.0) {
		return;
	}
	float3 albedo, normal;
	float height, kind;
	if (!NFX_Ground(positionAWS.xz, albedo, height, normal, kind)) {
		return;
	}
	float above = positionAWS.y - height;
	// the ground itself (on it and facing up), what is far above it or buried deep: as it is
	if (above > reach || above < -0.5 || (abs(above) < 0.04 && geomNormalWS.y > 0.7)) {
		return;
	}
	float t = saturate(1.0 - max(above, 0.0) / reach);
	t *= t;
	// the edge broken up: soil splashed up unevenly, not a ruler line
	float noise = NFX_Fbm(positionAWS.xz * 2.7 + positionAWS.y * 1.9);
	t = saturate(t * 1.7 - noise * 0.7);
	// fades out towards the map's edge
	float2 fromCentre = abs(positionAWS.xz - _NFX_GroundMapParams.xy) / (_NFX_GroundMapParams.z * _NFX_GroundMapParams.w * 0.5);
	t *= saturate((1.0 - max(fromCentre.x, fromCentre.y)) * 8.0);
	s.baseColor = lerp(s.baseColor, albedo, t * 0.85);
	s.smoothness = lerp(s.smoothness, min(s.smoothness, 0.2), t * 0.8);
	s.metallic = lerp(s.metallic, 0.0, t);
	s.occlusion = lerp(s.occlusion, s.occlusion * 0.85, t);
}

// Plants' cover around xz from the ground map (GroundMap.hlsl packs it into the kind: + 0.4 where a ray down met a
// plant first), 0 bare .. 1 under plants, over two rings (0.35 and 0.8 m): soft across a metre
float NFX_GroundCover(float2 xz) {
	int n = (int)_NFX_GroundMapParams.w;
	if (n <= 0) {
		return 0.0;
	}
	float total = 0.0;
	float weight = 0.0;
	[unroll]
	for (int k = 0; k < 17; ++k) {
		float angle = k * 0.785398 + (k > 8 ? 0.3927 : 0.0);
		float2 o = k == 0 ? 0.0 : float2(cos(angle), sin(angle)) * (k > 8 ? 0.8 : 0.35);
		int2 c = (int2)floor((xz + o - _NFX_GroundMapParams.xy) / _NFX_GroundMapParams.z + n * 0.5);
		if (any(c < 0) || any(c >= n)) {
			continue;
		}
		float w = k == 0 ? 3.0 : k > 8 ? 0.6 : 1.0;
		float kind = _NFX_GroundMap[(uint)(c.y * n + c.x) * 2u + 1u].w;
		total += saturate((kind - round(kind)) / 0.4) * w;
		weight += w;
	}
	return weight > 0.0 ? total / weight : 0.0;
}

// the ground itself (not the road) where plants cover it or stand near: the plants' shade, damp soil, old leaves
void NFX_ApplyGroundCover(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS) {
	if (_NFX_GroundMapParams.w <= 0.0 || geomNormalWS.y < 0.6) {
		return;
	}
	float3 albedo, normal;
	float height, kind;
	if (!NFX_Ground(positionAWS.xz, albedo, height, normal, kind) || abs(positionAWS.y - height) > 0.08 || kind > 1.5) {
		return;
	}
	float cover = NFX_GroundCover(positionAWS.xz);
	if (cover <= 0.01) {
		return;
	}
	// uneven: the litter reaches out further in places
	float m = saturate(cover * 1.8 - NFX_Fbm(positionAWS.xz * 1.7) * 0.6);
	float3 litter = s.baseColor * float3(0.6, 0.58, 0.48) + float3(0.02, 0.024, 0.01);
	s.baseColor = lerp(s.baseColor, litter, m * 0.8);
	s.occlusion *= 1.0 - 0.4 * m;
	s.smoothness = lerp(s.smoothness, min(s.smoothness, 0.15), m);
}

// a plant's card near the ground: darker towards its foot (its own shade) and towards the ground's colour, and the
// last 7 cm cut away unevenly (the same in every pass): no straight line where a card stands in the ground
void NFX_ApplyFoliageFoot(inout NFXSurface s, float3 positionAWS) {
	float3 albedo, normal;
	float height, kind;
	if (!NFX_Ground(positionAWS.xz, albedo, height, normal, kind)) {
		return;
	}
	float above = positionAWS.y - height;
	if (above > 0.35) {
		return;
	}
	float t = saturate(1.0 - max(above, 0.0) / 0.35);
	s.baseColor = lerp(s.baseColor, albedo * 0.6, t * t * 0.6);
	s.occlusion *= 1.0 - 0.5 * t;
	clip(above / 0.07 - NFX_Hash13(floor(positionAWS * 40.0)));
}

void NFX_ApplyPaint(inout NFXSurface s, float3 positionAWS, float3 geomNormalWS) {
	if (_NFX_PaintParams.w < 0.5) {
		return;
	}
	float2 uv = (positionAWS.xz - _NFX_PaintParams.xy) / _NFX_PaintParams.z;
	if (any(uv <= 0.0) || any(uv >= 1.0)) {
		return;
	}
	float4 p = SAMPLE_TEXTURE2D_LOD(_NFX_PaintMap, s_linear_clamp_sampler, uv, 0);
	if (p.r + p.g + p.b <= 0.001) {
		return;
	}
	float up = saturate(geomNormalWS.y);
	float below = p.a - positionAWS.y; // how far under the top the paint reaches
	float fine = NFX_Fbm(positionAWS.xz * 6.0 + positionAWS.y * 4.0);
	float big = NFX_Fbm(positionAWS.xz * 0.8);

	// mud splashed up from the road: blotches, thicker low down, rough and brown
	if (p.r > 0.0 && below > 0.0) {
		float m = p.r * saturate(below / 0.25) * smoothstep(0.35, 0.7, fine * 0.7 + big * 0.5);
		s.baseColor = lerp(s.baseColor, float3(0.16, 0.12, 0.08) * (0.8 + fine * 0.4), m * 0.85);
		s.smoothness = lerp(s.smoothness, 0.1, m);
		s.metallic = lerp(s.metallic, 0.0, m);
	}
	// water stains running down a wall's foot: dark streaks, on steep faces
	if (p.g > 0.0 && below > 0.0) {
		float streak = NFX_ValueNoise(float2(dot(positionAWS.xz, float2(0.7, 0.7)) * 7.0, positionAWS.y * 0.6));
		float m = p.g * smoothstep(0.35, 0.75, streak) * saturate(below / 0.4) * smoothstep(0.2, 0.6, 1.0 - up);
		s.baseColor *= 1.0 - m * 0.5;
		s.smoothness = lerp(s.smoothness, 0.08, m);
	}
	// ruts: the driven lines darker and a little smoother (rubber and oil), on the ground
	if (p.b > 0.0) {
		float m = p.b * up * (0.7 + big * 0.3);
		s.baseColor *= 1.0 - m * 0.4;
		s.smoothness = lerp(s.smoothness, max(s.smoothness, 0.35), m * 0.5);
	}
}

#endif

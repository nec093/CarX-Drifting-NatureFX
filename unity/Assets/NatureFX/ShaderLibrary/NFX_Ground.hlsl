#ifndef NFX_GROUND_INCLUDED
#define NFX_GROUND_INCLUDED

// What the ground map gives the map's surfaces (GroundMap.cs, NFXNative GroundMap.hlsl): the ground seen from above
// around the camera, each cell its colour, height, normal and kind. Read by the foot of the plants' cards
// (NFX_ApplyFoliageFoot): shaded and the ground's colour where they stand in it.

StructuredBuffer<float4> _NFX_GroundMap;  // two per cell: albedo rgb + height (absolute; < -1e8 none), normal xyz + kind
float4 _NFX_GroundMapParams;              // x, y the grid's centre (absolute x, z), z the cell size (m), w cells per side (0: none)

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

#endif

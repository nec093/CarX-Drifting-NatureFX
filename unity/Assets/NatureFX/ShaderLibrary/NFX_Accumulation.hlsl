#ifndef NFX_ACCUMULATION_INCLUDED
#define NFX_ACCUMULATION_INCLUDED

#include "NFX_Common.hlsl"

static const float3 NFX_SnowColor = float3(0.90, 0.92, 0.96);
static const float3 NFX_DustColor = float3(0.52, 0.45, 0.36);
static const float3 NFX_LeafColorA = float3(0.55, 0.20, 0.05);
static const float3 NFX_LeafColorB = float3(0.70, 0.45, 0.08);
static const float3 NFX_LeafColorC = float3(0.30, 0.18, 0.08);

// Fallen leaves: scattered round-ish blobs on a jittered grid, per-leaf colour.
float NFX_LeafMask(float2 p, out float3 color) {
	float2 cell = floor(p);
	float2 f = frac(p);
	float best = 0.0;
	color = NFX_LeafColorA;

	[unroll]
	for (int y = -1; y <= 1; ++y) {
		[unroll]
		for (int x = -1; x <= 1; ++x) {
			float2 c = cell + float2(x, y);
			float h = NFX_Hash12(c);
			float2 center = float2(x, y) + float2(h, NFX_Hash12(c + 7.3));
			float2 d = f - center;
			// elongated leaf shape with random rotation
			float a = h * 6.2832;
			float2 r = float2(d.x * cos(a) - d.y * sin(a), d.x * sin(a) + d.y * cos(a));
			r.x *= 1.8;
			float m = smoothstep(0.42, 0.30, length(r));
			if (m > best) {
				best = m;
				float k = NFX_Hash12(c + 3.1);
				color = k < 0.4 ? NFX_LeafColorA : (k < 0.8 ? NFX_LeafColorB : NFX_LeafColorC);
				color *= 0.8 + 0.4 * NFX_Hash12(c + 11.7);
			}
		}
	}
	return best;
}

// Shader Graph "Custom Function" node (Type: File, Name: NFX_Accumulation), placed before the Fragment outputs.
//   PositionAWS : Position node, Absolute World
//   NormalWS    : Normal Vector node, World (use the normal AFTER normal mapping for best results)
//   Exposure    : 1 = open sky, 0 = fully sheltered (e.g. under a bridge). Use 1 or vertex color A.
//   Porosity    : how much the surface darkens when wet (asphalt ~1, metal ~0)
// Outputs:
//   NormalFlatten: 0..1, lerp the tangent normal towards (0,0,1) by this (snow / puddles cover detail)
void NFX_Accumulation_float(
	float3 PositionAWS, float3 NormalWS, float3 BaseColor, float Smoothness, float Metallic, float Exposure, float Porosity,
	out float3 OutBaseColor, out float OutSmoothness, out float OutMetallic, out float NormalFlatten) {

	float snow = _NFX_Accumulation.x;
	float leaves = _NFX_Accumulation.y;
	float dust = _NFX_Accumulation.z;
	float wet = _NFX_Accumulation.w;

	float up = saturate(NormalWS.y);
	float2 uv = PositionAWS.xz;
	float large = NFX_Fbm(uv * 0.15);
	float detail = NFX_Fbm(uv * 1.7);
	Exposure = saturate(Exposure);

	float3 color = BaseColor;
	float smooth = Smoothness;
	float metal = Metallic;
	float flatten = 0.0;

	// Dust: settles everywhere, mostly on upward faces
	float dustMask = saturate(dust * (0.4 + up * 0.8) * (0.6 + detail * 0.8));
	color = lerp(color, NFX_DustColor * (0.85 + detail * 0.3), dustMask * 0.75);
	smooth = lerp(smooth, 0.15, dustMask);
	metal = lerp(metal, 0.0, dustMask);

	// Fallen leaves: patches that grow from the noise field as the amount rises
	float3 leafColor;
	float leafShape = NFX_LeafMask(uv * 4.0, leafColor);
	float patch = smoothstep(1.0 - leaves, 1.0 - leaves + 0.25, large + detail * 0.2);
	float leafMask = leafShape * patch * pow(up, 2.0) * saturate(leaves * 3.0) * Exposure;
	color = lerp(color, leafColor, leafMask);
	smooth = lerp(smooth, 0.25, leafMask);
	metal = lerp(metal, 0.0, leafMask);

	// Wetness: darker porous surfaces, glossier everything, puddles in flat low spots
	float wetMask = wet * Exposure;
	float darken = lerp(1.0, 0.55, wetMask * saturate(Porosity));
	color *= darken;
	smooth = lerp(smooth, 0.88, wetMask);
	float puddle = smoothstep(0.55, 0.45, large + (1.0 - wet) * 0.6) * pow(up, 8.0) * wetMask;
	color = lerp(color, color * 0.6, puddle);
	smooth = lerp(smooth, 0.98, puddle);
	flatten = max(flatten, puddle);

	// Snow: covers everything facing up, edges broken up by noise; it hides leaves and puddles
	float threshold = 1.0 - snow * 1.35;
	float snowMask = smoothstep(threshold, threshold + 0.2, up + (detail - 0.5) * 0.35) * saturate(snow * 4.0) * Exposure;
	color = lerp(color, NFX_SnowColor * (0.92 + detail * 0.08), snowMask);
	smooth = lerp(smooth, 0.4, snowMask);
	metal = lerp(metal, 0.0, snowMask);
	flatten = max(flatten, snowMask * 0.8);

	OutBaseColor = color;
	OutSmoothness = smooth;
	OutMetallic = metal;
	NormalFlatten = flatten;
}

#endif

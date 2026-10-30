#ifndef NFX_COMMON_INCLUDED
#define NFX_COMMON_INCLUDED

// Globals driven by the NatureFX mod (see src/NatureFX/Nature/ShaderGlobals.cs).
// Do NOT add these to a Shader Graph blackboard - they are set with Shader.SetGlobal*.
float4 _NFX_Wind;           // xyz: world direction, w: strength
float4 _NFX_WindParams;     // x: gust freq, y: gust strength, z: flutter freq, w: flutter strength
float  _NFX_WindTime;
float4 _NFX_Accumulation;   // x: snow, y: leaves, z: dust, w: wetness
float4 _NFX_Interactors[8]; // xyz: absolute world pos, w: radius
float  _NFX_InteractorCount;
float  _NFX_FoliageBend;     // wind weight used by the Lit-based foliage shader (no per-vertex weight available)
float4 _NFX_Weather;        // x: rain intensity (ripples, drips), y: snow depth in metres at full snow, z: surface detail
float4 _NFX_Road;           // x: wear of the driven ground, y: its height (absolute world), z: 1 = height known
float4 _NFX_TrackParams;    // xy: world origin of the track map, z: 1 / world size, w: enabled
TEXTURE2D(_NFX_TrackMap);   // r: rubber, g: dry line, b: compression
float4 _NFX_Lights;         // x: sun elevation (degrees), y: unscaled time (lamp flicker)

float NFX_Hash12(float2 p) {
	float3 p3 = frac(float3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return frac((p3.x + p3.y) * p3.z);
}

float NFX_Hash13(float3 p3) {
	p3 = frac(p3 * 0.1031);
	p3 += dot(p3, p3.zyx + 31.32);
	return frac((p3.x + p3.y) * p3.z);
}

float NFX_ValueNoise(float2 p) {
	float2 i = floor(p);
	float2 f = frac(p);
	float2 u = f * f * (3.0 - 2.0 * f);
	float a = NFX_Hash12(i);
	float b = NFX_Hash12(i + float2(1, 0));
	float c = NFX_Hash12(i + float2(0, 1));
	float d = NFX_Hash12(i + float2(1, 1));
	return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float NFX_Fbm(float2 p) {
	float v = 0.0;
	float a = 0.5;
	[unroll]
	for (int i = 0; i < 4; ++i) {
		v += a * NFX_ValueNoise(p);
		p = p * 2.03 + 17.1;
		a *= 0.5;
	}
	return v;
}

// Tyre marks / dry line / compression at a world position (0 outside the map or when disabled)
float4 NFX_Track(float2 xz) {
	if (_NFX_TrackParams.w <= 0.0) {
		return 0.0;
	}
	float2 uv = (xz - _NFX_TrackParams.xy) * _NFX_TrackParams.z;
	if (any(uv < 0.0) || any(uv > 1.0)) {
		return 0.0;
	}
	return SAMPLE_TEXTURE2D_LOD(_NFX_TrackMap, s_linear_clamp_sampler, uv, 0);
}

#endif

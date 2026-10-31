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
float  _NFX_VertexColors;    // 1: the map's vertex colours drive wind and exposure ([map] vertexColors, docs/map-authoring.md 2)
float4 _NFX_Headlights[4];   // the headlights nearest the camera: xyz absolute world position, w intensity (cd; 0 = none)
float4 _NFX_HeadlightDirs[4]; // xyz direction, w cosine of the half cone angle (retroreflection, NFX_MaterialProfile.hlsl)
float4 _NFX_Season;          // x: 0 spring, 1 summer, 2 autumn, 3 winter (blends between; < 0: no seasonal colours)
float4 _NFX_SunDir;          // xyz: towards the sun (world), w: 1 by day .. 0 at night (back lit leaves)
float4 _NFX_Puddles[16];     // puddle markers: xyz centre (absolute world), w how full (0 dry .. 1 after long rain)
float4 _NFX_PuddleSizes[16]; // xyz half size (m), w yaw (radians)
float  _NFX_PuddleCount;
float4 _NFX_Ice;             // x: how frozen wet roads are (0 .. 1, black ice)

// Noise from a texture the mod makes at start (ShaderGlobals.CreateNoiseTexture): 1024 texels over 256 units, so it
// repeats every 256 m. r: value noise (NFX_ValueNoise, the 4 lattice hashes and the smooth blend baked, read with
// bilinear filtering), b: white noise per integer cell (NFX_Hash12, read at the cell's texel). Computed in the
// shader, the hashes made FXC take 10 to 30 seconds per variant (every block below inlines dozens of them: 6 hours
// of bundle build); as lookups a variant compiles in a few seconds and the GPU does less too. Explicit LOD 0, so
// they are allowed in loops and in the vertex stage.
TEXTURE2D(_NFX_NoiseTex);
#define NFX_NOISE_PERIOD 256.0

// White noise for an integer cell (the fraction of p is dropped: callers pass cells, plus seeds like + 7.3)
float NFX_Hash12(float2 p) {
	return SAMPLE_TEXTURE2D_LOD(_NFX_NoiseTex, s_point_clamp_sampler, frac((floor(p) + 0.5) * (1.0 / NFX_NOISE_PERIOD)), 0).b;
}

float NFX_Hash13(float3 p3) {
	p3 = frac(p3 * 0.1031);
	p3 += dot(p3, p3.zyx + 31.32);
	return frac((p3.x + p3.y) * p3.z);
}

// Smooth value noise (one unit per lattice cell), 0 .. 1
float NFX_ValueNoise(float2 p) {
	return SAMPLE_TEXTURE2D_LOD(_NFX_NoiseTex, s_linear_repeat_sampler, p * (1.0 / NFX_NOISE_PERIOD), 0).r;
}

float NFX_Fbm(float2 p) {
	float v = 0.0;
	float a = 0.5;
	[loop]
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

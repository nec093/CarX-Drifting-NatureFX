#ifndef NFX_LITDATA_INCLUDED
#define NFX_LITDATA_INCLUDED

// Replaces the HDRP LitData.hlsl include in the generated NatureFX/* copies of HDRP/Lit
// (see Editor/GenerateNatureFXShaders.cs). The original functions are compiled under new names
// and wrapped, so all HDRP/Lit properties, keywords and passes keep working unchanged.
//   NFX_WIND         : wind / foliage interaction in ApplyVertexModification
//   NFX_ACCUMULATION : snow / leaves / dust / wetness / rain in GetSurfaceAndBuiltinData,
//                      plus snow thickness in ApplyVertexModification (ground shader only)

// hex tiled texture sampling (anti-tiling), in place of HDRP's SampleUVMapping.hlsl that LitData.hlsl includes
#include "NFX_AntiTiling.hlsl"

#define ApplyVertexModification NFX_ApplyVertexModification_Lit
#define GetSurfaceAndBuiltinData NFX_GetSurfaceAndBuiltinData_Lit
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl"
#undef ApplyVertexModification
#undef GetSurfaceAndBuiltinData

#include "NFX_Wind.hlsl"
#include "NFX_Accumulation.hlsl"
#include "NFX_MaterialProfile.hlsl"
#include "NFX_Ground.hlsl"
#include "NFX_Foliage.hlsl"
#include "NFX_Impostor.hlsl"

// Called from VertMesh.hlsl (HAVE_VERTEX_MODIFICATION is defined by Lit.shader).
// The motion vector pass calls it again with the previous frame's time, so wind uses timeParameters.
void ApplyVertexModification(AttributesMesh input, float3 normalWS, inout float3 positionRWS, float3 timeParameters) {
	NFX_ApplyVertexModification_Lit(input, normalWS, positionRWS, timeParameters);
#ifdef NFX_WIND
	// a far plant's impostor: the quad turned to the camera (NFX_Impostor.hlsl), still
	if (_NFX_Impostor.w > 0.5) {
		positionRWS = NFX_ImpostorVertexRWS(input.positionOS);
		return;
	}
#endif
	float3 positionAWS = GetAbsolutePositionWS(positionRWS);
#ifdef NFX_WIND
	float3 originAWS = GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23);
	// No per-vertex root/tip weight on game meshes: use the height above the object origin
	// (plants and per-tree leaf meshes have their pivot at the base), with a floor so merged tiles still move.
	float weight = saturate(0.35 + input.positionOS.y * 0.12);
	float stiffness = 0.0;
	float phase = NFX_Hash13(floor(originAWS * 2.0)) * 6.2832;
	float bend = _NFX_FoliageBend;
	float bladeLength = 0.0; // > 0: a generated grass blade, bent at its root keeping its length
	float3 rootRWS = 0.0;
	#ifdef ATTRIBUTES_NEED_COLOR
	// the map's vertex colours ([map] vertexColors): R sway weight, G phase, B stiffness (docs/map-authoring.md 2).
	// Every pass reads them (the generator asks for the colour in the depth and shadow passes too), so the shadows
	// sway with the leaves; they are painted for this, so they move even where the map's drawn sway is off
	if (_NFX_VertexColors > 0.5 || _NFX_Foliage2.w > 0.5) {
		weight = input.color.r;
		phase = input.color.g * 6.2832;
		stiffness = input.color.b;
		bend = max(bend, 1.0);
	}
	// generated grass blades (NFX_Foliage.hlsl): R height share, G phase, B the blade's height / 2 m
	if (_NFX_Foliage2.x > 0.5) {
		float bladeHeight = input.color.b * 2.0;
		weight = input.color.r;
		phase = input.color.g * 6.2832;
		stiffness = 0.0;
		bend = 1.0;
		float heightAbove = weight * bladeHeight;
		rootRWS = positionRWS - float3(0.0, heightAbove, 0.0);
		positionRWS += NFX_GrassBladeOffset(positionAWS, heightAbove, bladeHeight, input.color.g);
		// tyres and snow press the blade down and shorten it; the wind and the cars then only swing it round
		bladeLength = length(positionRWS - rootRWS);
	}
	#endif
	positionRWS += NFX_WindOffsetPhaseWS(positionAWS, phase, weight, stiffness, timeParameters.x) * bend;
	if (bladeLength > 1e-3) {
		// the offsets move the blade sideways; pulled back to its length from the root it swings round and bends
		// over instead of stretching and shearing
		float3 arm = positionRWS - rootRWS;
		positionRWS = rootRWS + arm * (bladeLength / max(length(arm), 1e-4));
	}
#elif defined(NFX_ACCUMULATION)
	positionRWS += NFX_SnowDisplacementWS(positionAWS, normalWS);
#endif
}

void GetSurfaceAndBuiltinData(FragInputs input, float3 V, inout PositionInputs posInput, out SurfaceData surfaceData, out BuiltinData builtinData RAY_TRACING_OPTIONAL_PARAMETERS) {
#if defined(NFX_WIND) && !defined(SHADER_STAGE_RAY_TRACING)
	// a far plant's impostor: the tile of the frame the camera sees, lit through the quad's tangent frame
	if (_NFX_Impostor.w > 0.5) {
		NFX_ImpostorFragment(input);
	}
#endif
	NFX_GetSurfaceAndBuiltinData_Lit(input, V, posInput, surfaceData, builtinData);
#ifdef _MASKMAP
	// CarX Colormask (parked cars, props): the mask map is HDRP's layout (r metal, g AO, a smoothness) with the
	// blue channel marking the painted panels, tinted by the paint colour (MaterialReplacer's translator)
	if (_NFX_Paint.a > 0.5)
	{
		float2 paintUV = input.texCoord0.xy * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
		float painted = SAMPLE_TEXTURE2D(_MaskMap, sampler_MaskMap, paintUV).b;
		surfaceData.baseColor *= lerp(1.0, _NFX_Paint.rgb, painted);
	}
#endif
#ifdef NFX_ACCUMULATION
	float3 positionAWS = GetAbsolutePositionWS(posInput.positionWS);
	#ifdef NFX_WIND
	float porosity = NFX_Porosity(0.3); // leaves barely darken when wet
	#else
	float porosity = NFX_Porosity(1.0);
	#endif
	float3 geomNormalWS = input.tangentToWorld[2];
	// [materials] variation=: brightness and roughness wander over tens of metres, so a tiled ground or wall does not
	// look the same all the way along (the repeat the hex tiles hide up close, this hides from afar)
	if (_NFX_Surface.y > 0.0 && _NFX_Foliage.x < 0.5) {
		float2 p = positionAWS.xz / max(_NFX_Surface.z, 1.0);
		float n = NFX_ValueNoise(p) * 0.65 + NFX_ValueNoise(p * 3.7 + 13.0) * 0.35;
		float k = (n - 0.5) * 2.0 * _NFX_Surface.y;
		surfaceData.baseColor *= 1.0 + k * 0.35;
		surfaceData.perceptualSmoothness = saturate(surfaceData.perceptualSmoothness * (1.0 - k * 0.3));
	}
	// emission follows the texture as painted, before snow or dirt land on it
	builtinData.emissiveColor += NFX_MaterialEmission(surfaceData.baseColor, builtinData.opacity, positionAWS);

	NFXSurface s;
	s.baseColor = surfaceData.baseColor;
	s.smoothness = surfaceData.perceptualSmoothness;
	s.metallic = surfaceData.metallic;
	s.normalWS = surfaceData.normalWS;
	s.occlusion = surfaceData.ambientOcclusion;
	// the surface's height for the height fill: its height map where the material has one, else its brightness (the
	// gaps between stones, cracks and joints are dark)
	s.height = saturate(dot(s.baseColor, float3(0.2126, 0.7152, 0.0722)) * 2.2);
	if (_NFX_Surface2.x > 0.5) {
		float2 heightUV = input.texCoord0.xy * _BaseColorMap_ST.xy + _BaseColorMap_ST.zw;
		s.height = SAMPLE_TEXTURE2D(_HeightMap, sampler_HeightMap, heightUV).r;
	}
	// the map's vertex colour A: how open to the sky (0 under roofs, bridges and in tunnels: no snow, leaves or wet)
	float exposure = 1.0;
	#ifdef VARYINGS_NEED_COLOR
	exposure = _NFX_VertexColors > 0.5 && _NFX_Foliage2.x < 0.5 ? input.color.a : 1.0;
	#endif

	// water surfaces (nocol_nfx_water_*, Map/MapWater.cs): dark, mirror smooth, moving ripples; no snow or leaves on it
	if (_NFX_Water.x > 0.5) {
		NFX_ApplyWater(s, positionAWS, geomNormalWS, _TimeParameters.x);
		surfaceData.baseColor = s.baseColor;
		surfaceData.perceptualSmoothness = s.smoothness;
		surfaceData.metallic = s.metallic;
		surfaceData.normalWS = s.normalWS;
		surfaceData.ambientOcclusion = s.occlusion;
		return;
	}

	#ifdef NFX_WIND
	// [foliage]: season colours, variation, back lit leaves; deciduous trees drop leaves in autumn and winter
	if (_NFX_Foliage.x > 0.5 || _NFX_Foliage2.x > 0.5) {
		float cutoff = 0.5;
		#ifdef _ALPHATEST_ON
		cutoff = _AlphaCutoff;
		#endif
		#ifdef VARYINGS_NEED_COLOR
		if (_NFX_Foliage2.x > 0.5) {
			// grass blades: darker at the root, each blade a little brighter or darker, plumes on susuki in autumn
			s.baseColor *= lerp(0.55, 1.0, input.color.r) * lerp(0.8, 1.15, input.color.a);
			float plume = _NFX_Foliage2.z * smoothstep(0.82, 0.9, input.color.r) * step(1.6, _NFX_Season.x) * step(_NFX_Season.x, 3.2);
			s.baseColor = lerp(s.baseColor, float3(0.78, 0.72, 0.6), plume);
		}
		#endif
		float keep = NFX_ApplyFoliage(s.baseColor, s.normalWS, positionAWS, V, builtinData.opacity, cutoff);
		// measured colours: a real leaf or blade of grass sends back some 4 to 12 % of the light (its luminance), an
		// olive green; textures are painted brighter and greener (sRGB 90, 140, 50 is 22 % and far more saturated).
		// Above 6 % the brightness is eased (a knee: 20 % comes out at 12 %), the colour a fifth less saturated; the
		// autumn colours keep their hue
		if (_NFX_FoliageTone.x > 0.0) {
			float y = dot(s.baseColor, float3(0.2126, 0.7152, 0.0722));
			float eased = y > 0.06 ? 0.06 + (y - 0.06) * 0.43 : y;
			float3 toned = s.baseColor * (eased / max(y, 1e-4));
			toned = lerp(eased, toned, 0.8);
			s.baseColor = lerp(s.baseColor, max(toned, 0.0), _NFX_FoliageTone.x);
		}
		#ifdef _ALPHATEST_ON
		if (keep < 0.999) {
			// whole bunches go: smooth noise over the card, the same in every pass (depth, shadows, G-buffer)
			clip(keep - NFX_ValueNoise(input.texCoord0.xy * 9.0 + NFX_Hash13(floor(positionAWS / 6.0)) * 17.0));
		}
		#endif
	}
	#endif
	#if defined(NFX_WIND) && defined(_ALPHATEST_ON) && !defined(SHADER_STAGE_RAY_TRACING)
	// a plant's card where it meets the ground: shaded, the ground's colour, the last few cm fading out unevenly
	if (_NFX_Foliage2.x < 0.5 && _NFX_Impostor.w < 0.5) {
		NFX_ApplyFoliageFoot(s, positionAWS);
	}
	#endif
	NFX_ApplyTerrain(s.baseColor, positionAWS, geomNormalWS);
	NFX_ApplyAgeing(s, positionAWS, geomNormalWS);
	// where it stands in the ground, the ground's colour (not grass blades or leaf cards: they grow out of it)
	#ifdef NFX_WIND
	#ifndef _ALPHATEST_ON
	if (_NFX_Foliage2.x < 0.5)
		NFX_ApplyGroundBlend(s, positionAWS, geomNormalWS);
	#endif
	#else
	NFX_ApplyGroundBlend(s, positionAWS, geomNormalWS);
	// the ground under and around plants: shaded, damp, litter (the ground map's cover)
	NFX_ApplyGroundCover(s, positionAWS, geomNormalWS);
	#endif
	// the map's painted dirt: mud up the verges, water stains down the walls, ruts
	NFX_ApplyPaint(s, positionAWS, geomNormalWS);
	// the map's decals under the weather: snow and leaves cover them
	NFX_ApplyDecals(s, positionAWS, geomNormalWS, V);
	#ifdef NFX_WIND
	// leaves and grass: wet, only a little glossier (a soaked forest at mirror gloss sent every reflection ray deep
	// into the alpha tested foliage: the reflections' trace went from 1.5 to 13.6 ms in rain)
	float foliageSmoothness = s.smoothness;
	#endif
	NFX_ApplyAccumulation(s, positionAWS, geomNormalWS, V, porosity, exposure, _TimeParameters.x, length(posInput.positionWS));
	#ifdef NFX_WIND
	s.smoothness = min(s.smoothness, max(foliageSmoothness, 0.0) + 0.15);
	#endif
	NFX_ApplyBeading(s, positionAWS, geomNormalWS, max(_NFX_Accumulation.w, _NFX_Weather.x));
	NFX_ApplyPuddleMarkers(s, positionAWS, geomNormalWS, exposure, _TimeParameters.x);
	// white lines, signs and reflectors send the headlights' light back towards them (after snow and dirt: a covered
	// marking does not shine)
	builtinData.emissiveColor += NFX_Retroreflection(s.baseColor, positionAWS, geomNormalWS, max(_NFX_Accumulation.w, _NFX_Weather.x) * exposure);

	surfaceData.baseColor = s.baseColor;
	surfaceData.perceptualSmoothness = s.smoothness;
	surfaceData.metallic = s.metallic;
	surfaceData.normalWS = s.normalWS;
	surfaceData.ambientOcclusion = s.occlusion;
#endif
}

#endif

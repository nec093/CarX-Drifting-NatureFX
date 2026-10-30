#ifndef NFX_LITDATA_INCLUDED
#define NFX_LITDATA_INCLUDED

// Replaces the HDRP LitData.hlsl include in the generated NatureFX/* copies of HDRP/Lit
// (see Editor/GenerateNatureFXShaders.cs). The original functions are compiled under new names
// and wrapped, so all HDRP/Lit properties, keywords and passes keep working unchanged.
//   NFX_WIND         : wind / foliage interaction in ApplyVertexModification
//   NFX_ACCUMULATION : snow / leaves / dust / wetness / rain in GetSurfaceAndBuiltinData,
//                      plus snow thickness in ApplyVertexModification (ground shader only)

#define ApplyVertexModification NFX_ApplyVertexModification_Lit
#define GetSurfaceAndBuiltinData NFX_GetSurfaceAndBuiltinData_Lit
#include "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl"
#undef ApplyVertexModification
#undef GetSurfaceAndBuiltinData

#include "NFX_Wind.hlsl"
#include "NFX_Accumulation.hlsl"
#include "NFX_MaterialProfile.hlsl"

// Called from VertMesh.hlsl (HAVE_VERTEX_MODIFICATION is defined by Lit.shader).
// The motion vector pass calls it again with the previous frame's time, so wind uses timeParameters.
void ApplyVertexModification(AttributesMesh input, float3 normalWS, inout float3 positionRWS, float3 timeParameters) {
	NFX_ApplyVertexModification_Lit(input, normalWS, positionRWS, timeParameters);
	float3 positionAWS = GetAbsolutePositionWS(positionRWS);
#ifdef NFX_WIND
	float3 originAWS = GetAbsolutePositionWS(UNITY_MATRIX_M._m03_m13_m23);
	// No per-vertex root/tip weight on game meshes: use the height above the object origin
	// (plants and per-tree leaf meshes have their pivot at the base), with a floor so merged tiles still move.
	float weight = saturate(0.35 + input.positionOS.y * 0.12);
	positionRWS += NFX_WindOffsetWS(positionAWS, originAWS, weight, 0.0, timeParameters.x) * _NFX_FoliageBend;
#elif defined(NFX_ACCUMULATION)
	positionRWS += NFX_SnowDisplacementWS(positionAWS, normalWS);
#endif
}

void GetSurfaceAndBuiltinData(FragInputs input, float3 V, inout PositionInputs posInput, out SurfaceData surfaceData, out BuiltinData builtinData RAY_TRACING_OPTIONAL_PARAMETERS) {
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
	// emission follows the texture as painted, before snow or dirt land on it
	builtinData.emissiveColor += NFX_MaterialEmission(surfaceData.baseColor, builtinData.opacity, positionAWS);

	NFXSurface s;
	s.baseColor = surfaceData.baseColor;
	s.smoothness = surfaceData.perceptualSmoothness;
	s.metallic = surfaceData.metallic;
	s.normalWS = surfaceData.normalWS;
	s.occlusion = surfaceData.ambientOcclusion;
	NFX_ApplyAgeing(s, positionAWS, geomNormalWS);
	NFX_ApplyAccumulation(s, positionAWS, geomNormalWS, V, porosity, 1.0, _TimeParameters.x, length(posInput.positionWS));
	NFX_ApplyBeading(s, positionAWS, geomNormalWS, max(_NFX_Accumulation.w, _NFX_Weather.x));

	surfaceData.baseColor = s.baseColor;
	surfaceData.perceptualSmoothness = s.smoothness;
	surfaceData.metallic = s.metallic;
	surfaceData.normalWS = s.normalWS;
	surfaceData.ambientOcclusion = s.occlusion;
#endif
}

#endif

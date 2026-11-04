using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace NatureFX.Editor {
	// Generates NatureFX/* shaders as copies of the installed HDRP/Lit with the LitData include swapped for
	// NFX_LitData.hlsl. Property names, keywords and passes stay identical to HDRP/Lit, so the mod can switch
	// a game material's shader without losing its textures or settings.
	public static class GenerateNatureFXShaders {
		private const string LitShaderPath = "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/Lit.shader";
		private const string LitDataInclude = "#include \"Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitData.hlsl\"";
		private const string NfxLitDataInclude = "#include \"Assets/NatureFX/ShaderLibrary/NFX_LitData.hlsl\"";
		private const string LitPropertiesPath = "Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl";
		private const string LitPropertiesInclude = "#include \"Packages/com.unity.render-pipelines.high-definition/Runtime/Material/Lit/LitProperties.hlsl\"";
		private const string OutputFolder = "Assets/NatureFX/Shaders/Generated";
		// HDRP's forward pass with the sun's ray traced shadow on transparent surfaces put in (NFX_TransparentShadow.hlsl)
		private const string ForwardPassPath = "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipeline/ShaderPass/ShaderPassForward.hlsl";
		private const string ForwardPassInclude = "#include \"" + ForwardPassPath + "\"";
		private const string NfxForwardPassPath = OutputFolder + "/NFX_ShaderPassForward.hlsl";
		private const string NfxForwardPassInclude = "#include \"" + NfxForwardPassPath + "\"";
		private const string ForwardLightingAnchor = "            float3 specularLighting = lightLoopOutput.specularLighting;\n";

		// Must match NFX_LitData.hlsl and src/NatureFX/Map/MaterialProfiles.cs
		private const string NfxMaterialCBuffer =
			"// NatureFX material profile (see NFX_LitData.hlsl)\n" +
			"float4 _NFX_Material;       // x: porosity (<0: shader default), yzw: unused (0)\n" +
			"float4 _NFX_Material2;      // xy: unused (0), z: water beading, w: retroreflection\n" +
			"float4 _NFX_Emission;       // rgb: emitted colour in nits, w: when (0 off, 1 night, 2 dusk, 3 always)\n" +
			"float4 _NFX_EmissionParams; // x: mask (0 bright texels, 1 alpha, 2 whole surface), y: share lit, z: flicker\n" +
			"float4 _NFX_Paint;          // rgb: paint colour where the mask map's blue channel is white, a: 1 = on (CarX Colormask)\n" +
			"float4 _NFX_Foliage;        // x: type (0 none, 1 conifer, 2 deciduous, 3 bamboo, 4 grass), y: translucency, z: variation, w: unused (0)\n" +
			"float4 _NFX_Foliage2;       // x: 1 = generated grass blades, y: hue shift (degrees), z: unused (0), w: 1 = vertex colours drive the sway\n" +
			"float4 _NFX_Water;          // x: 1 = water surface, y: flow direction (radians), z: flow speed (m/s), w: wind ripples\n" +
			"float4 _NFX_Terrain;        // x: 1 = triplanar, y: metres per texture tile (0: no [terrain]), z: slope where the steep look starts (0 flat .. 1 wall), w: blend width\n" +
			"float4 _NFX_Terrain2;       // x: 1 = steep map set, y: 1 = flat map set, z: noise in the blend, w: tile of the steep / flat maps (m)\n" +
			"float4 _NFX_Surface;        // x: anti-tiling (hex tiles, 0 off .. 1), y: large scale variation (0 .. 1), z: its size (m), w: unused (0)\n" +
			"float4 _NFX_Surface2;       // x: 1 = the height map gives the surface's relief for the water film (else the colour's brightness), yzw: unused\n" +
			"float4 _NFX_Impostor;       // x: frames per side, y: tile size (m), z: centre height (m), w: 1 = octahedral impostor (NFX_Impostor.hlsl)\n\n";

		private const string NfxMaterialProperties =
			"        [HideInInspector] _NFX_Material(\"NFX Material\", Vector) = (-1, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Material2(\"NFX Material 2\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Emission(\"NFX Emission\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_EmissionParams(\"NFX Emission Params\", Vector) = (0, 1, 0, 0)\n" +
			"        [HideInInspector] _NFX_Paint(\"NFX Paint\", Vector) = (1, 1, 1, 0)\n" +
			"        [HideInInspector] _NFX_Foliage(\"NFX Foliage\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Foliage2(\"NFX Foliage 2\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Water(\"NFX Water\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Terrain(\"NFX Terrain\", Vector) = (0, 0, 0.55, 0.15)\n" +
			"        [HideInInspector] _NFX_Terrain2(\"NFX Terrain 2\", Vector) = (0, 0, 0.5, 4)\n" +
			"        [HideInInspector] _NFX_Surface(\"NFX Surface\", Vector) = (0, 0, 20, 0)\n" +
			"        [HideInInspector] _NFX_Surface2(\"NFX Surface 2\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_Impostor(\"NFX Impostor\", Vector) = (0, 0, 0, 0)\n" +
			"        [HideInInspector] _NFX_SteepMap(\"NFX Steep Map\", 2D) = \"grey\" {}\n" +
			"        [HideInInspector] _NFX_FlatMap(\"NFX Flat Map\", 2D) = \"grey\" {}\n";

		private static readonly (string Name, string File, string[] Defines)[] Variants = {
			// ATTRIBUTES_NEED_COLOR: the vertex colour in every pass, also depth and shadows (map vertex colours drive the wind)
			("NatureFX/Foliage", "NFX_Foliage.shader", new[] { "NFX_WIND", "NFX_ACCUMULATION", "ATTRIBUTES_NEED_COLOR" }),
			("NatureFX/LitAccumulation", "NFX_LitAccumulation.shader", new[] { "NFX_ACCUMULATION" })
		};

		[MenuItem("NatureFX/Generate Shaders From HDRP Lit")]
		public static void Generate() {
			string source = File.ReadAllText(Path.GetFullPath(LitShaderPath));
			Directory.CreateDirectory(OutputFolder);
			GenerateForwardPass();

			foreach (var v in Variants) {
				string text = Patch(source, v.Name, v.Defines);
				string path = $"{OutputFolder}/{v.File}";
				File.WriteAllText(path, text);
				AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
				Debug.Log($"[NatureFX] Generated {v.Name} -> {path}");
			}
		}

		// HDRP's ShaderPassForward.hlsl with the transparent surfaces' sun shadow taken off right after the light loop
		private static void GenerateForwardPass() {
			string text = File.ReadAllText(Path.GetFullPath(ForwardPassPath)).Replace("\r\n", "\n");
			if (CountOf(text, ForwardLightingAnchor) != 1) {
				throw new Exception("The light loop's output not found in ShaderPassForward.hlsl; HDRP version changed?");
			}
			// the light loop's output is exposed only after the anchor in HDRP 2023 ("diffuseLighting *= GetCurrentExposureMultiplier()"):
			// the fog's light goes in unexposed then, else at the current exposure
			string exposure = text.Contains("diffuseLighting *= GetCurrentExposureMultiplier();") ? "1.0" : "GetCurrentExposureMultiplier()";
			text = text.Replace(ForwardLightingAnchor, ForwardLightingAnchor +
				"#if defined(_SURFACE_TYPE_TRANSPARENT)\n" +
				"            // NatureFX: the sun's ray traced shadow (NFX_TransparentShadow.hlsl)\n" +
				"            NFX_TransparentSunShadow(V, posInput, preLightData, bsdfData, builtinData, diffuseLighting, specularLighting);\n" +
				"            // NatureFX: the own engine's 3D fog in front of the surface (NFX_FogCommon.hlsl)\n" +
				"            NFX_FogForwardLighting(posInput, V, " + exposure + ", diffuseLighting, specularLighting);\n" +
				"#endif\n");
			text = "// Generated by Assets/NatureFX/Editor/GenerateNatureFXShaders.cs from HDRP's ShaderPassForward.hlsl. Do not edit.\n" +
			       "#include \"Assets/NatureFX/ShaderLibrary/NFX_TransparentShadow.hlsl\"\n" +
			       "#include \"Assets/NatureFX/ShaderLibrary/NFX_FogCommon.hlsl\"\n" + text;
			File.WriteAllText(NfxForwardPassPath, text);
			AssetDatabase.ImportAsset(NfxForwardPassPath, ImportAssetOptions.ForceUpdate);
		}

		private static string Patch(string lit, string shaderName, string[] defines) {
			const string header = "Shader \"HDRP/Lit\"";
			if (!lit.StartsWith(header, StringComparison.Ordinal)) {
				throw new Exception("Unexpected Lit.shader header; HDRP version changed?");
			}
			string text = $"Shader \"{shaderName}\"" + lit.Substring(header.Length);

			// Drop HDRP's ray tracing SubShader: the game ships with HDRP ray tracing disabled, the own engine (NFXNative)
			// reads the materials' values itself, and our GetSurfaceAndBuiltinData wrapper only forwards the raster
			// signature.
			int first = text.IndexOf("\n    SubShader", StringComparison.Ordinal);
			int second = first < 0 ? -1 : text.IndexOf("\n    SubShader", first + 1, StringComparison.Ordinal);
			int fallback = text.IndexOf("\n    FallBack", StringComparison.Ordinal);
			if (first < 0 || second < 0 || fallback < second) {
				throw new Exception("Could not find the Lit.shader SubShader blocks");
			}
			text = text.Remove(second, fallback - second);

			int includes = CountOf(text, LitDataInclude);
			if (includes == 0) {
				throw new Exception("LitData.hlsl include not found in Lit.shader");
			}
			text = text.Replace(LitDataInclude, NfxLitDataInclude);
			// the forward passes (transparent surfaces) with the sun's ray traced shadow
			if (CountOf(text, ForwardPassInclude) == 0) {
				throw new Exception("ShaderPassForward.hlsl include not found in Lit.shader");
			}
			text = text.Replace(ForwardPassInclude, NfxForwardPassInclude);

			// Per-material NatureFX values (map material profiles) go into UnityPerMaterial so the
			// SRP Batcher keeps batching: inline LitProperties.hlsl with them added before CBUFFER_END.
			if (CountOf(text, LitPropertiesInclude) != 1) {
				throw new Exception("LitProperties.hlsl include not found in Lit.shader");
			}
			string props = File.ReadAllText(Path.GetFullPath(LitPropertiesPath));
			int cbufferEnd = props.IndexOf("CBUFFER_END", StringComparison.Ordinal);
			if (cbufferEnd < 0 || props.IndexOf("CBUFFER_END", cbufferEnd + 1, StringComparison.Ordinal) >= 0) {
				throw new Exception("Expected exactly one CBUFFER_END in LitProperties.hlsl");
			}
			props = props.Insert(cbufferEnd, NfxMaterialCBuffer);
			text = text.Replace(LitPropertiesInclude, "// LitProperties.hlsl inlined by NatureFX\n" + props);

			const string propertiesBlock = "    Properties\n    {\n";
			text = text.Replace("\r\n", "\n");
			int propsAt = text.IndexOf(propertiesBlock, StringComparison.Ordinal);
			if (propsAt < 0) {
				throw new Exception("Properties block not found in Lit.shader");
			}
			text = text.Insert(propsAt + propertiesBlock.Length, NfxMaterialProperties);

			const string hlslInclude = "HLSLINCLUDE";
			int at = text.IndexOf(hlslInclude, StringComparison.Ordinal);
			if (at < 0) {
				throw new Exception("HLSLINCLUDE block not found in Lit.shader");
			}
			at += hlslInclude.Length;
			string defineBlock = "\n\n    // NatureFX\n";
			foreach (string d in defines) {
				defineBlock += $"    #define {d}\n";
			}
			text = text.Insert(at, defineBlock);

			return "// Generated by Assets/NatureFX/Editor/GenerateNatureFXShaders.cs from HDRP/Lit. Do not edit.\n" + text;
		}

		private static int CountOf(string text, string value) {
			int count = 0;
			for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal)) {
				++count;
			}
			return count;
		}
	}
}

using UnityEngine;

namespace NatureFX.Rebuild {
	// The catch-all of the shader rebuild (docs/shader-rebuild-plan.md, stage 0): any game material the specific
	// translators do not know becomes a NatureFX Lit material, its colour map, tint, normal map, metalness,
	// smoothness, alpha clip and emission found by the usual property names. HDRP/Lit sources already use the
	// target's names and only change shader.
	public static class GenericTranslator {
		// cd/m² an unlit sign or screen glows at (a lit sign or an LCD screen: a few hundred)
		public const float UnlitLuminance = 250.0f;

		public static void Apply(Material m, MaterialSnapshot src) {
			string shaderName = src.Shader != null ? src.Shader.name : "";
			bool litNames = shaderName == "HDRP/Lit" || shaderName.StartsWith("HDRP/LitTessellation");

			string baseMap = src.FirstTexture(AlphaClip.ColorMaps) ?? src.GuessTexture(AlphaClip.ColorWords);
			if (baseMap != null) {
				var t = src.Textures[baseMap];
				m.SetTexture("_BaseColorMap", t.Texture);
				m.SetTextureScale("_BaseColorMap", t.Scale);
				m.SetTextureOffset("_BaseColorMap", t.Offset);
			}

			// CarX Colormask (parked cars, tents, trucks): its mask map is HDRP's layout (r metal, g AO, a smoothness)
			// with the blue channel marking the painted panels; _BaseColor is the paint, not a whole-surface tint
			bool colormask = shaderName.Contains("Colormask");
			if (colormask) {
				var paint = src.TryColor(out var paintColor, "_BaseColor") ? paintColor : Color.white;
				m.SetColor("_BaseColor", Color.white);
				m.SetVector("_NFX_Paint", new Vector4(paint.r, paint.g, paint.b, 1.0f));
			} else if (src.TryColor(out var tint, "_BaseColor", "_Color", "_MainColor", "_TintColor", "_AlbedoColor", "_DiffuseColor")) {
				m.SetColor("_BaseColor", tint);
			} else if (!litNames) {
				m.SetColor("_BaseColor", Color.white);
			}

			string normal = src.FirstTexture("_NormalMap", "_BumpMap", "_Normal", "_NormalTex", "_NormalTexture");
			if (normal != null) {
				m.SetTexture("_NormalMap", src.Textures[normal].Texture);
				if (src.TryFloat(out float normalScale, "_NormalScale", "_BumpScale", "_NormalStrength")) {
					m.SetFloat("_NormalScale", normalScale);
				}
			}

			// HDRP's mask map layout (r metal, g AO, a smoothness) only from HDRP sources and Colormask (checked on its
			// textures): other Shader Graph masks mean something else
			if (colormask) {
				m.SetTexture("_MaskMap", src.Textures.TryGetValue("_MaskMap", out var pack) ? pack.Texture : null);
			} else if (!litNames && m.HasProperty("_MaskMap")) {
				m.SetTexture("_MaskMap", null);
			}
			if (src.TryFloat(out float metallic, "_Metallic", "_Metalness", "_Metal")) {
				m.SetFloat("_Metallic", Mathf.Clamp01(metallic));
			}
			if (src.TryFloat(out float smoothness, "_Smoothness", "_Glossiness", "_Gloss")) {
				m.SetFloat("_Smoothness", Mathf.Clamp01(smoothness));
			} else if (src.TryFloat(out float roughness, "_Roughness")) {
				m.SetFloat("_Smoothness", Mathf.Clamp01(1.0f - roughness));
			} else if (!litNames) {
				m.SetFloat("_Smoothness", 0.3f);
			}

			bool clip = src.HasKeyword("_ALPHATEST_ON")
			            || (src.TryFloat(out float clipOn, "_AlphaCutoffEnable", "_AlphaClip") && clipOn > 0.5f)
			            || (src.RenderQueue >= 2450 && src.RenderQueue < 2500);
			if (clip) {
				m.SetFloat("_AlphaCutoffEnable", 1.0f);
				if (src.TryFloat(out float cutoff, "_AlphaCutoff", "_Cutoff", "_AlphaClipThreshold")) {
					m.SetFloat("_AlphaCutoff", cutoff);
				}
			}
			if ((src.TryFloat(out float twoSided, "_DoubleSidedEnable") && twoSided > 0.5f)
			    || (src.TryFloat(out float cull, "_CullMode", "_Cull") && cull < 0.5f)) {
				m.SetFloat("_DoubleSidedEnable", 1.0f);
			}

			// unlit (signs, screens): their colour is light. In the track's units it was a dim glow that vanished in real
			// daylight (Units: the sun raised to real sunshine): it glows at a lit sign's or a screen's luminance instead,
			// over a dark surface (what the rays see of it), and the own engine's emitters take it as a light
			if (shaderName.IndexOf("unlit", System.StringComparison.OrdinalIgnoreCase) >= 0) {
				var glow = src.TryColor(out var unlitColor, "_UnlitColor", "_BaseColor", "_Color", "_MainColor", "_TintColor") ? unlitColor : Color.white;
				m.SetColor("_EmissiveColor", new Color(glow.r, glow.g, glow.b) * UnlitLuminance);
				m.SetTexture("_EmissiveColorMap", baseMap != null ? src.Textures[baseMap].Texture : null);
				if (baseMap != null) {
					m.SetTextureScale("_EmissiveColorMap", src.Textures[baseMap].Scale);
					m.SetTextureOffset("_EmissiveColorMap", src.Textures[baseMap].Offset);
				}
				m.SetColor("_BaseColor", new Color(glow.r * 0.04f, glow.g * 0.04f, glow.b * 0.04f, glow.a));
				m.SetFloat("_Smoothness", 0.6f);
				m.SetFloat("_Metallic", 0.0f);
				return;
			}

			if (!litNames && src.TryColor(out var emission, "_EmissiveColor", "_EmissionColor") && emission.maxColorComponent > 0.0f) {
				float intensity = src.TryFloat(out float e, "_EmissionIntensity", "_EmissiveIntensity") ? e : 1.0f;
				m.SetColor("_EmissiveColor", emission * intensity);
				string emissionMap = src.FirstTexture("_EmissiveColorMap", "_EmissionMap", "_EmissiveMap");
				if (emissionMap != null) {
					m.SetTexture("_EmissiveColorMap", src.Textures[emissionMap].Texture);
				}
			}
		}
	}
}

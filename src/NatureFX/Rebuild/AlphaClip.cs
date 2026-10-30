using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Rebuild {
	// Whether a material of any shader clips by alpha (leaves, fences, nets), for materials NatureFX has no rule for
	// (mod maps' own shaders): HDRP's toggle, keywords and properties the common shaders use, the alpha test render
	// queue, a colour texture that has an alpha channel at all and, for shaders that say none of this (their clip is
	// built into the shader), what that alpha channel holds. Used by the shader rebuild (keep the clip, or leave the
	// material alone when no bundled variant clips) and by the ray tracing scene (alpha tested instances).
	public static class AlphaClip {
		private static readonly string[] Toggles = { "_AlphaCutoffEnable", "_AlphaClip", "_UseAlphaClip", "_AlphaClipping", "_Cutout", "_AlphaTest" };
		private static readonly string[] Cutoffs = {
			"_AlphaCutoff", "_Cutoff", "_AlphaClipThreshold", "_AlphaClippingThreshold", "_AlphaThreshold", "_AlphaCutoffValue", "_Clip"
		};
		// the colour texture, as GenericTranslator finds it: these names first, then a guess by the property name
		public static readonly string[] ColorMaps = {
			"_BaseColorMap", "_BaseMap", "_MainTex", "_Albedo", "_AlbedoMap", "_AlbedoTex", "_DiffuseMap", "_Diffuse", "_MainTexture", "_ColorMap", "_Texture"
		};
		public static readonly string[] ColorWords = { "albedo", "diffuse", "base", "main", "color", "colour" };

		// what the alpha channel of a colour texture holds, per texture (sampled once)
		private static readonly Dictionary<int, bool> cutoutAlpha_ = new();
		private const int SampleSize = 64;

		public static bool Detect(Material m, out float cutoff) {
			return Detect(m, out cutoff, out _);
		}

		// reason: what decided it (for the "What is this?" tool)
		public static bool Detect(Material m, out float cutoff, out string reason) {
			cutoff = 0.5f;
			reason = "no material";
			if (m == null || m.shader == null) {
				return false;
			}
			// blended see-through surfaces (glass, decals, smoke) do not clip
			if (m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.Transparent ||
			    (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f)) {
				reason = "transparent";
				return false;
			}
			bool keyword = false;
			foreach (string k in m.shaderKeywords) {
				string u = k.ToUpperInvariant();
				if (u.Contains("ALPHATEST") || u.Contains("ALPHA_TEST") || u.Contains("ALPHACLIP") || u.Contains("ALPHA_CLIP") || u.Contains("CUTOUT")) {
					keyword = true;
					break;
				}
			}
			bool toggle = false;
			foreach (string name in Toggles) {
				if (m.HasProperty(name) && m.GetFloat(name) > 0.5f) {
					toggle = true;
					break;
				}
			}
			string cutoffName = null;
			foreach (string name in Cutoffs) {
				if (m.HasProperty(name)) {
					cutoffName = name;
					break;
				}
			}
			if (cutoffName != null) {
				cutoff = Mathf.Clamp(m.GetFloat(cutoffName), 0.01f, 0.99f);
			}
			// the alpha test queue range (AlphaTest 2450 up to the transparent queues)
			bool queue = m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.AlphaTest;
			var texture = ColorTexture(m);
			bool? hasAlpha = texture is Texture2D t2 ? FormatHasAlpha(t2.format) : null;
			if (keyword || toggle || queue) {
				// nothing to clip by without an alpha channel in the colour texture (a material may keep the toggle on)
				reason = $"{(keyword ? "keyword" : toggle ? "toggle" : "queue")}, colour map {(texture != null ? texture.name : "none")} alpha {hasAlpha?.ToString() ?? "?"}";
				return hasAlpha != false;
			}
			// HDRP/Lit and HDRP Shader Graphs state their alpha clipping (_AlphaCutoffEnable off: opaque); so does a
			// shader whose texture alpha is its smoothness
			if (m.HasProperty("_AlphaCutoffEnable") || (m.HasProperty("_SmoothnessTextureChannel") && m.GetFloat("_SmoothnessTextureChannel") > 0.5f) ||
			    m.IsKeywordEnabled("_SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A")) {
				reason = "says it does not clip";
				return false;
			}
			if (texture == null || hasAlpha == false) {
				reason = texture == null ? "no colour map" : $"colour map {texture.name} has no alpha";
				return false;
			}
			// a shader that clips without saying so: the colour map's alpha is cut out (mostly 0 or 1, with holes)
			bool cutout = CutoutAlpha(texture);
			reason = $"colour map {texture.name} alpha {(cutout ? "cut out" : "solid or smooth")}";
			return cutout;
		}

		public static Texture ColorTexture(Material m) {
			string name = ColorTextureName(m);
			return name != null ? m.GetTexture(name) : null;
		}

		// the property holding the material's colour texture (an assigned one), or null
		public static string ColorTextureName(Material m) {
			foreach (string name in ColorMaps) {
				if (m.HasProperty(name) && m.GetTexture(name) != null) {
					return name;
				}
			}
			var shader = m.shader;
			for (int i = 0; i < shader.GetPropertyCount(); ++i) {
				if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) {
					continue;
				}
				string name = shader.GetPropertyName(i);
				string lower = name.ToLowerInvariant();
				if (lower.Contains("emiss") || lower.Contains("mask") || lower.Contains("normal") || lower.Contains("bump")) {
					continue;
				}
				foreach (string word in ColorWords) {
					if (lower.Contains(word) && m.GetTexture(name) != null) {
						return name;
					}
				}
			}
			return null;
		}

		private static bool FormatHasAlpha(TextureFormat format) {
			switch (format) {
				case TextureFormat.DXT1:
				case TextureFormat.DXT1Crunched:
				case TextureFormat.RGB24:
				case TextureFormat.RGB565:
				case TextureFormat.BC4:
				case TextureFormat.BC5:
				case TextureFormat.BC6H:
				case TextureFormat.R8:
				case TextureFormat.R16:
				case TextureFormat.RG16:
				case TextureFormat.ETC_RGB4:
				case TextureFormat.ETC2_RGB:
					return false;
				default:
					return true;
			}
		}

		// The texture drawn small on the GPU and read back (game textures are not readable): cut out when some of it is
		// clear, a good part solid, and little in between (smoothness or height in alpha varies smoothly instead)
		private static bool CutoutAlpha(Texture texture) {
			int id = texture.GetInstanceID();
			if (cutoutAlpha_.TryGetValue(id, out bool known)) {
				return known;
			}
			bool cutout = false;
			var rt = RenderTexture.GetTemporary(SampleSize, SampleSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			var previous = RenderTexture.active;
			var read = new Texture2D(SampleSize, SampleSize, TextureFormat.RGBA32, false, true);
			try {
				Graphics.Blit(texture, rt);
				RenderTexture.active = rt;
				read.ReadPixels(new Rect(0, 0, SampleSize, SampleSize), 0, 0, false);
				int clear = 0, solid = 0;
				var pixels = read.GetPixels32();
				foreach (var p in pixels) {
					if (p.a < 64) {
						++clear;
					} else if (p.a > 191) {
						++solid;
					}
				}
				float n = pixels.Length;
				cutout = clear >= 0.03f * n && solid >= 0.1f * n && clear + solid >= 0.8f * n;
			} catch (System.Exception e) {
				KSL.API.Kino.Log.Info($"[NatureFX] Alpha clip: could not read {texture.name}: {e.Message}");
			} finally {
				RenderTexture.active = previous;
				RenderTexture.ReleaseTemporary(rt);
				Object.Destroy(read);
			}
			cutoutAlpha_[id] = cutout;
			return cutout;
		}
	}
}

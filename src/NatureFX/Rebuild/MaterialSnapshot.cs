using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rebuild {
	// Every property value of a material as its game shader defines them, taken before the shader is swapped:
	// translators read the game's values from it (the NFX shader no longer knows those names), and switching
	// NatureFX off writes it back, so translated values never leak into the game's look.
	public class MaterialSnapshot {
		public readonly Shader Shader;
		public readonly string[] Keywords;
		public readonly int RenderQueue;
		public readonly Dictionary<string, (Texture Texture, Vector2 Scale, Vector2 Offset)> Textures = new();
		public readonly Dictionary<string, Color> Colors = new();
		public readonly Dictionary<string, Vector4> Vectors = new();
		public readonly Dictionary<string, float> Floats = new();

		public MaterialSnapshot(Material m) {
			Shader = m.shader;
			Keywords = m.shaderKeywords;
			RenderQueue = m.renderQueue;
			if (Shader == null) {
				return;
			}
			for (int i = 0; i < Shader.GetPropertyCount(); ++i) {
				string name = Shader.GetPropertyName(i);
				switch (Shader.GetPropertyType(i)) {
					case ShaderPropertyType.Texture:
						Textures[name] = (m.GetTexture(name), m.GetTextureScale(name), m.GetTextureOffset(name));
						break;
					case ShaderPropertyType.Color:
						Colors[name] = m.GetColor(name);
						break;
					case ShaderPropertyType.Vector:
						Vectors[name] = m.GetVector(name);
						break;
					case ShaderPropertyType.Float:
					case ShaderPropertyType.Range:
						Floats[name] = m.GetFloat(name);
						break;
					case ShaderPropertyType.Int:
						Floats[name] = m.GetInteger(name);
						break;
				}
			}
		}

		public bool HasKeyword(string keyword) {
			return System.Array.IndexOf(Keywords, keyword) >= 0;
		}

		// First of the names this material has an assigned texture for
		public string FirstTexture(params string[] names) {
			foreach (string name in names) {
				if (Textures.TryGetValue(name, out var t) && t.Texture != null) {
					return name;
				}
			}
			return null;
		}

		// First texture whose property name contains one of the words (emission and masks left out)
		public string GuessTexture(params string[] words) {
			foreach (var kv in Textures) {
				if (kv.Value.Texture == null) {
					continue;
				}
				string lower = kv.Key.ToLowerInvariant();
				if (lower.Contains("emiss") || lower.Contains("mask") || lower.Contains("normal") || lower.Contains("bump")) {
					continue;
				}
				foreach (string word in words) {
					if (lower.Contains(word)) {
						return kv.Key;
					}
				}
			}
			return null;
		}

		public bool TryColor(out Color color, params string[] names) {
			foreach (string name in names) {
				if (Colors.TryGetValue(name, out color)) {
					return true;
				}
				if (Vectors.TryGetValue(name, out var v)) {
					color = v;
					return true;
				}
			}
			color = Color.white;
			return false;
		}

		public bool TryFloat(out float value, params string[] names) {
			foreach (string name in names) {
				if (Floats.TryGetValue(name, out value)) {
					return true;
				}
			}
			value = 0.0f;
			return false;
		}

		// Puts the game's shader, keywords and values back
		public void RestoreTo(Material m) {
			m.shader = Shader;
			m.shaderKeywords = Keywords;
			m.renderQueue = RenderQueue;
			foreach (var kv in Textures) {
				if (m.HasProperty(kv.Key)) {
					m.SetTexture(kv.Key, kv.Value.Texture);
					m.SetTextureScale(kv.Key, kv.Value.Scale);
					m.SetTextureOffset(kv.Key, kv.Value.Offset);
				}
			}
			foreach (var kv in Colors) {
				if (m.HasProperty(kv.Key)) {
					m.SetColor(kv.Key, kv.Value);
				}
			}
			foreach (var kv in Vectors) {
				if (m.HasProperty(kv.Key)) {
					m.SetVector(kv.Key, kv.Value);
				}
			}
			foreach (var kv in Floats) {
				if (m.HasProperty(kv.Key)) {
					m.SetFloat(kv.Key, kv.Value);
				}
			}
		}
	}
}

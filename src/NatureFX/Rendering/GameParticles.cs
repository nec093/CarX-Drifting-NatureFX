using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;

namespace NatureFX.Rendering {
	// Roadmap E and the shader rebuild's stage 3: the game's and the track's own particles drawn with NatureFX/Particle
	// instead of their shaders. Alpha blended ones (dust, spray, smoke, leaves of their own; mode 6, textured) are lit by
	// the own engine's sun or moon, sky and lamps (ParticleLighting) and fogged by its 3D fog, as NatureFX's particles
	// are. Additive ones (exhaust flames, sparks, glows; mode 7) are their own light: they glow at a real luminance
	// (cd/m², as the own engine's lamps: the game's own were in the track's dim units and vanished in real daylight)
	// and only dim in the fog. Each replaced material is made once per source material (texture, colour, sheet
	// animation via the particle's own uvs) and the originals come back when it is turned off.
	public class GameParticles {
		private readonly Dictionary<ParticleSystemRenderer, Material> original_ = new();
		private readonly Dictionary<Material, Material> made_ = new();
		private ShaderBundle bundle_;
		private float nextFind_;
		private bool on_;

		public int Count => original_.Count;
		public int Skipped { get; private set; }

		public void Create(ShaderBundle bundle) {
			bundle_ = bundle;
		}

		// a reloaded bundle: new materials from its shader
		public void Reload(ShaderBundle bundle) {
			Restore();
			bundle_ = bundle;
		}

		public void Update(bool on) {
			if (!on) {
				if (on_) {
					Restore();
				}
				return;
			}
			on_ = true;
			if (Time.unscaledTime < nextFind_) {
				return;
			}
			nextFind_ = Time.unscaledTime + 3.0f;
			int skipped = 0;
			foreach (var r in Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None)) {
				if (r == null || original_.ContainsKey(r) || r.name.StartsWith("NatureFX")) {
					continue;
				}
				var m = r.sharedMaterial;
				if (m == null || m.shader == null || m.shader.name.StartsWith("NatureFX")) {
					continue;
				}
				// mesh particles keep theirs (debris, pieces): the textured modes draw camera facing sprites
				bool glow = Additive(m);
				if (r.renderMode == ParticleSystemRenderMode.Mesh || !glow && !AlphaBlended(m)) {
					++skipped;
					continue;
				}
				if (!made_.TryGetValue(m, out var replaced)) {
					replaced = Make(m, glow);
					made_[m] = replaced;
				}
				if (replaced == null) {
					++skipped;
					continue;
				}
				original_[r] = m;
				r.sharedMaterial = replaced;
			}
			Skipped = skipped;
		}

		public void Restore() {
			foreach (var kv in original_) {
				if (kv.Key != null) {
					kv.Key.sharedMaterial = kv.Value;
				}
			}
			original_.Clear();
			foreach (var m in made_.Values) {
				if (m != null) {
					Object.Destroy(m);
				}
			}
			made_.Clear();
			on_ = false;
		}

		// alpha blended: not additive (HDRP's _BlendMode 1, a One / One blend, or a shader or material that says so)
		private static bool AlphaBlended(Material m) {
			string name = (m.shader.name + " " + m.name).ToLowerInvariant();
			foreach (var word in new[] { "additive", "add", "glow", "fire", "flame", "spark", "flare", "light", "premultipl", "emiss" }) {
				if (name.Contains(word)) {
					return false;
				}
			}
			if (m.HasProperty("_BlendMode") && m.GetFloat("_BlendMode") > 0.5f) {
				return false;
			}
			if (m.HasProperty("_DstBlend") && Mathf.Approximately(m.GetFloat("_DstBlend"), (float)UnityEngine.Rendering.BlendMode.One)) {
				return false;
			}
			return m.renderQueue >= 2450 || name.Contains("transparent") || name.Contains("alpha") || name.Contains("blend") || name.Contains("particle");
		}

		// additive: HDRP's _BlendMode 1, a One / One blend, or a shader or material named so (not premultiplied ones,
		// which also darken what is behind them: they keep their shaders)
		private static bool Additive(Material m) {
			string name = (m.shader.name + " " + m.name).ToLowerInvariant();
			if (name.Contains("premultipl")) {
				return false;
			}
			if (m.HasProperty("_BlendMode") && Mathf.Approximately(m.GetFloat("_BlendMode"), 1.0f)) {
				return true;
			}
			if (m.HasProperty("_DstBlend") && Mathf.Approximately(m.GetFloat("_DstBlend"), (float)UnityEngine.Rendering.BlendMode.One)) {
				return true;
			}
			foreach (var word in new[] { "additive", "glow", "fire", "flame", "spark" }) {
				if (name.Contains(word)) {
					return true;
				}
			}
			return false;
		}

		// how bright a game glow shines (cd/m²): sparks are white hot, flames a few thousand, the rest a soft glow
		private static float GlowLuminance(Material m) {
			string name = (m.shader.name + " " + m.name).ToLowerInvariant();
			if (name.Contains("spark")) {
				return 20000.0f;
			}
			if (name.Contains("fire") || name.Contains("flame") || name.Contains("exhaust")) {
				return 5000.0f;
			}
			return 1500.0f;
		}

		private Material Make(Material source, bool glow) {
			var tex = Texture(source);
			if (tex == null) {
				return null;
			}
			var m = ParticleAtlas.Material(bundle_, $"NatureFX.Game.{source.name}", glow ? ParticleAtlas.ModeTexturedGlow : ParticleAtlas.ModeTextured,
				new Vector2(0.0f, 0.0f), Tint(source), glow ? GlowLuminance(source) : 0.0f);
			if (m == null) {
				return null;
			}
			m.SetTexture("_MainTex", tex);
			// no alpha in the texture (drawn additive or by brightness in its own shader): its brightness is the coverage,
			// else the whole quad drew as a square
			bool alpha = tex is Texture2D t2 && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.HasAlphaChannel(t2.graphicsFormat);
			m.SetVector("_NFX_PTiles", new Vector4(0.0f, 0.0f, alpha ? 0.0f : 1.0f, 0.0f));
			m.SetTextureScale("_MainTex", source.mainTextureScale);
			m.SetTextureOffset("_MainTex", source.mainTextureOffset);
			m.renderQueue = source.renderQueue;
			m.hideFlags = HideFlags.DontSave;
			return m;
		}

		private static Texture Texture(Material m) {
			foreach (var p in new[] { "_BaseColorMap", "_UnlitColorMap", "_MainTex", "_BaseMap", "_ColorMap" }) {
				if (m.HasProperty(p) && m.GetTexture(p) != null) {
					return m.GetTexture(p);
				}
			}
			return null;
		}

		private static Color Tint(Material m) {
			foreach (var p in new[] { "_BaseColor", "_UnlitColor", "_TintColor", "_Color" }) {
				if (m.HasProperty(p)) {
					var c = m.GetColor(p);
					// legacy particle tints are doubled (_TintColor 0.5 is neutral)
					return p == "_TintColor" ? new Color(c.r * 2.0f, c.g * 2.0f, c.b * 2.0f, Mathf.Min(1.0f, c.a * 2.0f)) : c;
				}
			}
			return Color.white;
		}
	}
}

using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace NatureFX.Tools {
	// "What is this?": after the button in the window, the next click on the scene lists the renderers whose bounds
	// the click's ray passes through, nearest first, with their object path, materials, shaders and render queues
	// (see-through ones too: they are not in the ray tracing scene). Shown in the window and logged.
	public class Identify {
		private const int MaxListed = 12;

		// the shader rebuild's record of a swapped material (its game shader), for the details below
		public System.Func<Material, NatureFX.Rebuild.MaterialSnapshot> OriginalOf;

		public bool Armed { get; private set; }
		public string Result { get; private set; } = "";
		private int armedFrame_;

		public void Arm() {
			Armed = true;
			armedFrame_ = Time.frameCount;
			Result = "Click the thing to identify in the scene…";
		}

		// From OnGUI: IMGUI sees every mouse click (the game's input goes through Rewired)
		public void OnGUI() {
			var e = Event.current;
			// not the click that pressed the button
			if (!Armed || Time.frameCount <= armedFrame_ + 1 || e.type != EventType.MouseDown || e.button != 0) {
				return;
			}
			Armed = false;
			var screen = new Vector2(e.mousePosition.x, Screen.height - e.mousePosition.y);
			var camera = Camera.main;
			if (camera == null) {
				Result = "No camera.";
				return;
			}
			var ray = camera.ScreenPointToRay(screen);
			var hits = new List<(float Distance, Renderer Renderer)>();
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (!r.enabled || !r.isVisible || r is ParticleSystemRenderer) {
					continue;
				}
				// bounds around the camera (a whole map's trees in one mesh) say nothing about the click: the traced
				// scene below does
				if (r.bounds.IntersectRay(ray, out float distance) && distance > 0.0f) {
					hits.Add((distance, r));
				}
			}
			hits.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			var text = new StringBuilder();
			text.AppendLine($"Under the click ({hits.Count} renderers on the ray, nearest first):");
			for (int i = 0; i < hits.Count && i < MaxListed; ++i) {
				var r = hits[i].Renderer;
				// motion vectors: whether HDRP knows how this renderer moved (NRD follows a pixel through time by them)
				text.Append($"{hits[i].Distance:0.0} m  {Path(r.transform)}  [{r.GetType().Name}, shadows {r.shadowCastingMode}, motion vectors {r.motionVectorGenerationMode}]");
				foreach (var m in r.sharedMaterials) {
					if (m != null) {
						text.Append($"  '{m.name}' ({(m.shader != null ? m.shader.name : "-")}, queue {m.renderQueue}{(m.IsKeywordEnabled("_ALPHATEST_ON") ? ", alpha test" : "")})");
					}
				}
				text.AppendLine();
				// what the alpha clip detection makes of each (the game's material where the rebuild swapped it)
				foreach (var m in r.sharedMaterials) {
					if (m == null) {
						continue;
					}
					var original = OriginalOf?.Invoke(m);
					bool clips = NatureFX.Rebuild.AlphaClip.Detect(m, out float cutoff, out string reason);
					text.AppendLine($"      '{m.name}' alpha clip {clips} ({reason}, cutoff {cutoff:0.##}){(original != null ? $", swapped from {original.Shader?.name}" : "")}, " +
					                $"motion vector pass {m.GetShaderPassEnabled("MotionVectors")}");
				}
				// alpha tested or foliage: how it is drawn (the state a card's clip depends on) and what it was
				foreach (var m in r.sharedMaterials) {
					if (m == null || !(m.IsKeywordEnabled("_ALPHATEST_ON") || (m.shader != null && m.shader.name.Contains("Foliage")))) {
						continue;
					}
					var tex = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : null;
					string Value(string name) => m.HasProperty(name) ? m.GetFloat(name).ToString("0.###") : "-";
					text.AppendLine($"      '{m.name}': keywords [{string.Join(" ", m.shaderKeywords)}], cutoff {Value("_AlphaCutoff")}, cutoffEnable {Value("_AlphaCutoffEnable")}, " +
					                $"ZTestGBuffer {Value("_ZTestGBuffer")}, ZTestDepthEqual {Value("_ZTestDepthEqualForOpaque")}, alphaToMask {Value("_AlphaToMask")}, " +
					                $"base colour a {(m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").a.ToString("0.###") : "-")}, " +
					                $"map {(tex != null ? tex.name + " " + (tex is Texture2D t2 ? t2.format.ToString() : "") : "none")}, " +
					                $"passes depth {m.GetShaderPassEnabled("DepthOnly")} mv {m.GetShaderPassEnabled("MotionVectors")}");
					var original = OriginalOf?.Invoke(m);
					if (original != null) {
						text.AppendLine($"      was {(original.Shader != null ? original.Shader.name : "-")} [{string.Join(" ", original.Keywords)}] queue {original.RenderQueue}");
					}
				}
			}
			// what the ray tracing scene has under the click (exact triangles, unlike the bounds above)
			var picks = NatureFX.Rendering.NativeEngine.Pick(ray, 1000.0f, 6);
			text.AppendLine($"Traced scene under the click ({picks.Count} hits, nearest first):");
			foreach (var (distance, instance) in picks) {
				var m = instance.Material;
				var tex = m != null && m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : null;
				string Value(string name) => m != null && m.HasProperty(name) ? m.GetFloat(name).ToString("0.###") : "-";
				text.AppendLine($"{distance:0.0} m  {instance.Kind} {(instance.Mesh != null ? instance.Mesh.name : "-")}#{instance.SubMesh}  '{(m != null ? m.name : "-")}' " +
				                $"({(m != null && m.shader != null ? m.shader.name : "-")}), alpha test {instance.AlphaTest}, proxy alpha test {Value("_NFX_AlphaTest")}, " +
				                $"cutoff {Value("_AlphaCutoff")}, colour a {(m != null && m.HasProperty("_BaseColor") ? m.GetColor("_BaseColor").a.ToString("0.###") : "-")}, " +
				                $"map {(tex != null ? tex.name + " " + (tex is Texture2D t2 ? t2.format.ToString() : tex.GetType().Name) : "none")}");
			}
			Result = text.ToString().TrimEnd();
			KSL.API.Kino.Log.Info($"[NatureFX] Identify: {Result}");
		}

		private static string Path(Transform t) {
			string path = t.name;
			for (int i = 0; i < 3 && t.parent != null; ++i) {
				t = t.parent;
				path = t.name + "/" + path;
			}
			return path;
		}
	}
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace NatureFX.Tools {
	// Shader rebuild, stage 0: every material of the loaded scenes with its game
	// shader, all property values, keywords, where it is used and what the rebuild makes of it (NFX shader or the
	// reason it stays). One file per scene in <kino config>/NatureFX/inventory, rewritten on each load, so driving
	// through the maps collects the data the translators of stages 1 to 4 are written from.
	public static class MaterialInventory {
		private const int MaxObjects = 6;

		private class Entry {
			public Material Material;
			public Renderer FirstRenderer;
			public int Renderers;
			public readonly HashSet<string> Objects = new();
		}

		public static string Folder => Path.Combine(Kino.Paths.Config, "NatureFX", "inventory");

		// Returns the file written, or null
		public static string Write(MaterialReplacer replacer) {
			try {
				var entries = new Dictionary<Material, Entry>();
				foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
					foreach (var m in r.sharedMaterials) {
						if (m == null) {
							continue;
						}
						if (!entries.TryGetValue(m, out var e)) {
							e = new Entry { Material = m, FirstRenderer = r };
							entries[m] = e;
						}
						++e.Renderers;
						if (e.Objects.Count < MaxObjects) {
							e.Objects.Add(MaterialReplacer.ObjectPath(r.transform));
						}
					}
				}

				var sb = new StringBuilder();
				string scenes = string.Join(", ", Enumerable.Range(0, SceneManager.sceneCount).Select(i => SceneManager.GetSceneAt(i).name));
				sb.AppendLine($"NatureFX material inventory - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
				sb.AppendLine($"Scenes: {scenes}");
				sb.AppendLine($"Materials: {entries.Count}");
				sb.AppendLine();

				// summary: game shader -> what becomes of its materials
				var rows = new List<(string Shader, string Fate, int Materials, int Renderers)>();
				var details = new List<(string Shader, string Fate, Entry Entry)>();
				foreach (var e in entries.Values) {
					var snapshot = replacer.OriginalOf(e.Material);
					string shader = snapshot?.Shader != null ? snapshot.Shader.name : e.Material.shader != null ? e.Material.shader.name : "-";
					string fate;
					if (snapshot != null) {
						fate = $"-> {e.Material.shader.name}";
					} else {
						string path = MaterialReplacer.ObjectPath(e.FirstRenderer.transform);
						string target = replacer.Classify(e.Material, e.FirstRenderer, path, out string reason);
						fate = target != null ? $"-> {target}" : reason ?? "already NatureFX";
					}
					details.Add((shader, fate, e));
				}
				foreach (var g in details.GroupBy(d => (d.Shader, d.Fate))) {
					rows.Add((g.Key.Shader, g.Key.Fate, g.Count(), g.Sum(d => d.Entry.Renderers)));
				}
				sb.AppendLine("=== Summary (game shader | what it becomes | materials | renderers)");
				foreach (var row in rows.OrderByDescending(r => r.Renderers)) {
					sb.AppendLine($"{row.Shader} | {row.Fate} | {row.Materials} | {row.Renderers}");
				}
				sb.AppendLine();

				foreach (var group in details.GroupBy(d => d.Shader).OrderByDescending(g => g.Sum(d => d.Entry.Renderers))) {
					sb.AppendLine($"=== {group.Key}");
					foreach (var (_, fate, e) in group.OrderByDescending(d => d.Entry.Renderers)) {
						AppendMaterial(sb, e, fate, replacer.OriginalOf(e.Material));
					}
					sb.AppendLine();
				}

				Directory.CreateDirectory(Folder);
				string sceneForTextures = SceneManager.sceneCount > 0 ? SceneManager.GetSceneAt(SceneManager.sceneCount - 1).name : "scene";
				TextureSampler.Export(details.Select(d => (d.Shader, d.Entry.Material, replacer.OriginalOf(d.Entry.Material))),
					Path.Combine(Folder, "textures", string.Concat(sceneForTextures.Split(Path.GetInvalidFileNameChars()))));
				string sceneName = SceneManager.sceneCount > 0 ? SceneManager.GetSceneAt(SceneManager.sceneCount - 1).name : "scene";
				foreach (char c in Path.GetInvalidFileNameChars()) {
					sceneName = sceneName.Replace(c, '_');
				}
				string file = Path.Combine(Folder, $"{sceneName}.txt");
				File.WriteAllText(file, sb.ToString());
				Kino.Log.Info($"[NatureFX] Material inventory: {entries.Count} materials written to {file}");
				return file;
			} catch (Exception ex) {
				Kino.Log.Error($"[NatureFX] Material inventory failed: {ex.Message}");
				return null;
			}
		}

		// One material: name, use, fate, keywords and every property of its game shader with its value
		private static void AppendMaterial(StringBuilder sb, Entry e, string fate, Rebuild.MaterialSnapshot original) {
			var m = e.Material;
			var shader = original?.Shader ?? m.shader;
			string[] keywords = original?.Keywords ?? m.shaderKeywords;
			int queue = original?.RenderQueue ?? m.renderQueue;
			sb.AppendLine($"- {m.name}  ({e.Renderers} renderers, queue {queue}) {fate}");
			sb.AppendLine($"    objects: {string.Join(", ", e.Objects)}");
			if (keywords.Length > 0) {
				sb.AppendLine($"    keywords: {string.Join(" ", keywords)}");
			}
			if (shader == null) {
				return;
			}
			var values = new List<string>();
			for (int i = 0; i < shader.GetPropertyCount(); ++i) {
				string name = shader.GetPropertyName(i);
				if (IsHdrpPlumbing(name)) {
					continue;
				}
				values.Add($"{name}={Value(m, original, name, shader.GetPropertyType(i))}");
			}
			sb.AppendLine($"    {string.Join(", ", values)}");
		}

		// HDRP's render state properties (stencil, blend, z test ...): the same on every material, noise here
		private static bool IsHdrpPlumbing(string name) {
			return name.StartsWith("_Stencil") || name.StartsWith("_ZTest") || name.StartsWith("_ZWrite") || name.EndsWith("Blend")
			       || name.StartsWith("_Transparent") || name.StartsWith("_CullMode") || name.StartsWith("_RenderQueue")
			       || name.StartsWith("_DiffusionProfile") || name == "_AddPrecomputedVelocity" || name.EndsWith("DepthOffsetEnable")
			       || name == "_RayTracing" || name == "_ExcludeFromTUAndAA" || name.StartsWith("_Receives") || name == "_PerPixelSorting";
		}

		private static string Value(Material m, Rebuild.MaterialSnapshot original, string name, ShaderPropertyType type) {
			var inv = CultureInfo.InvariantCulture;
			switch (type) {
				case ShaderPropertyType.Texture: {
					Texture t;
					Vector2 scale;
					if (original != null && original.Textures.TryGetValue(name, out var snap)) {
						t = snap.Texture;
						scale = snap.Scale;
					} else {
						t = m.GetTexture(name);
						scale = m.GetTextureScale(name);
					}
					if (t == null) {
						return "none";
					}
					string tiling = scale != Vector2.one ? string.Format(inv, " x{0:0.##},{1:0.##}", scale.x, scale.y) : "";
					return $"{t.name}({t.width}x{t.height}{tiling})";
				}
				case ShaderPropertyType.Color: {
					var c = original != null && original.Colors.TryGetValue(name, out var oc) ? oc : m.GetColor(name);
					return string.Format(inv, "({0:0.###},{1:0.###},{2:0.###},{3:0.###})", c.r, c.g, c.b, c.a);
				}
				case ShaderPropertyType.Vector: {
					var v = original != null && original.Vectors.TryGetValue(name, out var ov) ? ov : m.GetVector(name);
					return string.Format(inv, "({0:0.###},{1:0.###},{2:0.###},{3:0.###})", v.x, v.y, v.z, v.w);
				}
				default: {
					float f = original != null && original.Floats.TryGetValue(name, out var of) ? of : m.GetFloat(name);
					return f.ToString("0.###", inv);
				}
			}
		}
	}
}

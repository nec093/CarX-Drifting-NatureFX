using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace NatureFX.Tools {
	// Writes every shader / material / volume in the loaded scenes to a text file,
	// so replacement rules can be written against the game's real shader names.
	public static class SceneShaderDumper {
		private const int MaxObjectsPerMaterial = 12;

		private const string VariantsFileName = "NatureFX_variants.txt";

		private class MaterialInfo {
			public int Renderers;
			public string Target;
			public readonly HashSet<string> Objects = new();
		}

		private class ShaderInfo {
			public Shader Shader;
			public int Renderers;
			public readonly Dictionary<Material, MaterialInfo> Materials = new();
		}

		// Also merges the keyword sets of materials the rules would switch into NatureFX_variants.txt,
		// which the Unity bundle build uses to keep those shader variants.
		public static string Dump(Nature.MaterialReplacer replacer) {
			var byShader = new Dictionary<string, ShaderInfo>();

			foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string objectPath = Nature.MaterialReplacer.ObjectPath(r.transform);
				foreach (var m in r.sharedMaterials) {
					if (m == null || m.shader == null) {
						continue;
					}

					if (!byShader.TryGetValue(m.shader.name, out var info)) {
						info = new ShaderInfo { Shader = m.shader };
						byShader[m.shader.name] = info;
					}
					if (!info.Materials.TryGetValue(m, out var mat)) {
						mat = new MaterialInfo();
						info.Materials[m] = mat;
					}

					++info.Renderers;
					++mat.Renderers;
					mat.Target ??= m.shader.name.StartsWith("NatureFX/") ? m.shader.name : replacer.ResolveTarget(m, objectPath);
					if (mat.Objects.Count < MaxObjectsPerMaterial) {
						// same path format the replacement rules match against
						mat.Objects.Add(objectPath);
					}
				}
			}

			var sb = new StringBuilder();
			sb.AppendLine($"NatureFX scene dump - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
			sb.AppendLine(Rendering.Capabilities.Describe());
			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				sb.AppendLine($"Scene: {SceneManager.GetSceneAt(i).name}");
			}
			sb.AppendLine();

			foreach (var info in byShader.Values.OrderByDescending(x => x.Renderers)) {
				var s = info.Shader;
				sb.AppendLine($"=== {s.name}  (renderers: {info.Renderers}, materials: {info.Materials.Count})");

				int count = s.GetPropertyCount();
				var props = new List<string>(count);
				for (int p = 0; p < count; ++p) {
					props.Add($"{s.GetPropertyName(p)}:{s.GetPropertyType(p)}");
				}
				sb.AppendLine($"  properties: {string.Join(", ", props)}");

				foreach (var kv in info.Materials.OrderByDescending(x => x.Value.Renderers)) {
					string target = kv.Value.Target != null ? $"  -> {kv.Value.Target}" : "";
					sb.AppendLine($"  - {kv.Key.name}  (renderers: {kv.Value.Renderers}){target}");
					sb.AppendLine($"      keywords: {string.Join(" ", kv.Key.shaderKeywords.OrderBy(k => k))}");
					sb.AppendLine($"      {string.Join(", ", kv.Value.Objects)}");
				}
				sb.AppendLine();
			}

			sb.AppendLine("=== Volumes");
			foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				var profile = v.sharedProfile;
				string comps = profile != null ? string.Join(", ", profile.components.Select(c => c.GetType().Name)) : "-";
				sb.AppendLine($"  {v.gameObject.name}  global:{v.isGlobal} priority:{v.priority} weight:{v.weight}  [{comps}]");
			}

			WriteVariants(byShader.Values.SelectMany(x => x.Materials));

			string path = Path.Combine(Kino.Paths.Config, $"NatureFX_dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
			File.WriteAllText(path, sb.ToString());
			Kino.Log.Info($"[NatureFX] Scene dump written: {path}");
			return path;
		}

		// One line per NFX shader + keyword set: "NatureFX/Foliage | _ALPHATEST_ON _DOUBLESIDED_ON".
		// Merged with the existing file so dumps from several tracks add up.
		private static void WriteVariants(IEnumerable<KeyValuePair<Material, MaterialInfo>> materials) {
			var found = new List<string>();
			foreach (var kv in materials) {
				if (kv.Value.Target == null) {
					continue;
				}
				var keywords = Nature.MaterialReplacer.KeywordsAfterSwap(kv.Key).OrderBy(k => k, StringComparer.Ordinal);
				found.Add($"{kv.Value.Target} | {string.Join(" ", keywords)}".TrimEnd());
			}
			MergeVariants(found);
		}

		// Adds keyword sets to NatureFX_variants.txt (also called on every map load with the materials the shader
		// rebuild switched: Unity's variant logging stays silent in the game's release build)
		public static void MergeVariants(IEnumerable<string> found) {
			string path = Path.Combine(Kino.Paths.Config, VariantsFileName);
			var lines = new SortedSet<string>(StringComparer.Ordinal);
			if (File.Exists(path)) {
				foreach (string l in File.ReadAllLines(path)) {
					if (l.Trim().Length > 0 && !l.StartsWith("#")) {
						lines.Add(l.Trim());
					}
				}
			}

			int before = lines.Count;
			foreach (string line in found) {
				lines.Add(line);
			}
			if (lines.Count == before && File.Exists(path)) {
				return;
			}

			var sb = new StringBuilder();
			sb.AppendLine("# NatureFX shader variants used by this game (written by Tools > Dump scene shaders).");
			sb.AppendLine("# Copy to unity/variants/ in the NatureFX repo and rebuild the shader bundle.");
			foreach (string l in lines) {
				sb.AppendLine(l);
			}
			File.WriteAllText(path, sb.ToString());
			Kino.Log.Info($"[NatureFX] Variants: {lines.Count} ({lines.Count - before} new) -> {path}");
		}
	}
}

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

		private class MaterialInfo {
			public int Renderers;
			public readonly HashSet<string> Objects = new();
		}

		private class ShaderInfo {
			public Shader Shader;
			public int Renderers;
			public readonly Dictionary<Material, MaterialInfo> Materials = new();
		}

		public static string Dump() {
			var byShader = new Dictionary<string, ShaderInfo>();

			foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string objectPath = null;
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
					if (mat.Objects.Count < MaxObjectsPerMaterial) {
						// same path format the replacement rules match against
						objectPath ??= Nature.MaterialReplacer.ObjectPath(r.transform);
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
					sb.AppendLine($"  - {kv.Key.name}  (renderers: {kv.Value.Renderers})");
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

			string path = Path.Combine(Kino.Paths.Config, $"NatureFX_dump_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
			File.WriteAllText(path, sb.ToString());
			Kino.Log.Info($"[NatureFX] Scene dump written: {path}");
			return path;
		}
	}
}

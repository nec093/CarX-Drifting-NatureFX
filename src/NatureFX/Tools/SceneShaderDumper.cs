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
		private class ShaderInfo {
			public Shader Shader;
			public int Renderers;
			public readonly HashSet<string> Materials = new();
			public readonly HashSet<string> Objects = new();
		}

		public static string Dump() {
			var byShader = new Dictionary<string, ShaderInfo>();

			foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				foreach (var m in r.sharedMaterials) {
					if (m == null || m.shader == null) {
						continue;
					}

					if (!byShader.TryGetValue(m.shader.name, out var info)) {
						info = new ShaderInfo { Shader = m.shader };
						byShader[m.shader.name] = info;
					}

					++info.Renderers;
					info.Materials.Add(m.name);
					if (info.Objects.Count < 10) {
						info.Objects.Add(r.gameObject.name);
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
				sb.AppendLine($"  materials: {string.Join(", ", info.Materials.Take(30))}");
				sb.AppendLine($"  objects:   {string.Join(", ", info.Objects)}");

				int count = s.GetPropertyCount();
				var props = new List<string>(count);
				for (int p = 0; p < count; ++p) {
					props.Add($"{s.GetPropertyName(p)}:{s.GetPropertyType(p)}");
				}
				sb.AppendLine($"  properties: {string.Join(", ", props)}");
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

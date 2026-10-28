using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace NatureFX.Nature {
	// Swaps game shaders for NFX shaders by rules loaded from <kino config>/NatureFX.rules.txt.
	// NFX graphs use the same property names as HDRP/Lit (_BaseColorMap, _NormalMap, _MaskMap ...),
	// so textures and colors carry over when only the shader is changed.
	public class MaterialReplacer {
		private class Rule {
			public Regex Shader;
			public Regex Material;
			public string Target;
		}

		private const string RulesFileName = "NatureFX.rules.txt";

		private const string DefaultRules =
			"# NatureFX material replacement rules\n" +
			"# <game shader regex> | <material name regex> => <NFX shader name>\n" +
			"# The first matching rule wins. Use the shader dump (mod menu > Tools) to find names.\n" +
			"# Lines starting with # are ignored.\n" +
			"\n" +
			"(?i)(foliage|grass|vegetation|leaf|leaves|bush|tree|speedtree) | .* => NatureFX/Foliage\n" +
			"(?i)^HDRP/Lit$ | (?i)(grass|leaf|leaves|bush|foliage|fern|hedge) => NatureFX/Foliage\n" +
			"(?i)^HDRP/Lit$ | (?i)(ground|terrain|dirt|soil|rock|cliff|gravel|road|asphalt|concrete|curb|kerb|sidewalk|roof) => NatureFX/LitAccumulation\n";

		private readonly List<Rule> rules_ = new();
		private readonly Dictionary<Material, Shader> originals_ = new();

		public int ReplacedCount => originals_.Count;
		public string RulesPath => Path.Combine(Kino.Paths.Config, RulesFileName);

		public void LoadRules() {
			rules_.Clear();
			string path = RulesPath;

			try {
				if (!File.Exists(path)) {
					File.WriteAllText(path, DefaultRules);
				}

				foreach (string raw in File.ReadAllLines(path)) {
					string line = raw.Trim();
					if (line.Length == 0 || line.StartsWith("#")) {
						continue;
					}

					int arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
					int bar = line.IndexOf('|');
					if (arrow < 0 || bar < 0 || bar > arrow) {
						Kino.Log.Warning($"[NatureFX] Bad rule: {line}");
						continue;
					}

					rules_.Add(new Rule {
						Shader = new Regex(line.Substring(0, bar).Trim()),
						Material = new Regex(line.Substring(bar + 1, arrow - bar - 1).Trim()),
						Target = line.Substring(arrow + 2).Trim()
					});
				}
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Failed to read rules '{path}': {e.Message}");
			}

			Kino.Log.Info($"[NatureFX] {rules_.Count} replacement rules loaded");
		}

		public void Apply(ShaderBundle bundle) {
			if (!bundle.Loaded) {
				return;
			}

			int replaced = 0;
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				foreach (var m in r.sharedMaterials) {
					if (TryReplace(m, bundle)) {
						++replaced;
					}
				}
			}

			foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				if (TryReplace(t.materialTemplate, bundle)) {
					++replaced;
				}
			}

			if (replaced > 0) {
				Kino.Log.Info($"[NatureFX] Replaced shaders on {replaced} materials");
			}
		}

		public void RestoreAll() {
			foreach (var kv in originals_) {
				if (kv.Key != null) {
					kv.Key.shader = kv.Value;
					Validate(kv.Key);
				}
			}
			originals_.Clear();
		}

		private bool TryReplace(Material m, ShaderBundle bundle) {
			if (m == null || m.shader == null || originals_.ContainsKey(m)) {
				return false;
			}

			string shaderName = m.shader.name;
			if (shaderName.StartsWith("NatureFX/")) {
				return false;
			}

			foreach (var rule in rules_) {
				if (!rule.Shader.IsMatch(shaderName) || !rule.Material.IsMatch(m.name)) {
					continue;
				}

				var target = bundle.Get(rule.Target);
				if (target == null) {
					return false;
				}

				originals_[m] = m.shader;
				int queue = m.renderQueue;
				m.shader = target;
				m.renderQueue = queue;
				Validate(m);
				return true;
			}

			return false;
		}

		// Rebuilds HDRP keywords / passes for the new shader.
		private static void Validate(Material m) {
			try {
				HDMaterial.ValidateMaterial(m);
			} catch (Exception) {
				// not an HDRP-generated shader; nothing to validate
			}
		}
	}
}

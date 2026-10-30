using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace NatureFX.Editor {
	// Fast bundle builds: compiles only the variants of the big Lit copies (NatureFX/Foliage, NatureFX/LitAccumulation)
	// that the game was seen using. The list is recorded in game (F8 > Tools > Shader variant recording, see
	// src/NatureFX/Tools/ShaderVariantRecorder.cs), copied to unity/variants/recorded_variants.txt and synced here
	// by tools/build_bundle.sh.
	//
	// NFX_VARIANTS=full builds everything (use it for releases: a variant missing from the recording falls back to
	// the nearest compiled one at runtime, which can look wrong). A shader without any recorded entry is left alone;
	// passes the game never drew (META, scene picking ...) are skipped entirely.
	public class RecordedVariantStripper : IPreprocessShaders {
		private const string RecordingPath = "Assets/NatureFX/Variants/recorded_variants.txt";
		private static readonly HashSet<string> Shaders = new() { "NatureFX/Foliage", "NatureFX/LitAccumulation" };

		// shader -> pass -> stage ("vertex" / "fragment" / "") -> keyword sets
		private static Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>> recorded_;
		private static readonly Dictionary<string, (int Kept, int Stripped)> stats_ = new();

		public int callbackOrder => 1000; // after HDRP's own stripping

		public static bool Active => Load() != null;

		public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data) {
			// the ray tracing pass is never drawn, so the recording cannot list it: keep its variants
			if (!Shaders.Contains(shader.name) || snippet.passName == "NFXRayTracing") {
				return;
			}
			var recording = Load();
			if (recording == null || !recording.TryGetValue(shader.name, out var passes)) {
				return;
			}

			string stage = Stage(snippet.shaderType);
			HashSet<string> allowed;
			if (!passes.TryGetValue(snippet.passName, out var stages)) {
				// the game never drew this pass (META, scene picking ...): nothing to keep
				allowed = new HashSet<string>();
			} else if (!stages.TryGetValue(stage, out allowed)) {
				// stage names in the log did not map: match on the pass alone
				allowed = new HashSet<string>(stages.Values.SelectMany(s => s));
			}

			int before = data.Count;
			for (int i = data.Count - 1; i >= 0; --i) {
				string keywords = string.Join(" ", data[i].shaderKeywordSet.GetShaderKeywords().Select(k => k.name).OrderBy(k => k, StringComparer.Ordinal));
				if (!allowed.Contains(keywords)) {
					data.RemoveAt(i);
				}
			}

			string key = $"{shader.name} {snippet.passName} {stage}";
			stats_.TryGetValue(key, out var s);
			stats_[key] = (s.Kept + data.Count, s.Stripped + before - data.Count);
		}

		// Called by BuildNatureFXBundle after the build
		public static void LogSummary() {
			if (stats_.Count == 0) {
				return;
			}
			foreach (var kv in stats_.OrderBy(k => k.Key)) {
				Debug.Log($"[NatureFX] Recorded variants: {kv.Key}: kept {kv.Value.Kept}, skipped {kv.Value.Stripped}");
			}
			stats_.Clear();
		}

		private static Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>> Load() {
			if (string.Equals(Environment.GetEnvironmentVariable("NFX_VARIANTS"), "full", StringComparison.OrdinalIgnoreCase)) {
				return null;
			}
			if (recorded_ != null) {
				return recorded_;
			}
			string path = Path.GetFullPath(RecordingPath);
			if (!File.Exists(path)) {
				return null;
			}

			recorded_ = new Dictionary<string, Dictionary<string, Dictionary<string, HashSet<string>>>>();
			int count = 0;
			foreach (string line in File.ReadAllLines(path)) {
				if (line.Length == 0 || line.StartsWith("#")) {
					continue;
				}
				string[] f = line.Split('|');
				if (f.Length < 4) {
					continue;
				}
				string keywords = string.Join(" ", f[3].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).OrderBy(k => k, StringComparer.Ordinal));
				if (!recorded_.TryGetValue(f[0], out var passes)) {
					recorded_[f[0]] = passes = new Dictionary<string, Dictionary<string, HashSet<string>>>();
				}
				if (!passes.TryGetValue(f[1], out var stages)) {
					passes[f[1]] = stages = new Dictionary<string, HashSet<string>>();
				}
				string stage = StageFromLog(f[2]);
				if (!stages.TryGetValue(stage, out var sets)) {
					stages[stage] = sets = new HashSet<string>();
				}
				sets.Add(keywords);
				++count;
			}
			Debug.Log($"[NatureFX] Recorded variants: {count} entries from {RecordingPath}; other variants of {string.Join(", ", Shaders)} are skipped (NFX_VARIANTS=full to build all)");
			return recorded_;
		}

		private static string Stage(ShaderType type) {
			return type == ShaderType.Vertex ? "vertex" : type == ShaderType.Fragment ? "fragment" : type.ToString().ToLowerInvariant();
		}

		private static string StageFromLog(string stage) {
			string s = stage.Trim().ToLowerInvariant();
			return s == "pixel" || s == "fragment" || s == "fp" ? "fragment" : s == "vertex" || s == "vp" ? "vertex" : s;
		}
	}
}

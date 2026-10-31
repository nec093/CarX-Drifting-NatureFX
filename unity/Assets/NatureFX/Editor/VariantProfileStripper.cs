using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace NatureFX.Editor {
	// Leaves out the variants of the big Lit copies (NatureFX/Foliage, NatureFX/LitAccumulation) that the game can
	// never ask for, by the facts the mod writes about how the game draws (src/NatureFX/Tools/VariantProfileDump.cs,
	// kino/config/NatureFX/game_hdrp/game_variant_profile.txt, copied to unity/variants/ and synced here by
	// tools/build_bundle.sh). Unlike a recording of single variants this keeps whole dimensions, so it is safe for
	// release builds as long as the profile covers every map the bundle will meet.
	//   litShaderMode=Deferred       opaque keyword sets lose the Forward pass and the transparent-only passes
	//                                (HDRP draws them through the GBuffer); transparent sets lose the GBuffer pass;
	//                                the Forward pass keeps the clustered light list only (transparent objects)
	//   directionalLightmaps=no      no DIRLIGHTMAP_COMBINED
	//   shadowmask=no                no SHADOWS_SHADOWMASK
	//   lodCrossfade=no              no LOD_FADE_CROSSFADE
	//   msaa=no                      no WRITE_MSAA_DEPTH
	// A fact missing from the file keeps its dimension. NFX_VARIANTS=full builds everything.
	public class VariantProfileStripper : IPreprocessShaders {
		private const string ProfilePath = "Assets/NatureFX/Variants/game_variant_profile.txt";
		private static readonly HashSet<string> Shaders = new() { "NatureFX/Foliage", "NatureFX/LitAccumulation" };
		// passes HDRP only draws for transparent materials
		private static readonly HashSet<string> TransparentPasses = new() {
			"Forward", "TransparentDepthPrepass", "TransparentDepthPostpass", "TransparentBackface", "DistortionVectors"
		};

		private static Dictionary<string, string> profile_;
		private static readonly Dictionary<string, (int Kept, int Stripped)> stats_ = new();

		public int callbackOrder => 1001; // after HDRP's own stripping and the recorded variants

		public static bool Active => Load() != null;

		public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data) {
			if (!Shaders.Contains(shader.name)) {
				return;
			}
			var profile = Load();
			if (profile == null) {
				return;
			}
			bool deferred = Value(profile, "litShaderMode") == "Deferred";
			bool noDirectional = Value(profile, "directionalLightmaps") == "no";
			bool noShadowmask = Value(profile, "shadowmask") == "no";
			bool noCrossfade = Value(profile, "lodCrossfade") == "no";
			bool noMsaa = Value(profile, "msaa") == "no";

			int before = data.Count;
			for (int i = data.Count - 1; i >= 0; --i) {
				var keywords = new HashSet<string>(data[i].shaderKeywordSet.GetShaderKeywords().Select(k => k.name));
				bool transparent = keywords.Contains("_SURFACE_TYPE_TRANSPARENT");
				bool strip = false;
				if (deferred) {
					if (!transparent && TransparentPasses.Contains(snippet.passName)) {
						strip = true;
					} else if (transparent && snippet.passName == "GBuffer") {
						strip = true;
					} else if (snippet.passName == "Forward" && keywords.Contains("USE_FPTL_LIGHTLIST")) {
						strip = true; // transparent objects take the clustered light list
					}
				}
				if (noDirectional && keywords.Contains("DIRLIGHTMAP_COMBINED")) {
					strip = true;
				}
				if (noShadowmask && keywords.Contains("SHADOWS_SHADOWMASK")) {
					strip = true;
				}
				if (noCrossfade && keywords.Contains("LOD_FADE_CROSSFADE")) {
					strip = true;
				}
				if (noMsaa && keywords.Contains("WRITE_MSAA_DEPTH")) {
					strip = true;
				}
				if (strip) {
					data.RemoveAt(i);
				}
			}

			string key = $"{shader.name} {snippet.passName} {snippet.shaderType}";
			stats_.TryGetValue(key, out var s);
			stats_[key] = (s.Kept + data.Count, s.Stripped + before - data.Count);
		}

		// Called by BuildNatureFXBundle after the build
		public static void LogSummary() {
			if (stats_.Count == 0) {
				return;
			}
			int kept = 0, stripped = 0;
			foreach (var kv in stats_.OrderBy(k => k.Key)) {
				if (kv.Value.Stripped > 0) {
					Debug.Log($"[NatureFX] Variant profile: {kv.Key}: kept {kv.Value.Kept}, left out {kv.Value.Stripped}");
				}
				kept += kv.Value.Kept;
				stripped += kv.Value.Stripped;
			}
			Debug.Log($"[NatureFX] Variant profile: {kept} variants kept, {stripped} left out in all");
			stats_.Clear();
		}

		private static string Value(Dictionary<string, string> profile, string key) {
			return profile.TryGetValue(key, out string v) ? v : "";
		}

		private static Dictionary<string, string> Load() {
			if (string.Equals(Environment.GetEnvironmentVariable("NFX_VARIANTS"), "full", StringComparison.OrdinalIgnoreCase)) {
				return null;
			}
			if (profile_ != null) {
				return profile_;
			}
			string path = Path.GetFullPath(ProfilePath);
			if (!File.Exists(path)) {
				return null;
			}
			profile_ = new Dictionary<string, string>(StringComparer.Ordinal);
			foreach (string line in File.ReadAllLines(path)) {
				int eq = line.IndexOf('=');
				if (line.StartsWith("#") || eq <= 0) {
					continue;
				}
				profile_[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
			}
			Debug.Log($"[NatureFX] Variant profile from {ProfilePath}: {string.Join(", ", profile_.Select(kv => $"{kv.Key}={kv.Value}"))} " +
			          "(NFX_VARIANTS=full to build all)");
			return profile_;
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace NatureFX.Tools {
	// Facts about how the game draws, so the bundle build can leave out the shader variants the game can never ask for
	// (unity/Assets/NatureFX/Editor/VariantProfileStripper.cs): kino/config/NatureFX/game_hdrp/game_variant_profile.txt,
	// next to the HDRP settings dump, copied to unity/variants/ with it. Written after each map loads and merged with
	// what earlier maps showed: a feature seen once stays "yes" (the bundle must serve every map), so the file only
	// ever grows towards the full set.
	//   litShaderMode        Deferred: opaque materials draw through the GBuffer pass, the Forward pass only draws
	//                        transparent ones (and then with the clustered light list). Forward sticks once seen.
	//   directionalLightmaps yes when a map's lightmaps carry a direction (DIRLIGHTMAP_COMBINED)
	//   shadowmask           yes when a map's lightmaps carry a shadow mask (SHADOWS_SHADOWMASK)
	//   lodCrossfade         yes when a map's LOD groups cross fade (LOD_FADE_CROSSFADE)
	//   msaa                 yes when the camera renders with MSAA (WRITE_MSAA_DEPTH)
	// Unity's "log shader compilation" (ShaderVariantRecorder) writes nothing in the game's release player, so the
	// variants cannot be recorded one by one; these facts cut the same dimensions.
	public static class VariantProfileDump {
		public const string FileName = "game_variant_profile.txt";
		public static string FilePath => Path.Combine(HdrpSettingsDump.Folder, FileName);
		public static string LastMessage { get; private set; } = "-";

		public static void Write(Camera camera) {
			if (camera == null) {
				return;
			}
			try {
				var hd = HDCamera.GetOrCreate(camera);
				bool forward = hd.frameSettings.litShaderMode == LitShaderMode.Forward;
				var lightmaps = LightmapSettings.lightmaps ?? Array.Empty<LightmapData>();
				bool directional = lightmaps.Length > 0 && LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional
				                   && lightmaps.Any(l => l != null && l.lightmapDir != null);
				bool shadowmask = lightmaps.Any(l => l != null && l.shadowMask != null);
				bool crossfade = Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(g => g.fadeMode != LODFadeMode.None);
				bool msaa = hd.msaaEnabled;

				var facts = Load();
				int maps = facts.TryGetValue("maps", out string m) && int.TryParse(m, out int seen) ? seen : 0;
				Merge(facts, "litShaderMode", forward ? "Forward" : "Deferred", "Forward");
				Merge(facts, "directionalLightmaps", directional ? "yes" : "no", "yes");
				Merge(facts, "shadowmask", shadowmask ? "yes" : "no", "yes");
				Merge(facts, "lodCrossfade", crossfade ? "yes" : "no", "yes");
				Merge(facts, "msaa", msaa ? "yes" : "no", "yes");
				facts["maps"] = (maps + 1).ToString();

				Directory.CreateDirectory(HdrpSettingsDump.Folder);
				var lines = new List<string> {
					"# NatureFX: how the game draws, merged over every map loaded so far (VariantProfileDump.cs). Copy to",
					"# unity/variants/ with the game_hdrp files: the bundle build leaves out the variants these rule out",
					"# (Editor/VariantProfileStripper.cs). A fact seen once stays; delete the file to start over."
				};
				lines.AddRange(facts.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"));
				File.WriteAllLines(FilePath, lines);
				LastMessage = string.Join(", ", facts.Where(kv => kv.Key != "maps").Select(kv => $"{kv.Key} {kv.Value}")) + $" ({facts["maps"]} maps)";
				Kino.Log.Info($"[NatureFX] Variant profile: this map {(forward ? "Forward" : "Deferred")}, directional lightmaps {directional}, " +
				              $"shadowmask {shadowmask}, LOD cross fade {crossfade}, MSAA {msaa}; merged: {LastMessage} -> {FilePath}");
			} catch (Exception e) {
				LastMessage = $"failed: {e.Message}";
				Kino.Log.Warning($"[NatureFX] Variant profile not written: {e.Message}");
			}
		}

		// the value that must win once seen (Forward, yes) sticks
		private static void Merge(Dictionary<string, string> facts, string key, string value, string sticky) {
			if (facts.TryGetValue(key, out string old) && old == sticky) {
				return;
			}
			facts[key] = value;
		}

		private static Dictionary<string, string> Load() {
			var facts = new Dictionary<string, string>(StringComparer.Ordinal);
			if (!File.Exists(FilePath)) {
				return facts;
			}
			foreach (string line in File.ReadAllLines(FilePath)) {
				int eq = line.IndexOf('=');
				if (line.StartsWith("#") || eq <= 0) {
					continue;
				}
				facts[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
			}
			return facts;
		}
	}
}

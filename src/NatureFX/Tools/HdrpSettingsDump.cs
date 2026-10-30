using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Tools {
	// Writes the game's HDRP pipeline settings to kino/config/NatureFX/game_hdrp/ so the bundle build can use
	// exactly the same HDRP assets (unity/Assets/NatureFX/Editor/BuildNatureFXBundle.cs) and compile only the shader
	// variants the game can ask for, instead of every feature:
	//   game_hdrp_<asset>.json   RenderPipelineSettings of each HDRP asset used by a quality level (JsonUtility)
	//   game_hdrp_levels.txt     quality level -> asset, and which level is active
	public static class HdrpSettingsDump {
		public static string Folder => Path.Combine(Kino.Paths.Config, "NatureFX", "game_hdrp");

		public static void Write() {
			try {
				Directory.CreateDirectory(Folder);
				int current = QualitySettings.GetQualityLevel();
				var fallback = GraphicsSettings.defaultRenderPipeline as HDRenderPipelineAsset;
				var lines = new List<string> { "# quality level | name | HDRP asset | active" };
				var written = new HashSet<string>();

				for (int i = 0; i < QualitySettings.names.Length; ++i) {
					var asset = QualitySettings.GetRenderPipelineAssetAt(i) as HDRenderPipelineAsset ?? fallback;
					string name = asset != null ? FileSafe(asset.name) : "-";
					lines.Add($"{i} | {QualitySettings.names[i]} | {name} | {(i == current ? "active" : "")}");
					if (asset != null && written.Add(name)) {
						File.WriteAllText(Path.Combine(Folder, $"game_hdrp_{name}.json"), JsonUtility.ToJson(asset.currentPlatformRenderPipelineSettings, true));
					}
				}
				File.WriteAllLines(Path.Combine(Folder, "game_hdrp_levels.txt"), lines);
				Kino.Log.Info($"[NatureFX] Game HDRP settings: {written.Count} assets ({string.Join(", ", written)}) for {QualitySettings.names.Length} quality levels, active '{QualitySettings.names[current]}' -> {Folder}");
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Could not write the game's HDRP settings: {e.Message}");
			}
		}

		private static string FileSafe(string name) {
			var invalid = Path.GetInvalidFileNameChars();
			return new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
		}
	}
}

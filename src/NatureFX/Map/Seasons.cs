using UnityEngine;

namespace NatureFX.Map {
	// The season the foliage colours, the 3D grass and the floating particles follow (docs/map-authoring.md 5.3):
	// the menu's choice, else the weather (the Autumn and Snow presets), else the map's [defaults] season, else none
	// (the textures' own colours). The shaders get it as a position 0 spring .. 3 winter that moves on by a season in
	// about 30 seconds, so a change rolls over the trees one cell after another (NFX_Foliage.hlsl).
	public static class Seasons {
		public const int None = -1;
		private const float SecondsPerSeason = 30.0f;
		private static readonly int SeasonId = Shader.PropertyToID("_NFX_Season");
		private static readonly string[] Names = { "spring", "summer", "autumn", "winter" };

		private static float position_ = None;

		// 0 spring .. 3 winter, or None
		public static int Current { get; private set; } = None;
		public static float Position => position_;

		public static string Describe() {
			return Current < 0 ? "none" : Names[Current];
		}

		public static void Update(ISettings s, int weatherPreset, string mapSeason, bool enabled) {
			int target = None;
			if (enabled) {
				if (s.Season >= 1 && s.Season <= 4) {
					target = s.Season - 1;
				} else if (s.Season == 0) {
					// weather presets: 5 Snow, 6 Autumn (WeatherController.Names)
					target = weatherPreset == 5 ? 3 : weatherPreset == 6 ? 2 : Parse(mapSeason);
				}
			}
			Current = target;
			if (target < 0) {
				position_ = None;
			} else if (position_ < 0.0f) {
				position_ = target;
			} else {
				// forward through the year to the target (autumn to spring passes winter)
				float ahead = Mathf.Repeat(target - position_, 4.0f);
				float step = Time.unscaledDeltaTime / SecondsPerSeason;
				position_ = ahead <= step || ahead > 3.999f ? target : Mathf.Repeat(position_ + step, 4.0f);
			}
			Shader.SetGlobalVector(SeasonId, new Vector4(position_, 0.0f, 0.0f, 0.0f));
		}

		public static int Parse(string name) {
			for (int i = 0; i < Names.Length; ++i) {
				if (string.Equals(name, Names[i], System.StringComparison.OrdinalIgnoreCase)) {
					return i;
				}
			}
			return None;
		}
	}
}

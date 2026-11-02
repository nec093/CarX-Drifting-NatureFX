using System;
using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using UnityEngine;

namespace NatureFX.Map {
	// The weather and wind values a map profile can set ([defaults] and [zone.<id>], docs/map-authoring.md 1 and 3)
	public enum MapKey { Snow, Leaves, Dust, Wetness, WindStrength, WindGust, WindDirection, FogDistance, FogHeight, GroundBlend }

	// One section's values; NaN = not set there
	public class MapValues {
		public const int Count = 10;
		private readonly float[] v_ = new float[Count];

		private static readonly (string Name, MapKey Key, float Min, float Max)[] Keys = {
			("snow", MapKey.Snow, 0.0f, 1.0f),
			("leaves", MapKey.Leaves, 0.0f, 1.0f),
			("dust", MapKey.Dust, 0.0f, 1.0f),
			("wetness", MapKey.Wetness, 0.0f, 1.0f),
			("wind.strength", MapKey.WindStrength, 0.0f, 2.0f),
			("wind.gust", MapKey.WindGust, 0.0f, 1.0f),
			("wind.direction", MapKey.WindDirection, -720.0f, 720.0f),
			("fog.distance", MapKey.FogDistance, 0.0f, 100000.0f),
			("fog.height", MapKey.FogHeight, 1.0f, 10000.0f),
			// how high objects take the ground's colour where they stand in it (m; NFX_Ground.hlsl), 0 off
			("groundblend", MapKey.GroundBlend, 0.0f, 3.0f)
		};

		// season = a shortcut for the accumulation values not written out: snow, leaves, dust, wetness
		private static readonly Dictionary<string, float[]> Seasons = new(StringComparer.OrdinalIgnoreCase) {
			["spring"] = new[] { 0.0f, 0.1f, 0.1f, 0.2f },
			["summer"] = new[] { 0.0f, 0.0f, 0.3f, 0.0f },
			["autumn"] = new[] { 0.0f, 0.5f, 0.15f, 0.1f },
			["winter"] = new[] { 0.5f, 0.1f, 0.0f, 0.3f }
		};

		// keys a zone section has on top of the values (WeatherZones)
		private static readonly HashSet<string> OtherKeys = new(StringComparer.OrdinalIgnoreCase) { "season", "blend", "priority" };

		public MapValues() {
			Clear();
		}

		public float this[MapKey k] {
			get => v_[(int)k];
			set => v_[(int)k] = value;
		}

		public bool Has(MapKey k) {
			return !float.IsNaN(v_[(int)k]);
		}

		public bool Any {
			get {
				foreach (float f in v_) {
					if (!float.IsNaN(f)) {
						return true;
					}
				}
				return false;
			}
		}

		public void Clear() {
			for (int i = 0; i < Count; ++i) {
				v_[i] = float.NaN;
			}
		}

		public static string Name(MapKey k) {
			return Keys[(int)k].Name;
		}

		public static MapValues Parse(MapProfile profile, string section) {
			var values = new MapValues();
			if (!profile.Loaded) {
				return values;
			}
			foreach (var key in Keys) {
				if (TryFloat(profile.Get(section, key.Name), out float f)) {
					values[key.Key] = Mathf.Clamp(f, key.Min, key.Max);
				}
			}
			string season = profile.Get(section, "season");
			if (season != null && Seasons.TryGetValue(season, out var table)) {
				for (int i = 0; i < table.Length; ++i) {
					if (!values.Has((MapKey)i)) {
						values[(MapKey)i] = table[i];
					}
				}
			} else if (season != null && !string.Equals(season, "none", StringComparison.OrdinalIgnoreCase)) {
				Kino.Log.Warning($"[NatureFX] [{section}] season = {season}: not one of none, spring, summer, autumn, winter");
			}
			foreach (string line in profile.Section(section)) {
				int eq = line.IndexOf('=');
				string name = eq > 0 ? line.Substring(0, eq).Trim() : line;
				if (!OtherKeys.Contains(name) && Array.FindIndex(Keys, k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) < 0) {
					Kino.Log.Warning($"[NatureFX] [{section}] unknown key '{name}' is ignored");
				}
			}
			return values;
		}

		public static bool TryFloat(string text, out float value) {
			return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
		}
	}

	// [defaults] and the weather zones as the controllers see them: each frame Update blends the zones around the
	// camera over the map's defaults, and Value gives a controller the map's value for a key or, where the map sets
	// none or the user moved that slider since the map loaded, the user's own (docs/map-authoring.md 1: the user's
	// changes come first). Nothing is written into the settings, so another map or switching map profiles off leaves
	// the user's values as they were.
	public class MapDefaults {
		// this frame's map values; NaN where the user's value stays
		private static readonly MapValues Effective = new();
		// how much of the map's fog is used (1 with fog in [defaults], else how far the camera is inside a fog zone)
		public static float FogWeight { get; private set; }

		private static readonly MapKey[] UserKeys = { MapKey.Snow, MapKey.Leaves, MapKey.Dust, MapKey.Wetness, MapKey.WindStrength, MapKey.WindGust, MapKey.WindDirection };

		private MapValues defaults_ = new();
		private readonly float[] baseline_ = new float[MapValues.Count];
		private readonly bool[] taken_ = new bool[MapValues.Count];

		public MapValues Defaults => defaults_;
		// [defaults] season (Seasons: the foliage colours follow it unless the menu or the weather says otherwise)
		public string Season { get; private set; }

		public static float Value(MapKey k, float user) {
			float v = Effective[k];
			return float.IsNaN(v) ? user : v;
		}

		public static bool Sets(MapKey k) {
			return Effective.Has(k);
		}

		// A new map: its defaults, and the user's values as they are now (moving a slider away from these takes it over)
		public void Load(MapProfile profile, ISettings user) {
			defaults_ = MapValues.Parse(profile, "defaults");
			Season = profile.Loaded ? profile.Get("defaults", "season") : null;
			foreach (var k in UserKeys) {
				baseline_[(int)k] = UserValue(k, user);
				taken_[(int)k] = false;
			}
			if (defaults_.Any) {
				var set = new List<string>();
				for (int i = 0; i < MapValues.Count; ++i) {
					if (defaults_.Has((MapKey)i)) {
						set.Add($"{MapValues.Name((MapKey)i)} {defaults_[(MapKey)i].ToString("0.##", CultureInfo.InvariantCulture)}");
					}
				}
				Kino.Log.Info($"[NatureFX] Map defaults: {string.Join(", ", set)}");
			}
		}

		public void Update(ISettings user, WeatherZones zones, Vector3 camera, bool enabled) {
			if (!enabled) {
				Effective.Clear();
				FogWeight = 0.0f;
				return;
			}
			zones.Sample(camera);
			for (int i = 0; i < MapValues.Count; ++i) {
				var k = (MapKey)i;
				float value = defaults_[k];
				bool isUser = i <= (int)MapKey.WindDirection;
				if (isUser) {
					float u = UserValue(k, user);
					// the user moved this slider since the map loaded: theirs from now on (until the next map)
					if (!taken_[i] && Mathf.Abs(u - baseline_[i]) > 1e-4f) {
						taken_[i] = true;
					}
					if (taken_[i]) {
						Effective[k] = float.NaN;
						continue;
					}
					value = zones.Blend(k, float.IsNaN(value) ? u : value, out _);
					// a value equal to the user's still counts as set: the zone may move it next frame
					Effective[k] = defaults_.Has(k) || zones.Sets(k) ? value : float.NaN;
				} else {
					// fog.distance = 0 is no fog, not the thickest: a zone's fog then fades in by the volume's weight
					bool noFog = k == MapKey.FogDistance && !(value > 0.0f);
					value = zones.Blend(k, noFog ? float.NaN : value, out float zoneWeight);
					Effective[k] = value;
					if (k == MapKey.FogDistance) {
						FogWeight = noFog ? zoneWeight : 1.0f;
					}
				}
			}
		}

		// what the map sets right now, for F8 > Map tab
		public string Describe() {
			var parts = new List<string>();
			for (int i = 0; i < MapValues.Count; ++i) {
				var k = (MapKey)i;
				if (Effective.Has(k)) {
					parts.Add($"{MapValues.Name(k)} {Effective[k].ToString("0.##", CultureInfo.InvariantCulture)}");
				} else if (i < taken_.Length && taken_[i] && (defaults_.Has(k))) {
					parts.Add($"{MapValues.Name(k)}: yours");
				}
			}
			return parts.Count == 0 ? "none" : string.Join(", ", parts);
		}

		private static float UserValue(MapKey k, ISettings s) {
			return k switch {
				MapKey.Snow => s.Snow,
				MapKey.Leaves => s.Leaves,
				MapKey.Dust => s.Dust,
				MapKey.Wetness => s.Wetness,
				MapKey.WindStrength => s.WindStrength,
				MapKey.WindGust => s.WindGust,
				MapKey.WindDirection => s.WindDirection,
				_ => 0.0f
			};
		}
	}
}

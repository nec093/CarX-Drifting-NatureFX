using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// Local fog (docs/map-authoring.md 7.4): nfx_volume_<preset> fills its box with HDRP Local Volumetric Fog over a
	// drifting 3D noise, when its conditions hold: mist in the early morning and after rain, steam on cold days, smoke
	// by 'when', road_steam on a wet road the sun comes out on after rain, haze on clear days. A disabled Local
	// Volumetric Fog of the map follows 'when' only. They fade in and out over several seconds; at most 32 nearest the
	// camera run. Local fog needs volumetric fog on: MapFog switches it on while any of them shows.
	public class MapVolumes {
		private const int MaxVolumes = 32;
		private const int NoiseSize = 48;
		private const float FadeSeconds = 8.0f;
		private const float RoadSteamMinutes = 10.0f;

		private enum When { Always, Night, Dusk, Day }

		private class Preset {
			public float MeanFreePath; // m at density 1
			public Color Albedo;
			public Vector3 Scroll;     // m/s, before the wind
			public Vector3 Size;
		}

		private static readonly Dictionary<string, Preset> Presets = new(System.StringComparer.OrdinalIgnoreCase) {
			["mist"] = new Preset { MeanFreePath = 25.0f, Albedo = new Color(0.9f, 0.92f, 0.95f), Scroll = new Vector3(0.2f, 0.0f, 0.1f), Size = new Vector3(60.0f, 8.0f, 60.0f) },
			["steam"] = new Preset { MeanFreePath = 4.0f, Albedo = Color.white, Scroll = new Vector3(0.0f, 0.6f, 0.0f), Size = new Vector3(2.0f, 4.0f, 2.0f) },
			["smoke"] = new Preset { MeanFreePath = 6.0f, Albedo = new Color(0.55f, 0.55f, 0.58f), Scroll = new Vector3(0.0f, 1.0f, 0.0f), Size = new Vector3(4.0f, 12.0f, 4.0f) },
			["road_steam"] = new Preset { MeanFreePath = 15.0f, Albedo = Color.white, Scroll = new Vector3(0.0f, 0.3f, 0.0f), Size = new Vector3(10.0f, 1.5f, 30.0f) },
			["haze"] = new Preset { MeanFreePath = 400.0f, Albedo = new Color(0.85f, 0.88f, 0.95f), Scroll = new Vector3(0.1f, 0.0f, 0.1f), Size = new Vector3(400.0f, 60.0f, 400.0f) }
		};

		private class Volume {
			public LocalVolumetricFog Fog;
			public string Preset;     // null: the author's own
			public Preset Look;
			public float Density;
			public bool Created;
			public bool HasWhen;
			public When When;
			public float Yaw = float.NaN; // smoke drift direction; NaN: the wind's
			public float Level;
			public bool Near;
			public string Why = "";  // what it shows or waits for (the Map tab)
			public Marker Marker;
		}

		// ---- the own engine's 3D fog boxes (AtmospherePass), from the volumes' own values, not read back from HDRP's
		// component (that is only for HDRP's volumetric fog, with the own fog off); a map's own Local Volumetric Fog is
		// the map's data, read from it
		public struct FogBox {
			public Vector3 Center, Right, Up, Forward, Half;
			public float MeanFreePath, FadeShare, NoiseSize;
			public Color Albedo;
			public bool Exponential;
		}
		public static readonly List<FogBox> Boxes = new();
		// the components these volumes drive (AtmospherePass leaves them to Boxes)
		public static readonly HashSet<LocalVolumetricFog> Driven = new();

		public static FogBox BoxOf(LocalVolumetricFog f) {
			var p = f.parameters;
			var t = f.transform;
			return new FogBox {
				Center = t.position, Right = t.right, Up = t.up, Forward = t.forward,
				Half = Vector3.Max(p.size * 0.5f, Vector3.one * 0.05f),
				MeanFreePath = p.meanFreePath,
				Albedo = p.albedo,
				FadeShare = Mathf.Clamp((p.positiveFade.x + p.positiveFade.y + p.positiveFade.z + p.negativeFade.x + p.negativeFade.y + p.negativeFade.z) / 6.0f, 0.05f, 1.0f),
				NoiseSize = p.textureTiling.x > 0.0f ? p.size.x / p.textureTiling.x : Mathf.Max(p.size.x / 3.0f, 0.5f),
				Exponential = p.falloffMode == LocalVolumetricFogFalloffMode.Exponential,
			};
		}

		private readonly List<Volume> volumes_ = new();
		private readonly List<Volume> sorted_ = new();
		private Texture3D noise_;
		private GameObject root_;
		private float nextSort_;
		private float lastRain_;
		private float sinceRain_ = float.MaxValue;

		public int Count => volumes_.Count;
		// MapFog: local fog shows only with volumetric fog on
		public bool AnyVisible { get; private set; }

		public void Load(MarkerScanner markers) {
			Clear();
			foreach (var m in markers.OfKind("volume")) {
				var v = new Volume { Density = Mathf.Max(0.01f, m.GetFloat("density", 1.0f)), Marker = m };
				string when = m.Get("when");
				v.HasWhen = when != null;
				v.When = ParseWhen(when);
				if (m.Get("yaw") != null) {
					v.Yaw = m.GetFloat("yaw", 0.0f);
				}
				if (m.Component is LocalVolumetricFog own) {
					v.Fog = own;
					volumes_.Add(v);
					continue;
				}
				string preset = string.IsNullOrEmpty(m.Preset) ? "mist" : m.Preset;
				if (!Presets.TryGetValue(preset, out var look)) {
					Kino.Log.Warning($"[NatureFX] Unknown volume preset '{preset}' (mist, steam, smoke, road_steam, haze)");
					continue;
				}
				if (root_ == null) {
					root_ = new GameObject("NatureFX.MapVolumes");
				}
				var go = new GameObject("NatureFX.Volume " + preset);
				go.transform.SetParent(root_.transform, false);
				go.transform.SetPositionAndRotation(m.Position, m.Rotation);
				var fog = go.AddComponent<LocalVolumetricFog>();
				var p = fog.parameters;
				p.size = m.Size.x > 0.5f && m.Size.y > 0.5f ? m.Size : look.Size;
				p.albedo = look.Albedo;
				p.meanFreePath = look.MeanFreePath / v.Density;
				p.volumeMask = Noise();
				p.maskMode = LocalVolumetricFogMaskMode.Texture;
				p.textureTiling = new Vector3(p.size.x / 12.0f, p.size.y / 12.0f, p.size.z / 12.0f);
				p.positiveFade = p.negativeFade = new Vector3(0.3f, 0.4f, 0.3f);
				p.falloffMode = LocalVolumetricFogFalloffMode.Exponential;
				p.distanceFadeStart = 120.0f;
				p.distanceFadeEnd = 200.0f;
				fog.parameters = p;
				fog.enabled = false;
				v.Fog = fog;
				v.Preset = preset.ToLowerInvariant();
				v.Look = look;
				v.Created = true;
				volumes_.Add(v);
			}
			if (volumes_.Count > 0) {
				Kino.Log.Info($"[NatureFX] {volumes_.Count} local fog volumes");
			}
			nextSort_ = 0.0f;
		}

		public void Clear() {
			foreach (var v in volumes_) {
				if (v.Fog == null) {
					continue;
				}
				if (v.Created) {
					Object.Destroy(v.Fog.gameObject);
				} else {
					v.Fog.enabled = false;
				}
			}
			volumes_.Clear();
			sorted_.Clear();
			Boxes.Clear();
			Driven.Clear();
			AnyVisible = false;
		}

		public void Destroy() {
			Clear();
			if (noise_ != null) {
				Object.Destroy(noise_);
			}
			if (root_ != null) {
				Object.Destroy(root_);
			}
		}

		public void Update(bool on, ISettings s, Light sun, Vector3 camera) {
			var w = Weather.Current;
			// time since the rain stopped (road steam)
			if (w.Rain > 0.15f) {
				sinceRain_ = 0.0f;
			} else if (lastRain_ > 0.15f || sinceRain_ < float.MaxValue) {
				sinceRain_ += Time.deltaTime;
			}
			lastRain_ = w.Rain;
			Boxes.Clear();
			if (volumes_.Count == 0) {
				AnyVisible = false;
				return;
			}

			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			// morning: the time of day when NatureFX sets it, else a low sun
			bool morning = s.SunOverride ? s.TimeOfDay >= 4.0f && s.TimeOfDay <= 8.5f : elevation > -3.0f && elevation < 12.0f;
			bool cold = w.Snow > 0.1f || Seasons.Current == 3;

			if (Time.unscaledTime >= nextSort_) {
				nextSort_ = Time.unscaledTime + 0.5f;
				sorted_.Clear();
				sorted_.AddRange(volumes_);
				sorted_.Sort((a, b) => Dist(a, camera).CompareTo(Dist(b, camera)));
				for (int i = 0; i < sorted_.Count; ++i) {
					sorted_[i].Near = i < MaxVolumes;
				}
			}

			var windDir = Shader.GetGlobalVector("_NFX_Wind");
			bool any = false;
			foreach (var v in volumes_) {
				if (v.Fog == null) {
					continue;
				}
				Driven.Add(v.Fog);
				bool suits = v.HasWhen ? Matches(v.When, elevation) : Condition(v.Preset, elevation, morning, cold, w);
				bool wanted = on && v.Near && suits;
				v.Level = Mathf.MoveTowards(v.Level, wanted ? 1.0f : 0.0f, Time.deltaTime / FadeSeconds);
				bool visible = v.Level > 0.001f;
				v.Why = !on ? "map effects off" : !v.Near ? "not among the 32 nearest" : wanted ? $"showing {v.Level:P0}"
					: v.HasWhen ? $"waits for when={v.When.ToString().ToLowerInvariant()}" : Waits(v.Preset);
				if (v.Fog.enabled != visible) {
					v.Fog.enabled = visible;
				}
				if (!visible) {
					continue;
				}
				any = true;
				if (v.Created) {
					float meanFreePath = v.Look.MeanFreePath / (v.Density * Mathf.Max(v.Level, 0.02f));
					var p = v.Fog.parameters;
					p.meanFreePath = meanFreePath;
					// drift with the wind (smoke along its yaw when given)
					var drift = float.IsNaN(v.Yaw) ? new Vector3(windDir.x, 0.0f, windDir.z) * windDir.w : Quaternion.Euler(0.0f, v.Yaw, 0.0f) * Vector3.forward;
					p.textureScrollingSpeed = v.Look.Scroll + drift * 0.6f;
					v.Fog.parameters = p;
					// the own fog's box from the volume's own values (as Load made the component: 0.3 / 0.4 fades, the
					// noise 12 m across, round)
					var size = v.Marker != null && v.Marker.Size.x > 0.5f && v.Marker.Size.y > 0.5f ? v.Marker.Size : v.Look.Size;
					var rot = v.Marker != null ? v.Marker.Rotation : v.Fog.transform.rotation;
					Boxes.Add(new FogBox {
						Center = v.Marker != null ? v.Marker.Position : v.Fog.transform.position,
						Right = rot * Vector3.right, Up = rot * Vector3.up, Forward = rot * Vector3.forward,
						Half = Vector3.Max(size * 0.5f, Vector3.one * 0.05f),
						MeanFreePath = meanFreePath, Albedo = v.Look.Albedo,
						FadeShare = (0.3f + 0.4f + 0.3f) / 3.0f, NoiseSize = 12.0f, Exponential = true,
					});
				} else {
					// the map's own component: its values are the map's data
					Boxes.Add(BoxOf(v.Fog));
				}
			}
			AnyVisible = any;
		}

		private static string Waits(string preset) {
			return preset switch {
				"mist" => "waits for the early morning (wet ground) or after rain",
				"steam" => "waits for a cold day (snow on the ground or winter)",
				"road_steam" => "waits for the sun on a wet road soon after rain",
				"haze" => "waits for a clear day",
				_ => "waits"
			};
		}

		// what this marker's volume is doing (MarkerOverlay)
		public string StatusOf(Marker m) {
			return volumes_.Find(v => v.Marker == m)?.Why;
		}

		// each volume and what it is doing, for the Map tab
		public List<string> Status(Vector3 camera) {
			var lines = new List<string>();
			foreach (var v in volumes_) {
				if (v.Fog != null) {
					lines.Add($"{v.Preset ?? "own fog"}  ·  {Vector3.Distance(v.Fog.transform.position, camera):0} m  ·  {v.Why}");
				}
			}
			return lines;
		}

		private bool Condition(string preset, float elevation, bool morning, bool cold, WeatherState w) {
			switch (preset) {
				case "mist": return (morning && w.Wetness > 0.1f) || (w.Rain < 0.1f && w.Wetness > 0.5f && elevation < 25.0f);
				case "steam": return cold;
				case "road_steam": return elevation > 8.0f && w.Rain < 0.05f && w.Wetness > 0.2f && sinceRain_ < RoadSteamMinutes * 60.0f && w.Cloud < 0.6f;
				case "haze": return elevation > 15.0f && w.Cloud < 0.35f && w.Rain < 0.05f;
				default: return true; // smoke: always, unless 'when' says otherwise
			}
		}

		private static bool Matches(When when, float elevation) {
			return when switch {
				When.Night => elevation < 0.0f,
				When.Dusk => elevation < 8.0f,
				When.Day => elevation >= 0.0f,
				_ => true
			};
		}

		private static When ParseWhen(string text) {
			return text?.ToLowerInvariant() switch {
				"night" => When.Night,
				"dusk" => When.Dusk,
				"day" => When.Day,
				_ => When.Always
			};
		}

		private static float Dist(Volume v, Vector3 camera) {
			return v.Fog != null ? (v.Fog.transform.position - camera).sqrMagnitude : float.MaxValue;
		}

		// soft, seamless noise for the fog's density (Rendering/VolumeNoise.cs)
		private Texture3D Noise() {
			if (noise_ == null) {
				noise_ = NatureFX.Rendering.VolumeNoise.Make("NatureFX.FogNoise", NoiseSize, 4, 0.3f, 0.75f, 17);
			}
			return noise_;
		}
	}
}

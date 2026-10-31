using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using UnityEngine;

namespace NatureFX.Map {
	// Weather zones (docs/map-authoring.md 3): nfx_zone_<id> boxes with the values of [zone.<id>]. Inside a box the
	// values move from the outside ones to the zone's over 'blend' metres from its edge; overlapping zones apply in
	// order of priority, so the highest is laid on last and wins.
	public class WeatherZones {
		private const float DefaultBlend = 20.0f;

		private class Zone {
			public string Id;
			public Vector3 Center;
			public Quaternion Rotation;
			public Vector3 Half;
			public float Blend;
			public float Priority;
			public MapValues Values;
			public float Weight; // this frame, at the camera
			public Marker Marker;
		}

		private readonly List<Zone> zones_ = new();

		public int Count => zones_.Count;

		public void Load(MapProfile profile, MarkerScanner markers) {
			zones_.Clear();
			foreach (var m in markers.OfKind("zone")) {
				string id = m.Preset;
				if (string.IsNullOrEmpty(id)) {
					Kino.Log.Warning($"[NatureFX] Zone marker without an id: {m.Source} (name it nfx_zone_<id>)");
					continue;
				}
				string section = "zone." + id;
				if (profile.Section(section).Count == 0) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: the profile has no [{section}]");
					continue;
				}
				var zone = new Zone {
					Marker = m,
					Id = id,
					Center = m.Position,
					Rotation = m.Rotation,
					Half = new Vector3(Mathf.Abs(m.Size.x), Mathf.Abs(m.Size.y), Mathf.Abs(m.Size.z)) * 0.5f,
					Blend = MapValues.TryFloat(profile.Get(section, "blend"), out float blend) ? Mathf.Max(0.0f, blend) : DefaultBlend,
					Priority = MapValues.TryFloat(profile.Get(section, "priority"), out float priority) ? priority : 0.0f,
					Values = MapValues.Parse(profile, section)
				};
				zones_.Add(zone);
			}
			// lowest priority first; equal ones keep the scan order
			var sorted = new List<Zone>(zones_);
			sorted.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : zones_.IndexOf(a).CompareTo(zones_.IndexOf(b)));
			zones_.Clear();
			zones_.AddRange(sorted);
			if (zones_.Count > 0) {
				Kino.Log.Info($"[NatureFX] {zones_.Count} weather zones: {string.Join(", ", zones_.ConvertAll(z => $"{z.Id} ({z.Half.x * 2:0}x{z.Half.y * 2:0}x{z.Half.z * 2:0} m, blend {z.Blend:0} m)"))}");
			}
		}

		public void Clear() {
			zones_.Clear();
		}

		// how far inside each box the camera is, as the share of the zone's value
		public void Sample(Vector3 camera) {
			foreach (var z in zones_) {
				var local = Quaternion.Inverse(z.Rotation) * (camera - z.Center);
				float depth = Mathf.Min(z.Half.x - Mathf.Abs(local.x), Mathf.Min(z.Half.y - Mathf.Abs(local.y), z.Half.z - Mathf.Abs(local.z)));
				z.Weight = depth <= 0.0f ? 0.0f : z.Blend <= 0.0f ? 1.0f : Mathf.SmoothStep(0.0f, 1.0f, depth / z.Blend);
			}
		}

		// 'outside' with the zones around the camera laid over it; NaN outside = the first zone's value as it is.
		// weight: the largest share of a zone that sets this key.
		public float Blend(MapKey k, float outside, out float weight) {
			float value = outside;
			weight = 0.0f;
			foreach (var z in zones_) {
				if (z.Weight <= 0.0f || !z.Values.Has(k)) {
					continue;
				}
				float v = z.Values[k];
				value = float.IsNaN(value) ? v : k == MapKey.WindDirection ? Mathf.LerpAngle(value, v, z.Weight) : Mathf.Lerp(value, v, z.Weight);
				weight = Mathf.Max(weight, z.Weight);
			}
			return value;
		}

		// a zone that sets this key holds the camera
		public bool Sets(MapKey k) {
			foreach (var z in zones_) {
				if (z.Weight > 0.0f && z.Values.Has(k)) {
					return true;
				}
			}
			return false;
		}

		// how far the camera is in this marker's zone (MarkerOverlay)
		public string StatusOf(Marker m) {
			var z = zones_.Find(x => x.Marker == m);
			return z == null ? $"no [zone.{m.Preset}] in the profile" : z.Weight > 0.0f ? $"inside {z.Weight:P0}" : "outside";
		}

		// the zones the camera is in, for F8 > Map tab
		public string Describe() {
			var inside = new List<string>();
			foreach (var z in zones_) {
				if (z.Weight > 0.0f) {
					inside.Add($"{z.Id} {(z.Weight * 100.0f).ToString("0", CultureInfo.InvariantCulture)}%");
				}
			}
			return inside.Count == 0 ? "outside" : string.Join(", ", inside);
		}
	}
}

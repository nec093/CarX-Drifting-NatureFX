using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace NatureFX.Map {
	// One thing a map places for NatureFX: a light, a decal, a puddle ... (docs/map-authoring.md, section 4)
	public class Marker {
		public string Kind;
		public string Preset;
		public Vector3 Position;
		public Vector3 Size;
		public Quaternion Rotation = Quaternion.identity;
		public readonly Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
		public Transform Transform;  // null for markers from the profile's [markers] section
		public Component Component;  // disabled built-in component the author set up (Light, ReflectionProbe ...), or null
		public Mesh Surface;         // MeshFilter-only surface (grass areas, water), or null
		public string Source;

		public string Get(string key, string fallback = null) {
			return Params.TryGetValue(key, out string v) ? v : fallback;
		}

		public float GetFloat(string key, float fallback) {
			return Params.TryGetValue(key, out string v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
		}

		public override string ToString() {
			string preset = string.IsNullOrEmpty(Preset) ? "" : "_" + Preset;
			string component = Component != null ? $" [{Component.GetType().Name}]" : Surface != null ? " [surface]" : "";
			return $"{Kind}{preset}{component} @ {Position.x:0.0}, {Position.y:0.0}, {Position.z:0.0}";
		}
	}

	// Collects markers placed in four ways (docs/map-authoring.md, 4.1 - 4.4):
	//   nfx_<kind>_<preset>[__<key>-<value>]...        empty object: Transform = centre, rotation, size (m)
	//   same name with a disabled built-in component    the author's Light / ReflectionProbe / DecalProjector /
	//                                                    LocalVolumetricFog / ParticleSystem; NatureFX enables it
	//   nocol_nfx_... with a MeshFilter only            surface (grass area, water)
	//   nocol_nfx_... with a MeshRenderer               box mesh marker for OBJ maps, hidden by NatureFX
	// plus the [markers] section of the map profile. Inactive objects are included: disabled is the default
	// state of everything NatureFX switches on.
	public class MarkerScanner {
		public const string Prefix = "nfx_";
		public const string MeshPrefix = "nocol_nfx_";

		public static readonly string[] Kinds = { "zone", "light", "probe", "grass", "fx", "decal", "puddle", "water", "volume" };

		// ".001" (Blender), " (1)" (Unity duplicates)
		private static readonly Regex CopySuffix = new(@"(\.\d+|\s*\(\d+\))$");
		private static readonly Regex EncodedNumber = new(@"^m?\d+(p\d+)?$");

		private readonly List<Marker> markers_ = new();
		private readonly List<Renderer> hidden_ = new();

		public IReadOnlyList<Marker> Markers => markers_;

		public IEnumerable<Marker> OfKind(string kind) {
			return markers_.Where(m => m.Kind == kind);
		}

		public static bool IsMarkerName(string name) {
			return name.StartsWith(Prefix, StringComparison.Ordinal) || name.StartsWith(MeshPrefix, StringComparison.Ordinal);
		}

		public void Scan(MapProfile profile) {
			Restore();
			markers_.Clear();

			foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string name = t.gameObject.name;
				if (!IsMarkerName(name) || name.StartsWith(MapProfile.ProfileObjectName, StringComparison.Ordinal)) {
					continue;
				}
				if (!TryParseName(name, out var marker)) {
					Kino.Log.Warning($"[NatureFX] Unknown marker '{name}' (kinds: {string.Join(", ", Kinds)})");
					continue;
				}
				marker.Transform = t;
				marker.Source = name;
				Place(marker, t);
				markers_.Add(marker);
			}

			if (profile.Loaded) {
				foreach (string line in profile.Section("markers")) {
					if (TryParseLine(line, out var marker)) {
						markers_.Add(marker);
					} else {
						Kino.Log.Warning($"[NatureFX] Bad [markers] line: {line}");
					}
				}
			}

			if (markers_.Count > 0) {
				var counts = markers_.GroupBy(m => m.Kind).Select(g => $"{g.Key} {g.Count()}");
				Kino.Log.Info($"[NatureFX] {markers_.Count} map markers ({string.Join(", ", counts)})");
			}
		}

		private void Place(Marker marker, Transform t) {
			var meshRenderer = t.GetComponent<MeshRenderer>();
			var meshFilter = t.GetComponent<MeshFilter>();

			if (meshRenderer != null) {
				// OBJ box marker: world bounds, orientation from the name keys, and keep it invisible
				var bounds = meshRenderer.bounds;
				marker.Position = bounds.center;
				marker.Size = bounds.size;
				marker.Rotation = Quaternion.Euler(marker.GetFloat("pitch", 0.0f), marker.GetFloat("yaw", 0.0f), 0.0f);
				if (meshRenderer.enabled) {
					meshRenderer.enabled = false;
					hidden_.Add(meshRenderer);
				}
				return;
			}

			marker.Position = t.position;
			marker.Rotation = t.rotation;
			marker.Size = t.lossyScale;
			marker.Component = FindComponent(t);
			if (meshFilter != null && meshFilter.sharedMesh != null) {
				marker.Surface = meshFilter.sharedMesh;
				var bounds = meshFilter.sharedMesh.bounds;
				marker.Position = t.TransformPoint(bounds.center);
				marker.Size = Vector3.Scale(bounds.size, t.lossyScale);
			}
		}

		private static Component FindComponent(Transform t) {
			return (Component)t.GetComponent<Light>()
			       ?? (Component)t.GetComponent<ReflectionProbe>()
			       ?? (Component)t.GetComponent<DecalProjector>()
			       ?? (Component)t.GetComponent<LocalVolumetricFog>()
			       ?? t.GetComponent<ParticleSystem>();
		}

		public void Restore() {
			foreach (var r in hidden_) {
				if (r != null) {
					r.enabled = true;
				}
			}
			hidden_.Clear();
		}

		// nfx_light_sodium__yaw-90__pitch-70  ->  kind light, preset sodium, yaw 90, pitch 70
		public static bool TryParseName(string objectName, out Marker marker) {
			marker = null;
			string name = CopySuffix.Replace(objectName, "");
			string body;
			if (name.StartsWith(MeshPrefix, StringComparison.Ordinal)) {
				body = name.Substring(MeshPrefix.Length);
			} else if (name.StartsWith(Prefix, StringComparison.Ordinal)) {
				body = name.Substring(Prefix.Length);
			} else {
				return false;
			}

			string[] parts = body.Split(new[] { "__" }, StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0 || !SplitKind(parts[0], out string kind, out string preset)) {
				return false;
			}

			marker = new Marker { Kind = kind, Preset = preset };
			for (int i = 1; i < parts.Length; ++i) {
				int dash = parts[i].IndexOf('-');
				if (dash <= 0) {
					marker.Params[parts[i]] = "on"; // bare flag, e.g. __shadow
					continue;
				}
				marker.Params[parts[i].Substring(0, dash)] = DecodeValue(parts[i].Substring(dash + 1));
			}
			return true;
		}

		// light_sodium = 120.5, 34.0, -88.2, yaw=90, size=4x0.3x2
		public static bool TryParseLine(string line, out Marker marker) {
			marker = null;
			int eq = line.IndexOf('=');
			if (eq <= 0 || !SplitKind(line.Substring(0, eq).Trim(), out string kind, out string preset)) {
				return false;
			}

			string[] fields = line.Substring(eq + 1).Split(',');
			if (fields.Length < 3) {
				return false;
			}
			var position = Vector3.zero;
			for (int i = 0; i < 3; ++i) {
				if (!float.TryParse(fields[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) {
					return false;
				}
				position[i] = v;
			}

			marker = new Marker { Kind = kind, Preset = preset, Position = position, Size = Vector3.one, Source = "[markers] " + line };
			for (int i = 3; i < fields.Length; ++i) {
				string field = fields[i].Trim();
				int keq = field.IndexOf('=');
				if (keq <= 0) {
					continue;
				}
				string key = field.Substring(0, keq).Trim();
				string value = field.Substring(keq + 1).Trim();
				if (string.Equals(key, "size", StringComparison.OrdinalIgnoreCase)) {
					marker.Size = ParseSize(value);
				} else {
					marker.Params[key] = value;
				}
			}
			marker.Rotation = Quaternion.Euler(marker.GetFloat("pitch", 0.0f), marker.GetFloat("yaw", 0.0f), 0.0f);
			return true;
		}

		private static bool SplitKind(string text, out string kind, out string preset) {
			int underscore = text.IndexOf('_');
			kind = (underscore < 0 ? text : text.Substring(0, underscore)).ToLowerInvariant();
			preset = underscore < 0 ? "" : text.Substring(underscore + 1);
			return Array.IndexOf(Kinds, kind) >= 0;
		}

		// Names cannot hold '.' or '-' safely: 0p5 = 0.5, m30 = -30
		private static string DecodeValue(string value) {
			if (!EncodedNumber.IsMatch(value)) {
				return value;
			}
			return value.Replace('p', '.').Replace('m', '-');
		}

		private static Vector3 ParseSize(string value) {
			string[] parts = value.Split('x');
			var size = Vector3.one;
			for (int i = 0; i < 3 && i < parts.Length; ++i) {
				if (float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) {
					size[i] = v;
				}
			}
			return size;
		}
	}
}

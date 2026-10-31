using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// Light markers (docs/map-authoring.md 6.1): nfx_light_<preset> gets an HDRP light of that preset, a disabled
	// Light the author set up keeps its own brightness, colour and shape. NatureFX switches them on by the sun's
	// elevation (each lamp a little apart, warming up over a second or two like a real lamp), flickers the old ones,
	// and keeps the cost bounded: only the 64 nearest the camera are drawn and the 8 nearest of those cast shadows.
	public class MapLights {
		private const int MaxDrawn = 64;
		private const int MaxShadowed = 8;
		private const float CullInterval = 0.25f;

		private enum When { Night, Dusk, Always }

		private class Preset {
			public LightType Type;
			public float Kelvin;
			public float Lumen;
			public float Range;
			public float Spot;      // full cone angle in degrees
			public float Pitch;     // degrees below the horizon when the marker says nothing (90 = straight down)
			public bool Shadow;
			public float WarmUp;    // seconds from off to full
			public Color Tint = Color.white; // instead of a colour temperature when Kelvin is 0
			public bool Blink;
			public Distribution Beam;
		}

		// light distributions (what an IES file would give): street lamps throw a long batwing along the road (the
		// lamp's local x, turned by yaw), tunnel lamps a broad even spread, floodlights a hot centre
		private enum Distribution { None, Street, Tunnel, Flood }

		private static readonly Dictionary<string, Preset> Presets = new() {
			["sodium"] = new Preset { Type = LightType.Spot, Kelvin = 2000, Lumen = 12000, Range = 35, Spot = 130, Pitch = 90, Shadow = true, WarmUp = 6.0f, Beam = Distribution.Street },
			["mercury"] = new Preset { Type = LightType.Spot, Kelvin = 4200, Lumen = 10000, Range = 35, Spot = 130, Pitch = 90, Shadow = true, WarmUp = 4.0f, Beam = Distribution.Street },
			["led_street"] = new Preset { Type = LightType.Spot, Kelvin = 4500, Lumen = 9000, Range = 35, Spot = 120, Pitch = 90, Shadow = true, WarmUp = 0.1f, Beam = Distribution.Street },
			["tunnel"] = new Preset { Type = LightType.Spot, Kelvin = 2000, Lumen = 4000, Range = 18, Spot = 150, Pitch = 90, Shadow = false, WarmUp = 3.0f, Beam = Distribution.Tunnel },
			// a vending machine's lit front: a wide spot out of the front instead of an area light (those do not
			// reach the ray traced lamps)
			["vending"] = new Preset { Type = LightType.Spot, Kelvin = 6000, Lumen = 1500, Range = 8, Spot = 160, Pitch = 10, Shadow = false, WarmUp = 0.1f },
			["floodlight"] = new Preset { Type = LightType.Spot, Kelvin = 5000, Lumen = 30000, Range = 70, Spot = 70, Pitch = 35, Shadow = true, WarmUp = 3.0f, Beam = Distribution.Flood },
			["beacon"] = new Preset { Type = LightType.Point, Kelvin = 0, Lumen = 800, Range = 15, Shadow = false, WarmUp = 0.05f, Tint = new Color(1.0f, 0.62f, 0.08f), Blink = true },
			["lantern"] = new Preset { Type = LightType.Point, Kelvin = 2200, Lumen = 300, Range = 8, Shadow = false, WarmUp = 0.2f }
		};

		private class Lamp {
			public Marker Marker;
			public Light Light;
			public HDAdditionalLightData Hd;
			public bool Created;     // NatureFX made it (destroyed on unload), else the author's disabled Light
			public When When;
			public float Threshold;  // sun elevation (degrees) below which it is on
			public float Flicker;
			public bool Blink;
			public bool WantsShadow;
			public float WarmUp;
			public float Seed;
			public float Level;      // 0 off .. 1 fully warmed up
			public bool Drawn;
			public bool Shadowed;
			public float Distance;
		}

		private readonly List<Lamp> lamps_ = new();
		private bool traced_;
		private readonly List<Lamp> sorted_ = new();
		private GameObject root_;
		private float nextCull_;
		private int shadowed_;
		private int drawn_;

		public int Count => lamps_.Count;
		// where lamps are on right now (MapParticles: moths gather there)
		public IReadOnlyList<Vector3> LitPositions => lit_;
		private readonly List<Vector3> lit_ = new();

		public void Load(MarkerScanner markers) {
			Clear();
			foreach (var m in markers.OfKind("light")) {
				var lamp = Make(m);
				if (lamp != null) {
					lamps_.Add(lamp);
				}
			}
			if (lamps_.Count > 0) {
				int own = lamps_.FindAll(l => !l.Created).Count;
				Kino.Log.Info($"[NatureFX] {lamps_.Count} map lights ({own} set up in the map, {lamps_.Count - own} from presets)");
			}
			nextCull_ = 0.0f;
		}

		public void Clear() {
			foreach (var l in lamps_) {
				if (l.Created) {
					if (l.Light != null) {
						Object.Destroy(l.Light.gameObject);
					}
				} else if (l.Light != null) {
					l.Light.enabled = false; // as the author left it
				}
			}
			lamps_.Clear();
			sorted_.Clear();
		}

		public void Destroy() {
			Clear();
			foreach (var c in cookies_.Values) {
				Object.Destroy(c);
			}
			cookies_.Clear();
			if (root_ != null) {
				Object.Destroy(root_);
			}
			root_ = null;
		}

		private Lamp Make(Marker m) {
			Presets.TryGetValue(m.Preset ?? "", out var preset);
			var lamp = new Lamp {
				Marker = m,
				When = ParseWhen(m.Get("when")),
				Flicker = Mathf.Clamp01(m.GetFloat("flicker", 0.0f)),
				Blink = preset != null && preset.Blink,
				WarmUp = preset?.WarmUp ?? 1.0f,
				Seed = Hash(m.Position)
			};
			// the same staggering as the emissive materials (NFX_MaterialEmission): a few degrees of sun apart
			lamp.Threshold = (lamp.When == When.Dusk ? 6.0f : -1.0f) + lamp.Seed * 4.0f;

			if (m.Component is Light own) {
				lamp.Light = own;
				lamp.Hd = own.GetComponent<HDAdditionalLightData>();
				lamp.WantsShadow = own.shadows != LightShadows.None && !string.Equals(m.Get("shadow"), "off", System.StringComparison.OrdinalIgnoreCase);
				own.enabled = false;
				return lamp;
			}
			if (preset == null) {
				if (!string.IsNullOrEmpty(m.Preset)) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: unknown light preset '{m.Preset}' (sodium, mercury, led_street, tunnel, vending, floodlight, beacon, lantern)");
				} else {
					Kino.Log.Warning($"[NatureFX] {m.Source}: a light marker needs a preset or a disabled Light on it");
				}
				return null;
			}

			if (root_ == null) {
				root_ = new GameObject("NatureFX.MapLights");
			}
			var go = new GameObject("NatureFX.Light " + m.Preset);
			go.transform.SetParent(root_.transform, false);
			go.transform.SetPositionAndRotation(m.Position, Direction(m, preset));
			var light = go.AddComponent<Light>();
			light.type = preset.Type;
			light.shadows = LightShadows.None;
			// adding the HDRP data resets the intensity, so everything is set after it
			var hd = go.AddComponent<HDAdditionalLightData>();
			float kelvin = m.GetFloat("k", preset.Kelvin);
			if (kelvin > 0.0f) {
				hd.SetColor(Color.white, kelvin);
			} else {
				hd.EnableColorTemperature(false);
				hd.SetColor(preset.Tint);
			}
			hd.SetIntensity(m.GetFloat("lm", preset.Lumen), LightUnit.Lumen);
			hd.SetRange(m.GetFloat("range", preset.Range));
			if (preset.Type == LightType.Spot) {
				hd.SetSpotAngle(Mathf.Clamp(m.GetFloat("spot", preset.Spot), 1.0f, 179.0f), 40.0f);
			}
			hd.shapeRadius = preset.Type == LightType.Spot ? 0.15f : 0.05f;
			if (preset.Beam != Distribution.None && !string.Equals(m.Get("ies"), "off", System.StringComparison.OrdinalIgnoreCase)) {
				hd.SetCookie(Cookie(preset.Beam));
			}
			hd.affectsVolumetric = true;
			string shadow = m.Get("shadow");
			lamp.WantsShadow = shadow != null ? !string.Equals(shadow, "off", System.StringComparison.OrdinalIgnoreCase) : preset.Shadow;
			light.enabled = false;
			lamp.Light = light;
			lamp.Hd = hd;
			lamp.Created = true;
			return lamp;
		}

		// yaw / pitch keys first, then a turned empty object's own rotation, else the preset's (down for street lamps)
		private static Quaternion Direction(Marker m, Preset preset) {
			bool keys = m.Get("yaw") != null || m.Get("pitch") != null;
			if (!keys && m.Transform != null && Quaternion.Angle(m.Rotation, Quaternion.identity) > 0.5f) {
				return m.Rotation;
			}
			return Quaternion.Euler(m.GetFloat("pitch", preset.Pitch), m.GetFloat("yaw", 0.0f), 0.0f);
		}

		// traced: the ray traced lamp shadows run (RayTracingEffects.LampShadowsTraced): every lamp near the camera
		// gets soft traced shadows there (LocalShadows.hlsl), so none of these keeps a shadow map
		public void Update(bool on, Light sun, Vector3 camera, float shafts, bool traced) {
			traced_ = traced;
			if (lamps_.Count == 0) {
				return;
			}
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			float dt = Time.unscaledDeltaTime;
			float t = Time.unscaledTime;

			foreach (var l in lamps_) {
				bool wanted = on && (l.When == When.Always || elevation < l.Threshold);
				l.Level = Mathf.MoveTowards(l.Level, wanted ? 1.0f : 0.0f, dt / Mathf.Max(0.05f, wanted ? l.WarmUp : 0.3f));
			}

			if (t >= nextCull_) {
				nextCull_ = t + CullInterval;
				Cull(camera);
				lit_.Clear();
				foreach (var l in lamps_) {
					if (l.Level > 0.5f) {
						lit_.Add(l.Marker.Position);
					}
				}
			}

			foreach (var l in lamps_) {
				if (l.Light == null) {
					continue;
				}
				bool visible = l.Drawn && l.Level > 0.0f;
				if (l.Light.enabled != visible) {
					l.Light.enabled = visible;
				}
				if (!visible || l.Hd == null) {
					continue;
				}
				// sodium and mercury lamps glow dim and red / blue first, then reach full brightness
				float level = l.Level * l.Level;
				if (l.Flicker > 0.0f) {
					float n = Mathf.PerlinNoise(t * 9.0f + l.Seed * 50.0f, l.Seed * 13.0f);
					level *= 1.0f - l.Flicker * Mathf.SmoothStep(0.0f, 1.0f, (n - 0.6f) / 0.15f) * 0.85f;
				}
				if (l.Blink) {
					level *= 0.1f + 0.9f * Mathf.Pow(Mathf.Max(0.0f, Mathf.Sin((t + l.Seed) * Mathf.PI * 2.4f)), 4.0f);
				}
				l.Hd.lightDimmer = level;
				// light in fog and rain at night, as strong as the headlights' (Volumetric headlight beams)
				l.Hd.volumetricDimmer = shafts > 0.0f ? shafts * 4.0f : 1.0f;
			}
		}

		// the nearest lamps that are on are drawn, the nearest of those with a shadow keep it
		private void Cull(Vector3 camera) {
			sorted_.Clear();
			foreach (var l in lamps_) {
				l.Distance = (l.Marker.Position - camera).sqrMagnitude;
				if (l.Level > 0.0f && l.Light != null) {
					sorted_.Add(l);
				} else {
					l.Drawn = false;
				}
			}
			sorted_.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			drawn_ = 0;
			shadowed_ = 0;
			foreach (var l in sorted_) {
				l.Drawn = drawn_ < MaxDrawn;
				if (!l.Drawn) {
					continue;
				}
				++drawn_;
				bool shadow = l.WantsShadow && shadowed_ < MaxShadowed && !traced_;
				if (shadow) {
					++shadowed_;
				}
				if (l.Shadowed != shadow || (l.Light.shadows != LightShadows.None) != shadow) {
					l.Shadowed = shadow;
					l.Light.shadows = shadow ? LightShadows.Soft : LightShadows.None;
				}
			}
		}

		// on or off, for this marker (MarkerOverlay)
		public string StatusOf(Marker m) {
			var l = lamps_.Find(x => x.Marker == m);
			if (l == null) {
				return "not made (see the log)";
			}
			return l.Level <= 0.0f ? $"off (lights at {(l.When == When.Always ? "always" : l.When == When.Dusk ? "dusk" : "night")})"
				: l.Drawn ? $"on {l.Level:P0}" + (l.Shadowed ? ", shadow map" : "") : "on, not drawn (beyond the 64 nearest)";
		}

		public string Describe() {
			int on = 0;
			foreach (var l in lamps_) {
				on += l.Level > 0.0f ? 1 : 0;
			}
			return traced_
				? string.Format(CultureInfo.InvariantCulture, "{0} ({1} on, {2} drawn, shadows ray traced)", lamps_.Count, on, drawn_)
				: string.Format(CultureInfo.InvariantCulture, "{0} ({1} on, {2} drawn, {3} with shadow maps)", lamps_.Count, on, drawn_, shadowed_);
		}

		private readonly Dictionary<Distribution, Texture2D> cookies_ = new();

		// The spot's cookie: intensity over the cone's cross section (u along the lamp's x, v along its y)
		private Texture2D Cookie(Distribution beam) {
			if (cookies_.TryGetValue(beam, out var tex)) {
				return tex;
			}
			const int size = 128;
			// grey RGBA: HDRP multiplies the light's colour by the cookie's rgb (a single channel texture tinted it red or cyan)
			tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "NatureFX.Beam " + beam, wrapMode = TextureWrapMode.Clamp };
			var pixels = new Color32[size * size];
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					float u = (x + 0.5f) / size * 2.0f - 1.0f;
					float v = (y + 0.5f) / size * 2.0f - 1.0f;
					float i;
					switch (beam) {
						case Distribution.Street: {
							// two lobes about 60° out along the road (batwing), narrow across it, a dimmer middle
							float along = Mathf.Abs(u);
							float lobes = Mathf.Exp(-Sq((along - 0.62f) / 0.28f)) + 0.45f * Mathf.Exp(-Sq(along / 0.35f));
							float across = Mathf.Exp(-Sq(v / 0.42f));
							i = Mathf.Clamp01(lobes * across * 1.1f);
							break;
						}
						case Distribution.Tunnel: {
							float r = Mathf.Max(Mathf.Abs(u) * 0.8f, Mathf.Abs(v));
							i = Mathf.Clamp01(1.0f - Mathf.SmoothStep(0.55f, 1.0f, r)) * 0.9f + 0.1f * (1.0f - r);
							break;
						}
						default: {
							float r = Mathf.Sqrt(u * u + v * v);
							i = Mathf.Clamp01(Mathf.Exp(-Sq(r / 0.35f)) + 0.35f * (1.0f - Mathf.SmoothStep(0.5f, 1.0f, r)));
							break;
						}
					}
					byte b = (byte)Mathf.Clamp(i * 255.0f, 0.0f, 255.0f);
					pixels[y * size + x] = new Color32(b, b, b, 255);
				}
			}
			tex.SetPixels32(pixels);
			tex.Apply(true, true);
			cookies_[beam] = tex;
			return tex;
		}

		private static float Sq(float x) {
			return x * x;
		}

		private static When ParseWhen(string text) {
			if (string.Equals(text, "dusk", System.StringComparison.OrdinalIgnoreCase)) {
				return When.Dusk;
			}
			if (string.Equals(text, "always", System.StringComparison.OrdinalIgnoreCase)) {
				return When.Always;
			}
			return When.Night;
		}

		private static float Hash(Vector3 p) {
			float h = Mathf.Sin(Vector3.Dot(new Vector3(Mathf.Floor(p.x), Mathf.Floor(p.y), Mathf.Floor(p.z)), new Vector3(12.9898f, 78.233f, 37.719f))) * 43758.5453f;
			return h - Mathf.Floor(h);
		}
	}
}

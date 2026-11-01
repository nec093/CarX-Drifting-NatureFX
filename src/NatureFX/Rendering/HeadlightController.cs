using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Car headlights (spot lights under heavy rigidbodies) get visible beams in volumetric fog, and (HeadlightType) a
	// real low beam in place of the game's round spot: the lamp's own colour and strength (halogen, HID, LED) and the
	// beam's pattern as a cookie: a sharp horizontal cut-off just below the horizon, brightest right under it in the
	// middle, spreading wide and dimmer to the sides and towards the car, with the kerb side's 15° rise (on the left: the
	// cars drive on the left in Japan) and a little stray light above. HDRP's lamp light (and with it the own engine's
	// exact lamp light, NFX_SunExact LampsExact) follows the cookie. The rear lights (red) keep the game's.
	public class HeadlightController {
		private const float ScanInterval = 5.0f;
		private const float MinCarMass = 400.0f;

		// 0 the game's own, 1 halogen, 2 HID (xenon), 3 LED. Per lamp: peak intensity of the low beam (cd, under the
		// cut-off; regulations ask some 10 to 40 thousand), colour temperature (K)
		private static readonly (float Candela, float Kelvin, string Name)[] Types = {
			(0.0f, 0.0f, "game"), (18000.0f, 3200.0f, "halogen"), (36000.0f, 4300.0f, "HID"), (30000.0f, 5800.0f, "LED"),
		};
		// the high beam: the same lamp, its light gathered on the axis without a cut-off (some 2.5 times the low
		// beam's peak; the regulations allow up to about 215 000 cd for all the car's high beams together)
		private const float HighBeamGain = 2.5f;
		private const float BeamAngle = 80.0f; // the cookie spans it: the low beam's wide near field included

		private struct Original {
			public float Intensity, Temperature, SpotAngle, Range, Inner;
			public bool UseTemperature;
			public Color Color;
			public Texture Cookie;
			public float Volumetric;
			public Quaternion LocalRotation;
		}

		private readonly Dictionary<HDAdditionalLightData, Original> originals_ = new();
		private readonly List<Light> lights_ = new();
		private readonly HashSet<Light> front_ = new();
		private readonly Dictionary<Light, Rigidbody> cars_ = new();
		private bool highBeam_;
		private float appliedAim_ = float.NaN;
		private Rigidbody appliedPlayer_;

		// the high beam of the player's car (the one nearest the camera): F7 or the Headlights card
		public bool HighBeam { get => highBeam_; set => highBeam_ = value; }
		public void ToggleHighBeam() => highBeam_ = !highBeam_;
		// retroreflection (docs/map-authoring.md 6.3): the headlights nearest the camera, for the shaders
		public const int MaxRetroLights = 4;
		private static readonly int HeadlightsId = Shader.PropertyToID("_NFX_Headlights");
		private static readonly int HeadlightDirsId = Shader.PropertyToID("_NFX_HeadlightDirs");
		private readonly Vector4[] positions_ = new Vector4[MaxRetroLights];
		private readonly Vector4[] directions_ = new Vector4[MaxRetroLights];
		private readonly List<(float Distance, Light Light)> nearest_ = new();
		private float nextScan_;
		private float applied_ = -1.0f;
		private int appliedType_ = -1;
		private readonly Dictionary<int, Texture2D> cookies_ = new();

		public string Status { get; private set; } = "the game's own";

		// retro: pass the headlights to the shaders for the map's retroreflective materials
		public void Update(ISettings s, bool retro) {
			float shafts = s.Enabled ? s.HeadlightShafts : 0.0f;
			int type = s.Enabled ? Mathf.Clamp(s.HeadlightType, 0, Types.Length - 1) : 0;

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan();
				applied_ = -1.0f;
				appliedType_ = -1;
			}

			UpdateRetroLights(retro);

			var player = PlayerCar();
			bool high = highBeam_;
			bool changed = !(Mathf.Approximately(shafts, applied_) && type == appliedType_ && Mathf.Approximately(s.HeadlightAim, appliedAim_)
			                 && player == appliedPlayer_ && high == appliedHigh_);
			applied_ = shafts;
			appliedType_ = type;
			appliedAim_ = s.HeadlightAim;
			appliedPlayer_ = player;
			appliedHigh_ = high;

			int front = 0;
			foreach (var kv in originals_) {
				var hd = kv.Key;
				if (hd == null) {
					continue;
				}
				var o = kv.Value;
				if (changed) {
					// 0 keeps the game's own value. 1.5 times the physical scattering at 1: at 4 the beams lit a bright
					// dome of the air right in front of the car, like a filter over the picture
					hd.volumetricDimmer = shafts > 0.0f ? shafts * 1.5f : o.Volumetric;
				}
				var light = hd.GetComponent<Light>();
				if (light == null || !front_.Contains(light)) {
					continue;
				}
				++front;
				if (type == 0) {
					if (changed) {
						Apply(light, hd, o);
						Beams.Remove(light);
					}
					continue;
				}
				cars_.TryGetValue(light, out var car);
				bool beamHigh = high && car != null && car == player;
				// every frame: the game sets its lights again now and then (a lamp switched on, its own presets): put
				// ours back as soon as it has, not at the next scan
				if (changed || !Holds(light, beamHigh ? HighCookie(type) : Cookie(type))) {
					ApplyBeam(light, hd, car, type, beamHigh, s.HeadlightAim);
				}
			}
			if (changed) {
				Status = type == 0 ? "the game's own"
					: $"{Types[type].Name}: {Types[type].Candela / 1000.0f:0} 000 cd a lamp, {Types[type].Kelvin:0} K, low beam aimed {s.HeadlightAim:0.0}° down ({front} lamps)" +
					  (high ? $"; your car's high beam on ({Types[type].Candela * HighBeamGain / 1000.0f:0} 000 cd)" : "");
			}
		}

		// what each headlight's beam is now, for the own engine's lamp light (LocalLights) and the particles
		// (ParticleLighting): 1 low beam, 2 high beam (none: a plain spot)
		public static readonly Dictionary<Light, int> Beams = new();

		// the lamp's turn about its axis from level, as NFX_BeamPattern rebuilds it (radians)
		public static float Roll(Light light) {
			var f = light.transform.forward;
			var level = Mathf.Abs(f.y) < 0.99f ? Vector3.up : Vector3.forward;
			var right0 = Vector3.Cross(level, f).normalized;
			var up0 = Vector3.Cross(f, right0);
			return Vector3.SignedAngle(up0, light.transform.up, f) * Mathf.Deg2Rad;
		}
		private readonly Dictionary<Light, (float Intensity, Quaternion Rotation)> held_ = new();

		private bool Holds(Light light, Texture cookie) {
			return held_.TryGetValue(light, out var h) && light.cookie == cookie && Mathf.Approximately(light.spotAngle, BeamAngle)
			       && Mathf.Abs(light.intensity - h.Intensity) <= h.Intensity * 0.01f && Quaternion.Angle(light.transform.localRotation, h.Rotation) < 0.05f;
		}

		private void ApplyBeam(Light light, HDAdditionalLightData hd, Rigidbody car, int type, bool beamHigh, float aim) {
			var t = Types[type];
			// the beam's axis from the car's body, not from however the game tilted the light: the low beam set a little
			// below level (the regulations' 1 % is 0.6°), the high beam level
			if (car != null && light.transform.parent != null) {
				var world = car.transform.rotation * Quaternion.Euler(beamHigh ? 0.0f : aim, 0.0f, 0.0f);
				light.transform.localRotation = Quaternion.Inverse(light.transform.parent.rotation) * world;
			}
			light.useColorTemperature = true;
			light.colorTemperature = t.Kelvin;
			// HID's lamps are a little blue-violet at the cut-off's edge (the cookie's fringe); the colour itself white
			light.color = Color.white;
			light.spotAngle = BeamAngle;
			light.innerSpotAngle = BeamAngle * 0.95f;
			light.range = 120.0f;
			hd.SetIntensity(t.Candela * (beamHigh ? HighBeamGain : 1.0f), LightUnit.Candela);
			hd.SetCookie(beamHigh ? HighCookie(type) : Cookie(type));
			hd.shapeRadius = 0.04f;
			held_[light] = (light.intensity, light.transform.localRotation);
			Beams[light] = beamHigh ? 2 : 1;
		}

		private bool appliedHigh_;

		// the car the camera follows: the heavy body nearest it
		private Rigidbody PlayerCar() {
			var cam = Camera.main;
			if (cam == null) {
				return null;
			}
			Rigidbody best = null;
			float bestDistance = float.MaxValue;
			foreach (var rb in cars_.Values) {
				if (rb == null) {
					continue;
				}
				float d = (rb.position - cam.transform.position).sqrMagnitude;
				if (d < bestDistance) {
					bestDistance = d;
					best = rb;
				}
			}
			return best;
		}

		// the high beam: the light gathered round the axis (a hot centre a few degrees across, a wider glow out to the
		// sides), no cut-off; the colour as the low beam's
		private Texture2D HighCookie(int type) {
			int key = type + 100;
			if (cookies_.TryGetValue(key, out var tex) && tex != null) {
				return tex;
			}
			const int size = 256;
			tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) {
				name = $"NatureFX.HighBeam.{Types[type].Name}", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
			};
			var px = new Color32[size * size];
			float half = Mathf.Tan(BeamAngle * 0.5f * Mathf.Deg2Rad);
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					float u = ((x + 0.5f) / size * 2.0f - 1.0f) * half, v = ((y + 0.5f) / size * 2.0f - 1.0f) * half;
					float h = Mathf.Atan(u) * Mathf.Rad2Deg, a = Mathf.Atan(v) * Mathf.Rad2Deg;
					float hot = Mathf.Exp(-Mathf.Pow(h / 5.0f, 2.0f) - Mathf.Pow(a / 2.5f, 2.0f));
					float glow = Mathf.Exp(-Mathf.Pow(h / 20.0f, 2.0f) - Mathf.Pow(a / 7.0f, 2.0f)) * 0.25f;
					// the road close in front still gets the low beam's share (both burn together)
					float near = a < -2.0f ? Mathf.Exp(-Mathf.Pow(h / 28.0f, 2.0f)) * 0.12f : 0.0f;
					float value = Mathf.Min(1.0f, hot + glow + near);
					px[y * size + x] = new Color(value, value, value, 1.0f);
				}
			}
			tex.SetPixels32(px);
			tex.Apply(true, false);
			cookies_[key] = tex;
			return tex;
		}

		private static void Apply(Light light, HDAdditionalLightData hd, Original o) {
			light.transform.localRotation = o.LocalRotation;
			light.useColorTemperature = o.UseTemperature;
			light.colorTemperature = o.Temperature;
			light.color = o.Color;
			light.spotAngle = o.SpotAngle;
			light.innerSpotAngle = o.Inner;
			light.range = o.Range;
			light.intensity = o.Intensity;
			light.cookie = o.Cookie;
		}

		// The low beam's pattern over the spot's square (u right, v up; tan of the angles across BeamAngle), relative to
		// its peak: under the cut-off (0.6° below the horizon; on the left, rising at 15° from the middle up to 1°
		// above) a hot spot a few degrees down, a wide fill that falls off to the sides, and the road close in front
		// dimmer (it is near: little light makes it bright); above it 1.5 % stray light. HID: a faint blue edge.
		private Texture2D Cookie(int type) {
			if (cookies_.TryGetValue(type, out var tex) && tex != null) {
				return tex;
			}
			const int size = 256;
			tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) {
				name = $"NatureFX.LowBeam.{Types[type].Name}", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
			};
			var px = new Color32[size * size];
			float half = Mathf.Tan(BeamAngle * 0.5f * Mathf.Deg2Rad);
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					float u = ((x + 0.5f) / size * 2.0f - 1.0f) * half, v = ((y + 0.5f) / size * 2.0f - 1.0f) * half;
					float h = Mathf.Atan(u) * Mathf.Rad2Deg, a = Mathf.Atan(v) * Mathf.Rad2Deg;
					// the cut-off: flat on the right, rising on the left (kerb side) by 15° up to 1°
					float cut = -0.6f + (h < 0.0f ? Mathf.Min(-h * Mathf.Tan(15.0f * Mathf.Deg2Rad), 1.6f) : 0.0f);
					float below = cut - a; // degrees under the cut-off
					float edge = Mathf.Clamp01(below / 0.35f + 0.5f); // its sharpness: a fraction of a degree
					float hot = Mathf.Exp(-Mathf.Pow(h / 9.0f, 2.0f) - Mathf.Pow((a + 2.0f) / 2.5f, 2.0f));
					float fill = Mathf.Exp(-Mathf.Pow(h / 28.0f, 2.0f)) * Mathf.Exp(-Mathf.Max(0.0f, below - 1.0f) / 9.0f) * 0.35f;
					float value = Mathf.Lerp(0.015f, Mathf.Min(1.0f, hot + fill), edge);
					var c = new Color(value, value, value, 1.0f);
					if (type == 2) {
						// xenon's blue-violet fringe right at the cut-off
						float fringe = Mathf.Exp(-Mathf.Pow(below / 0.25f, 2.0f)) * 0.25f * Mathf.Exp(-Mathf.Pow(h / 30.0f, 2.0f));
						c = new Color(value * (1.0f - fringe * 0.5f), value * (1.0f - fringe * 0.7f), Mathf.Min(1.0f, value + fringe * 0.3f), 1.0f);
					}
					px[y * size + x] = c;
				}
			}
			tex.SetPixels32(px);
			tex.Apply(true, false);
			cookies_[type] = tex;
			return tex;
		}

		// xyz position (absolute world), w intensity (cd, 0 = none); direction xyz, w cosine of the half cone angle
		private void UpdateRetroLights(bool on) {
			nearest_.Clear();
			var cam = Camera.main;
			if (on && cam != null) {
				var c = cam.transform.position;
				foreach (var l in lights_) {
					if (l != null && l.isActiveAndEnabled && l.intensity > 0.0f) {
						nearest_.Add(((l.transform.position - c).sqrMagnitude, l));
					}
				}
				nearest_.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			}
			for (int i = 0; i < MaxRetroLights; ++i) {
				if (i < nearest_.Count) {
					var l = nearest_[i].Light;
					var p = l.transform.position;
					var d = l.transform.forward;
					var color = l.color;
					float tint = Mathf.Max(color.r, Mathf.Max(color.g, color.b));
					positions_[i] = new Vector4(p.x, p.y, p.z, l.intensity * tint);
					directions_[i] = new Vector4(d.x, d.y, d.z, Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad));
				} else {
					positions_[i] = Vector4.zero;
					directions_[i] = new Vector4(0.0f, 0.0f, 1.0f, 1.0f);
				}
			}
			Shader.SetGlobalVectorArray(HeadlightsId, positions_);
			Shader.SetGlobalVectorArray(HeadlightDirsId, directions_);
		}

		public void Restore() {
			foreach (var kv in originals_) {
				if (kv.Key == null) {
					continue;
				}
				kv.Key.volumetricDimmer = kv.Value.Volumetric;
				var light = kv.Key.GetComponent<Light>();
				if (light != null && front_.Contains(light)) {
					Apply(light, kv.Key, kv.Value);
				}
			}
			originals_.Clear();
			front_.Clear();
			cars_.Clear();
			held_.Clear();
			Beams.Clear();
			appliedType_ = -1;
		}

		private void Scan() {
			// forget lights of cars that left
			var dead = new List<HDAdditionalLightData>();
			foreach (var kv in originals_) {
				if (kv.Key == null) {
					dead.Add(kv.Key);
				}
			}
			foreach (var d in dead) {
				originals_.Remove(d);
			}
			lights_.RemoveAll(l => l == null);
			front_.RemoveWhere(l => l == null);
			var goneCars = new List<Light>();
			foreach (var kv in cars_) {
				if (kv.Key == null) {
					goneCars.Add(kv.Key);
				}
			}
			foreach (var l in goneCars) {
				cars_.Remove(l);
			}

			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (light.type != LightType.Spot) {
					continue;
				}
				var rb = light.GetComponentInParent<Rigidbody>();
				if (rb == null || rb.mass < MinCarMass) {
					continue;
				}
				var hd = light.GetComponent<HDAdditionalLightData>();
				if (hd != null && !originals_.ContainsKey(hd)) {
					originals_[hd] = new Original {
						Intensity = light.intensity, Temperature = light.colorTemperature, UseTemperature = light.useColorTemperature, Color = light.color,
						SpotAngle = light.spotAngle, Inner = light.innerSpotAngle, Range = light.range, Cookie = light.cookie, Volumetric = hd.volumetricDimmer,
						LocalRotation = light.transform.localRotation,
					};
					cars_[light] = rb;
					lights_.Add(light);
					// a headlight: pointing the car's way, not red (the rear lights and brake lights are)
					var c = light.color;
					bool red = c.r > 0.5f && c.g < c.r * 0.6f;
					if (!red && Vector3.Dot(light.transform.forward, rb.transform.forward) > 0.5f) {
						front_.Add(light);
					}
				}
			}
		}
	}
}

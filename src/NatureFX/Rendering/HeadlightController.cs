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
		// the game makes or switches on its cars' lights again when a run starts: found within a second (at 5 s they
		// showed the game's own beam for a while, as if the settings had been reset)
		private const float ScanInterval = 1.0f;
		private readonly HashSet<int> loggedLoose_ = new();

		private static string Path(Transform t) {
			return t.parent != null ? $"{t.parent.name}/{t.name}" : t.name;
		}
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
			// HDRP's own copy of the intensity (in its unit): SetIntensity changes both, and HDRP writes it back over
			// Light.intensity whenever it runs UpdateLightIntensity (Awake / OnEnable, a shape setter: a car switched
			// back on), so with the legacy value alone restored the game's lights came back at NatureFX's brightness
			public LightUnit Unit;
			public float HdIntensity;
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
		// the low beam's side: false Japan (driving on the left: the cut-off rises on the left), true Europe / US
		private static bool rightTraffic_;
		private bool appliedRight_;
		private readonly Dictionary<int, Texture2D> cookies_ = new();

		public string Status { get; private set; } = "the game's own";

		// ---- the game sets its headlights again every frame while the car is driven, after NatureFX's Update (its own
		// LateUpdate): the frame was drawn with the game's beam. Just before HDRP renders (every script done), ours is put
		// back on each lamp the game changed.
		private bool hooked_;
		private int lateType_;
		private float lateAim_;
		private int lateFrame_ = -1;
		private int lateFixes_;

		private void Hook() {
			if (!hooked_) {
				hooked_ = true;
				UnityEngine.Rendering.RenderPipelineManager.beginContextRendering += BeforeRendering;
			}
		}

		public void Unhook() {
			if (hooked_) {
				hooked_ = false;
				UnityEngine.Rendering.RenderPipelineManager.beginContextRendering -= BeforeRendering;
			}
		}

		private void BeforeRendering(UnityEngine.Rendering.ScriptableRenderContext context, List<Camera> cameras) {
			if (lateType_ == 0 || lateFrame_ == Time.frameCount) {
				return;
			}
			lateFrame_ = Time.frameCount;
			var player = PlayerCar();
			foreach (var kv in originals_) {
				var hd = kv.Key;
				if (hd == null) {
					continue;
				}
				var light = hd.GetComponent<Light>();
				if (light == null || !front_.Contains(light)) {
					continue;
				}
				cars_.TryGetValue(light, out var car);
				bool beamHigh = highBeam_ && car != null && car == player;
				if (!Holds(light, beamHigh ? HighCookie(lateType_) : Cookie(lateType_), lateType_)) {
					ApplyBeam(light, hd, car, lateType_, beamHigh, lateAim_);
					if (lateFixes_ < 5) {
						++lateFixes_;
						KSL.API.Kino.Log.Info($"[NatureFX] Headlights: '{Path(light.transform)}' changed by the game after NatureFX's update; ours put back before rendering");
					}
				}
			}
		}

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
			kelvin_ = s.HeadlightKelvin >= 2700.0f ? Mathf.Min(s.HeadlightKelvin, 8000.0f) : 0.0f;
			rightTraffic_ = s.HeadlightStandard == 1;
			bool changed = !(Mathf.Approximately(shafts, applied_) && type == appliedType_ && rightTraffic_ == appliedRight_ && Mathf.Approximately(s.HeadlightAim, appliedAim_)
			                 && Mathf.Approximately(kelvin_, appliedKelvin_)
			                 && player == appliedPlayer_ && high == appliedHigh_);
			applied_ = shafts;
			appliedType_ = type;
			appliedRight_ = rightTraffic_;
			appliedAim_ = s.HeadlightAim;
			appliedKelvin_ = kelvin_;
			appliedPlayer_ = player;
			appliedHigh_ = high;

			lateType_ = type;
			lateAim_ = s.HeadlightAim;
			if (type != 0) {
				Hook();
			}
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
				if (changed || !Holds(light, beamHigh ? HighCookie(type) : Cookie(type), type)) {
					ApplyBeam(light, hd, car, type, beamHigh, s.HeadlightAim);
				}
			}
			if (changed) {
				Status = type == 0 ? "the game's own"
					: $"{Types[type].Name}: {Types[type].Candela / 1000.0f:0} 000 cd a lamp, {Kelvin(type):0} K, low beam aimed {s.HeadlightAim:0.0}° down ({front} lamps)" +
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
		private readonly List<Light> goneLights_ = new();

		// what the game set again on a lamp of ours (the first few times, in the log: the settings seemed to go back after
		// a while when only the intensity, cookie, angle and aim were watched)
		private int heldLogged_;

		private bool Holds(Light light, Texture cookie, int type) {
			if (!held_.TryGetValue(light, out var h)) {
				return false;
			}
			string changed = light.cookie != cookie ? "cookie"
				: !Mathf.Approximately(light.spotAngle, BeamAngle) ? "spot angle"
				: Mathf.Abs(light.intensity - h.Intensity) > h.Intensity * 0.01f ? $"intensity {h.Intensity:0} -> {light.intensity:0}"
				: Quaternion.Angle(light.transform.localRotation, h.Rotation) >= 0.05f ? "aim"
				: !light.useColorTemperature || Mathf.Abs(light.colorTemperature - Kelvin(type)) > 1.0f ? $"colour temperature -> {light.colorTemperature:0} K"
				: light.color != Color.white ? "colour"
				: !Mathf.Approximately(light.range, 120.0f) ? $"range -> {light.range:0}"
				: null;
			if (changed == null) {
				return true;
			}
			if (heldLogged_ < 12) {
				++heldLogged_;
				KSL.API.Kino.Log.Info($"[NatureFX] Headlights: the game set '{Path(light.transform)}' again ({changed}); ours put back");
			}
			return false;
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
			light.colorTemperature = Kelvin(type);
			// HID's lamps are a little blue-violet at the cut-off's edge (the cookie's fringe); the colour itself white
			light.color = Color.white;
			light.spotAngle = BeamAngle;
			light.innerSpotAngle = BeamAngle * 0.95f;
			light.range = 120.0f;
			hd.SetIntensity(t.Candela * (beamHigh ? HighBeamGain : 1.0f), LightUnit.Candela);
			hd.SetCookie(beamHigh ? HighCookie(type) : Cookie(type));
			hd.shapeRadius = 0.04f;
			held_[light] = (light.intensity, light.transform.localRotation);
			// 1 the low beam (driving on the left), 3 the same mirrored (on the right), 2 the high beam (NFX_BeamPattern)
			Beams[light] = beamHigh ? 2 : rightTraffic_ ? 3 : 1;
		}

		private bool appliedHigh_;
		// the user's colour temperature (K), 0: the type's own
		private float kelvin_, appliedKelvin_ = -1.0f;
		private float Kelvin(int type) => kelvin_ > 0.0f ? kelvin_ : Types[type].Kelvin;

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
			// the unit first (its setter converts the value), then the value in it; Light.intensity (candela, what the
			// light loop reads) is set again below in any case
			if (hd != null) {
				hd.lightUnit = o.Unit;
				hd.intensity = o.HdIntensity;
			}
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
			int key = type + (rightTraffic_ ? 100 : 0);
			if (cookies_.TryGetValue(key, out var tex) && tex != null) {
				return tex;
			}
			// fine enough for a projector's cut-off and its colour fringe (a tenth of a degree: 256 texels over 80° drew it
			// as a third of a degree, the fringe lost)
			int size = type >= 2 ? 1024 : 512;
			tex = new Texture2D(size, size, TextureFormat.RGBA32, true, true) {
				name = $"NatureFX.LowBeam.{Types[type].Name}{(rightTraffic_ ? ".right" : "")}", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
			};
			var px = new Color32[size * size];
			float half = Mathf.Tan(BeamAngle * 0.5f * Mathf.Deg2Rad);
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					float u = ((x + 0.5f) / size * 2.0f - 1.0f) * half, v = ((y + 0.5f) / size * 2.0f - 1.0f) * half;
					float h = Mathf.Atan(u) * Mathf.Rad2Deg, a = Mathf.Atan(v) * Mathf.Rad2Deg;
					// driving on the right: the same pattern mirrored (the kerb on the right)
					if (rightTraffic_) {
						h = -h;
					}
					// the cut-off: flat on the right, rising on the left (kerb side) by 15° up to 1°
					float cut = -0.6f + (h < 0.0f ? Mathf.Min(-h * Mathf.Tan(15.0f * Mathf.Deg2Rad), 1.6f) : 0.0f);
					float below = cut - a; // degrees under the cut-off
					// its sharpness: a reflector's (halogen) about half a degree, a projector lens's (HID, LED) a quarter or less
					float edgeWidth = type == 2 ? 0.25f : type == 3 ? 0.18f : 0.5f;
					float edge = Mathf.Clamp01(below / edgeWidth + 0.5f);
					// the peak as the regulations measure it (ECE R149 class B: the 75R / 50V points, 0.5 to 1.5° down, 25
					// to 75 m ahead), falling off fast above it and slower below
					float spread = a < -1.2f ? 2.2f : 1.2f;
					// the hot spot a little to the kerb side (Japan's measuring points: the brightest some 1 to 2° left of the
					// middle, under the cut-off), so the near verge and the lane are lit more than the oncoming lane
					float hot = Mathf.Exp(-Mathf.Pow((h + 1.5f) / 9.0f, 2.0f) - Mathf.Pow((a + 1.2f) / spread, 2.0f));
					// steeply down (the road a few metres ahead) a real low beam sends little: under 2 % of its peak 8° down,
					// so the road just in front is no brighter than the road 30 m on (with 5 % it was a white blob at night);
					// to the sides it fades out before the spot's edge, which drew a round rim on the verges
					float fill = Mathf.Exp(-Mathf.Pow(h / 22.0f, 2.0f)) * Mathf.Exp(-Mathf.Max(0.0f, below - 1.0f) / 2.0f) * 0.25f
					             * Mathf.Clamp01((38.0f - Mathf.Sqrt(h * h + a * a)) / 8.0f);
					float value = Mathf.Lerp(0.015f, Mathf.Min(1.0f, hot + fill), edge);
					var c = new Color(value, value, value, 1.0f);
					if (type >= 2) {
						// a projector lens (HID and LED) splits the cut-off's edge by colour (the blue bends more): a thin
						// blue-violet line just above it, a faint amber one just under it, as bright as the beam is at the
						// edge there (the hot spot's side bright, the far sides faint). Xenon's violet and strongest; an LED's
						// a cooler cyan-blue, thinner and fainter
						bool xenon = type == 2;
						float level = Mathf.Min(1.0f, hot + fill);
						float across = Mathf.Exp(-Mathf.Pow(h / 30.0f, 2.0f)) * level * (xenon ? 0.55f : 0.35f);
						float blueWidth = xenon ? 0.13f : 0.09f;
						float blue = Mathf.Exp(-Mathf.Pow((below + blueWidth * 0.8f) / blueWidth, 2.0f)) * across;
						float amber = Mathf.Exp(-Mathf.Pow((below - 0.18f) / (xenon ? 0.14f : 0.1f), 2.0f)) * across * (xenon ? 0.5f : 0.3f);
						var fringe = xenon ? new Color(0.55f, 0.35f, 1.0f) : new Color(0.3f, 0.6f, 1.0f);
						c = new Color(Mathf.Min(1.0f, value * (1.0f - amber * 0.1f) + blue * fringe.r + amber * 0.9f),
						              Mathf.Min(1.0f, value * (1.0f - amber * 0.35f) + blue * fringe.g + amber * 0.55f),
						              Mathf.Min(1.0f, value * (1.0f - amber * 0.8f) + blue * fringe.b), 1.0f);
					}
					px[y * size + x] = c;
				}
			}
			tex.SetPixels32(px);
			tex.Apply(true, false);
			cookies_[key] = tex;
			return tex;
		}

		// xyz position (absolute world), w intensity (cd, 0 = none); direction xyz, w cosine of the half cone angle
		private void UpdateRetroLights(bool on) {
			nearest_.Clear();
			var cam = Camera.main;
			if (on && cam != null) {
				var c = cam.transform.position;
				// the front lights where they are known: from a chase camera the nearest lamps are the own car's tail
				// lights, which would have sent the markings' light back in red
				bool frontOnly = front_.Count > 0;
				foreach (var l in lights_) {
					if (l != null && l.isActiveAndEnabled && l.intensity > 0.0f && (!frontOnly || front_.Contains(l))) {
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

		// The cookie textures (mod unload); the lights themselves are put back by Restore
		public void DestroyCookies() {
			foreach (var kv in cookies_) {
				if (kv.Value != null) {
					Object.Destroy(kv.Value);
				}
			}
			cookies_.Clear();
		}

		public void Restore() {
			lateType_ = 0;
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
			// the beams and the held values too: keyed by lights that are gone, they only grew
			goneLights_.Clear();
			foreach (var kv in Beams) {
				if (kv.Key == null) {
					goneLights_.Add(kv.Key);
				}
			}
			foreach (var kv in held_) {
				if (kv.Key == null && !goneLights_.Contains(kv.Key)) {
					goneLights_.Add(kv.Key);
				}
			}
			foreach (var l in goneLights_) {
				Beams.Remove(l);
				held_.Remove(l);
			}
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
				// the car's body: the first rigid body up the hierarchy heavy enough for a car (a lamp under a light part
				// with a body of its own, a bumper or a light pod, was passed over: the game's own beam when driving)
				Rigidbody rb = null;
				foreach (var candidate in light.GetComponentsInParent<Rigidbody>(true)) {
					if (candidate.mass >= MinCarMass) {
						rb = candidate;
						break;
					}
				}
				// a lamp the game keeps apart from the car's body (a view's own headlights, on the camera's rig): the car it
				// sits in front of and points along, the player's within 8 m
				if (rb == null) {
					var player = PlayerCar();
					if (player != null && (light.transform.position - player.position).sqrMagnitude < 64.0f
					    && Vector3.Dot(light.transform.forward, player.transform.forward) > 0.5f) {
						rb = player;
						if (loggedLoose_.Add(light.GetInstanceID())) {
							KSL.API.Kino.Log.Info($"[NatureFX] Headlights: '{Path(light.transform)}' is not on the car's body; taken as {player.name}'s");
						}
					}
				}
				if (rb == null || rb.mass < MinCarMass) {
					// a spot near the player's car that is not taken: in the log once (the game's own beam showed when driving)
					var near = PlayerCar();
					if (near != null && (light.transform.position - near.position).sqrMagnitude < 64.0f && loggedLoose_.Add(-light.GetInstanceID())) {
						KSL.API.Kino.Log.Info($"[NatureFX] Headlights: spot '{Path(light.transform)}' by {near.name} not taken (body {(rb != null ? $"{rb.name} {rb.mass:0} kg" : "none")}, " +
						                      $"facing {Vector3.Dot(light.transform.forward, near.transform.forward):0.00})");
					}
					continue;
				}
				var hd = light.GetComponent<HDAdditionalLightData>();
				if (hd != null && !originals_.ContainsKey(hd)) {
					originals_[hd] = new Original {
						Intensity = light.intensity, Temperature = light.colorTemperature, UseTemperature = light.useColorTemperature, Color = light.color,
						SpotAngle = light.spotAngle, Inner = light.innerSpotAngle, Range = light.range, Cookie = light.cookie, Volumetric = hd.volumetricDimmer,
						LocalRotation = light.transform.localRotation, Unit = hd.lightUnit, HdIntensity = hd.intensity,
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

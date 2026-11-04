using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using NatureFX.Nature;

namespace NatureFX.Rendering {
	// Time of day: drives the track's main directional light (rotation, intensity, colour temperature)
	// and its volumetric dimmer for light shafts. Everything is restored when the override is turned off.
	public class SunController {
		private struct Original {
			public Quaternion Rotation;
			public float Intensity;
			public float Temperature;
			public bool UseTemperature;
			public float VolumetricDimmer;
			public float AngularDiameter;
			public Color Color;
		}

		private Light sun_;
		private HDAdditionalLightData hdSun_;
		private Original original_;
		private bool applied_;
		private bool sizeApplied_;

		public Light Sun => sun_;
		// the track's daylight sun as the sun above the air (Sky.hlsli: the atmosphere colours and dims it itself)
		public static float TopOfAtmosphereLux { get; private set; }
		// The own engine's light (sky, clouds, fog, traced shadows): the sun, or after dusk the moon. Towards it, its
		// illuminance at the ground (rgb lux), above the air (lux), its angular radius (rad), and the sun's own
		// elevation sine (the stars come out by it)
		public static Vector3 SkyLightDir { get; private set; } = Vector3.up;
		public static Vector3 SkyLightGround { get; private set; }
		public static float SkyLightAbove { get; private set; }
		public static float SkyLightRadius { get; private set; } = 0.0047f;
		public static bool SkyLightIsMoon { get; private set; }
		public static float RealSunY { get; private set; } = 1.0f;
		public static Light SkyLight { get; private set; }

		// Call after each scene load: tracks bring their own sun.
		public void Refresh() {
			Restore();
			sun_ = FindSun();
			hdSun_ = sun_ != null ? sun_.GetComponent<HDAdditionalLightData>() : null;
			if (sun_ == null) {
				return;
			}

			original_ = new Original {
				Rotation = sun_.transform.rotation,
				Intensity = sun_.intensity,
				Temperature = sun_.colorTemperature,
				UseTemperature = sun_.useColorTemperature,
				VolumetricDimmer = hdSun_ != null ? hdSun_.volumetricDimmer : 1.0f,
				AngularDiameter = hdSun_ != null ? hdSun_.angularDiameter : 0.5f,
				Color = sun_.color
			};
			// the track's units, before the map's lamps are made from real values (MapLights, after this on a rescan)
			Units.FromSun(original_.Intensity);
		}

		public void Update(ISettings s) {
			if (sun_ == null) {
				return;
			}
			moonShadowsTraced_ = true;
			// (HDRP's soft shadow maps are gone: the traced shadows have their own softness)
			if (sizeApplied_ && hdSun_ != null) {
				hdSun_.angularDiameter = original_.AngularDiameter;
				sizeApplied_ = false;
			}

			// the track's sun raised to real sunshine (Units): the lamps are in real units already
			Units.FromSun(original_.Intensity > 0.0f ? original_.Intensity : sun_.intensity);
			physical_ = s.Enabled ? Units.PhysicalScale : 1.0f;
			TopOfAtmosphereLux = (original_.Intensity > 0.0f ? original_.Intensity : sun_.intensity) * physical_ * (s.SunOverride ? s.SunIntensityScale : 1.0f);
			float cloud = Weather.Current.Cloud;
			// under the own engine's cloud shadows the clouds darken the sun where they are, not all of it
			float cloudDim = CloudController.NativeShadowsActive ? 1.0f : 1.0f - 0.75f * cloud;
			if (!s.Enabled || (!s.SunOverride && cloud < 0.01f)) {
				RestoreTimeOfDay();
				// the track's sun as it is, at real strength while NatureFX is on (Units)
				sun_.intensity = original_.Intensity * physical_;
				UpdateSkyLight(cloud);
				return;
			}

			if (!s.SunOverride) {
				// weather only: keep the track's sun, dim it under clouds. What the time of day override set (the moon,
				// the sun's shadows off at night, its volumetric dimmer) goes back first: switching the override off
				// under clouds left the moon on and the sun without shadows
				UpdateMoon(0.0f, 0.0f, 0.0f, 0.0f, 0.0f);
				SetSunShadows(true);
				if (hdSun_ != null) {
					hdSun_.volumetricDimmer = original_.VolumetricDimmer;
				}
				sun_.transform.rotation = original_.Rotation;
				sun_.intensity = original_.Intensity * physical_ * cloudDim;
				sun_.colorTemperature = original_.Temperature;
				sun_.color = original_.Color;
				sun_.useColorTemperature = original_.UseTemperature;
				applied_ = true;
				UpdateSkyLight(cloud);
				return;
			}

			// Sun path: elevation follows a sine over the day (negative at night), heading turns 15 deg per hour
			// around the track's own sun heading so noon keeps the original shading direction.
			float elevation = s.SunMaxElevation * Mathf.Sin(Mathf.PI * (s.TimeOfDay - 6.0f) / 12.0f);
			float baseHeading = original_.Rotation.eulerAngles.y;
			float heading = baseHeading + s.SunAzimuthOffset + (s.TimeOfDay - 12.0f) * 15.0f;

			sun_.transform.rotation = Quaternion.Euler(Mathf.Max(elevation, -10.0f), heading, 0.0f);
			float daylight = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(-2.0f, 10.0f, elevation));
			// below the horizon the sun gives no light (a directional light ignores the horizon and lit the scene from
			// under the ground at night)
			sun_.intensity = original_.Intensity * physical_ * s.SunIntensityScale * daylight * cloudDim;
			{
				// phase 3: the sun light as the own engine's atmosphere lets it through (Sky.hlsli): the same air that
				// colours the sky reddens and dims the sun, instead of a colour temperature by elevation
				var cam = Camera.main;
				float aerosols = 1.0f + 2.0f * cloud + 2.0f * Weather.Current.Rain;
				var t = AtmosphereTransmittance(cam != null ? Mathf.Max(1.0f, cam.transform.position.y) : 1.0f, elevation, aerosols);
				float peak = Mathf.Max(t.x, Mathf.Max(t.y, t.z));
				float disc = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(-0.5f, 0.5f, elevation)); // the disc sinking
				sun_.useColorTemperature = false;
				sun_.color = peak > 1e-6f ? new Color(t.x / peak, t.y / peak, t.z / peak) : Color.black;
				sun_.intensity = TopOfAtmosphereLux * peak * disc * cloudDim;
			}

			// Night: a separate moon light high on the opposite side. The sun stays below the horizon so the
			// physically based sky goes dark; only one directional light casts shadows, so they swap.
			float night = s.Moonlight ? Mathf.InverseLerp(-2.0f, -12.0f, elevation) : 0.0f;
			// moonlight in the air is faint: the eye's adaptation would otherwise turn a night sky into a bright haze
			// the moon's strength: physical at 1, brighter for those who want to see more at night
			UpdateMoon(night, Mathf.Clamp(-elevation * 0.8f, 20.0f, 65.0f), heading + 180.0f, s.SunIntensityScale * cloudDim * Mathf.Clamp(s.MoonBrightness, 0.001f, 0.1f), 0.0f);
			SetSunShadows(night < 0.5f);

			if (hdSun_ != null) {
				// HDRP's volumetric fog is off: its sun lights none
				hdSun_.volumetricDimmer = 0.0f;
			}
			applied_ = true;
			UpdateSkyLight(cloud);
		}

		// which light the own engine follows, and its strength above the air (what the air lets through, undone)
		private void UpdateSkyLight(float cloud) {
			var cam = Camera.main;
			float height = cam != null ? Mathf.Max(1.0f, cam.transform.position.y) : 1.0f;
			float aerosols = 1.0f + 2.0f * cloud + 2.0f * Weather.Current.Rain;
			var toSun = -sun_.transform.forward;
			RealSunY = toSun.y;
			// the sky goes by the sun through the twilight: the air it still lights stays bright well below the horizon
			// (civil twilight to -6°). It went to the moon at -2°, when the moon was only just rising in strength: the
			// sky and all the light it gave (a garage's skylights) went out at once. Now the moon takes over around -7°,
			// where its light and the twilight's are about even; a degree apart both ways so it does not flip back and forth.
			float below = skyFromMoon_ ? MoonSkyBack : MoonSkyFrom;
			bool moon = moon_ != null && moon_.isActiveAndEnabled && moon_.intensity > 0.0f && toSun.y < Mathf.Sin(below * Mathf.Deg2Rad);
			skyFromMoon_ = moon;
			var light = moon ? moon_ : sun_;
			var dir = -light.transform.forward;
			var c = light.color.linear * light.intensity;
			if (light.useColorTemperature) {
				c *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
			}
			SkyLight = light;
			SkyLightIsMoon = moon;
			// OwnLighting.Sun: the light the own engine follows lights nothing in HDRP (the composite adds it); the other
			// keeps HDRP's own handling
			SetHdrpLighting(hdSun_, !(OwnLighting.Sun && !moon));
			SetHdrpLighting(hdMoon_, !(OwnLighting.Sun && moon));
			SkyLightDir = dir;
			SkyLightGround = new Vector3(c.r, c.g, c.b);
			SkyLightRadius = 0.0047f; // the sun's and the moon's are nearly the same (0.53 degrees across)
			float above;
			if (moon) {
				var t = AtmosphereTransmittance(height, Mathf.Asin(Mathf.Clamp(dir.y, -1.0f, 1.0f)) * Mathf.Rad2Deg, aerosols);
				float lum = Mathf.Max(0.2126f * t.x + 0.7152f * t.y + 0.0722f * t.z, 0.02f);
				above = (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / lum;
			} else {
				above = TopOfAtmosphereLux;
			}
			HandOver(moon, above);
			SkyLightAboveRaw = above;
			SkyLightAbove = above * SkyScale;
		}

		// ---- the sky's hand-over between the sun's twilight and the moon
		// One light makes the own engine's sky. At a hand-over NativeEngine.TraceSky keeps the sky of the last frame
		// before it and eases the new one in over some seconds (the screen's sky, its light, HDRP's cube and the sky in
		// the reflections together). A new generation at each hand-over.
		public static float SkyLightAboveRaw { get; private set; }
		public static float SkyScale => 1.0f;
		public static int SkyGeneration { get; private set; }
		private static bool lastMoon_;
		// the sine of the elevation where the sun's disc has sunk whole (its light dimmed to nothing, -0.5°): the engine
		// counts the sun as up above it
		public static readonly float DiscGoneY = Mathf.Sin(-0.6f * Mathf.Deg2Rad);

		private static float handOverAt_ = -1000.0f;

		private static void HandOver(bool moon, float above) {
			if (moon != lastMoon_) {
				lastMoon_ = moon;
				++SkyGeneration;
				handOverAt_ = Time.unscaledTime;
			}
		}

		// AtmospherePass's sky probe readings (kept for the debug page; the hand-over no longer needs them)
		public static void SkyMeasured(int generation, float reading, float rawAbove, float scale) {
		}

		// sun elevations (°) where the sky's light goes to the moon, and back to the sun
		private const float MoonSkyFrom = -7.5f, MoonSkyBack = -6.5f;
		private bool skyFromMoon_;

		private GameObject moonObject_;
		private Light moon_;
		private HDAdditionalLightData hdMoon_;
		private bool sunShadowsOff_;
		private bool moonShadowsTraced_, moonShadowsOn_;  // the moon's shadow map: off while the own engine traces shadows

		// the sun raised to real sunshine while NatureFX is on (Units), the track's own when it is off
		private float physical_ = 1.0f;

		private void UpdateMoon(float amount, float elevation, float heading, float scale, float shafts) {
			if (amount <= 0.0f) {
				if (moon_ != null) {
					moon_.enabled = false;
				}
				return;
			}

			if (moon_ == null) {
				moonObject_ = new GameObject("NatureFX.Moon");
				Object.DontDestroyOnLoad(moonObject_);
				moon_ = moonObject_.AddComponent<Light>();
				moon_.type = LightType.Directional;
				hdMoon_ = moonObject_.GetComponent<HDAdditionalLightData>() ?? moonObject_.AddComponent<HDAdditionalLightData>();
				moon_.shadows = LightShadows.Soft;
			}

			moon_.enabled = true;
			moonObject_.transform.rotation = Quaternion.Euler(elevation, heading, 0.0f);
			// a bright moonlit night: about 2 lux against the track's daylight sun (about 100 000 lux); shapes show,
			// colours and detail mostly come from the lamps and headlights. (0.4 % of the sun lit the night like an
			// overcast day.)
			moon_.intensity = original_.Intensity * physical_ * 0.00002f * scale * amount;
			moon_.useColorTemperature = true;
			moon_.colorTemperature = 7800.0f;
			if (hdMoon_ != null) {
				// its shadow map only while the own engine does not trace the shadows (it turns other directional
				// lights' shadows off every 2 s: set again every frame here, the moon's shadows doubled and flickered),
				// and only on a change (EnableShadows is not free)
				bool shadows = amount >= 0.5f && !moonShadowsTraced_;
				if (shadows != moonShadowsOn_) {
					hdMoon_.EnableShadows(shadows);
					moonShadowsOn_ = shadows;
				}
				hdMoon_.volumetricDimmer = shafts;
				hdMoon_.angularDiameter = 0.52f;
			}
		}

		private void SetSunShadows(bool on) {
			if (hdSun_ == null || on != sunShadowsOff_) {
				return;
			}
			hdSun_.EnableShadows(on);
			sunShadowsOff_ = !on;
		}

		// OwnLighting.Sun: HDRP's shading of the light off (a rendering layer no renderer is on), the light kept in HDRP's
		// lists for the exact sun pass
		private static void SetHdrpLighting(HDAdditionalLightData light, bool on) {
			var l = light != null ? light.GetComponent<Light>() : null;
			if (l == null) {
				return;
			}
			if (on) {
				OwnLighting.HdrpLightOn(l);
			} else {
				OwnLighting.HdrpLightOff(l);
			}
		}

		public void Restore() {
			SetHdrpLighting(hdSun_, true);
			SetHdrpLighting(hdMoon_, true);
			if (sizeApplied_ && hdSun_ != null) {
				hdSun_.angularDiameter = original_.AngularDiameter;
				sizeApplied_ = false;
			}
			physical_ = 1.0f;
			RestoreTimeOfDay();
			// the track's own strength in any case: the real sunshine NatureFX raised it to must not be read back as the
			// track's (Refresh takes the sun's intensity as its original: it grew 30 000 times on every rescan)
			if (sun_ != null && original_.Intensity > 0.0f) {
				sun_.intensity = original_.Intensity;
			}
		}

		// Undoes only the time of day override (rotation, intensity, colour, volumetric dimmer).
		private void RestoreTimeOfDay() {
			if (!applied_ || sun_ == null) {
				applied_ = false;
				return;
			}

			if (moon_ != null) {
				moon_.enabled = false;
			}
			SetSunShadows(true);
			sun_.transform.rotation = original_.Rotation;
			sun_.intensity = original_.Intensity * physical_;
			sun_.colorTemperature = original_.Temperature;
			sun_.color = original_.Color;
			sun_.useColorTemperature = original_.UseTemperature;
			if (hdSun_ != null) {
				hdSun_.volumetricDimmer = original_.VolumetricDimmer;
			}
			applied_ = false;
		}

		private static Light FindSun() {
			if (RenderSettings.sun != null && RenderSettings.sun.type == LightType.Directional) {
				return RenderSettings.sun;
			}

			Light best = null;
			foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (l.type == LightType.Directional && l.enabled && (best == null || l.intensity > best.intensity)) {
					best = l;
				}
			}
			return best;
		}

		// The share of the sun light the air lets through to a height (m) at an elevation (degrees): the optical depth
		// of Rayleigh, Mie (aerosols relative to a clear day) and ozone along the sun's ray to the top of the air,
		// with Sky.hlsli's constants; the planet's shadow below the horizon
		private static Vector3 AtmosphereTransmittance(float height, float elevation, float aerosols) {
			const float ground = 6360000.0f, top = 6460000.0f;
			float r = ground + height;
			float mu = Mathf.Sin(elevation * Mathf.Deg2Rad);
			float disc = r * r * (mu * mu - 1.0f) + ground * ground;
			if (mu < 0.0f && disc >= 0.0f) {
				return Vector3.zero;
			}
			float length = -r * mu + Mathf.Sqrt(Mathf.Max(r * r * (mu * mu - 1.0f) + top * top, 0.0f));
			const int steps = 32;
			float dt = length / steps;
			var depth = Vector3.zero;
			var rayleigh = new Vector3(5.802e-6f, 13.558e-6f, 33.1e-6f);
			var ozone = new Vector3(0.650e-6f, 1.881e-6f, 0.085e-6f);
			for (int i = 0; i < steps; ++i) {
				float t = (i + 0.5f) * dt;
				float h = Mathf.Sqrt(r * r + t * t + 2.0f * r * mu * t) - ground;
				float rd = Mathf.Exp(-Mathf.Max(h, 0.0f) / 8000.0f);
				float md = Mathf.Exp(-Mathf.Max(h, 0.0f) / 1200.0f) * aerosols;
				float od = Mathf.Max(0.0f, 1.0f - Mathf.Abs(h - 25000.0f) / 15000.0f);
				depth += (rayleigh * rd + Vector3.one * ((3.996e-6f + 4.40e-6f) * md) + ozone * od) * dt;
			}
			return new Vector3(Mathf.Exp(-depth.x), Mathf.Exp(-depth.y), Mathf.Exp(-depth.z));
		}
	}
}

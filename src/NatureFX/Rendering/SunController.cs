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
		}

		private Light sun_;
		private HDAdditionalLightData hdSun_;
		private Original original_;
		private bool applied_;
		private bool sizeApplied_;

		public Light Sun => sun_;
		// the track's daylight sun as the sun above the air (Sky.hlsli: the atmosphere colours and dims it itself)
		public static float TopOfAtmosphereLux { get; private set; }

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
				AngularDiameter = hdSun_ != null ? hdSun_.angularDiameter : 0.5f
			};
		}

		public void Update(ISettings s) {
			if (sun_ == null) {
				return;
			}
			if (hdSun_ != null && s.Enabled && s.RealisticShadows) {
				// soft shadow penumbra grows with distance from the caster (PCSS, High shadow filtering only)
				// overcast sky: the whole sky is the light source, so shadows get very soft
				hdSun_.angularDiameter = s.SunSize + Weather.Current.Cloud * 12.0f;
				sizeApplied_ = true;
			} else if (sizeApplied_ && hdSun_ != null) {
				hdSun_.angularDiameter = original_.AngularDiameter;
				sizeApplied_ = false;
			}

			TopOfAtmosphereLux = (original_.Intensity > 0.0f ? original_.Intensity : sun_.intensity) * (s.SunOverride ? s.SunIntensityScale : 1.0f);
			float cloud = Weather.Current.Cloud;
			float cloudDim = 1.0f - 0.75f * cloud;
			if (!s.Enabled || (!s.SunOverride && cloud < 0.01f)) {
				RestoreTimeOfDay();
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
				sun_.intensity = original_.Intensity * cloudDim;
				sun_.colorTemperature = original_.Temperature;
				sun_.useColorTemperature = original_.UseTemperature;
				applied_ = true;
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
			sun_.intensity = original_.Intensity * s.SunIntensityScale * daylight * cloudDim;
			sun_.useColorTemperature = true;
			sun_.colorTemperature = Mathf.Lerp(2200.0f, s.SunTemperature, Mathf.InverseLerp(0.0f, 30.0f, elevation));

			// Night: a separate moon light high on the opposite side. The sun stays below the horizon so the
			// physically based sky goes dark; only one directional light casts shadows, so they swap.
			float night = s.Moonlight ? Mathf.InverseLerp(-2.0f, -12.0f, elevation) : 0.0f;
			// moonlight in the air is faint: the eye's adaptation would otherwise turn a night sky into a bright haze
			UpdateMoon(night, Mathf.Clamp(-elevation * 0.8f, 20.0f, 65.0f), heading + 180.0f, s.SunIntensityScale * cloudDim, s.LightShafts * 0.15f);
			SetSunShadows(night < 0.5f);

			if (hdSun_ != null) {
				// a sun below the horizon lights no air: volumetric fog does not know the horizon, and the faint light
				// the sun keeps at night lit the whole sky white once the camera's volumetrics were on
				hdSun_.volumetricDimmer = s.LightShafts * Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(-2.0f, 2.0f, elevation));
			}
			applied_ = true;
		}

		private GameObject moonObject_;
		private Light moon_;
		private HDAdditionalLightData hdMoon_;
		private bool sunShadowsOff_;

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
			moon_.intensity = original_.Intensity * 0.00002f * scale * amount;
			moon_.useColorTemperature = true;
			moon_.colorTemperature = 7800.0f;
			if (hdMoon_ != null) {
				hdMoon_.EnableShadows(amount >= 0.5f);
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

		public void Restore() {
			if (sizeApplied_ && hdSun_ != null) {
				hdSun_.angularDiameter = original_.AngularDiameter;
				sizeApplied_ = false;
			}
			RestoreTimeOfDay();
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
			sun_.intensity = original_.Intensity;
			sun_.colorTemperature = original_.Temperature;
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
	}
}

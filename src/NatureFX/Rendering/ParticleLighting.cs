using System.Collections.Generic;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The light NFX_Particle.shader shades leaves, rain and snow with (it is not one of HDRP's lit shaders): the brightest
	// directional light above the horizon (the sun, else the moon), an estimate of the sky's light, the air's extinction
	// from the fog, and the eight lamps and headlights that light the camera's surroundings most.
	public static class ParticleLighting {
		private const int MaxLights = 8;
		private const float ScanInterval = 0.5f;
		private const float ScanRadius = 70.0f;

		private static readonly int SunId = Shader.PropertyToID("_NFX_PSun");
		private static readonly int SunDirId = Shader.PropertyToID("_NFX_PSunDir");
		private static readonly int SkyId = Shader.PropertyToID("_NFX_PSky");
		private static readonly int LightsId = Shader.PropertyToID("_NFX_PLights");
		private static readonly int ColorsId = Shader.PropertyToID("_NFX_PLightColors");
		private static readonly int DirsId = Shader.PropertyToID("_NFX_PLightDirs");
		private static readonly int CountId = Shader.PropertyToID("_NFX_PLightCount");
		private static readonly int BeamsId = Shader.PropertyToID("_NFX_PLightBeams");
		private static readonly Vector4[] beams_ = new Vector4[MaxLights];

		private static readonly List<Light> directional_ = new();
		private static readonly List<Light> punctual_ = new();
		private static readonly List<(float Score, Light Light)> ranked_ = new();
		private static readonly Vector4[] positions_ = new Vector4[MaxLights];
		private static readonly Vector4[] colors_ = new Vector4[MaxLights];
		private static readonly Vector4[] dirs_ = new Vector4[MaxLights];
		private static float nextScan_;

		public static void Update(Camera camera) {
			if (camera == null) {
				return;
			}
			var eye = camera.transform.position;
			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan(eye);
			}

			// the sun or the moon: whichever lights the ground more
			Light main = null;
			float best = 0.0f;
			foreach (var l in directional_) {
				if (l == null || !l.isActiveAndEnabled) {
					continue;
				}
				float up = Mathf.Clamp01(-l.transform.forward.y * 4.0f + 0.1f);
				float e = HeadlightController.Intensity(l) * up;
				if (e > best) {
					best = e;
					main = l;
				}
			}
			var sunColor = main != null ? Linear(main) * best : Color.black;
			var sunDir = main != null ? -main.transform.forward : Vector3.up;
			Shader.SetGlobalVector(SunId, new Vector4(sunColor.r, sunColor.g, sunColor.b, 0.0f));
			Shader.SetGlobalVector(SunDirId, sunDir);

			// sky: a clear sky gives about a fifth of the sun's light from all round, an overcast one much less in all but
			// all of it diffuse (the sun's intensity already carries the clouds' dimming); a little city glow at night
			float cloud = Weather.Current.Cloud;
			float skyLux = best * Mathf.Lerp(0.2f, 0.6f, cloud) + 0.03f;
			var skyTint = Color.Lerp(new Color(0.75f, 0.85f, 1.0f), new Color(0.9f, 0.92f, 0.95f), cloud);
			// the air's extinction at the camera: the own fog's (MapFog.NativeParams), thinning upwards from its base as
			// the fog volume does
			float extinction = 0.0f;
			var native = NatureFX.Map.MapFog.NativeParams;
			if (native.w > 0.0f) {
				float eyeY = Camera.main != null ? Camera.main.transform.position.y : native.y;
				extinction = native.x * Mathf.Exp(-Mathf.Max(eyeY - native.y, 0.0f) / Mathf.Max(native.z, 1.0f));
			}
			Shader.SetGlobalVector(SkyId, new Vector4(skyTint.r * skyLux, skyTint.g * skyLux, skyTint.b * skyLux, extinction));

			// lamps: the ones that light the camera's surroundings most (their illuminance at the camera)
			ranked_.Clear();
			foreach (var l in punctual_) {
				if (l == null || !l.isActiveAndEnabled || HeadlightController.Intensity(l) <= 0.0f) {
					continue;
				}
				float d2 = Mathf.Max((l.transform.position - eye).sqrMagnitude, 1.0f);
				if (d2 > l.range * l.range * 4.0f) {
					continue;
				}
				// Light.intensity of a point or spot light is in candela whatever unit it was set in (HDRP converts)
				ranked_.Add((HeadlightController.Intensity(l) / d2, l));
			}
			ranked_.Sort((a, b) => b.Score.CompareTo(a.Score));
			int count = Mathf.Min(ranked_.Count, MaxLights);
			for (int i = 0; i < count; ++i) {
				var l = ranked_[i].Light;
				var p = l.transform.position;
				var c = Linear(l) * HeadlightController.Intensity(l);
				positions_[i] = new Vector4(p.x, p.y, p.z, l.range);
				bool isBeam = HeadlightController.Beams.TryGetValue(l, out int beam);
				beams_[i] = new Vector4(isBeam ? beam : 0.0f, isBeam ? HeadlightController.Roll(l) : 0.0f, 0.0f, 0.0f);
				if (l.type == LightType.Spot) {
					float outer = Mathf.Cos(l.spotAngle * 0.5f * Mathf.Deg2Rad);
					float inner = Mathf.Cos(Mathf.Min(l.innerSpotAngle, l.spotAngle - 1.0f) * 0.5f * Mathf.Deg2Rad);
					colors_[i] = new Vector4(c.r, c.g, c.b, outer);
					var f = l.transform.forward;
					dirs_[i] = new Vector4(f.x, f.y, f.z, inner);
				} else {
					colors_[i] = new Vector4(c.r, c.g, c.b, -2.0f);
					dirs_[i] = Vector4.zero;
				}
			}
			Shader.SetGlobalVectorArray(LightsId, positions_);
			Shader.SetGlobalVectorArray(ColorsId, colors_);
			Shader.SetGlobalVectorArray(DirsId, dirs_);
			Shader.SetGlobalVectorArray(BeamsId, beams_);
			Shader.SetGlobalFloat(CountId, count);
		}

		private static void Scan(Vector3 eye) {
			directional_.Clear();
			punctual_.Clear();
			foreach (var l in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) {
				if (!l.isActiveAndEnabled) {
					continue;
				}
				if (l.type == LightType.Directional) {
					directional_.Add(l);
				} else if ((l.type == LightType.Point || l.type == LightType.Spot) && (l.transform.position - eye).sqrMagnitude < ScanRadius * ScanRadius) {
					punctual_.Add(l);
				}
			}
		}

		// the light's colour as HDRP uses it: the filter colour times the colour temperature's
		private static Color Linear(Light l) {
			var c = l.color.linear;
			if (l.useColorTemperature) {
				c *= Mathf.CorrelatedColorTemperatureToRGB(l.colorTemperature);
			}
			return c;
		}
	}
}

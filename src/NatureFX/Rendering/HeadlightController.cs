using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Car headlights (spot lights under heavy rigidbodies) get visible beams in volumetric fog.
	public class HeadlightController {
		private const float ScanInterval = 5.0f;
		private const float MinCarMass = 400.0f;

		private readonly Dictionary<HDAdditionalLightData, float> originals_ = new();
		private readonly List<Light> lights_ = new();
		// retroreflection (docs/map-authoring.md 6.3): the headlights nearest the camera, for the shaders
		public const int MaxRetroLights = 4;
		private static readonly int HeadlightsId = Shader.PropertyToID("_NFX_Headlights");
		private static readonly int HeadlightDirsId = Shader.PropertyToID("_NFX_HeadlightDirs");
		private readonly Vector4[] positions_ = new Vector4[MaxRetroLights];
		private readonly Vector4[] directions_ = new Vector4[MaxRetroLights];
		private readonly List<(float Distance, Light Light)> nearest_ = new();
		private float nextScan_;
		private float applied_ = -1.0f;

		// retro: pass the headlights to the shaders for the map's retroreflective materials
		public void Update(ISettings s, bool retro) {
			float shafts = s.Enabled ? s.HeadlightShafts : 0.0f;

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan();
				applied_ = -1.0f;
			}

			UpdateRetroLights(retro);

			if (Mathf.Approximately(shafts, applied_)) {
				return;
			}
			applied_ = shafts;

			foreach (var kv in originals_) {
				if (kv.Key != null) {
					// 0 keeps the game's own value. 1.5 times the physical scattering at 1: at 4 the beams lit a bright
					// dome of the air right in front of the car, like a filter over the picture
					kv.Key.volumetricDimmer = shafts > 0.0f ? shafts * 1.5f : kv.Value;
				}
			}
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
				if (kv.Key != null) {
					kv.Key.volumetricDimmer = kv.Value;
				}
			}
			originals_.Clear();
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
					originals_[hd] = hd.volumetricDimmer;
					lights_.Add(light);
				}
			}
		}
	}
}

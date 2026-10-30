using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Car headlights (spot lights under heavy rigidbodies) get visible beams in volumetric fog.
	public class HeadlightController {
		private const float ScanInterval = 5.0f;
		private const float MinCarMass = 400.0f;

		private readonly Dictionary<HDAdditionalLightData, float> originals_ = new();
		private float nextScan_;
		private float applied_ = -1.0f;

		public void Update(ISettings s) {
			float shafts = s.Enabled ? s.HeadlightShafts : 0.0f;

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan();
				applied_ = -1.0f;
			}

			if (Mathf.Approximately(shafts, applied_)) {
				return;
			}
			applied_ = shafts;

			foreach (var kv in originals_) {
				if (kv.Key != null) {
					// 0 keeps the game's own value
					kv.Key.volumetricDimmer = shafts > 0.0f ? shafts * 4.0f : kv.Value;
				}
			}
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
				}
			}
		}
	}
}

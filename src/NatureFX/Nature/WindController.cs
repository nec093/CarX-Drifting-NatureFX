using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Nature {
	// Drives the NFX wind globals and a Unity WindZone (picked up by any SpeedTree materials in the game).
	public class WindController {
		private const float InteractorScanInterval = 0.2f;
		private const float InteractorScanRadius = 80.0f;
		private const float MinCarMass = 400.0f;

		private readonly Vector4[] interactors_ = new Vector4[ShaderGlobals.MaxInteractors];
		private readonly Collider[] overlap_ = new Collider[128];
		private readonly HashSet<Rigidbody> seen_ = new();

		private GameObject zoneObject_;
		private WindZone zone_;
		private float time_;
		private float nextScan_;

		public void Create() {
			zoneObject_ = new GameObject("NatureFX.Wind");
			Object.DontDestroyOnLoad(zoneObject_);
			zone_ = zoneObject_.AddComponent<WindZone>();
			zone_.mode = WindZoneMode.Directional;
		}

		public void Destroy() {
			if (zoneObject_ != null) {
				Object.Destroy(zoneObject_);
			}
			Shader.SetGlobalVector(ShaderGlobals.Wind, Vector4.zero);
			Shader.SetGlobalInt(ShaderGlobals.InteractorCount, 0);
		}

		public void Update(ISettings s) {
			bool on = s.Enabled && s.Wind;
			float strength = on ? s.WindStrength : 0.0f;

			time_ += Time.unscaledDeltaTime;

			var dir = Quaternion.Euler(0.0f, s.WindDirection, 0.0f) * Vector3.forward;
			Shader.SetGlobalVector(ShaderGlobals.Wind, new Vector4(dir.x, 0.0f, dir.z, strength));
			Shader.SetGlobalVector(ShaderGlobals.WindParams, new Vector4(0.35f, s.WindGust, 6.0f, s.LeafFlutter));
			Shader.SetGlobalFloat(ShaderGlobals.WindTime, time_);

			if (zone_ != null) {
				zoneObject_.SetActive(on);
				zoneObject_.transform.rotation = Quaternion.LookRotation(dir);
				zone_.windMain = strength;
				zone_.windTurbulence = strength * s.WindGust;
				zone_.windPulseMagnitude = s.WindGust;
				zone_.windPulseFrequency = 0.25f;
			}

			if (on && s.InteractiveFoliage) {
				if (Time.unscaledTime >= nextScan_) {
					nextScan_ = Time.unscaledTime + InteractorScanInterval;
					ScanInteractors();
				}
			} else {
				Shader.SetGlobalInt(ShaderGlobals.InteractorCount, 0);
			}
		}

		// Cheap, game-agnostic car detection: heavy rigidbodies around the camera.
		private void ScanInteractors() {
			var cam = Camera.main;
			if (cam == null) {
				Shader.SetGlobalInt(ShaderGlobals.InteractorCount, 0);
				return;
			}

			int hits = Physics.OverlapSphereNonAlloc(cam.transform.position, InteractorScanRadius, overlap_, ~0, QueryTriggerInteraction.Ignore);
			int count = 0;
			seen_.Clear();
			for (int i = 0; i < hits && count < interactors_.Length; ++i) {
				var rb = overlap_[i].attachedRigidbody;
				if (rb == null || rb.mass < MinCarMass || !seen_.Add(rb)) {
					continue;
				}

				var p = rb.worldCenterOfMass;
				interactors_[count++] = new Vector4(p.x, p.y, p.z, 2.6f);
			}

			Shader.SetGlobalVectorArray(ShaderGlobals.Interactors, interactors_);
			Shader.SetGlobalInt(ShaderGlobals.InteractorCount, count);
		}
	}
}

using System.Collections.Generic;
using NatureFX.Map;
using UnityEngine;

namespace NatureFX.Nature {
	// Drives the NFX wind globals and a Unity WindZone (picked up by any SpeedTree materials in the game).
	public class WindController {
		private const float InteractorScanInterval = 0.2f;
		private const float InteractorScanRadius = 80.0f;
		private const float MinCarMass = 400.0f;
		private const float DefaultFoliageBend = 1.0f;

		private readonly Vector4[] interactors_ = new Vector4[ShaderGlobals.MaxInteractors];
		private readonly Collider[] overlap_ = new Collider[128];
		private readonly HashSet<Rigidbody> seen_ = new();
		private readonly List<Rigidbody> bodies_ = new();

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

		// the drawn foliage sways (NatureFX/Foliage); off: only the traced shadows do (MapProfile.FoliageWind)
		public bool DrawnSway;

		// the sway the traced foliage gets (NativeEngine.UpdateWind)
		public static float TracedBend { get; private set; } = DefaultFoliageBend;

		public void Update(ISettings s) {
			bool on = s.Enabled && s.Wind;
			// the map's [defaults] and weather zones where the user left a slider as it was (Map/MapDefaults)
			float gustiness = MapDefaults.Value(MapKey.WindGust, s.WindGust);
			float strength = on ? MapDefaults.Value(MapKey.WindStrength, s.WindStrength) * Mathf.Max(0.0f, Weather.Current.WindScale) : 0.0f;

			time_ += Time.unscaledDeltaTime;

			var dir = Quaternion.Euler(0.0f, MapDefaults.Value(MapKey.WindDirection, s.WindDirection), 0.0f) * Vector3.forward;
			Shader.SetGlobalVector(ShaderGlobals.Wind, new Vector4(dir.x, 0.0f, dir.z, strength));
			Shader.SetGlobalVector(ShaderGlobals.WindParams, new Vector4(0.35f, gustiness, 6.0f, s.LeafFlutter));
			Shader.SetGlobalFloat(ShaderGlobals.WindTime, time_);
			TracedBend = s.FoliageSway > 0.0f ? s.FoliageSway : DefaultFoliageBend;
			Shader.SetGlobalFloat(ShaderGlobals.FoliageBend, DrawnSway ? TracedBend : 0.0f);

			if (zone_ != null) {
				zoneObject_.SetActive(on);
				zoneObject_.transform.rotation = Quaternion.LookRotation(dir);
				zone_.windMain = strength;
				zone_.windTurbulence = strength * gustiness;
				zone_.windPulseMagnitude = gustiness;
				zone_.windPulseFrequency = 0.25f;
			}

			if (on && s.InteractiveFoliage) {
				if (Time.unscaledTime >= nextScan_) {
					nextScan_ = Time.unscaledTime + InteractorScanInterval;
					ScanInteractors();
				}
				// the scan finds the cars; their positions go to the shaders every frame, or the plants they push
				// would follow them in 0.2 s steps
				SendInteractors();
			} else {
				Shader.SetGlobalInt(ShaderGlobals.InteractorCount, 0);
			}
		}

		// Cheap, game-agnostic car detection: heavy rigidbodies around the camera.
		private void ScanInteractors() {
			var cam = Camera.main;
			if (cam == null) {
				bodies_.Clear();
				return;
			}

			int hits = Physics.OverlapSphereNonAlloc(cam.transform.position, InteractorScanRadius, overlap_, ~0, QueryTriggerInteraction.Ignore);
			seen_.Clear();
			bodies_.Clear();
			for (int i = 0; i < hits && bodies_.Count < interactors_.Length; ++i) {
				var rb = overlap_[i].attachedRigidbody;
				if (rb == null || rb.mass < MinCarMass || !seen_.Add(rb)) {
					continue;
				}
				bodies_.Add(rb);
			}
		}

		private void SendInteractors() {
			int count = 0;
			foreach (var rb in bodies_) {
				if (rb == null) {
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

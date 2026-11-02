using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// The map's fog (fog.distance, fog.height in [defaults] or a zone) as a global HDRP volume over the track's own:
	// distance is the fog's attenuation distance (scaled by the weather: rain and fog presets thicken it), height how
	// far above the camera it thins out (it starts at the camera, so it stays around the car on a mountain road at
	// any altitude). The volume's weight fades it in and out with the zones; fog.distance = 0 adds none.
	// Without map fog it can still add the distant haze, and switch volumetric fog on for the local fog volumes.
	public class MapFog {
		private const float Priority = 9000.0f;
		private const float DefaultHeight = 80.0f;

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;
		private Fog fog_;
		private float baseHeight_ = float.NaN;
		private bool gameFogOn_ = true;
		private float gameFogDistance_ = float.PositiveInfinity;  // the track's own attenuation distance
		private int resampleFrames_;

		// Reads the track's own fog again (after a map loaded): see Update
		public void Invalidate() {
			resampleFrames_ = 2;
		}

		// Native fog (Settings.NativeFog): the own engine draws the fog (NFXNative Fog.hlsl, RTShadowPass) from these:
		// x extinction at the base (1/m), y the base height (world y), z the scale height (m), w 1 = on. HDRP's global
		// fog is then switched off (this volume's mean free path out of reach); its volumetric fog stays for the map's
		// local fog volumes and the tyre smoke.
		public static Vector4 NativeParams { get; private set; }
		// the most the air scatters the lamps' light (1/m): unlimited in the weather's or the map's fog, else clear air's
		public static float LampAirSigma { get; private set; } = 1e-4f;

		public void Create() {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.MapFog");
			Object.DontDestroyOnLoad(root_);
			profile_ = ScriptableObject.CreateInstance<VolumeProfile>();
			profile_.name = "NatureFX.MapFog";
			fog_ = profile_.Add<Fog>();
			fog_.enabled.Override(true);
			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.weight = 0.0f;
			volume_.sharedProfile = profile_;
			volume_.enabled = false;
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (profile_ != null) {
				Object.Destroy(profile_);
			}
			root_ = null;
			profile_ = null;
		}

		// volumetric: the map's local fog shows (MapVolumes), which HDRP draws only with volumetric fog on.
		// haze (0..1, F8 Distant haze): without map fog, a thin layer of air that blues the far mountains (aerial
		// perspective) without touching the near road.
		public void Update(bool on, Vector3 camera, bool volumetric, float haze, bool native = false) {
			if (volume_ == null) {
				return;
			}
			float distance = MapDefaults.Value(MapKey.FogDistance, 0.0f);
			float weight = on && distance > 0.0f ? MapDefaults.FogWeight : 0.0f;
			volumetric &= on;
			haze = on ? Mathf.Clamp01(haze) : 0.0f;
			if (resampleFrames_ > 0) {
				// a new map: this volume off for a rendered frame, so the camera's stack shows the track's own fog
				// again (with the haze or the own engine's fog on from the first frame it was never read: every
				// track's fog counted as none)
				--resampleFrames_;
				volume_.enabled = false;
				if (resampleFrames_ > 0) {
					return;
				}
			}
			if (!volume_.enabled) {
				// the track's own fog, as it is without this volume
				var stack = NatureFX.Rendering.VolumeOverhaul.Stack()?.GetComponent<Fog>();
				gameFogOn_ = stack != null && stack.enabled.value;
				gameFogDistance_ = gameFogOn_ ? stack.meanFreePath.value : float.PositiveInfinity;
			}
			native &= on;
			volume_.enabled = weight > 0.0f || volumetric || haze > 0.0f || native;
			if (!native) {
				NativeParams = Vector4.zero;
			}
			if (volumetric) {
				// local fog and smoke further away than HDRP's default 64 m of volumetric fog show too
				fog_.enableVolumetricFog.Override(true);
				fog_.depthExtent.Override(150.0f);
				// spread over 150 m the fog's light came out in blocks (lamp cones, headlight beams): HDRP's high
				// quality tier, more of its slices close to the camera, and its denoising in space and time
				fog_.quality.Override(2);
				fog_.sliceDistributionUniformity.Override(0.55f);
				fog_.denoisingMode.Override(FogDenoisingMode.Both);
			} else {
				fog_.enableVolumetricFog.overrideState = false;
				fog_.depthExtent.overrideState = false;
				fog_.quality.overrideState = false;
				fog_.sliceDistributionUniformity.overrideState = false;
				fog_.denoisingMode.overrideState = false;
			}
			if (!volume_.enabled) {
				baseHeight_ = float.NaN;
				return;
			}
			if (native) {
				UpdateNative(camera, weight, distance, haze);
				return;
			}
			fog_.enabled.Override(true);
			// follows the camera's height slowly, so jumps and crests do not pump the fog
			baseHeight_ = float.IsNaN(baseHeight_) ? camera.y : Mathf.Lerp(baseHeight_, camera.y, 1.0f - Mathf.Exp(-Time.unscaledDeltaTime * 0.5f));
			float scale = Weather.Current.FogScale > 0.0f ? Weather.Current.FogScale : 1.0f;

			if (weight > 0.0f) {
				// the map's fog, faded in by its zones
				volume_.weight = weight;
				float height = MapDefaults.Value(MapKey.FogHeight, DefaultHeight);
				fog_.meanFreePath.Override(Mathf.Max(1.0f, distance * scale));
				fog_.baseHeight.Override(baseHeight_ - 5.0f);
				fog_.maximumHeight.Override(baseHeight_ - 5.0f + height);
				fog_.maxFogDistance.Override(Mathf.Max(5000.0f, distance * 4.0f));
			} else if (haze > 0.0f) {
				// a long attenuation distance through a deep layer: the road stays clear, kilometres away turn blue.
				// A track with denser fog of its own keeps it (the haze replaced a 300 m fog with a 25 km one), and
				// the weather thickens that too
				volume_.weight = 1.0f;
				float hazeDistance = Mathf.Lerp(40000.0f, 3500.0f, haze);
				fog_.meanFreePath.Override(Mathf.Max(1.0f, Mathf.Min(hazeDistance, gameFogDistance_) * scale));
				fog_.baseHeight.Override(baseHeight_ - 300.0f);
				fog_.maximumHeight.Override(baseHeight_ + 1500.0f);
				fog_.maxFogDistance.Override(20000.0f);
			} else {
				// only volumetric fog for the local volumes: the track's fog as it was, none if it had none
				volume_.weight = 1.0f;
				fog_.baseHeight.overrideState = false;
				fog_.maximumHeight.overrideState = false;
				fog_.maxFogDistance.overrideState = false;
				if (RainExtinction > 0.0f) {
					fog_.meanFreePath.Override(1.0f / RainExtinction);
					fog_.baseHeight.Override(baseHeight_ - 5.0f);
					fog_.maximumHeight.Override(baseHeight_ + 600.0f);
					fog_.maxFogDistance.Override(5000.0f);
				} else if (gameFogOn_) {
					fog_.meanFreePath.overrideState = false;
				} else {
					fog_.meanFreePath.Override(100000.0f);
				}
			}
		}

		// Rain's own haze: falling drops and the mist around them. Rain rate (mm/h) from the weather's rain, 1 to 50; the
		// visibility as measured in rain, about 10 km at 1 mm/h, 1.5 km at 20, 0.8 km at 50 (V = 10 R^-0.65 km), its
		// extinction 3.912 / V (per metre, the 2 % contrast distance)
		public static float RainRate => 1.0f + 49.0f * Weather.Current.Rain * Weather.Current.Rain;
		public static float RainExtinction {
			get {
				if (Weather.Current.Rain <= 0.001f) {
					return 0.0f;
				}
				float visibility = 10000.0f * Mathf.Pow(RainRate, -0.65f);
				return 3.912f / visibility * Mathf.Clamp01(Weather.Current.Rain * 4.0f);
			}
		}

		// the same fog as below, for the own engine: the map's fog, else the haze, else the track's own
		private void UpdateNative(Vector3 camera, float weight, float distance, float haze) {
			baseHeight_ = float.IsNaN(baseHeight_) ? camera.y : Mathf.Lerp(baseHeight_, camera.y, 1.0f - Mathf.Exp(-Time.unscaledDeltaTime * 0.5f));
			float scale = Weather.Current.FogScale > 0.0f ? Weather.Current.FogScale : 1.0f;
			float meanFreePath, baseHeight, layer;
			if (weight > 0.0f) {
				meanFreePath = Mathf.Max(1.0f, distance * scale) / Mathf.Max(weight, 0.01f);
				baseHeight = baseHeight_ - 5.0f;
				layer = MapDefaults.Value(MapKey.FogHeight, DefaultHeight);
			} else if (haze > 0.0f) {
				meanFreePath = Mathf.Max(1.0f, Mathf.Min(Mathf.Lerp(40000.0f, 3500.0f, haze), gameFogDistance_) * scale);
				baseHeight = baseHeight_ - 300.0f;
				layer = 1800.0f;
			} else if (gameFogOn_ && !float.IsInfinity(gameFogDistance_)) {
				meanFreePath = Mathf.Max(1.0f, gameFogDistance_ * scale);
				baseHeight = baseHeight_ - 300.0f;
				layer = 1800.0f;
			} else if (RainExtinction > 0.0f) {
				// only rain: its haze around the camera, a deep layer (it falls from the clouds)
				meanFreePath = float.PositiveInfinity;
				baseHeight = baseHeight_ - 300.0f;
				layer = 1800.0f;
			} else {
				meanFreePath = float.PositiveInfinity;
				baseHeight = 0.0f;
				layer = 1000.0f;
			}
			// the rain's haze on top of the fog, as dense at the camera as measured (the layer thins upwards from its base)
			float extinction = float.IsInfinity(meanFreePath) ? 0.0f : 1.0f / meanFreePath;
			float scaleHeight = Mathf.Max(20.0f, layer / 3.0f);
			float atCamera = Mathf.Exp(-Mathf.Max(0.0f, baseHeight_ - baseHeight) / scaleHeight);
			float rain = RainExtinction;
			if (rain > 0.0f) {
				extinction += rain / Mathf.Max(atCamera, 0.05f);
				meanFreePath = 1.0f / extinction;
			}
			// clear air scatters a lamp's light at about 2e-5 per metre (1e-4 drew a white veil out of the headlights on a
			// clear night), times the Headlight beams setting (0: none); the weather's fog in full
			float beams = NatureFX.Settings.Data != null ? Mathf.Max(0.0f, NatureFX.Settings.Data.HeadlightShafts) : 1.0f;
			// rain scatters the lamps' light like the fog it is (the beams show as cones in the rain)
			LampAirSigma = weight > 0.0f ? 1e6f : 2e-5f * beams + rain * Mathf.Max(beams, 0.5f);
			// HDRP's fog falls off from its base to its maximum height; as an exponential, a third of the layer
			NativeParams = new Vector4(float.IsInfinity(meanFreePath) ? 0.0f : 1.0f / meanFreePath, baseHeight, Mathf.Max(20.0f, layer / 3.0f), 1.0f);
			volume_.weight = 1.0f;
			fog_.baseHeight.overrideState = false;
			fog_.maximumHeight.overrideState = false;
			fog_.maxFogDistance.overrideState = false;
			fog_.meanFreePath.Override(100000.0f);
			// HDRP's fog off altogether: the own engine draws all of it (HDRP's fog with nothing in it still ran)
			fog_.enabled.Override(false);
		}
	}
}

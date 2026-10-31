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
		public void Update(bool on, Vector3 camera, bool volumetric, float haze) {
			if (volume_ == null) {
				return;
			}
			float distance = MapDefaults.Value(MapKey.FogDistance, 0.0f);
			float weight = on && distance > 0.0f ? MapDefaults.FogWeight : 0.0f;
			volumetric &= on;
			haze = on ? Mathf.Clamp01(haze) : 0.0f;
			if (!volume_.enabled) {
				// the track's own fog, as it is without this volume
				var stack = NatureFX.Rendering.VolumeOverhaul.Stack()?.GetComponent<Fog>();
				gameFogOn_ = stack != null && stack.enabled.value;
				gameFogDistance_ = gameFogOn_ ? stack.meanFreePath.value : float.PositiveInfinity;
			}
			volume_.enabled = weight > 0.0f || volumetric || haze > 0.0f;
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
				if (gameFogOn_) {
					fog_.meanFreePath.overrideState = false;
				} else {
					fog_.meanFreePath.Override(100000.0f);
				}
			}
		}
	}
}

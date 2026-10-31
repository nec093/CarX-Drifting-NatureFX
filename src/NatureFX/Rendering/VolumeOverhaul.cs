using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// A global HDRP Volume with very high priority: it switches the game's own screen space effects off where NatureFX
	// traces them instead, and keeps exposure and tone under NatureFX's control. Everything else falls through to the
	// game's own track volumes.
	public class VolumeOverhaul {
		private const float Priority = 10000.0f;

		// The volume values the game camera renders with. HDRP keeps a stack per camera; VolumeManager's own stack is
		// never updated for it and holds the defaults (it said "sky type 0" over the test map's physically based sky).
		public static VolumeStack Stack() {
			var cam = Camera.main;
			if (cam != null && cam.TryGetComponent<HDAdditionalCameraData>(out _)) {
				var stack = HDCamera.GetOrCreate(cam).volumeStack;
				if (stack != null) {
					return stack;
				}
			}
			return VolumeManager.instance.stack;
		}

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;

		private GlobalIllumination gi_;
		private ScreenSpaceReflection ssr_;
		private ScreenSpaceAmbientOcclusion ao_;
		private ContactShadows contact_;
		private Tonemapping tonemapping_;
		private ColorAdjustments color_;
		private Exposure exposure_;
		private VisualEnvironment environment_;
		private IndirectLightingController indirect_;
		private SkySettings skyDim_;          // the track's sky type in our profile, its brightness turned down at night
		private float skyBase_ = 1.0f;        // the track's own sky brightness (multiplier), read while ours is off
		private float lastSkyElevation_ = float.NaN;
		private float lastNight_;
		private int trackSky_ = -1;            // the track's own sky type, read while ours does not override it
		private PhysicallyBasedSky nightSky_;  // deep night over a sky that does not darken: HDRP's physically based one

		public string SkyState { get; private set; } = "";

		public void Create() {
			if (root_ != null) {
				return;
			}

			root_ = new GameObject("NatureFX.Volume");
			Object.DontDestroyOnLoad(root_);

			profile_ = ScriptableObject.CreateInstance<VolumeProfile>();
			profile_.name = "NatureFX.Profile";
			gi_ = profile_.Add<GlobalIllumination>();
			ssr_ = profile_.Add<ScreenSpaceReflection>();
			ao_ = profile_.Add<ScreenSpaceAmbientOcclusion>();
			contact_ = profile_.Add<ContactShadows>();
			tonemapping_ = profile_.Add<Tonemapping>();
			color_ = profile_.Add<ColorAdjustments>();
			exposure_ = profile_.Add<Exposure>();
			environment_ = profile_.Add<VisualEnvironment>();
			indirect_ = profile_.Add<IndirectLightingController>();

			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.weight = 1.0f;
			volume_.sharedProfile = profile_;
			volume_.enabled = false;
		}

		// 0 ACES, 1 Neutral, 2 Filmic (GT-style: soft toe, long highlight shoulder), 3 None
		private void ApplyTonemapping(int mode) {
			switch (mode) {
				case 1:
					tonemapping_.mode.Override(TonemappingMode.Neutral);
					break;
				case 2:
					tonemapping_.mode.Override(TonemappingMode.Custom);
					tonemapping_.toeStrength.Override(0.25f);
					tonemapping_.toeLength.Override(0.35f);
					tonemapping_.shoulderStrength.Override(0.6f);
					tonemapping_.shoulderLength.Override(1.6f);
					tonemapping_.shoulderAngle.Override(0.4f);
					tonemapping_.gamma.Override(1.0f);
					break;
				case 3:
					tonemapping_.mode.Override(TonemappingMode.None);
					break;
				default:
					tonemapping_.mode.Override(TonemappingMode.ACES);
					break;
			}
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

		// NatureFX off: the game's own look as it is
		public void Disable() {
			if (volume_ != null) {
				volume_.enabled = false;
			}
		}

		// The game's own screen space effects go where a ray traced one replaces them (both would darken or reflect
		// twice): reflections, GI and AO, contact shadows. Exposure and tone are under NatureFX's control: with the
		// game's fixed exposure the ray traced shade looked flat and too dark.
		public void Apply(ISettings s) {
			if (volume_ == null) {
				return;
			}
			volume_.enabled = true;
			ssr_.active = s.RTReflections;
			ssr_.enabled.Override(false);
			gi_.active = s.RTAO;
			gi_.enable.Override(false);
			ao_.active = s.RTAO;
			ao_.intensity.Override(0.0f);
			contact_.active = s.RTShadows;
			contact_.enable.Override(false);

			tonemapping_.active = s.AcesTonemapping;
			ApplyTonemapping(s.TonemapMode);
			color_.active = true;
			color_.postExposure.Override(s.Exposure);
			color_.saturation.Override(s.Saturation);
			color_.contrast.Override(s.Contrast);

			// Exposure: eye adaptation (histogram metering, slow to adjust like a real eye / camera), or the game's
			exposure_.active = s.ExposureMode != 0;
			exposure_.mode.Override(ExposureMode.AutomaticHistogram);
			exposure_.meteringMode.Override(MeteringMode.CenterWeighted);
			exposure_.limitMin.Override(s.AutoExposureMin);
			exposure_.limitMax.Override(Mathf.Max(s.AutoExposureMin + 0.5f, s.AutoExposureMax));
			exposure_.adaptationMode.Override(AdaptationMode.Progressive);
			exposure_.adaptationSpeedDarkToLight.Override(s.AdaptationSpeed * 2.0f);
			exposure_.adaptationSpeedLightToDark.Override(s.AdaptationSpeed);
			exposure_.compensation.Override(0.0f);

			// the ambient light and the sky's reflection from the sky as it is now: a track's baked ("static") sky keeps
			// the daylight it was baked with, so at night reflections showed a bright evening sky and the ambient light
			// (the ray traced effects read it too) stayed at dusk
			environment_.active = true;
			environment_.skyAmbientMode.Override(SkyAmbientMode.Dynamic);
		}

		// The time of day moved: redraw the sky's lighting and reflection (a sky set to update on demand never would).
		// A sky that is not HDRP's physically based one (a photo, a gradient) stays as bright at night as by day: its
		// ambient light and reflections are faded out after dusk, as the physically based sky does by itself, and the
		// sky itself is darkened to a night sky (it showed a white daytime photo at midnight).
		public void UpdateSky(Light sun, bool timeOfDay) {
			if (sun == null || indirect_ == null) {
				return;
			}
			float elevation = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;
			var env = VolumeOverhaul.Stack()?.GetComponent<VisualEnvironment>();
			if (env != null && (!environment_.skyType.overrideState || trackSky_ < 0) && env.skyType.value != trackSky_) {
				trackSky_ = env.skyType.value;
				// which volumes choose it (a track's sky volume, or the game's own on top of it)
				var from = new System.Collections.Generic.List<string>();
				foreach (var v in Object.FindObjectsOfType<Volume>()) {
					if (v != volume_ && v.sharedProfile != null && v.sharedProfile.TryGet<VisualEnvironment>(out var ve) && ve.skyType.overrideState) {
						from.Add($"{v.name} (priority {v.priority}, sky type {ve.skyType.value}{(v.isActiveAndEnabled ? "" : ", off")})");
					}
				}
				Kino.Log.Info($"[NatureFX] Track sky type {trackSky_}, set by: {(from.Count > 0 ? string.Join(", ", from) : "no volume (HDRP's default)")}");
			}
			// The sky is always HDRP's physically based one, computed from the sun as it is: a track's photo (HDRI) or
			// gradient sky is one moment with its clouds painted in (a red evening photo at 11 o'clock, and clouds that
			// Weather > Clouds could not touch). A track's own physically based settings (planet, ground, air) still
			// come through: our component overrides nothing but the sky type. Only while NatureFX is on (this volume is
			// off otherwise). The photo sky's dimming at night (DimSky, the indirect multipliers) is no longer needed.
			if (!environment_.skyType.overrideState) {
				nightSky_ ??= profile_.Add<PhysicallyBasedSky>();
				nightSky_.active = true;
				environment_.skyType.Override((int)SkyType.PhysicallyBased);
				lastSkyElevation_ = float.NaN; // redraw the sky's lighting now
			}
			const float night = 0.0f;
			indirect_.active = false;
			DimSky(env, night);
			string trackName = trackSky_ >= 0 && SkyTypes() != null && SkyTypes().TryGetValue(trackSky_, out var t) ? t.Name : $"type {trackSky_}";
			SkyState = trackSky_ == (int)SkyType.PhysicallyBased ? $"{trackName}, the track's own"
				: $"physically based (the track's {trackName} replaced){(timeOfDay ? "" : ", time of day is the game's")}";
			if (!float.IsNaN(lastSkyElevation_) && Mathf.Abs(elevation - lastSkyElevation_) < 0.5f && Mathf.Abs(night - lastNight_) < 0.02f) {
				return;
			}
			lastSkyElevation_ = elevation;
			lastNight_ = night;
			if (RenderPipelineManager.currentPipeline is HDRenderPipeline hdrp) {
				hdrp.RequestSkyEnvironmentUpdate();
			}
		}

		// The track's sky (an HDRI photo, a gradient, a game sky) as a night sky: the same sky type in our profile,
		// with its brightness as a multiplier of the track's own, down to 1/2000 (a moonlit sky against daylight) at
		// full night. The track's own brightness is read while ours is off, so it is never compounded.
		private static System.Collections.Generic.Dictionary<int, System.Type> skyTypes_;

		// HDRP's table of sky types by id (SkyManager.skyTypesDict, internal)
		private static System.Collections.Generic.Dictionary<int, System.Type> SkyTypes() {
			if (skyTypes_ == null) {
				var manager = typeof(HDRenderPipeline).Assembly.GetType("UnityEngine.Rendering.HighDefinition.SkyManager");
				var property = manager?.GetProperty("skyTypesDict", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
				skyTypes_ = property?.GetValue(null) as System.Collections.Generic.Dictionary<int, System.Type>;
			}
			return skyTypes_;
		}

		private void DimSky(VisualEnvironment env, float night) {
			System.Type type = null;
			if (env != null && night > 0.0f && trackSky_ >= 0) {
				SkyTypes()?.TryGetValue(trackSky_, out type);
			}
			if (skyDim_ != null && (type == null || skyDim_.GetType() != type)) {
				skyDim_.active = false;
				if (type != null) {
					profile_.Remove(skyDim_.GetType());
					skyDim_ = null;
				}
			}
			if (type == null) {
				return;
			}
			if (skyDim_ == null) {
				skyDim_ = profile_.Add(type) as SkySettings;
				if (skyDim_ == null) {
					return;
				}
				skyDim_.active = false;
			}
			if (!skyDim_.active) {
				var own = VolumeOverhaul.Stack()?.GetComponent(type) as SkySettings;
				skyBase_ = own != null ? own.GetIntensityFromSettings() : 1.0f;
			}
			skyDim_.active = true;
			skyDim_.skyIntensityMode.Override(SkyIntensityMode.Multiplier);
			skyDim_.multiplier.Override(skyBase_ * Mathf.Lerp(1.0f, 0.0005f, night));
		}
	}
}

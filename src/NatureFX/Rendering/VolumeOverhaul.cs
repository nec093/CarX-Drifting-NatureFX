using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// A global HDRP Volume with very high priority for the ray tracing only mode: it switches the game's own screen
	// space effects off so that only the NatureFX ray traced ones are seen, and keeps exposure and tone under
	// NatureFX's control. Everything else falls through to the game's own track volumes.
	public class VolumeOverhaul {
		private const float Priority = 10000.0f;

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

		// Outside the ray tracing only mode the game's own look is left as it is
		public void Disable() {
			if (volume_ != null) {
				volume_.enabled = false;
			}
		}

		// Ray tracing only mode: only the NatureFX ray traced effects should be seen, so this volume turns the game's
		// own screen space effects off (reflections, GI, AO, contact shadows). Exposure and tone stay under NatureFX's
		// control: with the game's fixed exposure the ray traced shade looked flat and too dark.
		public void ApplyIsolation(ISettings s) {
			if (volume_ == null) {
				return;
			}
			volume_.enabled = true;
			ssr_.active = true;
			ssr_.enabled.Override(false);
			gi_.active = true;
			gi_.enable.Override(false);
			ao_.active = true;
			ao_.intensity.Override(0.0f);
			contact_.active = true;
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
		}
	}
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// A global HDRP Volume with very high priority. Only parameters we enable are overridden,
	// everything else keeps falling through to the game's own track volumes.
	public class VolumeOverhaul {
		private const float Priority = 10000.0f;

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;

		private GlobalIllumination gi_;
		private ScreenSpaceReflection ssr_;
		private ScreenSpaceAmbientOcclusion ao_;
		private ContactShadows contact_;
		private Fog fog_;
		private Bloom bloom_;
		private Tonemapping tonemapping_;
		private ColorAdjustments color_;
		private PathTracing pathTracing_;

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
			fog_ = profile_.Add<Fog>();
			bloom_ = profile_.Add<Bloom>();
			tonemapping_ = profile_.Add<Tonemapping>();
			color_ = profile_.Add<ColorAdjustments>();
			pathTracing_ = profile_.Add<PathTracing>();

			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.weight = 1.0f;
			volume_.sharedProfile = profile_;
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

		public void Apply(ISettings s, bool photoMode) {
			if (volume_ == null) {
				return;
			}

			volume_.enabled = s.Enabled;
			if (!s.Enabled) {
				return;
			}

			bool rt = s.RayTracing && Capabilities.RayTracingAvailable;
			var rtMode = s.RayTracingQuality > 0 ? RayTracingMode.Quality : RayTracingMode.Performance;
			var tracing = rt ? RayCastingMode.Mixed : RayCastingMode.RayMarching;

			// Global illumination: SSGI ray marching, or "mixed" (screen space first, RT fallback) when available.
			gi_.active = true;
			gi_.enable.Override(s.GlobalIllumination && (Capabilities.PipelineSsgi || rt));
			gi_.tracing.Override(tracing);
			gi_.mode.Override(rtMode);

			// Reflections
			ssr_.active = true;
			ssr_.enabled.Override(s.Reflections && (Capabilities.PipelineSsr || rt));
			ssr_.tracing.Override(tracing);
			ssr_.mode.Override(rtMode);

			// Ambient occlusion
			ao_.active = s.AmbientOcclusion;
			ao_.intensity.Override(s.AmbientOcclusionIntensity);
			ao_.radius.Override(1.5f);
			ao_.rayTracing.Override(rt);

			contact_.active = true;
			contact_.enable.Override(s.ContactShadows);
			contact_.length.Override(0.25f);

			// Fog: height fog always, volumetrics when the pipeline allows it
			fog_.active = s.FogDistance > 0.0f;
			fog_.enabled.Override(true);
			fog_.meanFreePath.Override(Mathf.Max(10.0f, s.FogDistance));
			fog_.maximumHeight.Override(Mathf.Max(1.0f, s.FogHeight));
			fog_.enableVolumetricFog.Override(s.VolumetricFog && Capabilities.PipelineVolumetrics);
			fog_.anisotropy.Override(0.6f);

			bloom_.active = s.Bloom > 0.0f;
			bloom_.intensity.Override(s.Bloom);
			bloom_.scatter.Override(0.7f);

			tonemapping_.active = s.AcesTonemapping;
			tonemapping_.mode.Override(TonemappingMode.ACES);

			color_.active = true;
			color_.postExposure.Override(s.Exposure);
			color_.saturation.Override(s.Saturation);
			color_.contrast.Override(s.Contrast);

			// Real path tracing converges over many frames and resets whenever the camera moves,
			// so it is only enabled while the photo mode toggle is active.
			bool pt = photoMode && s.PhotoModePathTracing && Capabilities.RayTracingAvailable;
			pathTracing_.active = pt;
			pathTracing_.enable.Override(pt);
			pathTracing_.maximumSamples.Override(Mathf.Clamp(s.PathTracingSamples, 1, 4096));
			pathTracing_.maximumDepth.Override(Mathf.Clamp(s.PathTracingBounces, 1, 10));
		}
	}
}

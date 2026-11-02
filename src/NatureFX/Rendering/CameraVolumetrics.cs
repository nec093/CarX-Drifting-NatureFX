using KSL.API;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// A camera draws HDRP's volumetric effects only with them on in its frame settings, and the game's camera may have
	// them off even though its HDRP asset supports them: local fog and the tyre smoke need Volumetrics, the sky's
	// clouds need VolumetricClouds (off on the game camera: NatureFX's clouds never showed). While NatureFX needs one,
	// it is switched on for that camera (one overridden field, the rest as the game set it), and back off when no
	// longer needed.
	public class CameraVolumetrics {
		private readonly FrameSettingsField field_;
		private readonly string what_;
		private Camera camera_;
		private bool overridden_;
		private bool hadMaskBit_;
		private bool hadValue_;
		// overrides of all instances on the game camera: custom frame settings stay on while any is active
		private static int active_;
		private static bool hadCustom_; // the game's own value, from before the first override

		public CameraVolumetrics(FrameSettingsField field, string what) {
			field_ = field;
			what_ = what;
		}

		// what the camera does now, for the Map tab
		public string State { get; private set; } = "";

		public void Update(Camera camera, bool needed) {
			if (camera != camera_) {
				Restore();
				camera_ = camera;
			}
			if (camera_ == null) {
				State = "no camera";
				return;
			}
			bool on = HDCamera.GetOrCreate(camera_).frameSettings.IsEnabled(field_);
			if (needed && !on && !overridden_) {
				var data = camera_.GetComponent<HDAdditionalCameraData>();
				if (data == null) {
					State = "camera has no HDRP data";
					return;
				}
				if (active_ == 0) {
					hadCustom_ = data.customRenderingSettings;
				}
				hadMaskBit_ = data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field_];
				hadValue_ = data.renderingPathCustomFrameSettings.IsEnabled(field_);
				data.customRenderingSettings = true;
				data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field_] = true;
				data.renderingPathCustomFrameSettings.SetEnabled(field_, true);
				overridden_ = true;
				++active_;
				Kino.Log.Info($"[NatureFX] The game camera had {field_} off: switched on for {what_}");
			} else if (!needed && overridden_) {
				Restore();
			}
			State = overridden_ ? $"{field_} on (switched on by NatureFX)" : on ? $"{field_} on" : $"{field_} off";
		}

		public void Restore() {
			if (!overridden_) {
				return;
			}
			overridden_ = false;
			--active_; // counted when it was switched on, whether or not the camera still exists
			if (camera_ == null) {
				return;
			}
			var data = camera_.GetComponent<HDAdditionalCameraData>();
			if (data != null) {
				data.renderingPathCustomFrameSettings.SetEnabled(field_, hadValue_);
				data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field_] = hadMaskBit_;
				// the other override still needs the custom settings
				if (active_ <= 0) {
					data.customRenderingSettings = hadCustom_;
				}
			}
			overridden_ = false;
		}
	}
}

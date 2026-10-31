using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Decides when a temporal pass must drop its history: on request (F8), after a camera cut (replay and camera
	// switches teleport the view, and the motion vectors do not know) and when the pass did not run last frame (its
	// history shows an old view). A kept history in those cases burns old shadows and reflections into the image.
	public class HistoryGuard {
		private const float CutDistance = 8.0f;  // m in one frame: well beyond a car at full speed
		private const float CutAngle = 40.0f;    // degrees in one frame

		private int lastFrame_ = -10;
		private Vector3 lastPosition_;
		private Vector3 lastForward_;
		private bool resetRequested_ = true;
		private float lastFov_;

		// Frames in a row the camera has not moved (0: it moved or the history was dropped this frame)
		public int StillFrames { get; private set; }
		private const float StillDistance = 0.002f;  // m: what a parked chase camera still sways
		private const float StillAngle = 0.02f;      // degrees

		public void RequestReset() {
			resetRequested_ = true;
		}

		// Once per execution; true when this frame must start from the new samples alone
		public bool CheckReset(HDCamera hdCamera) {
			var t = hdCamera.camera.transform;
			bool reset = resetRequested_
			             || lastFrame_ != Time.frameCount - 1
			             || (t.position - lastPosition_).sqrMagnitude > CutDistance * CutDistance
			             || Vector3.Angle(t.forward, lastForward_) > CutAngle;
			bool still = !reset && (t.position - lastPosition_).sqrMagnitude < StillDistance * StillDistance
			             && Vector3.Angle(t.forward, lastForward_) < StillAngle && Mathf.Abs(hdCamera.camera.fieldOfView - lastFov_) < 0.01f;
			StillFrames = still ? StillFrames + 1 : 0;
			resetRequested_ = false;
			lastFrame_ = Time.frameCount;
			lastPosition_ = t.position;
			lastForward_ = t.forward;
			lastFov_ = hdCamera.camera.fieldOfView;
			return reset;
		}

		// Progressive accumulation while the camera stays put: the n-th still frame weighs 1 / (frames the history
		// already stands for + n), so the image converges to the mean of every frame since it stopped (down to
		// minBlend), then holds still. Moving: blend as it is.
		public float StillBlend(float blend, float minBlend) {
			if (StillFrames == 0 || blend >= 1.0f) {
				return blend;
			}
			// photo mode: no floor, the mean of every frame since the camera stopped
			if (PhotoMode.Active) {
				minBlend = 1.0f / 65536.0f;
			}
			float progressive = 1.0f / (1.0f / Mathf.Max(blend, 1e-3f) + StillFrames);
			return Mathf.Max(progressive, Mathf.Min(minBlend, blend));
		}
	}
}

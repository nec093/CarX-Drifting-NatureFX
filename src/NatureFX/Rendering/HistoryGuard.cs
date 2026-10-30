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
			resetRequested_ = false;
			lastFrame_ = Time.frameCount;
			lastPosition_ = t.position;
			lastForward_ = t.forward;
			return reset;
		}
	}
}

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// One full resolution RGBA16F copy of the camera colour, shared by every custom pass that composites onto the camera
	// colour and so needs to read a copy of it (the shadow, reflection, atmosphere and glass composites, the own
	// post-processing, the car rain, night vision). Each of them copies the camera colour into it at the start of its
	// own Execute and reads it only inside that Execute; HDRP runs the custom passes one after another, so no pass
	// depends on what another left in it. One texture instead of seven: 4K RGBA16F is 63 MB each.
	// Random write is on: the own post-processing's DLAA writes it back in place (UnpackDlss, a compute shader).
	// Reference counted: each pass Acquires in Setup and Releases in Cleanup; the last Release frees the texture.
	public static class SharedColor {
		private static RTHandle handle_;
		private static int users_;

		public static RTHandle Acquire() {
			++users_;
			if (handle_ == null) {
				handle_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
					colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, name: "NFX_SharedColor");
			}
			return handle_;
		}

		// call once per Acquire (with the handle it returned); null is ignored, so Cleanup without Setup is safe
		public static void Release(RTHandle handle) {
			if (handle == null || handle != handle_) {
				return;
			}
			if (--users_ <= 0) {
				users_ = 0;
				handle_.Release();
				handle_ = null;
			}
		}
	}
}

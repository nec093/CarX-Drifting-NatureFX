using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The camera ray per pixel that the ray traced reflections and shadows both need (the colour and metalness
	// HDRP's normal buffer lacks, the exact normal and position). The own engine's Surface trace (Surface.hlsl) traces
	// it once per frame at full resolution into two textures.
	public class RTSurfaceTrace {
		public static readonly int AlbedoTex = Shader.PropertyToID("_NFX_SurfaceAlbedo");
		public static readonly int NormalTex = Shader.PropertyToID("_NFX_SurfaceNormal");

		private RTHandle albedo_, normal_;
		private int tracedFrame_ = -1;

		// The camera the ray traced passes work for: the game's view on screen (not probes, mirrors rendering into
		// textures or editor previews)
		public static bool IsScreenCamera(HDCamera camera) {
			return camera.camera.cameraType == CameraType.Game && camera.camera.targetTexture == null;
		}

		public RTHandle Albedo => albedo_;
		public RTHandle Normal => normal_;

		// Traces this frame's surfaces unless another pass already did
		public void EnsureTraced(CustomPassContext ctx) {
			if (tracedFrame_ == Time.frameCount || !NativeEngine.Active) {
				return;
			}
			tracedFrame_ = Time.frameCount;
			if (albedo_ == null) {
				albedo_ = Alloc("NFX_RTSurface_Albedo", GraphicsFormat.R8G8B8A8_UNorm);
				// 32 bit: the exact normal is there to beat the G-buffer's quantised one
				normal_ = Alloc("NFX_RTSurface_Normal", GraphicsFormat.R32G32B32A32_SFloat);
			}
			var cmd = ctx.cmd;
			RTTimings.Surface.Begin(cmd);
			NativeEngine.TraceSurface(cmd, ctx, albedo_, normal_);
			RTTimings.Surface.End(cmd);
		}

		private static RTHandle Alloc(string name, GraphicsFormat format) {
			return RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: format, enableRandomWrite: true, useDynamicScale: true, name: name);
		}

		public void Release() {
			albedo_?.Release();
			normal_?.Release();
			albedo_ = null;
			normal_ = null;
		}
	}
}

using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// The sun's ray traced shadow map for transparent surfaces (NFX_TransparentShadow.hlsl): transparent surfaces have no
	// depth on screen, so the screen space traced shadows cannot reach them. A grid across the sun's direction around the
	// camera (Cells x Cells of CellSize m), each cell the distance from far up the sun's direction down to the first thing
	// the sun meets (NFXNative SunMap.hlsl). Traced a band of rows a frame into the back map; when it is whole the maps
	// swap and the next one starts where the camera and the sun are then (cars' shadows lag a fraction of a second).
	public static class SunShadowMap {
		private const int Cells = 1024;
		private const float CellSize = 0.2f;        // m: the map covers about 200 m
		private const int RowsPerFrame = 64;        // a whole map in 16 frames
		private const float Start = 300.0f;         // m up the sun's direction the rays start
		private const float Bias = 0.2f;            // m

		private static readonly int MapId = Shader.PropertyToID("_NFX_SunMap");
		private static readonly int OriginId = Shader.PropertyToID("_NFX_SunMapOrigin");
		private static readonly int DirId = Shader.PropertyToID("_NFX_SunMapDir");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_SunMapParams");

		private static GraphicsBuffer front_, back_;
		private static Vector4 frontOrigin_, frontDir_, backOrigin_, backDir_;
		private static int row_;
		private static int frame_ = -1;

		public static string Status { get; private set; } = "off";

		// Shadow pass, after NativeEngine.BeginFrame: the next band of rows, and the finished map for this frame's
		// transparent surfaces. sun: towards the sun, w > 0 when it is up.
		public static void Update(CommandBuffer cmd, Camera cam, Vector4 sun, bool on) {
			if (!on || sun.w <= 0.0f || !NativeEngine.Supports("sun-map")) {
				cmd.SetGlobalVector(OriginId, Vector4.zero);
				Status = !on ? "off" : sun.w <= 0.0f ? "the sun is down" : "waiting for the engine's new DLL";
				return;
			}
			if (frame_ == Time.frameCount) {
				return;
			}
			frame_ = Time.frameCount;
			if (front_ == null) {
				front_ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cells * Cells, 4) { name = "NFX_SunMapA" };
				back_ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cells * Cells, 4) { name = "NFX_SunMapB" };
				row_ = 0;
			}
			if (row_ == 0) {
				// a new map: centred on the camera (on whole cells, so still things keep their place), the sun as it is now
				var p = cam.transform.position;
				float step = CellSize * 16.0f;
				var centre = new Vector3(Mathf.Round(p.x / step) * step, Mathf.Round(p.y / step) * step, Mathf.Round(p.z / step) * step);
				backOrigin_ = new Vector4(centre.x, centre.y, centre.z, CellSize);
				backDir_ = new Vector4(sun.x, sun.y, sun.z, Cells);
			}
			int rows = Mathf.Min(RowsPerFrame, Cells - row_);
			NativeEngine.TraceSunMap(cmd, back_, backOrigin_, backDir_, row_, rows, Cells, Start);
			row_ += rows;
			if (row_ >= Cells) {
				(front_, back_) = (back_, front_);
				frontOrigin_ = backOrigin_;
				frontDir_ = backDir_;
				row_ = 0;
			}
			cmd.SetGlobalBuffer(MapId, front_);
			cmd.SetGlobalVector(OriginId, frontOrigin_.w > 0.0f ? frontOrigin_ : Vector4.zero);
			cmd.SetGlobalVector(DirId, frontDir_);
			cmd.SetGlobalVector(ParamsId, new Vector4(Start, Bias, 0.0f, 0.0f));
			Status = frontOrigin_.w > 0.0f
				? $"{Cells}x{Cells} cells of {CellSize * 100.0f:0} cm ({Cells * CellSize:0} m) around the camera, a new map every {Cells / RowsPerFrame} frames"
				: "tracing the first map";
		}

		public static void Release() {
			front_?.Release();
			back_?.Release();
			front_ = back_ = null;
			frontOrigin_ = Vector4.zero;
			row_ = 0;
		}
	}
}

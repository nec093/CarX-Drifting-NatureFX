using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// The ground seen from above around the camera (NFXNative GroundMap.hlsl): Cells x Cells of CellSize m, each its
	// colour, height, normal and kind (road or ground), from one ray straight down onto the ground instances (terrain,
	// wide flat meshes; NativeEngine flags them). What the foot of the plants' cards reads (NFX_Ground.hlsl
	// NFX_ApplyFoliageFoot: shaded and the ground's colour where they stand in it). Traced a band of rows a frame into
	// the back map; when it is whole the maps swap and the next one starts where the camera is then.
	public static class GroundMap {
		public const int Cells = 1024;
		public const float CellSize = 0.125f;      // m: the map covers 128 m
		private const int RowsPerFrame = 64;       // a whole map in 16 frames
		private const float Start = 200.0f;        // m above the camera the rays start

		private static readonly int MapId = Shader.PropertyToID("_NFX_GroundMap");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_GroundMapParams");

		private static GraphicsBuffer front_, back_;
		private static Vector4 frontParams_, backOrigin_;
		private static int row_;
		private static int frame_ = -1;

		// the map in use (null: none yet), its centre (x, z), cell size and cells per side; a new one each Generation
		public static GraphicsBuffer Map => frontParams_.w > 0.0f ? front_ : null;
		public static Vector4 Params => frontParams_;
		public static int Generation { get; private set; }
		public static string Status { get; private set; } = "off";

		// Shadow pass, after NativeEngine.BeginFrame: the next band of rows, and the finished map for this frame
		public static void Update(CommandBuffer cmd, Camera cam, bool on) {
			if (!on || !NativeEngine.Supports("ground-map")) {
				cmd.SetGlobalVector(ParamsId, Vector4.zero);
				frontParams_ = Vector4.zero;
				Status = !on ? "off" : "waiting for the engine's new DLL";
				return;
			}
			if (frame_ == Time.frameCount) {
				return;
			}
			frame_ = Time.frameCount;
			if (front_ == null) {
				front_ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cells * Cells * 2, 16) { name = "NFX_GroundMapA" };
				back_ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Cells * Cells * 2, 16) { name = "NFX_GroundMapB" };
				row_ = 0;
			}
			if (row_ == 0) {
				// a new map: centred on the camera, on whole 16 cells (still things keep their place)
				var p = cam.transform.position;
				float step = CellSize * 16.0f;
				backOrigin_ = new Vector4(Mathf.Round(p.x / step) * step, p.y, Mathf.Round(p.z / step) * step, CellSize);
			}
			int rows = Mathf.Min(RowsPerFrame, Cells - row_);
			NativeEngine.TraceGroundMap(cmd, back_, backOrigin_, row_, rows, Cells, Start);
			row_ += rows;
			if (row_ >= Cells) {
				(front_, back_) = (back_, front_);
				frontParams_ = new Vector4(backOrigin_.x, backOrigin_.z, CellSize, Cells);
				row_ = 0;
				++Generation;
			}
			cmd.SetGlobalBuffer(MapId, front_);
			cmd.SetGlobalVector(ParamsId, frontParams_);
			Status = frontParams_.w > 0.0f
				? $"{Cells}x{Cells} cells of {CellSize * 100.0f:0.#} cm ({Cells * CellSize:0} m) around the camera"
				: "tracing the first map";
		}

		public static void Release() {
			front_?.Release();
			back_?.Release();
			front_ = back_ = null;
			frontParams_ = Vector4.zero;
			row_ = 0;
		}
	}
}

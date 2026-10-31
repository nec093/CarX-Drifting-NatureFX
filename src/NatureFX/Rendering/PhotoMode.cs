using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Averages the finished HDR frames into a texture while the camera rests (NFX_PhotoAccumulate.shader), and shows the
	// average. Before post-processing: the anti-aliasing's jitter averages out too.
	public class PhotoAccumulatePass : CustomPass {
		private static readonly int SourceId = Shader.PropertyToID("_NFX_PhotoSource");
		private static readonly int WeightId = Shader.PropertyToID("_NFX_PhotoWeight");

		public Material Material;
		private RTHandle average_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			average_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R32G32B32A32_SFloat, useDynamicScale: true, name: "NFX_Photo_Average");
		}

		protected override void Execute(CustomPassContext ctx) {
			// the screen's camera only: a second game camera (mirror, picture in picture) averaged its view in too
			if (Material == null || !RTSurfaceTrace.IsScreenCamera(ctx.hdCamera) || !PhotoMode.Active) {
				return;
			}
			if (PhotoMode.Frames < PhotoMode.Samples) {
				// this frame into the average: the first replaces it (the target cleared first: weight 1 still
				// multiplies what was there by 0, and 0 times a NaN left in it stays NaN), the n-th weighs 1 / n
				ctx.cmd.SetGlobalTexture(SourceId, ctx.cameraColorBuffer);
				ctx.cmd.SetGlobalFloat(WeightId, 1.0f / (PhotoMode.Frames + 1));
				CoreUtils.SetRenderTarget(ctx.cmd, average_, PhotoMode.Frames == 0 ? ClearFlag.Color : ClearFlag.None, Color.clear);
				CoreUtils.DrawFullScreen(ctx.cmd, Material, null, 0);
				PhotoMode.Frames++;
			}
			ctx.cmd.SetGlobalTexture(SourceId, average_);
			CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(ctx.cmd, Material, null, 1);
		}

		protected override void Cleanup() {
			average_?.Release();
		}
	}

	// Photo mode (F9): a reference quality still. Every ray traced effect runs at its best (full resolution traces,
	// every GI bounce, second reflection bounce) through the usual denoisers, and while the camera rests the finished
	// frames are averaged (PhotoAccumulatePass): the residual noise of each frame averages out, up to Samples frames,
	// then the picture holds. A chase camera never stands perfectly still (the car idles), so only a move of a few
	// centimetres or a fraction of a degree starts over. The game itself keeps running (time is not touched).
	public static class PhotoMode {
		public const string ShaderName = "Hidden/NatureFX/PhotoAccumulate";
		public const int Samples = 256;
		private const float MoveDistance = 0.05f; // m
		private const float MoveAngle = 0.3f;     // degrees

		public static bool Active { get; private set; }
		// frames averaged so far (PhotoAccumulatePass counts them as it draws)
		public static int Frames;

		private static Vector3 position_;
		private static Vector3 forward_;
		private static GUIStyle style_;
		private static GameObject root_;
		private static PhotoAccumulatePass pass_;
		private static Material material_;

		public static void Create(ShaderBundle bundle) {
			var shader = bundle.Get(ShaderName);
			if (shader == null) {
				Kino.Log.Warning($"[NatureFX] {ShaderName} not in the shader bundle; photo mode averages nothing");
				return;
			}
			material_ = new Material(shader) { hideFlags = HideFlags.DontSave };
			root_ = new GameObject("NatureFX.Photo");
			Object.DontDestroyOnLoad(root_);
			var volume = root_.AddComponent<CustomPassVolume>();
			volume.isGlobal = true;
			volume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
			// enabled with F9 only: its full resolution float average is allocated when the pass first runs
			pass_ = new PhotoAccumulatePass { name = "NatureFX Photo", Material = material_, enabled = Active };
			volume.customPasses.Add(pass_);
		}

		public static void Reload(ShaderBundle bundle) {
			if (material_ == null) {
				Create(bundle);
				return;
			}
			var shader = bundle.Get(ShaderName);
			if (shader != null) {
				material_.shader = shader;
			}
		}

		public static void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (material_ != null) {
				Object.Destroy(material_);
			}
			root_ = null;
			material_ = null;
		}

		public static void Toggle() {
			Active = !Active;
			Frames = 0;
			if (pass_ != null) {
				pass_.enabled = Active;
			}
		}

		// Once per frame before rendering: start over when the camera has really moved
		public static void Update(Camera camera) {
			if (!Active || camera == null) {
				return;
			}
			var t = camera.transform;
			if ((t.position - position_).sqrMagnitude > MoveDistance * MoveDistance || Vector3.Angle(t.forward, forward_) > MoveAngle) {
				Frames = 0;
				position_ = t.position;
				forward_ = t.forward;
			}
		}

		public static void OnGUI() {
			if (!Active || Event.current.type != EventType.Repaint) {
				return;
			}
			style_ ??= new GUIStyle(GUI.skin.label) { fontSize = 16, alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(1.0f, 1.0f, 1.0f, 0.85f) } };
			string text = material_ == null ? "PHOTO  ·  needs the new shader bundle"
				: Frames >= Samples ? $"PHOTO  ·  done ({Samples} frames)" : $"PHOTO  ·  {Frames} / {Samples} frames";
			var r = new Rect(Screen.width * 0.5f - 170.0f, 12.0f, 340.0f, 26.0f);
			GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.45f);
			GUI.DrawTexture(r, Texture2D.whiteTexture);
			GUI.color = Color.white;
			GUI.Label(r, text, style_);
		}
	}
}

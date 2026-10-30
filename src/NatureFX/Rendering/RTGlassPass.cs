using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Ray traced reflections in glass (own engine only, Glass.hlsl). Glass is drawn transparent after the opaque
	// passes, so this runs before post processing, on the colour with the glass in it: per pixel the closest glass in
	// front of the opaque surface mirrors the scene, and the composite (NFX_RTReflect.shader, GlassComposite) swaps
	// the sky HDRP mirrored there for what the ray found. One mirror ray per pixel: no history to accumulate.
	public class RTGlassPass : CustomPass {
		private const int PassGlassComposite = 3; // NFX_RTReflect.shader

		private static readonly int ColorTex = Shader.PropertyToID("_NFX_ColorTex");
		private static readonly int GlassTex = Shader.PropertyToID("_NFX_GlassTex");
		private static readonly int GlassCompose = Shader.PropertyToID("_NFX_GlassCompose");

		public Material CompositeMaterial; // NFX_RTReflect
		public RayTracingScene Scene;
		public float Intensity = 1.0f;
		public float MaxDistance = 100.0f; // m
		public float Refraction = 0.35f;   // how far the bend of the glass is followed (0: none, 1: one surface of full glass)

		public string Status { get; private set; } = "not run yet";

		private readonly MaterialPropertyBlock props_ = new();
		private RTHandle color_, glass_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = Alloc("NFX_RTGlass_Color");
			glass_ = Alloc("NFX_RTGlass_Trace");
		}

		private static RTHandle Alloc(string name) {
			return RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, name: name);
		}

		protected override void Execute(CustomPassContext ctx) {
			if (!RTSurfaceTrace.IsScreenCamera(ctx.hdCamera)) {
				return;
			}
			if (CompositeMaterial == null || NativeEngine.Bridge == null) {
				Status = "unavailable (needs the shader bundle)";
				return;
			}
			if (!NativeEngine.Active) {
				Status = $"unavailable (own engine not running: {NativeEngine.Info})";
				return;
			}
			if (Scene == null || Scene.GlassCount == 0) {
				Status = "no glass in the scene";
				return;
			}
			var exposure = HdrpBindings.ExposureTexture(ctx.hdCamera);
			if (exposure == null) {
				Status = "unavailable (HDRP exposure not reachable)";
				return;
			}

			var cmd = ctx.cmd;
			int width = ctx.hdCamera.actualWidth;
			int height = ctx.hdCamera.actualHeight;
			var size = new Vector4(width, height, 1.0f / width, 1.0f / height);
			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			RTTimings.Glass.Begin(cmd);
			NativeEngine.TraceGlass(cmd, ctx, color_, exposure, glass_, size, new Vector4(MaxDistance, 1.0f, Refraction, 0.0f));
			RTTimings.Glass.End(cmd);

			props_.Clear();
			props_.SetTexture(ColorTex, color_);
			props_.SetTexture(GlassTex, glass_);
			props_.SetVector(GlassCompose, new Vector4(Intensity, 0.0f, 0.0f, 0.0f));
			CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, CompositeMaterial, props_, PassGlassComposite);
			Status = $"own engine (DXR 1.1): {Scene.GlassCount} glass surfaces, {width}x{height}";
		}

		protected override void Cleanup() {
			color_?.Release();
			glass_?.Release();
		}
	}
}

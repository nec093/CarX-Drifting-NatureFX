using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Ray traced reflections (DX12, the own engine): NativeEngine.TraceReflection (Reflection.hlsl) traces one GGX ray
	// per pixel of the smooth surfaces; NFX_RTReflect.shader accumulates it over frames (reprojecting the reflected
	// image), denoises it by roughness and blends it over the camera colour. Runs after the shadow pass in the same
	// custom pass volume, so the GI and shadows are part of what gets reflected on screen.
	public class RTReflectionPass : CustomPass {
		private const int PassReflectionDenoise = 1; // NFX_RTReflect.shader: blur follows the roughness
		private const int PassReflectionTemporal = 2; // NFX_RTReflect.shader: reprojects the reflected image

		private static readonly int ColorTex = Shader.PropertyToID("_NFX_ColorTex");
		private static readonly int TraceTex = Shader.PropertyToID("_NFX_TraceTex");
		private static readonly int HistoryTex = Shader.PropertyToID("_NFX_HistoryTex");
		private static readonly int SourceTex = Shader.PropertyToID("_NFX_SourceTex");
		private static readonly int TargetSize = Shader.PropertyToID("_NFX_TargetSize");
		private static readonly int ComposeParams = Shader.PropertyToID("_NFX_ComposeParams");
		private static readonly int DenoiseStep = Shader.PropertyToID("_NFX_DenoiseStep");
		private static readonly int LowSize = Shader.PropertyToID("_NFX_LowSize");
		private static readonly int ReflCompose = Shader.PropertyToID("_NFX_ReflCompose");
		private static readonly int DistTex = Shader.PropertyToID("_NFX_DistTex");
		private static readonly int ExposureTexture = Shader.PropertyToID("_ExposureTexture");

		public Material CompositeMaterial; // NFX_RTReflect: composite, denoise, temporal
		public RayTracingScene Scene;
		public RTSurfaceTrace Surfaces;
		public LocalLights Lights;
		public Vector4 SunDirection;
		public Vector4 SunIlluminance;

		public float Intensity = 1.0f;
		public float RoughnessCutoff = 0.5f;
		public float MaxDistance = 150.0f;
		// share of the new frame; the history follows the reflected image (Temporal pass), so it can be kept longer,
		// which averages the per-frame jitter into smooth edges
		public float TemporalBlend = 0.2f;
		public bool FullResolution;
		public bool SecondBounce = true;
		// wet roads stretch the reflections along the view by the weather's wetness (NatureFX.Nature.Weather)
		public bool WetStreaks = true;

		public string Status { get; private set; } = "not run yet";

		private readonly MaterialPropertyBlock props_ = new();
		public readonly HistoryGuard History = new();
		private RTHandle color_, trace_, denoise_, historyA_, historyB_, distance_;
		private bool allocatedFull_;
		private bool flip_;
		private int frame_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = SharedColor.Acquire();
			AllocTraceTargets();
		}

		private void AllocTraceTargets() {
			allocatedFull_ = FullResolution;
			var scale = Vector2.one * (FullResolution ? 1.0f : 0.5f);
			trace_ = Alloc(scale, "NFX_RTRefl_Trace");
			// the own filter's targets only while it runs (under NRD REBLUR_SPECULAR denoises the trace itself)
			distance_ = RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R32_SFloat, enableRandomWrite: true, useDynamicScale: true, name: "NFX_RTRefl_Distance");
		}

		private void ReleaseTraceTargets() {
			trace_?.Release();
			ReleaseFilterTargets();
			distance_?.Release();
		}

		private void ReleaseFilterTargets() {
			denoise_?.Release();
			historyA_?.Release();
			historyB_?.Release();
			denoise_ = historyA_ = historyB_ = null;
		}

		// the own filter's history and blur target; true when just made (their history is whatever memory held)
		private bool EnsureFilterTargets() {
			if (denoise_ != null) {
				return false;
			}
			var scale = Vector2.one * (allocatedFull_ ? 1.0f : 0.5f);
			denoise_ = Alloc(scale, "NFX_RTRefl_Denoise");
			historyA_ = Alloc(scale, "NFX_RTRefl_HistoryA");
			historyB_ = Alloc(scale, "NFX_RTRefl_HistoryB");
			return true;
		}

		private static RTHandle Alloc(Vector2 scale, string name) {
			return RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, name: name);
		}

		protected override void Execute(CustomPassContext ctx) {
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: reflections (RT)"); // (the debug page)
			// only the camera drawing to the screen: probes, mirrors and previews would flip the status (and with it
			// HDRP's sun shadows) every frame and mix their images into the history
			if (!RTSurfaceTrace.IsScreenCamera(ctx.hdCamera)) {
				return;
			}
			if (CompositeMaterial == null || Scene == null || Surfaces == null) {
				Status = "unavailable (needs DX12 ray tracing and the shader bundle)";
				return;
			}
			if (!NativeEngine.Active) {
				Status = $"unavailable (own engine not running: {NativeEngine.Info})";
				return;
			}
			var exposure = HdrpBindings.ExposureTexture(ctx.hdCamera);
			var ambient = HdrpBindings.AmbientProbe(ctx.hdCamera);
			if (exposure == null || ambient == null) {
				Status = "unavailable (HDRP exposure or sky probe not reachable)";
				return;
			}

			if (allocatedFull_ != FullResolution) {
				ReleaseTraceTargets();
				AllocTraceTargets();
				History.RequestReset(); // the new history textures hold whatever memory had (NaN stays for good in a lerp)
			}

			var cmd = ctx.cmd;
			bool resetHistory = History.CheckReset(ctx.hdCamera);
			if (NativeEngine.NrdReflections) {
				ReleaseFilterTargets();
			} else if (EnsureFilterTargets()) {
				resetHistory = true;
			}
			int width = ctx.hdCamera.actualWidth;
			int height = ctx.hdCamera.actualHeight;
			// rounded like the RTHandle system sizes the half resolution targets (width / 2 left their last column
			// untraced on odd widths)
			float scale = FullResolution ? 1.0f : 0.5f;
			int tw = Mathf.Max(1, Mathf.RoundToInt(width * scale));
			int th = Mathf.Max(1, Mathf.RoundToInt(height * scale));
			var low = new Vector4(tw, th, 1.0f / tw, 1.0f / th);

			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);

			// 1. trace (the own engine, NativeEngine)
			NativeEngine.BeginFrame(ctx.hdCamera.camera, SunDirection, 0.0f, SunIlluminance, frame_, Vector4.zero, Lights, ambient);
			Surfaces.EnsureTraced(ctx);
			RTTimings.ReflectionTrace.Begin(cmd);
			NativeEngine.TraceReflection(cmd, ctx, color_, exposure, Surfaces.Albedo, Surfaces.Normal, trace_, distance_, low,
				new Vector4(MaxDistance, RoughnessCutoff, SecondBounce ? 1.0f : 0.0f, WetStreaks ? Mathf.Clamp01(Mathf.Max(NatureFX.Nature.Weather.Current.Wetness, NatureFX.Nature.Weather.Current.Rain)) : 0.0f));
			RTTimings.ReflectionTrace.End(cmd);
			RTTimings.ReflectionFilter.Begin(cmd);

			// 2. temporal accumulation with the reprojected history, 3. two a-trous iterations; under NRD
			// (NativeEngine.NrdReflections) REBLUR_SPECULAR did both on the trace already: composited as it is
			props_.Clear();
			props_.SetTexture(RTSurfaceTrace.AlbedoTex, Surfaces.Albedo); // clear coat flag
			props_.SetTexture(RTSurfaceTrace.NormalTex, Surfaces.Normal);
			props_.SetVector(TargetSize, low);
			props_.SetVector(LowSize, low);
			// still camera: progressive accumulation down to 1/64 (HistoryGuard.StillBlend)
			props_.SetVector(ComposeParams, new Vector4(1.0f, 0.0f, resetHistory ? 1.0f : History.StillBlend(Mathf.Clamp01(TemporalBlend), 1.0f / 64.0f), frame_));
			var history = flip_ ? historyA_ : historyB_;
			var previous = flip_ ? historyB_ : historyA_;
			// (DLSS Ray Reconstruction is not given them raw: one ray on a rough floor or a tyre left white specks it
			// did not take out; mirrors keep the raw trace under REBLUR anyway)
			if (!NativeEngine.NrdReflections) {
				props_.SetTexture(TraceTex, trace_);
				props_.SetTexture(HistoryTex, previous);
				props_.SetTexture(DistTex, distance_);
				CoreUtils.SetRenderTarget(cmd, history, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, CompositeMaterial, props_, PassReflectionTemporal);

				props_.SetTexture(SourceTex, history);
				props_.SetFloat(DenoiseStep, 1.0f);
				CoreUtils.SetRenderTarget(cmd, denoise_, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, CompositeMaterial, props_, PassReflectionDenoise);

				props_.SetTexture(SourceTex, denoise_);
				props_.SetFloat(DenoiseStep, 2.0f);
				CoreUtils.SetRenderTarget(cmd, trace_, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, CompositeMaterial, props_, PassReflectionDenoise);
			}


			// 4. composite over the camera colour
			props_.SetTexture(ColorTex, color_);
			props_.SetTexture(SourceTex, trace_);
			props_.SetVector(ReflCompose, new Vector4(Intensity, RoughnessCutoff, 0.0f, 0.0f));
			CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, CompositeMaterial, props_, 0);
			RTTimings.ReflectionFilter.End(cmd);

			flip_ = !flip_;
			++frame_;
			Status = $"own engine ({NativeEngine.ModeName}) reflections, {tw}x{th}{(SecondBounce ? ", second bounce on metal" : "")}";
		}

		protected override void Cleanup() {
			SharedColor.Release(color_);
			color_ = null;
			ReleaseTraceTargets();
		}
	}
}

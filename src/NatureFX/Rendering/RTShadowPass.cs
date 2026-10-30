using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RendererUtils;

namespace NatureFX.Rendering {
	// Ray traced sun shadows, ambient light / GI, light shafts and local light shadows (DX12, the own engine). HDRP's
	// own sun shadows are switched off while this runs (RayTracingEffects.UpdateRayTracing), so the camera colour
	// holds the unshadowed sun. NativeEngine traces whether one point of the sun's disc is visible and how far the
	// blocker is (full or half resolution), and the ambient light at half resolution. NFX_RTShadow.shader accumulates
	// and blurs both (the sun by the width of its penumbra) and, per full resolution pixel, takes the sun light HDRP
	// added (exactly, or an estimate) away where it is blocked and scales the rest of the light by the ambient term.
	// Runs first in the custom pass volume, so GI and reflections see the shadowed image.
	public class RTShadowPass : CustomPass {
		private const int PassTemporal = 0; // NFX_RTShadow.shader
		private const int PassDenoise = 1;
		private const int PassComposite = 2;
		private const int PassDenoiseSun = 3;

		private static readonly int ColorTex = Shader.PropertyToID("_NFX_ColorTex");
		private static readonly int ShadowTex = Shader.PropertyToID("_NFX_ShadowTex");
		private static readonly int HistoryTex = Shader.PropertyToID("_NFX_HistoryTex");
		private static readonly int SourceTex = Shader.PropertyToID("_NFX_SourceTex");
		private static readonly int SunTex = Shader.PropertyToID("_NFX_SunTex");
		private static readonly int AmbientTex = Shader.PropertyToID("_NFX_AmbientTex");
		private static readonly int TargetSize = Shader.PropertyToID("_NFX_TargetSize");
		private static readonly int SunSize = Shader.PropertyToID("_NFX_SunSize");
		private static readonly int AmbientSize = Shader.PropertyToID("_NFX_AmbientSize");
		private static readonly int SunFilter = Shader.PropertyToID("_NFX_SunFilter");
		private static readonly int ShadowCompose = Shader.PropertyToID("_NFX_ShadowCompose");
		private static readonly int DenoiseStep = Shader.PropertyToID("_NFX_DenoiseStep");
		private static readonly int DenoiseMode = Shader.PropertyToID("_NFX_DenoiseMode");
		private static readonly int TraceJitter = Shader.PropertyToID("_NFX_TraceJitter");
		private static readonly int AmbientProbeId = Shader.PropertyToID("_NFX_AmbientProbe");
		private static readonly int RTSunDir = Shader.PropertyToID("_NFX_RTSunDir");
		private static readonly int RTSunColor = Shader.PropertyToID("_NFX_RTSunColor");
		private static readonly int LeafParams = Shader.PropertyToID("_NFX_LeafParams");
		private static readonly int ShaftTex = Shader.PropertyToID("_NFX_ShaftTex");
		private static readonly int ShaftSize = Shader.PropertyToID("_NFX_ShaftSize");
		private static readonly int ExposureTexture = Shader.PropertyToID("_ExposureTexture");
		private static readonly int LocalTex = Shader.PropertyToID("_NFX_LocalTex");
		private static readonly int LocalSize = Shader.PropertyToID("_NFX_LocalSize");
		private static readonly int SunExactTex = Shader.PropertyToID("_NFX_SunExactTex");
		private static readonly int SunExactParams = Shader.PropertyToID("_NFX_SunExact");
		private static readonly int[] GBufferTextures = {
			Shader.PropertyToID("_GBufferTexture0"), Shader.PropertyToID("_GBufferTexture1"),
			Shader.PropertyToID("_GBufferTexture2"), Shader.PropertyToID("_GBufferTexture3"),
		};
		private static readonly int[] DBufferTextures = {
			Shader.PropertyToID("_DBufferTexture0"), Shader.PropertyToID("_DBufferTexture1"),
			Shader.PropertyToID("_DBufferTexture2"), Shader.PropertyToID("_DBufferTexture3"),
		};
		private static readonly ShaderTagId GBufferPass = new("GBuffer");
		// cleared into the base colour target: no material writes it, so it marks the pixels without a GBuffer pass
		private static readonly Color GBufferUnset = new(1.0f, 0.0f, 1.0f, 0.0f);

		public Material Material; // NFX_RTShadow
		// NFX_SunExact: the sun light HDRP added, from a G-buffer of its own (ExactSun)
		public Material SunExactMaterial;
		public RayTracingScene Scene;
		public RTSurfaceTrace Surfaces;
		public LocalLights Lights;
		public long TracedRays { get; private set; }
		public Vector4 SunDirection;
		public Vector4 SunIlluminance;

		public bool TraceShadows = true;
		// take away exactly the sun light HDRP added (its G-buffer drawn again, its lighting worked out per pixel)
		// instead of an estimate of it; costs one more draw of the opaque renderers
		public bool ExactSun = true;
		private bool exactUsed_;
		public ComputeShader Bridge;  // NFX_NativeBridge.compute: what the own engine reads and writes
		public float Strength = 1.0f;
		public bool FullResolutionShadows = true;
		public bool TraceAO = true;
		public float AOStrength = 1.0f;
		public float AODistance = 3.0f; // m
		public float SkyOcclusionDistance = 30.0f; // m, 0: off
		public float ContactShadows = 0.5f;
		public bool Bounce = true;      // ambient rays shade what they hit (colour bleeding) instead of only blocking
		// own engine: global illumination with every bounce (GI.hlsl, a radiance cache) in place of the one bounce
		// ambient light, with rays of GIDistance
		public bool MultiBounceGI = true;
		public float GIDistance = 20.0f;
		public float GIIntensity = 1.0f; // strength of the light GI bounces in
		private bool giUsed_;
		public float SunAngularDiameter = 0.53f; // degrees; the real sun
		public float LeafTranslucency = 0.6f;    // share of the sun leaves let through (needs the traced shadows)
		public bool Shafts = true;               // light shafts through the trees
		public bool QuarterResolution;           // ambient / GI / lamp shadows at quarter resolution, jittered and built up over frames
		public bool VarianceDenoiser = true;     // the blur follows how much the rays disagree (SVGF style) instead of a fixed one
		public float ShaftDensity = 0.0003f;      // scattering of the air (1/m)
		public float ShaftAnisotropy = 0.6f;     // forward scattering: shafts brightest looking towards the sun
		public float ShaftDistance = 120.0f;     // m
		public float TemporalBlend = 0.15f;
		public bool DebugView; // grey image of the traced shadows alone
		public bool DebugGI;   // the ambient light / GI alone, relative to HDRP's (mid grey: unchanged)
		public bool DebugExactSun; // the exact sun light alone (dark magenta: not known, the estimate is used)

		public string Status { get; private set; } = "not run yet";

		private readonly MaterialPropertyBlock props_ = new();
		public readonly HistoryGuard History = new();
		private RTHandle color_;
		private RTHandle[] gbuffer_;
		private RenderTargetIdentifier[] gbufferIds_;
		private RTHandle dbufferBlack_, dbufferNormal_, dbufferWhite_; // neutral decal buffers: no decal anywhere
		private RTHandle sunExact_;
		private readonly Accumulation sun_ = new("Sun", GraphicsFormat.R16G16_SFloat);          // r visibility, g blocker distance
		private readonly Accumulation ambient_ = new("Ambient", GraphicsFormat.R16G16B16A16_SFloat);
		private readonly Accumulation shafts_ = new("Shafts", GraphicsFormat.R16G16B16A16_SFloat);
		private readonly Accumulation local_ = new("Local", GraphicsFormat.R16G16B16A16_SFloat);
		private int frame_;

		// A traced signal's buffers: new frame, two histories (ping-pong) and a blur target
		private class Accumulation {
			private readonly string name_;
			private readonly GraphicsFormat format_;
			public RTHandle Raw, Denoise, HistoryA, HistoryB;
			public float Scale { get; private set; }
			public bool Flip;

			public Accumulation(string name, GraphicsFormat format) {
				name_ = name;
				format_ = format;
			}

			public void Ensure(float scale) {
				if (Raw != null && Mathf.Approximately(scale, Scale)) {
					return;
				}
				Release();
				Scale = scale;
				Raw = Alloc(Vector2.one * scale, $"NFX_RT{name_}_Raw", format_);
				Denoise = Alloc(Vector2.one * scale, $"NFX_RT{name_}_Denoise", format_);
				HistoryA = Alloc(Vector2.one * scale, $"NFX_RT{name_}_HistoryA", format_);
				HistoryB = Alloc(Vector2.one * scale, $"NFX_RT{name_}_HistoryB", format_);
			}

			public void Release() {
				Raw?.Release();
				Denoise?.Release();
				HistoryA?.Release();
				HistoryB?.Release();
				Raw = null;
			}
		}

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = Alloc(Vector2.one, "NFX_RTShadow_Color", GraphicsFormat.R16G16B16A16_SFloat);
		}

		private static RTHandle Alloc(Vector2 scale, string name, GraphicsFormat format, bool randomWrite = true) {
			return RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: format, enableRandomWrite: randomWrite, useDynamicScale: true, name: name);
		}

		// HDRP/Lit's G-buffer layout (base colour sRGB, normal and roughness, material data, baked light)
		private void EnsureExactTargets() {
			if (gbuffer_ != null) {
				return;
			}
			gbuffer_ = new[] {
				Alloc(Vector2.one, "NFX_GBuffer0", GraphicsFormat.R8G8B8A8_SRGB, false),
				Alloc(Vector2.one, "NFX_GBuffer1", GraphicsFormat.R8G8B8A8_UNorm, false),
				Alloc(Vector2.one, "NFX_GBuffer2", GraphicsFormat.R8G8B8A8_UNorm, false),
				Alloc(Vector2.one, "NFX_GBuffer3", GraphicsFormat.B10G11R11_UFloatPack32, false),
			};
			gbufferIds_ = new RenderTargetIdentifier[gbuffer_.Length];
			for (int i = 0; i < gbuffer_.Length; ++i) {
				gbufferIds_[i] = gbuffer_[i];
			}
			dbufferBlack_ = Alloc(Vector2.one, "NFX_DBufferNone", GraphicsFormat.R8G8B8A8_UNorm, false);
			dbufferNormal_ = Alloc(Vector2.one, "NFX_DBufferNoneNormal", GraphicsFormat.R8G8B8A8_UNorm, false);
			dbufferWhite_ = Alloc(Vector2.one, "NFX_DBufferNoneMask", GraphicsFormat.R8G8B8A8_UNorm, false);
			sunExact_ = Alloc(Vector2.one, "NFX_SunExact", GraphicsFormat.R16G16B16A16_SFloat, false);
		}

		private void ReleaseExactTargets() {
			if (gbuffer_ == null) {
				return;
			}
			foreach (var t in gbuffer_) {
				t.Release();
			}
			dbufferBlack_.Release();
			dbufferNormal_.Release();
			dbufferWhite_.Release();
			sunExact_.Release();
			gbuffer_ = null;
		}

		// HDRP's G-buffer is gone by now (its memory reused), so the opaque renderers' GBuffer pass draws it again into
		// targets of its own, against the camera depth (no depth or stencil writes); NFX_SunExact then works out the sun
		// light HDRP's deferred lighting added from it.
		private void DrawExactSun(CustomPassContext ctx) {
			var cmd = ctx.cmd;
			EnsureExactTargets();
			// the decal buffers are not this frame's any more either: neutral ones, or every surface that receives
			// decals would blend in whatever the memory holds now (decals then go missing from the exact light only)
			CoreUtils.SetRenderTarget(cmd, dbufferBlack_, ClearFlag.Color, new Color(0.0f, 0.0f, 0.0f, 1.0f));
			CoreUtils.SetRenderTarget(cmd, dbufferNormal_, ClearFlag.Color, new Color(0.5f, 0.5f, 0.5f, 1.0f));
			CoreUtils.SetRenderTarget(cmd, dbufferWhite_, ClearFlag.Color, Color.white);
			cmd.SetGlobalTexture(DBufferTextures[0], dbufferBlack_);
			cmd.SetGlobalTexture(DBufferTextures[1], dbufferNormal_);
			cmd.SetGlobalTexture(DBufferTextures[2], dbufferBlack_);
			cmd.SetGlobalTexture(DBufferTextures[3], dbufferWhite_);

			CoreUtils.SetRenderTarget(cmd, gbufferIds_, ctx.cameraDepthBuffer, ClearFlag.Color, GBufferUnset);
			var desc = new RendererListDesc(GBufferPass, ctx.cameraCullingResults, ctx.hdCamera.camera) {
				renderQueueRange = RenderQueueRange.opaque,
				sortingCriteria = SortingCriteria.CommonOpaque,
				rendererConfiguration = PerObjectData.LightProbe | PerObjectData.LightProbeProxyVolume | PerObjectData.Lightmaps,
				// depth equal, as HDRP's own GBuffer pass after its depth prepass: alpha tested materials (foliage cards)
				// clip in the depth prepass only, and their GBuffer pass leaves the clip to the depth test; with a less
				// or equal test they wrote their whole card into the G-buffer (square patches of exact sun light)
				stateBlock = new RenderStateBlock(RenderStateMask.Depth | RenderStateMask.Stencil) {
					depthState = new DepthState(false, CompareFunction.Equal),
					stencilState = new StencilState(false),
				},
			};
			cmd.DrawRendererList(ctx.renderContext.CreateRendererList(desc));

			props_.Clear();
			for (int i = 0; i < gbuffer_.Length; ++i) {
				props_.SetTexture(GBufferTextures[i], gbuffer_[i]);
			}
			props_.SetVector(RTSunDir, SunDirection);
			CoreUtils.SetRenderTarget(cmd, sunExact_, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, SunExactMaterial, props_, 0);
		}

		private static Vector4 SizeOf(int width, int height, float scale) {
			int w = Mathf.Max(1, Mathf.RoundToInt(width * scale));
			int h = Mathf.Max(1, Mathf.RoundToInt(height * scale));
			return new Vector4(w, h, 1.0f / w, 1.0f / h);
		}

		protected override void Execute(CustomPassContext ctx) {
			// only the camera drawing to the screen: probes, mirrors and previews would flip the status (and with it
			// HDRP's sun shadows) every frame and mix their images into the history
			if (!RTSurfaceTrace.IsScreenCamera(ctx.hdCamera)) {
				return;
			}
			if (Material == null || Bridge == null || Scene == null || Surfaces == null) {
				Status = "unavailable (needs DX12 ray tracing and the shader bundle)";
				return;
			}
			// unavailable: HDRP keeps its own sun shadows meanwhile (RayTracingEffects.UpdateRayTracing)
			if (!NativeEngine.Active) {
				Status = $"unavailable (own engine not running: {NativeEngine.Info})";
				return;
			}
			var probe = HdrpBindings.AmbientProbe(ctx.hdCamera);
			var exposure = HdrpBindings.ExposureTexture(ctx.hdCamera);
			if (probe == null || exposure == null) {
				Status = "unavailable (HDRP sky probe or exposure not reachable)";
				return;
			}

			var cmd = ctx.cmd;
			bool resetHistory = History.CheckReset(ctx.hdCamera);
			int width = ctx.hdCamera.actualWidth;
			int height = ctx.hdCamera.actualHeight;
			sun_.Ensure(FullResolutionShadows ? 1.0f : 0.5f);
			float lowScale = QuarterResolution ? 0.25f : 0.5f;
			ambient_.Ensure(lowScale);
			shafts_.Ensure(0.25f);
			local_.Ensure(lowScale);
			// quarter resolution: each trace pixel looks at a different full resolution pixel of its 4 x 4 block every
			// frame (an R2 sequence); the accumulation gathers them, the composite's depth weighted upsample spreads them.
			// Not under NRD: REBLUR follows each pixel through time and needs it to stay where it is.
			var jitter = Vector2.zero;
			// NRD (NativeEngine.NrdEnabled): REBLUR takes the ambient light and the lamp shadows too, at half or quarter
			// resolution (unjittered); the Developer tab can hand each back to the filter here
			NativeEngine.NrdReset = resetHistory;
			NativeEngine.NrdAmbient = NativeEngine.NrdEnabled && NativeEngine.DevNrdAmbient;
			NativeEngine.NrdLamps = NativeEngine.NrdEnabled && NativeEngine.DevNrdLamps;
			if (QuarterResolution && !NativeEngine.NrdAmbient && !NativeEngine.NrdLamps) {
				jitter = new Vector2((0.5f + frame_ * 0.7548776662f) % 1.0f - 0.5f, (0.5f + frame_ * 0.5698402910f) % 1.0f - 0.5f);
			}
			ctx.cmd.SetComputeVectorParam(Bridge, TraceJitter, jitter);
			bool localShadows = Lights != null && Lights.ShadowCount > 0;
			var shaftSize = SizeOf(width, height, shafts_.Scale);
			var sunSize = SizeOf(width, height, sun_.Scale);
			var ambientSize = SizeOf(width, height, ambient_.Scale);
			float sunRadius = SunAngularDiameter * 0.5f * Mathf.Deg2Rad;
			float fov = ctx.hdCamera.camera.fieldOfView;

			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			// the own engine (NativeEngine) traces everything
			var ambientParams = new Vector4(Mathf.Max(0.1f, AODistance), Bounce ? 1.0f : 0.0f, SkyOcclusionDistance, Mathf.Clamp01(ContactShadows));
			var shaftParams = new Vector4(Mathf.Max(0.0f, ShaftDensity), Mathf.Clamp(ShaftAnisotropy, 0.0f, 0.95f), ShaftDistance, 0.0f);
			giUsed_ = TraceAO && Bounce && MultiBounceGI;
			if (giUsed_) {
				ambientParams.x = GIDistance;
			}
			NativeEngine.BeginFrame(ctx.hdCamera.camera, SunDirection, sunRadius, SunIlluminance, frame_, ambientParams, Lights, probe);
			Surfaces.EnsureTraced(ctx);
			exactUsed_ = ExactSun && TraceShadows && SunExactMaterial != null;
			if (exactUsed_) {
				RTTimings.ShadowFilter.Begin(cmd);
				DrawExactSun(ctx);
				RTTimings.ShadowFilter.End(cmd);
			} else {
				ReleaseExactTargets();
			}

			// 1. trace: one point of the sun's disc per pixel, one ambient ray per half resolution pixel
			RTTimings.ShadowTrace.Begin(cmd);
			if (TraceShadows) {
				RTTimings.Sun.Begin(cmd);
				NativeEngine.TraceSun(cmd, Bridge, ctx, Surfaces.Normal, sun_.Raw, sunSize);
				RTTimings.Sun.End(cmd);
			}
			RTTimings.Ambient.Begin(cmd);
			if (giUsed_) {
				NativeEngine.TraceGI(cmd, Bridge, ctx, Surfaces.Normal, ambient_.Raw, ambientSize);
			} else if (TraceAO) {
				NativeEngine.TraceAmbient(cmd, Bridge, ctx, Surfaces.Normal, ambient_.Raw, ambientSize);
			}
			RTTimings.Ambient.End(cmd);
			if (Shafts) {
				RTTimings.Shafts.Begin(cmd);
				NativeEngine.TraceShafts(cmd, Bridge, ctx, shafts_.Raw, shaftSize, shaftParams, exposure);
				RTTimings.Shafts.End(cmd);
			}
			if (localShadows) {
				RTTimings.Lamps.Begin(cmd);
				NativeEngine.TraceLocal(cmd, Bridge, ctx, Surfaces.Normal, local_.Raw, ambientSize);
				RTTimings.Lamps.End(cmd);
			}
			// for the overlay: rays per frame, roughly (one per traced pixel and kind; lamps take two)
			TracedRays = (TraceShadows ? (long)sunSize.x * (long)sunSize.y : 0)
			             + (TraceAO ? (long)ambientSize.x * (long)ambientSize.y * (giUsed_ ? 2 : 1) : 0)
			             + (Shafts ? (long)shaftSize.x * (long)shaftSize.y : 0)
			             + (localShadows ? (long)ambientSize.x * (long)ambientSize.y * 2 : 0);
			RTTimings.ShadowTrace.End(cmd);
			RTTimings.ShadowFilter.Begin(cmd);

			// 2. accumulate, 3. blur along surfaces (two a-trous steps; the sun's sized by its penumbra)
			float blend = resetHistory ? 1.0f : Mathf.Clamp01(TemporalBlend);
			// Still camera: progressive accumulation (HistoryGuard.StillBlend) on the pixels that do not move on screen
			// (the share passed on is the still blend over the moving one), settled within about a second (1/64: 256 frames took
			// too long to look done); the sun stops at 1/16, its shadows move
			// with cars over still ground. Converged, the light needs no wide blur any more: one step.
			float StillShare(float moving, float floor) => moving >= 1.0f ? 1.0f : History.StillBlend(moving, floor) / Mathf.Max(moving, 1e-4f);
			bool converged = History.StillFrames > 30;
			// the DXR 1.2 device's sun shadows arrive one frame late (RtDevice.inl): the filter reads them reprojected
			// NRD's SIGMA (NativeEngine.NrdSun) already denoised them, history included: passed through as they are
			var sunResult = !TraceShadows ? null
				: NativeEngine.NrdSun ? Filter(cmd, sun_, sunSize, 1.0f, PassDenoiseSun, sunRadius, fov, 0)
				: Filter(cmd, sun_, sunSize, blend, PassDenoiseSun, sunRadius, fov, 2, NativeEngine.Dxr12SunActive, StillShare(blend, 1.0f / 16.0f));
			// GI: one ray per half resolution pixel with every bounce behind it varies far more than the one bounce
			// ambient light: a longer history (about 20 frames) and a fifth, wider blur step. Ambient light and lamp
			// shadows (two lights a pixel) get blur steps of 1, 2, 4 and 8 pixels: two left them grainy.
			float slowBlend = resetHistory ? 1.0f : Mathf.Min(blend, 0.1f);
			// at quarter resolution a trace pixel already spans twice as much: one step fewer for the same reach.
			// Wider blurs only smeared the noise into blotches and softened the contact shadows: the noise goes in time
			// instead, still pixels keeping a quarter of the blend (about 40 frames).
			int wide = converged ? 1 : QuarterResolution ? 2 : 3;
			float giBlend = resetHistory ? 1.0f : Mathf.Min(blend, 0.05f);
			var ambientResult = !TraceAO ? null
				: NativeEngine.NrdAmbient ? Filter(cmd, ambient_, ambientSize, 1.0f, PassDenoise, sunRadius, fov, 0)
				: giUsed_ ? Filter(cmd, ambient_, ambientSize, giBlend, PassDenoise, sunRadius, fov, wide + 1, false, Mathf.Min(0.25f, StillShare(giBlend, 1.0f / 64.0f)))
				          : Filter(cmd, ambient_, ambientSize, slowBlend, PassDenoise, sunRadius, fov, wide, false, Mathf.Min(0.25f, StillShare(slowBlend, 1.0f / 64.0f)));
			var localResult = !localShadows ? null
				: NativeEngine.NrdLamps ? Filter(cmd, local_, ambientSize, 1.0f, PassDenoise, sunRadius, fov, 0)
				: Filter(cmd, local_, ambientSize, slowBlend, PassDenoise, sunRadius, fov, wide, false, Mathf.Min(0.25f, StillShare(slowBlend, 1.0f / 64.0f)));
			// one sample per pixel along a whole view ray: lean on the history longer than the surfaces do
			var shaftResult = Shafts ? Filter(cmd, shafts_, shaftSize, resetHistory ? 1.0f : blend * 0.5f, PassDenoise, sunRadius, fov, 3) : null;

			// 4. take the blocked sun light away and scale the rest by the ambient light, at full resolution
			props_.Clear();
			props_.SetVector(TargetSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
			props_.SetVector(SunSize, sunSize);
			props_.SetVector(AmbientSize, ambientSize);
			props_.SetVector(ShaftSize, shaftSize);
			props_.SetTexture(ShaftTex, shaftResult ?? color_);
			props_.SetVector(LocalSize, ambientSize);
			props_.SetTexture(LocalTex, localResult ?? color_);
			props_.SetVector(ShadowCompose, new Vector4(TraceShadows ? Strength : 0.0f, DebugExactSun ? 3.0f : DebugGI ? 2.0f : DebugView ? 1.0f : 0.0f, blend, TraceAO ? AOStrength : 0.0f));
			props_.SetTexture(ColorTex, color_);
			props_.SetTexture(SunTex, sunResult ?? color_);
			props_.SetTexture(AmbientTex, ambientResult ?? color_);
			props_.SetTexture(RTSurfaceTrace.AlbedoTex, Surfaces.Albedo);
			props_.SetTexture(RTSurfaceTrace.NormalTex, Surfaces.Normal);
			props_.SetTexture(SunExactTex, exactUsed_ ? sunExact_ : color_);
			props_.SetVector(SunExactParams, new Vector4(exactUsed_ ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f));
			props_.SetVector(RTSunDir, SunDirection);
			props_.SetVector(RTSunColor, SunIlluminance);
			props_.SetVector(LeafParams, new Vector4(TraceShadows ? LeafTranslucency : 0.0f, Shafts ? 1.0f : 0.0f, localShadows ? 1.0f : 0.0f,
				giUsed_ ? Mathf.Max(0.001f, GIIntensity) : 0.0f));
			props_.SetBuffer(AmbientProbeId, probe); // GI: the sky light HDRP gave, which the sky's share scales
			CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, PassComposite);
			RTTimings.ShadowFilter.End(cmd);

			++frame_;
			string ambientWhat = giUsed_ ? "global illumination (every bounce)" : Bounce ? "ambient light with bounce" : "AO";
			string sunWhat = $"sun shadows {sunSize.x}x{sunSize.y}" + (exactUsed_ ? " (exact sun light)" : "");
			string what = TraceShadows && TraceAO ? $"{sunWhat}, {ambientWhat} {ambientSize.x}x{ambientSize.y}"
				: TraceShadows ? sunWhat : $"{ambientWhat} {ambientSize.x}x{ambientSize.y}";
			Status = $"own engine ({NativeEngine.ModeName}): " + what + (Lights != null && Lights.Count > 0 ? $", {Lights.Count} local lights ({Lights.ShadowCount} ray traced shadows)" : "");
		}

		// Temporal accumulation and two a-trous steps; returns the filtered buffer
		private RTHandle Filter(CommandBuffer cmd, Accumulation a, Vector4 size, float blend, int denoisePass, float sunRadius, float fov, int steps = 2,
		                        bool oneFrameLate = false, float stillShare = 1.0f) {
			props_.Clear();
			props_.SetVector(TargetSize, size);
			props_.SetVector(ShadowCompose, new Vector4(oneFrameLate ? 1.0f : 0.0f, 0.0f, blend, stillShare));
			// penumbra width in trace pixels: blocker distance * 2 tan(sun radius) over the size of a pixel there
			float pixelAngle = 2.0f * Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1.0f, size.y);
			props_.SetVector(SunFilter, new Vector4(2.0f * Mathf.Tan(sunRadius), pixelAngle, 0.0f, 0.0f));
			props_.SetTexture(ShadowTex, a.Raw);
			var history = a.Flip ? a.HistoryA : a.HistoryB;
			var previous = a.Flip ? a.HistoryB : a.HistoryA;
			a.Flip = !a.Flip;
			props_.SetTexture(HistoryTex, previous);
			CoreUtils.SetRenderTarget(cmd, history, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, PassTemporal);

			// a-trous steps of 1, 2, 4, 8 ... pixels, back and forth between the denoise and raw buffers (an even count
			// ends in the raw one)
			var source = history;
			for (int i = 0; i < steps; ++i) {
				var target = i % 2 == 0 ? a.Denoise : a.Raw;
				props_.SetTexture(SourceTex, source);
				props_.SetFloat(DenoiseStep, 1 << i);
				props_.SetFloat(DenoiseMode, VarianceDenoiser ? 1.0f : 0.0f);
				CoreUtils.SetRenderTarget(cmd, target, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, Material, props_, denoisePass);
				source = target;
			}
			return source;
		}

		protected override void Cleanup() {
			color_?.Release();
			sun_.Release();
			ambient_.Release();
			shafts_.Release();
			local_.Release();
			ReleaseExactTargets();
		}
	}
}

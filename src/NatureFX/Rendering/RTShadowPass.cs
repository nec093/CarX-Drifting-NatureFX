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
		private static readonly int ShaftParams = Shader.PropertyToID("_NFX_ShaftParams");
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
		public bool TransparentShadows = true; // the sun's ray traced shadow map for transparent surfaces (SunShadowMap)
		public bool GroundDetail = true;       // the ground map (GroundMap): ground blend, painted dirt, debris
		public float GroundBlend = 0.35f;      // m: how high objects take the ground's colour where a material does not say
		public Vector4 SunIlluminance;

		public bool TraceShadows = true;
		// take away exactly the sun light HDRP added (its G-buffer drawn again, its lighting worked out per pixel)
		// instead of an estimate of it; costs one more draw of the opaque renderers
		public bool ExactSun = true;
		public bool LampMasks = true; // roadmap A4 (DrawExactLamps)
		public bool FullRelight = true; // roadmap A4: the composite takes HDRP's pixel apart and lights it again
		public bool RelightActive { get; private set; }
		private bool exactUsed_;
		private RTHandle lampsExact_;
		private static readonly int LampsExactTex = Shader.PropertyToID("_NFX_LampsExactTex");
		private static readonly int LampPosId = Shader.PropertyToID("_NFX_LampPos");
		private static readonly int LampColorId = Shader.PropertyToID("_NFX_LampColor");
		private static readonly int LampDirId = Shader.PropertyToID("_NFX_LampDir");
		private static readonly int LampSpotId = Shader.PropertyToID("_NFX_LampSpot");
		private static readonly int LampCountId = Shader.PropertyToID("_NFX_LampCount");
		public ComputeShader Bridge;  // NFX_NativeBridge.compute: what the own engine reads and writes
		// the sky drawn by the own engine (Sky.hlsli) over HDRP's: the sun's illuminance above the air (lux), haze
		public bool NativeSky;
		public float SkySunLux;
		public float SkyAerosols = 1.0f;
		public string SkyStatus { get; private set; } = "off";
		private bool cloudsUsed_;
		private float skyDay_ = 1.0f;
		private RTHandle skyView_, skyTransmittance_, clouds_, cloudShadow_;
		private RTHandle cloudHistoryA_, cloudHistoryB_;
		private bool cloudHistoryValid_;
		private Matrix4x4 prevView_ = Matrix4x4.identity;
		private Vector2 prevTan_ = Vector2.one;
		private static readonly int PrevViewXId = Shader.PropertyToID("_NFX_PrevViewX");
		private static readonly int PrevViewYId = Shader.PropertyToID("_NFX_PrevViewY");
		private static readonly int PrevViewZId = Shader.PropertyToID("_NFX_PrevViewZ");
		private static readonly int PrevTanId = Shader.PropertyToID("_NFX_PrevTan");
		private static readonly int CloudTex = Shader.PropertyToID("_NFX_CloudTex");
		private static readonly int CloudShadowTex = Shader.PropertyToID("_NFX_CloudShadowTex");
		private static readonly int CloudShadowParams = Shader.PropertyToID("_NFX_CloudShadowParams");
		private static readonly int SkyProbeTex = Shader.PropertyToID("_NFX_SkyProbe");
		private static readonly int CloudCurrentId = Shader.PropertyToID("_NFX_CloudCurrent");
		private static readonly int CloudHistoryId = Shader.PropertyToID("_NFX_CloudHistory");
		private static readonly int CloudTemporalId = Shader.PropertyToID("_NFX_CloudTemporal");
		private static readonly int TargetSizeBridge = Shader.PropertyToID("_NFX_TargetSize");
		private static readonly int NativeTargetBridge = Shader.PropertyToID("_NFX_NativeTarget");
		// the clouds' shadows on the ground (CloudShadow.hlsl); the sky's light as the ambient light (phase 1c)
		public bool CloudShadows = true;
		// phase 2: the fog and haze from the own engine (Fog.hlsl) in the light shafts' place; MapFog.NativeParams
		public bool NativeFog;
		public Vector4 FogParams;
		public string FogStatus { get; private set; } = "off";
		private const float FogReach = 20000.0f; // m over the sky
		private RTHandle fogHistoryA_, fogHistoryB_, fogVolume_;
		private bool fogHistoryValid_;
		private Vector3 lastCamera_;
		private static readonly int FogVolumeId = Shader.PropertyToID("_NFX_FogVolume");
		private static readonly int FogVolumeParamsId = Shader.PropertyToID("_NFX_FogVolumeParams");
		private static readonly int FogHeightId = Shader.PropertyToID("_NFX_FogHeight");
		private static readonly int FogSunId = Shader.PropertyToID("_NFX_FogSun");
		private static readonly int FogSunDirId = Shader.PropertyToID("_NFX_FogSunDir");
		private static readonly int SkyLightId = Shader.PropertyToID("_NFX_SkyLight");

		private static RTHandle FogTarget(string name) {
			return RTHandles.Alloc(NativeEngine.FogW, NativeEngine.FogH, NativeEngine.FogD, dimension: TextureDimension.Tex3D,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, filterMode: FilterMode.Bilinear,
				wrapMode: TextureWrapMode.Clamp, name: name);
		}
		public bool SkyAmbient = true;
		private Vector4 cloudShadowParams_;
		private static readonly int SkyViewTex = Shader.PropertyToID("_NFX_SkyView");
		private static readonly int SkyTransmittanceTex = Shader.PropertyToID("_NFX_SkyTransmittance");
		private static readonly int SkyParamsId = Shader.PropertyToID("_NFX_SkyParams");
		private static readonly int SkyAmbientOnId = Shader.PropertyToID("_NFX_SkyAmbientOn");
		private static readonly int OwnAmbientId = Shader.PropertyToID("_NFX_OwnAmbient");
		private static readonly int OwnSunId = Shader.PropertyToID("_NFX_OwnSun");
		private static readonly int OwnLampsId = Shader.PropertyToID("_NFX_OwnLamps");
		public static readonly int SkyShareTexId = Shader.PropertyToID("_NFX_SkyShareTex");
		public static readonly int SkyShareParamsId = Shader.PropertyToID("_NFX_SkyShareParams");

		private static RTHandle CloudTarget(int w, int h, string name) {
			return RTHandles.Alloc(w, h, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: name);
		}
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
		// DLSS Ray Reconstruction's guides (NFX_SunExact DlssGuides: diffuse and specular albedo), this frame's when
		// GuidesFrame is Time.frameCount (Dlaa, PostPass)
		private RTHandle guideDiffuse_, guideSpecular_;
		private RenderTargetIdentifier[] guideIds_;
		public static RTHandle GuideDiffuse { get; private set; }
		public static RTHandle GuideSpecular { get; private set; }
		public static int GuidesFrame { get; private set; } = -10;
		public static int ExactFrame { get; private set; } = -10;
		private RenderTargetIdentifier[] gbufferIds_;
		private RTHandle dbufferBlack_, dbufferNormal_, dbufferWhite_; // neutral decal buffers: no decal anywhere
		private RTHandle sunExact_;
		// roadmap A4: the lamps' own shadow masks (NativeEngine.TraceLampMask) and their history, full resolution
		private RTHandle lampMask_, lampHistoryA_, lampHistoryB_, lampsTotal_;
		private float lampMaskScale_;
		private bool lampFlip_, maskUsed_, lampHistoryValid_;
		private RenderTargetIdentifier[] lampTargets_ = new RenderTargetIdentifier[3];
		private static readonly int LampsTotalId = Shader.PropertyToID("_NFX_LampsTotalTex");
		private static readonly int GBuffer3Id = Shader.PropertyToID("_NFX_GBuffer3Tex");
		private static readonly int LampMaskId = Shader.PropertyToID("_NFX_LampMask");
		private static readonly int LampMaskHistoryId = Shader.PropertyToID("_NFX_LampMaskHistory");
		private static readonly int LampMaskSizeId = Shader.PropertyToID("_NFX_LampMaskSize");
		private static readonly int LampMaskParamsId = Shader.PropertyToID("_NFX_LampMaskParams");
		private readonly MaterialPropertyBlock lampProps_ = new();
		private readonly Accumulation sun_ = new("Sun", GraphicsFormat.R16G16_SFloat);          // r visibility, g blocker distance
		private readonly Accumulation ambient_ = new("Ambient", GraphicsFormat.R16G16B16A16_SFloat);
		private readonly Accumulation shafts_ = new("Shafts", GraphicsFormat.R16G16B16A16_SFloat);
		private readonly Accumulation local_ = new("Local", GraphicsFormat.R16G16B16A16_SFloat);
		private int frame_;

		// A traced signal's buffers: new frame, two histories (ping-pong) and a blur target. The histories and the blur
		// target only while the own filter runs on it: NRD (or DLSS Ray Reconstruction) gives the finished signal in
		// Raw, and they were three idle screens of memory (the sun's at full resolution) the whole time.
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
				fresh_ = true;
			}

			// The own filter's targets; true when they were just made (their history is whatever memory held)
			public bool EnsureFilter() {
				if (Denoise != null) {
					return false;
				}
				Denoise = Alloc(Vector2.one * Scale, $"NFX_RT{name_}_Denoise", format_);
				HistoryA = Alloc(Vector2.one * Scale, $"NFX_RT{name_}_HistoryA", format_);
				HistoryB = Alloc(Vector2.one * Scale, $"NFX_RT{name_}_HistoryB", format_);
				return true;
			}

			public void DropFilter() {
				Denoise?.Release();
				HistoryA?.Release();
				HistoryB?.Release();
				Denoise = HistoryA = HistoryB = null;
			}

			// True once after Ensure made new textures: their history holds whatever memory had (a NaN stays in a
			// lerp for good), so the frame starts from its new samples
			public bool TakeFresh() {
				bool fresh = fresh_;
				fresh_ = false;
				return fresh;
			}

			private bool fresh_;

			public void Release() {
				Raw?.Release();
				DropFilter();
				Raw = null;
			}
		}

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = SharedColor.Acquire(); // the camera colour copy, shared with the other composites
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
			lampsExact_ = Alloc(Vector2.one, "NFX_LampsExact", GraphicsFormat.R16G16B16A16_SFloat, false);
			// the lamp mask histories (32 B a pixel) and DLSS Ray Reconstruction's guides are made when first used
			// (EnsureLampHistory / EnsureGuides): always there, they were 400 MB at 4K whether or not either ran
			lampsTotal_ = Alloc(Vector2.one, "NFX_LampsTotal", GraphicsFormat.R16G16B16A16_SFloat, false);
			lampHistoryValid_ = false;
		}

		private void EnsureLampHistory() {
			if (lampHistoryA_ == null) {
				lampHistoryA_ = Alloc(Vector2.one, "NFX_LampMaskHistoryA", GraphicsFormat.R32G32B32A32_SFloat, false);
				lampHistoryB_ = Alloc(Vector2.one, "NFX_LampMaskHistoryB", GraphicsFormat.R32G32B32A32_SFloat, false);
				lampHistoryValid_ = false;
			}
		}

		private void ReleaseLampHistory() {
			lampHistoryA_?.Release();
			lampHistoryB_?.Release();
			lampHistoryA_ = lampHistoryB_ = null;
			lampHistoryValid_ = false;
		}

		private void EnsureGuides() {
			if (guideDiffuse_ == null) {
				guideDiffuse_ = Alloc(Vector2.one, "NFX_DlssGuideDiffuse", GraphicsFormat.R16G16B16A16_SFloat, false);
				guideSpecular_ = Alloc(Vector2.one, "NFX_DlssGuideSpecular", GraphicsFormat.R16G16B16A16_SFloat, false);
				guideIds_ = new RenderTargetIdentifier[] { guideDiffuse_, guideSpecular_ };
			}
		}

		private void ReleaseGuides() {
			guideDiffuse_?.Release();
			guideSpecular_?.Release();
			guideDiffuse_ = guideSpecular_ = null;
			guideIds_ = null;
			GuideDiffuse = GuideSpecular = null;
			GuidesFrame = -10;
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
			lampsExact_.Release();
			ReleaseLampHistory();
			lampsTotal_.Release();
			ReleaseGuides();
			lampMask_?.Release();
			lampMask_ = null;
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
			props_.SetFloat(OwnSunId, OwnLighting.Sun ? 1.0f : 0.0f);
			CoreUtils.SetRenderTarget(cmd, sunExact_, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, SunExactMaterial, props_, 0);
			ExactFrame = Time.frameCount;
			// DLSS Ray Reconstruction's albedos, from the same G-buffer
			if (Dlaa.RayReconstruction) {
				EnsureGuides();
				CoreUtils.SetRenderTarget(cmd, guideIds_, ctx.cameraDepthBuffer, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, SunExactMaterial, props_, 2);
				GuideDiffuse = guideDiffuse_;
				GuideSpecular = guideSpecular_;
				GuidesFrame = Time.frameCount;
			} else if (guideDiffuse_ != null) {
				ReleaseGuides();
			}
			// the light HDRP's lamps added, for the old blocked share (without lamp masks, DrawExactLamps)
			if (!maskUsed_) {
				if (lampHistoryA_ != null) {
					ReleaseLampHistory();
				}
				CoreUtils.SetRenderTarget(cmd, lampsExact_, ClearFlag.None);
				CoreUtils.DrawFullScreen(cmd, SunExactMaterial, props_, 1);
			}
		}

		// Roadmap A4: the lamps' own shadow masks traced, then NFX_SunExact LampsExact shadows each of HDRP's lamps
		// by its own (into lampsExact_: the light the shadows take away) and keeps the masks' history
		private void DrawExactLamps(CustomPassContext ctx, Vector4 maskSize, bool reset) {
			var cmd = ctx.cmd;
			if (lampMask_ == null || !Mathf.Approximately(lampMaskScale_, ambient_.Scale)) {
				lampMask_?.Release();
				lampMaskScale_ = ambient_.Scale;
				lampMask_ = Alloc(Vector2.one * lampMaskScale_, "NFX_LampMask", GraphicsFormat.R32G32B32A32_SFloat);
			}
			NativeEngine.TraceLampMask(cmd, Bridge, ctx, Surfaces.Normal, lampMask_, maskSize);
			EnsureLampHistory();
			var historyIn = lampFlip_ ? lampHistoryB_ : lampHistoryA_;
			var historyOut = lampFlip_ ? lampHistoryA_ : lampHistoryB_;
			lampFlip_ = !lampFlip_;
			lampProps_.Clear();
			for (int i = 0; i < gbuffer_.Length; ++i) {
				lampProps_.SetTexture(GBufferTextures[i], gbuffer_[i]);
			}
			lampProps_.SetTexture(LampMaskId, lampMask_);
			lampProps_.SetTexture(LampMaskHistoryId, historyIn);
			lampProps_.SetVector(LampMaskSizeId, maskSize);
			// one ray a lamp a frame: about five frames of history, held to what the neighbourhood sees now (NFX_SunExact)
			lampProps_.SetVector(LampMaskParamsId, new Vector4(reset || !lampHistoryValid_ ? 1.0f : 0.2f, 1.0f, 0.0f, 0.0f));
			if (Lights != null && Lights.Count > 0) {
				lampProps_.SetVectorArray(LampPosId, Lights.Positions);
			}
			lampProps_.SetFloat(LampCountId, Lights != null ? Lights.Count : 0);
			lampProps_.SetFloat(OwnLampsId, OwnLighting.Lamps ? 1.0f : 0.0f);
			lampTargets_[0] = lampsExact_;
			lampTargets_[1] = historyOut;
			lampTargets_[2] = lampsTotal_;
			CoreUtils.SetRenderTarget(cmd, lampTargets_, ctx.cameraDepthBuffer);
			CoreUtils.DrawFullScreen(cmd, SunExactMaterial, lampProps_, 1);
			lampHistoryValid_ = true;
		}

		private static Vector4 SizeOf(int width, int height, float scale) {
			int w = Mathf.Max(1, Mathf.RoundToInt(width * scale));
			int h = Mathf.Max(1, Mathf.RoundToInt(height * scale));
			return new Vector4(w, h, 1.0f / w, 1.0f / h);
		}

		protected override void Execute(CustomPassContext ctx) {
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: shadows, GI, lamps (RT)"); // (the debug page)
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
			// the sky's light: the own engine's probe once it replaces HDRP's (OwnLighting), HDRP's otherwise
			var probe = OwnLighting.Probe(ctx.hdCamera);
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
			resetHistory |= sun_.TakeFresh() | ambient_.TakeFresh() | shafts_.TakeFresh() | local_.TakeFresh();
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
			bool fogUsed = AtmospherePass.FogActive; // the 3D fog (AtmospherePass) has the shafts in it
			bool shafts = Shafts && !fogUsed; // the 3D fog has the shafts in it
			var shaftParams = fogUsed
				? new Vector4(0.0f, 0.0f, FogReach, 1.0f)
				: new Vector4(Mathf.Max(0.0f, ShaftDensity), Mathf.Clamp(ShaftAnisotropy, 0.0f, 0.95f), ShaftDistance, 0.0f);
			giUsed_ = TraceAO && Bounce && MultiBounceGI;
			if (giUsed_) {
				ambientParams.x = GIDistance;
			}
			NativeEngine.BeginFrame(ctx.hdCamera.camera, SunDirection, sunRadius, SunIlluminance, frame_, ambientParams, Lights, probe);
			// the sun's shadow on transparent surfaces (their forward pass reads the map)
			SunShadowMap.Update(cmd, ctx.hdCamera.camera, SunDirection, TransparentShadows && TraceShadows);
			// the ground seen from above: the map's ground blend, painted dirt and debris read it
			GroundMap.Update(cmd, ctx.hdCamera.camera, GroundDetail, GroundBlend);
			Surfaces.EnsureTraced(ctx);
			bool skyUsed = false; // the sky is the atmosphere pass's (AtmospherePass), drawn after this one
			float cameraHeight = 1.0f;
			exactUsed_ = ExactSun && TraceShadows && SunExactMaterial != null;
			maskUsed_ = exactUsed_ && localShadows && LampMasks;
			NativeEngine.LampMask = maskUsed_;
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
			if (shafts) {
				RTTimings.Shafts.Begin(cmd);
				NativeEngine.TraceShafts(cmd, Bridge, ctx, shafts_.Raw, shaftSize, shaftParams, exposure);
				RTTimings.Shafts.End(cmd);
			}
			if (localShadows) {
				RTTimings.Lamps.Begin(cmd);
				NativeEngine.TraceLocal(cmd, Bridge, ctx, Surfaces.Normal, local_.Raw, ambientSize);
				if (maskUsed_) {
					DrawExactLamps(ctx, ambientSize, resetHistory);
				}
				RTTimings.Lamps.End(cmd);
			}
			// for the overlay: rays per frame, roughly (one per traced pixel and kind; lamps take two)
			TracedRays = (TraceShadows ? (long)sunSize.x * (long)sunSize.y : 0)
			             + (TraceAO ? (long)ambientSize.x * (long)ambientSize.y * (giUsed_ ? 2 : 1) : 0)
			             + (shafts ? (long)shaftSize.x * (long)shaftSize.y : 0)
			             + (fogUsed ? (long)NativeEngine.FogW * NativeEngine.FogH * NativeEngine.FogD : 0)
			             + (localShadows ? (long)ambientSize.x * (long)ambientSize.y * (maskUsed_ ? 6 : 2) : 0);
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
			// DLSS Ray Reconstruction denoises them with the whole picture: this frame's rays as they are
			var sunResult = !TraceShadows ? null
				: NativeEngine.NrdSun || Dlaa.RayReconstruction ? Filter(cmd, sun_, sunSize, 1.0f, PassDenoiseSun, sunRadius, fov, 0)
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
			var shaftResult = shafts ? Filter(cmd, shafts_, shaftSize, resetHistory ? 1.0f : blend * 0.5f, PassDenoise, sunRadius, fov, 3) : null;

			// 4. take the blocked sun light away and scale the rest by the ambient light, at full resolution
			props_.Clear();
			props_.SetVector(TargetSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
			props_.SetVector(SunSize, sunSize);
			props_.SetVector(AmbientSize, ambientSize);
			props_.SetVector(ShaftSize, shaftSize);
			props_.SetVector(ShaftParams, shaftParams);
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
			props_.SetTexture(LampsExactTex, exactUsed_ ? lampsExact_ : color_);
			if (Lights != null && Lights.Count > 0) {
				props_.SetVectorArray(LampPosId, Lights.Positions);
				props_.SetVectorArray(LampColorId, Lights.Colors);
				props_.SetVectorArray(LampDirId, Lights.Directions);
				props_.SetVectorArray(LampSpotId, Lights.Spots);
			}
			props_.SetFloat(LampCountId, Lights != null ? Lights.Count : 0);
			// the full relight needs the lamps' whole light (the masks' pass) wherever there are lamps
			bool relight = exactUsed_ && FullRelight && (maskUsed_ || !localShadows);
			props_.SetTexture(LampsTotalId, maskUsed_ ? lampsTotal_ : color_);
			props_.SetTexture(GBuffer3Id, exactUsed_ ? gbuffer_[3] : color_);
			props_.SetVector(SunExactParams, new Vector4(exactUsed_ ? 1.0f : 0.0f, maskUsed_ ? 1.0f : 0.0f, relight ? 1.0f : 0.0f, 0.0f));
			RelightActive = relight;
			props_.SetVector(RTSunDir, SunDirection);
			props_.SetVector(RTSunColor, SunIlluminance);
			props_.SetVector(LeafParams, new Vector4(TraceShadows ? LeafTranslucency : 0.0f, shafts ? 1.0f : 0.0f, localShadows ? 1.0f : 0.0f,
				giUsed_ ? Mathf.Max(0.001f, GIIntensity) : 0.0f));
			props_.SetBuffer(AmbientProbeId, probe); // GI: the sky light HDRP gave, which the sky's share scales
			props_.SetTexture(SkyViewTex, skyUsed ? skyView_ : color_);
			props_.SetTexture(SkyTransmittanceTex, skyUsed ? skyTransmittance_ : color_);
			props_.SetTexture(CloudTex, skyUsed && cloudsUsed_ ? cloudHistoryA_ : color_);
			// the atmosphere pass's cloud shadows on the sun, its sky light in place of HDRP's
			var cloudShadowParams = TraceShadows ? AtmospherePass.Shared.CloudShadowParams : Vector4.zero;
			props_.SetTexture(CloudShadowTex, cloudShadowParams.w > 0.0f ? AtmospherePass.Shared.CloudShadow : color_);
			props_.SetVector(CloudShadowParams, cloudShadowParams);
			float skyShare = AtmospherePass.Shared.SkyLight;
			bool skyProbe = NativeEngine.SkyProbe != null && skyShare > 0.0f;
			props_.SetBuffer(SkyProbeTex, skyProbe ? NativeEngine.SkyProbe : probe);
			props_.SetVector(SkyParamsId, new Vector4(skyUsed ? (cloudsUsed_ ? 2.0f : 1.0f) : 0.0f, SkySunLux, cameraHeight, sunRadius));
			props_.SetFloat(SkyAmbientOnId, skyProbe ? skyShare : 0.0f); // the share of the engine's sky light
			// OwnLighting: HDRP's sky light is off (its indirect diffuse multiplier 0): the composite adds the own sky's
			// light on the surface's colour through the GI's sky share instead of darkening HDRP's
			props_.SetFloat(OwnAmbientId, OwnLighting.Ambient ? 1.0f : 0.0f);
			// OwnLighting.Sun: HDRP's sun is off; only with the exact sun light worked out (it is the sun's light then)
			props_.SetFloat(OwnSunId, OwnLighting.Sun && exactUsed_ ? 1.0f : 0.0f);
			if (exactUsed_ && TraceShadows) {
				OwnLighting.SunComposed();
			}
			// OwnLighting.Lamps: HDRP's lamps are off; with the lamp masks' exact light (lit share added)
			props_.SetFloat(OwnLampsId, OwnLighting.Lamps && maskUsed_ ? 1.0f : 0.0f);
			if (maskUsed_) {
				OwnLighting.LampsComposed();
			}
			// the GI's sky share for the reflections composite (its rough surfaces' sky): a the share with GI, else the
			// one bounce factor's brightness
			cmd.SetGlobalTexture(SkyShareTexId, ambientResult ?? color_);
			cmd.SetGlobalVector(SkyShareParamsId, new Vector4(ambientResult != null ? 1.0f : 0.0f, giUsed_ ? 1.0f : 0.0f, 0.0f, 0.0f));
			OwnLighting.ShadowComposed();
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
			// already denoised (NRD, Ray Reconstruction): the temporal pass at a blend of 1 and no blur step only
			// copied Raw; Raw itself is the result, and the filter's targets go
			if (blend >= 0.999f && steps == 0 && !oneFrameLate) {
				a.DropFilter();
				return a.Raw;
			}
			if (a.EnsureFilter()) {
				blend = 1.0f; // new histories: this frame's samples only
			}
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
			SharedColor.Release(color_);
			color_ = null;
			sun_.Release();
			ambient_.Release();
			shafts_.Release();
			local_.Release();
			ReleaseExactTargets();
			skyView_?.Release();
			skyTransmittance_?.Release();
			clouds_?.Release();
			cloudShadow_?.Release();
			cloudHistoryA_?.Release();
			cloudHistoryB_?.Release();
			fogHistoryA_?.Release();
			fogHistoryB_?.Release();
			fogVolume_?.Release();
			fogHistoryA_ = fogHistoryB_ = fogVolume_ = null;
			skyView_ = skyTransmittance_ = clouds_ = cloudShadow_ = cloudHistoryA_ = cloudHistoryB_ = null;
		}
	}
}

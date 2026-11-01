using System;
using KSL.API;
using NatureFX.Nature;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Roadmap D: NatureFX's own post-processing (NFX_Post.shader) in place of HDRP's exposure, bloom, tonemapping and
	// colour adjustments. The pass runs last before HDRP's post-processing, on the HDR colour (after the night vision,
	// the glass and the photo mode's accumulation); HDRP then only does TAA, depth of field, motion blur and lens flares,
	// with its own tonemapping, bloom and colour adjustments off (a volume above VolumeOverhaul's).
	// Exposure: the pass meters the scene (log2 luminance on a 64 x 36 grid, read back a frame or two later); the eye
	// adaptation here turns it into an EV100 and hands it to HDRP as its fixed exposure, so everything HDRP and the own
	// engine draw is exposed by it.
	public class PostPass : CustomPass {
		private const int PassMeter = 0, PassDownFirst = 1, PassDown = 2, PassUp = 3, PassFinal = 4;
		public const int MeterW = 64, MeterH = 36, BloomLevels = 6;

		private static readonly int SourceId = Shader.PropertyToID("_NFX_PostSource");
		private static readonly int LowId = Shader.PropertyToID("_NFX_PostLow");
		private static readonly int BloomId = Shader.PropertyToID("_NFX_PostBloom");
		private static readonly int SourceSizeId = Shader.PropertyToID("_NFX_PostSourceSize");
		private static readonly int TargetSizeId = Shader.PropertyToID("_NFX_PostTargetSize");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_PostParams");
		private static readonly int ModeId = Shader.PropertyToID("_NFX_PostMode");
		private static readonly int FlareId = Shader.PropertyToID("_NFX_PostFlare");
		private static readonly int FlareTexId = Shader.PropertyToID("_NFX_PostFlareTex");
		private static readonly int PlainId = Shader.PropertyToID("_NFX_PostPlain");
		private static readonly int UpscaledFinalId = Shader.PropertyToID("_NFX_UpscaledFinal");

		public Material Material;
		public float Bloom = 0.04f;        // the share of the light scattered by the lens and the eye
		public float Compensation;         // EV
		public float Contrast, Saturation; // -1 .. 1
		public int Tonemap;                // 0 ACES, 1 Neutral, 2 AgX, 3 none
		public float FlareGhosts, FlareStreaks; // the lens flare (0: none), as LensEffects weighs it

		// the meter's last reading: log2 luminance per cell, and when it came
		public readonly float[] Meter = new float[MeterW * MeterH];
		public int MeterFrame { get; private set; } = -1;

		private RTHandle source_;
		private readonly RTHandle[] down_ = new RTHandle[BloomLevels], up_ = new RTHandle[BloomLevels];
		private RenderTexture meter_;
		// DLSS upscaling (Dlaa): the display resolution's own textures (not scaled by HDRP's dynamic resolution): the
		// upscaled colour, the bloom chain and the finished picture HDRP's TAAU pass draws (NFX_Upscaled)
		private RTHandle fullSource_, fullFinal_;
		private readonly RTHandle[] fullDown_ = new RTHandle[BloomLevels], fullUp_ = new RTHandle[BloomLevels];
		private int fullWidth_, fullHeight_;
		private bool pending_;
		private readonly MaterialPropertyBlock props_ = new();

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			// written by DLAA's UnpackDlss (a compute shader): without random write Unity refused the write and the
			// jittered picture went on unresolved
			source_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, useDynamicScale: true, name: "NFX_Post_Source");
			for (int i = 0; i < BloomLevels; ++i) {
				var scale = Vector2.one / (2 << i);
				down_[i] = RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					useDynamicScale: true, name: $"NFX_Post_Down{i}");
				up_[i] = RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					useDynamicScale: true, name: $"NFX_Post_Up{i}");
			}
			meter_ = new RenderTexture(MeterW, MeterH, 0, GraphicsFormat.R32_SFloat) { name = "NFX_Post_Meter", hideFlags = HideFlags.DontSave };
			meter_.Create();
		}

		private void ReleaseFull() {
			fullSource_?.Release();
			fullFinal_?.Release();
			fullSource_ = fullFinal_ = null;
			for (int i = 0; i < BloomLevels; ++i) {
				fullDown_[i]?.Release();
				fullUp_[i]?.Release();
				fullDown_[i] = fullUp_[i] = null;
			}
			fullWidth_ = fullHeight_ = 0;
		}

		private void AllocateFull(int width, int height) {
			if (fullSource_ != null && fullWidth_ == width && fullHeight_ == height) {
				return;
			}
			ReleaseFull();
			fullWidth_ = width;
			fullHeight_ = height;
			fullSource_ = RTHandles.Alloc(width, height, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
				enableRandomWrite: true, name: "NFX_Post_FullSource");
			fullFinal_ = RTHandles.Alloc(width, height, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
				name: "NFX_Post_FullFinal");
			for (int i = 0; i < BloomLevels; ++i) {
				int w = Mathf.Max(1, width >> (i + 1)), h = Mathf.Max(1, height >> (i + 1));
				fullDown_[i] = RTHandles.Alloc(w, h, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					name: $"NFX_Post_FullDown{i}");
				fullUp_[i] = RTHandles.Alloc(w, h, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					name: $"NFX_Post_FullUp{i}");
			}
		}

		protected override void Cleanup() {
			ReleaseFull();
			source_?.Release();
			for (int i = 0; i < BloomLevels; ++i) {
				down_[i]?.Release();
				up_[i]?.Release();
			}
			if (meter_ != null) {
				meter_.Release();
				UnityEngine.Object.Destroy(meter_);
			}
		}

		private static Vector4 Size(float w, float h) => new(w, h, 1.0f / w, 1.0f / h);

		private void Draw(CommandBuffer cmd, RTHandle target, int pass, Vector4 targetSize) {
			props_.SetVector(TargetSizeId, targetSize);
			CoreUtils.SetRenderTarget(cmd, target, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, pass);
		}

		protected override void Execute(CustomPassContext ctx) {
			if (Material == null || ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}
			var cmd = ctx.cmd;
			int renderWidth = ctx.hdCamera.actualWidth, renderHeight = ctx.hdCamera.actualHeight;
			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, source_);
			// DLSS (in place of HDRP's TAA) on the HDR colour before everything here, so the meter, the bloom and the
			// flare see the anti-aliased picture: DLAA in place, or upscaled to the display resolution, where the rest of
			// this pass then runs on its own textures and leaves the picture for HDRP's TAAU pass (NFX_Upscaled)
			var cam = ctx.hdCamera.camera;
			int displayWidth = cam.pixelWidth, displayHeight = cam.pixelHeight;
			bool upscaled = false;
			if (Dlaa.UpscalingWithDlss && (displayWidth > renderWidth || displayHeight > renderHeight)) {
				AllocateFull(displayWidth, displayHeight);
				upscaled = Dlaa.Run(ctx, source_, fullSource_, renderWidth, renderHeight, displayWidth, displayHeight);
			} else {
				Dlaa.Run(ctx, source_, source_, renderWidth, renderHeight, renderWidth, renderHeight);
			}
			var source = upscaled ? fullSource_ : source_;
			var down = upscaled ? fullDown_ : down_;
			var up = upscaled ? fullUp_ : up_;
			var target = upscaled ? fullFinal_ : ctx.cameraColorBuffer;
			int width = upscaled ? displayWidth : renderWidth, height = upscaled ? displayHeight : renderHeight;
			props_.Clear();
			props_.SetFloat(PlainId, upscaled ? 1.0f : 0.0f);

			// meter
			props_.SetTexture(SourceId, source);
			props_.SetVector(TargetSizeId, Size(MeterW, MeterH));
			CoreUtils.SetRenderTarget(cmd, meter_, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, PassMeter);
			if (!pending_) {
				pending_ = true;
				int frame = Time.frameCount;
				cmd.RequestAsyncReadback(meter_, request => {
					pending_ = false;
					if (request.hasError) {
						return;
					}
					var data = request.GetData<float>();
					if (data.Length >= Meter.Length) {
						NativeArray<float>.Copy(data, Meter, Meter.Length);
						MeterFrame = frame;
					}
				});
			}

			// bloom: down the chain, then up it
			int levels = Bloom > 0.0f ? BloomLevels : 0;
			for (int i = 0; i < levels; ++i) {
				float w = Mathf.Max(1, width >> (i + 1)), h = Mathf.Max(1, height >> (i + 1));
				var from = i == 0 ? source : down[i - 1];
				float fw = i == 0 ? width : Mathf.Max(1, width >> i), fh = i == 0 ? height : Mathf.Max(1, height >> i);
				props_.SetTexture(SourceId, from);
				props_.SetVector(SourceSizeId, Size(fw, fh));
				Draw(cmd, down[i], i == 0 ? PassDownFirst : PassDown, Size(w, h));
			}
			for (int i = levels - 2; i >= 0; --i) {
				float w = Mathf.Max(1, width >> (i + 1)), h = Mathf.Max(1, height >> (i + 1));
				var low = i == levels - 2 ? down[levels - 1] : up[i + 1];
				props_.SetTexture(SourceId, down[i]);
				props_.SetTexture(LowId, low);
				props_.SetVector(SourceSizeId, Size(Mathf.Max(1, width >> (i + 2)), Mathf.Max(1, height >> (i + 2))));
				Draw(cmd, up[i], PassUp, Size(w, h));
			}

			// the lens flare (drawn in the final pass): how bright each light shows, from the bloom's quarter resolution level;
			// the threshold far above the scene's mid grey (about 0.1 exposed): lamps, headlights, the sun
			bool flare = levels >= 2 && (FlareGhosts > 0.0f || FlareStreaks > 0.0f);
			var flareParams = new Vector4(FlareGhosts, FlareStreaks, 4.0f, flare ? 1.0f : 0.0f);

			// the finished picture
			props_.SetTexture(SourceId, source);
			props_.SetTexture(FlareTexId, flare ? down[1] : source);
			props_.SetVector(FlareId, flareParams);
			props_.SetTexture(BloomId, levels > 0 ? (levels > 1 ? up[0] : down[0]) : source);
			props_.SetVector(ParamsId, new Vector4(levels > 0 ? Bloom : 0.0f, Compensation, Contrast, Saturation));
			props_.SetVector(ModeId, new Vector4(Tonemap, levels, 0.0f, 0.0f));
			Draw(cmd, target, PassFinal, Size(width, height));
			if (upscaled) {
				cmd.SetGlobalTexture(UpscaledFinalId, fullFinal_);
			}
		}
	}

	// The own post-processing's switch, its HDRP volume (HDRP's tonemapping, bloom and colour adjustments off; the
	// exposure fixed at what the eye adaptation here worked out) and the eye adaptation.
	public class PostProcess {
		public const string ShaderName = "Hidden/NatureFX/Post";
		private const float Priority = 10001.0f; // above VolumeOverhaul's

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;
		private Tonemapping tonemapping_;
		private Bloom bloom_;
		private ColorAdjustments color_;
		private Exposure exposure_;
		private PostPass pass_;
		private Material material_;
		private float ev_ = float.NaN;
		private int lastMeter_ = -1;
		private readonly float[] sorted_ = new float[PostPass.MeterW * PostPass.MeterH];
		private readonly float[] weights_ = new float[PostPass.MeterW * PostPass.MeterH];

		public string Status { get; private set; } = "off";
		public float EV100 => ev_;
		// running this frame: LensEffects then leaves the flare to it (HDRP's would come on top)
		public static bool Active { get; private set; }

		public void Create(ShaderBundle bundle) {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.Post");
			Dlaa.Hook();
			Dlaa.SetShader(bundle.Get(Dlaa.UpscaledShaderName));
			UnityEngine.Object.DontDestroyOnLoad(root_);
			profile_ = ScriptableObject.CreateInstance<VolumeProfile>();
			profile_.name = "NatureFX.Post";
			tonemapping_ = profile_.Add<Tonemapping>();
			tonemapping_.mode.Override(TonemappingMode.None);
			bloom_ = profile_.Add<Bloom>();
			bloom_.intensity.Override(0.0f);
			color_ = profile_.Add<ColorAdjustments>();
			color_.postExposure.Override(0.0f);
			color_.contrast.Override(0.0f);
			color_.saturation.Override(0.0f);
			exposure_ = profile_.Add<Exposure>();
			exposure_.mode.Override(ExposureMode.Fixed);
			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.sharedProfile = profile_;
			volume_.enabled = false;

			// the centre weighted meter: a wide Gaussian over the picture
			for (int y = 0; y < PostPass.MeterH; ++y) {
				for (int x = 0; x < PostPass.MeterW; ++x) {
					float u = (x + 0.5f) / PostPass.MeterW - 0.5f, v = (y + 0.5f) / PostPass.MeterH - 0.5f;
					weights_[y * PostPass.MeterW + x] = Mathf.Exp(-(u * u + v * v) / (2.0f * 0.3f * 0.3f));
				}
			}

			var shader = bundle.Get(ShaderName);
			if (shader == null) {
				Kino.Log.Warning($"[NatureFX] {ShaderName} not in the shader bundle; HDRP's post-processing stays");
				return;
			}
			material_ = new Material(shader) { hideFlags = HideFlags.DontSave };
			var passVolume = root_.AddComponent<CustomPassVolume>();
			passVolume.isGlobal = true;
			passVolume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
			passVolume.priority = -1000.0f; // last of the passes before post-processing (higher ones run first)
			pass_ = new PostPass { name = "NatureFX Post", Material = material_, enabled = false };
			passVolume.customPasses.Add(pass_);
		}

		public void Reload(ShaderBundle bundle) {
			Dlaa.SetShader(bundle.Get(Dlaa.UpscaledShaderName));
			var shader = bundle.Get(ShaderName);
			if (shader != null && material_ != null) {
				material_.shader = shader;
			}
		}

		public void Destroy() {
			Dlaa.Unhook();
			if (root_ != null) {
				UnityEngine.Object.Destroy(root_);
			}
			if (profile_ != null) {
				UnityEngine.Object.Destroy(profile_);
			}
			if (material_ != null) {
				UnityEngine.Object.Destroy(material_);
			}
			root_ = null;
		}

		public void Update(bool on, ISettings s) {
			if (root_ == null) {
				return;
			}
			bool active = on && s.NativePost && pass_ != null;
			Active = active;
			volume_.enabled = active;
			if (pass_ != null) {
				pass_.enabled = active;
			}
			if (!active) {
				Dlaa.Wanted = false;
				ev_ = float.NaN;
				Status = pass_ == null ? "not in the shader bundle (HDRP's post-processing)" : "off (HDRP's post-processing)";
				return;
			}
			pass_.Bloom = s.PostBloom;
			pass_.Compensation = s.Exposure;
			pass_.Contrast = s.Contrast / 100.0f;
			pass_.Saturation = s.Saturation / 100.0f;
			// the settings' "Filmic (GT)" is AgX here
			pass_.Tonemap = s.AcesTonemapping ? Mathf.Clamp(s.TonemapMode, 0, 3) : 0;
			pass_.FlareGhosts = LensEffects.FlareGhosts;
			pass_.FlareStreaks = LensEffects.FlareStreaks;
			Dlaa.Wanted = s.Dlaa;
			Dlaa.Quality = s.DlssQuality;
			Dlaa.RayReconstructionWanted = s.DlssRayReconstruction;

			// eye adaptation (with the game's exposure chosen, HDRP keeps it)
			exposure_.active = s.ExposureMode == 1;
			if (s.ExposureMode == 1 && pass_.MeterFrame != lastMeter_ && pass_.MeterFrame >= 0) {
				lastMeter_ = pass_.MeterFrame;
				// real-world EVs (the sun is raised to real sunshine, Units): a moonlit night and a sunny day where they are
				float target = Mathf.Clamp(MeteredEV(pass_.Meter), s.AutoExposureMin, Mathf.Max(s.AutoExposureMin + 0.5f, s.AutoExposureMax));
				if (float.IsNaN(ev_)) {
					ev_ = target;
				} else {
					// the eye opens up slowly in the dark and closes faster in the light
					float speed = target > ev_ ? s.AdaptationSpeed * 2.0f : s.AdaptationSpeed;
					ev_ += (target - ev_) * (1.0f - Mathf.Exp(-Time.unscaledDeltaTime * Mathf.Max(0.05f, speed)));
				}
			}
			// until the first reading HDRP keeps its own (a fixed exposure of nothing would flash the first frames)
			exposure_.active = s.ExposureMode == 1 && !float.IsNaN(ev_);
			if (!float.IsNaN(ev_)) {
				exposure_.fixedExposure.Override(ev_);
			}
			string[] curves = { "ACES", "Neutral", "AgX", "none" };
			Status = s.ExposureMode == 1
				? FormattableString.Invariant($"own: exposure {ev_:0.00} EV100, eye adaptation, bloom {s.PostBloom:0.00}, tone {curves[pass_.Tonemap]}")
				: FormattableString.Invariant($"own: the game's exposure, bloom {s.PostBloom:0.00}, tone {curves[pass_.Tonemap]}");
		}

		// the meter's cells as EV100 (K = 12.5): the brightness weighted to the centre, the darkest 30 % and the
		// brightest 5 % left out (dark corners and the sun's disc would pull it)
		private float MeteredEV(float[] meter) {
			Array.Copy(meter, sorted_, meter.Length);
			Array.Sort(sorted_);
			float lo = sorted_[(int)(sorted_.Length * 0.3f)], hi = sorted_[(int)(sorted_.Length * 0.95f)];
			float sum = 0.0f, wsum = 0.0f;
			for (int i = 0; i < meter.Length; ++i) {
				float v = meter[i];
				if (v < lo || v > hi || float.IsNaN(v)) {
					continue;
				}
				sum += v * weights_[i];
				wsum += weights_[i];
			}
			float log2L = wsum > 0.0f ? sum / wsum : 0.0f;
			return log2L + Mathf.Log(100.0f / 12.5f, 2.0f);
		}
	}
}

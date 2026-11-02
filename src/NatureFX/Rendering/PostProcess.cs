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
		private const int PassMeter = 0, PassDownFirst = 1, PassDown = 2, PassUp = 3, PassFinal = 4, PassMotionBlur = 5, PassDepthOfField = 6;
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
		private static readonly int MotionId = Shader.PropertyToID("_NFX_PostMotion");
		private static readonly int BlurId = Shader.PropertyToID("_NFX_PostBlur");
		private static readonly int LensId = Shader.PropertyToID("_NFX_PostLens");

		public Material Material;
		public float Bloom = 0.04f;        // the share of the light scattered by the lens and the eye
		public float Compensation;         // EV
		public float Contrast, Saturation; // -1 .. 1
		public int Tonemap;                // 0 ACES, 1 Neutral, 2 AgX, 3 none
		public float FlareGhosts, FlareStreaks; // the lens flare (0: none), as LensEffects weighs it
		public float MotionBlur;           // the shutter's share of a frame (0: none)
		public bool DepthOfField;
		// only the meter for the eye adaptation (the own post-processing off, HDRP's draws the picture with the
		// exposure worked out here): the exposure no longer goes back to HDRP's histogram with it
		public bool MeterOnly;
		public float FStop = 2.8f, Focus = 10.0f; // the lens's f-number, the focus distance (m)

		// the meter's last reading: log2 luminance per cell, and when it came
		public readonly float[] Meter = new float[MeterW * MeterH];
		// the cells' mean colour of what is not sky (cd/m², rgb), 0 where all is sky
		public readonly Vector3[] MeterColor = new Vector3[MeterW * MeterH];
		public Vector4 White = Vector4.zero; // the eye's white balance gains (PostProcess), 0: none
		private static readonly int WhiteId = Shader.PropertyToID("_NFX_PostWhite");
		public int MeterFrame { get; private set; } = -1;

		private RTHandle source_;
		private readonly RTHandle[] down_ = new RTHandle[BloomLevels], up_ = new RTHandle[BloomLevels];
		private RenderTexture meter_;
		// DLSS upscaling (Dlaa): the display resolution's own textures (not scaled by HDRP's dynamic resolution): the
		// upscaled colour, the bloom chain and the finished picture HDRP's TAAU pass draws (NFX_Upscaled)
		private RTHandle fullSource_, fullFinal_, fullWork_;
		// motion blur and depth of field: the motion at the render resolution, a second colour target
		private RTHandle motion_, work_;
		private readonly RTHandle[] fullDown_ = new RTHandle[BloomLevels], fullUp_ = new RTHandle[BloomLevels];
		private int fullWidth_, fullHeight_;
		private bool pending_;
		private readonly MaterialPropertyBlock props_ = new();

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			// written by DLAA's UnpackDlss (a compute shader): without random write Unity refused the write and the
			// jittered picture went on unresolved
			// (the camera colour copy shared with the other composites: SharedColor has random write on)
			source_ = SharedColor.Acquire();
			for (int i = 0; i < BloomLevels; ++i) {
				var scale = Vector2.one / (2 << i);
				down_[i] = RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					useDynamicScale: true, name: $"NFX_Post_Down{i}");
				up_[i] = RTHandles.Alloc(scale, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.B10G11R11_UFloatPack32,
					useDynamicScale: true, name: $"NFX_Post_Up{i}");
			}
			motion_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16_SFloat,
				enableRandomWrite: true, useDynamicScale: true, name: "NFX_Post_Motion");
			work_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
				useDynamicScale: true, name: "NFX_Post_Work");
			meter_ = new RenderTexture(MeterW, MeterH, 0, GraphicsFormat.R32G32B32A32_SFloat) { name = "NFX_Post_Meter", hideFlags = HideFlags.DontSave };
			meter_.Create();
		}

		private void ReleaseFull() {
			fullSource_?.Release();
			fullFinal_?.Release();
			fullWork_?.Release();
			fullSource_ = fullFinal_ = fullWork_ = null;
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
			fullWork_ = RTHandles.Alloc(width, height, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
				name: "NFX_Post_FullWork");
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
			motion_?.Release();
			work_?.Release();
			SharedColor.Release(source_);
			source_ = null;
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
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: post-processing"); // (the debug page)
			if (Material == null || ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}
			var cmd = ctx.cmd;
			if (MeterOnly) {
				props_.Clear();
				props_.SetFloat(PlainId, 0.0f);
				Measure(cmd, ctx.cameraColorBuffer, ctx.hdCamera.actualWidth, ctx.hdCamera.actualHeight);
				return;
			}
			Dlaa.NotePost(ctx.hdCamera.camera);
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

			// motion blur, then depth of field, on the scene's light (before the meter and the bloom), between source and a
			// second target; what they end in is the source for the rest
			bool blur = MotionBlur > 0.001f, dof = DepthOfField;
			if (blur || dof) {
				var work = upscaled ? fullWork_ : work_;
				var current = source;
				if (blur) {
					NativeEngine.PrepareMotion(cmd, NativeEngine.Bridge, ctx, motion_, renderWidth, renderHeight, Dlaa.RanThisFrame);
					props_.SetTexture(MotionId, motion_);
				}
				// a 24 mm high (full frame) sensor: the field of view gives the focal length
				float focal = 12.0f / Mathf.Tan(Mathf.Max(1.0f, cam.fieldOfView) * 0.5f * Mathf.Deg2Rad);
				props_.SetVector(LensId, new Vector4(focal, Mathf.Max(0.7f, FStop), Mathf.Max(0.1f, Focus), 24.0f));
				props_.SetVector(BlurId, new Vector4(Mathf.Clamp01(MotionBlur), 0.05f * height, 0.0f, 0.02f * height));
				props_.SetVector(SourceSizeId, Size(width, height));
				if (blur) {
					props_.SetTexture(SourceId, current);
					Draw(cmd, work, PassMotionBlur, Size(width, height));
					current = work;
				}
				if (dof) {
					var target2 = current == source ? work : source;
					props_.SetTexture(SourceId, current);
					Draw(cmd, target2, PassDepthOfField, Size(width, height));
					current = target2;
				}
				source = current;
			}

			Measure(cmd, source, width, height);

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
			props_.SetVector(SourceSizeId, Size(width, height));
			props_.SetTexture(FlareTexId, flare ? down[1] : source);
			props_.SetVector(FlareId, flareParams);
			props_.SetTexture(BloomId, levels > 0 ? (levels > 1 ? up[0] : down[0]) : source);
			props_.SetVector(ParamsId, new Vector4(levels > 0 ? Bloom : 0.0f, Compensation, Contrast, Saturation));
			props_.SetVector(ModeId, new Vector4(Tonemap, levels, 0.0f, 0.0f));
			props_.SetVector(WhiteId, White);
			Draw(cmd, target, PassFinal, Size(width, height));
			if (upscaled) {
				cmd.SetGlobalTexture(UpscaledFinalId, fullFinal_);
			}
		}

		// the meter (64x36 cells of the picture's light), read back without waiting
		private void Measure(CommandBuffer cmd, RTHandle source, float width, float height) {
			props_.SetTexture(SourceId, source);
			props_.SetVector(SourceSizeId, Size(width, height));
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
					if (data.Length >= Meter.Length * 4) {
						for (int i = 0; i < Meter.Length; ++i) {
							Meter[i] = data[i * 4];
							MeterColor[i] = new Vector3(data[i * 4 + 1], data[i * 4 + 2], data[i * 4 + 3]);
						}
						MeterFrame = frame;
					}
				});
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
		private float lastMeterTime_ = -1.0f;
		private const float FlashSeconds = 0.5f;
		private readonly System.Collections.Generic.List<(float Time, float Target)> recent_ = new();
		// the exposure is NatureFX's alone (HDRP's own automatic exposure off: VolumeOverhaul): eye adaptation on and
		// the own post-processing running. HDRP's histogram is only the fallback while it is not
		public static bool OwnExposure { get; private set; }
		// HDRP's exposure now (ExposureProbe): where the own adaptation starts, so taking over does not jump
		public static float HdrpEV = float.NaN;
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
			// the own post-processing off: its eye adaptation still runs (only the meter), HDRP's post-processing draws
			// the picture at the exposure worked out here
			bool meterOnly = on && !active && pass_ != null && s.ExposureMode == 1;
			// the game's exposure, raised as far as NatureFX raised the sun (Units): the track's exposure was set for its
			// sun of a few lux, against real sunshine it left the picture white
			bool gameScaled = on && s.ExposureMode == 0;
			Active = active;
			volume_.enabled = active || meterOnly || gameScaled;
			tonemapping_.active = active;
			bloom_.active = active;
			color_.active = active;
			if (pass_ != null) {
				pass_.enabled = active || meterOnly;
				pass_.MeterOnly = meterOnly;
			}
			if (!active) {
				HdrpBlur(false);
				Dlaa.Wanted = false;
			}
			if (!active && !meterOnly) {
				OwnExposure = false;
				exposure_.active = false;
				ev_ = float.NaN;
				if (gameScaled) {
					GameExposure();
					Status = FormattableString.Invariant($"HDRP's post-processing; the game's exposure {gameWhat_}");
					return;
				}
				Status = pass_ == null ? "not in the shader bundle (HDRP's post-processing)" : "off (HDRP's post-processing)";
				return;
			}
			if (meterOnly) {
				Adapt(s);
				Status = FormattableString.Invariant($"HDRP's post-processing; own exposure {ev_:0.00} EV100, eye adaptation");
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
			pass_.MotionBlur = s.MotionBlur;
			pass_.DepthOfField = s.DepthOfField;
			pass_.FStop = s.DofFStop > 0.0f ? s.DofFStop : 2.8f;
			pass_.Focus = LensEffects.FocusDistance;
			HdrpBlur(true);
			Dlaa.Wanted = s.Dlaa;
			Dlaa.Quality = s.DlssQuality;
			Dlaa.RayReconstructionWanted = s.DlssRayReconstruction;
			Adapt(s);
			string[] curves = { "ACES", "Neutral", "AgX", "none" };
			Status = s.ExposureMode == 1
				? FormattableString.Invariant($"own: exposure {ev_:0.00} EV100, eye adaptation, white balance {(warm_ >= 0 ? "cooler" : "warmer")} by {Mathf.Abs(warm_):0.00} stops (red / blue), bloom {s.PostBloom:0.00}, tone {curves[pass_.Tonemap]}")
				: FormattableString.Invariant($"own: the game's exposure {gameWhat_}, bloom {s.PostBloom:0.00}, tone {curves[pass_.Tonemap]}");
		}

		// The game's exposure (its highest priority volume with one), shifted by the stops NatureFX raised the sun
		// (Units.Log2Scale): a fixed one stays fixed, an automatic one keeps its mode with its limits moved; none: HDRP's
		// default fixed 0 EV, raised. Looked up every 2 s.
		private float nextGameScan_;
		private string gameWhat_ = "";
		private ExposureMode gameMode_ = ExposureMode.Fixed;
		private float gameFixed_, gameMin_, gameMax_, gameCompensation_;
		private void GameExposure() {
			if (Time.unscaledTime >= nextGameScan_) {
				nextGameScan_ = Time.unscaledTime + 2.0f;
				gameMode_ = ExposureMode.Fixed;
				gameFixed_ = 0.0f;
				gameMin_ = 0.0f;
				gameMax_ = 0.0f;
				gameCompensation_ = 0.0f;
				float best = float.NegativeInfinity;
				foreach (var v in UnityEngine.Object.FindObjectsByType<Volume>(FindObjectsSortMode.None)) {
					if (v == null || !v.isActiveAndEnabled || !v.isGlobal || v.gameObject.name.StartsWith("NatureFX", StringComparison.Ordinal) || v.priority < best) {
						continue;
					}
					var profile = v.sharedProfile;
					if (profile == null || profile.name.StartsWith("NatureFX", StringComparison.Ordinal) || !profile.TryGet<Exposure>(out var e) || !e.active || !e.mode.overrideState) {
						continue;
					}
					best = v.priority;
					gameMode_ = e.mode.value;
					gameFixed_ = e.fixedExposure.value;
					gameMin_ = e.limitMin.value;
					gameMax_ = e.limitMax.value;
					gameCompensation_ = e.compensation.value;
				}
			}
			float up = Units.Log2Scale;
			exposure_.active = true;
			if (gameMode_ == ExposureMode.Automatic || gameMode_ == ExposureMode.AutomaticHistogram) {
				exposure_.mode.Override(gameMode_);
				exposure_.limitMin.Override(gameMin_ + up);
				exposure_.limitMax.Override(gameMax_ + up);
				exposure_.compensation.Override(gameCompensation_);
				gameWhat_ = FormattableString.Invariant($"(automatic, {gameMin_ + up:0.0} to {gameMax_ + up:0.0} EV, +{up:0.0} for the real sun)");
			} else {
				exposure_.mode.Override(ExposureMode.Fixed);
				exposure_.fixedExposure.Override(gameFixed_ + up);
				gameWhat_ = FormattableString.Invariant($"(fixed {gameFixed_ + up:0.0} EV, +{up:0.0} for the real sun)");
			}
		}

		// eye adaptation (with the game's exposure chosen, HDRP keeps it)
		private void Adapt(ISettings s) {
			if (s.ExposureMode == 0) {
				ev_ = float.NaN; // nothing of the eye adaptation's left over (its fixed exposure, the white balance)
				GameExposure();
			} else {
				exposure_.active = true;
				exposure_.mode.Override(ExposureMode.Fixed);
			}
			if (s.ExposureMode == 1 && float.IsNaN(ev_) && !float.IsNaN(HdrpEV)) {
				// taking over: from where HDRP's exposure is (it adapted until now), not a jump to the first reading
				ev_ = HdrpEV;
			}
			if (s.ExposureMode == 1 && pass_.MeterFrame != lastMeter_ && pass_.MeterFrame >= 0) {
				lastMeter_ = pass_.MeterFrame;
				// real-world EVs (the sun is raised to real sunshine, Units): a moonlit night and a sunny day where they are
				float metered = MeteredEV(pass_.Meter);
				float target = Mathf.Clamp(metered + NightKey(metered, s.NightDarkness), s.AutoExposureMin, Mathf.Max(s.AutoExposureMin + 0.5f, s.AutoExposureMax));
				// a flash (an afterfire, a spark, a lamp swinging past) is not adapted to: the eye closes only to light
				// that stays, so going brighter takes the darkest reading of the last half second (the headlights' road
				// went dark at every afterfire)
				recent_.Add((Time.unscaledTime, target));
				recent_.RemoveAll(r => Time.unscaledTime - r.Time > FlashSeconds);
				float lasting = target;
				foreach (var r in recent_) {
					lasting = Mathf.Min(lasting, r.Target);
				}
				if (!float.IsNaN(ev_) && target > ev_) {
					target = Mathf.Max(ev_, lasting);
				}
				// the time since the last reading (readings come every frame or two): adapting by one frame's time per
				// reading made the eye half as fast when they came every other frame, and uneven
				float dt = lastMeterTime_ < 0.0f ? Time.unscaledDeltaTime : Mathf.Clamp(Time.unscaledTime - lastMeterTime_, 0.0f, 0.25f);
				lastMeterTime_ = Time.unscaledTime;
				if (float.IsNaN(ev_)) {
					ev_ = target;
				} else if (!float.IsNaN(target)) {
					// the eye opens up slowly in the dark and closes faster in the light
					float speed = target > ev_ ? s.AdaptationSpeed * 2.0f : s.AdaptationSpeed;
					ev_ += (target - ev_) * (1.0f - Mathf.Exp(-dt * Mathf.Max(0.05f, speed)));
				}
			}
			if (float.IsNaN(ev_) || float.IsInfinity(ev_)) {
				ev_ = float.NaN;
			}
			OwnExposure = s.ExposureMode == 1 && !float.IsNaN(ev_);
			// until the first reading HDRP keeps its own (a fixed exposure of nothing would flash the first frames)
			if (s.ExposureMode == 1) {
				exposure_.active = !float.IsNaN(ev_);
			}
			pass_.White = WhiteBalance(s);
			if (!float.IsNaN(ev_)) {
				// the compensation: the own final pass's with the own post-processing, HDRP's fixed exposure without it
				exposure_.fixedExposure.Override(pass_ != null && pass_.MeterOnly ? ev_ - s.Exposure : ev_);
			}
		}

		// HDRP's motion blur and depth of field off on the game's camera while the own ones run (its frame settings,
		// overridden per camera), put back as they were when the own post-processing is off
		private HDAdditionalCameraData blurCamera_;
		private bool blurCustom_, blurMaskMotion_, blurMaskDof_, blurMotion_, blurDof_;

		private void HdrpBlur(bool own) {
			var cam = Camera.main;
			var data = cam != null ? cam.GetComponent<HDAdditionalCameraData>() : null;
			if (blurCamera_ != null && (blurCamera_ != data || !own)) {
				var mask = blurCamera_.renderingPathCustomFrameSettingsOverrideMask;
				mask.mask[(uint)FrameSettingsField.MotionBlur] = blurMaskMotion_;
				mask.mask[(uint)FrameSettingsField.DepthOfField] = blurMaskDof_;
				blurCamera_.renderingPathCustomFrameSettingsOverrideMask = mask;
				var fs = blurCamera_.renderingPathCustomFrameSettings;
				fs.SetEnabled(FrameSettingsField.MotionBlur, blurMotion_);
				fs.SetEnabled(FrameSettingsField.DepthOfField, blurDof_);
				blurCamera_.renderingPathCustomFrameSettings = fs;
				blurCamera_.customRenderingSettings = blurCustom_;
				blurCamera_ = null;
			}
			if (!own || data == null || blurCamera_ == data) {
				return;
			}
			blurCamera_ = data;
			blurCustom_ = data.customRenderingSettings;
			var m = data.renderingPathCustomFrameSettingsOverrideMask;
			blurMaskMotion_ = m.mask[(uint)FrameSettingsField.MotionBlur];
			blurMaskDof_ = m.mask[(uint)FrameSettingsField.DepthOfField];
			m.mask[(uint)FrameSettingsField.MotionBlur] = true;
			m.mask[(uint)FrameSettingsField.DepthOfField] = true;
			data.renderingPathCustomFrameSettingsOverrideMask = m;
			var f = data.renderingPathCustomFrameSettings;
			blurMotion_ = f.IsEnabled(FrameSettingsField.MotionBlur);
			blurDof_ = f.IsEnabled(FrameSettingsField.DepthOfField);
			f.SetEnabled(FrameSettingsField.MotionBlur, false);
			f.SetEnabled(FrameSettingsField.DepthOfField, false);
			data.renderingPathCustomFrameSettings = f;
			data.customRenderingSettings = true;
		}

		// The eye's white balance (chromatic adaptation): the light the surfaces in view return (the meter's colour of
		// what is not sky, which a scene's own colours average out over) taken partly as white, along the warm / cool
		// axis only (sunset, the blue of a shade under a clear sky, warm lamps; a green forest is not turned magenta).
		// Partial, as the eye: Settings.WhiteBalance at daylight, less in the dark (at a street lamp's few EV the eye
		// leaves its colour), slowly (seconds, like the eye's own).
		private float warm_;
		private int lastWhiteMeter_ = -1;
		private Vector4 WhiteBalance(ISettings s) {
			float strength = Mathf.Clamp01(s.WhiteBalance);
			if (strength <= 0.0f || float.IsNaN(ev_)) {
				warm_ = 0.0f;
				return Vector4.zero;
			}
			if (pass_.MeterFrame != lastWhiteMeter_ && pass_.MeterFrame >= 0) {
				lastWhiteMeter_ = pass_.MeterFrame;
				Vector3 sum = Vector3.zero;
				for (int i = 0; i < pass_.MeterColor.Length; ++i) {
					var c = pass_.MeterColor[i];
					if (float.IsNaN(c.x) || float.IsNaN(c.y) || float.IsNaN(c.z)) {
						continue;
					}
					sum += c * weights_[i];
				}
				if (sum.x > 1e-6f && sum.z > 1e-6f) {
					// log2 of red over blue: 0 under daylight white (D65 in the linear Rec. 709 primaries)
					float target = Mathf.Clamp(Mathf.Log(sum.x / sum.z, 2.0f), -3.0f, 3.0f);
					target *= strength * Mathf.Clamp01((ev_ - 2.0f) / 6.0f);
					warm_ += (target - warm_) * (1.0f - Mathf.Exp(-Time.unscaledDeltaTime * 0.7f));
				}
			}
			float r = Mathf.Pow(2.0f, -warm_ * 0.5f), b = Mathf.Pow(2.0f, warm_ * 0.5f);
			float luma = 0.2126f * r + 0.7152f + 0.0722f * b;
			return new Vector4(r / luma, 1.0f / luma, b / luma, 0.0f);
		}

		public float WarmShift => warm_;

		// The eye does not see a dark scene as a grey one: the darker the scene, the lower the brightness it settles on
		// (Krawczyk et al. 2005, the key value a = 1.03 - 2 / (2 + log10(L + 1)) of the scene's mean luminance L, taken
		// relative to daylight's). Stops darker than mid grey: about 0.3 at dusk light (EV 10), 2 under street lamps
		// (EV 3), 3.6 at EV 0; times Settings.NightDarkness.
		private static float NightKey(float ev, float strength) {
			if (float.IsNaN(ev) || strength <= 0.0f) {
				return 0.0f;
			}
			static float Key(float luminance) => 1.03f - 2.0f / (2.0f + Mathf.Log10(luminance + 1.0f));
			float L = Mathf.Pow(2.0f, ev) * 12.5f / 100.0f;
			float day = Key(2048.0f);
			float key = Mathf.Max(Key(L), 0.02f);
			return Mathf.Clamp(Mathf.Log(day / key, 2.0f), 0.0f, 5.0f) * Mathf.Clamp01(strength);
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
			// the eye also settles on what is bright in view: the brightest 3 % at most 2.5 stops over mid grey (a road
			// under the headlights in a black night opened the exposure to its darkest and blew out; the lit road there
			// is less than a tenth of the picture, so the brightest tenth still held dark cells)
			float bright = sorted_[(int)(sorted_.Length * 0.97f)];
			// (2 stops: at 2.5 the road under the headlights still burnt out at night)
			if (!float.IsNaN(bright)) {
				log2L = Mathf.Max(log2L, bright - 2.0f);
			}
			// and on what is lit when it fills a tenth of the view: the street lights' pools on a night road (the black sky
			// over half the picture held the eye wide open, the lamps' pools came out white; the brightest 3 % there are
			// only the lamps' heads)
			float lit = sorted_[(int)(sorted_.Length * 0.90f)];
			if (!float.IsNaN(lit)) {
				log2L = Mathf.Max(log2L, lit - 2.0f);
			}
			return log2L + Mathf.Log(100.0f / 12.5f, 2.0f);
		}
	}
}

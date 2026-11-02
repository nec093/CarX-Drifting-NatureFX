using System.Collections.Generic;
using System.Reflection;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// NVIDIA DLSS (NFXNative Dlss.inl) in place of HDRP's TAA, with the own post-processing.
	//   DLAA (quality 0): HDRP renders at the display resolution with its anti-aliasing off; the camera is jittered here
	//   (Halton 2, 3 over 16 frames, in its projection matrix) and PostPass hands the HDR colour to DLSS before its
	//   meter, bloom and tone curve.
	//   Upscaling (quality 1..4): HDRP renders at a lower resolution (its software dynamic resolution, forced to the
	//   mode's share) with its TAAU upscaler chosen, so it jitters the camera itself and everything after the upscale
	//   (depth of field, motion blur, the final pass) runs at the display resolution. PostPass upscales the HDR colour
	//   with DLSS and makes the finished picture at the display resolution; HDRP's TAA material draws it in place of
	//   its TAAU (NFX_Upscaled.shader).
	// The motion DLSS gets is worked out from the camera without the jitter (PrepareDlss). Everything HDRP's is put back
	// when this is off or DLSS cannot run.
	public static class Dlaa {
		private const int Phases = 16;
		public const string UpscaledShaderName = "Hidden/NatureFX/Upscaled";

		// the settings, with the own post-processing on (PostProcess.Update, every frame)
		public static bool Wanted;
		public static int Quality;   // 0 DLAA, 1 Quality, 2 Balanced, 3 Performance, 4 Ultra performance
		public static bool RayReconstructionWanted;
		// this frame DLSS Ray Reconstruction takes the noisy sun shadows and reflections (Begin): RTShadowPass and
		// RTReflectionPass leave them raw and draw the guides; off when the engine says it cannot (no RR)
		public static bool RayReconstruction { get; private set; }
		public static readonly string[] Qualities = { "DLAA", "Quality", "Balanced", "Performance", "Ultra perf." };
		private static readonly float[] Shares = { 1.0f, 0.667f, 0.58f, 0.5f, 0.333f };
		// NVSDK_NGX_PerfQuality_Value: MaxPerf 0, Balanced 1, MaxQuality 2, UltraPerformance 3, UltraQuality 4, DLAA 5
		private static readonly int[] NgxQualities = { 5, 2, 1, 0, 3 };
		// Developer > DLAA (not saved): the jitter's sign handed to DLSS (0 both negated, as HDRP's DLSS; 1 as is;
		// 2 x negated; 3 y negated), the motion (0 the camera's and what moves of itself; 1 the camera's only; 2 none),
		// the jitter amount (DLAA's own; 0 none)
		public static int DevJitterSign, DevMotion;
		// Ray Reconstruction while upscaling, which is off for now (shaking, light noise, a GPU hang): to look into it
		public static bool DevRayReconstructionUpscaling;
		// the same lower resolution upscaled by HDRP's own TAAU instead of DLSS: what is DLSS's and what the
		// resolution's
		public static bool DevHdrpTaau;
		public static float DevJitterScale = 1.0f;
		public static readonly string[] JitterSigns = { "-x -y", "+x +y", "-x +y", "+x -y" };
		public static readonly string[] Motions = { "Camera + moving objects", "Camera only", "None" };

		private static bool hooked_, failed_;
		private static Camera jittered_, camera_;
		private static HDAdditionalCameraData overridden_;
		private static HDAdditionalCameraData.AntialiasingMode original_;
		private static bool originalDynamicRes_;
		private static float originalAntiFlicker_;
		private static Vector2 jitter_;
		private static int phase_, ranFrame_ = -1, checkFrame_;
		private static Vector3 lastPosition_;
		// the camera without the jitter, this frame and the one before (Begin), for the motion handed to DLSS
		private static Matrix4x4 projection_, view_, projectionPrev_, viewPrev_;
		private static Vector3 position_;
		private static int matricesFrame_ = -10;
		private static string failure_;
		private static int contextsLogged_, mainChanges_, missedLogged_, otherLogged_;
		private static string lastMode_;
		private static string lastContext_;
		private static Camera lastMain_;
		private static int postFrame_ = -1;

		// PostPass ran for this camera (diagnostics: post-processing on a camera other than the main one)
		public static void NotePost(Camera cam) {
			if (cam == camera_) {
				postFrame_ = Time.frameCount;
			} else if (otherLogged_ < 6) {
				++otherLogged_;
				Kino.Log.Info($"[NatureFX] DLSS diagnostics: frame {Time.frameCount} own post-processing on {cam.name} (not the main camera {(camera_ != null ? camera_.name : "-")})");
			}
		}
		// upscaling: HDRP's dynamic resolution settings as they were, its TAA material and shader
		private static Shader upscaledShader_;
		private static bool drsChanged_;
		private static GlobalDynamicResolutionSettings originalDrs_;
		private static Material taaMaterial_;
		private static Shader taaShader_;
		private static readonly FieldInfo SettingsField = typeof(HDRenderPipelineAsset).GetField("m_RenderPipelineSettings", BindingFlags.NonPublic | BindingFlags.Instance);
		private static readonly FieldInfo TaaMaterialField = typeof(HDRenderPipeline).GetField("m_TemporalAAMaterial", BindingFlags.NonPublic | BindingFlags.Instance);

		// DLSS ran on this frame (NativeEngine.DlssBuffer holds its motion and depth)
		public static bool RanThisFrame => ranFrame_ == Time.frameCount;

		public static bool Active => Wanted && !failed_ && NativeEngine.Ready && NativeEngine.Bridge != null && NativeEngine.Supports("dlss") && Settled;

		// DLSS started while the game was still starting up (its first scenes loading) left parts of the menu UI and
		// of the models flickering black or in other colours for the rest of the session (turning DLSS off did not
		// mend it); switched on later it never did. The first start waits until the own engine has traced a scene
		// for a while; once started, it may come and go.
		private const float SettleSeconds = 10.0f;
		private static float sunReadySince_ = -1.0f;
		private static bool settled_;
		private static bool Settled {
			get {
				if (settled_) {
					return true;
				}
				// without a traced scene (ray tracing off) a minute after start-up will do
				if (!NativeEngine.SunReady) {
					sunReadySince_ = -1.0f;
					settled_ = Time.realtimeSinceStartup >= 60.0f;
					return settled_;
				}
				if (sunReadySince_ < 0.0f) {
					sunReadySince_ = Time.realtimeSinceStartup;
				}
				settled_ = Time.realtimeSinceStartup - sunReadySince_ >= SettleSeconds;
				return settled_;
			}
		}
		// this frame HDRP renders at the lower resolution and PostPass upscales (Begin)
		public static bool Upscaling { get; private set; }
		// PostPass upscales with DLSS this frame (not HDRP's TAAU, Developer)
		public static bool UpscalingWithDlss => Upscaling && !DevHdrpTaau;

		public static string Status {
			get {
				if (failed_) {
					return $"off, TAA kept ({failure_})";
				}
				if (!Wanted) {
					return "off (HDRP's TAA)";
				}
				if (!Active) {
					if (NativeEngine.Ready && !NativeEngine.Supports("dlss")) {
						return "waiting for the engine's new DLL (HDRP's TAA)";
					}
					if (NativeEngine.Ready && !settled_) {
						return sunReadySince_ < 0.0f ? "waiting for the game to finish starting (HDRP's TAA)"
							: $"starting in {Mathf.CeilToInt(SettleSeconds - (Time.realtimeSinceStartup - sunReadySince_))} s, after the game's start-up (HDRP's TAA)";
					}
					return "waiting for the own engine (HDRP's TAA)";
				}
				if (RayReconstructionWanted && Quality > 0 && !DevRayReconstructionUpscaling) {
					return $"{NativeEngine.DlssStatus} (Ray Reconstruction with DLAA only for now)";
				}
				if (Quality > 0 && upscaledShader_ == null) {
					return $"{NativeEngine.DlssStatus} (upscaling needs {UpscaledShaderName} from the shader bundle: DLAA meanwhile)";
				}
				return NativeEngine.DlssStatus;
			}
		}

		public static void SetShader(Shader upscaled) {
			upscaledShader_ = upscaled;
		}

		public static void Hook() {
			if (hooked_) {
				return;
			}
			hooked_ = true;
			RenderPipelineManager.beginContextRendering += Begin;
			RenderPipelineManager.endContextRendering += End;
		}

		public static void Unhook() {
			if (!hooked_) {
				return;
			}
			hooked_ = false;
			RenderPipelineManager.beginContextRendering -= Begin;
			RenderPipelineManager.endContextRendering -= End;
			RestoreAll();
		}

		private static float Halton(int index, int radix) {
			float result = 0.0f, fraction = 1.0f / radix;
			for (; index > 0; index /= radix, fraction /= radix) {
				result += (index % radix) * fraction;
			}
			return result;
		}

		// Before HDRP prepares any camera of the frame: it takes the camera's matrices and the dynamic resolution
		// settings then, before its own beginCameraRendering (a jitter put in there only reached the next frame's
		// culling, never the picture)
		private static void Begin(ScriptableRenderContext context, List<Camera> cameras) {
			var cam = Camera.main;
			// diagnostics (the menu UI and models flickered with DLSS on): the cameras each context renders, and a
			// main camera that changes from frame to frame
			if (contextsLogged_ < 12 && cameras.Count > 0) {
				var names = new List<string>();
				foreach (var c in cameras) {
					names.Add($"{(c != null ? c.name : "null")} ({(c != null ? c.cameraType.ToString() : "-")}{(c != null && c.targetTexture != null ? ", to a texture" : "")}{(c == cam ? ", main" : "")})");
				}
				string line = string.Join(", ", names);
				if (line != lastContext_) {
					lastContext_ = line;
					++contextsLogged_;
					Kino.Log.Info($"[NatureFX] DLSS diagnostics: frame {Time.frameCount} context renders {line}");
				}
			}
			if (cam != null && cam != lastMain_) {
				if (lastMain_ != null && mainChanges_++ < 12) {
					Kino.Log.Info($"[NatureFX] DLSS diagnostics: frame {Time.frameCount} main camera {lastMain_.name} -> {cam.name}");
				}
				lastMain_ = cam;
			}
			if (cam == null || cam.cameraType != CameraType.Game || !cameras.Contains(cam)) {
				return;
			}
			CheckFailure();
			if (!Active) {
				RestoreAll();
				return;
			}
			var data = cam.GetComponent<HDAdditionalCameraData>();
			if (data == null) {
				RestoreAll();
				return;
			}
			if (overridden_ != data) {
				RestoreAll();
				overridden_ = data;
				original_ = data.antialiasing;
				originalDynamicRes_ = data.allowDynamicResolution;
				originalAntiFlicker_ = data.taaAntiFlicker;
			}
			camera_ = cam;

			var projection = cam.projectionMatrix;
			bool continuous = matricesFrame_ == Time.frameCount - 1;
			projectionPrev_ = continuous ? projection_ : projection;
			viewPrev_ = continuous ? view_ : cam.worldToCameraMatrix;
			projection_ = projection;
			view_ = cam.worldToCameraMatrix;
			position_ = cam.transform.position;
			matricesFrame_ = Time.frameCount;

			// Ray Reconstruction: the guides come from the exact sun light's G-buffer (RTShadowPass): only while that
			// is drawn
			// with DLAA only for now: upscaling with it shook the picture, threw light noise and once hung the GPU
			// (DXGI_ERROR_DEVICE_HUNG while switching modes)
			int quality = Mathf.Clamp(Quality, 0, Shares.Length - 1);
			RayReconstruction = RayReconstructionWanted && (quality == 0 || DevRayReconstructionUpscaling) && NativeEngine.DlssRayReconstructionAvailable != 0 &&
			                    Time.frameCount - RTShadowPass.ExactFrame <= 2;
			Upscaling = quality > 0 && upscaledShader_ != null && SwapTaa(!DevHdrpTaau) && SetDynamicResolution(Shares[quality] * 100.0f);
			// the sun shadows' rays kept about the display resolution's: a moving shadow over still ground has no
			// history, and the render resolution's noise was upscaled with it (TAAU the same: not DLSS's doing)
			NativeEngine.SunSamples = Upscaling ? Mathf.Clamp(Mathf.RoundToInt(1.0f / (Shares[quality] * Shares[quality])), 1, 4) : 1;
			// each mode change in the log, with the frame: a GPU hang after one (NFXNative-gpu-hang.txt) can be matched to it
			string mode = $"{Qualities[quality]}{(Upscaling ? " upscaling" : "")}{(RayReconstruction ? " + RR" : "")}";
			if (mode != lastMode_) {
				Kino.Log.Info($"[NatureFX] DLSS mode: {lastMode_ ?? "-"} -> {mode} at frame {Time.frameCount} ({cam.pixelWidth}x{cam.pixelHeight}, sun rays {NativeEngine.SunSamples})");
				lastMode_ = mode;
			}
			if (Upscaling) {
				// HDRP's TAAU path: it jitters the camera (taaJitter) and upscales in its TAA pass, drawn by NFX_Upscaled;
				// its anti-flicker at 0 tells that pass from the one after a physically based depth of field (3.5)
				data.antialiasing = HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing;
				data.allowDynamicResolution = true;
				data.taaAntiFlicker = DevHdrpTaau ? originalAntiFlicker_ : 0.0f;
				return;
			}
			SetDynamicResolution(-1.0f);
			SwapTaa(false);
			data.allowDynamicResolution = originalDynamicRes_;
			data.taaAntiFlicker = originalAntiFlicker_;
			data.antialiasing = HDAdditionalCameraData.AntialiasingMode.None;
			phase_ = (phase_ + 1) % Phases;
			jitter_ = new Vector2(Halton(phase_ + 1, 2) - 0.5f, Halton(phase_ + 1, 3) - 0.5f) * DevJitterScale;
			projection.m02 += jitter_.x * 2.0f / Mathf.Max(1, cam.pixelWidth);
			projection.m12 += jitter_.y * 2.0f / Mathf.Max(1, cam.pixelHeight);
			cam.projectionMatrix = projection;
			jittered_ = cam;
		}

		private static void End(ScriptableRenderContext context, List<Camera> cameras) {
			// a frame jittered without DLSS run on it: the raw jittered picture went to the screen
			if (jittered_ != null && ranFrame_ != Time.frameCount && missedLogged_ < 12) {
				++missedLogged_;
				Kino.Log.Info($"[NatureFX] DLSS diagnostics: frame {Time.frameCount} {jittered_.name} jittered but DLSS did not run (post pass for it: frame {postFrame_})");
			}
			if (jittered_ != null) {
				jittered_.ResetProjectionMatrix();
				jittered_ = null;
			}
		}

		// HDRP's dynamic resolution forced to this share (percent) of the display's, software, TAAU; below 0 put back
		private static bool SetDynamicResolution(float percent) {
			var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
			if (asset == null || SettingsField == null) {
				return false;
			}
			var settings = (RenderPipelineSettings)SettingsField.GetValue(asset);
			if (percent < 0.0f) {
				if (drsChanged_) {
					settings.dynamicResolutionSettings = originalDrs_;
					SettingsField.SetValue(asset, settings);
					drsChanged_ = false;
				}
				return true;
			}
			if (!drsChanged_) {
				originalDrs_ = settings.dynamicResolutionSettings;
				drsChanged_ = true;
			}
			var drs = settings.dynamicResolutionSettings;
			drs.enabled = true;
			drs.enableDLSS = false;
			drs.dynResType = DynamicResolutionType.Software;
			drs.upsampleFilter = DynamicResUpscaleFilter.TAAU;
			drs.forceResolution = true;
			drs.forcedPercentage = percent;
			drs.minPercentage = Mathf.Min(drs.minPercentage > 0.0f ? drs.minPercentage : percent, percent);
			drs.maxPercentage = 100.0f;
			drs.useMipBias = true;
			settings.dynamicResolutionSettings = drs;
			SettingsField.SetValue(asset, settings);
			return true;
		}

		// HDRP's TAA material drawing NFX_Upscaled (on) or its own shader again (off)
		private static bool SwapTaa(bool on) {
			var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
			var material = pipeline != null && TaaMaterialField != null ? TaaMaterialField.GetValue(pipeline) as Material : null;
			if (taaMaterial_ != null && taaMaterial_ != material) {
				// the pipeline was made again: the old material is gone with it
				taaMaterial_ = null;
			}
			if (!on) {
				if (taaMaterial_ != null && taaShader_ != null) {
					taaMaterial_.shader = taaShader_;
				}
				taaMaterial_ = null;
				return true;
			}
			if (material == null || upscaledShader_ == null) {
				return false;
			}
			if (taaMaterial_ == null) {
				taaMaterial_ = material;
				taaShader_ = material.shader;
			}
			if (material.shader != upscaledShader_) {
				material.shader = upscaledShader_;
			}
			return true;
		}

		private static void RestoreAll() {
			Upscaling = false;
			NativeEngine.SunSamples = 1;
			RayReconstruction = false;
			SetDynamicResolution(-1.0f);
			SwapTaa(false);
			if (overridden_ != null) {
				overridden_.antialiasing = original_;
				overridden_.allowDynamicResolution = originalDynamicRes_;
				overridden_.taaAntiFlicker = originalAntiFlicker_;
			}
			overridden_ = null;
		}

		// DLSS that cannot run (no RTX GPU, no nvngx_dlss.dll, an old driver) leaves TAA on for good
		private static void CheckFailure() {
			if (failed_ || !Active || Time.frameCount < checkFrame_) {
				return;
			}
			checkFrame_ = Time.frameCount + 30;
			string status = NativeEngine.DlssStatus;
			if (status.Contains("could not") || status.Contains("not available") || status.Contains("needs") || status.Contains("failed")) {
				failed_ = true;
				failure_ = status;
				Kino.Log.Warning($"[NatureFX] DLSS off, HDRP's TAA kept: {status}");
			}
		}

		// PostPass: the HDR colour (input, the render resolution) anti-aliased by DLSS into output (input itself for
		// DLAA, the display resolution when upscaling). False: nothing done this frame.
		public static bool Run(CustomPassContext ctx, RTHandle input, RTHandle output, int width, int height, int outWidth, int outHeight) {
			var cam = ctx.hdCamera.camera;
			if (!Active || cam != camera_ || matricesFrame_ != Time.frameCount || ranFrame_ == Time.frameCount) {
				return false;
			}
			if (!Upscaling && cam != jittered_ || Upscaling && DevHdrpTaau) {
				return false;
			}
			ranFrame_ = Time.frameCount;
			var position = cam.transform.position;
			// a camera cut (a replay camera, a teleport): the history thrown away
			bool reset = NativeEngine.NrdReset || (position - lastPosition_).sqrMagnitude > 25.0f * 25.0f;
			lastPosition_ = position;
			// HDRP's own jitter when it upscales, in render pixels as ours
			var offset = Upscaling ? new Vector2(ctx.hdCamera.taaJitter.x, ctx.hdCamera.taaJitter.y) : jitter_;
			// DLSS's sign: HDRP hands it the negated projection offset too
			var jitter = DevJitterSign switch {
				1 => offset,
				2 => new Vector2(-offset.x, offset.y),
				3 => new Vector2(offset.x, -offset.y),
				_ => -offset,
			};
			// camera relative world (HDRP's) to clip without the jitter, now and a frame ago
			var view = view_;
			view.m03 = view.m13 = view.m23 = 0.0f;
			var current = GL.GetGPUProjectionMatrix(projection_, true) * view;
			var previous = GL.GetGPUProjectionMatrix(projectionPrev_, true) * viewPrev_ * Matrix4x4.Translate(position_);
			int quality = Upscaling ? NgxQualities[Mathf.Clamp(Quality, 0, NgxQualities.Length - 1)] : NgxQualities[0];
			// Ray Reconstruction with this frame's guides only (else plain DLSS on the denoised picture this frame)
			bool rr = RayReconstruction && RTShadowPass.GuidesFrame == Time.frameCount;
			return NativeEngine.Dlss(ctx.cmd, NativeEngine.Bridge, ctx, input, output, width, height, outWidth, outHeight, jitter, reset, DevMotion,
				current, previous, quality, rr ? RTShadowPass.GuideDiffuse : null, rr ? RTShadowPass.GuideSpecular : null, view_, projection_);
		}
	}
}

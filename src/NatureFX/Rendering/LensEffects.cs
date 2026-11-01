using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Draws NFX_NightVision.shader over the HDR colour before post-processing (copied first, as it reads and writes it)
	public class NightVisionPass : CustomPass {
		private static readonly int SourceId = Shader.PropertyToID("_NFX_NightSource");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_Night");

		public Material Material;
		public Vector4 Params;
		private RTHandle copy_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			copy_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "NFX_NightVision_Source");
		}

		protected override void Execute(CustomPassContext ctx) {
			if (Material == null || ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}
			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, copy_);
			ctx.cmd.SetGlobalTexture(SourceId, copy_);
			Material.SetVector(ParamsId, Params);
			CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(ctx.cmd, Material, null, 0);
		}

		protected override void Cleanup() {
			copy_?.Release();
		}
	}

	// The camera and the eye:
	//   night vision: the Purkinje shift and the grain of the eye in the dark (NightVisionPass);
	//   lens flare: HDRP's screen space lens flare, ghosts and streaks from headlights, street lamps and the sun, stronger
	//   at night when the eye is wide open;
	//   depth of field: focused on the car nearest the camera, the background softening into bokeh (for replays and
	//   photos; off by default).
	public class LensEffects {
		public const string NightShader = "Hidden/NatureFX/NightVision";
		private const float Priority = 9003.0f;

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;
		private ScreenSpaceLensFlare flare_;
		private DepthOfField dof_;
		private NightVisionPass night_;
		private Material nightMaterial_;
		private float focus_ = 10.0f;
		// the distance the lens is focused at (m): the car nearest the camera
		public static float FocusDistance { get; private set; } = 10.0f;

		// focus on the car nearest the camera (smoothly, like a focus puller)
		private void UpdateFocus(Vector3 camera) {
			float target = 10.0f;
			float best = float.MaxValue;
			foreach (var b in TrackMap.Bodies) {
				if (b != null) {
					float d = Vector3.Distance(b.position, camera);
					if (d < best) {
						best = d;
						target = d;
					}
				}
			}
			focus_ = Mathf.Lerp(focus_, target, 1.0f - Mathf.Exp(-Time.unscaledDeltaTime * 3.0f));
			FocusDistance = focus_;
		}
		// the lens flare's strength now (night, rain), for the own post-processing's flare (PostProcess)
		public static float FlareGhosts { get; private set; }
		public static float FlareStreaks { get; private set; }

		public void Create(ShaderBundle bundle) {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.Lens");
			Object.DontDestroyOnLoad(root_);
			profile_ = ScriptableObject.CreateInstance<VolumeProfile>();
			profile_.name = "NatureFX.Lens";
			flare_ = profile_.Add<ScreenSpaceLensFlare>();
			dof_ = profile_.Add<DepthOfField>();
			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.sharedProfile = profile_;
			volume_.enabled = false;

			var shader = bundle.Get(NightShader);
			if (shader == null) {
				Kino.Log.Warning($"[NatureFX] {NightShader} not in the shader bundle; night vision disabled");
				return;
			}
			nightMaterial_ = new Material(shader) { hideFlags = HideFlags.DontSave };
			var passVolume = root_.AddComponent<CustomPassVolume>();
			passVolume.isGlobal = true;
			passVolume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
			night_ = new NightVisionPass { name = "NatureFX Night Vision", Material = nightMaterial_, enabled = false };
			passVolume.customPasses.Add(night_);
		}

		// a reloaded shader bundle: the new night vision shader
		public void Reload(ShaderBundle bundle) {
			var shader = bundle.Get(NightShader);
			if (shader != null && nightMaterial_ != null) {
				nightMaterial_.shader = shader;
			}
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (profile_ != null) {
				Object.Destroy(profile_);
			}
			if (nightMaterial_ != null) {
				Object.Destroy(nightMaterial_);
			}
			root_ = null;
		}

		public void Update(bool on, ISettings s, Light sun, Vector3 camera) {
			if (root_ == null) {
				return;
			}
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			float night = Mathf.SmoothStep(0.0f, 1.0f, (2.0f - elevation) / 10.0f);

			if (night_ != null) {
				night_.enabled = on && s.NightVision > 0.0f;
				night_.Params = new Vector4(s.NightVision, Time.unscaledTime, s.NightVision * 0.5f, 0.0f);
			}

			bool flare = on && s.LensFlare > 0.0f;
			// a wide open eye at night sees the flares much more; rain on the lens spreads them
			float strength = s.LensFlare * (0.4f + 0.6f * night) * (1.0f + Weather.Current.Rain * 0.5f);
			FlareGhosts = flare ? strength : 0.0f;
			FlareStreaks = flare ? s.LensStreaks * (0.5f + night * 0.5f) : 0.0f;
			// with the own post-processing the flare is its own (on the HDR picture, before the tone curve)
			if (PostProcess.Active) {
				flare = false;
			}
			// with the own post-processing the depth of field is its own (PostPass, focused on FocusDistance)
			bool dof = on && s.DepthOfField && !PostProcess.Active;
			if (on && s.DepthOfField && PostProcess.Active) {
				UpdateFocus(camera);
			}
			volume_.enabled = flare || dof;
			if (!volume_.enabled) {
				return;
			}

			flare_.active = flare;
			if (flare) {
				// a wide open eye at night sees the flares much more; rain on the lens spreads them
				// HDRP's screen space flare at 1 is a strong effect: the slider's 1 is a fifth of it
				float k = s.LensFlare * 0.2f * (0.4f + 0.6f * night) * (1.0f + Weather.Current.Rain * 0.5f);
				flare_.intensity.Override(k);
				flare_.firstFlareIntensity.Override(0.5f);
				flare_.secondaryFlareIntensity.Override(0.3f);
				flare_.warpedFlareIntensity.Override(0.2f);
				// streaks only from really bright points (lamps, headlights, the sun), their own slider
				flare_.streaksIntensity.Override(s.LensStreaks * (0.5f + night * 0.5f));
				flare_.streaksLength.Override(0.35f);
				flare_.streaksThreshold.Override(0.7f);
				flare_.chromaticAbberationIntensity.Override(0.3f);
			}

			dof_.active = dof;
			if (dof) {
				UpdateFocus(camera);
				dof_.focusMode.Override(DepthOfFieldMode.Manual);
				dof_.nearFocusStart.Override(0.0f);
				dof_.nearFocusEnd.Override(focus_ * 0.45f);
				dof_.farFocusStart.Override(focus_ + 4.0f);
				dof_.farFocusEnd.Override(focus_ + 40.0f + focus_ * 2.0f);
			}
		}
	}
}

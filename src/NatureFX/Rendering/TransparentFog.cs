using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.RendererUtils;

namespace NatureFX.Rendering {
	// The own engine's 3D fog on what HDRP draws see-through (NFX_TransparentFog.shader). The composite fogs the opaque
	// picture; HDRP's fog is off while the own engine draws it, so HDRP's transparent materials came out without any
	// fog (a fence of planks dark and sharp in front of a fogged forest). Before HDRP's transparent objects the picture
	// is kept (TransparentFogCapture); after them (TransparentFogPass, before post-processing) the transparent renderers
	// are drawn again with an override shader that keeps their material's values: the nearest one's depth and their
	// coverage. What they added over the kept picture then gets the fog in front of them.
	public static class TransparentFog {
		public const string ShaderName = "Hidden/NatureFX/TransparentFog";
		// Developer page: off to compare
		public static bool Enabled = true;
		public static string Status { get; internal set; } = "not run yet";

		internal static RTHandle Back, Cover;
		internal static int BackFrame = -1;

		internal static void Ensure() {
			Back ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "NFX_TransparentFog_Back");
			Cover ??= RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "NFX_TransparentFog_Cover");
		}

		internal static void Release() {
			Back?.Release();
			Cover?.Release();
			Back = Cover = null;
			BackFrame = -1;
		}

		// The second drawing of the see-through objects (their coverage) and the picture kept before them only where the
		// fog shows on them: not in clear air (under 2 % of fog over a kilometre, rain included, and no local fog box
		// near), which is most of the time by day; there the objects keep HDRP's picture, as the fog would not change it
		internal static bool Thin => NatureFX.Map.MapFog.NativeParams.x * 1000.0f < 0.02f && AtmospherePass.BoxCount == 0;

		internal static bool Wanted(CustomPassContext ctx) {
			return Enabled && AtmospherePass.FogFrame == Time.frameCount && !Thin && RTSurfaceTrace.IsScreenCamera(ctx.hdCamera);
		}
	}

	// before HDRP's transparent objects: the composite's fogged picture
	public class TransparentFogCapture : CustomPass {
		protected override void Execute(CustomPassContext ctx) {
			if (!TransparentFog.Wanted(ctx)) {
				return;
			}
			TransparentFog.Ensure();
			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, TransparentFog.Back);
			TransparentFog.BackFrame = Time.frameCount;
		}

		protected override void Cleanup() {
			TransparentFog.Release();
		}
	}

	// after them: their coverage, then the fog on what they added
	public class TransparentFogPass : CustomPass {
		private const int PassApply = 1;
		private static readonly ShaderTagId[] Passes = { new("Forward"), new("ForwardOnly"), new("SRPDefaultUnlit") };
		private static readonly int ColorId = Shader.PropertyToID("_NFX_TFColor");
		private static readonly int BackId = Shader.PropertyToID("_NFX_TFBack");
		private static readonly int CoverId = Shader.PropertyToID("_NFX_TFCover");

		public Material Material;
		private RTHandle color_;
		private readonly MaterialPropertyBlock props_ = new();

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = SharedColor.Acquire();
		}

		protected override void Execute(CustomPassContext ctx) {
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: transparent fog");
			if (Material == null) {
				TransparentFog.Status = "needs the new shader bundle (NFX_TransparentFog)";
				return;
			}
			if (!TransparentFog.Wanted(ctx)) {
				if (!TransparentFog.Enabled) {
					TransparentFog.Status = "off";
				} else if (TransparentFog.Thin) {
					TransparentFog.Status = "not drawn: the air is too clear to show on them (under 2 % of fog over 1 km)";
				} else if (AtmospherePass.FogFrame != Time.frameCount) {
					TransparentFog.Status = "waiting for the own 3D fog";
				}
				return;
			}
			if (TransparentFog.BackFrame != Time.frameCount || TransparentFog.Cover == null) {
				TransparentFog.Status = "no picture from before the transparent objects";
				return;
			}
			var cmd = ctx.cmd;
			CoreUtils.SetRenderTarget(cmd, TransparentFog.Cover, ctx.cameraDepthBuffer, ClearFlag.Color, Color.clear);
			var desc = new RendererListDesc(Passes, ctx.cameraCullingResults, ctx.hdCamera.camera) {
				renderQueueRange = RenderQueueRange.transparent,
				sortingCriteria = SortingCriteria.CommonTransparent,
				overrideShader = Material.shader,
				overrideShaderPassIndex = 0,
			};
			cmd.DrawRendererList(ctx.renderContext.CreateRendererList(desc));

			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			props_.Clear();
			props_.SetTexture(ColorId, color_);
			props_.SetTexture(BackId, TransparentFog.Back);
			props_.SetTexture(CoverId, TransparentFog.Cover);
			CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, PassApply);
			TransparentFog.Status = "on (HDRP's see-through objects get the own 3D fog)";
		}

		protected override void Cleanup() {
			SharedColor.Release(color_);
			color_ = null;
		}
	}
}

using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Draws the car renderers a second time with NFX_CarRain.shader on top of their own materials.
	public class CarRainPass : CustomPass {
		private static readonly int ColorTex = Shader.PropertyToID("_NFX_ColorTex");

		public Material Material;
		public readonly List<Renderer> Renderers = new();

		private RTHandle color_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = RTHandles.Alloc(Vector2.one, TextureXR.slices, dimension: TextureXR.dimension,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, useDynamicScale: true, name: "NFX_CarRain_Color");
		}

		protected override void Execute(CustomPassContext ctx) {
			if (Material == null || Renderers.Count == 0 || ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}

			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			ctx.cmd.SetGlobalTexture(ColorTex, color_);
			CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);

			foreach (var r in Renderers) {
				if (r == null || !r.enabled || !r.isVisible) {
					continue;
				}
				int submeshes = r.sharedMaterials.Length;
				for (int i = 0; i < submeshes; ++i) {
					ctx.cmd.DrawRenderer(r, Material, i, 0);
				}
			}
		}

		protected override void Cleanup() {
			color_?.Release();
		}
	}

	// Water beads, run-off and an oily sheen on cars when it rains. The car shaders themselves are untouched.
	public class CarRain {
		public const string ShaderName = "Hidden/NatureFX/CarRain";

		private static readonly int Params = Shader.PropertyToID("_NFX_CarRain");
		private const float ScanInterval = 2.0f;
		private const float MinCarMass = 400.0f;

		private GameObject root_;
		private CarRainPass pass_;
		private Material material_;
		private readonly Collider[] overlap_ = new Collider[256];
		private float nextScan_;

		public bool Available => material_ != null;

		public void Create(ShaderBundle bundle) {
			var shader = bundle.Get(ShaderName);
			if (shader == null) {
				Kino.Log.Warning($"[NatureFX] {ShaderName} not in the shader bundle; car rain disabled");
				return;
			}

			material_ = new Material(shader) { hideFlags = HideFlags.DontSave };
			root_ = new GameObject("NatureFX.CarRain");
			Object.DontDestroyOnLoad(root_);
			var volume = root_.AddComponent<CustomPassVolume>();
			volume.isGlobal = true;
			volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
			pass_ = new CarRainPass { name = "NatureFX Car Rain", Material = material_, enabled = false };
			volume.customPasses.Add(pass_);
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (material_ != null) {
				Object.Destroy(material_);
			}
			root_ = null;
			material_ = null;
		}

		public void Update(ISettings s, Light sun) {
			if (pass_ == null) {
				return;
			}

			var w = Weather.Current;
			float wet = s.Enabled && s.Accumulation && s.CarRain ? Mathf.Max(w.Rain, w.Wetness * 0.7f) : 0.0f;
			pass_.enabled = wet > 0.01f;
			if (!pass_.enabled) {
				return;
			}

			material_.SetVector(Params, new Vector4(s.CarRainDrops, s.OilFilm, wet, Time.time));
			if (sun != null) {
				Shader.SetGlobalVector(ShaderGlobals.SunDir, -sun.transform.forward);
			}

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan();
			}
		}

		// body, glass and trim of cars near the camera; wheels are left dry
		private void Scan() {
			pass_.Renderers.Clear();
			var cam = Camera.main;
			if (cam == null) {
				return;
			}

			var bodies = new HashSet<Rigidbody>();
			int hits = Physics.OverlapSphereNonAlloc(cam.transform.position, 120.0f, overlap_, ~0, QueryTriggerInteraction.Ignore);
			for (int i = 0; i < hits; ++i) {
				var rb = overlap_[i].attachedRigidbody;
				if (rb != null && rb.mass >= MinCarMass) {
					bodies.Add(rb);
				}
			}

			foreach (var rb in bodies) {
				foreach (var r in rb.GetComponentsInChildren<Renderer>()) {
					if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) {
						continue;
					}
					string path = Nature.MaterialReplacer.ObjectPath(r.transform).ToLowerInvariant();
					if (path.Contains("wheel") || path.Contains("tire") || path.Contains("tyre") || path.Contains("interior")) {
						continue;
					}
					pass_.Renderers.Add(r);
				}
			}
		}
	}
}

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
		public readonly List<(Renderer Renderer, int Submesh, Rigidbody Car)> Renderers = new();
		private static readonly int AirId = Shader.PropertyToID("_NFX_CarAir");

		private RTHandle color_;

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			color_ = SharedColor.Acquire();
		}

		protected override void Execute(CustomPassContext ctx) {
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: car rain"); // (the debug page)
			if (Material == null || Renderers.Count == 0 || ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}

			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			ctx.cmd.SetGlobalTexture(ColorTex, color_);
			CoreUtils.SetRenderTarget(ctx.cmd, ctx.cameraColorBuffer, ctx.cameraDepthBuffer, ClearFlag.None);

			Rigidbody last = null;
			foreach (var (r, submesh, car) in Renderers) {
				if (r == null || !r.enabled || !r.isVisible) {
					continue;
				}
				// the air each car drives through (its drops run with it)
				if (car != last) {
					last = car;
					var v = car != null ? car.velocity : Vector3.zero;
					ctx.cmd.SetGlobalVector(AirId, new Vector4(v.x, v.y, v.z, 0.0f));
				}
				ctx.cmd.DrawRenderer(r, Material, submesh, 0);
			}
		}

		protected override void Cleanup() {
			SharedColor.Release(color_);
			color_ = null;
		}
	}

	// Water beads, run-off and an oily sheen on cars when it rains. The car shaders themselves are untouched.
	public class CarRain {
		public const string ShaderName = "Hidden/NatureFX/CarRain";

		private static readonly int Params = Shader.PropertyToID("_NFX_CarRain");
		private static readonly int DirtId = Shader.PropertyToID("_NFX_CarDirt");
		private static readonly int LightId = Shader.PropertyToID("_NFX_CarLight");

		// Dirt on the car nearest the camera (0..1 each): mud from driving on wet roads, dust from dusty dry ones, snow
		// lying on top while it snows (blown off at speed). Rain washes mud and dust off slowly.
		public float Mud { get; private set; }
		public float Dust { get; private set; }
		public float Snow { get; private set; }
		private float groundY_;
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
			// after the own engine's glass (its drops sit on the outside of the pane), before the post-processing
			volume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
			volume.priority = -100.0f;
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
			pass_ = null; // Update keys on it: with the material gone it would throw every frame
		}

		public void Update(ISettings s, Light sun) {
			if (pass_ == null) {
				return;
			}

			var w = Weather.Current;
			float wet = s.Enabled && s.Accumulation && s.CarRain ? Mathf.Max(w.Rain, w.Wetness * 0.7f) : 0.0f;
			UpdateDirt(s, w);
			pass_.enabled = wet > 0.01f || Mud + Dust + Snow > 0.01f;
			if (!pass_.enabled) {
				return;
			}

			material_.SetVector(Params, new Vector4(s.CarRainDrops, s.OilFilm, wet, Time.time));
			material_.SetVector(DirtId, new Vector4(Mud, Dust, Snow, groundY_));
			// light for the dirt's own shading: the sun on a panel facing it, and the sky (an eighth of the sun by day)
			float sunLux = sun != null && sun.isActiveAndEnabled ? sun.intensity * Mathf.Max(0.0f, -sun.transform.forward.y + 0.1f) : 0.0f;
			float sunTotal = sun != null && sun.isActiveAndEnabled ? sun.intensity : 0.0f;
			material_.SetVector(LightId, new Vector4(sun != null ? sun.intensity * (1.0f - w.Cloud * 0.8f) : 0.0f, Mathf.Max(sunLux * 0.15f, sunTotal * 0.02f) + 0.5f, 0.0f, 0.0f));
			if (sun != null) {
				Shader.SetGlobalVector(ShaderGlobals.SunDir, -sun.transform.forward);
			}

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + ScanInterval;
				Scan();
			}
		}

		public void Wash() {
			Mud = Dust = Snow = 0.0f;
		}

		private void UpdateDirt(ISettings s, WeatherState w) {
			bool on = s.Enabled && s.CarDirt;
			float dt = Time.deltaTime;
			var cam = Camera.main;
			Rigidbody car = null;
			float best = float.MaxValue;
			foreach (var b in TrackMap.Bodies) {
				if (b != null && cam != null) {
					float d = (b.position - cam.transform.position).sqrMagnitude;
					if (d < best) {
						best = d;
						car = b;
					}
				}
			}
			if (!on || car == null) {
				Mud = Mathf.MoveTowards(Mud, 0.0f, dt * 0.2f);
				Dust = Mathf.MoveTowards(Dust, 0.0f, dt * 0.2f);
				Snow = Mathf.MoveTowards(Snow, 0.0f, dt * 0.2f);
				return;
			}
			float speed = car.velocity.magnitude;
			float driving = Mathf.Clamp01(speed / 25.0f);
			float ground = Mathf.Max(w.Wetness, w.Rain);
			// a few minutes of wet driving for full mud, of dusty driving for full dust
			Mud += dt * driving * ground * 0.006f * (1.0f + w.Snow);
			Dust += dt * driving * w.Dust * (1.0f - ground) * 0.006f;
			// rain washes the panels: dust quickly, mud slowly
			Dust -= dt * w.Rain * 0.02f;
			Mud -= dt * w.Rain * 0.003f;
			// snow settles while it lies around and the car is slow, blows off at speed
			Snow += dt * w.Snow * Mathf.Clamp01(1.0f - speed / 12.0f) * 0.01f;
			Snow -= dt * Mathf.Clamp01((speed - 12.0f) / 20.0f) * 0.05f + dt * (w.Snow < 0.05f ? 0.004f : 0.0f);
			Mud = Mathf.Clamp(Mud, 0.0f, 0.9f * s.CarDirtAmount);
			Dust = Mathf.Clamp(Dust, 0.0f, 0.8f * s.CarDirtAmount);
			Snow = Mathf.Clamp(Snow, 0.0f, 0.9f);
			// the ground under the car: its wheels' contacts
			int n = 0;
			float y = 0.0f;
			foreach (var c in TrackMap.Contacts) {
				if (c.Body == car) {
					y += c.Point.y;
					++n;
				}
			}
			if (n > 0) {
				groundY_ = y / n;
			}
		}

		// The headlights' beams, flares and glows: see-through meshes reaching metres in front of the car (a cone for the
		// beam). Drawn again with the run-off streaks they showed as white lines over the road ahead whenever the lights
		// were on. Glass stays (beads on the windows): it is see-through but blended normally and not named as a light.
		private static bool IsLightEffect(Material m) {
			if (m == null) {
				return true;
			}
			if (m.renderQueue < 2450) {
				return false;
			}
			bool additive = m.HasProperty("_BlendMode") && Mathf.RoundToInt(m.GetFloat("_BlendMode")) == 1;
			string name = (m.name + " " + (m.shader != null ? m.shader.name : "")).ToLowerInvariant();
			return additive || name.Contains("light") || name.Contains("beam") || name.Contains("flare") || name.Contains("glow")
			       || name.Contains("halo") || name.Contains("cone") || name.Contains("volum") || name.Contains("additive") || name.Contains("particle");
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
				// the car's own extent: its colliders, with room for spoilers and mirrors. A headlight beam or a light
				// cone reaches metres beyond it, whatever its material is called (by name and blending alone, the beam
				// still got the run-off streaks)
				var extent = new Bounds();
				bool hasExtent = false;
				foreach (var c in rb.GetComponentsInChildren<Collider>()) {
					if (c.isTrigger || !c.enabled) {
						continue;
					}
					if (hasExtent) {
						extent.Encapsulate(c.bounds);
					} else {
						extent = c.bounds;
						hasExtent = true;
					}
				}
				extent.Expand(1.2f);
				foreach (var r in rb.GetComponentsInChildren<Renderer>()) {
					if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) {
						continue;
					}
					string path = Nature.MaterialReplacer.ObjectPath(r.transform).ToLowerInvariant();
					if (path.Contains("wheel") || path.Contains("tire") || path.Contains("tyre") || path.Contains("interior")) {
						continue;
					}
					var b = r.bounds;
					if (hasExtent && !(extent.Contains(b.min) && extent.Contains(b.max))) {
						LogSkipped(r, path, $"reaches beyond the car ({b.size.x:0.0} x {b.size.y:0.0} x {b.size.z:0.0} m)");
						continue;
					}
					// the glass the own engine draws is hidden from HDRP (OwnGlass): its beads go by what was there
					var materials = OwnGlass.Originals(r) ?? r.sharedMaterials;
					for (int i = 0; i < materials.Length; ++i) {
						if (IsLightEffect(materials[i])) {
							LogSkipped(r, path, $"light effect material '{materials[i]?.name}' ({materials[i]?.shader?.name}, queue {materials[i]?.renderQueue})");
						} else {
							pass_.Renderers.Add((r, i, rb));
						}
					}
				}
			}
		}

		private static readonly HashSet<string> logged_ = new();

		private static void LogSkipped(Renderer r, string path, string why) {
			if (logged_.Count < 64 && logged_.Add(path + why)) {
				Kino.Log.Info($"[NatureFX] Car rain leaves out {path}: {why}");
			}
		}
	}
}

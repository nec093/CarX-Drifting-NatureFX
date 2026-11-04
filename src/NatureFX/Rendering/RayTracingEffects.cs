using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using NatureFX.Ui;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The ray traced effects: owns the global CustomPassVolume that runs the shadow / ambient light pass
	// (RTShadowPass) and the reflections (RTReflectionPass) after opaque geometry and sky. The rays are traced by
	// NatureFX's own engine (NativeEngine: DXR 1.0 pipelines or DXR 1.1 inline) against the scene RayTracingScene
	// collects.
	public class RayTracingEffects {
		public const string ShadowShaderName = "Hidden/NatureFX/RTShadow";
		public const string SunExactShaderName = "Hidden/NatureFX/SunExact";
		public const string ReflectionShaderName = "Hidden/NatureFX/RTReflect";

		private GameObject root_;
		private RayTracingScene dxrScene_;
		private RTReflectionPass reflection_;
		private RTGlassPass glass_;
		private RTShadowPass shadows_;
		private readonly RTSurfaceTrace surfaces_ = new();  // camera rays shared by reflections and shadows
		private readonly LocalLights lights_ = new();       // headlights and lamps near the camera
		private bool lampRecordHooked_;
		private AtmospherePass atmosphereTrace_, atmosphereComposite_;
		private Material transparentFogMaterial_;
		private Light shadowlessSun_;                        // sun whose HDRP shadows the ray traced ones replace
		private LightShadows sunShadowsBefore_;
		private LightBakingOutput sunBakingBefore_;          // its baked shadow mask is switched off as well
		// other directional lights that cast shadow maps (a map's fill or second sun): their shadows of the animated
		// foliage came on top of the traced ones (square patches moving with the wind); off as well, then restored
		private readonly Dictionary<Light, LightShadows> otherShadowsBefore_ = new();
		private readonly Dictionary<Light, LightShadows> lampShadowsBefore_ = new();
		private float nextLampScan_;

		// lamps and headlights: their HDRP shadow maps off, the soft ray traced shadows instead (Settings.RTLampShadows)
		public bool LampShadows;
		private float nextLightScan_;
		private Vector4 sunDirection_;
		private Vector4 sunIlluminance_;
		public readonly RTBenchmark Benchmark;

		public RayTracingEffects() {
			Benchmark = new RTBenchmark(this);
		}
		private RTStage benchmarkOff_;
		private ISettings applied_;
		public bool ShadowDebugView;                // F8: show only the traced shadows (not saved)
		public bool GIDebugView;                    // F8: show only the ambient light / GI (not saved)
		public bool ExactSunDebugView;              // F8: show the exact sun light the shadows take away (not saved)
		public bool AlbedoDebugView;                // debug page: the albedo check (not saved)

		public bool RayTracingSupported => dxrScene_ != null;
		public int RayTracingInstances => dxrScene_?.InstanceCount ?? 0;

		public void Create(ShaderBundle bundle) {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.RayTracing");
			Object.DontDestroyOnLoad(root_);
			var volume = root_.AddComponent<CustomPassVolume>();
			volume.isGlobal = true;
			volume.injectionPoint = CustomPassInjectionPoint.AfterOpaqueAndSky;
			NativeEngine.TryStart();

			// glass is drawn after the opaque passes: its reflections go on before post processing
			var glassVolume = root_.AddComponent<CustomPassVolume>();
			glassVolume.isGlobal = true;
			glassVolume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;

			// before HDRP draws anything: the own sky's probe as HDRP's (OwnLighting: the renderers HDRP still lights)
			var earlyVolume = root_.AddComponent<CustomPassVolume>();
			earlyVolume.isGlobal = true;
			earlyVolume.injectionPoint = CustomPassInjectionPoint.BeforeRendering;
			earlyVolume.customPasses.Add(new AmbientFeedPass { name = "NatureFX sky probe for HDRP" });

			if (TryCreateDxr(bundle, volume, glassVolume)) {
				Kino.Log.Info($"[NatureFX] DX12 ray tracing through the own engine ({NativeEngine.Info})");
			} else {
				Kino.Log.Info($"[NatureFX] Ray tracing unavailable (API {SystemInfo.graphicsDeviceType})");
			}
		}

		// The passes on DX12: the own engine (NFXNative, its own D3D12 ray tracing) traces for all of them; Unity's ray
		// tracing is not used. The proxy shader only carries the material values the engine reads.
		private bool TryCreateDxr(ShaderBundle bundle, CustomPassVolume volume, CustomPassVolume glassVolume) {
			if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) {
				return false;
			}
			var proxy = bundle.Get(RayTracingScene.ProxyShaderName);
			if (proxy == null) {
				Kino.Log.Warning($"[NatureFX] Ray tracing: {RayTracingScene.ProxyShaderName} missing from the bundle");
				return false;
			}
			dxrScene_ = new RayTracingScene(proxy);
			NativeEngine.Bridge = bundle.GetCompute(NativeEngine.BridgeShaderName);
			// Developer > Reload shaders: the passes' materials are switched by the bundle, the compute shader is held here
			bundle.Reloaded += () => {
				NativeEngine.Bridge = bundle.GetCompute(NativeEngine.BridgeShaderName);
				if (shadows_ != null) {
					shadows_.Bridge = NativeEngine.Bridge;
				}
			};

			var composite = bundle.Get(ReflectionShaderName);
			if (composite == null) {
				Kino.Log.Warning($"[NatureFX] {ReflectionShaderName} not in the shader bundle; ray traced reflections disabled");
			} else {
				reflection_ = new RTReflectionPass {
					name = "NatureFX RT reflections",
					CompositeMaterial = new Material(composite) { hideFlags = HideFlags.DontSave },
					Scene = dxrScene_,
					Surfaces = surfaces_,
					Lights = lights_,
					enabled = false
				};
				volume.customPasses.Add(reflection_);
				glass_ = new RTGlassPass {
					name = "NatureFX RT glass reflections",
					CompositeMaterial = reflection_.CompositeMaterial,
					Scene = dxrScene_,
					enabled = false
				};
				glassVolume.customPasses.Add(glass_);
			}

			// the atmosphere: traced before everything (the shadows read its cloud shadows and sky light), drawn after
			var atmosphereShader = bundle.Get(AtmospherePass.ShaderName);
			if (atmosphereShader == null) {
				Kino.Log.Warning($"[NatureFX] {AtmospherePass.ShaderName} not in the shader bundle; the own engine's sky is off");
			} else {
				atmosphereTrace_ = new AtmospherePass { name = "NatureFX atmosphere (trace)", Lights = lights_, enabled = false };
				atmosphereComposite_ = new AtmospherePass {
					name = "NatureFX atmosphere (composite)", IsComposite = true, Lights = lights_,
					Material = new Material(atmosphereShader) { hideFlags = HideFlags.DontSave }, enabled = false
				};
				volume.customPasses.Add(atmosphereComposite_);
			}

			// the 3D fog on HDRP's see-through objects: the picture kept before them, the fog put on after them (ahead of
			// the glass reflections, which fog their own mirrored light)
			if (atmosphereComposite_ != null && bundle.Get(TransparentFog.ShaderName) is Shader transparentFog) {
				transparentFogMaterial_ = new Material(transparentFog) { hideFlags = HideFlags.DontSave };
				var captureVolume = root_.AddComponent<CustomPassVolume>();
				captureVolume.isGlobal = true;
				captureVolume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
				captureVolume.customPasses.Add(new TransparentFogCapture { name = "NatureFX transparent fog (keep)" });
				glassVolume.customPasses.Insert(0, new TransparentFogPass { name = "NatureFX transparent fog", Material = transparentFogMaterial_ });
			} else if (atmosphereComposite_ != null) {
				TransparentFog.Status = "needs the new shader bundle (NFX_TransparentFog)";
			}

			// shadows go first, so the reflections see the shadowed image
			var shadowShader = bundle.Get(ShadowShaderName);
			var sunExactShader = bundle.Get(SunExactShaderName);
			if (sunExactShader == null) {
				Kino.Log.Warning($"[NatureFX] {SunExactShaderName} not in the shader bundle; shadows use an estimate of the sun light");
			}
			if (shadowShader == null) {
				Kino.Log.Warning($"[NatureFX] {ShadowShaderName} not in the shader bundle; ray traced shadows disabled");
			} else {
				shadows_ = new RTShadowPass {
					name = "NatureFX RT shadows",
					Material = new Material(shadowShader) { hideFlags = HideFlags.DontSave },
					SunExactMaterial = sunExactShader != null ? new Material(sunExactShader) { hideFlags = HideFlags.DontSave } : null,
					Bridge = bundle.GetCompute(NativeEngine.BridgeShaderName),
					Scene = dxrScene_,
					Surfaces = surfaces_,
					Lights = lights_,
					enabled = false
				};
				volume.customPasses.Insert(0, shadows_);
			}
			if (atmosphereTrace_ != null) {
				volume.customPasses.Insert(0, atmosphereTrace_);
			}
			return true;
		}

		// the ray traced sun shadows run now (HDRP's are off for them)
		public bool SunShadowsRunning => shadowlessSun_ != null;

		public string ShadowStatus => shadows_ == null ? "unavailable (needs DX12 ray tracing)" : !shadows_.enabled ? "off" : shadows_.Status;
		public string ReflectionStatus => reflection_ == null ? "unavailable (needs DX12 ray tracing)" : !reflection_.enabled ? "off" : reflection_.Status;
		public string SkyStatus => atmosphereTrace_ == null ? "unavailable (needs DX12 and the own engine)" : AtmospherePass.SkyStatus;
		public string FogStatus => atmosphereTrace_ == null ? "unavailable (needs DX12 and the own engine)" : AtmospherePass.FogStatus;
		public string GlassStatus => glass_ == null ? "unavailable (needs DX12 ray tracing)" : !glass_.enabled ? "off" : glass_.Status;

		// Per frame: move car instances, pass the sun to the hit lighting.
		public void UpdateRayTracing(Light sun) {
			NativeEngine.Update();
			bool reflections = reflection_ != null && reflection_.enabled;
			bool shadows = shadows_ != null && shadows_.enabled;
			// keep HDRP's shadows when the ray traced ones cannot run (no DXR resources), or nothing would shadow
			bool shadowsWork = shadows && shadows_.TraceShadows && !shadows_.Status.StartsWith("unavailable");
			UpdateSunShadows(shadowsWork ? sun : null);
			UpdateLampShadows(shadows && LampShadows && lights_.Enabled && !shadows_.Status.StartsWith("unavailable"));
			bool atmosphere = atmosphereTrace_ != null && atmosphereTrace_.enabled && atmosphereTrace_.NativeSky;
			if (dxrScene_ == null || (!reflections && !shadows && !atmosphere)) {
				return;
			}
			Benchmark.Update();
			if (!lampRecordHooked_) {
				lampRecordHooked_ = true;
				lights_.ShadowTaken = l => lampShadowsBefore_.ContainsKey(l);
			}
			lights_.Update(Camera.main);
			RTTimings.BeginSceneUpdate();
			dxrScene_.Update();
			// NRD and DLSS both follow the cars' pixels by their motion vectors
			dxrScene_.SetCarMotionVectors((NativeEngine.NrdEnabled || Dlaa.Active) && NativeEngine.DevCarMotionVectors);
			RTTimings.EndSceneUpdate();

			// the own engine's light: the sun, or after dusk the moon (SunController.SkyLight): the traced shadows,
			// the sky, the clouds and the fog all follow it, so the moon casts traced shadows and lights the night sky
			var engineLight = SunController.SkyLight != null && SunController.SkyLight.isActiveAndEnabled ? SunController.SkyLight : sun;
			if (engineLight != null && engineLight.isActiveAndEnabled) {
				var toSun = -engineLight.transform.forward;
				var color = engineLight.color.linear * engineLight.intensity;
				if (engineLight.useColorTemperature) {
					color *= Mathf.CorrelatedColorTemperatureToRGB(engineLight.colorTemperature);
				}
				// up while any of its disc is (SunController dims it to nothing by -0.5°): up only above 0°, the engine
				// stopped lighting with it while HDRP's sun, which the composite takes away, still shone (a dark minute at 18:00)
				sunDirection_ = new Vector4(toSun.x, toSun.y, toSun.z, toSun.y > SunController.DiscGoneY ? 1.0f : 0.0f);
				sunIlluminance_ = new Vector4(color.r, color.g, color.b, 0.0f);
			} else {
				sunDirection_ = Vector4.zero;
			}
			if (reflection_ != null) {
				reflection_.SunDirection = sunDirection_;
				reflection_.SunIlluminance = sunIlluminance_;
			}
			if (shadows_ != null) {
				shadows_.SunDirection = sunDirection_;
				shadows_.SunIlluminance = sunIlluminance_;
			}
		}

		// HDRP's own sun shadows are off while the ray traced ones run (they are applied on top of the unshadowed
		// sun), and come back as they were when the ray traced shadows stop or the sun changes.
		private void UpdateSunShadows(Light shadowlessSun) {
			UpdateOtherShadows(shadowlessSun);
			if (shadowlessSun_ == shadowlessSun) {
				if (shadowlessSun != null && shadowlessSun.shadows != LightShadows.None) {
					sunShadowsBefore_ = shadowlessSun.shadows; // the game switched them back on
					shadowlessSun.shadows = LightShadows.None;
				}
				return;
			}
			if (shadowlessSun_ != null) {
				shadowlessSun_.shadows = sunShadowsBefore_;
				shadowlessSun_.bakingOutput = sunBakingBefore_;
			}
			shadowlessSun_ = shadowlessSun;
			if (shadowlessSun != null) {
				sunShadowsBefore_ = shadowlessSun.shadows;
				shadowlessSun.shadows = LightShadows.None;
				// Maps bake the sun's shadows of static objects into a shadow mask, which HDRP applies even with
				// the realtime shadows off: with no mask channel the sun lights static objects unshadowed too.
				sunBakingBefore_ = shadowlessSun.bakingOutput;
				var baking = sunBakingBefore_;
				if (baking.lightmapBakeType == LightmapBakeType.Mixed && baking.mixedLightingMode == MixedLightingMode.Shadowmask) {
					baking.mixedLightingMode = MixedLightingMode.IndirectOnly;
					baking.occlusionMaskChannel = -1;
					shadowlessSun.bakingOutput = baking;
				}
				Kino.Log.Info($"[NatureFX] RT shadows: '{shadowlessSun.name}' sun shadows off (were {sunShadowsBefore_}), " +
				              $"baking {sunBakingBefore_.lightmapBakeType}/{sunBakingBefore_.mixedLightingMode}, mask channel {sunBakingBefore_.occlusionMaskChannel}");
			}
		}

		// Every other shadow casting light while the traced sun shadows run: directional ones lose their shadows (the
		// traced sun replaces any sun-like light); the others are only logged once, to see what else casts shadows.
		private void UpdateOtherShadows(Light sun) {
			if (sun == null) {
				foreach (var kv in otherShadowsBefore_) {
					if (kv.Key != null) {
						kv.Key.shadows = kv.Value;
					}
				}
				otherShadowsBefore_.Clear();
				return;
			}
			if (Time.unscaledTime < nextLightScan_) {
				return;
			}
			nextLightScan_ = Time.unscaledTime + 2.0f;
			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (light == sun || !light.isActiveAndEnabled || light.shadows == LightShadows.None) {
					continue;
				}
				if (light.type == LightType.Directional) {
					if (!otherShadowsBefore_.ContainsKey(light)) {
						otherShadowsBefore_[light] = light.shadows;
						Kino.Log.Info($"[NatureFX] RT shadows: '{light.name}' (another directional light, intensity {light.intensity:0.##}) shadows off (were {light.shadows})");
					}
					light.shadows = LightShadows.None;
				} else if (!LampShadows && loggedLights_.Add(light)) {
					Kino.Log.Info($"[NatureFX] RT shadows: '{light.name}' ({light.type}, range {light.range:0.#} m) casts shadow maps ({light.shadows}); left as it is");
				}
			}
		}
		private readonly HashSet<Light> loggedLights_ = new();

		// The measurements for the debug page (Tools/DebugPage.cs; once the on-screen overlay): the frame rate, GPU time
		// per stage, rays, memory, the scene, which RT features are on, the denoisers and the passes' state.
		public class DebugStats {
			public float Fps, FrameMs;
			public string Mode = "";
			public readonly List<(string Label, string Value, bool Part)> Timings = new();
			public string TimingNote;
			public ulong MemoryUsed, MemoryBudget;
			public readonly List<(string Label, string Value)> Scene = new();
			public readonly List<(string Name, bool On)> Features = new();
			public readonly List<(string Name, string State, bool Ok)> Denoisers = new();
			public readonly List<(string Name, string State)> Passes = new();
		}
		public DebugStats ReadStats() {
			var d = new DebugStats();
			d.Fps = 1.0f / Mathf.Max(Time.smoothDeltaTime, 1e-4f);
			d.FrameMs = Time.smoothDeltaTime * 1000.0f;
			d.Mode = NativeEngine.ModeName + (NativeEngine.Dxr12SunActive ? " + DXR 1.2 sun" : "");
			var readings = RTTimings.Readings(out float total);
			foreach (var (label, ms, part) in readings) {
				d.Timings.Add((label.Replace("NFX RT ", ""), $"{ms:0.00} ms", part));
			}
			if (d.Timings.Count > 0) {
				d.Timings.Add(("total GPU", $"{total:0.00} ms", false));
			} else {
				d.TimingNote = "GPU stage timings are not reported on this GPU / API";
			}
			d.Timings.Add(("car instances (CPU)", $"{RTTimings.SceneUpdateMs:0.00} ms", false));
			long rays = shadows_ != null && shadows_.enabled ? shadows_.TracedRays : 0;
			d.Timings.Add(("shadow / ambient rays", $"{rays / 1e6:0.00} M / frame", false));
			NativeEngine.GpuMemoryMB(out d.MemoryUsed, out d.MemoryBudget);

			// "678 geometries (0 new), 200 instances (3 alpha tested), 0.1 M triangles, BLAS 23.8 MB, ..." into rows
			foreach (string part in NativeEngine.SceneStatus.Split(new[] { ", " }, System.StringSplitOptions.RemoveEmptyEntries)) {
				d.Scene.Add(("", part));
			}
			d.Scene.Add(("lamps", $"{lights_.Count} ({lights_.ShadowCount} traced, {NativeEngine.Emitters.Count} glowing surfaces), GI lamp reference {lights_.LampReference:0.#} lx"));

			bool glassOn = glass_ != null && glass_.enabled;
			bool wet = reflection_ != null && reflection_.enabled && reflection_.WetStreaks;
			float wetness = Mathf.Max(NatureFX.Nature.Weather.Current.Wetness, NatureFX.Nature.Weather.Current.Rain);
			d.Features.Add(("light through glass", NativeEngine.GlassShadows));
			d.Features.Add(("glass reflections", glassOn));
			d.Features.Add((glassOn ? $"refraction {glass_.Refraction:0.00}" : "refraction", glassOn && glass_.Refraction > 0.0f));
			d.Features.Add(($"wet streaks {wetness:0.00}", wet && wetness > 0.0f));
			d.Features.Add(("glowing surfaces", lights_.Emissive));
			d.Features.Add(("lamp shadows", LampShadows));
			d.Features.Add((shadows_ != null && shadows_.QuarterResolution ? "quarter resolution" : "half resolution", true));

			if (NativeEngine.NrdEnabled) {
				// "sun: ...; ambient: ...; lamps: ...; reflections: ..."
				foreach (string part in NativeEngine.NrdStatus.Split(new[] { "; " }, System.StringSplitOptions.RemoveEmptyEntries)) {
					int colon = part.IndexOf(": ");
					string name = colon > 0 ? part.Substring(0, colon) : "NRD";
					string state = colon > 0 ? part.Substring(colon + 2) : part;
					bool ok = state.Contains("dispatches");
					// "SIGMA on the sun shadows, 1886x986, 16 dispatches" -> "SIGMA · 1886x986 · 16 passes"
					if (ok) {
						var bits = state.Split(new[] { ", " }, System.StringSplitOptions.None);
						string what = bits[0].Split(' ')[0];
						state = bits.Length >= 3 ? $"{what} · {bits[1]} · {bits[2].Replace(" dispatches", " passes")}" : state;
					}
					d.Denoisers.Add((name, state, ok));
				}
			} else {
				d.Denoisers.Add(("NatureFX", shadows_ != null && shadows_.VarianceDenoiser ? "variance guided" : "simple blur", true));
			}

			d.Passes.Add(("shadows", ShadowStatus));
			d.Passes.Add(("reflections", ReflectionStatus));
			d.Passes.Add(("glass", GlassStatus));
			return d;
		}

		// Point and spot lights lose their shadow maps while the traced lamp shadows run (LocalLights then traces them),
		// and get them back as they were when those stop. Also spares HDRP a shadow map per lamp.
		// lamps get ray traced shadows instead of shadow maps right now (MapLights then gives its lamps none)
		public bool LampShadowsTraced { get; private set; }

		private void UpdateLampShadows(bool takeOver) {
			LampShadowsTraced = takeOver;
			if (!takeOver) {
				if (lampShadowsBefore_.Count > 0) {
					foreach (var kv in lampShadowsBefore_) {
						if (kv.Key != null) {
							kv.Key.shadows = kv.Value;
						}
					}
					Kino.Log.Info($"[NatureFX] RT lamp shadows: shadow maps of {lampShadowsBefore_.Count} lamps back");
					lampShadowsBefore_.Clear();
				}
				return;
			}
			if (Time.unscaledTime < nextLampScan_) {
				return;
			}
			nextLampScan_ = Time.unscaledTime + 2.0f;
			int taken = 0;
			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if ((light.type != LightType.Point && light.type != LightType.Spot) || light.shadows == LightShadows.None) {
					continue;
				}
				if (!lampShadowsBefore_.ContainsKey(light)) {
					lampShadowsBefore_[light] = light.shadows;
					++taken;
				}
				light.shadows = LightShadows.None;
			}
			if (taken > 0) {
				Kino.Log.Info($"[NatureFX] RT lamp shadows: shadow maps of {taken} lamps off, traced instead ({lampShadowsBefore_.Count} in all)");
			}
		}

		// After each scene load: take the scene into the acceleration structure again
		// newMap: a single (not additive) scene was loaded: the own engine starts its scene over (NativeEngine.UploadScene
		// fresh); the game's small day / night scenes keep it
		public void PrepareSceneForRayTracing(bool newMap = false) {
			dxrScene_?.Rebuild(newMap);
		}

		public void Destroy() {
			if (reflection_?.CompositeMaterial != null) {
				Object.Destroy(reflection_.CompositeMaterial);
			}
			UpdateSunShadows(null);
			UpdateLampShadows(false);
			if (shadows_?.Material != null) {
				Object.Destroy(shadows_.Material);
			}
			if (transparentFogMaterial_ != null) {
				Object.Destroy(transparentFogMaterial_);
				transparentFogMaterial_ = null;
			}
			shadows_ = null;
			reflection_ = null;
			glass_ = null;
			surfaces_.Release();
			if (root_ != null) {
				Object.Destroy(root_);
			}
			dxrScene_?.Destroy();
			dxrScene_ = null;
			root_ = null;
		}

		// Materials changed shader: the ray tracing scene takes them again
		public void RequestSceneRebuild() {
			dxrScene_?.RequestRebuild();
		}

		// F8: drop the accumulated shadows and reflections (burnt in after a glitch)
		public void ResetHistory() {
			shadows_?.History.RequestReset();
			reflection_?.History.RequestReset();
		}

		// benchmark: switch stages off on top of the settings
		public void SetBenchmarkOff(RTStage off) {
			benchmarkOff_ = off;
			if (applied_ != null) {
				Apply(applied_);
			}
		}

		public void Apply(ISettings s) {
			applied_ = s;
			// one backend for every ray traced effect (Settings > Ray tracing)
			// the own engine traces everything: DXR 1.0 (ray tracing pipelines, backend 0) or DXR 1.1 (inline, 1)
			// the scene (BLAS, TLAS, their memory, the per frame wind upload) only while a ray traced effect runs; with
			// all of them off the engine stays idle (RayTracingScene.PushNative uploads it again when one comes on)
			// the sky, clouds and fog run on the own engine too, whatever the ray traced effects are set to
			NativeEngine.Enabled = s.Enabled;
			if (atmosphereTrace_ != null) {
				bool atmosphere = s.Enabled;
				if (atmosphereTrace_.enabled && !atmosphere) {
					atmosphereTrace_.Disable(); // a pass that no longer runs cannot clear what it shared
				}
				atmosphereTrace_.enabled = atmosphereComposite_.enabled = atmosphere;
				atmosphereTrace_.NativeSky = s.Enabled;
				atmosphereTrace_.NativeFog = true;
				atmosphereTrace_.Clouds = s.Clouds;
				atmosphereTrace_.CloudShadows = s.CloudShadows > 0.0f;
				atmosphereTrace_.Anisotropy = s.RTShaftAnisotropy;
				atmosphereComposite_.StarBrightness = s.StarBrightness;
			}
			NativeEngine.Dxr12Sun = NativeEngine.Enabled && s.RTDxr12Sun;
			NativeEngine.Wind = s.RTWind;
			NativeEngine.Dxr12SunPaused = (benchmarkOff_ & RTStage.Dxr12Sun) != 0;
			NativeEngine.OmmPaused = (benchmarkOff_ & RTStage.Omm) != 0;
			if (reflection_ != null) {
				reflection_.enabled = s.Enabled;
				reflection_.Intensity = s.RTReflectionIntensity;
				reflection_.RoughnessCutoff = Mathf.Clamp(s.RTReflectionRoughness, 0.05f, 1.0f);
				reflection_.MaxDistance = Mathf.Max(5.0f, s.RTReflectionDistance);
				// 0 performance: half resolution, 1 balanced: + second bounce on metal, 2 quality: full resolution too
				reflection_.FullResolution = s.RTReflectionQuality >= 2 || PhotoMode.Active;
				reflection_.SecondBounce = s.RTReflectionQuality >= 1 || PhotoMode.Active;
				reflection_.WetStreaks = s.RTWetReflections;
			}
			if (glass_ != null) {
				glass_.enabled = s.Enabled;
				glass_.Refraction = Mathf.Clamp01(s.RTGlassRefraction);
				glass_.Intensity = s.RTReflectionIntensity;
				glass_.MaxDistance = Mathf.Max(5.0f, s.RTReflectionDistance);
			}
			// roadmap E: the own engine draws the glass whole while its pass runs (HDRP's glass hidden meanwhile)
			OwnGlass.Update(glass_ != null && glass_.enabled && NativeEngine.Supports("own-glass") && Time.frameCount - glass_.LastRun < 30);
			NativeEngine.OwnGlass = OwnGlass.Active;
			if (shadows_ != null) {
				shadows_.enabled = s.Enabled;
				shadows_.NativeSky = false; // AtmospherePass
				if (!shadows_.enabled) {
					CloudController.NativeShadowsActive = false; // the pass that sets it no longer runs
					Shader.SetGlobalVector("_NFX_FogVolumeParams", Vector4.zero); // nor the 3D fog the particles read
				}
				shadows_.SkySunLux = SunController.TopOfAtmosphereLux;
				// haze: more aerosols as it clouds over and rains (the air near the ground turns milky)
				shadows_.SkyAerosols = 1.0f + 2.0f * Nature.Weather.Current.Cloud + 2.0f * Nature.Weather.Current.Rain;
				shadows_.TraceShadows = true;
				shadows_.TraceAO = true;
				shadows_.AOStrength = s.RTAOStrength;
				shadows_.AODistance = s.RTAODistance;
				shadows_.SkyOcclusionDistance = s.RTSkyOcclusion;
				shadows_.ContactShadows = s.RTContactShadows;
				shadows_.Bounce = true;
				shadows_.MultiBounceGI = true;
				shadows_.GIDistance = Mathf.Max(1.0f, s.RTGIDistance);
				shadows_.GIIntensity = Mathf.Max(0.0f, s.RTGIIntensity);
				shadows_.Strength = s.RTShadowStrength;
				shadows_.FullResolutionShadows = s.RTShadowFullRes || PhotoMode.Active;
				shadows_.ExactSun = s.RTExactSun;
				shadows_.LampMasks = s.RTLampMasks;
				shadows_.FullRelight = s.RTFullRelight;
				shadows_.TransparentShadows = s.RTTransparentShadows;
				shadows_.GroundDetail = s.MapGroundDetail;
				shadows_.GroundBlend = NatureFX.Map.MapDefaults.Value(NatureFX.Map.MapKey.GroundBlend, 0.35f);
				shadows_.LeafTranslucency = s.RTLeafTranslucency;
				shadows_.Shafts = s.RTShafts;
				lights_.Enabled = true;
				LampShadows = true;
				lights_.Emissive = s.RTEmissiveLights;
				NativeEngine.GlassShadows = s.RTGlassShadows;
				// photo mode: half resolution traces at least (the denoisers stay: PhotoMode averages their frames)
				shadows_.QuarterResolution = s.RTTraceScale == 1 && !PhotoMode.Active;
				shadows_.VarianceDenoiser = s.RTDenoiser >= 1;
				NativeEngine.NrdEnabled = s.RTDenoiser == 2 && s.Enabled;
				// DLSS Ray Reconstruction takes the sun shadows raw (Dlaa); the reflections stay REBLUR's
				NativeEngine.NrdSun = NativeEngine.NrdEnabled && NativeEngine.DevNrdSun && !Dlaa.RayReconstruction;
				NativeEngine.NrdReflections = NativeEngine.NrdEnabled && NativeEngine.DevNrdReflections;
				shadows_.ShaftDensity = s.RTShaftDensity;
				shadows_.ShaftAnisotropy = s.RTShaftAnisotropy;
				shadows_.SunAngularDiameter = Mathf.Clamp(s.SunSize, 0.05f, 5.0f);
				shadows_.DebugView = ShadowDebugView;
				shadows_.DebugGI = GIDebugView;
				shadows_.DebugExactSun = ExactSunDebugView;
				shadows_.DebugAlbedo = AlbedoDebugView;
			}

			if (reflection_ != null && (benchmarkOff_ & RTStage.Reflections) != 0) {
				reflection_.enabled = false;
				if (glass_ != null) {
					glass_.enabled = false;
				}
			}
			if (shadows_ != null && benchmarkOff_ != RTStage.None) {
				shadows_.TraceShadows &= (benchmarkOff_ & RTStage.Shadows) == 0;
				shadows_.TraceAO &= (benchmarkOff_ & RTStage.Ambient) == 0;
				shadows_.Bounce &= (benchmarkOff_ & RTStage.Bounce) == 0;
				shadows_.enabled &= shadows_.TraceShadows || shadows_.TraceAO || shadows_.Shafts;
			}
		}
	}
}

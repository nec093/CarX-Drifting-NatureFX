using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
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
			return true;
		}

		// the ray traced sun shadows run now (HDRP's are off for them)
		public bool SunShadowsRunning => shadowlessSun_ != null;

		public string ShadowStatus => shadows_ == null ? "unavailable (needs DX12 ray tracing)" : !shadows_.enabled ? "off" : shadows_.Status;
		public string ReflectionStatus => reflection_ == null ? "unavailable (needs DX12 ray tracing)" : !reflection_.enabled ? "off" : reflection_.Status;
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
			if (dxrScene_ == null || (!reflections && !shadows)) {
				return;
			}
			Benchmark.Update();
			lights_.Update(Camera.main);
			RTTimings.BeginSceneUpdate();
			dxrScene_.Update();
			RTTimings.EndSceneUpdate();

			if (sun != null && sun.isActiveAndEnabled) {
				var toSun = -sun.transform.forward;
				var color = sun.color.linear * sun.intensity;
				if (sun.useColorTemperature) {
					color *= Mathf.CorrelatedColorTemperatureToRGB(sun.colorTemperature);
				}
				sunDirection_ = new Vector4(toSun.x, toSun.y, toSun.z, toSun.y > 0.0f ? 1.0f : 0.0f);
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

		// The measurement overlay (Settings.RTOverlay): GPU time per stage, rays, lights, scene and memory, refreshed
		// twice a second so it can be read
		private string overlay_ = "";
		private float nextOverlay_;
		private GUIStyle overlayStyle_;

		public void DrawOverlay() {
			if (Time.unscaledTime >= nextOverlay_) {
				nextOverlay_ = Time.unscaledTime + 0.5f;
				var text = new System.Text.StringBuilder();
				text.AppendLine($"NatureFX RT  {NativeEngine.ModeName}{(NativeEngine.Dxr12SunActive ? " + DXR 1.2 sun" : "")}   {1.0f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):0} fps");
				text.AppendLine(RTTimings.Summary());
				long rays = shadows_ != null && shadows_.enabled ? shadows_.TracedRays : 0;
				text.AppendLine($"shadow / ambient rays  {rays / 1e6:0.00} M per frame");
				text.AppendLine($"lamps  {lights_.Count} ({lights_.ShadowCount} traced, {NativeEngine.Emitters.Count} emissive surfaces in the scene){(LampShadows ? ", shadow maps taken over" : "")}");
				text.AppendLine($"scene  {NativeEngine.SceneStatus}");
				text.AppendLine($"GPU memory  {NativeEngine.GpuMemory}");
				text.AppendLine($"shadows: {ShadowStatus}");
				text.Append($"reflections: {ReflectionStatus}");
				overlay_ = text.ToString();
			}
			overlayStyle_ ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 13, wordWrap = true, richText = false };
			overlayStyle_.normal.textColor = Color.white;
			var size = overlayStyle_.CalcHeight(new GUIContent(overlay_), 520.0f);
			GUI.Box(new Rect(10.0f, 10.0f, 520.0f, size + 8.0f), overlay_, overlayStyle_);
		}

		// Point and spot lights lose their shadow maps while the traced lamp shadows run (LocalLights then traces them),
		// and get them back as they were when those stop. Also spares HDRP a shadow map per lamp.
		private void UpdateLampShadows(bool takeOver) {
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
		public void PrepareSceneForRayTracing() {
			dxrScene_?.Rebuild();
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
			NativeEngine.Enabled = s.Enabled;
			NativeEngine.Pipeline = s.RTBackend == 0;
			NativeEngine.Dxr12Sun = NativeEngine.Enabled && s.RTDxr12Sun;
			NativeEngine.Wind = s.RTWind;
			NativeEngine.Dxr12SunPaused = (benchmarkOff_ & RTStage.Dxr12Sun) != 0;
			NativeEngine.OmmPaused = (benchmarkOff_ & RTStage.Omm) != 0;
			if (reflection_ != null) {
				reflection_.enabled = s.Enabled && s.RTReflections;
				reflection_.Intensity = s.RTReflectionIntensity;
				reflection_.RoughnessCutoff = Mathf.Clamp(s.RTReflectionRoughness, 0.05f, 1.0f);
				reflection_.MaxDistance = Mathf.Max(5.0f, s.RTReflectionDistance);
				// 0 performance: half resolution, 1 balanced: + second bounce on metal, 2 quality: full resolution too
				reflection_.FullResolution = s.RTReflectionQuality >= 2;
				reflection_.SecondBounce = s.RTReflectionQuality >= 1;
				reflection_.WetStreaks = s.RTWetReflections;
			}
			if (glass_ != null) {
				glass_.enabled = s.Enabled && s.RTReflections && s.RTGlassReflections;
				glass_.Refraction = Mathf.Clamp01(s.RTGlassRefraction);
				glass_.Intensity = s.RTReflectionIntensity;
				glass_.MaxDistance = Mathf.Max(5.0f, s.RTReflectionDistance);
			}
			if (shadows_ != null) {
				shadows_.enabled = s.Enabled && (s.RTShadows || s.RTAO || s.RTShafts);
				shadows_.TraceShadows = s.RTShadows;
				shadows_.TraceAO = s.RTAO;
				shadows_.AOStrength = s.RTAOStrength;
				shadows_.AODistance = s.RTAODistance;
				shadows_.SkyOcclusionDistance = s.RTSkyOcclusion;
				shadows_.ContactShadows = s.RTContactShadows;
				shadows_.Bounce = s.RTGIBounce;
				shadows_.MultiBounceGI = s.RTGIMultiBounce;
				shadows_.GIDistance = Mathf.Max(1.0f, s.RTGIDistance);
				shadows_.GIIntensity = Mathf.Max(0.0f, s.RTGIIntensity);
				shadows_.Strength = s.RTShadowStrength;
				shadows_.FullResolutionShadows = s.RTShadowFullRes;
				shadows_.ExactSun = s.RTExactSun;
				shadows_.LeafTranslucency = s.RTLeafTranslucency;
				shadows_.Shafts = s.RTShafts;
				lights_.Enabled = s.RTLocalLights;
				LampShadows = s.RTLampShadows;
				lights_.Emissive = s.RTEmissiveLights;
				NativeEngine.GlassShadows = s.RTGlassShadows;
				shadows_.QuarterResolution = s.RTTraceScale == 1;
				shadows_.VarianceDenoiser = s.RTDenoiser == 1;
				shadows_.ShaftDensity = s.RTShaftDensity;
				shadows_.ShaftAnisotropy = s.RTShaftAnisotropy;
				shadows_.SunAngularDiameter = Mathf.Clamp(s.SunSize, 0.05f, 5.0f);
				shadows_.DebugView = ShadowDebugView;
				shadows_.DebugGI = GIDebugView;
				shadows_.DebugExactSun = ExactSunDebugView;
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

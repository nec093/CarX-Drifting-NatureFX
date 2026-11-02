using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace NatureFX.Rendering {
	// HDRP's sky light, reflections and sky handed over to the own engine (Settings.OwnSkyLighting):
	// 1. HDRP mirrors nothing (its reflection multiplier 0): the reflections composite (NFX_RTReflect) adds the traced
	//    reflections and, on what it does not trace (rough surfaces), the own sky through the GI's sky share
	// 2. HDRP lights nothing with its sky (its indirect diffuse multiplier 0): the shadow composite (NFX_RTShadow) adds
	//    the own sky's light through the GI's sky share and the light bounced in
	// 3. HDRP draws no sky (sky type none, no cube map of the own sky for it any more)
	// Each part only while the composite that replaces it ran in the last frames: a pass that stops (switched off, the
	// engine not running) gives HDRP its own back, so nothing goes dark.
	public static class OwnLighting {
		private const int Grace = 30; // frames a composite may miss before HDRP takes over again

		private static int shadowFrame_ = -1000, reflectionFrame_ = -1000, sunFrame_ = -1000, lampsFrame_ = -1000;
		private static bool wanted_, wantedSun_, wantedLamps_;

		public static bool Ambient { get; private set; }      // HDRP's indirect diffuse is off; the shadow composite adds the sky
		public static bool Reflections { get; private set; }  // HDRP's reflections are off; the reflections composite adds them
		public static bool Sky => Ambient && Reflections;     // HDRP draws no sky at all
		public static float SkyStrength = 1.0f;               // Settings.SkyLightStrength: the own sky's light on surfaces
		public static bool ShadowsRunning { get; private set; } // the shadow composite ran (the traced sun and lamp shadows)
		// 4. HDRP's sun (the light the own engine follows) lights nothing: the shadow composite adds the exact sun light
		//    (NFX_SunExact, from the G-buffer as HDRP would) or its own BSDF under the traced shadow, instead of taking
		//    the shadowed share of HDRP's away (Settings.OwnSunLight)
		public static bool Sun { get; private set; }
		public static void SunComposed() => sunFrame_ = Time.frameCount;
		// HDRP dropped the sun lighting nothing from its lists (RTShadowPass.CheckOwnSun): HDRP's own for this session
		public static bool SunUnavailable;

		// 5. HDRP's lamps (point and spot lights) light nothing: the lamp masks' pass works their light out from the
		//    G-buffer at full strength and the shadow composite adds the lit share (Settings.OwnLampLight)
		public static bool Lamps { get; private set; }
		public static void LampsComposed() => lampsFrame_ = Time.frameCount;
		private static readonly System.Collections.Generic.List<Light> lampsOff_ = new();
		private static float nextLampScan_;

		// ---- a light HDRP keeps in its lists but that lights nothing in HDRP's own shading: on a rendering layer no
		// renderer is on (affectDiffuse / affectSpecular off made HDRP drop the light from its lists, and the exact
		// passes found no sun: RTShadowPass.CheckOwnSun). The exact passes do not look at layers.
		private const int UnusedLayer = 1 << 30;
		// 6. What HDRP draws see-through after the composites (fences, signs, lit particles) is not in the G-buffer the
		//    own engine lights: HDRP's sun and lamps go on a rendering layer only those renderers are put on (the game's
		//    opaque ones are taken off it), and HDRP's ambient multiplier leaves that layer out, with the own sky's
		//    probe bound as HDRP's (RTShadowPass). Bit 7: within the light layers every HDRP version keeps.
		private const int TransparentLayer = 1 << 7;
		public static bool TransparentsWanted;
		public static bool Transparents { get; private set; }
		public static uint TransparentMask => TransparentLayer;
		private static int LightLayer => Transparents ? TransparentLayer : UnusedLayer;
		private static readonly System.Collections.Generic.Dictionary<Renderer, uint> rendererMasks_ = new();
		private static Renderer[] pending_;
		private static int pendingAt_;
		private static float nextRendererScan_;
		private static readonly System.Collections.Generic.List<Material> materials_ = new();
		public static int TransparentRenderers { get; private set; }
		// 7. With the own BSDF (no G-buffer: RTShadowPass.OwnBrdf) what the own engine does not trace has no colour
		//    for it: such renderers (RayTracingScene marks them) and NatureFX's own cards (the distant forest, grass,
		//    debris: DrawnMask) go on the same layer, so HDRP lights them, and the composite keeps HDRP's light there
		public static readonly System.Collections.Generic.HashSet<Renderer> Untraced = new();
		public static bool UntracedLit { get; private set; }
		private static int ownBrdfFrame_ = -1000;
		public static void OwnBrdfComposed() => ownBrdfFrame_ = Time.frameCount;
		// the rendering layer to add to what NatureFX draws itself that the own engine does not trace
		public static uint UntracedBit => UntracedLit ? (uint)TransparentLayer : 0u;
		private static readonly System.Collections.Generic.Dictionary<Light, (uint Mask, object HdMask)> layered_ = new();
		private static System.Reflection.PropertyInfo hdLayers_;
		private static bool hdLayersLooked_;
		private static bool? layersSupported_;

		// whether HDRP's asset has its light (rendering) layers on: without them a light's layer is not looked at
		public static bool LayersSupported {
			get {
				if (layersSupported_ == null) {
					layersSupported_ = true; // unknown: assume so (RTShadowPass.CheckOwnSun still guards the sun)
					try {
						if (UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset) {
							object settings = asset.currentPlatformRenderPipelineSettings;
							foreach (string name in new[] { "supportLightLayers", "supportRenderingLayers" }) {
								var f = settings.GetType().GetField(name);
								if (f != null && f.FieldType == typeof(bool)) {
									layersSupported_ = (bool)f.GetValue(settings);
									break;
								}
							}
						}
					} catch (System.Exception) {
						// keep the assumption
					}
				}
				return layersSupported_.Value;
			}
		}

		public static void HdrpLightOff(Light light) {
			if (light == null || layered_.ContainsKey(light)) {
				return;
			}
			if (!hdLayersLooked_) {
				hdLayersLooked_ = true;
				hdLayers_ = typeof(HDAdditionalLightData).GetProperty("lightlayersMask");
			}
			object hdMask = null;
			var hd = light.GetComponent<HDAdditionalLightData>();
			if (hd != null && hdLayers_ != null && hdLayers_.CanWrite) {
				try {
					hdMask = hdLayers_.GetValue(hd);
					if (!SetHdLayer(hd, LightLayer)) {
						hdMask = null;
					}
				} catch (System.Exception) {
					hdMask = null;
				}
			}
			layered_[light] = ((uint)light.renderingLayerMask, hdMask);
			light.renderingLayerMask = LightLayer;
		}

		private static bool SetHdLayer(HDAdditionalLightData hd, int layer) {
			var type = hdLayers_.PropertyType;
			object value = type.IsEnum ? System.Enum.ToObject(type, layer)
				: type == typeof(uint) ? (object)(uint)layer : type == typeof(int) ? layer : null;
			if (value == null) {
				return false;
			}
			hdLayers_.SetValue(hd, value);
			return true;
		}

		// the lights already off moved to the layer now used (see-through surfaces lit or not)
		private static void Relayer() {
			foreach (var kv in layered_) {
				var light = kv.Key;
				if (light == null) {
					continue;
				}
				light.renderingLayerMask = LightLayer;
				var hd = light.GetComponent<HDAdditionalLightData>();
				if (hd != null && kv.Value.HdMask != null && hdLayers_ != null) {
					try {
						SetHdLayer(hd, LightLayer);
					} catch (System.Exception) {
					}
				}
			}
		}

		// see-through only: every material drawn after the opaque ones (a renderer with an opaque one too is left off:
		// its opaque part would be lit twice)
		private static bool SeeThrough(Renderer r) {
			r.GetSharedMaterials(materials_);
			bool any = false;
			foreach (var m in materials_) {
				if (m == null) {
					continue;
				}
				if (m.renderQueue <= 2500) {
					return false;
				}
				any = true;
			}
			return any;
		}

		// the renderers on the see-through layer (or off it), a few hundred a frame; all looked at again every 10 s
		// and after a scene load (Rescan)
		private static void UpdateRenderers() {
			if (!Transparents) {
				if (rendererMasks_.Count > 0) {
					foreach (var kv in rendererMasks_) {
						if (kv.Key != null) {
							kv.Key.renderingLayerMask = kv.Value;
						}
					}
					rendererMasks_.Clear();
				}
				pending_ = null;
				TransparentRenderers = 0;
				return;
			}
			if (pending_ == null) {
				if (Time.unscaledTime < nextRendererScan_) {
					return;
				}
				nextRendererScan_ = Time.unscaledTime + 10.0f;
				pending_ = Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
				pendingAt_ = 0;
				var dead = new System.Collections.Generic.List<Renderer>();
				foreach (var r in rendererMasks_.Keys) {
					if (r == null) {
						dead.Add(r);
					}
				}
				foreach (var r in dead) {
					rendererMasks_.Remove(r);
				}
			}
			int end = System.Math.Min(pending_.Length, pendingAt_ + 400);
			for (; pendingAt_ < end; ++pendingAt_) {
				var r = pending_[pendingAt_];
				if (r == null || rendererMasks_.ContainsKey(r)) {
					continue;
				}
				uint mask = r.renderingLayerMask;
				uint want = SeeThrough(r) || UntracedLit && Untraced.Contains(r) ? mask | TransparentLayer : mask & ~(uint)TransparentLayer;
				if (want != mask) {
					rendererMasks_[r] = mask;
					r.renderingLayerMask = want;
				}
			}
			if (pendingAt_ >= pending_.Length) {
				pending_ = null;
				int n = 0;
				foreach (var kv in rendererMasks_) {
					if (kv.Key != null && (kv.Value & TransparentLayer) == 0) {
						++n;
					}
				}
				TransparentRenderers = n;
			}
		}

		// a scene load: look at the renderers again now
		public static void Rescan() {
			nextRendererScan_ = 0.0f;
		}

		public static void HdrpLightOn(Light light) {
			if (light == null || !layered_.TryGetValue(light, out var was)) {
				return;
			}
			layered_.Remove(light);
			light.renderingLayerMask = (int)was.Mask;
			var hd = light.GetComponent<HDAdditionalLightData>();
			if (hd != null && was.HdMask != null && hdLayers_ != null) {
				try {
					hdLayers_.SetValue(hd, was.HdMask);
				} catch (System.Exception) {
				}
			}
		}

		// HDRP's punctual lights out of HDRP's own shading while Lamps (looked for once a second: lamps come and go)
		private static void UpdateLamps() {
			if (!Lamps) {
				if (lampsOff_.Count > 0) {
					foreach (var l in lampsOff_) {
						HdrpLightOn(l);
					}
					lampsOff_.Clear();
				}
				return;
			}
			if (Time.unscaledTime < nextLampScan_) {
				return;
			}
			nextLampScan_ = Time.unscaledTime + 1.0f;
			lampsOff_.RemoveAll(l => l == null);
			foreach (var light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None)) {
				if (light.type != LightType.Point && light.type != LightType.Spot || layered_.ContainsKey(light)) {
					continue;
				}
				HdrpLightOff(light);
				lampsOff_.Add(light);
			}
		}

		// the composites say they ran (with what they need: the own sky probe)
		public static void ShadowComposed() => shadowFrame_ = Time.frameCount;
		public static void ReflectionComposed() => reflectionFrame_ = Time.frameCount;

		// once a frame, before VolumeOverhaul.UpdateSky
		public static void Update(bool wanted, bool wantedSun, bool wantedLamps) {
			wanted_ = wanted && NativeEngine.SkyProbe != null;
			wantedSun_ = wantedSun;
			wantedLamps_ = wantedLamps;
			int now = Time.frameCount;
			Sun = wantedSun_ && !SunUnavailable && LayersSupported && now - sunFrame_ <= Grace;
			Lamps = wantedLamps_ && LayersSupported && now - lampsFrame_ <= Grace;
			UpdateLamps();
			bool transparents = TransparentsWanted && LayersSupported && (Sun || Lamps || wanted_);
			bool untracedLit = transparents && now - ownBrdfFrame_ <= Grace;
			if (transparents != Transparents || untracedLit != UntracedLit) {
				Transparents = transparents;
				UntracedLit = untracedLit;
				Relayer();
				// the renderers looked at again from the game's own masks
				foreach (var kv in rendererMasks_) {
					if (kv.Key != null) {
						kv.Key.renderingLayerMask = kv.Value;
					}
				}
				rendererMasks_.Clear();
				pending_ = null;
				nextRendererScan_ = 0.0f;
			}
			UpdateRenderers();
			ShadowsRunning = now - shadowFrame_ <= Grace;
			Ambient = wanted_ && ShadowsRunning;
			Reflections = wanted_ && now - reflectionFrame_ <= Grace;
		}

		// the sky's light as SH9 (HDRP's packing): the own engine's sky probe while the own sky runs (HDRP's is black
		// once HDRP draws no sky), else HDRP's
		public static GraphicsBuffer Probe(HDCamera camera) {
			return wanted_ && NativeEngine.SkyProbe != null ? NativeEngine.SkyProbe : HdrpBindings.AmbientProbe(camera);
		}

		public static string Status => !wanted_ ? "off (HDRP's own sky light and reflections)"
			: $"HDRP's sky light {(Ambient ? "from the own engine" : "HDRP's (shadow composite not running)")}, " +
			  $"reflections {(Reflections ? "from the own engine" : "HDRP's (reflections composite not running)")}, " +
			  $"HDRP's sky {(Sky ? "off" : "on")}, sun light {(Sun ? "from the own engine" : !LayersSupported && wantedSun_ ? "HDRP's (HDRP's light layers are off)" : SunUnavailable ? "HDRP's (HDRP drops a sun that lights nothing)" : wantedSun_ ? "HDRP's (exact sun pass not running)" : "HDRP's")}, " +
			  $"lamps {(Lamps ? $"from the own engine ({lampsOff_.Count} of HDRP's off)" : wantedLamps_ ? "HDRP's (lamp masks not running)" : "HDRP's")}, " +
			  $"see-through surfaces {(Transparents ? $"lit by HDRP ({TransparentRenderers} renderers on layer 7{(UntracedLit ? $", {Untraced.Count} untraced ones with them" : "")})" : TransparentsWanted ? "unlit" : "off")}";
	}

	// Before HDRP draws anything: the own sky's probe bound as HDRP's ambient probe (HDRP's is black with its sky off),
	// so what HDRP still lights on the see-through layer (OwnLighting 6 and 7: see-through surfaces, what the own
	// engine does not trace under the own BSDF) gets the sky's light in its G-buffer or forward pass. The rest is
	// unaffected: HDRP's indirect multiplier is 0 outside that layer (VolumeOverhaul).
	public class AmbientFeedPass : UnityEngine.Rendering.HighDefinition.CustomPass {
		private static readonly int AmbientProbeId = Shader.PropertyToID("_AmbientProbeData");

		protected override void Execute(UnityEngine.Rendering.HighDefinition.CustomPassContext ctx) {
			if (!OwnLighting.Transparents || !OwnLighting.Ambient || NativeEngine.SkyProbe == null) {
				return;
			}
			if (ctx.hdCamera.camera.cameraType != CameraType.Game) {
				return;
			}
			ctx.cmd.SetGlobalBuffer(AmbientProbeId, NativeEngine.SkyProbe);
		}
	}
}

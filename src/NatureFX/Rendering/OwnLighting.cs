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
		private static readonly System.Collections.Generic.List<HDAdditionalLightData> lampsOff_ = new();
		private static float nextLampScan_;

		// HDRP's punctual lights switched off for HDRP's lighting while Lamps (looked for once a second: lamps come and go)
		private static void UpdateLamps() {
			if (!Lamps) {
				if (lampsOff_.Count > 0) {
					foreach (var l in lampsOff_) {
						if (l != null) {
							l.affectDiffuse = true;
							l.affectSpecular = true;
						}
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
				if (light.type != LightType.Point && light.type != LightType.Spot) {
					continue;
				}
				var hd = light.GetComponent<HDAdditionalLightData>();
				if (hd == null || !hd.affectDiffuse && !hd.affectSpecular) {
					continue;
				}
				hd.affectDiffuse = false;
				hd.affectSpecular = false;
				lampsOff_.Add(hd);
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
			Sun = wantedSun_ && !SunUnavailable && now - sunFrame_ <= Grace;
			Lamps = wantedLamps_ && now - lampsFrame_ <= Grace;
			UpdateLamps();
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
			  $"HDRP's sky {(Sky ? "off" : "on")}, sun light {(Sun ? "from the own engine" : SunUnavailable ? "HDRP's (HDRP drops a sun that lights nothing)" : wantedSun_ ? "HDRP's (exact sun pass not running)" : "HDRP's")}, " +
			  $"lamps {(Lamps ? $"from the own engine ({lampsOff_.Count} of HDRP's off)" : wantedLamps_ ? "HDRP's (lamp masks not running)" : "HDRP's")}";
	}
}

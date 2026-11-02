using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

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

		private static int shadowFrame_ = -1000, reflectionFrame_ = -1000;
		private static bool wanted_;

		public static bool Ambient { get; private set; }      // HDRP's indirect diffuse is off; the shadow composite adds the sky
		public static bool Reflections { get; private set; }  // HDRP's reflections are off; the reflections composite adds them
		public static bool Sky => Ambient && Reflections;     // HDRP draws no sky at all

		// the composites say they ran (with what they need: the own sky probe)
		public static void ShadowComposed() => shadowFrame_ = Time.frameCount;
		public static void ReflectionComposed() => reflectionFrame_ = Time.frameCount;

		// once a frame, before VolumeOverhaul.UpdateSky
		public static void Update(bool wanted) {
			wanted_ = wanted && NativeEngine.SkyProbe != null;
			int now = Time.frameCount;
			Ambient = wanted_ && now - shadowFrame_ <= Grace;
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
			  $"HDRP's sky {(Sky ? "off" : "on")}";
	}
}

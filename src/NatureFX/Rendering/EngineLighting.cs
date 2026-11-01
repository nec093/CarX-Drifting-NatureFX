namespace NatureFX.Rendering {
	// Roadmap step A (docs/native-atmosphere-lighting-plan.md): with Native lighting on, the own engine does the opaque
	// lighting whatever the ray tracing toggles say: traced sun and moon shadows, lamp shadows, the sky light and GI,
	// reflections. The toggles then only matter with it off. Read these instead of the RT settings themselves.
	public static class EngineLighting {
		public static bool Shadows(ISettings s) => s.RTShadows || s.NativeLighting;
		public static bool Ambient(ISettings s) => s.RTAO || s.NativeLighting;
		public static bool Reflections(ISettings s) => s.RTReflections || s.NativeLighting;
		public static bool LocalLights(ISettings s) => s.RTLocalLights || s.NativeLighting;
		public static bool LampShadows(ISettings s) => s.RTLampShadows || s.NativeLighting;
		public static bool GlassReflections(ISettings s) => s.RTGlassReflections || s.NativeLighting;
	}
}

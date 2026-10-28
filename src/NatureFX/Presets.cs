namespace NatureFX {
	public enum Preset {
		Custom = 0,
		Performance = 1,
		Balanced = 2,
		Quality = 3,
		RayTraced = 4
	}

	public static class Presets {
		public static readonly string[] Names = { "Custom", "Performance", "Balanced", "Quality", "Ray Traced" };

		public static void Apply(ISettings s, Preset preset) {
			s.Preset = (int)preset;
			if (preset == Preset.Custom) {
				return;
			}

			bool perf = preset == Preset.Performance;
			bool rt = preset == Preset.RayTraced;

			s.GlobalIllumination = !perf;
			s.Reflections = true;
			s.AmbientOcclusion = true;
			s.AmbientOcclusionIntensity = perf ? 0.6f : 1.0f;
			s.ContactShadows = !perf;
			s.VolumetricFog = !perf;
			s.FogDistance = preset == Preset.Quality || rt ? 900.0f : 1400.0f;
			s.FogHeight = 60.0f;
			s.Bloom = perf ? 0.1f : 0.2f;
			s.Exposure = 0.0f;
			s.Saturation = 5.0f;
			s.Contrast = 8.0f;
			s.AcesTonemapping = true;

			s.RayTracing = rt;
			s.RayTracingQuality = 0;
			s.PathTracingSamples = 256;
			s.PathTracingBounces = 4;
		}
	}
}

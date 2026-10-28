using KSL.API;

namespace NatureFX {
	// Only KSL-supported primitive types, so every value auto-saves.
	public interface ISettings {
		// general
		public bool Enabled { get; set; }
		public int Preset { get; set; }

		// lighting (HDRP volume overrides)
		public bool GlobalIllumination { get; set; }
		public bool Reflections { get; set; }
		public bool AmbientOcclusion { get; set; }
		public float AmbientOcclusionIntensity { get; set; }
		public bool ContactShadows { get; set; }
		public bool VolumetricFog { get; set; }
		public float FogDistance { get; set; }
		public float FogHeight { get; set; }
		public float Bloom { get; set; }
		public float Exposure { get; set; }
		public float Saturation { get; set; }
		public float Contrast { get; set; }
		public bool AcesTonemapping { get; set; }

		// ray tracing (needs DX12 + HDRP ray tracing support in the game's pipeline asset)
		public bool RayTracing { get; set; }
		public int RayTracingQuality { get; set; }
		public bool PhotoModePathTracing { get; set; }
		public int PathTracingSamples { get; set; }
		public int PathTracingBounces { get; set; }

		// nature
		public bool Wind { get; set; }
		public float WindStrength { get; set; }
		public float WindGust { get; set; }
		public float WindDirection { get; set; }
		public float LeafFlutter { get; set; }
		public bool InteractiveFoliage { get; set; }

		// accumulation / weather
		public bool Accumulation { get; set; }
		public bool AccumulateOverTime { get; set; }
		public float AccumulationRate { get; set; }
		public float Snow { get; set; }
		public float Leaves { get; set; }
		public float Dust { get; set; }
		public float Wetness { get; set; }
		public bool WetGameMaterials { get; set; }

		// shader replacement (needs naturefx.bundle)
		public bool ReplaceShaders { get; set; }
	}

	public static class Settings {
		public static ISettings Data { get; private set; }

		public static bool Init() {
			Data = Kino.Config.RegisterConfig<ISettings>();
			if (Data == null) {
				return false;
			}

			if (Data.Preset == 0 && !Data.Enabled && Data.FogDistance <= 0.0f) {
				Presets.Apply(Data, Preset.Balanced);
				Data.Enabled = true;
				Data.Wind = true;
				Data.InteractiveFoliage = true;
				Data.WindStrength = 0.6f;
				Data.WindGust = 0.5f;
				Data.WindDirection = 45.0f;
				Data.LeafFlutter = 0.5f;
				Data.AccumulationRate = 0.02f;
				Data.ReplaceShaders = true;
			}

			return true;
		}

		public static void Save() {
			Kino.Config.Save<ISettings>();
		}
	}
}

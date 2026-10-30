using KSL.API;

namespace NatureFX {
	// Only KSL-supported primitive types, so every value auto-saves.
	public interface ISettings {
		// general
		public bool Enabled { get; set; }

		// exposure and tone (ray tracing only mode, VolumeOverhaul)
		public float Exposure { get; set; }
		public float Saturation { get; set; }
		public float Contrast { get; set; }
		public bool AcesTonemapping { get; set; }

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
		public bool FullReplacement { get; set; }          // shader rebuild: every game material becomes a NatureFX one

		// --- added in config version 2 ---
		public int ConfigVersion { get; set; }
		public float FoliageSway { get; set; }

		// sun / time of day (overrides the track's directional light while enabled)
		public bool SunOverride { get; set; }
		public float TimeOfDay { get; set; }          // hours 0..24
		public float SunAzimuthOffset { get; set; }   // degrees added to the track's sun heading
		public float SunMaxElevation { get; set; }    // degrees at noon
		public float SunIntensityScale { get; set; }
		public float SunTemperature { get; set; }     // kelvin at noon (warmer near the horizon)
		public float LightShafts { get; set; }        // sun volumetric dimmer, needs volumetric fog

		// camera / filters
		public int TonemapMode { get; set; }          // 0 ACES, 1 Neutral, 2 Filmic (GT-style custom curve), 3 None

		// --- added in config version 3: screen space path tracing ---

		// --- added in config version 4: weather and shadows ---
		public float Rain { get; set; }               // 0..1 ripples, drips, wet surfaces
		public float SnowDepth { get; set; }          // metres of real snow thickness at full snow
		public bool RealisticShadows { get; set; }
		public float SunSize { get; set; }            // angular diameter in degrees (PCSS, needs High shadow filtering)

		// --- added in config version 5: camera realism ---
		public int ExposureMode { get; set; }         // 0 game, 1 eye adaptation, 2 physical camera
		public float AutoExposureMin { get; set; }    // EV100
		public float AutoExposureMax { get; set; }
		public float AdaptationSpeed { get; set; }

		// weather & environment
		public bool WeatherCycle { get; set; }
		public int WeatherPreset { get; set; }        // see WeatherController.Presets
		public float WeatherCycleMinutes { get; set; }
		public float RainStreaks { get; set; }
		public float LensDrops { get; set; }
		public float HeatHaze { get; set; }
		public bool Moonlight { get; set; }
		public float HeadlightShafts { get; set; }
		public float SurfaceDetail { get; set; }

		// tire marks / car rain
		public bool TireMarks { get; set; }
		public float RubberAmount { get; set; }
		public bool CarRain { get; set; }
		public float CarRainDrops { get; set; }
		public float OilFilm { get; set; }

		// map profiles (kino/config/NatureFX/maps/<scene>.nfx)
		public bool UseMapProfile { get; set; }

		// development: log the NatureFX shader variants the game uses, so the bundle build can skip the rest
		public bool RecordShaderVariants { get; set; }

		// F8 window: background opacity, scale and size (0 = default)
		public float UiOpacity { get; set; }
		public float UiScale { get; set; }
		public float UiWidth { get; set; }
		public float UiHeight { get; set; }
		public bool UiLargeText { get; set; }

		// ray traced reflections (DX12, DXR)
		public bool RTReflections { get; set; }
		public float RTReflectionIntensity { get; set; }
		public float RTReflectionRoughness { get; set; }   // surfaces rougher than this get none
		public float RTReflectionDistance { get; set; }
		public int RTReflectionQuality { get; set; }       // 0 performance, 1 balanced, 2 quality
		public bool RTGlassReflections { get; set; }       // own engine: glass (windows, lenses) mirrors the scene too
		public bool CarGlossyPaint { get; set; }           // no longer used: the reflections treat car paint as varnish over a rough base
		public bool CarChromeRims { get; set; }            // metal rims polished to a mirror

		// ray traced sun shadows (DX12, DXR); softness from SunSize
		public bool RTShadows { get; set; }
		public float RTShadowStrength { get; set; }
		public bool RTShadowFullRes { get; set; }          // full resolution (sharp contact shadows) or half
		public bool RTExactSun { get; set; }               // take away exactly the sun light HDRP added (G-buffer drawn again)
		public bool RTHideFakeShadows { get; set; }        // hide the game's painted shadow planes while the ray traced shadows run
		public bool RebuildFoliage { get; set; }           // shader rebuild: foliage too (wind, snow); off leaves it to the game's shaders
		public bool RTWind { get; set; }                   // own engine: the traced foliage sways with the wind as it is drawn
		public float RTLeafTranslucency { get; set; }      // sunlight through back-lit leaves (0: off)
		public bool RTShafts { get; set; }                 // ray traced light shafts
		public float RTShaftDensity { get; set; }          // scattering of the air (1/m)
		public float RTShaftAnisotropy { get; set; }       // forward scattering
		public bool RTLocalLights { get; set; }            // headlights and lamps in the ray traced effects
		public bool RTLampShadows { get; set; }            // lamps' HDRP shadow maps off, soft ray traced shadows instead
		public bool RTEmissiveLights { get; set; }         // bright emissive surfaces (neon, signs) light their surroundings, with shadows
		public bool RTGlassShadows { get; set; }           // the sun through glass is dimmed (tinted windows more)
		public bool RTWetReflections { get; set; }         // wet roads stretch reflections along the view
		public float RTGlassRefraction { get; set; }       // how much glass bends what is seen through it (0: off)
		public bool RTOverlay { get; set; }                // on-screen timings, ray counts and memory
		public int RTTraceScale { get; set; }              // ambient / GI / lamp shadows: 0 half resolution, 1 quarter (upscaled over frames)
		public int RTDenoiser { get; set; }                // 0 the simple blur, 1 variance guided (sharper where the signal is clean)
		public bool RTNativeSun { get; set; }              // old own engine switch, read once by the version 22 migration
		public int RTBackend { get; set; }                 // how the own engine traces: 0 DXR 1.0 (ray tracing pipelines), 1 DXR 1.1 (inline)
		public bool RTDxr12Sun { get; set; }               // own engine: the sun shadows on the DXR 1.2 device (Agility SDK 1.619), test

		// ray traced ambient occlusion (DX12, DXR): darkens the light that does not come from the sun
		public bool RTAO { get; set; }
		public float RTAOStrength { get; set; }
		public float RTAODistance { get; set; }            // m
		public bool RTGIBounce { get; set; }               // ambient rays bring the colour of what they hit
		public bool RTGIMultiBounce { get; set; }          // own engine: every bounce (radiance cache), not one
		public float RTGIDistance { get; set; }            // own engine: GI ray length (m)
		public float RTGIIntensity { get; set; }           // own engine: strength of the light GI bounces in
		public float RTSkyOcclusion { get; set; }          // m: overhangs further than the ambient distance take the sky (0: off)
		public float RTContactShadows { get; set; }        // darkening where surfaces meet (0: off)

		// shader rebuild: road surface detail of the NatureFX shaders (grain, rubber laid down on the driven line)
		public float RoadWear { get; set; }

		// development: everything off except the ray traced effects (settings are kept)
		public bool DevRayTracingOnly { get; set; }
	}

	// Plain implementation of ISettings holding the default values (Settings.Defaults); the members mirror the
	// interface, so adding a setting without adding it here fails to compile.
	internal class SettingsDefaults : ISettings {
		// general
		public bool Enabled { get; set; }

		// exposure and tone (ray tracing only mode, VolumeOverhaul)
		public float Exposure { get; set; }
		public float Saturation { get; set; }
		public float Contrast { get; set; }
		public bool AcesTonemapping { get; set; }

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
		public bool FullReplacement { get; set; }          // shader rebuild: every game material becomes a NatureFX one

		// --- added in config version 2 ---
		public int ConfigVersion { get; set; }
		public float FoliageSway { get; set; }

		// sun / time of day (overrides the track's directional light while enabled)
		public bool SunOverride { get; set; }
		public float TimeOfDay { get; set; }          // hours 0..24
		public float SunAzimuthOffset { get; set; }   // degrees added to the track's sun heading
		public float SunMaxElevation { get; set; }    // degrees at noon
		public float SunIntensityScale { get; set; }
		public float SunTemperature { get; set; }     // kelvin at noon (warmer near the horizon)
		public float LightShafts { get; set; }        // sun volumetric dimmer, needs volumetric fog

		// camera / filters
		public int TonemapMode { get; set; }          // 0 ACES, 1 Neutral, 2 Filmic (GT-style custom curve), 3 None

		// --- added in config version 3: screen space path tracing ---

		// --- added in config version 4: weather and shadows ---
		public float Rain { get; set; }               // 0..1 ripples, drips, wet surfaces
		public float SnowDepth { get; set; }          // metres of real snow thickness at full snow
		public bool RealisticShadows { get; set; }
		public float SunSize { get; set; }            // angular diameter in degrees (PCSS, needs High shadow filtering)

		// --- added in config version 5: camera realism ---
		public int ExposureMode { get; set; }         // 0 game, 1 eye adaptation, 2 physical camera
		public float AutoExposureMin { get; set; }    // EV100
		public float AutoExposureMax { get; set; }
		public float AdaptationSpeed { get; set; }

		// weather & environment
		public bool WeatherCycle { get; set; }
		public int WeatherPreset { get; set; }        // see WeatherController.Presets
		public float WeatherCycleMinutes { get; set; }
		public float RainStreaks { get; set; }
		public float LensDrops { get; set; }
		public float HeatHaze { get; set; }
		public bool Moonlight { get; set; }
		public float HeadlightShafts { get; set; }
		public float SurfaceDetail { get; set; }

		// tire marks / car rain
		public bool TireMarks { get; set; }
		public float RubberAmount { get; set; }
		public bool CarRain { get; set; }
		public float CarRainDrops { get; set; }
		public float OilFilm { get; set; }

		// map profiles (kino/config/NatureFX/maps/<scene>.nfx)
		public bool UseMapProfile { get; set; }

		// development: log the NatureFX shader variants the game uses, so the bundle build can skip the rest
		public bool RecordShaderVariants { get; set; }

		// F8 window: background opacity, scale and size (0 = default)
		public float UiOpacity { get; set; }
		public float UiScale { get; set; }
		public float UiWidth { get; set; }
		public float UiHeight { get; set; }
		public bool UiLargeText { get; set; }

		// ray traced reflections (DX12, DXR)
		public bool RTReflections { get; set; }
		public float RTReflectionIntensity { get; set; }
		public float RTReflectionRoughness { get; set; }   // surfaces rougher than this get none
		public float RTReflectionDistance { get; set; }
		public int RTReflectionQuality { get; set; }       // 0 performance, 1 balanced, 2 quality
		public bool RTGlassReflections { get; set; }       // own engine: glass (windows, lenses) mirrors the scene too
		public bool CarGlossyPaint { get; set; }           // no longer used: the reflections treat car paint as varnish over a rough base
		public bool CarChromeRims { get; set; }            // metal rims polished to a mirror

		// ray traced sun shadows (DX12, DXR); softness from SunSize
		public bool RTShadows { get; set; }
		public float RTShadowStrength { get; set; }
		public bool RTShadowFullRes { get; set; }          // full resolution (sharp contact shadows) or half
		public bool RTExactSun { get; set; }               // take away exactly the sun light HDRP added (G-buffer drawn again)
		public bool RTHideFakeShadows { get; set; }        // hide the game's painted shadow planes while the ray traced shadows run
		public bool RebuildFoliage { get; set; }           // shader rebuild: foliage too (wind, snow); off leaves it to the game's shaders
		public bool RTWind { get; set; }                   // own engine: the traced foliage sways with the wind as it is drawn
		public float RTLeafTranslucency { get; set; }      // sunlight through back-lit leaves (0: off)
		public bool RTShafts { get; set; }                 // ray traced light shafts
		public float RTShaftDensity { get; set; }          // scattering of the air (1/m)
		public float RTShaftAnisotropy { get; set; }       // forward scattering
		public bool RTLocalLights { get; set; }            // headlights and lamps in the ray traced effects
		public bool RTLampShadows { get; set; }            // lamps' HDRP shadow maps off, soft ray traced shadows instead
		public bool RTEmissiveLights { get; set; }         // bright emissive surfaces (neon, signs) light their surroundings, with shadows
		public bool RTGlassShadows { get; set; }           // the sun through glass is dimmed (tinted windows more)
		public bool RTWetReflections { get; set; }         // wet roads stretch reflections along the view
		public float RTGlassRefraction { get; set; }       // how much glass bends what is seen through it (0: off)
		public bool RTOverlay { get; set; }                // on-screen timings, ray counts and memory
		public int RTTraceScale { get; set; }              // ambient / GI / lamp shadows: 0 half resolution, 1 quarter (upscaled over frames)
		public int RTDenoiser { get; set; }                // 0 the simple blur, 1 variance guided (sharper where the signal is clean)
		public bool RTNativeSun { get; set; }              // old own engine switch, read once by the version 22 migration
		public int RTBackend { get; set; }                 // how the own engine traces: 0 DXR 1.0 (ray tracing pipelines), 1 DXR 1.1 (inline)
		public bool RTDxr12Sun { get; set; }               // own engine: the sun shadows on the DXR 1.2 device (Agility SDK 1.619), test

		// ray traced ambient occlusion (DX12, DXR): darkens the light that does not come from the sun
		public bool RTAO { get; set; }
		public float RTAOStrength { get; set; }
		public float RTAODistance { get; set; }            // m
		public bool RTGIBounce { get; set; }               // ambient rays bring the colour of what they hit
		public bool RTGIMultiBounce { get; set; }          // own engine: every bounce (radiance cache), not one
		public float RTGIDistance { get; set; }            // own engine: GI ray length (m)
		public float RTGIIntensity { get; set; }           // own engine: strength of the light GI bounces in
		public float RTSkyOcclusion { get; set; }          // m: overhangs further than the ambient distance take the sky (0: off)
		public float RTContactShadows { get; set; }        // darkening where surfaces meet (0: off)

		// shader rebuild: road surface detail of the NatureFX shaders (grain, rubber laid down on the driven line)
		public float RoadWear { get; set; }

		// development: everything off except the ray traced effects (settings are kept)
		public bool DevRayTracingOnly { get; set; }
	}

	public static class Settings {
		public static ISettings Data { get; private set; }

		// Values of a fresh install (first run plus every migration); F8 resets single values to these.
		public static ISettings Defaults { get; private set; }

		// Everything off (Enabled = false): handed to the non ray tracing effects while DevRayTracingOnly is on,
		// so they switch themselves off without touching the saved settings.
		public static readonly ISettings Off = new SettingsDefaults();

		public static bool Init() {
			Data = Kino.Config.RegisterConfig<ISettings>();
			if (Data == null) {
				return false;
			}

			if (Data.ConfigVersion == 0 && !Data.Enabled) {
				ApplyFirstRun(Data);
			}
			Migrate(Data);

			Defaults = new SettingsDefaults();
			ApplyFirstRun(Defaults);
			Migrate(Defaults);
			return true;
		}

		private static void ApplyFirstRun(ISettings d) {
				d.Saturation = 5.0f;
				d.Contrast = 8.0f;
				d.AcesTonemapping = true;
				d.Enabled = true;
				d.Wind = true;
				d.InteractiveFoliage = true;
				d.WindStrength = 0.6f;
				d.WindGust = 0.5f;
				d.WindDirection = 45.0f;
				d.LeafFlutter = 0.5f;
				d.AccumulationRate = 0.02f;
				d.ReplaceShaders = true;
		}

		private static void Migrate(ISettings d) {
			// Older configs read the new values as 0; give them sane defaults once.
			if (d.ConfigVersion < 2) {
				d.FoliageSway = 1.0f;
				d.TimeOfDay = 14.0f;
				d.SunMaxElevation = 60.0f;
				d.SunIntensityScale = 1.0f;
				d.SunTemperature = 6000.0f;
				d.LightShafts = 1.0f;
				d.ConfigVersion = 2;
			}
			if (d.ConfigVersion < 3) {
				d.ConfigVersion = 3;
			}
			if (d.ConfigVersion < 4) {
				d.SnowDepth = 0.12f;
				d.RealisticShadows = true;
				d.SunSize = 0.53f; // the real sun
				d.ConfigVersion = 4;
			}
			if (d.ConfigVersion < 5) {
				d.ExposureMode = 1;
				d.AutoExposureMin = 8.0f;
				d.AutoExposureMax = 15.0f;
				d.AdaptationSpeed = 1.5f;
				d.WeatherPreset = 0;
				d.WeatherCycleMinutes = 15.0f;
				d.RainStreaks = 0.8f;
				d.LensDrops = 0.6f;
				d.HeatHaze = 0.3f;
				d.Moonlight = true;
				d.HeadlightShafts = 1.0f;
				d.SurfaceDetail = 0.6f;
				d.TireMarks = true;
				d.RubberAmount = 0.6f;
				d.CarRain = true;
				d.CarRainDrops = 0.7f;
				d.OilFilm = 0.3f;
				d.ConfigVersion = 5;
			}
			if (d.ConfigVersion < 6) {
				d.UseMapProfile = true;
				d.ConfigVersion = 6;
			}
			if (d.ConfigVersion < 7) {
				d.RTReflections = true;
				d.RTReflectionIntensity = 1.0f;
				d.RTReflectionRoughness = 0.5f;
				d.RTReflectionDistance = 150.0f;
				d.DevRayTracingOnly = true; // ray tracing development phase (2026-10-29)
				d.ConfigVersion = 7;
			}
			if (d.ConfigVersion < 8) {
				d.RTReflectionQuality = 2;
				d.ConfigVersion = 8;
			}
			if (d.ConfigVersion < 9) {
				d.RTShadows = true;
				d.RTShadowStrength = 1.0f;
				d.ConfigVersion = 9;
			}
			if (d.ConfigVersion < 10) {
				d.RTAO = true;
				d.RTAOStrength = 1.0f;
				d.RTAODistance = 3.0f;
				d.ConfigVersion = 10;
			}
			if (d.ConfigVersion < 11) {
				d.RTGIBounce = true;
				d.ConfigVersion = 11;
			}
			if (d.ConfigVersion < 12) {
				d.RTShadowFullRes = true;
				d.ConfigVersion = 12;
			}
			if (d.ConfigVersion < 13) {
				// ray tracing only mode now keeps NatureFX's exposure: start from eye adaptation and ACES
				d.ExposureMode = 1;
				d.AcesTonemapping = true;
				d.ConfigVersion = 13;
			}
			if (d.ConfigVersion < 14) {
				d.RTLeafTranslucency = 0.6f;
				d.ConfigVersion = 14;
			}
			if (d.ConfigVersion < 15) {
				d.RTReflectionRoughness = 0.8f; // rough reflections: asphalt sheen, dull paint
				d.ConfigVersion = 15;
			}
			if (d.ConfigVersion < 16) {
				d.RTShafts = true;
				d.RTShaftDensity = 0.004f;
				d.RTShaftAnisotropy = 0.6f;
				d.ConfigVersion = 16;
			}
			if (d.ConfigVersion < 17) {
				d.RTLocalLights = true;
				d.ConfigVersion = 17;
			}
			if (d.ConfigVersion < 18) {
				// shader rebuild, stage 0: replace every game material (docs/shader-rebuild-plan.md)
				d.ReplaceShaders = true;
				d.FullReplacement = true;
				d.ConfigVersion = 18;
			}
			if (d.ConfigVersion < 19) {
				// light shafts: 0.004 /m was fog (visibility about 1 km) and washed the view towards the sun out
				d.RTShaftDensity = 0.0003f;
				d.ConfigVersion = 19;
			}
			if (d.ConfigVersion < 20) {
				// ambient light: sky taken by roofs out of the ambient ray's reach, contact shadows; road wear
				d.RTSkyOcclusion = 30.0f;
				d.RTContactShadows = 0.5f;
				d.RoadWear = 0.6f;
				d.ConfigVersion = 20;
			}
			if (d.ConfigVersion < 21) {
				// the surface grain read as a grainy, noisy floor once the ray tracing lit it: subtler by default
				d.SurfaceDetail = System.Math.Min(d.SurfaceDetail, 0.2f);
				d.ConfigVersion = 21;
			}
			if (d.ConfigVersion < 22) {
				// one ray tracing backend for every effect, chosen in Settings (the own engine switch became it)
				d.RTBackend = d.RTNativeSun ? 1 : 0;
				d.ConfigVersion = 22;
			}
			if (d.ConfigVersion < 23) {
				// global illumination with every bounce in the own engine
				d.RTGIMultiBounce = true;
				d.RTGIDistance = 20.0f;
				d.ConfigVersion = 23;
			}
			if (d.ConfigVersion < 24) {
				// GI adds the light it bounces in (it scaled HDRP's ambient light before)
				d.RTGIIntensity = 1.0f;
				d.ConfigVersion = 24;
			}
			if (d.ConfigVersion < 25) {
				// shadows take away the sun light HDRP worked out from its G-buffer, not an estimate of it
				d.RTExactSun = true;
				d.ConfigVersion = 25;
			}
			if (d.ConfigVersion < 26) {
				// glass reflections with the own engine
				d.RTGlassReflections = true;
				d.ConfigVersion = 26;
			}
			if (d.ConfigVersion < 27) {
				// showroom car finish
				d.CarGlossyPaint = true;
				d.CarChromeRims = true;
				d.ConfigVersion = 27;
			}
			if (d.ConfigVersion < 28) {
				// the game's painted shadows hidden under the ray traced ones
				d.RTHideFakeShadows = true;
				d.ConfigVersion = 28;
			}
			if (d.ConfigVersion < 30) {
				// foliage rebuilt again (the square patches were another mod's graphics settings)
				d.RebuildFoliage = true;
				d.ConfigVersion = 30;
			}
			if (d.ConfigVersion < 31) {
				// the traced shadows of swaying foliage sway with it
				d.RTWind = true;
				d.ConfigVersion = 31;
			}
			if (d.ConfigVersion < 32) {
				// every lamp near the camera casts soft ray traced shadows (their shadow maps off)
				d.RTLampShadows = true;
				d.ConfigVersion = 32;
			}
			if (d.ConfigVersion < 33) {
				d.RTEmissiveLights = true;
				d.RTGlassShadows = true;
				d.RTWetReflections = true;
				d.RTGlassRefraction = 0.35f;
				d.RTDenoiser = 1;
				d.ConfigVersion = 33;
			}
		}

		public static void Save() {
			Kino.Config.Save<ISettings>();
		}
	}
}

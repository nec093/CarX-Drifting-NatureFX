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
		public bool SyncGameNight { get; set; }            // the game's night lighting (street lights) follows NatureFX's sun
		public float TimeOfDay { get; set; }          // hours 0..24
		public bool TimePasses { get; set; }          // the time of day runs on by itself
		public bool OwnSkyLighting { get; set; }      // HDRP's sky light, reflections and sky from the own engine (OwnLighting)
		public bool OwnSunLight { get; set; }         // HDRP's sun lights nothing; the shadow composite adds it (OwnLighting.Sun)
		public bool OwnLampLight { get; set; }        // HDRP's lamps light nothing; the shadow composite adds them (OwnLighting.Lamps)
		public bool OwnTransparentLight { get; set; } // HDRP's sun, lamps and the own sky light still light what is drawn see-through (OwnLighting.Transparents)
		public float SkyLightStrength { get; set; }   // the own sky's light on surfaces (OwnLighting.Ambient): the shade's brightness
		public float TimeSpeed { get; set; }          // how much faster than real time (60: an hour a minute)
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
		public bool Moonlight { get; set; }
		public float MoonBrightness { get; set; }           // the moon's light against its physical strength (1 = real)
		public float HeadlightShafts { get; set; }
		public int HeadlightType { get; set; }              // 0 the game's own, 1 halogen, 2 HID, 3 LED (a real low beam)
		public int HeadlightStandard { get; set; }          // 0 Japan (driving on the left), 1 Europe / US (on the right): the low beam's side
		public float HeadlightAim { get; set; }             // the low beam's axis below level (°)
		public float HeadlightKelvin { get; set; }          // the lamps' colour temperature (K); 0: the type's own
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
		public bool CarChromeRims { get; set; }            // metal rims polished to a mirror

		// ray traced sun shadows (DX12, DXR); softness from SunSize
		public bool RTShadows { get; set; }
		public float RTShadowStrength { get; set; }
		public bool RTShadowFullRes { get; set; }          // full resolution (sharp contact shadows) or half
		public bool RTExactSun { get; set; }               // take away exactly the sun light HDRP added (G-buffer drawn again)
		public bool RTLampMasks { get; set; }              // roadmap A4: each lamp shadowed by its own mask (with RTExactSun)
		public bool RTFullRelight { get; set; }            // roadmap A4: HDRP's pixel taken apart and lit again by the own engine
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

		// map extension (docs/map-authoring.md 5 and 7): season of the foliage colours and the floating particles
		// (0 automatic: the weather preset, else the map's [defaults] season; 1 spring .. 4 winter; 5 none), the 3D
		// grass (density multiplier, metres from the camera) and the map's decals, water, local fog and particles
		public int Season { get; set; }
		public float GrassDensity { get; set; }
		public float GrassDistance { get; set; }
		public float ShrubDensity { get; set; }            // the map's thickets of scanned shrubs (ShrubRenderer): density, 0 off
		public float ShrubDistance { get; set; }           // m: where the scans give way to their billboard clouds (impostors at 3x)
		// --- added in config version 62: the debug page (Tools/DebugPage.cs) ---
		public bool DebugPage { get; set; }                // a page with memory, timings and the log, for a browser on this PC
		public int DebugPagePort { get; set; }             // its port on 127.0.0.1
		public float TextureStreamingBudget { get; set; }  // MB for Unity's texture mip streaming; 0 the game's own
		public bool WriteMaterialInventory { get; set; }   // the shader rebuild's data at each map load (text and texture samples)
		public bool MapEffects { get; set; }
		// sky: HDRP volumetric clouds following the weather (and the map's cloud sea), the strength of their shadows,
		// the blue haze over distant mountains (0 off)
		public bool Clouds { get; set; }
		public float CloudShadows { get; set; }
		public float AerialHaze { get; set; }
		// the sky drawn by NatureFX's own engine (physically based atmosphere) over HDRP's
		public bool NativeSky { get; set; }
		// the fog and haze drawn by the own engine (lit by the traced sun and the engine's sky) instead of HDRP's
		public bool NativeFog { get; set; }
		// all of the opaque lighting from the own engine (roadmap step A): its shadows, lamp shadows, sky light and GI,
		// reflections, whatever the ray tracing toggles say (Rendering.EngineLighting)
		public bool NativeLighting { get; set; }
		// how bright the own engine's stars are against their physical brightness (the eye's night vision, which the
		// camera's exposure does not reach), and how long a change of weather takes (s)
		public float StarBrightness { get; set; }
		public float WeatherChangeTime { get; set; }
		// falling rain (with splashes) and snow around the camera: how dense (0 off)
		public float Precipitation { get; set; }
		// shader rebuild: hex tiled textures where the map's [materials] ask for it (antitiling=), 0 off .. 1
		public float AntiTiling { get; set; }
		// cars: NatureFX's lit volumetric tyre smoke and spray (amount, 0 off), whether it replaces the game's own smoke
		// on DX11 (on DX12 the game has none), glowing brake discs and afterfire
		public float TireSmoke { get; set; }
		public bool ReplaceGameSmoke { get; set; }
		public bool CarGlow { get; set; }
		// dirt, dust and snow building up on the car nearest the camera (how much at most)
		public bool CarDirt { get; set; }
		public float CarDirtAmount { get; set; }
		// the camera and the eye: night vision (Purkinje shift and grain, 0 off), lens flare (0 off), depth of field on
		// the car nearest the camera
		public float NightVision { get; set; }
		public float LensFlare { get; set; }
		public bool NativePost { get; set; }               // roadmap D: NatureFX's own exposure, bloom, tone curve and colour
		public bool Dlaa { get; set; }                     // NVIDIA DLSS (own engine) in place of HDRP's TAA, with the own post
		public int DlssQuality { get; set; }               // 0 DLAA, 1 Quality, 2 Balanced, 3 Performance, 4 Ultra performance (upscaling)
		public bool DlssRayReconstruction { get; set; }    // DLSS Ray Reconstruction: the sun shadows and reflections denoised by DLSS
		public bool NativeParticles { get; set; }          // roadmap E: the game's own particles drawn with NatureFX/Particle
		public bool NativeGlass { get; set; }              // roadmap E: the own engine draws the glass whole (HDRP's hidden)
		public float PostBloom { get; set; }               // the share of the light the lens and eye scatter (own post)
		public float LensStreaks { get; set; }
		public float MotionBlur { get; set; }              // own post: the shutter's share of a frame (0.5: 180°), 0 off
		public float DofFStop { get; set; }                // own post's depth of field: the lens's f-number
		public bool RTTransparentShadows { get; set; }     // the sun's ray traced shadow on transparent surfaces (SunShadowMap)
		public bool MapGroundDetail { get; set; }          // the ground map: ground blend, painted dirt and debris on the map
		public float Detritus { get; set; }                // debris (pebbles, twigs, leaves) along the road's edges: density, 0 off
		// the map's reflection probes (markers and along the road; off: HDRP's own reflections only)
		public bool MapProbes { get; set; }
		// draw the map's markers over the picture (F8 > Map), for map authors
		public bool ShowMarkers { get; set; }
		public bool DepthOfField { get; set; }

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
		public bool SyncGameNight { get; set; }            // the game's night lighting (street lights) follows NatureFX's sun
		public float TimeOfDay { get; set; }          // hours 0..24
		public bool TimePasses { get; set; }          // the time of day runs on by itself
		public bool OwnSkyLighting { get; set; }      // HDRP's sky light, reflections and sky from the own engine (OwnLighting)
		public bool OwnSunLight { get; set; }         // HDRP's sun lights nothing; the shadow composite adds it (OwnLighting.Sun)
		public bool OwnLampLight { get; set; }        // HDRP's lamps light nothing; the shadow composite adds them (OwnLighting.Lamps)
		public bool OwnTransparentLight { get; set; } // HDRP's sun, lamps and the own sky light still light what is drawn see-through (OwnLighting.Transparents)
		public float SkyLightStrength { get; set; }   // the own sky's light on surfaces (OwnLighting.Ambient): the shade's brightness
		public float TimeSpeed { get; set; }          // how much faster than real time (60: an hour a minute)
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
		public bool Moonlight { get; set; }
		public float MoonBrightness { get; set; }           // the moon's light against its physical strength (1 = real)
		public float HeadlightShafts { get; set; }
		public int HeadlightType { get; set; }              // 0 the game's own, 1 halogen, 2 HID, 3 LED (a real low beam)
		public int HeadlightStandard { get; set; }          // 0 Japan (driving on the left), 1 Europe / US (on the right): the low beam's side
		public float HeadlightAim { get; set; }             // the low beam's axis below level (°)
		public float HeadlightKelvin { get; set; }          // the lamps' colour temperature (K); 0: the type's own
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
		public bool CarChromeRims { get; set; }            // metal rims polished to a mirror

		// ray traced sun shadows (DX12, DXR); softness from SunSize
		public bool RTShadows { get; set; }
		public float RTShadowStrength { get; set; }
		public bool RTShadowFullRes { get; set; }          // full resolution (sharp contact shadows) or half
		public bool RTExactSun { get; set; }               // take away exactly the sun light HDRP added (G-buffer drawn again)
		public bool RTLampMasks { get; set; }              // roadmap A4: each lamp shadowed by its own mask (with RTExactSun)
		public bool RTFullRelight { get; set; }            // roadmap A4: HDRP's pixel taken apart and lit again by the own engine
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

		// map extension (docs/map-authoring.md 5 and 7): season of the foliage colours and the floating particles
		// (0 automatic: the weather preset, else the map's [defaults] season; 1 spring .. 4 winter; 5 none), the 3D
		// grass (density multiplier, metres from the camera) and the map's decals, water, local fog and particles
		public int Season { get; set; }
		public float GrassDensity { get; set; }
		public float GrassDistance { get; set; }
		public float ShrubDensity { get; set; }            // the map's thickets of scanned shrubs (ShrubRenderer): density, 0 off
		public float ShrubDistance { get; set; }           // m: where the scans give way to their billboard clouds (impostors at 3x)
		// --- added in config version 62: the debug page (Tools/DebugPage.cs) ---
		public bool DebugPage { get; set; }                // a page with memory, timings and the log, for a browser on this PC
		public int DebugPagePort { get; set; }             // its port on 127.0.0.1
		public float TextureStreamingBudget { get; set; }  // MB for Unity's texture mip streaming; 0 the game's own
		public bool WriteMaterialInventory { get; set; }   // the shader rebuild's data at each map load (text and texture samples)
		public bool MapEffects { get; set; }
		// sky: HDRP volumetric clouds following the weather (and the map's cloud sea), the strength of their shadows,
		// the blue haze over distant mountains (0 off)
		public bool Clouds { get; set; }
		public float CloudShadows { get; set; }
		public float AerialHaze { get; set; }
		// the sky drawn by NatureFX's own engine (physically based atmosphere) over HDRP's
		public bool NativeSky { get; set; }
		// the fog and haze drawn by the own engine (lit by the traced sun and the engine's sky) instead of HDRP's
		public bool NativeFog { get; set; }
		// all of the opaque lighting from the own engine (roadmap step A): its shadows, lamp shadows, sky light and GI,
		// reflections, whatever the ray tracing toggles say (Rendering.EngineLighting)
		public bool NativeLighting { get; set; }
		// how bright the own engine's stars are against their physical brightness (the eye's night vision, which the
		// camera's exposure does not reach), and how long a change of weather takes (s)
		public float StarBrightness { get; set; }
		public float WeatherChangeTime { get; set; }
		// falling rain (with splashes) and snow around the camera: how dense (0 off)
		public float Precipitation { get; set; }
		// shader rebuild: hex tiled textures where the map's [materials] ask for it (antitiling=), 0 off .. 1
		public float AntiTiling { get; set; }
		// cars: NatureFX's lit volumetric tyre smoke and spray (amount, 0 off), whether it replaces the game's own smoke
		// on DX11 (on DX12 the game has none), glowing brake discs and afterfire
		public float TireSmoke { get; set; }
		public bool ReplaceGameSmoke { get; set; }
		public bool CarGlow { get; set; }
		// dirt, dust and snow building up on the car nearest the camera (how much at most)
		public bool CarDirt { get; set; }
		public float CarDirtAmount { get; set; }
		// the camera and the eye: night vision (Purkinje shift and grain, 0 off), lens flare (0 off), depth of field on
		// the car nearest the camera
		public float NightVision { get; set; }
		public float LensFlare { get; set; }
		public bool NativePost { get; set; }               // roadmap D: NatureFX's own exposure, bloom, tone curve and colour
		public bool Dlaa { get; set; }                     // NVIDIA DLSS (own engine) in place of HDRP's TAA, with the own post
		public int DlssQuality { get; set; }               // 0 DLAA, 1 Quality, 2 Balanced, 3 Performance, 4 Ultra performance (upscaling)
		public bool DlssRayReconstruction { get; set; }    // DLSS Ray Reconstruction: the sun shadows and reflections denoised by DLSS
		public bool NativeParticles { get; set; }          // roadmap E: the game's own particles drawn with NatureFX/Particle
		public bool NativeGlass { get; set; }              // roadmap E: the own engine draws the glass whole (HDRP's hidden)
		public float PostBloom { get; set; }               // the share of the light the lens and eye scatter (own post)
		public float LensStreaks { get; set; }
		public float MotionBlur { get; set; }              // own post: the shutter's share of a frame (0.5: 180°), 0 off
		public float DofFStop { get; set; }                // own post's depth of field: the lens's f-number
		public bool RTTransparentShadows { get; set; }     // the sun's ray traced shadow on transparent surfaces (SunShadowMap)
		public bool MapGroundDetail { get; set; }          // the ground map: ground blend, painted dirt and debris on the map
		public float Detritus { get; set; }                // debris (pebbles, twigs, leaves) along the road's edges: density, 0 off
		// the map's reflection probes (markers and along the road; off: HDRP's own reflections only)
		public bool MapProbes { get; set; }
		// draw the map's markers over the picture (F8 > Map), for map authors
		public bool ShowMarkers { get; set; }
		public bool DepthOfField { get; set; }

	}

	public static class Settings {
		public static ISettings Data { get; private set; }

		// Values of a fresh install (first run plus every migration); F8 resets single values to these.
		public static ISettings Defaults { get; private set; }

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
				d.RTBackend = 0;
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
			if (d.ConfigVersion < 34) {
				d.GrassDensity = 1.0f;
				d.GrassDistance = 60.0f;
				d.MapEffects = true;
				d.ConfigVersion = 34;
			}
			if (d.ConfigVersion < 35) {
				d.Clouds = true;
				d.CloudShadows = 0.7f;
				d.AerialHaze = 0.4f;
				d.ConfigVersion = 35;
			}
			if (d.ConfigVersion < 36) {
				d.TireSmoke = 1.0f;
				d.CarGlow = true;
				d.ConfigVersion = 36;
			}
			if (d.ConfigVersion < 37) {
				d.CarDirt = true;
				d.CarDirtAmount = 1.0f;
				d.ConfigVersion = 37;
			}
			if (d.ConfigVersion < 38) {
				d.NightVision = 0.8f;
				d.LensFlare = 0.5f;
				d.ConfigVersion = 38;
			}
			if (d.ConfigVersion < 39) {
				// the first lens flare was far too strong
				d.LensFlare = 0.3f;
				d.LensStreaks = 0.3f;
				d.MapProbes = true;
				d.ConfigVersion = 39;
			}
			if (d.ConfigVersion < 40) {
				d.Precipitation = 1.0f;
				d.ConfigVersion = 40;
			}
			if (d.ConfigVersion < 41) {
				d.AntiTiling = 1.0f;
				d.ConfigVersion = 41;
			}
			if (d.ConfigVersion < 42) {
				d.NativeSky = true;
				d.ConfigVersion = 42;
			}
			if (d.ConfigVersion < 43) {
				d.NativeFog = true;
				d.ConfigVersion = 43;
			}
			if (d.ConfigVersion < 44) {
				d.NativeLighting = true;
				d.ConfigVersion = 44;
			}
			if (d.ConfigVersion < 45) {
				// the own engine's nights are as dark as moonlight: 8 EV kept them black (only an untouched default moves)
				if (System.Math.Abs(d.AutoExposureMin - 8.0f) < 1e-3f) {
					d.AutoExposureMin = 1.0f;
				}
				d.StarBrightness = 4.0f;
				d.WeatherChangeTime = 30.0f;
				d.ConfigVersion = 45;
			}
			if (d.ConfigVersion < 46) {
				// each lamp shadowed by its own mask: its shine too (a lamp's reflection on a wet road showed through walls)
				d.RTLampMasks = true;
				d.ConfigVersion = 46;
			}
			if (d.ConfigVersion < 47) {
				// the full relight (roadmap A4)
				d.RTFullRelight = true;
				d.ConfigVersion = 47;
			}
			if (d.ConfigVersion < 48) {
				// the own post-processing (roadmap D)
				d.NativePost = true;
				d.PostBloom = 0.04f;
				d.ConfigVersion = 48;
			}
			if (d.ConfigVersion < 49) {
				// the game's own particles lit and fogged by the own engine (roadmap E)
				d.NativeParticles = true;
				d.ConfigVersion = 49;
			}
			if (d.ConfigVersion < 50) {
				// the own engine draws the glass (roadmap E)
				d.NativeGlass = true;
				d.ConfigVersion = 50;
			}
			if (d.ConfigVersion < 51) {
				// the game's street lights at NatureFX's night
				d.SyncGameNight = true;
				d.ConfigVersion = 51;
			}
			if (d.ConfigVersion < 52) {
				// a moonlit night a little brighter than the real one by default (user: adjustable)
				d.MoonBrightness = 1.5f;
				d.ConfigVersion = 52;
			}
			if (d.ConfigVersion < 53) {
				// a real low beam (the game's round spot was too bright and lit everything above the road)
				d.HeadlightType = 3;
				d.ConfigVersion = 53;
			}
			if (d.ConfigVersion < 54) {
				// the low beam aimed by the car's body, 0.6° down (the regulations' 1 %)
				d.HeadlightAim = 0.6f;
				d.ConfigVersion = 54;
			}
			if (d.ConfigVersion < 55) {
				// the eye adaptation's limits are real-world EVs now (Units): a moonlit night at about -2, a sunny day about 15
				d.AutoExposureMin = -2.0f;
				d.AutoExposureMax = 16.0f;
				d.ConfigVersion = 55;
			}
			if (d.ConfigVersion < 56) {
				// the moon's strength from 0.001 to 0.1 (user): 0.1 about a real full moon, now that the sun is real (Units)
				d.MoonBrightness = 0.1f;
				d.ConfigVersion = 56;
			}
			if (d.ConfigVersion < 57) {
				// DLAA in place of TAA (falls back to TAA without an RTX GPU)
				d.Dlaa = true;
				d.ConfigVersion = 57;
			}
			if (d.ConfigVersion < 58) {
				// motion blur and depth of field in the own post-processing (HDRP's off with it)
				d.MotionBlur = 0.5f;
				d.DofFStop = 2.8f;
				d.ConfigVersion = 58;
			}
			if (d.ConfigVersion < 59) {
				// the sun's ray traced shadow on transparent surfaces
				d.RTTransparentShadows = true;
				d.ConfigVersion = 59;
			}
			if (d.ConfigVersion < 60) {
				// the ground map's details: ground blend, painted dirt, debris
				d.MapGroundDetail = true;
				d.Detritus = 1.0f;
				d.ConfigVersion = 60;
			}
			if (d.ConfigVersion < 61) {
				// thickets of scanned shrubs
				d.ShrubDensity = 1.0f;
				d.ShrubDistance = 25.0f;
				d.ConfigVersion = 61;
			}
			if (d.ConfigVersion < 62) {
				// the debug page: off, its port
				d.DebugPagePort = 8765;
				d.ConfigVersion = 62;
			}
			if (d.ConfigVersion < 63) {
				// the time of day running on: off, an hour a minute
				d.TimeSpeed = 60.0f;
				d.ConfigVersion = 63;
			}
			if (d.ConfigVersion < 64) {
				// HDRP's sky light, reflections and sky handed over to the own engine
				d.OwnSkyLighting = true;
				d.ConfigVersion = 64;
			}
			if (d.ConfigVersion < 65) {
				// the sun's light from the own engine
				d.OwnSunLight = true;
				d.ConfigVersion = 65;
			}
			if (d.ConfigVersion < 66) {
				// the lamps' light from the own engine
				d.OwnLampLight = true;
				d.ConfigVersion = 66;
			}
			if (d.ConfigVersion < 67) {
				// the shade's brightness (the own sky's light), as it is
				d.SkyLightStrength = 1.0f;
				d.ConfigVersion = 67;
			}
			if (d.ConfigVersion < 68) {
				// the low beam for driving on the left (Japan), as it was
				d.HeadlightStandard = 0;
				d.ConfigVersion = 68;
			}
			if (d.ConfigVersion < 69) {
				// see-through surfaces lit again under the own engine's lighting
				d.OwnTransparentLight = true;
				d.ConfigVersion = 69;
			}
		}

		public static void Save() {
			Kino.Config.Save<ISettings>();
		}
	}
}

using System.Collections;
using System.IO;
using System.Reflection;
using NatureFX.Map;
using NatureFX.Nature;
using NatureFX.Rendering;
using KSL.API;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NatureFX {
	[KSLMeta("NatureFX", "1.0.0", "nec093")]
	public class NatureFXMod : BaseMod {
		private const float RescanInterval = 10.0f;
		private static readonly int VertexColorsId = Shader.PropertyToID("_NFX_VertexColors");

		internal readonly VolumeOverhaul Volume = new();
		internal readonly WindController Wind = new();
		internal readonly AccumulationController Accumulation = new();
		internal readonly ShaderBundle Bundle = new();
		internal readonly MaterialReplacer Replacer = new();
		internal readonly SunController Sun = new();
		internal readonly RayTracingEffects RT = new();
		internal readonly CarFinish Finish = new();
		internal readonly FakeShadows PaintedShadows = new();
		internal readonly NatureFX.Tools.Identify Identify = new();
		private float nextFinishRebuild_;
		internal readonly WeatherController WeatherCtl = new();
		internal readonly HeadlightController Headlights = new();
		internal readonly TrackMap Tracks = new();
		internal readonly CarRain CarRainFx = new();
		internal readonly MapProfile Map = new();
		internal readonly MapPaint Paint = new();
		internal readonly Detritus Debris = new();
		internal readonly ShrubRenderer Shrubs = new();
		internal readonly MarkerScanner Markers = new();
		internal readonly MaterialProfiles MaterialLooks = new();
		internal readonly ExposureProbe Exposure = new();
		internal readonly MapDefaults MapWeather = new();
		internal readonly WeatherZones Zones = new();
		internal readonly MapFog Fog = new();
		internal readonly MapLights Lights = new();
		internal readonly MapProbes Probes = new();
		internal readonly Rendering.SceneProbes TrackProbes = new();
		internal readonly GrassRenderer Grass = new();
		internal readonly MapDecals Decals = new();
		internal readonly MapWater Water = new();
		internal readonly MapVolumes Volumes = new();
		internal readonly MapParticles Particles = new();
		internal readonly CloudController Clouds = new();
		internal readonly MapNight Night = new();
		internal readonly ForestRenderer Forest = new();
		internal readonly TireSmoke Smoke = new();
		internal readonly Precipitation Rain = new();
		internal readonly CarGlow Glow = new();
		internal readonly CameraVolumetrics CameraFog = new(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Volumetrics, "the local fog and the tyre smoke");
		internal readonly CameraVolumetrics CameraClouds = new(UnityEngine.Rendering.HighDefinition.FrameSettingsField.VolumetricClouds, "the sky's clouds");
		internal readonly LensEffects Lens = new();
		internal readonly PostProcess Post = new();
		internal readonly GameParticles GameFx = new();
		internal readonly GameDayNight GameNight = new();

		private SettingsUI ui_;
		private NatureFXWindow window_;
		private bool dirty_ = true;
		private float nextRescan_;
		private Coroutine pendingRescan_;
		private float nextVolumeRefresh_;
		private float pendingShrubDensity_ = float.NaN, shrubReloadAt_;

		private void Start() {
			if (!Settings.Init()) {
				Kino.Log.Error("[NatureFX] Config registration failed");
				return;
			}

			Capabilities.Refresh();
			Kino.Log.Info($"[NatureFX] {Capabilities.Describe()}");
			if (!Capabilities.IsHdrp) {
				Kino.Log.Error("[NatureFX] HDRP not detected, lighting overhaul disabled");
			}

			ui_ = new SettingsUI(this);
			window_ = new NatureFXWindow(this);

			ShaderGlobals.EnsureNoiseTexture(); // the rebuilt shaders' noise (NFX_Common.hlsl)
			Volume.Create();
			Fog.Create();
			Clouds.Create();
			Night.Create();
			Smoke.Create();
			Glow.Create();
			// The bundle also carries the ray tracing, night vision and photo shaders, so load it even when shader
			// replacement is off, and before anything looks its shaders up (Lens / PhotoMode found none otherwise)
			Bundle.Load();
			Lens.Create(Bundle);
			Bundle.Reloaded += () => Lens.Reload(Bundle);
			Post.Create(Bundle);
			Bundle.Reloaded += () => Post.Reload(Bundle);
			GameFx.Create(Bundle);
			OwnGlass.Create(Bundle);
			Bundle.Reloaded += () => OwnGlass.Create(Bundle);
			Bundle.Reloaded += () => GameFx.Reload(Bundle);
			PhotoMode.Create(Bundle);
			Bundle.Reloaded += () => PhotoMode.Reload(Bundle);
			Wind.Create();
			Replacer.Profiles = MaterialLooks;
			Replacer.LoadRules();
			Dx12Fix.Apply(Bundle);
			GpuCullingDx12.Install(Bundle);
			TireSmokeDx12.Install();
			RT.Create(Bundle);
			Tracks.Create(Bundle);
			CarRainFx.Create(Bundle);
			Rain.Create(Bundle);
			Smoke.CreateParticles(Bundle);
			Bundle.Reloaded += () => { Rain.Destroy(); Rain.Create(Bundle); Particles.Reload(Bundle); Smoke.DestroyParticles(); Smoke.CreateParticles(Bundle); };

			SceneManager.sceneLoaded += OnSceneLoaded;
			NatureFX.Tools.ShaderVariantRecorder.Recording = Settings.Data.RecordShaderVariants;
			NatureFX.Tools.HdrpSettingsDump.Write();
			Kino.Input.Bind(KeyCode.F8, () => window_.Toggle(), "NatureFX window");
			Kino.Input.Bind(KeyCode.F7, () => Headlights.ToggleHighBeam(), "NatureFX high beam");
			Kino.Input.Bind(KeyCode.F9, () => {
				PhotoMode.Toggle();
				MarkDirty(); // the ray traced effects take the photo quality (RayTracingEffects.Apply)
				RT.ResetHistory(); // their history textures change size with it
			}, "NatureFX photo mode");

			ScheduleRescan(1.0f);
		}

		private void OnDestroy() {
			if (Settings.Data != null && Settings.Data.RecordShaderVariants) {
				NatureFX.Tools.ShaderVariantRecorder.Save();
			}
			SceneManager.sceneLoaded -= OnSceneLoaded;
			NatureFX.Tools.DebugPage.Stop();
			window_?.Destroy();
			Replacer.RestoreAll();
			Markers.Restore();
			Accumulation.RestoreAll();
			Sun.Restore();
			RT.Destroy();
			Finish.RestoreAll();
			PaintedShadows.ShowAll();
			Headlights.Restore();
			Headlights.DestroyCookies();
			Tracks.Destroy();
			CarRainFx.Destroy();
			Bundle.Unload();
			Wind.Destroy();
			Fog.Destroy();
			Clouds.Destroy();
			Night.Destroy();
			Forest.Destroy();
			Shrubs.Destroy();
			Smoke.Destroy();
			Rain.Destroy();
			Glow.Destroy();
			Lens.Destroy();
			Post.Destroy();
			GameFx.Restore();
			OwnGlass.Restore();
			PhotoMode.Destroy();
			Lights.Destroy();
			Probes.Destroy();
			TrackProbes.Restore();
			Grass.Destroy();
			Decals.Destroy();
			Paint.Destroy();
			Debris.Destroy();
			Water.Destroy();
			Volumes.Destroy();
			Particles.Destroy();
			Volume.Destroy();
			CameraFog.Restore();
			CameraClouds.Restore();
			GroundMap.Release();
			ShaderGlobals.DestroyNoiseTexture();
		}

		private void Update() {
			var s = Settings.Data;
			if (s == null) {
				return;
			}
			// the debug page (a browser on this PC): whether NatureFX is on or not
			NatureFX.Tools.DebugPage.Update(this, s.DebugPage, s.DebugPagePort);
			NatureFX.Tools.CpuTimings.Active = NatureFX.Tools.DebugPage.Running;
			NatureFX.Tools.CpuTimings.BeginFrame();
			TextureBudget.Apply(s.TextureStreamingBudget);
			Replacer.Resync();

			// the map's [defaults] and weather zones at the camera, before the weather and wind read them
			var cam = Camera.main;
			var camPos = cam != null ? cam.transform.position : Vector3.zero;
			NatureFX.Tools.CpuTimings.Section("weather");
			MapWeather.Update(s, Zones, camPos, s.UseMapProfile && Map.Loaded);
			WeatherCtl.Update(s);
			Seasons.Update(s, WeatherCtl.ActivePreset, s.UseMapProfile && Map.Loaded ? MapWeather.Season : null, s.Enabled);
			// roadmap C: with the own engine's fog the local fog and the tyre smoke are its boxes (AtmospherePass), not
			// HDRP's volumetric fog
			bool nativeFog = s.NativeFog && s.NativeSky;
			bool hdrpVolumetric = !nativeFog && (Volumes.AnyVisible || Smoke.AnyVisible);
			NatureFX.Tools.CpuTimings.Section("fog and clouds");
			Fog.Update(s.Enabled, camPos, hdrpVolumetric, s.AerialHaze, nativeFog);
			CameraFog.Update(cam, s.Enabled && hdrpVolumetric);
			CameraClouds.Update(cam, s.Enabled && s.Clouds && !s.NativeSky);
			OwnLighting.Update(s.Enabled && s.NativeSky && s.OwnSkyLighting && NativeEngine.Active);
			Volume.UpdateSky(Sun.Sun, s.Enabled && s.SunOverride, s.Enabled && s.NativeSky);
			Weather.UpdateIce(s.Enabled && s.Accumulation, Sun.Sun);
			PhotoMode.Update(cam);
			Clouds.Update(s.Enabled, s, Sun.Sun, s.UseMapProfile && Map.Loaded && MapValues.TryFloat(Map.Get("clouds", "sea"), out float sea) ? sea : float.NaN);

			// weather blends fog, clouds and sun over time, so refresh the volume a few times per second
			NatureFX.Tools.CpuTimings.Section("volumes and RT settings");
			if (Time.unscaledTime >= nextVolumeRefresh_) {
				nextVolumeRefresh_ = Time.unscaledTime + 0.25f;
				dirty_ = true;
			}

			if (dirty_) {
				dirty_ = false;
				if (s.Enabled) {
					Volume.Apply(s);
				} else {
					Volume.Disable();
				}
				RT.Apply(s);
			}

			// the rebuilt shaders' road surface (grain, wear, tyre rubber)
			bool rebuiltSurface = s.Enabled && s.ReplaceShaders;
			Wind.DrawnSway = Map.FoliageWind;
			NatureFX.Tools.CpuTimings.Section("wind, build-up, sun");
			Wind.Update(s);
			Accumulation.Update(s);
			Accumulation.UpdateRoad(rebuiltSurface ? s.RoadWear : 0.0f);
			// the time of day running on (F8 > Sun & time); saved with the rest when the window closes
			if (s.Enabled && s.SunOverride && s.TimePasses && s.TimeSpeed > 0.0f) {
				float hours = Mathf.Min(Time.unscaledDeltaTime, 0.1f) * s.TimeSpeed / 3600.0f;
				s.TimeOfDay = Mathf.Repeat(s.TimeOfDay + hours, 24.0f);
			}
			Sun.Update(s);
			// the game's own night lighting (its street lights) at NatureFX's night
			NatureFX.Tools.CpuTimings.Section("day / night, headlights, lights");
			GameNight.Update(s.Enabled && s.SunOverride && s.SyncGameNight, Sun);
			Headlights.Update(s, s.Enabled && s.UseMapProfile);
			bool mapOn = s.Enabled && s.UseMapProfile;
			Lights.Update(mapOn, Sun.Sun, camPos, s.HeadlightShafts, RT.LampShadowsTraced);
			// with the own engine's sky HDRP's sky reflection is that sky (A3) and the traced reflections show what is near:
			// the probes, rendered by HDRP without the engine's sky, fog and light, kept a stale day sky into the night
			NatureFX.Tools.CpuTimings.Section("probes");
			Probes.Update(mapOn && s.MapProbes && !s.NativeSky, Sun.Sun, camPos);
			// and the track's own probes, rendered once at load under whatever sky HDRP had then
			TrackProbes.Update(s.Enabled && s.NativeSky);
			// the 3D grass belongs to the shader rebuild (it draws with NatureFX/Foliage), the rest to map effects
			NatureFX.Tools.CpuTimings.Section("grass, forest, shrubs");
			Grass.Update(mapOn && rebuiltSurface, s, camPos);
			Forest.Update(mapOn, camPos);
			// the thickets: planted again when the density changes; they draw with NatureFX/Foliage like the 3D grass
			if (Shrubs.NeedsReload(s.ShrubDensity)) {
				// once the slider rests: every frame of a drag replanted the thickets (materials, meshes, the ray tracing
				// scene) and stuttered
				if (!Mathf.Approximately(s.ShrubDensity, pendingShrubDensity_)) {
					pendingShrubDensity_ = s.ShrubDensity;
					shrubReloadAt_ = Time.unscaledTime + 0.5f;
				} else if (Time.unscaledTime >= shrubReloadAt_) {
					Shrubs.Load(Markers, Bundle, Replacer, s.ShrubDensity);
				}
			}
			Shrubs.Update(mapOn && rebuiltSurface, cam, s.ShrubDistance);
			bool effects = mapOn && s.MapEffects;
			NatureFX.Tools.CpuTimings.Section("decals, paint, debris, water, volumes, particles");
			Decals.Update(effects, camPos);
			// the ground map's painted dirt and debris (the ground blend itself is RT's)
			Paint.Update(effects && s.MapGroundDetail, camPos);
			Debris.Update(effects && s.MapGroundDetail, s.Detritus, camPos);
			Water.Update(effects, camPos);
			Volumes.Update(effects, s, Sun.Sun, camPos);
			Particles.Update(effects, Sun.Sun, camPos, Lights.LitPositions);
			NatureFX.Tools.CpuTimings.Section("night, tyre marks, car rain");
			Night.Update(s.Enabled, Sun.Sun);
			Tracks.Update(s);
			CarRainFx.Update(s, Sun.Sun);
			TireSmokeDx12.ReplaceGameSmoke = s.Enabled && s.TireSmoke > 0.0f && s.ReplaceGameSmoke;
			NatureFX.Tools.CpuTimings.Section("tyre smoke");
			Smoke.Update(s.Enabled, s.TireSmoke);
			NatureFX.Tools.CpuTimings.Section("particle lighting, rain, glow, lens, post");
			ParticleLighting.Update(cam);
			Rain.Update(s, cam);
			Glow.Update(s.Enabled && s.CarGlow, camPos);
			Lens.Update(s.Enabled, s, Sun.Sun, camPos);
			Post.Update(s.Enabled, s);
			NatureFX.Tools.CpuTimings.Section("game particles, painted shadows, finish, ray tracing");
			GameFx.Update(s.Enabled && s.NativeParticles);
			MaterialProfiles.UpdateGlobals(Sun.Sun);
			RT.UpdateRayTracing(Sun.Sun);
			// the game's painted shadow planes go while the ray traced sun shadows run
			PaintedShadows.Update(s.Enabled && Rendering.EngineLighting.Shadows(s) && s.RTHideFakeShadows && RT.SunShadowsRunning);
			Finish.ChromeRims = s.CarChromeRims;
			// the ray tracing scene takes the rims' new finish (renderer blocks are read when instances are added)
			if (Finish.Update(s.Enabled) && Time.unscaledTime >= nextFinishRebuild_) {
				nextFinishRebuild_ = Time.unscaledTime + 5.0f;
				RT.RequestSceneRebuild();
			}
			if (window_ != null && window_.Visible) {
				Exposure.Update();
			}
			NatureFX.Tools.CpuTimings.Section("F8 window and rescans");
			window_?.Update();

			// Tracks stream objects in, so pick up new materials periodically.
			if (Time.unscaledTime >= nextRescan_) {
				nextRescan_ = Time.unscaledTime + RescanInterval;
				if (s.Enabled && s.ReplaceShaders) {
					ApplyReplacement();
				}
			}
			NatureFX.Tools.CpuTimings.EndSection();
		}

		private void OnGUI() {
			if (Settings.Data == null) {
				return;
			}
			Identify.OriginalOf ??= Replacer.OriginalOf;
			Identify.OnGUI();
			PhotoMode.OnGUI();
			if (Settings.Data.ShowMarkers && Settings.Data.UseMapProfile) {
				MarkerOverlay.Draw(Camera.main, Markers.Markers, m => m.Kind switch {
					"zone" => Zones.StatusOf(m),
					"volume" => Volumes.StatusOf(m),
					"fx" => Particles.StatusOf(m),
					"light" => Lights.StatusOf(m),
					_ => null
				});
			}
			window_?.OnGUI();
		}

		public override void OnUIDraw() {
			ui_?.Draw();
		}

		public override void OnAdditionalAboutUIDraw() {
			Kino.UI.Label("HDRP lighting overhaul, foliage wind, snow / leaves / dust / wetness accumulation and ray tracing for CarX Drift Racing Online.");
			Kino.UI.Label(Capabilities.Describe());
		}

		public override Texture2D GetIcon() {
			var assembly = Assembly.GetExecutingAssembly();
			return assembly.GetManifestResourceInfo("NatureFX.Resources.icon.png") != null
				? Kino.Utils.LoadEmbeddedTexture(assembly, "NatureFX.Resources.icon.png")
				: null;
		}

		internal void MarkDirty() {
			dirty_ = true;
		}

		internal void SetShaderReplacement(bool enabled) {
			if (enabled) {
				Bundle.Load();
				ApplyReplacement();
			} else {
				Replacer.RestoreAll();
				RT.RequestSceneRebuild();
			}
		}

		// Shader rebuild: switch what the rules and the generic catch-all cover; materials that changed shader enter
		// the ray tracing scene again as themselves
		private void ApplyReplacement() {
			Replacer.Full = Settings.Data.FullReplacement;
			Replacer.RebuildFoliage = Settings.Data.RebuildFoliage;
			Replacer.Apply(Bundle);
			if (Replacer.LastReplaced > 0) {
				RT.RequestSceneRebuild();
			}
		}

		internal void ReloadRules() {
			Replacer.RestoreAll();
			Replacer.LoadRules(Map);
			if (Settings.Data.ReplaceShaders) {
				ApplyReplacement();
			}
		}

		// F8 > Map: re-read the map profile (authors edit it while the map is loaded)
		internal void ReloadMap() {
			RefreshMap(true);
			if (Settings.Data.Enabled && Settings.Data.ReplaceShaders) {
				ApplyReplacement();
			}
		}

		// Loads the profile of the current map; a different profile re-applies every material.
		private void RefreshMap(bool force) {
			bool changed = Map.Load(Settings.Data.UseMapProfile);
			if (changed || force) {
				Replacer.RestoreAll();
				MaterialLooks.Load(Map);
				Replacer.LoadRules(Map);
			}
			Markers.Scan(Map);
			Fog.Invalidate(); // the new track's own fog is read again (MapFog)
			// a new map takes the user's sliders as they are now as the point where the map's defaults apply
			if (changed || force) {
				MapWeather.Load(Map, Settings.Data);
			}
			Shader.SetGlobalFloat(VertexColorsId, Map.Loaded && Map.VertexColors ? 1.0f : 0.0f);
			Zones.Load(Map, Markers);
			Lights.Load(Markers);
			Probes.Load(Map, Markers);
			Grass.Load(Map, Markers, Bundle, Settings.Data.GrassDensity);
			Decals.Load(Markers);
			Paint.Load(Map);
			Debris.Create(Bundle);
			Water.Load(Markers, Bundle);
			Volumes.Load(Markers);
			Particles.Load(Markers, Bundle);
			Night.Load(Map, Bundle);
			Forest.Load(Markers, Bundle);
			Shrubs.Load(Markers, Bundle, Replacer, Settings.Data.ShrubDensity);
		}

		internal bool BundleFileExists() {
			return File.Exists(Bundle.BundlePath);
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
			NatureFX.Tools.DebugPage.MarkStep($"scene loaded: {scene.name} ({mode})");
			if (mode == LoadSceneMode.Single) {
				newMap_ = true; // the own engine starts its scene over at the rescan (not for the small additive scenes)
				NatureFX.Tools.MapDataProbe.Reset();
				Shrubs.Unload(); // they draw the old map's meshes, destroyed with its scene
			}
			// a track loads several scenes in a row (2 s for them all); the game's day / night lighting is one small scene
			ScheduleRescan(mode == LoadSceneMode.Additive && GameDayNight.Switching ? 0.3f : 2.0f);
		}

		// Tracks load several additive scenes in a row; restart the timer so they share one rescan.
		private bool newMap_;

		private void ScheduleRescan(float delay) {
			if (pendingRescan_ != null) {
				StopCoroutine(pendingRescan_);
			}
			pendingRescan_ = StartCoroutine(RescanAfterDelay(delay));
		}

		private IEnumerator RescanAfterDelay(float delay) {
			yield return new WaitForSecondsRealtime(delay);
			pendingRescan_ = null;

			NatureFX.Tools.DebugPage.MarkStep("rescan: start");
			Capabilities.Refresh();
			// what the bundle build may leave out (render path, lightmap modes, LOD cross fade): facts of this map
			NatureFX.Tools.VariantProfileDump.Write(Camera.main);
			Sun.Refresh();
			NatureFX.Tools.DebugPage.MarkStep("rescan: sun");
			RT.PrepareSceneForRayTracing(newMap_);
			newMap_ = false;
			NatureFX.Tools.DebugPage.MarkStep("rescan: ray tracing scene built");
			Accumulation.CollectSceneMaterials();
			NatureFX.Tools.DebugPage.MarkStep("rescan: build-up materials");
			RefreshMap(false);
			NatureFX.Tools.DebugPage.MarkStep("rescan: map (grass, forest, shrubs, markers)");
			NatureFX.Tools.MapDataProbe.Run();
			if (Settings.Data.RecordShaderVariants) {
				NatureFX.Tools.ShaderVariantRecorder.Save(); // variants used by the previous map
			}
			// the game's materials as they are, before any switch: data for the translators (stages 1 to 4)
			Replacer.Full = Settings.Data.FullReplacement;
			// only on request (F8 > Developer): it read every material's textures back from the GPU and wrote them as
			// PNG at each map load (some 300 to 500 MB more video memory for that frame, and a hitch)
			if (Settings.Data.WriteMaterialInventory) {
				NatureFX.Tools.MaterialInventory.Write(Replacer);
			}
			NatureFX.Tools.DebugPage.MarkStep("rescan: material inventory");
			if (Settings.Data.Enabled && Settings.Data.ReplaceShaders) {
				ApplyReplacement();
				NatureFX.Tools.SceneShaderDumper.MergeVariants(Replacer.UsedVariants);
				NatureFX.Tools.DebugPage.MarkStep("rescan: game shaders replaced");
			}
			MarkDirty();
			// what the next frames add (shaders compiled for their first draw, passes making their targets)
			yield return null;
			NatureFX.Tools.DebugPage.MarkStep("rescan: +1 frame");
			yield return new WaitForSecondsRealtime(1.0f);
			NatureFX.Tools.DebugPage.MarkStep("rescan: +1 s");
			yield return new WaitForSecondsRealtime(4.0f);
			NatureFX.Tools.DebugPage.MarkStep("rescan: +5 s");
		}
	}
}

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
		internal readonly MarkerScanner Markers = new();
		internal readonly MaterialProfiles MaterialLooks = new();
		internal readonly ExposureProbe Exposure = new();
		internal readonly MapDefaults MapWeather = new();
		internal readonly WeatherZones Zones = new();
		internal readonly MapFog Fog = new();
		internal readonly MapLights Lights = new();
		internal readonly MapProbes Probes = new();
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

		private SettingsUI ui_;
		private NatureFXWindow window_;
		private bool dirty_ = true;
		private float nextRescan_;
		private Coroutine pendingRescan_;
		private float nextVolumeRefresh_;

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
			Bundle.Reloaded += () => { Rain.Destroy(); Rain.Create(Bundle); Particles.Reload(Bundle); };

			SceneManager.sceneLoaded += OnSceneLoaded;
			NatureFX.Tools.ShaderVariantRecorder.Recording = Settings.Data.RecordShaderVariants;
			NatureFX.Tools.HdrpSettingsDump.Write();
			Kino.Input.Bind(KeyCode.F8, () => window_.Toggle(), "NatureFX window");
			Kino.Input.Bind(KeyCode.F9, () => {
				PhotoMode.Toggle();
				MarkDirty(); // the ray traced effects take the photo quality (RayTracingEffects.Apply)
			}, "NatureFX photo mode");

			ScheduleRescan(1.0f);
		}

		private void OnDestroy() {
			if (Settings.Data != null && Settings.Data.RecordShaderVariants) {
				NatureFX.Tools.ShaderVariantRecorder.Save();
			}
			SceneManager.sceneLoaded -= OnSceneLoaded;
			window_?.Destroy();
			Replacer.RestoreAll();
			Markers.Restore();
			Accumulation.RestoreAll();
			Sun.Restore();
			RT.Destroy();
			Finish.RestoreAll();
			PaintedShadows.ShowAll();
			Headlights.Restore();
			Tracks.Destroy();
			CarRainFx.Destroy();
			Bundle.Unload();
			Wind.Destroy();
			Fog.Destroy();
			Clouds.Destroy();
			Night.Destroy();
			Forest.Destroy();
			Smoke.Destroy();
			Rain.Destroy();
			Glow.Destroy();
			Lens.Destroy();
			PhotoMode.Destroy();
			Lights.Destroy();
			Probes.Destroy();
			Grass.Destroy();
			Decals.Destroy();
			Water.Destroy();
			Volumes.Destroy();
			Particles.Destroy();
			Volume.Destroy();
			CameraFog.Restore();
			CameraClouds.Restore();
			ShaderGlobals.DestroyNoiseTexture();
		}

		private void Update() {
			var s = Settings.Data;
			if (s == null) {
				return;
			}

			// the map's [defaults] and weather zones at the camera, before the weather and wind read them
			var cam = Camera.main;
			var camPos = cam != null ? cam.transform.position : Vector3.zero;
			MapWeather.Update(s, Zones, camPos, s.UseMapProfile && Map.Loaded);
			WeatherCtl.Update(s);
			Seasons.Update(s, WeatherCtl.ActivePreset, s.UseMapProfile && Map.Loaded ? MapWeather.Season : null, s.Enabled);
			Fog.Update(s.Enabled, camPos, Volumes.AnyVisible || Smoke.AnyVisible, s.AerialHaze);
			CameraFog.Update(cam, s.Enabled && (Volumes.AnyVisible || Smoke.AnyVisible));
			CameraClouds.Update(cam, s.Enabled && s.Clouds && !s.NativeSky);
			Volume.UpdateSky(Sun.Sun, s.Enabled && s.SunOverride);
			Weather.UpdateIce(s.Enabled && s.Accumulation, Sun.Sun);
			PhotoMode.Update(cam);
			Clouds.Update(s.Enabled, s, Sun.Sun, s.UseMapProfile && Map.Loaded && MapValues.TryFloat(Map.Get("clouds", "sea"), out float sea) ? sea : float.NaN);

			// weather blends fog, clouds and sun over time, so refresh the volume a few times per second
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
			Wind.Update(s);
			Accumulation.Update(s);
			Accumulation.UpdateRoad(rebuiltSurface ? s.RoadWear : 0.0f);
			Sun.Update(s);
			Headlights.Update(s, s.Enabled && s.UseMapProfile);
			bool mapOn = s.Enabled && s.UseMapProfile;
			Lights.Update(mapOn, Sun.Sun, camPos, s.HeadlightShafts, RT.LampShadowsTraced);
			Probes.Update(mapOn && s.MapProbes, Sun.Sun, camPos);
			// the 3D grass belongs to the shader rebuild (it draws with NatureFX/Foliage), the rest to map effects
			Grass.Update(mapOn && rebuiltSurface, s, camPos);
			Forest.Update(mapOn, camPos);
			bool effects = mapOn && s.MapEffects;
			Decals.Update(effects, camPos);
			Water.Update(effects, camPos);
			Volumes.Update(effects, s, Sun.Sun, camPos);
			Particles.Update(effects, Sun.Sun, camPos, Lights.LitPositions);
			Night.Update(s.Enabled, Sun.Sun);
			Tracks.Update(s);
			CarRainFx.Update(s, Sun.Sun);
			TireSmokeDx12.ReplaceGameSmoke = s.Enabled && s.TireSmoke > 0.0f && s.ReplaceGameSmoke;
			Smoke.Update(s.Enabled, s.TireSmoke);
			ParticleLighting.Update(cam);
			Rain.Update(s, cam);
			Glow.Update(s.Enabled && s.CarGlow, camPos);
			Lens.Update(s.Enabled, s, Sun.Sun, camPos);
			MaterialProfiles.UpdateGlobals(Sun.Sun);
			RT.UpdateRayTracing(Sun.Sun);
			// the game's painted shadow planes go while the ray traced sun shadows run
			PaintedShadows.Update(s.Enabled && s.RTShadows && s.RTHideFakeShadows && RT.SunShadowsRunning);
			Finish.ChromeRims = s.CarChromeRims;
			// the ray tracing scene takes the rims' new finish (renderer blocks are read when instances are added)
			if (Finish.Update(s.Enabled) && Time.unscaledTime >= nextFinishRebuild_) {
				nextFinishRebuild_ = Time.unscaledTime + 5.0f;
				RT.RequestSceneRebuild();
			}
			if (window_ != null && window_.Visible) {
				Exposure.Update();
			}
			window_?.Update();

			// Tracks stream objects in, so pick up new materials periodically.
			if (Time.unscaledTime >= nextRescan_) {
				nextRescan_ = Time.unscaledTime + RescanInterval;
				if (s.Enabled && s.ReplaceShaders) {
					ApplyReplacement();
				}
			}
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
			if (Settings.Data.Enabled && Settings.Data.RTOverlay && Event.current.type == EventType.Repaint) {
				RT.DrawOverlay();
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
			Water.Load(Markers, Bundle);
			Volumes.Load(Markers);
			Particles.Load(Markers, Bundle);
			Night.Load(Map, Bundle);
			Forest.Load(Markers, Bundle);
		}

		internal bool BundleFileExists() {
			return File.Exists(Bundle.BundlePath);
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
			if (mode == LoadSceneMode.Single) {
				NatureFX.Tools.MapDataProbe.Reset();
			}
			ScheduleRescan(2.0f);
		}

		// Tracks load several additive scenes in a row; restart the timer so they share one rescan.
		private void ScheduleRescan(float delay) {
			if (pendingRescan_ != null) {
				StopCoroutine(pendingRescan_);
			}
			pendingRescan_ = StartCoroutine(RescanAfterDelay(delay));
		}

		private IEnumerator RescanAfterDelay(float delay) {
			yield return new WaitForSecondsRealtime(delay);
			pendingRescan_ = null;

			Capabilities.Refresh();
			// what the bundle build may leave out (render path, lightmap modes, LOD cross fade): facts of this map
			NatureFX.Tools.VariantProfileDump.Write(Camera.main);
			Sun.Refresh();
			RT.PrepareSceneForRayTracing();
			Accumulation.CollectSceneMaterials();
			RefreshMap(false);
			NatureFX.Tools.MapDataProbe.Run();
			if (Settings.Data.RecordShaderVariants) {
				NatureFX.Tools.ShaderVariantRecorder.Save(); // variants used by the previous map
			}
			// the game's materials as they are, before any switch: data for the translators (stages 1 to 4)
			Replacer.Full = Settings.Data.FullReplacement;
			NatureFX.Tools.MaterialInventory.Write(Replacer);
			if (Settings.Data.Enabled && Settings.Data.ReplaceShaders) {
				ApplyReplacement();
				NatureFX.Tools.SceneShaderDumper.MergeVariants(Replacer.UsedVariants);
			}
			MarkDirty();
		}
	}
}

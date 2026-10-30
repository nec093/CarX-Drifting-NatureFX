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

		private SettingsUI ui_;
		private NatureFXWindow window_;
		private bool dirty_ = true;
		private float nextRescan_;
		private Coroutine pendingRescan_;
		private float nextVolumeRefresh_;
		private bool rayTracingOnly_;

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

			Volume.Create();
			Wind.Create();
			Replacer.Profiles = MaterialLooks;
			Replacer.LoadRules();
			// The bundle also carries the ray tracing shaders, so load it even when shader replacement is off.
			Bundle.Load();
			Dx12Fix.Apply(Bundle);
			GpuCullingDx12.Install(Bundle);
			TireSmokeDx12.Install();
			RT.Create(Bundle);
			Tracks.Create(Bundle);
			CarRainFx.Create(Bundle);

			SceneManager.sceneLoaded += OnSceneLoaded;
			NatureFX.Tools.ShaderVariantRecorder.Recording = Settings.Data.RecordShaderVariants;
			NatureFX.Tools.HdrpSettingsDump.Write();
			Kino.Input.Bind(KeyCode.F8, () => window_.Toggle(), "NatureFX window");

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
			Volume.Destroy();
		}

		private void Update() {
			var s = Settings.Data;
			if (s == null) {
				return;
			}

			// ray tracing development: every other effect sees Enabled = false and switches itself off
			var fx = s.DevRayTracingOnly ? Settings.Off : s;
			if (s.DevRayTracingOnly != rayTracingOnly_) {
				rayTracingOnly_ = s.DevRayTracingOnly;
				SetShaderReplacement(s.Enabled && s.ReplaceShaders);
				dirty_ = true;
			}

			WeatherCtl.Update(fx);

			// weather blends fog, clouds and sun over time, so refresh the volume a few times per second
			if (Time.unscaledTime >= nextVolumeRefresh_) {
				nextVolumeRefresh_ = Time.unscaledTime + 0.25f;
				dirty_ = true;
			}

			if (dirty_) {
				dirty_ = false;
				if (s.Enabled && s.DevRayTracingOnly) {
					Volume.ApplyIsolation(s);
				} else {
					Volume.Disable();
				}
				RT.Apply(s);
			}

			// the rebuilt shaders' road surface (grain, wear, tyre rubber) and the foliage's wind belong to the shader
			// rebuild, so they stay on while only the ray traced effects run (the traced foliage sways with it)
			bool rebuiltSurface = s.Enabled && s.ReplaceShaders;
			Wind.DrawnSway = Map.FoliageWind;
			Wind.Update(s.DevRayTracingOnly && rebuiltSurface ? s : fx);
			Accumulation.Update(fx);
			if (s.DevRayTracingOnly && rebuiltSurface) {
				Accumulation.SetSurfaceDetail(s.SurfaceDetail);
			}
			Accumulation.UpdateRoad(rebuiltSurface ? s.RoadWear : 0.0f);
			Sun.Update(fx);
			Headlights.Update(fx);
			Tracks.Update(s.DevRayTracingOnly && rebuiltSurface ? s : fx);
			CarRainFx.Update(fx, Sun.Sun);
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
			Identify.OriginalOf ??= Replacer.OriginalOf;
			Identify.OnGUI();
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

		// F8 > Tools: re-read the map profile (authors edit it while the map is loaded)
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

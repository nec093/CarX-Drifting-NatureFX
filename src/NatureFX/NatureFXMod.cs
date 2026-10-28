using System.Collections;
using System.IO;
using System.Reflection;
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

		private SettingsUI ui_;
		private bool dirty_ = true;
		private bool photoMode_;
		private float nextRescan_;
		private Coroutine pendingRescan_;

		internal bool PhotoMode => photoMode_;

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

			Volume.Create();
			Wind.Create();
			Replacer.LoadRules();
			if (Settings.Data.ReplaceShaders) {
				Bundle.Load();
			}

			SceneManager.sceneLoaded += OnSceneLoaded;
			Kino.Input.Bind(new[] { KeyCode.LeftControl, KeyCode.P }, TogglePhotoMode, "Photo mode (path tracing)");

			ScheduleRescan(1.0f);
		}

		private void OnDestroy() {
			SceneManager.sceneLoaded -= OnSceneLoaded;
			Replacer.RestoreAll();
			Accumulation.RestoreAll();
			Bundle.Unload();
			Wind.Destroy();
			Volume.Destroy();
		}

		private void Update() {
			var s = Settings.Data;
			if (s == null) {
				return;
			}

			if (dirty_) {
				dirty_ = false;
				Volume.Apply(s, photoMode_);
			}

			Wind.Update(s);
			Accumulation.Update(s);

			// Tracks stream objects in, so pick up new materials periodically.
			if (Time.unscaledTime >= nextRescan_) {
				nextRescan_ = Time.unscaledTime + RescanInterval;
				if (s.Enabled && s.ReplaceShaders) {
					Replacer.Apply(Bundle);
				}
			}
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

		internal void TogglePhotoMode() {
			photoMode_ = !photoMode_;
			if (photoMode_ && !Capabilities.RayTracingAvailable) {
				Kino.Log.Warning("[NatureFX] Path tracing needs DX12 (-force-d3d12) and ray tracing enabled in the game's HDRP asset");
			}
			MarkDirty();
		}

		internal void SetShaderReplacement(bool enabled) {
			if (enabled) {
				Bundle.Load();
				Replacer.Apply(Bundle);
			} else {
				Replacer.RestoreAll();
			}
		}

		internal void ReloadRules() {
			Replacer.RestoreAll();
			Replacer.LoadRules();
			if (Settings.Data.ReplaceShaders) {
				Replacer.Apply(Bundle);
			}
		}

		internal bool BundleFileExists() {
			return File.Exists(Bundle.BundlePath);
		}

		private void OnSceneLoaded(Scene scene, LoadSceneMode mode) {
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
			Accumulation.CollectSceneMaterials();
			if (Settings.Data.Enabled && Settings.Data.ReplaceShaders) {
				Replacer.Apply(Bundle);
			}
			MarkDirty();
		}
	}
}

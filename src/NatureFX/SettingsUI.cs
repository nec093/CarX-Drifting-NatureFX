using System;
using NatureFX.Rendering;
using NatureFX.Tools;
using KSL.API;

namespace NatureFX {
	internal class SettingsUI {
		private readonly NatureFXMod mod_;
		private string lastDump_;

		public SettingsUI(NatureFXMod mod) {
			mod_ = mod;
		}

		private static ISettings S => Settings.Data;

		public void Draw() {
			if (S == null) {
				Kino.UI.Label("Config failed to load. Check the KSL log.");
				return;
			}

			Toggle("Enabled", S.Enabled, v => S.Enabled = v, false);
			Kino.UI.Label("Ray tracing and everything else: F8 opens the NatureFX window.");

			Kino.UI.HorizontalLine();

			if (Kino.UI.ContextButton("Foliage & wind")) {
				Kino.UI.PushContext(NatureContext, "Foliage & wind");
			}
			if (Kino.UI.ContextButton("Weather & accumulation")) {
				Kino.UI.PushContext(WeatherContext, "Weather & accumulation");
			}
			if (Kino.UI.ContextButton("Tools")) {
				Kino.UI.PushContext(ToolsContext, "Tools");
			}
		}

		private void NatureContext() {
			Toggle("Wind", S.Wind, v => S.Wind = v, false);
			Slider("Strength", S.WindStrength, 0.0f, 2.0f, v => S.WindStrength = v, false);
			Slider("Gusts", S.WindGust, 0.0f, 1.0f, v => S.WindGust = v, false);
			Slider("Direction (deg)", S.WindDirection, 0.0f, 360.0f, v => S.WindDirection = v, false);
			Slider("Leaf flutter", S.LeafFlutter, 0.0f, 1.0f, v => S.LeafFlutter = v, false);
			Toggle("Cars push grass", S.InteractiveFoliage, v => S.InteractiveFoliage = v, false);

			Kino.UI.HorizontalLine();
			BundleStatus();
		}

		private void WeatherContext() {
			Toggle("Accumulation", S.Accumulation, v => S.Accumulation = v, false);
			Slider("Snow", S.Snow, 0.0f, 1.0f, v => S.Snow = v, false);
			Slider("Fallen leaves", S.Leaves, 0.0f, 1.0f, v => S.Leaves = v, false);
			Slider("Dust", S.Dust, 0.0f, 1.0f, v => S.Dust = v, false);
			Slider("Wetness", S.Wetness, 0.0f, 1.0f, v => S.Wetness = v, false);

			Kino.UI.HorizontalLine();
			Toggle("Build up over time", S.AccumulateOverTime, v => S.AccumulateOverTime = v, false);
			Slider("Build up speed (per minute)", S.AccumulationRate, 0.005f, 0.5f, v => S.AccumulationRate = v, false);
			var c = mod_.Accumulation.Current;
			Kino.UI.Label($"Current: snow {c.x:P0}, leaves {c.y:P0}, dust {c.z:P0}, wet {c.w:P0}");
			if (Kino.UI.Button("Reset build up")) {
				mod_.Accumulation.ResetProgress();
			}

			Kino.UI.HorizontalLine();
			Toggle("Wet look on game materials (no bundle needed)", S.WetGameMaterials, v => S.WetGameMaterials = v, false);
			Kino.UI.Label("Snow, leaves and dust need the NFX shader bundle.");
		}

		private void ToolsContext() {
			if (Kino.UI.Button("Dump scene shaders")) {
				lastDump_ = SceneShaderDumper.Dump(mod_.Replacer);
			}
			if (lastDump_ != null) {
				Kino.UI.Label($"Written: {lastDump_}");
			}

			if (Kino.UI.Button("Reload replacement rules")) {
				mod_.ReloadRules();
			}
			Kino.UI.Label($"Rules: {mod_.Replacer.RulesPath}");

			Kino.UI.HorizontalLine();
			BundleStatus();
		}

		private void BundleStatus() {
			if (!mod_.BundleFileExists()) {
				Kino.UI.Label($"Shader bundle missing: {mod_.Bundle.BundlePath}");
			} else {
				Kino.UI.Label($"Bundle shaders: {mod_.Bundle.Shaders.Count}, replaced materials: {mod_.Replacer.ReplacedCount}");
			}

			Toggle("Replace game shaders", S.ReplaceShaders, v => {
				S.ReplaceShaders = v;
				mod_.SetShaderReplacement(v);
			}, false);
		}

		// Properties can't be passed by ref, so copy -> edit -> write back.
		private void Toggle(string label, bool value, Action<bool> set, bool unused = false) {
			if (Kino.UI.Toggle(label, ref value)) {
				set(value);
				mod_.MarkDirty();
			}
		}

		private void Slider(string label, float value, float min, float max, Action<float> set, bool unused = false) {
			if (Kino.UI.Slider(ref value, min, max, $"{label}: {value:0.###}")) {
				set(value);
				mod_.MarkDirty();
			}
		}
	}
}

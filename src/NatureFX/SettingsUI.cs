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

			Kino.UI.GroupLabel($"Preset: {Presets.Names[S.Preset]}");
			Kino.UI.BeginHorizontal();
			for (int i = 1; i < Presets.Names.Length; ++i) {
				var group = i == 1 ? Group.HLeft : i == Presets.Names.Length - 1 ? Group.HRight : Group.HMiddle;
				if (Kino.UI.Button(Presets.Names[i], group, S.Preset == i)) {
					Presets.Apply(S, (Preset)i);
					Settings.Save();
					mod_.MarkDirty();
				}
			}
			Kino.UI.EndHorizontal();

			Kino.UI.HorizontalLine();

			if (Kino.UI.ContextButton("Lighting")) {
				Kino.UI.PushContext(LightingContext, "Lighting");
			}
			if (Kino.UI.ContextButton("Ray tracing")) {
				Kino.UI.PushContext(RayTracingContext, "Ray tracing");
			}
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

		private void LightingContext() {
			Toggle("Global illumination (SSGI)", S.GlobalIllumination, v => S.GlobalIllumination = v);
			Toggle("Screen space reflections", S.Reflections, v => S.Reflections = v);
			Toggle("Ambient occlusion", S.AmbientOcclusion, v => S.AmbientOcclusion = v);
			Slider("AO intensity", S.AmbientOcclusionIntensity, 0.0f, 4.0f, v => S.AmbientOcclusionIntensity = v);
			Toggle("Contact shadows", S.ContactShadows, v => S.ContactShadows = v);

			Kino.UI.HorizontalLine();
			Toggle("Volumetric fog", S.VolumetricFog, v => S.VolumetricFog = v);
			Slider("Fog distance (m, 0 = off)", S.FogDistance, 0.0f, 5000.0f, v => S.FogDistance = v);
			Slider("Fog height (m)", S.FogHeight, 1.0f, 500.0f, v => S.FogHeight = v);

			Kino.UI.HorizontalLine();
			Toggle("ACES tonemapping", S.AcesTonemapping, v => S.AcesTonemapping = v);
			Slider("Bloom", S.Bloom, 0.0f, 1.0f, v => S.Bloom = v);
			Slider("Exposure (EV)", S.Exposure, -3.0f, 3.0f, v => S.Exposure = v);
			Slider("Contrast", S.Contrast, -50.0f, 50.0f, v => S.Contrast = v);
			Slider("Saturation", S.Saturation, -50.0f, 50.0f, v => S.Saturation = v);
		}

		private void RayTracingContext() {
			Kino.UI.Label(Capabilities.Describe());
			if (!Capabilities.IsD3D12) {
				Kino.UI.Label("The game is running on DirectX 11. Add -force-d3d12 to the Steam launch options to use ray tracing.");
			} else if (!Capabilities.PipelineRayTracing) {
				Kino.UI.Label("This game build has ray tracing disabled in its HDRP asset. Screen-space fallbacks are used.");
			}

			Kino.UI.HorizontalLine();
			Toggle("Ray traced GI / reflections / AO", S.RayTracing, v => S.RayTracing = v);
			bool quality = S.RayTracingQuality > 0;
			Toggle("Quality mode (slower, multi-bounce)", quality, v => S.RayTracingQuality = v ? 1 : 0);

			Kino.UI.HorizontalLine();
			Kino.UI.GroupLabel("Photo mode path tracing");
			Kino.UI.Label("Full path tracing accumulates samples while the camera is still. Use it for screenshots (Ctrl+P).");
			Toggle("Allow path tracing in photo mode", S.PhotoModePathTracing, v => S.PhotoModePathTracing = v);
			float samples = S.PathTracingSamples;
			Slider($"Samples: {S.PathTracingSamples}", samples, 16.0f, 2048.0f, v => S.PathTracingSamples = (int)v);
			float bounces = S.PathTracingBounces;
			Slider($"Bounces: {S.PathTracingBounces}", bounces, 1.0f, 10.0f, v => S.PathTracingBounces = (int)v);

			if (Kino.UI.Button(mod_.PhotoMode ? "Exit photo mode" : "Enter photo mode", active: mod_.PhotoMode)) {
				mod_.TogglePhotoMode();
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
				lastDump_ = SceneShaderDumper.Dump();
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
		// Lighting edits switch the preset to Custom and re-apply the volume.
		private void Toggle(string label, bool value, Action<bool> set, bool lighting = true) {
			if (Kino.UI.Toggle(label, ref value)) {
				set(value);
				Changed(lighting);
			}
		}

		private void Slider(string label, float value, float min, float max, Action<float> set, bool lighting = true) {
			if (Kino.UI.Slider(ref value, min, max, $"{label}: {value:0.###}")) {
				set(value);
				Changed(lighting);
			}
		}

		private void Changed(bool lighting) {
			if (lighting) {
				S.Preset = (int)Preset.Custom;
			}
			mod_.MarkDirty();
		}
	}
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NatureFX.Nature;
using NatureFX.Rendering;
using UnityEngine;

namespace NatureFX.Tools {
	// The developer's switches and tools (once the F8 window's Developer tab), on the debug page: each control's state goes
	// out with the page's stats.json, a change comes back as GET /set?id=..&v=.. on the page's server thread and is applied
	// here on the main thread (Unity's objects are not to be touched from another thread).
	public static class DevControls {
		private enum Kind { Switch, Slider, Chips, Button, Info }

		private sealed class Control {
			public string Group, Id, Label, Help;
			public Kind Kind;
			public Func<float> Get;
			public Action<float> Set;
			public float Min, Max, Step;
			public string[] Options;
			public Func<string> Text;  // an info row's text, or a control's state line
			public bool Saved;         // a setting of the settings file: saved after a change
		}

		private static readonly List<Control> controls_ = new();
		private static readonly Dictionary<string, Control> byId_ = new();
		private static readonly ConcurrentQueue<(string Id, float Value)> pending_ = new();
		private static NatureFXMod mod_;
		private static string reloadStatus_ = "", lastDump_ = "";

		private static ISettings S => Settings.Data;

		// on the server thread: a change asked for by the page
		public static void Request(string id, float value) {
			pending_.Enqueue((id, value));
		}

		// on the main thread, once a frame: the changes the page asked for. True when something changed.
		public static bool Apply(NatureFXMod mod) {
			if (mod_ != mod) {
				mod_ = mod;
				Build();
			}
			bool changed = false, save = false;
			while (pending_.TryDequeue(out var r)) {
				if (!byId_.TryGetValue(r.Id, out var c) || c.Set == null) {
					continue;
				}
				try {
					c.Set(c.Kind == Kind.Slider ? Mathf.Clamp(r.Value, c.Min, c.Max) : r.Value);
					changed = true;
					save |= c.Saved;
					KSL.API.Kino.Log.Info($"[NatureFX] Debug page: {c.Group} / {c.Label} = {r.Value.ToString(CultureInfo.InvariantCulture)}");
				} catch (Exception e) {
					KSL.API.Kino.Log.Warning($"[NatureFX] Debug page: {c.Label} failed ({e.GetType().Name}: {e.Message})");
				}
			}
			if (changed) {
				mod_?.MarkDirty();
				if (save) {
					Settings.Save();
				}
			}
			return changed;
		}

		// the controls with their state, as "controls":[..] in stats.json (main thread)
		public static void Write(StringBuilder sb, Func<string, string> json) {
			var inv = CultureInfo.InvariantCulture;
			sb.Append(",\"controls\":[");
			for (int i = 0; i < controls_.Count; ++i) {
				var c = controls_[i];
				string text = "";
				float value = 0.0f;
				try {
					text = c.Text != null ? c.Text() ?? "" : "";
					value = c.Get != null ? c.Get() : 0.0f;
				} catch (Exception e) {
					text = $"({e.GetType().Name})";
				}
				sb.Append(i > 0 ? "," : "").Append("{\"group\":").Append(json(c.Group)).Append(",\"id\":").Append(json(c.Id))
				  .Append(",\"label\":").Append(json(c.Label ?? "")).Append(",\"kind\":").Append(json(c.Kind.ToString().ToLowerInvariant()))
				  .Append(",\"value\":").Append(value.ToString("0.#####", inv))
				  .Append(",\"min\":").Append(c.Min.ToString("0.#####", inv)).Append(",\"max\":").Append(c.Max.ToString("0.#####", inv))
				  .Append(",\"step\":").Append(c.Step.ToString("0.#####", inv))
				  .Append(",\"help\":").Append(json(c.Help ?? "")).Append(",\"text\":").Append(json(text)).Append(",\"options\":[");
				if (c.Options != null) {
					for (int k = 0; k < c.Options.Length; ++k) {
						sb.Append(k > 0 ? "," : "").Append(json(c.Options[k]));
					}
				}
				sb.Append("]}");
			}
			sb.Append(']');
		}

		// ------------------------------------------------------------------ the controls
		private static void Add(Control c) {
			c.Id ??= $"c{controls_.Count}";
			controls_.Add(c);
			byId_[c.Id] = c;
		}

		private static void Switch(string group, string id, string label, Func<bool> get, Action<bool> set, string help = null, bool saved = false) {
			Add(new Control { Group = group, Id = id, Label = label, Kind = Kind.Switch, Get = () => get() ? 1.0f : 0.0f, Set = v => set(v > 0.5f), Help = help, Saved = saved });
		}

		private static void Slider(string group, string id, string label, Func<float> get, Action<float> set, float min, float max, float step, string help = null, bool saved = false) {
			Add(new Control { Group = group, Id = id, Label = label, Kind = Kind.Slider, Get = get, Set = set, Min = min, Max = max, Step = step, Help = help, Saved = saved });
		}

		private static void Chips(string group, string id, string label, string[] options, Func<int> get, Action<int> set, string help = null) {
			Add(new Control { Group = group, Id = id, Label = label, Kind = Kind.Chips, Options = options, Get = () => get(), Set = v => set(Mathf.RoundToInt(v)), Help = help });
		}

		private static void Button(string group, string id, string label, Action run, Func<string> state = null, string help = null) {
			Add(new Control { Group = group, Id = id, Label = label, Kind = Kind.Button, Set = _ => run(), Text = state, Help = help });
		}

		private static void Info(string group, string label, Func<string> text) {
			Add(new Control { Group = group, Label = label, Kind = Kind.Info, Text = text });
		}

		// an info row read again only every 10 s (the system's description walks the lightmaps and more: not every second)
		private static void SlowInfo(string group, string label, Func<string> text) {
			string cached = null;
			float next = 0.0f;
			Info(group, label, () => {
				if (cached == null || Time.unscaledTime >= next) {
					next = Time.unscaledTime + 10.0f;
					cached = text();
				}
				return cached;
			});
		}

		private static void Build() {
			controls_.Clear();
			byId_.Clear();
			var mod = mod_;

			const string views = "Ray tracing views";
			Button(views, "reset-history", "Reset accumulated image", () => mod.RT.ResetHistory(), null,
				"Shadows, ambient light and reflections build up over several frames; camera cuts reset them on their own, this clears anything left burnt in.");
			Switch(views, "view-shadows", "Show traced shadows only", () => mod.RT.ShadowDebugView, v => mod.RT.ShadowDebugView = v,
				"White is lit by the sun, black is the traced shadow, blue-grey faces away from the sun or is sky.");
			Switch(views, "view-exact-sun", "Show exact sun light", () => mod.RT.ExactSunDebugView, v => mod.RT.ExactSunDebugView = v,
				"The sun light the composite adds, before shadowing. Dark magenta where it is not known.");
			Switch(views, "view-gi", "Show ambient light / GI only", () => mod.RT.GIDebugView, v => mod.RT.GIDebugView = v,
				"Mid grey where the ambient light is unchanged, darker where the sky is blocked, brighter and tinted where light bounces in.");
			Button(views, "identify", "What is this? (then click the scene in the game)", () => mod.Identify.Arm(),
				() => mod.Identify.Armed ? "click the scene in the game..." : mod.Identify.Result);

			const string reload = "Reload shaders";
			Button(reload, "reload-shaders", "Reload shaders", () => reloadStatus_ = $"Bundle: {mod.Bundle.Reload()}. Engine: {NativeEngine.ReloadShaders()}",
				() => reloadStatus_, "Without a restart: the newest shader bundle in kino/mods/NatureFX/reload/ and the engine's shaders in NativeShaders/ " +
				                     "(tools/build_native.ps1 -ShadersOnly). C# and the engine's C++ still need a restart.");
			Info(reload, "engine shaders", () => NativeEngine.ShaderStatus);

			const string nrd = "NRD parts";
			Switch(nrd, "nrd-sun", "Sun shadows (SIGMA)", () => NativeEngine.DevNrdSun, v => NativeEngine.DevNrdSun = v,
				"Each signal off goes back to NatureFX's own filter, to find which part a problem comes from. Not saved.");
			Switch(nrd, "nrd-ambient", "Ambient light / GI (REBLUR)", () => NativeEngine.DevNrdAmbient, v => NativeEngine.DevNrdAmbient = v);
			Switch(nrd, "nrd-lamps", "Lamp shadows (REBLUR)", () => NativeEngine.DevNrdLamps, v => NativeEngine.DevNrdLamps = v);
			Switch(nrd, "nrd-reflections", "Reflections (REBLUR)", () => NativeEngine.DevNrdReflections, v => NativeEngine.DevNrdReflections = v);
			Switch(nrd, "nrd-fast-refl", "Reflections: cheaper REBLUR", () => NativeEngine.NrdFastReflections, v => NativeEngine.NrdFastReflections = v,
				"Without REBLUR's pre-pass blur, stabilization and anti-firefly: compare the paint's reflections and 'nrd: REBLUR reflections'.");
			Switch(nrd, "nrd-fast-diffuse", "Ambient / GI and lamps: cheaper REBLUR", () => NativeEngine.NrdFastDiffuse, v => NativeEngine.NrdFastDiffuse = v,
				"The same for the ambient light, GI and the lamp shadows.");
			Switch(nrd, "car-motion", "Cars' motion vector pass", () => NativeEngine.DevCarMotionVectors, v => NativeEngine.DevCarMotionVectors = v,
				"NRD and DLSS follow a moving car's pixels by its motion vectors; the game draws cars without them.");

			const string dlss = "DLSS tests";
			Chips(dlss, "dlss-jitter-sign", "Jitter sign", Dlaa.JitterSigns, () => Dlaa.DevJitterSign, v => Dlaa.DevJitterSign = v,
				"What DLSS is handed, to find what makes the picture worse than TAA. Not saved.");
			Chips(dlss, "dlss-motion", "Motion", Dlaa.Motions, () => Dlaa.DevMotion, v => Dlaa.DevMotion = v);
			Slider(dlss, "dlss-jitter", "Jitter", () => Dlaa.DevJitterScale, v => Dlaa.DevJitterScale = v, 0.0f, 1.0f, 0.01f);
			Switch(dlss, "dlss-rr-upscale", "Ray Reconstruction with upscaling (test)", () => Dlaa.DevRayReconstructionUpscaling, v => Dlaa.DevRayReconstructionUpscaling = v,
				"Off for now: it shook the picture and once hung the GPU. NVIDIA's own log goes to kino/mods/NatureFX/NFX_ngx.log.");
			Switch(dlss, "dlss-taau-path", "Upscaling through HDRP's TAAU pass (the old way)", () => Dlaa.DevTaauPath, v => Dlaa.DevTaauPath = v,
				"DLSS's upscaled picture handed to the screen by HDRP's TAAU pass, HDRP jittering, instead of HDRP's final pass (NFX_UpscaledFinal) with the camera jittered by NatureFX. Not saved.");
			Switch(dlss, "dlss-taau", "Upscale with HDRP's TAAU instead (test)", () => Dlaa.DevHdrpTaau, v => Dlaa.DevHdrpTaau = v,
				"The same lower resolution upscaled by HDRP's own TAAU: tells DLSS's doing from the resolution's.");
			Info(dlss, "now", () => Dlaa.Status);

			const string memory = "Video memory";
			Slider(memory, "texture-budget", "Texture streaming budget (MB, 0 the game's)", () => S.TextureStreamingBudget,
				v => S.TextureStreamingBudget = Mathf.Round(v / 64.0f) * 64.0f, 0.0f, 4096.0f, 64.0f,
				"Unity's texture mip streaming budget: lower, Unity drops the finest mips of the textures seen smallest.", true);
			Info(memory, "now", () => TextureBudget.Status);
			Switch(memory, "force-mips", "Load every texture mip (test: blurred textures)", () => Texture.streamingTextureForceLoadAll, v => Texture.streamingTextureForceLoadAll = v);
			Info(memory, "own sky: track sky textures out of video memory", () => VolumeOverhaul.UnloadedSky);

			const string tests = "Comparisons";
			Switch(tests, "gi-lamp-ref", "Lamps in the GI's reference", () => LocalLights.UseLampReference, v => LocalLights.UseLampReference = v,
				"The GI's clamp of single bright rays also goes by the brightest lamp, so a headlight's bounce shows at night. Off: the old reference. Not saved.");
			Switch(tests, "fog-lists", "3D fog: light lists per tile", () => NativeEngine.FogLightLists, v => NativeEngine.FogLightLists = v,
				"The same picture: off only to compare the 3D fog's time. Not saved.");
			Switch(tests, "transparent-fog", "3D fog on see-through objects", () => TransparentFog.Enabled, v => TransparentFog.Enabled = v,
				"HDRP's transparent materials (fences on cards, signs, blended foliage) get the own engine's fog in front of them. Off: as before, unfogged. Not saved.");
			Info(tests, "transparent fog", () => TransparentFog.Status);
			Switch(tests, "rain-collision", "Rain: drops stopped by the world's colliders", () => Precipitation.WorldCollision, v => Precipitation.WorldCollision = v,
				"Off: the drops fall through roofs and the car. Turned off by itself when no drop lives with it (see Weather's precipitation state).");
			Slider(tests, "smoke-size", "Tyre smoke: largest puff on screen (x)", () => TireSmoke.MaxScreenSize, v => TireSmoke.MaxScreenSize = v, 0.2f, 4.0f, 0.1f,
				"Smaller is cheaper close to the smoke; too small and the smoke around the camera looks cut. Not saved.");

			const string tools = "Tools";
			Button(tools, "dump-shaders", "Dump scene shaders", () => lastDump_ = SceneShaderDumper.Dump(mod.Replacer), () => lastDump_);
			Button(tools, "reload-rules", "Reload replacement rules", () => mod.ReloadRules(), () => mod.Replacer.RulesPath);
			Switch(tools, "record-variants", "Record shader variants", () => S.RecordShaderVariants, v => {
				S.RecordShaderVariants = v;
				ShaderVariantRecorder.Recording = v;
			}, "Records which NatureFX shader variants the game uses: play maps in DX11 and DX12, day and night, rain and snow, then save.", true);
			Button(tools, "save-variants", "Save recorded variants", () => ShaderVariantRecorder.Save(),
				() => ShaderVariantRecorder.LastMessage ?? ShaderVariantRecorder.FilePath);
			Switch(tools, "material-inventory", "Write the material inventory at each map load", () => S.WriteMaterialInventory, v => S.WriteMaterialInventory = v,
				"For the shader rebuild (kino/config/NatureFX/inventory). Costs a hitch and some hundred MB of video memory at each load.", true);

			const string system = "System";
			Switch(system, "read-cpu", "Read CPU counters (threads)", () => CpuStats.Threads, v => CpuStats.Threads = v,
				"The debug page's counters are read on a thread of their own once a second. Switch them off one by one if the game hitches every second only while the page is open. Not saved.");
			Switch(system, "read-gpu", "Read GPU counters (NVML: busy)", () => GpuStats.Counters, v => GpuStats.Counters = v);
			Switch(system, "read-gpu-details", "Read GPU clocks, power, temperature (NVML)", () => GpuStats.Details, v => GpuStats.Details = v,
				"Off by default: some drivers stall the GPU while these are read.");
			SlowInfo(system, "capabilities", () => Capabilities.Describe());
			SlowInfo(system, "shader bundle", () => $"{mod.Bundle.Shaders.Count} shaders, {mod.Replacer.ReplacedCount} materials replaced");
			SlowInfo(system, "native engine", () => $"{NativeEngine.Info}; self test: {NativeEngine.TestResult}");
			SlowInfo(system, "scene", () => NativeEngine.SceneStatus);
			SlowInfo(system, "check", () => $"{NativeEngine.CheckStatus}; queue: {NativeEngine.QueueStatus}");
			SlowInfo(system, "sun shadows", () => NativeEngine.SunCounters);
		}
	}
}

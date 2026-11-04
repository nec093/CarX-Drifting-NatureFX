using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// GPU time of each ray tracing stage (Unity's profiling recorders, averaged by Unity over a few frames), shown
	// in F8 so the cost can be traced to a stage instead of guessed.
	public static class RTTimings {
		public static readonly ProfilingSampler Scene = Create("NFX RT scene build");
		public static readonly ProfilingSampler Surface = Create("NFX RT camera rays");
		public static readonly ProfilingSampler ShadowTrace = Create("NFX RT shadow rays");
		public static readonly ProfilingSampler Sun = Create("NFX RT sun");
		public static readonly ProfilingSampler Ambient = Create("NFX RT ambient / GI");
		public static readonly ProfilingSampler Shafts = Create("NFX RT light shafts");
		public static readonly ProfilingSampler Lamps = Create("NFX RT lamp shadows");
		public static readonly ProfilingSampler Glass = Create("NFX RT glass");
		public static readonly ProfilingSampler ShadowFilter = Create("NFX RT shadow filter");
		public static readonly ProfilingSampler ReflectionTrace = Create("NFX RT reflection rays");
		public static readonly ProfilingSampler ReflectionFilter = Create("NFX RT reflection filter");
		public static readonly ProfilingSampler FogVolume = Create("NFX RT fog volume");

		private static readonly (string Label, ProfilingSampler Sampler)[] Stages = {
			("scene build", Scene),
			("camera rays", Surface),
			("shadow rays", ShadowTrace),
			("  sun", Sun),
			("  ambient / GI", Ambient),
			("  light shafts", Shafts),
			("  lamp shadows", Lamps),
			("shadow filter", ShadowFilter),
			("reflection rays", ReflectionTrace),
			("reflection filter", ReflectionFilter),
			("glass", Glass),
			("fog volume", FogVolume)
		};

		// CPU time moving the cars' instances (RayTracingScene.Update), smoothed
		public static float SceneUpdateMs { get; private set; }
		private static readonly Stopwatch stopwatch_ = new();

		private static ProfilingSampler Create(string name) {
			return new ProfilingSampler(name) { enableRecording = true };
		}

		public static void BeginSceneUpdate() {
			stopwatch_.Restart();
		}

		public static void EndSceneUpdate() {
			float ms = (float)stopwatch_.Elapsed.TotalMilliseconds;
			SceneUpdateMs = SceneUpdateMs <= 0.0f ? ms : SceneUpdateMs + (ms - SceneUpdateMs) * 0.05f;
		}

		// One line per stage that ran recently: "shadow rays  1.23 ms"
		// The stages that ran, as (label, ms, part of the one above), and the total of the top level ones
		public static List<(string Label, float Ms, bool Part)> Readings(out float total) {
			var list = new List<(string, float, bool)>();
			total = 0.0f;
			foreach (var (label, sampler) in Stages) {
				float ms = sampler.gpuElapsedTime;
				if (ms <= 0.0f) {
					continue;
				}
				bool part = label.StartsWith(" ");
				if (!part) {
					total += ms;
				}
				list.Add((label.Trim(), ms, part));
			}
			return list;
		}

		public static string Summary() {
			var text = new StringBuilder();
			float total = 0.0f;
			foreach (var (label, sampler) in Stages) {
				float ms = sampler.gpuElapsedTime;
				if (ms <= 0.0f) {
					continue;
				}
				if (!label.StartsWith(" ")) {
					total += ms;  // the indented stages are parts of the one above
				}
				text.AppendLine($"{label}  {ms:0.00} ms");
			}
			if (text.Length == 0) {
				return "GPU timings not available (no stage ran, or this GPU / API does not report them)";
			}
			text.AppendLine($"total GPU  {total:0.00} ms");
			text.Append($"car instances (CPU)  {SceneUpdateMs:0.00} ms");
			return text.ToString();
		}
	}
}

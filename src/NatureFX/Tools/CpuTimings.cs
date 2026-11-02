using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Tools {
	// CPU time of NatureFX's own work on the main thread, by part (the debug page), and the frames that took far longer
	// than the others with what took the time in them. NatureFXMod.Update is cut into named sections (Section); the
	// custom passes and HDRP's whole render are timed as scopes (Scope, nested: they are not taken off anything).
	// Only while the debug page runs (Active); otherwise every call returns at once.
	public static class CpuTimings {
		public static bool Active;
		private static readonly Stopwatch clock_ = Stopwatch.StartNew();
		private static string section_;
		private static double sectionStart_;
		// this frame's, the frames' since the page's last snapshot (sum), and how many frames
		private static readonly Dictionary<string, double> frame_ = new();
		private static readonly Dictionary<string, double> window_ = new();
		private static int windowFrames_;
		private static int gcBefore0_, gcBefore1_, gcBefore2_;
		private static int gcWindow_;
		private static double averageMs_ = 16.7;
		private static double renderStart_ = -1.0;
		private static bool hooked_;

		public sealed class Spike {
			public float Time;
			public double FrameMs;
			public int Collections;
			public string Top;
		}
		private static readonly List<Spike> spikes_ = new();
		private static readonly object lock_ = new();

		private static double Now => clock_.Elapsed.TotalMilliseconds;

		private static void Add(string name, double ms) {
			frame_.TryGetValue(name, out double v);
			frame_[name] = v + ms;
		}

		// At the start of NatureFXMod.Update: the frame before is done (its length is this frame's delta time)
		public static void BeginFrame() {
			if (!Active) {
				return;
			}
			if (!hooked_) {
				hooked_ = true;
				RenderPipelineManager.beginContextRendering += (_, _) => { if (Active) renderStart_ = Now; };
				RenderPipelineManager.endContextRendering += (_, _) => {
					if (Active && renderStart_ >= 0.0) {
						Add("HDRP render (all cameras, the passes included)", Now - renderStart_);
						renderStart_ = -1.0;
					}
				};
			}
			EndSection();
			double frameMs = UnityEngine.Time.unscaledDeltaTime * 1000.0;
			int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
			int collections = (g0 - gcBefore0_) + (g1 - gcBefore1_) + (g2 - gcBefore2_);
			gcBefore0_ = g0;
			gcBefore1_ = g1;
			gcBefore2_ = g2;
			// a frame far longer than the usual: what took the time in it
			if (frame_.Count > 0 && frameMs > Math.Max(25.0, averageMs_ * 1.6)) {
				var list = new List<KeyValuePair<string, double>>(frame_);
				list.Sort((a, b) => b.Value.CompareTo(a.Value));
				var top = new List<string>();
				for (int i = 0; i < list.Count && i < 4; ++i) {
					top.Add($"{list[i].Key} {list[i].Value:0.0}");
				}
				lock (lock_) {
					spikes_.Add(new Spike { Time = UnityEngine.Time.unscaledTime, FrameMs = frameMs, Collections = collections, Top = string.Join(", ", top) });
					if (spikes_.Count > 30) {
						spikes_.RemoveAt(0);
					}
				}
			}
			averageMs_ += (frameMs - averageMs_) * 0.05;
			lock (lock_) {
				foreach (var kv in frame_) {
					window_.TryGetValue(kv.Key, out double v);
					window_[kv.Key] = v + kv.Value;
				}
				++windowFrames_;
				gcWindow_ += collections;
			}
			frame_.Clear();
		}

		// From here on in NatureFXMod.Update the time counts as name
		public static void Section(string name) {
			if (!Active) {
				return;
			}
			double now = Now;
			if (section_ != null) {
				Add("update: " + section_, now - sectionStart_);
			}
			section_ = name;
			sectionStart_ = now;
		}

		public static void EndSection() {
			if (!Active || section_ == null) {
				return;
			}
			Add("update: " + section_, Now - sectionStart_);
			section_ = null;
		}

		public struct Timer : IDisposable {
			private readonly string name_;
			private readonly double start_;
			public Timer(string name) {
				name_ = name;
				start_ = Active ? Now : 0.0;
			}
			public void Dispose() {
				if (Active && name_ != null) {
					Add(name_, Now - start_);
				}
			}
		}

		// using (CpuTimings.Scope("pass: ...")) { ... }
		public static Timer Scope(string name) => new(Active ? name : null);

		// The averages per frame since the last call (ms), the frames and collections in between
		public static List<KeyValuePair<string, double>> Take(out int frames, out int collections) {
			lock (lock_) {
				frames = Math.Max(1, windowFrames_);
				collections = gcWindow_;
				var list = new List<KeyValuePair<string, double>>();
				foreach (var kv in window_) {
					list.Add(new KeyValuePair<string, double>(kv.Key, kv.Value / frames));
				}
				window_.Clear();
				windowFrames_ = 0;
				gcWindow_ = 0;
				list.Sort((a, b) => b.Value.CompareTo(a.Value));
				return list;
			}
		}

		public static List<Spike> Spikes() {
			lock (lock_) {
				return new List<Spike>(spikes_);
			}
		}
	}
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Tools {
	// Records which variants of the NatureFX shaders the game actually uses, so the bundle build can skip the
	// rest (unity/Assets/NatureFX/Editor/RecordedVariantStripper.cs). Unity logs every variant it uploads to the
	// GPU when GraphicsSettings.logWhenShaderIsCompiled is on; those lines are read back from Player.log and
	// merged into kino/config/NatureFX/recorded_variants.txt, one "shader|pass|stage|keywords" per line.
	public static class ShaderVariantRecorder {
		public const string FileName = "recorded_variants.txt";

		// e.g. "Uploaded shader variant to the GPU driver: NatureFX/LitAccumulation, pass: GBuffer, stage: pixel, keywords LIGHT_LAYERS _NORMALMAP"
		private static readonly Regex Line = new(@"shader variant[^:]*:\s*(?<shader>[^,]+),\s*pass:\s*(?<pass>[^,]+),\s*stage:\s*(?<stage>[^,]+),\s*keywords\s*(?<keywords>.*)$");

		private static long readOffset_;
		private static string readPath_;

		public static string FilePath => Path.Combine(Kino.Paths.Config, "NatureFX", FileName);
		public static int SavedCount { get; private set; } = -1;
		public static string LastMessage { get; private set; }

		public static bool Recording {
			get => GraphicsSettings.logWhenShaderIsCompiled;
			set => GraphicsSettings.logWhenShaderIsCompiled = value;
		}

		// Reads new Player.log lines and merges NatureFX variants into the recorded file. Returns how many were new.
		public static int Save() {
			try {
				var known = Load();
				int before = known.Count;
				int seen = 0;
				foreach (string entry in ReadNewLogEntries()) {
					++seen;
					known.Add(entry);
				}

				Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
				File.WriteAllLines(FilePath, new[] { "# NatureFX shader variants used in game (shader|pass|stage|keywords). Copy to unity/variants/ for the bundle build." }
					.Concat(known.OrderBy(s => s, StringComparer.Ordinal)));
				SavedCount = known.Count;
				int added = known.Count - before;
				LastMessage = $"{known.Count} variants recorded ({added} new from {seen} log lines)";
				Kino.Log.Info($"[NatureFX] Shader variants: {LastMessage} -> {FilePath}");
				return added;
			} catch (Exception e) {
				LastMessage = $"Could not save: {e.Message}";
				Kino.Log.Error($"[NatureFX] Shader variant recording: {e.Message}");
				return 0;
			}
		}

		private static HashSet<string> Load() {
			var set = new HashSet<string>(StringComparer.Ordinal);
			if (File.Exists(FilePath)) {
				foreach (string line in File.ReadAllLines(FilePath)) {
					if (line.Length > 0 && !line.StartsWith("#")) {
						set.Add(line.Trim());
					}
				}
			}
			return set;
		}

		// Player.log is still being written by Unity, so open it shared and only read what was added since the last call.
		private static IEnumerable<string> ReadNewLogEntries() {
			string path = Application.consoleLogPath;
			if (string.IsNullOrEmpty(path) || !File.Exists(path)) {
				yield break;
			}
			if (path != readPath_) {
				readPath_ = path;
				readOffset_ = 0;
			}

			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
			if (stream.Length < readOffset_) {
				readOffset_ = 0; // log was restarted
			}
			stream.Seek(readOffset_, SeekOrigin.Begin);
			using var reader = new StreamReader(stream);
			string line;
			while ((line = reader.ReadLine()) != null) {
				var m = Line.Match(line);
				if (!m.Success) {
					continue;
				}
				string shader = m.Groups["shader"].Value.Trim();
				if (!shader.StartsWith("NatureFX/", StringComparison.Ordinal)) {
					continue;
				}
				yield return $"{shader}|{m.Groups["pass"].Value.Trim()}|{m.Groups["stage"].Value.Trim()}|{NormalizeKeywords(m.Groups["keywords"].Value)}";
			}
			readOffset_ = stream.Position;
		}

		// "<no keywords>" -> "", otherwise the keywords sorted and space separated
		private static string NormalizeKeywords(string text) {
			var keywords = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
				.Where(k => !k.StartsWith("<"))
				.OrderBy(k => k, StringComparer.Ordinal);
			return string.Join(" ", keywords);
		}
	}
}

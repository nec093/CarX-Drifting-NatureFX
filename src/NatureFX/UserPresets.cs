using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using KSL.API;
using UnityEngine;

namespace NatureFX {
	// User presets: every ISettings property saved as "Key = value" text in kino/config/NatureFX/presets,
	// plus a one-line share code ("NFX1:" + base64 of the deflated text) for pasting into chats.
	// Properties are read by reflection, so new settings are included automatically; unknown keys in an
	// imported preset are ignored and missing ones keep their current value.
	public static class UserPresets {
		public const string Extension = ".nfxpreset";
		private const string ShareTag = "NFX1:";

		// machine / session specific values that a shared look should not change
		// window layout and development switches are per PC, not part of a look
		private static readonly HashSet<string> Excluded = new() {
			nameof(ISettings.ConfigVersion), nameof(ISettings.Enabled), nameof(ISettings.UiOpacity), nameof(ISettings.UiScale), nameof(ISettings.UiWidth),
			nameof(ISettings.UiHeight), nameof(ISettings.UiLargeText), nameof(ISettings.RecordShaderVariants), nameof(ISettings.DevRayTracingOnly)
		};

		private static readonly PropertyInfo[] Properties = typeof(ISettings).GetProperties()
			.Where(p => p.CanRead && p.CanWrite && !Excluded.Contains(p.Name))
			.OrderBy(p => p.Name, StringComparer.Ordinal)
			.ToArray();

		public static string Folder => Path.Combine(Kino.Paths.Config, "NatureFX", "presets");

		public static string[] List() {
			try {
				Directory.CreateDirectory(Folder);
				return Directory.GetFiles(Folder, "*" + Extension)
					.Select(Path.GetFileNameWithoutExtension)
					.OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
					.ToArray();
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Preset folder unavailable: {e.Message}");
				return Array.Empty<string>();
			}
		}

		public static bool Save(ISettings s, string name) {
			name = SanitizeName(name);
			if (name.Length == 0) {
				return false;
			}
			try {
				Directory.CreateDirectory(Folder);
				File.WriteAllText(Path.Combine(Folder, name + Extension), Serialize(s, name));
				Kino.Log.Info($"[NatureFX] Preset saved: {name}");
				return true;
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Could not save preset {name}: {e.Message}");
				return false;
			}
		}

		public static int Load(ISettings s, string name) {
			try {
				return Deserialize(s, File.ReadAllText(Path.Combine(Folder, SanitizeName(name) + Extension)));
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Could not load preset {name}: {e.Message}");
				return -1;
			}
		}

		public static bool Delete(string name) {
			try {
				File.Delete(Path.Combine(Folder, SanitizeName(name) + Extension));
				return true;
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Could not delete preset {name}: {e.Message}");
				return false;
			}
		}

		// One line share code for the current settings.
		public static string Export(ISettings s, string name) {
			return Encode(Serialize(s, name));
		}

		// Share code of a saved preset, without applying it.
		public static string ExportFile(string name) {
			try {
				return Encode(File.ReadAllText(Path.Combine(Folder, SanitizeName(name) + Extension)));
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Could not read preset {name}: {e.Message}");
				return null;
			}
		}

		private static string Encode(string text) {
			var raw = Encoding.UTF8.GetBytes(text);
			using var output = new MemoryStream();
			using (var deflate = new DeflateStream(output, System.IO.Compression.CompressionLevel.Optimal, true)) {
				deflate.Write(raw, 0, raw.Length);
			}
			return ShareTag + Convert.ToBase64String(output.ToArray());
		}

		// Accepts a share code or plain preset text. Returns the number of applied values (-1 = not a preset)
		// and the preset's name if it has one.
		public static int Import(ISettings s, string text, out string name) {
			name = null;
			if (string.IsNullOrWhiteSpace(text)) {
				return -1;
			}
			text = text.Trim();
			try {
				if (text.StartsWith(ShareTag, StringComparison.Ordinal)) {
					var bytes = Convert.FromBase64String(text.Substring(ShareTag.Length));
					using var input = new DeflateStream(new MemoryStream(bytes), CompressionMode.Decompress);
					using var reader = new StreamReader(input, Encoding.UTF8);
					text = reader.ReadToEnd();
				}
				name = ReadName(text);
				return Deserialize(s, text);
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Not a NatureFX preset: {e.Message}");
				return -1;
			}
		}

		public static string SanitizeName(string name) {
			if (name == null) {
				return "";
			}
			var invalid = Path.GetInvalidFileNameChars();
			var sb = new StringBuilder();
			foreach (char c in name.Trim()) {
				sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
			}
			return sb.ToString();
		}

		private static string Serialize(ISettings s, string name) {
			var sb = new StringBuilder();
			sb.AppendLine("# NatureFX preset");
			sb.AppendLine($"# name = {name}");
			sb.AppendLine($"# created = {DateTime.Now:yyyy-MM-dd HH:mm}");
			foreach (var p in Properties) {
				sb.AppendLine($"{p.Name} = {Format(p.GetValue(s))}");
			}
			return sb.ToString();
		}

		private static int Deserialize(ISettings s, string text) {
			var byName = Properties.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
			int applied = 0;
			bool any = false;
			foreach (string raw in text.Split('\n')) {
				string line = raw.Trim();
				if (line.Length == 0 || line[0] == '#') {
					continue;
				}
				int eq = line.IndexOf('=');
				if (eq <= 0) {
					continue;
				}
				any = true;
				if (!byName.TryGetValue(line.Substring(0, eq).Trim(), out var p)) {
					continue; // setting from a newer or older version
				}
				if (TryParse(line.Substring(eq + 1).Trim(), p.PropertyType, out var value)) {
					p.SetValue(s, value);
					++applied;
				}
			}
			return any ? applied : -1;
		}

		private static string ReadName(string text) {
			foreach (string raw in text.Split('\n')) {
				string line = raw.Trim();
				if (line.StartsWith("# name =", StringComparison.Ordinal)) {
					return line.Substring(8).Trim();
				}
			}
			return null;
		}

		private static string Format(object value) {
			switch (value) {
				case float f: return f.ToString("R", CultureInfo.InvariantCulture);
				case bool b: return b ? "true" : "false";
				case string str: return str.Replace("\r", "").Replace("\n", " ");
				case IFormattable fmt: return fmt.ToString(null, CultureInfo.InvariantCulture);
				default: return value?.ToString() ?? "";
			}
		}

		private static bool TryParse(string text, Type type, out object value) {
			value = null;
			if (type == typeof(float)) {
				if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) {
					value = f;
				}
			} else if (type == typeof(int)) {
				if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) {
					value = i;
				}
			} else if (type == typeof(bool)) {
				if (bool.TryParse(text, out var b)) {
					value = b;
				}
			} else if (type == typeof(string)) {
				value = text;
			}
			return value != null;
		}
	}
}

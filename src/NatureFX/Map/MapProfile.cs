using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KSL.API;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace NatureFX.Map {
	// Per-map settings (docs/map-authoring.md, section 1). Two places, same INI-like text:
	//   - outside the map: kino/config/NatureFX/maps/<scene name>.nfx (official tracks, OBJ maps, local tweaks)
	//   - inside the map: the text of a TextMesh on an object named nfx_profile, renderer off, so the map
	//     carries its profile and loads unchanged without NatureFX
	// The outside file wins when both exist. [section] headers, '#' comments, raw lines per section;
	// each consumer (rules, materials, markers ...) parses its own section.
	public class MapProfile {
		public const string Extension = ".nfx";
		public const string ProfileObjectName = "nfx_profile";

		private readonly Dictionary<string, List<string>> sections_ = new(StringComparer.OrdinalIgnoreCase);
		private string loadedKey_;

		public static string Folder => Path.Combine(Kino.Paths.Config, "NatureFX", "maps");

		public string FilePath { get; private set; }  // null for a profile read from the map
		public string Scene { get; private set; }
		public string Source { get; private set; }    // human readable origin for the UI and log
		public string InMapText { get; private set; } // the map's own profile, even when a file overrides it
		public bool Loaded => Source != null;
		public bool FromMap => Loaded && FilePath == null;
		public string Name => Get("map", "name") ?? Scene;
		public bool InheritGlobalRules => !string.Equals(Get("map", "inheritGlobalRules"), "false", StringComparison.OrdinalIgnoreCase);
		// the drawn foliage sways with the wind only where the map says its foliage meshes allow it (elsewhere trunks
		// merged into the same mesh swayed too); the traced shadows sway everywhere
		public bool FoliageWind => string.Equals(Get("map", "foliageWind"), "true", StringComparison.OrdinalIgnoreCase);

		// Scene names a profile can be named after (tracks load several scenes additively).
		public static List<string> LoadedSceneNames() {
			var names = new List<string>();
			for (int i = 0; i < SceneManager.sceneCount; ++i) {
				var scene = SceneManager.GetSceneAt(i);
				if (scene.isLoaded && !string.IsNullOrEmpty(scene.name)) {
					names.Add(scene.name);
				}
			}
			return names;
		}

		// Loads the outside file of the first loaded scene that has one, else the map's nfx_profile, else
		// clears. Returns true when the active profile (origin or contents) changed.
		public bool Load(bool enabled) {
			InMapText = FindInMapText(out var inMapObject);
			if (!enabled) {
				bool wasLoaded = Loaded;
				Clear();
				return wasLoaded;
			}

			foreach (string name in LoadedSceneNames()) {
				string path = Path.Combine(Folder, name + Extension);
				if (!File.Exists(path)) {
					continue;
				}
				string key = "file:" + path + ":" + File.GetLastWriteTimeUtc(path).Ticks;
				if (key == loadedKey_) {
					return false;
				}
				try {
					return Apply(File.ReadAllLines(path), key, path, name, path);
				} catch (Exception e) {
					Kino.Log.Error($"[NatureFX] Failed to read map profile '{path}': {e.Message}");
					Clear();
					return true;
				}
			}

			if (InMapText != null) {
				string key = "map:" + inMapObject.GetInstanceID() + ":" + InMapText.GetHashCode();
				if (key == loadedKey_) {
					return false;
				}
				var scene = inMapObject.scene.name;
				return Apply(InMapText.Split('\n'), key, null, scene, $"the map ({ProfileObjectName} in scene '{scene}')");
			}

			bool changed = Loaded;
			Clear();
			return changed;
		}

		private bool Apply(string[] lines, string key, string filePath, string scene, string source) {
			Clear();
			Parse(lines);
			FilePath = filePath;
			Scene = scene;
			Source = source;
			loadedKey_ = key;
			Kino.Log.Info($"[NatureFX] Map profile '{Name}' loaded from {source} ({string.Join(", ", SectionSummary())})");
			return true;
		}

		// TextMesh on an object named nfx_profile (inactive objects count too)
		private static string FindInMapText(out GameObject owner) {
			owner = null;
			foreach (var text in UnityEngine.Object.FindObjectsByType<TextMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				if (text.gameObject.name.StartsWith(ProfileObjectName, StringComparison.Ordinal) && !string.IsNullOrEmpty(text.text)) {
					owner = text.gameObject;
					return text.text.Replace("\r\n", "\n");
				}
			}
			return null;
		}

		// Copies the map's own profile to the outside file, where it overrides the map and can be edited.
		public string ExportInMapProfile() {
			if (InMapText == null) {
				return "This map has no nfx_profile";
			}
			string scene = Scene ?? LoadedSceneNames().FirstOrDefault() ?? "map";
			string path = Path.Combine(Folder, scene + Extension);
			try {
				Directory.CreateDirectory(Folder);
				if (File.Exists(path)) {
					File.Copy(path, path + ".bak", true);
				}
				File.WriteAllText(path, InMapText);
				return $"Written: {path}";
			} catch (Exception e) {
				return $"Could not write {path}: {e.Message}";
			}
		}

		// Writes a commented starter profile for this scene; returns a message for the UI.
		public static string WriteTemplate(string scene) {
			string path = Path.Combine(Folder, scene + Extension);
			if (File.Exists(path)) {
				return $"Already exists: {path}";
			}
			try {
				Directory.CreateDirectory(Folder);
				File.WriteAllText(path, string.Format(Template, scene));
				return $"Written: {path}";
			} catch (Exception e) {
				return $"Could not write {path}: {e.Message}";
			}
		}

		private const string Template =
			"# NatureFX map profile for scene '{0}' (docs/map-authoring.md)\n" +
			"# Lines starting with # are comments. Edit, then F8 > Tools > Reload map profile.\n" +
			"\n" +
			"[map]\n" +
			"name = {0}\n" +
			"inheritGlobalRules = true\n" +
			"\n" +
			"[rules]\n" +
			"# <game shader regex> | <name regex> => <NatureFX shader>   (same as NatureFX.rules.txt, checked first)\n" +
			"# (?i)^HDRP/Lit$ | (^|/)road_ => NatureFX/LitAccumulation\n" +
			"\n" +
			"[materials]\n" +
			"# <material or object name regex> => <material>[, key=value ...]\n" +
			"# materials: asphalt_old asphalt_new asphalt_paint concrete masonry rock metal_galvanized metal_painted glass plastic wood soil\n" +
			"# keys (0..1): porosity moss rust stain wear dirt beading retro\n" +
			"# (?i)guardrail => metal_galvanized, rust=0.2\n" +
			"# (?i)masonry => masonry, moss=0.6\n" +
			"\n" +
			"[emissive]\n" +
			"# <material or object name regex> => nits=<cd/m2>, k=<kelvin>, when=night|dusk|always, mask=bright|alpha|full, random=<0..1>, flicker=<0..1>\n" +
			"# (?i)led_sign => nits=800, k=6500, when=always\n" +
			"# (?i)window => nits=60, k=2700, when=night, random=0.6\n" +
			"\n" +
			"[markers]\n" +
			"# <kind>_<preset> = x, y, z[, key=value ...]   (F8 > Tools > Copy camera position)\n" +
			"# light_sodium = 120.5, 34.0, -88.2, yaw=90, pitch=70\n";

		public void Clear() {
			sections_.Clear();
			FilePath = null;
			Scene = null;
			Source = null;
			loadedKey_ = null;
		}

		public IReadOnlyList<string> Section(string name) {
			return sections_.TryGetValue(name, out var lines) ? lines : Array.Empty<string>();
		}

		// "key = value" inside a section; the last occurrence wins.
		public string Get(string section, string key) {
			string result = null;
			foreach (string line in Section(section)) {
				int eq = line.IndexOf('=');
				if (eq > 0 && string.Equals(line.Substring(0, eq).Trim(), key, StringComparison.OrdinalIgnoreCase)) {
					result = line.Substring(eq + 1).Trim();
				}
			}
			return result;
		}

		private void Parse(string[] lines) {
			List<string> current = null;
			foreach (string raw in lines) {
				string line = StripComment(raw).Trim();
				if (line.Length == 0) {
					continue;
				}
				if (line.StartsWith("[") && line.EndsWith("]")) {
					string name = line.Substring(1, line.Length - 2).Trim();
					if (!sections_.TryGetValue(name, out current)) {
						current = new List<string>();
						sections_[name] = current;
					}
					continue;
				}
				current?.Add(line);
			}
		}

		// A comment starts with '#' at the beginning of the line or after whitespace, so regexes
		// in [rules] / [materials] may still contain '#' as long as it follows a non-space character.
		private static string StripComment(string line) {
			for (int i = 0; i < line.Length; ++i) {
				if (line[i] == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) {
					return line.Substring(0, i);
				}
			}
			return line;
		}

		private IEnumerable<string> SectionSummary() {
			foreach (var kv in sections_) {
				yield return $"[{kv.Key}] {kv.Value.Count}";
			}
		}
	}
}

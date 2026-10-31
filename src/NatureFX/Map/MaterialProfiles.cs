using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using KSL.API;
using UnityEngine;

namespace NatureFX.Map {
	// [materials] and [emissive] of the map profile (docs/map-authoring.md, 6.2 / 7.1):
	//   <name regex> => <preset>[, key=value ...]       e.g.  (?i)guardrail => metal_galvanized, rust=0.2
	//   <name regex> => nits=800, k=6500, when=night    (emissive)
	// The regex is tested against the material name and the object path, like the replacement rules.
	// Values end up in _NFX_Material* of the generated Lit copies (ShaderLibrary/NFX_MaterialProfile.hlsl).
	public class MaterialProfiles {
		private class Look {
			public float Porosity = -1.0f;
			public float Moss, Rust, Stain, Wear, Dirt, Beading, Retro;
			public bool Mirror; // curve mirrors: a perfect metal mirror (the ray traced reflections draw the picture)
			// anti-tiling (NFX_AntiTiling.hlsl): hex tiles against a texture's visible repeat, 0 .. 1; a large scale
			// variation of brightness and roughness over Size metres against the same ground going on and on
			public float AntiTiling, Variation, VariationSize = 20.0f;

			public Look Clone() {
				return (Look)MemberwiseClone();
			}
		}

		private class Entry {
			public Regex Name;
			public string Text;
			public Look Look;
			public Vector4 Emission;
			public Vector4 EmissionParams;
			public Vector4 Foliage;
			public Vector4 Terrain;
			public Vector4 Terrain2;
			public string Steep;   // material whose colour map the steep faces take
			public string Flat;
		}

		private static readonly Dictionary<string, Look> Presets = new(StringComparer.OrdinalIgnoreCase) {
			["asphalt_old"] = new Look { Porosity = 1.0f, Wear = 0.4f, Variation = 0.35f },
			["asphalt_new"] = new Look { Porosity = 0.9f, Variation = 0.25f },
			["asphalt_paint"] = new Look { Porosity = 0.3f, Beading = 0.3f, Wear = 0.2f },
			["concrete"] = new Look { Porosity = 0.8f, Stain = 0.3f, Variation = 0.3f },
			["masonry"] = new Look { Porosity = 0.9f, Moss = 0.3f, Variation = 0.3f },
			["rock"] = new Look { Porosity = 0.7f, Moss = 0.15f, Variation = 0.4f },
			["metal_galvanized"] = new Look { Porosity = 0.0f, Beading = 0.8f, Dirt = 0.1f },
			["metal_painted"] = new Look { Porosity = 0.05f, Beading = 0.6f },
			["glass"] = new Look { Porosity = 0.0f, Beading = 1.0f },
			["plastic"] = new Look { Porosity = 0.05f, Beading = 0.5f },
			["wood"] = new Look { Porosity = 0.8f, Dirt = 0.1f },
			["soil"] = new Look { Porosity = 1.0f, Variation = 0.4f },
			["mirror"] = new Look { Porosity = 0.0f, Beading = 0.4f, Mirror = true }
		};

		private static readonly int MaterialId = Shader.PropertyToID("_NFX_Material");
		private static readonly int Material2Id = Shader.PropertyToID("_NFX_Material2");
		private static readonly int SurfaceId = Shader.PropertyToID("_NFX_Surface");
		private static readonly int EmissionId = Shader.PropertyToID("_NFX_Emission");
		private static readonly int EmissionParamsId = Shader.PropertyToID("_NFX_EmissionParams");
		private static readonly int LightsId = Shader.PropertyToID("_NFX_Lights");
		private static readonly int FoliageId = Shader.PropertyToID("_NFX_Foliage");
		private static readonly int SunDirId = Shader.PropertyToID("_NFX_SunDir");
		private static readonly int TerrainId = Shader.PropertyToID("_NFX_Terrain");
		private static readonly int Terrain2Id = Shader.PropertyToID("_NFX_Terrain2");
		private static readonly int SteepMapId = Shader.PropertyToID("_NFX_SteepMap");
		private static readonly int FlatMapId = Shader.PropertyToID("_NFX_FlatMap");

		// [foliage] type names in the order of _NFX_Foliage.x (NFX_Foliage.hlsl)
		private static readonly string[] FoliageTypes = { "", "conifer", "deciduous", "bamboo", "grass" };
		private static readonly string[] AutumnColours = { "mixed", "red", "yellow", "brown" };

		private readonly List<Entry> materials_ = new();
		private readonly List<Entry> emissive_ = new();
		private readonly List<Entry> foliage_ = new();
		private readonly List<Entry> terrain_ = new();
		private readonly Dictionary<string, Texture> mapTextures_ = new(StringComparer.OrdinalIgnoreCase);

		public int MaterialCount => materials_.Count;
		public int EmissiveCount => emissive_.Count;
		public int FoliageCount => foliage_.Count;
		public int TerrainCount => terrain_.Count;
		public bool Any => materials_.Count + emissive_.Count + foliage_.Count + terrain_.Count > 0;
		public static IEnumerable<string> PresetNames => Presets.Keys;

		public void Load(MapProfile profile) {
			materials_.Clear();
			emissive_.Clear();
			foliage_.Clear();
			terrain_.Clear();
			mapTextures_.Clear();
			if (!profile.Loaded) {
				return;
			}

			foreach (string line in profile.Section("materials")) {
				if (TryParse(line, out var entry, out var values)) {
					entry.Look = ParseLook(values, line);
					if (entry.Look != null) {
						materials_.Add(entry);
					}
				}
			}
			foreach (string line in profile.Section("emissive")) {
				if (TryParse(line, out var entry, out var values)) {
					ParseEmission(entry, values);
					emissive_.Add(entry);
				}
			}
			foreach (string line in profile.Section("foliage")) {
				if (TryParse(line, out var entry, out var values)) {
					entry.Foliage = ParseFoliage(values, line);
					foliage_.Add(entry);
				}
			}
			foreach (string line in profile.Section("terrain")) {
				if (TryParse(line, out var entry, out var values)) {
					ParseTerrain(entry, values);
					terrain_.Add(entry);
				}
			}
			if (Any) {
				Kino.Log.Info($"[NatureFX] Map materials: {materials_.Count} material, {emissive_.Count} emissive, {foliage_.Count} foliage, {terrain_.Count} terrain entries");
			}
		}

		// [foliage] entries switch a HDRP/Lit material to NatureFX/Foliage (MaterialReplacer.ResolveTarget)
		public bool MatchesFoliage(Material m, string objectPath) {
			return Find(foliage_, m, objectPath) != null;
		}

		public bool Matches(Material m, string objectPath) {
			return Find(materials_, m, objectPath) != null || Find(emissive_, m, objectPath) != null || Find(terrain_, m, objectPath) != null;
		}

		// Always writes every value, so a material that no longer matches goes back to the defaults.
		public void Apply(Material m, string objectPath) {
			if (m == null || !m.HasProperty(MaterialId)) {
				return;
			}
			var look = Find(materials_, m, objectPath)?.Look ?? new Look();
			var emissive = Find(emissive_, m, objectPath);
			m.SetVector(MaterialId, new Vector4(look.Porosity, look.Moss, look.Rust, look.Stain));
			m.SetVector(Material2Id, new Vector4(look.Wear, look.Dirt, look.Beading, look.Retro));
			if (m.HasProperty(SurfaceId)) {
				m.SetVector(SurfaceId, new Vector4(look.AntiTiling, look.Variation, look.VariationSize, 0.0f));
			}
			if (look.Mirror) {
				m.SetColor("_BaseColor", new Color(0.95f, 0.95f, 0.95f, 1.0f));
				m.SetFloat("_Metallic", 1.0f);
				m.SetFloat("_Smoothness", 1.0f);
				m.SetFloat("_SmoothnessRemapMin", 1.0f);
				m.SetFloat("_SmoothnessRemapMax", 1.0f);
			}
			m.SetVector(EmissionId, emissive?.Emission ?? Vector4.zero);
			m.SetVector(EmissionParamsId, emissive?.EmissionParams ?? new Vector4(0.0f, 1.0f, 0.0f, 0.0f));
			if (m.HasProperty(FoliageId)) {
				m.SetVector(FoliageId, Find(foliage_, m, objectPath)?.Foliage ?? Vector4.zero);
			}
			if (m.HasProperty(TerrainId)) {
				var terrain = Find(terrain_, m, objectPath);
				var steep = terrain != null ? MapTexture(terrain.Steep) : null;
				var flat = terrain != null ? MapTexture(terrain.Flat) : null;
				// tile 0: no [terrain] entry (NFX_ApplyTerrain does nothing)
				var t = terrain?.Terrain ?? Vector4.zero;
				var t2 = terrain?.Terrain2 ?? Vector4.zero;
				t2.x = steep != null ? 1.0f : 0.0f;
				t2.y = flat != null ? 1.0f : 0.0f;
				m.SetVector(TerrainId, t);
				m.SetVector(Terrain2Id, t2);
				m.SetTexture(SteepMapId, steep);
				m.SetTexture(FlatMapId, flat);
			}
		}

		// [terrain]: <name regex> => triplanar, tile=4, steep=<material>, flat=<material>, slope=0.55, blend=0.15, noise=0.5
		private static void ParseTerrain(Entry entry, List<string> values) {
			var t = new Vector4(0.0f, 4.0f, 0.55f, 0.15f);
			var t2 = new Vector4(0.0f, 0.0f, 0.5f, 4.0f);
			foreach (string v in values) {
				if (!SplitKeyValue(v, out string key, out string value)) {
					if (string.Equals(v, "triplanar", StringComparison.OrdinalIgnoreCase)) {
						t.x = 1.0f;
					} else {
						Kino.Log.Warning($"[NatureFX] Map profile: '{v}' in '{entry.Text}' is not key=value or triplanar");
					}
					continue;
				}
				// the material names keep their case (SplitKeyValue lowers the value)
				string raw = v.Substring(v.IndexOf('=') + 1).Trim();
				switch (key.ToLowerInvariant()) {
					case "triplanar": t.x = value == "false" || value == "off" ? 0.0f : 1.0f; break;
					case "tile": t.y = Mathf.Max(0.1f, ParseFloat(value)); break;
					case "slope": t.z = Mathf.Clamp01(ParseFloat(value)); break;
					case "blend": t.w = Mathf.Clamp(ParseFloat(value), 0.01f, 0.5f); break;
					case "noise": t2.z = Mathf.Clamp01(ParseFloat(value)); break;
					case "maptile": t2.w = Mathf.Max(0.1f, ParseFloat(value)); break;
					case "steep": entry.Steep = raw; break;
					case "flat": entry.Flat = raw; break;
					default: Kino.Log.Warning($"[NatureFX] Map profile: unknown key '{key}' in '{entry.Text}'"); break;
				}
			}
			entry.Terrain = t;
			entry.Terrain2 = t2;
		}

		// the colour map of a material of the map, by its name
		private Texture MapTexture(string materialName) {
			if (string.IsNullOrEmpty(materialName)) {
				return null;
			}
			if (mapTextures_.TryGetValue(materialName, out var cached)) {
				return cached;
			}
			Texture found = null;
			foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) {
				if (m != null && string.Equals(m.name.Replace(" (Instance)", ""), materialName, StringComparison.OrdinalIgnoreCase)) {
					found = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : m.HasProperty("_MainTex") ? m.GetTexture("_MainTex") : null;
					if (found != null) {
						break;
					}
				}
			}
			if (found == null) {
				Kino.Log.Warning($"[NatureFX] [terrain]: no material '{materialName}' with a colour map in the map");
			}
			mapTextures_[materialName] = found;
			return found;
		}

		// x: type, y: translucency, z: variation, w: autumn colour (docs/map-authoring.md 5.2)
		private static Vector4 ParseFoliage(List<string> values, string line) {
			var f = new Vector4(2.0f, 0.5f, 0.2f, 0.0f); // a broadleaf tree unless told otherwise
			bool translucencySet = false;
			foreach (string v in values) {
				if (!SplitKeyValue(v, out string key, out string value)) {
					Kino.Log.Warning($"[NatureFX] Map profile: '{v}' in '{line}' is not key=value");
					continue;
				}
				switch (key.ToLowerInvariant()) {
					case "type":
						int type = System.Array.IndexOf(FoliageTypes, value);
						if (type <= 0) {
							Kino.Log.Warning($"[NatureFX] Map profile: unknown foliage type '{value}' in '{line}' (conifer, deciduous, bamboo, grass)");
						} else {
							f.x = type;
						}
						break;
					case "translucency": f.y = Mathf.Clamp01(ParseFloat(value)); translucencySet = true; break;
					case "variation": f.z = Mathf.Clamp01(ParseFloat(value)); break;
					case "autumn":
						int colour = System.Array.IndexOf(AutumnColours, value);
						if (colour < 0) {
							Kino.Log.Warning($"[NatureFX] Map profile: unknown autumn colour '{value}' in '{line}' (red, yellow, mixed, brown)");
						} else {
							f.w = colour;
						}
						break;
					default: Kino.Log.Warning($"[NatureFX] Map profile: unknown key '{key}' in '{line}'"); break;
				}
			}
			if (!translucencySet) {
				// needles let little light through, broad leaves and blades much more
				f.y = f.x == 1.0f ? 0.25f : f.x == 3.0f ? 0.45f : f.x == 4.0f ? 0.5f : 0.6f;
			}
			return f;
		}

		// x: sun elevation in degrees (lamps switch on by it), y: time for flicker
		public static void UpdateGlobals(Light sun) {
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			Shader.SetGlobalVector(LightsId, new Vector4(elevation, Time.unscaledTime, 0.0f, 0.0f));
			// towards the sun, and how much day there is for the back lit leaves (NFX_Foliage.hlsl)
			var toSun = sun != null ? -sun.transform.forward : Vector3.up;
			Shader.SetGlobalVector(SunDirId, new Vector4(toSun.x, toSun.y, toSun.z, Mathf.Clamp01((elevation + 2.0f) / 10.0f)));
		}

		private static Entry Find(List<Entry> entries, Material m, string objectPath) {
			foreach (var e in entries) {
				if (e.Name.IsMatch(m.name) || (objectPath != null && e.Name.IsMatch(objectPath))) {
					return e;
				}
			}
			return null;
		}

		private static bool TryParse(string line, out Entry entry, out List<string> values) {
			entry = null;
			values = null;
			int arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
			if (arrow <= 0) {
				Kino.Log.Warning($"[NatureFX] Map profile: missing '=>' in '{line}'");
				return false;
			}
			try {
				entry = new Entry { Name = new Regex(line.Substring(0, arrow).Trim()), Text = line };
			} catch (ArgumentException e) {
				Kino.Log.Warning($"[NatureFX] Map profile: bad regex in '{line}': {e.Message}");
				return false;
			}
			values = new List<string>();
			foreach (string v in line.Substring(arrow + 2).Split(',')) {
				if (v.Trim().Length > 0) {
					values.Add(v.Trim());
				}
			}
			return true;
		}

		private static Look ParseLook(List<string> values, string line) {
			Look look = null;
			foreach (string v in values) {
				if (!SplitKeyValue(v, out string key, out string value)) {
					if (!Presets.TryGetValue(v, out var preset)) {
						Kino.Log.Warning($"[NatureFX] Map profile: unknown material '{v}' in '{line}' (known: {string.Join(", ", Presets.Keys)})");
						return null;
					}
					look = preset.Clone();
					continue;
				}
				look ??= new Look();
				float f = ParseFloat(value);
				switch (key.ToLowerInvariant()) {
					case "porosity": look.Porosity = Mathf.Clamp01(f); break;
					case "moss": look.Moss = Mathf.Clamp01(f); break;
					case "rust": look.Rust = Mathf.Clamp01(f); break;
					case "stain": look.Stain = Mathf.Clamp01(f); break;
					case "wear": look.Wear = Mathf.Clamp01(f); break;
					case "dirt": look.Dirt = Mathf.Clamp01(f); break;
					case "beading": look.Beading = Mathf.Clamp01(f); break;
					case "retro": look.Retro = Mathf.Clamp01(f); break;
					case "antitiling": look.AntiTiling = Mathf.Clamp01(f); break;
					case "variation": look.Variation = Mathf.Clamp01(f); break;
					case "variation_size": look.VariationSize = Mathf.Max(1.0f, f); break;
					default: Kino.Log.Warning($"[NatureFX] Map profile: unknown key '{key}' in '{line}'"); break;
				}
			}
			return look;
		}

		private static void ParseEmission(Entry entry, List<string> values) {
			float nits = 100.0f;
			float kelvin = 6500.0f;
			float when = 1.0f;
			float mask = 0.0f;
			float share = 1.0f;
			float flicker = 0.0f;
			foreach (string v in values) {
				if (!SplitKeyValue(v, out string key, out string value)) {
					continue;
				}
				switch (key.ToLowerInvariant()) {
					case "nits": nits = Mathf.Max(0.0f, ParseFloat(value)); break;
					case "k": kelvin = Mathf.Clamp(ParseFloat(value), 1000.0f, 20000.0f); break;
					case "when": when = value == "always" ? 3.0f : value == "dusk" ? 2.0f : value == "off" ? 0.0f : 1.0f; break;
					case "mask": mask = value == "alpha" ? 1.0f : value == "full" ? 2.0f : 0.0f; break;
					case "random": share = Mathf.Clamp01(ParseFloat(value)); break;
					case "flicker": flicker = Mathf.Clamp01(ParseFloat(value)); break;
					default: Kino.Log.Warning($"[NatureFX] Map profile: unknown key '{key}' in '{entry.Text}'"); break;
				}
			}

			// colour of a black body at that temperature, scaled so its luminance is 'nits'
			var c = Mathf.CorrelatedColorTemperatureToRGB(kelvin);
			float luminance = Mathf.Max(0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b, 1e-4f);
			float scale = nits / luminance;
			entry.Emission = new Vector4(c.r * scale, c.g * scale, c.b * scale, when);
			entry.EmissionParams = new Vector4(mask, share, flicker, 0.0f);
		}

		private static bool SplitKeyValue(string text, out string key, out string value) {
			int eq = text.IndexOf('=');
			key = eq > 0 ? text.Substring(0, eq).Trim() : null;
			value = eq > 0 ? text.Substring(eq + 1).Trim().ToLowerInvariant() : null;
			return eq > 0;
		}

		private static float ParseFloat(string value) {
			return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0.0f;
		}
	}
}

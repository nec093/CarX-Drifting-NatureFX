using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using KSL.API;
using NatureFX.Map;
using NatureFX.Rebuild;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Object = UnityEngine.Object;

namespace NatureFX.Nature {
	// Swaps game shaders for NFX shaders by rules loaded from <kino config>/NatureFX.rules.txt.
	// NFX graphs use the same property names as HDRP/Lit (_BaseColorMap, _NormalMap, _MaskMap ...),
	// so textures and colors carry over when only the shader is changed.
	public class MaterialReplacer {
		private class Rule {
			public Regex Shader;
			public Regex Name;
			public string Target;
		}

		private const string RulesFileName = "NatureFX.rules.txt";
		private const int ObjectPathDepth = 3;

		private const string DefaultRules =
			"# NatureFX material replacement rules\n" +
			"# <game shader regex> | <name regex> => <NFX shader name>\n" +
			"# <name regex> is tested against the material name AND the object path of every renderer using it\n" +
			"# (up to 3 levels, e.g. 'Track/Road/main_LOD0'). CarX hashes most material names, so prefer object names.\n" +
			"# Materials are shared: if any renderer matches, every object using that material is changed.\n" +
			"# The separator is ' | ' with spaces; '|' without spaces is regex alternation.\n" +
			"# The first matching rule wins. Use the shader dump (mod menu > Tools) to find names.\n" +
			"# Lines starting with # are ignored.\n" +
			"\n" +
			"(?i)(foliage|grass|vegetation|leaf|leaves|bush|tree|speedtree) | .* => NatureFX/Foliage\n" +
			"\n" +
			"# KN maps: the object name prefix is the Kino surface tag (road_ grass_ kerb_ gravel_ dirt_ sand_ snow_ icyroad_,\n" +
			"# nocol_, alpha_/alphaCol_/alphaLit_ for transparent cards). Keep these above the generic name rules,\n" +
			"# otherwise 'grass_' ground would be caught as foliage. Tags are case-sensitive.\n" +
			"(?i)^HDRP/Lit$ | (^|/)alpha(Lit|Col)?_ => NatureFX/Foliage\n" +
			"(?i)^HDRP/Lit$ | (^|/)(road|kerb|gravel|dirt|sand|grass|snow|icyroad)_ => NatureFX/LitAccumulation\n" +
			"\n" +
			"# Generic name rules (official tracks and other maps). Ground words must be whole words;\n" +
			"# signs, billboards and wheels are excluded.\n" +
			"(?i)^HDRP/Lit$ | ^(?!.*(?i:bil+board|banner|sign|wheel|tire|tyre|vehicle))(?i).*(grass|leaf|leaves|bush|foliage|fern|hedge) => NatureFX/Foliage\n" +
			"(?i)^HDRP/Lit$ | ^(?!.*(?i:bil+board|banner|sign|wheel|tire|tyre|vehicle))(?i).*(^|[/_ -])(ground|terrain|dirt|soil|rock|cliff|gravel|road|asphalt|concrete|curb|kerb|sidewalk|roof)([/_ -]|[0-9]|$) => NatureFX/LitAccumulation\n";

		private readonly List<Rule> rules_ = new();

		private const string FoliageShader = "NatureFX/Foliage";
		public bool RebuildFoliage = true;
		private const string ProfileShader = "NatureFX/LitAccumulation";

		private readonly Dictionary<Material, MaterialSnapshot> originals_ = new();

		// Textures a translator moved from the game's property names to HDRP/Lit's (game shader _MainTex -> _BaseColorMap):
		// the game may assign its finished textures after the switch (a map reload: a small stand-in first, the full
		// texture later) under its own names, which the NFX shader does not read. Resync follows them.
		private readonly Dictionary<Material, List<(string From, string To)>> links_ = new();
		private static readonly string[] LinkedTargets = { "_BaseColorMap", "_NormalMap", "_MaskMap", "_EmissiveColorMap" };
		private float nextResync_;
		public int Resynced { get; private set; }
		public int Unreadable { get; private set; }

		// Shader rebuild: every game material the rules leave alone becomes a NatureFX
		// material too (GenericTranslator), except what later stages rebuild or what is not a surface
		public bool Full { get; set; }

		// Why materials were left as they are, from the last Apply: reason -> materials
		public readonly Dictionary<string, int> Deferred = new();

		// "NFX shader | keywords" of every material switched since start: the variants the bundle must compile
		// (Tools/SceneShaderDumper.MergeVariants writes them to NatureFX_variants.txt on each map load)
		public readonly SortedSet<string> UsedVariants = new(StringComparer.Ordinal);

		public MaterialSnapshot OriginalOf(Material m) {
			return m != null && originals_.TryGetValue(m, out var snapshot) ? snapshot : null;
		}

		private static Type carType_;
		private static bool carTypeResolved_;

		// The game's car component (players' and AI cars)
		private static Type CarType {
			get {
				if (!carTypeResolved_) {
					carTypeResolved_ = true;
					carType_ = HarmonyLib.AccessTools.TypeByName("BaseCar");
				}
				return carType_;
			}
		}

		private static readonly string[] ExcludedShaderPrefixes = {
			"Hidden/", "Sprites/", "UI/", "TextMeshPro", "GUI/", "Skybox/", "HDRP/Decal", "Shader Graphs/Decal", "HDRP/Custom/",
			"HDRP/Sky", "Legacy Shaders/Particles", "Particles/", "Mobile/Particles", "HDRP/AxF", "HDRP/StackLit", "HDRP/Hair",
			"HDRP/Eye", "HDRP/Fabric"
		};
		private int foliageDiagnostics_;

		public int ReplacedCount => originals_.Count;
		public string RulesPath => Path.Combine(Kino.Paths.Config, RulesFileName);

		// Map profile looks ([materials] / [emissive]); materials they match are switched to the ground shader
		// even without a replacement rule, since only the NatureFX shaders read those values.
		public MaterialProfiles Profiles { get; set; }

		// Rules of the current map profile come first; the global rules follow unless the profile opts out.
		public void LoadRules(MapProfile map = null) {
			rules_.Clear();
			int mapRules = 0;
			if (map != null && map.Loaded) {
				foreach (string line in map.Section("rules")) {
					if (ParseRule(line)) {
						++mapRules;
					}
				}
				if (!map.InheritGlobalRules) {
					Kino.Log.Info($"[NatureFX] {mapRules} map replacement rules loaded (global rules not inherited)");
					return;
				}
			}

			string path = RulesPath;
			try {
				if (!File.Exists(path)) {
					File.WriteAllText(path, DefaultRules);
				}

				foreach (string raw in File.ReadAllLines(path)) {
					string line = raw.Trim();
					if (line.Length > 0 && !line.StartsWith("#")) {
						ParseRule(line);
					}
				}
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Failed to read rules '{path}': {e.Message}");
			}

			Kino.Log.Info($"[NatureFX] {rules_.Count} replacement rules loaded ({mapRules} from the map profile)");
		}

		private bool ParseRule(string line) {
			// " | " (with spaces) separates the fields; a bare '|' belongs to the regex.
			int arrow = line.LastIndexOf("=>", StringComparison.Ordinal);
			int bar = line.IndexOf(" | ", StringComparison.Ordinal);
			if (arrow < 0 || bar < 0 || bar > arrow) {
				Kino.Log.Warning($"[NatureFX] Bad rule: {line}");
				return false;
			}

			try {
				rules_.Add(new Rule {
					Shader = new Regex(line.Substring(0, bar).Trim()),
					Name = new Regex(line.Substring(bar + 3, arrow - bar - 3).Trim()),
					Target = line.Substring(arrow + 2).Trim()
				});
				return true;
			} catch (ArgumentException e) {
				Kino.Log.Warning($"[NatureFX] Bad regex in rule '{line}': {e.Message}");
				return false;
			}
		}

		public void Apply(ShaderBundle bundle) {
			if (!bundle.Loaded) {
				return;
			}

			int replaced = 0;
			var samples = new List<string>();
			Deferred.Clear();
			var replacedNow = new HashSet<Material>();
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string path = null;
				bool touched = false;
				var materials = r.sharedMaterials;
				foreach (var m in materials) {
					if (m == null) {
						continue;
					}
					if (originals_.ContainsKey(m)) {
						touched |= replacedNow.Contains(m); // switched for an earlier renderer that shares it
						continue;
					}
					path ??= ObjectPath(r.transform);
					string from = m.shader != null ? m.shader.name : "-";
					string target = Classify(m, r, path, out string reason);
					if (target == null) {
						if (reason != null) {
							Deferred.TryGetValue(reason, out int n);
							Deferred[reason] = n + 1;
						}
						continue;
					}
					if (TryReplace(m, path, bundle, target)) {
						++replaced;
						touched = true;
						replacedNow.Add(m);
						if (samples.Count < 5) {
							samples.Add($"{m.name}#{m.GetInstanceID()} ({from} -> {m.shader.name}) @ {path}");
						}
						Diagnose(m, r, path);
					}
				}
				// the shader was switched on the material itself, which the renderer is not told of: Unity's texture
				// mip streaming kept the texture list it read from the old shader and dropped the swapped material's
				// textures to their smallest allowed mips (blurred windows and walls once the budget is short).
				// Assigning the same materials again makes the renderer read them anew.
				if (touched) {
					r.sharedMaterials = materials;
				}
			}

			foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string path = ObjectPath(t.transform);
				string target = t.materialTemplate != null ? ResolveTarget(t.materialTemplate, path) : null;
				if (target != null && TryReplace(t.materialTemplate, path, bundle, target)) {
					++replaced;
				}
			}
			LastReplaced = replaced;

			if (replaced > 0) {
				Kino.Log.Info($"[NatureFX] Replaced shaders on {replaced} materials (total {originals_.Count}), e.g.\n  {string.Join("\n  ", samples)}");
			}
		}

		// Debug page: the textures of the switched materials as the GPU holds them now (blurred textures with the full
		// replacement after a map reload): how many sit below their full size mip by Unity's mip streaming, and the
		// colour maps a translator picked smaller than the largest texture of the game's material
		public List<(string Label, string Value)> TextureReport(int worst = 12) {
			var rows = new List<(string, string)>();
			int alive = 0, stale = 0, textures = 0, streamed = 0, reduced = 0, wantReduced = 0, requested = 0, smallerBase = 0;
			var seen = new HashSet<Texture>();
			var bad = new List<(int Level, string Text)>();
			var smaller = new List<string>();
			foreach (var kv in originals_) {
				var m = kv.Key;
				if (m == null) {
					++stale;
					continue;
				}
				++alive;
				var shader = m.shader;
				if (shader == null) {
					continue;
				}
				for (int i = 0; i < shader.GetPropertyCount(); ++i) {
					if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) {
						continue;
					}
					string prop = shader.GetPropertyName(i);
					var t = m.GetTexture(prop);
					if (t == null || !seen.Add(t)) {
						continue;
					}
					++textures;
					if (t is Texture2D t2 && t2.streamingMipmaps) {
						++streamed;
						int loaded = t2.loadedMipmapLevel, desired = t2.desiredMipmapLevel;
						if (t2.requestedMipmapLevel > 0) {
							++requested;
						}
						if (desired > 0) {
							++wantReduced;
						}
						if (loaded > 0) {
							++reduced;
							bad.Add((loaded, $"{m.name} {prop} = {t.name} {t.width}x{t.height}: mip {loaded} loaded, {desired} wanted ({kv.Value.Shader?.name} -> {shader.name})"));
						}
					}
				}
				// the colour map now, against the largest texture the game's material had
				var baseNow = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : null;
				if (baseNow != null) {
					Texture largest = null;
					string largestName = null;
					foreach (var t in kv.Value.Textures) {
						if (t.Value.Texture != null && (largest == null || t.Value.Texture.width * t.Value.Texture.height > largest.width * largest.height)) {
							largest = t.Value.Texture;
							largestName = t.Key;
						}
					}
					if (largest != null && largest.width * largest.height > baseNow.width * baseNow.height * 2) {
						++smallerBase;
						if (smaller.Count < worst) {
							smaller.Add($"{m.name}: colour map {baseNow.name} {baseNow.width}x{baseNow.height}, the game's {largestName} = {largest.name} {largest.width}x{largest.height} ({kv.Value.Shader?.name})");
						}
					}
				}
			}
			rows.Add(("switched materials", $"{alive} (and {stale} of an earlier scene, gone)"));
			rows.Add(("their textures", $"{textures}, {streamed} mip streamed"));
			rows.Add(("below full size now", $"{reduced} loaded smaller, {wantReduced} wanted smaller by Unity, {requested} with a requested mip"));
			bad.Sort((a, b) => b.Level.CompareTo(a.Level));
			for (int i = 0; i < bad.Count && i < worst; ++i) {
				rows.Add(("  smaller", bad[i].Text));
			}
			rows.Add(("textures moved from the game's names", $"{links_.Count} materials; {Resynced} set again by the game after the switch and followed, {Unreadable} names unreadable"));
			int blocks = 0, blockTextures = 0;
			var block = new MaterialPropertyBlock();
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) {
				if (r == null || !r.HasPropertyBlock()) {
					continue;
				}
				bool ours = false;
				foreach (var m in r.sharedMaterials) {
					if (m != null && originals_.ContainsKey(m)) {
						ours = true;
						break;
					}
				}
				if (!ours) {
					continue;
				}
				++blocks;
				r.GetPropertyBlock(block);
				foreach (string name in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_Diffuse" }) {
					if (block.HasTexture(name)) {
						++blockTextures;
						if (rows.Count < 60) {
							var bt = block.GetTexture(name);
							rows.Add(("  block", $"{ObjectPath(r.transform)}: {name} = {(bt != null ? $"{bt.name} {bt.width}x{bt.height}" : "none")}"));
						}
					}
				}
			}
			rows.Add(("renderers of switched materials with a property block", $"{blocks}, {blockTextures} colour textures set there"));
			// every material the scene draws now, by shader: NatureFX shaders this replacer no longer tracks (switched by
			// an earlier load, their snapshot lost) would show here as untracked
			var byShader = new Dictionary<string, int>();
			var counted = new HashSet<Material>();
			int untracked = 0;
			var untrackedRows = new List<string>();
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None)) {
				if (r == null) {
					continue;
				}
				foreach (var m in r.sharedMaterials) {
					if (m == null || !counted.Add(m)) {
						continue;
					}
					string sn = m.shader != null ? m.shader.name : "(none)";
					byShader.TryGetValue(sn, out int c);
					byShader[sn] = c + 1;
					if (sn.StartsWith("NatureFX/", StringComparison.Ordinal) && !originals_.ContainsKey(m)) {
						++untracked;
						if (untrackedRows.Count < worst) {
							var bt = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") : null;
							string mip = bt is Texture2D b2 && b2.streamingMipmaps ? $", mip {b2.loadedMipmapLevel} loaded / {b2.desiredMipmapLevel} wanted" : "";
							untrackedRows.Add($"{m.name} ({sn}) @ {ObjectPath(r.transform)}: colour {(bt != null ? $"{bt.name} {bt.width}x{bt.height}{mip}" : "none")}");
						}
					}
				}
			}
			rows.Add(("materials drawn now", $"{counted.Count}; NatureFX shaders not tracked (switched earlier, no snapshot): {untracked}"));
			foreach (string line in untrackedRows) {
				rows.Add(("  untracked", line));
			}
			var shaders = new List<KeyValuePair<string, int>>(byShader);
			shaders.Sort((a, b) => b.Value.CompareTo(a.Value));
			for (int i = 0; i < shaders.Count && i < 15; ++i) {
				rows.Add(("  shader", $"{shaders[i].Key}: {shaders[i].Value}"));
			}
			foreach (var kv in Deferred) {
				rows.Add(("  left as it is", $"{kv.Value}: {kv.Key}"));
			}
			rows.Add(("colour map smaller than the game's largest texture", $"{smallerBase} materials"));
			foreach (string line in smaller) {
				rows.Add(("  picked", line));
			}
			return rows;
		}

		// Parking map investigation: spectators, road markings and road surfaces went missing once switched. Logs
		// their state after the swap, a few per kind.
		private readonly Dictionary<string, int> diagnosed_ = new();
		private static readonly string[] DiagnosedKinds = { "people", "decal", "marking", "parking_road", "terrain_road", "fence" };

		private void Diagnose(Material m, Renderer r, string path) {
			string kind = null;
			foreach (string k in DiagnosedKinds) {
				if (path.Contains(k)) {
					kind = k;
					break;
				}
			}
			if (kind == null) {
				return;
			}
			diagnosed_.TryGetValue(kind, out int n);
			if (n >= 2) {
				return;
			}
			diagnosed_[kind] = n + 1;
			var passes = new List<string>();
			for (int i = 0; i < m.passCount; ++i) {
				string pass = m.GetPassName(i);
				passes.Add(m.GetShaderPassEnabled(pass) ? pass : $"!{pass}");
			}
			Kino.Log.Info($"[NatureFX] Swap check {kind}: {m.name} @ {path}: shader {m.shader.name} supported {m.shader.isSupported}, queue {m.renderQueue}, " +
			              $"keywords [{string.Join(" ", m.shaderKeywords)}], passes [{string.Join(" ", passes)}], " +
			              $"surface {(m.HasProperty("_SurfaceType") ? m.GetFloat("_SurfaceType") : -1)}, cutoff {(m.HasProperty("_AlphaCutoffEnable") ? m.GetFloat("_AlphaCutoffEnable") : -1)}, " +
			              $"base {(m.GetTexture("_BaseColorMap") != null ? m.GetTexture("_BaseColorMap").name : "none")} colour {m.GetColor("_BaseColor")}, " +
			              $"renderer {r.GetType().Name} enabled {r.enabled} forceOff {r.forceRenderingOff} layer {r.gameObject.layer} lightmap {r.lightmapIndex}, " +
			              $"state {State(m, "_AlphaCutoff")} {State(m, "_ZTestGBuffer")} {State(m, "_ZTestDepthEqualForOpaque")} {State(m, "_ZWrite")} " +
			              $"{State(m, "_SrcBlend")} {State(m, "_DstBlend")} {State(m, "_CullMode")} {State(m, "_StencilRefGBuffer")} {State(m, "_StencilRefDepth")}");
		}

		private static string State(Material m, string name) {
			return m.HasProperty(name) ? $"{name}={m.GetFloat(name)}" : $"{name}=-";
		}

		// Materials switched by the last Apply (the ray tracing scene takes them again when > 0)
		public int LastReplaced { get; private set; }

		public void RestoreAll() {
			foreach (var kv in originals_) {
				if (kv.Key != null) {
					kv.Value.RestoreTo(kv.Key);
					Validate(kv.Key);
				}
			}
			originals_.Clear();
			links_.Clear();
		}

		// which game property each translated texture came from (same texture object, another name)
		private void Link(Material m, MaterialSnapshot snapshot) {
			List<(string, string)> list = null;
			foreach (string to in LinkedTargets) {
				if (!m.HasProperty(to)) {
					continue;
				}
				var t = m.GetTexture(to);
				if (t == null) {
					continue;
				}
				if (snapshot.Textures.TryGetValue(to, out var same) && same.Texture == t) {
					continue; // the game's own name: its later changes land in the NFX shader as they are
				}
				foreach (var kv in snapshot.Textures) {
					if (kv.Value.Texture == t && kv.Key != to) {
						(list ??= new List<(string, string)>()).Add((kv.Key, to));
						break;
					}
				}
			}
			if (list != null) {
				links_[m] = list;
			}
		}

		// Once a second: a texture the game set after the switch under its own name goes where the NFX shader reads it
		public void Resync() {
			if (Time.unscaledTime < nextResync_ || links_.Count == 0) {
				return;
			}
			nextResync_ = Time.unscaledTime + 1.0f;
			int unreadable = 0;
			List<Material> gone = null;
			foreach (var kv in links_) {
				var m = kv.Key;
				if (m == null || !originals_.TryGetValue(m, out var snapshot)) {
					(gone ??= new List<Material>()).Add(m);
					continue;
				}
				foreach (var (from, to) in kv.Value) {
					if (!m.HasTexture(from)) {
						++unreadable;
						continue;
					}
					var now = m.GetTexture(from);
					if (now == null || now == m.GetTexture(to)) {
						continue;
					}
					snapshot.Textures.TryGetValue(from, out var before);
					Kino.Log.Info($"[NatureFX] Shader rebuild: '{m.name}' {from} became {now.name} {now.width}x{now.height} after the switch " +
					              $"(was {(before.Texture != null ? $"{before.Texture.name} {before.Texture.width}x{before.Texture.height}" : "none")}); now its {to}");
					m.SetTexture(to, now);
					snapshot.Textures[from] = (now, before.Scale, before.Offset); // switching off restores the game's latest
					++Resynced;
				}
			}
			if (gone != null) {
				foreach (var m in gone) {
					links_.Remove(m);
				}
			}
			Unreadable = unreadable;
		}

		// The NFX shader this material becomes, or null with the reason it stays (null reason: nothing to say,
		// e.g. already a NatureFX material). Rules first, then the full rebuild's generic catch-all.
		public string Classify(Material m, Renderer r, string objectPath, out string reason) {
			reason = null;
			if (m == null || m.shader == null) {
				return null;
			}
			string shaderName = m.shader.name;
			if (shaderName.StartsWith("NatureFX/")) {
				return null;
			}
			if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer || r is BillboardRenderer) {
				reason = "VFX (stage 3)";
				return null;
			}
			if (r is SpriteRenderer || r is CanvasRenderer) {
				reason = "UI and sprites (not replaced)";
				return null;
			}
			// what the game draws for its menus (the car and part icons of the IconGenerator scene, KSL's livery
			// editor background): the UI's own pictures. Swapped by the full replacement, the menu UI and the
			// settings window flickered (gone for a frame now and then).
			if (r != null && (r.gameObject.scene.name == "IconGenerator" || r.transform.root.name.StartsWith("Kino.LiveryGroups", StringComparison.Ordinal))) {
				reason = "menu pictures (icons, livery editor)";
				return null;
			}
			foreach (string prefix in ExcludedShaderPrefixes) {
				if (shaderName.StartsWith(prefix, StringComparison.Ordinal)) {
					reason = $"not a surface or not supported: {prefix}";
					return null;
				}
			}
			string lower = shaderName.ToLowerInvariant();
			if (lower.Contains("smoke") || lower.Contains("particle") || lower.Contains("blobshadow")) {
				reason = "VFX (stage 3)";
				return null;
			}
			// the game's car shaders, also on parts outside a car body (select screen, driver, wheels on display):
			// their own translators come in stage 2
			if (shaderName.StartsWith("Shader Graphs/Car", StringComparison.Ordinal) && !shaderName.StartsWith("Shader Graphs/CarXFoliage", StringComparison.Ordinal)
			    || shaderName.Contains("mirror_shader")) {
				reason = "cars (stage 2)";
				return null;
			}
			// players' cars (RaceCar : BaseCar), whatever shader their parts use
			if (r != null && CarType != null && r.GetComponentInParent(CarType) != null) {
				reason = "cars (stage 2)";
				return null;
			}
			// parked cars, cones and barriers move when hit: never the swaying foliage shader
			bool moving = r != null && r.GetComponentInParent<Rigidbody>() != null;

			string target = ResolveTarget(m, objectPath);
			// foliage left to the game's shaders (Settings.RebuildFoliage off): no wind or snow on it
			if (target == FoliageShader && !RebuildFoliage) {
				reason = "foliage (foliage rebuild off)";
				return null;
			}
			if (target != null || !Full) {
				if (target == null) {
					reason = "no rule (full replacement off)";
				}
				return moving && target == FoliageShader ? ProfileShader : target;
			}

			// HDRP/Lit glass keeps its blend settings under the same property names; other transparent shaders wait
			bool transparent = m.renderQueue >= (int)UnityEngine.Rendering.RenderQueue.GeometryLast + 1 || (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f);
			// transparent: HDRP/Lit, and HDRP Shader Graphs (their surface type and blend under HDRP/Lit's names: the generic
			// surface takes them over, and the sun's traced shadow with it); glass and water have their own handling
			if (transparent && shaderName != "HDRP/Lit" && (!m.HasProperty("_SurfaceType") || lower.Contains("glass") || lower.Contains("water"))) {
				reason = "transparent (glass, water, not HDRP's kind)";
				return null;
			}
			// unlit (signs, screens): the generic surface glowing at a real luminance (GenericTranslator); transparent ones
			// stayed above
			return ProfileShader; // the generic NatureFX surface (GenericTranslator)
		}

		// "grandparent/parent/object", the part of the hierarchy that usually carries a readable name.
		public static string ObjectPath(Transform t) {
			string path = t.name;
			var p = t.parent;
			for (int i = 1; i < ObjectPathDepth && p != null; ++i, p = p.parent) {
				path = p.name + "/" + path;
			}
			return path;
		}

		// A copy of source on targetName, converted as the map's own materials are (ShrubRenderer's prototypes, whose
		// renderers stay off); null when it cannot be. Nothing to restore: the copy is the caller's
		public Material ConvertCopy(Material source, ShaderBundle bundle, string targetName, string objectPath) {
			var m = new Material(source) { name = source.name, hideFlags = HideFlags.DontSave };
			if (!TryReplace(m, objectPath, bundle, targetName)) {
				Object.Destroy(m);
				return null;
			}
			originals_.Remove(m);
			return m;
		}

		private bool TryReplace(Material m, string objectPath, ShaderBundle bundle, string targetName) {
			if (m == null || m.shader == null || originals_.ContainsKey(m) || targetName == null) {
				return false;
			}

			var target = bundle.Get(targetName);
			if (target == null) {
				return false;
			}

			// Keywords as the NFX shader would see them, computed before the switch drops unknown local keywords.
			var wanted = new HashSet<string>(KeywordsAfterSwap(m));
			bool foliage = targetName == FoliageShader;
			bool hadCutoff = m.HasProperty("_AlphaCutoff");
			// alpha clipping of any shader (mod maps' own ones too): kept, or the material is left alone below
			bool clips = AlphaClip.Detect(m, out float sourceCutoff);
			if (clips) {
				wanted.Add("_ALPHATEST_ON");
			}
			string before = $"{m.shader.name} [{string.Join(" ", m.shaderKeywords)}] cutoffEnable={(m.HasProperty("_AlphaCutoffEnable") ? m.GetFloat("_AlphaCutoffEnable") : -1)}";
			var snapshot = new MaterialSnapshot(m);
			originals_[m] = snapshot;

			// Foliage cards are always alpha clipped and two sided, whatever the source material says
			// (CarX's foliage Shader Graph bakes its clip threshold into the graph).
			if (foliage) {
				wanted.Add("_ALPHATEST_ON");
				wanted.Add("_DOUBLESIDED_ON");
			}

			int queue = m.renderQueue;
			m.shader = target;
			m.renderQueue = queue;
			// the game's values under HDRP/Lit names (HDRP/Lit sources keep theirs), then the keywords they imply
			GenericTranslator.Apply(m, snapshot);
			Link(m, snapshot);
			foreach (string k in DerivedLitKeywords(m)) {
				wanted.Add(k);
			}
			var sorted = new List<string>(wanted);
			sorted.Sort(StringComparer.Ordinal);
			UsedVariants.Add($"{targetName} | {string.Join(" ", sorted)}".TrimEnd());
			Validate(m);

			var variant = bundle.BestVariant(targetName, wanted);
			if (variant != null) {
				m.shaderKeywords = new List<string>(variant).ToArray();
			} else {
				CarryOverLitKeywords(m);
			}
			// a clipping material no bundled variant clips for would turn opaque (squares where it was cut out): it keeps
			// its own shader (the bundle cannot hold a variant for every mod map's material)
			if (clips && !m.IsKeywordEnabled("_ALPHATEST_ON")) {
				snapshot.RestoreTo(m);
				originals_.Remove(m);
				Kino.Log.Info($"[NatureFX] Shader rebuild: '{m.name}' ({snapshot.Shader?.name}) clips by alpha and no bundled {targetName} variant does; left as it is");
				return false;
			}
			if (m.IsKeywordEnabled("_ALPHATEST_ON")) {
				m.SetFloat("_AlphaCutoffEnable", 1.0f);
				if (!hadCutoff) {
					m.SetFloat("_AlphaCutoff", clips ? sourceCutoff : 0.5f);
				}
				m.renderQueue = Mathf.Max(m.renderQueue, (int)UnityEngine.Rendering.RenderQueue.AlphaTest);
				// HDRP/Lit's GBuffer pass skips the alpha clip when alpha testing (the depth prepass clips) and relies on
				// an equal depth test. Validate ran before the keyword was set (alpha test off then), which left the
				// less-or-equal test: the cards drew whole squares into the G-buffer.
				m.SetFloat("_ZTestGBuffer", (float)UnityEngine.Rendering.CompareFunction.Equal);
				m.SetFloat("_ZTestDepthEqualForOpaque", (float)UnityEngine.Rendering.CompareFunction.Equal);
			}
			if (m.IsKeywordEnabled("_DOUBLESIDED_ON")) {
				m.SetFloat("_DoubleSidedEnable", 1.0f);
				m.SetFloat("_CullMode", 0.0f);
				m.SetFloat("_CullModeForward", 0.0f);
			}
			Profiles?.Apply(m, objectPath);

			if (foliage && foliageDiagnostics_ < 3) {
				++foliageDiagnostics_;
				var tex = m.HasProperty("_BaseColorMap") ? m.GetTexture("_BaseColorMap") as Texture2D : null;
				string texInfo = tex != null ? $"{tex.name} {tex.format}" : "none";
				Kino.Log.Info($"[NatureFX] Foliage swap {m.name}: {before} -> [{string.Join(" ", m.shaderKeywords)}] " +
				              $"cutoff={m.GetFloat("_AlphaCutoff")} queue={m.renderQueue} baseMap={texInfo}");
			}
			return true;
		}

		// NFX shader name the first matching rule assigns to this material, or null.
		public string ResolveTarget(Material m, string objectPath) {
			if (m == null || m.shader == null) {
				return null;
			}

			string shaderName = m.shader.name;
			if (shaderName.StartsWith("NatureFX/")) {
				return null;
			}

			foreach (var rule in rules_) {
				if (rule.Shader.IsMatch(shaderName) && (rule.Name.IsMatch(m.name) || rule.Name.IsMatch(objectPath))) {
					return rule.Target;
				}
			}
			if (shaderName == "HDRP/Lit" && Profiles != null && Profiles.MatchesFoliage(m, objectPath)) {
				return FoliageShader; // [foliage]: colour variation and back lit leaves need the foliage shader
			}
			if (shaderName == "HDRP/Lit" && Profiles != null && Profiles.Matches(m, objectPath)) {
				return ProfileShader;
			}
			return null;
		}

		// Keywords the material ends up with once switched to an NFX shader. The shader bundle must contain
		// these keyword combinations (see the variants export in the scene dump), or HDRP strips the variants.
		public static IEnumerable<string> KeywordsAfterSwap(Material m) {
			var set = new HashSet<string>(m.shaderKeywords);
			foreach (string k in DerivedLitKeywords(m)) {
				set.Add(k);
			}
			return set;
		}

		// NatureFX shaders are HDRP/Lit copies, but HDRP does not recognise them by name, so ValidateMaterial
		// does not set Lit keywords. Materials coming from Shader Graphs (e.g. CarXFoliageSimple) need the
		// alpha clip / double sided keywords derived from their float toggles.
		private static IEnumerable<string> DerivedLitKeywords(Material m) {
			if (m.HasProperty("_AlphaCutoffEnable") && m.GetFloat("_AlphaCutoffEnable") > 0.5f) {
				yield return "_ALPHATEST_ON";
			}
			if (m.HasProperty("_DoubleSidedEnable") && m.GetFloat("_DoubleSidedEnable") > 0.5f) {
				yield return "_DOUBLESIDED_ON";
			}
			if (m.HasProperty("_NormalMap") && m.GetTexture("_NormalMap") != null) {
				yield return "_NORMALMAP";
			}
			if (m.HasProperty("_MaskMap") && m.GetTexture("_MaskMap") != null) {
				yield return "_MASKMAP";
			}
			if (m.HasProperty("_EmissiveColorMap") && m.GetTexture("_EmissiveColorMap") != null) {
				yield return "_EMISSIVE_COLOR_MAP";
			}
			if (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f) {
				yield return "_SURFACE_TYPE_TRANSPARENT";
				yield return "_ENABLE_FOG_ON_TRANSPARENT";
			}
		}

		private static void CarryOverLitKeywords(Material m) {
			foreach (string k in DerivedLitKeywords(m)) {
				m.EnableKeyword(k);
			}
		}

		// Rebuilds HDRP keywords / passes for the new shader.
		private static void Validate(Material m) {
			try {
				HDMaterial.ValidateMaterial(m);
			} catch (Exception) {
				// not an HDRP-generated shader; nothing to validate
			}
		}
	}
}

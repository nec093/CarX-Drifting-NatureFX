using System.Collections.Generic;
using System.IO;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Nature {
	// Loads kino/mods/NatureFX/naturefx.bundle built by unity/Assets/NatureFX/Editor/BuildNatureFXBundle.cs
	public class ShaderBundle {
		public const string FolderName = "NatureFX";
		public const string BundleName = "naturefx.bundle";

		private const string VariantListAsset = "nfx_variants";

		private AssetBundle bundle_;
		private readonly Dictionary<string, Shader> shaders_ = new();
		private readonly Dictionary<string, ComputeShader> computeShaders_ = new();
		// HDRP runtime resource field -> bundled compute shader name (nfx_hdrp_compute.txt)
		private readonly Dictionary<string, string> hdrpComputeMap_ = new();
		// keyword sets compiled into the bundle, per shader (from nfx_variants.txt written by the bundle build)
		private readonly Dictionary<string, List<HashSet<string>>> variants_ = new();

		public bool Loaded => bundle_ != null;
		public string BundlePath => Path.Combine(Kino.Paths.Mods, FolderName, BundleName);
		public IReadOnlyDictionary<string, Shader> Shaders => shaders_;

		public bool Load() {
			if (Loaded) {
				return true;
			}

			string path = BundlePath;
			if (!File.Exists(path)) {
				Kino.Log.Warning($"[NatureFX] Shader bundle not found: {path}. Foliage/accumulation shaders are disabled.");
				return false;
			}

			bundle_ = AssetBundle.LoadFromFile(path);
			if (bundle_ == null) {
				Kino.Log.Error("[NatureFX] Failed to load shader bundle (was it built with Unity 2023.2.22f1 / HDRP?)");
				return false;
			}
			Fill();
			return true;
		}

		// The loaded bundle's shaders, compute shaders, variant list and HDRP compute map into the lookups
		private void Fill() {
			shaders_.Clear();
			computeShaders_.Clear();
			hdrpComputeMap_.Clear();
			foreach (var shader in bundle_.LoadAllAssets<Shader>()) {
				// the ray tracing proxy has only a DXR pass, which isSupported does not count
				if (!shader.isSupported && shader.name != "Hidden/NatureFX/RTProxy") {
					Kino.Log.Warning($"[NatureFX] Shader not supported on this GPU/API: {shader.name}");
					continue;
				}
				shaders_[shader.name] = shader;
				Kino.Log.Info($"[NatureFX] Loaded shader: {shader.name}");
			}

			foreach (var cs in bundle_.LoadAllAssets<ComputeShader>()) {
				computeShaders_[cs.name] = cs;
				Kino.Log.Info($"[NatureFX] Loaded compute shader: {cs.name}");
			}


			LoadVariantList();
			var map = bundle_.LoadAsset<TextAsset>("nfx_hdrp_compute");
			if (map != null) {
				foreach (string line in map.text.Split('\n')) {
					int eq = line.IndexOf('=');
					if (eq > 0) {
						hdrpComputeMap_[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
					}
				}
			}
		}

		// Shader reload without a restart: the newest copy in reload/ (the bundle build writes one there while the game
		// runs) replaces the loaded bundle. Every material on one of the old shaders gets the new one of the same name
		// (its queue kept), and Reloaded tells whoever holds a compute shader. The old shaders stay alive
		// (Unload(false)) for anything not switched (HDRP's replaced compute shaders keep theirs).
		public event System.Action Reloaded;
		public string ReloadFolder => Path.Combine(Kino.Paths.Mods, FolderName, "reload");
		private string loadedReload_;

		public string Reload() {
			string folder = ReloadFolder;
			string newest = null;
			if (Directory.Exists(folder)) {
				foreach (string f in Directory.GetFiles(folder, "naturefx-*.bundle")) {
					newest = newest == null || string.CompareOrdinal(f, newest) > 0 ? f : newest;
				}
			}
			if (newest == null) {
				return "nothing to reload: no bundle in reload/ (build the shader bundle while the game runs)";
			}
			if (newest == loadedReload_) {
				return $"{Path.GetFileName(newest)} is already loaded";
			}
			var old = new Dictionary<string, Shader>(shaders_);
			bundle_?.Unload(false);
			bundle_ = AssetBundle.LoadFromFile(newest);
			if (bundle_ == null) {
				// back to what ran before
				bundle_ = loadedReload_ != null ? AssetBundle.LoadFromFile(loadedReload_) : AssetBundle.LoadFromFile(BundlePath);
				if (bundle_ != null) {
					Fill();
				}
				return $"{Path.GetFileName(newest)} could not be loaded; kept the shaders before";
			}
			Fill();
			var replacement = new Dictionary<Shader, Shader>();
			foreach (var kv in old) {
				if (shaders_.TryGetValue(kv.Key, out var shader) && shader != kv.Value) {
					replacement[kv.Value] = shader;
				}
			}
			int switched = 0;
			foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) {
				if (m != null && m.shader != null && replacement.TryGetValue(m.shader, out var shader)) {
					int queue = m.renderQueue;
					m.shader = shader;
					m.renderQueue = queue;
					++switched;
				}
			}
			loadedReload_ = newest;
			Reloaded?.Invoke();
			string result = $"{Path.GetFileName(newest)}: {shaders_.Count} shaders and {computeShaders_.Count} compute shaders, {switched} materials switched";
			Kino.Log.Info($"[NatureFX] Shaders reloaded from {result}");
			return result;
		}

		public Shader Get(string name) {
			return shaders_.TryGetValue(name, out var s) ? s : null;
		}

		public ComputeShader GetCompute(string name) {
			return computeShaders_.TryGetValue(name, out var cs) ? cs : null;
		}


		// Bundled copy of the compute shader HDRP stores in this runtime resource field.
		public ComputeShader GetHdrpCompute(string fieldName) {
			return hdrpComputeMap_.TryGetValue(fieldName, out var name) ? GetCompute(name) : null;
		}

		// The largest bundled keyword set for this shader that only uses keywords from 'wanted'.
		// Setting exactly these keywords guarantees Unity finds the variant instead of guessing a fallback
		// (which drops things like alpha clipping). Returns null when the bundle has no variant list.
		public HashSet<string> BestVariant(string shaderName, IEnumerable<string> wanted) {
			if (!variants_.TryGetValue(shaderName, out var sets)) {
				return null;
			}

			var want = new HashSet<string>(wanted);
			HashSet<string> best = null;
			foreach (var set in sets) {
				if (set.IsSubsetOf(want) && (best == null || set.Count > best.Count)) {
					best = set;
				}
			}
			return best ?? new HashSet<string>();
		}

		private void LoadVariantList() {
			variants_.Clear();
			var list = bundle_.LoadAsset<TextAsset>(VariantListAsset);
			if (list == null) {
				Kino.Log.Warning("[NatureFX] Shader bundle has no variant list; material keywords are left as they are");
				return;
			}

			int count = 0;
			foreach (string raw in list.text.Split('\n')) {
				int bar = raw.IndexOf('|');
				if (bar < 0) {
					continue;
				}
				string shader = raw.Substring(0, bar).Trim();
				var keywords = new HashSet<string>(raw.Substring(bar + 1).Split(new[] { ' ' }, System.StringSplitOptions.RemoveEmptyEntries));
				if (!variants_.TryGetValue(shader, out var sets)) {
					sets = new List<HashSet<string>>();
					variants_[shader] = sets;
				}
				sets.Add(keywords);
				++count;
			}
			Kino.Log.Info($"[NatureFX] {count} bundled shader variants");
		}

		public void Unload() {
			if (bundle_ != null) {
				bundle_.Unload(false);
			}
			bundle_ = null;
			shaders_.Clear();
			computeShaders_.Clear();
			hdrpComputeMap_.Clear();
			variants_.Clear();
		}
	}
}

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
			return true;
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

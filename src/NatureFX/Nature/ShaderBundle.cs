using System.Collections.Generic;
using System.IO;
using KSL.API;
using UnityEngine;

namespace NatureFX.Nature {
	// Loads kino/mods/NatureFX/naturefx.bundle built by unity/Assets/NatureFX/Editor/BuildNatureFXBundle.cs
	public class ShaderBundle {
		public const string FolderName = "NatureFX";
		public const string BundleName = "naturefx.bundle";

		private AssetBundle bundle_;
		private readonly Dictionary<string, Shader> shaders_ = new();

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
				if (!shader.isSupported) {
					Kino.Log.Warning($"[NatureFX] Shader not supported on this GPU/API: {shader.name}");
					continue;
				}
				shaders_[shader.name] = shader;
				Kino.Log.Info($"[NatureFX] Loaded shader: {shader.name}");
			}

			return true;
		}

		public Shader Get(string name) {
			return shaders_.TryGetValue(name, out var s) ? s : null;
		}

		public void Unload() {
			if (bundle_ != null) {
				bundle_.Unload(false);
			}
			bundle_ = null;
			shaders_.Clear();
		}
	}
}

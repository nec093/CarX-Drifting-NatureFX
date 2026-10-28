using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace NatureFX.Editor {
	// Builds every shader / shader graph under Assets/NatureFX/Shaders into naturefx.bundle.
	// Must be run in Unity 2023.2.22f1 with HDRP, the same version as the game.
	public static class BuildNatureFXBundle {
		private const string ShaderFolder = "Assets/NatureFX/Shaders";
		private const string OutputFolder = "Build/NatureFX";
		private const string BundleName = "naturefx.bundle";
		private const string GameDirPrefKey = "NatureFX.GameDir";

		[MenuItem("NatureFX/Build Shader Bundle")]
		public static void Build() {
			BuildInternal(false);
		}

		[MenuItem("NatureFX/Build And Copy To Game")]
		public static void BuildAndCopy() {
			BuildInternal(true);
		}

		[MenuItem("NatureFX/Set Game Folder...")]
		public static void SetGameDir() {
			string current = EditorPrefs.GetString(GameDirPrefKey, @"C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online");
			string dir = EditorUtility.OpenFolderPanel("CarX Drift Racing Online folder", current, "");
			if (!string.IsNullOrEmpty(dir)) {
				EditorPrefs.SetString(GameDirPrefKey, dir);
			}
		}

		private static void BuildInternal(bool copyToGame) {
			string[] assets = AssetDatabase.FindAssets("t:Shader", new[] { ShaderFolder })
				.Select(AssetDatabase.GUIDToAssetPath)
				.Distinct()
				.ToArray();

			if (assets.Length == 0) {
				Debug.LogError($"[NatureFX] No shaders found in {ShaderFolder}");
				return;
			}

			Directory.CreateDirectory(OutputFolder);
			var build = new AssetBundleBuild {
				assetBundleName = BundleName,
				assetNames = assets
			};

			var manifest = BuildPipeline.BuildAssetBundles(
				OutputFolder,
				new[] { build },
				BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
				BuildTarget.StandaloneWindows64);

			if (manifest == null) {
				Debug.LogError("[NatureFX] Bundle build failed");
				return;
			}

			string built = Path.Combine(OutputFolder, BundleName);
			Debug.Log($"[NatureFX] Built {built} with {assets.Length} shaders:\n{string.Join("\n", assets)}");

			if (!copyToGame) {
				return;
			}

			string gameDir = EditorPrefs.GetString(GameDirPrefKey, @"C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online");
			string target = Path.Combine(gameDir, "kino", "mods", "NatureFX");
			Directory.CreateDirectory(target);
			File.Copy(built, Path.Combine(target, BundleName), true);
			Debug.Log($"[NatureFX] Copied to {target}");
		}
	}
}

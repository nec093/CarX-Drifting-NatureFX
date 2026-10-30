using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

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

		// Unity.exe -batchmode -projectPath <proj> -executeMethod NatureFX.Editor.BuildNatureFXBundle.BatchBuild [-gameDir <dir>] -quit
		public static void BatchBuild() {
			try {
				string[] args = Environment.GetCommandLineArgs();
				int i = Array.IndexOf(args, "-gameDir");
				if (i >= 0 && i + 1 < args.Length) {
					EditorPrefs.SetString(GameDirPrefKey, args[i + 1]);
				}

				EnsureGraphicsApis();
				EnsureHdrpAsset();
				GenerateNatureFXShaders.Generate();
				if (!BuildInternal(true)) {
					EditorApplication.Exit(1);
				}
			} catch (Exception e) {
				Debug.LogException(e);
				EditorApplication.Exit(1);
			}
		}

		// The game runs on DX11 by default and on DX12 with -force-d3d12 (needed for ray traced GI),
		// so the bundle carries both shader binaries.
		private static void EnsureGraphicsApis() {
			PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
			PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
		}

		// HDRP strips shader variants by the HDRP assets in the build, so enable everything the game may use.
		// CarX ships with legacy light probes (no APV), so probe volume variants are left out.
		// HDRP strips whole shaders and variants by the HDRP assets in the build and keeps a variant if any asset
		// needs it. The game's asset is unknown beyond what the mod logs, so every feature is enabled, and two
		// assets on alternating quality levels keep both sides of the mutually exclusive switches
		// (screen space shadows on/off, area shadow quality).
		private static void EnsureHdrpAsset() {
			if (MirrorGameHdrpAssets()) {
				PlayerSettings.allowHDRDisplaySupport = true;
				KeepAllBuiltinVariants();
				return;
			}

			var a = PrepareHdrpAsset("Assets/NatureFX/Settings/NatureFX_HDRP.asset", true, HDAreaShadowFilteringQuality.High);
			var b = PrepareHdrpAsset("Assets/NatureFX/Settings/NatureFX_HDRP_B.asset", false, HDAreaShadowFilteringQuality.Medium);
			AssetDatabase.DeleteAsset("Assets/NatureFX/Settings/NatureFX_HDRP_L1.asset");
			AssetDatabase.DeleteAsset("Assets/NatureFX/Settings/NatureFX_HDRP_L2.asset");
			AssetDatabase.SaveAssets();

			GraphicsSettings.defaultRenderPipeline = a;
			for (int q = 0; q < QualitySettings.names.Length; ++q) {
				QualitySettings.SetQualityLevel(q, false);
				QualitySettings.renderPipeline = q % 2 == 0 ? a : b;
			}

			// keep the HDR output variants too
			PlayerSettings.allowHDRDisplaySupport = true;

			KeepAllBuiltinVariants();
			Debug.Log("[NatureFX] HDRP assets ready");
		}

		// The game's own HDRP settings (written by the mod to kino/config/NatureFX/game_hdrp/, copied to
		// unity/variants/): one asset per game asset, so HDRP keeps exactly the variants the game can request
		// (its shadow filtering, no screen space shadows, its feature set ...) instead of every feature.
		// NFX_HDRP=all goes back to the two feature-complete assets.
		private static bool MirrorGameHdrpAssets() {
			if (string.Equals(Environment.GetEnvironmentVariable("NFX_HDRP"), "all", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(VariantListFolder)) {
				return false;
			}
			string[] files = Directory.GetFiles(VariantListFolder, "game_hdrp_*.json");
			if (files.Length == 0) {
				return false;
			}

			var assets = new List<HDRenderPipelineAsset>();
			foreach (string file in files.OrderBy(f => f, StringComparer.Ordinal)) {
				string name = Path.GetFileNameWithoutExtension(file).Substring("game_hdrp_".Length);
				string path = $"Assets/NatureFX/Settings/Game_{name}.asset";
				var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
				if (asset == null) {
					Directory.CreateDirectory(Path.GetDirectoryName(path));
					asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
					AssetDatabase.CreateAsset(asset, path);
				}
				asset.currentPlatformRenderPipelineSettings = JsonUtility.FromJson<RenderPipelineSettings>(File.ReadAllText(file));
				EditorUtility.SetDirty(asset);
				assets.Add(asset);

				var s = asset.currentPlatformRenderPipelineSettings;
				Debug.Log($"[NatureFX] Mirroring game HDRP asset '{name}': lit shader mode {s.supportedLitShaderMode}, shadow filtering {s.hdShadowInitParams.directionalShadowFilteringQuality}, " +
				          $"screen space shadows {s.hdShadowInitParams.supportScreenSpaceShadows}, light layers {s.supportLightLayers}, shadowmask {s.supportShadowMask}, decals {s.supportDecals}");
			}
			AssetDatabase.SaveAssets();

			GraphicsSettings.defaultRenderPipeline = assets[0];
			for (int q = 0; q < QualitySettings.names.Length; ++q) {
				QualitySettings.SetQualityLevel(q, false);
				QualitySettings.renderPipeline = assets[q % assets.Count];
			}
			Debug.Log($"[NatureFX] HDRP assets mirror the game ({assets.Count}); NFX_HDRP=all builds every feature");
			return true;
		}

		private static HDRenderPipelineAsset PrepareHdrpAsset(string path, bool screenSpaceShadows, HDAreaShadowFilteringQuality areaShadows) {
			var asset = AssetDatabase.LoadAssetAtPath<HDRenderPipelineAsset>(path);
			if (asset == null) {
				Directory.CreateDirectory(Path.GetDirectoryName(path));
				asset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
				AssetDatabase.CreateAsset(asset, path);
			}

			var s = asset.currentPlatformRenderPipelineSettings;
			s.supportedLitShaderMode = RenderPipelineSettings.SupportedLitShaderMode.Both;
			s.colorBufferFormat = RenderPipelineSettings.ColorBufferFormat.R16G16B16A16;
			s.supportDecals = true;
			s.supportDecalLayers = true;
			s.supportSSR = true;
			s.supportSSAO = true;
			s.supportSSGI = true;
			s.supportVolumetrics = true;
			s.supportVolumetricClouds = true;
			s.supportWater = true;
			s.supportHighQualityLineRendering = true;
			s.supportRuntimeAOVAPI = true;
			s.supportSubsurfaceScattering = true;
			s.supportMotionVectors = true;
			s.supportTransparentBackface = true;
			s.supportTransparentDepthPrepass = true;
			s.supportTransparentDepthPostpass = true;
			s.supportDistortion = true;
			s.lightProbeSystem = RenderPipelineSettings.LightProbeSystem.LegacyLightProbes;
			// CarX enables both: without them HDRP strips the LIGHT_LAYERS / SHADOWS_SHADOWMASK variants, and the
			// replaced materials write no rendering layers, so they stop receiving dynamic shadows
			s.supportLightLayers = true;
			s.supportShadowMask = true;
			// CarX uses Low shadow filtering (logged by the mod); only that SHADOW_* variant is kept
			var shadows = s.hdShadowInitParams;
			shadows.directionalShadowFilteringQuality = HDShadowFilteringQuality.Low;
			shadows.punctualShadowFilteringQuality = HDShadowFilteringQuality.Low;
			shadows.areaShadowFilteringQuality = areaShadows;
			shadows.supportScreenSpaceShadows = screenSpaceShadows;
			s.hdShadowInitParams = shadows;
			asset.currentPlatformRenderPipelineSettings = s;
			EditorUtility.SetDirty(asset);
			return asset;
		}

		// Unity strips lightmap and instancing variants based on the scenes in the build; a shader-only bundle
		// has none, so keep them all (the game's tracks use their own lightmaps).
		private static void KeepAllBuiltinVariants() {
			var graphics = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset").FirstOrDefault();
			if (graphics == null) {
				Debug.LogWarning("[NatureFX] GraphicsSettings.asset not found; lightmap variants may be stripped");
				return;
			}

			var so = new SerializedObject(graphics);
			void SetInt(string name, int value) {
				var p = so.FindProperty(name);
				if (p != null) {
					p.intValue = value;
				} else {
					Debug.LogWarning($"[NatureFX] GraphicsSettings.{name} not found");
				}
			}
			void SetBool(string name, bool value = true) {
				var p = so.FindProperty(name);
				if (p != null) {
					p.boolValue = value;
				} else {
					Debug.LogWarning($"[NatureFX] GraphicsSettings.{name} not found");
				}
			}

			SetInt("m_LightmapStripping", 1); // custom
			SetBool("m_LightmapKeepPlain");
			SetBool("m_LightmapKeepDirCombined");
			// no realtime GI (Enlighten dynamic lightmaps) in CarX, and Subtractive is a built-in pipeline mode
			SetBool("m_LightmapKeepDynamicPlain", false);
			SetBool("m_LightmapKeepDynamicDirCombined", false);
			SetBool("m_LightmapKeepShadowMask");
			SetBool("m_LightmapKeepSubtractive", false);
			SetInt("m_InstancingStripping", 2); // keep all
			so.ApplyModifiedPropertiesWithoutUndo();
		}

		[MenuItem("NatureFX/Set Game Folder...")]
		public static void SetGameDir() {
			string current = EditorPrefs.GetString(GameDirPrefKey, @"C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online");
			string dir = EditorUtility.OpenFolderPanel("CarX Drift Racing Online folder", current, "");
			if (!string.IsNullOrEmpty(dir)) {
				EditorPrefs.SetString(GameDirPrefKey, dir);
			}
		}

		private const string VariantMaterialFolder = "Assets/NatureFX/Shaders/Generated/Variants";
		private const string VariantListFolder = "Assets/NatureFX/Variants";

		// Keyword sets always kept, in addition to the ones exported from the game (NatureFX_variants.txt).
		private static readonly string[] DefaultVariants = BuildDefaultVariants();

		private static string[] BuildDefaultVariants() {
			var list = new List<string>();
			string[] normal = { "", "_NORMALMAP _NORMALMAP_TANGENT_SPACE" };
			string[] mask = { "", "_MASKMAP" };
			foreach (string n in normal) {
				foreach (string m in mask) {
					list.Add($"NatureFX/Foliage | _ALPHATEST_ON _DOUBLESIDED_ON {n} {m}");
					list.Add($"NatureFX/LitAccumulation | {n} {m}");
					list.Add($"NatureFX/LitAccumulation | _ALPHATEST_ON {n} {m}");
					list.Add($"NatureFX/LitAccumulation | _DOUBLESIDED_ON {n} {m}");
				}
			}
			list.Add("NatureFX/Foliage | ");
			return list.ToArray();
		}

		// HDRP keeps only the shader_feature variants used by materials in the build, so the bundle carries
		// one small material per keyword set.
		private static string[] CreateVariantMaterials() {
			var lines = new List<string>(DefaultVariants);
			if (Directory.Exists(VariantListFolder)) {
				foreach (string file in Directory.GetFiles(VariantListFolder, "*.txt")) {
					// recorded_variants.txt is the stripping list of RecordedVariantStripper, not material keyword sets
					// game_hdrp_levels.txt is the quality level table of the HDRP settings dump
					string fileName = Path.GetFileName(file);
					if (fileName.StartsWith("recorded_variants", StringComparison.OrdinalIgnoreCase) || fileName.StartsWith("game_hdrp", StringComparison.OrdinalIgnoreCase)) {
						continue;
					}
					lines.AddRange(File.ReadAllLines(file).Where(l => l.Trim().Length > 0 && !l.StartsWith("#")));
				}
			}

			if (Directory.Exists(VariantMaterialFolder)) {
				AssetDatabase.DeleteAsset(VariantMaterialFolder);
			}
			Directory.CreateDirectory(VariantMaterialFolder);

			var seen = new HashSet<string>();
			var paths = new List<string>();
			var dummy = DummyTexture();
			int mismatched = 0;
			foreach (string raw in lines) {
				int bar = raw.IndexOf('|');
				if (bar < 0) {
					continue;
				}
				string shaderName = raw.Substring(0, bar).Trim();
				string[] keywords = raw.Substring(bar + 1).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
					.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();
				string key = shaderName + "|" + string.Join(" ", keywords);
				if (!seen.Add(key)) {
					continue;
				}

				var shader = Shader.Find(shaderName);
				if (shader == null) {
					Debug.LogWarning($"[NatureFX] Variant for unknown shader skipped: {raw}");
					continue;
				}

				var mat = new Material(shader) { name = $"variant_{paths.Count:D3}" };
				// HDRP recognises the NatureFX Lit copies and recomputes their keywords from the properties when the
				// material is saved: without matching properties every sample ended up with the same defaults, the
				// bundle compiled no normal map, mask map, alpha test or transparent variant, and switched materials
				// lost them (glaring roads, solid fences, broken road markings).
				ApplyKeywordProperties(mat, keywords, dummy);
				foreach (string k in keywords) {
					mat.EnableKeyword(k);
				}
				string path = $"{VariantMaterialFolder}/{mat.name}.mat";
				AssetDatabase.CreateAsset(mat, path);
				paths.Add(path);

				var saved = AssetDatabase.LoadAssetAtPath<Material>(path);
				var kept = new HashSet<string>(saved.shaderKeywords);
				var lost = keywords.Where(k => !kept.Contains(k)).ToArray();
				if (lost.Length > 0) {
					++mismatched;
					Debug.LogWarning($"[NatureFX] Variant material {saved.name} ({shaderName}) lost keywords: {string.Join(" ", lost)}");
				}
			}

			// The mod reads this list to snap a material's keywords onto a variant that is really in the bundle.
			string listPath = $"{VariantMaterialFolder}/nfx_variants.txt";
			File.WriteAllText(listPath, string.Join("\n", seen));
			AssetDatabase.ImportAsset(listPath);
			paths.Add(listPath);

			AssetDatabase.SaveAssets();
			Debug.Log($"[NatureFX] {paths.Count - 1} variant materials, {mismatched} with keywords HDRP dropped");
			return paths.ToArray();
		}

		// A 4x4 texture asset to stand in for the maps whose presence turns HDRP keywords on
		private static Texture2D DummyTexture() {
			string path = $"{VariantMaterialFolder}/nfx_dummy.asset";
			var t = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "nfx_dummy" };
			AssetDatabase.CreateAsset(t, path);
			return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
		}

		// The properties HDRP derives each Lit keyword from (HDRP/Lit's LitAPI.SetupLitKeywordsAndPass)
		private static void ApplyKeywordProperties(Material m, string[] keywords, Texture2D dummy) {
			void Set(string name, float value) {
				if (m.HasProperty(name)) {
					m.SetFloat(name, value);
				}
			}
			void Tex(string name) {
				if (m.HasProperty(name)) {
					m.SetTexture(name, dummy);
				}
			}

			// receiving SSR and decals are on by default: their DISABLE keywords come from turning them off
			Set("_ReceivesSSR", keywords.Contains("_DISABLE_SSR") ? 0.0f : 1.0f);
			Set("_ReceivesSSRTransparent", keywords.Contains("_DISABLE_SSR_TRANSPARENT") ? 0.0f : 1.0f);
			Set("_SupportDecals", keywords.Contains("_DISABLE_DECALS") ? 0.0f : 1.0f);
			Set("_NormalMapSpace", keywords.Contains("_NORMALMAP_TANGENT_SPACE") || !keywords.Any(k => k.StartsWith("_NORMALMAP")) ? 0.0f : 1.0f);
			foreach (string k in keywords) {
				switch (k) {
					case "_NORMALMAP": Tex("_NormalMap"); break;
					case "_MASKMAP": Tex("_MaskMap"); break;
					case "_EMISSIVE_COLOR_MAP": Tex("_EmissiveColorMap"); break;
					case "_DETAIL_MAP": Tex("_DetailMap"); break;
					case "_HEIGHTMAP": Tex("_HeightMap"); break;
					case "_BENTNORMALMAP": Tex("_BentNormalMap"); break;
					case "_ALPHATEST_ON":
						Set("_AlphaCutoffEnable", 1.0f);
						break;
					case "_DOUBLESIDED_ON":
						Set("_DoubleSidedEnable", 1.0f);
						break;
					case "_SURFACE_TYPE_TRANSPARENT":
						Set("_SurfaceType", 1.0f);
						break;
					case "_ENABLE_FOG_ON_TRANSPARENT":
						Set("_EnableFogOnTransparent", 1.0f);
						break;
					case "_ENABLE_GEOMETRIC_SPECULAR_AA":
						Set("_EnableGeometricSpecularAA", 1.0f);
						break;
					case "_MATERIAL_FEATURE_CLEAR_COAT":
						Set("_CoatMask", 1.0f);
						break;
					case "_MATERIAL_FEATURE_TRANSMISSION":
						Set("_MaterialID", 5.0f); // translucent: transmission without subsurface
						Set("_TransmissionEnable", 1.0f);
						break;
				}
			}
			if (!keywords.Contains("_ENABLE_FOG_ON_TRANSPARENT")) {
				Set("_EnableFogOnTransparent", 0.0f);
			}
			try {
				HDMaterial.ValidateMaterial(m);
			} catch (Exception) {
				// not a shader HDRP knows: the keywords set by the caller stay as they are
			}
		}

		// The CarX build ships HDRP's compute shaders without DX12 kernels, so the HDRP pipeline cannot start on
		// -force-d3d12. The bundle carries every compute shader of HDRP's runtime resources (same HDRP version,
		// compiled for DX11 and DX12) and the mod swaps them in by name on DX12 (Dx12Fix.cs).
		private const string HdrpRuntimeResources = "Packages/com.unity.render-pipelines.high-definition/Runtime/RenderPipelineResources/HDRenderPipelineRuntimeResources.asset";

		private static IEnumerable<string> HdrpRuntimeComputeShaderGuids() {
			var paths = AssetDatabase.GetDependencies(HdrpRuntimeResources, true)
				.Where(p => p.EndsWith(".compute", StringComparison.OrdinalIgnoreCase))
				.Distinct()
				.ToArray();
			Debug.Log($"[NatureFX] {paths.Length} HDRP runtime compute shaders added for DX12");
			return paths.Select(AssetDatabase.AssetPathToGUID).Append(AssetDatabase.AssetPathToGUID(WriteHdrpComputeMap()));
		}

		// "resourceField=computeShaderName" for HDRP's runtime shader resources, so the mod can also replace
		// compute shaders the game renamed (CarX ships ExposureXboxNaNFixed / HDMotionBlur).
		private static string WriteHdrpComputeMap() {
			var lines = new List<string>();
			var resources = AssetDatabase.LoadAssetAtPath<ScriptableObject>(HdrpRuntimeResources);
			var shaders = resources?.GetType().GetField("shaders")?.GetValue(resources);
			if (shaders != null) {
				foreach (var f in shaders.GetType().GetFields()) {
					if (f.FieldType == typeof(ComputeShader) && f.GetValue(shaders) is ComputeShader cs && cs != null) {
						lines.Add($"{f.Name}={cs.name}");
					}
				}
			}
			const string path = "Assets/NatureFX/Shaders/Generated/nfx_hdrp_compute.txt";
			Directory.CreateDirectory(Path.GetDirectoryName(path));
			File.WriteAllText(path, string.Join("\n", lines));
			AssetDatabase.ImportAsset(path);
			Debug.Log($"[NatureFX] HDRP compute map: {lines.Count} entries");
			return path;
		}

		private static bool BuildInternal(bool copyToGame) {
			string[] assets = AssetDatabase.FindAssets("t:Shader", new[] { ShaderFolder })
				.Concat(AssetDatabase.FindAssets("t:ComputeShader", new[] { ShaderFolder }))
				.Concat(AssetDatabase.FindAssets("t:RayTracingShader", new[] { ShaderFolder }))
				.Concat(HdrpRuntimeComputeShaderGuids())
				.Select(AssetDatabase.GUIDToAssetPath)
				.Distinct()
				.Concat(CreateVariantMaterials())
				.ToArray();

			if (assets.Length == 0) {
				Debug.LogError($"[NatureFX] No shaders found in {ShaderFolder}");
				return false;
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
			RecordedVariantStripper.LogSummary();

			if (manifest == null) {
				Debug.LogError("[NatureFX] Bundle build failed");
				return false;
			}

			string built = Path.Combine(OutputFolder, BundleName);
			Debug.Log($"[NatureFX] Built {built} with {assets.Length} shaders:\n{string.Join("\n", assets)}");

			if (!copyToGame) {
				return true;
			}

			string gameDir = EditorPrefs.GetString(GameDirPrefKey, @"C:\Program Files (x86)\Steam\steamapps\common\CarX Drift Racing Online");
			string target = Path.Combine(gameDir, "kino", "mods", "NatureFX");
			// Windows lets the bundle be overwritten while the game has it loaded, and the game then reads a mix of
			// both ("archive ... is corrupted"): copy it only while the game is closed
			if (System.Diagnostics.Process.GetProcessesByName("Drift Racing Online").Length > 0) {
				Debug.Log($"[NatureFX] Not copied: the game is running (copy {built} to {target} once it is closed)");
				return true;
			}
			Directory.CreateDirectory(target);
			File.Copy(built, Path.Combine(target, BundleName), true);
			Debug.Log($"[NatureFX] Copied to {target}");
			return true;
		}
	}
}

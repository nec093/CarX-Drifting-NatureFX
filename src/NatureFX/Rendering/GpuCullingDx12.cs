using System;
using HarmonyLib;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// CarX's own GPU culling (GpuCulling.GpuCullingService) uses three compute shaders that have no DX12 kernels.
	// On DX12 the config's shaders are swapped for NatureFX replacements (Shaders/DX12/*.compute) right after
	// the service loads its config, so its Setup and Process find the kernels they look for.
	public static class GpuCullingDx12 {
		private const string ServiceType = "GpuCulling.GpuCullingService";
		private const string ConfigResource = "GpuCullingConfig";

		private static ComputeShader cull_, copy_, mip_;
		private static Harmony harmony_;

		public static void Install(ShaderBundle bundle) {
			if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || harmony_ != null) {
				return;
			}

			cull_ = bundle.GetCompute("NFX_GpuCulling");
			copy_ = bundle.GetCompute("NFX_HizCopy");
			mip_ = bundle.GetCompute("NFX_HizMip");
			if (cull_ == null || copy_ == null || mip_ == null) {
				Kino.Log.Error("[NatureFX] DX12 culling fix: replacement compute shaders missing from the bundle");
				return;
			}

			try {
				var type = AccessTools.TypeByName(ServiceType);
				var loadConfig = type != null ? AccessTools.Method(type, "LoadConfig") : null;
				if (loadConfig == null) {
					Kino.Log.Warning("[NatureFX] DX12 culling fix: GpuCullingService.LoadConfig not found (game updated?)");
					return;
				}

				harmony_ = new Harmony("nec093.NatureFX.dx12culling");
				harmony_.Patch(loadConfig, postfix: new HarmonyMethod(typeof(GpuCullingDx12), nameof(AfterLoadConfig)));

				// the service may already have loaded (and failed with) the config before the mod started
				Swap(Resources.Load(ConfigResource));
				Kino.Log.Info($"[NatureFX] DX12 culling fix installed (IsSupported: culling {Dx12Fix.FirstKernelSupported(cull_)}, hiz copy {Dx12Fix.FirstKernelSupported(copy_)}, hiz mip {Dx12Fix.FirstKernelSupported(mip_)})");
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] DX12 culling fix failed: {e.Message}");
			}
		}

		private static void AfterLoadConfig(object __instance) {
			var field = AccessTools.Field(__instance.GetType(), "m_config");
			Swap(field?.GetValue(__instance));
		}

		private static void Swap(object config) {
			if (config == null) {
				return;
			}
			var type = config.GetType();
			Set(type, config, "cullingProcessShader", cull_);
			Set(type, config, "copyDepthTextureShader", copy_);
			Set(type, config, "mipGeneratorShader", mip_);
		}

		private static void Set(Type type, object config, string name, ComputeShader value) {
			var field = AccessTools.Field(type, name);
			if (field != null && value != null && !ReferenceEquals(field.GetValue(config), value)) {
				field.SetValue(config, value);
			}
		}
	}
}

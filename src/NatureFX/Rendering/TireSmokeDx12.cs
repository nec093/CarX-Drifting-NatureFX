using System;
using HarmonyLib;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// CarX's GPU tire smoke (GpuExtensions.dll: ParticleSimulationPass / ParticleRenderPass) uses compute shaders
	// without DX12 kernels ("Kernel 'BlurMS' not found"). Temporary measure while DX12 support is investigated:
	// on DX12 the two passes are skipped so the rest of the frame renders. DX11 is untouched.
	public static class TireSmokeDx12 {
		private static Harmony harmony_;

		public static bool Active => harmony_ != null;

		public static void Install() {
			if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || harmony_ != null) {
				return;
			}

			try {
				var render = AccessTools.TypeByName("ParticleRenderPass");
				var simulate = AccessTools.TypeByName("ParticleSimulationPass");
				if (render == null || simulate == null) {
					Kino.Log.Warning("[NatureFX] DX12 smoke workaround: particle passes not found (game updated?)");
					return;
				}

				// CustomPass declares obsolete Execute / Setup overloads too, so name the exact signatures
				var setupArgs = new[] { typeof(ScriptableRenderContext), typeof(CommandBuffer) };
				var executeArgs = new[] { typeof(UnityEngine.Rendering.HighDefinition.CustomPassContext) };
				var methods = new[] {
					AccessTools.DeclaredMethod(render, "Setup", setupArgs),
					AccessTools.DeclaredMethod(render, "Execute", executeArgs),
					AccessTools.DeclaredMethod(render, "Cleanup", Type.EmptyTypes),
					AccessTools.DeclaredMethod(simulate, "Execute", executeArgs)
				};

				harmony_ = new Harmony("nec093.NatureFX.dx12smoke");
				var skip = new HarmonyMethod(typeof(TireSmokeDx12), nameof(Skip));
				int patched = 0;
				foreach (var method in methods) {
					if (method != null) {
						harmony_.Patch(method, prefix: skip);
						++patched;
					}
				}
				Kino.Log.Info($"[NatureFX] DX12 smoke workaround: {patched} of {methods.Length} methods patched");
				Kino.Log.Warning("[NatureFX] DX12: GPU tire smoke disabled (its compute shaders have no DX12 kernels)");
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] DX12 smoke workaround failed: {e.Message}");
			}
		}

		// Harmony prefix: returning false skips the original method
		private static bool Skip() {
			return false;
		}
	}
}

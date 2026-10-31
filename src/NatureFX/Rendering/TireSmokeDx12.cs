using System;
using HarmonyLib;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// CarX's GPU tire smoke (GpuExtensions.dll: ParticleSimulationPass / ParticleRenderPass) uses compute shaders
	// without DX12 kernels ("Kernel 'BlurMS' not found"). Temporary measure while DX12 support is investigated:
	// on DX12 the two passes are skipped so the rest of the frame renders. On DX11 they are skipped only while
	// ReplaceGameSmoke is set (NatureFX's lit smoke, TireSmoke, instead of the game's).
	public static class TireSmokeDx12 {
		private static Harmony harmony_;
		private static bool dx12_;

		public static bool Active => harmony_ != null && (dx12_ || ReplaceGameSmoke);
		public static bool ReplaceGameSmoke;

		public static void Install() {
			if (harmony_ != null) {
				return;
			}
			dx12_ = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;

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
				// Setup and Cleanup are skipped on DX12 only: on DX11 the switch changes at runtime, and a pass whose
				// Setup was skipped threw from the game's Execute once the switch went off again (HDRP sets a pass up
				// once), so there the game's resources are always made and only the drawing is skipped
				var lifetime = new[] {
					AccessTools.DeclaredMethod(render, "Setup", setupArgs),
					AccessTools.DeclaredMethod(render, "Cleanup", Type.EmptyTypes)
				};
				var methods = new[] {
					AccessTools.DeclaredMethod(render, "Execute", executeArgs),
					AccessTools.DeclaredMethod(simulate, "Execute", executeArgs)
				};

				harmony_ = new Harmony("nec093.NatureFX.dx12smoke");
				var skip = new HarmonyMethod(typeof(TireSmokeDx12), nameof(Skip));
				var skipOnDx12 = new HarmonyMethod(typeof(TireSmokeDx12), nameof(SkipOnDx12));
				int patched = 0;
				foreach (var method in lifetime) {
					if (method != null) {
						harmony_.Patch(method, prefix: skipOnDx12);
						++patched;
					}
				}
				foreach (var method in methods) {
					if (method != null) {
						harmony_.Patch(method, prefix: skip);
						++patched;
					}
				}
				Kino.Log.Info($"[NatureFX] Game smoke switch: {patched} of {methods.Length} methods patched");
				if (dx12_) {
					Kino.Log.Warning("[NatureFX] DX12: GPU tire smoke disabled (its compute shaders have no DX12 kernels)");
				}
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] DX12 smoke workaround failed: {e.Message}");
			}
		}

		// Harmony prefix: returning false skips the original method
		private static bool Skip() {
			return !(dx12_ || ReplaceGameSmoke);
		}

		private static bool SkipOnDx12() {
			return !dx12_;
		}
	}
}

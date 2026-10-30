using System;
using System.Reflection;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The CarX build ships HDRP's compute shaders without DX12 kernels, so on -force-d3d12 the HDRP pipeline
	// fails to construct every frame ("Kernel 'KDepthDownsample8DualUav' not found", then
	// 'KEncodeFastCubemapMip', ...). The NatureFX bundle carries every compute shader referenced by HDRP's
	// runtime resources, compiled for DX11 and DX12 from the same HDRP 16.0.6 source. On DX12 they replace the
	// game's copies by name; HDRP retries the construction each frame, so the next attempt succeeds.
	public static class Dx12Fix {
		public static void Apply(ShaderBundle bundle) {
			if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) {
				return;
			}

			object shaders;
			try {
				shaders = FindShaderResources();
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] DX12 fix: HDRP resources not reachable ({e.Message})");
				return;
			}
			if (shaders == null) {
				Kino.Log.Warning("[NatureFX] DX12 fix: HDRP shader resources not found");
				return;
			}

			int replaced = 0, missing = 0;
			var unsupported = new System.Collections.Generic.List<string>();
			foreach (var field in shaders.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance)) {
				if (field.FieldType != typeof(ComputeShader)) {
					continue;
				}
				var current = field.GetValue(shaders) as ComputeShader;
				if (current == null) {
					continue;
				}

				// same name, or the HDRP original for this field when the game renamed it
				var replacement = bundle.GetCompute(current.name) ?? bundle.GetHdrpCompute(field.Name);
				if (replacement == null) {
					++missing;
					Kino.Log.Warning($"[NatureFX] DX12 fix: no bundled copy of {current.name} ({field.Name})");
					continue;
				}
				if (replacement != current) {
					field.SetValue(shaders, replacement);
					++replaced;
				}
				if (!FirstKernelSupported(replacement)) {
					unsupported.Add($"{replacement.name} ({field.Name})");
				}
			}

			Kino.Log.Info($"[NatureFX] DX12 fix: replaced {replaced} HDRP compute shaders ({missing} without a bundled copy)");
			if (unsupported.Count > 0) {
				Kino.Log.Warning($"[NatureFX] DX12 fix: {unsupported.Count} compute shaders report IsSupported false: {string.Join(", ", unsupported)}");
			} else {
				Kino.Log.Info("[NatureFX] DX12 fix: every replaced compute shader reports IsSupported true");
			}
		}

		// Kernel 0 exists in every compute shader that has kernels for this API; IsSupported is false when
		// the GPU / API rejects the compiled code, in which case dispatches silently do nothing.
		// Shaders with keywords are skipped: IsSupported checks the keyword-free variant, which many HDRP
		// shaders do not have, and logs a "Can't find kernel variant" error for it.
		public static bool FirstKernelSupported(ComputeShader cs) {
			if (cs.keywordSpace.keywordCount > 0) {
				return true;
			}
			try {
				return cs.IsSupported(0);
			} catch (Exception) {
				return false;
			}
		}

		// GlobalSettings.m_RenderPipelineResources (internal) -> .shaders
		private static object FindShaderResources() {
			var settings = GraphicsSettings.GetSettingsForRenderPipeline<HDRenderPipeline>();
			if (settings == null) {
				return null;
			}
			var resourcesField = settings.GetType().GetField("m_RenderPipelineResources", BindingFlags.NonPublic | BindingFlags.Instance);
			var resources = resourcesField?.GetValue(settings);
			var shadersField = resources?.GetType().GetField("shaders", BindingFlags.Public | BindingFlags.Instance);
			return shadersField?.GetValue(resources);
		}
	}
}

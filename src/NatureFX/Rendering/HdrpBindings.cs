using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// HDRP resources a NatureFX ray tracing shader needs but does not receive on its own: HDRP's render graph sets
	// _ExposureTexture / _AmbientProbeData as globals only for its own passes, and Unity reports them as unset
	// ("Texture ... is not set. Dispatching ray generation shader failed"). The accessors are internal, so they are
	// reached by reflection once; null when this HDRP version differs.
	public static class HdrpBindings {
		private static bool resolved_;
		private static MethodInfo getExposureTexture_;
		private static FieldInfo skyManager_;
		private static MethodInfo getAmbientProbeBuffer_;

		private static void Resolve() {
			if (resolved_) {
				return;
			}
			resolved_ = true;
			const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
			try {
				getExposureTexture_ = typeof(HDRenderPipeline).GetMethod("GetExposureTexture", flags, null, new[] { typeof(HDCamera) }, null);
				skyManager_ = typeof(HDRenderPipeline).GetField("m_SkyManager", flags);
				getAmbientProbeBuffer_ = skyManager_?.FieldType.GetMethod("GetDiffuseAmbientProbeBuffer", flags, null, new[] { typeof(HDCamera) }, null);
			} catch (Exception e) {
				KSL.API.Kino.Log.Warning($"[NatureFX] HDRP internals not found ({e.Message}); ray traced GI uses fallbacks");
			}
		}

		// Pre-exposure texture of this camera (x = exposure multiplier)
		public static RTHandle ExposureTexture(HDCamera camera) {
			Resolve();
			var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
			try {
				return pipeline != null && getExposureTexture_ != null ? getExposureTexture_.Invoke(pipeline, new object[] { camera }) as RTHandle : null;
			} catch (Exception) {
				return null;
			}
		}

		// Diffuse ambient probe of the sky (SH, as sampled by SampleSH9)
		public static GraphicsBuffer AmbientProbe(HDCamera camera) {
			Resolve();
			var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
			try {
				var sky = pipeline != null ? skyManager_?.GetValue(pipeline) : null;
				return sky != null && getAmbientProbeBuffer_ != null ? getAmbientProbeBuffer_.Invoke(sky, new object[] { camera }) as GraphicsBuffer : null;
			} catch (Exception) {
				return null;
			}
		}
	}
}

using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// What the current game build / GPU / graphics API can actually do.
	public static class Capabilities {
		public static bool IsHdrp { get; private set; }
		public static bool IsD3D12 { get; private set; }
		public static bool GpuRayTracing { get; private set; }
		public static bool PipelineSsgi { get; private set; }
		public static bool PipelineSsr { get; private set; }
		public static bool PipelineVolumetrics { get; private set; }
		public static bool PipelineProbeVolume { get; private set; }
		public static string ProbeVolumeBands { get; private set; } = "-";
		public static string ShadowFiltering { get; private set; } = "-";
		public static string RenderPath { get; private set; } = "-";

		public static void Refresh() {
			IsD3D12 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
			GpuRayTracing = SystemInfo.supportsRayTracing;

			var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
			IsHdrp = asset != null;
			if (!IsHdrp) {
				PipelineSsgi = PipelineSsr = PipelineVolumetrics = PipelineProbeVolume = false;
				return;
			}

			var s = asset.currentPlatformRenderPipelineSettings;
			PipelineSsgi = s.supportSSGI;
			PipelineSsr = s.supportSSR;
			PipelineVolumetrics = s.supportVolumetrics;
			PipelineProbeVolume = s.lightProbeSystem == RenderPipelineSettings.LightProbeSystem.ProbeVolumes;
			ProbeVolumeBands = PipelineProbeVolume ? s.probeVolumeSHBands.ToString() : "-";
			ShadowFiltering = s.hdShadowInitParams.directionalShadowFilteringQuality.ToString();
			// deferred materials get their light and shadows from HDRP's Deferred compute shader, forward ones in their own pass
			RenderPath = $"lit shader mode {s.supportedLitShaderMode}, light layers {s.supportLightLayers}, shadowmask {s.supportShadowMask}, screen space shadows {s.hdShadowInitParams.supportScreenSpaceShadows}";
		}

		public static string Describe() {
			return $"HDRP: {IsHdrp}, API: {SystemInfo.graphicsDeviceType}, GPU: {SystemInfo.graphicsDeviceName}\n" +
			       $"DXR GPU: {GpuRayTracing} (traced by the own engine, NFXNative), SSGI: {PipelineSsgi}, SSR: {PipelineSsr}, Volumetrics: {PipelineVolumetrics}\n" +
			       $"Probe volumes: {PipelineProbeVolume} ({ProbeVolumeBands}), lightmaps loaded: {LightmapSettings.lightmaps.Length}, shadow filtering: {ShadowFiltering}\n" +
			       RenderPath;
		}
	}
}

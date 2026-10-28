using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// What the current game build / GPU / graphics API can actually do.
	public static class Capabilities {
		public static bool IsHdrp { get; private set; }
		public static bool IsD3D12 { get; private set; }
		public static bool GpuRayTracing { get; private set; }
		public static bool PipelineRayTracing { get; private set; }
		public static bool PipelineSsgi { get; private set; }
		public static bool PipelineSsr { get; private set; }
		public static bool PipelineVolumetrics { get; private set; }

		// Ray tracing needs all three: DX12, a DXR GPU, and an HDRP asset with ray tracing enabled.
		public static bool RayTracingAvailable => IsD3D12 && GpuRayTracing && PipelineRayTracing;

		public static void Refresh() {
			IsD3D12 = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
			GpuRayTracing = SystemInfo.supportsRayTracing;

			var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
			IsHdrp = asset != null;
			if (!IsHdrp) {
				PipelineRayTracing = PipelineSsgi = PipelineSsr = PipelineVolumetrics = false;
				return;
			}

			var s = asset.currentPlatformRenderPipelineSettings;
			PipelineRayTracing = s.supportRayTracing;
			PipelineSsgi = s.supportSSGI;
			PipelineSsr = s.supportSSR;
			PipelineVolumetrics = s.supportVolumetrics;
		}

		public static string Describe() {
			return $"HDRP: {IsHdrp}, API: {SystemInfo.graphicsDeviceType}, GPU: {SystemInfo.graphicsDeviceName}\n" +
			       $"DXR GPU: {GpuRayTracing}, Pipeline RT: {PipelineRayTracing}, SSGI: {PipelineSsgi}, SSR: {PipelineSsr}, Volumetrics: {PipelineVolumetrics}";
		}
	}
}

using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Reads back the exposure HDRP is currently using (EV100) for the F8 window, so DX11 and DX12 can be
	// compared and the auto exposure limits set from real values. HDRP only exposes this internally
	// (HDRenderPipeline.GetExposureTexture), so it is reached by reflection.
	public class ExposureProbe {
		private const float Interval = 0.5f;

		private MethodInfo getExposureTexture_;
		private bool resolved_;
		private bool pending_;
		private float next_;

		public bool Valid { get; private set; }
		public float EV100 { get; private set; }
		public float Multiplier { get; private set; }

		public void Update() {
			if (pending_ || Time.unscaledTime < next_) {
				return;
			}
			next_ = Time.unscaledTime + Interval;

			var pipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
			var cam = Camera.main;
			if (pipeline == null || cam == null) {
				Valid = false;
				return;
			}

			if (!resolved_) {
				resolved_ = true;
				getExposureTexture_ = typeof(HDRenderPipeline).GetMethod("GetExposureTexture", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, new[] { typeof(HDCamera) }, null);
			}
			if (getExposureTexture_ == null) {
				return;
			}

			try {
				var hdCamera = HDCamera.GetOrCreate(cam);
				var handle = getExposureTexture_.Invoke(pipeline, new object[] { hdCamera }) as RTHandle;
				var texture = handle?.rt;
				if (texture == null) {
					Valid = false;
					return;
				}
				pending_ = true;
				AsyncGPUReadback.Request(texture, 0, OnReadback);
			} catch (Exception) {
				Valid = false;
				pending_ = false;
			}
		}

		private void OnReadback(AsyncGPUReadbackRequest request) {
			pending_ = false;
			if (request.hasError) {
				Valid = false;
				return;
			}
			// RG32F texel: x = exposure multiplier, y = EV100
			var data = request.GetData<float>();
			if (data.Length >= 2) {
				Multiplier = data[0];
				EV100 = data[1];
				Valid = true;
			}
		}
	}
}

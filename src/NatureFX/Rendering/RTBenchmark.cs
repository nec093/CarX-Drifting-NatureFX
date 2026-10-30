using System;
using System.Linq;
using System.Text;
using UnityEngine;

namespace NatureFX.Rendering {
	// Ray tracing stages the benchmark can switch off for a moment
	[Flags]
	public enum RTStage {
		None = 0,
		Reflections = 1,
		Shadows = 2,
		Ambient = 4,
		Bounce = 8,
		Dxr12Sun = 16, // the sun shadows back on Unity's device (DXR 1.1) instead of the DXR 1.2 device
		Omm = 32       // the DXR 1.2 device without its opacity micromaps (every alpha tested hit tested in the shader)
	}

	// Cost of each ray tracing stage, measured A/B: the frame time with everything on against the frame time with
	// one stage off. Unity's GPU recorders report nothing in the game's build, and a GPU bound frame time shows the
	// difference just as well (vsync or a frame cap hide it).
	public class RTBenchmark {
		private const int SettleFrames = 30;   // temporal accumulation and pipeline catch up after a switch
		private const int MeasureFrames = 90;

		private static readonly (string Label, RTStage Off)[] AllSteps = {
			("all on", RTStage.None),
			("reflections", RTStage.Reflections),
			("sun shadows (against HDRP's)", RTStage.Shadows),
			("bounce light (against plain AO)", RTStage.Bounce),
			("ambient light", RTStage.Ambient),
			("DXR 1.2 sun shadows (against DXR 1.1)", RTStage.Dxr12Sun),
			("opacity micromaps (against without)", RTStage.Omm)
		};
		// the DXR 1.2 steps only while the sun shadows run there
		private (string Label, RTStage Off)[] Steps = AllSteps;

		private readonly RayTracingEffects owner_;
		private readonly float[] ms_ = new float[AllSteps.Length];
		private int step_ = -1;
		private int frame_;
		private double sum_;

		public bool Running => step_ >= 0;
		public string Result { get; private set; } = "";

		public RTBenchmark(RayTracingEffects owner) {
			owner_ = owner;
		}

		public void Start() {
			// the last two only while the sun shadows run on the DXR 1.2 device
			Steps = NativeEngine.Dxr12Sun ? AllSteps : AllSteps.Take(AllSteps.Length - 2).ToArray();
			step_ = 0;
			BeginStep();
		}

		public void Cancel() {
			if (Running) {
				step_ = -1;
				owner_.SetBenchmarkOff(RTStage.None);
				Result = "Cancelled.";
			}
		}

		// Once per frame
		public void Update() {
			if (!Running) {
				return;
			}
			++frame_;
			if (frame_ > SettleFrames) {
				sum_ += Time.unscaledDeltaTime;
			}
			Result = $"Measuring {Steps[step_].Label}… ({step_ + 1}/{Steps.Length})";
			if (frame_ < SettleFrames + MeasureFrames) {
				return;
			}
			ms_[step_] = (float)(sum_ / MeasureFrames * 1000.0);
			if (++step_ < Steps.Length) {
				BeginStep();
				return;
			}
			step_ = -1;
			owner_.SetBenchmarkOff(RTStage.None);
			Result = Report();
		}

		private void BeginStep() {
			frame_ = 0;
			sum_ = 0.0;
			owner_.SetBenchmarkOff(Steps[step_].Off);
		}

		private string Report() {
			var text = new StringBuilder();
			float baseline = ms_[0];
			text.AppendLine($"all on  {baseline:0.0} ms ({1000.0f / Mathf.Max(baseline, 0.01f):0} fps)");
			for (int i = 1; i < Steps.Length; ++i) {
				text.AppendLine($"{Steps[i].Label}  {baseline - ms_[i]:+0.0;-0.0} ms");
			}
			text.Append("Measured with the frame time; keep the camera still while it runs.");
			return text.ToString();
		}
	}
}

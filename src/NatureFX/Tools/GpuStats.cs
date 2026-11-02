using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

namespace NatureFX.Tools {
	// The GPU's own counters for the debug page, from NVIDIA's management library (nvml.dll, installed with the driver in
	// System32): how busy the GPU and its memory are, clocks, temperature, power and why the clocks are held back.
	// Read once a second while the page is open. Not there (another vendor, an old driver): Available false.
	public static class GpuStats {
		[StructLayout(LayoutKind.Sequential)]
		private struct Utilization {
			public uint Gpu, Memory;
		}

		[DllImport("nvml", EntryPoint = "nvmlInit_v2")] private static extern int Init();
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetCount_v2")] private static extern int GetCount(out uint count);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] private static extern int GetHandle(uint index, out IntPtr device);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetName")] private static extern int GetName(IntPtr device, byte[] name, uint length);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetUtilizationRates")] private static extern int GetUtilization(IntPtr device, out Utilization u);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetTemperature")] private static extern int GetTemperature(IntPtr device, int sensor, out uint celsius);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetPowerUsage")] private static extern int GetPower(IntPtr device, out uint milliwatts);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetEnforcedPowerLimit")] private static extern int GetPowerLimit(IntPtr device, out uint milliwatts);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetClockInfo")] private static extern int GetClock(IntPtr device, int type, out uint mhz);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetMaxClockInfo")] private static extern int GetMaxClock(IntPtr device, int type, out uint mhz);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetPerformanceState")] private static extern int GetPState(IntPtr device, out int state);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetCurrentClocksThrottleReasons")] private static extern int GetThrottle(IntPtr device, out ulong reasons);
		[DllImport("nvml", EntryPoint = "nvmlDeviceGetFanSpeed")] private static extern int GetFan(IntPtr device, out uint percent);

		private static bool tried_, ok_;
		private static IntPtr device_;
		public static string Name { get; private set; } = "";
		public static string Problem { get; private set; } = "";
		public static bool Available => ok_;

		// the latest reading
		public static uint GpuPercent, MemoryPercent, Celsius, FanPercent, GraphicsMHz, MemoryMHz, MaxGraphicsMHz;
		public static float Watts, WattLimit;
		public static int PState = -1;
		public static ulong Throttle;

		private static void Start() {
			tried_ = true;
			try {
				if (Init() != 0 || GetCount(out uint count) != 0 || count == 0) {
					Problem = "NVML did not start";
					return;
				}
				// the device the game renders on (by name), else the first
				string want = SystemInfo.graphicsDeviceName ?? "";
				for (uint i = 0; i < count; ++i) {
					if (GetHandle(i, out var d) != 0) {
						continue;
					}
					var buffer = new byte[96];
					string name = GetName(d, buffer, (uint)buffer.Length) == 0 ? Encoding.ASCII.GetString(buffer).TrimEnd('\0') : "";
					if (device_ == IntPtr.Zero || (name.Length > 0 && want.Contains(name))) {
						device_ = d;
						Name = name;
					}
				}
				ok_ = device_ != IntPtr.Zero;
				if (!ok_) {
					Problem = "no NVIDIA device";
				} else if (GetMaxClock(device_, 0, out uint max) == 0) {
					MaxGraphicsMHz = max;
				}
			} catch (DllNotFoundException) {
				Problem = "nvml.dll not found (not an NVIDIA driver)";
			} catch (Exception e) {
				Problem = $"{e.GetType().Name}: {e.Message}";
			}
		}

		public static void Read() {
			if (!tried_) {
				Start();
			}
			if (!ok_) {
				return;
			}
			try {
				if (GetUtilization(device_, out var u) == 0) {
					GpuPercent = u.Gpu;
					MemoryPercent = u.Memory;
				}
				GetTemperature(device_, 0, out Celsius);
				Watts = GetPower(device_, out uint mw) == 0 ? mw / 1000.0f : 0.0f;
				WattLimit = GetPowerLimit(device_, out uint limit) == 0 ? limit / 1000.0f : 0.0f;
				GetClock(device_, 0, out GraphicsMHz);
				GetClock(device_, 2, out MemoryMHz);
				PState = GetPState(device_, out int p) == 0 ? p : -1;
				FanPercent = GetFan(device_, out uint fan) == 0 ? fan : 0;
				try {
					Throttle = GetThrottle(device_, out ulong reasons) == 0 ? reasons : 0;
				} catch (EntryPointNotFoundException) {
					Throttle = 0;
				}
			} catch (Exception e) {
				ok_ = false;
				Problem = $"{e.GetType().Name}: {e.Message}";
			}
		}

		// what holds the clocks back now (NVML's clock throttle reasons)
		public static string ThrottleText {
			get {
				if (Throttle == 0) {
					return "none";
				}
				var parts = new System.Collections.Generic.List<string>();
				if ((Throttle & 0x1) != 0) parts.Add("idle");
				if ((Throttle & 0x2) != 0) parts.Add("application clocks");
				if ((Throttle & 0x4) != 0) parts.Add("power limit");
				if ((Throttle & 0x8) != 0) parts.Add("hardware slowdown");
				if ((Throttle & 0x20) != 0) parts.Add("thermal (software)");
				if ((Throttle & 0x40) != 0) parts.Add("thermal (hardware)");
				if ((Throttle & 0x80) != 0) parts.Add("power brake");
				if ((Throttle & 0x100) != 0) parts.Add("display clock");
				return parts.Count > 0 ? string.Join(", ", parts) : $"0x{Throttle:x}";
			}
		}
	}
}

using System;
using System.Diagnostics;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// CarX ships with DX12 (D3D12Core.dll, split gfx jobs) but starts on DX11; NatureFX needs DX12. Unity only picks
	// the graphics API at startup, so switching means quitting and relaunching through Steam with -force-d3d12.
	public static class GraphicsApiSwitcher {
		private const uint CarXAppId = 635260;
		// Steam refuses a second instance (single-instance in boot.config), so wait for this one to exit
		private const int RelaunchDelaySeconds = 6;

		public static bool IsD3D12 => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
		public static string Current => SystemInfo.graphicsDeviceType.ToString().Replace("Direct3D", "DX");
		public const string LaunchOption = "-force-d3d12";

		public static bool RestartInDx12() {
			uint appId = CarXAppId;
			try {
				if (Kino.Info.AppId != 0) {
					appId = Kino.Info.AppId;
				}
			} catch (Exception) {
				// KSL info not ready; keep the known CarX app id
			}

			string url = $"steam://run/{appId}//{LaunchOption}/";
			try {
				// a detached shell waits for the game to close, then asks Steam to start it again with the option
				var info = new ProcessStartInfo("cmd.exe", $"/c timeout /t {RelaunchDelaySeconds} /nobreak >nul & start \"\" \"{url}\"") {
					CreateNoWindow = true,
					UseShellExecute = false
				};
				Process.Start(info);
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Could not schedule the relaunch: {e.Message}");
				return false;
			}

			Kino.Log.Info($"[NatureFX] Restarting with {LaunchOption} via {url}");
			Application.Quit();
			return true;
		}
	}
}

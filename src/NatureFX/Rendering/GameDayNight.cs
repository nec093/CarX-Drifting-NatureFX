using System;
using System.Reflection;
using KSL.API;
using UnityEngine;

namespace NatureFX.Rendering {
	// The game's own day and night: a track's street lights, lit windows and the like belong to its night lighting
	// scenario (DayNightController loads it as an additive scene and switches its IDayNightBehaviours' lights on). The
	// game chooses it once, from the race's night mode; with NatureFX's time of day the lamps stayed off at night. This
	// follows NatureFX's sun instead: night lighting from dusk (the sun below 2°), day again above 4°, through the
	// game's own DayNightController.SetDayNightMode (reflection: the game's types are not referenced). The game's own
	// choice comes back when NatureFX stops driving the time.
	public class GameDayNight {
		private const float NightBelow = 2.0f, DayAbove = 4.0f; // sun elevation (°)

		private Type type_;
		private PropertyInfo instance_, isDay_, isReady_, timeoutOver_;
		private MethodInfo setMode_;
		private bool looked_;
		private bool driving_;
		private bool? original_;
		private UnityEngine.Object controller_;

		public string Status { get; private set; } = "off";

		private bool Bind() {
			if (looked_) {
				return type_ != null;
			}
			looked_ = true;
			foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) {
				type_ = assembly.GetType("DayNightController");
				if (type_ != null) {
					break;
				}
			}
			if (type_ == null) {
				Kino.Log.Warning("[NatureFX] The game's DayNightController not found: its night lights stay as the game sets them");
				return false;
			}
			const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
			instance_ = type_.GetProperty("instance", any);
			isDay_ = type_.GetProperty("IsDayLight", any);
			isReady_ = type_.GetProperty("isReady", any);
			timeoutOver_ = type_.GetProperty("isTimeoutToChangeModeOver", any);
			setMode_ = type_.GetMethod("SetDayNightMode", any, null, new[] { typeof(bool) }, null);
			if (instance_ == null || isDay_ == null || setMode_ == null) {
				Kino.Log.Warning("[NatureFX] The game's DayNightController has changed: its night lights stay as the game sets them");
				type_ = null;
				return false;
			}
			return true;
		}

		public void Update(bool drive, Light sun) {
			if (!Bind()) {
				Status = "not available in this game version";
				return;
			}
			var controller = instance_.GetValue(null) as UnityEngine.Object;
			if (controller == null) {
				// no track loaded (menus): nothing to switch, and the next track starts from its own choice
				controller_ = null;
				original_ = null;
				driving_ = false;
				Status = "no track";
				return;
			}
			if (controller != controller_) {
				controller_ = controller;
				original_ = null;
			}
			bool ready = (isReady_ == null || (bool)isReady_.GetValue(controller)) && (timeoutOver_ == null || (bool)timeoutOver_.GetValue(controller));
			bool isDay = (bool)isDay_.GetValue(controller);
			if (!drive || sun == null) {
				if (driving_ && original_.HasValue && ready && isDay != original_.Value) {
					setMode_.Invoke(controller, new object[] { original_.Value });
				}
				driving_ = false;
				Status = $"the game's own ({(isDay ? "day" : "night")} lighting)";
				return;
			}
			if (!original_.HasValue) {
				original_ = isDay;
			}
			driving_ = true;
			float elevation = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;
			bool wantDay = isDay ? elevation > NightBelow : elevation > DayAbove;
			if (ready && wantDay != isDay) {
				try {
					setMode_.Invoke(controller, new object[] { wantDay });
					Kino.Log.Info($"[NatureFX] The game's {(wantDay ? "day" : "night")} lighting (sun at {elevation:0.0}°)");
				} catch (Exception e) {
					Kino.Log.Warning($"[NatureFX] The game's day / night switch failed: {e.InnerException?.Message ?? e.Message}");
					type_ = null;
				}
			}
			Status = $"follows NatureFX's sun ({elevation:0.0}°): {(wantDay ? "day" : "night")} lighting, the street lights {(wantDay ? "off" : "on")}";
		}
	}
}

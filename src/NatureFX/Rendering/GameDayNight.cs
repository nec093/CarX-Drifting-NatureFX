using System;
using System.Reflection;
using KSL.API;
using UnityEngine;

namespace NatureFX.Rendering {
	// The game's own day and night: a track's street lights, lit windows and the like belong to its night lighting
	// scenario (DayNightController loads it as an additive scene and switches its IDayNightBehaviours' lights on). The
	// game chooses it once, from the race's night mode; with NatureFX's time of day the lamps stayed off at night. This
	// follows NatureFX's sun instead: night lighting from a little before dusk (the sun below 6°), day again above 8°, through the
	// game's own DayNightController.SetDayNightMode (reflection: the game's types are not referenced). The game's own
	// choice comes back when NatureFX stops driving the time.
	public class GameDayNight {
		// sun elevation (°): the game's lighting scene takes a while to load, so the switch starts a little before dusk
		// (its lamps are faint against daylight) and is done by the time the street lights matter
		private const float NightBelow = 6.0f, DayAbove = 8.0f;
		// the switch is under way: the next scene load is only the lighting (NatureFXMod rescans it sooner)
		public static bool Switching { get; private set; }

		private Type type_;
		private PropertyInfo instance_, isDay_, isReady_, timeoutOver_;
		private MethodInfo setMode_;
		private bool looked_;
		private bool driving_;
		private bool? original_;
		private UnityEngine.Object controller_;
		// after a switch: the lighting scene loads (its own directional light may come with it); then NatureFX's sun is
		// found again and any other directional light it brought is switched off while NatureFX drives the time
		private float refreshAt_ = -1.0f;
		// not fighting the game: after a switch none for a while, and when the game sets its own back (a track or scene
		// load reapplies the race's choice) NatureFX waits, and gives up on this track after a few times. Both switching
		// back and forth loaded the lighting scene over and over.
		private const float MinInterval = 8.0f, BackOff = 30.0f;
		private float lastSwitch_ = -1000.0f, backOffUntil_;
		private bool? expected_;
		private int overruled_;
		private bool gaveUp_;
		private readonly System.Collections.Generic.List<Light> extraSuns_ = new();

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

		private void HideExtraSuns(Light keep) {
			foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (l.type != LightType.Directional || l == keep || !l.enabled || l.name.StartsWith("NatureFX")) {
					continue;
				}
				l.enabled = false;
				extraSuns_.Add(l);
				Kino.Log.Info($"[NatureFX] The game's lighting scene brought a second directional light ({l.name}): off while NatureFX drives the sun");
			}
		}

		private void RestoreExtraSuns() {
			foreach (var l in extraSuns_) {
				if (l != null) {
					l.enabled = true;
				}
			}
			extraSuns_.Clear();
		}

		public void Update(bool drive, SunController sunController) {
			var sun = sunController?.Sun;
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
				expected_ = null;
				overruled_ = 0;
				gaveUp_ = false;
				// a track just loaded: let it settle (it applies its own lighting first)
				backOffUntil_ = Time.unscaledTime + MinInterval;
			}
			bool ready = (isReady_ == null || (bool)isReady_.GetValue(controller)) && (timeoutOver_ == null || (bool)timeoutOver_.GetValue(controller));
			bool isDay = (bool)isDay_.GetValue(controller);
			if (drive && sun == null) {
				// the sun went with the lighting scene it belonged to: find it again (no switching back meanwhile)
				if (ready) {
					sunController?.Refresh();
				}
				Status = "finding the sun again after the lighting changed";
				return;
			}
			if (!drive) {
				if (driving_ && original_.HasValue && ready && isDay != original_.Value) {
					setMode_.Invoke(controller, new object[] { original_.Value });
					refreshAt_ = Time.unscaledTime + 0.5f;
				}
				driving_ = false;
				RestoreExtraSuns();
				if (refreshAt_ > 0.0f && ready && Time.unscaledTime >= refreshAt_) {
					refreshAt_ = -1.0f;
					sunController?.Refresh();
				}
				Status = $"the game's own ({(isDay ? "day" : "night")} lighting)";
				return;
			}
			if (!original_.HasValue) {
				original_ = isDay;
			}
			driving_ = true;
			// the game changed it without us: it wants its own (the race's choice, applied again on a load)
			if (ready && expected_.HasValue && isDay != expected_.Value) {
				expected_ = isDay;
				++overruled_;
				backOffUntil_ = Time.unscaledTime + BackOff;
				gaveUp_ = overruled_ >= 3;
				Kino.Log.Info($"[NatureFX] The game set its own {(isDay ? "day" : "night")} lighting back ({overruled_}x)" +
				              (gaveUp_ ? ": left to the game on this track" : $": waiting {BackOff:0} s"));
			}
			float elevation = Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg;
			bool wantDay = isDay ? elevation > NightBelow : elevation > DayAbove;
			if (gaveUp_) {
				Status = $"left to the game on this track (it set its own lighting back {overruled_} times): {(isDay ? "day" : "night")} lighting";
				return;
			}
			bool calm = Time.unscaledTime - lastSwitch_ >= MinInterval && Time.unscaledTime >= backOffUntil_;
			if (ready && calm && wantDay != isDay) {
				lastSwitch_ = Time.unscaledTime;
				expected_ = wantDay;
				try {
					setMode_.Invoke(controller, new object[] { wantDay });
					Kino.Log.Info($"[NatureFX] The game's {(wantDay ? "day" : "night")} lighting (sun at {elevation:0.0}°)");
					refreshAt_ = Time.unscaledTime + 0.5f;
					Switching = true;
					if (wantDay) {
						RestoreExtraSuns();
					}
				} catch (Exception e) {
					Kino.Log.Warning($"[NatureFX] The game's day / night switch failed: {e.InnerException?.Message ?? e.Message}");
					type_ = null;
				}
			}
			// the lighting scene has loaded: NatureFX's sun again (a scene may bring its own), the others off
			if (refreshAt_ > 0.0f && ready && Time.unscaledTime >= refreshAt_) {
				refreshAt_ = -1.0f;
				Switching = false;
				sunController.Refresh();
				HideExtraSuns(sunController.Sun);
			}
			Status = $"follows NatureFX's sun ({elevation:0.0}°): {(wantDay ? "day" : "night")} lighting, the street lights {(wantDay ? "off" : "on")}";
		}
	}
}

using System;
using NatureFX.Map;
using NatureFX.Rendering;
using NatureFX.Tools;
using NatureFX.Ui;
using NatureFX.Nature;
using UnityEngine;

namespace NatureFX {
	// In-game settings window (F8): header with the master switch, tab sidebar, card based content.
	internal class NatureFXWindow {
		private const int WindowId = 0x4E4658; // "NFX"
		private const float DefaultWidth = 820;
		private const float DefaultHeight = 560;
		private const float MinWidth = 600;
		private const float MinHeight = 360;
		private const float DefaultOpacity = 0.9f;
		private const float SidebarWidth = 150;
		private const float DefaultScale = 0.85f;

		private static readonly string[] Tabs = {
			"Overview",
			"Sun & time", "Sky & fog", "Lighting & shadows", "Ambient light & GI", "Reflections & glass",
			"Exposure & tone", "DLSS", "Lens & eye",
			"Weather", "Nature", "Road", "Cars", "Map",
			"Ray tracing", "Shader rebuild", "Settings", "Developer",
		};
		private static readonly string[] TonemapNames = { "ACES", "Neutral", "Filmic (GT) / AgX", "None" };
		private static readonly string[] ReflectionQualityNames = { "Performance", "Balanced", "Quality" };

		private readonly NatureFXMod mod_;
		private Vector2 position_ = new(60, 60);
		private Vector2 scroll_, sidebarScroll_;
		private int tab_;
		private bool visible_;
		private bool prevCursorVisible_;
		private CursorLockMode prevLockState_;
		private bool resizing_;
		private float uiScaleDraft_;
		private float fps_;
		private string mapMessage_;
		private string presetName_ = "";
		private string presetStatus_;
		private string pendingDelete_;
		private bool confirmReset_;
		private string[] presetList_;
		private readonly InputLock lock_ = new();

		public bool Visible => visible_;

		public void Destroy() {
			lock_.Release();
		}

		public NatureFXWindow(NatureFXMod mod) {
			mod_ = mod;
		}

		private static ISettings S => Settings.Data;

		public void Toggle() {
			visible_ = !visible_;
			if (visible_) {
				prevCursorVisible_ = Cursor.visible;
				prevLockState_ = Cursor.lockState;
			} else {
				Cursor.visible = prevCursorVisible_;
				Cursor.lockState = prevLockState_;
				lock_.Release();
				Settings.Save();
			}
		}

		// The game re-hides the cursor while driving; keep it usable while the window is open, and keep the game
		// behind it locked (the keyboard too while one of our text fields has focus).
		public void Update() {
			if (visible_) {
				Cursor.visible = true;
				Cursor.lockState = CursorLockMode.None;
				lock_.Hold(GUIUtility.keyboardControl != 0);
			}
		}

		public void OnGUI() {
			if (!visible_ || S == null) {
				return;
			}
			// The game hides the system cursor and draws its own from Rewired's mouse, which InputLock turns off,
			// and it re-hides the system cursor after our Update: OnGUI runs last in the frame, so show it here too.
			Cursor.visible = true;
			Cursor.lockState = CursorLockMode.None;

			Theme.Opacity = S.UiOpacity > 0.0f ? S.UiOpacity : DefaultOpacity;
			Theme.LargeText = S.UiLargeText;
			Theme.Ensure();

			// keep the window the same physical size on 1080p .. 4K
			float scale = UiScale();
			var oldMatrix = GUI.matrix;
			GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1.0f));

			var size = WindowSize(scale);
			var rect = new Rect(position_.x, position_.y, size.x, size.y);
			rect = GUI.Window(WindowId, rect, Draw, GUIContent.none, Theme.Window);
			position_ = new Vector2(
				Mathf.Clamp(rect.x, 0, Screen.width / scale - 120),
				Mathf.Clamp(rect.y, 0, Screen.height / scale - 60));

			GUI.matrix = oldMatrix;
		}

		// Same physical size on 1080p .. 4K, times the user's UI scale (default a bit smaller than 1:1).
		private static float UiScale() {
			float user = S.UiScale > 0.0f ? S.UiScale : DefaultScale;
			return Mathf.Clamp(Screen.height / 1080.0f, 0.75f, 2.0f) * Mathf.Clamp(user, 0.5f, 1.5f);
		}

		private Vector2 WindowSize(float scale) {
			float w = S.UiWidth > 0.0f ? S.UiWidth : DefaultWidth;
			float h = S.UiHeight > 0.0f ? S.UiHeight : DefaultHeight;
			return new Vector2(Mathf.Clamp(w, MinWidth, Screen.width / scale), Mathf.Clamp(h, MinHeight, Screen.height / scale));
		}

		private void Draw(int id) {
			if (Event.current.type == EventType.Layout) {
				Widgets.BeginFrame();
				UpdateFps();
			}
			// the tooltip of a control from another tab (or a closed section) must not linger
			if (Event.current.type == EventType.Repaint) {
				GUI.tooltip = "";
			}
			var size = WindowSize(UiScale());
			HandleResize(size);

			DrawHeader();
			GUILayout.Box(GUIContent.none, new GUIStyle { normal = { background = Theme.LineTex } }, GUILayout.Height(1), GUILayout.ExpandWidth(true));

			GUILayout.BeginHorizontal();
			DrawSidebar();
			GUILayout.Box(GUIContent.none, new GUIStyle { normal = { background = Theme.LineTex } }, GUILayout.Width(1), GUILayout.ExpandHeight(true));

			GUILayout.BeginVertical();
			scroll_ = GUILayout.BeginScrollView(scroll_, GUIStyle.none, GUI.skin.verticalScrollbar);
			GUILayout.Space(6);
			if (string.IsNullOrEmpty(Widgets.Filter)) {
				DrawTab(tab_);
			} else {
				// searching looks through every tab
				foreach (var group in VisibleGroups) {
					foreach (int i in group.Tabs) {
						DrawTab(i);
					}
				}
			}
			GUILayout.Space(10);
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
			GUILayout.Space(6);
			GUILayout.EndHorizontal();

			DrawFooter();
			HandleShortcuts();
			Widgets.DrawTooltip(size);
			GUI.DragWindow(new Rect(0, 0, size.x, 40));
		}

		private void DrawTab(int index) {
			switch (index) {
				case 0: DrawOverview(); break;
				case 1: DrawSun(); break;
				case 2: DrawSkyAndFog(); break;
				case 3: DrawLighting(); break;
				case 4: DrawRayTracedAO(); break;
				case 5: DrawReflections(); break;
				case 6: DrawExposureAndTone(); break;
				case 7: DrawDlss(); break;
				case 8: DrawLens(); break;
				case 9: DrawWeather(); break;
				case 10: DrawNature(); break;
				case 11: DrawRoad(); break;
				case 12: DrawCars(); break;
				case 13: DrawMapProfile(); break;
				case 14: DrawRayTracingBackend(); break;
				case 15: DrawShaderRebuild(); break;
				case 16: DrawSettings(); break;
				case 17: DrawDeveloper(); break;
			}
		}

		private void DrawHeader() {
			GUILayout.BeginHorizontal(Theme.Header, GUILayout.Height(40));
			GUILayout.Label("NatureFX", Theme.Title, GUILayout.Height(24));
			GUILayout.Space(10);
			string api = SystemInfo.graphicsDeviceType.ToString().Replace("Direct3D", "DX");
			string engine = NativeEngine.ModeName;
			GUILayout.Label($"{api}  ·  {engine}", Theme.Subtitle, GUILayout.Height(24));
			GUILayout.FlexibleSpace();

			GUILayout.Label("Search", Theme.Value, GUILayout.Width(48), GUILayout.Height(24)); // Ctrl+F
			GUI.SetNextControlName("NFXSearch");
			string filter = GUILayout.TextField(Widgets.Filter ?? "", 40, Theme.TextField, GUILayout.Width(180));
			if (filter != Widgets.Filter) {
				Widgets.Filter = filter;
				scroll_ = Vector2.zero;
			}
			GUILayout.Space(12);
			bool enabled = S.Enabled;
			if (Widgets.Switch("", ref enabled)) {
				S.Enabled = enabled;
				mod_.MarkDirty();
			}
			GUILayout.Label("Enabled", Theme.Label, GUILayout.Width(56), GUILayout.Height(24));
			var close = GUILayoutUtility.GetRect(24, 24, GUILayout.Width(24), GUILayout.Height(24));
			bool closeHover = close.Contains(Event.current.mousePosition);
			if (closeHover && Event.current.type == EventType.Repaint) {
				Theme.Fill(close, Theme.FrameHoverColor);
			}
			Theme.DrawIcon(new Rect(close.x + 5, close.y + 5, 14, 14), Theme.CloseTex, closeHover ? Theme.Foreground : Theme.Muted);
			if (GUI.Button(close, GUIContent.none, GUIStyle.none)) {
				Toggle();
			}
			GUILayout.EndHorizontal();
		}

		// tab index groups shown in the sidebar
		private static (string Name, int[] Tabs)[] VisibleGroups => Groups;

		// light and sky first (the own engine's), then what the camera does
		// with the picture, the world and its cars, and the engine and tools last
		private static readonly (string Name, int[] Tabs)[] Groups = {
			("GENERAL", new[] { 0 }),
			("LIGHT & SKY", new[] { 1, 2, 3, 4, 5 }),
			("CAMERA", new[] { 6, 7, 8 }),
			("WORLD", new[] { 9, 10, 11, 12, 13 }),
			("ADVANCED", new[] { 14, 15, 16, 17 })
		};

		private void DrawSidebar() {
			GUILayout.BeginVertical(Theme.Sidebar, GUILayout.Width(SidebarWidth));
			// scrolls when the window is shorter than the list
			sidebarScroll_ = GUILayout.BeginScrollView(sidebarScroll_, GUIStyle.none, GUIStyle.none);
			foreach (var group in VisibleGroups) {
				GUILayout.Label(group.Name, Theme.SidebarGroup);
				foreach (int i in group.Tabs) {
					bool active = i == tab_ && string.IsNullOrEmpty(Widgets.Filter);
					var r = GUILayoutUtility.GetRect(new GUIContent(Tabs[i]), Theme.SidebarItem, GUILayout.ExpandWidth(true));
					if (GUI.Button(r, Tabs[i], active ? Theme.SidebarItemActive : Theme.SidebarItem)) {
						tab_ = i;
						scroll_ = Vector2.zero;
						Widgets.Filter = "";
					}
					if (active && Event.current.type == EventType.Repaint) {
						GUI.DrawTexture(new Rect(r.x, r.y + 5, 2, r.height - 10), Theme.AccentBarTex);
					}
				}
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndScrollView();
			GUILayout.EndVertical();
		}

		private void DrawFooter() {
			GUILayout.Box(GUIContent.none, new GUIStyle { normal = { background = Theme.LineTex } }, GUILayout.Height(1), GUILayout.ExpandWidth(true));
			GUILayout.BeginHorizontal(new GUIStyle { padding = new RectOffset(14, 22, 3, 5) }, GUILayout.Height(22));
			GUILayout.Label($"{fps_:0} fps  ·  {1000.0f / Mathf.Max(fps_, 1.0f):0.0} ms", Theme.Status);
			GUILayout.FlexibleSpace();
			GUILayout.Label("Esc / F8 close  ·  Ctrl+F search  ·  right-click resets  ·  Ctrl+click types a value  ·  Left/Right nudge", Theme.Status);
			GUILayout.EndHorizontal();
		}

		// Esc clears the search, then closes; Ctrl+F jumps to the search box. Controls that use these keys
		// themselves (typing a slider value) consume the event first.
		private void HandleShortcuts() {
			var e = Event.current;
			if (e.type != EventType.KeyDown) {
				return;
			}
			if (e.keyCode == KeyCode.Escape) {
				if (!string.IsNullOrEmpty(Widgets.Filter)) {
					Widgets.Filter = "";
				} else {
					Toggle();
				}
				GUI.FocusControl(null);
				e.Use();
			} else if (e.keyCode == KeyCode.F && e.control) {
				GUI.FocusControl("NFXSearch");
				e.Use();
			}
		}

		private void UpdateFps() {
			float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
			fps_ = fps_ <= 0.0f ? 1.0f / dt : Mathf.Lerp(fps_, 1.0f / dt, 0.05f);
		}

		// bottom right corner drag resizes the window; the size is saved with the settings
		private void HandleResize(Vector2 size) {
			var grip = new Rect(size.x - 16, size.y - 16, 16, 16);
			var e = Event.current;
			if (e.type == EventType.Repaint) {
				GUI.DrawTexture(new Rect(grip.xMax - 9, grip.yMax - 4, 6, 1), Theme.GripTex);
				GUI.DrawTexture(new Rect(grip.xMax - 4, grip.yMax - 9, 1, 6), Theme.GripTex);
			}
			if (e.type == EventType.MouseDown && e.button == 0 && grip.Contains(e.mousePosition)) {
				resizing_ = true;
				e.Use();
			} else if (resizing_ && e.type == EventType.MouseDrag) {
				S.UiWidth = Mathf.Max(MinWidth, size.x + e.delta.x);
				S.UiHeight = Mathf.Max(MinHeight, size.y + e.delta.y);
				e.Use();
			} else if (resizing_ && (e.type == EventType.MouseUp || e.rawType == EventType.MouseUp)) {
				resizing_ = false;
			}
		}

		// ---------------------------------------------------------------- tabs

		private void DrawOverview() {
			DrawUserPresets();
		}

		// A preset can change anything: re-apply shader replacement and the rest.
		private void AfterPresetApplied() {
			// off then on: materials already switched keep their classification otherwise, so a preset with another
			// "Every material" / "Foliage too" looked the same until a restart (the Shader rebuild tab does the same)
			mod_.SetShaderReplacement(false);
			mod_.SetShaderReplacement(S.ReplaceShaders);
			mod_.MarkDirty();
			Settings.Save();
		}

		private void DrawUserPresets() {
			Widgets.BeginCard("My presets", "Save every NatureFX setting under a name, or share it as a one-line code.");

			GUILayout.BeginHorizontal();
			presetName_ = Widgets.TextField(presetName_, 40);
			if (Widgets.Button("Save", 80)) {
				string name = UserPresets.SanitizeName(presetName_);
				if (name.Length == 0) {
					presetStatus_ = "Enter a name first.";
				} else {
					presetStatus_ = UserPresets.Save(S, name) ? $"Saved \"{name}\"." : "Could not save, see the KSL log.";
					presetList_ = null;
				}
			}
			GUILayout.EndHorizontal();

			presetList_ ??= UserPresets.List();
			if (presetList_.Length == 0) {
				Widgets.Info("No presets yet.");
			}
			foreach (string name in presetList_) {
				GUILayout.BeginHorizontal(Theme.Row);
				GUILayout.Label(name, Theme.Label, GUILayout.Height(30));
				GUILayout.FlexibleSpace();
				if (Widgets.Button("Load", 70)) {
					int n = UserPresets.Load(S, name);
					presetStatus_ = n >= 0 ? $"Loaded \"{name}\" ({n} values)." : "Could not load, see the KSL log.";
					AfterPresetApplied();
				}
				if (Widgets.Button("Export", 80)) {
					string code = UserPresets.ExportFile(name);
					if (code != null) {
						GUIUtility.systemCopyBuffer = code;
						presetStatus_ = $"Share code for \"{name}\" copied to the clipboard.";
					} else {
						presetStatus_ = "Could not read the preset, see the KSL log.";
					}
				}
				bool confirm = pendingDelete_ == name;
				if (Widgets.Button(confirm ? "Sure?" : "Delete", 70)) {
					if (confirm) {
						UserPresets.Delete(name);
						presetStatus_ = $"Deleted \"{name}\".";
						pendingDelete_ = null;
						presetList_ = null;
					} else {
						pendingDelete_ = name;
					}
				}
				GUILayout.EndHorizontal();
			}

			GUILayout.Space(4);
			GUILayout.BeginHorizontal();
			if (Widgets.Button("Copy current as code", 170)) {
				string name = string.IsNullOrWhiteSpace(presetName_) ? "Shared" : presetName_;
				GUIUtility.systemCopyBuffer = UserPresets.Export(S, name);
				presetStatus_ = "Share code copied to the clipboard.";
			}
			if (Widgets.Button("Import from clipboard", 180)) {
				int n = UserPresets.Import(S, GUIUtility.systemCopyBuffer, out string name);
				if (n < 0) {
					presetStatus_ = "The clipboard does not contain a NatureFX preset.";
				} else {
					presetStatus_ = $"Imported {(name != null ? $"\"{name}\" " : "")}({n} values). Save it to keep it.";
					presetName_ = name ?? presetName_;
					AfterPresetApplied();
				}
			}
			if (Widgets.Button("Open folder", 110)) {
				System.IO.Directory.CreateDirectory(UserPresets.Folder);
				Application.OpenURL("file:///" + UserPresets.Folder.Replace('\\', '/'));
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();

			if (!string.IsNullOrEmpty(presetStatus_)) {
				Widgets.Info(presetStatus_);
			}
			Widgets.EndCard();

			Widgets.BeginCard("Reset", "Every setting back to a fresh install's values (the window's size and the on / off switch stay). " +
			                           "Save a preset first to come back to what you have now. One value: right-click it.");
			GUILayout.BeginHorizontal();
			if (Widgets.Button(confirmReset_ ? "Sure? Reset everything" : "Reset all to defaults", 220)) {
				if (confirmReset_) {
					int n = UserPresets.ResetAll(S);
					presetStatus_ = $"Reset to defaults ({n} values changed).";
					confirmReset_ = false;
					AfterPresetApplied();
				} else {
					confirmReset_ = true;
				}
			}
			if (confirmReset_ && Widgets.Button("Cancel", 90)) {
				confirmReset_ = false;
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			Widgets.EndCard();
		}

		private void DrawGraphicsApi() {
			Widgets.BeginCard("Graphics API", "NatureFX needs DirectX 12 (its own engine traces on DX12; DX11 is no longer supported).", true);
			Widgets.Info($"Current: {GraphicsApiSwitcher.Current}");
			Widgets.Info($"To always start in DX12: Steam > CarX Drift Racing Online > Properties > Launch options: {GraphicsApiSwitcher.LaunchOption}");
			Widgets.EndCard();
		}

		// Sky & light > Sky & fog: the own engine's sky, clouds, stars and fog
		private void DrawSkyAndFog() {
			Widgets.BeginCard("Sky", "The sky computed by NatureFX's own engine from the sun or the moon and the air (physically based), " +
			                         "with its clouds, sun and moon discs and stars; it lights the scene with the sky light, sun, lamps and " +
			                         "reflections of the own engine.");
			Widgets.Info($"Now: {mod_.RT.SkyStatus}");
			Widgets.Info($"Own lighting: {Rendering.OwnLighting.Status}");
			Widgets.Info($"HDRP's sky: {mod_.Volume.SkyState}");
			Widgets.Info($"Cube for HDRP: {Rendering.AtmospherePass.CubeStatus}");
			Widgets.Info($"Sky light: {Rendering.AtmospherePass.ProbeStatus}");
			Widgets.Info($"Track reflection probes: {(mod_.TrackProbes.Count > 0 ? $"{mod_.TrackProbes.Count} off (reflections show the own sky)" : "none off")}");
			Widgets.EndCard();

			Widgets.BeginCard("Clouds and stars", "Volumetric clouds of the weather's cloud types, drifting with its wind, and their shadows on the ground. " +
			                                      "A map can put a cloud sea into its valleys ([clouds] sea = <height>).");
			Other(Widgets.Switch("Volumetric clouds", S.Clouds, v => S.Clouds = v));
			Widgets.BeginDependent(S.Clouds);
			Other(Widgets.Slider("Cloud shadows", S.CloudShadows, 0, 1, v => S.CloudShadows = v));
			Widgets.EndDependent();
			Other(Widgets.Slider("Star brightness", S.StarBrightness, 0, 20, v => S.StarBrightness = v, "0.0", "x"));
			Widgets.Info("The stars against their physical brightness: the eye's night vision sees far more of them than a camera's exposure.");
			Widgets.Info($"Clouds: {mod_.Clouds.State}  ·  Stars: {Rendering.AtmospherePass.StarStatus}");
			Widgets.EndCard();

			Widgets.BeginCard("Fog", "Fog and haze in 3D from the own engine, lit by the traced sun or moon, the sky and the lamps and headlights; " +
			                         "the map's local fog and the tyre smoke are part of it.");
			Other(Widgets.Slider("Distant haze", S.AerialHaze, 0, 1, v => S.AerialHaze = v));
			Other(Widgets.Slider("Headlight beams", S.HeadlightShafts, 0, 4, v => S.HeadlightShafts = v));
			Widgets.Info("How much the air shows the headlights' and lamps' beams: clear air, rain and fog alike (0: not at all; 1: as real air; in fog and rain at 1 or more as dense as they are).");
			Widgets.Info($"Now: {mod_.RT.FogStatus}");
			Widgets.EndCard();

			DrawLightShafts();
		}

		// Lighting & shadows: the own engine's lighting as a whole, the sun's and the lamps' traced shadows, the game's
		// particles lit by it, and HDRP's shadows where the own engine does not do them
		private void DrawLighting() {
			Widgets.BeginCard("Lighting", "All of the lighting is the own engine's: traced sun and moon shadows, lamp and headlight shadows, " +
			                              "the sky light and GI, reflections. Their quality is set below and in Ambient light & GI and Reflections & glass.");
			Widgets.Info($"Shadows: {mod_.RT.ShadowStatus}");
			Widgets.Info($"Reflections: {mod_.RT.ReflectionStatus}");
			Widgets.EndCard();

			DrawRayTracedShadows();
			Widgets.BeginCard("The game's particles", "The game's and the track's own alpha blended particles (dust, spray, smoke) drawn with NatureFX's particle shader, " +
			                                          "lit by the own engine's sun or moon, sky and lamps and inside its 3D fog. Fire, sparks and glows keep their own.");
			Widgets.Info($"Now: {mod_.GameFx.Count} particle systems drawn by NatureFX, {mod_.GameFx.Skipped} kept (additive, mesh or no texture)");
			Widgets.EndCard();

		}

		}

		private void DrawSun() {
			Widgets.BeginCard("Time of day", mod_.Sun.Sun != null ? $"Driving \"{mod_.Sun.Sun.name}\"" : "No directional light found in this scene");
			Other(Widgets.Switch("Override sun", S.SunOverride, v => S.SunOverride = v));
			Widgets.BeginDependent(S.SunOverride);
			Other(Widgets.Switch("The game's night lights", S.SyncGameNight, v => S.SyncGameNight = v,
				"The track's own night lighting (its street lights, lit windows) switches on at dusk and off at dawn of NatureFX's time, as the game does for a night race."));
			Widgets.Info($"Now: {mod_.GameNight.Status}");
			Other(Widgets.Slider("Time", S.TimeOfDay, 0, 24, v => S.TimeOfDay = v, "0.0", $" h  ({Clock(S.TimeOfDay)})"));
			Other(Widgets.Switch("Time passes", S.TimePasses, v => S.TimePasses = v,
				"The time of day runs on by itself, at the speed below (the slider above follows it)."));
			Widgets.BeginDependent(S.TimePasses);
			Other(Widgets.Slider("Speed", S.TimeSpeed, 1, 720, v => S.TimeSpeed = Mathf.Round(v), "0", $"x  (a day in {DayLength(S.TimeSpeed)})"));
			Widgets.EndDependent();
			Other(Widgets.Slider("Heading offset", S.SunAzimuthOffset, -180, 180, v => S.SunAzimuthOffset = v, "0", "°"));
			Other(Widgets.Slider("Max elevation", S.SunMaxElevation, 5, 90, v => S.SunMaxElevation = v, "0", "°"));
			Other(Widgets.Slider("Intensity", S.SunIntensityScale, 0, 3, v => S.SunIntensityScale = v));
			Widgets.EndDependent();
			Other(Widgets.Switch("Moonlight at night", S.Moonlight, v => S.Moonlight = v));
			Widgets.BeginDependent(S.Moonlight);
			// 0.001 to 0.1 on a log scale (a hundredfold: every step as fine at the dark end as at the bright one)
			float moonLog = Mathf.Log10(Mathf.Clamp(S.MoonBrightness, 0.001f, 0.1f));
			Other(Widgets.Slider("Moon brightness", moonLog, -3, -1, v => S.MoonBrightness = Mathf.Pow(10.0f, v), "0.00", " (log10)"));
			Widgets.Info(FormattableString.Invariant($"{S.MoonBrightness:0.####}x: the moon lights the ground with about {S.MoonBrightness * 2.0f:0.####} lux. ") +
			             "About 0.1x is a real full moon (0.2 to 0.3 lux), 0.01x a half moon, 0.001x hardly more than the stars.");
			Widgets.EndDependent();
			Widgets.EndCard();

		}

		private void DrawNature() {
			Widgets.BeginCard("Wind");
			Other(Widgets.Switch("Enable", S.Wind, v => S.Wind = v));
			Widgets.BeginDependent(S.Wind);
			Other(Widgets.Slider("Strength", S.WindStrength, 0, 2, v => S.WindStrength = v));
			Other(Widgets.Slider("Gusts", S.WindGust, 0, 1, v => S.WindGust = v));
			Other(Widgets.Slider("Direction", S.WindDirection, 0, 360, v => S.WindDirection = v, "0", "°"));
			Other(Widgets.Slider("Leaf flutter", S.LeafFlutter, 0, 1, v => S.LeafFlutter = v));
			Other(Widgets.Slider("Foliage sway", S.FoliageSway, 0.05f, 3, v => S.FoliageSway = v));
			Other(Widgets.Switch("Cars push grass", S.InteractiveFoliage, v => S.InteractiveFoliage = v));
			Widgets.EndDependent();
			Widgets.EndCard();
		}

		private void DrawWeather() {
			Widgets.BeginCard("Weather");
			Other(Widgets.Switch("Enable", S.Accumulation, v => S.Accumulation = v));
			Widgets.BeginDependent(S.Accumulation);
			int preset = Mathf.Clamp(S.WeatherPreset, 0, WeatherController.Names.Length - 1);
			if (Widgets.Chips(ref preset, WeatherController.Names)) {
				S.WeatherPreset = preset;
				Other(true);
			}
			Other(Widgets.Switch("Changing weather", S.WeatherCycle, v => S.WeatherCycle = v, "Moves between clear, cloudy, rain, fog... on its own."));
			Widgets.BeginDependent(S.WeatherCycle);
			Other(Widgets.Slider("Change every", S.WeatherCycleMinutes, 2, 60, v => S.WeatherCycleMinutes = v, "0", " min"));
			Widgets.EndDependent();
			if (S.WeatherPreset <= 1 && !S.WeatherCycle) {
				Other(Widgets.Slider("Clouds", S.CloudAmount, 0, 1, v => S.CloudAmount = v, "0.00"));
				Widgets.Info("The clouds with the Clear and Manual weather: 0 a clear sky with a few fair weather clouds, 0.5 half covered, 1 overcast. Manual rain closes the sky over at least as far as it needs.");
			}
			Other(Widgets.Slider("Change time", S.WeatherChangeTime, 5, 300, v => S.WeatherChangeTime = v, "0", " s"));
			Widgets.Info("How long a change of weather takes, the clouds included.");
			Widgets.EndDependent();
			var w = Weather.Current;
			Widgets.Info($"Now: rain {w.Rain:P0} · wet {w.Wetness:P0} · snow {w.Snow:P0} · leaves {w.Leaves:P0} · clouds {w.Cloud:P0}");
			Widgets.EndCard();

			Widgets.BeginCard("Season", "Colours of the trees and grass that the map's profile describes, and the floating particles. " +
			                            "Auto: the Autumn and Snow weather, else the map's own season.");
			int season = Mathf.Clamp(S.Season, 0, SeasonNames.Length - 1);
			if (Widgets.Chips(ref season, SeasonNames)) {
				S.Season = season;
				Other(true);
			}
			Widgets.Info($"Now: {Seasons.Describe()}");
			Other(Widgets.Slider("Real leaf colour", S.FoliageColor, 0, 1, v => S.FoliageColor = v, "0.00"));
			Widgets.Info("1: leaves and grass as dark and olive as real ones (a leaf sends back some 4 to 12 % of the light). 0: the textures' own colours.");
			Widgets.EndCard();

			Widgets.BeginCard("Rain and snow", "Falling rain and snow around the camera, with the weather: rain streaks that splash where they land and show in headlights and under street lights, drifting snowflakes. 0 turns them off.");
			Widgets.Slider("Precipitation", S.Precipitation, 0, 2, v => S.Precipitation = v);
			Other(Widgets.Slider("Snow depth", S.SnowDepth, 0, 0.5f, v => S.SnowDepth = v, "0.00", " m"));
			Widgets.Info($"Now: {mod_.Rain.State}");
			Widgets.EndCard();

			// only the Manual preset uses these values
			if (S.WeatherPreset == 0) {
				DrawManualWeather();
			}
			DrawBuildUp();
		}

		private void DrawManualWeather() {
			Widgets.BeginCard("Manual values", "Used when the preset is Manual.");
			Other(Widgets.Slider("Rain", S.Rain, 0, 1, v => S.Rain = v));
			Other(Widgets.Slider("Wetness", S.Wetness, 0, 1, v => S.Wetness = v));
			Other(Widgets.Slider("Snow", S.Snow, 0, 1, v => S.Snow = v));
			Other(Widgets.Slider("Fallen leaves", S.Leaves, 0, 1, v => S.Leaves = v));
			Other(Widgets.Slider("Dust", S.Dust, 0, 1, v => S.Dust = v));
			Widgets.EndCard();
		}

		private void DrawBuildUp() {
			Widgets.BeginCard("Build up over time");
			Other(Widgets.Switch("Enable", S.AccumulateOverTime, v => S.AccumulateOverTime = v));
			Widgets.BeginDependent(S.AccumulateOverTime);
			Other(Widgets.Slider("Speed", S.AccumulationRate, 0.005f, 0.5f, v => S.AccumulationRate = v, "0.000", " /min"));
			Widgets.EndDependent();
			var c = mod_.Accumulation.Current;
			Widgets.Info($"Now: snow {c.x:P0} · leaves {c.y:P0} · dust {c.z:P0} · wet {c.w:P0}");
			if (Widgets.Button("Reset build up", 140)) {
				mod_.Accumulation.ResetProgress();
			}
			Widgets.EndCard();
		}

		private void DrawRoad() {
			Widgets.BeginCard("Road surface", "Marks are kept around the camera and cleared when you drive far away.");
			Other(Widgets.Switch("Tyre marks, dry line, snow ruts", S.TireMarks, v => S.TireMarks = v));
			Widgets.BeginDependent(S.TireMarks);
			Other(Widgets.Slider("Rubber when sliding", S.RubberAmount, 0, 2, v => S.RubberAmount = v));
			Widgets.EndDependent();
			Other(Widgets.Slider("Surface detail", S.SurfaceDetail, 0, 1, v => S.SurfaceDetail = v));
			Widgets.BeginDependent(S.ReplaceShaders);
			Other(Widgets.Slider("Road wear", S.RoadWear, 0, 1, v => S.RoadWear = v));
			Widgets.Info("Rubber laid down on the driven ground in long streaks, worn smooth in places (needs Replace game shaders, Shader rebuild).");
			Widgets.EndDependent();
			if (Widgets.Button("Clear marks", 120)) {
				mod_.Tracks.Clear();
			}
			Widgets.EndCard();

		}

		private void DrawCars() {
			Widgets.BeginCard("Headlights", "A real low beam in place of the game's round spot: the lamp's colour and strength, and the beam's sharp cut-off " +
			                                "just below the horizon (rising on the kerb side, left: driving on the left), so the road is lit and the eyes above it are not.");
			int lamp = Mathf.Clamp(S.HeadlightType, 0, 3);
			if (Widgets.Chips(ref lamp, new[] { "Game", "Halogen", "HID", "LED" })) {
				S.HeadlightType = lamp;
				Other(true);
			}
			Widgets.Info("Halogen: warm 3200 K, 18 000 cd a lamp. HID (xenon): 4300 K, 36 000 cd, a faint blue edge at the cut-off. LED: 5800 K, 30 000 cd.");
			Widgets.BeginDependent(S.HeadlightType != 0);
			int standard = Mathf.Clamp(S.HeadlightStandard, 0, 1);
			if (Widgets.Chips(ref standard, new[] { "Japan (left-hand traffic)", "Europe / US (right-hand traffic)" })) {
				S.HeadlightStandard = standard;
				Other(true);
			}
			Widgets.Info(S.HeadlightStandard == 0
				? "Japan's low beam (the safety standard, harmonised with UN R112 / R149 for driving on the left): flat cut-off on the right, the oncoming side, rising 15° on the left to light the kerb and the signs; the brightest part a little left of the middle, about 1° down."
				: "Europe's and America's low beam (driving on the right): the same mirrored, the cut-off rising on the right.");
			Other(Widgets.Slider("Beam aim", S.HeadlightAim, -1, 3, v => S.HeadlightAim = v, "0.0", "° down"));
			Other(Widgets.Slider("Colour temperature", S.HeadlightKelvin, 0, 8000, v => S.HeadlightKelvin = v < 2700.0f ? 0.0f : v, "0", " K"));
			Widgets.Info(S.HeadlightKelvin > 0.0f ? "Your colour; 0 (far left) is the lamp type's own." : "The lamp type's own (halogen 3200 K, HID 4300 K, LED 5800 K); move it for your own (2700 to 8000 K).");
			Widgets.Info("The low beam's axis below level, from the car's body: 0.6° is the usual setting (1 %); more lights the road closer, less reaches further and dazzles.");
			Widgets.Switch("High beam (F7)", mod_.Headlights.HighBeam, v => mod_.Headlights.HighBeam = v,
				"Your car's high beam: no cut-off, the light gathered far ahead, 2.5 times the low beam. Not saved.");
			Widgets.EndDependent();
			Widgets.Info($"Now: {mod_.Headlights.Status}");
			Widgets.EndCard();

			Widgets.BeginCard("Car finish", "Paint reflects like real car paint: a clear varnish mirror over the coloured base. DX12 ray tracing.");
			Other(Widgets.Switch("Chrome rims", S.CarChromeRims, v => S.CarChromeRims = v,
				"Metal rims, chrome paint included, polished to a mirror instead of brushed aluminium. Painted rims stay as they are."));
			Widgets.Info($"Now: {mod_.Finish.Status}");
			Widgets.EndCard();
			Widgets.BeginCard("Cars in the rain", "Beading drops, water running off and an oily sheen, drawn over the car paint.");
			Other(Widgets.Switch("Enable", S.CarRain, v => S.CarRain = v));
			Widgets.BeginDependent(S.CarRain);
			Other(Widgets.Slider("Drops", S.CarRainDrops, 0, 1, v => S.CarRainDrops = v));
			Other(Widgets.Slider("Oil film", S.OilFilm, 0, 1, v => S.OilFilm = v));
			Widgets.EndDependent();
			Widgets.EndCard();

			Widgets.BeginCard("Smoke, glow and dirt", "Volumetric tyre smoke and spray lit by the sun, lamps and headlights " +
			                                                    "(needs Tyre marks on: it starts where the tyres slide). Brake discs that glow after hard braking, afterfire.");
			Widgets.Slider("Tyre smoke and spray", S.TireSmoke, 0, 2, v => S.TireSmoke = v);
			Widgets.BeginDependent(S.TireSmoke > 0.0f);
			Widgets.EndDependent();
			Widgets.Switch("Glowing brakes, afterfire", S.CarGlow, v => S.CarGlow = v);
			Widgets.Switch("Dirt, dust and snow on the car", S.CarDirt, v => S.CarDirt = v, "Builds up as you drive: mud on wet roads, dust on dusty ones, snow on top while it snows. Rain washes it off.");
			Widgets.BeginDependent(S.CarDirt);
			Widgets.Slider("Most dirt", S.CarDirtAmount, 0, 1, v => S.CarDirtAmount = v);
			if (Widgets.Button("Wash the car", 140)) {
				mod_.CarRainFx.Wash();
			}
			Widgets.EndDependent();
			Widgets.Info($"Tyre smoke: {mod_.Smoke.Particles} particles ({mod_.Smoke.ShadowedParticles} in shadow), hottest tread {mod_.Smoke.HottestTyre:0} C (smoke from 140 C)  ·  Car: mud {mod_.CarRainFx.Mud:P0}, dust {mod_.CarRainFx.Dust:P0}, snow {mod_.CarRainFx.Snow:P0}");
			Widgets.EndCard();
		}

		private void DrawReflections() {
			Widgets.BeginCard("Ray traced reflections", "Wet roads, puddles and car paint reflect the whole scene, including what is off screen. DX12 only.");
			Other(Widgets.Slider("Strength", S.RTReflectionIntensity, 0, 2, v => S.RTReflectionIntensity = v));
			Other(Widgets.Slider("Rough surfaces up to", S.RTReflectionRoughness, 0.05f, 1, v => S.RTReflectionRoughness = v));
			Other(Widgets.Slider("Ray length", S.RTReflectionDistance, 10, 500, v => S.RTReflectionDistance = v, "0", " m"));
			int quality = Mathf.Clamp(S.RTReflectionQuality, 0, ReflectionQualityNames.Length - 1);
			if (Widgets.Chips(ref quality, ReflectionQualityNames)) {
				S.RTReflectionQuality = quality;
				Other(true);
			}
			Widgets.Info(quality == 0 ? "Half resolution." : quality == 1 ? "Half resolution, mirrors and chrome reflect each other." : "Full resolution, mirrors and chrome reflect each other (about 4x the cost of half resolution).");
			Other(Widgets.Slider("Glass refraction", S.RTGlassRefraction, 0, 1, v => S.RTGlassRefraction = v));
			Widgets.Info("How much curved glass (lenses, screens) bends what is seen through it. 0: off.");
			Other(Widgets.Switch("Wet road streaks", S.RTWetReflections, v => S.RTWetReflections = v,
				"When the road is wet (Weather: wetness or rain), reflections of lamps and cars stretch along the view as on real wet asphalt."));
			Widgets.Info($"Now: {mod_.RT.ReflectionStatus}");
			Widgets.Info($"Glass: {mod_.RT.GlassStatus}");
			Widgets.EndCard();

			Widgets.BeginCard("Glass", "The own engine draws the glass whole: what it lets through (tinted by its colour as far as it is opaque), what it " +
			                           "mirrors (traced), the sun's glint and the bend of solid glass. HDRP's glass is hidden meanwhile.");
			Widgets.Info($"Now: {mod_.RT.GlassStatus}");
			Widgets.EndCard();
		}

		private void DrawRayTracedShadows() {
			Widgets.BeginCard("Ray traced sun shadows", "Shadows traced against the whole scene: sharp where objects touch, softer further away, " +
			                                            "like the real sun. Replaces the game's sun shadows. DX12 only.");
			Other(Widgets.Slider("Strength", S.RTShadowStrength, 0, 1, v => S.RTShadowStrength = v));
			Other(Widgets.Switch("Full resolution", S.RTShadowFullRes, v => S.RTShadowFullRes = v,
				"Sharp shadows where objects touch the ground and thin ones (poles, wires, leaves). Off: half resolution, about a quarter of the rays."));
			Other(Widgets.Switch("Hide the game's painted shadows", S.RTHideFakeShadows, v => S.RTHideFakeShadows = v,
				"CarX lays dark planes under cars, trees and bushes in place of real shadows; with the traced shadows they darken twice."));
			Other(Widgets.Switch("Light through glass", S.RTGlassShadows, v => S.RTGlassShadows = v,
				"The sun through windows is dimmed by the glass (tinted windows more) instead of passing untouched."));
			Widgets.Info($"Painted shadows: {mod_.PaintedShadows.Status}");
			Other(Widgets.Switch("Swaying foliage", S.RTWind, v => S.RTWind = v,
				"The traced shadows of the leaves sway with the wind as the leaves do (own engine; foliage from the shader rebuild). Off: they stay still."));
			Other(Widgets.Switch("Exact sun light", S.RTExactSun, v => S.RTExactSun = v,
				"Shadows take away exactly the sun light the game added (its materials drawn once more to read them). Off: an estimate, a little lighter on the CPU and GPU."));
			Other(Widgets.Switch("Full relight", S.RTFullRelight, v => S.RTFullRelight = v,
				"The own engine lights every opaque pixel again: HDRP's image is taken apart exactly (its baked sky light and glow, sun, lamps, reflections) " +
				"and put together with the traced shadows, sky share, GI and the own sky. Needs Exact sun light. Off: HDRP's image with corrections."));
			Other(Widgets.Switch("Shadows on transparent surfaces", S.RTTransparentShadows, v => S.RTTransparentShadows = v,
				"Garlands, flags, see-through sheets and other transparent things take the sun's traced shadow too (a ray traced shadow map " +
				"around the camera; NatureFX shaders only, the shader rebuild's)."));
			Widgets.Info($"Transparent surfaces: {Rendering.SunShadowMap.Status}");
			Other(Widgets.Switch("Per-lamp shadows", S.RTLampMasks, v => S.RTLampMasks = v,
				"Each lamp and headlight shadowed by its own traced mask (the four that matter most per pixel), its shine on a wet road too. Needs Exact sun light. Off: one blocked share for all lamps."));
			Other(Widgets.Slider("Sun size (softness)", S.SunSize, 0.1f, 3, v => S.SunSize = v, "0.00", "°"));
			Widgets.Info("0.53° is the real sun; larger values soften shadows like a hazy or low sun.");
			Other(Widgets.Slider("Leaf translucency", S.RTLeafTranslucency, 0, 1, v => S.RTLeafTranslucency = v));
			Widgets.Info("Leaves and grass lit from behind glow with the sunlight passing through them.");
			Widgets.Info($"Now: {mod_.RT.ShadowStatus}");
			Widgets.EndCard();

			Widgets.BeginCard("Lamps and headlights", "The 128 point and spot lights nearest to the camera light what reflections and bounce light " +
			                                          "show, and cast soft ray traced shadows where the game gives them none. Mostly at night. DX12 only.");
			Other(Widgets.Switch("Glowing surfaces light up", S.RTEmissiveLights, v => S.RTEmissiveLights = v,
				"Bright neon, signs and lamp shades light what is around them, with soft shadows (they are not lights to the game)."));
			Widgets.EndCard();
		}

		// Camera > Exposure & tone: the own post-processing's exposure, bloom, tone curve and colour
		private void DrawExposureAndTone() {
			Widgets.BeginCard("Own post-processing", "NatureFX's own exposure (metering and eye adaptation), bloom, tone curve and colour, in place of HDRP's. " +
			                                         "HDRP's own post-processing, TAA and colour grading are off meanwhile (anti-aliasing: DLSS, else the own TAA).");
			Other(Widgets.Slider("Bloom", S.PostBloom, 0, 0.2f, v => S.PostBloom = v, "0.000"));
			Widgets.Info("The share of all light a lens and the eye scatter around: bright lamps glow, a grey wall hardly at all (no threshold).");
			Widgets.Info($"Now: {mod_.Post.Status}");
			Widgets.EndCard();

			Widgets.BeginCard("Exposure", "Fixed: one exposure for every scene, like a camera set by hand; eye adaptation brightens shade and tunnels and settles back in sunlight.");
			int mode = Mathf.Clamp(S.ExposureMode, 0, 1);
			if (Widgets.Chips(ref mode, new[] { "Fixed", "Eye adaptation" })) {
				S.ExposureMode = mode;
				Other(true);
			}
			if (mode == 0) {
				Other(Widgets.Slider("Fixed exposure", S.FixedExposure, -2, 18, v => S.FixedExposure = v, "0.0", " EV"));
				Widgets.Info("Real-world EV: a sunny day about 15, overcast 12, a lit street at night about 3.");
			}
			Widgets.BeginDependent(mode == 1);
			Other(Widgets.Slider("Darkest scene", S.AutoExposureMin, -6, 16, v => S.AutoExposureMin = v, "0.0", " EV"));
			Other(Widgets.Slider("Brightest scene", S.AutoExposureMax, -2, 18, v => S.AutoExposureMax = v, "0.0", " EV"));
			Widgets.Info("Real-world EV: a sunny day is about 15, a lit street at night about 3, a moonlit night about -2. " +
			             "Darkest scene is how far the eye opens up at night: lower lets a moonlit night show.");
			Other(Widgets.Slider("Adaptation speed", S.AdaptationSpeed, 0.1f, 5, v => S.AdaptationSpeed = v));
			Other(Widgets.Slider("Night seen dark", S.NightDarkness, 0, 1, v => S.NightDarkness = v, "0.00"));
			Widgets.Info("The eye does not lift a dark scene to grey: about 2 stops darker under street lamps, 3.5 in a dark night. 0: every scene as bright as day.");
			Widgets.EndDependent();
			Other(Widgets.Slider("Exposure compensation", S.Exposure, -3, 3, v => S.Exposure = v, "0.00", " EV"));
			var probe = mod_.Exposure;
			Widgets.Info(probe.Valid
				? FormattableString.Invariant($"Current exposure: {probe.EV100:0.00} EV100 (x{probe.Multiplier:0.#####}; the track's sun raised {Rendering.Units.PhysicalScale:0} times to real sunshine)")
				: "Current exposure: not available");
			Widgets.EndCard();

			Widgets.BeginCard("Tone");
			Other(Widgets.Switch("Override tonemapping", S.AcesTonemapping, v => S.AcesTonemapping = v));
			Widgets.BeginDependent(S.AcesTonemapping);
			int tone = Mathf.Clamp(S.TonemapMode, 0, TonemapNames.Length - 1);
			if (Widgets.Chips(ref tone, TonemapNames)) {
				S.TonemapMode = tone;
				Other(true);
			}
			Widgets.EndDependent();
			Other(Widgets.Slider("Contrast", S.Contrast, -50, 50, v => S.Contrast = v, "0"));
			Other(Widgets.Slider("Saturation", S.Saturation, -50, 50, v => S.Saturation = v, "0"));
			Widgets.Info("Like a camera: AgX, contrast 8, saturation 0 (highlights turn white, bright colours do not clip). Neutral keeps the surfaces' colours saturated up to white.");
			Other(Widgets.Slider("White balance (eye)", S.WhiteBalance, 0, 1, v => S.WhiteBalance = v, "0.00"));
			Widgets.Info("How much the eye takes the light around as white: a sunset or warm lamps less orange, a shade under a blue " +
			             "sky less blue. 0: the camera's daylight white. Less in the dark, as the eye. Needs Own post-processing.");
			Widgets.EndCard();
		}

		// Camera > DLSS: NVIDIA's anti-aliasing and upscaling in place of HDRP's TAA (Rendering.Dlaa)
		private void DrawDlss() {
			Widgets.BeginCard("NVIDIA DLSS", "Anti-aliasing and upscaling by NatureFX's own engine in place of HDRP's TAA (RTX GPUs; others get the own TAA). " +
			                                 "");
			Other(Widgets.Switch("NVIDIA DLSS", S.Dlaa, v => S.Dlaa = v));
			Widgets.BeginDependent(S.Dlaa);
			int dlss = Mathf.Clamp(S.DlssQuality, 0, Rendering.Dlaa.Qualities.Length - 1);
			if (Widgets.Chips(ref dlss, Rendering.Dlaa.Qualities)) {
				S.DlssQuality = dlss;
				Other(true);
			}
			Other(Widgets.Switch("Ray Reconstruction", S.DlssRayReconstruction, v => S.DlssRayReconstruction = v,
				"DLSS denoises the ray traced sun shadows with the whole picture, in place of NRD and NatureFX's filter " +
				"(reflections, ambient light, GI and lamp shadows keep theirs). Needs the exact sun light (Shadows)."));
			Widgets.EndDependent();
			Widgets.Info("DLAA: anti-aliasing at the display resolution. Quality, Balanced, Performance, Ultra perf.: rendered at 67, 58, 50, " +
			             "33 % of it and upscaled (faster; the sun shadows get 2 to 4 rays a pixel to keep their quality). At 1080p Quality or " +
			             "Balanced; Ultra perf. is for 4K and above.");
			Widgets.Info($"Now: {Rendering.Dlaa.Status}");
			Widgets.EndCard();
		}

		// Camera > Lens & eye: what a lens and the eye add to the picture
		private void DrawLens() {
			Widgets.BeginCard("Eye and lens", "Night vision: in the dark colours fade towards blue-green and dim parts get grainy, as the eye sees. " +
			                                  "Lens flare: ghosts and streaks from bright lights. Depth of field: focus on the car, for replays and photos.");
			Widgets.Slider("Night vision", S.NightVision, 0, 1, v => S.NightVision = v);
			Widgets.Slider("Lens flare", S.LensFlare, 0, 1, v => S.LensFlare = v);
			Widgets.BeginDependent(S.LensFlare > 0.0f);
			Widgets.Slider("Light streaks", S.LensStreaks, 0, 1, v => S.LensStreaks = v);
			Widgets.EndDependent();
			Widgets.Switch("Depth of field", S.DepthOfField, v => S.DepthOfField = v);
			Widgets.BeginDependent(S.DepthOfField);
			Other(Widgets.Slider("Aperture", S.DofFStop, 1.4f, 16, v => S.DofFStop = v, "0.0", " (f-number)"));
			Widgets.Info("A real lens focused on the car nearest the camera: a low f-number blurs the background more (f/1.4 to f/2.8 a portrait look, f/8 and up nearly everything sharp).");
			Widgets.EndDependent();
			Other(Widgets.Slider("Motion blur", S.MotionBlur, 0, 1, v => S.MotionBlur = v, "0.00", " frame"));
			Widgets.Info("How long the shutter stays open, as a share of a frame: 0.5 is a film camera's 180° shutter, 0 off. The chased car stays sharp, the road streams past.");
			Widgets.EndCard();
		}

		private void DrawRayTracedAO() {
			Widgets.BeginCard("Ray traced ambient light", "Light that does not come straight from the sun: darkened where the surroundings block the sky " +
			                                              "(under cars, roofs and trees, in corners and gaps) and tinted by what it bounces off. " +
			                                              "The sun light is left to the shadows. DX12 only.");
			Other(Widgets.Slider("GI distance", S.RTGIDistance, 2, 60, v => S.RTGIDistance = v, "0", " m"));
			Other(Widgets.Slider("Bounce intensity", S.RTGIIntensity, 0, 3, v => S.RTGIIntensity = v, "0.00"));
			Other(Widgets.Slider("Sky light in the shade", S.SkyLightStrength, 0, 3, v => S.SkyLightStrength = v, "0.00"));
			Widgets.Info("How bright the sky makes what the sun does not reach (with the sky light from the own engine, Sky & fog). 1: as the sky is.");
			Other(Widgets.Slider("Sky light colour", S.SkyLightSaturation, 0, 1, v => S.SkyLightSaturation = v, "0.00"));
			Widgets.Info("How blue the sky's light is in the shade: 1 as the sky model gives it, 0.5 as measured under a clear sky.");
			Widgets.Info("How strongly the light bounced in (and its colour) is added; 1 is physically right.");
			Other(Widgets.Slider("Strength", S.RTAOStrength, 0, 1, v => S.RTAOStrength = v));
			Other(Widgets.Slider("Distance", S.RTAODistance, 0.5f, 20, v => S.RTAODistance = v, "0.0", " m"));
			Widgets.Info("How far the light is traced: short for contact shadows in gaps, long for the shade under roofs and trees and bounce from further away.");
			Other(Widgets.Slider("Sky occlusion", S.RTSkyOcclusion, 0, 100, v => S.RTSkyOcclusion = v, "0", " m"));
			Widgets.Info("Roofs and overhangs further away than the distance still take the sky (the crowd under a stand's roof). 0: off.");
			Other(Widgets.Slider("Contact shadows", S.RTContactShadows, 0, 1, v => S.RTContactShadows = v));
			Widgets.Info("Darkens the creases where surfaces meet: barriers on the road, seat rows, wheel arches.");
			Widgets.Info($"Now: {mod_.RT.ShadowStatus}");
			Widgets.EndCard();
		}

		private void DrawLightShafts() {
			Widgets.BeginCard("Light shafts", "Sunlight scattered by the air, shadowed by the trees and buildings: shafts through the canopy " +
			                                  "and gaps. Strongest looking towards the sun. DX12 only.");
			Other(Widgets.Switch("Enable", S.RTShafts, v => S.RTShafts = v));
			Widgets.BeginDependent(S.RTShafts);
			// shown per 10 km of air: 1 is a clear day (visibility about 40 km), 10 a hazy one, 40+ mist
			Other(Widgets.Slider("Haze", S.RTShaftDensity * 10000.0f, 0.2f, 60, v => S.RTShaftDensity = v / 10000.0f, "0.0"));
			Other(Widgets.Slider("Forward scattering", S.RTShaftAnisotropy, 0, 0.95f, v => S.RTShaftAnisotropy = v));
			Widgets.Info("Haze: how much the air scatters (clear day 1, hazy 10, mist 40+). Forward scattering: how much brighter " +
			             "the shafts get looking towards the sun.");
			Widgets.EndDependent();
			Widgets.EndCard();
		}

		// Shader rebuild: replacement switches, what was left and why, inventory
		private void DrawShaderRebuild() {
			Widgets.BeginCard("Shader rebuild", "NatureFX's own shaders in place of the game's. Replaced materials also enter the ray tracing " +
			                                    "with their real colour and smoothness.");
			Widgets.Switch("Replace game shaders", S.ReplaceShaders, v => {
				S.ReplaceShaders = v;
				mod_.SetShaderReplacement(v);
				mod_.MarkDirty();
			});
			Widgets.BeginDependent(S.ReplaceShaders);
			Widgets.Switch("Every material (full replacement)", S.FullReplacement, v => {
				S.FullReplacement = v;
				mod_.SetShaderReplacement(false);
				mod_.SetShaderReplacement(true);
				mod_.MarkDirty();
			}, "Off: only the materials the replacement rules name (foliage, roads).");
			Widgets.Switch("Foliage too (wind, snow)", S.RebuildFoliage, v => {
				S.RebuildFoliage = v;
				mod_.SetShaderReplacement(false);
				mod_.SetShaderReplacement(true);
				mod_.MarkDirty();
			}, "Off: trees, bushes and grass keep the game's shaders (no wind or snow on them).");
			Other(Widgets.Slider("Anti-tiling", S.AntiTiling, 0, 1, v => S.AntiTiling = v));
			Widgets.Info("Hex tiles against a texture's visible repeat and seams, on the materials the map's profile asks for (antitiling=). " +
			             "Three texture reads instead of one there.");
			Widgets.EndDependent();
			Other(Widgets.Switch("Wet look on unreplaced HDRP/Lit", S.WetGameMaterials, v => S.WetGameMaterials = v,
				"Materials left with the game's shaders still get wet in the rain."));
			Other(Widgets.Switch("Real albedo for the map's materials", S.CalibrateAlbedo, v => S.CalibrateAlbedo = v,
				"The materials the map's profile names (asphalt, concrete, stone, wood, soil) brought on average to the brightness the real material has; the texture's own detail kept. albedo= in [materials] sets a material's own."));
			Widgets.Info($"Albedo: {mod_.MaterialLooks.AlbedoStatus}");
			var replacer = mod_.Replacer;
			Widgets.Info($"Replaced: {replacer.ReplacedCount} materials");
			foreach (var kv in replacer.Deferred) {
				Widgets.Info($"Left as they are: {kv.Value} - {kv.Key}");
			}
			if (Widgets.Button("Write material inventory", 200)) {
				inventoryPath_ = NatureFX.Tools.MaterialInventory.Write(replacer) ?? "failed, see the log";
			}
			Widgets.Info(inventoryPath_ ?? $"Written on every map load to {NatureFX.Tools.MaterialInventory.Folder}");
			Widgets.Info($"Shader variants the replaced materials need: {replacer.UsedVariants.Count}, added to NatureFX_variants.txt " +
			             "on every map load (the next shader bundle build compiles them).");
			Widgets.EndCard();
		}

		private string inventoryPath_;

		// Developer: what the traced light looks like on its own, clearing what built up, finding a surface's material
		// Ray tracing: each stage's cost
		private void DrawRayTracingCost() {
			Widgets.BeginCard("Ray tracing cost", "Switches each ray tracing stage off for a few seconds and compares the frame time. " +
			                                      "Keep the camera still; a frame rate cap or vsync hides the difference.");
			var bench = mod_.RT.Benchmark;
			if (Widgets.Button(bench.Running ? "Cancel" : "Measure", 120)) {
				if (bench.Running) {
					bench.Cancel();
				} else {
					bench.Start();
				}
			}
			if (bench.Result.Length > 0) {
				Widgets.Info(bench.Result);
			}
			string instances = mod_.RT.RayTracingInstances > 0 ? $"{mod_.RT.RayTracingInstances} instances in the scene" : "scene not built";
			Widgets.Info(instances);
			Widgets.EndCard();
		}

		// One choice for every ray traced effect: surfaces, shadows, ambient light, lamps, light shafts and reflections
		private void DrawRayTracingBackend() {
			Widgets.BeginCard("Ray tracing engine", "NatureFX's own engine (NFXNative.dll) traces every ray traced effect (surfaces, shadows, ambient light, lamps, light shafts, reflections) with DXR 1.1's inline ray queries. DX12 and a DXR 1.1 GPU (RTX 20, RX 6000, Arc or newer) needed.");
			if (!NativeEngine.Ready) {
				Widgets.Info($"<color=#F5B14C>The own engine is not available: {NativeEngine.Info}. No ray traced effects.</color>");
			}
			Widgets.Info($"GPU memory in use: {NativeEngine.GpuMemory}");
			int scale = Mathf.Clamp(S.RTTraceScale, 0, 1);
			if (Widgets.Chips(ref scale, new[] { "Half resolution", "Quarter (upscaled)" })) {
				S.RTTraceScale = scale;
				Other(true);
			}
			Widgets.Info(scale == 0 ? "Ambient light, GI and lamp shadows at half resolution."
				: "Ambient light, GI and lamp shadows at quarter resolution, a different pixel of each 2x2 block every frame, built up over frames and upscaled by depth: about a quarter of the rays.");
			int denoiser = Mathf.Clamp(S.RTDenoiser, 0, 2);
			if (Widgets.Chips(ref denoiser, new[] { "Simple blur", "Variance guided", "NVIDIA NRD" })) {
				S.RTDenoiser = denoiser;
				Other(true);
			}
			Widgets.Info(denoiser == 0 ? "A fixed blur along surfaces."
				: denoiser == 1 ? "Blurs where the rays disagree and keeps detail where they agree (sharper contact shadows, less smear)."
				: "NVIDIA Real-Time Denoisers: SIGMA for the sun shadows, REBLUR for the ambient light and GI (at quarter resolution " +
				  $"without the frame to frame offset), the lamp shadows and the reflections. Now: {NativeEngine.NrdStatus}");
			Widgets.Info("Timings, rays, lamps, the scene and GPU memory: the debug page (Developer > Debug page, in a browser).");
			Other(Widgets.Switch("Sun shadows on DXR 1.2 (test)", S.RTDxr12Sun, v => S.RTDxr12Sun = v,
				"Stage 1 of the move to DXR 1.2: the sun shadows are traced on a Shader Model 6.9 device next to the game's. Same look; compare the frame rate."));
			Widgets.Info($"DXR 1.2 sun shadows: {NativeEngine.Dxr12SunStatus}");
			if (Widgets.Button("Test DXR 1.2", 200)) {
				NativeEngine.RunDxr12Probe();
			}
			Widgets.Info($"DXR 1.2 (shader execution reordering, opacity micromaps; needs Agility SDK 1.619 next to the game's): {NativeEngine.Dxr12Result}");
			Widgets.EndCard();
			DrawRayTracingCost();
		}


		private void DrawSettings() {
			Widgets.BeginCard("Interface");
			Widgets.Slider("Background opacity", S.UiOpacity > 0.0f ? S.UiOpacity : DefaultOpacity, 0.4f, 1.0f, v => S.UiOpacity = v, "0%");
			// applied when the slider is released, so the window does not resize under the mouse while dragging
			Widgets.Slider("UI scale", uiScaleDraft_ > 0.0f ? uiScaleDraft_ : S.UiScale > 0.0f ? S.UiScale : DefaultScale, 0.6f, 1.3f, v => uiScaleDraft_ = v, "0%");
			if (uiScaleDraft_ > 0.0f && GUIUtility.hotControl == 0) {
				S.UiScale = uiScaleDraft_;
				uiScaleDraft_ = 0.0f;
			}
			Widgets.Switch("Larger text", S.UiLargeText, v => S.UiLargeText = v);
			if (Widgets.Button("Reset size and position", 200)) {
				S.UiWidth = S.UiHeight = 0.0f;
				position_ = new Vector2(60, 60);
			}
			Widgets.EndCard();

			DrawGraphicsApi();
		}

		// tools for working on NatureFX itself: reload, status, diagnostics, variant recording
		private void DrawDeveloper() {
			Widgets.BeginCard("Debug page", "Everything for development is on the debug page now: video memory, GPU and CPU load, the scene " +
				"and the log, and the developer's switches and tools (ray tracing views, NRD parts, DLSS tests, shader reload, " +
				"texture budget, shader dumps). Served on this PC only (127.0.0.1): open the address below in a browser (a second screen).");
			Other(Widgets.Switch("Debug page", S.DebugPage, v => S.DebugPage = v));
			Widgets.Info($"Now: {NatureFX.Tools.DebugPage.Status}" + (S.DebugPagePort != 8765 ? $" (port {S.DebugPagePort} in the settings file)" : ""));
			Widgets.EndCard();
		}

		private static readonly string[] SeasonNames = { "Auto", "Spring", "Summer", "Autumn", "Winter", "None" };

		// The Map tab: what the loaded map asks for and what NatureFX is doing with it right now, card by card, so a map
		// author sees at a glance what loaded, what shows and why something does not
		private void DrawMapProfile() {
			var map = mod_.Map;
			var cam = Camera.main;
			var camPos = cam != null ? cam.transform.position : Vector3.zero;
			var scenes = MapProfile.LoadedSceneNames();

			// --- the profile itself
			Widgets.BeginCard("Map profile", "Per-map settings from the map's nfx_profile object, or kino/config/NatureFX/maps/<scene>.nfx (that file wins). docs/map-authoring.md");
			if (Widgets.Switch("Use map profiles", S.UseMapProfile, v => S.UseMapProfile = v)) {
				mod_.ReloadMap();
			}
			Widgets.Switch("Show markers", S.ShowMarkers, v => S.ShowMarkers = v,
				"Draws every marker over the picture: zones, fog, particle boxes, puddles, decals, probes and lights, with what each is doing.");
			if (map.Loaded) {
				Widgets.Info($"Map:  {map.Name}");
				Widgets.Info($"From:  {map.Source}");
				if (!map.FromMap && map.InMapText != null) {
					Widgets.Info("The map has its own profile too; this file overrides it.");
				}
			} else {
				Widgets.Info($"No profile for the loaded scenes ({string.Join(", ", scenes)}). Put an nfx_profile in the map, or a file named after one of these scenes.");
			}
			GUILayout.BeginHorizontal();
			if (Widgets.Button("Reload map profile", 170)) {
				mod_.ReloadMap();
			}
			if (Widgets.Button("Copy camera position", 190)) {
				if (cam != null) {
					GUIUtility.systemCopyBuffer = FormattableString.Invariant($"{camPos.x:0.0}, {camPos.y:0.0}, {camPos.z:0.0}");
					mapMessage_ = $"Copied {GUIUtility.systemCopyBuffer}";
				}
			}
			if (map.FromMap && Widgets.Button("Export to file", 140)) {
				// the outside file then overrides the map's profile and can be edited and reloaded
				mapMessage_ = map.ExportInMapProfile();
				mod_.ReloadMap();
			}
			if (!map.Loaded && scenes.Count > 0 && Widgets.Button("Create template", 160)) {
				mapMessage_ = MapProfile.WriteTemplate(scenes[0]);
				mod_.ReloadMap();
			}
			GUILayout.EndHorizontal();
			if (mapMessage_ != null) {
				Widgets.Info(mapMessage_);
			}
			Widgets.EndCard();

			if (!S.UseMapProfile) {
				return;
			}

			// --- what was read
			Widgets.BeginCard("Loaded", "What the profile and the map's markers gave NatureFX.");
			var looks = mod_.MaterialLooks;
			Widgets.Info($"Rules {map.Section("rules").Count}  ·  materials {looks.MaterialCount}  ·  emissive {looks.EmissiveCount}  ·  foliage {looks.FoliageCount}  ·  terrain {looks.TerrainCount}");
			var kinds = new System.Collections.Generic.SortedDictionary<string, int>();
			foreach (var m in mod_.Markers.Markers) {
				kinds.TryGetValue(m.Kind, out int n);
				kinds[m.Kind] = n + 1;
			}
			var parts = new System.Collections.Generic.List<string>();
			foreach (var kv in kinds) {
				parts.Add($"{kv.Key} {kv.Value}");
			}
			Widgets.Info(parts.Count > 0 ? $"Markers:  {string.Join("  ·  ", parts)}" : "Markers:  none in this map");
			Widgets.EndCard();

			// --- weather
			Widgets.BeginCard("Weather and season", "The map's starting weather ([defaults]) and its weather zones, at the camera. A slider you moved since the map loaded is yours (\"yours\").");
			Widgets.Info($"Map values now:  {mod_.MapWeather.Describe()}");
			Widgets.Info(mod_.Zones.Count > 0 ? $"Zones ({mod_.Zones.Count}):  {mod_.Zones.Describe()}" : "Zones:  none");
			Widgets.Info($"Season:  {Seasons.Describe()}  (Weather > Season)");
			Widgets.Info($"Sky:  {mod_.Clouds.State}  ·  night: {mod_.Night.State}");
			Widgets.Info($"Sky type:  {mod_.Volume.SkyState}");
			Widgets.EndCard();

			// --- light
			Widgets.BeginCard("Lights and reflections", "Light markers switch on at dusk or night (Sun > time of day to check).");
			Widgets.Info(mod_.Lights.Count > 0 ? $"Lights:  {mod_.Lights.Describe()}" : "Lights:  no light markers");
			Widgets.Switch("Map reflection probes", S.MapProbes, v => S.MapProbes = v, "Reflection probes at the map's markers and along the road (redrawn when the time or the weather changes).");
			Widgets.Info(mod_.Probes.Count > 0 ? $"Reflection probes:  {mod_.Probes.Describe()}" : "Reflection probes:  none");
			Widgets.EndCard();

			// --- plants
			Widgets.BeginCard("Grass and forest", "3D grass needs Replace shaders on (Shader rebuild).");
			Widgets.Slider("3D grass density", S.GrassDensity, 0, 2, v => S.GrassDensity = v);
			Widgets.Slider("3D grass distance", S.GrassDistance, 10, 120, v => S.GrassDistance = v, "0", " m");
			Widgets.Info(mod_.Grass.AreaCount > 0 ? $"3D grass:  {mod_.Grass.Describe()}" : "3D grass:  no grass markers");
			Widgets.Info(mod_.Forest.AreaCount > 0 ? $"Forest:  {mod_.Forest.Describe()}" : "Forest:  no forest markers");
			Widgets.Slider("Thicket density", S.ShrubDensity, 0, 2, v => S.ShrubDensity = v);
			Widgets.Slider("Scanned shrubs up to", S.ShrubDistance, 5, 80, v => S.ShrubDistance = v, "0", " m");
			Widgets.Info(mod_.Shrubs.AreaCount > 0 ? $"Thickets:  {mod_.Shrubs.Describe()}" : "Thickets:  no shrub markers");
			Widgets.EndCard();

			// --- the ground map's details
			Widgets.BeginCard("Ground detail", "Objects take the ground's colour where they stand in it, the profile's painted dirt ([paint]) and debris along the road's edges. Needs the own engine.");
			Widgets.Switch("Ground detail", S.MapGroundDetail, v => S.MapGroundDetail = v);
			Widgets.Slider("Debris (pebbles, twigs, leaves)", S.Detritus, 0, 3, v => S.Detritus = v);
			Widgets.Info($"Ground map:  {NatureFX.Rendering.GroundMap.Status}");
			Widgets.Info($"Painted dirt:  {mod_.Paint.Status}");
			Widgets.Info($"Debris:  {mod_.Debris.Status}");
			Widgets.EndCard();

			// --- effects, each with what it waits for
			Widgets.BeginCard("Effects", "Decals, puddles and water, local fog and floating particles. Each shows what it is doing or what it waits for.");
			Widgets.Switch("Map effects", S.MapEffects, v => S.MapEffects = v);
			Widgets.Info($"Decals {mod_.Decals.Count}  ·  puddles {mod_.Water.PuddleCount} (water {mod_.Water.Level:P0})  ·  water surfaces {mod_.Water.SurfaceCount}");
			Widgets.Info($"Volumetric fog:  {mod_.CameraFog.State}");
			Widgets.Info($"Volumetric clouds:  {mod_.CameraClouds.State}");
			var fog = mod_.Volumes.Status(camPos);
			if (fog.Count > 0) {
				Widgets.Info("Local fog:");
				foreach (string line in fog) {
					Widgets.Info("   " + line);
				}
			}
			var fx = mod_.Particles.Status(camPos);
			if (fx.Count > 0) {
				Widgets.Info("Particles:");
				foreach (string line in fx) {
					Widgets.Info("   " + line);
				}
			}
			Widgets.EndCard();
		}

		// ---------------------------------------------------------------- helpers

		// how long a whole day takes at a time speed, in real time
		private static string DayLength(float speed) {
			float minutes = 1440.0f / Mathf.Max(speed, 1.0f);
			return minutes >= 60.0f ? $"{minutes / 60.0f:0.#} h" : $"{minutes:0.#} min";
		}

		private static string Clock(float hours) {
			int h = Mathf.FloorToInt(hours) % 24;
			int m = Mathf.FloorToInt((hours - Mathf.Floor(hours)) * 60.0f);
			return $"{h:00}:{m:00}";
		}

		// A non-preset value changed.
		private void Other(bool changed) {
			if (changed) {
				mod_.MarkDirty();
			}
		}
	}
}

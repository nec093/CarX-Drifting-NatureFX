using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace NatureFX.Ui {
	// Immediate mode controls in the NatureFX look (compact ReShade-like rows: label column on the left, control
	// on the right). Each returns true when the user changed the value.
	//
	// Sections (BeginCard / EndCard) collapse, and Filter hides rows whose label does not contain it. Hidden content
	// is still laid out, inside a zero-height clipped area with GUI.enabled off, so callers keep their plain
	// immediate-mode code and the IMGUI layout stays identical between the Layout and Repaint events.
	//
	// Rows: hover highlight, the whole row toggles a checkbox, right-click resets to Settings.Defaults (for the
	// property overloads), Ctrl+click on a slider types a value, Left/Right nudge the slider under the mouse.
	internal static class Widgets {
		public const float IndentWidth = 12;
		public static float RowHeight => Theme.LargeText ? 26 : 24;

		// search text from the window header; empty = show everything
		public static string Filter = "";

		private static readonly Dictionary<string, bool> collapsed_ = new();
		private static Dictionary<string, bool> sectionMatched_ = new();
		private static Dictionary<string, bool> sectionMatchedNext_ = new();
		// the ISettings property behind a row's setter, and whether the setter stores the value as it is (a row shown
		// in other units, like the haze per 10 km, converts: its default goes to the property directly)
		private static readonly Dictionary<string, (PropertyInfo Property, bool Direct)> resetTargets_ = new();
		private static readonly Stack<(int Indent, bool Enabled, Color Color)> scopes_ = new();
		private static string section_;
		private static bool sectionTitleMatches_;
		private static bool sectionHidden_;
		private static bool savedEnabled_;
		private static int indent_;
		private static int editId_;
		private static string editText_ = "";

		private static bool Filtering => !string.IsNullOrEmpty(Filter);

		// Call once per frame before drawing (on the Layout event): which sections had matching rows last frame
		public static void BeginFrame() {
			(sectionMatched_, sectionMatchedNext_) = (sectionMatchedNext_, sectionMatched_);
			sectionMatchedNext_.Clear();
			indent_ = 0;
			scopes_.Clear();
		}

		// ---------------------------------------------------------------- sections

		public static void Heading(string text) {
			if (Filtering) {
				return;
			}
			GUILayout.Space(6);
			GUILayout.Label(text, Theme.SidebarGroup);
		}

		public static void BeginCard(string title, string hint = null, bool collapsedByDefault = false) {
			section_ = title;
			sectionTitleMatches_ = Filtering && Matches(title);
			bool open = collapsed_.TryGetValue(title, out bool c) ? !c : !collapsedByDefault;
			bool anyRowMatched = sectionMatched_.TryGetValue(title, out bool m) && m;
			bool visible = !Filtering || sectionTitleMatches_ || anyRowMatched;

			if (visible) {
				GUILayout.Space(4);
				var rect = GUILayoutUtility.GetRect(new GUIContent(title), Theme.Section, GUILayout.ExpandWidth(true));
				bool hover = rect.Contains(Event.current.mousePosition);
				if (Event.current.type == EventType.Repaint) {
					(hover ? Theme.SectionHover : Theme.Section).Draw(rect, false, false, false, false);
				}
				Theme.DrawIcon(new Rect(rect.x + 7, rect.y + (rect.height - 10) * 0.5f, 10, 10), Filtering || open ? Theme.ArrowDownTex : Theme.ArrowRightTex, Theme.Muted);
				GUI.Label(new Rect(rect.x + 22, rect.y, rect.width - 30, rect.height), new GUIContent(title, hint), Theme.SectionTitle);
				if (!Filtering && GUI.Button(rect, GUIContent.none, GUIStyle.none)) {
					collapsed_[title] = open;
					open = !open;
				}
				GUILayout.Space(3);
			}

			sectionHidden_ = !visible || (!Filtering && !open);
			GUILayout.BeginVertical();
			if (sectionHidden_) {
				savedEnabled_ = GUI.enabled;
				GUI.enabled = false;
				GUILayout.BeginScrollView(Vector2.zero, false, false, GUIStyle.none, GUIStyle.none, GUIStyle.none, GUILayout.Height(0));
			} else {
				GUILayout.BeginVertical(new GUIStyle { padding = new RectOffset(12, 4, 0, 0) });
			}
		}

		public static void EndCard() {
			if (sectionHidden_) {
				GUILayout.EndScrollView();
				GUI.enabled = savedEnabled_;
			} else {
				GUILayout.EndVertical();
				GUILayout.Space(6);
			}
			GUILayout.EndVertical();
			section_ = null;
			sectionHidden_ = false;
		}

		// Rows that only matter while a parent setting is on: indented, and dimmed and locked when it is off.
		public static void BeginDependent(bool parentOn) {
			scopes_.Push((indent_, GUI.enabled, GUI.color));
			++indent_;
			if (!parentOn) {
				GUI.enabled = false;
				GUI.color = new Color(GUI.color.r, GUI.color.g, GUI.color.b, GUI.color.a * 0.45f);
			}
		}

		public static void EndDependent() {
			if (scopes_.Count == 0) {
				return;
			}
			var (indent, enabled, color) = scopes_.Pop();
			indent_ = indent;
			GUI.enabled = enabled;
			GUI.color = color;
		}

		// Whether a row with this label is shown under the current filter; records matches for the section.
		private static bool Visible(string label) {
			if (!Filtering || sectionTitleMatches_) {
				return true;
			}
			bool match = Matches(label);
			if (match && section_ != null) {
				sectionMatchedNext_[section_] = true;
			}
			return match;
		}

		private static bool Matches(string text) {
			return !string.IsNullOrEmpty(text) && text.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// One row: label column (42% of the row) and control area. Returns the whole row and the control rect.
		private static Rect Row(string label, string hint, out Rect control) {
			var row = GUILayoutUtility.GetRect(10, RowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(RowHeight));
			float indent = indent_ * IndentWidth;
			float labelWidth = Mathf.Max(110, row.width * 0.42f);
			control = new Rect(row.x + labelWidth, row.y + 2, row.width - labelWidth - 6, row.height - 4);

			if (Event.current.type == EventType.Repaint && GUI.enabled && row.Contains(Event.current.mousePosition)) {
				Theme.Fill(row, Theme.RowHoverColor);
			}
			GUI.Label(new Rect(row.x + 6 + indent, row.y, labelWidth - 10 - indent, row.height), new GUIContent(label, hint), Theme.Label);
			return row;
		}

		private static bool RightClicked(Rect row) {
			var e = Event.current;
			if (e.type == EventType.MouseDown && e.button == 1 && GUI.enabled && row.Contains(e.mousePosition)) {
				e.Use();
				return true;
			}
			return false;
		}

		// ---------------------------------------------------------------- controls

		// Checkbox. With an empty label only the box is drawn, for inline use; otherwise the whole row toggles it.
		public static bool Switch(string label, ref bool value, string hint = null) {
			return SwitchCore(label, ref value, hint, out _);
		}

		private static bool SwitchCore(string label, ref bool value, string hint, out bool reset) {
			reset = false;
			if (!Visible(label)) {
				return false;
			}
			Rect hit, box;
			if (string.IsNullOrEmpty(label)) {
				var r = GUILayoutUtility.GetRect(18, RowHeight, GUILayout.Width(18), GUILayout.Height(RowHeight));
				box = new Rect(r.x, r.y + (r.height - 16) * 0.5f, 16, 16);
				hit = new Rect(box.x - 2, box.y - 2, box.width + 4, box.height + 4);
			} else {
				hit = Row(label, hint, out var control);
				box = new Rect(control.x, control.y + (control.height - 16) * 0.5f, 16, 16);
				reset = RightClicked(hit);
			}

			bool hover = hit.Contains(Event.current.mousePosition) && GUI.enabled;
			if (Event.current.type == EventType.Repaint) {
				Theme.Fill(box, value ? Theme.Accent : hover ? Theme.FrameHoverColor : Theme.FrameColor);
			}
			if (value) {
				Theme.DrawIcon(new Rect(box.x + 1, box.y + 1, box.width - 2, box.height - 2), Theme.CheckTex, Theme.Hex(0x0C0F13));
			}
			if (GUI.Button(hit, new GUIContent("", hint), GUIStyle.none)) {
				value = !value;
				return true;
			}
			return false;
		}

		// ImGui style slider: the bar fills to the value and shows it in the middle. Drag anywhere on the bar,
		// Ctrl+click to type a value, Left/Right (Shift for bigger steps) while the mouse is over it.
		public static bool Slider(string label, ref float value, float min, float max, string format = "0.00", string suffix = "") {
			return SliderCore(label, ref value, min, max, format, suffix, out _);
		}

		private static bool SliderCore(string label, ref float value, float min, float max, string format, string suffix, out bool reset) {
			reset = false;
			if (!Visible(label)) {
				return false;
			}
			var row = Row(label, null, out var frame);
			int id = GUIUtility.GetControlID(FocusType.Passive, frame);
			var e = Event.current;

			if (editId_ == id) {
				return EditValue(id, frame, ref value, min, max);
			}
			reset = RightClicked(row);

			bool changed = false;
			switch (e.GetTypeForControl(id)) {
				case EventType.MouseDown:
					if (e.button == 0 && frame.Contains(e.mousePosition) && GUI.enabled) {
						if (e.control) {
							editId_ = id;
							editText_ = value.ToString(format, CultureInfo.InvariantCulture);
						} else {
							GUIUtility.hotControl = id;
							changed = SetFromMouse(ref value, min, max, frame, e.mousePosition.x);
						}
						e.Use();
					}
					break;
				case EventType.MouseDrag:
					if (GUIUtility.hotControl == id) {
						changed = SetFromMouse(ref value, min, max, frame, e.mousePosition.x);
						e.Use();
					}
					break;
				case EventType.MouseUp:
					if (GUIUtility.hotControl == id) {
						GUIUtility.hotControl = 0;
						e.Use();
					}
					break;
				case EventType.KeyDown:
					if (GUI.enabled && GUIUtility.keyboardControl == 0 && frame.Contains(e.mousePosition) && (e.keyCode == KeyCode.LeftArrow || e.keyCode == KeyCode.RightArrow)) {
						float step = (max - min) * (e.shift ? 0.1f : 0.01f) * (e.keyCode == KeyCode.LeftArrow ? -1 : 1);
						float next = Mathf.Clamp(value + step, min, max);
						changed = !Mathf.Approximately(next, value);
						value = next;
						e.Use();
					}
					break;
				case EventType.Repaint:
					bool active = GUIUtility.hotControl == id;
					bool hover = frame.Contains(e.mousePosition) && GUI.enabled;
					Theme.Fill(frame, active ? Theme.FrameActiveColor : hover ? Theme.FrameHoverColor : Theme.FrameColor);
					float t = Mathf.InverseLerp(min, max, value);
					if (t > 0.0f) {
						Theme.Fill(new Rect(frame.x, frame.y, Mathf.Max(4, frame.width * t), frame.height), Theme.FillColor);
					}
					Theme.ValueOnFrame.Draw(frame, value.ToString(format) + suffix, false, false, false, false);
					break;
			}
			return changed;
		}

		// Typed value: Enter applies, Esc or a click elsewhere cancels.
		private static bool EditValue(int id, Rect frame, ref float value, float min, float max) {
			var e = Event.current;
			bool commit = false, cancel = false;
			if (e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)) {
				commit = true;
				e.Use();
			} else if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape) {
				cancel = true;
				e.Use();
			} else if (e.type == EventType.MouseDown && !frame.Contains(e.mousePosition)) {
				cancel = true;
			}

			string control = "nfx_edit_" + id;
			GUI.SetNextControlName(control);
			editText_ = GUI.TextField(frame, editText_, Theme.TextField);
			if (GUI.GetNameOfFocusedControl() != control && !commit && !cancel) {
				GUI.FocusControl(control);
			}

			if (!commit && !cancel) {
				return false;
			}
			editId_ = 0;
			GUI.FocusControl(null);
			if (cancel || !float.TryParse(editText_.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float typed)) {
				return false;
			}
			typed = Mathf.Clamp(typed, min, max);
			bool changed = !Mathf.Approximately(typed, value);
			value = typed;
			return changed;
		}

		private static bool SetFromMouse(ref float value, float min, float max, Rect frame, float x) {
			float next = Mathf.Lerp(min, max, Mathf.InverseLerp(frame.x, frame.xMax, x));
			if (Mathf.Approximately(next, value)) {
				return false;
			}
			value = next;
			return true;
		}

		public static bool IntSlider(string label, ref int value, int min, int max) {
			return IntSliderCore(label, ref value, min, max, out _);
		}

		private static bool IntSliderCore(string label, ref int value, int min, int max, out bool reset) {
			float f = value;
			if (!SliderCore(label, ref f, min, max, "0", "", out reset)) {
				return false;
			}
			int next = Mathf.RoundToInt(f);
			if (next == value) {
				return false;
			}
			value = next;
			return true;
		}

		// Segmented control, indented like a row; returns true when the selection changed.
		public static bool Chips(ref int selected, string[] names) {
			bool changed = false;
			GUILayout.BeginHorizontal();
			GUILayout.Space(6 + indent_ * IndentWidth);
			for (int i = 0; i < names.Length; ++i) {
				if (GUILayout.Button(names[i], i == selected ? Theme.SegmentActive : Theme.Segment) && i != selected) {
					selected = i;
					changed = true;
				}
			}
			GUILayout.FlexibleSpace();
			GUILayout.EndHorizontal();
			GUILayout.Space(3);
			return changed;
		}

		public static bool Button(string text, float width = 0) {
			return width > 0 ? GUILayout.Button(text, Theme.Button, GUILayout.Width(width)) : GUILayout.Button(text, Theme.Button);
		}

		public static string TextField(string value, int maxLength = 48, float width = 0) {
			return width > 0
				? GUILayout.TextField(value ?? "", maxLength, Theme.TextField, GUILayout.Width(width))
				: GUILayout.TextField(value ?? "", maxLength, Theme.TextField);
		}

		public static void Info(string text) {
			if (Filtering && !sectionTitleMatches_) {
				return; // explanatory text only makes sense next to its rows
			}
			GUILayout.BeginHorizontal();
			GUILayout.Space(6 + indent_ * IndentWidth);
			GUILayout.Label(text, Theme.Hint);
			GUILayout.EndHorizontal();
		}

		// Tooltip of the control under the mouse (labels and sections carry their hint as tooltip)
		public static void DrawTooltip(Vector2 windowSize) {
			string tip = GUI.tooltip;
			if (string.IsNullOrEmpty(tip) || Event.current.type != EventType.Repaint) {
				return;
			}
			var content = new GUIContent(tip);
			float width = Mathf.Min(320, Theme.Tooltip.CalcSize(content).x + 4);
			float height = Theme.Tooltip.CalcHeight(content, width);
			var mouse = Event.current.mousePosition;
			var rect = new Rect(mouse.x + 14, mouse.y + 18, width, height);
			if (rect.xMax > windowSize.x - 4) {
				rect.x = windowSize.x - 4 - width;
			}
			if (rect.yMax > windowSize.y - 4) {
				rect.y = mouse.y - height - 6;
			}
			var old = GUI.color;
			GUI.color = Color.white;
			Theme.Tooltip.Draw(rect, content, false, false, false, false);
			GUI.color = old;
		}

		// ---------------------------------------------------------------- property helpers
		// ISettings exposes properties, which cannot be passed by ref. These also support right-click reset.

		public static bool Switch(string label, bool value, Action<bool> set, string hint = null) {
			bool changed = SwitchCore(label, ref value, hint, out bool reset);
			if (reset) {
				return ResetToDefault(label, set, !value);
			}
			if (changed) {
				set(value);
			}
			return changed;
		}

		public static bool Slider(string label, float value, float min, float max, Action<float> set, string format = "0.00", string suffix = "") {
			bool changed = SliderCore(label, ref value, min, max, format, suffix, out bool reset);
			if (reset) {
				return ResetToDefault(label, set, value + 1.234567f);
			}
			if (changed) {
				set(value);
			}
			return changed;
		}

		public static bool IntSlider(string label, int value, int min, int max, Action<int> set) {
			bool changed = IntSliderCore(label, ref value, min, max, out bool reset);
			if (reset) {
				return ResetToDefault(label, set, value + 7);
			}
			if (changed) {
				set(value);
			}
			return changed;
		}

		// The setter is a lambda over one ISettings property. Which one is found once by calling it with a probe
		// value and seeing what changed (then putting everything back); the default comes from Settings.Defaults.
		private static bool ResetToDefault<T>(string label, Action<T> set, T probe) {
			var data = Settings.Data;
			var defaults = Settings.Defaults;
			if (data == null || defaults == null) {
				return false;
			}

			string key = section_ + "/" + label;
			if (!resetTargets_.TryGetValue(key, out var target)) {
				var properties = typeof(ISettings).GetProperties();
				var before = new object[properties.Length];
				for (int i = 0; i < properties.Length; ++i) {
					before[i] = properties[i].GetValue(data);
				}
				set(probe);
				PropertyInfo property = null;
				bool direct = true;
				for (int i = 0; i < properties.Length; ++i) {
					object now = properties[i].GetValue(data);
					if (!Equals(now, before[i])) {
						if (property == null) {
							property = properties[i];
							direct = Equals(now, probe);
						}
						properties[i].SetValue(data, before[i]);
					}
				}
				target = (property, direct);
				resetTargets_[key] = target;
			}
			if (target.Property == null || target.Property.PropertyType != typeof(T)) {
				return false;
			}
			object value = target.Property.GetValue(defaults);
			if (target.Direct) {
				set((T)value);
			} else {
				// the setter converts units: the raw default through it came out wrong (the haze slider's 0.0003 / 10000)
				target.Property.SetValue(data, value);
			}
			return true;
		}
	}
}

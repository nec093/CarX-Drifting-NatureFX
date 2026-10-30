using System;
using UnityEngine;

namespace NatureFX.Ui {
	// Colours, generated textures and GUIStyles for the F8 window, in the spirit of ReShade's overlay: a
	// translucent dark panel the game shows through, thin frames, compact rows and one accent colour.
	// Built lazily on the first OnGUI and rebuilt when the window opacity changes.
	internal static class Theme {
		public static readonly Color Foreground = Hex(0xE6E9EE);
		public static readonly Color Muted = Hex(0xA3ACB9); // readable over bright scenes through the glass
		public static readonly Color Faint = Hex(0x7A8494);
		public static readonly Color Accent = Hex(0x3DDC97);
		public static readonly Color Warning = Hex(0xF5B14C);

		// translucent layers over the panel (white at low alpha reads as a lighter glass)
		public static readonly Color FrameColor = new(1, 1, 1, 0.06f);
		public static readonly Color FrameHoverColor = new(1, 1, 1, 0.10f);
		public static readonly Color FrameActiveColor = new(1, 1, 1, 0.14f);
		public static readonly Color RowHoverColor = new(1, 1, 1, 0.045f);
		public static readonly Color FillColor = new(0.239f, 0.863f, 0.592f, 0.45f);
		private static readonly Color SectionColor = new(1, 1, 1, 0.045f);
		private static readonly Color LineColor = new(1, 1, 1, 0.08f);

		public static GUIStyle Window, Header, Title, Subtitle, Sidebar, SidebarGroup, SidebarItem, SidebarItemActive,
			Section, SectionHover, SectionTitle, Label, Value, ValueOnFrame, Hint, Segment, SegmentActive, Button, Close,
			Status, TextField, Row, Tooltip, Check;

		public static Texture2D WindowTex, FrameTex, FrameHoverTex, FrameActiveTex, FillTex, CheckOnTex, SectionTex,
			SectionHoverTex, ItemActiveTex, RowHoverTex, AccentBarTex, LineTex, TooltipTex, GripTex,
			ArrowDownTex, ArrowRightTex, CheckTex, CloseTex;

		private static float builtOpacity_ = -1;
		private static bool builtLarge_;
		private static Font font_;

		public static float Opacity { get; set; } = 0.9f;
		public static bool LargeText { get; set; }

		public static void Ensure() {
			if (WindowTex != null && Mathf.Approximately(builtOpacity_, Opacity) && builtLarge_ == LargeText) {
				return;
			}
			builtOpacity_ = Opacity;
			builtLarge_ = LargeText;
			font_ ??= Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Yu Gothic UI", "Arial" }, 13);

			WindowTex = Rounded(10, Hex(0x0C0F13, Opacity), new Color(1, 1, 1, 0.10f));
			FrameTex = Rounded(4, FrameColor);
			FrameHoverTex = Rounded(4, FrameHoverColor);
			FrameActiveTex = Rounded(4, FrameActiveColor);
			FillTex = Rounded(4, new Color(Accent.r, Accent.g, Accent.b, 0.45f));
			CheckOnTex = Rounded(4, Accent);
			SectionTex = Rounded(4, SectionColor);
			SectionHoverTex = Rounded(4, FrameHoverColor);
			ItemActiveTex = Rounded(4, new Color(Accent.r, Accent.g, Accent.b, 0.14f));
			RowHoverTex = Rounded(4, new Color(1, 1, 1, 0.045f));
			AccentBarTex = Solid(Accent);
			LineTex = Solid(LineColor);
			TooltipTex = Rounded(6, Hex(0x161B22, 0.97f), new Color(1, 1, 1, 0.12f));
			GripTex = Solid(new Color(1, 1, 1, 0.25f));
			BuildIcons();

			Window = new GUIStyle { normal = { background = WindowTex }, border = new RectOffset(10, 10, 10, 10), padding = new RectOffset(0, 0, 0, 0) };
			Header = new GUIStyle { padding = new RectOffset(14, 10, 8, 6) };
			Title = Text(15, FontStyle.Bold, Foreground);
			Subtitle = Text(11, FontStyle.Normal, Muted);

			Sidebar = new GUIStyle { padding = new RectOffset(8, 6, 6, 8) };
			SidebarGroup = Text(10, FontStyle.Bold, Faint);
			SidebarGroup.padding = new RectOffset(10, 0, 8, 2);
			SidebarItem = Text(12, FontStyle.Normal, Muted);
			SidebarItem.padding = new RectOffset(12, 6, 0, 0);
			SidebarItem.fixedHeight = 26;
			SidebarItem.hover.background = FrameTex;
			SidebarItem.hover.textColor = Foreground;
			SidebarItem.border = new RectOffset(4, 4, 4, 4);
			SidebarItemActive = new GUIStyle(SidebarItem) {
				normal = { background = ItemActiveTex, textColor = Foreground },
				hover = { background = ItemActiveTex, textColor = Foreground }
			};

			Section = new GUIStyle { normal = { background = SectionTex }, border = new RectOffset(4, 4, 4, 4), padding = new RectOffset(8, 8, 0, 0), margin = new RectOffset(0, 4, 0, 2), fixedHeight = 24 };
			SectionHover = new GUIStyle(Section) { normal = { background = SectionHoverTex } };
			SectionTitle = Text(12, FontStyle.Bold, Foreground);
			Label = Text(12, FontStyle.Normal, Foreground);
			Value = Text(12, FontStyle.Normal, Muted);
			Value.alignment = TextAnchor.MiddleRight;
			ValueOnFrame = Text(11, FontStyle.Normal, Foreground);
			ValueOnFrame.alignment = TextAnchor.MiddleCenter;
			Hint = Text(11, FontStyle.Normal, Muted);
			Hint.wordWrap = true;
			Status = Text(11, FontStyle.Normal, Faint);
			Status.wordWrap = true;
			Check = Text(12, FontStyle.Bold, Hex(0x0C0F13));
			Check.alignment = TextAnchor.MiddleCenter;

			Segment = Text(11, FontStyle.Normal, Muted);
			Segment.alignment = TextAnchor.MiddleCenter;
			Segment.normal.background = FrameTex;
			Segment.hover.background = FrameHoverTex;
			Segment.hover.textColor = Foreground;
			Segment.active.background = FrameActiveTex;
			Segment.border = new RectOffset(4, 4, 4, 4);
			Segment.fixedHeight = 22;
			Segment.margin = new RectOffset(0, 2, 1, 1);
			Segment.padding = new RectOffset(8, 8, 0, 0);
			SegmentActive = new GUIStyle(Segment) {
				normal = { background = CheckOnTex, textColor = Hex(0x0C0F13) },
				hover = { background = CheckOnTex, textColor = Hex(0x0C0F13) },
				fontStyle = FontStyle.Bold
			};
			Button = new GUIStyle(Segment) { normal = { background = FrameTex, textColor = Foreground }, margin = new RectOffset(0, 4, 1, 1) };

			Close = Text(14, FontStyle.Normal, Muted);
			Close.alignment = TextAnchor.MiddleCenter;
			Close.hover.textColor = Foreground;
			Close.hover.background = FrameHoverTex;
			Close.border = new RectOffset(4, 4, 4, 4);

			TextField = Text(12, FontStyle.Normal, Foreground);
			TextField.normal.background = FrameTex;
			TextField.hover.background = FrameHoverTex;
			TextField.focused.background = FrameActiveTex;
			TextField.focused.textColor = Foreground;
			TextField.border = new RectOffset(4, 4, 4, 4);
			TextField.padding = new RectOffset(8, 8, 0, 0);
			TextField.fixedHeight = 22;
			TextField.margin = new RectOffset(0, 4, 1, 1);

			Row = new GUIStyle { normal = { background = FrameTex }, border = new RectOffset(4, 4, 4, 4), padding = new RectOffset(8, 4, 1, 1), margin = new RectOffset(0, 0, 0, 2) };
			Tooltip = Text(11, FontStyle.Normal, Foreground);
			Tooltip.wordWrap = true;
			Tooltip.normal.background = TooltipTex;
			Tooltip.border = new RectOffset(6, 6, 6, 6);
			Tooltip.padding = new RectOffset(8, 8, 6, 6);
		}

		private static GUIStyle Text(int size, FontStyle style, Color color) {
			return new GUIStyle {
				font = font_,
				fontSize = size + (LargeText ? 2 : 0),
				fontStyle = style,
				normal = { textColor = color },
				richText = true,
				alignment = TextAnchor.MiddleLeft,
				clipping = TextClipping.Clip
			};
		}

		public static Color Hex(int rgb, float alpha = 1.0f) {
			return new Color(((rgb >> 16) & 0xFF) / 255.0f, ((rgb >> 8) & 0xFF) / 255.0f, (rgb & 0xFF) / 255.0f, alpha);
		}

		private static Texture2D Solid(Color c) {
			var tex = NewTexture(1);
			tex.SetPixel(0, 0, c);
			tex.Apply();
			return tex;
		}

		// Anti-aliased rounded rectangle for 9-slice use (border = radius), optional 1px outline.
		private static Texture2D Rounded(int radius, Color fill, Color? outline = null) {
			int size = radius * 2 + 2;
			var tex = NewTexture(size);
			var pixels = new Color[size * size];
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					float cx = Mathf.Clamp(x + 0.5f, radius, size - radius);
					float cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
					float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(cx, cy));
					float inside = Mathf.Clamp01(radius - d + 0.5f);
					var c = fill;
					if (outline.HasValue) {
						float edge = Mathf.Clamp01(1.0f - Mathf.Abs(radius - 0.5f - d));
						c = Color.Lerp(fill, outline.Value, edge * outline.Value.a);
						c.a = Mathf.Max(fill.a, outline.Value.a * edge);
					}
					c.a *= inside;
					pixels[y * size + x] = c;
				}
			}
			tex.SetPixels(pixels);
			tex.Apply();
			return tex;
		}

		// Icons are drawn as textures: the OS font has no glyphs for arrows, check marks or crosses (they showed
		// as boxes). Shapes are in 0..1 coordinates with y up, supersampled 4x4 for smooth edges, white so
		// GUI.color / DrawTexture can tint them.
		private static Texture2D Icon(int size, Func<Vector2, bool> inside) {
			var tex = NewTexture(size);
			var pixels = new Color[size * size];
			for (int y = 0; y < size; ++y) {
				for (int x = 0; x < size; ++x) {
					int hits = 0;
					for (int sy = 0; sy < 4; ++sy) {
						for (int sx = 0; sx < 4; ++sx) {
							if (inside(new Vector2((x + (sx + 0.5f) / 4.0f) / size, (y + (sy + 0.5f) / 4.0f) / size))) {
								++hits;
							}
						}
					}
					pixels[y * size + x] = new Color(1, 1, 1, hits / 16.0f);
				}
			}
			tex.SetPixels(pixels);
			tex.Apply();
			return tex;
		}

		private static bool NearSegment(Vector2 p, Vector2 a, Vector2 b, float halfWidth) {
			var ab = b - a;
			float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
			return (p - (a + ab * t)).magnitude <= halfWidth;
		}

		private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c) {
			float d1 = (p.x - b.x) * (a.y - b.y) - (a.x - b.x) * (p.y - b.y);
			float d2 = (p.x - c.x) * (b.y - c.y) - (b.x - c.x) * (p.y - c.y);
			float d3 = (p.x - a.x) * (c.y - a.y) - (c.x - a.x) * (p.y - a.y);
			bool neg = d1 < 0 || d2 < 0 || d3 < 0;
			bool pos = d1 > 0 || d2 > 0 || d3 > 0;
			return !(neg && pos);
		}

		private static void BuildIcons() {
			ArrowDownTex = Icon(24, p => InTriangle(p, new Vector2(0.22f, 0.66f), new Vector2(0.78f, 0.66f), new Vector2(0.5f, 0.32f)));
			ArrowRightTex = Icon(24, p => InTriangle(p, new Vector2(0.34f, 0.22f), new Vector2(0.34f, 0.78f), new Vector2(0.68f, 0.5f)));
			CheckTex = Icon(24, p => NearSegment(p, new Vector2(0.2f, 0.52f), new Vector2(0.42f, 0.3f), 0.085f)
			                         || NearSegment(p, new Vector2(0.42f, 0.3f), new Vector2(0.8f, 0.72f), 0.085f));
			CloseTex = Icon(24, p => NearSegment(p, new Vector2(0.28f, 0.28f), new Vector2(0.72f, 0.72f), 0.06f)
			                         || NearSegment(p, new Vector2(0.28f, 0.72f), new Vector2(0.72f, 0.28f), 0.06f));
		}

		private static Texture2D white_;

		// Solid rounded rectangle. GUI.DrawTexture rounds the corners itself (borderRadius), so a 1x1 white
		// texture stays crisp at any size; a small pre-rounded texture stretched over a long bar blurs its ends.
		public static void Fill(Rect rect, Color color, float radius = 4) {
			if (Event.current.type != EventType.Repaint || rect.width <= 0 || rect.height <= 0) {
				return;
			}
			if (white_ == null) {
				white_ = Solid(Color.white);
			}
			var tint = color;
			tint.a *= GUI.color.a;
			GUI.DrawTexture(rect, white_, ScaleMode.StretchToFill, true, 0, tint, 0, Mathf.Min(radius, Mathf.Min(rect.width, rect.height) * 0.5f));
		}

		// Draws a white icon texture tinted with a colour.
		public static void DrawIcon(Rect rect, Texture2D icon, Color color) {
			if (Event.current.type != EventType.Repaint) {
				return;
			}
			var old = GUI.color;
			GUI.color = color;
			GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);
			GUI.color = old;
		}

		private static Texture2D NewTexture(int size) {
			return new Texture2D(size, size, TextureFormat.RGBA32, false) {
				filterMode = FilterMode.Bilinear,
				wrapMode = TextureWrapMode.Clamp,
				hideFlags = HideFlags.HideAndDontSave
			};
		}
	}
}

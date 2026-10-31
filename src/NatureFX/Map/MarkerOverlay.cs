using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Map {
	// F8 > Map > Show markers: every map marker drawn over the picture, so an author sees where the zones, fog, particle
	// boxes, puddles, decals, probes and lights are. Boxes as coloured outlines (rotated as the marker), lights and
	// other points as a small cross, each with its name, its distance and, where there is one, what it is doing.
	// Drawn in OnGUI with GL lines in screen space: no shader of the game or the bundle needed.
	public static class MarkerOverlay {
		private const float MaxDistance = 250.0f;
		private const float LabelDistance = 120.0f;

		private static Material lines_;
		private static GUIStyle label_;
		private static readonly Vector3[] Corners = new Vector3[8];

		private static readonly Dictionary<string, Color> Colors = new() {
			["zone"] = new Color(0.3f, 0.75f, 1.0f),
			["volume"] = new Color(0.85f, 0.85f, 0.95f),
			["fx"] = new Color(0.55f, 1.0f, 0.4f),
			["puddle"] = new Color(0.2f, 0.5f, 1.0f),
			["water"] = new Color(0.2f, 0.8f, 0.9f),
			["decal"] = new Color(1.0f, 0.55f, 0.85f),
			["probe"] = new Color(1.0f, 1.0f, 0.4f),
			["light"] = new Color(1.0f, 0.65f, 0.2f),
			["grass"] = new Color(0.3f, 0.9f, 0.3f),
			["forest"] = new Color(0.2f, 0.6f, 0.25f)
		};

		// status: an extra line for a marker (zone weight, fog / particle state), or null
		public static void Draw(Camera camera, IReadOnlyList<Marker> markers, System.Func<Marker, string> status) {
			if (camera == null || markers == null || Event.current.type != EventType.Repaint) {
				return;
			}
			if (lines_ == null) {
				var shader = Shader.Find("Hidden/Internal-Colored");
				if (shader == null) {
					return;
				}
				lines_ = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
				lines_.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
				lines_.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
				lines_.SetInt("_ZWrite", 0);
			}
			label_ ??= new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.UpperCenter, richText = false };

			var eye = camera.transform.position;
			var labels = new List<(Vector2 At, string Text, Color Color, float Distance)>();
			lines_.SetPass(0);
			GL.PushMatrix();
			GL.LoadPixelMatrix();
			GL.Begin(GL.LINES);
			foreach (var m in markers) {
				float distance = Vector3.Distance(m.Position, eye);
				if (distance > MaxDistance) {
					continue;
				}
				var color = Colors.TryGetValue(m.Kind, out var c) ? c : Color.white;
				// fade with distance so a busy map stays readable
				color.a = Mathf.Lerp(1.0f, 0.35f, distance / MaxDistance);
				GL.Color(color);
				bool box = m.Size.x > 0.3f && m.Size.z > 0.3f && m.Kind != "light";
				if (box) {
					DrawBox(camera, m);
				} else {
					DrawCross(camera, m.Position, Mathf.Clamp(distance * 0.02f, 0.3f, 2.0f));
				}
				if (distance < LabelDistance) {
					var top = m.Position + Vector3.up * (box ? Mathf.Abs(m.Size.y) * 0.5f : 0.6f);
					var s = camera.WorldToScreenPoint(top);
					if (s.z > 0.0f) {
						string name = string.IsNullOrEmpty(m.Preset) ? m.Kind : $"{m.Kind}_{m.Preset}";
						string extra = status?.Invoke(m);
						string text = $"{name}  {distance:0} m" + (string.IsNullOrEmpty(extra) ? "" : "\n" + extra);
						labels.Add((new Vector2(s.x, UnityEngine.Screen.height - s.y), text, color, distance));
					}
				}
			}
			GL.End();
			GL.PopMatrix();

			// nearest first gets its place; a label that would cover one already placed moves up above it, and one with
			// no free place left (a crowded corner far away) is left out rather than drawn over the others
			labels.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			placed_.Clear();
			foreach (var (at, text, color, _) in labels) {
				var size = label_.CalcSize(new GUIContent(text));
				var r = new Rect(at.x - size.x * 0.5f - 4.0f, at.y - size.y - 6.0f, size.x + 8.0f, size.y + 2.0f);
				bool free = false;
				for (int tries = 0; tries < 8 && !free; ++tries) {
					free = true;
					foreach (var other in placed_) {
						if (other.Overlaps(r)) {
							r.y = other.yMin - r.height - 1.0f;
							free = false;
							break;
						}
					}
				}
				if (!free || r.yMax < 0.0f) {
					continue;
				}
				placed_.Add(r);
				GUI.color = new Color(0.0f, 0.0f, 0.0f, 0.7f);
				GUI.DrawTexture(r, Texture2D.whiteTexture);
				GUI.color = Color.white;
				label_.normal.textColor = new Color(color.r, color.g, color.b, 1.0f);
				GUI.Label(r, text, label_);
			}
			GUI.color = Color.white;
		}

		private static readonly List<Rect> placed_ = new();

		private static void DrawBox(Camera camera, Marker m) {
			var half = new Vector3(Mathf.Abs(m.Size.x), Mathf.Abs(m.Size.y), Mathf.Abs(m.Size.z)) * 0.5f;
			for (int i = 0; i < 8; ++i) {
				var local = new Vector3((i & 1) == 0 ? -half.x : half.x, (i & 2) == 0 ? -half.y : half.y, (i & 4) == 0 ? -half.z : half.z);
				Corners[i] = m.Position + m.Rotation * local;
			}
			// the 12 edges: pairs differing in one bit
			for (int i = 0; i < 8; ++i) {
				for (int bit = 1; bit < 8; bit <<= 1) {
					int j = i | bit;
					if (j != i) {
						Line(camera, Corners[i], Corners[j]);
					}
				}
			}
		}

		private static void DrawCross(Camera camera, Vector3 p, float size) {
			Line(camera, p - Vector3.right * size, p + Vector3.right * size);
			Line(camera, p - Vector3.up * size, p + Vector3.up * size);
			Line(camera, p - Vector3.forward * size, p + Vector3.forward * size);
		}

		// a segment cut at the near plane (an end behind the camera would project mirrored, across the screen; dropping
		// the whole edge left boxes around the car half drawn)
		private static void Line(Camera camera, Vector3 wa, Vector3 wb) {
			var eye = camera.transform.position;
			var forward = camera.transform.forward;
			float near = camera.nearClipPlane + 0.01f;
			float da = Vector3.Dot(wa - eye, forward) - near, db = Vector3.Dot(wb - eye, forward) - near;
			if (da < 0.0f && db < 0.0f) {
				return;
			}
			if (da < 0.0f) {
				wa = Vector3.Lerp(wa, wb, da / (da - db));
			} else if (db < 0.0f) {
				wb = Vector3.Lerp(wa, wb, da / (da - db));
			}
			var a = camera.WorldToScreenPoint(wa);
			var b = camera.WorldToScreenPoint(wb);
			GL.Vertex3(a.x, a.y, 0.0f);
			GL.Vertex3(b.x, b.y, 0.0f);
		}
	}
}

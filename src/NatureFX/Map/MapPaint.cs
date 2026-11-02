using System;
using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using UnityEngine;

namespace NatureFX.Map {
	// The map profile's painted dirt ([paint]): mud splashed up the verges, water stains down the foot of walls, dark ruts,
	// along lines the map gives by position. Drawn into a map around the camera (Size m over Texels texels, redrawn
	// when the camera has gone a quarter of it away) that the map's surfaces read (NFX_Ground.hlsl NFX_ApplyPaint):
	// r mud, g water stains, b ruts, a the top they reach (absolute y).
	//   [paint]
	//   mud   = x,y,z; x,y,z; ... | width=1.5 height=0.6 strength=0.8
	//   stain = ...                (y: the ground there; height: how far up from it)
	//   rut   = ...
	public class MapPaint {
		private const int Texels = 512;
		private const float Size = 256.0f;  // m: 0.5 m a texel

		private static readonly int MapId = Shader.PropertyToID("_NFX_PaintMap");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_PaintParams");

		private class Line {
			public int Channel;            // 0 mud, 1 stain, 2 rut
			public Vector3[] Points;
			public float Width = 1.0f, Height = 0.5f, Strength = 0.8f;
		}

		private readonly List<Line> lines_ = new();
		private Texture2D map_;
		private Color[] texels_;
		private Vector2 corner_;
		private bool drawn_;

		public int Count => lines_.Count;
		public string Status { get; private set; } = "none";

		public void Load(MapProfile profile) {
			drawing_?.Wait();
			drawing_ = null;
			lines_.Clear();
			drawn_ = false;
			foreach (string raw in profile.Section("paint")) {
				var line = Parse(raw);
				if (line != null) {
					lines_.Add(line);
				}
			}
			Status = lines_.Count > 0 ? $"{lines_.Count} lines" : "none";
		}

		private static Line Parse(string raw) {
			int eq = raw.IndexOf('=');
			if (eq < 0) {
				return null;
			}
			string kind = raw.Substring(0, eq).Trim().ToLowerInvariant();
			int channel = kind == "mud" ? 0 : kind == "stain" ? 1 : kind == "rut" ? 2 : -1;
			if (channel < 0) {
				Kino.Log.Warning($"[NatureFX] Map profile [paint]: unknown kind '{kind}' in '{raw}' (mud, stain, rut)");
				return null;
			}
			string rest = raw.Substring(eq + 1);
			string options = "";
			int bar = rest.IndexOf('|');
			if (bar >= 0) {
				options = rest.Substring(bar + 1);
				rest = rest.Substring(0, bar);
			}
			var points = new List<Vector3>();
			foreach (string p in rest.Split(';')) {
				var c = p.Split(',');
				if (c.Length == 3 && float.TryParse(c[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
				    && float.TryParse(c[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
				    && float.TryParse(c[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)) {
					points.Add(new Vector3(x, y, z));
				}
			}
			if (points.Count == 0) {
				Kino.Log.Warning($"[NatureFX] Map profile [paint]: no points (x,y,z; x,y,z) in '{raw}'");
				return null;
			}
			var line = new Line { Channel = channel, Points = points.ToArray() };
			if (channel == 1) {
				line.Height = 1.0f;
			} else if (channel == 2) {
				line.Width = 0.4f;
			}
			foreach (string o in options.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries)) {
				var kv = o.Split('=');
				if (kv.Length != 2 || !float.TryParse(kv[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) {
					continue;
				}
				switch (kv[0].Trim().ToLowerInvariant()) {
					case "width": line.Width = Mathf.Clamp(v, 0.1f, 20.0f); break;
					case "height": line.Height = Mathf.Clamp(v, 0.0f, 10.0f); break;
					case "strength": line.Strength = Mathf.Clamp01(v); break;
				}
			}
			return line;
		}

		public void Update(bool on, Vector3 camera) {
			if (!on || lines_.Count == 0) {
				Shader.SetGlobalVector(ParamsId, Vector4.zero);
				return;
			}
			var centre = new Vector2(camera.x, camera.z);
			// drawn on a worker thread (a 512² float map took a frame's time), put in when done
			if (drawing_ != null && drawing_.IsCompleted) {
				Finish();
			}
			if (drawing_ == null && (!drawn_ || Vector2.Distance(centre, corner_ + Vector2.one * (Size * 0.5f)) > Size * 0.25f)) {
				Draw(centre);
			}
			if (!drawn_) {
				Shader.SetGlobalVector(ParamsId, Vector4.zero);
				return;
			}
			Shader.SetGlobalTexture(MapId, map_);
			Shader.SetGlobalVector(ParamsId, new Vector4(corner_.x, corner_.y, Size, 1.0f));
		}

		// every line's capsules into the texels around centre: each channel the strongest, a the highest top
		private System.Threading.Tasks.Task<int> drawing_;
		private Vector2 nextCorner_;

		private void Draw(Vector2 centre) {
			if (map_ == null) {
				map_ = new Texture2D(Texels, Texels, TextureFormat.RGBAFloat, false, true) {
					name = "NatureFX.PaintMap", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
				};
				texels_ = new Color[Texels * Texels];
			}
			const float texel = Size / Texels;
			var corner = new Vector2(Mathf.Floor((centre.x - Size * 0.5f) / texel) * texel, Mathf.Floor((centre.y - Size * 0.5f) / texel) * texel);
			nextCorner_ = corner;
			var texels = texels_;
			var lines = lines_.ToArray();
			drawing_ = System.Threading.Tasks.Task.Run(() => {
				Array.Clear(texels, 0, texels.Length);
				for (int i = 0; i < texels.Length; ++i) {
					texels[i].a = -1e6f;
				}
				int drawn = 0;
				foreach (var line in lines) {
					for (int k = 0; k < line.Points.Length; ++k) {
						var a = line.Points[k];
						var b = line.Points[Math.Min(k + 1, line.Points.Length - 1)];
						drawn += Segment(texels, corner, line, a, b, texel);
					}
				}
				return drawn;
			});
		}

		private void Finish() {
			var task = drawing_;
			drawing_ = null;
			if (task.IsFaulted) {
				Kino.Log.Warning($"[NatureFX] Painted dirt: {task.Exception?.GetBaseException().Message}");
				return;
			}
			corner_ = nextCorner_;
			map_.SetPixels(texels_);
			map_.Apply(false, false);
			drawn_ = true;
			Status = $"{lines_.Count} lines, {task.Result} texels painted around the camera";
		}

		private static int Segment(Color[] texels_, Vector2 corner_, Line line, Vector3 a, Vector3 b, float texel) {
			float reach = line.Width;
			int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x) - reach - corner_.x) / texel));
			int x1 = Mathf.Min(Texels - 1, Mathf.CeilToInt((Mathf.Max(a.x, b.x) + reach - corner_.x) / texel));
			int y0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z) - reach - corner_.y) / texel));
			int y1 = Mathf.Min(Texels - 1, Mathf.CeilToInt((Mathf.Max(a.z, b.z) + reach - corner_.y) / texel));
			var ab = new Vector2(b.x - a.x, b.z - a.z);
			float len2 = Mathf.Max(ab.sqrMagnitude, 1e-6f);
			int count = 0;
			for (int y = y0; y <= y1; ++y) {
				for (int x = x0; x <= x1; ++x) {
					var p = new Vector2(corner_.x + (x + 0.5f) * texel, corner_.y + (y + 0.5f) * texel);
					float t = Mathf.Clamp01(Vector2.Dot(p - new Vector2(a.x, a.z), ab) / len2);
					var q = new Vector2(a.x, a.z) + ab * t;
					float d = Vector2.Distance(p, q);
					if (d > reach) {
						continue;
					}
					// soft across: full in the middle half, fading to the edge
					float v = line.Strength * Mathf.Clamp01((reach - d) / (reach * 0.5f));
					int i = y * Texels + x;
					var c = texels_[i];
					if (line.Channel == 0) {
						c.r = Mathf.Max(c.r, v);
					} else if (line.Channel == 1) {
						c.g = Mathf.Max(c.g, v);
					} else {
						c.b = Mathf.Max(c.b, v);
					}
					if (line.Channel != 2) {
						c.a = Mathf.Max(c.a, Mathf.Lerp(a.y, b.y, t) + line.Height);
					}
					texels_[i] = c;
					++count;
				}
			}
			return count;
		}

		public void Destroy() {
			if (map_ != null) {
				UnityEngine.Object.Destroy(map_);
			}
			map_ = null;
		}
	}
}

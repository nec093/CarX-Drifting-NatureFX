using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// Decal markers (docs/map-authoring.md 7.2): nfx_decal_<preset> lays a pattern NatureFX paints itself (crack, patch,
	// oil, leaves_pile, sand, skid, moss_edge) on the ground in its box. The game's HDRP asset has decals switched off,
	// so the NatureFX ground shaders draw them (NFX_ApplyDecals: the 32 nearest the camera, projected from above,
	// turned by the marker's yaw); they show on surfaces with NatureFX shaders only. A disabled Decal Projector the author set up is switched on as it is (shows only if the game ever
	// turns decals on).
	public class MapDecals {
		private const int MaxDecals = 512;
		private const int MaxDrawn = 32;
		private const int TextureSize = 128;
		private static readonly Vector3 DefaultSize = new(4.0f, 1.0f, 4.0f);
		private static readonly string[] PresetNames = {
			"crack", "patch", "oil", "leaves_pile", "sand", "skid", "moss_edge",
			// Japanese road paint and fittings
			"slow_dots", "zebra", "speed_30", "speed_40", "speed_50", "joint", "manhole", "worn_line"
		};

		private static readonly int DecalsId = Shader.PropertyToID("_NFX_Decals");
		private static readonly int DecalSizesId = Shader.PropertyToID("_NFX_DecalSizes");
		private static readonly int DecalCountId = Shader.PropertyToID("_NFX_DecalCount");
		private static readonly int DecalAtlasId = Shader.PropertyToID("_NFX_DecalAtlas");

		private class Decal {
			public Vector3 Center;
			public Vector3 Half;      // x across, y depth (up / down), z along
			public float Yaw;         // radians
			public int Slice;         // preset in the texture array
			public float Opacity;
			public DecalProjector Own;
		}

		private readonly List<Decal> decals_ = new();
		private readonly List<Decal> nearest_ = new();
		private readonly Vector4[] centers_ = new Vector4[MaxDrawn];
		private readonly Vector4[] sizes_ = new Vector4[MaxDrawn];
		private Texture2DArray atlas_;
		private float nextSort_;

		public int Count => decals_.Count;

		public void Load(MarkerScanner markers) {
			Clear();
			foreach (var m in markers.OfKind("decal")) {
				if (decals_.Count >= MaxDecals) {
					Kino.Log.Warning($"[NatureFX] More than {MaxDecals} decal markers: the rest are ignored");
					break;
				}
				float opacity = Mathf.Clamp01(m.GetFloat("opacity", 1.0f));
				if (m.Component is DecalProjector own) {
					decals_.Add(new Decal { Own = own, Opacity = opacity });
					continue;
				}
				string preset = string.IsNullOrEmpty(m.Preset) ? "crack" : m.Preset.ToLowerInvariant();
				int slice = System.Array.IndexOf(PresetNames, preset);
				if (slice < 0) {
					Kino.Log.Warning($"[NatureFX] Unknown decal preset '{preset}' ({string.Join(", ", PresetNames)})");
					continue;
				}
				// an empty object's scale is the pattern's size: x across, z along, y how far above and below it reaches
				var size = m.Size.x > 0.3f && m.Size.z > 0.3f ? m.Size : DefaultSize;
				float yaw = m.Get("yaw") != null ? m.GetFloat("yaw", 0.0f) : m.Rotation.eulerAngles.y;
				decals_.Add(new Decal {
					Center = m.Position,
					Half = new Vector3(Mathf.Abs(size.x) * 0.5f, Mathf.Max(0.5f, Mathf.Abs(size.y)) * 0.5f, Mathf.Abs(size.z) * 0.5f),
					Yaw = yaw * Mathf.Deg2Rad,
					Slice = slice,
					Opacity = opacity
				});
			}
			if (decals_.Count > 0) {
				Kino.Log.Info($"[NatureFX] {decals_.Count} map decals");
				Atlas();
			}
			nextSort_ = 0.0f;
		}

		public void Clear() {
			foreach (var d in decals_) {
				if (d.Own != null) {
					d.Own.enabled = false;
				}
			}
			decals_.Clear();
			nearest_.Clear();
			Shader.SetGlobalFloat(DecalCountId, 0.0f);
		}

		public void Destroy() {
			Clear();
			if (atlas_ != null) {
				Object.Destroy(atlas_);
			}
			atlas_ = null;
		}

		public void Update(bool on, Vector3 camera) {
			if (!on || decals_.Count == 0) {
				Shader.SetGlobalFloat(DecalCountId, 0.0f);
				foreach (var d in decals_) {
					if (d.Own != null && d.Own.enabled) {
						d.Own.enabled = false;
					}
				}
				return;
			}
			if (Time.unscaledTime >= nextSort_) {
				nextSort_ = Time.unscaledTime + 0.5f;
				nearest_.Clear();
				foreach (var d in decals_) {
					if (d.Own != null) {
						d.Own.enabled = true;
					} else {
						nearest_.Add(d);
					}
				}
				nearest_.Sort((a, b) => (a.Center - camera).sqrMagnitude.CompareTo((b.Center - camera).sqrMagnitude));
				if (nearest_.Count > MaxDrawn) {
					nearest_.RemoveRange(MaxDrawn, nearest_.Count - MaxDrawn);
				}
			}
			for (int i = 0; i < MaxDrawn; ++i) {
				if (i < nearest_.Count) {
					var d = nearest_[i];
					float opacity = d.Opacity;
					// w packs the preset and the opacity: slice * 4 + opacity (NFX_ApplyDecals)
					centers_[i] = new Vector4(d.Center.x, d.Center.y, d.Center.z, d.Slice * 4.0f + Mathf.Min(opacity, 0.99f));
					sizes_[i] = new Vector4(d.Half.x, d.Half.y, d.Half.z, d.Yaw);
				} else {
					centers_[i] = Vector4.zero;
					sizes_[i] = Vector4.zero;
				}
			}
			Shader.SetGlobalVectorArray(DecalsId, centers_);
			Shader.SetGlobalVectorArray(DecalSizesId, sizes_);
			Shader.SetGlobalFloat(DecalCountId, nearest_.Count);
			if (atlas_ != null) {
				Shader.SetGlobalTexture(DecalAtlasId, atlas_);
			}
		}

		// every preset's pattern in one texture array (slice = index in PresetNames)
		private void Atlas() {
			if (atlas_ != null) {
				return;
			}
			atlas_ = new Texture2DArray(TextureSize, TextureSize, PresetNames.Length, TextureFormat.RGBA32, true) {
				name = "NatureFX.DecalAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear
			};
			for (int i = 0; i < PresetNames.Length; ++i) {
				atlas_.SetPixels(Paint(PresetNames[i], 0), i, 0);
			}
			atlas_.Apply(true, true);
		}

		// The pattern: rgb colour, a coverage. Value noise and cells, no textures from the game.
		private static Color[] Paint(string preset, int seed) {
			var pixels = new Color[TextureSize * TextureSize];
			float s = seed * 13.7f;
			for (int y = 0; y < TextureSize; ++y) {
				for (int x = 0; x < TextureSize; ++x) {
					float u = (x + 0.5f) / TextureSize;
					float v = (y + 0.5f) / TextureSize;
					float edge = Mathf.Clamp01(Mathf.Min(Mathf.Min(u, 1.0f - u), Mathf.Min(v, 1.0f - v)) * 6.0f);
					float n = Fbm(u * 6.0f + s, v * 6.0f);
					float fine = Fbm(u * 24.0f + s, v * 24.0f + 7.0f);
					Color c;
					switch (preset) {
						case "crack": {
							float line = Mathf.Abs(Fbm(u * 3.0f + s, v * 3.0f) - 0.5f);
							float branch = Mathf.Abs(Fbm(u * 7.0f + s + 3.0f, v * 7.0f) - 0.5f);
							float a = Mathf.Max(Mathf.SmoothStep(0.025f, 0.0f, line), Mathf.SmoothStep(0.015f, 0.0f, branch) * 0.8f);
							c = new Color(0.03f, 0.03f, 0.03f, a * edge);
							break;
						}
						case "patch": {
							float a = Mathf.SmoothStep(0.0f, 0.08f, Mathf.Min(Mathf.Min(u, 1.0f - u), Mathf.Min(v, 1.0f - v)) + (n - 0.5f) * 0.05f);
							float g = 0.07f + fine * 0.05f;
							c = new Color(g, g, g * 1.05f, a * 0.85f);
							break;
						}
						case "oil": {
							float blob = Mathf.SmoothStep(0.55f, 0.65f, n + (0.5f - Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f))) * 0.8f);
							c = new Color(0.02f, 0.02f, 0.025f, blob * 0.7f * edge);
							break;
						}
						case "leaves_pile": {
							float pile = Mathf.SmoothStep(0.45f, 0.6f, n + (0.5f - Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f))) * 0.9f);
							float leaf = Mathf.SmoothStep(0.45f, 0.55f, fine);
							float k = Hash(Mathf.Floor(u * 30.0f), Mathf.Floor(v * 30.0f) + s);
							var col = k < 0.35f ? new Color(0.55f, 0.18f, 0.05f) : k < 0.7f ? new Color(0.62f, 0.42f, 0.08f) : new Color(0.35f, 0.22f, 0.1f);
							c = new Color(col.r, col.g, col.b, pile * Mathf.Lerp(0.6f, 1.0f, leaf) * edge);
							break;
						}
						case "sand": {
							float grain = Hash(Mathf.Floor(u * 90.0f) + s, Mathf.Floor(v * 90.0f));
							float a = Mathf.SmoothStep(0.45f, 0.7f, n) * (grain > 0.4f ? 1.0f : 0.4f);
							c = new Color(0.55f + grain * 0.1f, 0.48f + grain * 0.08f, 0.36f, a * edge * 0.9f);
							break;
						}
						case "skid": {
							float track = Mathf.Max(Mathf.SmoothStep(0.09f, 0.05f, Mathf.Abs(u - 0.3f)), Mathf.SmoothStep(0.09f, 0.05f, Mathf.Abs(u - 0.7f)));
							float a = track * Mathf.Lerp(0.4f, 0.9f, fine) * Mathf.Clamp01(v * 4.0f) * Mathf.Clamp01((1.0f - v) * 1.5f);
							c = new Color(0.02f, 0.02f, 0.02f, a);
							break;
						}
						case "slow_dots": {
							// the dotted deceleration marks laid across a lane before a corner: rows of white blocks
							float row = Mathf.Repeat(v * 6.0f, 1.0f);
							float col = Mathf.Repeat(u * 5.0f + Mathf.Floor(v * 6.0f) * 0.5f, 1.0f);
							float block = Mathf.SmoothStep(0.62f, 0.55f, Mathf.Abs(row - 0.5f) * 2.0f) * Mathf.SmoothStep(0.72f, 0.64f, Mathf.Abs(col - 0.5f) * 2.0f);
							c = new Color(0.85f, 0.85f, 0.82f, block * Mathf.Lerp(0.75f, 1.0f, fine));
							break;
						}
						case "zebra": {
							// diagonal stripes inside a frame (a no-stopping area)
							float stripe = Mathf.SmoothStep(0.45f, 0.4f, Mathf.Abs(Mathf.Repeat((u + v) * 4.0f, 1.0f) - 0.5f));
							float frame = Mathf.Max(Mathf.SmoothStep(0.06f, 0.04f, Mathf.Min(u, 1.0f - u)), Mathf.SmoothStep(0.06f, 0.04f, Mathf.Min(v, 1.0f - v)));
							c = new Color(0.86f, 0.86f, 0.83f, Mathf.Max(stripe, frame) * Mathf.Lerp(0.7f, 1.0f, fine));
							break;
						}
						case "speed_40": case "speed_30": case "speed_50": {
							// the painted speed limit, read from the driver's side (+z towards the reader)
							int value = preset == "speed_30" ? 30 : preset == "speed_50" ? 50 : 40;
							float glyph = Mathf.Max(Digit(value / 10, (u - 0.08f) / 0.4f, v), Digit(value % 10, (u - 0.52f) / 0.4f, v));
							c = new Color(0.86f, 0.86f, 0.83f, glyph * Mathf.Lerp(0.7f, 1.0f, fine));
							break;
						}
						case "joint": {
							// a bridge or expansion joint: a dark steel strip with tar on both sides
							float strip = Mathf.SmoothStep(0.12f, 0.08f, Mathf.Abs(v - 0.5f));
							float tar = Mathf.SmoothStep(0.2f, 0.12f, Mathf.Abs(v - 0.5f)) * 0.6f;
							float teeth = Mathf.SmoothStep(0.3f, 0.2f, Mathf.Abs(Mathf.Repeat(u * 16.0f, 1.0f) - 0.5f)) * Mathf.SmoothStep(0.05f, 0.02f, Mathf.Abs(v - 0.5f));
							float g = 0.05f + teeth * 0.12f;
							c = new Color(g, g, g * 1.05f, Mathf.Max(strip, tar) * edge);
							break;
						}
						case "manhole": {
							float r = Vector2.Distance(new Vector2(u, v), new Vector2(0.5f, 0.5f)) * 2.0f;
							float lid = Mathf.SmoothStep(0.86f, 0.82f, r);
							float ring = Mathf.SmoothStep(0.03f, 0.0f, Mathf.Abs(r - 0.84f));
							float pattern = Mathf.SmoothStep(0.45f, 0.5f, Mathf.Abs(Mathf.Repeat(u * 10.0f, 1.0f) - 0.5f) + Mathf.Abs(Mathf.Repeat(v * 10.0f, 1.0f) - 0.5f)); // diamond tread
							float g = 0.12f + pattern * 0.06f + fine * 0.04f - ring * 0.08f;
							c = new Color(g, g * 0.97f, g * 0.93f, Mathf.Max(lid, ring));
							break;
						}
						case "worn_line": {
							// an old edge line: broken up, with the asphalt showing through
							float line = Mathf.SmoothStep(0.16f, 0.12f, Mathf.Abs(u - 0.5f));
							float worn = Mathf.SmoothStep(0.35f, 0.6f, fine * 0.6f + n * 0.4f);
							c = new Color(0.8f, 0.8f, 0.76f, line * worn * 0.9f);
							break;
						}
						default: { // moss_edge: a band along the +x side, ragged towards the road
							float band = Mathf.SmoothStep(0.35f, 0.75f, u + (n - 0.5f) * 0.4f);
							var col = Color.Lerp(new Color(0.28f, 0.22f, 0.14f), new Color(0.14f, 0.24f, 0.07f), Mathf.SmoothStep(0.4f, 0.6f, fine));
							c = new Color(col.r, col.g, col.b, band * edge * 0.9f);
							break;
						}
					}
					pixels[y * TextureSize + x] = c;
				}
			}
			return pixels;
		}

		// a seven segment digit in the unit square (u across, v up), 1 on a stroke
		private static float Digit(int d, float u, float v) {
			if (u < 0.0f || u > 1.0f || v < 0.1f || v > 0.9f) {
				return 0.0f;
			}
			// segments a (top) b c (right) d (bottom) e f (left) g (middle)
			int[] masks = { 0x3F, 0x06, 0x5B, 0x4F, 0x66, 0x6D, 0x7D, 0x07, 0x7F, 0x6F };
			int mask = masks[Mathf.Clamp(d, 0, 9)];
			float y = (v - 0.1f) / 0.8f;
			const float w = 0.16f;
			float best = 0.0f;
			void Seg(int bit, bool horizontal, float x0, float y0, float length) {
				if ((mask & (1 << bit)) == 0) {
					return;
				}
				float along = horizontal ? u - x0 : y - y0;
				float across = horizontal ? y - y0 : u - x0;
				if (along >= 0.0f && along <= length && Mathf.Abs(across) <= w * 0.5f) {
					best = 1.0f;
				}
			}
			Seg(0, true, 0.1f, 0.95f, 0.8f);
			Seg(1, false, 0.9f, 0.5f, 0.45f);
			Seg(2, false, 0.9f, 0.05f, 0.45f);
			Seg(3, true, 0.1f, 0.05f, 0.8f);
			Seg(4, false, 0.1f, 0.05f, 0.45f);
			Seg(5, false, 0.1f, 0.5f, 0.45f);
			Seg(6, true, 0.1f, 0.5f, 0.8f);
			return best;
		}

		private static float Hash(float x, float y) {
			float h = Mathf.Sin(x * 127.1f + y * 311.7f) * 43758.5453f;
			return h - Mathf.Floor(h);
		}

		private static float Fbm(float x, float y) {
			float sum = 0.0f;
			float amp = 0.5f;
			for (int i = 0; i < 4; ++i) {
				sum += Mathf.PerlinNoise(x, y) * amp;
				x *= 2.03f;
				y *= 2.03f;
				amp *= 0.5f;
			}
			return sum / 0.9375f;
		}
	}
}

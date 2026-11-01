using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Rendering {
	// The look of NatureFX's particles (NFX_Particle.shader): 4 x 4 tiles painted once at start. Leaves after real ones
	// of a Japanese mountain road in autumn (momiji in four shades, a zelkova / cherry leaf, a yellow one, ginkgo, a dry
	// curled oak leaf) with their veins, mottling, dark spots and browned edges; two cherry petals with the notch;
	// rain, snow, motes, a glow and a moth.
	public static class ParticleAtlas {
		public const int Size = 1024;
		private const int Tile = Size / 4;

		// tile ranges (first, count) for NFX_Particle.shader's _NFX_PTiles
		public static readonly Vector2 Leaves = new(0, 8);
		public static readonly Vector2 Momiji = new(0, 4);
		public static readonly Vector2 Petals = new(8, 2);
		public static readonly Vector2 Rain = new(10, 1);
		public static readonly Vector2 Snow = new(11, 2);
		public static readonly Vector2 Mote = new(13, 1);
		public static readonly Vector2 Glow = new(14, 1);
		public static readonly Vector2 Moth = new(15, 1);

		private static Texture2D atlas_;

		// NFX_Particle.shader's modes
		public const int ModeLeaf = 0, ModeRain = 1, ModeSnow = 2, ModeGlow = 3, ModeMote = 4, ModeSmoke = 5, ModeTextured = 6, ModeTexturedGlow = 7;

		// a material of NFX_Particle.shader (null when the bundle is older than it); tiles: first and count
		public static Material Material(NatureFX.Nature.ShaderBundle bundle, string name, int mode, Vector2 tiles, Color tint, float emission = 0.0f) {
			var shader = bundle?.Get("NatureFX/Particle");
			if (shader == null) {
				return null;
			}
			var m = new Material(shader) { name = name, renderQueue = 3000 };
			m.SetTexture("_MainTex", Get());
			m.SetColor("_BaseColor", tint);
			m.SetFloat("_NFX_PMode", mode);
			m.SetVector("_NFX_PTiles", new Vector4(tiles.x, tiles.y, 0.0f, 0.0f));
			m.SetFloat("_Emission", emission);
			m.SetFloat("_Cutoff", 0.5f);
			bool cut = mode == ModeLeaf;
			bool add = mode == ModeGlow || mode == ModeTexturedGlow;
			m.SetFloat("_SrcBlend", (float)(cut || add ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.SrcAlpha));
			m.SetFloat("_DstBlend", (float)(cut ? UnityEngine.Rendering.BlendMode.Zero : add ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
			m.SetFloat("_ZWrite", cut ? 1.0f : 0.0f);
			return m;
		}

		// the vertex streams NFX_Particle.shader reads: the stable random number picks the tile
		public static void SetStreams(ParticleSystemRenderer renderer) {
			renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream> {
				ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color,
				ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomX
			});
		}

		private static Mesh leaf_;

		// a leaf for the mesh particles: a 4 x 4 grid, cupped across and curled along, so it turns and catches the
		// light as a real leaf does while it falls
		public static Mesh LeafMesh() {
			if (leaf_ != null) {
				return leaf_;
			}
			const int n = 4;
			var vertices = new List<Vector3>();
			var normals = new List<Vector3>();
			var uvs = new List<Vector2>();
			var triangles = new List<int>();
			for (int j = 0; j <= n; ++j) {
				for (int i = 0; i <= n; ++i) {
					float u = (float)i / n, v = (float)j / n;
					float x = u - 0.5f, y = v - 0.5f;
					float z = x * x * 0.5f + y * y * 0.25f;
					vertices.Add(new Vector3(x, y, z));
					normals.Add(new Vector3(-x, -y * 0.5f, 1.0f).normalized * -1.0f);
					uvs.Add(new Vector2(u, v));
				}
			}
			for (int j = 0; j < n; ++j) {
				for (int i = 0; i < n; ++i) {
					int a = j * (n + 1) + i;
					triangles.AddRange(new[] { a, a + n + 1, a + 1, a + 1, a + n + 1, a + n + 2 });
				}
			}
			leaf_ = new Mesh { name = "NatureFX.Leaf" };
			leaf_.SetVertices(vertices);
			leaf_.SetNormals(normals);
			leaf_.SetUVs(0, uvs);
			leaf_.SetTriangles(triangles, 0);
			leaf_.RecalculateBounds();
			return leaf_;
		}

		public static Texture2D Get() {
			if (atlas_ != null) {
				return atlas_;
			}
			var px = new Color32[Size * Size];
			for (int t = 0; t < 16; ++t) {
				Paint(px, t);
			}
			atlas_ = new Texture2D(Size, Size, TextureFormat.RGBA32, true, false) {
				name = "NatureFX.ParticleAtlas", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Trilinear, anisoLevel = 4
			};
			atlas_.SetPixels32(px);
			atlas_.Apply(true, true);
			return atlas_;
		}

		// one tile; local coordinates p in -1..1 with y towards the leaf's tip
		private static void Paint(Color32[] px, int tile) {
			int ox = (tile % 4) * Tile;
			int oy = (3 - tile / 4) * Tile; // tile 0 at the top left, as the shader reads it
			float aa = 2.5f / Tile;
			for (int y = 0; y < Tile; ++y) {
				for (int x = 0; x < Tile; ++x) {
					// a margin of transparent pixels so the mip maps do not bleed into the next tile
					var p = new Vector2((x + 0.5f) / Tile * 2.0f - 1.0f, (y + 0.5f) / Tile * 2.0f - 1.0f) * 1.08f;
					Color c = Shade(tile, p, aa);
					px[(oy + y) * Size + ox + x] = c;
				}
			}
		}

		private static Color Shade(int tile, Vector2 p, float aa) {
			switch (tile) {
				case 0: return Maple(p, aa, new Color(0.62f, 0.07f, 0.04f), new Color(0.42f, 0.03f, 0.03f), 1);
				case 1: return Maple(p, aa, new Color(0.78f, 0.18f, 0.04f), new Color(0.55f, 0.07f, 0.03f), 2);
				case 2: return Maple(p, aa, new Color(0.88f, 0.42f, 0.06f), new Color(0.7f, 0.2f, 0.04f), 3);
				case 3: return Maple(p, aa, new Color(0.9f, 0.68f, 0.12f), new Color(0.78f, 0.46f, 0.06f), 4);
				case 4: return Ovate(p, aa, new Color(0.78f, 0.36f, 0.08f), new Color(0.5f, 0.2f, 0.06f), 5, 0.36f);
				case 5: return Ovate(p, aa, new Color(0.86f, 0.66f, 0.14f), new Color(0.6f, 0.44f, 0.1f), 6, 0.3f);
				case 6: return Ginkgo(p, aa);
				case 7: return Oak(p, aa);
				case 8: return Petal(p, aa, new Color(0.98f, 0.78f, 0.84f), 8);
				case 9: return Petal(p, aa, new Color(0.99f, 0.9f, 0.92f), 9);
				case 10: return RainStreak(p);
				case 11: return Flake(p, 11, 0.55f);
				case 12: return Flake(p, 12, 0.8f);
				case 13: return Soft(p, 0.55f, Color.white);
				case 14: return Soft(p, 0.35f, Color.white);
				default: return MothWings(p, aa);
			}
		}

		// --- leaves

		// Japanese maple: seven pointed lobes cut deep, fine teeth, veins running out to each tip, a thin stalk
		private static Color Maple(Vector2 p, float aa, Color baseColor, Color edgeColor, int seed) {
			var c = new Vector2(0.0f, -0.3f);
			var d = p - c;
			float r = d.magnitude;
			float a = Mathf.Atan2(d.x, d.y); // 0 up
			float best = 0.0f;
			float vein = 1.0f;
			for (int j = 0; j < 7; ++j) {
				float center = -1.95f + j * 0.65f;
				float len = j == 3 ? 1.0f : (j == 2 || j == 4 ? 0.93f : (j == 1 || j == 5 ? 0.78f : 0.5f));
				float da = (a - center) / 0.42f;
				float lobe = Mathf.Abs(da) < 1.0f ? Mathf.Pow(Mathf.Cos(da * Mathf.PI * 0.5f), 1.2f) * len : 0.0f;
				best = Mathf.Max(best, lobe);
				// vein from the centre to the lobe's tip
				float off = Mathf.Abs(Mathf.Sin(a - center)) * r;
				if (Mathf.Abs(a - center) < 0.6f) {
					vein = Mathf.Min(vein, off / Mathf.Lerp(0.02f, 0.006f, r / 0.8f));
				}
			}
			float teeth = 1.0f + 0.035f * Mathf.Sin(a * 90.0f + seed) * best;
			float radius = (0.2f + 0.72f * best) * teeth;
			float inside = Mathf.Clamp01((radius - r) / aa);
			inside = Mathf.Max(inside, Stalk(p, aa, -0.3f));
			if (inside <= 0.0f) {
				return Clear;
			}
			float edge = Mathf.Clamp01(1.0f - (radius - r) / 0.12f);
			return Leaf(p, inside, baseColor, edgeColor, edge, vein, seed);
		}

		// an ovate leaf with a serrated edge: zelkova, cherry
		private static Color Ovate(Vector2 p, float aa, Color baseColor, Color edgeColor, int seed, float width) {
			float t = (p.y + 0.82f) / 1.64f;
			if (t < 0.0f || t > 1.0f) {
				return Stalk(p, aa, -0.82f) > 0.0f ? Leaf(p, Stalk(p, aa, -0.82f), baseColor, edgeColor, 1.0f, 1.0f, seed) : Clear;
			}
			float half = width * Mathf.Pow(Mathf.Sin(Mathf.PI * t), 0.85f) * (1.15f - 0.35f * t);
			half *= 1.0f + 0.05f * Mathf.Sin(t * 70.0f);
			float inside = Mathf.Clamp01((half - Mathf.Abs(p.x)) / aa);
			inside = Mathf.Max(inside, Stalk(p, aa, -0.82f));
			if (inside <= 0.0f) {
				return Clear;
			}
			// midrib and side veins sloping up towards the edge
			float mid = Mathf.Abs(p.x) / 0.012f;
			float side = Mathf.Abs(Frac((p.y - Mathf.Abs(p.x) * 0.9f) * 7.0f) - 0.5f) * 2.0f / 0.08f;
			float vein = Mathf.Min(mid, Mathf.Lerp(side, 99.0f, Mathf.Abs(p.x) / Mathf.Max(half, 1e-3f)));
			float edge = Mathf.Clamp01(1.0f - (half - Mathf.Abs(p.x)) / 0.1f);
			return Leaf(p, inside, baseColor, edgeColor, edge, vein, seed);
		}

		// ginkgo: a fan on a long stalk, notched in the middle, fine veins fanning out
		private static Color Ginkgo(Vector2 p, float aa) {
			var c = new Vector2(0.0f, -0.42f);
			var d = p - c;
			float r = d.magnitude;
			float a = Mathf.Atan2(d.x, d.y);
			float spread = 1.1f;
			float outer = 0.98f + 0.03f * Mathf.Sin(a * 14.0f) - 0.22f * Mathf.Exp(-a * a / 0.004f);
			float inside = Mathf.Abs(a) < spread ? Mathf.Clamp01((outer - r) / aa) * Mathf.Clamp01((spread - Mathf.Abs(a)) / 0.05f) * Mathf.Clamp01((r - 0.05f) / aa) : 0.0f;
			inside = Mathf.Max(inside, Stalk(p, aa, -0.38f));
			if (inside <= 0.0f) {
				return Clear;
			}
			float vein = Mathf.Abs(Frac(a * 30.0f) - 0.5f) * 2.0f / 0.25f;
			float edge = Mathf.Clamp01(1.0f - (outer - r) / 0.1f);
			return Leaf(p, inside, new Color(0.95f, 0.78f, 0.12f), new Color(0.8f, 0.55f, 0.08f), edge, Mathf.Lerp(99.0f, vein, 0.5f), 7);
		}

		// a dry, curled brown oak leaf with rounded lobes
		private static Color Oak(Vector2 p, float aa) {
			float t = (p.y + 0.82f) / 1.64f;
			float half = t >= 0.0f && t <= 1.0f ? 0.36f * Mathf.Pow(Mathf.Sin(Mathf.PI * t), 0.7f) * (0.7f + 0.3f * Mathf.Abs(Mathf.Cos(t * Mathf.PI * 4.5f))) : -1.0f;
			float inside = Mathf.Max(Mathf.Clamp01((half - Mathf.Abs(p.x)) / aa), Stalk(p, aa, -0.82f));
			if (inside <= 0.0f) {
				return Clear;
			}
			float mid = Mathf.Abs(p.x) / 0.014f;
			float side = Mathf.Abs(Frac((p.y - Mathf.Abs(p.x) * 0.5f) * 4.5f) - 0.5f) * 2.0f / 0.07f;
			float edge = half > 0.0f ? Mathf.Clamp01(1.0f - (half - Mathf.Abs(p.x)) / 0.16f) : 1.0f;
			var c = Leaf(p, inside, new Color(0.45f, 0.27f, 0.12f), new Color(0.28f, 0.15f, 0.07f), edge, Mathf.Min(mid, side), 13);
			// crumpled: light and dark folds
			float fold = Noise(p * 7.0f, 21) - 0.5f;
			return new Color(c.r * (1.0f + fold * 0.5f), c.g * (1.0f + fold * 0.5f), c.b * (1.0f + fold * 0.5f), c.a);
		}

		private static float Stalk(Vector2 p, float aa, float top) {
			if (p.y > top || p.y < top - 0.45f) {
				return 0.0f;
			}
			float bend = (p.y - top) * (p.y - top) * 0.08f;
			return Mathf.Clamp01((0.018f - Mathf.Abs(p.x - bend)) / aa);
		}

		// the leaf's surface: mottled colour from the base towards the edge colour, lighter veins, a few dark spots,
		// browned edges
		private static Color Leaf(Vector2 p, float inside, Color baseColor, Color edgeColor, float edge, float vein, int seed) {
			float n = Noise(p * 5.0f, seed);
			float fine = Noise(p * 23.0f, seed + 50);
			var c = Color.Lerp(baseColor, edgeColor, Mathf.Clamp01(edge * 0.8f + (n - 0.5f) * 0.6f));
			c *= 0.88f + 0.24f * fine;
			float v = 1.0f - Mathf.Clamp01(vein);
			c = Color.Lerp(c, Color.Lerp(c, new Color(0.95f, 0.85f, 0.55f), 0.35f), v * 0.6f);
			// dark spots (tar spot, insect bites)
			float spots = Noise(p * 16.0f, seed + 90);
			if (spots > 0.84f) {
				c = Color.Lerp(c, new Color(0.12f, 0.06f, 0.03f), Mathf.Clamp01((spots - 0.84f) / 0.04f) * 0.7f);
			}
			// dried brown rim
			c = Color.Lerp(c, new Color(0.3f, 0.17f, 0.08f), Mathf.Pow(edge, 4.0f) * 0.55f);
			c.a = inside;
			return c;
		}

		// cherry petal: a broad petal with a notch at the tip, pink at the base fading to white
		private static Color Petal(Vector2 p, float aa, Color color, int seed) {
			float t = (p.y + 0.85f) / 1.7f;
			if (t < 0.0f || t > 1.0f) {
				return Clear;
			}
			float half = 0.5f * Mathf.Pow(Mathf.Sin(Mathf.PI * Mathf.Min(t * 0.62f, 0.5f)), 0.6f) * Mathf.Sqrt(Mathf.Clamp01((1.0f - t) * 6.0f + 0.4f));
			float notch = t > 0.82f ? (t - 0.82f) * 1.4f : 0.0f;
			float inside = Mathf.Clamp01((half - Mathf.Abs(p.x)) / aa) * Mathf.Clamp01((Mathf.Abs(p.x) - notch) / aa + (notch <= 0.0f ? 1.0f : 0.0f));
			if (inside <= 0.0f) {
				return Clear;
			}
			var c = Color.Lerp(new Color(0.93f, 0.55f, 0.66f), color, Mathf.SmoothStep(0.0f, 0.6f, t));
			float lines = Mathf.Abs(Frac(Mathf.Atan2(p.x, p.y + 0.9f) * 18.0f) - 0.5f);
			c *= 0.94f + 0.06f * lines + 0.06f * Noise(p * 9.0f, seed);
			c.a = inside;
			return c;
		}

		// --- weather and small things

		// a soft rounded streak; the particle itself is stretched along its fall
		private static Color RainStreak(Vector2 p) {
			float across = Mathf.Exp(-p.x * p.x / 0.08f);
			float along = Mathf.SmoothStep(1.0f, 0.55f, Mathf.Abs(p.y));
			return new Color(1.0f, 1.0f, 1.0f, across * along);
		}

		private static Color Flake(Vector2 p, int seed, float fluff) {
			float a = Mathf.Atan2(p.x, p.y);
			float r = p.magnitude / (0.75f + 0.12f * Mathf.Sin(a * 6.0f + seed) * fluff + 0.1f * (Noise(new Vector2(a * 2.0f, 0.0f), seed) - 0.5f));
			float alpha = Mathf.SmoothStep(1.0f, 0.4f, r) * (0.75f + 0.25f * Noise(p * 6.0f, seed));
			return new Color(1.0f, 1.0f, 1.0f, Mathf.Clamp01(alpha));
		}

		private static Color Soft(Vector2 p, float radius, Color color) {
			color.a = Mathf.Exp(-p.sqrMagnitude / (radius * radius * 0.5f));
			return color;
		}

		private static Color MothWings(Vector2 p, float aa) {
			var q = new Vector2(Mathf.Abs(p.x), p.y);
			float upper = Ellipse(Rotate(q - new Vector2(0.38f, 0.18f), 0.5f), 0.42f, 0.3f);
			float lower = Ellipse(Rotate(q - new Vector2(0.3f, -0.25f), -0.4f), 0.28f, 0.22f);
			float body = Ellipse(p, 0.07f, 0.5f);
			float inside = Mathf.Clamp01(Mathf.Max(upper, Mathf.Max(lower, body)) / aa);
			if (inside <= 0.0f) {
				return Clear;
			}
			var c = Color.Lerp(new Color(0.55f, 0.5f, 0.42f), new Color(0.35f, 0.3f, 0.24f), Noise(p * 8.0f, 31));
			c.a = Mathf.Clamp01(inside);
			return c;
		}

		// > 0 inside, by about the distance to the edge
		private static float Ellipse(Vector2 p, float rx, float ry) {
			return (1.0f - Mathf.Sqrt(p.x * p.x / (rx * rx) + p.y * p.y / (ry * ry))) * Mathf.Min(rx, ry);
		}

		private static Vector2 Rotate(Vector2 p, float a) {
			float c = Mathf.Cos(a), s = Mathf.Sin(a);
			return new Vector2(c * p.x - s * p.y, s * p.x + c * p.y);
		}

		private static readonly Color Clear = new(0.0f, 0.0f, 0.0f, 0.0f);

		private static float Frac(float v) {
			return v - Mathf.Floor(v);
		}

		private static float Noise(Vector2 p, int seed) {
			return Mathf.PerlinNoise(p.x + seed * 17.13f, p.y + seed * 7.71f);
		}
	}
}

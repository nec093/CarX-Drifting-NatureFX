using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using NatureFX.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Nature {
	// Distant forest (docs/map-authoring.md 5.5): nocol_nfx_forest_<conifer|broadleaf|mixed> surfaces (MeshFilter only)
	// are covered with trees as crossed cards (impostors) out to 2 km, so a map can fill whole mountainsides that it
	// does not model. The card picture is painted by NatureFX (a cedar, a broadleaf crown); they draw with
	// NatureFX/Foliage, so they sway, take the season's colours and drop leaves in winter like the map's own trees.
	// 64 m cells; far cells draw a quarter, then half of their trees; shadows near the camera only.
	public class ForestRenderer {
		private const float CellSize = 64.0f;
		private const float MaxDistance = 2000.0f;
		private const float ShadowDistance = 200.0f;
		private const int BuildsPerCheck = 8;

		private class Area {
			public int Kind;          // 0 conifer, 1 broadleaf, 2 mixed
			public float Density;     // trees per 100 m²
			public float Height;      // metres, the tallest
		}

		private struct Tri {
			public Vector3 A, B, C;
			public int Area;
		}

		private class Cell {
			public Vector2Int Key;
			public Vector3 Center;
			public readonly List<Tri> Tris = new();
			public Mesh Mesh;
			public float Distance;
		}

		private readonly List<Area> areas_ = new();
		private readonly Dictionary<Vector2Int, Cell> cells_ = new();
		private readonly List<Cell> visible_ = new();
		private Material conifer_;
		private Material broadleaf_;
		private Texture2D atlas_;
		private float nextCull_;
		private int trees_;

		public int AreaCount => areas_.Count;

		public void Load(MarkerScanner markers, ShaderBundle bundle) {
			Clear();
			var shader = bundle.Get("NatureFX/Foliage");
			if (shader == null) {
				return;
			}
			int unreadable = 0;
			foreach (var m in markers.OfKind("forest")) {
				if (m.Surface == null || m.Transform == null) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: a forest marker needs a mesh (MeshFilter) to grow on");
					continue;
				}
				if (!m.Surface.isReadable) {
					++unreadable;
					continue;
				}
				string preset = (m.Preset ?? "").ToLowerInvariant();
				var area = new Area {
					Kind = preset == "broadleaf" ? 1 : preset == "mixed" ? 2 : 0,
					Density = Mathf.Max(0.01f, m.GetFloat("density", 2.0f)),
					Height = Mathf.Max(2.0f, m.GetFloat("height", preset == "broadleaf" ? 14.0f : 22.0f))
				};
				areas_.Add(area);
				AddSurface(m.Surface, m.Transform.localToWorldMatrix, areas_.Count - 1);
			}
			if (areas_.Count == 0) {
				return;
			}
			atlas_ = Paint();
			// null when the bundle carries no variant list for the shader (the materials then take Unity's fallback)
			var keywords = bundle.BestVariant(shader.name, new[] { "_ALPHATEST_ON", "_DOUBLESIDED_ON", "_DISABLE_SSR_TRANSPARENT", "_NORMALMAP_TANGENT_SPACE" })
			               ?? new HashSet<string>();
			conifer_ = MakeMaterial(shader, keywords, Color.white, 1.0f);
			broadleaf_ = MakeMaterial(shader, keywords, Color.white, 2.0f);
			Kino.Log.Info($"[NatureFX] Forest: {areas_.Count} areas in {cells_.Count} cells" + (unreadable > 0 ? $"; {unreadable} surfaces not readable" : ""));
		}

		private Material MakeMaterial(Shader shader, HashSet<string> keywords, Color color, float type) {
			var m = new Material(shader) { name = "NatureFX.Forest " + type };
			foreach (string k in keywords) {
				m.EnableKeyword(k);
			}
			m.SetTexture("_BaseColorMap", atlas_);
			m.SetColor("_BaseColor", color);
			m.SetFloat("_Smoothness", 0.15f);
			m.SetFloat("_AlphaCutoffEnable", 1.0f);
			m.SetFloat("_AlphaCutoff", 0.45f);
			m.SetFloat("_DoubleSidedEnable", 1.0f);
			m.SetFloat("_CullMode", 0.0f);
			m.SetFloat("_CullModeForward", 0.0f);
			m.SetFloat("_ZTestGBuffer", (float)CompareFunction.Equal);
			m.SetFloat("_ZTestDepthEqualForOpaque", (float)CompareFunction.Equal);
			m.renderQueue = (int)RenderQueue.AlphaTest;
			m.SetVector("_NFX_Foliage", new Vector4(type, type == 1.0f ? 0.25f : 0.6f, 0.35f, 0.0f));
			m.SetVector("_NFX_Foliage2", new Vector4(0.0f, 0.0f, 0.0f, 1.0f)); // the cards' vertex colours drive the sway
			return m;
		}

		private void AddSurface(Mesh mesh, Matrix4x4 toWorld, int area) {
			var v = mesh.vertices;
			var t = mesh.triangles;
			for (int i = 0; i + 2 < t.Length; i += 3) {
				var tri = new Tri { A = toWorld.MultiplyPoint3x4(v[t[i]]), B = toWorld.MultiplyPoint3x4(v[t[i + 1]]), C = toWorld.MultiplyPoint3x4(v[t[i + 2]]), Area = area };
				var min = Vector3.Min(tri.A, Vector3.Min(tri.B, tri.C));
				var max = Vector3.Max(tri.A, Vector3.Max(tri.B, tri.C));
				for (int x = Mathf.FloorToInt(min.x / CellSize); x <= Mathf.FloorToInt(max.x / CellSize); ++x) {
					for (int z = Mathf.FloorToInt(min.z / CellSize); z <= Mathf.FloorToInt(max.z / CellSize); ++z) {
						var key = new Vector2Int(x, z);
						if (!cells_.TryGetValue(key, out var cell)) {
							cell = new Cell { Key = key, Center = new Vector3((x + 0.5f) * CellSize, (min.y + max.y) * 0.5f, (z + 0.5f) * CellSize) };
							cells_[key] = cell;
						}
						cell.Tris.Add(tri);
					}
				}
			}
		}

		public void Clear() {
			foreach (var c in cells_.Values) {
				if (c.Mesh != null) {
					Object.Destroy(c.Mesh);
				}
			}
			cells_.Clear();
			visible_.Clear();
			areas_.Clear();
			if (conifer_ != null) {
				Object.Destroy(conifer_);
			}
			if (broadleaf_ != null) {
				Object.Destroy(broadleaf_);
			}
			if (atlas_ != null) {
				Object.Destroy(atlas_);
			}
			conifer_ = broadleaf_ = null;
			atlas_ = null;
			trees_ = 0;
		}

		public void Destroy() {
			Clear();
		}

		public void Update(bool on, Vector3 camera) {
			if (!on || cells_.Count == 0) {
				return;
			}
			if (Time.unscaledTime >= nextCull_) {
				nextCull_ = Time.unscaledTime + 0.5f;
				visible_.Clear();
				var toBuild = new List<Cell>();
				foreach (var c in cells_.Values) {
					c.Distance = Vector3.Distance(new Vector3(camera.x, c.Center.y, camera.z), c.Center) - CellSize * 0.7f;
					if (c.Distance > MaxDistance) {
						continue;
					}
					if (c.Mesh != null) {
						visible_.Add(c);
					} else {
						toBuild.Add(c);
					}
				}
				// nearest first, a few per check
				toBuild.Sort((a, b) => a.Distance.CompareTo(b.Distance));
				for (int i = 0; i < toBuild.Count && i < BuildsPerCheck; ++i) {
					Build(toBuild[i]);
					visible_.Add(toBuild[i]);
				}
			}
			foreach (var c in visible_) {
				// sub meshes: 0 conifer quarter, 1 conifer quarter, 2 conifer half, 3..5 broadleaf the same
				int parts = c.Distance < 500.0f ? 3 : c.Distance < 1000.0f ? 2 : 1;
				var shadows = c.Distance < ShadowDistance ? ShadowCastingMode.On : ShadowCastingMode.Off;
				for (int sub = 0; sub < c.Mesh.subMeshCount; ++sub) {
					if (sub % 3 >= parts || c.Mesh.GetIndexCount(sub) == 0) {
						continue;
					}
					var rp = new RenderParams(sub < 3 ? conifer_ : broadleaf_) { shadowCastingMode = shadows, receiveShadows = true, worldBounds = c.Mesh.bounds };
					rp.renderingLayerMask |= NatureFX.Rendering.OwnLighting.UntracedBit; // not traced: HDRP lights it under the own BSDF
					Graphics.RenderMesh(rp, c.Mesh, sub, Matrix4x4.identity);
				}
			}
		}

		// trees on the cell's part of the surfaces: two crossed cards each, shuffled into LOD parts
		private void Build(Cell c) {
			var rng = new System.Random(c.Key.x * 92837111 ^ c.Key.y * 689287499);
			float x0 = c.Key.x * CellSize;
			float z0 = c.Key.y * CellSize;
			var trees = new List<(Vector3 Pos, float Height, bool Broad)>();
			foreach (var t in c.Tris) {
				var area = areas_[t.Area];
				var n = Vector3.Cross(t.B - t.A, t.C - t.A);
				float size = n.magnitude * 0.5f;
				if (Mathf.Abs(n.normalized.y) < 0.4f) {
					continue; // cliffs
				}
				float expected = size * area.Density / 100.0f;
				int count = Mathf.FloorToInt(expected) + (rng.NextDouble() < expected - Mathf.Floor(expected) ? 1 : 0);
				for (int i = 0; i < count; ++i) {
					float u = (float)rng.NextDouble();
					float v = (float)rng.NextDouble();
					if (u + v > 1.0f) {
						u = 1.0f - u;
						v = 1.0f - v;
					}
					var p = t.A + (t.B - t.A) * u + (t.C - t.A) * v;
					if (p.x < x0 || p.x >= x0 + CellSize || p.z < z0 || p.z >= z0 + CellSize) {
						continue;
					}
					bool broad = area.Kind == 1 || (area.Kind == 2 && rng.NextDouble() < 0.5);
					trees.Add((p, area.Height * (0.6f + (float)rng.NextDouble() * 0.4f), broad));
				}
			}
			if (trees.Count == 0) {
				c.Mesh = new Mesh(); // nothing here; do not try again
				c.Mesh.subMeshCount = 0;
				return;
			}
			for (int i = trees.Count - 1; i > 0; --i) {
				int j = rng.Next(i + 1);
				(trees[i], trees[j]) = (trees[j], trees[i]);
			}
			var positions = new List<Vector3>();
			var normals = new List<Vector3>();
			var uvs = new List<Vector2>();
			var colors = new List<Color>();
			var subs = new List<int>[6];
			for (int i = 0; i < 6; ++i) {
				subs[i] = new List<int>();
			}
			var conifers = trees.FindAll(t => !t.Broad);
			var broads = trees.FindAll(t => t.Broad);
			AddTrees(conifers, 0, rng, positions, normals, uvs, colors, subs);
			AddTrees(broads, 3, rng, positions, normals, uvs, colors, subs);
			var mesh = new Mesh { name = $"NatureFX.Forest {c.Key}", indexFormat = IndexFormat.UInt32 };
			mesh.SetVertices(positions);
			mesh.SetNormals(normals);
			mesh.SetUVs(0, uvs);
			mesh.SetColors(colors);
			mesh.subMeshCount = 6;
			for (int i = 0; i < 6; ++i) {
				mesh.SetTriangles(subs[i], i, false);
			}
			mesh.RecalculateBounds();
			mesh.UploadMeshData(true);
			c.Mesh = mesh;
			trees_ += trees.Count;
		}

		private static void AddTrees(List<(Vector3 Pos, float Height, bool Broad)> trees, int firstSub, System.Random rng, List<Vector3> positions,
		                             List<Vector3> normals, List<Vector2> uvs, List<Color> colors, List<int>[] subs) {
			int[] cut = { 0, trees.Count / 4, trees.Count / 2, trees.Count };
			for (int part = 0; part < 3; ++part) {
				for (int i = cut[part]; i < cut[part + 1]; ++i) {
					var (p, h, broad) = trees[i];
					// the atlas has two conifers (cedar, cypress) and two broadleaf crowns (round, vase shaped)
					int tile = (broad ? 2 : 0) + (rng.NextDouble() < 0.5 ? 0 : 1);
					float w = h * (broad ? (tile == 2 ? 0.85f : 0.7f) : (tile == 0 ? 0.42f : 0.5f));
					float yaw = (float)rng.NextDouble() * Mathf.PI;
					float u0 = tile * 0.25f;
					// mirrored half the time, so neighbours of one type do not look alike
					bool flip = rng.NextDouble() < 0.5;
					float phase = (float)rng.NextDouble();
					var crown = p + Vector3.up * h * (broad ? 0.6f : 0.5f);
					// three cards 60 degrees apart: from any side the tree is as wide as it is, with no thin edge-on view
					for (int card = 0; card < 3; ++card) {
						float a = yaw + card * Mathf.PI / 3.0f;
						var side = new Vector3(Mathf.Cos(a), 0.0f, Mathf.Sin(a)) * (w * 0.5f);
						int b = positions.Count;
						var basePos = p - Vector3.up * 0.3f;
						var corners = new[] { basePos - side, basePos + side, basePos - side + Vector3.up * h, basePos + side + Vector3.up * h };
						foreach (var c in corners) {
							positions.Add(c);
							// a rounded crown: the normal points away from the crown's middle and up, so the sunny side
							// is light and the side away from the sun and the bottom are in shade, as on a real tree
							var outward = c - crown;
							outward.y *= 0.6f;
							normals.Add((outward.normalized * 0.7f + Vector3.up * 0.6f).normalized);
						}
						float ua = flip ? u0 + 0.25f : u0, ub = flip ? u0 : u0 + 0.25f;
						uvs.Add(new Vector2(ua, 0.0f));
						uvs.Add(new Vector2(ub, 0.0f));
						uvs.Add(new Vector2(ua, 1.0f));
						uvs.Add(new Vector2(ub, 1.0f));
						// vertex colours as the map's (weight, phase, stiffness, open sky): the tops sway a little
						colors.Add(new Color(0.0f, phase, 0.6f, 1.0f));
						colors.Add(new Color(0.0f, phase, 0.6f, 1.0f));
						colors.Add(new Color(0.6f, phase, 0.6f, 1.0f));
						colors.Add(new Color(0.6f, phase, 0.6f, 1.0f));
						subs[firstSub + part].AddRange(new[] { b, b + 2, b + 1, b + 1, b + 2, b + 3 });
					}
				}
			}
		}

		// Four trees side by side, 256 x 1024 each, painted as thousands of small sprays of needles or leaves, each
		// shaded as part of a rounded crown lit from above (light on top and on the sunny side, dark inside and below,
		// sky showing through the gaps), with trunks and the branches showing between the sprays:
		//   0 sugi (Japanese cedar): tall and narrow, drooping tiers;  1 hinoki / pine: looser, irregular layers;
		//   2 konara (oak): a broad round crown;                       3 keyaki (zelkova): a vase of branches.
		// The leaves' colour is painted in (a dark cedar green, a lighter broadleaf green; the material stays white so the
		// bark stays brown), with a slight warm tip / cool shade.
		private const int TileW = 256;
		private const int TileH = 1024;
		// each tree's card width over its height (AddTrees), so the sprays are painted round on the card, not in pixels
		private static readonly float[] TileAspect = { 0.42f, 0.5f, 0.85f, 0.7f };
		private static float aspect_ = 1.0f;

		private static Texture2D Paint() {
			int w = TileW * 4;
			var tex = new Texture2D(w, TileH, TextureFormat.RGBA32, true) { name = "NatureFX.ForestAtlas", wrapMode = TextureWrapMode.Clamp, anisoLevel = 4 };
			var px = new Color[w * TileH];
			var depth = new float[w * TileH];
			for (int i = 0; i < depth.Length; ++i) {
				depth[i] = float.MaxValue;
			}
			for (int tile = 0; tile < 4; ++tile) {
				PaintTree(px, depth, tile, new System.Random(11 + tile * 7));
			}
			// give the cut out pixels their neighbours' colour, so the mips do not darken the edges
			for (int pass = 0; pass < 4; ++pass) {
				for (int y = 1; y < TileH - 1; ++y) {
					for (int x = 1; x < w - 1; ++x) {
						int i = y * w + x;
						if (px[i].a > 0.0f || px[i].r > 0.0f) {
							continue;
						}
						var n = px[i - 1].r > 0.0f ? px[i - 1] : px[i + 1].r > 0.0f ? px[i + 1] : px[i - w].r > 0.0f ? px[i - w] : px[i + w];
						px[i] = new Color(n.r, n.g, n.b, 0.0f);
					}
				}
			}
			tex.SetPixels(px);
			tex.Apply(true, true);
			return tex;
		}

		// the light the sprays are shaded with: from above, a little to one side and towards the viewer
		private static readonly Vector3 PaintLight = new Vector3(-0.45f, 0.75f, 0.5f).normalized;

		private static void PaintTree(Color[] px, float[] depth, int tile, System.Random rng) {
			int ox = tile * TileW;
			bool conifer = tile < 2;
			// a pixel is this much wider on the card than it is tall
			aspect_ = TileAspect[tile] * TileH / TileW;
			// crown shape: half width (of the tile, 0..0.5) at a height v (0 ground .. 1 top)
			System.Func<float, float> width = tile switch {
				0 => v => v < 0.1f ? 0.0f : 0.46f * Mathf.Pow(Mathf.Clamp01((1.0f - v) / 0.9f), 0.85f),
				1 => v => v < 0.18f ? 0.0f : 0.44f * Mathf.Pow(Mathf.Clamp01((1.0f - v) / 0.82f), 0.6f) * (0.8f + 0.2f * Mathf.Sin(v * 23.0f)),
				2 => v => v < 0.3f ? 0.0f : 0.48f * Mathf.Sqrt(Mathf.Clamp01(1.0f - Mathf.Pow((v - 0.62f) / 0.36f, 2.0f))),
				_ => v => v < 0.28f ? 0.0f : 0.47f * Mathf.Sqrt(Mathf.Clamp01(1.0f - Mathf.Pow((v - 0.66f) / 0.36f, 2.0f))) * Mathf.Lerp(0.55f, 1.0f, Mathf.Clamp01((v - 0.28f) / 0.4f))
			};
			float centerV = conifer ? 0.5f : 0.62f;

			// trunk (and for the broadleaves the main branches), drawn first, behind the sprays
			float trunkTop = conifer ? 0.97f : 0.55f;
			float trunkW = conifer ? 0.025f : 0.035f;
			for (int y = 0; y < TileH * trunkTop; ++y) {
				float v = (float)y / TileH;
				float half = trunkW * (1.0f - v * 0.6f);
				for (int x = 0; x < TileW; ++x) {
					float u = (x + 0.5f) / TileW - 0.5f;
					if (Mathf.Abs(u) < half) {
						float bark = 0.22f + 0.1f * Mathf.PerlinNoise(x * 0.4f, y * 0.05f) - (u < 0.0f ? 0.0f : 0.05f);
						Put(px, depth, ox + x, y, new Color(bark * 1.05f, bark * 0.95f, bark * 0.85f, 1.0f), 10.0f);
					}
				}
			}
			if (!conifer) {
				int branches = tile == 3 ? 9 : 6;
				for (int b = 0; b < branches; ++b) {
					float start = 0.3f + (float)rng.NextDouble() * 0.2f;
					float angle = (b / (float)(branches - 1) - 0.5f) * (tile == 3 ? 1.3f : 2.2f);
					float len = 0.3f + (float)rng.NextDouble() * 0.25f;
					for (float t = 0.0f; t < 1.0f; t += 0.002f) {
						float u = Mathf.Sin(angle) * len * t * 0.9f;
						float v = start + Mathf.Cos(angle) * len * t;
						float r = Mathf.Lerp(0.018f, 0.004f, t);
						Disc(px, depth, ox, u, v, r, new Color(0.24f, 0.22f, 0.19f, 1.0f), 9.0f);
					}
				}
			}

			// sprays: small clusters of needles or leaves filling the crown; the nearer (front) ones drawn over the rest
			int sprays = conifer ? 8000 : 4600;
			var leafColor = conifer ? new Color(0.2f, 0.32f, 0.17f) : new Color(0.32f, 0.46f, 0.19f);
			for (int k = 0; k < sprays; ++k) {
				float v = (float)rng.NextDouble();
				float half = width(v);
				if (half <= 0.0f) {
					continue;
				}
				float u = ((float)rng.NextDouble() * 2.0f - 1.0f) * half;
				// a point inside the rounded crown, with a depth in front of or behind the card
				float across = u / Mathf.Max(half, 1e-3f);
				float front = Mathf.Sqrt(Mathf.Max(0.0f, 1.0f - across * across)) * ((float)rng.NextDouble() * 2.0f - 1.0f);
				// clumps: sprays gather into lumps with gaps between them
				float lump = Mathf.PerlinNoise(u * TileAspect[tile] * (conifer ? 40.0f : 14.0f) + tile * 3.1f, v * (conifer ? 40.0f : 14.0f));
				if (lump < (conifer ? 0.3f : 0.4f)) {
					continue;
				}
				// the spray's normal: away from the crown's middle, and up for the tops of the lumps
				var n = new Vector3(across, (v - centerV) * 1.6f + (lump - 0.5f) * 0.8f, front).normalized;
				float lit = Mathf.Clamp01(Vector3.Dot(n, PaintLight));
				// inside the crown little light reaches: darker the deeper (further behind) the spray
				float inside = Mathf.Lerp(0.45f, 1.0f, (front + 1.0f) * 0.5f) * Mathf.Lerp(0.6f, 1.0f, lump);
				float shade = (0.12f + 0.88f * lit) * inside * (0.85f + 0.3f * (float)rng.NextDouble());
				// warm light tips, cool shade
				// the shade is a grey as painted (sRGB), the leaf colour a linear tint as the material's was; the atlas is
				// sRGB, so the product goes back to gamma
				float g = Mathf.GammaToLinearSpace(shade);
				var c = new Color(leafColor.r * g * (1.0f + lit * 0.12f), leafColor.g * g * (1.0f + lit * 0.06f), leafColor.b * (g * (1.0f - lit * 0.1f) + (1.0f - lit) * 0.01f), 1.0f).gamma;
				float size = conifer ? 0.012f + 0.01f * (float)rng.NextDouble() : 0.016f + 0.014f * (float)rng.NextDouble();
				if (tile == 0) {
					// cedar: sprays hang down in tiers
					Drop(px, depth, ox, u, v, size, c, -front);
				} else {
					Disc(px, depth, ox, u, v, size, c, -front, conifer ? 0.35f : 0.55f);
				}
			}
		}

		private static void Put(Color[] px, float[] depth, int x, int y, Color c, float z) {
			int i = y * TileW * 4 + x;
			if (z <= depth[i]) {
				depth[i] = z;
				px[i] = c;
			}
		}

		// a small ragged disc of foliage (fill: how solid; needles leave more gaps)
		private static void Disc(Color[] px, float[] depth, int ox, float u, float v, float r, Color c, float z, float fill = 1.0f) {
			int cx = ox + Mathf.RoundToInt((u + 0.5f) * TileW);
			int cy = Mathf.RoundToInt(v * TileH);
			int rad = Mathf.Max(1, Mathf.CeilToInt(r * TileW));
			int radY = Mathf.Max(1, Mathf.CeilToInt(r * TileW * aspect_));
			for (int y = cy - radY; y <= cy + radY; ++y) {
				for (int x = cx - rad; x <= cx + rad; ++x) {
					if (x < ox + 1 || x >= ox + TileW - 1 || y < 1 || y >= TileH - 1) {
						continue;
					}
					float dx = (x - cx) / (float)rad, dy = (y - cy) / (float)radY;
					float d = dx * dx + dy * dy;
					if (d > 1.0f || (fill < 1.0f && Hash(x, y) > fill + (1.0f - d) * 0.5f)) {
						continue;
					}
					// darker towards the spray's lower edge
					float k = 1.0f - Mathf.Max(0.0f, -dy) * 0.25f;
					Put(px, depth, x, y, new Color(c.r * k, c.g * k, c.b * k, 1.0f), z);
				}
			}
		}

		// a drooping cedar spray: wider on top, hanging down
		private static void Drop(Color[] px, float[] depth, int ox, float u, float v, float r, Color c, float z) {
			for (int s = 0; s < 4; ++s) {
				var k = new Color(c.r * (1.0f - s * 0.08f), c.g * (1.0f - s * 0.08f), c.b * (1.0f - s * 0.06f), 1.0f);
				Disc(px, depth, ox, u + (s - 1.5f) * r * 0.35f, v - s * r * 0.45f * aspect_ * TileW / TileH, r * (1.0f - s * 0.18f), k, z + s * 0.001f, 0.45f);
			}
		}

		private static float Hash(int x, int y) {
			unchecked {
				uint h = (uint)(x * 374761393 + y * 668265263);
				h = (h ^ (h >> 13)) * 1274126177u;
				return (h & 0xffff) / 65535.0f;
			}
		}

		public string Describe() {
			int built = 0;
			foreach (var c in cells_.Values) {
				built += c.Mesh != null ? 1 : 0;
			}
			return string.Format(CultureInfo.InvariantCulture, "{0} areas, {1} of {2} cells built, {3} trees", areas_.Count, built, cells_.Count, trees_);
		}
	}
}

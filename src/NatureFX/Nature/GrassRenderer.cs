using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using NatureFX.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Nature {
	// 3D grass (docs/map-authoring.md 5.1): blades grown on nocol_nfx_grass_<preset> surfaces (MeshFilter only, so
	// nothing shows without NatureFX) and, with [grass] auto = true, on readable grass_ ground meshes. The ground is
	// cut into 8 m cells; a cell's blades become one mesh when the camera comes within the grass distance and go again
	// when it leaves. They draw with NatureFX/Foliage (lit and shadowed by HDRP), whose vertex code sways them in the
	// wind, presses them down under tyres (the track map's compression) and buries them under deep snow
	// (NFX_Foliage.hlsl). Blades are shuffled, so further cells draw only the first part of their mesh: thinner with
	// distance, without popping whole cells.
	public class GrassRenderer {
		private const float CellSize = 8.0f;
		private const int BuildsPerFrame = 2;
		private const int MaxBladesPerCell = 60000;
		private const float CullInterval = 0.25f;

		private class Preset {
			public float Density;    // blades per m² at GrassDensity 1
			public float MinHeight, MaxHeight;
			public float Width;
			public Color Color;
			public float Type;       // _NFX_Foliage.x: 4 grass, 3 bamboo
			public bool Plume;
			public float Distance;   // share of the grass distance it is drawn to
		}

		private static readonly Dictionary<string, Preset> Presets = new(System.StringComparer.OrdinalIgnoreCase) {
			["lawn"] = new Preset { Density = 180, MinHeight = 0.05f, MaxHeight = 0.1f, Width = 0.012f, Color = new Color(0.22f, 0.38f, 0.1f), Type = 4, Distance = 0.5f },
			["wild"] = new Preset { Density = 45, MinHeight = 0.2f, MaxHeight = 0.5f, Width = 0.025f, Color = new Color(0.3f, 0.4f, 0.15f), Type = 4, Distance = 1.0f },
			["susuki"] = new Preset { Density = 18, MinHeight = 0.8f, MaxHeight = 1.5f, Width = 0.02f, Color = new Color(0.42f, 0.46f, 0.24f), Type = 4, Plume = true, Distance = 1.0f },
			["sasa"] = new Preset { Density = 30, MinHeight = 0.4f, MaxHeight = 0.8f, Width = 0.07f, Color = new Color(0.15f, 0.32f, 0.08f), Type = 3, Distance = 1.0f },
			["moss"] = new Preset { Density = 400, MinHeight = 0.01f, MaxHeight = 0.02f, Width = 0.01f, Color = new Color(0.12f, 0.22f, 0.06f), Type = 4, Distance = 0.3f }
		};

		private class Area {
			public Preset Preset;
			public float Density;
			public float Height;
			public Material Material;
		}

		private struct Tri {
			public Vector3 A, B, C;
			public int Area;
		}

		private class Cell {
			public Vector2Int Key;
			public Vector3 Center;
			public readonly List<Tri> Tris = new();
			public float Distance;       // furthest a preset in it is drawn (m, at GrassDistance 1)
			public Mesh Mesh;
			public readonly List<(Material Material, int SubMesh)> Parts = new();
			public float CameraDistance;
			public int Blades;
		}

		private readonly List<Area> areas_ = new();
		private readonly Dictionary<Vector2Int, Cell> cells_ = new();
		private readonly List<Cell> visible_ = new();
		private readonly List<Cell> toBuild_ = new();
		private readonly Dictionary<string, Material> materials_ = new();
		private Shader shader_;
		private HashSet<string> keywords_;
		private float density_ = -1.0f;
		private float nextCull_;
		private int blades_;

		public int AreaCount => areas_.Count;

		public void Load(MapProfile profile, MarkerScanner markers, ShaderBundle bundle, float density) {
			Clear();
			shader_ = bundle.Get("NatureFX/Foliage");
			if (shader_ == null) {
				return;
			}
			keywords_ = bundle.BestVariant(shader_.name, new[] { "_DOUBLESIDED_ON", "_DISABLE_SSR_TRANSPARENT", "_NORMALMAP_TANGENT_SPACE" });
			density_ = density;
			int unreadable = 0;
			foreach (var m in markers.OfKind("grass")) {
				if (m.Surface == null || m.Transform == null) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: a grass marker needs a mesh (MeshFilter) to grow on");
					continue;
				}
				if (!AddSurface(m.Surface, m.Transform.localToWorldMatrix, MakeArea(m.Preset, m.Params))) {
					++unreadable;
				}
			}
			if (profile.Loaded && string.Equals(profile.Get("grass", "auto"), "true", System.StringComparison.OrdinalIgnoreCase)) {
				string preset = profile.Get("grass", "preset") ?? "wild";
				var area = MakeArea(preset, null);
				foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None)) {
					if (mf.sharedMesh != null && mf.gameObject.name.StartsWith("grass_", System.StringComparison.OrdinalIgnoreCase)) {
						var r = mf.GetComponent<MeshRenderer>();
						if (r != null && r.isPartOfStaticBatch) {
							++unreadable; // the batch's merged mesh holds the whole map
							continue;
						}
						if (!AddSurface(mf.sharedMesh, mf.transform.localToWorldMatrix, area)) {
							++unreadable;
						}
					}
				}
			}
			if (cells_.Count > 0 || unreadable > 0) {
				Kino.Log.Info($"[NatureFX] 3D grass: {areas_.Count} areas in {cells_.Count} cells" +
				              (unreadable > 0 ? $"; {unreadable} surfaces skipped (mesh not readable or statically batched)" : ""));
			}
		}

		private Area MakeArea(string presetName, Dictionary<string, string> keys) {
			if (!Presets.TryGetValue(string.IsNullOrEmpty(presetName) ? "wild" : presetName, out var preset)) {
				Kino.Log.Warning($"[NatureFX] Unknown grass preset '{presetName}' (lawn, wild, susuki, sasa, moss); using wild");
				preset = Presets["wild"];
			}
			float Get(string key, float fallback) {
				return keys != null && keys.TryGetValue(key, out string v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
			}
			float hue = Get("hue", 0.0f);
			var area = new Area {
				Preset = preset,
				Density = Mathf.Max(0.0f, Get("density", preset.Density)),
				Height = Mathf.Max(0.05f, Get("height", 1.0f)),
				Material = MaterialFor(presetName ?? "wild", preset, hue)
			};
			areas_.Add(area);
			return area;
		}

		private Material MaterialFor(string name, Preset preset, float hue) {
			string key = name + "|" + hue.ToString(CultureInfo.InvariantCulture);
			if (materials_.TryGetValue(key, out var m)) {
				return m;
			}
			m = new Material(shader_) { name = "NatureFX.Grass " + key };
			foreach (string k in keywords_ ?? new HashSet<string>()) {
				m.EnableKeyword(k);
			}
			m.SetColor("_BaseColor", preset.Color);
			m.SetFloat("_Smoothness", 0.25f);
			m.SetFloat("_Metallic", 0.0f);
			m.SetFloat("_DoubleSidedEnable", 1.0f);
			m.SetFloat("_CullMode", 0.0f);
			m.SetFloat("_CullModeForward", 0.0f);
			m.SetFloat("_AlphaCutoff", 0.0f);
			m.SetVector("_NFX_Foliage", new Vector4(preset.Type, 0.5f, 0.3f, 0.0f));
			m.SetVector("_NFX_Foliage2", new Vector4(1.0f, hue, preset.Plume ? 1.0f : 0.0f, 0.0f));
			m.enableInstancing = false;
			materials_[key] = m;
			return m;
		}

		// the surface's triangles into the cells their centres fall in
		private bool AddSurface(Mesh mesh, Matrix4x4 toWorld, Area area) {
			if (!mesh.isReadable) {
				return false;
			}
			var vertices = mesh.vertices;
			var triangles = mesh.triangles;
			int areaIndex = areas_.IndexOf(area);
			for (int i = 0; i + 2 < triangles.Length; i += 3) {
				var tri = new Tri {
					A = toWorld.MultiplyPoint3x4(vertices[triangles[i]]),
					B = toWorld.MultiplyPoint3x4(vertices[triangles[i + 1]]),
					C = toWorld.MultiplyPoint3x4(vertices[triangles[i + 2]]),
					Area = areaIndex
				};
				// big triangles go into every cell they cover, cut to its square when the blades are placed
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
						cell.Distance = Mathf.Max(cell.Distance, area.Preset.Distance);
					}
				}
			}
			return true;
		}

		public void Clear() {
			foreach (var c in cells_.Values) {
				Drop(c);
			}
			cells_.Clear();
			visible_.Clear();
			toBuild_.Clear();
			areas_.Clear();
			foreach (var m in materials_.Values) {
				Object.Destroy(m);
			}
			materials_.Clear();
			blades_ = 0;
		}

		public void Destroy() {
			Clear();
		}

		public void Update(bool on, ISettings s, Vector3 camera) {
			if (!on || cells_.Count == 0) {
				return;
			}
			if (!Mathf.Approximately(density_, s.GrassDensity)) {
				// another density: every built cell again
				density_ = s.GrassDensity;
				foreach (var c in cells_.Values) {
					Drop(c);
				}
			}
			float distance = Mathf.Max(5.0f, s.GrassDistance);

			if (Time.unscaledTime >= nextCull_) {
				nextCull_ = Time.unscaledTime + CullInterval;
				visible_.Clear();
				toBuild_.Clear();
				foreach (var c in cells_.Values) {
					c.CameraDistance = Vector3.Distance(new Vector3(camera.x, c.Center.y, camera.z), c.Center) - CellSize * 0.7f;
					float reach = distance * c.Distance;
					if (c.CameraDistance <= reach) {
						if (c.Mesh != null) {
							visible_.Add(c);
						} else {
							toBuild_.Add(c);
						}
					} else if (c.Mesh != null && c.CameraDistance > reach + 15.0f) {
						Drop(c);
					}
				}
				toBuild_.Sort((a, b) => a.CameraDistance.CompareTo(b.CameraDistance));
			}

			int built = 0;
			while (built < BuildsPerFrame && toBuild_.Count > 0) {
				var c = toBuild_[0];
				toBuild_.RemoveAt(0);
				Build(c);
				if (c.Mesh != null) {
					visible_.Add(c);
				}
				++built;
			}

			foreach (var c in visible_) {
				if (c.Mesh == null) {
					continue;
				}
				// thinner with distance: a quarter of the blades always, half nearer, all of them close by
				float share = c.CameraDistance / (distance * c.Distance);
				int parts = share < 0.35f ? 3 : share < 0.65f ? 2 : 1;
				var shadows = c.CameraDistance < 25.0f ? ShadowCastingMode.On : ShadowCastingMode.Off;
				foreach (var (material, subMesh) in c.Parts) {
					if (subMesh % 3 >= parts) {
						continue;
					}
					var rp = new RenderParams(material) { shadowCastingMode = shadows, receiveShadows = true, worldBounds = c.Mesh.bounds };
					rp.renderingLayerMask |= NatureFX.Rendering.OwnLighting.UntracedBit; // not traced: HDRP lights it under the own BSDF
					Graphics.RenderMesh(rp, c.Mesh, subMesh, Matrix4x4.identity);
				}
			}
		}

		private void Drop(Cell c) {
			if (c.Mesh != null) {
				Object.Destroy(c.Mesh);
				c.Mesh = null;
			}
			c.Parts.Clear();
			blades_ -= c.Blades;
			c.Blades = 0;
		}

		// Blades for one cell: per area a shuffled list split into three sub meshes (25 %, 25 %, 50 %)
		private void Build(Cell c) {
			var rng = new System.Random(c.Key.x * 73856093 ^ c.Key.y * 19349663);
			float x0 = c.Key.x * CellSize;
			float z0 = c.Key.y * CellSize;
			var byArea = new Dictionary<int, List<Vector4>>(); // xyz root, w the blade's seed
			var normals = new Dictionary<int, List<Vector3>>();
			int total = 0;
			foreach (var t in c.Tris) {
				var area = areas_[t.Area];
				float triArea = Vector3.Cross(t.B - t.A, t.C - t.A).magnitude * 0.5f;
				var n = Vector3.Cross(t.B - t.A, t.C - t.A).normalized;
				if (n.y < 0.0f) {
					n = -n;
				}
				if (n.y < 0.5f) {
					continue; // no grass on steep faces
				}
				float expected = triArea * area.Density * Mathf.Max(0.0f, density_);
				int count = Mathf.FloorToInt(expected) + (rng.NextDouble() < expected - Mathf.Floor(expected) ? 1 : 0);
				if (!byArea.TryGetValue(t.Area, out var list)) {
					list = new List<Vector4>();
					byArea[t.Area] = list;
					normals[t.Area] = new List<Vector3>();
				}
				for (int i = 0; i < count && total < MaxBladesPerCell; ++i) {
					float u = (float)rng.NextDouble();
					float v = (float)rng.NextDouble();
					if (u + v > 1.0f) {
						u = 1.0f - u;
						v = 1.0f - v;
					}
					var p = t.A + (t.B - t.A) * u + (t.C - t.A) * v;
					// a triangle in several cells: each keeps only the blades on its own square
					if (p.x < x0 || p.x >= x0 + CellSize || p.z < z0 || p.z >= z0 + CellSize) {
						continue;
					}
					list.Add(new Vector4(p.x, p.y, p.z, (float)rng.NextDouble()));
					normals[t.Area].Add(n);
					++total;
				}
			}
			if (total == 0) {
				return;
			}

			var positions = new List<Vector3>(total * 3);
			var vertexNormals = new List<Vector3>(total * 3);
			var colors = new List<Color>(total * 3);
			var uvs = new List<Vector2>(total * 3);
			var subMeshes = new List<(int Area, List<int> Indices)>();
			foreach (var kv in byArea) {
				var area = areas_[kv.Key];
				var blades = kv.Value;
				var ns = normals[kv.Key];
				// shuffle so any first part of the list is spread over the whole cell
				for (int i = blades.Count - 1; i > 0; --i) {
					int j = rng.Next(i + 1);
					(blades[i], blades[j]) = (blades[j], blades[i]);
					(ns[i], ns[j]) = (ns[j], ns[i]);
				}
				int[] cut = { 0, blades.Count / 4, blades.Count / 2, blades.Count };
				for (int part = 0; part < 3; ++part) {
					var indices = new List<int>((cut[part + 1] - cut[part]) * 3);
					for (int i = cut[part]; i < cut[part + 1]; ++i) {
						AddBlade(blades[i], ns[i], area, rng, positions, vertexNormals, colors, uvs, indices);
					}
					subMeshes.Add((kv.Key, indices));
				}
			}

			var mesh = new Mesh { name = $"NatureFX.Grass {c.Key}", indexFormat = IndexFormat.UInt32 };
			mesh.SetVertices(positions);
			mesh.SetNormals(vertexNormals);
			mesh.SetColors(colors);
			mesh.SetUVs(0, uvs);
			mesh.subMeshCount = subMeshes.Count;
			for (int i = 0; i < subMeshes.Count; ++i) {
				mesh.SetTriangles(subMeshes[i].Indices, i, false);
			}
			mesh.RecalculateBounds();
			// room for the sway
			var b = mesh.bounds;
			b.Expand(new Vector3(1.0f, 0.5f, 1.0f));
			mesh.bounds = b;
			mesh.UploadMeshData(true);
			c.Mesh = mesh;
			for (int i = 0; i < subMeshes.Count; ++i) {
				c.Parts.Add((areas_[subMeshes[i].Area].Material, i));
			}
			c.Blades = total;
			blades_ += total;
		}

		// one blade: a thin triangle from the root to a leaning tip (a wider leaf for sasa)
		private static void AddBlade(Vector4 root, Vector3 ground, Area area, System.Random rng, List<Vector3> positions, List<Vector3> normals,
		                             List<Color> colors, List<Vector2> uvs, List<int> indices) {
			var preset = area.Preset;
			float seed = root.w;
			float height = Mathf.Lerp(preset.MinHeight, preset.MaxHeight, (float)rng.NextDouble()) * area.Height;
			float yaw = (float)rng.NextDouble() * Mathf.PI * 2.0f;
			var side = new Vector3(Mathf.Cos(yaw), 0.0f, Mathf.Sin(yaw)) * (preset.Width * (0.7f + (float)rng.NextDouble() * 0.6f) * 0.5f);
			var facing = Vector3.Cross(side, Vector3.up).normalized;
			float lean = (0.1f + (float)rng.NextDouble() * 0.35f) * height;
			var p = new Vector3(root.x, root.y, root.z);
			var tip = p + Vector3.up * height + facing * lean;
			// lit mostly like the ground it grows on, so a field does not flicker blade by blade
			var n = Vector3.Normalize(ground * 0.7f + facing * 0.3f);
			float brightness = (float)rng.NextDouble();
			var root01 = new Color(0.0f, seed, Mathf.Clamp01(height * 0.5f), brightness);
			var tip01 = new Color(1.0f, seed, Mathf.Clamp01(height * 0.5f), brightness);
			int b = positions.Count;
			positions.Add(p - side);
			positions.Add(p + side);
			positions.Add(tip);
			normals.Add(n);
			normals.Add(n);
			normals.Add(n);
			colors.Add(root01);
			colors.Add(root01);
			colors.Add(tip01);
			uvs.Add(new Vector2(0.0f, 0.0f));
			uvs.Add(new Vector2(1.0f, 0.0f));
			uvs.Add(new Vector2(0.5f, 1.0f));
			indices.Add(b);
			indices.Add(b + 2);
			indices.Add(b + 1);
		}

		public string Describe() {
			int built = 0;
			foreach (var c in cells_.Values) {
				built += c.Mesh != null ? 1 : 0;
			}
			return string.Format(CultureInfo.InvariantCulture, "{0} areas, {1} of {2} cells built, {3} blades", areas_.Count, built, cells_.Count, blades_);
		}
	}
}

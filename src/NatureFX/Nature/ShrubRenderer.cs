using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using NatureFX.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Nature {
	// Thickets of scanned shrubs (docs/map-authoring.md 5.6): nocol_nfx_shrub_<proto[+proto...]> surfaces (MeshFilter
	// only) are planted with the map's shrub prototypes, thousands of them, drawn with GPU instancing in three levels:
	//   near   the scan itself (nfxproto_lod0: its pieces, as the map has them)
	//   middle its billboard cloud (nfxproto_cloud: 20 .. 50 cards fitted to the scan, bake_shrub_lods.py)
	//   far    its octahedral impostor (nfxproto_impostor: one quad showing the scan baked from N x N directions,
	//          NFX_Impostor.hlsl)
	// A prototype: an empty nocol_nfx_shrubproto_<name>__frames-<N> with those three children, their renderers off
	// (nothing shows without NatureFX). Every level draws with NatureFX/Foliage (wind, wetness, back light).
	// The own engine's scene gets every shrub's billboard cloud (RayInstances), so they cast traced shadows and show
	// in reflections. 16 m cells; the levels are picked per shrub a few times a second.
	public class ShrubRenderer {
		private const float CellSize = 16.0f;
		private const float FarDistance = 300.0f;
		private const float PickInterval = 0.15f;
		private const int MaxPerCall = 1023;
		// the scans are heavy (up to 150k triangles each): only the nearest this many are drawn as scans, the rest of
		// those within the scan distance as their clouds
		private const int MaxScans = 40;

		private class Part {
			public Mesh Mesh;
			public Material[] Materials;
			public Matrix4x4 Local;     // in the prototype's space
		}

		private class Proto {
			public string Name;
			public readonly List<Part>[] Levels = { new(), new(), new() };
			public float Radius = 1.0f;
			// this frame's shrubs per level and part
			public readonly List<List<Matrix4x4>>[] Draw = { new(), new(), new() };
		}

		private struct Shrub {
			public int Proto;
			public Matrix4x4 Place;
			public Vector3 Position;
		}

		private class Cell {
			public Vector3 Center;
			public readonly List<Shrub> Shrubs = new();
			public Bounds Bounds;
		}

		public struct RayInstance {
			public Mesh Mesh;
			public Matrix4x4 Matrix;
			public Material Material;
		}

		// for RayTracingScene: the clouds of every shrub planted, under a key that changes with each scatter
		public static Object RayKey { get; private set; }
		public static readonly List<RayInstance> RayInstances = new();

		private readonly List<Proto> protos_ = new();
		private readonly Dictionary<string, int> byName_ = new(System.StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<Vector2Int, Cell> cells_ = new();
		private readonly List<Material> owned_ = new();
		private readonly Plane[] planes_ = new Plane[6];
		private int shrubs_;
		private int drawn_;
		private int[] perLevel_ = new int[3];
		private float nextPick_;
		private float density_ = -1.0f;
		private float distance_ = -1.0f;

		public int AreaCount { get; private set; }

		public void Load(MarkerScanner markers, ShaderBundle bundle, MaterialReplacer replacer, float density) {
			Clear();
			density_ = density;
			if (density <= 0.0f) {
				return;
			}
			foreach (var m in markers.OfKind("shrubproto")) {
				if (m.Transform != null) {
					LoadProto(m, bundle, replacer);
				}
			}
			int unreadable = 0;
			foreach (var m in markers.OfKind("shrub")) {
				if (m.Surface == null || m.Transform == null) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: a shrub marker needs a mesh (MeshFilter) to grow on");
					continue;
				}
				var protos = new List<int>();
				foreach (string name in (m.Preset ?? "").Split('+')) {
					if (byName_.TryGetValue(name, out int index)) {
						protos.Add(index);
					} else if (name.Length > 0) {
						Kino.Log.Warning($"[NatureFX] {m.Source}: no shrub prototype '{name}' (nocol_nfx_shrubproto_{name}) in the map");
					}
				}
				if (protos.Count == 0) {
					continue;
				}
				if (!Plant(m, protos, density)) {
					++unreadable;
				}
				++AreaCount;
			}
			MakeRayInstances();
			if (protos_.Count > 0 || AreaCount > 0) {
				Kino.Log.Info($"[NatureFX] Shrubs: {protos_.Count} prototypes, {AreaCount} areas, {shrubs_} shrubs in {cells_.Count} cells" +
				              (unreadable > 0 ? $"; {unreadable} surfaces skipped (mesh not readable)" : ""));
			}
		}

		public bool NeedsReload(float density) => !Mathf.Approximately(density, density_);

		// The scene is going (a new map loads): the shrubs draw the map's own meshes, which Unity destroys with it,
		// and RenderMeshInstanced throws on a destroyed mesh every frame until the rescan planted them again
		public void Unload() {
			Clear();
		}

		private void LoadProto(Marker m, ShaderBundle bundle, MaterialReplacer replacer) {
			var proto = new Proto { Name = m.Preset };
			int frames = Mathf.RoundToInt(m.GetFloat("frames", 8.0f));
			var root = m.Transform;
			string[] levels = { "nfxproto_lod0", "nfxproto_cloud", "nfxproto_impostor" };
			var bounds = new Bounds();
			bool any = false;
			for (int level = 0; level < 3; ++level) {
				var child = root.Find(levels[level]);
				if (child == null) {
					continue;
				}
				foreach (var mf in child.GetComponentsInChildren<MeshFilter>(true)) {
					var r = mf.GetComponent<MeshRenderer>();
					if (mf.sharedMesh == null || r == null) {
						continue;
					}
					// the map leaves them off; nothing of it should draw but through here
					r.enabled = false;
					var part = new Part {
						Mesh = mf.sharedMesh,
						Local = root.worldToLocalMatrix * mf.transform.localToWorldMatrix,
						Materials = new Material[r.sharedMaterials.Length],
					};
					for (int i = 0; i < part.Materials.Length; ++i) {
						var source = r.sharedMaterials[i];
						var converted = source == null ? null : replacer.ConvertCopy(source, bundle, "NatureFX/Foliage", m.Source);
						if (converted == null) {
							continue;
						}
						converted.enableInstancing = true;
						if (level == 2) {
							// the impostor's quad: x across, y up, the tile as big as the quad
							var b = mf.sharedMesh.bounds;
							converted.SetVector("_NFX_Impostor", new Vector4(frames, b.size.x, b.center.y, 1.0f));
							converted.SetTextureScale("_BaseColorMap", Vector2.one);
							converted.SetTextureOffset("_BaseColorMap", Vector2.zero);
						} else if (level == 1) {
							converted.SetVector("_NFX_Foliage2", new Vector4(0.0f, 0.0f, 0.0f, 1.0f)); // the cards' colours sway them
						}
						owned_.Add(converted);
						part.Materials[i] = converted;
					}
					proto.Levels[level].Add(part);
					var world = part.Local * Matrix4x4.identity;
					var mb = mf.sharedMesh.bounds;
					var c = world.MultiplyPoint3x4(mb.center);
					if (!any) {
						bounds = new Bounds(c, Vector3.zero);
						any = true;
					}
					bounds.Encapsulate(new Bounds(c, mb.size));
				}
			}
			if (proto.Levels[0].Count == 0 && proto.Levels[1].Count == 0) {
				Kino.Log.Warning($"[NatureFX] {m.Source}: a shrub prototype needs nfxproto_lod0 or nfxproto_cloud children with meshes");
				return;
			}
			proto.Radius = Mathf.Max(0.3f, bounds.extents.magnitude);
			for (int level = 0; level < 3; ++level) {
				foreach (var unused in proto.Levels[level]) {
					proto.Draw[level].Add(new List<Matrix4x4>());
				}
			}
			byName_[proto.Name] = protos_.Count;
			protos_.Add(proto);
		}

		// shrubs over the surface's triangles: density per m² (the marker's density=, times the setting), random yaw
		// and size, none on slopes steeper than 40°
		private bool Plant(Marker m, List<int> protos, float density) {
			var mesh = m.Surface;
			if (!mesh.isReadable) {
				return false;
			}
			float perM2 = Mathf.Max(0.0f, m.GetFloat("density", 0.15f)) * density;
			float scale = Mathf.Max(0.1f, m.GetFloat("scale", 1.0f));
			var matrix = m.Transform.localToWorldMatrix;
			var vertices = mesh.vertices;
			var triangles = mesh.triangles;
			var rng = new System.Random(m.Source.GetHashCode());
			float carry = 0.0f;
			for (int t = 0; t + 2 < triangles.Length; t += 3) {
				var a = matrix.MultiplyPoint3x4(vertices[triangles[t]]);
				var b = matrix.MultiplyPoint3x4(vertices[triangles[t + 1]]);
				var c = matrix.MultiplyPoint3x4(vertices[triangles[t + 2]]);
				var cross = Vector3.Cross(b - a, c - a);
				float area = cross.magnitude * 0.5f;
				if (area < 1e-6f || Mathf.Abs(cross.normalized.y) < 0.766f) {
					continue;
				}
				carry += area * perM2;
				int count = Mathf.FloorToInt(carry);
				carry -= count;
				for (int i = 0; i < count; ++i) {
					float u = (float)rng.NextDouble(), v = (float)rng.NextDouble();
					if (u + v > 1.0f) {
						u = 1.0f - u;
						v = 1.0f - v;
					}
					var p = a + (b - a) * u + (c - a) * v;
					int proto = protos[rng.Next(protos.Count)];
					float size = scale * (0.75f + (float)rng.NextDouble() * 0.5f);
					var place = Matrix4x4.TRS(p, Quaternion.Euler(0.0f, (float)rng.NextDouble() * 360.0f, 0.0f), Vector3.one * size);
					var key = new Vector2Int(Mathf.FloorToInt(p.x / CellSize), Mathf.FloorToInt(p.z / CellSize));
					if (!cells_.TryGetValue(key, out var cell)) {
						cell = new Cell { Center = new Vector3((key.x + 0.5f) * CellSize, p.y, (key.y + 0.5f) * CellSize) };
						cell.Bounds = new Bounds(p, Vector3.zero);
						cells_[key] = cell;
					}
					float r = protos_[proto].Radius * size;
					cell.Bounds.Encapsulate(new Bounds(p + Vector3.up * r, Vector3.one * (r * 2.0f)));
					cell.Shrubs.Add(new Shrub { Proto = proto, Place = place, Position = p });
					++shrubs_;
				}
			}
			return true;
		}

		private void MakeRayInstances() {
			RayInstances.Clear();
			if (RayKey != null) {
				Object.Destroy(RayKey);
			}
			RayKey = null;
			foreach (var cell in cells_.Values) {
				foreach (var s in cell.Shrubs) {
					var proto = protos_[s.Proto];
					// the cloud, else the scan itself
					var parts = proto.Levels[1].Count > 0 ? proto.Levels[1] : proto.Levels[0];
					foreach (var part in parts) {
						if (part.Materials.Length > 0 && part.Materials[0] != null) {
							RayInstances.Add(new RayInstance { Mesh = part.Mesh, Matrix = s.Place * part.Local, Material = part.Materials[0] });
						}
					}
				}
			}
			if (RayInstances.Count > 0) {
				RayKey = ScriptableObject.CreateInstance<ScriptableObject>();
				RayKey.hideFlags = HideFlags.DontSave;
			}
		}

		// distance: where the scans give way to the clouds (m); the clouds give way to the impostors at three times it
		public void Update(bool on, Camera cam, float distance) {
			if (!on || cam == null || protos_.Count == 0 || cells_.Count == 0) {
				return;
			}
			if (Time.unscaledTime >= nextPick_ || !Mathf.Approximately(distance, distance_)) {
				nextPick_ = Time.unscaledTime + PickInterval;
				distance_ = distance;
				Pick(cam, distance);
			}
			Draw(cam);
		}

		private void Pick(Camera cam, float distance) {
			foreach (var proto in protos_) {
				foreach (var lists in proto.Draw) {
					foreach (var list in lists) {
						list.Clear();
					}
				}
			}
			GeometryUtility.CalculateFrustumPlanes(cam, planes_);
			var eye = cam.transform.position;
			float near = distance, middle = distance * 3.0f;
			float far = Mathf.Max(FarDistance, middle * 1.5f);
			drawn_ = 0;
			perLevel_ = new int[3];
			foreach (var cell in cells_.Values) {
				if ((cell.Bounds.ClosestPoint(eye) - eye).sqrMagnitude > far * far || !GeometryUtility.TestPlanesAABB(planes_, cell.Bounds)) {
					continue;
				}
				foreach (var s in cell.Shrubs) {
					float d = Vector3.Distance(eye, s.Position);
					if (d > far) {
						continue;
					}
					var proto = protos_[s.Proto];
					int level = d < near ? 0 : d < middle ? 1 : 2;
					if (level == 0 && proto.Levels[0].Count > 0) {
						near_.Add((d, s));
						continue;
					}
					Add(s, level);
				}
			}
			near_.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			for (int i = 0; i < near_.Count; ++i) {
				Add(near_[i].Shrub, i < MaxScans ? 0 : 1);
			}
			near_.Clear();
		}

		private readonly List<(float Distance, Shrub Shrub)> near_ = new();

		private void Add(Shrub s, int level) {
			var proto = protos_[s.Proto];
			// a level the prototype lacks: the next one it has
			while (level < 3 && proto.Levels[level].Count == 0) {
				++level;
			}
			if (level == 3) {
				level = proto.Levels[1].Count > 0 ? 1 : 0;
			}
			var parts = proto.Levels[level];
			for (int i = 0; i < parts.Count; ++i) {
				proto.Draw[level][i].Add(s.Place * parts[i].Local);
			}
			++drawn_;
			++perLevel_[level];
		}

		private void Draw(Camera cam) {
			foreach (var proto in protos_) {
				for (int level = 0; level < 3; ++level) {
					var parts = proto.Levels[level];
					for (int i = 0; i < parts.Count; ++i) {
						var list = proto.Draw[level][i];
						if (list.Count == 0) {
							continue;
						}
						var part = parts[i];
						if (part.Mesh == null) {
							continue; // the map's mesh is gone (scene unloading)
						}
						int subMeshes = Mathf.Min(part.Mesh.subMeshCount, part.Materials.Length);
						for (int sm = 0; sm < subMeshes; ++sm) {
							var material = part.Materials[sm];
							if (material == null) {
								continue;
							}
							var rp = new RenderParams(material) {
								camera = cam,
								// the own engine traces their shadows (the clouds are in its scene); HDRP's shadow maps would
								// draw them all again
								shadowCastingMode = ShadowCastingMode.Off,
								receiveShadows = true,
								// instanced draws have no previous matrices: per object motion smeared them across the
								// screen (DLSS, motion blur); they stand still, the camera's motion is right
								motionVectorMode = MotionVectorGenerationMode.Camera,
								worldBounds = new Bounds(cam.transform.position, Vector3.one * (FarDistance * 4.0f)),
							};
							for (int start = 0; start < list.Count; start += MaxPerCall) {
								int count = Mathf.Min(MaxPerCall, list.Count - start);
								Graphics.RenderMeshInstanced(rp, part.Mesh, sm, list, count, start);
							}
						}
					}
				}
			}
		}

		public string Describe() {
			return string.Format(CultureInfo.InvariantCulture, "{0} prototypes, {1} areas, {2} shrubs; drawn {3} (scans {4}, clouds {5}, impostors {6})",
			                     protos_.Count, AreaCount, shrubs_, drawn_, perLevel_[0], perLevel_[1], perLevel_[2]);
		}

		private void Clear() {
			protos_.Clear();
			byName_.Clear();
			cells_.Clear();
			foreach (var m in owned_) {
				if (m != null) {
					Object.Destroy(m);
				}
			}
			owned_.Clear();
			shrubs_ = 0;
			drawn_ = 0;
			AreaCount = 0;
			RayInstances.Clear();
			if (RayKey != null) {
				Object.Destroy(RayKey);
			}
			RayKey = null;
		}

		public void Destroy() {
			Clear();
		}
	}
}

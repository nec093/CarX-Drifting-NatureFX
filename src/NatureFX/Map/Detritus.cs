using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using NatureFX.Rendering;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Map {
	// Debris on the road's edges: pebbles, fallen leaves and twigs where the road and the ground meet (the ground map's
	// kinds, GroundMap.cs), finer than the grass tufts. Placed on the GPU over the ground map (NFX_NativeBridge
	// ScatterDetritus), read back, and built into one mesh per kind around the camera (small made-up shapes: a lumpy
	// stone, a bent leaf, a thin bent stick), drawn as the map's own surfaces are (NatureFX/LitAccumulation: lit, shaded
	// and relit like the rest). Built again when the camera has gone Rebuild metres from where it was built.
	public class Detritus {
		private const int MaxPieces = 40000;
		private const int SampleCells = 4;      // the ground map's cells between samples (0.5 m)
		private const float Rebuild = 24.0f;

		private static readonly int GroundMapId = Shader.PropertyToID("_NFX_GroundMap");
		private static readonly int GroundParamsId = Shader.PropertyToID("_NFX_GroundMapParams");
		private static readonly int ParamsId = Shader.PropertyToID("_NFX_DetritusParams");
		private static readonly int OutId = Shader.PropertyToID("_NFX_Detritus");

		private static readonly (string Name, Color Colour)[] Kinds = {
			("Pebbles", new Color(0.19f, 0.18f, 0.17f)),
			("Leaves", new Color(0.36f, 0.22f, 0.09f)),
			("Twigs", new Color(0.15f, 0.10f, 0.06f)),
		};

		private ShaderBundle bundle_;
		private readonly Material[] materials_ = new Material[3];
		private readonly Mesh[] meshes_ = new Mesh[3];
		private readonly Mesh[] shapes_ = new Mesh[3];
		private GraphicsBuffer pieces_, count_;
		private Vector3 builtAt_ = new(float.MaxValue, 0.0f, 0.0f);
		private int builtGeneration_ = -1;
		private bool pending_;
		private Vector3 anchor_;
		private float density_ = -1.0f;

		public string Status { get; private set; } = "off";

		public void Create(ShaderBundle bundle) {
			bundle_ = bundle;
		}

		public void Update(bool on, float density, Vector3 camera) {
			if (!on || density <= 0.0f) {
				Status = "off";
				return;
			}
			if (GroundMap.Map != null && !pending_ && GroundMap.Generation != builtGeneration_
			    && ((camera - builtAt_).sqrMagnitude > Rebuild * Rebuild || !Mathf.Approximately(density, density_))) {
				Scatter(camera, density);
			}
			Draw();
		}

		private void Scatter(Vector3 camera, float density) {
			var bridge = NativeEngine.Bridge;
			// an older bundle without the kernel: nothing to place (and no error every frame)
			if (bridge == null || !bridge.HasKernel("ScatterDetritus")) {
				Status = "waiting for the new shader bundle";
				builtGeneration_ = GroundMap.Generation;
				builtAt_ = camera;
				return;
			}
			if (pieces_ == null) {
				pieces_ = new GraphicsBuffer(GraphicsBuffer.Target.Append, MaxPieces, 32) { name = "NFX_Detritus" };
				count_ = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 1, 4) { name = "NFX_DetritusCount" };
			}
			var map = GroundMap.Map;
			var p = GroundMap.Params;
			int samples = Mathf.CeilToInt(p.w / SampleCells);
			int kernel = bridge.FindKernel("ScatterDetritus");
			pieces_.SetCounterValue(0);
			bridge.SetBuffer(kernel, GroundMapId, map);
			bridge.SetVector(GroundParamsId, p);
			bridge.SetVector(ParamsId, new Vector4(SampleCells, 1.2f * density, MaxPieces, 17.0f));
			bridge.SetBuffer(kernel, OutId, pieces_);
			bridge.Dispatch(kernel, (samples + 7) / 8, (samples + 7) / 8, 1);
			GraphicsBuffer.CopyCount(pieces_, count_, 0);
			pending_ = true;
			builtAt_ = camera;
			builtGeneration_ = GroundMap.Generation;
			density_ = density;
			var anchor = new Vector3(p.x, camera.y, p.y);
			anchor_ = anchor;
			AsyncGPUReadback.Request(count_, request => {
				if (request.hasError) {
					pending_ = false;
					return;
				}
				int count = Mathf.Min(request.GetData<int>()[0], MaxPieces);
				if (count <= 0) {
					pending_ = false;
					Build(anchor, new NativeArray<Vector4>(0, Allocator.Temp));
					return;
				}
				AsyncGPUReadback.Request(pieces_, count * 32, 0, data => {
					pending_ = false;
					if (!data.hasError) {
						Build(anchor, data.GetData<Vector4>());
					}
				});
			});
		}

		// the pieces' places, a share a frame (BuildPerFrame): the shapes are drawn instanced at them
		private void Build(Vector3 anchor, NativeArray<Vector4> data) {
			incoming_ = data.Length > 0 ? data.ToArray() : System.Array.Empty<Vector4>();
			next_ = 0;
			for (int k = 0; k < 3; ++k) {
				building_[k].Clear();
			}
		}

		private const int BuildPerFrame = 4000;
		private Vector4[] incoming_;
		private int next_;
		private readonly List<Matrix4x4>[] building_ = { new(), new(), new() };
		private List<Matrix4x4>[] places_ = { new(), new(), new() };

		private void Continue() {
			if (incoming_ == null) {
				return;
			}
			EnsureShapes();
			int pieces = incoming_.Length / 2;
			int end = Mathf.Min(pieces, next_ + BuildPerFrame);
			for (int i = next_; i < end; ++i) {
				var place = incoming_[i * 2];
				var tilt = incoming_[i * 2 + 1];
				int kind = Mathf.Clamp(Mathf.FloorToInt(place.w / 10.0f), 0, 2);
				float size = place.w - kind * 10.0f;
				var up = new Vector3(tilt.x, tilt.y, tilt.z);
				if (up.sqrMagnitude < 0.01f) {
					up = Vector3.up;
				}
				var rotation = Quaternion.FromToRotation(Vector3.up, up.normalized) * Quaternion.Euler(0.0f, tilt.w * Mathf.Rad2Deg, 0.0f);
				building_[kind].Add(Matrix4x4.TRS(new Vector3(place.x, place.y, place.z), rotation, Vector3.one * size));
			}
			next_ = end;
			if (next_ < pieces) {
				return;
			}
			// whole: the new places in, the old list reused for the next build
			for (int k = 0; k < 3; ++k) {
				(places_[k], building_[k]) = (building_[k], places_[k]);
				building_[k].Clear();
			}
			incoming_ = null;
			Status = $"{pieces} pieces along the road's edges, placed around the camera";
		}

		private void Draw() {
			Continue();
			for (int k = 0; k < 3; ++k) {
				var list = places_[k];
				if (list.Count == 0 || shapes_[k] == null) {
					continue;
				}
				var material = Material(k);
				if (material == null) {
					return;
				}
				var rp = new RenderParams(material) {
					shadowCastingMode = ShadowCastingMode.Off,
					receiveShadows = true,
					motionVectorMode = MotionVectorGenerationMode.Camera,
					worldBounds = new Bounds(anchor_, Vector3.one * 400.0f),
				};
				for (int start = 0; start < list.Count; start += 1023) {
					Graphics.RenderMeshInstanced(rp, shapes_[k], 0, list, Mathf.Min(1023, list.Count - start), start);
				}
			}
		}

		private Material Material(int kind) {
			if (materials_[kind] != null) {
				return materials_[kind];
			}
			var shader = bundle_?.Get("NatureFX/LitAccumulation");
			if (shader == null) {
				return null;
			}
			var m = new Material(shader) { name = $"NatureFX.Detritus.{Kinds[kind].Name}", hideFlags = HideFlags.DontSave, enableInstancing = true };
			m.SetColor("_BaseColor", Kinds[kind].Colour);
			m.SetFloat("_Smoothness", kind == 0 ? 0.25f : 0.2f);
			m.SetFloat("_Metallic", 0.0f);
			// varied over a couple of metres (no two stones alike), never blended into the ground they lie on
			m.SetVector("_NFX_Surface", new Vector4(0.0f, 0.8f, 1.5f, -1.0f));
			m.SetVector("_NFX_Material", new Vector4(-1.0f, 0.0f, 0.0f, 0.0f));
			UnityEngine.Rendering.HighDefinition.HDMaterial.ValidateMaterial(m);
			materials_[kind] = m;
			return m;
		}

		// the made-up shapes, about 1 at size 1: a lumpy stone 5 cm across, a bent leaf 6 cm long, a stick 25 cm long
		private void EnsureShapes() {
			if (shapes_[0] != null) {
				return;
			}
			shapes_[0] = Stone();
			shapes_[1] = Leaf();
			shapes_[2] = Stick();
		}

		private static Mesh Stone() {
			// an octahedron split twice onto a sphere, pushed in and out, flattened (stones lie on their flat side)
			var verts = new List<Vector3> { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
			var tris = new List<int> { 0, 4, 3, 0, 3, 5, 0, 5, 2, 0, 2, 4, 1, 3, 4, 1, 5, 3, 1, 2, 5, 1, 4, 2 };
			for (int s = 0; s < 2; ++s) {
				var next = new List<int>();
				var mid = new Dictionary<long, int>();
				int Mid(int a, int b) {
					long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
					if (!mid.TryGetValue(key, out int i)) {
						i = verts.Count;
						verts.Add(((verts[a] + verts[b]) * 0.5f).normalized);
						mid[key] = i;
					}
					return i;
				}
				for (int t = 0; t < tris.Count; t += 3) {
					int a = tris[t], b = tris[t + 1], c = tris[t + 2];
					int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
					next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
				}
				tris = next;
			}
			var rng = new System.Random(7);
			for (int i = 0; i < verts.Count; ++i) {
				var v = verts[i] * (0.8f + (float)rng.NextDouble() * 0.4f);
				verts[i] = new Vector3(v.x * 0.03f, Mathf.Max(v.y, -0.3f) * 0.016f + 0.004f, v.z * 0.025f);
			}
			var mesh = new Mesh { name = "NatureFX.Stone", hideFlags = HideFlags.DontSave };
			mesh.SetVertices(verts);
			mesh.SetTriangles(tris, 0);
			mesh.RecalculateNormals();
			return mesh;
		}

		private static Mesh Leaf() {
			// a leaf folded along its middle, both sides (no two sided material needed), a little above the ground
			var v = new List<Vector3> {
				new(0.0f, 0.003f, -0.03f), new(-0.015f, 0.006f, 0.0f), new(0.0f, 0.002f, 0.0f), new(0.015f, 0.006f, 0.0f), new(0.0f, 0.004f, 0.03f),
			};
			var t = new List<int> { 0, 1, 2, 0, 2, 3, 2, 1, 4, 2, 4, 3 };
			int n = v.Count;
			for (int i = 0; i < n; ++i) {
				v.Add(v[i]);
			}
			for (int i = 0, c = t.Count; i < c; i += 3) {
				t.AddRange(new[] { t[i] + n, t[i + 2] + n, t[i + 1] + n });
			}
			var mesh = new Mesh { name = "NatureFX.Leaf", hideFlags = HideFlags.DontSave };
			mesh.SetVertices(v);
			mesh.SetTriangles(t, 0);
			mesh.RecalculateNormals();
			return mesh;
		}

		private static Mesh Stick() {
			// six sided, bent once, lying on the ground
			const int sides = 6;
			var v = new List<Vector3>();
			var t = new List<int>();
			float[] along = { -0.125f, 0.0f, 0.125f };
			float[] lift = { 0.006f, 0.012f, 0.006f };
			for (int r = 0; r < along.Length; ++r) {
				float radius = 0.006f * (1.0f - r * 0.2f);
				for (int s = 0; s < sides; ++s) {
					float a = s * Mathf.PI * 2.0f / sides;
					v.Add(new Vector3(Mathf.Cos(a) * radius + (r == 1 ? 0.01f : 0.0f), Mathf.Sin(a) * radius + lift[r], along[r]));
				}
			}
			for (int r = 0; r < along.Length - 1; ++r) {
				for (int s = 0; s < sides; ++s) {
					int a = r * sides + s, b = r * sides + (s + 1) % sides, c = a + sides, d = b + sides;
					t.AddRange(new[] { a, c, b, b, c, d });
				}
			}
			var mesh = new Mesh { name = "NatureFX.Stick", hideFlags = HideFlags.DontSave };
			mesh.SetVertices(v);
			mesh.SetTriangles(t, 0);
			mesh.RecalculateNormals();
			return mesh;
		}

		public void Destroy() {
			pieces_?.Release();
			count_?.Release();
			pieces_ = count_ = null;
			for (int k = 0; k < 3; ++k) {
				if (meshes_[k] != null) {
					Object.Destroy(meshes_[k]);
				}
				if (shapes_[k] != null) {
					Object.Destroy(shapes_[k]); // the stone / leaf / stick meshes that are really drawn
				}
				if (materials_[k] != null) {
					Object.Destroy(materials_[k]);
				}
				meshes_[k] = null;
				shapes_[k] = null;
				materials_[k] = null;
			}
		}
	}
}

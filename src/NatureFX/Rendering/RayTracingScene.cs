using System;
using System.Collections.Generic;
using KSL.API;
using NatureFX.Map;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace NatureFX.Rendering {
	// The scene the own engine (NativeEngine) traces. Every mesh is added with an NFX_RTProxy material: one proxy
	// per game material, carrying its base colour map, tint and alpha cutoff, so hits know the surface colour and
	// leaves are alpha tested. Static scenery is added once per scene; cars follow their transforms.
	public class RayTracingScene {
		private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
		private static readonly int EmissiveColorMapId = Shader.PropertyToID("_EmissiveColorMap");
		public const string ProxyShaderName = "Hidden/NatureFX/RTProxy";

		private static readonly int BaseColorMapId = Shader.PropertyToID("_BaseColorMap");
		private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
		private static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoff");
		private static readonly int AlphaTestId = Shader.PropertyToID("_NFX_AlphaTest");
		private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
		private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
		private static readonly int CoatId = Shader.PropertyToID("_NFX_Coat");
		private static readonly int ClearCoatEnabledId = Shader.PropertyToID("_ClearCoatEnabled");

		private readonly Shader proxyShader_;
		private readonly Dictionary<Material, Material> proxies_ = new();
		private readonly List<(int Handle, Transform Transform, bool Unscaled)> moving_ = new();
		// instances per source, so a renderer switched off (distance culling, damage) can leave and come back
		private readonly Dictionary<Object, List<int>> handles_ = new();
		private bool needsRebuild_;
		private float nextSync_;
		private const float SyncInterval = 1.0f;

		public int InstanceCount { get; private set; }

		// Cars (moving renderers and their materials). The game draws them without their motion vectors (the
		// materials' pass off, the renderers on camera motion only), so HDRP's motion vectors give their pixels the
		// camera's motion: NRD and DLSS, which follow each pixel through time by them, lost a moving car's history
		// every frame (GI and shadows on it stayed noise, its edges smeared). SetCarMotionVectors turns both on while
		// NRD or DLSS runs and puts back what it changed when they stop.
		private readonly HashSet<Material> carMaterials_ = new();
		private readonly HashSet<Renderer> carRenderers_ = new();
		private readonly HashSet<Material> motionVectorsOn_ = new();
		private readonly Dictionary<Renderer, MotionVectorGenerationMode> modesChanged_ = new();
		private int carMaterialsSeen_ = -1, carRenderersSeen_ = -1;
		private bool carMotionVectors_;

		public void SetCarMotionVectors(bool on) {
			if (on == carMotionVectors_ && carMaterials_.Count == carMaterialsSeen_ && carRenderers_.Count == carRenderersSeen_) {
				return;
			}
			carMotionVectors_ = on;
			carMaterialsSeen_ = carMaterials_.Count;
			carRenderersSeen_ = carRenderers_.Count;
			if (on) {
				foreach (var m in carMaterials_) {
					if (m != null && !m.GetShaderPassEnabled("MotionVectors") && motionVectorsOn_.Add(m)) {
						m.SetShaderPassEnabled("MotionVectors", true);
					}
				}
				// HDRP draws a renderer's motion vectors only when it asks for its own (Object)
				foreach (var r in carRenderers_) {
					if (r != null && r.motionVectorGenerationMode != MotionVectorGenerationMode.Object && !modesChanged_.ContainsKey(r)) {
						modesChanged_[r] = r.motionVectorGenerationMode;
						r.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
					}
				}
				KSL.API.Kino.Log.Info($"[NatureFX] Car motion vectors on: pass on {motionVectorsOn_.Count} materials, {modesChanged_.Count} of {carRenderers_.Count} renderers switched to their own motion");
			} else {
				foreach (var m in motionVectorsOn_) {
					if (m != null) {
						m.SetShaderPassEnabled("MotionVectors", false);
					}
				}
				motionVectorsOn_.Clear();
				foreach (var kv in modesChanged_) {
					if (kv.Key != null) {
						kv.Key.motionVectorGenerationMode = kv.Value;
					}
				}
				modesChanged_.Clear();
			}
		}

		// the scene is the own engine's only (NativeEngine): instances get handles from here (glass: negative ones)
		private int nextOwnHandle_ = 1;
		public int GlassCount => glass_; // glass surfaces in the own engine's scene (RTGlassPass)

		private int SwayingCount() {
			int n = 0;
			foreach (var i in nativeInstances_.Values) {
				n += i.Wind ? 1 : 0;
			}
			return n;
		}

		public RayTracingScene(Shader proxyShader) {
			proxyShader_ = proxyShader;
		}

		public void Rebuild() {
			needsRebuild_ = false;
			nextOwnHandle_ = 1;
			moving_.Clear();
			handles_.Clear();
			nativeInstances_.Clear();
			nativeClear_ = true;
			// the meshes made for the last scene go now, so the engine frees their BLAS in this upload (it keeps
			// what is still a Mesh): Sync makes the terrain chunks again, and a baked skinned mesh whose renderer
			// is gone has no use. Kept, every rebuild added a duplicate set (VRAM grew with each map and car change)
			foreach (var mesh in terrainMeshes_) {
				if (mesh != null) {
					Object.DestroyImmediate(mesh);
				}
			}
			terrainMeshes_.Clear();
			goneBaked_.Clear();
			foreach (var pair in baked_) {
				if (pair.Key == null) {
					goneBaked_.Add(pair.Key);
					if (pair.Value != null) {
						Object.DestroyImmediate(pair.Value);
					}
				}
			}
			foreach (var r in goneBaked_) {
				baked_.Remove(r);
			}
			InstanceCount = 0;
			failed_ = 0;
			skipped_ = 0;
			native_ = 0;
			glass_ = 0;
			OwnGlass.Slots.Clear();
			int trees = Sync();
			int failed = failed_;
			Kino.Log.Info($"[NatureFX] Ray tracing scene: {InstanceCount} instances, {proxies_.Count} materials, {moving_.Count} moving, {lowerLodCount_} lower LOD renderers skipped, " +
			              $"{skipped_} tiny props or shadowless foliage left out, {terrains_} terrains with {trees} trees, {native_} instances with NatureFX materials, " +
			              $"{glass_} glass surfaces (own engine only), {SwayingCount()} swaying with the wind (NatureFX/Foliage)" +
			              (failed > 0 ? $", {failed} meshes rejected" : ""));
			PushNative();
		}

		// The same scene for NatureFX's own engine (NativeEngine): every instance added here, by handle
		private readonly Dictionary<int, NativeEngine.Instance> nativeInstances_ = new();
		private bool nativeDirty_;
		private bool nativeClear_;

		// glass is in the own engine's scene only (Unity's has no use for it): its handles are negative, from here
		private int nextGlassHandle_ = -1;
		private int glass_;

		private void AddNative(int handle, Mesh mesh, int subMesh, Matrix4x4 matrix, bool alphaTest, string kind, Transform follow = null, bool unscaled = false,
		                       Material material = null, MaterialPropertyBlock overrides = null) {
			nativeInstances_[handle] = new NativeEngine.Instance {
				Mesh = mesh, SubMesh = subMesh, Matrix = matrix, AlphaTest = alphaTest, Kind = kind, Follow = follow, Unscaled = unscaled, Material = material,
				Glass = kind == "glass",
				Wind = material != null && material.shader != null && material.shader.name == "NatureFX/Foliage",
				Color = overrides != null && overrides.HasColor(BaseColorId) ? overrides.GetColor(BaseColorId) : (Color?)null,
				Finish = overrides != null && (overrides.HasFloat(MetallicId) || overrides.HasFloat(SmoothnessId) || overrides.HasFloat(CoatId))
					? new Vector4(overrides.HasFloat(MetallicId) ? overrides.GetFloat(MetallicId) : -1.0f,
					              overrides.HasFloat(SmoothnessId) ? overrides.GetFloat(SmoothnessId) : -1.0f,
					              overrides.HasFloat(CoatId) ? overrides.GetFloat(CoatId) : -1.0f, 0.0f)
					: (Vector4?)null
			};
			nativeDirty_ = true;
		}

		private void PushNative() {
			if (!NativeEngine.Enabled) {
				nativeClear_ = true; // switched on later: the whole scene goes over then
				return;
			}
			if (!nativeDirty_ && !nativeClear_) {
				return;
			}
			NativeEngine.UploadScene(nativeInstances_.Values, nativeClear_);
			nativeDirty_ = false;
			nativeClear_ = false;
		}

		private int lowerLodCount_;
		private int terrains_;
		private readonly HashSet<Object> seen_ = new();
		private readonly List<Object> gone_ = new();

		// Brings the scene in line with what is enabled now: maps and the game switch distant objects off and on, and
		// one taken at load time only missed whatever was off then (no ray traced shadow there, only the shadow
		// baked into the map's lightmap). Returns the number of terrain trees added.
		private int Sync() {
			// LOD groups keep every detail level enabled and draw one; the scene takes only the most detailed, since
			// overlapping levels made rays hit them alternately (stripes on cars in reflections)
			var lowerLods = new HashSet<Renderer>();
			foreach (var group in Object.FindObjectsByType<LODGroup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				var lods = group.GetLODs();
				for (int l = 1; l < lods.Length; ++l) {
					foreach (var lodRenderer in lods[l].renderers) {
						if (lodRenderer != null) {
							lowerLods.Add(lodRenderer);
						}
					}
				}
				if (lods.Length > 0) {
					foreach (var lodRenderer in lods[0].renderers) {
						lowerLods.Remove(lodRenderer); // a renderer shared with LOD 0 stays
					}
				}
			}
			lowerLodCount_ = lowerLods.Count;
			seen_.Clear();

			foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (!r.enabled || lowerLods.Contains(r) || MarkerScanner.IsMarkerName(r.gameObject.name)) {
					continue;
				}
				var mesh = r.GetComponent<MeshFilter>()?.sharedMesh;
				if (mesh == null) {
					continue;
				}
				seen_.Add(r);
				if (handles_.ContainsKey(r)) {
					continue;
				}

				// statically batched renderers share one combined mesh in world space
				bool batched = r.isPartOfStaticBatch;
				int firstSubMesh = batched ? r.subMeshStartIndex : 0;
				var matrix = batched ? Matrix4x4.identity : r.localToWorldMatrix;
				bool moving = !batched && r.GetComponentInParent<Rigidbody>() != null;
				// tiny static props (bolts, signs' screws, pebbles) cost an instance each and barely show in a ray
				if (!moving && r.bounds.size.magnitude < MinStaticSize) {
					++skipped_;
					handles_[r] = new List<int>();
					continue;
				}
				handles_[r] = AddRenderer(r, mesh, firstSubMesh, matrix, moving, false, r);
			}

			// Skinned meshes (car parts driven by bones): their pose is baked once into a mesh in the renderer's
			// space, scale included, then they follow the renderer's position and rotation like rigid parts.
			foreach (var r in Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (!r.enabled || r.sharedMesh == null || lowerLods.Contains(r)) {
					continue;
				}
				seen_.Add(r);
				if (handles_.ContainsKey(r)) {
					continue;
				}
				if (!baked_.TryGetValue(r, out var mesh) || mesh == null) {
					mesh = new Mesh { name = "NFX_RT_" + r.sharedMesh.name, hideFlags = HideFlags.DontSave };
					r.BakeMesh(mesh, true);
					baked_[r] = mesh;
				}
				var t = r.transform;
				bool moving = r.GetComponentInParent<Rigidbody>() != null;
				handles_[r] = AddRenderer(r, mesh, 0, Matrix4x4.TRS(t.position, t.rotation, Vector3.one), moving, true, r);
			}

			int trees = 0;
			terrains_ = 0;
			foreach (var terrain in Terrain.activeTerrains) {
				if (terrain == null || terrain.terrainData == null) {
					continue;
				}
				++terrains_;
				seen_.Add(terrain);
				if (!handles_.ContainsKey(terrain)) {
					var list = AddTerrain(terrain, out int added);
					handles_[terrain] = list;
					trees += added;
				}
			}

			// what was switched off or destroyed leaves
			gone_.Clear();
			foreach (var source in handles_.Keys) {
				if (source == null || !seen_.Contains(source)) {
					gone_.Add(source);
				}
			}
			if (gone_.Count > 0) {
				var removed = new HashSet<int>();
				foreach (var source in gone_) {
					foreach (int handle in handles_[source]) {
						nativeDirty_ |= nativeInstances_.Remove(handle);
						if (handle < 0) {
							--glass_;
							continue; // glass: own engine only
						}
						removed.Add(handle);
						--InstanceCount;
					}
					handles_.Remove(source);
				}
				moving_.RemoveAll(m => removed.Contains(m.Handle));
			}
			return trees;
		}

		// Unity terrain: its height field as meshes (64 x 64 cell chunks) and its trees as instances of their
		// prototypes' most detailed meshes. Terrains are not mesh renderers, so they were missing: no ray traced
		// shadows from the trees, only the ones baked into the lightmap.
		private const int MaxTerrainTrees = 30000;
		private Material terrainProxy_;
		private readonly List<Mesh> terrainMeshes_ = new();

		private List<int> AddTerrain(Terrain terrain, out int trees) {
			var handles = new List<int>();
			var data = terrain.terrainData;
			var origin = terrain.GetPosition();
			var size = data.size;
			trees = 0;

			if (terrainProxy_ == null) {
				terrainProxy_ = new Material(proxyShader_) { name = "NFX_RT_Terrain", hideFlags = HideFlags.DontSave };
				terrainProxy_.SetTexture(BaseColorMapId, Texture2D.whiteTexture);
				terrainProxy_.SetColor(BaseColorId, new Color(0.22f, 0.22f, 0.18f)); // earth and grass, roughly
				terrainProxy_.SetFloat(AlphaCutoffId, 0.5f);
				terrainProxy_.SetFloat(AlphaTestId, 0.0f);
				terrainProxy_.SetFloat(MetallicId, 0.0f);
				terrainProxy_.SetFloat(SmoothnessId, 0.1f);
			}

			if (terrain.drawHeightmap) {
				int res = data.heightmapResolution;
				int step = Mathf.Max(1, (res - 1) / 512); // at most 512 cells a side
				var heights = data.GetHeights(0, 0, res, res);
				int cells = (res - 1) / step;
				const int Chunk = 64;
				for (int cz = 0; cz < cells; cz += Chunk) {
					for (int cx = 0; cx < cells; cx += Chunk) {
						int nx = Mathf.Min(Chunk, cells - cx);
						int nz = Mathf.Min(Chunk, cells - cz);
						var vertices = new Vector3[(nx + 1) * (nz + 1)];
						var normals = new Vector3[vertices.Length];
						var uvs = new Vector2[vertices.Length];
						for (int z = 0; z <= nz; ++z) {
							for (int x = 0; x <= nx; ++x) {
								int hx = Mathf.Min((cx + x) * step, res - 1);
								int hz = Mathf.Min((cz + z) * step, res - 1);
								int v = z * (nx + 1) + x;
								vertices[v] = new Vector3(hx / (float)(res - 1) * size.x, heights[hz, hx] * size.y, hz / (float)(res - 1) * size.z);
								normals[v] = data.GetInterpolatedNormal(hx / (float)(res - 1), hz / (float)(res - 1));
								uvs[v] = new Vector2(hx / (float)(res - 1), hz / (float)(res - 1));
							}
						}
						var indices = new int[nx * nz * 6];
						int k = 0;
						for (int z = 0; z < nz; ++z) {
							for (int x = 0; x < nx; ++x) {
								int a = z * (nx + 1) + x;
								int b = a + 1;
								int c = a + nx + 1;
								int d = c + 1;
								indices[k++] = a; indices[k++] = c; indices[k++] = b;
								indices[k++] = b; indices[k++] = c; indices[k++] = d;
							}
						}
						var mesh = new Mesh { name = "NFX_RT_Terrain", hideFlags = HideFlags.DontSave };
						mesh.vertices = vertices;
						mesh.normals = normals;
						mesh.uv = uvs;
						mesh.triangles = indices;
						terrainMeshes_.Add(mesh);
						try {
							int handle = nextOwnHandle_++;
							handles.Add(handle);
							AddNative(handle, mesh, 0, Matrix4x4.Translate(origin), false, "terrain");
							++InstanceCount;
						} catch (Exception) {
							++failed_;
						}
					}
				}
			}

			if (terrain.drawTreesAndFoliage) {
				var prototypes = data.treePrototypes;
				var parts = new List<(Renderer Renderer, Mesh Mesh, Matrix4x4 Local)>[prototypes.Length];
				for (int i = 0; i < prototypes.Length; ++i) {
					parts[i] = TreeParts(prototypes[i].prefab);
				}
				foreach (var tree in data.treeInstances) {
					if (trees >= MaxTerrainTrees || tree.prototypeIndex < 0 || tree.prototypeIndex >= parts.Length) {
						continue;
					}
					var position = origin + Vector3.Scale(tree.position, size);
					var placement = Matrix4x4.TRS(position, Quaternion.Euler(0.0f, tree.rotation * Mathf.Rad2Deg, 0.0f),
						new Vector3(tree.widthScale, tree.heightScale, tree.widthScale));
					foreach (var (renderer, mesh, local) in parts[tree.prototypeIndex]) {
						handles.AddRange(AddRenderer(renderer, mesh, 0, placement * local, false, false, null));
					}
					++trees;
				}
			}
			return handles;
		}

		// The most detailed meshes of a tree prefab, relative to its root
		private static List<(Renderer, Mesh, Matrix4x4)> TreeParts(GameObject prefab) {
			var parts = new List<(Renderer, Mesh, Matrix4x4)>();
			if (prefab == null) {
				return parts;
			}
			var root = prefab.transform.worldToLocalMatrix;
			var group = prefab.GetComponent<LODGroup>();
			var renderers = group != null && group.GetLODs().Length > 0 ? group.GetLODs()[0].renderers : prefab.GetComponentsInChildren<Renderer>();
			foreach (var r in renderers) {
				var mesh = r != null ? r.GetComponent<MeshFilter>()?.sharedMesh : null;
				if (mesh != null) {
					parts.Add((r, mesh, root * r.transform.localToWorldMatrix));
				}
			}
			return parts;
		}

		private int failed_;
		private int skipped_;
		private int native_;

		private readonly Dictionary<Shader, bool> nativeShaders_ = new();

		// The ray tracing pass sits in its own SubShader (like HDRP's), which Material.FindPass does not search
		private bool HasNativePass(Shader shader) {
			if (shader == null) {
				return false;
			}
			// the shader rebuild's HDRP/Lit copies keep HDRP/Lit's property names: the engine reads them as they are
			if (!nativeShaders_.TryGetValue(shader, out bool found)) {
				found = shader.name.StartsWith("NatureFX/", StringComparison.Ordinal);
				nativeShaders_[shader] = found;
			}
			return found;
		}

		// Materials changed shader (the NatureFX shader rebuild): take the scene again so they enter as themselves
		public void RequestRebuild() {
			needsRebuild_ = true;
		}
		private const float MinStaticSize = 0.3f; // bounding box diagonal (m)

		// Surfaces that are not real geometry for rays: anything drawn transparent (render queue, or HDRP's
		// surface type when the queue says opaque) and the cars' fake contact shadow (CarX "CarBlobshadow", a dark
		// box under the car that showed as a black platform in reflections).
		private static bool Solid(Material m) {
			if (m.renderQueue >= (int)RenderQueue.Transparent) {
				return false;
			}
			if (m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f) {
				return false;
			}
			string shader = m.shader != null ? m.shader.name.ToLowerInvariant() : "";
			// motion vector proxies (the spinning wheels' blur helper) are never seen
			return !shader.Contains("blobshadow") && !shader.Contains("fakeshadow") && !shader.Contains("shadowplane") && !shader.Contains("decal") &&
			       !shader.Contains("motionvector") && !shader.Contains("motionproxy");
		}

		// Of what is not Solid: lit see-through surfaces that mirror their surroundings (car windows, lamp lenses,
		// building windows), for the own engine's glass reflections. Not glows, flares, smoke, fake shadows, decals.
		private static readonly string[] NotGlass = {
			"blobshadow", "fakeshadow", "shadowplane", "shadow", "decal", "particle", "unlit", "additive", "sprite", "ui/", "text",
			"flare", "glow", "smoke", "dust", "fog", "cloud", "water", "skid", "tire", "trail", "light shaft", "beam",
			// overlays blended onto roads and walls (a touge map's blend_road_wear dimmed the whole road as "glass")
			"blend", "wear", "dirt", "overlay", "puddle", "stain", "leak", "grime", "moss", "crack", "mark",
		};
		// what a material's name or shader says glass is, whatever its smoothness
		private static readonly string[] GlassWords = { "glass", "window", "windshield", "lens", "screen", "mirror", "visor", "lamp", "light" };

		private static bool Glass(Material m) {
			if (m.renderQueue < (int)RenderQueue.Transparent && !(m.HasProperty("_SurfaceType") && m.GetFloat("_SurfaceType") > 0.5f)) {
				return false;
			}
			string shader = m.shader != null ? m.shader.name.ToLowerInvariant() : "";
			string name = m.name.ToLowerInvariant();
			foreach (string word in NotGlass) {
				if (shader.Contains(word) || name.Contains(word)) {
					return false;
				}
			}
			foreach (string word in GlassWords) {
				if (shader.Contains(word) || name.Contains(word)) {
					return true;
				}
			}
			// drawn as transparent but not see-through: an opaque surface in the transparent queue (the garage's car paint,
			// alpha 1) is no pane: as glass the own engine drew the car body see-through (roadmap E)
			if (m.HasProperty("_BaseColor") && m.GetColor("_BaseColor").a >= 0.95f || m.HasProperty("_Color") && !m.HasProperty("_BaseColor") && m.GetColor("_Color").a >= 0.95f) {
				return false;
			}
			// glass is smooth: a rough see-through surface is some overlay or effect, not a pane
			float smoothness = m.HasProperty("_Smoothness") ? m.GetFloat("_Smoothness") : m.HasProperty("_Glossiness") ? m.GetFloat("_Glossiness") : 1.0f;
			return smoothness >= 0.6f;
		}
		private readonly Dictionary<SkinnedMeshRenderer, Mesh> baked_ = new();
		private readonly List<SkinnedMeshRenderer> goneBaked_ = new();

		private List<int> AddRenderer(Renderer r, Mesh mesh, int firstSubMesh, Matrix4x4 matrix, bool moving, bool unscaled, Renderer owner) {
			var handles = new List<int>();
			// the glass the own engine draws is hidden from HDRP (OwnGlass): the scene reads what was there
			var materials = OwnGlass.Originals(r) ?? r.sharedMaterials;
			if (moving) {
				carRenderers_.Add(r);
				foreach (var m in materials) {
					if (m != null) {
						carMaterials_.Add(m);
					}
				}
			}
			for (int i = 0; i < materials.Length; ++i) {
				int subMesh = firstSubMesh + i;
				var source = materials[i];
				if (subMesh >= mesh.subMeshCount || source == null) {
					continue;
				}
				if (!Solid(source)) {
					// glass, particles, decals, fake shadows do not block light; glass goes to the own engine to mirror
					if (Glass(source)) {
						int glassHandle = nextGlassHandle_--;
						AddNative(glassHandle, mesh, subMesh, matrix, false, "glass", moving ? r.transform : null, unscaled, GetProxy(source),
							owner != null ? InstanceOverrides(r, i, source, moving) : null);
						handles.Add(glassHandle);
						++glass_;
						OwnGlass.Slots.Add((r, i));
					}
					continue;
				}

				// NatureFX shaders carry their own ray tracing pass (NFX_RTLitHit.hlsl): such a material is its own
				// hit material, with its real colour, mask map and alpha test; the others get a proxy guess
				bool native = HasNativePass(source.shader);
				var proxy = native ? source : GetProxy(source);
				bool alphaTest = native ? source.IsKeywordEnabled("_ALPHATEST_ON") : proxy.GetFloat(AlphaTestId) > 0.5f;
				if (native) {
					++native_;
				}
				// alpha tested cards the game draws without shadows are grass and small plants: by far the most
				// instances and any hit work in a ray, for surfaces that show in a reflection as a blur at most
				if (alphaTest && !moving && r.shadowCastingMode == ShadowCastingMode.Off) {
					++skipped_;
					continue;
				}
				var overrides = owner != null ? InstanceOverrides(r, i, source, moving) : null;
				try {
					int handle = nextOwnHandle_++;
					handles.Add(handle);
					AddNative(handle, mesh, subMesh, matrix, alphaTest, owner == null ? "tree" : unscaled ? "skinned" : moving ? "car" : "static",
						moving ? r.transform : null, unscaled, proxy, overrides);
					++InstanceCount;
					if (moving) {
						moving_.Add((handle, r.transform, unscaled));
					}
				} catch (Exception) {
					++failed_;
				}
			}
			return handles;
		}

		// Moves car instances and, once a second, follows what was switched on or off; a destroyed car triggers a
		// rebuild so its instances do not linger.
		public void Update() {
			if (needsRebuild_) {
				Rebuild();
				return;
			}
			if (Time.unscaledTime >= nextSync_) {
				nextSync_ = Time.unscaledTime + SyncInterval;
				Sync();
				PushNative();
			}
			foreach (var (handle, t, unscaled) in moving_) {
				if (t == null) {
					needsRebuild_ = true;
					continue;
				}
				// the own engine follows them itself (NativeEngine.Update)
			}
		}

		public void Destroy() {
			foreach (var proxy in proxies_.Values) {
				if (proxy != null) {
					Object.Destroy(proxy);
				}
			}
			proxies_.Clear();
			foreach (var mesh in baked_.Values) {
				if (mesh != null) {
					Object.Destroy(mesh);
				}
			}
			baked_.Clear();
			moving_.Clear();
			handles_.Clear();
			foreach (var mesh in terrainMeshes_) {
				if (mesh != null) {
					Object.Destroy(mesh);
				}
			}
			terrainMeshes_.Clear();
			if (terrainProxy_ != null) {
				Object.Destroy(terrainProxy_);
			}
		}

		private readonly HashSet<string> loggedShaders_ = new();
		private readonly HashSet<Material> loggedCarMaterials_ = new();
		private readonly MaterialPropertyBlock rendererBlock_ = new();

		private static readonly string[] ColorNames = { "_BaseColor", "_Color", "_MainColor", "_PaintColor", "_AlbedoColor", "_DiffuseColor", "_BodyColor", "_CarColor", "_Color1", "_PrimaryColor" };

		// CarX paints cars per renderer (MaterialPropertyBlock), so the material alone says white: copy the
		// renderer's colour onto this instance. Logs what it finds on car materials once, to name new properties.
		private MaterialPropertyBlock InstanceOverrides(Renderer r, int materialIndex, Material source, bool moving) {
			rendererBlock_.Clear();
			if (r.HasPropertyBlock()) {
				r.GetPropertyBlock(rendererBlock_, materialIndex);
				if (rendererBlock_.isEmpty) {
					r.GetPropertyBlock(rendererBlock_);
				}
			}

			MaterialPropertyBlock result = null;
			string found = null;
			if (!rendererBlock_.isEmpty) {
				foreach (string name in ColorNames) {
					if (rendererBlock_.HasColor(name) || rendererBlock_.HasVector(name)) {
						var c = rendererBlock_.HasColor(name) ? rendererBlock_.GetColor(name) : (Color)rendererBlock_.GetVector(name);
						result = new MaterialPropertyBlock();
						result.SetColor(BaseColorId, c);
						found = $"{name}={c}";
						break;
					}
				}
				// CarX's paint shaders keep the finish per renderer too (chrome rims: _Metalness 0.98, _Roughness 0.15)
				string metal = FirstFloat(rendererBlock_, "_Metallic", "_Metalness");
				string smooth = FirstFloat(rendererBlock_, "_Smoothness", "_Glossiness");
				string rough = smooth == null ? FirstFloat(rendererBlock_, "_Roughness") : null;
				string coat = FirstFloat(rendererBlock_, "_Clearcoat", "_ClearCoat");
				if (metal != null || smooth != null || rough != null || coat != null) {
					result ??= new MaterialPropertyBlock();
					if (metal != null) {
						result.SetFloat(MetallicId, Mathf.Clamp01(rendererBlock_.GetFloat(metal)));
					}
					if (smooth != null || rough != null) {
						result.SetFloat(SmoothnessId, smooth != null ? Mathf.Clamp01(rendererBlock_.GetFloat(smooth)) : 1.0f - Mathf.Clamp01(rendererBlock_.GetFloat(rough)));
					}
					if (coat != null) {
						result.SetFloat(CoatId, rendererBlock_.GetFloat(coat) > 0.5f ? 1.0f : 0.0f);
					}
				}
			}

			if (moving && loggedCarMaterials_.Add(source)) {
				string colour = FindProperty(source, ShaderPropertyType.Color, ColorNames);
				string texture = FindProperty(source, ShaderPropertyType.Texture, "_BaseColorMap", "_MainTex", "_BaseMap", "_AlbedoMap", "_Albedo", "_DiffuseMap", "_Diffuse");
				KSL.API.Kino.Log.Info($"[NatureFX] Ray tracing car material {source.name} ({source.shader.name}): colour {colour}={(colour != null ? source.GetColor(colour).ToString() : "-")}, " +
				                      $"texture {texture ?? "-"}, renderer block {(rendererBlock_.isEmpty ? "none" : found ?? "has values, no known colour")}");
			}
			return result;
		}

		private static string FirstFloat(MaterialPropertyBlock block, params string[] names) {
			foreach (string name in names) {
				if (block.HasFloat(name)) {
					return name;
				}
			}
			return null;
		}

		// First property of this type among the known names, else the first whose name contains "color" / "albedo"
		// / "diffuse" / "base" (textures: also "main"), else null.
		private static string FindProperty(Material m, ShaderPropertyType type, params string[] names) {
			foreach (string name in names) {
				if (m.HasProperty(name) && PropertyType(m.shader, name) == type) {
					return name;
				}
			}
			var shader = m.shader;
			for (int i = 0; i < shader.GetPropertyCount(); ++i) {
				if (shader.GetPropertyType(i) != type) {
					continue;
				}
				string n = shader.GetPropertyName(i).ToLowerInvariant();
				if (n.Contains("emission") || n.Contains("emissive")) {
					continue; // light colour, not surface colour
				}
				bool colourLike = n.Contains("albedo") || n.Contains("diffuse") || n.Contains("basecolor") || (type == ShaderPropertyType.Color && n.Contains("color"))
				                  || (type == ShaderPropertyType.Texture && (n.Contains("maintex") || n.Contains("basemap")));
				bool metalLike = (type == ShaderPropertyType.Range || type == ShaderPropertyType.Float) && n.Contains("metal");
				if (colourLike || metalLike) {
					return shader.GetPropertyName(i);
				}
			}
			return null;
		}

		private static ShaderPropertyType? PropertyType(Shader shader, string name) {
			int index = shader.FindPropertyIndex(name);
			return index >= 0 ? shader.GetPropertyType(index) : null;
		}

		// Once per shader whose colour could not be found, so its property names can be added above
		private void LogUnknownShader(Material m) {
			if (!loggedShaders_.Add(m.shader.name)) {
				return;
			}
			var names = new List<string>();
			for (int i = 0; i < m.shader.GetPropertyCount() && names.Count < 40; ++i) {
				names.Add($"{m.shader.GetPropertyName(i)}:{m.shader.GetPropertyType(i)}");
			}
			KSL.API.Kino.Log.Info($"[NatureFX] Ray tracing: no colour found on shader '{m.shader.name}' (material {m.name}); grey used. Properties: {string.Join(", ", names)}");
		}

		private Material GetProxy(Material source) {
			if (proxies_.TryGetValue(source, out var proxy) && proxy != null) {
				return proxy;
			}

			proxy = new Material(proxyShader_) { name = "NFX_RT_" + source.name, hideFlags = HideFlags.DontSave };

			// the colour texture as the shader rebuild and the alpha clip find it (mod maps' shaders name it their own way)
			string map = NatureFX.Rebuild.AlphaClip.ColorTextureName(source);
			if (map != null) {
				proxy.SetTexture(BaseColorMapId, source.GetTexture(map));
				proxy.SetTextureScale(BaseColorMapId, source.GetTextureScale(map));
				proxy.SetTextureOffset(BaseColorMapId, source.GetTextureOffset(map));
			}
			// game shaders (CarX car paint ...) name their colour differently; unknown ones fall back to grey,
			// which reflects far more plausibly than white
			string colorName = FindProperty(source, ShaderPropertyType.Color, ColorNames);
			var color = colorName != null ? source.GetColor(colorName) : map != null ? Color.white : new Color(0.5f, 0.5f, 0.5f);
			proxy.SetColor(BaseColorId, color); // Unity linearises colours for the shader itself
			if (colorName == null && map == null) {
				LogUnknownShader(source);
			}

			// any shader's alpha clipping (mod maps' own shaders: their keywords, toggles, cutoff names, queue)
			bool clips = NatureFX.Rebuild.AlphaClip.Detect(source, out float cutoff);
			bool alphaTest = clips || source.IsKeywordEnabled("_ALPHATEST_ON")
			                 || (source.HasProperty("_AlphaCutoffEnable") && source.GetFloat("_AlphaCutoffEnable") > 0.5f)
			                 || source.shader.name.StartsWith("NatureFX/Foliage", StringComparison.Ordinal);
			proxy.SetFloat(AlphaTestId, alphaTest ? 1.0f : 0.0f);
			proxy.SetFloat(AlphaCutoffId, source.HasProperty("_AlphaCutoff") ? source.GetFloat("_AlphaCutoff") : cutoff);
			string metallic = FindProperty(source, ShaderPropertyType.Range, "_Metallic", "_Metalness", "_MetallicAmount")
			                  ?? FindProperty(source, ShaderPropertyType.Float, "_Metallic", "_Metalness", "_MetallicAmount");
			proxy.SetFloat(MetallicId, metallic != null ? Mathf.Clamp01(source.GetFloat(metallic)) : 0.0f);
			string smoothness = FindProperty(source, ShaderPropertyType.Range, "_Smoothness", "_Glossiness", "_Gloss")
			                    ?? FindProperty(source, ShaderPropertyType.Float, "_Smoothness", "_Glossiness", "_Gloss");
			string roughness = smoothness == null ? FindProperty(source, ShaderPropertyType.Range, "_Roughness") ?? FindProperty(source, ShaderPropertyType.Float, "_Roughness") : null;
			// glass whose shader names no smoothness (CarX's CarGlass) is polished glass, not a half rough surface
			proxy.SetFloat(SmoothnessId, smoothness != null ? Mathf.Clamp01(source.GetFloat(smoothness))
				: roughness != null ? 1.0f - Mathf.Clamp01(source.GetFloat(roughness)) : Solid(source) ? 0.5f : 0.95f);
			// CarX car paint is clear coated: the reflection passes mirror through its varnish. Its Shader Graph
			// (CarPaint, CarPaintRim, CarPaintStitches) has neither HDRP's coat keyword nor property, so it goes by name.
			// CarX's own _Clearcoat says whether it is varnished (its rims: no; a renderer may say otherwise)
			bool coat = source.HasProperty("_Clearcoat")
				? source.GetFloat("_Clearcoat") > 0.5f
				: source.IsKeywordEnabled("_MATERIAL_FEATURE_CLEAR_COAT")
				  || (source.HasProperty(ClearCoatEnabledId) && source.GetFloat(ClearCoatEnabledId) > 0.5f)
				  || source.shader.name.StartsWith("Shader Graphs/CarPaint", StringComparison.Ordinal);
			proxy.SetFloat(CoatId, coat ? 1.0f : 0.0f);
			// what it gives off (HDRP's _EmissiveColor holds the intensity; the built-in shaders' _EmissionColor only
			// counts with their emission keyword): light for the GI, and a lamp where it is bright (NativeEngine.Emitters)
			if (proxy.HasProperty(EmissiveColorId)) {
				Color emission = Color.black;
				string emissionMap = null;
				if (source.HasProperty(EmissiveColorId)) {
					emission = source.GetColor(EmissiveColorId);
					emissionMap = source.HasProperty("_EmissiveColorMap") ? "_EmissiveColorMap" : null;
				} else if (source.HasProperty("_EmissionColor") && source.IsKeywordEnabled("_EMISSION")) {
					emission = source.GetColor("_EmissionColor");
					emissionMap = source.HasProperty("_EmissionMap") ? "_EmissionMap" : null;
				}
				proxy.SetColor(EmissiveColorId, emission);
				if (emissionMap != null && source.GetTexture(emissionMap) != null) {
					proxy.SetTexture(EmissiveColorMapId, source.GetTexture(emissionMap));
				}
			}

			proxies_[source] = proxy;
			return proxy;
		}
	}
}

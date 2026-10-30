using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Rendering {
	// Chrome rims: CarX sets a rim's finish per renderer (MaterialPropertyBlock: CarPaintRim's _Metalness, _Roughness),
	// so metal rims, chrome paint included, get a near mirror roughness through each renderer's block. Every float
	// property whose name says smoothness / glossiness or roughness is handled; each car shader's properties are
	// logged once. The body paint is left as the game has it: its base layer is meant to be rough under the varnish
	// (the reflections mirror the varnish alone), and lowering its roughness made it look like tinted chrome.
	public class CarFinish {
		private const float SyncInterval = 1.0f;

		public bool ChromeRims;
		public float ChromeSmoothness = 0.99f;

		public string Status { get; private set; } = "off";

		private enum Part { None, Rim }

		// what a renderer's block held before (null: the block did not have it), to put back when switched off
		private class Changed {
			public Renderer Renderer;
			public int MaterialIndex;
			public readonly Dictionary<string, float?> Before = new();
		}

		private readonly Dictionary<(Renderer, int), Changed> changed_ = new();
		private readonly HashSet<Shader> logged_ = new();
		private readonly MaterialPropertyBlock block_ = new();
		private float nextSync_;
		private int rims_;

		// true when the materials changed (the ray tracing scene takes them again)
		public bool Update(bool enabled) {
			if (!enabled || !ChromeRims) {
				if (changed_.Count > 0) {
					RestoreAll();
					Status = "off";
					return true;
				}
				Status = "off";
				return false;
			}
			if (Time.unscaledTime < nextSync_) {
				return false;
			}
			nextSync_ = Time.unscaledTime + SyncInterval;
			rims_ = 0;
			bool any = false;
			foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (r is not MeshRenderer && r is not SkinnedMeshRenderer) {
					continue;
				}
				var materials = r.sharedMaterials;
				for (int i = 0; i < materials.Length; ++i) {
					var m = materials[i];
					if (m == null || m.shader == null) {
						continue;
					}
					if (m.shader.name.StartsWith("Shader Graphs/Car", System.StringComparison.Ordinal)) {
						LogOnce(r, i, m);
					}
					var part = PartOf(m);
					if (part == Part.None) {
						continue;
					}
					any |= Apply(r, i, m, part);
				}
			}
			Status = $"{rims_} metal rims polished";
			return any;
		}

		private static Part PartOf(Material m) {
			string shader = m.shader.name;
			if (shader.StartsWith("Shader Graphs/CarPaintRim", System.StringComparison.Ordinal)) {
				return Part.Rim;
			}
			return Part.None;
		}

		private static bool Is(string name, params string[] words) {
			string n = name.ToLowerInvariant();
			foreach (string w in words) {
				if (n.Contains(w)) {
					return true;
				}
			}
			return false;
		}

		// CarX sets its paint either per material or for the whole renderer; a per material block would hide the
		// renderer's (its colour), so the values go wherever the renderer keeps them
		private static bool PerMaterial(Renderer r, int index, MaterialPropertyBlock block) {
			if (!r.HasPropertyBlock()) {
				return true;
			}
			r.GetPropertyBlock(block, index);
			return !block.isEmpty;
		}

		private static void GetBlock(Renderer r, int index, MaterialPropertyBlock block) {
			if (PerMaterial(r, index, block)) {
				r.GetPropertyBlock(block, index);
			} else {
				r.GetPropertyBlock(block);
			}
		}

		private static void SetBlock(Renderer r, int index, MaterialPropertyBlock block) {
			// asked before the change: an empty per material block plus a renderer block means the renderer's
			var probe = new MaterialPropertyBlock();
			if (PerMaterial(r, index, probe)) {
				r.SetPropertyBlock(block, index);
			} else {
				r.SetPropertyBlock(block);
			}
		}

		private bool Apply(Renderer r, int index, Material m, Part part) {
			GetBlock(r, index, block_);
			var shader = m.shader;
			// rims: chrome only where the rim is metal (painted rims keep their paint)
			if (part == Part.Rim) {
				float metal = -1.0f;
				for (int p = 0; p < shader.GetPropertyCount(); ++p) {
					string name = shader.GetPropertyName(p);
					if (IsFloat(shader, p) && Is(name, "metal")) {
						metal = Mathf.Max(metal, Current(m, name));
					}
				}
				if (!ChromeRims || metal < 0.5f) {
					return Restore(r, index);
				}
			}

			bool changed = false;
			for (int p = 0; p < shader.GetPropertyCount(); ++p) {
				if (!IsFloat(shader, p)) {
					continue;
				}
				string name = shader.GetPropertyName(p);
				if (Is(name, "flake", "dirt", "dust", "damage", "scratch", "wear")) {
					continue;
				}
				float now = Current(m, name);
				float want = now;
				float smooth = ChromeSmoothness;
				if (Is(name, "smooth", "gloss")) {
					want = Mathf.Max(now, smooth);
				} else if (Is(name, "rough")) {
					want = Mathf.Min(now, 1.0f - smooth);
				}
				if (!Mathf.Approximately(want, now)) {
					Remember(r, index, name);
					block_.SetFloat(name, want);
					changed = true;
				}
			}
			if (changed) {
				SetBlock(r, index, block_);
			}
			++rims_;
			return changed;
		}

		private static bool IsFloat(Shader shader, int p) {
			var type = shader.GetPropertyType(p);
			return type == ShaderPropertyType.Float || type == ShaderPropertyType.Range;
		}

		// the value the renderer uses now: its block's, else the material's
		private float Current(Material m, string name) {
			return block_.HasFloat(name) ? block_.GetFloat(name) : m.GetFloat(name);
		}

		private void Remember(Renderer r, int index, string name) {
			if (!changed_.TryGetValue((r, index), out var c)) {
				c = changed_[(r, index)] = new Changed { Renderer = r, MaterialIndex = index };
			}
			if (!c.Before.ContainsKey(name)) {
				c.Before[name] = block_.HasFloat(name) ? block_.GetFloat(name) : null;
			}
		}

		private bool Restore(Renderer r, int index) {
			if (!changed_.TryGetValue((r, index), out var c)) {
				return false;
			}
			RestoreOne(c);
			changed_.Remove((r, index));
			return true;
		}

		private void RestoreOne(Changed c) {
			if (c.Renderer == null) {
				return;
			}
			var materials = c.Renderer.sharedMaterials;
			if (c.MaterialIndex >= materials.Length || materials[c.MaterialIndex] == null) {
				return;
			}
			GetBlock(c.Renderer, c.MaterialIndex, block_);
			foreach (var kv in c.Before) {
				// a block cannot drop one value: the material's own stands in for "not in the block"
				block_.SetFloat(kv.Key, kv.Value ?? materials[c.MaterialIndex].GetFloat(kv.Key));
			}
			SetBlock(c.Renderer, c.MaterialIndex, block_);
		}

		public void RestoreAll() {
			foreach (var c in changed_.Values) {
				RestoreOne(c);
			}
			changed_.Clear();
		}

		// every float property of a car shader once, with the material's and the renderer's value, to tune by name
		private void LogOnce(Renderer r, int index, Material m) {
			if (!logged_.Add(m.shader)) {
				return;
			}
			GetBlock(r, index, block_);
			var shader = m.shader;
			var parts = new List<string>();
			for (int p = 0; p < shader.GetPropertyCount(); ++p) {
				if (!IsFloat(shader, p)) {
					continue;
				}
				string name = shader.GetPropertyName(p);
				string inBlock = block_.HasFloat(name) ? $"/{block_.GetFloat(name):0.###}" : "";
				parts.Add($"{name}={m.GetFloat(name):0.###}{inBlock}");
			}
			KSL.API.Kino.Log.Info($"[NatureFX] Car finish: {shader.name} ({m.name}) floats (material/renderer): {string.Join(", ", parts)}");
		}
	}
}

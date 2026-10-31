using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;

namespace NatureFX.Map {
	// Puddles and water surfaces (docs/map-authoring.md 7.3).
	//   nfx_puddle: a box where rain water gathers. How full it is rises while it rains (heavy rain fills it in about a
	//   minute and a half) and falls slowly once it stops; the ground shaders draw the water (NFX_ApplyPuddleMarkers:
	//   the middle fills first, ripples from the rain). At most 16, nearest the camera.
	//   nocol_nfx_water_<preset>: a water surface on the marker's own mesh (stream, gutter, pond), drawn with
	//   NatureFX/LitAccumulation as dark, mirror smooth moving water (NFX_ApplyWater). A gutter's water rises with rain.
	public class MapWater {
		private const int MaxPuddles = 16;
		private const float FillSeconds = 90.0f;
		private const float DrySeconds = 600.0f;

		private static readonly int PuddlesId = Shader.PropertyToID("_NFX_Puddles");
		private static readonly int PuddleSizesId = Shader.PropertyToID("_NFX_PuddleSizes");
		private static readonly int PuddleCountId = Shader.PropertyToID("_NFX_PuddleCount");
		private static readonly int WaterId = Shader.PropertyToID("_NFX_Water");

		private class Puddle {
			public Vector3 Center;
			public Vector3 Half;
			public float Yaw;
			public float Amount; // the box's own multiplier
		}

		private class Surface {
			public GameObject Object;
			public Material Material;
			public string Preset;
			public float Yaw;
			public float Rise;   // gutter: metres the water comes up in heavy rain
		}

		private readonly List<Puddle> puddles_ = new();
		private readonly List<Puddle> nearest_ = new();
		private readonly List<Surface> surfaces_ = new();
		private readonly Vector4[] centers_ = new Vector4[MaxPuddles];
		private readonly Vector4[] sizes_ = new Vector4[MaxPuddles];
		private float level_;
		private float nextSort_;

		public int PuddleCount => puddles_.Count;
		public int SurfaceCount => surfaces_.Count;
		public float Level => level_;

		public void Load(MarkerScanner markers, ShaderBundle bundle) {
			Clear();
			foreach (var m in markers.OfKind("puddle")) {
				var size = m.Size.x > 0.3f && m.Size.z > 0.3f ? m.Size : new Vector3(4.0f, 1.0f, 4.0f);
				puddles_.Add(new Puddle {
					Center = m.Position,
					Half = new Vector3(Mathf.Abs(size.x), Mathf.Max(0.5f, Mathf.Abs(size.y)), Mathf.Abs(size.z)) * 0.5f,
					Yaw = m.Rotation.eulerAngles.y * Mathf.Deg2Rad,
					Amount = Mathf.Clamp01(m.GetFloat("amount", 1.0f))
				});
			}
			var shader = bundle.Get("NatureFX/LitAccumulation");
			foreach (var m in markers.OfKind("water")) {
				if (m.Surface == null || m.Transform == null) {
					Kino.Log.Warning($"[NatureFX] {m.Source}: a water marker needs a mesh (MeshFilter) for its surface");
					continue;
				}
				if (shader == null) {
					break;
				}
				string preset = string.IsNullOrEmpty(m.Preset) ? "pond" : m.Preset.ToLowerInvariant();
				var go = new GameObject("NatureFX.Water " + preset);
				go.transform.SetParent(m.Transform, false);
				go.AddComponent<MeshFilter>().sharedMesh = m.Surface;
				var material = new Material(shader) { name = "NatureFX.Water " + preset };
				// null when the bundle carries no variant list for the shader
				foreach (string k in bundle.BestVariant(shader.name, new string[0]) ?? new HashSet<string>()) {
					material.EnableKeyword(k);
				}
				material.SetColor("_BaseColor", new Color(0.02f, 0.025f, 0.025f));
				material.SetFloat("_Smoothness", 0.97f);
				material.SetFloat("_Metallic", 0.0f);
				go.AddComponent<MeshRenderer>().sharedMaterial = material;
				surfaces_.Add(new Surface {
					Object = go, Material = material, Preset = preset,
					Yaw = m.GetFloat("yaw", m.Rotation.eulerAngles.y) * Mathf.Deg2Rad,
					Rise = preset == "gutter" ? m.GetFloat("rise", 0.08f) : 0.0f
				});
			}
			if (puddles_.Count + surfaces_.Count > 0) {
				Kino.Log.Info($"[NatureFX] {puddles_.Count} puddle markers, {surfaces_.Count} water surfaces");
			}
			nextSort_ = 0.0f;
		}

		public void Clear() {
			foreach (var s in surfaces_) {
				if (s.Object != null) {
					Object.Destroy(s.Object);
				}
				if (s.Material != null) {
					Object.Destroy(s.Material);
				}
			}
			surfaces_.Clear();
			puddles_.Clear();
			nearest_.Clear();
			Shader.SetGlobalFloat(PuddleCountId, 0.0f);
		}

		public void Destroy() {
			Clear();
		}

		public void Update(bool on, Vector3 camera) {
			var w = Weather.Current;
			// fills with rain, dries slowly; the wet ground keeps a little water in the deepest spots
			if (w.Rain > 0.02f) {
				level_ = Mathf.MoveTowards(level_, Mathf.Clamp01(0.3f + w.Rain), Time.deltaTime * w.Rain / FillSeconds * 1.5f);
			} else {
				level_ = Mathf.MoveTowards(level_, Mathf.Clamp01(w.Wetness * 0.3f), Time.deltaTime / DrySeconds);
			}

			if (!on || puddles_.Count == 0) {
				Shader.SetGlobalFloat(PuddleCountId, 0.0f);
			} else {
				if (Time.unscaledTime >= nextSort_) {
					nextSort_ = Time.unscaledTime + 0.5f;
					nearest_.Clear();
					nearest_.AddRange(puddles_);
					nearest_.Sort((a, b) => (a.Center - camera).sqrMagnitude.CompareTo((b.Center - camera).sqrMagnitude));
					if (nearest_.Count > MaxPuddles) {
						nearest_.RemoveRange(MaxPuddles, nearest_.Count - MaxPuddles);
					}
				}
				for (int i = 0; i < MaxPuddles; ++i) {
					if (i < nearest_.Count) {
						var p = nearest_[i];
						centers_[i] = new Vector4(p.Center.x, p.Center.y, p.Center.z, level_ * p.Amount);
						sizes_[i] = new Vector4(p.Half.x, p.Half.y, p.Half.z, p.Yaw);
					} else {
						centers_[i] = Vector4.zero;
						sizes_[i] = Vector4.zero;
					}
				}
				Shader.SetGlobalVectorArray(PuddlesId, centers_);
				Shader.SetGlobalVectorArray(PuddleSizesId, sizes_);
				Shader.SetGlobalFloat(PuddleCountId, nearest_.Count);
			}

			foreach (var s in surfaces_) {
				if (s.Object == null) {
					continue;
				}
				if (s.Object.activeSelf != on) {
					s.Object.SetActive(on);
				}
				// x on, y flow direction, z flow speed, w wind ripples
				float speed = s.Preset == "stream" ? 0.9f : s.Preset == "gutter" ? 0.2f + w.Rain * 1.5f : 0.0f;
				float ripples = s.Preset == "pond" ? 1.0f : 0.3f;
				s.Material.SetVector(WaterId, new Vector4(1.0f, s.Yaw, speed, ripples));
				if (s.Rise > 0.0f) {
					s.Object.transform.localPosition = s.Object.transform.parent.InverseTransformVector(Vector3.up * s.Rise * Mathf.Max(w.Rain, level_));
				}
			}
		}
	}
}

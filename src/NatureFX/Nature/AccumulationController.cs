using System.Collections.Generic;
using KSL.API;
using UnityEngine;

namespace NatureFX.Nature {
	// Snow / leaves / dust / wetness. NFX shaders read the global vector directly;
	// plain HDRP/Lit track materials get a cheap wetness approximation (darker albedo + higher smoothness).
	public class AccumulationController {
		private struct LitOriginal {
			public Color BaseColor;
			public float Smoothness;
			public float SmoothnessRemapMax;
		}

		private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
		private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
		private static readonly int SmoothnessRemapMaxId = Shader.PropertyToID("_SmoothnessRemapMax");

		private readonly Dictionary<Material, LitOriginal> litMaterials_ = new();
		private Vector4 current_;
		private float appliedWetness_ = -1.0f;

		public Vector4 Current => current_;

		private readonly RaycastHit[] groundHits_ = new RaycastHit[16];
		private float nextGround_;
		private float groundY_;
		private bool groundKnown_;

		// The rebuilt shaders' surface grain while only the ray traced effects run (Update got everything off)
		public void SetSurfaceDetail(float detail) {
			Shader.SetGlobalVector(ShaderGlobals.Weather, new Vector4(0.0f, 0.0f, detail, 0.0f));
		}

		// Road wear of the rebuilt shaders (_NFX_Road): how much rubber the driven ground carries, and the height of
		// that ground so roofs and trailers stay clean
		public void UpdateRoad(float wear) {
			if (wear > 0.0f && Time.unscaledTime >= nextGround_) {
				nextGround_ = Time.unscaledTime + 0.25f;
				FindGround();
			}
			Shader.SetGlobalVector(ShaderGlobals.Road, new Vector4(wear, groundY_, wear > 0.0f && groundKnown_ ? 1.0f : 0.0f, 0.0f));
		}

		// The highest static surface under the camera: the ground the followed car drives on (cars have rigidbodies)
		private void FindGround() {
			var cam = Camera.main;
			if (cam == null) {
				return;
			}
			int n = Physics.RaycastNonAlloc(cam.transform.position, Vector3.down, groundHits_, 50.0f, ~0, QueryTriggerInteraction.Ignore);
			float best = float.NegativeInfinity;
			for (int i = 0; i < n; ++i) {
				if (groundHits_[i].rigidbody == null && groundHits_[i].point.y > best) {
					best = groundHits_[i].point.y;
				}
			}
			if (!float.IsNegativeInfinity(best)) {
				groundY_ = best;
				groundKnown_ = true;
			}
		}

		public void ResetProgress() {
			current_ = Vector4.zero;
		}

		public void Update(ISettings s) {
			var w = Weather.Current;
			var target = s.Enabled && s.Accumulation ? new Vector4(w.Snow, w.Leaves, w.Dust, w.Wetness) : Vector4.zero;

			if (s.AccumulateOverTime && target != Vector4.zero) {
				// AccumulationRate: fraction per minute. Melting / drying happens at double speed.
				float step = s.AccumulationRate / 60.0f * Time.deltaTime;
				for (int i = 0; i < 4; ++i) {
					float rate = current_[i] < target[i] ? step : step * 2.0f;
					current_[i] = Mathf.MoveTowards(current_[i], target[i], rate);
				}
			} else {
				current_ = target;
			}

			Shader.SetGlobalVector(ShaderGlobals.Accumulation, current_);
			float rain = s.Enabled && s.Accumulation ? Weather.Current.Rain : 0.0f;
			Shader.SetGlobalVector(ShaderGlobals.Weather, new Vector4(rain, s.SnowDepth, s.Enabled ? s.SurfaceDetail : 0.0f, 0.0f));

			float wet = s.WetGameMaterials ? current_.w : 0.0f;
			if (Mathf.Abs(wet - appliedWetness_) > 0.005f) {
				ApplyWetness(wet);
			}
		}

		// Called after each scene load
		public void CollectSceneMaterials() {
			RestoreAll();
			litMaterials_.Clear();

			var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
			foreach (var r in renderers) {
				// skip anything that moves (cars, props with physics)
				if (r.GetComponentInParent<Rigidbody>() != null) {
					continue;
				}

				foreach (var m in r.sharedMaterials) {
					if (m == null || litMaterials_.ContainsKey(m) || m.shader == null || m.shader.name != "HDRP/Lit") {
						continue;
					}
					// transparent surfaces (glass, water decals) look wrong when "wet"
					if (m.renderQueue >= 2500 || !m.HasProperty(SmoothnessId)) {
						continue;
					}

					litMaterials_[m] = new LitOriginal {
						BaseColor = m.GetColor(BaseColorId),
						Smoothness = m.GetFloat(SmoothnessId),
						SmoothnessRemapMax = m.HasProperty(SmoothnessRemapMaxId) ? m.GetFloat(SmoothnessRemapMaxId) : 1.0f
					};
				}
			}

			appliedWetness_ = -1.0f;
			Kino.Log.Info($"[NatureFX] Wetness: tracking {litMaterials_.Count} HDRP/Lit materials");
		}

		public void RestoreAll() {
			ApplyWetness(0.0f);
		}

		private void ApplyWetness(float wet) {
			appliedWetness_ = wet;
			foreach (var kv in litMaterials_) {
				var m = kv.Key;
				if (m == null) {
					continue;
				}

				var o = kv.Value;
				float darken = Mathf.Lerp(1.0f, 0.6f, wet);
				var c = o.BaseColor;
				m.SetColor(BaseColorId, new Color(c.r * darken, c.g * darken, c.b * darken, c.a));
				m.SetFloat(SmoothnessId, Mathf.Lerp(o.Smoothness, 0.9f, wet));
				if (m.HasProperty(SmoothnessRemapMaxId)) {
					m.SetFloat(SmoothnessRemapMaxId, Mathf.Lerp(o.SmoothnessRemapMax, 1.0f, wet));
				}
			}
		}
	}
}

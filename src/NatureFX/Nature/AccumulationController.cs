using System.Collections.Generic;
using KSL.API;
using UnityEngine;

namespace NatureFX.Nature {
	// Wetness. NFX shaders read the global vector directly (_NFX_Accumulation.w; xyz stay 0);
	// plain HDRP/Lit track materials get a cheap wetness approximation (darker albedo + higher smoothness).
	public class AccumulationController {
		private static readonly int AntiTilingId = Shader.PropertyToID("_NFX_AntiTiling");
		// soaked to dry in about two minutes once the weather says the ground dries
		private const float DryPerSecond = 1.0f / 120.0f;

		private struct LitOriginal {
			public Color BaseColor;
			public float Smoothness;
			public float SmoothnessRemapMax;
		}

		private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
		private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
		private static readonly int SmoothnessRemapMaxId = Shader.PropertyToID("_SmoothnessRemapMax");

		private readonly Dictionary<Material, LitOriginal> litMaterials_ = new();
		private float wet_;
		private float appliedWetness_ = -1.0f;

		// how wet the ground is now (0..1)
		public float Wetness => wet_;

		public void Update(ISettings s) {
			var w = Weather.Current;
			// while it rains the ground ends up soaked whatever the weather's wetness says
			float soak = w.Rain > 0.001f ? Mathf.Max(w.Wetness, Mathf.Clamp01(w.Rain * 2.0f)) : w.Wetness;
			float target = s.Enabled && s.Accumulation ? soak : 0.0f;

			if (wet_ < target && w.Rain > 0.001f && s.Enabled && s.Accumulation) {
				// rain wets the ground as fast as it falls: about 5 minutes at 1 mm/h, half a minute at 20
				float mmPerHour = NatureFX.Map.MapFog.RainRate;
				wet_ = Mathf.MoveTowards(wet_, target, (0.15f + mmPerHour / 10.0f) / 60.0f * Time.deltaTime);
			} else if (wet_ > target && s.Enabled && s.Accumulation) {
				// it dries slowly
				wet_ = Mathf.MoveTowards(wet_, target, DryPerSecond * Time.deltaTime);
			} else {
				// no rain making it wetter, or the weather off: as the weather says
				wet_ = target;
			}

			Shader.SetGlobalVector(ShaderGlobals.Accumulation, new Vector4(0.0f, 0.0f, 0.0f, wet_));
			float rain = s.Enabled && s.Accumulation ? Weather.Current.Rain : 0.0f;
			Shader.SetGlobalVector(ShaderGlobals.Weather, new Vector4(rain, 0.0f, s.Enabled ? s.SurfaceDetail : 0.0f, 0.0f));
			Shader.SetGlobalFloat(AntiTilingId, s.Enabled ? s.AntiTiling : 0.0f);

			float wet = s.WetGameMaterials ? wet_ : 0.0f;
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

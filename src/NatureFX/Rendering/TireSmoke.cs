using System.Collections.Generic;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Lit tyre smoke: puffs of HDRP Local Volumetric Fog where tyres slide (TrackMap.Contacts). Being part of the
	// volumetric fog, they take the sun, the lamps, the headlights and the brake lights, and shadows fall through them
	// (a headlight beam cuts a bright shaft through a cloud of smoke at night). Each puff grows, rises, drifts with the
	// wind and the car's wake, and thins out over its life; the oldest is reused when the pool is full. On a wet road
	// the tyres throw spray instead: lower, shorter lived, and thrown at speed even without sliding.
	// The game's own GPU smoke has no DX12 kernels (TireSmokeDx12): on DX12 this is the only smoke there is.
	public class TireSmoke {
		private const int PoolSize = 56;
		private const int NoiseSize = 32;
		private const float SpawnInterval = 0.07f;

		private class Puff {
			public LocalVolumetricFog Fog;
			public Vector3 Position;
			public Vector3 Velocity;
			public float Age;
			public float Life;
			public float Size0, Size1;
			public float Density;      // 1 at the start
			public bool Spray;
			public bool Alive;
		}

		private readonly List<Puff> pool_ = new();
		private readonly Dictionary<Transform, float> lastSpawn_ = new();
		private GameObject root_;
		private Texture3D noise_;
		private int next_;

		public bool AnyVisible { get; private set; }
		public int Alive { get; private set; }

		public void Create() {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.TireSmoke");
			Object.DontDestroyOnLoad(root_);
			noise_ = Noise();
			for (int i = 0; i < PoolSize; ++i) {
				var go = new GameObject("NatureFX.Puff");
				go.transform.SetParent(root_.transform, false);
				var fog = go.AddComponent<LocalVolumetricFog>();
				var p = fog.parameters;
				p.volumeMask = noise_;
				p.maskMode = LocalVolumetricFogMaskMode.Texture;
				p.falloffMode = LocalVolumetricFogFalloffMode.Exponential;
				p.positiveFade = p.negativeFade = new Vector3(0.45f, 0.45f, 0.45f);
				p.blendingMode = LocalVolumetricFogBlendingMode.Additive;
				p.distanceFadeStart = 60.0f;
				p.distanceFadeEnd = 90.0f;
				fog.parameters = p;
				fog.enabled = false;
				pool_.Add(new Puff { Fog = fog });
			}
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (noise_ != null) {
				Object.Destroy(noise_);
			}
			root_ = null;
			pool_.Clear();
		}

		public void Update(bool on, float amount) {
			if (root_ == null) {
				return;
			}
			float dt = Time.deltaTime;
			float t = Time.time;
			var w = Weather.Current;
			float wet = Mathf.Max(w.Wetness, w.Rain);
			var windGlobal = Shader.GetGlobalVector("_NFX_Wind");
			var wind = new Vector3(windGlobal.x, 0.0f, windGlobal.z) * windGlobal.w * 2.5f;

			if (on && amount > 0.0f) {
				foreach (var c in TrackMap.Contacts) {
					if (c.Wheel == null) {
						continue;
					}
					float speed = c.Velocity.magnitude;
					// smoke from a sliding tyre on a dry road, spray from any fast tyre on a wet one
					float smoke = c.Slide * (1.0f - Mathf.Clamp01(wet * 1.5f));
					float spray = wet > 0.25f ? Mathf.Clamp01((speed - 8.0f) / 20.0f) * Mathf.Clamp01(wet) : 0.0f;
					if (smoke < 0.15f && spray < 0.15f) {
						continue;
					}
					if (lastSpawn_.TryGetValue(c.Wheel, out float last) && t - last < SpawnInterval / Mathf.Max(0.3f, amount)) {
						continue;
					}
					lastSpawn_[c.Wheel] = t;
					Spawn(c.Point, c.Velocity, smoke >= spray ? smoke : spray, smoke < spray);
				}
			}

			int alive = 0;
			foreach (var p in pool_) {
				if (!p.Alive) {
					continue;
				}
				p.Age += dt;
				if (p.Age >= p.Life) {
					p.Alive = false;
					p.Fog.enabled = false;
					continue;
				}
				++alive;
				float k = p.Age / p.Life;
				// the wake slows down, the wind takes over; smoke rises as it warms the air, spray falls back
				p.Velocity = Vector3.Lerp(p.Velocity, wind + Vector3.up * (p.Spray ? -0.3f : 0.5f), 1.0f - Mathf.Exp(-dt * 1.2f));
				p.Position += p.Velocity * dt;
				float size = Mathf.Lerp(p.Size0, p.Size1, 1.0f - (1.0f - k) * (1.0f - k));
				var par = p.Fog.parameters;
				par.size = new Vector3(size, size * (p.Spray ? 0.5f : 0.75f), size);
				// thick at first, thinning out as it spreads (and fading in over the first moments)
				float density = p.Density * Mathf.SmoothStep(0.0f, 1.0f, k * 8.0f) * (1.0f - k) * (1.0f - k);
				par.meanFreePath = Mathf.Max(0.8f, (p.Spray ? 6.0f : 2.0f) / Mathf.Max(density, 0.01f));
				par.textureScrollingSpeed = p.Velocity * 0.3f + Vector3.up * 0.2f;
				par.textureTiling = Vector3.one * Mathf.Max(0.5f, size / 3.0f);
				p.Fog.parameters = par;
				p.Fog.transform.position = p.Position + Vector3.up * size * 0.3f;
			}
			Alive = alive;
			AnyVisible = alive > 0;
		}

		private void Spawn(Vector3 point, Vector3 velocity, float strength, bool spray) {
			// a free puff, else the oldest
			Puff p = null;
			for (int i = 0; i < pool_.Count; ++i) {
				var candidate = pool_[(next_ + i) % pool_.Count];
				if (!candidate.Alive) {
					p = candidate;
					next_ = (next_ + i + 1) % pool_.Count;
					break;
				}
			}
			if (p == null) {
				p = pool_[next_];
				next_ = (next_ + 1) % pool_.Count;
			}
			p.Alive = true;
			p.Age = 0.0f;
			p.Spray = spray;
			p.Life = spray ? 1.6f : 4.5f + strength * 3.0f;
			p.Size0 = spray ? 0.8f : 1.0f;
			p.Size1 = spray ? 3.0f : 4.0f + strength * 4.0f;
			p.Density = Mathf.Clamp01(0.4f + strength);
			p.Position = point + Vector3.up * 0.25f;
			// it leaves the tyre with part of the car's speed
			p.Velocity = velocity * (spray ? 0.5f : 0.25f);
			var par = p.Fog.parameters;
			par.albedo = spray ? new Color(0.8f, 0.83f, 0.86f) : new Color(0.88f, 0.88f, 0.9f);
			p.Fog.parameters = par;
			p.Fog.enabled = true;
		}

		private static Texture3D Noise() {
			// soft billows: low values cut away, a smooth rise to dense (seamless, VolumeNoise.cs)
			return VolumeNoise.Make("NatureFX.SmokeNoise", NoiseSize, 4, 0.32f, 0.72f, 5);
		}
	}
}

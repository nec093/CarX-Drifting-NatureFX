using System.Collections.Generic;
using NatureFX.Nature;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Lit tyre smoke: puffs of HDRP Local Volumetric Fog where tyres slide (TrackMap.Contacts). Being part of the
	// volumetric fog, they take the sun, the lamps, the headlights and the brake lights, and shadows fall through them
	// (a headlight beam cuts a bright shaft through a cloud of smoke at night). Each puff grows, rises, drifts with the
	// wind and the car's wake, and thins out over its life; the oldest is reused when the pool is full. On a wet road
	// the tyres throw spray instead: lower, shorter lived, and thrown at speed even without sliding.
	// The game's own GPU smoke has no DX12 kernels (TireSmokeDx12): on DX12 this is the only smoke there is.
	// With the shader bundle's particle shader (CreateParticles) the puffs are soft lit particles instead (NFX_Particle
	// smoke mode: lit by the own engine's sun or moon, sky and lamps, fogged by its 3D fog): each spawn emits a few,
	// which leave the tyre with the car's wake, slow down in the air, swirl (the particle system's curl noise), rise as
	// the smoke is warm and drift with the wind, growing and thinning out over their life. The fog boxes are then not
	// used (in the own engine's froxel fog they were too coarse, lagged behind, and turned black at night).
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
		public int Particles => particles_ != null ? particles_.particleCount : 0;

		private ParticleSystem particles_;

		// The tread's surface temperature per tyre (degrees C). Heat in: the friction's power (mu x load x slip speed),
		// half of it into the tread's thin surface layer (about 150 J/K: it heats within a second or two of a drift).
		// Heat out: the air (more at speed), the road and the carcass under it, and on a wet road the water. Rubber
		// starts to break down into smoke above about 160 C, more the hotter it gets and the faster it slides.
		private class TyreHeat {
			public float Temperature = 20.0f;
			public int Frame; // last on the road
		}

		private readonly Dictionary<Transform, TyreHeat> heat_ = new();
		private readonly List<Transform> goneWheels_ = new();
		private Material particleMaterial_;
		private const float SurfaceHeatCapacity = 150.0f; // J/K
		private const float SmokeOnset = 140.0f;          // C
		private const float SmokeFull = 260.0f;           // C: as much smoke as the tyre gives
		public float HottestTyre { get; private set; } = 20.0f;

		private float AmbientTemperature(WeatherState w) {
			return Mathf.Lerp(20.0f, -2.0f, Mathf.Clamp01(w.Snow * 2.0f)) - 5.0f * Mathf.Clamp01(w.Rain);
		}

		// the tyre's smoke now (0 none .. 1 a full cloud), its temperature brought up to date
		private float TyreSmokeRate(TrackMap.WheelContact c, float dt, float wet, float ambient) {
			if (!heat_.TryGetValue(c.Wheel, out var h)) {
				h = new TyreHeat { Temperature = ambient };
				heat_[c.Wheel] = h;
			}
			h.Frame = Time.frameCount;
			float mu = Mathf.Lerp(1.0f, 0.6f, wet);
			// the load from the car's mass, kept to a car's (a game body's mass need not be the real one)
			float load = Mathf.Clamp(c.Load, 2000.0f, 6000.0f);
			float power = mu * load * c.SlipSpeed * 0.6f;                    // W into the tread
			float speed = c.Velocity.magnitude;
			// W/K: the air over the hot surface layer, the carcass under it, water. A drift at 8 m/s of slip settles
			// near 250 C within two or three seconds; the cooled layer at 160 C is about 2.5 s of drifting in
			float cooling = 15.0f + 1.5f * speed + 30.0f + wet * 600.0f;
			h.Temperature += (power - cooling * (h.Temperature - ambient)) / SurfaceHeatCapacity * dt;
			h.Temperature = Mathf.Clamp(h.Temperature, ambient, 450.0f);
			float burn = Mathf.SmoothStep(0.0f, 1.0f, Mathf.Clamp01((h.Temperature - SmokeOnset) / (SmokeFull - SmokeOnset)));
			return burn * Mathf.Clamp01(c.SlipSpeed / 6.0f) * (1.0f - Mathf.Clamp01(wet * 1.5f));
		}
		private static readonly ParticleSystem.EmitParams emit_ = new();
		private const float AirDrag = 1.5f;  // 1 / s: how fast a puff takes the air's velocity
		private const float Buoyancy = 0.8f; // m/s² off the tyre (it settles at Buoyancy / AirDrag rising)

		// the sun's shadow on the smoke: each frame a share of the particles casts a ray towards the sun (batched on the
		// job threads) against the colliders (buildings, cars, the ground's relief); the result eases in over a few
		// updates so a puff drifting out of a shadow brightens smoothly
		private const int ShadowRaysPerFrame = 2000;
		private ParticleSystem.Particle[] buffer_ = new ParticleSystem.Particle[0];
		private readonly List<Vector4> custom_ = new();
		private int shadowCursor_;
		public int ShadowedParticles { get; private set; }

		// the smoke's shadow map (NFX_FogCommon NFX_SmokeTransmittance): 128 x 128 cells of 0.4 m across the sun
		private const int SmokeCells = 128;
		private const float SmokeCell = 0.4f;
		private readonly float[] smokeTau_ = new float[SmokeCells * SmokeCells];
		private readonly float[] smokeNear_ = new float[SmokeCells * SmokeCells];
		private readonly float[] smokeFar_ = new float[SmokeCells * SmokeCells];
		private readonly Color[] smokePixels_ = new Color[SmokeCells * SmokeCells];
		private Texture2D smokeShadow_;
		private static readonly int SmokeTexId = Shader.PropertyToID("_NFX_SmokeShadowTex");
		private static readonly int SmokeCenterId = Shader.PropertyToID("_NFX_SmokeShadowCenter");
		private static readonly int SmokeUId = Shader.PropertyToID("_NFX_SmokeShadowU");
		private static readonly int SmokeVId = Shader.PropertyToID("_NFX_SmokeShadowV");
		private static readonly int SmokeSId = Shader.PropertyToID("_NFX_SmokeShadowS");

		private Vector3 smokeCenter_, smokeU_, smokeV_, smokeS_;
		private bool smokeValid_;

		// what of the smoke lies between a point and the sun (NFX_FogCommon NFX_SmokeTransmittance, the same bilinear lookup)
		private float SmokeTransmittance(Vector3 p) {
			if (!smokeValid_) {
				return 1.0f;
			}
			var d = p - smokeCenter_;
			float inv = 1.0f / SmokeCell;
			float cx = Vector3.Dot(d, smokeU_) * inv + SmokeCells * 0.5f - 0.5f, cy = Vector3.Dot(d, smokeV_) * inv + SmokeCells * 0.5f - 0.5f;
			if (cx < 0.0f || cy < 0.0f || cx >= SmokeCells - 1 || cy >= SmokeCells - 1) {
				return 1.0f;
			}
			float depth = Vector3.Dot(d, smokeS_);
			int bx = (int)cx, by = (int)cy;
			float fx = cx - bx, fy = cy - by;
			float sum = 0.0f;
			for (int i = 0; i < 4; ++i) {
				int ox = i & 1, oy = i >> 1;
				int k = (by + oy) * SmokeCells + bx + ox;
				float tau = smokeTau_[k];
				float between = tau > 0.0f ? Mathf.Clamp01((smokeFar_[k] - depth) / Mathf.Max(smokeFar_[k] - smokeNear_[k], 0.3f)) : 0.0f;
				float w = (ox == 1 ? fx : 1.0f - fx) * (oy == 1 ? fy : 1.0f - fy);
				sum += w * Mathf.Exp(-tau * between);
			}
			return sum;
		}

		// the smoke's density in 3D for the lamps' light (NFX_FogCommon NFX_SmokeLampTransmittance)
		private const int VolX = 48, VolY = 24, VolZ = 48;
		private const float VolCell = 1.0f;
		private readonly float[] volume_ = new float[VolX * VolY * VolZ];
		private Texture3D smokeVolume_;
		private static readonly int VolumeTexId = Shader.PropertyToID("_NFX_SmokeVolume");
		private static readonly int VolumeOriginId = Shader.PropertyToID("_NFX_SmokeVolumeOrigin");
		private static readonly int VolumeSizeId = Shader.PropertyToID("_NFX_SmokeVolumeSize");
		private static readonly int BoundsMinId = Shader.PropertyToID("_NFX_SmokeBoundsMin");
		private static readonly int BoundsMaxId = Shader.PropertyToID("_NFX_SmokeBoundsMax");

		// the same grid for the own engine's 3D fog (NativeEngine.TraceFogVolume): 3 float4 of header, then the cells
		// four to a float4; null while there is no smoke
		public static Vector4[] FogSmoke { get; private set; }
		private readonly Vector4[] fogSmoke_ = new Vector4[3 + VolX * VolY * VolZ / 4];

		// The visible particles of this frame (alpha 0.02 or more), read once on the main thread (the particle API is
		// Unity's): position, alpha, size. The grids below are rasterised from these on worker threads.
		private Vector3[] gatherPos_ = new Vector3[0];
		private float[] gatherAlpha_ = new float[0];
		private float[] gatherSize_ = new float[0];
		private int gathered_;
		// slabs the grids are split into, one task each (each slab's cells sum their particles in the same order as
		// one thread would: the same result)
		private const int RasterSlabs = 8;

		private void Gather(int count) {
			if (gatherPos_.Length < count) {
				gatherPos_ = new Vector3[buffer_.Length];
				gatherAlpha_ = new float[buffer_.Length];
				gatherSize_ = new float[buffer_.Length];
			}
			int n = 0;
			for (int i = 0; i < count; ++i) {
				var p = buffer_[i];
				float alpha = p.GetCurrentColor(particles_).a / 255.0f;
				if (alpha < 0.02f) {
					continue;
				}
				gatherPos_[n] = p.position;
				gatherAlpha_[n] = alpha;
				gatherSize_[n] = p.GetCurrentSize(particles_);
				++n;
			}
			gathered_ = n;
		}

		private void SmokeVolumeOff() {
			Shader.SetGlobalVector(VolumeSizeId, Vector4.zero);
			FogSmoke = null;
		}

		private void UpdateSmokeVolume(int count) {
			var cam = Camera.main;
			if (cam == null || count == 0) {
				SmokeVolumeOff();
				return;
			}
			if (smokeVolume_ == null) {
				smokeVolume_ = new Texture3D(VolX, VolY, VolZ, TextureFormat.RFloat, false) {
					name = "NatureFX.SmokeVolume", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
				};
			}
			var c = cam.transform.position;
			// the camera's cell, the grid reaching 8 m below and 16 m above it
			var origin = new Vector3(Mathf.Floor(c.x / VolCell) * VolCell - VolX * VolCell * 0.5f, Mathf.Floor(c.y / VolCell) * VolCell - 8.0f,
			                         Mathf.Floor(c.z / VolCell) * VolCell - VolZ * VolCell * 0.5f);
			System.Array.Clear(volume_, 0, volume_.Length);
			var lo = new Vector3(1e9f, 1e9f, 1e9f);
			var hi = new Vector3(-1e9f, -1e9f, -1e9f);
			float inv = 1.0f / VolCell;
			int n = gathered_;
			// the smoke's box: the puffs that reach into the grid
			for (int i = 0; i < n; ++i) {
				float r = Mathf.Max(gatherSize_[i] * 0.4f, VolCell * 0.6f);
				var q = (gatherPos_[i] - origin) * inv;
				int rc = Mathf.CeilToInt(r * inv);
				if ((int)q.x + rc < 0 || (int)q.x - rc > VolX - 1 || (int)q.y + rc < 0 || (int)q.y - rc > VolY - 1 ||
				    (int)q.z + rc < 0 || (int)q.z - rc > VolZ - 1) {
					continue;
				}
				lo = Vector3.Min(lo, gatherPos_[i] - Vector3.one * r);
				hi = Vector3.Max(hi, gatherPos_[i] + Vector3.one * r);
			}
			// the cells, a slab of z per task
			var pos = gatherPos_;
			var alphas = gatherAlpha_;
			var sizes = gatherSize_;
			var volume = volume_;
			System.Threading.Tasks.Parallel.For(0, RasterSlabs, slab => {
				int zLo = slab * VolZ / RasterSlabs, zHi = (slab + 1) * VolZ / RasterSlabs - 1;
				for (int i = 0; i < n; ++i) {
					float r = Mathf.Max(sizes[i] * 0.4f, VolCell * 0.6f);
					// the puff's optical depth through it (about 1.4 x its alpha) spread over its width
					float sigma = 1.4f * alphas[i] / (2.0f * r);
					var q = (pos[i] - origin) * inv;
					int rc = Mathf.CeilToInt(r * inv);
					int x0 = Mathf.Max(0, (int)q.x - rc), x1 = Mathf.Min(VolX - 1, (int)q.x + rc);
					int y0 = Mathf.Max(0, (int)q.y - rc), y1 = Mathf.Min(VolY - 1, (int)q.y + rc);
					int z0 = Mathf.Max(zLo, (int)q.z - rc), z1 = Mathf.Min(zHi, (int)q.z + rc);
					if (x0 > x1 || y0 > y1 || z0 > z1) {
						continue;
					}
					float r2 = r * r * inv * inv;
					for (int z = z0; z <= z1; ++z) {
						for (int y = y0; y <= y1; ++y) {
							for (int x = x0; x <= x1; ++x) {
								float dx = x + 0.5f - q.x, dy = y + 0.5f - q.y, dz = z + 0.5f - q.z;
								float w = 1.0f - (dx * dx + dy * dy + dz * dz) / r2;
								if (w > 0.0f) {
									volume[(z * VolY + y) * VolX + x] += sigma * w * 1.5f;
								}
							}
						}
					}
				}
			});
			if (lo.x > hi.x) {
				SmokeVolumeOff();
				return;
			}
			smokeVolume_.SetPixelData(volume_, 0);
			smokeVolume_.Apply(false, false);
			Shader.SetGlobalTexture(VolumeTexId, smokeVolume_);
			Shader.SetGlobalVector(VolumeOriginId, new Vector4(origin.x, origin.y, origin.z, inv));
			Shader.SetGlobalVector(VolumeSizeId, new Vector4(VolX, VolY, VolZ, 1.0f));
			Shader.SetGlobalVector(BoundsMinId, lo);
			Shader.SetGlobalVector(BoundsMaxId, hi);
			fogSmoke_[0] = new Vector4(origin.x, origin.y, origin.z, inv);
			fogSmoke_[1] = lo;
			fogSmoke_[2] = hi;
			for (int i = 0; i < volume_.Length / 4; ++i) {
				fogSmoke_[3 + i] = new Vector4(volume_[i * 4], volume_[i * 4 + 1], volume_[i * 4 + 2], volume_[i * 4 + 3]);
			}
			FogSmoke = fogSmoke_;
		}

		private void SmokeShadowOff() {
			smokeValid_ = false;
			Shader.SetGlobalVector(SmokeSId, Vector4.zero);
		}

		private void UpdateSmokeShadow(int count, Vector3 sunDir) {
			var cam = Camera.main;
			if (cam == null || count == 0) {
				SmokeShadowOff();
				return;
			}
			if (smokeShadow_ == null) {
				smokeShadow_ = new Texture2D(SmokeCells, SmokeCells, TextureFormat.RGBAFloat, false, true) {
					name = "NatureFX.SmokeShadow", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.DontSave
				};
			}
			var s = sunDir;
			var u = Vector3.Cross(s, Mathf.Abs(s.y) < 0.95f ? Vector3.up : Vector3.forward).normalized;
			var v = Vector3.Cross(u, s);
			// centred on the camera, snapped to whole cells (the shadow would swim with it otherwise)
			var c = cam.transform.position;
			float cu = Mathf.Round(Vector3.Dot(c, u) / SmokeCell) * SmokeCell, cv = Mathf.Round(Vector3.Dot(c, v) / SmokeCell) * SmokeCell;
			float cs = Vector3.Dot(c, s);
			var center = u * cu + v * cv + s * cs;
			System.Array.Clear(smokeTau_, 0, smokeTau_.Length);
			for (int i = 0; i < smokeNear_.Length; ++i) {
				smokeNear_[i] = 1e6f;
				smokeFar_[i] = -1e6f;
			}
			float inv = 1.0f / SmokeCell;
			int n = gathered_;
			var pos = gatherPos_;
			var alphas = gatherAlpha_;
			var sizes = gatherSize_;
			var tauMap = smokeTau_;
			var nearMap = smokeNear_;
			var farMap = smokeFar_;
			// a band of rows per task
			System.Threading.Tasks.Parallel.For(0, RasterSlabs, slab => {
				int rowLo = slab * SmokeCells / RasterSlabs, rowHi = (slab + 1) * SmokeCells / RasterSlabs - 1;
				for (int i = 0; i < n; ++i) {
					var d = pos[i] - center;
					float r = Mathf.Max(sizes[i] * 0.4f, SmokeCell);
					float px = Vector3.Dot(d, u) * inv + SmokeCells * 0.5f, py = Vector3.Dot(d, v) * inv + SmokeCells * 0.5f;
					float depth = Vector3.Dot(d, s);
					// the puff's optical depth through its centre (its alpha as the shader draws it, about this much)
					float tau = 1.4f * alphas[i];
					int rc = Mathf.CeilToInt(r * inv);
					int x0 = Mathf.Max(0, (int)px - rc), x1 = Mathf.Min(SmokeCells - 1, (int)px + rc);
					int y0 = Mathf.Max(rowLo, (int)py - rc), y1 = Mathf.Min(rowHi, (int)py + rc);
					float r2 = r * r * inv * inv;
					for (int y = y0; y <= y1; ++y) {
						for (int x = x0; x <= x1; ++x) {
							float dx = x + 0.5f - px, dy = y + 0.5f - py;
							float q = 1.0f - (dx * dx + dy * dy) / r2;
							if (q <= 0.0f) {
								continue;
							}
							int k = y * SmokeCells + x;
							tauMap[k] += tau * q * q;
							nearMap[k] = Mathf.Min(nearMap[k], depth - r * 0.5f);
							farMap[k] = Mathf.Max(farMap[k], depth + r * 0.5f);
						}
					}
				}
			});
			for (int k = 0; k < smokePixels_.Length; ++k) {
				smokePixels_[k] = smokeTau_[k] > 0.0f ? new Color(smokeTau_[k], smokeNear_[k], smokeFar_[k], 0.0f) : Color.clear;
			}
			smokeShadow_.SetPixels(smokePixels_);
			smokeShadow_.Apply(false, false);
			Shader.SetGlobalTexture(SmokeTexId, smokeShadow_);
			Shader.SetGlobalVector(SmokeCenterId, new Vector4(center.x, center.y, center.z, inv));
			Shader.SetGlobalVector(SmokeUId, new Vector4(u.x, u.y, u.z, SmokeCells));
			Shader.SetGlobalVector(SmokeVId, new Vector4(v.x, v.y, v.z, 0.0f));
			Shader.SetGlobalVector(SmokeSId, new Vector4(s.x, s.y, s.z, 1.0f));
			smokeCenter_ = center;
			smokeU_ = u;
			smokeV_ = v;
			smokeS_ = s;
			smokeValid_ = true;
		}

		private void UpdateShadows() {
			int count = particles_.particleCount;
			if (count == 0) {
				ShadowedParticles = 0;
				SmokeShadowOff();
				SmokeVolumeOff();
				return;
			}
			if (buffer_.Length < count) {
				buffer_ = new ParticleSystem.Particle[particles_.main.maxParticles];
			}
			count = particles_.GetParticles(buffer_, count);
			particles_.GetCustomParticleData(custom_, ParticleSystemCustomData.Custom1);
			Gather(count);
			// the lamps' smoke grid every other frame (the sun's map on the frames between)
			if ((Time.frameCount & 1) == 1) {
				UpdateSmokeVolume(count);
			}
			var sunDir = (Vector3)Shader.GetGlobalVector("_NFX_PSunDir");
			if (sunDir.sqrMagnitude < 0.5f || sunDir.y < -0.05f) {
				SmokeShadowOff();
				return;
			}
			sunDir.Normalize();
			// every other frame: the smoke moves slowly against its own size
			if ((Time.frameCount & 1) == 0) {
				UpdateSmokeShadow(count, sunDir);
			}
			int rays = Mathf.Min(count, ShadowRaysPerFrame);
			var commands = new NativeArray<RaycastCommand>(rays, Allocator.TempJob);
			var hits = new NativeArray<RaycastHit>(rays, Allocator.TempJob);
			var query = new QueryParameters(~0, false, QueryTriggerInteraction.Ignore, false);
			int start = shadowCursor_ % count;
			for (int k = 0; k < rays; ++k) {
				int i = (start + k) % count;
				commands[k] = new RaycastCommand(buffer_[i].position + sunDir * 0.3f, sunDir, query, 300.0f);
			}
			RaycastCommand.ScheduleBatch(commands, hits, 64, 1).Complete();
			// the smoke's own shadow on every particle, at its centre
			for (int i = 0; i < count && i < custom_.Count; ++i) {
				var c = custom_[i];
				c.y = Mathf.Lerp(c.y, 1.0f - SmokeTransmittance(buffer_[i].position), 0.5f);
				custom_[i] = c;
			}
			int shadowed = 0;
			for (int k = 0; k < rays; ++k) {
				int i = (start + k) % count;
				float target = hits[k].colliderInstanceID != 0 ? 1.0f : 0.0f;
				var c = i < custom_.Count ? custom_[i] : Vector4.zero;
				c.x = Mathf.Lerp(c.x, target, 0.6f);
				if (i < custom_.Count) {
					custom_[i] = c;
				}
				if (c.x > 0.5f) {
					++shadowed;
				}
			}
			commands.Dispose();
			hits.Dispose();
			shadowCursor_ = (start + rays) % count; // kept small: it grew without bound and overflowed negative after hours (index out of range every frame)
			ShadowedParticles = shadowed * count / Mathf.Max(1, rays);
			particles_.SetCustomParticleData(custom_, ParticleSystemCustomData.Custom1);
		}

		public void CreateParticles(ShaderBundle bundle) {
			if (particles_ != null || root_ == null) {
				return;
			}
			var material = ParticleAtlas.Material(bundle, "NatureFX.TyreSmoke", ParticleAtlas.ModeSmoke, ParticleAtlas.Snow, new Color(0.88f, 0.88f, 0.9f, 1.0f));
			if (material == null) {
				return;
			}
			particleMaterial_ = material;
			var go = new GameObject("NatureFX.TyreSmokeParticles");
			go.transform.SetParent(root_.transform, false);
			var ps = go.AddComponent<ParticleSystem>();
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			var main = ps.main;
			main.loop = false;
			main.playOnAwake = false;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.maxParticles = 12000;
			main.startSpeed = 0.0f;
			var emission = ps.emission;
			emission.enabled = false;
			var shape = ps.shape;
			shape.enabled = false;
			// grows fast at first, then slowly: a puff spreading into the air
			var size = ps.sizeOverLifetime;
			size.enabled = true;
			size.size = new ParticleSystem.MinMaxCurve(1.0f, new AnimationCurve(new Keyframe(0.0f, 0.25f, 0.0f, 2.5f), new Keyframe(0.3f, 0.7f), new Keyframe(1.0f, 1.0f, 0.2f, 0.0f)));
			// in quickly, out slowly
			var color = ps.colorOverLifetime;
			color.enabled = true;
			var g = new Gradient();
			g.SetKeys(new[] { new GradientColorKey(Color.white, 0.0f), new GradientColorKey(Color.white, 1.0f) },
				// a puff keeps its soot as it grows: spread over a larger area it thins out (about 1 / size², the size
				// curve below), and it is gone as it mixes into the air
				new[] { new GradientAlphaKey(0.0f, 0.0f), new GradientAlphaKey(1.0f, 0.04f), new GradientAlphaKey(0.6f, 0.15f), new GradientAlphaKey(0.32f, 0.3f),
				        new GradientAlphaKey(0.16f, 0.6f), new GradientAlphaKey(0.0f, 1.0f) });
			color.color = g;
			// the air's drag: linear in the velocity relative to the air (the soot is fine enough to be carried by it).
			// With the force k x wind (Update) a puff settles at the wind's velocity in about 1 / k seconds, the jet off
			// the tyre and the car's wake die away in the same time
			var limit = ps.limitVelocityOverLifetime;
			limit.enabled = true;
			limit.drag = AirDrag;
			limit.multiplyDragByParticleSize = false;
			limit.multiplyDragByParticleVelocity = false;
			// swirls (curl noise)
			var noise = ps.noise;
			noise.enabled = true;
			noise.strength = new ParticleSystem.MinMaxCurve(0.8f);
			noise.frequency = 0.35f;
			noise.scrollSpeed = 0.25f;
			noise.octaveCount = 2;
			noise.quality = ParticleSystemNoiseQuality.Medium;
			var rot = ps.rotationOverLifetime;
			rot.enabled = true;
			rot.z = new ParticleSystem.MinMaxCurve(-0.4f, 0.4f);
			// the smoke meets the car and the road instead of passing through them: it stops where it touches (a car's
			// body, the ground) and is carried along it by the wake and the swirls. High quality: the car moves, and
			// only that collides with moving colliders
			var collision = ps.collision;
			collision.enabled = true;
			collision.type = ParticleSystemCollisionType.World;
			collision.mode = ParticleSystemCollisionMode.Collision3D;
			collision.quality = ParticleSystemCollisionQuality.High;
			collision.enableDynamicColliders = true;
			collision.radiusScale = 0.12f;       // the puff's dense core, not its soft edge
			collision.dampen = 0.7f;
			collision.bounce = 0.05f;
			collision.lifetimeLoss = 0.0f;
			collision.maxCollisionShapes = 128;
			collision.collidesWith = ~0;
			var force = ps.forceOverLifetime;
			force.enabled = true;
			force.space = ParticleSystemSimulationSpace.World;
			// buoyancy: the smoke is hot off the tyre and rises, less as it mixes with the air and cools
			force.y = new ParticleSystem.MinMaxCurve(Buoyancy, new AnimationCurve(new Keyframe(0.0f, 1.0f), new Keyframe(0.35f, 0.35f), new Keyframe(1.0f, 0.1f)));
			// the sun's visibility per particle (Custom1.x, 1 = shadowed: new particles start in the sun), for
			// NFX_Particle's smoke (TEXCOORD0.w)
			var custom = ps.customData;
			custom.enabled = false;
			var renderer = go.GetComponent<ParticleSystemRenderer>();
			renderer.sharedMaterial = material;
			renderer.renderMode = ParticleSystemRenderMode.Billboard;
			renderer.sortMode = ParticleSystemSortMode.Distance;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			renderer.maxParticleSize = 4.0f;
			renderer.SetActiveVertexStreams(new List<ParticleSystemVertexStream> {
				ParticleSystemVertexStream.Position, ParticleSystemVertexStream.Normal, ParticleSystemVertexStream.Color,
				ParticleSystemVertexStream.UV, ParticleSystemVertexStream.StableRandomX, ParticleSystemVertexStream.Custom1XY
			});
			particles_ = ps;
		}

		public void DestroyParticles() {
			if (particles_ != null) {
				Object.Destroy(particles_.gameObject);
			}
			particles_ = null;
			if (particleMaterial_ != null) {
				Object.Destroy(particleMaterial_); // made again by CreateParticles (each shader reload leaked one)
			}
			particleMaterial_ = null;
		}

		// a puff as particles: a few overlapping, as big as the fog puff would grow, as long lived
		private void EmitParticles(Vector3 point, Vector3 velocity, float strength, bool spray) {
			var p = emit_;
			// many small particles rather than a few big ones: the puff's shape comes from how they spread
			int count = spray ? 4 : 8;
			float life = spray ? 1.6f : 4.5f + strength * 3.0f;
			float size = (spray ? 3.0f : 4.0f + strength * 4.0f) * 0.55f;
			for (int i = 0; i < count; ++i) {
				p.position = point + Vector3.up * 0.25f + Random.insideUnitSphere * 0.4f;
				p.velocity = velocity * (spray ? 0.5f : 0.25f) + Random.insideUnitSphere * 0.9f;
				p.startLifetime = life * Random.Range(0.8f, 1.2f);
				p.startSize = size * Random.Range(0.7f, 1.1f);
				p.rotation = Random.Range(0.0f, 360.0f);
				p.startColor = spray ? new Color(0.8f, 0.83f, 0.86f, Mathf.Clamp01(0.3f + strength * 0.5f)) : new Color(0.88f, 0.88f, 0.9f, Mathf.Clamp01(0.45f + strength * 0.55f));
				particles_.Emit(p, 1);
			}
			if (!particles_.isPlaying) {
				particles_.Play();
			}
		}

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
			if (smokeShadow_ != null) {
				Object.Destroy(smokeShadow_);
			}
			if (smokeVolume_ != null) {
				Object.Destroy(smokeVolume_);
			}
			smokeShadow_ = null;
			smokeVolume_ = null;
			DestroyParticles();
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

			if (particles_ != null) {
				// the drag pulls each particle to the wind's velocity (force = drag x wind, Create); gusty, stronger wind
				// stirs the smoke more
				var force = particles_.forceOverLifetime;
				force.x = wind.x * AirDrag;
				force.z = wind.z * AirDrag;
				var noise = particles_.noise;
				noise.strength = new ParticleSystem.MinMaxCurve(0.5f + 0.12f * wind.magnitude);
				UpdateShadows();
			}

			float ambient = AmbientTemperature(w);
			HottestTyre = ambient;
			// tyres off the road cool in the air (those on it in TyreSmokeRate); wheels of cars that left (destroyed, or
			// not seen for half a minute) are forgotten, they only accumulated
			goneWheels_.Clear();
			foreach (var kv in heat_) {
				if (kv.Key == null || kv.Value.Frame < Time.frameCount - 1800) {
					goneWheels_.Add(kv.Key);
					continue;
				}
				if (kv.Value.Frame >= Time.frameCount - 1) {
					continue;
				}
				kv.Value.Temperature = Mathf.MoveTowards(kv.Value.Temperature, ambient, (kv.Value.Temperature - ambient) * 0.4f * dt);
			}
			foreach (var wheel in goneWheels_) {
				heat_.Remove(wheel);
				lastSpawn_.Remove(wheel);
			}
			if (on && amount > 0.0f) {
				foreach (var c in TrackMap.Contacts) {
					if (c.Wheel == null) {
						continue;
					}
					float speed = c.Velocity.magnitude;
					// smoke from a tyre hot enough to burn its rubber (TyreSmokeRate), spray from any fast tyre on a wet one
					float smoke = TyreSmokeRate(c, dt, wet, ambient);
					HottestTyre = Mathf.Max(HottestTyre, heat_[c.Wheel].Temperature);
					float spray = wet > 0.25f ? Mathf.Clamp01((speed - 8.0f) / 20.0f) * Mathf.Clamp01(wet) : 0.0f;
					if (smoke < 0.15f && spray < 0.15f) {
						continue;
					}
					float interval = particles_ != null ? SpawnInterval * 0.5f : SpawnInterval;
					if (lastSpawn_.TryGetValue(c.Wheel, out float last) && t - last < interval / Mathf.Max(0.3f, amount)) {
						continue;
					}
					lastSpawn_[c.Wheel] = t;
					// from the outside of the tyre and a little behind it, not from inside the wheel arch
					var at = c.Point;
					if (c.Body != null && c.Wheel != null) {
						var outward = Vector3.ProjectOnPlane(c.Wheel.position - c.Body.worldCenterOfMass, Vector3.up);
						var along = Vector3.ProjectOnPlane(c.Velocity, Vector3.up);
						at += (outward.sqrMagnitude > 1e-4f ? outward.normalized * 0.3f : Vector3.zero)
						      - (along.sqrMagnitude > 0.25f ? along.normalized * 0.35f : Vector3.zero);
					}
					Spawn(at, c.Velocity, smoke >= spray ? smoke : spray, smoke < spray);
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
			if (particles_ != null) {
				EmitParticles(point, velocity, strength, spray);
				return;
			}
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

using NatureFX.Nature;
using UnityEngine;

namespace NatureFX.Rendering {
	// Falling rain and snow around the camera (Weather > Rain and snow), following the weather's rain and snow.
	// Rain: thin streaks falling at a raindrop's speed (about 9 m/s) and drifting with the wind, stopped by whatever they
	// hit (roofs, tunnels, the car) where some burst into a small splash. Snow: flakes drifting down at about 1 m/s,
	// wandering. Both are lit by NFX_Particle.shader from the sun or the moon, the sky and the nearest lamps and
	// headlights, so rain shows in the beams at night. The volume moves with the camera, ahead of it when it moves.
	public class Precipitation {
		private const float RainBox = 36.0f;
		private const float SnowBox = 30.0f;
		private const float RainRate = 7000.0f;   // drops a second in heavy rain
		private const float SnowRate = 2500.0f;   // flakes a second in heavy snow

		private GameObject root_;
		private ParticleSystem rain_;
		private ParticleSystem splash_;
		private ParticleSystem snow_;
		private Vector3 lastEye_;
		private Vector3 velocity_;

		public string State { get; private set; } = "off";

		public void Create(ShaderBundle bundle) {
			if (root_ != null) {
				return;
			}
			var rainMaterial = ParticleAtlas.Material(bundle, "NatureFX.Rain", ParticleAtlas.ModeRain, ParticleAtlas.Rain, new Color(0.9f, 0.95f, 1.0f, 0.4f));
			var splashMaterial = ParticleAtlas.Material(bundle, "NatureFX.Splash", ParticleAtlas.ModeRain, ParticleAtlas.Rain, new Color(0.9f, 0.95f, 1.0f, 0.6f));
			var snowMaterial = ParticleAtlas.Material(bundle, "NatureFX.Snow", ParticleAtlas.ModeSnow, ParticleAtlas.Snow, new Color(1.0f, 1.0f, 1.0f, 0.9f));
			if (rainMaterial == null) {
				State = "needs the new shader bundle (NatureFX/Particle)";
				return;
			}
			materials_ = new[] { rainMaterial, splashMaterial, snowMaterial }; // destroyed with the root (each reload made new ones)
			root_ = new GameObject("NatureFX.Precipitation");
			Object.DontDestroyOnLoad(root_);

			rain_ = MakeRain(rainMaterial);
			splash_ = MakeSplash(splashMaterial);
			var sub = rain_.subEmitters;
			sub.enabled = true;
			sub.AddSubEmitter(splash_, ParticleSystemSubEmitterType.Collision, ParticleSystemSubEmitterProperties.InheritNothing, 0.3f);
			snow_ = MakeSnow(snowMaterial);
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (materials_ != null) {
				foreach (var m in materials_) {
					if (m != null) {
						Object.Destroy(m);
					}
				}
			}
			materials_ = null;
			root_ = null;
			rain_ = splash_ = snow_ = null;
		}

		private Material[] materials_;

		public void Update(ISettings s, Camera camera) {
			if (root_ == null || camera == null) {
				return;
			}
			var w = Weather.Current;
			bool on = s.Enabled && s.Precipitation > 0.0f;
			float rain = on ? Mathf.Clamp01(w.Rain) * s.Precipitation : 0.0f;
			float snow = on ? Mathf.Clamp01(w.Snow) * s.Precipitation : 0.0f;

			// the camera's velocity, smoothed, to put the volume where the camera will be
			var eye = camera.transform.position;
			float dt = Mathf.Max(Time.deltaTime, 1e-3f);
			var v = (eye - lastEye_) / dt;
			velocity_ = (eye - lastEye_).sqrMagnitude > 100.0f ? Vector3.zero : Vector3.Lerp(velocity_, v, Mathf.Clamp01(dt * 4.0f));
			lastEye_ = eye;

			var wind = Shader.GetGlobalVector(ShaderGlobals.Wind);
			var windVelocity = new Vector3(wind.x, 0.0f, wind.z) * wind.w * 8.0f;

			// drops of 1 to 5 mm fall at 4 to 9 m/s (Gunn and Kinzer): each drop its own speed between them
			Drive(rain_, rain * RainRate, eye + velocity_ * 0.8f + Vector3.up * 12.0f, windVelocity * 0.7f + Vector3.down * 9.0f, 4.0f);
			Drive(snow_, snow * SnowRate, eye + velocity_ * 1.5f + Vector3.up * 8.0f, windVelocity + Vector3.down * 1.0f);
			CheckRainAlive(rain);
			State = rain > 0.0f || snow > 0.0f
				? $"rain {rain:P0} ({rain_.particleCount} drops{RainDetail()}), snow {snow:P0} ({snow_.particleCount} flakes)"
				: (on ? "waiting for rain or snow" : "off");
		}

		// Developer page: the drops stopped by the world's colliders (off: they fall through roofs and the car)
		public static bool WorldCollision = true;
		private bool collisionOn_ = true;
		private bool collisionGaveUp_;
		private float emptyFor_;

		// Drops emitted but none alive (seen: rain at 60 %, 0 drops): the world's colliders kill them where they are made
		// (the emitter's box inside a collider, the map's invisible walls or ceilings). After 2 s of that the drops fall
		// without the world's colliders, and the state says so.
		private void CheckRainAlive(float rain) {
			bool want = WorldCollision && !collisionGaveUp_;
			if (rain > 0.0f && rain_.isPlaying && rain_.particleCount == 0 && collisionOn_) {
				emptyFor_ += Time.unscaledDeltaTime;
				if (emptyFor_ > 2.0f) {
					collisionGaveUp_ = true;
					want = false;
					KSL.API.Kino.Log.Warning("[NatureFX] Precipitation: no drop lived for 2 s with the world's collision on; the drops fall without it now");
				}
			} else {
				emptyFor_ = 0.0f;
			}
			if (want != collisionOn_) {
				collisionOn_ = want;
				var collision = rain_.collision;
				collision.enabled = want;
				rain_.Clear();
			}
		}

		private string RainDetail() {
			string detail = rain_.isPlaying ? (rain_.isEmitting ? "" : ", not emitting") : ", stopped";
			if (!collisionOn_) {
				detail += collisionGaveUp_ ? ", world collision off: none lived with it" : ", world collision off";
			}
			return detail;
		}

		// after a map loads: the world's collision is tried again
		public void ResetCollision() {
			collisionGaveUp_ = false;
			emptyFor_ = 0.0f;
		}

		// spread: how much slower than velocity's fall the slowest falls (each particle its own, between the two)
		private static void Drive(ParticleSystem ps, float rate, Vector3 at, Vector3 velocity, float spread = 0.0f) {
			if (ps == null) {
				return;
			}
			ps.transform.position = at;
			var emission = ps.emission;
			emission.rateOverTime = rate;
			var vel = ps.velocityOverLifetime;
			// all three in the same mode (Unity wants them alike)
			vel.x = new ParticleSystem.MinMaxCurve(velocity.x, velocity.x);
			vel.y = new ParticleSystem.MinMaxCurve(velocity.y + spread, velocity.y);
			vel.z = new ParticleSystem.MinMaxCurve(velocity.z, velocity.z);
			if (rate > 0.0f && !ps.isPlaying) {
				ps.Play();
			}
		}

		private ParticleSystem Make(string name, Material material, float box, float lifetime, int max) {
			var go = new GameObject(name);
			go.transform.SetParent(root_.transform, false);
			// the box lies flat: the shape's z is the system's forward, turned to point down
			var ps = go.AddComponent<ParticleSystem>();
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			var main = ps.main;
			main.loop = true;
			main.playOnAwake = false;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.startLifetime = lifetime;
			main.startSpeed = 0.0f;
			main.maxParticles = max;
			main.scalingMode = ParticleSystemScalingMode.Shape;
			var shape = ps.shape;
			shape.shapeType = ParticleSystemShapeType.Box;
			shape.scale = new Vector3(box, 0.5f, box);
			var vel = ps.velocityOverLifetime;
			vel.enabled = true;
			vel.space = ParticleSystemSimulationSpace.World;
			var emission = ps.emission;
			emission.rateOverTime = 0.0f;
			// stopped by what it falls on
			var collision = ps.collision;
			collision.enabled = true;
			collision.type = ParticleSystemCollisionType.World;
			collision.mode = ParticleSystemCollisionMode.Collision3D;
			collision.quality = ParticleSystemCollisionQuality.Low;
			collision.voxelSize = 0.5f;
			collision.lifetimeLoss = 1.0f;
			collision.bounce = 0.0f;
			collision.enableDynamicColliders = true;
			var renderer = go.GetComponent<ParticleSystemRenderer>();
			renderer.sharedMaterial = material;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			ParticleAtlas.SetStreams(renderer);
			return ps;
		}

		private ParticleSystem MakeRain(Material material) {
			// lives long enough for the slowest drops (4 m/s) to reach the ground from 12 m up
			var ps = Make("Rain", material, RainBox, 3.5f, 24000);
			var main = ps.main;
			// real drops are 1 to 5 mm (8 to 14 mm drew rods); thin streaks, faint unless lit
			main.startSize = new ParticleSystem.MinMaxCurve(0.0025f, 0.005f);
			main.startColor = new ParticleSystem.MinMaxGradient(new Color(1, 1, 1, 0.5f), Color.white);
			var renderer = ps.GetComponent<ParticleSystemRenderer>();
			// a drop seen through a 1/50 s shutter: a streak as long as it falls in that time
			renderer.renderMode = ParticleSystemRenderMode.Stretch;
			renderer.velocityScale = 0.02f;
			renderer.lengthScale = 1.0f;
			renderer.cameraVelocityScale = 0.01f;
			return ps;
		}

		private ParticleSystem MakeSplash(Material material) {
			var ps = Make("Splash", material, 0.0f, 0.25f, 3000);
			ps.transform.SetParent(rain_.transform, false);
			var main = ps.main;
			main.startLifetime = new ParticleSystem.MinMaxCurve(0.12f, 0.3f);
			main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 2.2f);
			main.startSize = new ParticleSystem.MinMaxCurve(0.005f, 0.009f);
			main.gravityModifier = 1.0f;
			var shape = ps.shape;
			shape.shapeType = ParticleSystemShapeType.Cone;
			shape.angle = 40.0f;
			shape.radius = 0.02f;
			shape.rotation = new Vector3(-90.0f, 0.0f, 0.0f);
			var vel = ps.velocityOverLifetime;
			vel.enabled = false;
			var emission = ps.emission;
			emission.rateOverTime = 0.0f;
			emission.SetBursts(new[] { new ParticleSystem.Burst(0.0f, 3, 6) });
			var collision = ps.collision;
			collision.enabled = false;
			var renderer = ps.GetComponent<ParticleSystemRenderer>();
			renderer.renderMode = ParticleSystemRenderMode.Stretch;
			renderer.velocityScale = 0.03f;
			renderer.lengthScale = 1.0f;
			return ps;
		}

		private ParticleSystem MakeSnow(Material material) {
			var ps = Make("Snow", material, SnowBox, 10.0f, 25000);
			var main = ps.main;
			main.startSize = new ParticleSystem.MinMaxCurve(0.006f, 0.02f);
			main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
			var noise = ps.noise;
			noise.enabled = true;
			noise.strength = 0.7f;
			noise.frequency = 0.35f;
			noise.scrollSpeed = 0.25f;
			noise.quality = ParticleSystemNoiseQuality.Low;
			var rot = ps.rotationOverLifetime;
			rot.enabled = true;
			rot.z = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);
			var renderer = ps.GetComponent<ParticleSystemRenderer>();
			renderer.renderMode = ParticleSystemRenderMode.Billboard;
			return ps;
		}
	}
}

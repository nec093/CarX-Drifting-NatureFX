using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using NatureFX.Rendering;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// Living things and floating bits (docs/map-authoring.md 5.4): nfx_fx_<preset> fills its box with small
	// particles when the season, the time and the weather suit them (fireflies on summer nights, leaves in autumn
	// with more wind, petals in spring, moths around lamps on summer nights, dust in the sunlight, pollen in spring and
	// early summer). Moths also gather by themselves at the lit light markers nearest the camera. A disabled Particle
	// System of the map named nfx_fx plays while map effects are on. At most 32 run, stopped beyond 80 m.
	public class MapParticles {
		private const int MaxRunning = 32;
		private const float StopDistance = 80.0f;
		private const int AutoMoths = 6;

		private class Preset {
			public Color Color;
			public float Emission;    // nits; 0 for lit particles
			public float Size;
			public float Rate;        // per second per 100 m³ of box
			public float Lifetime;
			public float Gravity;
			public float Noise;       // wandering
			public int MaxParticles;
		}

		private static readonly Dictionary<string, Preset> Presets = new(System.StringComparer.OrdinalIgnoreCase) {
			["fireflies"] = new Preset { Color = new Color(0.02f, 0.03f, 0.0f), Emission = 600.0f, Size = 0.06f, Rate = 1.5f, Lifetime = 5.0f, Gravity = 0.0f, Noise = 0.4f, MaxParticles = 120 },
			["leaves"] = new Preset { Color = Color.white, Size = 0.08f, Rate = 2.0f, Lifetime = 14.0f, Gravity = 0.06f, Noise = 0.8f, MaxParticles = 300 },
			["petals"] = new Preset { Color = Color.white, Size = 0.025f, Rate = 4.0f, Lifetime = 14.0f, Gravity = 0.025f, Noise = 0.6f, MaxParticles = 400 },
			["moths"] = new Preset { Color = new Color(0.55f, 0.5f, 0.42f), Size = 0.045f, Rate = 4.0f, Lifetime = 3.0f, Gravity = 0.0f, Noise = 2.5f, MaxParticles = 40 },
			["dust"] = new Preset { Color = new Color(0.9f, 0.85f, 0.75f), Emission = 4.0f, Size = 0.02f, Rate = 8.0f, Lifetime = 10.0f, Gravity = -0.005f, Noise = 0.15f, MaxParticles = 400 },
			["pollen"] = new Preset { Color = new Color(0.95f, 0.95f, 0.9f), Size = 0.03f, Rate = 2.0f, Lifetime = 12.0f, Gravity = -0.01f, Noise = 0.35f, MaxParticles = 200 }
		};

		private class Fx {
			public ParticleSystem System;
			public string Preset;      // null: the author's own
			public float Rate;
			public bool Created;
			public bool Auto;          // moths at a lamp
			public bool Running;
			public Vector3 Position;
			public string Why = "";  // what it does or waits for (the Map tab)
			public Marker Marker;
		}

		private readonly List<Fx> fx_ = new();
		private readonly List<Fx> sorted_ = new();
		private readonly Dictionary<string, Material> materials_ = new();
		private ShaderBundle bundle_;
		private MarkerScanner markers_;
		private GameObject root_;
		private float nextCheck_;

		public int Count => fx_.Count;

		public void Load(MarkerScanner markers, ShaderBundle bundle) {
			Clear();
			bundle_ = bundle;
			markers_ = markers;
			foreach (var m in markers.OfKind("fx")) {
				if (m.Component is ParticleSystem own) {
					// a system made in the plain Unity editor keeps the built-in Default-Particle material, which HDRP
					// draws magenta: give it our dust look instead
					var ownRenderer = own.GetComponent<ParticleSystemRenderer>();
					if (ownRenderer != null && !RendersInHdrp(ownRenderer.sharedMaterial)) {
						var dust = MaterialFor("dust", Presets["dust"]);
						if (dust != null) {
							ownRenderer.sharedMaterial = dust;
							ParticleAtlas.SetStreams(ownRenderer);
							Kino.Log.Info($"[NatureFX] fx '{own.name}': its material has no HDRP pass, using the dust look");
						} else {
							ownRenderer.enabled = false;
						}
					}
					fx_.Add(new Fx { System = own, Position = m.Position, Marker = m });
					continue;
				}
				string preset = string.IsNullOrEmpty(m.Preset) ? "dust" : m.Preset.ToLowerInvariant();
				var size = m.Size.x > 0.5f && m.Size.y > 0.5f ? m.Size : new Vector3(10.0f, 4.0f, 10.0f);
				var fx = Make(preset, m.Position, m.Rotation, size, Mathf.Max(0.0f, m.GetFloat("amount", 1.0f)), false);
				if (fx != null) {
					fx.Marker = m;
					fx_.Add(fx);
				}
			}
			if (fx_.Count > 0) {
				Kino.Log.Info($"[NatureFX] {fx_.Count} particle markers");
			}
			nextCheck_ = 0.0f;
		}

		// a new shader bundle came in (F8 > Shader rebuild > reload): the particles' materials come from it
		public void Reload(ShaderBundle bundle) {
			foreach (var m in materials_.Values) {
				Object.Destroy(m);
			}
			materials_.Clear();
			if (markers_ != null) {
				Load(markers_, bundle);
			}
		}

		private Fx Make(string preset, Vector3 position, Quaternion rotation, Vector3 size, float amount, bool auto) {
			if (!Presets.TryGetValue(preset, out var look)) {
				Kino.Log.Warning($"[NatureFX] Unknown fx preset '{preset}' (fireflies, leaves, petals, moths, dust, pollen)");
				return null;
			}
			var material = MaterialFor(preset, look);
			if (material == null) {
				Kino.Log.Warning($"[NatureFX] fx '{preset}' needs the new shader bundle (NatureFX/Particle)");
				return null;
			}
			if (root_ == null) {
				root_ = new GameObject("NatureFX.MapParticles");
			}
			var go = new GameObject("NatureFX.Fx " + preset);
			go.transform.SetParent(root_.transform, false);
			go.transform.SetPositionAndRotation(position, rotation);
			go.SetActive(false);
			var ps = go.AddComponent<ParticleSystem>();
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			var main = ps.main;
			main.loop = true;
			main.playOnAwake = false;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.startLifetime = new ParticleSystem.MinMaxCurve(look.Lifetime * 0.6f, look.Lifetime);
			main.startSpeed = new ParticleSystem.MinMaxCurve(0.0f, 0.2f);
			main.startSize = new ParticleSystem.MinMaxCurve(look.Size * 0.7f, look.Size * 1.3f);
			main.startRotation = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
			main.gravityModifier = look.Gravity;
			main.maxParticles = look.MaxParticles;
			var shape = ps.shape;
			shape.shapeType = ParticleSystemShapeType.Box;
			shape.scale = size;
			var emission = ps.emission;
			float volume = Mathf.Max(1.0f, size.x * size.y * size.z);
			float rate = look.Rate * volume / 100.0f * amount;
			emission.rateOverTime = rate;
			var noise = ps.noise;
			noise.enabled = look.Noise > 0.0f;
			noise.strength = look.Noise;
			noise.frequency = 0.4f;
			noise.scrollSpeed = 0.3f;
			if (preset == "fireflies") {
				// glow and fade, a few times a life
				var sol = ps.sizeOverLifetime;
				sol.enabled = true;
				var curve = new AnimationCurve(new Keyframe(0.0f, 0.0f), new Keyframe(0.15f, 1.0f), new Keyframe(0.3f, 0.1f), new Keyframe(0.55f, 1.0f), new Keyframe(0.7f, 0.0f), new Keyframe(1.0f, 0.0f));
				sol.size = new ParticleSystem.MinMaxCurve(1.0f, curve);
			}
			var renderer = go.GetComponent<ParticleSystemRenderer>();
			renderer.sharedMaterial = material;
			renderer.renderMode = ParticleSystemRenderMode.Billboard;
			renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
			renderer.receiveShadows = false;
			if (preset == "leaves" || preset == "petals") {
				// a cupped leaf turning over and over in 3D as it flutters down, sliding sideways with each swing,
				// slowed by the air, then lying where it lands for the rest of its life
				renderer.renderMode = ParticleSystemRenderMode.Mesh;
				renderer.mesh = ParticleAtlas.LeafMesh();
				renderer.alignment = ParticleSystemRenderSpace.World;
				main.startRotation3D = true;
				main.startRotationX = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
				main.startRotationY = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
				main.startRotationZ = new ParticleSystem.MinMaxCurve(0.0f, Mathf.PI * 2.0f);
				var rol = ps.rotationOverLifetime;
				rol.enabled = true;
				rol.separateAxes = true;
				rol.x = new ParticleSystem.MinMaxCurve(-4.0f, 4.0f);
				rol.y = new ParticleSystem.MinMaxCurve(-1.5f, 1.5f);
				rol.z = new ParticleSystem.MinMaxCurve(-3.0f, 3.0f);
				var drag = ps.limitVelocityOverLifetime;
				drag.enabled = true;
				drag.limit = preset == "leaves" ? 1.2f : 0.8f;
				drag.dampen = 0.15f;
				var collision = ps.collision;
				collision.enabled = true;
				collision.type = ParticleSystemCollisionType.World;
				collision.quality = ParticleSystemCollisionQuality.Medium;
				collision.bounce = 0.0f;
				collision.dampen = 1.0f;
				collision.lifetimeLoss = 0.0f;
				collision.radiusScale = 0.3f;
				// on the ground they stop turning (the collision's dampen takes the speed; the spin is the rotation's)
				var sizeFade = ps.sizeOverLifetime;
				sizeFade.enabled = true;
				sizeFade.size = new ParticleSystem.MinMaxCurve(1.0f, new AnimationCurve(new Keyframe(0.0f, 0.0f), new Keyframe(0.03f, 1.0f), new Keyframe(0.92f, 1.0f), new Keyframe(1.0f, 0.0f)));
			}
			ParticleAtlas.SetStreams(renderer);
			return new Fx { System = ps, Preset = preset, Rate = rate, Created = true, Auto = auto, Position = position };
		}

		private static bool RendersInHdrp(Material m) {
			if (m == null || m.shader == null || !m.shader.isSupported) {
				return false;
			}
			var lightMode = new UnityEngine.Rendering.ShaderTagId("LightMode");
			for (int i = 0; i < m.shader.passCount; ++i) {
				string mode = m.shader.FindPassTagValue(i, lightMode).name;
				if (mode == "ForwardOnly" || mode == "Forward" || mode == "SRPDefaultUnlit" || mode == "GBuffer") {
					return true;
				}
			}
			return false; // no tag at all: a built-in pipeline shader
		}

		private Material MaterialFor(string preset, Preset look) {
			if (materials_.TryGetValue(preset, out var m)) {
				return m;
			}
			string name = "NatureFX.Fx " + preset;
			m = preset switch {
				// momiji and the other leaves, red, orange and yellow
				"leaves" => ParticleAtlas.Material(bundle_, name, ParticleAtlas.ModeLeaf, ParticleAtlas.Leaves, look.Color),
				"petals" => ParticleAtlas.Material(bundle_, name, ParticleAtlas.ModeLeaf, ParticleAtlas.Petals, look.Color),
				"moths" => ParticleAtlas.Material(bundle_, name, ParticleAtlas.ModeLeaf, ParticleAtlas.Moth, look.Color),
				"fireflies" => ParticleAtlas.Material(bundle_, name, ParticleAtlas.ModeGlow, ParticleAtlas.Glow, new Color(0.75f, 1.0f, 0.2f), look.Emission),
				_ => ParticleAtlas.Material(bundle_, name, ParticleAtlas.ModeMote, ParticleAtlas.Mote, new Color(look.Color.r, look.Color.g, look.Color.b, 0.8f))
			};
			if (m != null) {
				materials_[preset] = m;
			}
			return m;
		}

		public void Clear() {
			foreach (var f in fx_) {
				if (f.System == null) {
					continue;
				}
				if (f.Created) {
					Object.Destroy(f.System.gameObject);
				} else {
					f.System.Stop();
					f.System.gameObject.SetActive(false);
				}
			}
			fx_.Clear();
			sorted_.Clear();
		}

		public void Destroy() {
			Clear();
			foreach (var m in materials_.Values) {
				Object.Destroy(m);
			}
			materials_.Clear();
			if (root_ != null) {
				Object.Destroy(root_);
			}
		}

		public void Update(bool on, Light sun, Vector3 camera, IReadOnlyList<Vector3> litLamps) {
			if (Time.unscaledTime < nextCheck_) {
				return;
			}
			nextCheck_ = Time.unscaledTime + 0.5f;

			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			var w = Weather.Current;
			int season = Seasons.Current;
			bool summerNight = on && (season == 1 || season < 0) && elevation < -2.0f && w.Rain < 0.05f;
			PlaceMoths(summerNight, litLamps, camera);

			sorted_.Clear();
			foreach (var f in fx_) {
				if (f.System != null && (f.Position - camera).sqrMagnitude < StopDistance * StopDistance) {
					sorted_.Add(f);
				}
			}
			sorted_.Sort((a, b) => (a.Position - camera).sqrMagnitude.CompareTo((b.Position - camera).sqrMagnitude));
			var allowed = new HashSet<Fx>();
			for (int i = 0; i < sorted_.Count && i < MaxRunning; ++i) {
				allowed.Add(sorted_[i]);
			}

			float wind = Shader.GetGlobalVector("_NFX_Wind").w;
			foreach (var f in fx_) {
				if (f.System == null) {
					continue;
				}
				bool suits = f.Preset == null || Suits(f.Preset, season, elevation, w);
				bool wanted = on && allowed.Contains(f) && suits;
				float distance = Vector3.Distance(f.Position, camera);
				f.Why = !on ? "map effects off" : distance >= StopDistance ? $"stopped beyond {StopDistance:0} m" : !allowed.Contains(f) ? "not among the 32 nearest"
					: wanted ? $"running ({f.System.particleCount} in the air)" : Waits(f.Preset);
				if (wanted && f.Preset == "leaves") {
					var emission = f.System.emission;
					emission.rateOverTime = f.Rate * Mathf.Clamp(0.3f + wind, 0.3f, 3.0f) * Mathf.Max(0.3f, w.Leaves * 2.0f);
				}
				if (wanted == f.Running) {
					continue;
				}
				f.Running = wanted;
				if (wanted) {
					f.System.gameObject.SetActive(true);
					f.System.Play(true);
				} else {
					// stop emitting; the ones in the air finish their life
					f.System.Stop(true, ParticleSystemStopBehavior.StopEmitting);
				}
			}
		}

		private static string Waits(string preset) {
			return preset switch {
				"fireflies" => "waits for a summer night without rain",
				"leaves" => "waits for autumn (or many fallen leaves)",
				"petals" => "waits for spring",
				"moths" => "waits for a summer night",
				"dust" => "waits for a clear, dry day",
				"pollen" => "waits for a spring or early summer day",
				_ => "waits"
			};
		}

		// what this marker's particles are doing (MarkerOverlay)
		public string StatusOf(Marker m) {
			return fx_.Find(f => f.Marker == m)?.Why;
		}

		// each particle box and what it is doing, for the Map tab
		public List<string> Status(Vector3 camera) {
			var lines = new List<string>();
			foreach (var f in fx_) {
				if (f.System != null && !f.Auto) {
					lines.Add($"{f.Preset ?? "own particles"}  ·  {Vector3.Distance(f.Position, camera):0} m  ·  {f.Why}");
				}
			}
			int moths = fx_.FindAll(f => f.Auto).Count;
			if (moths > 0) {
				lines.Add($"moths at {moths} lit lamps");
			}
			return lines;
		}

		private static bool Suits(string preset, int season, float elevation, WeatherState w) {
			bool dry = w.Rain < 0.05f;
			// no season set: everything that is not tied to one particular season
			switch (preset) {
				case "fireflies": return (season == 1 || season < 0) && elevation < -2.0f && dry;
				case "leaves": return season == 2 || (season < 0 && w.Leaves > 0.3f);
				case "petals": return season == 0;
				case "moths": return (season == 1 || season < 0) && elevation < -2.0f && dry;
				case "dust": return elevation > 10.0f && w.Cloud < 0.4f && dry;
				case "pollen": return (season == 0 || (season == 1 && Seasons.Position < 1.5f)) && elevation > 0.0f && dry;
				default: return true;
			}
		}

		// moths at the nearest lit lamps (summer nights)
		private void PlaceMoths(bool active, IReadOnlyList<Vector3> lamps, Vector3 camera) {
			var autos = fx_.FindAll(f => f.Auto);
			var wanted = new List<Vector3>();
			if (active && lamps != null) {
				var near = new List<Vector3>(lamps);
				near.Sort((a, b) => (a - camera).sqrMagnitude.CompareTo((b - camera).sqrMagnitude));
				for (int i = 0; i < near.Count && i < AutoMoths; ++i) {
					if ((near[i] - camera).sqrMagnitude < StopDistance * StopDistance) {
						wanted.Add(near[i]);
					}
				}
			}
			foreach (var a in autos) {
				if (!wanted.Exists(p => (p - a.Position).sqrMagnitude < 0.25f)) {
					if (a.System != null) {
						Object.Destroy(a.System.gameObject);
					}
					fx_.Remove(a);
				}
			}
			foreach (var p in wanted) {
				if (!autos.Exists(a => (a.Position - p).sqrMagnitude < 0.25f && a.System != null)) {
					var fx = Make("moths", p, Quaternion.identity, new Vector3(1.5f, 1.0f, 1.5f), 1.0f, true);
					if (fx != null) {
						fx_.Add(fx);
					}
				}
			}
		}
	}
}

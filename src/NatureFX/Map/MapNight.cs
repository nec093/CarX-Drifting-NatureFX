using System.Globalization;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// The night background (docs/map-authoring.md 6.5):
	//   stars: the star field the own engine's sky draws (Rendering.AtmospherePass), made here;
	//   skyline: [night] skyline = <azimuth> lays the lights of a town in the valley far away in that direction (spread,
	//   distance, height and lights keys), on at night (points only: the glow over a town would need a transparent
	//   material the game's shaders do not have).
	public class MapNight {
		private GameObject root_;
		private Cubemap stars_;
		private ParticleSystem skyline_;
		private Material lightMaterial_;
		private bool starsWanted_ = true;

		public string State { get; private set; } = "off";
		// the star field for the own engine's sky (Rendering.AtmospherePass draws it): the texture (nits), its
		// rotation (the sky turns) and whether the map wants stars
		public static Cubemap StarTexture { get; private set; }
		public static Vector3 StarRotation { get; private set; }
		public static bool StarsWanted { get; private set; } = true;

		public void Create() {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.Night");
			Object.DontDestroyOnLoad(root_);
		}

		public void Destroy() {
			ClearSkyline();
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (stars_ != null) {
				Object.Destroy(stars_);
			}
			root_ = null;
		}

		public void Load(MapProfile profile, ShaderBundle bundle) {
			ClearSkyline();
			starsWanted_ = !(profile.Loaded && string.Equals(profile.Get("night", "stars"), "false", System.StringComparison.OrdinalIgnoreCase));
			if (!profile.Loaded || !MapValues.TryFloat(profile.Get("night", "skyline"), out float azimuth)) {
				return;
			}
			float F(string key, float fallback) {
				return MapValues.TryFloat(profile.Get("night", key), out float v) ? v : fallback;
			}
			float spread = Mathf.Clamp(F("spread", 70.0f), 5.0f, 360.0f);
			float distance = Mathf.Max(300.0f, F("distance", 3000.0f));
			float height = F("height", -150.0f);
			int lights = Mathf.Clamp((int)F("lights", 900.0f), 10, 5000);
			var shader = Shader.Find("HDRP/Lit") ?? bundle.Get("NatureFX/LitAccumulation");
			if (shader == null) {
				return;
			}
			lightMaterial_ = new Material(shader) { name = "NatureFX.Skyline lights" };
			lightMaterial_.SetColor("_BaseColor", Color.black);
			lightMaterial_.SetColor("_EmissiveColor", new Color(1.0f, 0.75f, 0.45f) * 4000.0f);
			if (shader.name == "HDRP/Lit") {
				HDMaterial.ValidateMaterial(lightMaterial_);
			}
			skyline_ = MakeSystem("NatureFX.Skyline", lightMaterial_, lights);

			// the town: lights scattered over a band of land, denser towards the middle, a few streets in lines
			var rng = new System.Random(Mathf.RoundToInt(azimuth * 100.0f));
			var cam = Camera.main;
			var origin = cam != null ? cam.transform.position : Vector3.zero;
			var particles = new ParticleSystem.Particle[lights];
			for (int i = 0; i < lights; ++i) {
				float t = (float)(rng.NextDouble() + rng.NextDouble()) * 0.5f - 0.5f;
				float a = (azimuth + t * spread) * Mathf.Deg2Rad;
				float d = distance * (0.8f + (float)rng.NextDouble() * 0.6f);
				var p = new Vector3(origin.x + Mathf.Sin(a) * d, origin.y + height + (float)rng.NextDouble() * 20.0f, origin.z + Mathf.Cos(a) * d);
				particles[i] = new ParticleSystem.Particle {
					position = p,
					startSize = 2.0f + (float)rng.NextDouble() * 3.0f,
					startLifetime = 1e6f,
					remainingLifetime = 1e6f,
					startColor = Color.white
				};
			}
			skyline_.SetParticles(particles, lights);
			Kino.Log.Info(string.Format(CultureInfo.InvariantCulture, "[NatureFX] Night skyline: {0} lights at {1:0}° ({2:0}° wide), {3:0} m away", lights, azimuth, spread, distance));
		}

		private ParticleSystem MakeSystem(string name, Material material, int count) {
			var go = new GameObject(name);
			go.SetActive(false);
			var ps = go.AddComponent<ParticleSystem>();
			ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
			var main = ps.main;
			main.loop = false;
			main.playOnAwake = false;
			main.simulationSpace = ParticleSystemSimulationSpace.World;
			main.maxParticles = count;
			main.startLifetime = 1e6f;
			var emission = ps.emission;
			emission.enabled = false;
			var shape = ps.shape;
			shape.enabled = false;
			var r = go.GetComponent<ParticleSystemRenderer>();
			r.sharedMaterial = material;
			r.renderMode = ParticleSystemRenderMode.Billboard;
			r.shadowCastingMode = ShadowCastingMode.Off;
			r.receiveShadows = false;
			r.maxParticleSize = 1.0f;
			go.SetActive(true);
			ps.Pause();
			return ps;
		}

		private void ClearSkyline() {
			if (skyline_ != null) {
				Object.Destroy(skyline_.gameObject);
			}
			if (lightMaterial_ != null) {
				Object.Destroy(lightMaterial_);
			}
			skyline_ = null;
			lightMaterial_ = null;
		}

		public void Update(bool on, Light sun) {
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			float night = on ? Mathf.SmoothStep(0.0f, 1.0f, (-2.0f - elevation) / 10.0f) : 0.0f;

			if (skyline_ != null) {
				bool show = night > 0.01f;
				var r = skyline_.GetComponent<ParticleSystemRenderer>();
				if (r.enabled != show) {
					r.enabled = show;
				}
				if (show) {
					lightMaterial_.SetColor("_EmissiveColor", new Color(1.0f, 0.75f, 0.45f) * 4000.0f * night);
				}
			}

			// the star field for the own engine's sky: made once, turning slowly
			StarsWanted = starsWanted_;
			StarRotation = new Vector3(-35.0f, Time.unscaledTime * 0.0042f, 0.0f);
			if (starsWanted_ && StarTexture == null) {
				StarTexture = Stars();
			}
			State = (starsWanted_ ? "stars by the own sky" : "no stars (the map)") + (skyline_ != null ? ", skyline" : "");
		}

		// A star field in physical units (nits), as the eye sees a dark country sky: about 9000 stars down to magnitude
		// 6.5, each the illuminance of its magnitude (a magnitude 0 star gives 2.1 microlux) spread over the one texel
		// it falls in, so it shows as a point (at 256 texels a face, a texel was a third of a degree and every star a
		// big square). A faint airglow and the Milky Way's glow behind them, both thousandths of a nit.
		private Cubemap Stars() {
			if (stars_ != null) {
				return stars_;
			}
			const int size = 1024;
			var format = SystemInfo.SupportsTextureFormat(TextureFormat.RGB9e5Float) ? TextureFormat.RGB9e5Float : TextureFormat.RGBAHalf;
			stars_ = new Cubemap(size, format, false) { name = "NatureFX.Stars", filterMode = FilterMode.Bilinear };
			var rng = new System.Random(42);
			var faces = new Color[6][];
			// the glow, worked out at 64 texels a face and spread over the full size (it has no detail finer than that)
			const int coarse = 64;
			var bandNormal = Quaternion.Euler(60.0f, 0.0f, 25.0f) * Vector3.up;
			for (int f = 0; f < 6; ++f) {
				var low = new Color[coarse * coarse];
				for (int y = 0; y < coarse; ++y) {
					for (int x = 0; x < coarse; ++x) {
						var dir = Direction((CubemapFace)f, (x + 0.5f) / coarse * 2.0f - 1.0f, (y + 0.5f) / coarse * 2.0f - 1.0f);
						float band = Mathf.Exp(-Mathf.Pow(Vector3.Dot(dir, bandNormal) / 0.16f, 2.0f));
						float clump = Mathf.PerlinNoise(dir.x * 7.0f + 10.0f, dir.y * 7.0f + dir.z * 3.0f);
						float g = 0.0002f + band * (0.0004f + clump * 0.0012f);
						low[y * coarse + x] = new Color(g * 0.85f, g * 0.9f, g, 1.0f);
					}
				}
				var face = new Color[size * size];
				for (int y = 0; y < size; ++y) {
					float fy = Mathf.Clamp((y + 0.5f) / size * coarse - 0.5f, 0.0f, coarse - 1.001f);
					int y0 = (int)fy;
					float ty = fy - y0;
					for (int x = 0; x < size; ++x) {
						float fx = Mathf.Clamp((x + 0.5f) / size * coarse - 0.5f, 0.0f, coarse - 1.001f);
						int x0 = (int)fx;
						float tx = fx - x0;
						var a = Color.Lerp(low[y0 * coarse + x0], low[y0 * coarse + x0 + 1], tx);
						var b = Color.Lerp(low[(y0 + 1) * coarse + x0], low[(y0 + 1) * coarse + x0 + 1], tx);
						face[y * size + x] = Color.Lerp(a, b, ty);
					}
				}
				faces[f] = face;
			}
			// the stars: magnitudes from -1.5 to 6.5, three times as many for each magnitude fainter
			for (int i = 0; i < 9000; ++i) {
				float mag = -1.5f + Mathf.Log(1.0f + (float)rng.NextDouble() * (Mathf.Pow(3.0f, 8.0f) - 1.0f), 3.0f);
				float lux = Mathf.Pow(10.0f, -0.4f * (mag + 14.18f));
				int f = rng.Next(6);
				int x = rng.Next(size);
				int y = rng.Next(size);
				float u = (x + 0.5f) / size * 2.0f - 1.0f, v = (y + 0.5f) / size * 2.0f - 1.0f;
				float solidAngle = 4.0f / (size * size) / Mathf.Pow(1.0f + u * u + v * v, 1.5f);
				float t = (float)rng.NextDouble();
				var tint = t < 0.12f ? new Color(1.0f, 0.78f, 0.6f) : t < 0.3f ? new Color(0.8f, 0.88f, 1.0f) : t < 0.45f ? new Color(1.0f, 0.95f, 0.85f) : Color.white;
				faces[f][y * size + x] += tint * (lux / solidAngle);
			}
			for (int f = 0; f < 6; ++f) {
				stars_.SetPixels(faces[f], (CubemapFace)f);
			}
			stars_.Apply(false, true);
			return stars_;
		}

		private static Vector3 Direction(CubemapFace face, float u, float v) {
			return (face switch {
				CubemapFace.PositiveX => new Vector3(1.0f, -v, -u),
				CubemapFace.NegativeX => new Vector3(-1.0f, -v, u),
				CubemapFace.PositiveY => new Vector3(u, 1.0f, v),
				CubemapFace.NegativeY => new Vector3(u, -1.0f, -v),
				CubemapFace.PositiveZ => new Vector3(u, -v, 1.0f),
				_ => new Vector3(-u, -v, -1.0f)
			}).normalized;
		}
	}
}

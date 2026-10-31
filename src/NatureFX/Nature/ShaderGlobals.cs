using UnityEngine;

namespace NatureFX.Nature {
	// Must match unity/Assets/NatureFX/ShaderLibrary/NFX_*.hlsl
	public static class ShaderGlobals {
		public const int MaxInteractors = 8;

		// xyz: world wind direction (normalized), w: strength
		public static readonly int Wind = Shader.PropertyToID("_NFX_Wind");
		// x: gust frequency, y: gust strength, z: flutter frequency, w: flutter strength
		public static readonly int WindParams = Shader.PropertyToID("_NFX_WindParams");
		// Own time, so wind keeps moving smoothly regardless of game time scale
		public static readonly int WindTime = Shader.PropertyToID("_NFX_WindTime");

		// x: snow, y: leaves, z: dust, w: wetness (0..1)
		public static readonly int Accumulation = Shader.PropertyToID("_NFX_Accumulation");

		// xyz: absolute world position, w: radius. Cars push foliage away.
		public static readonly int Interactors = Shader.PropertyToID("_NFX_Interactors");
		public static readonly int InteractorCount = Shader.PropertyToID("_NFX_InteractorCount");

		// Wind weight for the Lit-based NatureFX/Foliage shader, which has no per-vertex root/tip weight:
		// the whole card moves, so keep this small.
		public static readonly int FoliageBend = Shader.PropertyToID("_NFX_FoliageBend");

		// x: rain intensity, y: snow depth (m)
		public static readonly int Weather = Shader.PropertyToID("_NFX_Weather");

		// tyre marks / dry line / compression map and xy: world origin, z: 1 / world size, w: enabled
		public static readonly int TrackMap = Shader.PropertyToID("_NFX_TrackMap");
		public static readonly int TrackParams = Shader.PropertyToID("_NFX_TrackParams");
		public static readonly int Road = Shader.PropertyToID("_NFX_Road");

		// direction towards the sun (for car rain highlights)
		public static readonly int SunDir = Shader.PropertyToID("_NFX_SunDirWS");

		// the noise texture of NFX_Common.hlsl (NFX_ValueNoise, NFX_Hash12): 1024 texels over 256 units, r smooth value
		// noise, b white noise per integer cell (read at the cell's texel). Made here once, so the shaders need no hash
		// maths (which took the shader compiler 10 to 30 seconds per variant)
		public static readonly int NoiseTex = Shader.PropertyToID("_NFX_NoiseTex");
		public const int NoisePeriod = 256;
		public const int NoiseTexels = 1024;
		private static Texture2D noise_;

		public static void EnsureNoiseTexture() {
			if (noise_ != null) {
				Shader.SetGlobalTexture(NoiseTex, noise_);
				return;
			}
			const int n = NoiseTexels;
			const int perCell = NoiseTexels / NoisePeriod;
			var pixels = new Color[n * n];
			for (int y = 0; y < n; ++y) {
				for (int x = 0; x < n; ++x) {
					float px = (x + 0.5f) / perCell;
					float py = (y + 0.5f) / perCell;
					pixels[y * n + x] = new Color(ValueNoise(px, py), ValueNoise(px + 97.0f, py + 51.0f),
						Hash12(x / perCell, y / perCell), Hash12(x / perCell + 131, y / perCell + 71));
				}
			}
			noise_ = new Texture2D(n, n, TextureFormat.RGBAHalf, false, true) {
				name = "NFX_Noise", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave
			};
			noise_.SetPixels(pixels);
			noise_.Apply(false, true);
			Shader.SetGlobalTexture(NoiseTex, noise_);
		}

		public static void DestroyNoiseTexture() {
			if (noise_ != null) {
				Object.Destroy(noise_);
				noise_ = null;
			}
		}

		// the hash the shaders used to compute (NFX_Hash12), on a lattice that wraps every NoisePeriod cells
		private static float Hash12(int x, int y) {
			x = ((x % NoisePeriod) + NoisePeriod) % NoisePeriod;
			y = ((y % NoisePeriod) + NoisePeriod) % NoisePeriod;
			float p0 = Frac(x * 0.1031f), p1 = Frac(y * 0.1031f), p2 = Frac(x * 0.1031f);
			float d = p0 * (p1 + 33.33f) + p1 * (p2 + 33.33f) + p2 * (p0 + 33.33f);
			p0 += d; p1 += d; p2 += d;
			return Frac((p0 + p1) * p2);
		}

		private static float ValueNoise(float px, float py) {
			int ix = Mathf.FloorToInt(px), iy = Mathf.FloorToInt(py);
			float fx = px - ix, fy = py - iy;
			float ux = fx * fx * (3.0f - 2.0f * fx), uy = fy * fy * (3.0f - 2.0f * fy);
			float a = Hash12(ix, iy), b = Hash12(ix + 1, iy), c = Hash12(ix, iy + 1), d = Hash12(ix + 1, iy + 1);
			return Mathf.Lerp(Mathf.Lerp(a, b, ux), Mathf.Lerp(c, d, ux), uy);
		}

		private static float Frac(float v) {
			return v - Mathf.Floor(v);
		}
	}
}

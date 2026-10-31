using UnityEngine;

namespace NatureFX.Rendering {
	// Density masks for HDRP's local volumetric fog (map fog volumes, tyre smoke): a seamless (tiling) 3D noise.
	// The old masks were 2D Perlin noise sheared through the third axis: they did not tile, so every repeat showed a
	// seam, and seen from the side the shear drew long straight streaks through the fog (they looked like rain).
	// Here: periodic 3D value noise, several octaves, with a billowy turn (1 - |2n - 1|) mixed in for soft lumps.
	// HDRP reads the density from the alpha (rgb tints the albedo): white with the noise in alpha.
	public static class VolumeNoise {
		public static Texture3D Make(string name, int size, int cells, float low, float high, int seed) {
			var tex = new Texture3D(size, size, size, TextureFormat.RGBA32, false) { name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear };
			var px = new Color32[size * size * size];
			for (int z = 0; z < size; ++z) {
				for (int y = 0; y < size; ++y) {
					for (int x = 0; x < size; ++x) {
						float f = 0.0f;
						float amp = 0.55f;
						float total = 0.0f;
						int period = cells;
						for (int o = 0; o < 4; ++o) {
							float scale = (float)period / size;
							float n = Value(x * scale, y * scale, z * scale, period, seed + o * 131);
							if (o == 0) {
								n = Mathf.Lerp(n, 1.0f - Mathf.Abs(2.0f * n - 1.0f), 0.5f); // billows
							}
							f += n * amp;
							total += amp;
							amp *= 0.5f;
							period *= 2;
						}
						f /= total;
						byte b = (byte)Mathf.Clamp(Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(low, high, f)) * 255.0f, 0.0f, 255.0f);
						px[(z * size + y) * size + x] = new Color32(255, 255, 255, b);
					}
				}
			}
			tex.SetPixels32(px);
			tex.Apply(false, true);
			return tex;
		}

		// value noise on a lattice that repeats every 'period' cells
		private static float Value(float x, float y, float z, int period, int seed) {
			int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y), z0 = Mathf.FloorToInt(z);
			float fx = Smooth(x - x0), fy = Smooth(y - y0), fz = Smooth(z - z0);
			float c000 = Hash(x0, y0, z0, period, seed), c100 = Hash(x0 + 1, y0, z0, period, seed);
			float c010 = Hash(x0, y0 + 1, z0, period, seed), c110 = Hash(x0 + 1, y0 + 1, z0, period, seed);
			float c001 = Hash(x0, y0, z0 + 1, period, seed), c101 = Hash(x0 + 1, y0, z0 + 1, period, seed);
			float c011 = Hash(x0, y0 + 1, z0 + 1, period, seed), c111 = Hash(x0 + 1, y0 + 1, z0 + 1, period, seed);
			float a = Mathf.Lerp(Mathf.Lerp(c000, c100, fx), Mathf.Lerp(c010, c110, fx), fy);
			float b = Mathf.Lerp(Mathf.Lerp(c001, c101, fx), Mathf.Lerp(c011, c111, fx), fy);
			return Mathf.Lerp(a, b, fz);
		}

		private static float Smooth(float t) {
			return t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);
		}

		private static float Hash(int x, int y, int z, int period, int seed) {
			x = ((x % period) + period) % period;
			y = ((y % period) + period) % period;
			z = ((z % period) + period) % period;
			unchecked {
				uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(seed * 2654435761u);
				h ^= h >> 13;
				h *= 0x5bd1e995;
				h ^= h >> 15;
				return (h & 0xffffff) / (float)0xffffff;
			}
		}
	}
}

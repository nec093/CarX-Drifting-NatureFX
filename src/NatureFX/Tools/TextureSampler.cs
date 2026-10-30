using System;
using System.Collections.Generic;
using System.IO;
using KSL.API;
using UnityEngine;
using Object = UnityEngine.Object;

namespace NatureFX.Tools {
	// Shader rebuild: saves the textures of a few materials per game shader as PNG next to the inventory, packed
	// maps also split into their R, G, B, A channels side by side, so the layout of the game's packed textures
	// (which channel is metal, smoothness, AO or the paint mask) can be seen instead of guessed.
	public static class TextureSampler {
		private const int MaterialsPerShader = 3;
		private const int MaxSize = 512;

		public static void Export(IEnumerable<(string Shader, Material Material, Rebuild.MaterialSnapshot Original)> materials, string folder) {
			try {
				var perShader = new Dictionary<string, int>();
				int files = 0;
				foreach (var (shader, m, original) in materials) {
					perShader.TryGetValue(shader, out int n);
					if (n >= MaterialsPerShader) {
						continue;
					}
					perShader[shader] = n + 1;

					string dir = Path.Combine(folder, Safe(shader));
					Directory.CreateDirectory(dir);
					foreach (var (name, texture) in Textures(m, original)) {
						if (texture is not Texture2D t2) {
							continue;
						}
						string baseName = $"{Safe(m.name)}{name}";
						var pixels = Read(t2, out int w, out int h);
						if (pixels == null) {
							continue;
						}
						File.WriteAllBytes(Path.Combine(dir, baseName + ".png"), Encode(pixels, w, h, -1));
						// every map but the colour one: channels side by side (R G B A)
						if (!name.Contains("BaseColor") && !name.Contains("Base") && !name.Contains("Main")) {
							File.WriteAllBytes(Path.Combine(dir, baseName + "_RGBA.png"), EncodeChannels(pixels, w, h));
						}
						files += 2;
					}
				}
				Kino.Log.Info($"[NatureFX] Texture samples: {files} files written to {folder}");
			} catch (Exception e) {
				Kino.Log.Error($"[NatureFX] Texture samples failed: {e.Message}");
			}
		}

		private static IEnumerable<(string Name, Texture Texture)> Textures(Material m, Rebuild.MaterialSnapshot original) {
			if (original != null) {
				foreach (var kv in original.Textures) {
					if (kv.Value.Texture != null) {
						yield return (kv.Key, kv.Value.Texture);
					}
				}
				yield break;
			}
			foreach (string name in m.GetTexturePropertyNames()) {
				var t = m.GetTexture(name);
				if (t != null) {
					yield return (name, t);
				}
			}
		}

		// The texture's texels as stored (linear read, no sRGB conversion), scaled down to MaxSize
		private static Color32[] Read(Texture2D source, out int w, out int h) {
			float scale = Mathf.Min(1.0f, MaxSize / (float)Mathf.Max(source.width, source.height));
			w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
			h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
			var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
			var previous = RenderTexture.active;
			try {
				Graphics.Blit(source, rt);
				RenderTexture.active = rt;
				var copy = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
				copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
				copy.Apply();
				var pixels = copy.GetPixels32();
				Object.Destroy(copy);
				return pixels;
			} finally {
				RenderTexture.active = previous;
				RenderTexture.ReleaseTemporary(rt);
			}
		}

		private static byte[] Encode(Color32[] pixels, int w, int h, int unused) {
			var t = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
			t.SetPixels32(pixels);
			t.Apply();
			byte[] png = ImageConversion.EncodeToPNG(t);
			Object.Destroy(t);
			return png;
		}

		// R, G, B and A as four grey images in a row
		private static byte[] EncodeChannels(Color32[] pixels, int w, int h) {
			var t = new Texture2D(w * 4, h, TextureFormat.RGBA32, false, true);
			var row = new Color32[w * 4 * h];
			for (int y = 0; y < h; ++y) {
				for (int x = 0; x < w; ++x) {
					var c = pixels[y * w + x];
					row[y * w * 4 + x] = new Color32(c.r, c.r, c.r, 255);
					row[y * w * 4 + w + x] = new Color32(c.g, c.g, c.g, 255);
					row[y * w * 4 + 2 * w + x] = new Color32(c.b, c.b, c.b, 255);
					row[y * w * 4 + 3 * w + x] = new Color32(c.a, c.a, c.a, 255);
				}
			}
			t.SetPixels32(row);
			t.Apply();
			byte[] png = ImageConversion.EncodeToPNG(t);
			Object.Destroy(t);
			return png;
		}

		private static string Safe(string name) {
			foreach (char c in Path.GetInvalidFileNameChars()) {
				name = name.Replace(c, '_');
			}
			return name.Replace('/', '_').Replace(' ', '_');
		}
	}
}

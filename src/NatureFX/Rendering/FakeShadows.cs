using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Rendering {
	// The game's painted shadows: CarX lays dark see-through planes under cars, trees and bushes (blob / fake shadow
	// planes) in place of real shadows. With the ray traced sun shadows they come on top of the real ones, so they
	// are hidden (forceRenderingOff) while those run, and shown again otherwise. Found by name: renderers whose object,
	// material or shader name says shadow; what is hidden is logged once per name, to check the match.
	public class FakeShadows {
		private const float SyncInterval = 2.0f;

		public string Status { get; private set; } = "off";

		private readonly HashSet<Renderer> hidden_ = new();
		private readonly HashSet<string> logged_ = new();
		private float nextSync_;

		public void Update(bool hide) {
			if (!hide) {
				if (hidden_.Count > 0) {
					ShowAll();
				}
				Status = "off";
				return;
			}
			if (Time.unscaledTime < nextSync_) {
				return;
			}
			nextSync_ = Time.unscaledTime + SyncInterval;
			hidden_.RemoveWhere(r => r == null);
			foreach (var r in Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
				if (hidden_.Contains(r) || r.forceRenderingOff || !IsFakeShadow(r, out string what)) {
					continue;
				}
				r.forceRenderingOff = true;
				hidden_.Add(r);
				if (logged_.Add(what)) {
					KSL.API.Kino.Log.Info($"[NatureFX] Fake shadows: hiding {what} (e.g. {Path(r.transform)})");
				}
			}
			Status = $"{hidden_.Count} painted shadow planes hidden";
		}

		public void ShowAll() {
			foreach (var r in hidden_) {
				if (r != null) {
					r.forceRenderingOff = false;
				}
			}
			hidden_.Clear();
		}

		// A plane that only darkens: its name says shadow, and it casts none itself (real geometry named "...shadow..."
		// would; a shadow-only caster is left alone, the ray tracing scene skips those anyway)
		private static bool IsFakeShadow(Renderer r, out string what) {
			what = null;
			if (r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off) {
				return false;
			}
			string objectName = r.gameObject.name.ToLowerInvariant();
			foreach (var m in r.sharedMaterials) {
				if (m == null) {
					continue;
				}
				string shader = m.shader != null ? m.shader.name : "";
				string material = m.name;
				if (Says(objectName) || Says(material.ToLowerInvariant()) || Says(shader.ToLowerInvariant())) {
					what = $"material '{material}' ({shader})";
					return true;
				}
			}
			return false;
		}

		private static bool Says(string name) {
			return name.Contains("shadow") && !name.Contains("shadowless") && !name.Contains("noshadow");
		}

		private static string Path(Transform t) {
			string path = t.name;
			for (int i = 0; i < 3 && t.parent != null; ++i) {
				t = t.parent;
				path = t.name + "/" + path;
			}
			return path;
		}
	}
}

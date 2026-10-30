using System.Collections.Generic;
using System.Linq;
using System.Text;
using KSL.API;
using UnityEngine;
using UnityEngine.Rendering;

namespace NatureFX.Tools {
	// Logs every nfx_* / nocol_nfx* object of the loaded map with what survived the .knmap build: active state,
	// components and whether they are enabled, Transform, TextMesh text length, mesh vertex colours, colliders
	// Kino may have added, and the test material value. Used with the NatureFX data test map
	// (tools/kino_sdk/NfxDataTestMap.cs) to check docs/map-authoring.md chapter 0 in the real game.
	public static class MapDataProbe {
		private static readonly HashSet<int> logged_ = new();

		public static void Run() {
			var lines = new List<string>();
			foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None)) {
				string name = t.gameObject.name;
				if (!name.StartsWith("nfx") && !name.StartsWith("nocol_nfx")) {
					continue;
				}
				if (!logged_.Add(t.gameObject.GetInstanceID())) {
					continue; // already reported for this scene load
				}
				lines.Add(Describe(t));
			}
			if (lines.Count > 0) {
				Kino.Log.Info($"[NatureFX] Map data: {lines.Count} NatureFX objects in the map\n  " + string.Join("\n  ", lines));
			}
		}

		private static string Describe(Transform t) {
			var go = t.gameObject;
			var sb = new StringBuilder();
			sb.Append($"{go.name}: active {go.activeInHierarchy}, pos {Fmt(t.position)}, rot {Fmt(t.eulerAngles)}, scale {Fmt(t.lossyScale)}");

			var components = go.GetComponents<Component>()
				.Where(c => !(c is Transform))
				.Select(c => c == null ? "MISSING SCRIPT" : c is Behaviour b ? $"{c.GetType().Name}({(b.enabled ? "on" : "off")})" : c is Renderer r ? $"{c.GetType().Name}({(r.enabled ? "on" : "off")})" : c is Collider col ? $"{c.GetType().Name}({(col.enabled ? "on" : "off")})" : c.GetType().Name);
			sb.Append($", components [{string.Join(", ", components)}]");

			var text = go.GetComponent<TextMesh>();
			if (text != null) {
				string s = text.text ?? "";
				sb.Append($", text {s.Length} chars, lines {s.Split('\n').Length}, end marker {s.Contains("#END-CHECK")}");
			}

			var mesh = go.GetComponent<MeshFilter>()?.sharedMesh;
			if (mesh != null) {
				sb.Append($", mesh {mesh.name} ({mesh.vertexCount} verts, readable {mesh.isReadable}, colours {mesh.HasVertexAttribute(VertexAttribute.Color)})");
				if (mesh.isReadable && mesh.HasVertexAttribute(VertexAttribute.Color) && mesh.vertexCount > 0) {
					var c = mesh.colors[0];
					sb.Append($" first colour ({c.r:0.00}, {c.g:0.00}, {c.b:0.00}, {c.a:0.00})");
				}
			}

			var renderer = go.GetComponent<Renderer>();
			var mat = renderer != null ? renderer.sharedMaterial : null;
			if (mat != null) {
				// GetFloat on a property the shader lacks logs a Unity error, so only read it when present
				bool has = mat.HasProperty("_NFX_TestValue");
				sb.Append($", material {mat.name} ({mat.shader?.name}), _NFX_TestValue {(has ? mat.GetFloat("_NFX_TestValue").ToString("0.00") : "absent")}");
			}
			return sb.ToString();
		}

		public static void Reset() {
			logged_.Clear();
		}

		private static string Fmt(Vector3 v) {
			return $"({v.x:0.0}, {v.y:0.0}, {v.z:0.0})";
		}
	}
}

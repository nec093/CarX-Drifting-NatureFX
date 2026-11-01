using System.Collections.Generic;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;

namespace NatureFX.Rendering {
	// Roadmap E: while the own engine draws the glass whole (Glass.hlsl with NFX_FLAG_OWN_GLASS), HDRP must not draw it
	// too. The game's glass is one material slot of a renderer (a car's windows share the renderer with its body), so
	// each glass slot RayTracingScene found gets Hidden/NatureFX/Invisible in its place; the scene keeps reading the
	// originals (Originals) so the glass stays glass for the own engine. Back when it is off, or the glass pass stops.
	public static class OwnGlass {
		public const string ShaderName = "Hidden/NatureFX/Invisible";

		// the glass slots the ray tracing scene found: the renderer and its material index
		public static readonly List<(Renderer Renderer, int Slot)> Slots = new();
		private static readonly Dictionary<Renderer, Material[]> originals_ = new();
		private static Material invisible_;

		public static bool Active { get; private set; }
		public static int Hidden => originals_.Count;

		public static void Create(ShaderBundle bundle) {
			var shader = bundle?.Get(ShaderName);
			if (shader == null) {
				Kino.Log.Warning($"[NatureFX] {ShaderName} not in the shader bundle; HDRP keeps drawing the glass");
				return;
			}
			if (invisible_ == null) {
				invisible_ = new Material(shader) { name = "NatureFX.InvisibleGlass", hideFlags = HideFlags.DontSave };
			} else {
				invisible_.shader = shader;
			}
		}

		// the materials a renderer had before its glass was hidden (null: not hidden)
		public static Material[] Originals(Renderer r) {
			return originals_.TryGetValue(r, out var m) ? m : null;
		}

		public static void Update(bool on) {
			on &= invisible_ != null;
			Active = on;
			if (!on) {
				Restore();
				return;
			}
			Slots.RemoveAll(s => s.Renderer == null);
			foreach (var (r, slot) in Slots) {
				if (!originals_.TryGetValue(r, out var original)) {
					original = r.sharedMaterials;
					originals_[r] = original;
				}
				var now = r.sharedMaterials;
				if (slot < now.Length && now[slot] != invisible_) {
					now[slot] = invisible_;
					r.sharedMaterials = now;
				}
			}
		}

		public static void Restore() {
			foreach (var kv in originals_) {
				if (kv.Key != null) {
					kv.Key.sharedMaterials = kv.Value;
				}
			}
			originals_.Clear();
			Active = false;
		}
	}
}

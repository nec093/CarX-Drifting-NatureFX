using System.Collections.Generic;
using UnityEngine;

namespace NatureFX.Rendering {
	// The track's own reflection probes while the own engine draws the sky (A3). They were rendered by HDRP once, at
	// load, under whatever sky HDRP had then (its own day sky, or the engine's cube while it was still black), and are
	// not rendered again: a mirror or a wet road showed a day sky at night, or a black one at noon, and which depended
	// on the restart. Off, HDRP falls back to its sky reflection, which is the engine's sky; the near things in the
	// reflections are the traced reflections'. Back on when the own sky is.
	public class SceneProbes {
		private readonly List<ReflectionProbe> off_ = new();
		private bool on_;
		private float nextFind_;

		public int Count => off_.Count;

		public void Update(bool nativeSky) {
			if (!nativeSky) {
				if (on_) {
					Restore();
				}
				return;
			}
			on_ = true;
			// the track loads its probes with it
			if (Time.unscaledTime < nextFind_) {
				return;
			}
			nextFind_ = Time.unscaledTime + 2.0f;
			off_.RemoveAll(p => p == null);
			foreach (var p in Object.FindObjectsByType<ReflectionProbe>(FindObjectsSortMode.None)) {
				if (!p.enabled || p.name.StartsWith("NatureFX.")) {
					continue;
				}
				p.enabled = false;
				off_.Add(p);
			}
		}

		public void Restore() {
			foreach (var p in off_) {
				if (p != null) {
					p.enabled = true;
				}
			}
			off_.Clear();
			on_ = false;
		}
	}
}

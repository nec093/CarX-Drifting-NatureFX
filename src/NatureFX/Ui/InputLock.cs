using System;
using System.Collections.Generic;
using KSL.API;
using Rewired;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace NatureFX.Ui {
	// Locks the game behind the F8 window, like KSL's own menu. The window is IMGUI, which the game never sees:
	//   - uGUI: the EventSystems' input modules are switched off, so no clicks, hovers or navigation reach the
	//     game's menus (the EventSystem itself stays, as game code reads EventSystem.current)
	//   - Rewired (the game's input): the mouse is disabled, and the keyboard too while a text field is focused
	// Everything is restored to its previous state on unlock.
	public class InputLock {
		private readonly List<BaseInputModule> modules_ = new();
		private bool locked_;
		private float nextScan_;
		private bool mouseLocked_;
		private bool keyboardLocked_;
		private bool mouseWasEnabled_;
		private bool keyboardWasEnabled_;

		// Called every frame while the window is open: scenes load new EventSystems, and the game may re-enable things.
		public void Hold(bool lockKeyboard) {
			// a scene search every frame is wasteful; new EventSystems only appear on scene loads
			if (!locked_ || Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + 0.5f;
				foreach (var module in Object.FindObjectsByType<BaseInputModule>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
					if (module.enabled) {
						module.enabled = false;
						modules_.Add(module);
					}
				}
			}
			locked_ = true;

			try {
				if (!ReInput.isReady) {
					return;
				}
				var mouse = ReInput.controllers.Mouse;
				if (mouse != null) {
					if (!mouseLocked_) {
						mouseWasEnabled_ = mouse.enabled;
						mouseLocked_ = true;
					}
					mouse.enabled = false;
				}

				var keyboard = ReInput.controllers.Keyboard;
				if (keyboard != null) {
					if (lockKeyboard && !keyboardLocked_) {
						keyboardWasEnabled_ = keyboard.enabled;
						keyboardLocked_ = true;
						keyboard.enabled = false;
					} else if (!lockKeyboard && keyboardLocked_) {
						keyboard.enabled = keyboardWasEnabled_;
						keyboardLocked_ = false;
					}
				}
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Input lock: Rewired not usable ({e.Message})");
			}
		}

		public void Release() {
			if (!locked_) {
				return;
			}
			locked_ = false;

			foreach (var module in modules_) {
				if (module != null) {
					module.enabled = true;
				}
			}
			modules_.Clear();

			try {
				if (ReInput.isReady) {
					if (mouseLocked_ && ReInput.controllers.Mouse != null) {
						ReInput.controllers.Mouse.enabled = mouseWasEnabled_;
					}
					if (keyboardLocked_ && ReInput.controllers.Keyboard != null) {
						ReInput.controllers.Keyboard.enabled = keyboardWasEnabled_;
					}
				}
			} catch (Exception) {
				// Rewired shut down with the game; nothing to restore
			}
			mouseLocked_ = keyboardLocked_ = false;
		}
	}
}

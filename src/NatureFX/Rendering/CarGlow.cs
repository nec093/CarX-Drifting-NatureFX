using System.Collections.Generic;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// Light from the cars themselves: brake discs that glow after hard braking (a small orange light at each wheel hub,
	// its colour the disc's temperature, cooling over several seconds) and afterfire (flashes at the exhaust when the
	// throttle is lifted at speed). They are HDRP lights, so they light the wheel arches, the road and the tyre smoke.
	// Cars come from TrackMap; the exhaust is taken at the rear of the car's bounds.
	public class CarGlow {
		private const int MaxCars = 4;

		private class CarState {
			public Rigidbody Body;
			public Vector3 LastVelocity;
			public float LastAccel;
			public float Heat;          // 0 cold .. 1 glowing orange
			public float RearOffset;    // m from the body's origin to the rear bumper
			public float FlameUntil;
			public float NextPop;
			public int Pops;
			public readonly Dictionary<Transform, Light> Discs = new();
			public Light Flame;
		}

		private readonly Dictionary<Rigidbody, CarState> cars_ = new();
		private readonly List<Rigidbody> dead_ = new();
		private GameObject root_;

		public void Create() {
			if (root_ == null) {
				root_ = new GameObject("NatureFX.CarGlow");
				Object.DontDestroyOnLoad(root_);
			}
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			root_ = null;
			cars_.Clear();
		}

		public void Update(bool on, Vector3 camera) {
			if (root_ == null) {
				return;
			}
			float dt = Mathf.Max(Time.deltaTime, 1e-4f);
			dead_.Clear();
			foreach (var kv in cars_) {
				if (kv.Key == null || !TrackMap.Bodies.Contains(kv.Key)) {
					dead_.Add(kv.Key);
				}
			}
			foreach (var body in dead_) {
				Remove(body);
			}

			var bodies = new List<Rigidbody>(TrackMap.Bodies);
			bodies.RemoveAll(b => b == null); // a destroyed body's position throws in the sort
			bodies.Sort((a, b) => (a.position - camera).sqrMagnitude.CompareTo((b.position - camera).sqrMagnitude));
			for (int i = 0; i < bodies.Count; ++i) {
				var body = bodies[i];
				if (body == null) {
					continue;
				}
				if (!cars_.TryGetValue(body, out var car)) {
					if (i >= MaxCars) {
						continue;
					}
					car = new CarState { Body = body, LastVelocity = body.velocity, RearOffset = RearOf(body) };
					cars_[body] = car;
				}
				var v = body.velocity;
				var forward = body.transform.forward;
				float speed = Vector3.Dot(v, forward);
				float accel = Vector3.Dot((v - car.LastVelocity) / dt, forward);
				car.LastVelocity = v;

				// brakes: heat from the energy taken out, cooling faster at speed (air through the wheel)
				if (on && accel < -5.0f && speed > 6.0f) {
					car.Heat += (-accel - 5.0f) * speed * dt * 0.0015f;
				}
				car.Heat = Mathf.Clamp01(car.Heat - dt * 0.05f * (1.0f + Mathf.Abs(speed) * 0.02f));

				// afterfire: off the throttle at speed (strong acceleration, then a sudden drop)
				if (on && speed > 14.0f && car.LastAccel > 2.0f && accel < -0.5f && Random.value < 0.6f) {
					car.Pops = Random.Range(2, 5);
					car.NextPop = Time.time;
				}
				car.LastAccel = Mathf.Lerp(car.LastAccel, accel, 1.0f - Mathf.Exp(-dt * 10.0f));
				if (car.Pops > 0 && Time.time >= car.NextPop) {
					--car.Pops;
					car.FlameUntil = Time.time + Random.Range(0.03f, 0.07f);
					car.NextPop = Time.time + Random.Range(0.06f, 0.18f);
				}

				UpdateDiscs(car, on);
				UpdateFlame(car, on);
			}
		}

		private void UpdateDiscs(CarState car, bool on) {
			foreach (var c in TrackMap.Contacts) {
				if (c.Body == car.Body && c.Wheel != null && !car.Discs.ContainsKey(c.Wheel)) {
					car.Discs[c.Wheel] = MakeLight("Disc", LightType.Point, 0.9f);
				}
			}
			float glow = Mathf.SmoothStep(0.0f, 1.0f, (car.Heat - 0.35f) / 0.65f);
			foreach (var kv in car.Discs) {
				var light = kv.Value;
				if (light == null) {
					continue;
				}
				bool show = on && glow > 0.01f && kv.Key != null;
				light.enabled = show;
				if (!show) {
					continue;
				}
				// the disc sits on the inboard side of the wheel
				var toCenter = car.Body.worldCenterOfMass - kv.Key.position;
				var side = car.Body.transform.right * Mathf.Sign(Vector3.Dot(toCenter, car.Body.transform.right));
				light.transform.position = kv.Key.position + side * 0.12f;
				var hd = light.GetComponent<HDAdditionalLightData>();
				hd.SetColor(Color.white, Mathf.Lerp(900.0f, 1500.0f, glow));
				hd.SetIntensity(Mathf.Lerp(2.0f, 120.0f, glow * glow), LightUnit.Lumen);
			}
		}

		private void UpdateFlame(CarState car, bool on) {
			bool show = on && Time.time < car.FlameUntil;
			if (car.Flame == null) {
				if (!show) {
					return;
				}
				car.Flame = MakeLight("Afterfire", LightType.Point, 5.0f);
			}
			car.Flame.enabled = show;
			if (show) {
				var t = car.Body.transform;
				car.Flame.transform.position = car.Body.worldCenterOfMass - t.forward * car.RearOffset - t.up * 0.25f;
				var hd = car.Flame.GetComponent<HDAdditionalLightData>();
				hd.SetColor(Color.white, Random.Range(1700.0f, 2600.0f));
				hd.SetIntensity(Random.Range(2500.0f, 6000.0f), LightUnit.Lumen);
			}
		}

		private Light MakeLight(string name, LightType type, float range) {
			var go = new GameObject("NatureFX." + name);
			go.transform.SetParent(root_.transform, false);
			var light = go.AddComponent<Light>();
			light.type = type;
			light.shadows = LightShadows.None;
			var hd = go.AddComponent<HDAdditionalLightData>();
			hd.SetRange(range);
			hd.shapeRadius = 0.05f;
			hd.affectsVolumetric = true;
			light.enabled = false;
			return light;
		}

		private void Remove(Rigidbody body) {
			// a destroyed body is still a usable key (Unity's null only compares equal to null)
			if (cars_.TryGetValue(body, out var car)) {
				foreach (var l in car.Discs.Values) {
					if (l != null) {
						Object.Destroy(l.gameObject);
					}
				}
				if (car.Flame != null) {
					Object.Destroy(car.Flame.gameObject);
				}
			}
			cars_.Remove(body);
		}

		// distance from the centre of mass to the rear of the car's renderers
		private static float RearOf(Rigidbody body) {
			float rear = 2.0f;
			var t = body.transform;
			foreach (var r in body.GetComponentsInChildren<Renderer>()) {
				var b = r.bounds;
				for (int i = 0; i < 8; ++i) {
					var corner = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
					rear = Mathf.Max(rear, -Vector3.Dot(corner - body.worldCenterOfMass, t.forward));
				}
			}
			return Mathf.Min(rear, 3.0f);
		}
	}
}

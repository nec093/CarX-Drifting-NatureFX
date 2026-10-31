using System.Collections.Generic;
using System.Runtime.InteropServices;
using KSL.API;
using UnityEngine;

namespace NatureFX.Nature {
	// Tyre marks, dry lines in the rain and ruts in snow / leaves: a world aligned map around the camera
	// written by NFX_TrackMap.compute and read by the NatureFX ground shaders.
	public class TrackMap {
		public const string ComputeName = "NFX_TrackMap";

		private const int Size = 2048;           // texels
		private const float WorldSize = 256.0f;  // metres -> 12.5 cm per texel
		private const float RecenterDistance = 80.0f;
		private const int MaxStamps = 64;
		private const float CarScanInterval = 1.0f;
		private const float MinCarMass = 400.0f;
		private const float RegionMetres = 16.0f;

		[StructLayout(LayoutKind.Sequential)]
		private struct StampData {
			public Vector2 A;
			public Vector2 B;
			public float Width;
			public float Rubber;
			public float Press;
			public float Dry;
		}

		private class Wheel {
			public Transform Transform;
			public float Radius;
			public float Width;
			public Vector3 LastContact;
			public bool HadContact;
		}

		private class Car {
			public Rigidbody Body;
			public readonly List<Wheel> Wheels = new();
		}

		// What the wheels did this frame, for the tyre smoke, the spray and the brake glow (Rendering/TireSmoke.cs,
		// Rendering/CarGlow.cs): contact point, how much it slides (0..1), the car's velocity there, the wheel and car
		public struct WheelContact {
			public Vector3 Point;
			public float Slide;
			public Vector3 Velocity;
			public Transform Wheel;
			public Rigidbody Body;
		}

		public static readonly List<WheelContact> Contacts = new();
		// the cars being tracked (CarGlow)
		public static readonly List<Rigidbody> Bodies = new();

		private static readonly int MapId = Shader.PropertyToID("_NFX_Map");
		private static readonly int StampsId = Shader.PropertyToID("_NFX_Stamps");
		private static readonly int StampCountId = Shader.PropertyToID("_NFX_StampCount");
		private static readonly int MapParamsId = Shader.PropertyToID("_NFX_MapParams");
		private static readonly int RegionId = Shader.PropertyToID("_NFX_Region");
		private static readonly int DecayId = Shader.PropertyToID("_NFX_DecayAmount");

		private ComputeShader compute_;
		private RenderTexture map_;
		private ComputeBuffer stamps_;
		private readonly StampData[] stampData_ = new StampData[MaxStamps];
		private readonly List<Car> cars_ = new();
		private readonly Collider[] overlap_ = new Collider[256];
		private Vector2 origin_;
		private bool centered_;
		private float nextScan_;
		private float decayTimer_;
		private int kStamp_, kDecay_, kClear_;

		public bool Available => compute_ != null;

		public void Create(ShaderBundle bundle) {
			compute_ = bundle.GetCompute(ComputeName);
			if (compute_ == null) {
				Kino.Log.Warning($"[NatureFX] {ComputeName} not in the shader bundle; tyre marks disabled");
				return;
			}

			kStamp_ = compute_.FindKernel("Stamp");
			kDecay_ = compute_.FindKernel("Decay");
			kClear_ = compute_.FindKernel("Clear");

			map_ = new RenderTexture(Size, Size, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear) {
				name = "NFX_TrackMap",
				enableRandomWrite = true,
				wrapMode = TextureWrapMode.Clamp,
				filterMode = FilterMode.Bilinear,
				hideFlags = HideFlags.DontSave
			};
			map_.Create();
			stamps_ = new ComputeBuffer(MaxStamps, Marshal.SizeOf<StampData>());
			Shader.SetGlobalTexture(ShaderGlobals.TrackMap, map_);
		}

		public void Destroy() {
			stamps_?.Release();
			if (map_ != null) {
				map_.Release();
				Object.Destroy(map_);
			}
			Shader.SetGlobalVector(ShaderGlobals.TrackParams, Vector4.zero);
			stamps_ = null;
			map_ = null;
		}

		public void Clear() {
			if (compute_ == null) {
				return;
			}
			SetMapParams();
			compute_.SetTexture(kClear_, MapId, map_);
			compute_.Dispatch(kClear_, Size / 8, Size / 8, 1);
		}

		public void Update(ISettings s) {
			Contacts.Clear();
			if (compute_ == null) {
				Bodies.Clear();
				return;
			}
			bool on = s.Enabled && s.TireMarks;
			var cam = Camera.main;
			if (!on || cam == null) {
				// the bodies too: left over from before, they were destroyed with the car / scene and the brake glow
				// sorted them by position (an exception every frame)
				Bodies.Clear();
				Shader.SetGlobalVector(ShaderGlobals.TrackParams, Vector4.zero);
				return;
			}

			// keep the map around the camera; moving it loses the old marks
			var camXZ = new Vector2(cam.transform.position.x, cam.transform.position.z);
			var center = origin_ + Vector2.one * (WorldSize * 0.5f);
			if (!centered_ || Vector2.Distance(camXZ, center) > RecenterDistance) {
				origin_ = camXZ - Vector2.one * (WorldSize * 0.5f);
				centered_ = true;
				Clear();
			}
			Shader.SetGlobalVector(ShaderGlobals.TrackParams, new Vector4(origin_.x, origin_.y, 1.0f / WorldSize, 1.0f));

			if (Time.unscaledTime >= nextScan_) {
				nextScan_ = Time.unscaledTime + CarScanInterval;
				ScanCars(cam.transform.position);
			}

			SetMapParams();
			Bodies.Clear();
			foreach (var car in cars_) {
				StampCar(car, s);
				if (car.Body != null) {
					Bodies.Add(car.Body);
				}
			}

			// rain gradually re-wets the dry line
			float rain = Weather.Current.Rain;
			decayTimer_ += Time.deltaTime;
			if (rain > 0.01f && decayTimer_ > 0.5f) {
				compute_.SetFloat(DecayId, rain * decayTimer_ * 0.05f);
				compute_.SetTexture(kDecay_, MapId, map_);
				compute_.Dispatch(kDecay_, Size / 8, Size / 8, 1);
				decayTimer_ = 0.0f;
			}
		}

		private void SetMapParams() {
			compute_.SetVector(MapParamsId, new Vector4(origin_.x, origin_.y, WorldSize, Size));
		}

		private void StampCar(Car car, ISettings s) {
			if (car.Body == null) {
				return;
			}

			int count = 0;
			var center = Vector3.zero;
			foreach (var wheel in car.Wheels) {
				if (wheel.Transform == null || count >= MaxStamps) {
					continue;
				}

				var pos = wheel.Transform.position;
				var down = -car.Body.transform.up;
				if (!Physics.Raycast(pos, down, out var hit, wheel.Radius * 1.6f, ~0, QueryTriggerInteraction.Ignore) || hit.rigidbody == car.Body) {
					wheel.HadContact = false;
					continue;
				}

				var contact = hit.point;
				if (wheel.HadContact && (contact - wheel.LastContact).sqrMagnitude < 25.0f) {
					// sliding: velocity across the wheel's rolling direction
					var v = car.Body.GetPointVelocity(contact);
					float lateral = Mathf.Abs(Vector3.Dot(v, wheel.Transform.right));
					float slide = Mathf.Clamp01((lateral - 1.5f) / 6.0f);
					Contacts.Add(new WheelContact { Point = contact, Slide = slide, Velocity = v, Wheel = wheel.Transform, Body = car.Body });

					stampData_[count++] = new StampData {
						A = new Vector2(wheel.LastContact.x, wheel.LastContact.z),
						B = new Vector2(contact.x, contact.z),
						Width = wheel.Width * (1.0f + slide * 0.4f),
						Rubber = slide * s.RubberAmount,
						Press = 1.0f,
						Dry = 1.0f
					};
					center += contact;
				}
				wheel.LastContact = contact;
				wheel.HadContact = true;
			}

			if (count == 0) {
				return;
			}
			center /= count;

			// only the texels around this car
			int regionTexels = Mathf.CeilToInt(RegionMetres / WorldSize * Size);
			var texel = new Vector2((center.x - origin_.x) / WorldSize * Size, (center.z - origin_.y) / WorldSize * Size);
			int x0 = Mathf.FloorToInt(texel.x) - regionTexels / 2;
			int y0 = Mathf.FloorToInt(texel.y) - regionTexels / 2;

			stamps_.SetData(stampData_, 0, 0, count);
			compute_.SetInt(StampCountId, count);
			compute_.SetInts(RegionId, x0, y0);
			compute_.SetBuffer(kStamp_, StampsId, stamps_);
			compute_.SetTexture(kStamp_, MapId, map_);
			compute_.Dispatch(kStamp_, (regionTexels + 7) / 8, (regionTexels + 7) / 8, 1);
		}

		// Cars: heavy rigidbodies near the camera. Wheels: children named "Wheel..." (CarX naming) that are
		// not inside another wheel; radius and width come from their renderers' bounds.
		private void ScanCars(Vector3 around) {
			var found = new HashSet<Rigidbody>();
			int hits = Physics.OverlapSphereNonAlloc(around, 150.0f, overlap_, ~0, QueryTriggerInteraction.Ignore);
			for (int i = 0; i < hits; ++i) {
				var rb = overlap_[i].attachedRigidbody;
				if (rb != null && rb.mass >= MinCarMass) {
					found.Add(rb);
				}
			}

			cars_.RemoveAll(c => c.Body == null || !found.Contains(c.Body));
			foreach (var rb in found) {
				if (cars_.Exists(c => c.Body == rb)) {
					continue;
				}
				var car = new Car { Body = rb };
				foreach (var t in rb.GetComponentsInChildren<Transform>()) {
					if (!t.name.StartsWith("Wheel", System.StringComparison.OrdinalIgnoreCase) || IsInsideWheel(t, rb.transform)) {
						continue;
					}
					var bounds = new Bounds(t.position, Vector3.zero);
					bool any = false;
					foreach (var r in t.GetComponentsInChildren<Renderer>()) {
						if (!any) {
							bounds = r.bounds;
							any = true;
						} else {
							bounds.Encapsulate(r.bounds);
						}
					}
					float radius = any ? Mathf.Clamp(bounds.extents.y, 0.2f, 0.6f) : 0.33f;
					var local = rb.transform.InverseTransformVector(bounds.size);
					float width = any ? Mathf.Clamp(Mathf.Abs(local.x), 0.15f, 0.4f) : 0.25f;
					car.Wheels.Add(new Wheel { Transform = t, Radius = radius, Width = width });
				}
				if (car.Wheels.Count > 0) {
					cars_.Add(car);
				}
			}
		}

		private static bool IsInsideWheel(Transform t, Transform root) {
			for (var p = t.parent; p != null && p != root; p = p.parent) {
				if (p.name.StartsWith("Wheel", System.StringComparison.OrdinalIgnoreCase)) {
					return true;
				}
			}
			return false;
		}
	}
}

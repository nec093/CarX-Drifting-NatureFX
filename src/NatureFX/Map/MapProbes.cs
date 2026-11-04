using System.Collections.Generic;
using System.Globalization;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Map {
	// Reflection probes (docs/map-authoring.md 6.4): nfx_probe markers (an empty object's box is the influence, a
	// disabled Reflection Probe keeps its own) and, with [probes] auto = true, probes dropped along the road every
	// 'spacing' metres as the car drives it (the road is where the car goes; at most 16, the farthest made way for
	// the new one). They are HDRP's on demand realtime probes with time slicing: one probe at a time is rendered,
	// one face per frame, nearest the camera first, again whenever the time of day or the weather has moved on.
	public class MapProbes {
		private const int MaxProbes = 16;
		private const float DefaultSpacing = 60.0f;
		private const int FramesPerProbe = 7; // six faces, one per frame, and a spare
		private static readonly Vector3 DefaultSize = new(60.0f, 30.0f, 60.0f);

		private class Probe {
			public Vector3 Position;
			public HDAdditionalReflectionData Hd;
			public ReflectionProbe Unity;
			public bool Created;
			public bool Auto;
			public int Version = -1; // the scene look it was last rendered for
		}

		private readonly List<Probe> probes_ = new();
		private GameObject root_;
		private bool auto_;
		private float spacing_ = DefaultSpacing;
		private int version_;
		private float lastElevation_ = float.NaN;
		private WeatherState lastWeather_;
		private int nextRenderFrame_;
		private bool on_;
		// the sky HDRP renders the probes with: the own engine's (Rendering.AtmospherePass.SkyCube, A3) changes all the
		// time (clouds, the light): a probe made while it was still black, or an hour of sky ago, is made again
		private Texture lastSky_;
		private float nextSkyRefresh_;
		private const float SkyRefreshSeconds = 20.0f;

		public int Count => probes_.Count;

		public void Load(MapProfile profile, MarkerScanner markers) {
			Clear();
			auto_ = profile.Loaded && string.Equals(profile.Get("probes", "auto"), "true", System.StringComparison.OrdinalIgnoreCase);
			spacing_ = MapValues.TryFloat(profile.Get("probes", "spacing"), out float spacing) ? Mathf.Max(10.0f, spacing) : DefaultSpacing;
			foreach (var m in markers.OfKind("probe")) {
				if (probes_.Count >= MaxProbes) {
					Kino.Log.Warning($"[NatureFX] More than {MaxProbes} reflection probe markers: the rest are ignored");
					break;
				}
				if (m.Component is ReflectionProbe own) {
					var hd = own.GetComponent<HDAdditionalReflectionData>() ?? own.gameObject.AddComponent<HDAdditionalReflectionData>();
					Configure(hd);
					own.enabled = on_; // Clear() switched it off; Update only touches it when on_ changes
					probes_.Add(new Probe { Position = m.Position, Hd = hd, Unity = own });
					continue;
				}
				var size = m.Size.x > 2.0f && m.Size.y > 2.0f && m.Size.z > 2.0f ? m.Size : DefaultSize;
				probes_.Add(Create(m.Position, m.Rotation, size, false));
			}
			if (probes_.Count > 0 || auto_) {
				Kino.Log.Info($"[NatureFX] {probes_.Count} reflection probe markers{(auto_ ? $", more along the road every {spacing_:0} m" : "")}");
			}
			version_++;
		}

		public void Clear() {
			foreach (var p in probes_) {
				if (p.Created) {
					if (p.Hd != null) {
						Object.Destroy(p.Hd.gameObject);
					}
				} else if (p.Unity != null) {
					p.Unity.enabled = false;
				}
			}
			probes_.Clear();
		}

		public void Destroy() {
			Clear();
			if (root_ != null) {
				Object.Destroy(root_);
			}
			root_ = null;
		}

		private Probe Create(Vector3 position, Quaternion rotation, Vector3 size, bool auto) {
			if (root_ == null) {
				root_ = new GameObject("NatureFX.MapProbes");
			}
			var go = new GameObject(auto ? "NatureFX.Probe auto" : "NatureFX.Probe");
			go.transform.SetParent(root_.transform, false);
			go.transform.SetPositionAndRotation(position, rotation);
			var unity = go.AddComponent<ReflectionProbe>();
			var hd = go.GetComponent<HDAdditionalReflectionData>() ?? go.AddComponent<HDAdditionalReflectionData>();
			Configure(hd);
			var influence = hd.influenceVolume;
			influence.shape = InfluenceShape.Box;
			influence.boxSize = size;
			var blend = Vector3.one * Mathf.Min(10.0f, size.x * 0.2f);
			influence.boxBlendDistancePositive = blend;
			influence.boxBlendDistanceNegative = blend;
			unity.enabled = on_;
			return new Probe { Position = position, Hd = hd, Unity = unity, Created = true, Auto = auto };
		}

		private static void Configure(HDAdditionalReflectionData hd) {
			hd.mode = UnityEngine.Rendering.HighDefinition.ProbeSettings.Mode.Realtime;
			hd.realtimeMode = UnityEngine.Rendering.HighDefinition.ProbeSettings.RealtimeMode.OnDemand;
			hd.timeSlicing = true;
			SetResolution(hd, CubeReflectionResolution.CubeReflectionResolution128);
		}

		// 16 probes at the game's cube size (the atlas takes each at twice or four times it) filled HDRP's reflection
		// probe atlas: "No more space in Reflection Probe Atlas" every frame and probes that never showed. 128 is plenty
		// for the rough reflections a probe gives (ray traced reflections take the sharp ones). ProbeSettings.cubeResolution
		// is internal; its value is a class, so setting it on the copy reaches the probe's own.
		private static System.Reflection.FieldInfo cubeResolutionField_;

		private static void SetResolution(HDProbe hd, CubeReflectionResolution resolution) {
			cubeResolutionField_ ??= typeof(ProbeSettings).GetField("cubeResolution", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
			if (cubeResolutionField_?.GetValue(hd.settingsRaw) is ProbeSettings.CubeReflectionResolutionScalableSettingValue value) {
				value.@override = resolution;
				value.useOverride = true;
			}
		}

		public void Update(bool on, Light sun, Vector3 camera) {
			if (on != on_) {
				on_ = on;
				foreach (var p in probes_) {
					if (p.Unity != null) {
						p.Unity.enabled = on;
					}
				}
				version_++;
			}
			if (!on) {
				return;
			}

			if (auto_) {
				DropAlongRoad(camera);
			}

			// the time of day or the weather moved on: everything again, nearest first
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			var w = Weather.Current;
			var sky = NatureFX.Rendering.AtmospherePass.SkyCube;
			if (sky != lastSky_) {
				// a new sky: give HDRP a moment to draw it into its own sky before the probes see it
				lastSky_ = sky;
				nextSkyRefresh_ = Time.unscaledTime + 2.0f;
			} else if (sky != null && Time.unscaledTime >= nextSkyRefresh_) {
				nextSkyRefresh_ = Time.unscaledTime + SkyRefreshSeconds;
				version_++;
			}
			if (float.IsNaN(lastElevation_) || Mathf.Abs(elevation - lastElevation_) > 2.0f || Mathf.Abs(w.Cloud - lastWeather_.Cloud) > 0.1f
			    || Mathf.Abs(w.Wetness - lastWeather_.Wetness) > 0.15f) {
				lastElevation_ = elevation;
				lastWeather_ = w;
				version_++;
			}

			if (Time.frameCount < nextRenderFrame_) {
				return;
			}
			Probe next = null;
			float best = float.MaxValue;
			foreach (var p in probes_) {
				if (p.Hd == null || p.Version == version_) {
					continue;
				}
				float d = (p.Position - camera).sqrMagnitude;
				if (d < best) {
					best = d;
					next = p;
				}
			}
			if (next != null) {
				next.Hd.RequestRenderNextUpdate();
				next.Version = version_;
				nextRenderFrame_ = Time.frameCount + FramesPerProbe;
			}
		}

		// a new probe where the car is when none is within the spacing (at most 16: the farthest auto probe goes)
		private void DropAlongRoad(Vector3 camera) {
			float near = spacing_ * spacing_;
			foreach (var p in probes_) {
				if ((p.Position - camera).sqrMagnitude < near) {
					return;
				}
			}
			if (probes_.Count >= MaxProbes) {
				Probe far = null;
				float farthest = -1.0f;
				foreach (var p in probes_) {
					float d = (p.Position - camera).sqrMagnitude;
					if (p.Auto && d > farthest) {
						farthest = d;
						far = p;
					}
				}
				if (far == null) {
					return;
				}
				Object.Destroy(far.Hd.gameObject);
				probes_.Remove(far);
			}
			var size = new Vector3(spacing_ * 1.2f, 30.0f, spacing_ * 1.2f);
			probes_.Add(Create(camera, Quaternion.identity, size, true));
		}

		public string Describe() {
			int auto = probes_.FindAll(p => p.Auto).Count;
			int ready = probes_.FindAll(p => p.Version == version_).Count;
			return string.Format(CultureInfo.InvariantCulture, "{0} ({1} along the road, {2} up to date)", probes_.Count, auto, ready);
		}
	}
}

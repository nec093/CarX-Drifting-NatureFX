using NatureFX.Map;
using UnityEngine;

namespace NatureFX.Nature {
	// Effective weather for this frame. Systems read Weather.Current instead of the raw settings so presets and
	// the automatic cycle can blend values without writing the config every frame.
	public struct WeatherState {
		public float Rain;
		public float Wetness;
		public float Snow;
		public float Leaves;
		public float Dust;
		public float Cloud;     // 0 clear .. 1 overcast: dims the sun and softens shadows
		public float FogScale;  // multiplies the fog distance (< 1 = denser)
		public float WindScale;

		public static WeatherState Lerp(WeatherState a, WeatherState b, float t) {
			return new WeatherState {
				Rain = Mathf.Lerp(a.Rain, b.Rain, t),
				Wetness = Mathf.Lerp(a.Wetness, b.Wetness, t),
				Snow = Mathf.Lerp(a.Snow, b.Snow, t),
				Leaves = Mathf.Lerp(a.Leaves, b.Leaves, t),
				Dust = Mathf.Lerp(a.Dust, b.Dust, t),
				Cloud = Mathf.Lerp(a.Cloud, b.Cloud, t),
				FogScale = Mathf.Lerp(a.FogScale, b.FogScale, t),
				WindScale = Mathf.Lerp(a.WindScale, b.WindScale, t)
			};
		}
	}

	public static class Weather {
		public static WeatherState Current;
		// black ice on wet roads (0..1): forms over about a minute when it is cold (snow on the ground or winter) and
		// the road is wet but no rain is falling on it, thaws in the sun (_NFX_Ice, NFX_Accumulation.hlsl)
		public static float Ice;
		private static readonly int IceId = Shader.PropertyToID("_NFX_Ice");

		public static void UpdateIce(bool on, Light sun) {
			var w = Current;
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			bool cold = w.Snow > 0.05f || Seasons.Current == 3;
			float target = on && cold && w.Rain < 0.1f ? Mathf.Clamp01(w.Wetness * 2.0f) * (elevation > 25.0f ? 0.4f : 1.0f) : 0.0f;
			Ice = Mathf.MoveTowards(Ice, target, Time.deltaTime / 60.0f);
			Shader.SetGlobalVector(IceId, new Vector4(Ice, 0.0f, 0.0f, 0.0f));
		}
	}

	public class WeatherController {
		public static readonly string[] Names = { "Manual", "Clear", "Overcast", "Rain", "Storm", "Snow", "Autumn", "Fog" };

		// rain, wetness, snow, leaves, dust, cloud, fog scale, wind scale
		private static readonly WeatherState[] Presets = {
			default,
			new() { Rain = 0.0f, Wetness = 0.0f, Snow = 0.0f, Leaves = 0.05f, Dust = 0.15f, Cloud = 0.0f, FogScale = 1.6f, WindScale = 0.6f },
			new() { Rain = 0.0f, Wetness = 0.15f, Snow = 0.0f, Leaves = 0.1f, Dust = 0.05f, Cloud = 0.8f, FogScale = 0.8f, WindScale = 1.0f },
			new() { Rain = 0.6f, Wetness = 0.85f, Snow = 0.0f, Leaves = 0.1f, Dust = 0.0f, Cloud = 0.9f, FogScale = 0.8f, WindScale = 1.2f },
			new() { Rain = 1.0f, Wetness = 1.0f, Snow = 0.0f, Leaves = 0.2f, Dust = 0.0f, Cloud = 1.0f, FogScale = 0.6f, WindScale = 2.0f },
			new() { Rain = 0.0f, Wetness = 0.2f, Snow = 0.8f, Leaves = 0.0f, Dust = 0.0f, Cloud = 0.85f, FogScale = 0.5f, WindScale = 0.8f },
			new() { Rain = 0.0f, Wetness = 0.25f, Snow = 0.0f, Leaves = 0.75f, Dust = 0.05f, Cloud = 0.4f, FogScale = 1.0f, WindScale = 1.3f },
			new() { Rain = 0.0f, Wetness = 0.4f, Snow = 0.0f, Leaves = 0.1f, Dust = 0.0f, Cloud = 0.7f, FogScale = 0.15f, WindScale = 0.3f }
		};

		private const float DefaultTransitionSeconds = 30.0f; // Settings.WeatherChangeTime

		private WeatherState from_;
		private int target_ = -1;
		private float blend_ = 1.0f;
		private float nextChange_;
		private int lastPreset_ = -1;

		public int ActivePreset => target_;

		public void Update(ISettings s) {
			if (!s.Enabled || !s.Accumulation) {
				Weather.Current = Manual(s);
				Weather.Current.Rain = Weather.Current.Wetness = Weather.Current.Snow = Weather.Current.Leaves = Weather.Current.Dust = 0.0f;
				return;
			}

			int wanted = s.WeatherPreset;
			if (s.WeatherCycle) {
				if (target_ <= 0 || Time.unscaledTime >= nextChange_) {
					wanted = PickNext(target_);
					nextChange_ = Time.unscaledTime + Mathf.Max(1.0f, s.WeatherCycleMinutes) * 60.0f;
				} else {
					wanted = target_;
				}
			}

			if (wanted != lastPreset_) {
				// start a transition from wherever we are now
				from_ = Weather.Current;
				blend_ = lastPreset_ < 0 ? 1.0f : 0.0f;
				lastPreset_ = wanted;
			}
			target_ = wanted;

			blend_ = Mathf.MoveTowards(blend_, 1.0f, Time.unscaledDeltaTime / (s.WeatherChangeTime > 0.0f ? s.WeatherChangeTime : DefaultTransitionSeconds));
			var goal = target_ <= 0 ? Manual(s) : Presets[target_];
			Weather.Current = WeatherState.Lerp(from_, goal, Mathf.SmoothStep(0.0f, 1.0f, blend_));
		}

		private static WeatherState Manual(ISettings s) {
			return new WeatherState {
				// the map's [defaults] and weather zones where the user left a slider as it was (Map/MapDefaults)
				Rain = s.Rain,
				Wetness = MapDefaults.Value(MapKey.Wetness, s.Wetness),
				Snow = MapDefaults.Value(MapKey.Snow, s.Snow),
				Leaves = MapDefaults.Value(MapKey.Leaves, s.Leaves),
				Dust = MapDefaults.Value(MapKey.Dust, s.Dust),
				// rain falls from clouds: the sky closes over as the rain sets in (a clear sky with full sunshine in rain
				// was the manual weather's), the air's haze from the rain itself (MapFog.RainExtinction)
				Cloud = Mathf.Clamp01(s.Rain * 1.6f), FogScale = 1.0f, WindScale = 1.0f + 0.5f * s.Rain
			};
		}

		// Plausible next weather: mostly clear / overcast, rain follows clouds, snow stays rare.
		private static int PickNext(int current) {
			int[] pool = current switch {
				1 => new[] { 1, 2, 2, 6 },
				2 => new[] { 1, 3, 3, 7 },
				3 => new[] { 2, 4, 3 },
				4 => new[] { 3, 2 },
				5 => new[] { 2, 5 },
				6 => new[] { 1, 2, 6 },
				7 => new[] { 2, 1 },
				_ => new[] { 1, 2, 6 }
			};
			return pool[Random.Range(0, pool.Length)];
		}
	}
}

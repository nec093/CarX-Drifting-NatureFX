using NatureFX.Map;
using UnityEngine;

namespace NatureFX.Nature {
	// Effective weather for this frame. Systems read Weather.Current instead of the raw settings so presets and
	// the automatic cycle can blend values without writing the config every frame.
	public struct WeatherState {
		public float Rain;
		public float Wetness;
		public float Cloud;     // 0 clear .. 1 overcast: dims the sun and softens shadows
		public float FogScale;  // multiplies the fog distance (< 1 = denser)
		public float WindScale;

		public static WeatherState Lerp(WeatherState a, WeatherState b, float t) {
			return new WeatherState {
				Rain = Mathf.Lerp(a.Rain, b.Rain, t),
				Wetness = Mathf.Lerp(a.Wetness, b.Wetness, t),
				Cloud = Mathf.Lerp(a.Cloud, b.Cloud, t),
				FogScale = Mathf.Lerp(a.FogScale, b.FogScale, t),
				WindScale = Mathf.Lerp(a.WindScale, b.WindScale, t)
			};
		}
	}

	public static class Weather {
		public static WeatherState Current;
	}

	public class WeatherController {
		// the settings keep the index (Settings.WeatherPreset; config version 82 moved the old Snow and Autumn to Clear)
		public static readonly string[] Names = { "Manual", "Clear", "Overcast", "Rain", "Storm", "Fog" };

		// rain, wetness, cloud, fog scale, wind scale
		private static readonly WeatherState[] Presets = {
			default,
			new() { Rain = 0.0f, Wetness = 0.0f, Cloud = 0.0f, FogScale = 1.6f, WindScale = 0.6f },
			new() { Rain = 0.0f, Wetness = 0.15f, Cloud = 0.8f, FogScale = 0.8f, WindScale = 1.0f },
			new() { Rain = 0.6f, Wetness = 0.85f, Cloud = 0.9f, FogScale = 0.8f, WindScale = 1.2f },
			new() { Rain = 1.0f, Wetness = 1.0f, Cloud = 1.0f, FogScale = 0.6f, WindScale = 2.0f },
			new() { Rain = 0.0f, Wetness = 0.4f, Cloud = 0.7f, FogScale = 0.15f, WindScale = 0.3f }
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
				Weather.Current.Rain = Weather.Current.Wetness = 0.0f;
				return;
			}

			// an index past the list (a saved preset from before the Snow and Autumn weather went): Clear
			int wanted = s.WeatherPreset >= 0 && s.WeatherPreset < Presets.Length ? s.WeatherPreset : 1;
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
			if (target_ == 1) {
				// a clear day with as many fair weather clouds as the user wants (Settings.CloudAmount)
				goal.Cloud = Mathf.Clamp01(s.CloudAmount);
			}
			Weather.Current = WeatherState.Lerp(from_, goal, Mathf.SmoothStep(0.0f, 1.0f, blend_));
		}

		private static WeatherState Manual(ISettings s) {
			return new WeatherState {
				// the map's [defaults] and weather zones where the user left a slider as it was (Map/MapDefaults)
				Rain = s.Rain,
				Wetness = MapDefaults.Value(MapKey.Wetness, s.Wetness),
				// rain falls from clouds: the sky closes over as the rain sets in (a clear sky with full sunshine in rain
				// was the manual weather's), the air's haze from the rain itself (MapFog.RainExtinction)
				Cloud = Mathf.Clamp01(Mathf.Max(s.Rain * 1.6f, s.CloudAmount)), FogScale = 1.0f, WindScale = 1.0f + 0.5f * s.Rain
			};
		}

		// Plausible next weather: mostly clear / overcast, rain follows clouds, fog after overcast.
		private static int PickNext(int current) {
			int[] pool = current switch {
				1 => new[] { 1, 2, 2 },
				2 => new[] { 1, 3, 3, 5 },
				3 => new[] { 2, 4, 3 },
				4 => new[] { 3, 2 },
				5 => new[] { 2, 1 },
				_ => new[] { 1, 2, 2 }
			};
			return pool[Random.Range(0, pool.Length)];
		}
	}
}

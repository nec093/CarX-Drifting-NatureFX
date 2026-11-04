using NatureFX.Map;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// HDRP volumetric clouds (the game's HDRP asset supports them) as a global volume over the track's own:
	//   sky clouds built from cloud types, as a meteorologist would describe the sky (HDRP's advanced mode, whose cloud
	//   map NatureFX paints from noise): fair weather cumulus when clear, more and bigger cumulus as it clouds over, an
	//   altostratus deck when overcast, cumulonimbus towers where it rains. Each type has HDRP's own height profile
	//   (flat-bottomed heaps, a thin high sheet, an anvil), so they no longer are one noise of varying density;
	//   the clouds drift with the weather's wind (Weather > Wind: direction and strength), faster than the wind at the
	//   ground as winds aloft are, lean with the wind shear and boil slowly as they go;
	//   their shadows sweep over the mountains (HDRP's cloud shadows on the sun);
	//   a cloud sea ([clouds] sea = <altitude> in the map profile, docs/map-authoring.md 1): the layer drops into the
	//   valley in the morning and after rain, so the summit road looks down onto it.
	public class CloudController {
		private const float Priority = 9001.0f;
		private const int MapSize = 256;

		private GameObject root_;
		private Volume volume_;
		private VolumeProfile profile_;
		private VolumetricClouds clouds_;
		private Texture2D cumulus_, altostratus_, cumulonimbus_, rainMap_;
		private float sea_;      // 0 sky clouds .. 1 the cloud sea
		private float wantSea_;
		private float cover_ = -1.0f; // the cover drawn: follows the weather slowly, so the clouds grow and thin out
		                              // instead of jumping
		private float storm_;         // the same for rain (cumulonimbus)
		private float windKmh_;
		private float windAngle_ = float.NaN;

		public string State { get; private set; } = "off";

		// Native sky on: the own engine draws the clouds (NFXNative Clouds.hlsl, RTShadowPass) instead of HDRP. What it
		// needs: whether to, the cover by cloud type (x cumulus, y altostratus, z cumulonimbus, w density) and the
		// motion (xy how far the wind has carried the air (m, negated: the map is read upwind), z the clouds' age (s),
		// w the distance at which they fade into the haze (m))
		public static bool NativeOn { get; private set; }
		// the own engine's cloud shadows darken the sun where the clouds are (RTShadowPass): the sun is then not dimmed
		// as a whole under cloud cover as well (SunController)
		public static bool NativeShadowsActive { get; set; }
		public static Vector4 NativeCover { get; private set; }
		public static Vector4 NativeMotion { get; private set; }
		private Vector2 windOffset_;
		private float changeTime_ = 30.0f;
		private float age_;

		public void Create() {
			if (root_ != null) {
				return;
			}
			root_ = new GameObject("NatureFX.Clouds");
			Object.DontDestroyOnLoad(root_);
			profile_ = ScriptableObject.CreateInstance<VolumeProfile>();
			profile_.name = "NatureFX.Clouds";
			clouds_ = profile_.Add<VolumetricClouds>();
			volume_ = root_.AddComponent<Volume>();
			volume_.isGlobal = true;
			volume_.priority = Priority;
			volume_.sharedProfile = profile_;
			volume_.enabled = false;
		}

		public void Destroy() {
			if (root_ != null) {
				Object.Destroy(root_);
			}
			if (profile_ != null) {
				Object.Destroy(profile_);
			}
			foreach (var t in new[] { cumulus_, altostratus_, cumulonimbus_, rainMap_ }) {
				if (t != null) {
					Object.Destroy(t);
				}
			}
			cumulus_ = altostratus_ = cumulonimbus_ = rainMap_ = null;
			root_ = null;
			profile_ = null;
		}

		// seaAltitude: the map's cloud sea (absolute world height of its top), NaN for none
		public void Update(bool on, ISettings s, Light sun, float seaAltitude) {
			if (volume_ == null) {
				return;
			}
			var w = Weather.Current;
			changeTime_ = s.WeatherChangeTime > 0.0f ? s.WeatherChangeTime : 30.0f;
			if (on && s.Clouds && s.NativeSky) {
				volume_.enabled = false;
				UpdateNative(w);
				return;
			}
			NativeOn = false;
			NativeShadowsActive = false;
			volume_.enabled = on && s.Clouds;
			if (!volume_.enabled) {
				State = "off";
				return;
			}
			float elevation = sun != null ? Mathf.Asin(Mathf.Clamp(-sun.transform.forward.y, -1.0f, 1.0f)) * Mathf.Rad2Deg : 45.0f;
			bool morning = s.SunOverride ? s.TimeOfDay >= 4.0f && s.TimeOfDay <= 9.5f : elevation > -4.0f && elevation < 15.0f;
			wantSea_ = !float.IsNaN(seaAltitude) && (morning || (w.Wetness > 0.4f && w.Rain < 0.1f)) ? 1.0f : 0.0f;
			// the sea forms and burns off over a couple of minutes
			sea_ = Mathf.MoveTowards(sea_, wantSea_, Time.deltaTime / 120.0f);

			// a change of weather takes a few minutes to cross the sky
			float target = Mathf.Clamp01(w.Cloud);
			cover_ = cover_ < 0.0f ? target : Mathf.MoveTowards(cover_, target, Time.deltaTime / 180.0f);
			storm_ = Mathf.MoveTowards(storm_, Mathf.Clamp01(w.Rain), Time.deltaTime / 180.0f);
			float cover = cover_;
			clouds_.enable.Override(true);
			clouds_.ghostingReduction.Override(true);
			ApplyWind();

			// the shape's scale and the layer's height never change: changing them re-shapes every cloud at once
			if (sea_ > 0.5f) {
				// a flat, dense layer whose top is at the given height
				clouds_.cloudControl.Override(VolumetricClouds.CloudControl.Simple);
				clouds_.cloudSimpleMode.Override(VolumetricClouds.CloudSimpleMode.Quality);
				clouds_.localClouds.Override(true);
				clouds_.bottomAltitude.Override(Mathf.Max(0.01f, seaAltitude - 120.0f));
				clouds_.altitudeRange.Override(120.0f);
				clouds_.densityMultiplier.Override(0.6f * Mathf.SmoothStep(0.0f, 1.0f, (sea_ - 0.5f) * 2.0f));
				clouds_.shapeFactor.Override(0.35f);
				clouds_.shapeScale.Override(2.5f);
				clouds_.erosionFactor.Override(0.6f);
				State = $"cloud sea at {seaAltitude:0} m";
			} else {
				EnsureMaps();
				clouds_.cloudControl.Override(VolumetricClouds.CloudControl.Advanced);
				clouds_.localClouds.Override(false);
				clouds_.earthCurvature.Override(0.4f); // the layer bends down to the horizon instead of a flat ceiling
				clouds_.bottomAltitude.Override(1200.0f);
				clouds_.altitudeRange.Override(2500.0f);
				clouds_.cloudMapResolution.Override(VolumetricClouds.CloudMapResolution.Ultra256x256);
				clouds_.cumulusMap.Override(cumulus_);
				clouds_.altoStratusMap.Override(altostratus_);
				clouds_.cumulonimbusMap.Override(cumulonimbus_);
				clouds_.rainMap.Override(rainMap_);
				// the cover as cloud types. The multipliers are quantised: HDRP rebuilds its cloud map whenever they change
				float cumulus = Mathf.Lerp(0.3f, 0.85f, Mathf.SmoothStep(0.0f, 0.75f, cover)) * (1.0f - 0.5f * Mathf.SmoothStep(0.75f, 1.0f, cover));
				float alto = Mathf.SmoothStep(0.45f, 1.0f, cover);
				float tower = Mathf.SmoothStep(0.25f, 0.9f, storm_) * Mathf.Clamp01(cover * 1.5f);
				clouds_.cumulusMapMultiplier.Override(Step(cumulus));
				clouds_.altoStratusMapMultiplier.Override(Step(alto));
				clouds_.cumulonimbusMapMultiplier.Override(Step(tower));
				clouds_.densityMultiplier.Override(Mathf.Lerp(0.3f, 0.5f, cover));
				clouds_.shapeFactor.Override(0.9f);
				clouds_.shapeScale.Override(4.0f);
				clouds_.erosionFactor.Override(0.8f);
				clouds_.erosionScale.Override(107.0f);
				// billowing edges: Worley erosion and the fine erosion close up
				clouds_.erosionNoiseType.Override(VolumetricClouds.CloudErosionNoise.Worley32);
				clouds_.microErosion.Override(true);
				clouds_.microErosionFactor.Override(0.6f);
				clouds_.microErosionScale.Override(200.0f);
				// light through the cloud: more steps (no banding in the towers), light scattered many times inside it
				// (the bright, soft sides of a cumulus), and the dark "powder" rims where light enters
				clouds_.numPrimarySteps.Override(96);
				clouds_.numLightSteps.Override(8);
				clouds_.multiScattering.Override(0.65f);
				clouds_.powderEffectIntensity.Override(0.35f);
				clouds_.erosionOcclusion.Override(0.2f);
				State = $"sky, cover {cover:P0} (cumulus {cumulus:0.00}, altostratus {alto:0.00}, cumulonimbus {tower:0.00}), wind {windKmh_:0} km/h";
			}
			if (GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset asset && !asset.currentPlatformRenderPipelineSettings.supportVolumetricClouds) {
				State += " - NOT DRAWN: the game's HDRP asset has volumetric clouds off";
			}
			clouds_.shadows.Override(s.CloudShadows > 0.0f);
			clouds_.shadowOpacity.Override(Mathf.Clamp01(s.CloudShadows));
			clouds_.shadowDistance.Override(6000.0f);
			clouds_.shadowResolution.Override(VolumetricClouds.CloudShadowResolution.High512);
		}

		private static float Step(float v) => Mathf.Round(v * 50.0f) / 50.0f;

		private void UpdateNative(WeatherState w) {
			float target = Mathf.Clamp01(w.Cloud);
			// the weather already blends over Settings.WeatherChangeTime: the clouds follow it at the same pace
			float follow = Mathf.Max(5.0f, changeTime_);
			cover_ = cover_ < 0.0f ? target : Mathf.MoveTowards(cover_, target, Time.deltaTime / follow);
			storm_ = Mathf.MoveTowards(storm_, Mathf.Clamp01(w.Rain), Time.deltaTime / follow);
			float cover = cover_;
			// the cover as cloud types: scattered cumulus when clear, more and taller as it clouds over, an altostratus
			// sheet when overcast, cumulonimbus where it rains
			float cumulus = Mathf.Lerp(0.35f, 0.9f, Mathf.SmoothStep(0.0f, 0.75f, cover)) * (1.0f - 0.4f * Mathf.SmoothStep(0.75f, 1.0f, cover));
			float alto = Mathf.SmoothStep(0.45f, 1.0f, cover);
			float tower = Mathf.SmoothStep(0.25f, 0.9f, storm_) * Mathf.Clamp01(cover * 1.5f);
			NativeCover = new Vector4(cumulus, alto, tower, Mathf.Lerp(0.6f, 1.0f, cover));
			// carried by the wind (WindController's direction and strength; aloft faster than at the ground)
			var wind = Shader.GetGlobalVector(ShaderGlobals.Wind);
			var dir = new Vector2(wind.x, wind.z);
			float strength = dir.sqrMagnitude > 1e-4f ? Mathf.Max(0.0f, wind.w) : 0.0f;
			float speed = (15.0f + strength * 8.0f * 2.5f * 3.6f) / 3.6f; // m/s
			windKmh_ = speed * 3.6f;
			if (dir.sqrMagnitude > 1e-4f) {
				float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
				windAngle_ = float.IsNaN(windAngle_) ? angle : Mathf.MoveTowardsAngle(windAngle_, angle, Time.deltaTime * 3.0f);
			} else if (float.IsNaN(windAngle_)) {
				windAngle_ = 0.0f;
			}
			float a = windAngle_ * Mathf.Deg2Rad;
			float dt = Mathf.Min(Time.deltaTime, 0.1f);
			windOffset_ += new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed * dt;
			// keep the numbers small (the noise repeats every 32 km at most)
			windOffset_.x = Mathf.Repeat(windOffset_.x, 32000.0f * 4.0f);
			windOffset_.y = Mathf.Repeat(windOffset_.y, 32000.0f * 4.0f);
			age_ = Mathf.Repeat(age_ + dt, 4000.0f / 0.6f * 8.0f);
			float haze = Mathf.Lerp(60000.0f, 15000.0f, Mathf.Clamp01(cover * 0.5f + w.Rain * 0.5f));
			NativeMotion = new Vector4(-windOffset_.x, -windOffset_.y, age_, haze);
			NativeOn = true;
			State = $"own engine, cover {cover:P0} (cumulus {cumulus:0.00}, altostratus {alto:0.00}, cumulonimbus {tower:0.00}), wind {windKmh_:0} km/h";
		}

		// The weather's wind (WindController: direction it blows to, strength 0..1+) carries the clouds. At cloud height
		// the wind is two to three times the wind at the ground; a light breeze still moves them, so the sky never stands
		// still. HDRP moves the cloud map towards its orientation angle, measured from +x towards +z.
		private void ApplyWind() {
			var wind = Shader.GetGlobalVector(ShaderGlobals.Wind);
			var dir = new Vector2(wind.x, wind.z);
			float strength = dir.sqrMagnitude > 1e-4f ? Mathf.Max(0.0f, wind.w) : 0.0f;
			// ground wind about 8 m/s per unit of strength (as the rain drifts, Precipitation), aloft 2.5 times that
			float kmh = 15.0f + strength * 8.0f * 2.5f * 3.6f;
			windKmh_ = Mathf.MoveTowards(windKmh_ <= 0.0f ? kmh : windKmh_, kmh, Time.deltaTime * 5.0f);
			if (strength > 0.0f) {
				float angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
				windAngle_ = float.IsNaN(windAngle_) ? angle : Mathf.MoveTowardsAngle(windAngle_, angle, Time.deltaTime * 3.0f);
			} else if (float.IsNaN(windAngle_)) {
				windAngle_ = 0.0f;
			}
			clouds_.globalWindSpeed.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = windKmh_ });
			clouds_.orientation.Override(new WindParameter.WindParamaterValue { mode = WindParameter.WindOverrideMode.Custom, customValue = Mathf.Repeat(windAngle_, 360.0f) });
			// the big shapes travel with the map, the erosion boils more slowly; the tops lean downwind (shear)
			clouds_.cloudMapSpeedMultiplier.Override(0.6f);
			clouds_.shapeSpeedMultiplier.Override(0.8f);
			clouds_.erosionSpeedMultiplier.Override(0.3f);
			clouds_.altitudeDistortion.Override(Mathf.Lerp(0.15f, 0.5f, Mathf.Clamp01(strength)));
			clouds_.verticalShapeWindSpeed.Override(0.0f);
			clouds_.verticalErosionWindSpeed.Override(-3.0f); // the erosion rises slowly: tops seem to boil upward
		}

		// The cloud type maps, made once (tileable noise, 256 texels over HDRP's cloud domain):
		//   cumulus: separate heaps of many sizes, with clear sky between them;
		//   altostratus: a broad, gently varying sheet;
		//   cumulonimbus: a few big isolated cells, where it rains.
		private void EnsureMaps() {
			if (cumulus_ != null) {
				return;
			}
			var cu = new float[MapSize * MapSize];
			var alto = new float[MapSize * MapSize];
			var cb = new float[MapSize * MapSize];
			for (int y = 0; y < MapSize; ++y) {
				for (int x = 0; x < MapSize; ++x) {
					float u = (float)x / MapSize, v = (float)y / MapSize;
					// cumulus: fine blobs gathered into fields by a coarser noise
					float field = Fbm(u, v, 4, 3, 11);
					float blobs = Cells(u, v, 18, 23);
					cu[y * MapSize + x] = Mathf.Clamp01(Mathf.SmoothStep(0.25f, 0.85f, blobs * 0.65f + Fbm(u, v, 16, 3, 5) * 0.35f) * Mathf.SmoothStep(0.25f, 0.7f, field));
					alto[y * MapSize + x] = Mathf.Clamp01(Mathf.SmoothStep(0.3f, 0.75f, Fbm(u, v, 3, 5, 31)) * 0.85f + 0.15f);
					cb[y * MapSize + x] = Mathf.SmoothStep(0.62f, 0.85f, Fbm(u, v, 3, 3, 47) * 0.7f + Cells(u, v, 5, 53) * 0.3f);
				}
			}
			cumulus_ = MakeTexture("NatureFX.Clouds.Cumulus", cu);
			altostratus_ = MakeTexture("NatureFX.Clouds.Altostratus", alto);
			cumulonimbus_ = MakeTexture("NatureFX.Clouds.Cumulonimbus", cb);
			// it rains from the towers, and lightly from the thick of the deck
			var rain = new float[MapSize * MapSize];
			for (int i = 0; i < rain.Length; ++i) {
				rain[i] = Mathf.Max(cb[i], Mathf.SmoothStep(0.75f, 1.0f, alto[i]) * 0.5f);
			}
			rainMap_ = MakeTexture("NatureFX.Clouds.Rain", rain);
		}

		private static Texture2D MakeTexture(string name, float[] values) {
			var t = new Texture2D(MapSize, MapSize, TextureFormat.R8, false, true) {
				name = name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.DontSave,
			};
			var bytes = new byte[values.Length];
			for (int i = 0; i < values.Length; ++i) {
				bytes[i] = (byte)Mathf.RoundToInt(Mathf.Clamp01(values[i]) * 255.0f);
			}
			t.SetPixelData(bytes, 0);
			t.Apply(false, true);
			return t;
		}

		// tileable value noise: a lattice of 'period' cells over the unit square
		private static float Lattice(int x, int y, int period, int seed) {
			x = ((x % period) + period) % period;
			y = ((y % period) + period) % period;
			uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
			h = (h ^ (h >> 13)) * 1274126177u;
			return ((h ^ (h >> 16)) & 0xffffu) / 65535.0f;
		}

		private static float Value(float u, float v, int period, int seed) {
			float x = u * period, y = v * period;
			int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
			float fx = x - ix, fy = y - iy;
			fx = fx * fx * (3.0f - 2.0f * fx);
			fy = fy * fy * (3.0f - 2.0f * fy);
			float a = Mathf.Lerp(Lattice(ix, iy, period, seed), Lattice(ix + 1, iy, period, seed), fx);
			float b = Mathf.Lerp(Lattice(ix, iy + 1, period, seed), Lattice(ix + 1, iy + 1, period, seed), fx);
			return Mathf.Lerp(a, b, fy);
		}

		private static float Fbm(float u, float v, int period, int octaves, int seed) {
			float sum = 0.0f, amp = 0.5f, norm = 0.0f;
			for (int o = 0; o < octaves; ++o) {
				sum += Value(u, v, period << o, seed + o * 17) * amp;
				norm += amp;
				amp *= 0.5f;
			}
			return sum / norm;
		}

		// rounded heaps: 1 at a random point in each cell, falling off to 0 at the cell's edge (inverted Worley)
		private static float Cells(float u, float v, int period, int seed) {
			float x = u * period, y = v * period;
			int ix = Mathf.FloorToInt(x), iy = Mathf.FloorToInt(y);
			float best = 9.0f;
			for (int dy = -1; dy <= 1; ++dy) {
				for (int dx = -1; dx <= 1; ++dx) {
					int cx = ix + dx, cy = iy + dy;
					float px = cx + Lattice(cx, cy, period, seed);
					float py = cy + Lattice(cx, cy, period, seed + 7);
					float d = (px - x) * (px - x) + (py - y) * (py - y);
					best = Mathf.Min(best, d);
				}
			}
			return Mathf.Clamp01(1.0f - Mathf.Sqrt(best));
		}
	}
}

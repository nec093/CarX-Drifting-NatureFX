using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The point and spot lights nearest to the camera (headlights, street lamps), handed to the ray tracing shaders:
	// they light what reflections and bounce light hit, and the ones HDRP casts no shadows for get soft ray traced
	// shadows (LocalShadows.hlsl, from the size of the light's shape). Lights with HDRP shadows keep them (shadowing
	// them twice would darken) unless RayTracingEffects took those over (Settings.RTLampShadows).
	public class LocalLights {
		public const int Max = 128; // NFX_MAX_LOCAL_LIGHTS in NFXCommon.hlsli
		// the smallest light shape the soft shadows assume: HDRP's default (2.5 cm) is a bare bulb, lamps and
		// headlights have a reflector and a lens around it
		private const float MinShapeRadius = 0.12f;
		private const float MaxShapeRadius = 1.0f;
		private const float RefreshInterval = 1.0f;


		private readonly Vector4[] pos_ = new Vector4[Max];     // xyz position, w range (m)
		private readonly Vector4[] color_ = new Vector4[Max];   // rgb intensity (cd), w 1 = ray traced shadows
		private readonly Vector4[] dir_ = new Vector4[Max];     // xyz spot direction, w angle scale (0: point light)
		private readonly Vector4[] spot_ = new Vector4[Max];    // x angle offset, y shape radius (m)
		private readonly Dictionary<Light, float> shapes_ = new();
		private readonly List<Light> all_ = new();
		private readonly List<(float Distance, Light Light, int Emitter)> near_ = new();  // Light null: NativeEngine.Emitters[Emitter]
		private float nextRefresh_;

		public bool Enabled = true;
		// bright emissive surfaces (NativeEngine.Emitters) as lamps too
		public bool Emissive = true;

		// the lights for the own engine (NativeEngine): positions, colours, spot directions and offsets
		public Vector4[] Positions => pos_;
		public Vector4[] Colors => color_;
		public Vector4[] Directions => dir_;
		public Vector4[] Spots => spot_;
		public int Count { get; private set; }
		public int ShadowCount { get; private set; }
		// About the illuminance the brightest light in use casts (lux): its intensity (cd) at half its range. The GI's
		// clamp and its cache's fixed point scale take it as their reference besides the sun and the sky
		// (LightCount.w, GI.hlsl / NFXCommon.hlsli CacheReference): at night with the sun's alone, a headlight's bounce
		// was cut to a few hundredth.
		public float LampReference { get; private set; }
		// Developer: off gives the GI its old reference (the sun and the sky only), to compare
		public static bool UseLampReference = true;
		// Only lights that light a room or a road count (small ones light their own housing), seen from at least 2 m,
		// and never above a sunlit level: with 1 m and no cap, a bright lamp of short range made the reference
		// thousands of lux, the GI's clamp no longer caught single bright rays and the cache's steps grew coarse
		// (coloured speckles all over a garage)
		private const float ReferenceMinRange = 3.0f;
		private const float ReferenceMaxLux = 2000.0f;

		private static float Reference(float r, float g, float b, float range) {
			if (!UseLampReference || range < ReferenceMinRange) {
				return 0.0f;
			}
			float half = 0.5f * range;
			return Mathf.Min(Mathf.Max(r, Mathf.Max(g, b)) / Mathf.Max(4.0f, half * half), ReferenceMaxLux);
		}

		// Once per frame: the nearest lights to the camera
		public void Update(Camera camera) {
			Count = 0;
			ShadowCount = 0;
			LampReference = 0.0f;
			if (!Enabled || camera == null) {
				return;
			}
			if (Time.unscaledTime >= nextRefresh_) {
				nextRefresh_ = Time.unscaledTime + RefreshInterval;
				all_.Clear();
				shapes_.Clear();
				foreach (var light in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
					if (light.type == LightType.Point || light.type == LightType.Spot) {
						all_.Add(light);
						float shape = light.TryGetComponent<HDAdditionalLightData>(out var hd) ? hd.shapeRadius : 0.0f;
						shapes_[light] = Mathf.Clamp(shape, MinShapeRadius, MaxShapeRadius);
					}
				}
			}

			var eye = camera.transform.position;
			near_.Clear();
			foreach (var light in all_) {
				if (light == null || !light.isActiveAndEnabled || HeadlightController.Intensity(light) <= 0.0f) {
					continue;
				}
				float distance = Vector3.Distance(light.transform.position, eye) - light.range;
				if (distance < 60.0f) {
					near_.Add((distance, light, -1));
				}
			}
			if (Emissive) {
				var emitters = NativeEngine.Emitters;
				for (int i = 0; i < emitters.Count; ++i) {
					float distance = Vector3.Distance(emitters[i].Position, eye) - emitters[i].Range;
					if (distance < 60.0f) {
						near_.Add((distance, null, i));
					}
				}
			}
			near_.Sort((a, b) => a.Distance.CompareTo(b.Distance));

			for (int i = 0; i < near_.Count && Count < Max; ++i) {
				var light = near_[i].Light;
				if (light == null) {
					// an emissive surface: a point light HDRP does not have (added where it gets through, w 2)
					var e = NativeEngine.Emitters[near_[i].Emitter];
					pos_[Count] = new Vector4(e.Position.x, e.Position.y, e.Position.z, e.Range);
					color_[Count] = new Vector4(e.Intensity.r, e.Intensity.g, e.Intensity.b, 2.0f);
					dir_[Count] = Vector4.zero;
					spot_[Count] = new Vector4(0.0f, e.Radius, 0.0f, 0.0f);
					LampReference = Mathf.Max(LampReference, Reference(e.Intensity.r, e.Intensity.g, e.Intensity.b, e.Range));
					++ShadowCount;
					++Count;
					continue;
				}
				var t = light.transform;
				// HDRP keeps punctual intensities in candela; a headlight NatureFX holds: its own value
				var color = light.color.linear * HeadlightController.Intensity(light);
				if (light.useColorTemperature) {
					color *= Mathf.CorrelatedColorTemperatureToRGB(light.colorTemperature);
				}
				// small lights (indicators, interior and dash lamps) light only their own housing: no shadows for them
				bool traceShadows = light.shadows == LightShadows.None && light.range >= 3.0f;
				pos_[Count] = new Vector4(t.position.x, t.position.y, t.position.z, Mathf.Max(0.1f, light.range));
				color_[Count] = new Vector4(color.r, color.g, color.b, traceShadows ? 1.0f : 0.0f);
				LampReference = Mathf.Max(LampReference, Reference(color.r, color.g, color.b, pos_[Count].w));
				if (light.type == LightType.Spot) {
					// HDRP's spot attenuation: saturate(cos * scale + offset), squared
					float cosOuter = Mathf.Cos(light.spotAngle * 0.5f * Mathf.Deg2Rad);
					float cosInner = Mathf.Cos(Mathf.Max(0.0f, light.innerSpotAngle) * 0.5f * Mathf.Deg2Rad);
					float scale = 1.0f / Mathf.Max(0.001f, cosInner - cosOuter);
					var forward = t.forward;
					dir_[Count] = new Vector4(forward.x, forward.y, forward.z, scale);
					// a headlight's beam pattern (its cookie, which the engine does not see)
					float beam = HeadlightController.Beams.TryGetValue(light, out int kind) ? kind : 0.0f;
					spot_[Count] = new Vector4(-cosOuter * scale, Shape(light), beam, beam > 0.0f ? HeadlightController.Roll(light) : 0.0f);
				} else {
					dir_[Count] = Vector4.zero;
					spot_[Count] = new Vector4(0.0f, Shape(light), 0.0f, 0.0f);
				}
				if (traceShadows) {
					++ShadowCount;
				}
				++Count;
			}
		}

		private float Shape(Light light) {
			return shapes_.TryGetValue(light, out float r) ? r : MinShapeRadius;
		}

	}
}

using UnityEngine;

namespace NatureFX.Rendering {
	// The track's sun against the real one. CarX's tracks light their scenes with a sun of a few "lux" (HDRP's default of
	// pi, not the 100 000 lux of real sunshine), while their lamps, headlights and glowing surfaces are in HDRP's real
	// units: the sun about 30 000 times too weak against them. With NatureFX on, the sun (and with it the own engine's
	// sky and the moon) is raised by this to real sunshine (SunController), so the sun and the lamps are in the same real
	// units and the eye adaptation's limits are real-world EVs. (Scaling the lamps down instead left the lamps' glows and
	// the lit windows real, and blinding at night.)
	public static class Units {
		private const float RealSunLux = 100000.0f;

		// real lux per lux of the track's sun (1 where it is already real)
		public static float PhysicalScale { get; private set; } = 1.0f;
		// the same in stops
		public static float Log2Scale { get; private set; }

		// the track's own daylight sun (its intensity before NatureFX touched it)
		public static void FromSun(float gameSunLux) {
			float scale = gameSunLux > 0.0f && gameSunLux < 1000.0f ? Mathf.Clamp(RealSunLux / gameSunLux, 1.0f, 1e6f) : 1.0f;
			PhysicalScale = scale;
			Log2Scale = Mathf.Log(scale, 2.0f);
		}
	}
}

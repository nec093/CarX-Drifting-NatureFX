using UnityEngine;

namespace NatureFX.Nature {
	// Must match unity/Assets/NatureFX/ShaderLibrary/NFX_*.hlsl
	public static class ShaderGlobals {
		public const int MaxInteractors = 8;

		// xyz: world wind direction (normalized), w: strength
		public static readonly int Wind = Shader.PropertyToID("_NFX_Wind");
		// x: gust frequency, y: gust strength, z: flutter frequency, w: flutter strength
		public static readonly int WindParams = Shader.PropertyToID("_NFX_WindParams");
		// Own time, so wind keeps moving smoothly regardless of game time scale
		public static readonly int WindTime = Shader.PropertyToID("_NFX_WindTime");

		// x: snow, y: leaves, z: dust, w: wetness (0..1)
		public static readonly int Accumulation = Shader.PropertyToID("_NFX_Accumulation");

		// xyz: absolute world position, w: radius. Cars push foliage away.
		public static readonly int Interactors = Shader.PropertyToID("_NFX_Interactors");
		public static readonly int InteractorCount = Shader.PropertyToID("_NFX_InteractorCount");
	}
}

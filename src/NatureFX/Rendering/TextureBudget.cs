using UnityEngine;

namespace NatureFX.Rendering {
	// Unity's texture mip streaming budget (Settings.TextureStreamingBudget, F8 > Developer > Video memory). The game
	// streams its textures with a budget of about 4 GB, far above what its maps use, so every texture keeps its
	// full size mips however small it is on screen; a lower budget lets Unity drop the finest mips of the textures
	// seen smallest (only textures imported with mip streaming). 0 gives the game its own value back.
	public static class TextureBudget {
		private static float original_ = -1.0f;

		public static void Apply(float budgetMb) {
			if (!QualitySettings.streamingMipmapsActive) {
				return;
			}
			if (original_ < 0.0f) {
				original_ = QualitySettings.streamingMipmapsMemoryBudget;
			}
			float want = budgetMb > 0.0f ? Mathf.Max(256.0f, budgetMb) : original_;
			if (Mathf.Abs(QualitySettings.streamingMipmapsMemoryBudget - want) > 0.5f) {
				QualitySettings.streamingMipmapsMemoryBudget = want;
			}
		}

		public static string Status {
			get {
				if (!QualitySettings.streamingMipmapsActive) {
					return "mip streaming is off in this game";
				}
				return $"budget {QualitySettings.streamingMipmapsMemoryBudget:0} MB (the game's {(original_ >= 0.0f ? original_ : QualitySettings.streamingMipmapsMemoryBudget):0}); " +
				       $"textures now {Texture.currentTextureMemory / 1048576:0} MB, wanted {Texture.desiredTextureMemory / 1048576:0} MB, " +
				       $"not streamed {Texture.nonStreamingTextureMemory / 1048576:0} MB";
			}
		}
	}
}

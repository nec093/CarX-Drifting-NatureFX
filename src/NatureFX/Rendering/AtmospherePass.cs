using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// The sky, the clouds, the stars and the fog from the own engine, whatever the ray traced effects are set to
	// (Settings.NativeSky / NativeFog). Two instances in the AfterOpaqueAndSky volume:
	//   the tracer, first: the atmosphere's tables, the clouds (and their history), their shadow map, the sky's light
	//   as an ambient probe (NativeEngine.TraceSky) and the 3D fog (NativeEngine.TraceFogVolume), all lit by
	//   SunController's sky light: the sun, or the moon at night;
	//   the composite, last (after the ray traced shadows and reflections): the sky pixels drawn from the engine's sky
	//   with its clouds, sun or moon disc and stars, and the fog over everything (NFX_Atmosphere.shader).
	// What the ray traced shadow pass reads from it: the cloud shadow map and the sky probe (Shared).
	public class AtmospherePass : CustomPass {
		public const string ShaderName = "Hidden/NatureFX/Atmosphere";

		public bool IsComposite;
		public Material Material;     // NFX_Atmosphere (the composite)
		public LocalLights Lights;    // the fog's lamps and headlights

		// settings (RayTracingEffects)
		public bool NativeSky, NativeFog, Clouds;
		public bool CloudShadows = true, SkyAmbient = true;
		public float Anisotropy = 0.6f;

		// what the other passes read, this frame
		public static class Shared {
			public static RTHandle CloudShadow;
			public static Vector4 CloudShadowParams;  // xy centre (world xz), z size (m), w 1 = on
			public static float SkyLight;              // the share of the engine's sky light in place of HDRP's (0..1)
			public static bool SkyDrawn;
		}

		public static string SkyStatus { get; private set; } = "off";
		// A3: the engine's sky as a cube map, HDRP's sky (VolumeOverhaul: an HDRI sky of it), its reflections,
		// reflection probes and ambient probe; null until the first frame of the engine's sky
		public static RenderTexture SkyCube { get; private set; }
		public bool FeedHdrp = true;
		// the own sky as HDRP's cube map: not once HDRP draws no sky (OwnLighting)
		private bool Feed => FeedHdrp && !OwnLighting.Sky;
		public float StarBrightness = 4.0f;
		// what the cube HDRP gets holds: the texel straight up and one at the horizon, read back now and then (F8)
		public static string CubeStatus { get; private set; } = "not read yet";
		private static float nextCubeRead_;

		private static void ReadCube() {
			if (SkyCube == null || Time.unscaledTime < nextCubeRead_) {
				return;
			}
			nextCubeRead_ = Time.unscaledTime + 2.0f;
			int size = NativeEngine.SkyCubeSize;
			// face 2 is +Y (straight up), face 4 is +Z (the horizon in the middle row)
			UnityEngine.Rendering.AsyncGPUReadback.Request(SkyCube, 0, size / 2, 1, size / 2, 1, 2, 1, up => {
				if (up.hasError) {
					CubeStatus = "read back failed";
					return;
				}
				var u = up.GetData<ushort>();
				UnityEngine.Rendering.AsyncGPUReadback.Request(SkyCube, 0, size / 2, 1, size / 2, 1, 4, 1, side => {
					if (side.hasError) {
						CubeStatus = "read back failed";
						return;
					}
					var h = side.GetData<ushort>();
					CubeStatus = $"up ({Half(u[0]):G3}, {Half(u[1]):G3}, {Half(u[2]):G3}) nits, horizon ({Half(h[0]):G3}, {Half(h[1]):G3}, {Half(h[2]):G3})";
				});
			});
		}

		private static float Half(ushort h) => Mathf.HalfToFloat(h);

		// HDRP's own ambient probe (what it made of its sky) against the engine's, straight up: whether HDRP lights
		// with the cube at all
		public static string ProbeStatus { get; private set; } = "not read yet";
		private static float nextProbeRead_, nextStatusRead_;

		private static void ReadProbes(GraphicsBuffer hdrp) {
			if (hdrp == null || NativeEngine.SkyProbe == null || Time.unscaledTime < nextProbeRead_) {
				return;
			}
			// often: the sky's hand-over (SunController.SkyMeasured) matches the sky's brightness by these readings
			nextProbeRead_ = Time.unscaledTime + 2.0f;
			int generation = SunController.SkyGeneration;
			float rawAbove = SunController.SkyLightAboveRaw, scale = SunController.SkyScale;
			UnityEngine.Rendering.AsyncGPUReadback.Request(NativeEngine.SkyProbe, s => {
				if (!s.hasError) {
					var up = Up(s.GetData<Vector4>());
					SunController.SkyMeasured(generation, 0.2126f * up.x + 0.7152f * up.y + 0.0722f * up.z, rawAbove, scale);
				}
			});
			if (Time.unscaledTime < nextStatusRead_) {
				return;
			}
			nextStatusRead_ = Time.unscaledTime + 2.0f;
			UnityEngine.Rendering.AsyncGPUReadback.Request(hdrp, a => {
				if (a.hasError) {
					return;
				}
				var h = a.GetData<Vector4>();
				UnityEngine.Rendering.AsyncGPUReadback.Request(NativeEngine.SkyProbe, b => {
					if (b.hasError) {
						return;
					}
					var e = b.GetData<Vector4>();
					ProbeStatus = $"HDRP {Up(h):G3}, engine {Up(e):G3} (irradiance / pi from straight up)";
				});
			});
		}

		// SampleSH9 for the normal straight up: x1 = SHA.y + SHA.w, no x2, minus SHC
		private static Vector3 Up(Unity.Collections.NativeArray<Vector4> sh) {
			if (sh.Length < 7) {
				return Vector3.zero;
			}
			return new Vector3(sh[0].y + sh[0].w - sh[6].x, sh[1].y + sh[1].w - sh[6].y, sh[2].y + sh[2].w - sh[6].z);
		}
		public static string StarStatus { get; private set; } = "off";
		public static bool FogActive => fogUsed_;
		public static string FogStatus { get; private set; } = "off";

		// the tracer's state (static: the composite instance reads it)
		private static RTHandle skyView_, skyTransmittance_, clouds_, cloudShadow_, cloudHistoryA_, cloudHistoryB_;
		private static RTHandle fogHistoryA_, fogHistoryB_, fogVolume_;
		private static bool cloudHistoryValid_, fogHistoryValid_, cloudsUsed_, skyUsed_, fogUsed_;
		private static Matrix4x4 prevView_ = Matrix4x4.identity;
		private static Vector2 prevTan_ = Vector2.one;
		private static Vector3 lastCamera_;
		private static float cameraHeight_;
		private static int frame_;
		private RTHandle color_;
		private MaterialPropertyBlock props_;

		private static readonly int TargetSizeId = Shader.PropertyToID("_NFX_TargetSize");
		private static readonly int NativeTargetId = Shader.PropertyToID("_NFX_NativeTarget");
		private static readonly int CloudCurrentId = Shader.PropertyToID("_NFX_CloudCurrent");
		private static readonly int CloudHistoryId = Shader.PropertyToID("_NFX_CloudHistory");
		private static readonly int CloudTemporalId = Shader.PropertyToID("_NFX_CloudTemporal");
		private static readonly int PrevViewXId = Shader.PropertyToID("_NFX_PrevViewX");
		private static readonly int PrevViewYId = Shader.PropertyToID("_NFX_PrevViewY");
		private static readonly int PrevViewZId = Shader.PropertyToID("_NFX_PrevViewZ");
		private static readonly int PrevTanId = Shader.PropertyToID("_NFX_PrevTan");
		// the frame the 3D fog was last made (TransparentFog: the see-through objects get it only then)
		public static int FogFrame { get; private set; } = -1;
		private static readonly int FogVolumeId = Shader.PropertyToID("_NFX_FogVolume");
		private static readonly int FogVolumeParamsId = Shader.PropertyToID("_NFX_FogVolumeParams");
		private static readonly int FogHeightId = Shader.PropertyToID("_NFX_FogHeight");
		private static readonly int FogSunId = Shader.PropertyToID("_NFX_FogSun");
		private static readonly int FogSunDirId = Shader.PropertyToID("_NFX_FogSunDir");
		private static readonly int SkyLightId = Shader.PropertyToID("_NFX_SkyLight");
		private static readonly int ColorTexId = Shader.PropertyToID("_NFX_ColorTex");
		private static readonly int SkyViewTex = Shader.PropertyToID("_NFX_SkyView");
		private static readonly int SkyTransmittanceTex = Shader.PropertyToID("_NFX_SkyTransmittance");
		private static readonly int CloudTex = Shader.PropertyToID("_NFX_CloudTex");
		private static readonly int SkyParamsId = Shader.PropertyToID("_NFX_SkyParams");
		private static readonly int SkyLightDirId = Shader.PropertyToID("_NFX_SkyLightDir");
		private static readonly int StarsTex = Shader.PropertyToID("_NFX_Stars");
		private static readonly int StarParamsId = Shader.PropertyToID("_NFX_StarParams");
		private static readonly int StarRowXId = Shader.PropertyToID("_NFX_StarRowX");
		private static readonly int StarRowYId = Shader.PropertyToID("_NFX_StarRowY");
		private static readonly int StarRowZId = Shader.PropertyToID("_NFX_StarRowZ");

		protected override void Setup(ScriptableRenderContext renderContext, CommandBuffer cmd) {
			if (IsComposite) {
				color_ = SharedColor.Acquire();
				props_ = new MaterialPropertyBlock();
			}
		}

		protected override void Cleanup() {
			SharedColor.Release(color_);
			color_ = null;
			if (!IsComposite) {
				foreach (var t in new[] { skyView_, skyTransmittance_, clouds_, cloudShadow_, cloudHistoryA_, cloudHistoryB_, fogHistoryA_, fogHistoryB_, fogVolume_ }) {
					t?.Release();
				}
				skyView_ = skyTransmittance_ = clouds_ = cloudShadow_ = cloudHistoryA_ = cloudHistoryB_ = fogHistoryA_ = fogHistoryB_ = fogVolume_ = null;
			}
		}

		private static RTHandle Target2D(int w, int h, string name, GraphicsFormat format = GraphicsFormat.R16G16B16A16_SFloat) {
			return RTHandles.Alloc(w, h, TextureXR.slices, dimension: TextureXR.dimension, colorFormat: format,
				enableRandomWrite: true, filterMode: FilterMode.Bilinear, name: name);
		}

		private static RTHandle Target3D(string name) {
			return RTHandles.Alloc(NativeEngine.FogW, NativeEngine.FogH, NativeEngine.FogD, dimension: TextureDimension.Tex3D,
				colorFormat: GraphicsFormat.R16G16B16A16_SFloat, enableRandomWrite: true, filterMode: FilterMode.Bilinear,
				wrapMode: TextureWrapMode.Clamp, name: name);
		}

		protected override void Execute(CustomPassContext ctx) {
			using var cpuTime = NatureFX.Tools.CpuTimings.Scope("pass: sky, clouds, fog (AtmospherePass)"); // (the debug page)
			if (!RTSurfaceTrace.IsScreenCamera(ctx.hdCamera)) {
				return;
			}
			if (IsComposite) {
				Draw(ctx);
				return;
			}
			ResetShared();
			if (!NativeSky) {
				SkyStatus = "off (HDRP's sky)";
				FogStatus = "off (HDRP's fog)";
				ctx.cmd.SetGlobalVector(FogVolumeParamsId, Vector4.zero);
				return;
			}
			if (!NativeEngine.Active || NativeEngine.Bridge == null) {
				SkyStatus = FogStatus = $"waiting for the own engine ({NativeEngine.Info})";
				ctx.cmd.SetGlobalVector(FogVolumeParamsId, Vector4.zero);
				return;
			}
			Trace(ctx);
		}

		// What the other passes read from this one, cleared: at the start of each trace, and when the pass is switched
		// off (RayTracingEffects.Apply), since a disabled pass does not run and left the last frame's cloud shadow, fog
		// and sky light standing (a still cloud shadow on the ground, no light shafts, old fog on glass and particles)
		public void ResetShared() {
			skyUsed_ = fogUsed_ = cloudsUsed_ = false;
			Shared.SkyDrawn = false;
			Shared.CloudShadowParams = Vector4.zero;
			Shared.SkyLight = 0.0f;
			CloudController.NativeShadowsActive = false;
		}

		public void Disable() {
			ResetShared();
			Shader.SetGlobalVector(FogVolumeParamsId, Vector4.zero);
			SkyStatus = "off (HDRP's sky)";
			FogStatus = "off (HDRP's fog)";
		}

		private void Trace(CustomPassContext ctx) {
			var cmd = ctx.cmd;
			var bridge = NativeEngine.Bridge;
			var cam = ctx.hdCamera.camera;
			var camPos = cam.transform.position;
			bool reset = (camPos - lastCamera_).sqrMagnitude > 50.0f * 50.0f;
			var dir = SunController.SkyLightDir;
			var ground = SunController.SkyLightGround;
			var lightDir = new Vector4(dir.x, dir.y, dir.z, dir.y > SunController.DiscGoneY ? 1.0f : 0.0f);
			var lightGround = new Vector4(ground.x, ground.y, ground.z, 0.0f);
			// the frame's constants (the ray traced shadow pass, if it runs, adds its own ambient values)
			var hdrpProbe = HdrpBindings.AmbientProbe(ctx.hdCamera);
			if (hdrpProbe == null) {
				// the sky tables and the fog are issued with the probe's buffer bound: without one the engine skips
				// them silently, and the "made" flags below would have kept it from trying again
				SkyStatus = FogStatus = "unavailable (HDRP's ambient probe not reachable)";
				return;
			}
			NativeEngine.BeginFrame(cam, lightDir, SunController.SkyLightRadius, lightGround, frame_++, Vector4.zero, Lights, OwnLighting.Probe(ctx.hdCamera) ?? hdrpProbe);
			ReadProbes(hdrpProbe);

			float lux = SunController.SkyLightAbove;
			cameraHeight_ = Mathf.Max(1.0f, camPos.y);
			if (lux <= 0.0f) {
				SkyStatus = "waiting for the sun";
				lastCamera_ = camPos;
				return;
			}
			if (skyView_ == null) {
				skyView_ = Target2D(NativeEngine.SkyViewW, NativeEngine.SkyViewH, "NFX_SkyView", GraphicsFormat.R32G32B32A32_SFloat);
				skyTransmittance_ = Target2D(NativeEngine.TransmittanceW, NativeEngine.TransmittanceH, "NFX_SkyTransmittance");
			}
			var w = Weather.Current;
			float aerosols = 1.0f + 2.0f * w.Cloud + 2.0f * w.Rain;
			bool cloudsOn = Clouds && CloudController.NativeOn;
			int cw = Mathf.Max(1, (ctx.hdCamera.actualWidth + 3) / 4), ch = Mathf.Max(1, (ctx.hdCamera.actualHeight + 3) / 4);
			if (cloudsOn && (clouds_ == null || clouds_.rt.width != cw || clouds_.rt.height != ch)) {
				clouds_?.Release();
				cloudHistoryA_?.Release();
				cloudHistoryB_?.Release();
				clouds_ = Target2D(cw, ch, "NFX_Clouds");
				cloudHistoryA_ = Target2D(cw, ch, "NFX_CloudHistoryA");
				cloudHistoryB_ = Target2D(cw, ch, "NFX_CloudHistoryB");
				cloudHistoryValid_ = false;
			}
			bool shadowsOn = cloudsOn && CloudShadows;
			if (shadowsOn && cloudShadow_ == null) {
				cloudShadow_ = Target2D(NativeEngine.CloudShadowSize, NativeEngine.CloudShadowSize, "NFX_CloudShadow");
			}
			var cloudSize = new Vector4(cw, ch, 1.0f / cw, 1.0f / ch);
			// the engine's sky light: by day, and at night lit by the moon; without the moon HDRP's night sky light stays
			// the sky's light by the sun goes with the twilight itself (the sky tables dim it as the sun sinks); only past
			// the astronomical twilight (-12 to -18°, a sky lit by no light at all: moonlight off) it fades out. It faded
			// between -5.7° and 0° before: with the sky by the sun down to -7.5° (SunController), the sky's ambient light
			// was gone from -5.7° until the moon took over, then came back at once.
			float day = SunController.SkyLightIsMoon ? 1.0f
				: Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(Mathf.Sin(-18.0f * Mathf.Deg2Rad), Mathf.Sin(-12.0f * Mathf.Deg2Rad), SunController.RealSunY));
			if (Feed && SkyCube == null) {
				SkyCube = new RenderTexture(NativeEngine.SkyCubeSize, NativeEngine.SkyCubeSize, 0, GraphicsFormat.R16G16B16A16_SFloat) {
					dimension = TextureDimension.Cube, useMipMap = false, name = "NFX_SkyCube", hideFlags = HideFlags.DontSave
				};
				SkyCube.Create();
			}
			NativeEngine.TraceSky(cmd, bridge, skyView_, skyTransmittance_, lux, cameraHeight_, aerosols,
				cloudsOn ? clouds_ : null, cloudSize, CloudController.NativeCover, CloudController.NativeMotion,
				shadowsOn ? cloudShadow_ : null, SkyAmbient, Feed ? SkyCube : null);
			if (cloudsOn) {
				// each frame marches the clouds from another offset: blended with the history at last frame's place
				int temporal = bridge.FindKernel("CloudTemporal");
				cmd.SetComputeVectorParam(bridge, TargetSizeId, cloudSize);
				cmd.SetComputeVectorParam(bridge, CloudTemporalId, new Vector4(cloudHistoryValid_ && !reset ? 0.12f : 1.0f, 0.0f, 0.0f, 0.0f));
				cmd.SetComputeVectorParam(bridge, PrevViewXId, prevView_.GetRow(0));
				cmd.SetComputeVectorParam(bridge, PrevViewYId, prevView_.GetRow(1));
				cmd.SetComputeVectorParam(bridge, PrevViewZId, prevView_.GetRow(2));
				cmd.SetComputeVectorParam(bridge, PrevTanId, new Vector4(prevTan_.x, prevTan_.y, 0.0f, 0.0f));
				cmd.SetComputeTextureParam(bridge, temporal, CloudCurrentId, clouds_);
				cmd.SetComputeTextureParam(bridge, temporal, CloudHistoryId, cloudHistoryA_);
				cmd.SetComputeTextureParam(bridge, temporal, NativeTargetId, cloudHistoryB_);
				cmd.DispatchCompute(bridge, temporal, (cw + 7) / 8, (ch + 7) / 8, TextureXR.slices);
				(cloudHistoryA_, cloudHistoryB_) = (cloudHistoryB_, cloudHistoryA_);
				cloudHistoryValid_ = true;
			}
			prevView_ = cam.worldToCameraMatrix;
			float tanY = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
			prevTan_ = new Vector2(tanY * cam.aspect, tanY);
			float texel = NativeEngine.CloudShadowArea / NativeEngine.CloudShadowSize;
			Shared.CloudShadow = cloudShadow_;
			Shared.CloudShadowParams = shadowsOn
				? new Vector4(Mathf.Floor(camPos.x / texel) * texel, Mathf.Floor(camPos.z / texel) * texel, NativeEngine.CloudShadowArea, 1.0f)
				: Vector4.zero;
			CloudController.NativeShadowsActive = shadowsOn;
			cloudsUsed_ = cloudsOn;
			skyUsed_ = true;
			Shared.SkyDrawn = true;
			bool probe = SkyAmbient && NativeEngine.SkyProbe != null;
			// HDRP's own sky light is the engine's sky when it gets the cube: nothing left to swap in the composite
			Shared.SkyLight = probe && !(Feed && SkyCube != null) ? day : 0.0f;
			if (probe) {
				cmd.SetGlobalBuffer(SkyLightId, NativeEngine.SkyProbe);
			}
			SkyStatus = $"own engine, {(SunController.SkyLightIsMoon ? "moon" : "sun")} {lux:0.###} lx above the air, haze x{aerosols:0.00}";
			ReadCube();

			// the 3D fog
			var fog = NatureFX.Map.MapFog.NativeParams;
			fogUsed_ = NativeFog && fog.w > 0.0f && probe;
			if (fogUsed_) {
				FogFrame = Time.frameCount;
				if (fogVolume_ == null) {
					fogHistoryA_ = Target3D("NFX_FogHistoryA");
					fogHistoryB_ = Target3D("NFX_FogHistoryB");
					fogVolume_ = Target3D("NFX_FogVolume");
					fogHistoryValid_ = false;
				}
				var fogParams = new Vector4(fog.x, fog.y, fog.z, Mathf.Clamp(Anisotropy, 0.0f, 0.9f));
				int boxes = GatherBoxes(camPos);
				RTTimings.FogVolume.Begin(cmd);
				NativeEngine.TraceFogVolume(cmd, bridge, fogParams, fogHistoryA_, fogHistoryB_, fogVolume_, camPos - lastCamera_,
					fogHistoryValid_ && !reset ? (boxes > 0 ? 0.3f : 0.1f) : 1.0f, boxes_, boxes, TireSmoke.FogSmoke);
				RTTimings.FogVolume.End(cmd);
				(fogHistoryA_, fogHistoryB_) = (fogHistoryB_, fogHistoryA_);
				fogHistoryValid_ = true;
				cmd.SetGlobalTexture(FogVolumeId, fogVolume_);
				cmd.SetGlobalVector(FogHeightId, fogParams);
				cmd.SetGlobalVector(FogSunId, new Vector4(ground.x, ground.y, ground.z, lightDir.w));
				cmd.SetGlobalVector(FogSunDirId, lightDir);
				FogStatus = (fog.x > 0.0f ? $"own engine, visibility {3.0f / fog.x / 1000.0f:0.0} km at the base" : "own engine, clear air") + $", {BoxCount} local fog / smoke boxes"
				            + (NativeEngine.FogLightLists ? $", {NativeEngine.FogLampCount} lamps in tile lists" : ", lamps unlisted (all per cell)");
			} else {
				FogStatus = NativeFog ? "waiting for the fog values" : "off (HDRP's fog)";
			}
			cmd.SetGlobalVector(FogVolumeParamsId, new Vector4(fogUsed_ ? 1.0f : 0.0f, probe ? 1.0f : 0.0f, 0.0f, 0.0f));
			lastCamera_ = camPos;
		}

		// Roadmap C: HDRP's local fog boxes (the map's fog volumes, MapVolumes; the tyre smoke, TireSmoke) as the 3D
		// fog's boxes, nearest first, at most 64; HDRP's volumetric fog is off meanwhile (NatureFXMod)
		private static readonly Vector4[] boxes_ = new Vector4[64 * 5];
		private static LocalVolumetricFog[] found_ = System.Array.Empty<LocalVolumetricFog>();
		private static float nextFind_;
		private static readonly System.Collections.Generic.List<(float Distance, LocalVolumetricFog Fog)> near_ = new();
		public static int BoxCount { get; private set; }

		private static int GatherBoxes(Vector3 camera) {
			if (Time.unscaledTime >= nextFind_) {
				nextFind_ = Time.unscaledTime + 0.5f;
				found_ = Object.FindObjectsByType<LocalVolumetricFog>(FindObjectsSortMode.None);
			}
			near_.Clear();
			foreach (var f in found_) {
				// NatureFX's own fog puffs (TireSmoke without its particles) are not the fog's: smoke is particles
				if (f == null || !f.isActiveAndEnabled || f.parameters.meanFreePath <= 0.0f || f.name == "NatureFX.Puff") {
					continue;
				}
				float d = (f.transform.position - camera).sqrMagnitude;
				if (d < 3000.0f * 3000.0f) {
					near_.Add((d, f));
				}
			}
			near_.Sort((a, b) => a.Distance.CompareTo(b.Distance));
			int count = Mathf.Min(near_.Count, 64);
			for (int i = 0; i < count; ++i) {
				var f = near_[i].Fog;
				var p = f.parameters;
				var t = f.transform;
				var half = Vector3.Max(p.size * 0.5f, Vector3.one * 0.05f);
				var c = t.position;
				var albedo = p.albedo.linear;
				boxes_[i * 5] = new Vector4(c.x, c.y, c.z, 1.0f / p.meanFreePath);
				var ax = t.right / half.x;
				var ay = t.up / half.y;
				var az = t.forward / half.z;
				boxes_[i * 5 + 1] = new Vector4(ax.x, ax.y, ax.z, albedo.r);
				boxes_[i * 5 + 2] = new Vector4(ay.x, ay.y, ay.z, albedo.g);
				boxes_[i * 5 + 3] = new Vector4(az.x, az.y, az.z, albedo.b);
				// HDRP's look of it: how far in it fades, the size of its noise, round (exponential falloff) or a box
				float fadeShare = Mathf.Clamp((p.positiveFade.x + p.positiveFade.y + p.positiveFade.z + p.negativeFade.x + p.negativeFade.y + p.negativeFade.z) / 6.0f, 0.05f, 1.0f);
				float noiseSize = p.textureTiling.x > 0.0f ? p.size.x / p.textureTiling.x : Mathf.Max(p.size.x / 3.0f, 0.5f);
				boxes_[i * 5 + 4] = new Vector4(fadeShare, Mathf.Clamp(noiseSize, 0.3f, 50.0f),
					p.falloffMode == LocalVolumetricFogFalloffMode.Exponential ? 1.0f : 0.0f, 0.0f);
			}
			BoxCount = count;
			return count;
		}

		// the sky pixels and the fog over the finished opaque image
		private void Draw(CustomPassContext ctx) {
			if (Material == null || (!skyUsed_ && !fogUsed_)) {
				return;
			}
			var cmd = ctx.cmd;
			CustomPassUtils.Copy(ctx, ctx.cameraColorBuffer, color_);
			props_.Clear();
			props_.SetTexture(ColorTexId, color_);
			props_.SetTexture(SkyViewTex, skyUsed_ ? skyView_ : color_);
			props_.SetTexture(SkyTransmittanceTex, skyUsed_ ? skyTransmittance_ : color_);
			props_.SetTexture(CloudTex, cloudsUsed_ ? cloudHistoryA_ : color_);
			props_.SetVector(SkyParamsId, new Vector4(skyUsed_ ? (cloudsUsed_ ? 2.0f : 1.0f) : 0.0f, SunController.SkyLightAbove, cameraHeight_, SunController.SkyLightRadius));
			var d = SunController.SkyLightDir;
			props_.SetVector(SkyLightDirId, new Vector4(d.x, d.y, d.z, SunController.SkyLightIsMoon ? 1.0f : 0.0f));
			// stars as the sun sets (nautical dusk), in front of nothing but the air and the clouds
			var stars = NatureFX.Map.MapNight.StarTexture;
			float night = Mathf.SmoothStep(0.0f, 1.0f, Mathf.InverseLerp(-0.03f, -0.2f, SunController.RealSunY));
			bool starsOn = stars != null && NatureFX.Map.MapNight.StarsWanted && night > 0.0f;
			if (starsOn) {
				props_.SetTexture(StarsTex, stars);
				var rot = Matrix4x4.Rotate(Quaternion.Euler(NatureFX.Map.MapNight.StarRotation));
				props_.SetVector(StarRowXId, rot.GetRow(0));
				props_.SetVector(StarRowYId, rot.GetRow(1));
				props_.SetVector(StarRowZId, rot.GetRow(2));
			}
			props_.SetVector(StarParamsId, new Vector4(starsOn ? night * StarBrightness : 0.0f, 0.0f, 0.0f, 0.0f));
			StarStatus = stars == null ? "no star field yet" : !NatureFX.Map.MapNight.StarsWanted ? "off (the map)" : night <= 0.0f ? "day" : $"drawn, {night:P0} of the night, x{StarBrightness:0.0}";
			CoreUtils.SetRenderTarget(cmd, ctx.cameraColorBuffer, ClearFlag.None);
			CoreUtils.DrawFullScreen(cmd, Material, props_, 0);
		}
	}
}

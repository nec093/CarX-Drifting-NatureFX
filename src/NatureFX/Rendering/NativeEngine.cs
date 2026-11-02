using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using KSL.API;
using NatureFX.Nature;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace NatureFX.Rendering {
	// NatureFX's own D3D12 ray tracing engine (native/NFXNative, kino/mods/NatureFX/NFXNative.dll). Unity's player
	// here reports no inline ray tracing although the GPU and the D3D12 runtime have DXR 1.1, so the engine drives
	// Unity's D3D12 device itself.
	// Stage 1: load it, report the device, trace one inline test ray.
	// Stage 2: build the ray tracing scene from Unity's meshes (RayTracingScene hands over what it adds) and check
	// every instance with a ray from above; catch Unity's command queue and run a test on it from the render thread.
	public static class NativeEngine {
		private const string DllName = "NFXNative";
		// Through Unity's own plugin loader, which hands a plugin its interfaces (UnityPluginLoad) when it loads it;
		// a path relative to the game folder, the player's working directory
		private const string DllPath = "kino/mods/NatureFX/NFXNative";

		[DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool SetDllDirectoryW(string path);

		[DllImport(DllPath, EntryPoint = "NFX_Native_PluginInterface")]
		private static extern int PluginInterfaceByPath();

		[DllImport(DllName)] private static extern int NFX_Native_Init(IntPtr unityResource);
		[DllImport(DllName)] private static extern int NFX_Native_Info(byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_RayQueryTest();
		[DllImport(DllName)] private static extern int NFX_Native_Dxr12Probe([MarshalAs(UnmanagedType.LPStr)] string sdkPath, byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_StartRt([MarshalAs(UnmanagedType.LPStr)] string sdkPath);
		[DllImport(DllName)] private static extern void NFX_Native_SetRtActive(int active);
		[DllImport(DllName)] private static extern void NFX_Native_SetRtOmm(int enabled);
		[DllImport(DllName)] private static extern int NFX_Native_SetPipeline(int pipeline);
		[DllImport(DllName)] private static extern int NFX_Native_GpuMemory(out ulong usedMb, out ulong budgetMb);

		// The game's video memory use and budget (MB), as the OS counts them; logged every 30 s while the engine runs
		public static bool GpuMemoryMB(out ulong used, out ulong budget) {
			used = budget = 0;
			try {
				return Ready && NFX_Native_GpuMemory(out used, out budget) == 1;
			} catch (Exception) {
				return false;
			}
		}

		public static string GpuMemory {
			get {
				try {
					return Ready && NFX_Native_GpuMemory(out ulong used, out ulong budget) == 1 ? $"{used} MB of {budget} MB" : "unknown";
				} catch (Exception) {
					return "unknown";
				}
			}
		}
		private static float nextMemoryLog_;
		[DllImport(DllName)] private static extern void NFX_Native_SetWind(float[] frame, int active);
		[DllImport(DllName)] private static extern int NFX_Native_RtStatus(byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_NrdStatus(byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_DlssStatus(byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_DlssCaps();
		[DllImport(DllName)] private static extern void NFX_Native_ClearScene();
		[DllImport(DllName)] private static extern int NFX_Native_AddGeometry(IntPtr vertexBuffer, ulong vertexAddressOffset, uint vertexStride, uint vertexCount, int positionFormat,
		                                                                      IntPtr indexBuffer, ulong indexAddressOffset, uint indexCount, int indexFormat, int opaque,
		                                                                      IntPtr uvBuffer, ulong uvAddressOffset, uint uvStride, int uvFormat,
		                                                                      IntPtr normalBuffer, ulong normalAddressOffset, uint normalStride, int normalFormat);
		[DllImport(DllName)] private static extern int NFX_Native_AddGeometryData(float[] positions, int vertexCount, int[] indices, int indexCount, int opaque, float[] uvs,
		                                                                          float[] normals);
		[DllImport(DllName)] private static extern void NFX_Native_SetInstances(float[] transforms, int[] geometries, int[] flags, int[] materials, int count);
		[DllImport(DllName)] private static extern int NFX_Native_AddMaterial(IntPtr texture, int srgb, float cutoff, float alphaScale, float scaleX, float scaleY, float offsetX, float offsetY,
		                                                                      float r, float g, float b, float metallic, float smoothness,
		                                                                      IntPtr mask, float metallicMin, float metallicMax, float smoothnessMin, float smoothnessMax, int flags,
		                                                                      IntPtr emissionMap, int emissionSrgb, float er, float eg, float eb);
		[DllImport(DllName)] private static extern void NFX_Native_ReleaseGeometry(int id);
		[DllImport(DllName)] private static extern void NFX_Native_ReleaseMaterial(int id);
		[DllImport(DllName)] private static extern int NFX_Native_BuildScene();
		[DllImport(DllName)] private static extern int NFX_Native_SceneStats(byte[] buffer, int size);
		[DllImport(DllName)] private static extern int NFX_Native_TraceTestRays(float[] rays, int count, float[] distances, int[] instances);
		[DllImport(DllName)] private static extern int NFX_Native_CaptureQueue();
		[DllImport(DllName)] private static extern int NFX_Native_QueueStatus(int giveUp);
		[DllImport(DllName)] private static extern IntPtr NFX_Native_RenderEventFunc();
		[DllImport(DllName)] private static extern int NFX_Native_LoadShaders(string folder);
		[DllImport(DllName)] private static extern int NFX_Native_ShaderStatus(byte[] buffer, int size);

		// The engine's shaders from kino/mods/NatureFX/NativeShaders (tools/build_native.ps1 -ShadersOnly): read here,
		// taken in by render event 8 on the render thread, which makes the trace pipelines again
		public static string ReloadShaders() {
			if (!Ready) {
				return "the engine is not running";
			}
			try {
				string folder = Path.Combine(Kino.Paths.Mods, "NatureFX", "NativeShaders");
				if (!Directory.Exists(folder)) {
					return $"no {folder}";
				}
				int count = NFX_Native_LoadShaders(folder);
				InvalidateSky();
				var cmd = new CommandBuffer { name = "NatureFX shader reload" };
				cmd.IssuePluginEvent(NFX_Native_RenderEventFunc(), 8);
				Graphics.ExecuteCommandBuffer(cmd);
				cmd.Release();
				return $"{count} shader files read";
			} catch (Exception e) {
				return $"unavailable ({e.GetType().Name}: the engine's DLL is older than the mod)";
			}
		}

		public static string ShaderStatus {
			get {
				try {
					var buffer = new byte[256];
					int length = NFX_Native_ShaderStatus(buffer, buffer.Length);
					return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
				} catch (Exception e) {
					return $"unavailable ({e.GetType().Name})";
				}
			}
		}
		[DllImport(DllName)] private static extern int NFX_Native_QueueTestResult();
		[DllImport(DllName)] private static extern IntPtr NFX_Native_RenderEventAndDataFunc();
		[DllImport(DllName)] private static extern void NFX_Native_UpdateTransforms(int[] indices, float[] transforms, int count);
		[DllImport(DllName)] private static extern int NFX_Native_SunReady();
		[DllImport(DllName)] private static extern void NFX_Native_SunCounters(out int traced, out int skipped);

		public const string BridgeShaderName = "NFX_NativeBridge";

		// What the ray tracing scene holds: one mesh / sub mesh placed in the world
		public struct Instance {
			public Mesh Mesh;
			public int SubMesh;
			public Matrix4x4 Matrix;
			public Material Material;  // what the ray tracing scene hits: the proxy, or a NatureFX material itself
			public Color? Color;       // the renderer's own colour (car paint), over the material's
			public Vector4? Finish;    // the renderer's own metalness, smoothness, clear coat (each < 0: the material's)
			public bool AlphaTest;
			public string Kind;        // static, car, skinned, tree, terrain, glass
			public bool Glass;         // seen only by the glass trace (instance mask 2)
			public bool Wind;          // NatureFX/Foliage: swayed by the wind as it is drawn (Wind.inl)
			public Transform Follow;   // moving instances: their transform now
			public bool Unscaled;      // baked skinned meshes: position and rotation only
		}

		public static bool Ready { get; private set; }

		// NFX_FLAG_* of NFXCommon.hlsli: sun shadows through glass dimmed
		public static bool GlassShadows;
		// NFX_FLAG_LAMP_MASK: HDRP's lamps shadowed one by one (TraceLampMask, roadmap A4); LocalShadows only the
		// lights NatureFX adds
		public static bool LampMask;
		// NFX_FLAG_OWN_GLASS: Glass.hlsl draws the glass whole (OwnGlass hides HDRP's)
		public static bool OwnGlass;
		// NFX_FLAG_FOG_LISTS: the 3D fog loops over the lamps listed per tile of its grid (the bridge's PrepareFogVolume)
		// instead of all of them; off for comparing (the same picture, only slower)
		public static bool FogLightLists = true;
		private static bool fogListsIssued_; // set while TraceFogVolume issues its trace
		// what the loaded DLL can do (NFX_Native_Info's "features:"): a mod newer than its DLL must not hide HDRP's glass
		public static bool Supports(string feature) => Info != null && Info.Contains(feature);

		// Emissive surfaces of the static scene that are bright enough to light their surroundings: LocalLights hands
		// the nearest ones to the traces as lamps HDRP does not have (light added where it gets through)
		public struct Emitter {
			public Vector3 Position;
			public Color Intensity;  // cd, as a lamp of the same brightness facing the point
			public float Range;
			public float Radius;     // the shape the soft shadows sample
		}
		public static readonly List<Emitter> Emitters = new();
		private static int loggedEmitters_;
		private const float EmitterMinLuminance = 200.0f;  // nits: neon, lit signs and lamp shades, not glowing paint

		// DXR 1.0 (TraceRay in ray tracing pipelines) or DXR 1.1 (inline ray queries), Settings.RTBackend 0 / 1
		public static bool Pipeline { get; set; }
		private static int pipelineSet_ = -1;
		private static bool pipelineAvailable_ = true;
		public static string ModeName => pipelineSet_ == 1 ? "DXR 1.0" : "DXR 1.1";

		private static void UpdatePipeline() {
			int wanted = Pipeline && pipelineAvailable_ ? 1 : 0;
			if (wanted == pipelineSet_) {
				return;
			}
			int available = NFX_Native_SetPipeline(wanted);
			if (wanted == 1 && available != 1) {
				// the pipelines are made with the scene's first build: until then, and if they fail, inline queries
				pipelineSet_ = 0;
				return;
			}
			pipelineSet_ = wanted;
			Kino.Log.Info($"[NatureFX] Native engine: traces with {ModeName}");
		}

		// The traced foliage sways with the wind NatureFX/Foliage draws (Settings.RTWind)
		public static bool Wind { get; set; }
		private static readonly float[] windFrame_ = new float[48];
		private static bool windFailed_;

		// Once a frame: the wind globals NatureFX/Foliage draws with (WindController), as WindDisplace.hlsl reads them
		private static void UpdateWind() {
			if (windFailed_) {
				return;
			}
			var f = windFrame_;
			Put(f, 0, Shader.GetGlobalVector(ShaderGlobals.Wind));
			Put(f, 4, Shader.GetGlobalVector(ShaderGlobals.WindParams));
			// HDRP's _TimeParameters.x, the time NatureFX/Foliage sways with
			f[8] = Time.time;
			// the traced foliage sways even where the drawn one does not (MapProfile.FoliageWind)
			f[9] = WindController.TracedBend;
			int count = Mathf.Clamp(Shader.GetGlobalInt(ShaderGlobals.InteractorCount), 0, ShaderGlobals.MaxInteractors);
			f[10] = count;
			f[11] = 0.0f;
			var interactors = Shader.GetGlobalVectorArray(ShaderGlobals.Interactors);
			for (int i = 0; i < ShaderGlobals.MaxInteractors; ++i) {
				Put(f, 12 + i * 4, interactors != null && i < interactors.Length && i < count ? interactors[i] : Vector4.zero);
			}
			NFX_Native_SetWind(f, Wind ? 1 : 0);
		}

		// DXR 1.2 probe (Settings, on request): Shader Model 6.9 through an Agility SDK 1.619 device next to the game's
		// runtime, its opacity micromaps and shader execution reordering, and a buffer / fence shared with Unity's device
		public static string Dxr12Result { get; private set; } = "not run";

		// Stage 1 of the move to DXR 1.2 (RtDevice.inl): the sun shadows traced on an Agility SDK 1.619 device
		public static bool Dxr12Sun { get; set; }
		public static bool Dxr12SunPaused { get; set; } // benchmark: on Unity's device for a moment
		public static bool OmmPaused { get; set; }      // benchmark: the DXR 1.2 device without its opacity micromaps
		private static bool ommOff_;
		public static bool Dxr12SunActive => rtActive_;  // the sun traces go to the DXR 1.2 device (one frame late)

		// NVIDIA Real-Time Denoisers (NFXNative Nrd.inl): SIGMA denoises the sun shadows right after their trace (render
		// event 5); the shadow pass then skips its own filter for them. Set per frame by RayTracingEffects.
		public static bool NrdEnabled;     // the NVIDIA NRD denoiser is chosen (Settings.RTDenoiser 2)
		public static bool NrdSun;         // SIGMA denoises the sun shadows
		public static bool NrdAmbient;     // REBLUR denoises the ambient light / GI (set by the shadow pass)
		public static bool NrdLamps;       // REBLUR denoises the lamp shadows (set by the shadow pass)
		public static bool NrdReflections; // REBLUR_SPECULAR denoises the reflections
		// Developer tab: each signal can go back to NatureFX's own filter, and the cars' motion vector pass stay off,
		// to find which part of NRD a problem comes from (not saved)
		public static bool DevNrdSun = true, DevNrdAmbient = true, DevNrdLamps = true, DevNrdReflections = true, DevCarMotionVectors = true;
		public static bool NrdReset;       // the shadow pass dropped its history this frame
		public static int SunSamples = 1;  // sun shadow rays per pixel (SunShadow.hlsl; Dlaa: more while upscaling)
		public static string NrdStatus {
			get {
				if (!NrdEnabled) {
					return "off";
				}
				try {
					var buffer = new byte[256];
					int length = NFX_Native_NrdStatus(buffer, buffer.Length);
					return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
				} catch (Exception e) {
					return $"unavailable ({e.GetType().Name}: the engine's DLL is older than the mod)";
				}
			}
		}
		public static string DlssStatus {
			get {
				if (!Supports("dlss")) {
					return "the engine's DLL is older than the mod";
				}
				try {
					var buffer = new byte[256];
					int length = NFX_Native_DlssStatus(buffer, buffer.Length);
					return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
				} catch (Exception e) {
					return $"unavailable ({e.GetType().Name})";
				}
			}
		}
		// DLSS Ray Reconstruction on this GPU: -1 not known yet (NGX starts with the first DLSS frame), 0 no, 1 yes
		public static int DlssRayReconstructionAvailable {
			get {
				if (!Supports("dlss-rr")) {
					return 0;
				}
				try {
					int caps = NFX_Native_DlssCaps();
					return caps < 0 ? -1 : (caps & 2) != 0 ? 1 : 0;
				} catch (Exception) {
					return 0;
				}
			}
		}
		private static bool rtStarted_, rtFailed_, rtActive_;
		private static float nextRtReport_;

		private static string Dxr12Folder => Path.Combine(Kino.Paths.Mods, ShaderBundle.FolderName, "D3D12");

		public static string Dxr12SunStatus {
			get {
				if (!Dxr12Sun) {
					return "off";
				}
				if (!rtStarted_) {
					return rtFailed_ ? "could not start" : "starting";
				}
				try {
					var buffer = new byte[512];
					int length = NFX_Native_RtStatus(buffer, buffer.Length);
					return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
				} catch (Exception e) {
					return $"error ({e.GetType().Name})";
				}
			}
		}

		// Per frame: start the DXR 1.2 device when chosen (once), and send the sun traces to it or not
		private static void UpdateDxr12() {
			if (Dxr12Sun && !rtStarted_ && !rtFailed_) {
				string sdk = Dxr12Folder;
				if (!File.Exists(Path.Combine(sdk, "D3D12Core.dll"))) {
					rtFailed_ = true;
					Kino.Log.Warning($"[NatureFX] DXR 1.2 sun shadows: D3D12Core.dll (Agility SDK 1.619) not found in {sdk}");
					return;
				}
				int result = NFX_Native_StartRt(Path.GetFullPath(sdk) + Path.DirectorySeparatorChar);
				rtStarted_ = result == 1;
				rtFailed_ = !rtStarted_;
				Kino.Log.Info($"[NatureFX] DXR 1.2 sun shadows: {(rtStarted_ ? "started" : $"failed ({result})")}: {Dxr12SunStatus}");
			}
			if (rtStarted_ && Time.unscaledTime >= nextRtReport_) {
				nextRtReport_ = Time.unscaledTime + 30.0f;
				Kino.Log.Info($"[NatureFX] DXR 1.2 sun shadows: {Dxr12SunStatus}");
			}
			if (rtStarted_ && OmmPaused != ommOff_) {
				ommOff_ = OmmPaused;
				NFX_Native_SetRtOmm(ommOff_ ? 0 : 1);
			}
			bool active = Dxr12Sun && rtStarted_ && !Dxr12SunPaused;
			if (active != rtActive_) {
				rtActive_ = active;
				NFX_Native_SetRtActive(active ? 1 : 0);
			}
		}

		public static void RunDxr12Probe() {
			if (!Ready) {
				Dxr12Result = "the own engine is not loaded";
				return;
			}
			string sdk = Path.Combine(Kino.Paths.Mods, ShaderBundle.FolderName, "D3D12");
			if (!File.Exists(Path.Combine(sdk, "D3D12Core.dll"))) {
				Dxr12Result = $"D3D12Core.dll (Agility SDK 1.619) not found in {sdk}";
				return;
			}
			try {
				var buffer = new byte[4096];
				int result = NFX_Native_Dxr12Probe(Path.GetFullPath(sdk) + Path.DirectorySeparatorChar, buffer, buffer.Length);
				int length = Array.IndexOf(buffer, (byte)0);
				string report = Encoding.UTF8.GetString(buffer, 0, length < 0 ? buffer.Length : length);
				string firstFailure = null;
				foreach (string line in report.Split('\n')) {
					if (line.Length > 0) {
						Kino.Log.Info($"[NatureFX] DXR 1.2 probe: {line}");
						firstFailure ??= line.StartsWith("FAIL") ? line : null;
					}
				}
				Dxr12Result = result == 1 ? "all checks passed (SM 6.9, opacity micromaps, SER, shared with Unity's device)"
					: $"failed: {firstFailure ?? report}";
			} catch (Exception e) {
				Dxr12Result = $"error ({e.GetType().Name}: {e.Message})";
			}
			Kino.Log.Info($"[NatureFX] DXR 1.2 probe: {Dxr12Result}");
		}
		// The engine builds and traces its scene only while chosen (Settings.RTBackend 1): it is experimental,
		// and a bad mesh once hung the GPU, which takes the whole game down
		public static bool Enabled { get; set; }
		public static string Info { get; private set; } = "not loaded";
		public static string TestResult { get; private set; } = "not run";
		public static string SceneStatus { get; private set; } = "no scene";
		public static string CheckStatus { get; private set; } = "not run";
		public static string QueueStatus { get; private set; } = "not started";

		private static bool tried_;
		private static readonly Dictionary<(Mesh, int), int> geometries_ = new();
		private static readonly List<Instance> uploaded_ = new();

		// Once per session on DX12: load the DLL from the mod folder, find the device, run the self test
		public static void TryStart() {
			if (tried_ || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) {
				return;
			}
			tried_ = true;
			CheckMarker();
			string folder = Path.Combine(Kino.Paths.Mods, ShaderBundle.FolderName);
			if (!File.Exists(Path.Combine(folder, DllName + ".dll"))) {
				Info = "NFXNative.dll not found";
				Kino.Log.Info($"[NatureFX] Native engine: {Info} in {folder}");
				return;
			}
			try {
				// first through Unity's plugin loader (by path), so that it may hand over its D3D12 interface
				try {
					PluginInterfaceByPath();
				} catch (Exception e) {
					Kino.Log.Info($"[NatureFX] Native engine: not loadable through Unity's plugin loader ({e.GetType().Name})");
				}
				SetDllDirectoryW(folder);
				// any D3D12 resource of Unity's leads to its device when the plugin interface was not handed over
				int init = NFX_Native_Init(Texture2D.whiteTexture.GetNativeTexturePtr());
				var buffer = new byte[1024];
				int length = NFX_Native_Info(buffer, buffer.Length);
				Info = Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
				Kino.Log.Info($"[NatureFX] Native engine: device {(init == 1 ? "found" : "not found")}; {Info}");
				if (init != 1) {
					return;
				}
				int test = NFX_Native_RayQueryTest();
				TestResult = test switch {
					2 => "inline ray query works (hit)",
					1 => "inline ray query ran but missed",
					-2 => "device has no DXR 1.1",
					_ => $"failed at stage {-test}"
				};
				Kino.Log.Info($"[NatureFX] Native engine self test: {TestResult}");
				Ready = test == 2;
				if (Ready) {
					int capture = NFX_Native_CaptureQueue();
					QueueStatus = capture == 1 ? "waiting for Unity's queue" : $"capture failed ({capture})";
					captureStart_ = Time.frameCount;
				}
			} catch (Exception e) {
				Info = $"load failed ({e.GetType().Name}: {e.Message})";
				Kino.Log.Warning($"[NatureFX] Native engine: {Info}");
			} finally {
				SetDllDirectoryW(null);
			}
		}

		// ------------------------------------------------------------------ Unity's command queue

		private static int sizeWidth_, sizeHeight_;
		private static float sizeLogAt_;
		private static int captureStart_;
		private static int queueTestFrame_ = -1;
		private static bool queueReported_;
		private const int CaptureFrames = 600;

		// Per frame (main thread): follow the queue capture, then run the queue test once from the render thread
		public static void Update() {
			if (!Ready) {
				return;
			}
			// each pass that drops its history sets it again this frame; left standing, a true from the frame the
			// shadow pass was switched off made DLSS and the NRD reflections reset for ever
			NrdReset = false;
			// buffers replaced by a resize or a DLSS mode change: freed here (every frame, whether or not a pass runs:
			// with only DLSS on they were never freed) once the queued frames that used them are past
			ReleaseRetired(3);
			if (Time.unscaledTime >= nextMemoryLog_) {
				nextMemoryLog_ = Time.unscaledTime + 30.0f;
				Kino.Log.Info($"[NatureFX] GPU memory: {GpuMemory}");
			}
			// 2 s after the camera size settles: what the GPU holds and how much of it is the engine's trace buffers
			// (they follow the size; earlier ones are released 10 frames after being replaced)
			if (sizeLogAt_ > 0.0f && Time.unscaledTime >= sizeLogAt_) {
				sizeLogAt_ = 0.0f;
				long bytes = 0, retiredBytes = 0;
				// per kind too (MB, 1 decimal), to see which of them a change of layout shrank
				var parts = new System.Text.StringBuilder();
				for (int k = 0; k < buffers_.Length; ++k) {
					var b = buffers_[k];
					if (b.Surface == null) {
						continue;
					}
					long kb = (long)b.Surface.count * b.Surface.stride + (long)b.Result.count * b.Result.stride;
					bytes += kb;
					parts.Append($" {KindName(k)} {kb / 1048576.0:0.0}");
				}
				long screenBytes = screen_ != null ? (long)screen_.count * screen_.stride : 0;
				bytes += screenBytes;
				parts.Append($" screen {screenBytes / 1048576.0:0.0}");
				long guideBytes = 0;
				foreach (var g in new[] { nrdSunGuide_, nrdAmbientGuide_, nrdReflectionGuide_ }) {
					guideBytes += g.Buffer != null ? (long)g.Buffer.count * g.Buffer.stride : 0;
				}
				foreach (var r in retired_) {
					retiredBytes += (long)r.Buffer.count * r.Buffer.stride;
				}
				Kino.Log.Info($"[NatureFX] Camera {sizeWidth_}x{sizeHeight_}: GPU memory {GpuMemory}; trace buffers {bytes / 1048576} MB " +
				              $"(+ NRD guides {guideBytes / 1048576} MB), {retired_.Count} replaced ones still held ({retiredBytes / 1048576} MB); by kind (MB):{parts}");
			}
			try {
				UpdatePipeline();
			} catch (Exception e) {
				pipelineAvailable_ = false;
				Kino.Log.Warning($"[NatureFX] Native engine: DXR 1.0 pipelines unavailable ({e.GetType().Name}: {e.Message})");
			}
			try {
				UpdateWind();
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Native engine: wind not passed on ({e.GetType().Name}: {e.Message})");
				windFailed_ = true;
			}
			try {
				UpdateDxr12();
			} catch (Exception e) {
				rtFailed_ = true;
				Kino.Log.Warning($"[NatureFX] DXR 1.2 sun shadows: {e.GetType().Name}: {e.Message}");
			}
			try {
				UpdateMoving();
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Native engine: moving instances not updated ({e.GetType().Name}: {e.Message})");
				movingIndices_.Clear();
			}
			if (queueReported_) {
				return;
			}
			try {
				int status = NFX_Native_QueueStatus(0);
				if (status == 1) {
					if (Time.frameCount - captureStart_ > CaptureFrames) {
						NFX_Native_QueueStatus(1);
						QueueStatus = "Unity's queue not caught (hook removed)";
						Kino.Log.Info($"[NatureFX] Native engine: {QueueStatus}");
						queueReported_ = true;
					}
					return;
				}
				if (status != 2) {
					return;
				}
				if (queueTestFrame_ < 0) {
					if (SceneStatus == "no scene") {
						return; // the test traces the scene: wait for a map
					}
					QueueStatus = "Unity's queue caught; testing";
					var cmd = new CommandBuffer { name = "NatureFX native queue test" };
					cmd.IssuePluginEvent(NFX_Native_RenderEventFunc(), 1);
					Graphics.ExecuteCommandBuffer(cmd);
					cmd.Release();
					queueTestFrame_ = Time.frameCount;
					return;
				}
				int result = NFX_Native_QueueTestResult();
				if (result == 1 && Time.frameCount - queueTestFrame_ < 120) {
					return;
				}
				QueueStatus = result switch {
					2 => "Unity's queue caught; work submitted from the render thread ran",
					1 => "Unity's queue caught; submitted work did not finish within 120 frames",
					0 => "Unity's queue caught; the render event never ran",
					_ => $"Unity's queue caught; render thread test failed ({result})"
				};
				Kino.Log.Info($"[NatureFX] Native engine: {QueueStatus}");
				queueReported_ = true;
			} catch (Exception e) {
				QueueStatus = $"failed ({e.GetType().Name}: {e.Message})";
				Kino.Log.Warning($"[NatureFX] Native engine queue: {QueueStatus}");
				queueReported_ = true;
			}
		}

		// ------------------------------------------------------------------ traces
		// Each trace: Prepare (Unity compute, NFX_NativeBridge) fills a buffer the engine reads, render event 3 runs the
		// engine's shader on Unity's queue, Unpack (Unity compute) copies the result into the shadow pass's texture.

		private static string KindName(int kind) {
			switch (kind) {
				case TraceSun_: return "sun";
				case TraceAmbient_: return "ambient";
				case TraceLocal_: return "local";
				case TraceShafts_: return "shafts";
				case TraceSurface_: return "surface";
				case TraceReflection_: return "reflection";
				case TraceGI_: return "gi";
				case TraceGlass_: return "glass";
				case TraceFog_: return "fog";
				case TraceFogVolume_: return "fog-volume";
				case TraceLampMask_: return "lamp-mask";
				default: return $"kind{kind}";
			}
		}

		public const int TraceSun_ = 0, TraceAmbient_ = 1, TraceLocal_ = 2, TraceShafts_ = 3, TraceSurface_ = 4, TraceReflection_ = 5,
		                 TraceGI_ = 6, TraceCacheResolve_ = 7, TraceGlass_ = 8;
		// the radiance cache's entries (CacheEntries in NFXCommon.hlsli), resolved as a 1024 x 512 grid of threads
		private const int CacheWidth = 1024, CacheHeight = 512;

		private static readonly int SurfaceNormalId = Shader.PropertyToID("_NFX_SurfaceNormal");
		private static readonly int NativeSurfaceId = Shader.PropertyToID("_NFX_NativeSurface");
		private static readonly int NativeSunId = Shader.PropertyToID("_NFX_NativeSun");
		private static readonly int NativeResultId = Shader.PropertyToID("_NFX_NativeResult");
		private static readonly int NativeTargetId = Shader.PropertyToID("_NFX_NativeTarget");
		private static readonly int NativeDefaultId = Shader.PropertyToID("_NFX_NativeDefault");
		private static readonly int NativeFrameId = Shader.PropertyToID("_NFX_NativeFrame");
		private static readonly int ShaftParamsId = Shader.PropertyToID("_NFX_ShaftParams");
		private static readonly int SunOutputId = Shader.PropertyToID("_NFX_SunOutput");
		private static readonly int TargetSizeId = Shader.PropertyToID("_NFX_TargetSize");
		private static readonly int SunDirId = Shader.PropertyToID("_NFX_RTSunDir");
		private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");
		private static readonly int NormalBufferId = Shader.PropertyToID("_NormalBufferTexture");
		private static readonly int ExposureTextureId = Shader.PropertyToID("_ExposureTexture");

		// sun shadow rays from past this distance treat alpha tested surfaces as solid (Blocked / FirstBlocker coarse)
		private const float CoarseShadowDistance = 50.0f;

		// render event data (TraceEventData in NFXNative.cpp): 4 pointers, kind, width, height, Unity frame, 2100 floats
		private const int EventSlotSize = 8448;
		private const int ConstantsOffset = 48;
		// trace events whose data the render thread may still have to read: a frame issues up to about 20 (sky, clouds,
		// their shadow, the sky's light and cube, the 3D fog, sun, GI, lamps, lamp masks, surface, reflections, glass,
		// plus the ground and sun map bands) and 2 or 3 frames are queued; 64 could wrap onto a slot still unread
		private const int EventSlots = 256;
		private static IntPtr eventData_;
		private static int eventSlot_;
		private static readonly float[] constants_ = new float[2100];

		// per trace kind: the buffer Prepare fills and the result buffer
		private class TraceBuffers {
			public GraphicsBuffer Surface, Result;
			public IntPtr SurfacePtr, ResultPtr;
			public int Pixels;
		}
		private static readonly TraceBuffers[] buffers_ = { new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new(), new() };
		private static GraphicsBuffer screen_;
		private static IntPtr screenPtr_;
		private static int screenPixels_;
		// replaced buffers live on a few frames: the engine's work for earlier frames may still use them
		private static readonly List<(GraphicsBuffer Buffer, int Frame)> retired_ = new();

		// this frame's state, from the shadow pass (BeginFrame)
		private static Vector4 sunDirection_, sunColor_, ambientParams_;
		private static float sunRadius_;
		private static Vector3 camera_;
		private static int frame_;
		private static LocalLights lights_;
		private static GraphicsBuffer probe_;
		private static IntPtr probePtr_;
		private static int ambientPreparedFrame_ = -1;
		private static Matrix4x4 viewProj_;
		private static Vector4 screenSize_, reflParams_;
		private static int beganFrame_ = -1;

		// The own engine traces this frame: switched on, Unity's queue caught, scene built, bridge loaded
		public static bool Active => Enabled && Bridge != null && SunReady;

		// NFX_NativeBridge.compute, from the shader bundle
		public static ComputeShader Bridge;

		// Traces can run this frame: Unity's queue caught, scene built
		public static bool SunReady {
			get {
				try {
					return Ready && NFX_Native_SunReady() == 1;
				} catch (Exception) {
					return false;
				}
			}
		}

		// This frame's state; the shadow and reflection passes both call it (ambientParams only matter to the first)
		public static void BeginFrame(Camera cameraObject, Vector4 sunDirection, float sunRadius, Vector4 sunColor, int frame, Vector4 ambientParams,
		                              LocalLights lights, GraphicsBuffer probe) {
			var camera = cameraObject.transform.position;
			if (beganFrame_ == Time.frameCount) {
				if (ambientParams != Vector4.zero) {
					ambientParams_ = ambientParams;
				}
				if (sunRadius > 0.0f) {
					sunRadius_ = sunRadius; // the shadow pass's (Sun size): the atmosphere pass begins the frame with the disc's
				}
				return;
			}
			beganFrame_ = Time.frameCount;
			skyProbePtrFrame_ = IntPtr.Zero; // HDRP's probe until TraceSky makes this frame's own
			// camera relative world to clip, as HDRP's UNITY_MATRIX_VP (no translation in the view: HDRP renders
			// relative to the camera)
			var view = cameraObject.worldToCameraMatrix;
			view.m03 = view.m13 = view.m23 = 0.0f;
			viewProj_ = GL.GetGPUProjectionMatrix(cameraObject.projectionMatrix, true) * view;
			screenSize_ = new Vector4(cameraObject.pixelWidth, cameraObject.pixelHeight, 1.0f / Mathf.Max(1, cameraObject.pixelWidth), 1.0f / Mathf.Max(1, cameraObject.pixelHeight));
			if (cameraObject.pixelWidth != sizeWidth_ || cameraObject.pixelHeight != sizeHeight_) {
				sizeWidth_ = cameraObject.pixelWidth;
				sizeHeight_ = cameraObject.pixelHeight;
				sizeLogAt_ = Time.unscaledTime + 2.0f;
			}
			sunDirection_ = sunDirection;
			sunRadius_ = sunRadius;
			sunColor_ = sunColor;
			camera_ = camera;
			frame_ = frame;
			ambientParams_ = ambientParams;
			lights_ = lights;
			if (probe != probe_) {
				probe_ = probe;
				probePtr_ = probe != null ? probe.GetNativeBufferPtr() : IntPtr.Zero;
			}
			ReleaseRetired(3);
		}

		// Buffers a resize replaced, freed once the frames that may still use them are submitted (the engine's work
		// goes into Unity's queue with each frame's command buffers, 2 or 3 frames behind at most). A resize at 4K
		// retires about 2 GB at once: held for 10 frames it pushed the GPU over its memory budget
		private static void ReleaseRetired(int frames) {
			for (int i = retired_.Count - 1; i >= 0; --i) {
				if (Time.frameCount - retired_[i].Frame > frames) {
					retired_[i].Buffer.Release();
					retired_.RemoveAt(i);
				}
			}
		}

		private static TraceBuffers Buffers(int kind, int pixels, int surfaceFloat4s, int resultStride) {
			var b = buffers_[kind];
			if (b.Surface != null && b.Pixels == pixels) {
				return b;
			}
			if (b.Surface != null) {
				retired_.Add((b.Surface, Time.frameCount));
				retired_.Add((b.Result, Time.frameCount));
			}
			b.Pixels = pixels;
			b.Surface = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, pixels * surfaceFloat4s), 16) { name = $"NFX_NativeSurface{kind}" };
			b.Result = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, pixels), resultStride) { name = $"NFX_NativeResult{kind}" };
			b.SurfacePtr = b.Surface.GetNativeBufferPtr();
			b.ResultPtr = b.Result.GetNativeBufferPtr();
			return b;
		}

		private static float Bits(uint value) {
			return BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
		}

		// Render event 3 with this trace's data (the Frame constant buffer of NFXCommon.hlsli)
		private static void Issue(CommandBuffer cmd, int kind, IntPtr surface, IntPtr result, int width, int height, IntPtr screen = default, IntPtr probe = default) {
			var c = constants_;
			Array.Clear(c, 0, c.Length);
			c[0] = sunDirection_.x; c[1] = sunDirection_.y; c[2] = sunDirection_.z; c[3] = sunRadius_;
			c[4] = sunColor_.x; c[5] = sunColor_.y; c[6] = sunColor_.z; c[7] = CoarseShadowDistance;
			c[8] = camera_.x; c[9] = camera_.y; c[10] = camera_.z; c[11] = Bits((uint)frame_);
			c[12] = Bits((uint)width); c[13] = Bits((uint)height); c[14] = Bits(sunDirection_.w > 0.0f ? 1u : 0u);
			c[15] = Bits((GlassShadows ? 1u : 0u) | (LampMask ? 2u : 0u) | (OwnGlass ? 4u : 0u) | (fogListsIssued_ ? 16u : 0u) | ((uint)(Mathf.Clamp(SunSamples, 1, 8) - 1) << 8));
			c[16] = ambientParams_.x; c[17] = ambientParams_.y; c[18] = ambientParams_.z; c[19] = ambientParams_.w;
			Put(c, 20, skyParams_);
			c[25] = aerosols_; c[26] = 1.0f; // LightCount.y aerosols, .z ozone (Sky.hlsli)
			for (int i = 0; i < 16; ++i) {
				// column major (Matrix4x4's single index), as dxc packs a float4x4 constant without -Zpr: written by
				// rows it arrived transposed and ScreenVisibility (the camera colour in reflections and glass) never
				// found the hit on screen
				c[28 + i] = viewProj_[i];
			}
			Put(c, 44, screenSize_);
			Put(c, 48, reflParams_);
			if (kindParams_) {
				// the clouds' own values in slots their kind does not otherwise read (Clouds.hlsl)
				Put(c, 16, kindA_);
				Put(c, 48, kindB_);
			}
			// the local lights last: LightPos, LightColor, LightDir, LightSpot, LocalLights.Max each
			if (lights_ != null) {
				const int m = LocalLights.Max * 4;
				for (int i = 0; i < lights_.Count; ++i) {
					Put(c, 52 + i * 4, lights_.Positions[i]);
					Put(c, 52 + m + i * 4, lights_.Colors[i]);
					Put(c, 52 + 2 * m + i * 4, lights_.Directions[i]);
					Put(c, 52 + 3 * m + i * 4, lights_.Spots[i]);
				}
				c[24] = lights_.Count;
				c[27] = lights_.LampReference; // LightCount.w: the GI's clamp and cache scale (CacheReference)
			}
			if (eventData_ == IntPtr.Zero) {
				eventData_ = Marshal.AllocHGlobal(EventSlotSize * EventSlots);
			}
			var slot = eventData_ + EventSlotSize * (eventSlot_++ % EventSlots);
			Marshal.WriteIntPtr(slot, 0, surface);
			Marshal.WriteIntPtr(slot, 8, result);
			Marshal.WriteIntPtr(slot, 16, probe != IntPtr.Zero ? probe : skyProbePtrFrame_ != IntPtr.Zero ? skyProbePtrFrame_ : probePtr_);
			Marshal.WriteIntPtr(slot, 24, screen);
			Marshal.WriteInt32(slot, 32, kind);
			Marshal.WriteInt32(slot, 36, width);
			Marshal.WriteInt32(slot, 40, height);
			Marshal.WriteInt32(slot, 44, Time.frameCount); // the TLAS once per Unity frame, whichever pass traces first
			Marshal.Copy(c, 0, slot + ConstantsOffset, c.Length);
			cmd.IssuePluginEventAndData(NFX_Native_RenderEventAndDataFunc(), 3, slot);
		}

		private static void Put(float[] c, int at, Vector4 v) {
			c[at] = v.x; c[at + 1] = v.y; c[at + 2] = v.z; c[at + 3] = v.w;
		}

		private static void Unpack4(CommandBuffer cmd, ComputeShader bridge, TraceBuffers b, RTHandle target, Vector4 size) {
			int unpack = bridge.FindKernel("Unpack4");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeBufferParam(bridge, unpack, NativeResultId, b.Result);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTargetId, target);
			cmd.DispatchCompute(bridge, unpack, ((int)size.x + 7) / 8, ((int)size.y + 7) / 8, TextureXR.slices);
		}

		// ------------------------------------------------------------------ sky (Sky.hlsli)
		// The atmosphere's tables in buffers of their own: transmittance and multiple scattering when the air changes
		// (and after a shader reload), the sky view every frame. No rays: these kinds only use the trace plumbing.
		public const int TraceSkyTransmittance_ = 9, TraceSkyMultiScatter_ = 10, TraceSkyView_ = 11;
		public const int TransmittanceW = 256, TransmittanceH = 64, MultiScatterSize = 32, SkyViewW = 192, SkyViewH = 108;
		private static TraceBuffers skyTransmittance_, skyMultiScatter_, skyView_;
		// the transmittance table, then the clouds' noise and type map (Sky.hlsli: SkyDataBytes / 16)
		private const int SkyDataFloat4s = 155648;
		public const int TraceCloudNoise_ = 12, TraceClouds_ = 13, TraceCloudShadow_ = 14, TraceSkyAmbient_ = 15;
		public const int CloudShadowSize = 512;
		public const float CloudShadowArea = 16000.0f; // CloudShadow.hlsl
		// phase 1c: the sky's light (clouds included) as an ambient probe, in HDRP's packing (7 float4): every trace
		// after TraceSky in a frame reads it instead of HDRP's probe, and the composite puts it in place of HDRP's
		public static GraphicsBuffer SkyProbe { get; private set; }
		private static IntPtr skyProbePtr_, skyProbePtrFrame_;
		private static readonly int SkySamplesId = Shader.PropertyToID("_NFX_SkySamples");
		private static readonly int SkyProbeId = Shader.PropertyToID("_NFX_SkyProbe");
		private static bool cloudNoiseMade_;
		private static bool kindParams_;
		private static Vector4 kindA_, kindB_;
		private static Vector4 skyParams_;
		private static float aerosols_ = 1.0f;
		private static float skyTablesFor_ = float.NaN; // the aerosols the two slow tables were made for

		public static void InvalidateSky() {
			skyTablesFor_ = float.NaN;
			cloudNoiseMade_ = false;
		}

		private static TraceBuffers SkyBuffer(ref TraceBuffers b, int texels, string name) {
			if (b == null) {
				var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, texels, 16) { name = name };
				var ptr = buffer.GetNativeBufferPtr();
				b = new TraceBuffers { Surface = buffer, Result = buffer, SurfacePtr = ptr, ResultPtr = ptr, Pixels = texels };
			}
			return b;
		}

		// sunLux: the sun's illuminance above the air; height: the camera above the ground (m); aerosols: haze relative
		// to a clear day. view gets the sky view table every frame, transmittance its table when it is remade.
		// clouds: their target (null: none) and size, cover (x cumulus, y altostratus, z cumulonimbus, w density) and
		// motion (xy wind offset, z age, w the haze distance), CloudController.Native*
		public static void TraceSky(CommandBuffer cmd, ComputeShader bridge, RTHandle view, RTHandle transmittance, float sunLux, float height, float aerosols,
		                            RTHandle clouds, Vector4 cloudSize, Vector4 cover, Vector4 motion, RTHandle cloudShadow, bool ambient,
		                            RenderTexture skyCube = null) {
			var t = SkyBuffer(ref skyTransmittance_, SkyDataFloat4s, "NFX_SkyData");
			var m = SkyBuffer(ref skyMultiScatter_, MultiScatterSize * MultiScatterSize, "NFX_SkyMultiScatter");
			var v = SkyBuffer(ref skyView_, SkyViewW * SkyViewH, "NFX_SkyView");
			skyParams_ = new Vector4(sunLux, sunLux, sunLux, Mathf.Max(1.0f, height));
			aerosols_ = aerosols;
			if (float.IsNaN(skyTablesFor_) || Mathf.Abs(skyTablesFor_ - aerosols) > 0.02f) {
				skyTablesFor_ = aerosols;
				Issue(cmd, TraceSkyTransmittance_, m.ResultPtr, t.ResultPtr, TransmittanceW, TransmittanceH);
				Issue(cmd, TraceSkyMultiScatter_, v.ResultPtr, m.ResultPtr, MultiScatterSize, MultiScatterSize, t.ResultPtr);
			}
			// every frame (cheap): an unpacked table survives a lost render texture this way
			Unpack4(cmd, bridge, t, transmittance, new Vector4(TransmittanceW, TransmittanceH, 1.0f / TransmittanceW, 1.0f / TransmittanceH));
			Issue(cmd, TraceSkyView_, m.ResultPtr, v.ResultPtr, SkyViewW, SkyViewH, t.ResultPtr);
			Unpack4(cmd, bridge, v, view, new Vector4(SkyViewW, SkyViewH, 1.0f / SkyViewW, 1.0f / SkyViewH));
			// the clouds' noise once, whether or not the clouds are drawn: the 3D fog's boxes and the tyre smoke read
			// its Worley noise too (FogVolume.hlsl), and were empty in a session that never showed clouds
			if (!cloudNoiseMade_) {
				cloudNoiseMade_ = true;
				Issue(cmd, TraceCloudNoise_, m.ResultPtr, t.ResultPtr, 4096, 64);
			}
			if (clouds != null) {
				int width = (int)cloudSize.x, height2 = (int)cloudSize.y;
				var b = Buffers(TraceClouds_, width * height2, 1, 16);
				int prepare = bridge.FindKernel("PrepareClouds");
				cmd.SetComputeVectorParam(bridge, TargetSizeId, cloudSize);
				cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
				cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
				cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height2 + 7) / 8, 1);
				kindParams_ = true;
				kindA_ = cover;
				kindB_ = motion;
				Issue(cmd, TraceClouds_, b.SurfacePtr, b.ResultPtr, width, height2, t.ResultPtr, v.ResultPtr);
				kindParams_ = false;
				Unpack4(cmd, bridge, b, clouds, cloudSize);
				if (cloudShadow != null) {
					var sb = Buffers(TraceCloudShadow_, CloudShadowSize * CloudShadowSize, 1, 16);
					kindParams_ = true;
					Issue(cmd, TraceCloudShadow_, m.ResultPtr, sb.ResultPtr, CloudShadowSize, CloudShadowSize, t.ResultPtr);
					kindParams_ = false;
					Unpack4(cmd, bridge, sb, cloudShadow, new Vector4(CloudShadowSize, CloudShadowSize, 1.0f / CloudShadowSize, 1.0f / CloudShadowSize));
				}
			}
			if (ambient) {
				var a = Buffers(TraceSkyAmbient_, 512, 1, 16);
				kindParams_ = true;
				kindA_ = clouds != null ? cover : Vector4.zero;
				kindB_ = motion;
				Issue(cmd, TraceSkyAmbient_, m.ResultPtr, a.ResultPtr, 32, 16, t.ResultPtr, v.ResultPtr);
				kindParams_ = false;
				if (SkyProbe == null) {
					// 7 float4 of HDRP's packing, and [7] the sky's mean radiance (the fog's sky light)
					SkyProbe = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 8, 16) { name = "NFX_SkyProbe" };
					skyProbePtr_ = SkyProbe.GetNativeBufferPtr();
				}
				int project = bridge.FindKernel("SkyAmbientSH");
				cmd.SetComputeBufferParam(bridge, project, SkySamplesId, a.Result);
				cmd.SetComputeBufferParam(bridge, project, SkyProbeId, SkyProbe);
				cmd.DispatchCompute(bridge, project, 1, 1, 1);
				skyProbePtrFrame_ = skyProbePtr_;
			}
			if (skyCube != null) {
				TraceSkyCube(cmd, bridge, skyCube, clouds != null ? cover : Vector4.zero, motion); // with this frame's sky values
			}
			skyParams_ = Vector4.zero; // the other kinds see zeros, as before
		}

		// Sun shadows into the shadow pass's sun texture
		public static void TraceSun(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, RTHandle sunOutput, Vector4 size) {
			int width = (int)size.x, height = (int)size.y;
			var b = Buffers(TraceSun_, width * height, 1, 4); // packed: SunShadow.hlsl (origin and pixel; 2 halves)
			int prepare = bridge.FindKernel("Prepare");
			int unpack = bridge.FindKernel("Unpack");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeVectorParam(bridge, SunDirId, sunDirection_);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, NormalBufferId, ctx.cameraNormalBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, SurfaceNormalId, surfaceNormal);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSunId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			Issue(cmd, TraceSun_, b.SurfacePtr, b.ResultPtr, width, height);
			if (NrdSun) {
				IssueNrd(cmd, bridge, ctx, nrdSunGuide_, b.SurfacePtr, b.ResultPtr, width, height, size, 5, 0);
			}
			cmd.SetComputeBufferParam(bridge, unpack, NativeSunId, b.Result);
			cmd.SetComputeTextureParam(bridge, unpack, SunOutputId, sunOutput);
			cmd.DispatchCompute(bridge, unpack, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
		}

		// NRD over a trace just issued (NFXNative Nrd.inl): its guides at the trace's resolution (PrepareNrdGuide), then
		// render event 5 (SIGMA, the sun) or 6 (REBLUR, the ambient light or GI) with the camera's matrices (NrdEventData,
		// 328 bytes)
		private class NrdGuide {
			public GraphicsBuffer Buffer;
			public IntPtr Ptr;
			public int Pixels;
		}
		private static readonly NrdGuide nrdSunGuide_ = new(), nrdAmbientGuide_ = new(), nrdReflectionGuide_ = new();
		private static IntPtr nrdEvents_;
		private static int nrdEvent_;
		private const int NrdEventSize = 328;
		private const int NrdEvents = 32;
		private static readonly float[] nrdFloats_ = new float[NrdEventSize / 4];
		// this frame's camera and the one before (taken once a frame: both events of a frame see the same pair)
		private static Matrix4x4 nrdView_, nrdProj_, nrdViewPrev_, nrdProjPrev_;
		private static int nrdFrame_ = -10;
		private static readonly int NrdGuideId = Shader.PropertyToID("_NFX_NrdGuide");
		private static readonly int MotionVectorsId = Shader.PropertyToID("_NFX_MotionVectors");

		private static void IssueNrd(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, NrdGuide guide, IntPtr surface, IntPtr result,
		                             int width, int height, Vector4 size, int eventId, int mode) {
			int pixels = width * height;
			if (guide.Buffer == null || guide.Pixels != pixels) {
				if (guide.Buffer != null) {
					retired_.Add((guide.Buffer, Time.frameCount));
				}
				guide.Pixels = pixels;
				guide.Buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, pixels), 16) { name = "NFX_NrdGuide" }; // a packed uint4 per pixel
				guide.Ptr = guide.Buffer.GetNativeBufferPtr();
			}
			int kernel = bridge.FindKernel("PrepareNrdGuide");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeTextureParam(bridge, kernel, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, kernel, NormalBufferId, ctx.cameraNormalBuffer);
			cmd.SetComputeTextureParam(bridge, kernel, MotionVectorsId, ctx.cameraMotionVectorsBuffer);
			cmd.SetComputeBufferParam(bridge, kernel, NrdGuideId, guide.Buffer);
			cmd.DispatchCompute(bridge, kernel, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);

			if (nrdFrame_ != Time.frameCount) {
				var cam = ctx.hdCamera.camera;
				bool continuous = nrdFrame_ == Time.frameCount - 1;
				nrdViewPrev_ = continuous ? nrdView_ : cam.worldToCameraMatrix;
				nrdProjPrev_ = continuous ? nrdProj_ : cam.nonJitteredProjectionMatrix;
				nrdView_ = cam.worldToCameraMatrix;
				nrdProj_ = cam.nonJitteredProjectionMatrix;
				nrdFrame_ = Time.frameCount;
			}
			var f = nrdFloats_;
			Array.Clear(f, 0, f.Length);
			// floats from byte 40 (after 3 pointers and 4 uints): tan radius, sun direction, 4 matrices, range, time
			int o = 10;
			f[o] = Mathf.Tan(sunRadius_);
			f[o + 1] = sunDirection_.x; f[o + 2] = sunDirection_.y; f[o + 3] = sunDirection_.z;
			for (int i = 0; i < 16; ++i) {
				f[o + 4 + i] = nrdProj_[i];          // Matrix4x4[i]: column major, as NRD reads them
				f[o + 20 + i] = nrdProjPrev_[i];
				f[o + 36 + i] = nrdView_[i];
				f[o + 52 + i] = nrdViewPrev_[i];
			}
			f[o + 68] = 5000.0f;                       // m: the sky (view depth 1e6 in the guides) is outside it
			f[o + 69] = Time.unscaledDeltaTime * 1000.0f;
			if (nrdEvents_ == IntPtr.Zero) {
				nrdEvents_ = Marshal.AllocHGlobal(NrdEventSize * NrdEvents);
			}
			var slot = nrdEvents_ + NrdEventSize * (nrdEvent_++ % NrdEvents);
			Marshal.Copy(f, 0, slot, f.Length);
			Marshal.WriteIntPtr(slot, 0, guide.Ptr);
			Marshal.WriteIntPtr(slot, 8, surface);
			Marshal.WriteIntPtr(slot, 16, result);
			Marshal.WriteInt32(slot, 24, width);
			Marshal.WriteInt32(slot, 28, height);
			Marshal.WriteInt32(slot, 32, frame_);
			Marshal.WriteInt32(slot, 36, NrdReset ? 1 : 0);
			Marshal.WriteInt32(slot, 320, mode);
			cmd.IssuePluginEventAndData(NFX_Native_RenderEventAndDataFunc(), eventId, slot);
		}

		// ------------------------------------------------------------------ DLSS (NFXNative Dlss.inl)
		// The colour (HDR, pre-exposed) anti-aliased by DLSS, at the render resolution (DLAA: output is color) or upscaled
		// to the output's: PrepareDlss packs it with the motion and the depth, render event 9 runs DLSS on Unity's queue,
		// UnpackDlss writes the result into output. jitter: the projection's offset this frame in render pixels; reset:
		// the history dropped; motion: Dlaa.DevMotion; current / previous: camera relative world to clip without the
		// jitter; quality: NVSDK_NGX_PerfQuality_Value (DLAA 5).
		private static GraphicsBuffer dlssBuffer_, dlssOutput_;
		private static IntPtr dlssBufferPtr_, dlssOutputPtr_, dlssEvents_;
		private static int dlssPixels_, dlssOutputPixels_, dlssEvent_;
		private const int DlssEventSize = 192, DlssEvents = 8;
		private static GraphicsBuffer dlssGuides_;
		private static IntPtr dlssGuidesPtr_;
		private static int dlssGuidePixels_;
		private static readonly float[] dlssMatrices_ = new float[32];
		private static readonly int DlssGuidesId = Shader.PropertyToID("_NFX_DlssGuides");
		private static readonly int DlssGuideDiffuseId = Shader.PropertyToID("_NFX_DlssGuideDiffuse");
		private static readonly int DlssGuideSpecularId = Shader.PropertyToID("_NFX_DlssGuideSpecular");
		private static readonly int DlssRayReconstructionId = Shader.PropertyToID("_NFX_DlssRR");
		private static readonly int DlssColorId = Shader.PropertyToID("_NFX_DlssColor");
		private static readonly int DlssBufferId = Shader.PropertyToID("_NFX_DlssBuffer");
		private static readonly int DlssModeId = Shader.PropertyToID("_NFX_DlssMode");
		private static readonly int DlssCurrentId = Shader.PropertyToID("_NFX_DlssCurrentVP");
		private static readonly int DlssPreviousId = Shader.PropertyToID("_NFX_DlssPreviousVP");

		private static void DlssBuffer(ref GraphicsBuffer buffer, ref IntPtr ptr, ref int pixels, int wanted, string name) {
			if (buffer != null && pixels == wanted) {
				return;
			}
			if (buffer != null) {
				retired_.Add((buffer, Time.frameCount));
			}
			pixels = wanted;
			buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, wanted), 16) { name = name };
			ptr = buffer.GetNativeBufferPtr();
		}

		public static bool Dlss(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle color, RTHandle output, int width, int height,
		                        int outWidth, int outHeight, Vector2 jitter, bool reset, int motion, Matrix4x4 current, Matrix4x4 previous, int quality,
		                        RTHandle guideDiffuse = null, RTHandle guideSpecular = null, Matrix4x4 view = default, Matrix4x4 projection = default) {
			if (bridge == null || !Ready || !Supports("dlss")) {
				return false;
			}
			DlssBuffer(ref dlssBuffer_, ref dlssBufferPtr_, ref dlssPixels_, width * height, "NFX_DlssBuffer");
			bool upscaling = outWidth != width || outHeight != height;
			if (upscaling) {
				DlssBuffer(ref dlssOutput_, ref dlssOutputPtr_, ref dlssOutputPixels_, outWidth * outHeight, "NFX_DlssOutput");
			}
			var outBuffer = upscaling ? dlssOutput_ : dlssBuffer_;
			var outPtr = upscaling ? dlssOutputPtr_ : dlssBufferPtr_;

			int prepare = bridge.FindKernel("PrepareDlss");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, new Vector4(width, height, 1.0f / width, 1.0f / height));
			cmd.SetComputeFloatParam(bridge, DlssModeId, motion);
			cmd.SetComputeMatrixParam(bridge, DlssCurrentId, current);
			cmd.SetComputeMatrixParam(bridge, DlssPreviousId, previous);
			cmd.SetComputeTextureParam(bridge, prepare, DlssColorId, color);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, MotionVectorsId, ctx.cameraMotionVectorsBuffer);
			cmd.SetComputeBufferParam(bridge, prepare, DlssBufferId, dlssBuffer_);
			// Ray Reconstruction: the albedos, the normals and the roughness too (a second uint4 per pixel)
			bool rr = guideDiffuse != null && guideSpecular != null;
			// made once at the render size and kept (switching it with RR every frame remade 16 MB a frame)
			if (rr || dlssGuides_ == null) {
				DlssBuffer(ref dlssGuides_, ref dlssGuidesPtr_, ref dlssGuidePixels_, rr ? width * height : 1, "NFX_DlssGuides");
			}
			cmd.SetComputeFloatParam(bridge, DlssRayReconstructionId, rr ? 1.0f : 0.0f);
			// unused without RR, but bound as what the kernel declares (TEXTURE2D_X: a texture array here): a plain 2D
			// texture made Unity skip the whole dispatch, and DLSS upscaled whatever the buffer held
			cmd.SetComputeTextureParam(bridge, prepare, DlssGuideDiffuseId, rr ? guideDiffuse : TextureXR.GetBlackTexture());
			cmd.SetComputeTextureParam(bridge, prepare, DlssGuideSpecularId, rr ? guideSpecular : TextureXR.GetBlackTexture());
			cmd.SetComputeTextureParam(bridge, prepare, NormalBufferId, ctx.cameraNormalBuffer);
			cmd.SetComputeBufferParam(bridge, prepare, DlssGuidesId, dlssGuides_);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);

			if (dlssEvents_ == IntPtr.Zero) {
				dlssEvents_ = Marshal.AllocHGlobal(DlssEventSize * DlssEvents);
			}
			var slot = dlssEvents_ + DlssEventSize * (dlssEvent_++ % DlssEvents);
			Marshal.WriteIntPtr(slot, 0, dlssBufferPtr_);
			Marshal.WriteIntPtr(slot, 8, outPtr);
			Marshal.WriteInt32(slot, 16, width);
			Marshal.WriteInt32(slot, 20, height);
			Marshal.WriteInt32(slot, 24, outWidth);
			Marshal.WriteInt32(slot, 28, outHeight);
			Marshal.Copy(new[] { jitter.x, jitter.y }, 0, slot + 32, 2);
			Marshal.WriteInt32(slot, 40, reset ? 1 : 0);
			Marshal.Copy(new[] { Time.unscaledDeltaTime * 1000.0f }, 0, slot + 44, 1);
			Marshal.WriteInt32(slot, 48, quality);
			Marshal.WriteInt32(slot, 52, rr ? 1 : 0);
			Marshal.WriteIntPtr(slot, 56, dlssGuidesPtr_);
			// Ray Reconstruction's camera without the jitter: world to view (absolute) and view to clip (as rendered
			// into a texture, reversed depth), Matrix4x4's column major order
			var clip = rr ? GL.GetGPUProjectionMatrix(projection, true) : Matrix4x4.zero;
			for (int i = 0; i < 16; ++i) {
				dlssMatrices_[i] = view[i];
				dlssMatrices_[16 + i] = clip[i];
			}
			Marshal.Copy(dlssMatrices_, 0, slot + 64, 32);
			cmd.IssuePluginEventAndData(NFX_Native_RenderEventAndDataFunc(), 9, slot);

			int unpack = bridge.FindKernel("UnpackDlss");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, new Vector4(outWidth, outHeight, 1.0f / outWidth, 1.0f / outHeight));
			cmd.SetComputeBufferParam(bridge, unpack, DlssBufferId, outBuffer);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTargetId, output);
			cmd.DispatchCompute(bridge, unpack, (outWidth + 7) / 8, (outHeight + 7) / 8, TextureXR.slices);
			return true;
		}

		// Motion blur's motion (NFX_NativeBridge PrepareMotion): the DLSS buffer's when DLSS ran this frame (without the
		// jitter), else HDRP's motion vectors; uv, previous - current, into target at the render resolution
		private static readonly int PostMotionOutId = Shader.PropertyToID("_NFX_PostMotionOut");
		private static readonly int MotionFromDlssId = Shader.PropertyToID("_NFX_MotionFromDlss");

		public static void PrepareMotion(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle target, int width, int height, bool fromDlss) {
			if (bridge == null) {
				return;
			}
			bool dlss = fromDlss && dlssBuffer_ != null && dlssPixels_ == width * height;
			int kernel = bridge.FindKernel("PrepareMotion");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, new Vector4(width, height, 1.0f / width, 1.0f / height));
			cmd.SetComputeFloatParam(bridge, MotionFromDlssId, dlss ? 1.0f : 0.0f);
			cmd.SetComputeTextureParam(bridge, kernel, MotionVectorsId, ctx.cameraMotionVectorsBuffer);
			if (dlss) {
				cmd.SetComputeBufferParam(bridge, kernel, DlssBufferId, dlssBuffer_);
			} else {
				// bound all the same (the kernel declares it)
				DlssBuffer(ref dlssBuffer_, ref dlssBufferPtr_, ref dlssPixels_, Math.Max(dlssPixels_, 1), "NFX_DlssBuffer");
				cmd.SetComputeBufferParam(bridge, kernel, DlssBufferId, dlssBuffer_);
			}
			cmd.SetComputeTextureParam(bridge, kernel, PostMotionOutId, target);
			cmd.DispatchCompute(bridge, kernel, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
		}

		// The surfaces the ambient light and the local light shadows trace from (they share the resolution); the
		// result buffer of the kind given is reset to its default on the way
		private static TraceBuffers PrepareAmbient(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, Vector4 size,
		                                           TraceBuffers result, Vector4 resultDefault) {
			int width = (int)size.x, height = (int)size.y;
			var surface = Buffers(TraceAmbient_, width * height, 3, 16);
			int prepare = bridge.FindKernel("PrepareAmbient");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeVectorParam(bridge, NativeDefaultId, resultDefault);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, NormalBufferId, ctx.cameraNormalBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, SurfaceNormalId, surfaceNormal);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, surface.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, result.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			ambientPreparedFrame_ = Time.frameCount;
			return surface;
		}

		// Ambient light (bounce light or AO, far sky, contact shadows) into the shadow pass's ambient texture
		// Global illumination with every bounce (GI.hlsl and its radiance cache) into the shadow pass's ambient texture,
		// in place of TraceAmbient: same surfaces, same result (ambient light relative to HDRP's)
		public static void TraceGI(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, RTHandle target, Vector4 size) {
			int width = (int)size.x, height = (int)size.y;
			var surface = PrepareAmbient(cmd, bridge, ctx, surfaceNormal, size, Buffers(TraceAmbient_, width * height, 3, 16), Vector4.one);
			Issue(cmd, TraceGI_, surface.SurfacePtr, surface.ResultPtr, width, height);
			// this frame's samples into the cache (the buffers only fill the trace's slots: the resolve uses the cache)
			Issue(cmd, TraceCacheResolve_, surface.SurfacePtr, surface.ResultPtr, CacheWidth, CacheHeight);
			if (NrdAmbient) {
				IssueNrd(cmd, bridge, ctx, nrdAmbientGuide_, surface.SurfacePtr, surface.ResultPtr, width, height, size, 6, 1);
			}
			Unpack4(cmd, bridge, surface, target, size);
		}

		public static void TraceAmbient(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, RTHandle target, Vector4 size) {
			int width = (int)size.x, height = (int)size.y;
			var surface = PrepareAmbient(cmd, bridge, ctx, surfaceNormal, size, Buffers(TraceAmbient_, width * height, 3, 16), Vector4.one);
			Issue(cmd, TraceAmbient_, surface.SurfacePtr, surface.ResultPtr, width, height);
			if (NrdAmbient) {
				IssueNrd(cmd, bridge, ctx, nrdAmbientGuide_, surface.SurfacePtr, surface.ResultPtr, width, height, size, 6, 0);
			}
			Unpack4(cmd, bridge, surface, target, size);
		}

		// Blocked local light into the shadow pass's local light texture (same resolution as the ambient light)
		public static void TraceLocal(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, RTHandle target, Vector4 size) {
			int width = (int)size.x, height = (int)size.y;
			var result = Buffers(TraceLocal_, width * height, 1, 16);
			TraceBuffers surface;
			var ambient = buffers_[TraceAmbient_];
			if (ambientPreparedFrame_ == Time.frameCount && ambient.Surface != null && ambient.Pixels == width * height) {
				surface = ambient;
				int clear = bridge.FindKernel("Clear4");
				cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
				cmd.SetComputeVectorParam(bridge, NativeDefaultId, Vector4.zero);
				cmd.SetComputeBufferParam(bridge, clear, NativeResultId, result.Result);
				cmd.DispatchCompute(bridge, clear, (width + 7) / 8, (height + 7) / 8, 1);
			} else {
				surface = PrepareAmbient(cmd, bridge, ctx, surfaceNormal, size, result, Vector4.zero);
			}
			Issue(cmd, TraceLocal_, surface.SurfacePtr, result.ResultPtr, width, height);
			if (NrdLamps) {
				IssueNrd(cmd, bridge, ctx, nrdAmbientGuide_, surface.SurfacePtr, result.ResultPtr, width, height, size, 6, 2);
			}
			Unpack4(cmd, bridge, result, target, size);
		}

		// Roadmap A4: each of the four heaviest lamps' own visibility per half resolution pixel (LampMask.hlsl), into
		// target (R32G32B32A32 float: the packed small integers must stay exact), on the surface TraceLocal prepared
		// this frame (else its own)
		public const int TraceLampMask_ = 19;

		// The sun's ray traced shadow map for transparent surfaces (SunShadowMap, NFXNative SunMap.hlsl): rows row ..
		// row + rows of a cells x cells map into target, centred on origin (xyz, w the cell size), across dir (towards
		// the sun as the map is traced, whatever the sun does meanwhile), the rays starting start m up it
		public const int TraceSunMap_ = 20;

		// The ground for the ground map (GroundMap, NFXNative GroundMap.hlsl): terrain, and wide flat meshes that do not
		// move (roads, verges, the ground's own meshes; a rock, a wall or a post is neither wide nor flat)
		private static bool Ground(Instance instance) {
			// leaves and grass cards are never the ground, however wide a combined mesh of them is (a map's combined
			// grass cards, flat and many metres across, would put the ground at their tips)
			if (instance.Glass || instance.Follow != null || instance.Mesh == null || instance.AlphaTest || instance.Wind) {
				return false;
			}
			if (instance.Kind == "terrain") {
				return true;
			}
			var b = instance.SubMesh < instance.Mesh.subMeshCount ? instance.Mesh.GetSubMesh(instance.SubMesh).bounds : instance.Mesh.bounds;
			var m = instance.Matrix;
			// the world box's size: each axis the sum of the box's extents along it
			var e = b.extents;
			float sx = Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z;
			float sy = Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z;
			float sz = Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z;
			float wide = Mathf.Max(sx, sz) * 2.0f;
			return wide >= 6.0f && sy * 2.0f <= wide * 0.35f;
		}

		// the road among the ground: by its material's or mesh's name (Kino's road_ tag, asphalt)
		private static bool Road(Instance instance) {
			string name = ((instance.Material != null ? instance.Material.name : "") + " " + (instance.Mesh != null ? instance.Mesh.name : "")).ToLowerInvariant();
			return name.Contains("road") || name.Contains("asphalt") || name.Contains("tarmac");
		}

		// The ground map (GroundMap): rows row .. row + rows of a cells x cells map into target, centred on origin (x, z;
		// y the camera's height; w the cell size), the rays starting start m above it
		public const int TraceGroundMap_ = 21;

		public static void TraceGroundMap(CommandBuffer cmd, GraphicsBuffer target, Vector4 origin, int row, int rows, int cells, float start) {
			var ptr = target.GetNativeBufferPtr();
			kindParams_ = true;
			kindA_ = origin;
			kindB_ = new Vector4(row, start, cells, 0.0f);
			Issue(cmd, TraceGroundMap_, ptr, ptr, cells, rows);
			kindParams_ = false;
		}

		public static void TraceSunMap(CommandBuffer cmd, GraphicsBuffer target, Vector4 origin, Vector4 dir, int row, int rows, int cells, float start) {
			var ptr = target.GetNativeBufferPtr();
			var sun = sunDirection_;
			sunDirection_ = new Vector4(dir.x, dir.y, dir.z, 1.0f);
			kindParams_ = true;
			kindA_ = origin;
			kindB_ = new Vector4(row, start, cells, 0.0f);
			Issue(cmd, TraceSunMap_, ptr, ptr, cells, rows);
			kindParams_ = false;
			sunDirection_ = sun;
		}

		public static void TraceLampMask(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle surfaceNormal, RTHandle target, Vector4 size) {
			int width = (int)size.x, height = (int)size.y;
			var result = Buffers(TraceLampMask_, width * height, 1, 16);
			var ambient = buffers_[TraceAmbient_];
			TraceBuffers surface = ambientPreparedFrame_ == Time.frameCount && ambient.Surface != null && ambient.Pixels == width * height
				? ambient
				: PrepareAmbient(cmd, bridge, ctx, surfaceNormal, size, result, Vector4.zero);
			Issue(cmd, TraceLampMask_, surface.SurfacePtr, result.ResultPtr, width, height);
			Unpack4(cmd, bridge, result, target, size);
		}

		private static readonly int NativeTarget2Id = Shader.PropertyToID("_NFX_NativeTarget2");
		private static readonly int NativeScreenId = Shader.PropertyToID("_NFX_NativeScreen");
		private static readonly int ColorTexId = Shader.PropertyToID("_NFX_ColorTex");
		private static readonly int SurfaceAlbedoId = Shader.PropertyToID("_NFX_SurfaceAlbedo");
		private static readonly int ReflPrepareId = Shader.PropertyToID("_NFX_ReflPrepare");

		// The camera's surfaces at full resolution into RTSurfaceTrace's textures
		public static void TraceSurface(CommandBuffer cmd, CustomPassContext ctx, RTHandle albedo, RTHandle normal) {
			var bridge = Bridge;
			int width = ctx.hdCamera.actualWidth, height = ctx.hdCamera.actualHeight;
			var size = new Vector4(width, height, 1.0f / width, 1.0f / height);
			var b = Buffers(TraceSurface_, width * height, 1, 16); // packed: Surface.hlsl
			int prepare = bridge.FindKernel("PrepareSurface");
			int unpack = bridge.FindKernel("UnpackSurface");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			Issue(cmd, TraceSurface_, b.SurfacePtr, b.ResultPtr, width, height);
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeBufferParam(bridge, unpack, NativeResultId, b.Result);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTargetId, albedo);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTarget2Id, normal);
			cmd.DispatchCompute(bridge, unpack, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
		}

		// Reflections into the reflection pass's trace and distance textures.
		// reflParams: max distance, roughness cutoff, 1 = second bounce. color: the camera colour copy.
		public static void TraceReflection(CommandBuffer cmd, CustomPassContext ctx, RTHandle color, RTHandle exposure, RTHandle surfaceAlbedo,
		                                   RTHandle surfaceNormal, RTHandle trace, RTHandle distance, Vector4 size, Vector4 reflParams) {
			var bridge = Bridge;
			int width = (int)size.x, height = (int)size.y;
			reflParams_ = reflParams;
			PrepareScreen(cmd, bridge, ctx, color);

			var b = Buffers(TraceReflection_, width * height, 2, 16); // packed: NfxReflSurface / NfxPackReflResult
			int prepare = bridge.FindKernel("PrepareReflection");
			int unpack = bridge.FindKernel("UnpackReflection");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeVectorParam(bridge, ReflPrepareId, new Vector4(reflParams.y, 0.0f, 0.0f, 0.0f));
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, NormalBufferId, ctx.cameraNormalBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, SurfaceAlbedoId, surfaceAlbedo);
			cmd.SetComputeTextureParam(bridge, prepare, SurfaceNormalId, surfaceNormal);
			cmd.SetComputeTextureParam(bridge, prepare, ExposureTextureId, exposure);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			Issue(cmd, TraceReflection_, b.SurfacePtr, b.ResultPtr, width, height, screenPtr_);
			if (NrdReflections) {
				IssueNrd(cmd, bridge, ctx, nrdReflectionGuide_, b.SurfacePtr, b.ResultPtr, width, height, size, 7, 0);
			}
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeBufferParam(bridge, unpack, NativeResultId, b.Result);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTargetId, trace);
			cmd.SetComputeTextureParam(bridge, unpack, NativeTarget2Id, distance);
			cmd.DispatchCompute(bridge, unpack, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
		}

		// The camera colour and depth for the traces to read where a hit is on screen (ScreenVisibility)
		private static void PrepareScreen(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle color) {
			int screenPixels = ctx.hdCamera.actualWidth * ctx.hdCamera.actualHeight;
			if (screen_ == null || screenPixels_ != screenPixels) {
				if (screen_ != null) {
					retired_.Add((screen_, Time.frameCount));
				}
				screenPixels_ = screenPixels;
				screen_ = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Math.Max(1, screenPixels), 8) { name = "NFX_NativeScreen" }; // 4 halves (PrepareScreen)
				screenPtr_ = screen_.GetNativeBufferPtr();
			}
			// ScreenSize is this buffer's size: the render resolution, not the camera's pixel size. With DLSS upscaling
			// they differ (3840x2054 shown, 1280x685 rendered) and the traces read the buffer with the larger stride,
			// far past its end through a root SRV (no bounds check): the GPU faulted (device removed) the moment an
			// upscaling mode was chosen
			screenSize_ = new Vector4(ctx.hdCamera.actualWidth, ctx.hdCamera.actualHeight, 1.0f / Mathf.Max(1, ctx.hdCamera.actualWidth), 1.0f / Mathf.Max(1, ctx.hdCamera.actualHeight));
			int screenKernel = bridge.FindKernel("PrepareScreen");
			cmd.SetComputeTextureParam(bridge, screenKernel, ColorTexId, color);
			cmd.SetComputeTextureParam(bridge, screenKernel, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeBufferParam(bridge, screenKernel, NativeScreenId, screen_);
			cmd.DispatchCompute(bridge, screenKernel, (ctx.hdCamera.actualWidth + 7) / 8, (ctx.hdCamera.actualHeight + 7) / 8, TextureXR.slices);
		}

		// Glass reflections (Glass.hlsl) at full resolution into target: rgb the change to the camera colour, a the
		// glass's Fresnel; color is the camera colour with the transparent surfaces drawn. reflParams as TraceReflection.
		public static void TraceGlass(CommandBuffer cmd, CustomPassContext ctx, RTHandle color, RTHandle exposure, RTHandle target, Vector4 size,
		                              Vector4 reflParams) {
			var bridge = Bridge;
			int width = (int)size.x, height = (int)size.y;
			reflParams_ = reflParams;
			PrepareScreen(cmd, bridge, ctx, color);
			var b = Buffers(TraceGlass_, width * height, 1, 16); // packed: Glass.hlsl (one uint4 per pixel)
			int prepare = bridge.FindKernel("PrepareGlass");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, ExposureTextureId, exposure);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			Issue(cmd, TraceGlass_, b.SurfacePtr, b.ResultPtr, width, height, screenPtr_);
			Unpack4(cmd, bridge, b, target, size);
		}

		// Light shafts into the shadow pass's shaft texture; shaftParams: scattering, anisotropy, max distance
		public static void TraceShafts(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle target, Vector4 size, Vector4 shaftParams,
		                               RTHandle exposure) {
			int width = (int)size.x, height = (int)size.y;
			var b = Buffers(TraceShafts_, width * height, 2, 16);
			int prepare = bridge.FindKernel("PrepareShafts");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeVectorParam(bridge, SunDirId, sunDirection_);
			cmd.SetComputeVectorParam(bridge, ShaftParamsId, shaftParams);
			cmd.SetComputeVectorParam(bridge, NativeFrameId, new Vector4(frame_, 0.0f, 0.0f, 0.0f));
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeTextureParam(bridge, prepare, ExposureTextureId, exposure);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			Issue(cmd, TraceShafts_, b.SurfacePtr, b.ResultPtr, width, height);
			Unpack4(cmd, bridge, b, target, size);
		}

		// Phase 2: the fog and haze (Fog.hlsl) into the shafts' texture; shaftParams.z how far over the sky, fog as
		// MapFog.NativeParams (w the anisotropy)
		public const int TraceFog_ = 16;
		public static void TraceFog(CommandBuffer cmd, ComputeShader bridge, CustomPassContext ctx, RTHandle target, Vector4 size, Vector4 shaftParams, Vector4 fog) {
			int width = (int)size.x, height = (int)size.y;
			var b = Buffers(TraceFog_, width * height, 1, 16);
			int prepare = bridge.FindKernel("PrepareFog");
			cmd.SetComputeVectorParam(bridge, TargetSizeId, size);
			cmd.SetComputeVectorParam(bridge, ShaftParamsId, shaftParams);
			cmd.SetComputeTextureParam(bridge, prepare, CameraDepthId, ctx.cameraDepthBuffer);
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			cmd.SetComputeBufferParam(bridge, prepare, NativeResultId, b.Result);
			cmd.DispatchCompute(bridge, prepare, (width + 7) / 8, (height + 7) / 8, TextureXR.slices);
			kindParams_ = true;
			kindA_ = fog;
			kindB_ = reflParams_;
			Issue(cmd, TraceFog_, b.SurfacePtr, b.ResultPtr, width, height);
			kindParams_ = false;
			Unpack4(cmd, bridge, b, target, size);
		}

		// Phase 2b: the 3D fog (FogVolume.hlsl), FogW x FogH x FogD froxels: the engine's scattering per cell, blended
		// with last frame's (FogReproject: history ping-pong), integrated along each column (FogIntegrate) into
		// integrated, which RTShadowPass makes global (_NFX_FogVolume, NFX_FogCommon.hlsl)
		public const int TraceFogVolume_ = 17;
		// A3: the engine's sky as HDRP's sky (SkyCube.hlsl): 64² a face, into a cube render texture
		public const int TraceSkyCube_ = 18, SkyCubeSize = 64;
		private static readonly int SkyCubeTargetId = Shader.PropertyToID("_NFX_SkyCubeTarget");
		private static RenderTexture skyFaces_;

		private static void TraceSkyCube(CommandBuffer cmd, ComputeShader bridge, RenderTexture cube, Vector4 cover, Vector4 motion) {
			if (skyTransmittance_ == null || skyView_ == null) {
				return; // TraceSky first
			}
			var b = Buffers(TraceSkyCube_, SkyCubeSize * SkyCubeSize * 6, 1, 16);
			kindParams_ = true;
			kindA_ = cover;
			kindB_ = motion;
			Issue(cmd, TraceSkyCube_, skyMultiScatter_.ResultPtr, b.ResultPtr, SkyCubeSize, SkyCubeSize * 6, skyTransmittance_.ResultPtr, skyView_.ResultPtr);
			kindParams_ = false;
			// written as a 2D array of six slices (a compute shader cannot be relied on to write a cube map), then copied
			// face by face into the cube HDRP takes: the black sky in the reflections was a cube never written
			if (skyFaces_ == null) {
				skyFaces_ = new RenderTexture(SkyCubeSize, SkyCubeSize, 0, cube.graphicsFormat) {
					dimension = TextureDimension.Tex2DArray, volumeDepth = 6, enableRandomWrite = true, name = "NFX_SkyCubeFaces", hideFlags = HideFlags.DontSave
				};
				skyFaces_.Create();
			}
			int unpack = bridge.FindKernel("SkyCubeUnpack");
			cmd.SetComputeBufferParam(bridge, unpack, NativeResultId, b.Result);
			cmd.SetComputeTextureParam(bridge, unpack, SkyCubeTargetId, skyFaces_);
			cmd.DispatchCompute(bridge, unpack, (SkyCubeSize + 7) / 8, (SkyCubeSize * 6 + 7) / 8, 1);
			for (int face = 0; face < 6; ++face) {
				cmd.CopyTexture(skyFaces_, face, 0, cube, face, 0);
			}
		}
		public const int FogW = 160, FogH = 90, FogD = 64;
		private static readonly int FogTargetId = Shader.PropertyToID("_NFX_FogTarget");
		private static readonly int FogSourceId = Shader.PropertyToID("_NFX_FogSource");
		private static readonly int FogCamDeltaId = Shader.PropertyToID("_NFX_FogCamDelta");
		private static readonly int FogLampPosId = Shader.PropertyToID("_NFX_FogLampPos");
		private static readonly int FogLampParamsId = Shader.PropertyToID("_NFX_FogLampParams");
		// the lights the fog's light lists were last built with (F8)
		public static int FogLampCount { get; private set; }
		// boxes: the local fog boxes (5 float4 each, FogVolume.hlsl BoxesSigma), boxCount of them
		public static void TraceFogVolume(CommandBuffer cmd, ComputeShader bridge, Vector4 fog, RTHandle historyIn, RTHandle historyOut, RTHandle integrated,
		                                  Vector3 cameraDelta, float share, Vector4[] boxes = null, int boxCount = 0, Vector4[] smoke = null) {
			if (skyTransmittance_ == null) {
				return; // the fog reads the sky data (its noise, the transmittance): nothing sensible without it
			}
			var b = Buffers(TraceFogVolume_, FogW * FogH * FogD, 1, 16);
			if (boxes != null && boxCount > 0) {
				cmd.SetBufferData(b.Surface, boxes, 0, FogW * FogH * 3, boxCount * 5);
			} // surface: 3 float4 per column (fits)
			// the tyre smoke's grid after the 64 boxes' room (FogVolume.hlsl SmokeTransmittance)
			if (smoke != null) {
				cmd.SetBufferData(b.Surface, smoke, 0, FogW * FogH * 3 + 64 * 5, smoke.Length);
			}
			int prepare = bridge.FindKernel("PrepareFogVolume");
			cmd.SetComputeBufferParam(bridge, prepare, NativeSurfaceId, b.Surface);
			// the light lists, from the same lights Issue hands the engine (lights_, BeginFrame): one thread group per
			// tile of 8 x 8 columns (FogVolume.hlsl reads them by the same tiles)
			int lampCount = FogLightLists && lights_ != null ? Mathf.Min(lights_.Count, LocalLights.Max) : 0;
			if (lampCount > 0) {
				cmd.SetComputeVectorArrayParam(bridge, FogLampPosId, lights_.Positions);
			}
			cmd.SetComputeVectorParam(bridge, FogLampParamsId, new Vector4(lampCount, 0.0f, 0.0f, 0.0f));
			FogLampCount = lampCount;
			cmd.DispatchCompute(bridge, prepare, (FogW + 7) / 8, (FogH + 7) / 8, 1);
			kindParams_ = true;
			fogListsIssued_ = lampCount > 0;
			kindA_ = fog;
			kindB_ = new Vector4(boxCount, Time.time, smoke != null ? 1.0f : 0.0f, NatureFX.Map.MapFog.LampAirSigma);
			// Screen: the sky data (the boxes' noise)
			Issue(cmd, TraceFogVolume_, b.SurfacePtr, b.ResultPtr, FogW, FogH * FogD, skyTransmittance_ != null ? skyTransmittance_.ResultPtr : IntPtr.Zero);
			kindParams_ = false;
			fogListsIssued_ = false;
			int reproject = bridge.FindKernel("FogReproject");
			cmd.SetComputeBufferParam(bridge, reproject, NativeResultId, b.Result);
			cmd.SetComputeTextureParam(bridge, reproject, FogSourceId, historyIn);
			cmd.SetComputeTextureParam(bridge, reproject, FogTargetId, historyOut);
			cmd.SetComputeVectorParam(bridge, FogCamDeltaId, new Vector4(cameraDelta.x, cameraDelta.y, cameraDelta.z, share));
			cmd.DispatchCompute(bridge, reproject, (FogW + 7) / 8, (FogH * FogD + 7) / 8, 1);
			int integrate = bridge.FindKernel("FogIntegrate");
			cmd.SetComputeTextureParam(bridge, integrate, FogSourceId, historyOut);
			cmd.SetComputeTextureParam(bridge, integrate, FogTargetId, integrated);
			cmd.DispatchCompute(bridge, integrate, (FogW + 7) / 8, (FogH + 7) / 8, 1);
		}

		// Cars move: their transforms go to the engine every frame, for the render thread's TLAS
		private static readonly List<int> movingIndices_ = new();
		private static int[] movingIndexArray_ = Array.Empty<int>();
		private static float[] movingTransforms_ = Array.Empty<float>();

		private static void UpdateMoving() {
			if (movingIndices_.Count == 0) {
				return;
			}
			int n = 0;
			foreach (int i in movingIndices_) {
				var t = uploaded_[i].Follow;
				if (t == null) {
					continue;
				}
				var m = uploaded_[i].Unscaled ? Matrix4x4.TRS(t.position, t.rotation, Vector3.one) : t.localToWorldMatrix;
				int o = n * 12;
				movingTransforms_[o + 0] = m.m00; movingTransforms_[o + 1] = m.m01; movingTransforms_[o + 2] = m.m02; movingTransforms_[o + 3] = m.m03;
				movingTransforms_[o + 4] = m.m10; movingTransforms_[o + 5] = m.m11; movingTransforms_[o + 6] = m.m12; movingTransforms_[o + 7] = m.m13;
				movingTransforms_[o + 8] = m.m20; movingTransforms_[o + 9] = m.m21; movingTransforms_[o + 10] = m.m22; movingTransforms_[o + 11] = m.m23;
				movingIndexArray_[n] = i;
				++n;
			}
			NFX_Native_UpdateTransforms(movingIndexArray_, movingTransforms_, n);
		}

		public static string SunCounters {
			get {
				try {
					NFX_Native_SunCounters(out int traced, out int skipped);
					return $"{traced} frames traced, {skipped} skipped";
				} catch (Exception) {
					return "-";
				}
			}
		}

		// ------------------------------------------------------------------ scene

		// Builds the engine's scene from what the ray tracing scene holds. clear: a new map (all geometry dropped);
		// otherwise meshes already built are reused and only the instances are taken again.
		public static void UploadScene(ICollection<Instance> instances, bool clear) {
			if (!Ready || !Enabled) {
				return;
			}
			try {
				// meshes and materials built before stay unless they are gone (a new map): rebuilding them all made
				// every scene change a 250 ms hitch
				if (clear) {
					ReleaseGone();
				}
				int geometriesBefore = geometries_.Count;
				var transforms = new float[instances.Count * 12];
				var ids = new int[instances.Count];
				var flags = new int[instances.Count];
				var materials = new int[instances.Count];
				uploaded_.Clear();
				int n = 0, rejected = 0;
				foreach (var source in instances) {
					var instance = source;
					int id = Geometry(instance.Mesh, instance.SubMesh, !instance.AlphaTest);
					if (id < 0) {
						++rejected;
						continue;
					}
					// cars move: where they are now, not where they were added
					if (instance.Follow != null) {
						var t = instance.Follow;
						instance.Matrix = instance.Unscaled ? Matrix4x4.TRS(t.position, t.rotation, Vector3.one) : t.localToWorldMatrix;
					}
					var m = instance.Matrix;
					int o = n * 12;
					transforms[o + 0] = m.m00; transforms[o + 1] = m.m01; transforms[o + 2] = m.m02; transforms[o + 3] = m.m03;
					transforms[o + 4] = m.m10; transforms[o + 5] = m.m11; transforms[o + 6] = m.m12; transforms[o + 7] = m.m13;
					transforms[o + 8] = m.m20; transforms[o + 9] = m.m21; transforms[o + 10] = m.m22; transforms[o + 11] = m.m23;
					ids[n] = id;
					bool ground = Ground(instance);
					flags[n] = (instance.AlphaTest ? 1 : 0) | (instance.Glass ? 2 : 0) | (instance.Wind ? 4 : 0) | (instance.Follow != null ? 8 : 0)
					           | (ground ? 16 : 0) | (ground && Road(instance) ? 32 : 0);
					materials[n] = MaterialId(instance.Material, instance.Color, instance.Finish);
					uploaded_.Add(instance);
					++n;
				}
				NFX_Native_SetInstances(transforms, ids, flags, materials, n);
				FindEmitters();
				movingIndices_.Clear();
				for (int i = 0; i < uploaded_.Count; ++i) {
					if (uploaded_[i].Follow != null) {
						movingIndices_.Add(i);
					}
				}
				movingIndexArray_ = new int[movingIndices_.Count];
				movingTransforms_ = new float[movingIndices_.Count * 12];
				SetMarker(true);
				int result = NFX_Native_BuildScene();
				SetMarker(false);
				pending_.Clear();
				var buffer = new byte[512];
				int length = NFX_Native_SceneStats(buffer, buffer.Length);
				SceneStatus = result == 1 ? Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length)) + (rejected > 0 ? $", {rejected} meshes not usable ({runtimeMeshes_} made at runtime)" : "")
				                          : $"build failed at stage {-result}";
				Kino.Log.Info($"[NatureFX] Native engine scene: {SceneStatus}");
				if (rtStarted_) {
					Kino.Log.Info($"[NatureFX] DXR 1.2 sun shadows: {Dxr12SunStatus}");
				}
				if (result != 1) {
					StopIfGpuStopped();
					return;
				}
				if (clear || geometries_.Count > geometriesBefore) {
					CheckScene();
				}
			} catch (Exception e) {
				SceneStatus = $"failed ({e.GetType().Name}: {e.Message})";
				Kino.Log.Warning($"[NatureFX] Native engine scene: {SceneStatus}");
				Ready = false;
			}
		}

		private static readonly Dictionary<(Material, Color?, Vector4?), int> materials_ = new();
		private static readonly int MetallicId = Shader.PropertyToID("_Metallic");
		private static readonly int SmoothnessId = Shader.PropertyToID("_Smoothness");
		private static readonly int MaskMapId = Shader.PropertyToID("_MaskMap");
		private static readonly int MetallicRemapMinId = Shader.PropertyToID("_MetallicRemapMin");
		private static readonly int MetallicRemapMaxId = Shader.PropertyToID("_MetallicRemapMax");
		private static readonly int SmoothnessRemapMinId = Shader.PropertyToID("_SmoothnessRemapMin");
		private static readonly int SmoothnessRemapMaxId = Shader.PropertyToID("_SmoothnessRemapMax");
		private static readonly int CoatId = Shader.PropertyToID("_NFX_Coat");
		private static readonly int EmissiveColorId = Shader.PropertyToID("_EmissiveColor");
		private static readonly int EmissiveColorMapId = Shader.PropertyToID("_EmissiveColorMap");
		private static readonly int BaseColorMapId = Shader.PropertyToID("_BaseColorMap");
		private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
		private static readonly int AlphaCutoffId = Shader.PropertyToID("_AlphaCutoff");

		// A material for the engine's hits: its base colour map (the proxies carry the source material's: colour for
		// bounce light, alpha for the alpha test), base colour (the renderer's own, car paint, when it has one),
		// cutoff, metalness and smoothness. -1: none (the engine then uses grey).
		private static int MaterialId(Material material, Color? color, Vector4? finish) {
			if (material == null) {
				return -1;
			}
			if (materials_.TryGetValue((material, color, finish), out int id)) {
				return id;
			}
			var texture = material.HasProperty(BaseColorMapId) ? material.GetTexture(BaseColorMapId) : null;
			var native = texture != null ? texture.GetNativeTexturePtr() : IntPtr.Zero;
			bool srgb = texture != null && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(texture.graphicsFormat);
			var scale = native != IntPtr.Zero ? material.GetTextureScale(BaseColorMapId) : Vector2.one;
			var offset = native != IntPtr.Zero ? material.GetTextureOffset(BaseColorMapId) : Vector2.zero;
			float cutoff = material.HasProperty(AlphaCutoffId) ? material.GetFloat(AlphaCutoffId) : 0.5f;
			var baseColor = color ?? (material.HasProperty(BaseColorId) ? material.GetColor(BaseColorId) : new Color(0.5f, 0.5f, 0.5f));
			float alpha = material.HasProperty(BaseColorId) ? material.GetColor(BaseColorId).a : 1.0f;
			var linear = baseColor.linear;
			float metallic = material.HasProperty(MetallicId) ? Mathf.Clamp01(material.GetFloat(MetallicId)) : 0.0f;
			float smoothness = material.HasProperty(SmoothnessId) ? Mathf.Clamp01(material.GetFloat(SmoothnessId)) : 0.5f;
			// what the surface gives off (HDRP's _EmissiveColor, intensity included, times its map): the GI's light sources
			var emission = material.HasProperty(EmissiveColorId) ? material.GetColor(EmissiveColorId) : Color.black; // HDR: already linear
			var emissionTexture = emission.maxColorComponent > 0.0f && material.HasProperty(EmissiveColorMapId) ? material.GetTexture(EmissiveColorMapId) : null;
			var emissionMap = emissionTexture != null ? emissionTexture.GetNativeTexturePtr() : IntPtr.Zero;
			bool emissionSrgb = emissionTexture != null && UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(emissionTexture.graphicsFormat);
			// HDRP mask map (r metallic, a smoothness, remapped) where the material uses one
			var maskTexture = material.IsKeywordEnabled("_MASKMAP") && material.HasProperty(MaskMapId) ? material.GetTexture(MaskMapId) : null;
			var mask = maskTexture != null ? maskTexture.GetNativeTexturePtr() : IntPtr.Zero;
			float Get(int prop, float fallback) => material.HasProperty(prop) ? material.GetFloat(prop) : fallback;
			bool coat = material.IsKeywordEnabled("_MATERIAL_FEATURE_CLEAR_COAT") || Get(CoatId, 0.0f) > 0.5f;
			if (finish is Vector4 f) {
				metallic = f.x >= 0.0f ? Mathf.Clamp01(f.x) : metallic;
				smoothness = f.y >= 0.0f ? Mathf.Clamp01(f.y) : smoothness;
				coat = f.z >= 0.0f ? f.z > 0.5f : coat;
			}
			id = NFX_Native_AddMaterial(native, srgb ? 1 : 0, cutoff, alpha, scale.x, scale.y, offset.x, offset.y, linear.r, linear.g, linear.b, metallic, smoothness,
				mask, Get(MetallicRemapMinId, 0.0f), Get(MetallicRemapMaxId, 1.0f), Get(SmoothnessRemapMinId, 0.0f), Get(SmoothnessRemapMaxId, 1.0f), coat ? 1 : 0,
				emissionMap, emissionSrgb ? 1 : 0, emission.r, emission.g, emission.b);
			materials_[(material, color, finish)] = id;
			return id;
		}

		// Frees what belongs to destroyed meshes and materials (Unity's == null)
		private static void ReleaseGone() {
			var goneGeometries = new List<(Mesh, int)>();
			foreach (var pair in geometries_) {
				if (pair.Key.Item1 == null) {
					goneGeometries.Add(pair.Key);
					if (pair.Value >= 0) {
						NFX_Native_ReleaseGeometry(pair.Value);
					}
				}
			}
			foreach (var key in goneGeometries) {
				geometries_.Remove(key);
			}
			var goneMaterials = new List<(Material, Color?, Vector4?)>();
			foreach (var pair in materials_) {
				if (pair.Key.Item1 == null) {
					goneMaterials.Add(pair.Key);
					if (pair.Value >= 0) {
						NFX_Native_ReleaseMaterial(pair.Value);
					}
				}
			}
			foreach (var key in goneMaterials) {
				materials_.Remove(key);
			}
		}

		// A mesh / sub mesh as the engine's geometry; -1 when it is not usable. Meshes the CPU can read (deformable
		// car bodies, baked skinned meshes) go over as a copy of their data. Others are read from Unity's own GPU
		// buffers, inside their bounds, and their indices are checked on the GPU before the BLAS is built (bad ones
		// hung the GPU). Alpha tested ones bring their first UV set for the shadow rays' alpha test.
		private static int Geometry(Mesh mesh, int subMesh, bool opaque) {
			if (mesh == null) {
				return -1;
			}
			if (geometries_.TryGetValue((mesh, subMesh), out int id)) {
				return id;
			}
			id = -1;
			if (subMesh < mesh.subMeshCount && mesh.isReadable) {
				if (mesh.GetSubMesh(subMesh).topology == MeshTopology.Triangles) {
					var vertices = mesh.vertices;
					var indices = mesh.GetIndices(subMesh, true);
					var positions = new float[vertices.Length * 3];
					for (int v = 0; v < vertices.Length; ++v) {
						positions[v * 3] = vertices[v].x;
						positions[v * 3 + 1] = vertices[v].y;
						positions[v * 3 + 2] = vertices[v].z;
					}
					float[] uvs = null;
					{
						var uv = mesh.uv;
						if (uv.Length == vertices.Length) {
							uvs = new float[uv.Length * 2];
							for (int v = 0; v < uv.Length; ++v) {
								uvs[v * 2] = uv[v].x;
								uvs[v * 2 + 1] = uv[v].y;
							}
						}
					}
					float[] normals = null;
					{
						var n = mesh.normals;
						if (n.Length == vertices.Length) {
							normals = new float[n.Length * 3];
							for (int v = 0; v < n.Length; ++v) {
								normals[v * 3] = n[v].x;
								normals[v * 3 + 1] = n[v].y;
								normals[v * 3 + 2] = n[v].z;
							}
						}
					}
					id = NFX_Native_AddGeometryData(positions, vertices.Length, indices, indices.Length, opaque ? 1 : 0, uvs, normals);
				}
			} else if (mesh.GetInstanceID() < 0) {
				// made at runtime (the player's car: customised and deformed parts) and not readable: Unity keeps such
				// meshes in buffers it moves and reuses, and building from one hung the GPU twice. Left out.
				++runtimeMeshes_;
			} else if (subMesh < mesh.subMeshCount && mesh.HasVertexAttribute(VertexAttribute.Position)) {
				var sm = mesh.GetSubMesh(subMesh);
				var format = mesh.GetVertexAttributeFormat(VertexAttribute.Position);
				int dimension = mesh.GetVertexAttributeDimension(VertexAttribute.Position);
				// DXGI_FORMAT_R32G32B32_FLOAT, R16G16B16A16_FLOAT
				int dxgi = format == VertexAttributeFormat.Float32 && dimension >= 3 ? 6 : format == VertexAttributeFormat.Float16 && dimension == 4 ? 10 : -1;
				if (sm.topology == MeshTopology.Triangles && sm.indexCount >= 3 && dxgi > 0) {
					int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
					int stride = mesh.GetVertexBufferStride(stream);
					int offset = mesh.GetVertexAttributeOffset(VertexAttribute.Position);
					int indexSize = mesh.indexFormat == IndexFormat.UInt16 ? 2 : 4;
					IntPtr vb = mesh.GetNativeVertexBufferPtr(stream);
					IntPtr ib = mesh.GetNativeIndexBufferPtr();
					// first UV set: 1 two floats, 2 two halves
					IntPtr uvBuffer = IntPtr.Zero;
					ulong uvOffset = 0;
					int uvStride = 0, uvFormat = 0;
					if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0) && mesh.GetVertexAttributeDimension(VertexAttribute.TexCoord0) >= 2) {
						var uvType = mesh.GetVertexAttributeFormat(VertexAttribute.TexCoord0);
						uvFormat = uvType == VertexAttributeFormat.Float32 ? 1 : uvType == VertexAttributeFormat.Float16 ? 2 : 0;
						if (uvFormat > 0) {
							int uvStream = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
							uvStride = mesh.GetVertexBufferStride(uvStream);
							uvOffset = (ulong)(sm.baseVertex * uvStride + mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0));
							uvBuffer = mesh.GetNativeVertexBufferPtr(uvStream);
						}
					}
					// vertex normals: 3 three floats, 4 four halves (smooth hit normals; else the triangle's own)
					IntPtr normalBuffer = IntPtr.Zero;
					ulong normalOffset = 0;
					int normalStride = 0, normalFormat = 0;
					if (mesh.HasVertexAttribute(VertexAttribute.Normal)) {
						var normalType = mesh.GetVertexAttributeFormat(VertexAttribute.Normal);
						int normalDimension = mesh.GetVertexAttributeDimension(VertexAttribute.Normal);
						normalFormat = normalType == VertexAttributeFormat.Float32 && normalDimension >= 3 ? 3 : normalType == VertexAttributeFormat.Float16 && normalDimension == 4 ? 4 : 0;
						if (normalFormat > 0) {
							int normalStream = mesh.GetVertexAttributeStream(VertexAttribute.Normal);
							normalStride = mesh.GetVertexBufferStride(normalStream);
							normalOffset = (ulong)(sm.baseVertex * normalStride + mesh.GetVertexAttributeOffset(VertexAttribute.Normal));
							normalBuffer = mesh.GetNativeVertexBufferPtr(normalStream);
						}
					}
					if (vb != IntPtr.Zero && ib != IntPtr.Zero) {
						id = NFX_Native_AddGeometry(vb, (ulong)(sm.baseVertex * stride + offset), (uint)stride, (uint)(mesh.vertexCount - sm.baseVertex), dxgi,
							ib, (ulong)(sm.indexStart * indexSize), (uint)sm.indexCount, indexSize * 8, opaque ? 1 : 0,
							uvBuffer, uvOffset, (uint)uvStride, uvBuffer != IntPtr.Zero ? uvFormat : 0,
							normalBuffer, normalOffset, (uint)normalStride, normalBuffer != IntPtr.Zero ? normalFormat : 0);
					}
				}
			}
			geometries_[(mesh, subMesh)] = id;
			if (id >= 0 && pending_.Count < 40) {
				pending_.Add($"{mesh.name}#{subMesh}{(mesh.isReadable ? " (copy)" : "")}");
			}
			return id;
		}

		private static int runtimeMeshes_;
		// meshes added since the last build, written to the crash guard
		private static readonly List<string> pending_ = new();

		// ------------------------------------------------------------------ crash guard
		// A GPU hang in the engine's build takes the whole game down, and the setting stays on: the next start would
		// hang at the same place. A marker file stands while a build runs; one left over at start means the last
		// build crashed, and the engine stays off until switched on again.
		private static string MarkerPath => Path.Combine(Kino.Paths.Mods, ShaderBundle.FolderName, "NFXNative.building");

		// True once at start when the last session crashed in a build (the caller switches the engine off)
		public static bool CrashedLastTime { get; private set; }

		private static void CheckMarker() {
			try {
				if (File.Exists(MarkerPath)) {
					CrashedLastTime = true;
					Kino.Log.Warning($"[NatureFX] Native engine: the last session crashed while building the scene; own engine switched off. Meshes in that build: {File.ReadAllText(MarkerPath)}");
					File.Delete(MarkerPath);
				}
			} catch (Exception) {
			}
		}

		private static void SetMarker(bool building) {
			try {
				if (building) {
					File.WriteAllText(MarkerPath, pending_.Count > 0 ? string.Join(", ", pending_) : "(no new meshes)");
				} else if (File.Exists(MarkerPath)) {
					File.Delete(MarkerPath);
				}
			} catch (Exception) {
			}
		}

		// What a ray hits in the traced scene, nearest first, stepping past each hit (the "What is this?" tool)
		public static List<(float Distance, Instance Instance)> Pick(Ray ray, float maxDistance, int maxHits) {
			var result = new List<(float, Instance)>();
			if (!Ready || uploaded_.Count == 0) {
				return result;
			}
			float travelled = 0.0f;
			var distances = new float[1];
			var hits = new int[1];
			for (int i = 0; i < maxHits && travelled < maxDistance; ++i) {
				var o = ray.origin + ray.direction * travelled;
				var d = ray.direction;
				var rays = new[] { o.x, o.y, o.z, maxDistance - travelled, d.x, d.y, d.z, 0.0f };
				if (NFX_Native_TraceTestRays(rays, 1, distances, hits) != 1 || distances[0] < 0.0f || hits[0] < 0 || hits[0] >= uploaded_.Count) {
					break;
				}
				travelled += distances[0];
				result.Add((travelled, uploaded_[hits[0]]));
				travelled += 0.01f;
			}
			return result;
		}

		// Every instance gets one ray straight down through the middle of its bounds, from just above them. It should
		// hit the instance itself (or something overlapping it); a ray that passes through the bounds untouched
		// means the mesh came over wrong or in the wrong place. Reported per kind (static, car, skinned, ...).
		private static void CheckScene() {
			int count = uploaded_.Count;
			if (count == 0) {
				return;
			}
			// map geometry: one ray down the middle; cars (and the rest that moves): a 3 x 3 grid of rays over the
			// middle of their bounds, so a concave part (a mirror housing, a body with an open cabin) is not missed
			var owner = new List<int>();
			var rayList = new List<float>();
			var heights = new float[count];
			for (int i = 0; i < count; ++i) {
				var b = WorldBounds(uploaded_[i]);
				heights[i] = b.size.y;
				string kind = uploaded_[i].Kind;
				int grid = kind == "static" || kind == "tree" || kind == "terrain" || kind == "glass" ? 1 : 3;
				for (int gz = 0; gz < grid; ++gz) {
					for (int gx = 0; gx < grid; ++gx) {
						float fx = grid == 1 ? 0.0f : (gx - 1) * 0.3f;
						float fz = grid == 1 ? 0.0f : (gz - 1) * 0.3f;
						rayList.Add(b.center.x + b.size.x * fx); rayList.Add(b.max.y + 1.0f); rayList.Add(b.center.z + b.size.z * fz); rayList.Add(b.size.y + 2.0f);
						rayList.Add(0.0f); rayList.Add(-1.0f); rayList.Add(0.0f); rayList.Add(0.0f);
						owner.Add(i);
					}
				}
			}
			var rays = rayList.ToArray();
			var distances = new float[owner.Count];
			var hits = new int[owner.Count];
			int result = NFX_Native_TraceTestRays(rays, owner.Count, distances, hits);
			if (result != 1) {
				CheckStatus = $"check failed at stage {-result}";
				Kino.Log.Warning($"[NatureFX] Native engine scene check: {CheckStatus}");
				StopIfGpuStopped();
				return;
			}

			// per instance: 2 a ray hit it, 1 a ray hit something overlapping it, 0 every ray passed through
			var verdict = new int[count];
			for (int r = 0; r < owner.Count; ++r) {
				int i = owner[r];
				bool inside = distances[r] >= 0.0f && distances[r] <= heights[i] + 1.05f;
				verdict[i] = Math.Max(verdict[i], inside ? (hits[r] == i ? 2 : 1) : 0);
			}
			var stats = new Dictionary<string, int[]>(); // kind -> self, other, through
			var missing = new List<string>();
			for (int i = 0; i < count; ++i) {
				string kind = uploaded_[i].Kind ?? "?";
				if (!stats.TryGetValue(kind, out var st)) {
					stats[kind] = st = new int[3];
				}
				if (verdict[i] == 2) {
					++st[0];
				} else if (verdict[i] == 1) {
					++st[1];
				} else {
					++st[2];
					if (kind != "static" && kind != "tree" && kind != "glass" && missing.Count < 8) {
						var mesh = uploaded_[i].Mesh;
						string source = mesh.isReadable ? "copy" : mesh.GetInstanceID() < 0 ? "runtime" : "GPU";
						missing.Add($"{kind}:{mesh.name}#{uploaded_[i].SubMesh} ({source}, {mesh.vertexCount} vertices)");
					}
				}
			}
			var report = new StringBuilder();
			foreach (var pair in stats) {
				report.Append($" {pair.Key} {pair.Value[0]} self / {pair.Value[1]} other / {pair.Value[2]} through;");
			}
			CheckStatus = report.ToString().Trim();
			Kino.Log.Info($"[NatureFX] Native engine scene check (rays down through each instance):{report}" +
			              (missing.Count > 0 ? $" passed through: {string.Join(", ", missing)}" : ""));
		}

		// After a failure: when the engine reports that the GPU stopped, it is off for the rest of the session
		private static void StopIfGpuStopped() {
			var buffer = new byte[1024];
			int length = NFX_Native_Info(buffer, buffer.Length);
			string info = Encoding.UTF8.GetString(buffer, 0, Math.Max(0, length));
			if (info.Contains("GPU stopped")) {
				Info = info;
				Ready = false;
				Kino.Log.Warning($"[NatureFX] Native engine stopped: {info}");
			}
		}

		// The bright emissive instances of the static scene, as lamps: intensity = luminance x area (a flat emitter seen
		// face on), the range where that falls to a few lux, the shape from the instance's size
		private static void FindEmitters() {
			Emitters.Clear();
			foreach (var instance in uploaded_) {
				var m = instance.Material;
				if (instance.Follow != null || instance.Glass || m == null || !m.HasProperty(EmissiveColorId)) {
					continue;
				}
				var emission = m.GetColor(EmissiveColorId); // HDR: already linear (.linear made 3000 nits 44 million)
				float luminance = 0.2126f * emission.r + 0.7152f * emission.g + 0.0722f * emission.b;
				if (luminance < EmitterMinLuminance) {
					continue;
				}
				var b = WorldBounds(instance);
				var s = b.size;
				// the two largest sides: the face of a sign or a tube's length times its width
				float area = Mathf.Max(s.x * s.y, Mathf.Max(s.y * s.z, s.x * s.z));
				area = Mathf.Clamp(area, 0.01f, 20.0f);
				var intensity = emission * area;
				float peak = Mathf.Max(intensity.r, Mathf.Max(intensity.g, intensity.b));
				Emitters.Add(new Emitter {
					Position = b.center,
					Intensity = intensity,
					Range = Mathf.Clamp(Mathf.Sqrt(peak / 2.0f), 1.0f, 40.0f),
					Radius = Mathf.Clamp(0.5f * Mathf.Max(s.x, Mathf.Max(s.y, s.z)), 0.1f, 2.0f)
				});
			}
			if (Emitters.Count > 0 && Emitters.Count != loggedEmitters_) {
				loggedEmitters_ = Emitters.Count;
				Kino.Log.Info($"[NatureFX] Native engine: {Emitters.Count} emissive surfaces light their surroundings");
			}
		}

		private static Bounds WorldBounds(Instance instance) {
			// the sub mesh's own bounds: a statically batched mesh holds a whole area
			var local = instance.SubMesh < instance.Mesh.subMeshCount ? instance.Mesh.GetSubMesh(instance.SubMesh).bounds : instance.Mesh.bounds;
			if (local.size == Vector3.zero) {
				local = instance.Mesh.bounds;
			}
			var m = instance.Matrix;
			var b = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
			for (int c = 0; c < 8; ++c) {
				var corner = local.center + Vector3.Scale(local.extents, new Vector3((c & 1) != 0 ? 1 : -1, (c & 2) != 0 ? 1 : -1, (c & 4) != 0 ? 1 : -1));
				b.Encapsulate(m.MultiplyPoint3x4(corner));
			}
			return b;
		}
	}
}

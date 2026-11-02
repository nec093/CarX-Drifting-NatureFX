using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using KSL.API;
using NatureFX.Rendering;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace NatureFX.Tools {
	// The debug page: a small web server on this PC only (127.0.0.1) that a browser opens beside the game, so the
	// detail (video memory by kind, timings, the scene, the log) stays off the game's screen. Settings.DebugPage turns
	// it on (F8 > Developer). The page polls /stats.json once a second; the main thread makes that snapshot (Unity's
	// API is main thread only) while a page has asked for it in the last few seconds, and the server thread only hands
	// the latest one out. Nothing leaves the PC: the listener is bound to the loopback address.
	public static class DebugPage {
		private static TcpListener listener_;
		private static Thread thread_;
		private static volatile bool running_;
		private static volatile string snapshot_ = "{}";
		private static long lastRequestTicks_;
		private static float nextSnapshot_, nextTextures_;
		private static int port_;
		private static string logPath_;
		private static float fps_ = 60.0f;
		private static List<(string Group, long Bytes, int Count)> nfxTargets_ = new();
		private static List<(string Name, long Bytes)> bigTargets_ = new();
		private static long otherTargets_;
		private static int otherTargetCount_;
		public static string Status { get; private set; } = "off";
		public static string Url => $"http://127.0.0.1:{port_}/";

		// Per frame (main thread): start or stop as the setting says, and make a snapshot when a page is watching
		private static NatureFXMod mod_;

		public static void Update(NatureFXMod mod, bool on, int port) {
			mod_ = mod;
			if (port < 1024 || port > 65535) {
				port = 8765;
			}
			if (on && (!running_ || port != port_)) {
				Stop();
				Start(port);
			} else if (!on && running_) {
				Stop();
			}
			if (!running_) {
				return;
			}
			float dt = Time.unscaledDeltaTime;
			if (dt > 0.0f) {
				fps_ += (1.0f / dt - fps_) * 0.05f;
			}
			bool watched = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastRequestTicks_) < TimeSpan.TicksPerSecond * 5;
			if (!watched || Time.unscaledTime < nextSnapshot_) {
				return;
			}
			nextSnapshot_ = Time.unscaledTime + 1.0f;
			try {
				// the render targets and textures every 3 s (a walk over every texture Unity has)
				if (Time.unscaledTime >= nextTextures_) {
					nextTextures_ = Time.unscaledTime + 3.0f;
					CountTextures();
				}
				snapshot_ = Snapshot();
			} catch (Exception e) {
				snapshot_ = "{\"error\":" + Json(e.GetType().Name + ": " + e.Message) + "}";
			}
		}

		private static void Start(int port) {
			try {
				port_ = port;
				logPath_ = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "kino", "output.log"));
				listener_ = new TcpListener(IPAddress.Loopback, port);
				listener_.Start();
				running_ = true;
				thread_ = new Thread(Serve) { IsBackground = true, Name = "NatureFX debug page" };
				thread_.Start();
				Status = $"on: {Url}";
				Kino.Log.Info($"[NatureFX] Debug page: {Url} (this PC only)");
			} catch (Exception e) {
				running_ = false;
				listener_ = null;
				Status = $"could not start on port {port} ({e.Message})";
				Kino.Log.Warning($"[NatureFX] Debug page: {Status}");
			}
		}

		public static void Stop() {
			running_ = false;
			try {
				listener_?.Stop();
			} catch (Exception) {
				// closing anyway
			}
			listener_ = null;
			thread_ = null;
			Status = "off";
		}

		// ------------------------------------------------------------------ events: memory at each step of a load
		// NatureFXMod marks the steps after a scene load (the ray tracing scene, the material replacement, ...); each
		// mark counts the memory by kind there and then, so the page can show which step added what. Only while the
		// page is on (a count walks every texture and mesh: some tens of ms at a load).
		private sealed class Mark {
			public float Time;
			public string Label;
			public long Gpu, Native, Buffers, Targets, OtherTargets, GameTextures, Meshes;
		}
		private static readonly List<Mark> marks_ = new();
		private static readonly object marksLock_ = new();

		public static void MarkStep(string label) {
			if (!running_) {
				return;
			}
			try {
				CountTextures();
				var m = new Mark { Time = UnityEngine.Time.unscaledTime, Label = label };
				NativeEngine.GpuMemoryMB(out ulong used, out _);
				m.Gpu = (long)used << 20;
				var native = NativeEngine.NativeMemory();
				if (native != null) {
					for (int i = 0; i < native.Length; ++i) {
						m.Native += NativeEngine.NativeMemoryNames[i].Contains("(used)") ? 0 : (long)native[i];
					}
				}
				foreach (var (_, bytes) in NativeEngine.TraceMemory()) {
					m.Buffers += bytes;
				}
				foreach (var (_, bytes, _) in nfxTargets_) {
					m.Targets += bytes;
				}
				m.OtherTargets = otherTargets_;
				m.GameTextures = gameTextures_;
				m.Meshes = meshes_;
				lock (marksLock_) {
					marks_.Add(m);
					if (marks_.Count > 80) {
						marks_.RemoveAt(0);
					}
				}
			} catch (Exception e) {
				Kino.Log.Warning($"[NatureFX] Debug page: mark '{label}' failed ({e.Message})");
			}
		}

		private static void AppendMarks(StringBuilder sb, CultureInfo inv) {
			sb.Append(",\"marks\":[");
			lock (marksLock_) {
				for (int i = 0; i < marks_.Count; ++i) {
					var m = marks_[i];
					sb.Append(i > 0 ? "," : "").Append("{\"t\":").Append(m.Time.ToString("0.0", inv)).Append(",\"label\":").Append(Json(m.Label))
					  .Append(",\"gpu\":").Append(m.Gpu).Append(",\"native\":").Append(m.Native).Append(",\"buffers\":").Append(m.Buffers)
					  .Append(",\"targets\":").Append(m.Targets).Append(",\"other\":").Append(m.OtherTargets)
					  .Append(",\"textures\":").Append(m.GameTextures).Append(",\"meshes\":").Append(m.Meshes).Append('}');
				}
			}
			sb.Append(']');
		}

		// ------------------------------------------------------------------ server thread
		private static void Serve() {
			while (running_) {
				TcpClient client;
				try {
					client = listener_.AcceptTcpClient();
				} catch (Exception) {
					return; // stopped
				}
				try {
					using (client) {
						client.ReceiveTimeout = 2000;
						client.SendTimeout = 2000;
						var stream = client.GetStream();
						string path = ReadPath(stream);
						if (path == null) {
							continue;
						}
						if (path == "/" || path.StartsWith("/?", StringComparison.Ordinal)) {
							Send(stream, "200 OK", "text/html; charset=utf-8", Page.Html);
						} else if (path.StartsWith("/stats.json", StringComparison.Ordinal)) {
							Interlocked.Exchange(ref lastRequestTicks_, DateTime.UtcNow.Ticks);
							Send(stream, "200 OK", "application/json; charset=utf-8", snapshot_);
						} else if (path.StartsWith("/log", StringComparison.Ordinal)) {
							Send(stream, "200 OK", "text/plain; charset=utf-8", LogTail());
						} else {
							Send(stream, "404 Not Found", "text/plain", "not found");
						}
					}
				} catch (Exception) {
					// a browser closing the connection early: nothing to do
				}
			}
		}

		// The request line's path; the headers are read and dropped
		private static string ReadPath(NetworkStream stream) {
			var buffer = new byte[4096];
			int total = 0;
			while (total < buffer.Length) {
				int n = stream.Read(buffer, total, buffer.Length - total);
				if (n <= 0) {
					break;
				}
				total += n;
				string text = Encoding.ASCII.GetString(buffer, 0, total);
				if (text.Contains("\r\n\r\n")) {
					break;
				}
			}
			string request = Encoding.ASCII.GetString(buffer, 0, total);
			int end = request.IndexOf("\r\n", StringComparison.Ordinal);
			string[] parts = (end > 0 ? request.Substring(0, end) : request).Split(' ');
			return parts.Length >= 2 && parts[0] == "GET" ? parts[1] : null;
		}

		private static void Send(NetworkStream stream, string status, string type, string body) {
			byte[] data = Encoding.UTF8.GetBytes(body);
			string head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {data.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
			byte[] headBytes = Encoding.ASCII.GetBytes(head);
			stream.Write(headBytes, 0, headBytes.Length);
			stream.Write(data, 0, data.Length);
		}

		// The last NatureFX lines of kino/output.log (read on the server thread, the file shared with the writer)
		private static string LogTail() {
			try {
				if (logPath_ == null || !File.Exists(logPath_)) {
					return $"(no log at {logPath_})";
				}
				using var file = new FileStream(logPath_, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
				long start = Math.Max(0, file.Length - 256 * 1024);
				file.Seek(start, SeekOrigin.Begin);
				using var reader = new StreamReader(file, Encoding.UTF8);
				var lines = new List<string>();
				string line;
				while ((line = reader.ReadLine()) != null) {
					if (line.Contains("NatureFX")) {
						lines.Add(line);
					}
				}
				int from = Math.Max(0, lines.Count - 300);
				return string.Join("\n", lines.GetRange(from, lines.Count - from));
			} catch (Exception e) {
				return $"(log not readable: {e.Message})";
			}
		}

		// ------------------------------------------------------------------ main thread: the snapshot
		private static long TextureBytes(Texture t) {
			var format = t.graphicsFormat;
			int depth = 1;
			bool mips = t.mipmapCount > 1;
			if (t is RenderTexture rt) {
				depth = Mathf.Max(1, rt.volumeDepth);
				mips = rt.useMipMap;
			} else if (t is Texture3D t3) {
				depth = t3.depth;
			} else if (t is Texture2DArray ta) {
				depth = ta.depth;
			} else if (t is Cubemap) {
				depth = 6;
			}
			long bytes = 0;
			if (format != GraphicsFormat.None) {
				uint bw = Math.Max(1u, GraphicsFormatUtility.GetBlockWidth(format)), bh = Math.Max(1u, GraphicsFormatUtility.GetBlockHeight(format));
				long blocks = ((t.width + bw - 1) / bw) * (long)((t.height + bh - 1) / bh);
				bytes = blocks * GraphicsFormatUtility.GetBlockSize(format) * depth;
			}
			if (t is RenderTexture r) {
				bytes *= Math.Max(1, r.antiAliasing);
				if (r.depthStencilFormat != GraphicsFormat.None) {
					bytes += (long)r.width * r.height * 4 * Math.Max(1, r.antiAliasing);
				}
			}
			return mips ? bytes * 4 / 3 : bytes;
		}

		// "NFX_RTShadow_Color" -> "NFX_RTShadow"; "NatureFX.SmokeVolume" -> "NatureFX.SmokeVolume"
		private static string Group(string name) {
			if (name.StartsWith("NFX_", StringComparison.Ordinal)) {
				int second = name.IndexOf('_', 4);
				return second > 0 ? name.Substring(0, second) : name;
			}
			return name;
		}

		// the game's own textures (materials, lightmaps) and every mesh: what makes up the "rest"
		private static long gameTextures_, gameTexturesReadable_, gameTexturesStreamed_;
		private static int gameTextureCount_, gameTextureStreamedCount_;
		private static List<(string Name, long Bytes)> bigGameTextures_ = new();
		private static List<(string Format, long Bytes, int Count)> gameTextureFormats_ = new();
		private static long meshes_, rtMeshes_, natureMeshes_;
		private static int meshCount_, rtMeshCount_, natureMeshCount_;
		private static List<(string Name, long Bytes)> bigMeshes_ = new();

		// The largest game texture and what refers to it (materials, volume profiles' skies): where a texture nothing
		// draws comes from
		private static Texture biggest_;
		private static long biggestBytes_;
		private static string biggestUsers_ = "-";

		private static string UsersOf(Texture t) {
			if (t == null) {
				return "-";
			}
			var users = new List<string>();
			foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) {
				if (m == null) {
					continue;
				}
				foreach (int id in m.GetTexturePropertyNameIDs()) {
					if (m.GetTexture(id) == t) {
						users.Add($"material {m.name} ({(m.shader != null ? m.shader.name : "-")})");
						break;
					}
				}
				if (users.Count >= 8) {
					break;
				}
			}
			foreach (var p in Resources.FindObjectsOfTypeAll<UnityEngine.Rendering.VolumeProfile>()) {
				if (p != null && p.TryGet<UnityEngine.Rendering.HighDefinition.PhysicallyBasedSky>(out var pbs) && pbs.spaceEmissionTexture.value == t) {
					users.Add($"volume profile {p.name} (physically based sky, space emission{(pbs.active ? "" : ", component off")})");
				}
			}
			if (RenderSettings.skybox != null && RenderSettings.skybox.HasProperty("_Tex") && RenderSettings.skybox.GetTexture("_Tex") == t) {
				users.Add($"RenderSettings.skybox {RenderSettings.skybox.name}");
			}
			return $"{t.name}: " + (users.Count > 0 ? string.Join("; ", users) : "nothing found (a script holds it)");
		}

		// What a mesh's vertex and index buffers take on the GPU
		private static long MeshBytes(Mesh m) {
			long bytes = 0;
			for (int s = 0; s < m.vertexBufferCount; ++s) {
				bytes += (long)m.vertexCount * m.GetVertexBufferStride(s);
			}
			long indices = 0;
			for (int sub = 0; sub < m.subMeshCount; ++sub) {
				indices += (long)m.GetIndexCount(sub);
			}
			return bytes + indices * (m.indexFormat == UnityEngine.Rendering.IndexFormat.UInt16 ? 2 : 4);
		}

		private static void CountTextures() {
			var groups = new Dictionary<string, (long Bytes, int Count)>();
			var big = new List<(string, long)>();
			var bigGame = new List<(string, long)>();
			var formats = new Dictionary<string, (long Bytes, int Count)>();
			biggest_ = null;
			biggestBytes_ = 0;
			long other = 0, game = 0, readable = 0, streamed = 0;
			int otherCount = 0, gameCount = 0, streamedCount = 0;
			foreach (var t in Resources.FindObjectsOfTypeAll<Texture>()) {
				if (t == null) {
					continue;
				}
				if (t is RenderTexture rt && !rt.IsCreated()) {
					continue;
				}
				string name = t.name ?? "";
				bool ours = name.StartsWith("NFX_", StringComparison.Ordinal) || name.StartsWith("NatureFX", StringComparison.Ordinal);
				long bytes = TextureBytes(t);
				if (ours) {
					string g = Group(name);
					groups.TryGetValue(g, out var v);
					groups[g] = (v.Bytes + bytes, v.Count + 1);
				} else if (t is RenderTexture) {
					other += bytes;
					++otherCount;
					if (bytes >= 4L << 20) {
						big.Add(($"{name} ({t.width}x{t.height} {t.graphicsFormat})", bytes));
					}
				} else {
					// streamed textures hold only some of their mips
					if (t is Texture2D t2 && t2.streamingMipmaps && t2.loadedMipmapLevel > 0) {
						bytes >>= 2 * Math.Min(t2.loadedMipmapLevel, 15);
					}
					game += bytes;
					++gameCount;
					if (t is Texture2D ts && ts.streamingMipmaps) {
						streamed += bytes;
						++streamedCount;
					}
					if (t.isReadable) {
						readable += bytes;
					}
					string f = t.graphicsFormat.ToString();
					formats.TryGetValue(f, out var fv);
					formats[f] = (fv.Bytes + bytes, fv.Count + 1);
					if (bytes >= 64L << 20 && (biggest_ == null || bytes > biggestBytes_)) {
						biggest_ = t;
						biggestBytes_ = bytes;
					}
					if (bytes >= 4L << 20) {
						bigGame.Add(($"{(name.Length > 0 ? name : "(no name)")} ({t.GetType().Name} {t.width}x{t.height} {t.graphicsFormat}{(t.isReadable ? ", readable" : "")})", bytes));
					}
				}
			}
			var list = new List<(string, long, int)>();
			foreach (var pair in groups) {
				list.Add((pair.Key, pair.Value.Bytes, pair.Value.Count));
			}
			list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			big.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			bigGame.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			var formatList = new List<(string, long, int)>();
			foreach (var pair in formats) {
				formatList.Add((pair.Key, pair.Value.Bytes, pair.Value.Count));
			}
			formatList.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			nfxTargets_ = list;
			bigTargets_ = big.Count > 30 ? big.GetRange(0, 30) : big;
			otherTargets_ = other;
			otherTargetCount_ = otherCount;
			gameTextures_ = game;
			gameTexturesReadable_ = readable;
			gameTextureCount_ = gameCount;
			gameTexturesStreamed_ = streamed;
			gameTextureStreamedCount_ = streamedCount;
			bigGameTextures_ = bigGame.Count > 30 ? bigGame.GetRange(0, 30) : bigGame;
			gameTextureFormats_ = formatList.Count > 12 ? formatList.GetRange(0, 12) : formatList;
			biggestUsers_ = bigGame.Count > 0 && bigGame[0].Item2 >= 64L << 20 ? UsersOf(biggest_) : "-";

			// meshes: the game's, NatureFX's own (grass, forest, debris), and the ones made only for the ray tracing
			// scene (NFX_RT_*: terrain chunks, baked skinned meshes; the engine keeps its own copy of their data)
			long all = 0, rtOnly = 0, nature = 0;
			int count = 0, rtCount = 0, natureCount = 0;
			var bigMeshes = new List<(string, long)>();
			foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>()) {
				if (m == null) {
					continue;
				}
				long bytes;
				try {
					bytes = MeshBytes(m);
				} catch (Exception) {
					continue;
				}
				string name = m.name ?? "";
				all += bytes;
				++count;
				if (name.StartsWith("NFX_RT_", StringComparison.Ordinal)) {
					rtOnly += bytes;
					++rtCount;
				} else if (name.StartsWith("NatureFX", StringComparison.Ordinal)) {
					nature += bytes;
					++natureCount;
				}
				if (bytes >= 4L << 20) {
					bigMeshes.Add(($"{(name.Length > 0 ? name : "(no name)")} ({m.vertexCount:N0} vertices, {m.subMeshCount} sub meshes{(m.isReadable ? ", readable" : "")})", bytes));
				}
			}
			bigMeshes.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			meshes_ = all;
			rtMeshes_ = rtOnly;
			natureMeshes_ = nature;
			meshCount_ = count;
			rtMeshCount_ = rtCount;
			natureMeshCount_ = natureCount;
			bigMeshes_ = bigMeshes.Count > 20 ? bigMeshes.GetRange(0, 20) : bigMeshes;
		}

		// HDRP's own features as the game camera runs them now (its frame settings) and the volume values that decide
		// whether they do any work: where the own engine replaces one, HDRP running it too is wasted time and memory
		private static void HdrpState(Section sec) {
			var cam = Camera.main;
			if (cam == null) {
				sec.Add("camera", "none");
				return;
			}
			UnityEngine.Rendering.HighDefinition.FrameSettings fs;
			try {
				fs = UnityEngine.Rendering.HighDefinition.HDCamera.GetOrCreate(cam).frameSettings;
			} catch (Exception e) {
				sec.Add("frame settings", $"({e.GetType().Name})", -1.0, false, 1);
				return;
			}
			string[] fields = {
				"Volumetrics", "VolumetricClouds", "AtmosphericScattering", "SSR", "SSAO", "SSGI", "ContactShadows", "ScreenSpaceShadows",
				"ShadowMaps", "Shadowmask", "ReflectionProbe", "PlanarProbe", "SkyReflection", "Bloom", "LensFlareScreenSpace",
				"LensFlareDataDriven", "MotionBlur", "DepthOfField", "Antialiasing", "Postprocess", "Decals", "LowResTransparent",
				"Refraction", "Distortion", "Water", "SubsurfaceScattering", "Transmission", "CustomPass"
			};
			foreach (string f in fields) {
				if (Enum.TryParse(f, out UnityEngine.Rendering.HighDefinition.FrameSettingsField field)) {
					sec.Add($"camera: {f}", fs.IsEnabled(field) ? "on" : "off");
				}
			}
			var stack = VolumeOverhaul.Stack();
			if (stack == null) {
				return;
			}
			sec.Try("volume: fog", () => {
				var fog = stack.GetComponent<UnityEngine.Rendering.HighDefinition.Fog>();
				return fog == null ? "-" : $"{(fog.enabled.value ? "on" : "off")}, volumetric {(fog.enableVolumetricFog.value ? "on" : "off")}, mean free path {fog.meanFreePath.value:0} m";
			});
			sec.Try("volume: volumetric clouds", () => {
				var c = stack.GetComponent<UnityEngine.Rendering.HighDefinition.VolumetricClouds>();
				return c == null ? "-" : c.enable.value ? "on" : "off";
			});
			sec.Try("volume: bloom", () => {
				var b = stack.GetComponent<UnityEngine.Rendering.HighDefinition.Bloom>();
				return b == null ? "-" : $"intensity {b.intensity.value:0.###}";
			});
			sec.Try("volume: screen space lens flare", () => {
				var l = stack.GetComponent<UnityEngine.Rendering.HighDefinition.ScreenSpaceLensFlare>();
				return l == null ? "-" : $"intensity {l.intensity.value:0.###}";
			});
			sec.Try("volume: SSR", () => {
				var r = stack.GetComponent<UnityEngine.Rendering.HighDefinition.ScreenSpaceReflection>();
				return r == null ? "-" : r.enabled.value ? "on" : "off";
			});
			sec.Try("volume: SSAO", () => {
				var a = stack.GetComponent<UnityEngine.Rendering.HighDefinition.ScreenSpaceAmbientOcclusion>();
				return a == null ? "-" : $"intensity {a.intensity.value:0.###}";
			});
			sec.Try("volume: SSGI", () => {
				var g = stack.GetComponent<UnityEngine.Rendering.HighDefinition.GlobalIllumination>();
				return g == null ? "-" : g.enable.value ? "on" : "off";
			});
			sec.Try("volume: contact shadows", () => {
				var c = stack.GetComponent<UnityEngine.Rendering.HighDefinition.ContactShadows>();
				return c == null ? "-" : c.enable.value ? "on" : "off";
			});
			sec.Try("volume: shadow distance", () => {
				var sh = stack.GetComponent<UnityEngine.Rendering.HighDefinition.HDShadowSettings>();
				return sh == null ? "-" : $"{sh.maxShadowDistance.value:0} m, {sh.cascadeShadowSplitCount.value} cascades";
			});
			// lights that still have HDRP shadow maps (the cascade atlas is there while a directional one casts)
			try {
				var shadowed = new List<string>();
				foreach (var l in UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
					if (l.isActiveAndEnabled && l.type == LightType.Directional && l.shadows != LightShadows.None) {
						shadowed.Add($"{l.name} ({l.intensity:0.##})");
					}
				}
				sec.Add("directional lights with shadow maps", shadowed.Count > 0 ? string.Join(", ", shadowed) : "none", -1.0, false, shadowed.Count > 0 ? 1 : 0);
			} catch (Exception e) {
				sec.Add("directional lights with shadow maps", $"({e.GetType().Name})");
			}
			sec.Try("quality: texture mip limit", () => $"{QualitySettings.globalTextureMipmapLimit} (0 full size)");
			sec.Try("quality: mip streaming", () => QualitySettings.streamingMipmapsActive ? $"on, budget {QualitySettings.streamingMipmapsMemoryBudget:0} MB" : "off");
		}

		// ------------------------------------------------------------------ the snapshot: sections of rows
		// A row: label, value, a bar's value (bytes or ms; < 0 none), indented, level (0 plain, 1 warning, 2 bad)
		private sealed class Section {
			public string Id, Title;
			public readonly List<(string Label, string Value, double Bar, bool Indent, int Level)> Rows = new();
			public Section(string id, string title) { Id = id; Title = title; }
			public void Add(string label, string value, double bar = -1.0, bool indent = false, int level = 0) => Rows.Add((label, value ?? "", bar, indent, level));
			public void Bytes(string label, long bytes, bool indent = false) => Add(label, Mb(bytes), bytes, indent);
			// a status from somewhere that may not be set up (null members early on): never stops the page
			public void Try(string label, Func<string> value) {
				string v;
				try {
					v = value();
				} catch (Exception e) {
					v = $"({e.GetType().Name})";
				}
				Add(label, v);
			}
		}

		private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

		private static string Snapshot() {
			var inv = CultureInfo.InvariantCulture;
			var mod = mod_;
			var rt = mod?.RT;
			RayTracingEffects.DebugStats stats = null;
			try {
				stats = rt?.ReadStats();
			} catch (Exception) {
				// the passes not made yet
			}
			var sections = new List<Section>();
			NativeEngine.GpuMemoryMB(out ulong used, out ulong budget);

			// memory
			var native = NativeEngine.NativeMemory();
			long nativeTotal = 0;
			var engine = new Section("engine-memory", "Own engine memory (NFXNative)");
			if (native != null) {
				for (int i = 0; i < native.Length; ++i) {
					bool part = NativeEngine.NativeMemoryNames[i].Contains("(used)");
					engine.Bytes(NativeEngine.NativeMemoryNames[i], (long)native[i], part);
					nativeTotal += part ? 0 : (long)native[i];
				}
				engine.Add("not counted", "DLSS (NGX allocates on its own)");
			} else {
				engine.Add("not reported", "this NFXNative.dll is older than the page", -1.0, false, 1);
			}
			var buffers = new Section("buffers", "Trace buffers");
			long bufferTotal = 0;
			var traces = NativeEngine.TraceMemory();
			traces.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
			foreach (var (name, bytes) in traces) {
				buffers.Bytes(name, bytes);
				bufferTotal += bytes;
			}
			var targets = new Section("targets", "NatureFX render targets and textures");
			long targetTotal = 0;
			foreach (var (group, bytes, count) in nfxTargets_) {
				targets.Add($"{group} ({count})", Mb(bytes), bytes);
				targetTotal += bytes;
			}
			var others = new Section("other-targets", "Other render targets (game, HDRP)");
			others.Add($"all ({otherTargetCount_})", Mb(otherTargets_), otherTargets_);
			foreach (var (name, bytes) in bigTargets_) {
				others.Bytes(name, bytes, true);
			}
			long ours = nativeTotal + bufferTotal + targetTotal;
			long usedBytes = (long)used << 20;
			var split = new Section("split", "GPU memory split");
			split.Bytes("own engine", nativeTotal);
			split.Bytes("trace buffers", bufferTotal);
			split.Bytes("NatureFX render targets / textures", targetTotal);
			split.Bytes("other render targets (game, HDRP)", otherTargets_);
			split.Bytes("game textures", gameTextures_);
			split.Bytes("meshes", meshes_);
			split.Bytes("rest (HDRP's buffers, DLSS, driver, ...)", Math.Max(0, usedBytes - ours - otherTargets_ - gameTextures_ - meshes_));

			// performance
			var perf = new Section("perf", "Performance");
			perf.Add("frame rate", $"{fps_:0} fps, {1000.0f / Mathf.Max(fps_, 1.0f):0.0} ms", -1.0, false, fps_ < 30.0f ? 2 : fps_ < 55.0f ? 1 : 0);
			perf.Add("GPU memory", budget > 0 ? $"{used:N0} / {budget:N0} MB" : "unknown", -1.0, false, budget > 0 && used > budget * 0.9 ? 2 : budget > 0 && used > budget * 0.8 ? 1 : 0);
			perf.Add("NatureFX counted", Mb(ours));
			var cam = Camera.main;
			perf.Add("camera", cam != null ? $"{cam.pixelWidth}x{cam.pixelHeight}" : "-");
			if (stats != null) {
				perf.Add("mode", stats.Mode);
			}
			var timings = new Section("timings", "GPU time");
			if (stats != null) {
				if (stats.TimingNote != null) {
					timings.Add("note", stats.TimingNote, -1.0, false, 1);
				}
				foreach (var (label, value, part) in stats.Timings) {
					double ms = -1.0;
					int space = value.IndexOf(" ms", StringComparison.Ordinal);
					if (space > 0) {
						double.TryParse(value.Substring(0, space), NumberStyles.Float, inv, out ms);
					}
					timings.Add(label.Trim(), value, label == "total GPU" ? -1.0 : ms, part);
				}
			}

			// the scene and the ray traced effects (what the overlay showed)
			var scene = new Section("scene", "Scene");
			var features = new Section("features", "RT features");
			var denoisers = new Section("denoisers", "Denoisers");
			var passes = new Section("passes", "RT passes");
			if (stats != null) {
				foreach (var (label, value) in stats.Scene) {
					scene.Add(label.Length > 0 ? label : "", value);
				}
				foreach (var (name, on) in stats.Features) {
					features.Add(name, on ? "on" : "off", -1.0, false, 0);
				}
				foreach (var (name, state, ok) in stats.Denoisers) {
					denoisers.Add(name, state, -1.0, false, ok ? 0 : 1);
				}
				foreach (var (name, state) in stats.Passes) {
					passes.Add(name, state);
				}
			}

			// what the F8 cards say under their settings
			var eng = new Section("engine", "Engine");
			eng.Try("graphics API", () => GraphicsApiSwitcher.Current);
			eng.Try("engine", () => NativeEngine.Info);
			eng.Try("self test", () => NativeEngine.TestResult);
			eng.Try("scene", () => NativeEngine.SceneStatus);
			eng.Try("check", () => NativeEngine.CheckStatus);
			eng.Try("queue", () => NativeEngine.QueueStatus);
			eng.Try("sun counters", () => NativeEngine.SunCounters);
			eng.Try("engine shaders", () => NativeEngine.ShaderStatus);
			eng.Try("NRD", () => NativeEngine.NrdStatus);
			eng.Try("DXR 1.2 sun", () => NativeEngine.Dxr12SunStatus);
			eng.Try("DXR 1.2 probe", () => NativeEngine.Dxr12Result);
			eng.Try("shader bundle", () => $"{mod.Bundle.Shaders.Count} shaders, {mod.Replacer.ReplacedCount} materials replaced");
			eng.Try("debug page", () => Status);

			var sky = new Section("sky", "Sky, fog, clouds");
			sky.Try("sky", () => rt.SkyStatus);
			sky.Try("HDRP sky", () => mod.Volume.SkyState);
			sky.Try("cube for HDRP", () => AtmospherePass.CubeStatus);
			sky.Try("sky light", () => AtmospherePass.ProbeStatus);
			sky.Try("track reflection probes", () => mod.TrackProbes.Count > 0 ? $"{mod.TrackProbes.Count} off" : "none off");
			sky.Try("fog", () => rt.FogStatus);
			sky.Try("clouds", () => mod.Clouds.State);
			sky.Try("night", () => mod.Night.State);
			sky.Try("game day / night", () => mod.GameNight.Status);
			sky.Try("volumetric fog (HDRP)", () => mod.CameraFog.State);
			sky.Try("volumetric clouds (HDRP)", () => mod.CameraClouds.State);

			var light = new Section("lighting", "Shadows, reflections, post");
			light.Try("shadows", () => rt.ShadowStatus);
			light.Try("reflections", () => rt.ReflectionStatus);
			light.Try("glass", () => rt.GlassStatus);
			light.Try("transparent surfaces", () => SunShadowMap.Status);
			light.Try("painted shadows", () => mod.PaintedShadows.Status);
			light.Try("post-processing", () => mod.Post.Status);
			light.Try("DLSS", () => Dlaa.Status);
			light.Try("headlights", () => mod.Headlights.Status);
			light.Try("car finish", () => mod.Finish.Status);

			var world = new Section("world", "Weather, cars, map");
			world.Try("weather", () => {
				var w = NatureFX.Nature.Weather.Current;
				return $"rain {w.Rain:P0}, wet {w.Wetness:P0}, snow {w.Snow:P0}, leaves {w.Leaves:P0}, clouds {w.Cloud:P0}";
			});
			world.Try("season", () => NatureFX.Map.Seasons.Describe());
			world.Try("rain and snow", () => mod.Rain.State);
			world.Try("build up", () => {
				var c = mod.Accumulation.Current;
				return $"snow {c.x:P0}, leaves {c.y:P0}, dust {c.z:P0}, wet {c.w:P0}";
			});
			world.Try("tyre smoke", () => $"{mod.Smoke.Particles} particles ({mod.Smoke.ShadowedParticles} in shadow), hottest tread {mod.Smoke.HottestTyre:0} C");
			world.Try("car dirt", () => $"mud {mod.CarRainFx.Mud:P0}, dust {mod.CarRainFx.Dust:P0}, snow {mod.CarRainFx.Snow:P0}");
			world.Try("game particles", () => $"{mod.GameFx.Count} drawn by NatureFX, {mod.GameFx.Skipped} kept");
			world.Try("map", () => mod.Map.Loaded ? $"{mod.Map.Name} ({mod.Map.Source})" : "no profile");
			world.Try("map values", () => mod.MapWeather.Describe());
			world.Try("ground map", () => GroundMap.Status);
			world.Try("painted dirt", () => mod.Paint.Status);
			world.Try("debris", () => mod.Debris.Status);
			world.Try("decals, water", () => $"decals {mod.Decals.Count}, puddles {mod.Water.PuddleCount} (water {mod.Water.Level:P0}), water surfaces {mod.Water.SurfaceCount}");

			// the "rest": the game's textures and meshes, HDRP's features
			var gameTex = new Section("game-textures", "Game textures (GPU copies; mip streamed ones as loaded)");
			gameTex.Add($"all ({gameTextureCount_})", Mb(gameTextures_), gameTextures_);
			gameTex.Add("of them readable (a CPU copy too)", Mb(gameTexturesReadable_), gameTexturesReadable_, true);
			gameTex.Add($"of them mip streamed ({gameTextureStreamedCount_})", Mb(gameTexturesStreamed_), gameTexturesStreamed_, true);
			gameTex.Try("Unity's streaming", () => TextureBudget.Status);
			gameTex.Try("own sky: out of video memory", () => VolumeOverhaul.UnloadedSky);
			gameTex.Add("the largest is used by", biggestUsers_);
			foreach (var (format, bytes, count) in gameTextureFormats_) {
				gameTex.Add($"format {format} ({count})", Mb(bytes), bytes, true);
			}
			foreach (var (name, bytes) in bigGameTextures_) {
				gameTex.Bytes(name, bytes, true);
			}
			var meshes = new Section("meshes", "Meshes (vertex and index buffers)");
			meshes.Add($"all ({meshCount_})", Mb(meshes_), meshes_);
			meshes.Add($"made for the ray tracing scene only, NFX_RT_* ({rtMeshCount_})", Mb(rtMeshes_), rtMeshes_, true);
			meshes.Add($"NatureFX's own (grass, forest, debris) ({natureMeshCount_})", Mb(natureMeshes_), natureMeshes_, true);
			foreach (var (name, bytes) in bigMeshes_) {
				meshes.Bytes(name, bytes, true);
			}
			var hdrp = new Section("hdrp", "HDRP features now (the game camera)");
			HdrpState(hdrp);

			sections.Add(perf);
			sections.Add(split);
			sections.Add(timings);
			sections.Add(engine);
			sections.Add(buffers);
			sections.Add(targets);
			sections.Add(others);
			sections.Add(gameTex);
			sections.Add(meshes);
			sections.Add(hdrp);
			sections.Add(scene);
			sections.Add(features);
			sections.Add(denoisers);
			sections.Add(passes);
			sections.Add(eng);
			sections.Add(sky);
			sections.Add(light);
			sections.Add(world);

			var sb = new StringBuilder(16384);
			sb.Append('{');
			sb.Append("\"time\":").Append(Time.unscaledTime.ToString("0.0", inv));
			sb.Append(",\"fps\":").Append(fps_.ToString("0.0", inv));
			sb.Append(",\"gpuUsedMB\":").Append(used).Append(",\"gpuBudgetMB\":").Append(budget);
			sb.Append(",\"oursMB\":").Append((ours / 1048576.0).ToString("0.0", inv));
			AppendMarks(sb, inv);
			sb.Append(",\"sections\":[");
			for (int i = 0; i < sections.Count; ++i) {
				var sec = sections[i];
				sb.Append(i > 0 ? "," : "").Append("{\"id\":").Append(Json(sec.Id)).Append(",\"title\":").Append(Json(sec.Title)).Append(",\"rows\":[");
				for (int k = 0; k < sec.Rows.Count; ++k) {
					var row = sec.Rows[k];
					sb.Append(k > 0 ? "," : "").Append('[').Append(Json(row.Label)).Append(',').Append(Json(row.Value)).Append(',')
					  .Append(row.Bar.ToString("0.###", inv)).Append(',').Append(row.Indent ? 1 : 0).Append(',').Append(row.Level).Append(']');
				}
				sb.Append("]}");
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private static string Json(string s) {
			var sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			foreach (char c in s) {
				switch (c) {
					case '"': sb.Append("\\\""); break;
					case '\\': sb.Append("\\\\"); break;
					case '\n': sb.Append("\\n"); break;
					case '\r': break;
					case '\t': sb.Append("\\t"); break;
					default:
						if (c < 0x20) {
							sb.Append("\\u").Append(((int)c).ToString("x4"));
						} else {
							sb.Append(c);
						}
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}
	}
}

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
		public static void Update(bool on, int port) {
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

		private static void CountTextures() {
			var groups = new Dictionary<string, (long Bytes, int Count)>();
			var big = new List<(string, long)>();
			long other = 0;
			int otherCount = 0;
			foreach (var t in Resources.FindObjectsOfTypeAll<Texture>()) {
				if (t == null) {
					continue;
				}
				if (t is RenderTexture rt && !rt.IsCreated()) {
					continue;
				}
				string name = t.name ?? "";
				bool ours = name.StartsWith("NFX_", StringComparison.Ordinal) || name.StartsWith("NatureFX", StringComparison.Ordinal);
				if (!ours && !(t is RenderTexture)) {
					continue; // the game's own textures (materials): not what this page is about
				}
				long bytes = TextureBytes(t);
				if (ours) {
					string g = Group(name);
					groups.TryGetValue(g, out var v);
					groups[g] = (v.Bytes + bytes, v.Count + 1);
				} else if (t is RenderTexture) {
					other += bytes;
					++otherCount;
					if (bytes >= 16L << 20) {
						big.Add(($"{name} ({t.width}x{t.height} {t.graphicsFormat})", bytes));
					}
				}
			}
			var list = new List<(string, long, int)>();
			foreach (var pair in groups) {
				list.Add((pair.Key, pair.Value.Bytes, pair.Value.Count));
			}
			list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			big.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			nfxTargets_ = list;
			bigTargets_ = big.Count > 20 ? big.GetRange(0, 20) : big;
			otherTargets_ = other;
			otherTargetCount_ = otherCount;
		}

		private static string Snapshot() {
			var sb = new StringBuilder(8192);
			var inv = CultureInfo.InvariantCulture;
			sb.Append('{');
			sb.Append("\"time\":").Append(Time.unscaledTime.ToString("0.0", inv));
			sb.Append(",\"fps\":").Append(fps_.ToString("0.0", inv));
			var cam = Camera.main;
			sb.Append(",\"resolution\":").Append(Json(cam != null ? $"{cam.pixelWidth}x{cam.pixelHeight}" : "-"));
			NativeEngine.GpuMemoryMB(out ulong used, out ulong budget);
			sb.Append(",\"gpuUsedMB\":").Append(used).Append(",\"gpuBudgetMB\":").Append(budget);

			// the engine's own memory
			sb.Append(",\"native\":[");
			var native = NativeEngine.NativeMemory();
			if (native != null) {
				for (int i = 0; i < native.Length; ++i) {
					sb.Append(i > 0 ? "," : "").Append("{\"name\":").Append(Json(NativeEngine.NativeMemoryNames[i])).Append(",\"bytes\":").Append(native[i]).Append('}');
				}
			}
			sb.Append(']');
			sb.Append(",\"nativeNote\":").Append(Json(native == null ? "this NFXNative.dll does not report its memory (an older build)" : "DLSS's own memory (NGX) is not counted"));

			// the trace buffers
			sb.Append(",\"buffers\":[");
			bool first = true;
			foreach (var (name, bytes) in NativeEngine.TraceMemory()) {
				sb.Append(first ? "" : ",").Append("{\"name\":").Append(Json(name)).Append(",\"bytes\":").Append(bytes).Append('}');
				first = false;
			}
			sb.Append(']');

			// render targets and textures
			sb.Append(",\"targets\":[");
			first = true;
			foreach (var (group, bytes, count) in nfxTargets_) {
				sb.Append(first ? "" : ",").Append("{\"name\":").Append(Json(group)).Append(",\"bytes\":").Append(bytes).Append(",\"count\":").Append(count).Append('}');
				first = false;
			}
			sb.Append(']');
			sb.Append(",\"otherTargets\":{\"bytes\":").Append(otherTargets_).Append(",\"count\":").Append(otherTargetCount_).Append('}');
			sb.Append(",\"bigOtherTargets\":[");
			first = true;
			foreach (var (name, bytes) in bigTargets_) {
				sb.Append(first ? "" : ",").Append("{\"name\":").Append(Json(name)).Append(",\"bytes\":").Append(bytes).Append('}');
				first = false;
			}
			sb.Append(']');

			// timings
			sb.Append(",\"timings\":[");
			var readings = RTTimings.Readings(out float total);
			for (int i = 0; i < readings.Count; ++i) {
				sb.Append(i > 0 ? "," : "").Append("{\"name\":").Append(Json(readings[i].Label.Trim())).Append(",\"ms\":")
				  .Append(readings[i].Ms.ToString("0.000", inv)).Append(",\"part\":").Append(readings[i].Part ? "true" : "false").Append('}');
			}
			sb.Append(']');
			sb.Append(",\"timingTotal\":").Append(total.ToString("0.000", inv));

			// status lines
			sb.Append(",\"status\":[");
			AppendStatus(sb, "engine", NativeEngine.Info, true);
			AppendStatus(sb, "scene", NativeEngine.SceneStatus, false);
			AppendStatus(sb, "NRD", NativeEngine.NrdStatus, false);
			AppendStatus(sb, "DXR 1.2 sun", NativeEngine.Dxr12SunStatus, false);
			AppendStatus(sb, "fog", AtmospherePass.FogStatus, false);
			AppendStatus(sb, "sky", AtmospherePass.SkyStatus, false);
			sb.Append(']');
			sb.Append('}');
			return sb.ToString();
		}

		private static void AppendStatus(StringBuilder sb, string name, string value, bool first) {
			sb.Append(first ? "" : ",").Append("{\"name\":").Append(Json(name)).Append(",\"value\":").Append(Json(value ?? "")).Append('}');
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

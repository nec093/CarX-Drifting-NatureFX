using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NatureFX.Tools {
	// The CPU's load for the debug page, read once a second while it is open (Windows' own counters, no profiler):
	// the whole machine's use, and each of the game's threads by its share of one core with the name the game gave it
	// (Unity names its threads: the main thread, the render thread, the job workers, the loading threads). A thread
	// near 100 % of a core holds the frame back however idle the rest of the CPU is.
	public static class CpuStats {
		[StructLayout(LayoutKind.Sequential)]
		private struct FileTime {
			public uint Low, High;
			public ulong Value => ((ulong)High << 32) | Low;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ThreadEntry32 {
			public uint Size, Usage, ThreadId, OwnerProcessId;
			public int BasePriority, DeltaPriority;
			public uint Flags;
		}

		private const uint SnapThread = 0x00000004;
		private const uint ThreadQueryLimited = 0x0800;

		[DllImport("kernel32")] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
		[DllImport("kernel32")] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
		[DllImport("kernel32")] private static extern bool Thread32First(IntPtr snapshot, ref ThreadEntry32 entry);
		[DllImport("kernel32")] private static extern bool Thread32Next(IntPtr snapshot, ref ThreadEntry32 entry);
		[DllImport("kernel32")] private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
		[DllImport("kernel32")] private static extern bool GetThreadTimes(IntPtr thread, out FileTime creation, out FileTime exit, out FileTime kernel, out FileTime user);
		[DllImport("kernel32")] private static extern bool CloseHandle(IntPtr handle);
		[DllImport("kernel32")] private static extern uint GetCurrentProcessId();
		[DllImport("kernel32")] private static extern uint GetCurrentThreadId();
		[DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern int GetThreadDescription(IntPtr thread, out IntPtr description);
		[DllImport("kernel32")] private static extern IntPtr LocalFree(IntPtr memory);

		public static string Problem { get; private set; } = "";
		public static float SystemPercent { get; private set; } = -1.0f;
		public static int ThreadCount { get; private set; }
		// the busiest threads: name, % of one core
		public static readonly List<(string Name, float Percent, bool Main)> Busiest = new();
		public static float MainPercent { get; private set; } = -1.0f;

		private static ulong lastIdle_, lastSystemBusy_;
		private static DateTime lastRead_;
		private static readonly Dictionary<uint, ulong> lastThreadTime_ = new();
		private static readonly Dictionary<uint, string> names_ = new();
		private static readonly List<(string, float, bool)> all_ = new();
		private static bool failed_;

		// once a second on the main thread (the debug page's snapshot)
		public static void Read() {
			if (failed_) {
				return;
			}
			try {
				ReadSystem();
				ReadThreads();
			} catch (Exception e) {
				failed_ = true;
				Problem = $"{e.GetType().Name}: {e.Message}";
			}
		}

		private static void ReadSystem() {
			if (!GetSystemTimes(out var idle, out var kernel, out var user)) {
				return;
			}
			// kernel time includes idle time
			ulong busy = kernel.Value + user.Value;
			if (lastSystemBusy_ != 0) {
				ulong total = busy - lastSystemBusy_;
				ulong idleDelta = idle.Value - lastIdle_;
				SystemPercent = total > 0 ? 100.0f * (1.0f - (float)idleDelta / total) : 0.0f;
			}
			lastSystemBusy_ = busy;
			lastIdle_ = idle.Value;
		}

		private static void ReadThreads() {
			var now = DateTime.UtcNow;
			double wall100ns = lastRead_ != default ? (now - lastRead_).Ticks : 0.0; // DateTime ticks are 100 ns, as FILETIME
			lastRead_ = now;
			uint pid = GetCurrentProcessId();
			uint main = GetCurrentThreadId();
			IntPtr snap = CreateToolhelp32Snapshot(SnapThread, 0);
			if (snap == IntPtr.Zero || snap == new IntPtr(-1)) {
				Problem = "no thread snapshot";
				return;
			}
			all_.Clear();
			var seen = new HashSet<uint>();
			try {
				var entry = new ThreadEntry32 { Size = (uint)Marshal.SizeOf<ThreadEntry32>() };
				for (bool ok = Thread32First(snap, ref entry); ok; ok = Thread32Next(snap, ref entry)) {
					if (entry.OwnerProcessId != pid) {
						continue;
					}
					uint id = entry.ThreadId;
					IntPtr h = OpenThread(ThreadQueryLimited, false, id);
					if (h == IntPtr.Zero) {
						continue;
					}
					try {
						if (!GetThreadTimes(h, out _, out _, out var k, out var u)) {
							continue;
						}
						seen.Add(id);
						ulong t = k.Value + u.Value;
						if (!names_.ContainsKey(id)) {
							names_[id] = Describe(h, id, id == main);
						}
						if (wall100ns > 0.0 && lastThreadTime_.TryGetValue(id, out ulong before) && t >= before) {
							float percent = (float)((t - before) / wall100ns * 100.0);
							all_.Add((names_[id], percent, id == main));
							if (id == main) {
								MainPercent = percent;
							}
						}
						lastThreadTime_[id] = t;
					} finally {
						CloseHandle(h);
					}
				}
			} finally {
				CloseHandle(snap);
			}
			// threads that ended
			var gone = new List<uint>();
			foreach (var id in lastThreadTime_.Keys) {
				if (!seen.Contains(id)) {
					gone.Add(id);
				}
			}
			foreach (var id in gone) {
				lastThreadTime_.Remove(id);
				names_.Remove(id);
			}
			ThreadCount = seen.Count;
			all_.Sort((a, b) => b.Item2.CompareTo(a.Item2));
			Busiest.Clear();
			for (int i = 0; i < all_.Count && i < 12; ++i) {
				if (all_[i].Item2 < 0.5f && !all_[i].Item3) {
					break;
				}
				Busiest.Add(all_[i]);
			}
		}

		// the name the game gave the thread (Windows 10 1607 and later), else its id
		private static string Describe(IntPtr thread, uint id, bool main) {
			string name = null;
			try {
				if (GetThreadDescription(thread, out IntPtr text) >= 0 && text != IntPtr.Zero) {
					name = Marshal.PtrToStringUni(text);
					LocalFree(text);
				}
			} catch (EntryPointNotFoundException) {
				// an older Windows: no names
			}
			if (string.IsNullOrEmpty(name)) {
				name = $"thread {id}";
			}
			return main ? $"{name} (main thread)" : name;
		}

		public static string CpuName => $"{SystemInfo.processorType.Trim()}, {SystemInfo.processorCount} logical CPUs, {SystemInfo.processorFrequency} MHz";
	}
}

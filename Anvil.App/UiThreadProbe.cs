using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;
using Anvil.Services;

namespace Anvil
{
	/// <summary>
	/// DEV-ONLY UI-thread heartbeat (<c>perf.ui</c>). Answers the question <c>perf.pan</c> cannot: the
	/// page kept drawing at full cadence while a heavy loop loaded (rAF p95 ≈ 34 ms), yet pan/zoom felt
	/// laggy. In WinUI 3 the WebView2 is composition-hosted, so mouse input reaches it THROUGH this
	/// thread — a busy or CPU-starved UI thread delays every drag while the page's frames stay perfect.
	/// </summary>
	/// <remarks>
	/// <code>
	///   UI thread:  tick ─50ms─ tick ─50ms─ tick ────── 180ms ────── tick     lateness = gap − 50
	///   pool timer (1 s): read lateness + message cost + CPU per process → one perf.ui line
	///
	///   lateness HIGH + msgMs/syncMs HIGH  → our own UI-thread work (cause 2)
	///   lateness HIGH + both LOW, cpu HIGH → CPU starvation (cause 1)
	///   lateness LOW                       → input is not delayed here; look in the renderer
	/// </code>
	/// ⚠️ IT MUST NOT PERTURB WHAT IT MEASURES: the UI thread does one timestamp per tick and one per
	/// routed message; process CPU and the JSONL write happen on the pool timer. The WebView2 process LIST
	/// is a COM call, so it is refreshed on the UI thread only every 5 s.
	/// ⚠️ Quiet when idle: a window is written only when something happened (late tick, message cost,
	/// radar CPU jobs), else every 10th window as a baseline.
	/// ⚠️ EXCISABLE: this file, its <c>#if DEBUG</c> start in MainWindow, the router's NoteMessage call and
	/// <see cref="RadarPerfCounters"/>. Grep <c>perf.ui</c>.
	/// </remarks>
	internal sealed class UiThreadProbe
	{
		private const int TickMs = 50;
		private const int WindowMs = 1000;
		private const int BaselineEvery = 10;

		private static UiThreadProbe? _instance;

		private readonly object _gate = new();
		private readonly Stopwatch _clock = Stopwatch.StartNew();
		private readonly DispatcherQueueTimer _tick;
		private readonly Timer _window;
		private readonly CoreWebView2 _web;
		private readonly int _cores = Environment.ProcessorCount;

		// Window accumulators (UI thread writes, pool timer reads + resets) — all under _gate.
		private double _lastTickMs;
		private int _ticks, _late50, _late100;
		private double _lateMax;
		private int _msgs;
		private double _msgMs, _msgMax;
		private string? _msgMaxType;

		// WebView2 processes: pid → kind, refreshed on the UI thread; Process handles kept by pid.
		private Dictionary<int, CoreWebView2ProcessKind> _webPids = new();
		private readonly Dictionary<int, Process> _procs = new();
		private readonly Dictionary<int, TimeSpan> _lastCpu = new();
		private double _lastWall;
		private long _lastSysIdle, _lastSysTotal;
		private int _windowNo;
		private int _pidRefreshTicks;

		private UiThreadProbe(CoreWebView2 web)
		{
			_web = web;
			RefreshWebPids();
			_lastWall = _clock.Elapsed.TotalMilliseconds;
			SampleCpu(_lastWall); // prime the per-process baselines

			_tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
			_tick.Interval = TimeSpan.FromMilliseconds(TickMs);
			_tick.IsRepeating = true;
			_tick.Tick += OnTick;
			_lastTickMs = _clock.Elapsed.TotalMilliseconds;
			_tick.Start();

			_window = new Timer(_ => EmitWindow(), null, WindowMs, WindowMs);
		}

		/// <summary>Start the probe. Call ON THE UI THREAD once the CoreWebView2 exists.</summary>
		public static void Start(CoreWebView2 web) => _instance ??= new UiThreadProbe(web);

		/// <summary>One routed page message handled on the UI thread, and how long its handler took.</summary>
		public static void NoteMessage(string type, double ms)
		{
			var p = _instance;
			if (p is null) return;
			lock (p._gate)
			{
				p._msgs++;
				p._msgMs += ms;
				if (ms > p._msgMax) { p._msgMax = ms; p._msgMaxType = type; }
			}
		}

		private void OnTick(DispatcherQueueTimer sender, object args)
		{
			var now = _clock.Elapsed.TotalMilliseconds;
			var late = Math.Max(0, now - _lastTickMs - TickMs);
			_lastTickMs = now;
			lock (_gate)
			{
				_ticks++;
				if (late > _lateMax) _lateMax = late;
				if (late > 50) _late50++;
				if (late > 100) _late100++;
			}
			if (++_pidRefreshTicks >= 5000 / TickMs) { _pidRefreshTicks = 0; RefreshWebPids(); }
		}

		private void RefreshWebPids()
		{
			try
			{
				var next = new Dictionary<int, CoreWebView2ProcessKind>();
				foreach (var info in _web.Environment.GetProcessInfos()) next[info.ProcessId] = info.Kind;
				Volatile.Write(ref _webPids, next);
			}
			catch { /* a measurement must never break the app */ }
		}

		private int _emitting; // pool-timer callbacks can overlap if one stalls; the CPU dictionaries are not shared-safe

		private void EmitWindow()
		{
			if (Interlocked.Exchange(ref _emitting, 1) == 1) return;
			try
			{
				var now = _clock.Elapsed.TotalMilliseconds;
				int ticks, late50, late100, msgs;
				double lateMax, msgMs, msgMax;
				string? msgMaxType;
				lock (_gate)
				{
					// A tick that is overdue RIGHT NOW (the UI thread is stuck) has not reported yet — count it.
					var pending = Math.Max(0, now - _lastTickMs - TickMs);
					ticks = _ticks; late50 = _late50; late100 = _late100; msgs = _msgs;
					lateMax = Math.Max(_lateMax, pending); msgMs = _msgMs; msgMax = _msgMax; msgMaxType = _msgMaxType;
					_ticks = _late50 = _late100 = _msgs = 0;
					_lateMax = _msgMs = _msgMax = 0;
					_msgMaxType = null;
				}
				var (jobs, jobsPeak) = RadarPerfCounters.TakeJobs();
				var syncMs = RadarPerfCounters.TakeSyncMs();
				var cpu = SampleCpu(now);

				var busy = lateMax >= 40 || msgMs >= 20 || syncMs >= 5 || jobsPeak > 0;
				if (!busy && ++_windowNo % BaselineEvery != 0) return;

				static double R(double v) => Math.Round(v, 1);
				RadarDiagnostics.Log("ui", "perf.ui",
					("ticks", ticks), ("lateMax", R(lateMax)), ("late50", late50), ("late100", late100),
					("msgs", msgs), ("msgMs", R(msgMs)), ("msgMax", R(msgMax)), ("msgMaxType", msgMaxType),
					("syncMs", R(syncMs)), ("jobs", jobs), ("jobsPeak", jobsPeak),
					("cpuApp", R(cpu.App)), ("cpuRend", R(cpu.Renderer)), ("cpuGpu", R(cpu.Gpu)),
					("cpuWebOther", R(cpu.WebOther)), ("cpuSys", R(cpu.System)), ("cores", _cores));
			}
			catch { /* a measurement must never break the app */ }
			finally { Volatile.Write(ref _emitting, 0); }
		}

		// CPU since the previous sample, as % of the WHOLE machine (100 = every core busy) — so the process
		// columns and cpuSys add up on the same scale.
		private (double App, double Renderer, double Gpu, double WebOther, double System) SampleCpu(double nowMs)
		{
			var wallMs = Math.Max(1, nowMs - _lastWall);
			_lastWall = nowMs;
			var scale = 100.0 / (wallMs * _cores);

			double app = 0, rend = 0, gpu = 0, other = 0;
			var pids = Volatile.Read(ref _webPids);
			var self = Environment.ProcessId;
			foreach (var pid in Concat(self, pids.Keys))
			{
				var delta = CpuDelta(pid);
				if (pid == self) { app = delta * scale; continue; }
				switch (pids[pid])
				{
					case CoreWebView2ProcessKind.Renderer: rend += delta * scale; break;
					case CoreWebView2ProcessKind.Gpu: gpu += delta * scale; break;
					default: other += delta * scale; break;
				}
			}
			// Forget processes that have gone.
			foreach (var gone in new List<int>(_procs.Keys))
			{
				if (gone != self && !pids.ContainsKey(gone)) { _procs[gone].Dispose(); _procs.Remove(gone); _lastCpu.Remove(gone); }
			}

			double sys = 0;
			if (GetSystemTimes(out var idle, out var kernel, out var user))
			{
				long total = kernel + user; // kernel time INCLUDES idle
				long dTotal = total - _lastSysTotal, dIdle = idle - _lastSysIdle;
				if (_lastSysTotal != 0 && dTotal > 0) sys = 100.0 * (dTotal - dIdle) / dTotal;
				_lastSysTotal = total; _lastSysIdle = idle;
			}
			return (app, rend, gpu, other, sys);
		}

		private static IEnumerable<int> Concat(int first, IEnumerable<int> rest)
		{
			yield return first;
			foreach (var r in rest) if (r != first) yield return r;
		}

		// ms of CPU this process used since we last asked (0 on the first sighting).
		private double CpuDelta(int pid)
		{
			try
			{
				if (!_procs.TryGetValue(pid, out var p)) { p = Process.GetProcessById(pid); _procs[pid] = p; }
				p.Refresh();
				var t = p.TotalProcessorTime;
				var had = _lastCpu.TryGetValue(pid, out var prev);
				_lastCpu[pid] = t;
				return had ? (t - prev).TotalMilliseconds : 0;
			}
			catch { return 0; } // exited, or access denied
		}

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
	}
}

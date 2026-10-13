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
	///   svcHops = download chunks that resumed ON this thread (Level2RadarService.ReadBodyAsync)
	///   readouts / readoutMs = radar readout refreshes and their cost, bindings included (RaiseRadarReadout)
	///   feeds / feedMs = overlay-feed refreshes (warnings, …) and their parse/merge time ON this thread;
	///   feedBy = the same per feed, "name:refreshes/ms" costliest first (outlookRead = the outlook VMs' cache reads)
	///   msgBy = page-message handling per TYPE, "type:count/ms" costliest first
	///   scrubs / scrubMs = dot-matrix scrubber paints and their cost; scrubBy = per kind "paint|lit|relay:count/ms"
	///   gz* = legacy .gz volumes streamed, completeness checks, their gunzip / tilt-walk CPU (pool threads, NOT this
	///         thread — logged here only because this is the one per-second line) and the download's wait on them
	///   diag* = RadarDiagnostics cost: radarLog lines, their debugger echo, Gate wait (+ worst) and hold; report rebuilds,
	///         render ms under the Gate (+ worst), .md write ms, and the run-wide sample lists' length
	///   gc* = .NET GC (THIS process, not the page's JS): collections per gen, total pause ms (a blocking GC stops the UI
	///         thread too), the latest GC's gen / pause / concurrent / compacted, managed heap MB
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
		// Per message TYPE: count + ms (→ "msgBy", costliest first) — which page message the UI time went to.
		private readonly Dictionary<string, (int N, double Ms)> _msgBy = new(StringComparer.Ordinal);

		// WebView2 processes: pid → kind, refreshed on the UI thread; Process handles kept by pid.
		private Dictionary<int, CoreWebView2ProcessKind> _webPids = new();
		private readonly Dictionary<int, Process> _procs = new();
		private readonly Dictionary<int, TimeSpan> _lastCpu = new();
		private double _lastWall;
		private long _lastSysIdle, _lastSysTotal;
		private int _windowNo;
		private int _pidRefreshTicks;
		// .NET GC baselines as of the last WRITTEN window (pool timer only) — a quiet window's collections carry forward.
		private int _gc0 = GC.CollectionCount(0), _gc1 = GC.CollectionCount(1), _gc2 = GC.CollectionCount(2);
		private TimeSpan _gcPause = GC.GetTotalPauseDuration();
		private long _gcLastIndex = GC.GetGCMemoryInfo(GCKind.Any).Index;

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
				p._msgBy.TryGetValue(type, out var c);
				p._msgBy[type] = (c.N + 1, c.Ms + ms);
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
				string? msgBy = null;
				lock (_gate)
				{
					if (_msgBy.Count > 0)
					{
						var parts = new List<(string Type, int N, double Ms)>();
						foreach (var (t, c) in _msgBy) parts.Add((t, c.N, c.Ms));
						parts.Sort((a, b) => b.Ms.CompareTo(a.Ms));
						msgBy = string.Join(" ", parts.ConvertAll(x =>
							string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{x.Type}:{x.N}/{x.Ms:0.0}")));
						_msgBy.Clear();
					}
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
				var svcHops = RadarPerfCounters.TakeHops();
				var (readouts, readoutMs) = RadarPerfCounters.TakeReadouts();
				var (feeds, feedMs, feedBy) = RadarPerfCounters.TakeFeeds();
				var (scrubs, scrubMs, scrubBy) = RadarPerfCounters.TakeScrubs();
				var gz = RadarPerfCounters.TakeGz();
				var cpu = SampleCpu(now);

				// .NET GC since the last written window: collections per generation, total pause (EVERY thread stops in a
				// blocking GC — the UI thread included), and the latest GC's shape. Lead: the 1–3 s stall at a load's end.
				int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
				var gcPause = GC.GetTotalPauseDuration();
				var gcPauseMs = (gcPause - _gcPause).TotalMilliseconds;
				var lastGc = GC.GetGCMemoryInfo(GCKind.Any);
				var gcNew = lastGc.Index != _gcLastIndex;

				// A loop that is up refreshes its readout every second, so every such second is logged (playback included).
				// A feed refresh is logged too, so a fix that takes its cost to ~0 still shows the refresh it measured.
				var busy = lateMax >= 40 || msgMs >= 20 || syncMs >= 5 || jobsPeak > 0 || svcHops > 0 || readouts > 0 || feedBy is not null || scrubs > 0 || gz.Volumes > 0 || gz.Checks > 0
					|| gc2 > _gc2 || gcPauseMs >= 20;
				if (!busy && ++_windowNo % BaselineEvery != 0) return;
				int dGc0 = gc0 - _gc0, dGc1 = gc1 - _gc1, dGc2 = gc2 - _gc2;
				_gc0 = gc0; _gc1 = gc1; _gc2 = gc2; _gcPause = gcPause; _gcLastIndex = lastGc.Index;
				double lastPauseMs = 0;
				if (gcNew) foreach (var p in lastGc.PauseDurations) lastPauseMs += p.TotalMilliseconds;
				// Taken only for a window that is WRITTEN, so a quiet window's report rebuild carries into the next line. Not part
				// of `busy`: this line's own write dirties the report, so a rebuild would keep every window busy forever.
				var diag = RadarPerfCounters.TakeDiag();

				static double R(double v) => Math.Round(v, 1);
				RadarDiagnostics.Log("ui", "perf.ui",
					("ticks", ticks), ("lateMax", R(lateMax)), ("late50", late50), ("late100", late100),
					("msgs", msgs), ("msgMs", R(msgMs)), ("msgMax", R(msgMax)), ("msgMaxType", msgMaxType), ("msgBy", msgBy),
					("syncMs", R(syncMs)), ("svcHops", svcHops), ("readouts", readouts), ("readoutMs", R(readoutMs)),
					("feeds", feeds), ("feedMs", R(feedMs)), ("feedBy", feedBy),
					("scrubs", scrubs), ("scrubMs", R(scrubMs)), ("scrubBy", scrubBy),
					("gzVols", gz.Volumes), ("gzChecks", gz.Checks), ("gzInflateMs", R(gz.InflateMs)), ("gzWalkMs", R(gz.WalkMs)),
					("gzWaitMs", R(gz.WaitMs)),
					("diagLogs", diag.Logs), ("diagEchoMs", R(diag.EchoMs)), ("diagWaitMs", R(diag.WaitMs)),
					("diagWaitMax", R(diag.WaitMax)), ("diagHeldMs", R(diag.HeldMs)), ("diagReports", diag.Reports),
					("diagRenderMs", R(diag.RenderMs)), ("diagRenderMax", R(diag.RenderMax)), ("diagWriteMs", R(diag.WriteMs)),
					("diagLists", diag.Lists),
					("gc0", dGc0), ("gc1", dGc1), ("gc2", dGc2), ("gcPauseMs", R(gcPauseMs)),
					("gcLastGen", gcNew ? lastGc.Generation : null), ("gcLastPauseMs", gcNew ? R(lastPauseMs) : null),
					("gcLastConc", gcNew ? lastGc.Concurrent : null), ("gcLastCompact", gcNew ? lastGc.Compacted : null),
					("gcHeapMb", R(lastGc.HeapSizeBytes / 1048576.0)),
					("jobs", jobs), ("jobsPeak", jobsPeak),
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

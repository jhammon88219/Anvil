using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// PERF PROBE (perf.ui) — two counters the UI-thread probe reads once a second to say WHY the UI thread
	/// was late while a loop loaded: how much CPU-bound radar work was running in the background, and how
	/// much of the service's work ran synchronously ON the caller's (UI) context.
	/// </summary>
	/// <remarks>
	/// <code>
	///   RadarCpuWork job  jobs ▲ ─── work on a radar CPU thread ─── jobs ▼ → "jobs" / "jobsPeak"
	///   BeginSync … EndSync  (only timed when a SynchronizationContext is
	///                         present, i.e. the await resumed on the UI)  → "syncMs"
	/// </code>
	/// Always on (a pair of Interlocked ops per extraction is free); only <c>UiThreadProbe</c> (App, Debug)
	/// ever reads it. ⚠️ EXCISABLE: this file, its call sites in <see cref="Level2RadarService"/> and
	/// <see cref="RadarCpuWork"/> (grep <c>RadarPerfCounters</c>) and the App's <c>UiThreadProbe</c> are the whole
	/// feature. Grep <c>perf.ui</c>.
	/// </remarks>
	public static class RadarPerfCounters
	{
		private static int _jobs, _jobsPeak;
		private static long _syncTicks;

		/// <summary>A <see cref="RadarCpuWork"/> job began running. Returns a token for <see cref="JobEnded"/>.</summary>
		public static int JobStarted()
		{
			var n = Interlocked.Increment(ref _jobs);
			int peak;
			while (n > (peak = Volatile.Read(ref _jobsPeak)) && Interlocked.CompareExchange(ref _jobsPeak, n, peak) != peak) { }
			return n;
		}

		public static void JobEnded(int started) => Interlocked.Decrement(ref _jobs);

		/// <summary>Jobs running now, and the peak since the last call (the peak is reset to "now").</summary>
		public static (int Now, int Peak) TakeJobs()
		{
			var now = Volatile.Read(ref _jobs);
			return (now, Math.Max(now, Interlocked.Exchange(ref _jobsPeak, now)));
		}

		/// <summary>Start timing a synchronous stretch. Returns 0 (= not timed) off a sync context, so only
		/// work that actually landed on the UI thread is counted.</summary>
		public static long BeginSync() => SynchronizationContext.Current is null ? 0 : Stopwatch.GetTimestamp();

		public static void EndSync(long started)
		{
			if (started != 0) Interlocked.Add(ref _syncTicks, Stopwatch.GetTimestamp() - started);
		}

		/// <summary>Milliseconds of timed synchronous work since the last call.</summary>
		public static double TakeSyncMs() => Interlocked.Exchange(ref _syncTicks, 0) * 1000.0 / Stopwatch.Frequency;
	}
}

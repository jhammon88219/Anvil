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
	///   NoteHop              (a download chunk resumed on the UI)        → "svcHops"
	///   Begin/EndReadout     (RaiseRadarReadout + the bindings it runs)  → "readouts" / "readoutMs"
	///   Begin/EndFeed, TimeFeed (a feed's parse/merge ON the UI thread) → "feeds" / "feedMs" / "feedBy"
	///   Begin/EndScrub       (a dot-matrix scrubber paint)               → "scrubs" / "scrubMs"
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

		private static int _hops;

		/// <summary>A download continuation just resumed — counted only when it landed on a sync context (the UI
		/// thread). Called once per body chunk, so the count is how many times a download woke the UI thread.</summary>
		public static void NoteHop()
		{
			if (SynchronizationContext.Current is not null) Interlocked.Increment(ref _hops);
		}

		/// <summary>UI-thread download continuations since the last call.</summary>
		public static int TakeHops() => Interlocked.Exchange(ref _hops, 0);

		private static int _readouts;
		private static long _readoutTicks;

		/// <summary>Start timing one radar readout refresh (RadarViewModel.RaiseRadarReadout). x:Bind updates its targets
		/// INSIDE PropertyChanged, so the span covers the bindings and every VM listening to it, not just the raise.</summary>
		public static long BeginReadout() => Stopwatch.GetTimestamp();

		public static void EndReadout(long started)
		{
			Interlocked.Increment(ref _readouts);
			Interlocked.Add(ref _readoutTicks, Stopwatch.GetTimestamp() - started);
		}

		/// <summary>Readout refreshes and their total milliseconds since the last call.</summary>
		public static (int Count, double Ms) TakeReadouts() =>
			(Interlocked.Exchange(ref _readouts, 0), Interlocked.Exchange(ref _readoutTicks, 0) * 1000.0 / Stopwatch.Frequency);

		private static int _scrubs;
		private static long _scrubTicks;

		/// <summary>Start timing one scrubber paint (DotMatrixScrubber: a segment's repaint, a lit move, a re-lay) — UI thread.</summary>
		public static long BeginScrub() => Stopwatch.GetTimestamp();

		public static void EndScrub(long started)
		{
			Interlocked.Increment(ref _scrubs);
			Interlocked.Add(ref _scrubTicks, Stopwatch.GetTimestamp() - started);
		}

		/// <summary>Scrubber paints and their total milliseconds since the last call.</summary>
		public static (int Count, double Ms) TakeScrubs() =>
			(Interlocked.Exchange(ref _scrubs, 0), Interlocked.Exchange(ref _scrubTicks, 0) * 1000.0 / Stopwatch.Frequency);

		// Overlay feeds (warnings, watches, outlook, …), PER FEED so a total can be traced to its source. Rare calls
		// (a few per refresh), so a plain lock.
		private static readonly object _feedGate = new();
		private static readonly System.Collections.Generic.Dictionary<string, (int N, long Ticks)> _feedBy = new(StringComparer.Ordinal);

		/// <summary>One refresh of <paramref name="feed"/> got its data — the denominator for "feed ms per refresh".</summary>
		public static void NoteFeedRefresh(string feed)
		{
			lock (_feedGate)
			{
				_feedBy.TryGetValue(feed, out var c);
				_feedBy[feed] = (c.N + 1, c.Ticks);
			}
		}

		/// <summary>Start timing a feed's parse/merge/serialize stretch. Like <see cref="BeginSync"/>: 0 (= not timed) off
		/// a sync context, so only the part that ran ON the UI thread counts — after the off-thread fix it reads ~0.</summary>
		public static long BeginFeed() => SynchronizationContext.Current is null ? 0 : Stopwatch.GetTimestamp();

		public static void EndFeed(long started, string feed)
		{
			if (started == 0) return;
			var ticks = Stopwatch.GetTimestamp() - started;
			lock (_feedGate)
			{
				_feedBy.TryGetValue(feed, out var c);
				_feedBy[feed] = (c.N, c.Ticks + ticks);
			}
		}

		/// <summary>Runs <paramref name="work"/> (a synchronous parse/build) timed as <paramref name="feed"/>'s.</summary>
		public static T TimeFeed<T>(string feed, Func<T> work)
		{
			var t = BeginFeed();
			try { return work(); }
			finally { EndFeed(t, feed); }
		}

		/// <summary>Feed refreshes, their UI-thread ms, and the per-feed split ("warnings:2/14.1 outlook:1/3.0", refreshes/ms,
		/// costliest first) since the last call.</summary>
		public static (int Count, double Ms, string? By) TakeFeeds()
		{
			lock (_feedGate)
			{
				if (_feedBy.Count == 0) return (0, 0, null);
				int n = 0; long ticks = 0;
				var parts = new System.Collections.Generic.List<(string Name, int N, double Ms)>();
				foreach (var (name, c) in _feedBy)
				{
					n += c.N; ticks += c.Ticks;
					parts.Add((name, c.N, c.Ticks * 1000.0 / Stopwatch.Frequency));
				}
				_feedBy.Clear();
				parts.Sort((a, b) => b.Ms.CompareTo(a.Ms));
				var by = string.Join(" ", parts.ConvertAll(p =>
					string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Name}:{p.N}/{p.Ms:0.0}")));
				return (n, ticks * 1000.0 / Stopwatch.Frequency, by);
			}
		}
	}
}

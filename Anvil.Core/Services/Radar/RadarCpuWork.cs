using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Anvil.Services
{
	/// <summary>
	/// Where <see cref="Level2RadarService"/>'s CPU-bound work runs (tilt extraction, gunzip, sweep select):
	/// a FIXED set of <see cref="Threads"/> BELOW-NORMAL priority threads instead of the thread pool.
	/// </summary>
	/// <remarks>
	/// <code>
	///   EnsureCachedAsync ×12 ─┐   urgent lane ─┐  (first-paint frame, cache-hit parse, live select)
	///   EnsureVwpTilts…    ────┼─▶ normal lane ─┴─▶ [ T0 ][ T1 ][ T2 ]  BelowNormal threads
	///                          └─ queued here, never more than 3 running
	/// </code>
	/// WHY (measured 2026-10-01, perf.ui): a cold replay ran up to 12 extractions at once on the pool at NORMAL
	/// priority, beside the page's 4 decode workers, on 8 logical cores. The machine sat at 93–100%, the WinUI
	/// UI thread waited for CPU (up to 5 s late), and because WinUI's WebView2 receives mouse input THROUGH that
	/// thread, pan/zoom lagged while the map's own frame rate stayed perfect. BelowNormal means Windows always
	/// runs the UI thread first; the cap keeps a core free even for normal-priority work. Idle machine = the
	/// threads still run flat out, so an untouched load is not meant to get slower — check `timing` rows.
	/// ⚠️ Downloads stay as wide as before (12 replay / 6 live) — only the DECOMPRESS is queued here.
	/// ⚠️ Urgent = work something is visibly waiting on. Don't put backfill there or the lanes mean nothing.
	/// ⚠️ Work here must not block on other tasks scheduled here (3 threads → deadlock). HideScheduler keeps
	/// any Task.Factory call inside the work off these threads.
	/// </remarks>
	public static class RadarCpuWork
	{
		/// <summary>Concurrent CPU jobs. 3 of 8 logical cores: extraction keeps moving, the UI keeps a core.</summary>
		public const int Threads = 3;

		private static readonly object Gate = new();
		private static readonly Queue<(Task Task, Lane Lane)> UrgentQueue = new(), NormalQueue = new();
		private static readonly SemaphoreSlim Signal = new(0);
		private static readonly Lane Urgent = new(urgent: true), Normal = new(urgent: false);

		static RadarCpuWork()
		{
			for (var i = 0; i < Threads; i++)
			{
				new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"Radar CPU {i}" }.Start();
			}
		}

		/// <summary>Run <paramref name="work"/> on the radar CPU threads. <paramref name="urgent"/> jumps the queue.</summary>
		public static Task<T> Run<T>(Func<T> work, CancellationToken ct, bool urgent = false) =>
			Task.Factory.StartNew(() =>
			{
				var started = RadarPerfCounters.JobStarted(); // perf.ui probe (excisable)
				try { return work(); }
				finally { RadarPerfCounters.JobEnded(started); }
			}, ct, TaskCreationOptions.DenyChildAttach | TaskCreationOptions.HideScheduler, urgent ? Urgent : Normal);

		private static void Loop()
		{
			while (true)
			{
				Signal.Wait();
				(Task Task, Lane Lane) next;
				lock (Gate)
				{
					next = UrgentQueue.Count > 0 ? UrgentQueue.Dequeue() : NormalQueue.Dequeue();
				}
				next.Lane.Execute(next.Task);
			}
		}

		// Two schedulers over ONE set of threads; the lane only decides which queue a task waits in.
		private sealed class Lane : TaskScheduler
		{
			private readonly bool _urgent;

			public Lane(bool urgent) => _urgent = urgent;

			public override int MaximumConcurrencyLevel => Threads;

			internal void Execute(Task task) => TryExecuteTask(task);

			protected override void QueueTask(Task task)
			{
				lock (Gate) (_urgent ? UrgentQueue : NormalQueue).Enqueue((task, this));
				Signal.Release();
			}

			// Never inline onto a caller's thread — that would run the work at the caller's priority, possibly
			// on the UI thread.
			protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

			protected override IEnumerable<Task> GetScheduledTasks()
			{
				lock (Gate)
				{
					var list = new List<Task>();
					foreach (var (t, lane) in _urgent ? UrgentQueue : NormalQueue) if (lane == this) list.Add(t);
					return list;
				}
			}
		}
	}
}

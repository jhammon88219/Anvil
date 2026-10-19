using System;
using System.Threading;
using System.Threading.Tasks;
using Anvil.Models;
using Anvil.ViewModels;

namespace Anvil.Services
{
	/// <summary>
	/// ONE feed's schedule, handed out by <see cref="LiveFeedHub"/>: how long its refresh loop waits before the next cycle
	/// (<see cref="Schedule"/>), and a wait that a product can cut short (<see cref="WaitAsync"/>).
	/// </summary>
	/// <remarks>
	/// ⚠️ THE RULE (the user's, 2026-10-19): while the live source is connected a PUSHED feed does not poll — it fetches
	/// when a product wakes it, re-checks a few times after (<see cref="LiveFeedKinds.ChaseDelays"/>), and otherwise only
	/// the long safety re-check. Otherwise (Polling only, the source down, or storm cells) it polls on the user's
	/// interval. So a feed never sends a poll AND a push-driven fetch for the same news.
	/// ⚠️ Any thread: the loops run on pool threads, the hub calls in from the source's thread.
	/// </remarks>
	public sealed class LiveFeedChannel
	{
		private readonly object _gate = new();
		private readonly Func<bool> _isLive;
		private readonly Func<TimeSpan> _pollInterval;
		private TaskCompletionSource<bool>? _waiter;
		private bool _woken;
		private int _chase = int.MaxValue; // index into ChaseDelays; MaxValue = no run in progress

		internal LiveFeedChannel(LiveFeedKind kind, Func<bool> isLive, Func<TimeSpan> pollInterval)
		{
			Kind = kind;
			_isLive = isLive;
			_pollInterval = pollInterval;
		}

		/// <summary>A channel with no hub: always polls every <paramref name="interval"/> (tests, and a VM built alone).</summary>
		public static LiveFeedChannel Polling(LiveFeedKind kind, TimeSpan interval) => new(kind, () => false, () => interval);

		public LiveFeedKind Kind { get; }

		/// <summary>True = a connected live source carries this feed right now (its schedule is paused).</summary>
		public bool IsLive => LiveFeedKinds.IsPushed(Kind) && _isLive();

		/// <summary>The user's polling interval for this feed (Settings → Live Data), clamped.</summary>
		public TimeSpan PollInterval => _pollInterval();

		/// <summary>
		/// Called at the END of each cycle: the delay before the next one, also written to the feed's row — "live" / "when
		/// issued" while live, else the poll interval and its clock time.
		/// </summary>
		public TimeSpan Schedule(FeedStatus status)
		{
			if (IsLive)
			{
				status.Live();
				lock (_gate)
				{
					if (_chase < LiveFeedKinds.ChaseDelays.Count) { return LiveFeedKinds.ChaseDelays[_chase++]; }
					_chase = int.MaxValue;
				}
				return LiveFeedKinds.LiveSafetyInterval;
			}
			lock (_gate) { _chase = int.MaxValue; }
			var interval = _pollInterval();
			status.Scheduled(interval);
			return interval;
		}

		/// <summary>Waits <paramref name="delay"/>, or less if the feed is woken first (<see cref="Trigger"/> /
		/// <see cref="Wake"/>). A wake that lands mid-cycle is kept, so the next wait returns at once.</summary>
		public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
		{
			TaskCompletionSource<bool> waiter;
			lock (_gate)
			{
				if (_woken) { _woken = false; return; }
				waiter = _waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
			}
			// The delay is cancelled once the wait ends either way, so a woken wait leaves no timer running behind it.
			using var stopDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
			var timer = Task.Delay(delay, stopDelay.Token);
			try
			{
				await Task.WhenAny(waiter.Task, timer).ConfigureAwait(false);
				ct.ThrowIfCancellationRequested();
			}
			finally
			{
				stopDelay.Cancel();
				// A Wake racing the timer is served by the cycle about to run either way — nothing to keep.
				lock (_gate) { if (ReferenceEquals(_waiter, waiter)) { _waiter = null; } }
			}
		}

		/// <summary>A product for this feed arrived: fetch now, then run the re-check gaps.</summary>
		internal void Trigger()
		{
			lock (_gate) { _chase = 0; }
			Wake();
		}

		/// <summary>Fetch now (a catch-up after a drop, a gap, or a source change) without a re-check run.</summary>
		internal void Wake()
		{
			TaskCompletionSource<bool>? waiter;
			lock (_gate)
			{
				waiter = _waiter;
				if (waiter is null) { _woken = true; } // mid-cycle: the next wait returns at once
				_waiter = null;
			}
			waiter?.TrySetResult(true);
		}
	}
}

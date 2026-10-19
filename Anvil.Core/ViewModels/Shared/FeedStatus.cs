using System;
using System.Globalization;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Where a feed's refresh stands.</summary>
	public enum FeedState { Waiting, Checking, Ok, Failed }

	/// <summary>
	/// ONE live feed's timing for the temporal window's activity expander — a row of its FEED · EVERY · LAST · NEXT table
	/// (warnings, watches, reports, cells, tropical, MDs, and the radar's live poll). Each feed VM owns one (<c>Status</c>);
	/// its refresh LOOP ends every cycle with <c>LiveFeedChannel.Schedule(Status)</c> (→ <see cref="Scheduled"/> or
	/// <see cref="Live"/>), its FETCH calls <see cref="Checking"/> /
	/// <see cref="Succeeded"/> / <see cref="Failed"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ CLOCK TIMES, NEVER COUNTDOWNS (the user's call, 2026-10-19: several counting lines were distracting). Nothing here
	/// ticks — every text changes only when the feed does something.
	/// ⚠️ THREAD-SAFE BY POSTING: the loops run on pool threads, so every mutation hops to the UI thread through the
	/// dispatcher before it touches a property (no dispatcher = the caller is already on the UI thread — the radar's row).
	/// ⚠️ TRUTH ONLY: LAST is a fetch that pulled data; a failed cycle leaves it alone (its age is the staleness).
	/// </remarks>
	public sealed class FeedStatus : ObservableObject
	{
		private readonly IDispatcher? _dispatcher;
		private FeedState _state = FeedState.Waiting;
		private DateTimeOffset? _lastUpdated, _failedAt, _nextCheck;
		private string _every = string.Empty, _lastText = "—", _nextText = "—";
		private bool _live;

		/// <param name="layerId">The feed's <c>Models.LayerOrder</c> id — its place in the window's layer order.</param>
		/// <param name="dispatcher">Null = every call already arrives on the UI thread.</param>
		public FeedStatus(string layerId, string name, IDispatcher? dispatcher)
		{
			LayerId = layerId;
			Name = name;
			_dispatcher = dispatcher;
		}

		public string LayerId { get; }
		public string Name { get; }

		public FeedState State { get => _state; private set => SetProperty(ref _state, value); }

		/// <summary>The EVERY column: "15 s" / "2 min" / "scan" — how often the feed checks.</summary>
		public string Every { get => _every; private set => SetProperty(ref _every, value); }

		/// <summary>The LAST column: "12:04:31 PM" (last pulled data) · "Checking" · "failed 12:00 PM" · "—".</summary>
		public string LastText { get => _lastText; private set => SetProperty(ref _lastText, value); }

		/// <summary>The NEXT column: the clock time of the next check, or "—" before one is scheduled.</summary>
		public string NextText { get => _nextText; private set => SetProperty(ref _nextText, value); }

		public DateTimeOffset? LastUpdated => _lastUpdated;
		public DateTimeOffset? FailedAt => _failedAt;

		// ── Feed hooks (any thread) ──

		/// <summary>The loop set its next cycle <paramref name="interval"/> from now.</summary>
		internal void Scheduled(TimeSpan interval) => Run(() =>
		{
			_live = false;
			_nextCheck = DateTimeOffset.Now + interval;
			Every = EveryWords(interval);
			Refresh();
		});

		/// <summary>The next check is at <paramref name="next"/> (a planner that knows the moment, like the radar's).</summary>
		internal void ScheduledAt(DateTimeOffset? next, string every) => Run(() =>
		{
			_live = false;
			_nextCheck = next;
			Every = every;
			Refresh();
		});

		/// <summary>A connected live source carries this feed: EVERY "live", NEXT "when issued" (no clock time — the next
		/// fetch is whenever the NWS issues something; LiveFeedChannel).</summary>
		internal void Live() => Run(() =>
		{
			_nextCheck = null;
			_live = true;
			Every = "live";
			Refresh();
		});

		/// <summary>A fetch started.</summary>
		internal void Checking() => Run(() => { State = FeedState.Checking; Refresh(); });

		/// <summary>A fetch pulled data (or confirmed nothing newer exists).</summary>
		internal void Succeeded() => Run(() =>
		{
			_lastUpdated = DateTimeOffset.Now;
			_failedAt = null;
			State = FeedState.Ok;
			OnPropertyChanged(nameof(LastUpdated));
			OnPropertyChanged(nameof(FailedAt));
			Refresh();
		});

		/// <summary>A fetch failed (the feed keeps its last good data on screen).</summary>
		internal void Failed() => Run(() =>
		{
			_failedAt = DateTimeOffset.Now;
			State = FeedState.Failed;
			OnPropertyChanged(nameof(FailedAt));
			Refresh();
		});

		private void Run(Action action)
		{
			if (_dispatcher is null) action();
			else _dispatcher.Post(action);
		}

		private void Refresh()
		{
			LastText = State switch
			{
				FeedState.Checking => "Checking",
				FeedState.Failed when _failedAt is { } f => $"failed {ClockMinutes(f)}",
				_ => _lastUpdated is { } u ? Clock(u) : "—",
			};
			NextText = _live ? "when issued" : _nextCheck is { } n ? Clock(n) : "—";
		}

		/// <summary>The EVERY column's words for an interval: "15 s" under 90 s, else whole minutes ("2 min").</summary>
		internal static string EveryWords(TimeSpan interval) =>
			interval.TotalSeconds < 90 ? $"{interval.TotalSeconds:0} s" : $"{interval.TotalMinutes:0} min";

		internal static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

		internal static string ClockMinutes(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
	}
}

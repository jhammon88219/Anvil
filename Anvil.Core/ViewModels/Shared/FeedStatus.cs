using System;
using System.Globalization;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>Where a feed's refresh stands.</summary>
	public enum FeedState { Waiting, Checking, Ok, Failed }

	/// <summary>
	/// ONE live feed's timing for the temporal window's activity expander (warnings, watches, reports, cells, tropical,
	/// MDs): when it last pulled data, when it checks next, how often, and whether the last check failed. Each feed VM
	/// owns one (<c>Status</c>); its refresh LOOP calls <see cref="Scheduled"/> every cycle, its FETCH calls
	/// <see cref="Checking"/> / <see cref="Succeeded"/> / <see cref="Failed"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ THREAD-SAFE BY POSTING: the loops run on pool threads, so every mutation hops to the UI thread through the
	/// dispatcher before it touches a property. Call from anywhere.
	/// ⚠️ TRUTH ONLY: "Updated" is a fetch that pulled data; a failed cycle leaves it alone (its age is the staleness),
	/// exactly like the feeds' own cards. The countdown re-reads the clock on <see cref="Tick"/> (the activity VM's 1 s timer).
	/// </remarks>
	public sealed class FeedStatus : ObservableObject
	{
		private readonly IDispatcher _dispatcher;
		private FeedState _state = FeedState.Waiting;
		private DateTimeOffset? _lastUpdated, _failedAt, _nextCheck;
		private string _cadence = string.Empty, _updatedText = "—", _countdownText = string.Empty;

		/// <param name="layerId">The feed's <c>Models.LayerOrder</c> id — its place in the window's layer order.</param>
		public FeedStatus(string layerId, string name, IDispatcher dispatcher)
		{
			LayerId = layerId;
			Name = name;
			_dispatcher = dispatcher;
		}

		public string LayerId { get; }
		public string Name { get; }

		public FeedState State { get => _state; private set => SetProperty(ref _state, value); }

		/// <summary>"every 15 s" / "every 2 min" — the interval the loop last scheduled.</summary>
		public string Cadence { get => _cadence; private set => SetProperty(ref _cadence, value); }

		/// <summary>"12:04:31 PM" (last pulled data) · "Checking" · "Failed 12:00 PM" · "—" before the first.</summary>
		public string UpdatedText { get => _updatedText; private set => SetProperty(ref _updatedText, value); }

		/// <summary>"in 0:09" · "retry 1:12" (after a failure) · empty while checking or unscheduled.</summary>
		public string CountdownText { get => _countdownText; private set => SetProperty(ref _countdownText, value); }

		public DateTimeOffset? LastUpdated => _lastUpdated;

		// ── Feed hooks (any thread) ──

		/// <summary>The loop set its next cycle <paramref name="interval"/> from now.</summary>
		internal void Scheduled(TimeSpan interval) => _dispatcher.Post(() =>
		{
			_nextCheck = DateTimeOffset.Now + interval;
			Cadence = CadenceWords(interval);
			Refresh();
		});

		/// <summary>A fetch started.</summary>
		internal void Checking() => _dispatcher.Post(() => { State = FeedState.Checking; Refresh(); });

		/// <summary>A fetch pulled data.</summary>
		internal void Succeeded() => _dispatcher.Post(() =>
		{
			_lastUpdated = DateTimeOffset.Now;
			_failedAt = null;
			State = FeedState.Ok;
			OnPropertyChanged(nameof(LastUpdated));
			Refresh();
		});

		/// <summary>A fetch failed (the feed keeps its last good data on screen).</summary>
		internal void Failed() => _dispatcher.Post(() =>
		{
			_failedAt = DateTimeOffset.Now;
			State = FeedState.Failed;
			Refresh();
		});

		/// <summary>Re-read the clock for the countdown (UI thread; the activity VM's 1 s tick).</summary>
		internal void Tick() => Refresh();

		private void Refresh()
		{
			UpdatedText = State switch
			{
				FeedState.Checking => "Checking",
				FeedState.Failed when _failedAt is { } f => $"Failed {ClockMinutes(f)}",
				_ => _lastUpdated is { } u ? Clock(u) : "—",
			};
			if (State == FeedState.Checking || _nextCheck is not { } next)
			{
				CountdownText = string.Empty;
				return;
			}
			var rem = (int)Math.Ceiling(Math.Max(0, (next - DateTimeOffset.Now).TotalSeconds));
			var left = $"{rem / 60}:{rem % 60:00}";
			CountdownText = State == FeedState.Failed ? $"retry {left}" : $"in {left}";
		}

		internal static string CadenceWords(TimeSpan interval) =>
			interval.TotalSeconds < 90 ? $"every {interval.TotalSeconds:0} s" : $"every {interval.TotalMinutes:0} min";

		private static string Clock(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

		private static string ClockMinutes(DateTimeOffset t) => t.ToLocalTime().ToString("h:mm tt", CultureInfo.CurrentCulture);
	}
}

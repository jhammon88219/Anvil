using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Anvil.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// ONE temporal mode's ACTIVITY (the window's <c>TemporalWindows/TemporalWindowActivityExpander</c>): the radar's live
	/// work (<see cref="Radar"/>, NowCast only), the mode's live feeds (<see cref="Feeds"/>, in the window's layer order),
	/// the collapsed state's one-line feed summary, and the expander's state (Hidden ⇄ Full, persisted per window).
	/// </summary>
	/// <remarks>
	/// ⚠️ ONLY THAT MODE'S WORK (the user's rule, 2026-10-16): NowCast = the live poll, the site check and its six live
	/// feeds; PastCast and ForeCast have none yet (empty — the window shows a bare band, no tab). Give a mode its own by
	/// building it with a radar activity and/or feeds in <c>MapViewModel</c>.
	/// ⚠️ NOTHING TICKS: every row shows clock times (FeedStatus), so there is no timer here (removed 2026-10-19).
	/// </remarks>
	public sealed class TemporalActivityViewModel : ObservableObject
	{
		private readonly Action<string> _writeState;
		private string _state;
		private string _summary = string.Empty;
		private bool _summaryHasFailure;

		/// <param name="readState">Reads the persisted state token (an <c>AppSettings</c> property).</param>
		/// <param name="writeState">Writes it back.</param>
		/// <param name="live">Where the feeds' updates come from (the Full table's SOURCE LINE); null = no line.</param>
		public TemporalActivityViewModel(BarActivityViewModel? radar, IEnumerable<FeedStatus> feeds,
			Func<string> readState, Action<string> writeState, LiveDataViewModel? live = null)
		{
			Radar = radar;
			Live = live;
			Feeds = new ObservableCollection<FeedStatus>(feeds);
			_writeState = writeState;
			_state = ActivityExpanderStates.Normalize(readState());
			foreach (var f in Feeds) f.PropertyChanged += OnFeedChanged;
			RefreshSummary();
		}

		/// <summary>The radar's live work (NowCast); null for a mode without radar activity.</summary>
		public BarActivityViewModel? Radar { get; }

		public bool HasRadar => Radar is not null;

		/// <summary>The mode's live feeds, top first in the window's layer order (<see cref="ApplyLayerOrder"/>).</summary>
		public ObservableCollection<FeedStatus> Feeds { get; }

		public bool HasFeeds => Feeds.Count > 0;

		/// <summary>The live source behind the feeds — the Full table's last line (NowCast only).</summary>
		public LiveDataViewModel? Live { get; }

		public bool HasLive => Live is not null;

		/// <summary>Anything to show at all — false = a bare band with no tab.</summary>
		public bool HasContent => HasRadar || HasFeeds;

		// ── The expander's state (ActivityExpanderStates tokens) ──

		/// <summary>The current token. Setting it persists it.</summary>
		public string State
		{
			get => _state;
			set
			{
				var token = ActivityExpanderStates.Normalize(value);
				if (!SetProperty(ref _state, token)) return;
				_writeState(token);
				OnPropertyChanged(nameof(IsHidden));
				OnPropertyChanged(nameof(IsFull));
				OnPropertyChanged(nameof(OpensMore));
				OnPropertyChanged(nameof(TabLabel));
			}
		}

		public bool IsHidden => !IsFull;
		public bool IsFull => ActivityExpanderStates.IsFull(_state);

		/// <summary>The tab's next click opens (chevron up) — else it closes.</summary>
		public bool OpensMore => ActivityExpanderStates.OpensMore(_state);

		/// <summary>"More" / "Less" — what the tab's click does.</summary>
		public string TabLabel => OpensMore ? "More" : "Less";

		/// <summary>The tab's click: Hidden ⇄ Full.</summary>
		public void Advance() => State = ActivityExpanderStates.Next(_state);

		// ── The collapsed state's FEED SUMMARY: one line that names a feed only when it has a problem ──

		/// <summary>"All 6 feeds current · last 12:04:31 PM" · "Tropical failed at 12:00 PM" · "2 feeds failed: …" ·
		/// "4 of 6 feeds current · …" while some haven't reported yet · "Feeds starting" before any have.</summary>
		public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

		/// <summary>The summary is about a failure (the view gives it the failed dot).</summary>
		public bool SummaryHasFailure { get => _summaryHasFailure; private set => SetProperty(ref _summaryHasFailure, value); }

		private void OnFeedChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(FeedStatus.State) or nameof(FeedStatus.LastUpdated) or nameof(FeedStatus.FailedAt))
			{
				RefreshSummary();
			}
		}

		private void RefreshSummary()
		{
			var failed = Feeds.Where(f => f.State == FeedState.Failed).ToList();
			SummaryHasFailure = failed.Count > 0;
			if (failed.Count == 1)
			{
				var at = failed[0].FailedAt is { } t ? $" at {FeedStatus.ClockMinutes(t)}" : string.Empty;
				Summary = $"{failed[0].Name} failed{at}";
				return;
			}
			if (failed.Count > 1)
			{
				Summary = $"{failed.Count} feeds failed: {string.Join(", ", failed.Select(f => f.Name))}";
				return;
			}
			var current = Feeds.Where(f => f.LastUpdated is not null).ToList();
			if (current.Count == 0)
			{
				Summary = HasFeeds ? "Feeds starting" : string.Empty;
				return;
			}
			var last = FeedStatus.Clock(current.Max(f => f.LastUpdated!.Value));
			Summary = current.Count == Feeds.Count
				? $"All {Feeds.Count} feeds current · last {last}"
				: $"{current.Count} of {Feeds.Count} feeds current · last {last}";
		}

		// ── Feed order ──

		/// <summary>Re-sort <see cref="Feeds"/> to the window's layer order (top first); unknown ids keep their place at the end.</summary>
		public void ApplyLayerOrder(IReadOnlyList<string> topFirst)
		{
			var rank = topFirst.Select((id, i) => (id, i)).ToDictionary(p => p.id, p => p.i);
			var sorted = Feeds.OrderBy(f => rank.TryGetValue(f.LayerId, out var r) ? r : int.MaxValue).ToList();
			for (var i = 0; i < sorted.Count; i++)
			{
				var at = Feeds.IndexOf(sorted[i]);
				if (at != i) Feeds.Move(at, i);
			}
		}
	}
}

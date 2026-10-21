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
	/// feeds; ForeCast = the SPC outlooks (2026-10-19); PastCast has none yet (empty — the window shows a bare band, no
	/// tab). Give a mode its own by building it with a radar activity and/or feeds in <c>MapViewModel</c>.
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
		/// <param name="pastLayers">PastCast's LAYER · RESULT · SOURCE rows (what the loaded event downloaded); null = none.</param>
		public TemporalActivityViewModel(BarActivityViewModel? radar, IEnumerable<FeedStatus> feeds,
			Func<string> readState, Action<string> writeState, LiveDataViewModel? live = null,
			IEnumerable<PastLayerStatus>? pastLayers = null)
		{
			Radar = radar;
			Live = live;
			Feeds = new ObservableCollection<FeedStatus>(feeds);
			PastLayers = new ObservableCollection<PastLayerStatus>(pastLayers ?? Array.Empty<PastLayerStatus>());
			_writeState = writeState;
			_state = ActivityExpanderStates.Normalize(readState());
			foreach (var f in Feeds) f.PropertyChanged += OnFeedChanged;
			foreach (var l in PastLayers) l.PropertyChanged += OnPastLayerChanged;
			RefreshSummary();
		}

		// ── PastCast: what the loaded event downloaded ──

		/// <summary>PastCast's rows, top first in the window's layer order (<see cref="ApplyLayerOrder"/>).</summary>
		public ObservableCollection<PastLayerStatus> PastLayers { get; }

		public bool HasPastLayers => PastLayers.Count > 0;

		private string _eventLine = string.Empty, _loadedText = string.Empty;

		/// <summary>The table's title: the loaded event ("KTLX · May 20, 2013 · 1:00 PM–3:00 PM"), empty before a load.</summary>
		public string EventLine { get => _eventLine; private set => SetProperty(ref _eventLine, value); }

		/// <summary>The title's right side: "loaded 3:12 PM" once the radar finished, else empty.</summary>
		public string LoadedText { get => _loadedText; private set => SetProperty(ref _loadedText, value); }

		/// <summary>Sets the title line (MapViewModel, from the loop holding gate's event line + the load's end).</summary>
		internal void SetEvent(string eventLine, DateTimeOffset? loadedAt)
		{
			EventLine = eventLine;
			LoadedText = loadedAt is { } t ? $"loaded {FeedStatus.ClockMinutes(t)}" : string.Empty;
		}

		private void OnPastLayerChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName is nameof(PastLayerStatus.State) or nameof(PastLayerStatus.ResultText)) { RefreshSummary(); }
		}

		/// <summary>The radar's live work (NowCast); null for a mode without radar activity.</summary>
		public BarActivityViewModel? Radar { get; }

		public bool HasRadar => Radar is not null;

		/// <summary>The mode's live feeds, top first in the window's layer order (<see cref="ApplyLayerOrder"/>).</summary>
		public ObservableCollection<FeedStatus> Feeds { get; }

		/// <summary>
		/// Adds or removes a feed's row — a feed that only works while its layer is on (ForeCast's HRRR, while Short Range
		/// is ticked). Added at the END; <see cref="ApplyLayerOrder"/> then sorts it into place.
		/// </summary>
		internal void SetFeedShown(FeedStatus feed, bool shown)
		{
			if (shown == Feeds.Contains(feed)) return;
			if (shown)
			{
				Feeds.Add(feed);
				feed.PropertyChanged += OnFeedChanged;
			}
			else
			{
				Feeds.Remove(feed);
				feed.PropertyChanged -= OnFeedChanged;
			}
			OnPropertyChanged(nameof(HasFeeds));
			OnPropertyChanged(nameof(HasContent));
			OnPropertyChanged(nameof(HasFeedTable));
			OnPropertyChanged(nameof(HasSummary));
			RefreshSummary();
		}

		public bool HasFeeds => Feeds.Count > 0;

		/// <summary>The live source behind the feeds — the Full table's last line (NowCast only).</summary>
		public LiveDataViewModel? Live { get; }

		public bool HasLive => Live is not null;

		/// <summary>Anything to show at all — false = a bare band with no tab.</summary>
		public bool HasContent => HasRadar || HasFeeds || HasPastLayers;

		/// <summary>The FEED · EVERY · LAST · NEXT table shows (NowCast, ForeCast) — PastCast has its own.</summary>
		public bool HasFeedTable => HasRadar || HasFeeds;

		/// <summary>There's a one-line <see cref="Summary"/> to show.</summary>
		public bool HasSummary => HasFeeds || HasPastLayers;

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
			if (e.PropertyName is nameof(FeedStatus.State) or nameof(FeedStatus.LastUpdated) or nameof(FeedStatus.FailedAt) or nameof(FeedStatus.LastText))
			{
				RefreshSummary();
			}
		}

		private void RefreshSummary()
		{
			if (HasPastLayers)
			{
				RefreshPastSummary();
				return;
			}
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
			// A feed whose LAST is words (a model run's progress) LEADS the line in its own words — "HRRR 17Z · 7/18 · …" —
			// and the rest are summed up after it as usual.
			var noted = Feeds.Where(f => f.HasNote).ToList();
			var lead = string.Join(" · ", noted.Select(f => $"{f.Name} {f.LastText}"));
			var rest = Feeds.Where(f => !f.HasNote).ToList();
			var restLine = SummaryOf(rest);
			Summary = lead.Length == 0 ? restLine : restLine.Length == 0 ? lead : $"{lead} · {restLine}";
		}

		private string SummaryOf(IReadOnlyList<FeedStatus> feeds)
		{
			var current = feeds.Where(f => f.LastUpdated is not null).ToList();
			// ONE feed (ForeCast's outlooks) is named, not counted: "Outlooks current · last …", "Outlooks starting".
			var single = feeds.Count == 1 ? feeds[0].Name : null;
			if (current.Count == 0)
			{
				return feeds.Count == 0 ? string.Empty : single is not null ? $"{single} starting" : "Feeds starting";
			}
			var last = FeedStatus.Clock(current.Max(f => f.LastUpdated!.Value));
			if (single is not null) return $"{single} current · last {last}";
			return current.Count == feeds.Count
				? $"All {feeds.Count} feeds current · last {last}"
				: $"{current.Count} of {feeds.Count} feeds current · last {last}";
		}

		// PastCast's line (both states): "Load an event to see what it downloads" · "Loading · 6 of 9 layers in" ·
		// "All 9 layers loaded" · "8 of 9 layers loaded · Damage surveys failed" · "7 of 9 … · 2 failed: a, b".
		private void RefreshPastSummary()
		{
			var total = PastLayers.Count;
			var failed = PastLayers.Where(l => l.State == PastLayerState.Failed).ToList();
			var done = PastLayers.Count(l => l.IsIn);
			SummaryHasFailure = failed.Count > 0;
			if (PastLayers.All(l => l.State == PastLayerState.Waiting))
			{
				Summary = "Load an event to see what it downloads";
				return;
			}
			if (PastLayers.Any(l => l.State == PastLayerState.Loading))
			{
				Summary = $"Loading · {done} of {total} layers in";
				return;
			}
			Summary = failed.Count switch
			{
				0 when done == total => $"All {total} layers loaded",
				0 => $"{done} of {total} layers loaded",
				1 => $"{done} of {total} layers loaded · {failed[0].Name} failed",
				_ => $"{done} of {total} layers loaded · {failed.Count} failed: {string.Join(", ", failed.Select(l => l.Name))}",
			};
		}

		// ── Row order ──

		/// <summary>Re-sort <see cref="Feeds"/> and <see cref="PastLayers"/> to the window's layer order (top first);
		/// unknown ids keep their place at the end.</summary>
		public void ApplyLayerOrder(IReadOnlyList<string> topFirst)
		{
			// First place wins if a saved order ever repeats an id (a plain ToDictionary would throw).
			var rank = topFirst.Select((id, i) => (id, i)).GroupBy(p => p.id).ToDictionary(g => g.Key, g => g.First().i);
			Sort(Feeds, f => f.LayerId);
			Sort(PastLayers, l => l.LayerId);

			void Sort<T>(ObservableCollection<T> rows, Func<T, string> idOf)
			{
				var sorted = rows.OrderBy(r => rank.TryGetValue(idOf(r), out var n) ? n : int.MaxValue).ToList();
				for (var i = 0; i < sorted.Count; i++)
				{
					var at = rows.IndexOf(sorted[i]);
					if (at != i) rows.Move(at, i);
				}
			}
		}
	}
}

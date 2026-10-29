using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the storm-based WARNING subsystem — active Tornado / Severe Thunderstorm / Flash Flood / Special Marine Warnings
	/// (the modern forecaster-drawn polygons). Sibling of <see cref="WatchesViewModel"/>: watches are the
	/// large outlook areas, warnings are the imminent-threat polygons, so each gets its own layer, toggle,
	/// and refresh loop. Surfaced under NowCast in the UI (current-conditions alerts, not a forecast).
	/// Visibility, opacity, the map-ready latch and the source push come from
	/// <see cref="MapOverlayViewModel"/>; the per-type toggles, the live counts and the section card come
	/// from <see cref="PhenomOverlayViewModel"/>, shared with <see cref="WatchesViewModel"/>. This class
	/// adds only what is specific to warnings: the ~1-min ADAPTIVE background refresh loop. Fetch/cache is
	/// in <see cref="IWarningService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class WarningsViewModel : PhenomOverlayViewModel
	{
		private readonly IMapService _mapService;
		private readonly IWarningService _warningService;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<WarningsViewModel> _logger;

		/// <summary>This feed's refresh timing for the NowCast activity expander (FeedStatus).</summary>
		public FeedStatus Status { get; }

		/// <param name="live">This feed's schedule from the live-feed hub (null = poll at the default interval).</param>
		/// <param name="dayService">The day list's ENDED warnings (IEM); null = the list holds only what this session saw.</param>
		public WarningsViewModel(IMapService mapService, IWarningService warningService, IDispatcher dispatcher, ILogger<WarningsViewModel> logger,
			LiveFeedChannel? live = null, IPastAlertService? dayService = null)
		{
			Status = new FeedStatus(Models.LayerOrder.Warnings, "Warnings", dispatcher);
			_live = live ?? LiveFeedChannel.Polling(LiveFeedKind.Warnings, LiveFeedKinds.DefaultPoll(LiveFeedKind.Warnings));
			_mapService = mapService;
			_warningService = warningService;
			_dayService = dayService;
			_dispatcher = dispatcher;
			_logger = logger;

			TornadoStepper = new WarningStepper("TO", FlyToAsync);
			SevereStepper = new WarningStepper("SV", FlyToAsync);
			FlashFloodStepper = new WarningStepper("FF", FlyToAsync);
			// NowCast turning OFF forgets every tile's place and history (its window closes with it).
			PropertyChanged += (_, e) =>
			{
				if (e.PropertyName == nameof(IsModeActive) && !IsModeActive)
				{
					TornadoStepper.Reset();
					SevereStepper.Reset();
					FlashFloodStepper.Reset();
				}
				else if (e.PropertyName == nameof(IsModeActive))
				{
					// NowCast on: the day list catches up at once (its "ended" rows were last checked while it was off).
					RebuildAlertList();
					_ = FetchDayIfDueAsync();
				}
			};
		}

		// ── The NowCast tiles' ‹ › arrows + clickable state lines (WarningStepper has the rules) ──

		/// <summary>The Tornado tile's arrows.</summary>
		public WarningStepper TornadoStepper { get; }

		/// <summary>The Severe tile's arrows.</summary>
		public WarningStepper SevereStepper { get; }

		/// <summary>The Flash flood tile's arrows.</summary>
		public WarningStepper FlashFloodStepper { get; }

		// ⚠️ Flies even when that type's row is UNTICKED (or the layer is hidden): the page flashes the polygons
		// from the geometry handed here, not from the warning layer — so you never land on an empty map.
		// One warning (the arrows) or a state's set (a state line): the box is their UNION, and several geometries
		// go over as one GeometryCollection, so they all flash at once.
		private Task FlyToAsync(IReadOnlyList<Models.WarningTarget> targets) =>
			IsMapReady && targets.Count > 0
				? _mapService.FocusWarningAsync(GeometryOf(targets),
					targets.Min(t => t.West), targets.Min(t => t.South), targets.Max(t => t.East), targets.Max(t => t.North))
				: Task.CompletedTask;

		/// <summary>The flash geometry: the one polygon, or a GeometryCollection of all of them. Internal for tests.</summary>
		internal static string GeometryOf(IReadOnlyList<Models.WarningTarget> targets) =>
			targets.Count == 1
				? targets[0].GeometryJson
				: "{\"type\":\"GeometryCollection\",\"geometries\":[" + string.Join(",", targets.Select(t => t.GeometryJson)) + "]}";

		// ── The section's DAY LIST (WarningDayList has the rules) ──
		// In effect = every CAP refresh (the map's set); ended = IEM's day so far, re-asked every DayEvery while NowCast
		// runs (the list is only on screen then), + whatever this session saw end. Rows go through RowSync: a refresh
		// changes only the rows that came, went or changed — never a rebuilt list (it flashed; 2026-10-21).

		private readonly IPastAlertService? _dayService;
		private readonly WarningDayList _day = new();
		private DateTimeOffset _dayFetchedAt = DateTimeOffset.MinValue;
		private static readonly TimeSpan DayEvery = TimeSpan.FromMinutes(5);

		/// <summary>The SPC convective day's start (12Z) containing <paramref name="now"/> — the list's day, the same as
		/// the storm reports'. Internal for tests.</summary>
		internal static DateTimeOffset DayStart(DateTimeOffset now)
		{
			var u = now.UtcDateTime;
			var start = new DateTimeOffset(u.Year, u.Month, u.Day, 12, 0, 0, TimeSpan.Zero);
			return u.Hour >= 12 ? start : start.AddDays(-1);
		}

		// IEM's day, at most every DayEvery, only while NowCast runs. Fire-and-forget: catches its own failures (the list
		// keeps the last day it had). _dayFetchedAt is stamped BEFORE the await, so two callers can't both fetch.
		private async Task FetchDayIfDueAsync()
		{
			var now = DateTimeOffset.UtcNow;
			if (_dayService is null || !IsModeActive || now - _dayFetchedAt < DayEvery) { return; }
			_dayFetchedAt = now;
			try
			{
				var day = await _dayService.FetchWarningDayAsync(DayStart(now), now, _shutdown.Token).ConfigureAwait(false);
				if (day is null) { _dayFetchedAt = now - DayEvery + TimeSpan.FromMinutes(1); return; } // retry in a minute
				_dispatcher.Post(() => { _day.SetDay(day); RebuildAlertList(); });
			}
			catch (OperationCanceledException) { }
			catch (Exception ex) { _logger.LogWarning(ex, "Warnings day list check failed"); }
		}

		// UI thread.
		private void RebuildAlertList()
		{
			var now = DateTimeOffset.UtcNow;
			var entries = _day.Build(DayStart(now), now);
			var byKey = new Dictionary<string, WarningDayEntry>();
			foreach (var e in entries) { byKey[KeyOf(e)] = e; }
			// In effect keeps Build's order (newest issued first); ended = newest ENDED first (no time known sorts last).
			SyncRows(ActiveAlerts, entries.Where(e => e.InEffect).Select(KeyOf).ToList(), byKey);
			SyncRows(EndedAlerts, entries.Where(e => !e.InEffect)
				.OrderByDescending(e => e.EndedAt ?? DateTimeOffset.MinValue)
				.Select(KeyOf).ToList(), byKey);
			RaiseAlertList();
		}

		private void SyncRows(System.Collections.ObjectModel.ObservableCollection<WarningListRow> rows, List<string> keys,
			Dictionary<string, WarningDayEntry> byKey)
		{
			RowSync.Sync(rows, keys, r => r.Key, k => new WarningListRow(k, byKey[k], IsTicked(byKey[k].Target.Phenom)));
			foreach (var row in rows)
			{
				row.Entry = byKey[row.Key];
				row.IsDrawn = IsTicked(row.Phenom);
			}
		}

		private static string KeyOf(WarningDayEntry e) => e.Target.Key.Length > 0 ? e.Target.Key : e.Target.Id;

		/// <inheritdoc/>
		public override void FlyTo(WarningListRow row) => _ = FlyToAsync(new[] { row.Target });

		protected override string SourceUrl => _warningService.WarningsUrl;
		protected override Task SetVisibleAsync(bool visible) => _mapService.SetWarningsVisibleAsync(visible);
		protected override Task SetOpacityAsync(double opacity) => _mapService.SetWarningsOpacityAsync(opacity);
		protected override Task SetSourceAsync(string url) => _mapService.SetWarningSourceAsync(url);
		protected override Task SetKindsAsync(bool tornado, bool severe, bool flashFlood, bool marine) =>
			_mapService.SetWarningKindsAsync(tornado, severe, flashFlood, marine);

		protected override string ItemNounSingular => "warning";
		protected override string ItemNounPlural => "warnings";

		public override bool SupportsFlashFlood => true;
		public override bool SupportsMarine => true;

		// The elevated damage-threat tags from the latest fetch (see WarningService.ThreatTier).
		private WarningThreatCounts _threats = WarningThreatCounts.None;

		/// <summary>The NowCast header tiles' tag lines, per type ("1 emergency · 2 PDS"). Every active warning,
		/// not just the ticked types; the map marks the same warnings with a heavier outline. (The section card's
		/// all-types line was removed 2026-10-21 — the card is one line.)</summary>
		public string TornadoThreats => _threats.TornadoLine;

		/// <inheritdoc cref="TornadoThreats"/>
		public string SevereThreats => _threats.SevereLine;

		/// <inheritdoc cref="TornadoThreats"/>
		public string FlashFloodThreats => _threats.FlashFloodLine;

		// ── The NowCast window's HEADER square (TemporalPaneNowCastContent's name row) ──
		// ForeCast's theory: the square is the WORST thing in effect, in its colour (DATA, never themed). Tornado →
		// Severe → Flash flood → Special marine, counting EVERY active warning like the tiles (not the ticked rows). None =
		// PastCast's blue (OutlookViewModel.NoAreasFill). ⚠️ The fills MIRROR the tiles' Ink literals in
		// TemporalPaneNowCastContent.xaml — change both. MarineFill (NWS's own SMW orange, no tile yet) mirrors
		// warnings.js MA and the Alerts body's marine row.
		public const string TornadoFill = "#FF2A2A";
		public const string SevereFill = "#FFD21A";
		public const string FlashFloodFill = "#2EE05A";
		public const string MarineFill = "#FFA500";

		public string HeaderSquareFill =>
			TornadoCount > 0 ? TornadoFill :
			SevereCount > 0 ? SevereFill :
			FlashFloodCount > 0 ? FlashFloodFill :
			MarineCount > 0 ? MarineFill :
			OutlookViewModel.NoAreasFill;

		public string HeaderSquareTip =>
			TornadoCount > 0 ? $"Highest warning in effect: Tornado ({TornadoCount})" :
			SevereCount > 0 ? $"Highest warning in effect: Severe thunderstorm ({SevereCount})" :
			FlashFloodCount > 0 ? $"Highest warning in effect: Flash flood ({FlashFloodCount})" :
			MarineCount > 0 ? $"Highest warning in effect: Special marine ({MarineCount})" :
			LastUpdated is null ? "Warnings not checked yet" : "No warnings in effect";

		private void RaiseHeaderSquare()
		{
			OnPropertyChanged(nameof(HeaderSquareFill));
			OnPropertyChanged(nameof(HeaderSquareTip));
		}

		// ⚠️ The one overlay that states its cadence on the card. Warnings are the short-fused layer, so "how current
		// is this number" matters: "live" while a live source carries them, else the polling interval.
		protected override string CadenceSuffix =>
			_live.IsLive ? " · live" : $" · checking every {FeedStatus.EveryWords(_live.PollInterval)}";

		// Cancels this VM's app-lifetime loops when the window closes. See Shutdown().
		private readonly CancellationTokenSource _shutdown = new();

		/// <summary>Stops this subsystem's app-lifetime loops. Called from <see cref="MapViewModel.Shutdown"/>
		/// on the main window's Closed, so no loop ticks into a XAML runtime that is being torn down.</summary>
		public void Shutdown() => _shutdown.Cancel();

		/// <summary>Kicks off the warning background refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = RefreshWarningsInBackgroundAsync();

		// Warnings are short-fused (issued/expiring on ~30-45 min cycles, new ones appearing continuously).
		// The endpoint sends Cache-Control: max-age=0 (revalidated per request, no edge-TTL floor), so a fetch
		// genuinely gets fresher data. WHEN to fetch is the live-feed hub's (LiveFeedChannel): at once when a
		// TOR/SVR/FFW/statement is issued while a live source is connected, else the user's polling interval
		// (Settings → Live Data). The old adaptive 15 s / 60 s poll is gone (2026-10-19).
		private readonly LiveFeedChannel _live;

		private Task RefreshWarningsInBackgroundAsync() => BackgroundRefresh.RunLiveAsync(_live, async first =>
		{
			try
			{
				Status.Checking();
				var result = await _warningService.RefreshAsync();
				_logger.LogInformation("Warnings refresh: {Status} active={Active} {Message}", result.Status, result.ActiveCount, result.Message);

				// A completed fetch tells us the current active state; a failure leaves the prior state.
				if (result.Status is WarningFetchStatus.Updated)
				{
					Status.Succeeded();

					// Push the per-type counts to the NowCast readout on the UI thread, then reload the map.
					_dispatcher.Post(() =>
					{
						_threats = result.Threats ?? WarningThreatCounts.None;
							OnPropertyChanged(nameof(TornadoThreats));
							OnPropertyChanged(nameof(SevereThreats));
							OnPropertyChanged(nameof(FlashFloodThreats));
						ApplyRefreshed(result.ActiveCount, result.TornadoCount, result.SevereCount, result.FlashFloodCount, result.MarineCount);
						RaiseHeaderSquare();
						var targets = result.Targets ?? Array.Empty<Models.WarningTarget>();
						TornadoStepper.Update(targets);
						SevereStepper.Update(targets);
						FlashFloodStepper.Update(targets);
						_day.SetActive(targets, DateTimeOffset.UtcNow);
						RebuildAlertList();
						RepushSource();
					});
				}
				else if (first)
				{
					Status.Failed();
					// First cycle with no data yet — still point the page at the (empty) cache.
					_dispatcher.Post(() =>
					{
						ApplyRefreshFailed(result.Message);
						RepushSource();
					});
				}
				else
				{
					Status.Failed();
					_dispatcher.Post(() => ApplyRefreshFailed(result.Message));
				}
			}
			catch (Exception ex)
			{
				Status.Failed();
				_logger.LogWarning(ex, "Warnings refresh aborted");
			}

			// The day list's ended warnings — DETACHED, so a slow IEM never holds the next CAP check.
			_ = FetchDayIfDueAsync();

			return _live.Schedule(Status);
		}, _shutdown.Token);
	}
}

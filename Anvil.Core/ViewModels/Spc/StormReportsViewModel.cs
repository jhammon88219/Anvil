using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// View model for the SPC storm-reports verification overlay — the filtered Tornado / Wind / Hail
	/// reports SPC uses to verify its outlooks, drawn as colored dots so you can pull up an outlook and see
	/// how it verified. Works in BOTH temporal modes off one map overlay: in PastCast it shows the replay
	/// day's reports (keyed to <see cref="RadarViewModel.ReplayStartUtc"/>, immutable), in NowCast today's
	/// (re-fetched on a background loop, since the day's reports accumulate). Per-type toggles filter which
	/// dots show; the reports are always keyed to the SPC convective day (12Z→12Z), so they line up with the
	/// outlook that was valid over that window. Fetch/cache is in <see cref="IStormReportService"/>; the map
	/// is driven through <see cref="IMapService"/>.
	/// </summary>
	public sealed class StormReportsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IStormReportService _reportService;
		private readonly RadarViewModel _radar;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<StormReportsViewModel> _logger;

		private bool _isMapReady;
		private int _applyToken;            // guards a stale async apply when the selection/day changes mid-fetch
		private DateOnly? _loadedDay;       // the convective day whose points are currently on the map (null = none)

		public StormReportsViewModel(IMapService mapService, IStormReportService reportService, RadarViewModel radar, IDispatcher dispatcher, ILogger<StormReportsViewModel> logger)
		{
			_mapService = mapService;
			_reportService = reportService;
			_radar = radar;
			_dispatcher = dispatcher;
			_logger = logger;

			// Re-key the overlay to the new convective day when the temporal mode flips or the replay date
			// changes (only matters while some type is shown; the toggle setters cover the show/hide case).
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Per-type toggles (default all off, so the app launches with no dots) ──

		private bool _showTornado;
		public bool ShowTornado
		{
			get => _showTornado;
			set { if (SetProperty(ref _showTornado, value)) { OnKindToggled(); } }
		}

		private bool _showWind;
		public bool ShowWind
		{
			get => _showWind;
			set { if (SetProperty(ref _showWind, value)) { OnKindToggled(); } }
		}

		private bool _showHail;
		public bool ShowHail
		{
			get => _showHail;
			set { if (SetProperty(ref _showHail, value)) { OnKindToggled(); } }
		}

		/// <summary>
		/// Whether this section has a day to act on at all — always true in NowCast (today is always a day),
		/// and in PastCast only once a replay window has been LOADED.
		/// </summary>
		/// <remarks>
		/// ⚠️ It gates the controls, not just the readout: before a window is loaded there is no convective
		/// day, so a type toggle would have nothing to fetch and nothing to draw.
		/// ⚠️ It has to be a VM property rather than a host binding because this control is shared by BOTH
		/// temporal windows, and the answer differs between them.
		/// </remarks>
		public bool IsReady => !_radar.IsPastEventMode || _radar.HasLoadedReplayWindow;

		/// <summary>Whether ANY type is switched on. False = nothing is drawn, so Opacity has nothing to act
		/// on and the section's controls below the types are disabled — the same off-switch rule the
		/// outlook's "None" follows.</summary>
		public bool AnyShown => _showTornado || _showWind || _showHail;

		// ── Opacity ──

		private double _opacity = 0.9;
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (SetProperty(ref _opacity, value) && _isMapReady)
				{
					_ = _mapService.SetStormReportsOpacityAsync(value);
				}
			}
		}

		// ── Readouts (per-type counts for the card, like the warning "Active" row) ──

		private int _tornadoCount, _windCount, _hailCount;
		public int TornadoCount { get => _tornadoCount; private set => SetProperty(ref _tornadoCount, value); }
		public int WindCount { get => _windCount; private set => SetProperty(ref _windCount, value); }
		public int HailCount { get => _hailCount; private set => SetProperty(ref _hailCount, value); }

		// ── The reports card ──────────────────────────────────────────────────────────────────────
		// The section shows a CARD above its type rows, the same shape as the Timeframe and outlook cards in
		// the same window: headline, context, footer. Written only through SetCard so it cannot be left half
		// describing a previous day.
		//
		// ⚠️ THE FOOTER IS THE ERROR AND PROGRESS CHANNEL, exactly as in PastOutlookViewModel — headline is
		// the answer, footer is what came back. A failed fetch has no sensible headline.
		//
		// ⚠️ THE HEADLINE IS A TOTAL THAT THE ROWS BELOW ALSO BREAK DOWN, which is a real redundancy and was
		// accepted deliberately: the card is the section's summary line, and a section whose card said
		// something the rows did not would be inventing a second fact to display. If it ever reads as noise,
		// the honest fix is to drop the card here rather than to find it different words.

		private string _cardHeadline = "No reports loaded";
		private string _cardContext = string.Empty;
		private string _cardFooterMessage = string.Empty;

		/// <summary>The card's headline: the total across the shown types.</summary>
		public string CardHeadline
		{
			get => _cardHeadline;
			private set => SetProperty(ref _cardHeadline, value);
		}

		/// <summary>The card's middle line: which convective day the counts are for.</summary>
		public string CardContext
		{
			get => _cardContext;
			private set => SetProperty(ref _cardContext, value);
		}

		/// <summary>
		/// The card's footer: loading and failure messages, and — when there is nothing to report — the fact
		/// that no type is switched on.
		/// </summary>
		/// <remarks>
		/// ⚠️ COMPOSED, not stored, because two different things want this line. The counts now populate with
		/// every type off, so the card can truthfully say "448 reports" while the map is empty; without the
		/// second clause that reads as a bug. A real message always wins — an error is more urgent than a
		/// filter being empty.
		/// </remarks>
		public string CardFooter =>
			_cardFooterMessage.Length > 0 ? _cardFooterMessage :
			AnyShown ? string.Empty :
			"None shown — pick a type below";

		private const string NoReportsHeadline = "No reports";

		private void SetCard(string headline, string context, string footer)
		{
			CardHeadline = headline;
			CardContext = context;
			if (_cardFooterMessage != footer)
			{
				_cardFooterMessage = footer;
				OnPropertyChanged(nameof(CardFooter));
			}
		}

		// ⚠️ The date is stamped "12Z-12Z" because the SPC convective day is NOT the calendar day: reports
		// before 12Z belong to the previous one. Without that the card looks wrong to anyone reading it
		// against a wall clock late in the evening.
		private static string ContextFor(DateOnly day) => $"{day:MMM d, yyyy} · 12Z-12Z";

		// ── Lifecycle ──

		/// <summary>Called by MapViewModel once the map page is ready.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetStormReportsOpacityAsync(_opacity);
			// ⚠️ Unconditional. The counts are this section's READOUT, not a by-product of drawing dots, so
			// they have to be right before any type is switched on. Cost: one report fetch at launch that
			// used to happen only when a box was ticked.
			await EnsureAndShowAsync();
		}

		/// <summary>Kicks off the storm-report background refresh loop (called once at launch). Only does work
		/// while NowCast is showing some report type — a historical day is immutable and never refreshed.</summary>
		public void StartBackgroundRefresh() => _ = RefreshReportsInBackgroundAsync();

		// Today's reports grow through the day; a few-minute refresh keeps the NowCast overlay current.
		private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

		private Task RefreshReportsInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(RefreshInterval, async first =>
		{
			try
			{
				// ⚠️ NOT gated on anything being shown: today's counts are a live readout even with every type
				// switched off, exactly as the warning counts in the same window are. Past days are immutable,
				// so replay mode still skips.
				if (!_isMapReady || _radar.IsPastEventMode) { return; }

				var day = TodayConvectiveDay();
				var result = await _reportService.EnsureReportsAsync(day, immutable: false);
				_dispatcher.Post(() => ApplyRefreshed(day, result));
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Storm reports refresh aborted");
			}
		});

		// UI-thread continuation of a background refresh: re-point the page at the (freshly cached) file so it
		// reloads, and update the counts — but only if we're still in the state that asked for it.
		private void ApplyRefreshed(DateOnly day, StormReportResult result)
		{
			if (!_isMapReady || _radar.IsPastEventMode || !result.Found) { return; }
			SetCounts(result);
			_loadedDay = day;
			_ = _mapService.SetStormReportsSourceAsync(_reportService.LocalUrl(day));
			SetCard(SummaryFor(result), ContextFor(day), string.Empty);
		}

		// ── Reactions ──

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			// ⚠️ A LOAD, not a picker move. HasLoadedReplayWindow is re-raised by every successful load (and
			// by leaving replay), which is exactly when the replay day this overlay follows can change. The
			// date/time properties are deliberately NOT watched: they describe the window Load would set up
			// next, so reacting to them fetched reports for a day the user had not committed to yet.
			if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode)
				or nameof(RadarViewModel.HasLoadedReplayWindow))
			{
				OnPropertyChanged(nameof(IsReady)); // both names change the answer
				// Still deduped on the DAY: re-loading the same window, or a load whose window sits inside the
				// convective day already showing, has nothing new to fetch.
				if (_isMapReady && ActiveDay() != _loadedDay) { _ = EnsureAndShowAsync(); }
			}
		}

		// A type checkbox flipped: if we already have the right day loaded, just re-filter (cheap, no fetch);
		// otherwise (first enable, or the day changed) fetch + show. When nothing is shown, push the (empty)
		// filter so the dots hide without tearing the source down.
		private void OnKindToggled()
		{
			// ⚠️ Raised here rather than in the three setters: all of them land here, and AnyShown depends on
			// all three, so one place cannot miss a case. CardFooter goes with it — its second clause reads
			// AnyShown, so the "none shown" line has to appear and clear with the checkboxes.
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(CardFooter));

			if (!_isMapReady) { return; }
			// ⚠️ A LIVE day is refetched when a type is switched on, a past one is not: today's reports
			// accumulate through the day, a historical day is immutable. Without the live case the counts
			// fetched at launch would still be on screen when you finally tick a box in the evening.
			if (AnyShown && (_loadedDay != ActiveDay() || !_radar.IsPastEventMode))
			{
				_ = EnsureAndShowAsync();
			}
			else
			{
				_ = _mapService.SetStormReportKindsAsync(_showTornado, _showWind, _showHail);
			}
		}

		// ── Core ──

		// The convective day the overlay should show: the LOADED replay day in PastCast, today otherwise.
		// Null means there is nothing to show yet — PastCast is up but no window has been loaded.
		//
		// ⚠️ IT READS THE LOADED WINDOW, NOT THE PICKERS. Between editing a date and pressing Load the two
		// disagree, and following the pickers meant the counts described a day that was not on the map —
		// they populated the moment you touched the date, before Load had done anything.
		private DateOnly? ActiveDay() =>
			_radar.IsPastEventMode
				? _radar.LoadedReplayStartUtc is { } start ? ConvectiveDay(start) : null
				: TodayConvectiveDay();

		// Fetch (past = immutable/cache-forever, live = re-fetch) the active day's reports, publish the
		// counts + card, and push the dots for whichever types are switched on.
		//
		// ⚠️ IT RUNS WITH EVERY TYPE OFF, and that is the point. It used to return early unless something was
		// shown, so the numbers sat at zero until you ticked a box — which read as broken data rather than as
		// an empty filter. The counts describe the day; the checkboxes only decide what is DRAWN, and pushing
		// an all-false kind filter simply draws nothing.
		private async Task EnsureAndShowAsync()
		{
			if (!_isMapReady) { return; }

			var token = ++_applyToken;
			if (ActiveDay() is not { } day)
			{
				// PastCast is up but nothing has been loaded — there is no day to report on yet.
				_loadedDay = null;
				TornadoCount = 0;
				WindCount = 0;
				HailCount = 0;
				SetCard("No reports loaded", string.Empty, "Load a timeframe to see its reports");
				return;
			}
			var immutable = _radar.IsPastEventMode;
			SetCard(CardHeadline, ContextFor(day), "Loading…");

			var result = await _reportService.EnsureReportsAsync(day, immutable);
			if (token != _applyToken) { return; } // a newer day/selection won

			if (!result.Found)
			{
				SetCard(NoReportsHeadline, ContextFor(day), result.Error ?? "Storm reports unavailable.");
				return;
			}

			SetCounts(result);
			_loadedDay = day;
			await _mapService.SetStormReportsSourceAsync(_reportService.LocalUrl(day));
			await _mapService.SetStormReportKindsAsync(_showTornado, _showWind, _showHail);
			await _mapService.SetStormReportsOpacityAsync(_opacity);
			SetCard(SummaryFor(result), ContextFor(day), string.Empty);
		}

		private void SetCounts(StormReportResult result)
		{
			TornadoCount = result.Tornado;
			WindCount = result.Wind;
			HailCount = result.Hail;
		}

		// The card's headline. ⚠️ The TOTAL, not the shown subset: the per-type rows underneath already say
		// which types are on, and a headline that moved every time a checkbox flipped would read as the data
		// changing rather than the filter.
		private static string SummaryFor(StormReportResult result)
		{
			var total = result.Tornado + result.Wind + result.Hail;
			return total == 1 ? "1 report" : $"{total} reports";
		}

		// The SPC "convective day" (12Z→12Z) containing an instant — the date SPC files that day's reports
		// under (before 12Z belongs to the previous convective day). Matches the outlook's valid window.
		private static DateOnly ConvectiveDay(DateTimeOffset instant)
		{
			var d = instant.UtcDateTime;
			return DateOnly.FromDateTime(d.Hour >= 12 ? d : d.AddDays(-1));
		}

		private static DateOnly TodayConvectiveDay() => ConvectiveDay(DateTimeOffset.UtcNow);
	}
}

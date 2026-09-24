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
	/// View model for the NWS damage-survey overlay (the Damage Assessment Toolkit's tornado polygons,
	/// tracks and survey points) — PastCast only. Everything is keyed to the LOADED replay window: a
	/// surveyed tornado shows when its time on the ground overlaps that window. Three layer toggles;
	/// polygons and tracks are ticked by default. Tracks come from the DAT and, for anything the DAT lacks,
	/// NCEI Storm Events (1950 on). Fetch/cache/link/filter is in
	/// <see cref="IDamageSurveyService"/>; the map is driven through <see cref="IMapService"/>.
	/// </summary>
	/// <remarks>
	/// Same shape as <see cref="StormReportsViewModel"/> on purpose — card, per-layer rows with counts,
	/// select-all header, opacity, mode gate, and the same ⚠️ token/dedupe discipline — minus the live
	/// refresh loop: PastCast is the only mode that shows it, so there is no "today" to keep current, and
	/// hence no Shutdown.
	/// </remarks>
	public sealed class DamageSurveysViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IDamageSurveyService _surveyService;
		private readonly RadarViewModel _radar;
		private readonly ILogger<DamageSurveysViewModel> _logger;

		private bool _isMapReady;
		private int _applyToken;                                // a newer run supersedes an older one's map push
		private (DateTimeOffset Start, DateTimeOffset End)? _loadedWindow;   // what is on the map (null = none)
		// ⚠️ IN-FLIGHT GUARD, as in StormReportsViewModel: leaving replay raises two watched names from ONE
		// setter, and the token alone does not stop both runs starting.
		private bool _fetchInFlight;
		private (DateTimeOffset Start, DateTimeOffset End)? _inFlightWindow;

		public DamageSurveysViewModel(IMapService mapService, IDamageSurveyService surveyService, RadarViewModel radar, ILogger<DamageSurveysViewModel> logger)
		{
			_mapService = mapService;
			_surveyService = surveyService;
			_radar = radar;
			_logger = logger;
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Per-layer toggles ──
		// ⚠️ POLYGONS AND TRACKS ARE TICKED BY DEFAULT (the user's call, revised from polygons-only): the
		// point of the section is the tornado's path under the radar loop, and most tornadoes — every
		// pre-DAT one, and most today — have a track but no polygon. Points are detail, off by default.
		// Like every overlay tick, it means "draw while PastCast runs" (IsModeActive).

		private bool _showAreas = true;
		public bool ShowAreas
		{
			get => _showAreas;
			set { if (SetProperty(ref _showAreas, value)) { OnKindToggled(); } }
		}

		private bool _showTracks = true;
		public bool ShowTracks
		{
			get => _showTracks;
			set { if (SetProperty(ref _showTracks, value)) { OnKindToggled(); } }
		}

		private bool _showPoints;
		public bool ShowPoints
		{
			get => _showPoints;
			set { if (SetProperty(ref _showPoints, value)) { OnKindToggled(); } }
		}

		/// <summary>Whether there is a loaded replay window to act on. Gates the controls, as in the storm
		/// reports section: before a Load there is nothing to fetch and nothing to draw.</summary>
		public bool IsReady => _radar.IsPastEventMode && _radar.HasLoadedReplayWindow;

		public bool AnyShown => _showAreas || _showTracks || _showPoints;

		/// <summary>The section header's tri-state: true = every layer ticked, false = none, null = some.</summary>
		public bool? AllShown => _showAreas && _showTracks && _showPoints ? true : AnyShown ? null : false;

		/// <summary>The section header checkbox: ticks every layer unless all are ticked, then clears them.</summary>
		public void ToggleAll()
		{
			if (!IsReady) { return; }
			var on = AllShown != true;
			// Fields, then ONE OnKindToggled — three setters would push three filters.
			_showAreas = on;
			_showTracks = on;
			_showPoints = on;
			OnPropertyChanged(nameof(ShowAreas));
			OnPropertyChanged(nameof(ShowTracks));
			OnPropertyChanged(nameof(ShowPoints));
			OnKindToggled();
		}

		private bool _isModeActive;

		/// <summary>Whether PastCast is running — set by <c>MapViewModel</c>. The overlay draws only while it is.</summary>
		public bool IsModeActive
		{
			get => _isModeActive;
			set { if (SetProperty(ref _isModeActive, value)) { ApplyKinds(); } }
		}

		// ⚠️ THE ONE PLACE THE LAYER FILTER IS PUSHED, so the mode gate cannot be skipped by any path.
		private Task PushKindsAsync() => _mapService.SetDamageSurveyKindsAsync(
			_showAreas && _isModeActive, _showTracks && _isModeActive, _showPoints && _isModeActive);

		// ── Opacity ──

		private double _opacity = 0.7;
		public double Opacity
		{
			get => _opacity;
			set
			{
				if (SetProperty(ref _opacity, value) && _isMapReady)
				{
					_ = _mapService.SetDamageSurveysOpacityAsync(value);
				}
			}
		}

		// ── Readouts ──

		private int _areaCount, _trackCount, _pointCount;
		public int AreaCount { get => _areaCount; private set => SetProperty(ref _areaCount, value); }
		public int TrackCount { get => _trackCount; private set => SetProperty(ref _trackCount, value); }
		public int PointCount { get => _pointCount; private set => SetProperty(ref _pointCount, value); }

		// ── The card ─────────────────────────────────────────────────────────────────────────────
		// Headline = how many tornado tracks the window caught; context = the
		// window; footer = progress/errors, else a note about what the ticks leave out.

		private string _cardHeadline = "No surveys loaded";
		private string _cardContext = string.Empty;
		private string _cardFooterMessage = string.Empty;

		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }
		public string CardContext { get => _cardContext; private set => SetProperty(ref _cardContext, value); }

		/// <summary>
		/// The card's footer. A real message (loading, failure) wins; otherwise it explains an empty map.
		/// </summary>
		/// <remarks>
		/// ⚠️ THE POLYGON-COVERAGE LINE IS LOAD-BEARING. Only some offices draw damage polygons, so with the
		/// default ticks a window can hold several surveyed tornadoes and draw nothing. Without this line
		/// that reads as missing data; with it, the fix (tick Tracks) is one glance away.
		/// </remarks>
		public string CardFooter =>
			_cardFooterMessage.Length > 0 ? _cardFooterMessage :
			!AnyShown ? "None shown — pick a layer below" :
			_showAreas && !_showTracks && _areaCount == 0 && _trackCount > 0
				? "No damage polygons for these tornadoes — only some offices draw them. Tick Tracks to see every path."
				: string.Empty;

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

		// The loaded window in local time, as the Timeframe card writes it.
		private static string ContextFor((DateTimeOffset Start, DateTimeOffset End) w)
		{
			var s = w.Start.ToLocalTime();
			var e = w.End.ToLocalTime();
			return $"{s:ddd MMM d, yyyy} · {s:h:mm tt} → {e:h:mm tt}";
		}

		// ⚠️ It counts TRACKS and says so, rather than "tornadoes": some polygons have no DAT track at all
		// (most of 2011-04-27's), so "N tornadoes" from the track count would undercount those days.
		private static string SummaryFor(DamageSurveyResult r) => r.Tracks switch
		{
			0 when r.Areas == 0 && r.Points == 0 => "No surveyed tornadoes",
			0 => "Survey damage, no tracks",
			1 => "1 tornado track",
			var n => $"{n} tornado tracks",
		};

		// ── Lifecycle ──

		/// <summary>Called by MapViewModel once the map page is ready.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetDamageSurveysOpacityAsync(_opacity);
			await EnsureAndShowAsync();
		}

		// ── Reactions ──

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			// ⚠️ A LOAD, not a picker move — same rule and same two names as the storm reports. The pickers
			// describe what Load would do next; the overlay follows what is on the map.
			if (e.PropertyName is nameof(RadarViewModel.IsPastEventMode)
				or nameof(RadarViewModel.HasLoadedReplayWindow))
			{
				OnPropertyChanged(nameof(IsReady));
				if (_isMapReady && !Equals(ActiveWindow(), _loadedWindow)) { _ = EnsureAndShowAsync(); }
			}
		}

		private void OnKindToggled()
		{
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(AllShown));
			OnPropertyChanged(nameof(CardFooter));
			ApplyKinds();
		}

		// A tick or mode flip: the window's file is already written whenever one is loaded (counts are the
		// readout), so this only ever re-filters — unless the loaded window has moved on underneath us.
		private void ApplyKinds()
		{
			if (!_isMapReady) { return; }
			if (AnyShown && _isModeActive && !Equals(_loadedWindow, ActiveWindow())) { _ = EnsureAndShowAsync(); }
			else { _ = PushKindsAsync(); }
		}

		// ── Core ──

		// The LOADED replay window, or null when PastCast has nothing loaded (or is not running).
		private (DateTimeOffset Start, DateTimeOffset End)? ActiveWindow() =>
			_radar.IsPastEventMode && _radar.LoadedReplayStartUtc is { } s && _radar.LoadedReplayEndUtc is { } e
				? (s, e)
				: null;

		private async Task EnsureAndShowAsync()
		{
			if (!_isMapReady) { return; }

			var active = ActiveWindow();

			// ⚠️⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS — see the
			// same block in StormReportsViewModel for the bug this ordering fixed.
			if (active is { } busy && _fetchInFlight && Equals(_inFlightWindow, busy)) { return; }

			var token = ++_applyToken;
			if (active is not { } window)
			{
				AreaCount = 0;
				TrackCount = 0;
				PointCount = 0;
				SetCard("No surveys loaded", string.Empty, "Load a timeframe to see its damage surveys");
				await ClearOverlayAsync();
				return;
			}

			SetCard(CardHeadline, ContextFor(window), "Loading…");

			DamageSurveyResult result;
			_fetchInFlight = true;
			_inFlightWindow = window;
			try
			{
				result = await _surveyService.EnsureWindowAsync(window.Start, window.End);
			}
			catch (Exception ex)
			{
				// Fire-and-forget caller: an uncaught throw would leave the previous window drawn.
				_logger.LogWarning(ex, "Damage surveys fetch failed for {Start}–{End}", window.Start, window.End);
				if (token == _applyToken)
				{
					SetCard("No surveys", ContextFor(window), "NWS damage surveys unavailable.");
					await ClearOverlayAsync();
				}
				return;
			}
			finally
			{
				_fetchInFlight = false;
				_inFlightWindow = null;
			}

			if (token != _applyToken) { return; }

			if (!result.Found)
			{
				SetCard("No surveys", ContextFor(window), result.Error ?? "NWS damage surveys unavailable.");
				await ClearOverlayAsync();
				return;
			}

			AreaCount = result.Areas;
			TrackCount = result.Tracks;
			PointCount = result.Points;
			_loadedWindow = window;
			await _mapService.SetDamageSurveysSourceAsync(_surveyService.LocalUrl(window.Start, window.End));
			await PushKindsAsync();
			await _mapService.SetDamageSurveysOpacityAsync(_opacity);
			// The counts moved, and the coverage line reads them.
			SetCard(SummaryFor(result), ContextFor(window), string.Empty);
			OnPropertyChanged(nameof(CardFooter));
		}

		private async Task ClearOverlayAsync()
		{
			_loadedWindow = null;
			await _mapService.ClearDamageSurveysAsync();
		}
	}
}

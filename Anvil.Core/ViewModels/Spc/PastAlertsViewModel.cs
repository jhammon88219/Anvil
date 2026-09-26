using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// PastCast's warnings and watches: what NWS/SPC had in effect at the DISPLAYED radar frame of the loaded
	/// replay, drawn exactly like NowCast's (same colours, tiers and county-filled watches) on overlays of
	/// their own. Fetch is <see cref="IPastAlertService"/> (IEM's VTEC archive), once per loaded window.
	/// </summary>
	/// <remarks>
	/// ⚠️ A COORDINATOR OVER TWO ORDINARY PHENOM OVERLAYS. <see cref="Warnings"/> and <see cref="Watches"/>
	/// are <see cref="PhenomOverlayViewModel"/>s, so the PastCast rows, select-all, draw gate, opacity and
	/// card are the NowCast ones; this class owns what they share — the window fetch and the frame time.
	/// <para>⚠️ FOLLOWS THE SCRUBBER, like the storm cells: every alert of the window is in the page file with
	/// its own t0/t1, and each frame change pushes only the time (and recounts here).</para>
	/// <para>⚠️ SEPARATE MAP LAYERS FROM NOWCAST (past-alerts.js), not a hand-off of the live ones: the live
	/// warnings VM re-points its layer every 15–60 s, which would pull today's polygons over a replay.</para>
	/// </remarks>
	public sealed class PastAlertsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IPastAlertService _service;
		private readonly RadarViewModel _radar;
		private readonly ILogger<PastAlertsViewModel> _logger;

		private bool _isMapReady;
		private int _applyToken;
		private (DateTimeOffset Start, DateTimeOffset End)? _loadedWindow;
		private (DateTimeOffset Start, DateTimeOffset End)? _inFlightWindow;
		private IReadOnlyList<PastAlert> _warnings = Array.Empty<PastAlert>();
		private IReadOnlyList<PastAlert> _watches = Array.Empty<PastAlert>();
		private DateTimeOffset? _frameTime;

		public PastAlertsViewModel(IMapService mapService, IPastAlertService service, RadarViewModel radar, ILogger<PastAlertsViewModel> logger)
		{
			_mapService = mapService;
			_service = service;
			_radar = radar;
			_logger = logger;
			Warnings = new PastPhenomOverlay(mapService, "warnings", "warning", "warnings", flashFlood: true);
			Watches = new PastPhenomOverlay(mapService, "watches", "watch", "watches", flashFlood: false);
			_radar.PropertyChanged += OnRadarChanged;
		}

		/// <summary>The PastCast warning polygons (tornado / severe / flash flood, every follow-up version).</summary>
		public PastPhenomOverlay Warnings { get; }

		/// <summary>The PastCast watches, county-filled (tornado / severe).</summary>
		public PastPhenomOverlay Watches { get; }

		private bool _isModeActive;

		/// <summary>Whether PastCast is running — set by <c>MapViewModel</c>; gates both overlays.</summary>
		public bool IsModeActive
		{
			get => _isModeActive;
			set
			{
				if (!SetProperty(ref _isModeActive, value)) { return; }
				Warnings.IsModeActive = value;
				Watches.IsModeActive = value;
				Reconcile();
			}
		}

		public async Task OnMapsReadyAsync()
		{
			await Warnings.OnMapsReadyAsync();
			await Watches.OnMapsReadyAsync();
			_isMapReady = true;
			_frameTime = _radar.DisplayedFrameTimeUtc;
			await _mapService.SetPastAlertTimeAsync(_frameTime?.ToUnixTimeMilliseconds());
			Reconcile();
		}

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(RadarViewModel.DisplayedFrameTimeUtc):
					if (_radar.DisplayedFrameTimeUtc == _frameTime) { return; }
					_frameTime = _radar.DisplayedFrameTimeUtc;
					if (_isMapReady) { _ = _mapService.SetPastAlertTimeAsync(_frameTime?.ToUnixTimeMilliseconds()); }
					Recount();
					break;
				// ⚠️ A LOAD, not a picker move — the same two names every PastCast overlay keys on.
				case nameof(RadarViewModel.IsPastEventMode):
				case nameof(RadarViewModel.HasLoadedReplayWindow):
					Reconcile();
					break;
			}
		}

		private (DateTimeOffset Start, DateTimeOffset End)? ActiveWindow() =>
			_isModeActive && _radar.IsPastEventMode && _radar.LoadedReplayStartUtc is { } s && _radar.LoadedReplayEndUtc is { } e
				? (s, e)
				: null;

		private void Reconcile()
		{
			if (!_isMapReady) { return; }
			var want = ActiveWindow();
			if (want is null)
			{
				if (_loadedWindow is not null) { Clear(); }
				Warnings.SetIdle();
				Watches.SetIdle();
				return;
			}
			if (!Equals(want, _loadedWindow)) { _ = EnsureAsync(want.Value); }
		}

		private async Task EnsureAsync((DateTimeOffset Start, DateTimeOffset End) window)
		{
			// ⚠️⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS.
			if (Equals(_inFlightWindow, window)) { return; }
			var token = ++_applyToken;
			Warnings.SetMessage("Loading…");
			Watches.SetMessage("Loading…");

			PastAlertFetch fetch;
			_inFlightWindow = window;
			try
			{
				fetch = await _service.FetchAsync(window.Start, window.End);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "PastCast alerts fetch failed for {Start}–{End}", window.Start, window.End);
				fetch = PastAlertFetch.Failed("IEM warning archive unavailable.");
			}
			finally
			{
				_inFlightWindow = null;
			}

			if (token != _applyToken) { return; }
			if (!fetch.Found)
			{
				Clear();
				Warnings.SetMessage(fetch.Error ?? "IEM warning archive unavailable.");
				Watches.SetMessage(fetch.Error ?? "IEM warning archive unavailable.");
				return;
			}

			_loadedWindow = window;
			_warnings = fetch.Warnings;
			_watches = fetch.Watches;
			Warnings.PointAt(fetch.WarningsUrl);
			Watches.PointAt(fetch.WatchesUrl);
			Recount();
		}

		// What was in effect at the displayed frame — one count per ALERT (a warning's versions and a watch's
		// counties share a key), taking a warning's CURRENT version's tier.
		private void Recount()
		{
			if (_loadedWindow is null) { return; }
			if (_frameTime is not { } t)
			{
				Warnings.ShowAt(null, Array.Empty<PastAlert>());
				Watches.ShowAt(null, Array.Empty<PastAlert>());
				return;
			}
			Warnings.ShowAt(t, InEffect(_warnings, t));
			Watches.ShowAt(t, InEffect(_watches, t));
		}

		internal static List<PastAlert> InEffect(IReadOnlyList<PastAlert> alerts, DateTimeOffset t) =>
			alerts.Where(a => a.IsInEffectAt(t))
				.GroupBy(a => a.Key)
				.Select(g => g.OrderByDescending(a => a.Start).First())
				.ToList();

		private void Clear()
		{
			++_applyToken;
			_loadedWindow = null;
			_warnings = Array.Empty<PastAlert>();
			_watches = Array.Empty<PastAlert>();
			Warnings.PointAt(string.Empty);
			Watches.PointAt(string.Empty);
		}
	}

	/// <summary>
	/// One PastCast alert overlay — the NowCast <see cref="PhenomOverlayViewModel"/> rows and card, drawing
	/// on past-alerts.js's layer of <paramref name="kind"/>, fed by <see cref="PastAlertsViewModel"/>.
	/// </summary>
	public sealed class PastPhenomOverlay : PhenomOverlayViewModel
	{
		private readonly IMapService _map;
		private readonly string _kind;
		private readonly string _singular;
		private readonly string _plural;
		private readonly bool _flashFlood;
		private string _url = string.Empty;
		private string _context = string.Empty;
		private WarningThreatCounts _threats = WarningThreatCounts.None;

		internal PastPhenomOverlay(IMapService map, string kind, string singular, string plural, bool flashFlood)
		{
			_map = map;
			_kind = kind;
			_singular = singular;
			_plural = plural;
			_flashFlood = flashFlood;
		}

		public override bool SupportsFlashFlood => _flashFlood;

		protected override string SourceUrl => _url;
		protected override Task SetVisibleAsync(bool visible) => _map.SetPastAlertVisibleAsync(_kind, visible);
		protected override Task SetOpacityAsync(double opacity) => _map.SetPastAlertOpacityAsync(_kind, opacity);
		protected override Task SetSourceAsync(string url) => _map.SetPastAlertSourceAsync(_kind, url);
		protected override Task SetKindsAsync(bool tornado, bool severe, bool flashFlood) =>
			_map.SetPastAlertKindsAsync(_kind, tornado, severe, flashFlood);

		protected override string ItemNounSingular => _singular;
		protected override string ItemNounPlural => _plural;

		/// <summary>Which moment the counts describe (a replay has no "Updated…").</summary>
		public override string CardContext => _context;

		public override string CardThreats => _flashFlood ? _threats.ToCardLine() : string.Empty;

		internal void PointAt(string url)
		{
			_url = url;
			RepushSource();
		}

		internal void SetMessage(string message) => ApplyRefreshFailed(message);

		internal void SetIdle()
		{
			_context = string.Empty;
			_threats = WarningThreatCounts.None;
			ApplyRefreshed(0, 0, 0, 0);
			ApplyRefreshFailed(IsModeActive ? "Load a timeframe to see what was in effect" : string.Empty);
		}

		/// <summary>The counts at the displayed frame <paramref name="t"/> (null = no frame yet).</summary>
		internal void ShowAt(DateTimeOffset? t, IReadOnlyList<PastAlert> inEffect)
		{
			_context = t is { } at ? $"In effect at {at.ToLocalTime():h:mm tt} · {at.ToLocalTime():MMM d, yyyy}" : "Waiting for a radar frame…";
			_threats = WarningThreatCounts.From(inEffect.Select(a => (a.Phenom, a.Tier)));
			ApplyRefreshed(inEffect.Count,
				inEffect.Count(a => a.Phenom == "TO"),
				inEffect.Count(a => a.Phenom == "SV"),
				inEffect.Count(a => a.Phenom == "FF"));
		}
	}
}

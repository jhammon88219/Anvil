using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Anvil.Models;
using Anvil.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Anvil.ViewModels
{
	/// <summary>
	/// The radar's own storm-cell attributes on the map — cell tracks (past + 15/30/45/60-min forecast),
	/// TVS, mesocyclones and hail — for the LOADED site, in both NowCast and PastCast. Fetch/cache/track
	/// building is <see cref="IStormCellService"/> + <see cref="StormCellTracks"/>; the page draws one scan.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE CELLS FOLLOW THE SCRUBBER, NOT THE CLOCK. Every scan of the window is in the page file; this VM
	/// picks the one for <see cref="RadarViewModel.DisplayedFrameTimeUtc"/> (<see cref="StormCellTracks.SelectScan"/>)
	/// and pushes only its TIME, so playback costs one tiny call per scan change and the counts on the rows
	/// always describe the frame on screen.
	/// <para>Same shape as <see cref="DamageSurveysViewModel"/> — card, per-layer rows with counts, select-all
	/// header, opacity, mode gate, and the ⚠️ token/dedupe discipline — plus a live refresh loop.</para>
	/// </remarks>
	public sealed class StormCellsViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly IStormCellService _service;
		private readonly RadarViewModel _radar;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<StormCellsViewModel> _logger;

		// Live: a rolling window this long, re-fetched on this cadence (IEM updates once per volume).
		private static readonly TimeSpan LiveSpan = TimeSpan.FromHours(2);
		private static readonly TimeSpan LiveRefresh = TimeSpan.FromMinutes(2);

		private readonly CancellationTokenSource _shutdown = new();
		private bool _isMapReady;
		private int _applyToken;
		private FetchKey? _loadedKey;     // what the page file describes
		private FetchKey? _inFlightKey;   // ⚠️ in-flight guard, as in DamageSurveysViewModel
		private IReadOnlyList<StormCellScan> _scans = Array.Empty<StormCellScan>();
		private int _scanIndex = -1;
		private long? _pushedScanMs;
		private DateTimeOffset? _frameTime;

		// Live windows move, so a live key is just the site: a refresh re-fetches the same key.
		private sealed record FetchKey(string Site, bool Live, DateTimeOffset Start, DateTimeOffset End);

		public StormCellsViewModel(IMapService mapService, IStormCellService service, RadarViewModel radar,
			IDispatcher dispatcher, ILogger<StormCellsViewModel> logger)
		{
			_mapService = mapService;
			_service = service;
			_radar = radar;
			_dispatcher = dispatcher;
			_logger = logger;
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Per-layer toggles (all ticked by default; a tick means "draw while the mode runs") ──

		private bool _showTracks = true, _showTvs = true, _showMeso = true, _showHail = true;

		/// <summary>Cell centroids, ids, past tracks and forecast tracks.</summary>
		public bool ShowTracks { get => _showTracks; set { if (SetProperty(ref _showTracks, value)) { OnKindToggled(); } } }

		/// <summary>Tornado vortex signatures (TVS and elevated ETVS).</summary>
		public bool ShowTvs { get => _showTvs; set { if (SetProperty(ref _showTvs, value)) { OnKindToggled(); } } }

		/// <summary>Mesocyclones — MDA strength rank 5 and up.</summary>
		public bool ShowMeso { get => _showMeso; set { if (SetProperty(ref _showMeso, value)) { OnKindToggled(); } } }

		/// <summary>Hail cells — probability of severe hail 50% and up.</summary>
		public bool ShowHail { get => _showHail; set { if (SetProperty(ref _showHail, value)) { OnKindToggled(); } } }

		public bool AnyShown => _showTracks || _showTvs || _showMeso || _showHail;

		/// <summary>The section header's tri-state: true = every layer ticked, false = none, null = some.</summary>
		public bool? AllShown => _showTracks && _showTvs && _showMeso && _showHail ? true : AnyShown ? null : false;

		/// <summary>The section header checkbox: ticks every layer unless all are ticked, then clears them.</summary>
		public void ToggleAll()
		{
			var on = AllShown != true;
			// Fields, then ONE OnKindToggled — four setters would push four filters.
			_showTracks = _showTvs = _showMeso = _showHail = on;
			OnPropertyChanged(nameof(ShowTracks));
			OnPropertyChanged(nameof(ShowTvs));
			OnPropertyChanged(nameof(ShowMeso));
			OnPropertyChanged(nameof(ShowHail));
			OnKindToggled();
		}

		private bool _isModeActive;

		/// <summary>Whether NowCast or PastCast is running — set by <c>MapViewModel</c>. Draws only while it is.</summary>
		public bool IsModeActive
		{
			get => _isModeActive;
			set
			{
				if (!SetProperty(ref _isModeActive, value)) { return; }
				_ = PushKindsAsync();
				Reconcile();
			}
		}

		// ⚠️ THE ONE PLACE THE LAYER FILTER IS PUSHED, so the mode gate cannot be skipped by any path.
		private Task PushKindsAsync() => !_isMapReady ? Task.CompletedTask : _mapService.SetStormCellKindsAsync(
			_showTracks && _isModeActive, _showTvs && _isModeActive, _showMeso && _isModeActive, _showHail && _isModeActive);

		private double _opacity = 1.0;
		public double Opacity
		{
			get => _opacity;
			set { if (SetProperty(ref _opacity, value) && _isMapReady) { _ = _mapService.SetStormCellsOpacityAsync(value); } }
		}

		// ── Readouts: the DISPLAYED scan's counts ──

		private int _cellCount, _tvsCount, _mesoCount, _hailCount;
		public int CellCount { get => _cellCount; private set => SetProperty(ref _cellCount, value); }
		public int TvsCount { get => _tvsCount; private set => SetProperty(ref _tvsCount, value); }
		public int MesoCount { get => _mesoCount; private set => SetProperty(ref _mesoCount, value); }
		public int HailCount { get => _hailCount; private set => SetProperty(ref _hailCount, value); }

		// ── The card ──

		private string _cardHeadline = "No storm cells";
		private string _cardContext = string.Empty;
		private string _cardMessage = string.Empty;

		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }
		public string CardContext { get => _cardContext; private set => SetProperty(ref _cardContext, value); }

		/// <summary>A real message (loading, failure, no scan near this frame) wins; else what the ticks leave out.</summary>
		public string CardFooter =>
			_cardMessage.Length > 0 ? _cardMessage :
			!AnyShown ? "None shown — pick a layer below" :
			string.Empty;

		private void SetMessage(string message)
		{
			if (_cardMessage == message) { return; }
			_cardMessage = message;
			OnPropertyChanged(nameof(CardFooter));
		}

		// ── Lifecycle ──

		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;
			await _mapService.SetStormCellsOpacityAsync(_opacity);
			await PushKindsAsync();
			Reconcile();
		}

		/// <summary>Starts the live refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = BackgroundRefresh.RunAdaptiveAsync(first =>
		{
			// The first cycle is the launch itself — Reconcile already runs on map-ready and on every change.
			if (!first) { _dispatcher.Post(() => { if (DesiredKey() is { Live: true }) { _ = EnsureAsync(refresh: true); } }); }
			return Task.FromResult(LiveRefresh);
		}, _shutdown.Token);

		/// <summary>Stops the live refresh loop. Called from <see cref="MapViewModel.Shutdown"/>.</summary>
		public void Shutdown() => _shutdown.Cancel();

		// ── Reactions ──

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(RadarViewModel.DisplayedFrameTimeUtc):
					if (_radar.DisplayedFrameTimeUtc != _frameTime)
					{
						_frameTime = _radar.DisplayedFrameTimeUtc;
						ApplyScan();
					}
					break;
				// ⚠️ A LOAD, not a picker move (same two names as the damage surveys), plus the site itself.
				case nameof(RadarViewModel.SelectedRadarOption):
				case nameof(RadarViewModel.IsPastEventMode):
				case nameof(RadarViewModel.HasLoadedReplayWindow):
					Reconcile();
					break;
			}
		}

		private void OnKindToggled()
		{
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(AllShown));
			OnPropertyChanged(nameof(CardFooter));
			_ = PushKindsAsync();
		}

		// ── Core ──

		// What SHOULD be on the map now, or null when nothing can be.
		private FetchKey? DesiredKey()
		{
			if (!_isModeActive || _radar.SelectedRadarOption?.Site is not { } site) { return null; }
			if (_radar.IsPastEventMode)
			{
				return _radar.LoadedReplayStartUtc is { } s && _radar.LoadedReplayEndUtc is { } e
					? new FetchKey(site.Id, false, s, e)
					: null;
			}
			return new FetchKey(site.Id, true, default, default);
		}

		private void Reconcile()
		{
			if (!_isMapReady) { return; }
			var want = DesiredKey();
			if (want is null)
			{
				if (_loadedKey is not null || _scans.Count > 0) { _ = ClearAsync(); }
				SetIdleCard();
				return;
			}
			if (!Equals(want, _loadedKey)) { _ = EnsureAsync(refresh: false); }
		}

		private void SetIdleCard()
		{
			CardHeadline = "No storm cells";
			CardContext = string.Empty;
			SetMessage(_isModeActive ? "Load a radar site to see its storm cells" : string.Empty);
		}

		private async Task EnsureAsync(bool refresh)
		{
			if (!_isMapReady || DesiredKey() is not { } key) { return; }

			// ⚠️⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS.
			if (Equals(_inFlightKey, key)) { return; }
			if (!refresh && Equals(_loadedKey, key)) { return; }

			if (_radar.SelectedRadarOption?.Site?.Class is { } cls && cls != RadarSiteClass.Operational)
			{
				++_applyToken;
				await ClearAsync();
				CardHeadline = "No storm cells";
				CardContext = key.Site;
				SetMessage("Storm attributes exist for NWS WSR-88D sites only.");
				return;
			}

			var token = ++_applyToken;
			if (!refresh) { SetMessage("Loading…"); }

			var end = key.Live ? DateTimeOffset.UtcNow : key.End;
			var start = key.Live ? end - LiveSpan : key.Start;
			StormCellFetch fetch;
			_inFlightKey = key;
			try
			{
				fetch = await _service.FetchAsync(key.Site, start, end, key.Live);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Storm cells fetch failed for {Site}", key.Site);
				fetch = StormCellFetch.Failed("IEM storm attributes unavailable.");
			}
			finally
			{
				_inFlightKey = null;
			}

			if (token != _applyToken) { return; }

			if (!fetch.Found)
			{
				// A failed LIVE refresh keeps the last good scans on screen; only a first load clears.
				if (!refresh) { await ClearAsync(); }
				SetMessage(fetch.Error ?? "IEM storm attributes unavailable.");
				return;
			}

			_scans = fetch.Scans;
			_loadedKey = key;
			_pushedScanMs = null; // the page re-reads its file, so re-push the scan whatever it was
			await _mapService.SetStormCellsSourceAsync(fetch.Url);
			await PushKindsAsync();
			await _mapService.SetStormCellsOpacityAsync(_opacity);
			SetMessage(string.Empty);
			ApplyScan();
		}

		// Picks the scan for the displayed frame, pushes it if it moved, and refreshes the rows + card.
		private void ApplyScan()
		{
			if (_loadedKey is null) { return; }
			_scanIndex = _frameTime is { } t ? StormCellTracks.SelectScan(_scans, t) : -1;
			var scan = _scanIndex >= 0 ? _scans[_scanIndex] : null;
			var ms = scan?.Valid.ToUnixTimeMilliseconds();
			if (ms != _pushedScanMs && _isMapReady)
			{
				_pushedScanMs = ms;
				_ = _mapService.SetStormCellScanAsync(ms);
			}

			var cells = scan?.Cells ?? (IReadOnlyList<StormCell>)Array.Empty<StormCell>();
			CellCount = cells.Count;
			TvsCount = cells.Count(StormCellThresholds.IsTvs);
			MesoCount = cells.Count(StormCellThresholds.IsMeso);
			HailCount = cells.Count(StormCellThresholds.IsHail);

			CardHeadline = scan is null ? "No storm cells" : cells.Count == 1 ? "1 storm cell" : $"{cells.Count} storm cells";
			CardContext = scan is null
				? _loadedKey.Site
				: $"Scan {scan.Valid.ToLocalTime():h:mm tt}{(_loadedKey.Live ? string.Empty : $" · {scan.Valid.ToLocalTime():MMM d, yyyy}")} · {_loadedKey.Site}";
			if (_cardMessage.Length == 0 || _cardMessage == NoScanMessage)
			{
				SetMessage(scan is null && _frameTime is not null ? NoScanMessage : string.Empty);
			}
		}

		private static readonly string NoScanMessage =
			$"No scan within {StormCellThresholds.MaxFrameLag.TotalMinutes:0} minutes of this frame.";

		private async Task ClearAsync()
		{
			_loadedKey = null;
			_scans = Array.Empty<StormCellScan>();
			_scanIndex = -1;
			_pushedScanMs = null;
			CellCount = TvsCount = MesoCount = HailCount = 0;
			await _mapService.ClearStormCellsAsync();
		}
	}
}

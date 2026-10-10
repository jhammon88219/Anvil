using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
	/// Tropical-cyclone watches and warnings (every <see cref="TropicalProducts"/> product) as forecast zones on the
	/// map — the NowCast and PastCast "Tropical" section. Fetch/cache/naming is <see cref="ITropicalService"/>.
	/// </summary>
	/// <remarks>
	/// ⚠️ THE MESOSCALE DISCUSSIONS' SHAPE (MesoDiscussionsViewModel): LIVE in NowCast (refreshed every
	/// <see cref="LiveRefresh"/>, everything in the file is in effect), the LOADED WINDOW in PastCast (zones carry
	/// t0/t1; the map + counts show the moment of the displayed radar frame — they follow the scrubber).
	/// ⚠️ PRODUCTS ARE DATA: the rows are built from <see cref="TropicalProducts.All"/> and visibility persists as ONE
	/// list of hidden ids — a product added there needs no new property, row or setting.
	/// </remarks>
	public sealed class TropicalViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ITropicalService _service;
		private readonly RadarViewModel _radar;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<TropicalViewModel> _logger;

		private static readonly TimeSpan LiveRefresh = TimeSpan.FromMinutes(2);

		private readonly CancellationTokenSource _shutdown = new();
		private bool _isMapReady;
		private int _applyToken;
		private FetchKey? _loadedKey;
		private FetchKey? _inFlightKey;
		private DateTimeOffset? _moment;
		private IReadOnlyList<TropicalZone> _zones = Array.Empty<TropicalZone>();
		private IReadOnlyDictionary<string, string> _storms = new Dictionary<string, string>();

		private sealed record FetchKey(bool Live, DateTimeOffset Start, DateTimeOffset End);

		public TropicalViewModel(IMapService mapService, ITropicalService service, RadarViewModel radar,
			IDispatcher dispatcher, ILogger<TropicalViewModel> logger)
		{
			_mapService = mapService;
			_service = service;
			_radar = radar;
			_dispatcher = dispatcher;
			_logger = logger;
			Kinds = new ReadOnlyCollection<TropicalKindRow>(
				TropicalProducts.All.Select(p => new TropicalKindRow(p, OnKindToggled)).ToList());
			Warnings = Kinds.Where(k => k.Product.IsWarning).ToList();
			Watches = Kinds.Where(k => !k.Product.IsWarning).ToList();
			_radar.PropertyChanged += OnRadarChanged;
		}

		// ── Kinds (one row per product, NWS priority order) ──

		public IReadOnlyList<TropicalKindRow> Kinds { get; }

		/// <summary>The section's WARNINGS rows.</summary>
		public IReadOnlyList<TropicalKindRow> Warnings { get; }

		/// <summary>The section's WATCHES rows.</summary>
		public IReadOnlyList<TropicalKindRow> Watches { get; }

		public bool AnyShown => Kinds.Any(k => k.IsShown);

		/// <summary>The section header's tri-state.</summary>
		public bool? AllShown => Kinds.All(k => k.IsShown) ? true : AnyShown ? null : false;

		public void ToggleAll()
		{
			var on = AllShown != true;
			foreach (var k in Kinds) { k.SetSilently(on); }
			OnKindToggled();
		}

		/// <summary>The hidden products' ids — the persisted form (null/empty = all shown).</summary>
		public List<string> HiddenKindIds => Kinds.Where(k => !k.IsShown).Select(k => k.Product.Id).ToList();

		/// <summary>Restores <see cref="HiddenKindIds"/>; unknown ids are ignored, unlisted products are shown.</summary>
		public void RestoreHidden(IEnumerable<string> hidden)
		{
			var set = new HashSet<string>(hidden, StringComparer.Ordinal);
			foreach (var k in Kinds) { k.SetSilently(!set.Contains(k.Product.Id)); }
			OnKindToggled();
		}

		/// <summary>Raised when a product is toggled (the persistence hook).</summary>
		public event EventHandler? KindsChanged;

		private void OnKindToggled()
		{
			OnPropertyChanged(nameof(AnyShown));
			OnPropertyChanged(nameof(AllShown));
			OnPropertyChanged(nameof(CardFooter));
			KindsChanged?.Invoke(this, EventArgs.Empty);
			_ = PushKindsAsync();
		}

		private Task PushKindsAsync() => !_isMapReady ? Task.CompletedTask :
			_mapService.SetTropicalKindsAsync(_isModeActive ? string.Join(",", Kinds.Where(k => k.IsShown).Select(k => k.Product.Id)) : string.Empty);

		private bool _isModeActive;

		/// <summary>Whether NowCast or PastCast is running — set by <c>MapViewModel</c>.</summary>
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

		private double _opacity = 1.0;
		public double Opacity
		{
			get => _opacity;
			set { if (SetProperty(ref _opacity, value) && _isMapReady) { _ = _mapService.SetTropicalOpacityAsync(value); } }
		}

		// ── The card ──

		private string _cardHeadline = "No tropical alerts";
		private string _cardContext = string.Empty;
		private string _cardMessage = string.Empty;

		/// <summary>The storm(s) in effect ("Tropical Storm Isaias"), or "No tropical alerts".</summary>
		public string CardHeadline { get => _cardHeadline; private set => SetProperty(ref _cardHeadline, value); }

		/// <summary>"3 products · 76 zones" (+ the moment in PastCast).</summary>
		public string CardContext { get => _cardContext; private set => SetProperty(ref _cardContext, value); }

		// No tick-driven line ("None shown…") — a section's height must not change with what you tick
		// (PhenomOverlayViewModel.CardFooter).
		public string CardFooter => _cardMessage;

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
			await _mapService.SetTropicalOpacityAsync(_opacity);
			await PushKindsAsync();
			Reconcile();
		}

		/// <summary>Starts the live refresh loop (called once at launch).</summary>
		public void StartBackgroundRefresh() => _ = BackgroundRefresh.RunAdaptiveAsync(first =>
		{
			if (!first)
			{
				_dispatcher.Post(() =>
				{
					if (DesiredKey() is { Live: true }) { _ = EnsureAsync(refresh: true); }
				});
			}
			return Task.FromResult(LiveRefresh);
		}, _shutdown.Token);

		public void Shutdown() => _shutdown.Cancel();

		private void OnRadarChanged(object? sender, PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(RadarViewModel.DisplayedFrameTimeUtc):
					if (_loadedKey is { Live: false }) { ApplyMoment(_radar.DisplayedFrameTimeUtc); }
					break;
				// ⚠️ A LOAD, not a picker move — the same two names every PastCast overlay keys on.
				case nameof(RadarViewModel.IsPastEventMode):
				case nameof(RadarViewModel.HasLoadedReplayWindow):
					Reconcile();
					break;
			}
		}

		// ── Core ──

		private FetchKey? DesiredKey()
		{
			if (!_isModeActive) { return null; }
			if (_radar.IsPastEventMode)
			{
				return _radar.LoadedReplayStartUtc is { } s && _radar.LoadedReplayEndUtc is { } e ? new FetchKey(false, s, e) : null;
			}
			return new FetchKey(true, default, default);
		}

		private void Reconcile()
		{
			if (!_isMapReady) { return; }
			var want = DesiredKey();
			if (want is null)
			{
				if (_loadedKey is not null) { _ = ClearAsync(); }
				CardHeadline = "No tropical alerts";
				CardContext = string.Empty;
				SetMessage(_isModeActive && _radar.IsPastEventMode ? "Load a timeframe to see its tropical alerts" : string.Empty);
				return;
			}
			if (!Equals(want, _loadedKey)) { _ = EnsureAsync(refresh: false); }
		}

		private async Task EnsureAsync(bool refresh)
		{
			if (!_isMapReady || DesiredKey() is not { } key) { return; }
			// ⚠️⚠️ DEDUPE BEFORE TAKING A TOKEN, AND NEVER TOUCH _applyToken ON A CALL THAT BAILS.
			if (Equals(_inFlightKey, key)) { return; }
			if (!refresh && Equals(_loadedKey, key)) { return; }

			var token = ++_applyToken;
			if (!refresh) { SetMessage("Loading…"); }

			TropicalFetch fetch;
			_inFlightKey = key;
			try { fetch = key.Live ? await _service.FetchLiveAsync() : await _service.FetchPastAsync(key.Start, key.End); }
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Tropical fetch failed");
				fetch = TropicalFetch.Failed("Tropical alerts unavailable.");
			}
			finally { _inFlightKey = null; }

			if (token != _applyToken) { return; }
			if (!fetch.Found)
			{
				if (!refresh) { await ClearAsync(); }
				SetMessage(fetch.Error ?? "Tropical alerts unavailable.");
				return;
			}

			_loadedKey = key;
			_zones = fetch.Zones;
			_storms = fetch.Storms;
			await _mapService.SetTropicalSourceAsync(fetch.Url);
			await PushKindsAsync();
			await _mapService.SetTropicalOpacityAsync(_opacity);
			SetMessage(fetch.Error ?? string.Empty);
			ApplyMoment(key.Live ? null : _radar.DisplayedFrameTimeUtc, force: true);
		}

		// The moment the map shows and the counts describe: PastCast's displayed frame; null = live (all in effect).
		private void ApplyMoment(DateTimeOffset? t, bool force = false)
		{
			if (!force && t == _moment) { return; }
			_moment = t;
			if (_isMapReady) { _ = _mapService.SetTropicalTimeAsync(t?.ToUnixTimeMilliseconds()); }

			var live = _loadedKey is { Live: true };
			var inEffect = _zones.Where(z => live || z.IsInEffectAt(t)).ToList();
			foreach (var k in Kinds) { k.Count = inEffect.Count(z => z.ProductId == k.Product.Id); }

			var storms = inEffect.Select(z => z.StormKey).Distinct()
				.Select(key => key.Length > 0 && _storms.TryGetValue(key, out var words) ? words : string.Empty)
				.Where(s => s.Length > 0).ToList();
			var products = inEffect.Select(z => z.ProductId).Distinct().Count();
			CardHeadline = inEffect.Count == 0 ? "No tropical alerts in effect"
				: storms.Count > 0 ? string.Join(" · ", storms)
				: "Tropical alerts in effect";
			var counts = inEffect.Count == 0 ? string.Empty
				: $"{products} {(products == 1 ? "product" : "products")} · {inEffect.Count} {(inEffect.Count == 1 ? "zone" : "zones")}";
			CardContext = live ? counts
				: t is { } at ? (counts.Length > 0 ? $"At {at.ToLocalTime():h:mm tt} · {counts}" : $"At {at.ToLocalTime():h:mm tt}")
				: counts;
		}

		private async Task ClearAsync()
		{
			++_applyToken;
			_loadedKey = null;
			_zones = Array.Empty<TropicalZone>();
			_storms = new Dictionary<string, string>();
			foreach (var k in Kinds) { k.Count = 0; }
			await _mapService.ClearTropicalAsync();
		}
	}

	/// <summary>One product's row in the section: toggle, colour, how many zones are in effect.</summary>
	public sealed class TropicalKindRow : ObservableObject
	{
		private readonly Action _changed;
		private bool _isShown = true;
		private int _count;

		internal TropicalKindRow(TropicalProduct product, Action changed)
		{
			Product = product;
			_changed = changed;
		}

		public TropicalProduct Product { get; }
		public string Label => Product.RowLabel;
		public string Fill => Product.Fill;

		/// <summary>The NWS's name over its meaning, for the row's tooltip.</summary>
		public string ToolTip => $"{Product.Name} — {Product.Definition}";

		public bool IsShown
		{
			get => _isShown;
			set { if (SetProperty(ref _isShown, value)) { _changed(); } }
		}

		internal void SetSilently(bool shown)
		{
			if (_isShown == shown) { return; }
			_isShown = shown;
			OnPropertyChanged(nameof(IsShown));
		}

		/// <summary>Zones in effect at the moment shown. A zero row is DIMMED, never hidden.</summary>
		public int Count
		{
			get => _count;
			internal set { if (SetProperty(ref _count, value)) { OnPropertyChanged(nameof(RowOpacity)); } }
		}

		public double RowOpacity => _count == 0 ? 0.4 : 1.0;
	}
}

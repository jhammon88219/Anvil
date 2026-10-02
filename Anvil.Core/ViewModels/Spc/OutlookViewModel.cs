using System;
using System.Collections.Generic;
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
	/// View model for the SPC outlook subsystem: the day/product selection + overlay opacity,
	/// the issued/valid readout + info card + forecast-discussion narrative, the next-update progress
	/// bar, and the outlook background refresh loop. Extracted from MapViewModel; drives the map through
	/// <see cref="IMapService"/>. (SPC watch boxes are a separate subsystem — see
	/// <see cref="WatchesViewModel"/> — since they're current-conditions alerts, not a forecast.)
	/// </summary>
	public sealed class OutlookViewModel : ObservableObject
	{
		private readonly IMapService _mapService;
		private readonly ISpcOutlookService _spcOutlookService;
		private readonly IDispatcher _dispatcher;
		private readonly ILogger<OutlookViewModel> _logger;

		// Readiness guard: outlook/watch commands only run once the map page has reported 'mapReady'
		// (set by OnMapsReadyAsync, called from MapViewModel.OnMapsReadyAsync).
		private bool _isMapReady;

		// Selected SPC outlook day + product option. The option list cascades to
		// whatever's valid for the selected day (plus a leading "None" entry); selecting
		// an option shows that product on the map, or clears it for None.
		private int _selectedDay;
		private DayOption? _selectedDayOption;
		private IReadOnlyList<OutlookOption> _productOptions = new List<OutlookOption>();
		private OutlookOption? _selectedOption;
		// Master "show outlook layer" gate (Outlook tool-window toggle). Defaults OFF so the app
		// launches with no outlook drawn; flipping it on shows the armed Day/Product selection.
		private bool _isOutlookVisible;

		// Authoritative issued/valid/expire readout for the loaded outlook, parsed from
		// the product's cached GeoJSON. Empty when None is selected or no times are known.
		private string _outlookTimesText = string.Empty;

		// Legend rows for the loaded outlook (official SPC colors + names, read from the same cached GeoJSON
		// the map draws). Empty when None is selected or the layer is off.
		private IReadOnlyList<SpcRiskLevel> _legendEntries = System.Array.Empty<SpcRiskLevel>();
		private IReadOnlyList<OutlookHatchLegendRow> _hatchLegendRows = System.Array.Empty<OutlookHatchLegendRow>();

		// Whether the CIG hatching is drawn. Session-only, starts shown — a fresh launch never hides hatching
		// the user has forgotten about.
		private bool _showHatching = true;

		// Fill opacity (0-1) for the outlook polygons; the outlines stay opaque so the
		// basemap reads through. Driven by the ribbon's opacity slider.
		private double _outlookOpacity = 0.05;

		// Suppresses outlook map updates while a day-change cascade re-selects the
		// option (the product combobox transiently nulls its selection mid-swap).
		private bool _suppressOutlookUpdate;

		// Outlook refresh schedule (set by MainWindow each ~15-min cycle) for the Outlook tool
		// window's next-update progress bar.
		private DateTimeOffset? _outlookCycleStart;
		private DateTimeOffset? _nextOutlookRefreshAt;

		public OutlookViewModel(IMapService mapService, ISpcOutlookService spcOutlookService, IDispatcher dispatcher, ILogger<OutlookViewModel> logger)
		{
			_mapService = mapService;
			_spcOutlookService = spcOutlookService;
			_dispatcher = dispatcher;
			_logger = logger;

			// SPC outlook selectors. Day 1 Categorical is the armed default, but the visibility toggle
			// (IsOutlookVisible) defaults off, so nothing is drawn on launch. Assign backing fields
			// directly so construction fires no map command (OnMapsReadyAsync applies state when ready).
			Days = BuildDayOptions(_spcOutlookService.AvailableDays);
			_selectedDayOption = Days.FirstOrDefault();
			_selectedDay = _selectedDayOption?.Day ?? 0;
			RebuildProductOptions();
			_selectedOption = DefaultOptionForDay();
			RebuildCycleOptions();
		}

		// Cancels this VM's app-lifetime loops when the window closes. See Shutdown().
		private readonly CancellationTokenSource _shutdown = new();

		/// <summary>Stops this subsystem's app-lifetime loops. Called from <see cref="MapViewModel.Shutdown"/>
		/// on the main window's Closed, so no loop ticks into a XAML runtime that is being torn down.</summary>
		public void Shutdown() => _shutdown.Cancel();

		/// <summary>Kicks off the outlook background refresh loop (called once at launch). Watches have
		/// their own loop — see <see cref="WatchesViewModel.StartBackgroundRefresh"/>.</summary>
		public void StartBackgroundRefresh()
		{
			_ = RefreshOutlooksInBackgroundAsync();
		}

		// SPC products update a handful of times a day at scheduled issuances; poll periodically so
		// we catch new ones. Conditional GETs make each cycle cheap (mostly 304s when unchanged).
		private static readonly TimeSpan OutlookRefreshInterval = TimeSpan.FromMinutes(15);

		private Task RefreshOutlooksInBackgroundAsync() => BackgroundRefresh.RunPeriodicAsync(OutlookRefreshInterval, async first =>
		{
			try
			{
				var results = await _spcOutlookService.RefreshAllAsync();
				var updated = results.Count(r => r.Status is SpcOutlookFetchStatus.Updated);
				var failed = results.Count(r => r.Status is SpcOutlookFetchStatus.FailedCacheKept
					or SpcOutlookFetchStatus.FailedNoCache);
				_logger.LogInformation("Outlooks refresh: {Total} products, {Updated} updated, {Failed} failed", results.Count, updated, failed);

				// Re-apply the current outlook on launch (so a first-run empty cache overlay appears and
				// the issued/valid readout picks up times) and whenever a cycle actually pulled new data —
				// but not when a periodic cycle was all 304s, so we don't needlessly re-render every 15 min.
				if (first || updated > 0)
				{
					_dispatcher.Post(() => OnOutlooksRefreshed());
				}
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Outlooks refresh aborted");
			}

			// Tell the next-update bar when the next periodic refresh is roughly due (fixed cadence;
			// the refresh itself takes seconds, negligible vs the 15-min interval). Runs every cycle.
			var cycleStart = DateTimeOffset.Now;
			_dispatcher.Post(() => SetOutlookRefreshSchedule(cycleStart, cycleStart + OutlookRefreshInterval));
		}, _shutdown.Token);

		/// <summary>Outlook days that have products (1-8), each labeled with its date;
		/// static for the app lifetime.</summary>
		public IReadOnlyList<DayOption> Days { get; }

		/// <summary>
		/// The selected outlook day (carrying its date label). Changing it cascades the
		/// product list and auto-selects a product for the new day so an overlay stays
		/// visible.
		/// </summary>
		public DayOption? SelectedDayOption
		{
			get => _selectedDayOption;
			set
			{
				if (value is null || _selectedDayOption == value)
				{
					return;
				}

				_selectedDay = value.Day;
				SetProperty(ref _selectedDayOption, value);

				// Rebuild the option list, then re-select an option for the new day.
				// Suppress overlay updates during the swap (the product combobox briefly
				// nulls its selection as its items change) and push one update at the end.
				_suppressOutlookUpdate = true;
				RebuildProductOptions();
				SelectedOption = DefaultOptionForDay();
				RebuildCycleOptions();
				_suppressOutlookUpdate = false;

				ApplyCurrentOutlook();
			}
		}

		/// <summary>
		/// Options for the product selector: a leading "None" entry (clears the overlay)
		/// followed by the products valid for <see cref="SelectedDay"/>.
		/// </summary>
		public IReadOnlyList<OutlookOption> ProductOptions => _productOptions;

		/// <summary>
		/// The selected option. Setting it shows that product on the map, or clears the
		/// overlay for the "None" option, once the map is ready.
		/// </summary>
		public OutlookOption? SelectedOption
		{
			get => _selectedOption;
			set
			{
				if (SetProperty(ref _selectedOption, value))
				{
					OnPropertyChanged(nameof(SelectedProduct));

					if (!_suppressOutlookUpdate)
					{
						ApplyCurrentOutlook();
					}
				}
			}
		}

		/// <summary>The product behind the current selection (null for "None").</summary>
		public SpcOutlookProduct? SelectedProduct => _selectedOption?.Product;

		/// <summary>
		/// Master on/off gate for the outlook overlay (bound to the Outlook tool-window toggle).
		/// Independent of the Day/Product selection: when off, no outlook is drawn even if a
		/// product is selected; when on, the selected product (if any) is shown. Defaults off so
		/// the app launches with no outlook on the map.
		/// </summary>
		public bool IsOutlookVisible
		{
			get => _isOutlookVisible;
			set
			{
				if (SetProperty(ref _isOutlookVisible, value))
				{
					ApplyCurrentOutlook();
				}
			}
		}

		// ── Issuance cycle — the same row as PastCast's (SpcIssuanceCycles is the one table both read) ──
		// "Latest" (the default) is the live feed exactly as before: it follows every new SPC issuance. An
		// earlier cycle of TODAY's outlook is fetched from IEM's archive (near-real-time — today's 20Z was there
		// minutes after issue) and HELD: the 15-min refresh doesn't move it.
		// ⚠️ NOT PERSISTED, deliberately, unlike PastCast's: a held "13Z" names one issuance of one day, and
		// restoring it tomorrow would pin a different (or unissued) outlook. Every launch starts on Latest.

		private static readonly PastCycleOption LatestCycle = new("Latest", null);
		private IReadOnlyList<PastCycleOption> _cycleOptions = new[] { LatestCycle };
		private PastCycleOption _selectedCycleOption = LatestCycle;

		/// <summary>Latest + the selected day's issuances already out (earliest first). Days 4-8: Latest only.</summary>
		public IReadOnlyList<PastCycleOption> CycleOptions => _cycleOptions;

		/// <summary>The picked issuance; <see cref="PastCycleOption.Cycle"/> null = Latest (the live feed).</summary>
		public PastCycleOption SelectedCycleOption
		{
			get => _selectedCycleOption;
			set
			{
				if (value is null || _selectedCycleOption == value) { return; }
				SetProperty(ref _selectedCycleOption, value);
				ApplyCurrentOutlook();
			}
		}

		/// <summary>The Cycle row's gate: a product picked AND an earlier issuance to pick.</summary>
		public bool CanPickCycle => HasOutlook && _cycleOptions.Count > 1;

		// The HELD issuance's own product (its IEM cache file), once fetched; null while Latest, loading or failed.
		private SpcOutlookProduct? _heldProduct;
		// The held issuance's status for the card footer ("Loading…", "Not in the 13Z issuance"); "" once drawn.
		private string _heldNote = string.Empty;
		private int _heldToken;

		// Which VALID day (IEM's `valid`) the selected day's outlook is for: the live file's expire is 12Z the day
		// after. No file yet → today's convective day + (day − 1).
		private DateOnly ValidDayFor(SpcOutlookProduct? live, int day) =>
			live is not null && _spcOutlookService.GetTimesForProduct(live)?.Expire is { } expire
				? DateOnly.FromDateTime(expire.UtcDateTime.AddDays(-1))
				: SpcIssuanceCycles.ConvectiveDay(DateTimeOffset.UtcNow).AddDays(day - 1);

		// Rebuild the list for the selected day (a day change; each refresh, as new issuances land), keeping the
		// held cycle if it is still listed, else falling back to Latest.
		private void RebuildCycleOptions()
		{
			var day = _selectedDay;
			var anchor = _selectedOption?.Product ?? _productOptions.Select(o => o.Product).FirstOrDefault(p => p is not null);
			var valid = ValidDayFor(anchor, day);
			var now = DateTimeOffset.UtcNow;
			var opts = new List<PastCycleOption> { LatestCycle };
			opts.AddRange(SpcIssuanceCycles.For(day)
				.Where(c => SpcIssuanceCycles.IssuedAtUtc(day, c, valid) <= now)
				.Select(c => new PastCycleOption(SpcIssuanceCycles.Label(c), c)));
			var keep = opts.FirstOrDefault(o => o.Cycle == _selectedCycleOption.Cycle) ?? LatestCycle;

			_cycleOptions = opts;
			OnPropertyChanged(nameof(CycleOptions));
			// ⚠️ Re-raised even when unchanged: the ComboBox drops its selection when its list is swapped.
			_selectedCycleOption = keep;
			OnPropertyChanged(nameof(SelectedCycleOption));
			OnPropertyChanged(nameof(CanPickCycle));
		}

		// Fetch + draw the held issuance of the live product's day/type. Token-guarded: any newer apply wins.
		private async Task ShowHeldIssuanceAsync(SpcOutlookProduct live, int cycle)
		{
			var token = ++_heldToken;
			var label = SpcIssuanceCycles.Label(cycle);
			_heldProduct = null;
			_heldNote = $"Loading the {label} issuance…";
			await _mapService.ClearOutlookAsync();
			UpdateOutlookTimes();

			var valid = ValidDayFor(live, live.Day);
			PastOutlookResult? result = null;
			try
			{
				result = await _spcOutlookService.EnsurePastOutlookAsync(valid, live.Day, cycle);
			}
			catch (Exception ex)
			{
				_logger.LogWarning(ex, "Outlook issuance {Cycle} fetch failed", cycle);
			}
			if (token != _heldToken) { return; }

			if (result is null || result.Error is not null)
			{
				_heldNote = $"Couldn't fetch the {label} issuance{(result?.Error is { } e ? $": {e}" : ".")}";
			}
			else if (!result.Found || !result.AvailableTypes.Contains(live.Type))
			{
				_heldNote = $"Not in the {label} issuance (or not archived yet) — pick Latest.";
			}
			else
			{
				var file = SpcOutlookService.PastCacheName(valid, live.Day, cycle, live.Type);
				_heldProduct = new SpcOutlookProduct($"held-{valid:yyyyMMdd}-d{live.Day}-c{cycle:D2}-{live.Type}",
					live.Day, live.Type, live.DisplayName, file, $"https://{SpcOutlookService.CacheHostName}/{file}");
				_heldNote = string.Empty;
				await ShowWithHatchingAsync(_heldProduct);
				if (token != _heldToken) { return; }
			}
			UpdateOutlookTimes();
		}

		// ── The section header's SHOW/HIDE box — the same contract as PastOutlookViewModel.IsShown. ──
		// ⚠️ NOT the mode: IsOutlookVisible IS ForeCast (MapViewModel.IsForeCast projects onto it) and a body
		// never arms or clears its mode. This is a layer toggle UNDER the mode — hiding keeps Day / Product, so
		// ticking it back redraws the same outlook. The card and discussion keep describing the pick; only
		// the map and the legend go.

		private bool _isShown = true;

		/// <summary>Whether the picked outlook is drawn while ForeCast runs. Default true; persisted.</summary>
		public bool IsShown
		{
			get => _isShown;
			set
			{
				if (SetProperty(ref _isShown, value))
				{
					OnPropertyChanged(nameof(ShownOnMap));
					ApplyCurrentOutlook();
				}
			}
		}

		/// <summary>A product is picked AND shown — gates the opacity slider; the header BOX binds
		/// <see cref="IsShown"/> alone.</summary>
		public bool ShownOnMap => _isShown && HasOutlook;

		/// <summary>The header box's click. Works whatever the product, None included (PastCast's rule).</summary>
		public void ToggleShown() => IsShown = !_isShown;

		/// <summary>
		/// Authoritative "Issued … · Valid … → …" line for the loaded outlook, parsed from
		/// the product's cached GeoJSON (local time). Empty when None is selected or the
		/// cache has no times yet. Reaches the ForeCast card as <see cref="CardContext"/>, whose host
		/// collapses the line while it is empty — so there is no separate "has times" flag.
		/// </summary>
		public string OutlookTimesText
		{
			get => _outlookTimesText;
			private set => SetProperty(ref _outlookTimesText, value);
		}

		/// <summary>The SOLID half of the selected product's legend — NOAA's own colors + level names for every
		/// category/probability level in its scale, least-severe first (shown even when today's issuance omits
		/// some). The hatched half is <see cref="HatchLegendRows"/>. Empty (and hidden) when None or off.</summary>
		public IReadOnlyList<SpcRiskLevel> LegendEntries => _legendEntries;

		/// <summary>The Conditional Intensity Groups in the product's scale, each marked with whether the
		/// cached outlook actually contains it. Empty for products with no CIGs (Categorical, fire).</summary>
		public IReadOnlyList<OutlookHatchLegendRow> HatchLegendRows => _hatchLegendRows;

		/// <summary>Whether the loaded outlook has any legend rows to show.</summary>
		public bool HasLegend => _legendEntries.Count > 0 || _hatchLegendRows.Count > 0;

		/// <summary>Whether the product has intensity groups at all — gates the hatching block + its box.</summary>
		public bool HasHatchLegend => _hatchLegendRows.Count > 0;

		/// <summary>Draws or hides the CIG hatching on the map (fills and outlines stay).</summary>
		public bool ShowHatching
		{
			get => _showHatching;
			set
			{
				if (SetProperty(ref _showHatching, value) && _isMapReady)
				{
					_ = _mapService.SetOutlookHatchingVisibleAsync(value);
				}
			}
		}

		// ── The ForeCast window's two text surfaces: the card's headline, and SPC's forecast discussion
		//    (fetched from their HTML page via GetNarrativeAsync, disk cached). Both are recomputed in
		//    UpdateOutlookCard when the selection changes or the outlook cache refreshes. ──
		private string _outlookCardTitle = string.Empty;
		private string _outlookNarrative = string.Empty;

		/// <summary>SPC forecast-discussion text for the selected outlook (or a status line).</summary>
		public string OutlookNarrativeText
		{
			get => _outlookNarrative;
			private set => SetProperty(ref _outlookNarrative, value);
		}

		// ⚠️ There is no OutlookNextUpdateProgress companion: the card's footer states the countdown in
		// WORDS and no ProgressBar binds the outlook refresh, so the 0-100 fraction had no reader.
		// Re-adding a bar means the getter back plus a raise wherever the countdown is raised below.

		/// <summary>Countdown label to the next SPC outlook refresh (e.g. "next ~9 min").</summary>
		public string OutlookNextUpdateText => NextUpdate.CountdownOf(_nextOutlookRefreshAt);

		// ── The ForeCast window's section card ──
		// Three lines in the shape every card in the two temporal windows uses (headline / context /
		// footer), composed from state this VM already kept — the title, the issued/valid line and the
		// countdown were all being computed and shown NOWHERE. ⚠️ Nothing new is fetched or tracked here;
		// if a line looks wrong, the bug is upstream in ApplyCurrentOutlook, not in these three getters.

		/// <summary>Whether a product (not "None") is selected — the section's off switch, and so the gate
		/// on its opacity slider. Same role as PastOutlookViewModel.HasOutlook.</summary>
		public bool HasOutlook => _selectedOption?.Product is not null;

		/// <summary>The card's headline: which outlook is drawn, e.g. "Day 1 · Categorical".</summary>
		public string CardHeadline => _outlookCardTitle.Length > 0 ? _outlookCardTitle : "No outlook drawn";

		/// <summary>The card's middle line: the authoritative issued/valid times from the cached GeoJSON.
		/// Empty for "None", which collapses the line rather than leaving a gap.</summary>
		public string CardContext => OutlookTimesText;

		/// <summary>
		/// The card's footer: how long until the next refresh, or — with nothing selected — what to do
		/// about it.
		/// </summary>
		/// <remarks>
		/// ⚠️ It re-reads <see cref="OutlookNextUpdateText"/> rather than formatting its own countdown, so
		/// the card and the next-update readout can never disagree. That also means this must be raised
		/// wherever that one is: on the 1 s tick as well as on a selection change.
		/// </remarks>
		public string CardFooter
		{
			get
			{
				if (!HasOutlook) { return "Pick a product below"; }
				// A HELD issuance doesn't refresh, so no countdown: its status, else how to go back to following.
				if (_selectedCycleOption.Cycle is { } held)
				{
					return _heldNote.Length > 0 ? _heldNote
						: $"Holding the {SpcIssuanceCycles.Label(held)} issuance — pick Latest to follow SPC's updates";
				}
				var countdown = OutlookNextUpdateText;
				return countdown.Length == 0
					? string.Empty
					: char.ToUpperInvariant(countdown[0]) + countdown[1..];
			}
		}

		// Re-raises the three card lines. Called from both places their inputs move: the selection funnel
		// (UpdateOutlookCard) and the countdown tick.
		private void RaiseCard()
		{
			OnPropertyChanged(nameof(CardHeadline));
			OnPropertyChanged(nameof(CardContext));
			OnPropertyChanged(nameof(CardFooter));
		}

		/// <summary>Called by MainWindow after each outlook refresh with the next refresh schedule.</summary>
		public void SetOutlookRefreshSchedule(DateTimeOffset cycleStart, DateTimeOffset next)
		{
			_outlookCycleStart = cycleStart;
			_nextOutlookRefreshAt = next;
			OnPropertyChanged(nameof(OutlookNextUpdateText));
			RaiseCard();
		}

		// App-lifetime 1s tick that advances the outlook next-update progress bar (independent of any
		// radar loop, so the outlook bar updates even when no loop is active).
		private async Task RunProgressTickAsync()
		{
			try
			{
				using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
				while (await timer.WaitForNextTickAsync(_shutdown.Token))
				{
					OnPropertyChanged(nameof(OutlookNextUpdateText));
					RaiseCard();
				}
			}
			catch (OperationCanceledException)
			{
				// Window closed. This tick raises PropertyChanged straight at live bindings, so it is
				// exactly the kind of loop that must not outlive the XAML tree it is notifying.
			}
		}


		/// <summary>
		/// Fill opacity (0-1) of the outlook polygons. The outlines stay opaque, so
		/// lowering this lets the basemap and its borders read through the fill.
		/// </summary>
		public double OutlookOpacity
		{
			get => _outlookOpacity;
			set
			{
				if (SetProperty(ref _outlookOpacity, value) && _isMapReady)
				{
					_ = _mapService.SetOutlookOpacityAsync(value);
				}
			}
		}

		// Rebuilds the per-day option list ("None" + the day's products).
		private void RebuildProductOptions()
		{
			var options = new List<OutlookOption> { new("None", null) };
			options.AddRange(_spcOutlookService
				.GetProductsForDay(_selectedDay)
				.Select(p => new OutlookOption(p.TypeLabel, p)));
			_productOptions = options;
			OnPropertyChanged(nameof(ProductOptions));
		}

		// The option to show for the current day: its Categorical if present (days 1-3),
		// else the day's first real product (days 4-8 lead with Probabilistic).
		private OutlookOption DefaultOptionForDay() =>
			_productOptions.FirstOrDefault(o => o.Product?.Type == SpcOutlookType.Categorical)
			?? _productOptions.FirstOrDefault(o => o.Product is not null)
			?? _productOptions[0];

		// Pushes the current selection to the map (show product, or clear for None),
		// once the map is ready.
		private void ApplyCurrentOutlook()
		{
			if (!_isMapReady)
			{
				return;
			}

			var product = _selectedOption?.Product;
			if (product is not null && _isOutlookVisible && _isShown && _selectedCycleOption.Cycle is { } cycle)
			{
				_ = ShowHeldIssuanceAsync(product, cycle); // bumps _heldToken itself, then updates the card
				return;
			}

			// ⚠️ Every OTHER path orphans a held fetch still in flight, so it can't land over this one.
			++_heldToken;
			_heldProduct = null;
			_heldNote = string.Empty;
			if (product is not null && _isOutlookVisible && _isShown)
			{
				_ = ShowWithHatchingAsync(product);
			}
			else
			{
				_ = _mapService.ClearOutlookAsync();
			}

			UpdateOutlookTimes();
		}

		// ⚠️ ORDERED: the page resets hatching to shown on every show (the layer is shared with PastCast), so
		// the hatching push must land after the show, never alongside it.
		private async Task ShowWithHatchingAsync(SpcOutlookProduct product)
		{
			await _mapService.ShowOutlookAsync(product);
			await _mapService.SetOutlookHatchingVisibleAsync(_showHatching);
		}

		// Labels each outlook day with the calendar date it covers (Day N = today + N-1,
		// local), e.g. "Day 1 · Sat Jun 14". A close-enough mapping for orientation; the
		// authoritative issued/valid times come from the loaded GeoJSON (UpdateOutlookTimes).
		private static IReadOnlyList<DayOption> BuildDayOptions(IReadOnlyList<int> days)
		{
			var today = DateTime.Now.Date;
			return days
				.Select(d => new DayOption(d, $"Day {d} · {today.AddDays(d - 1):ddd MMM d}"))
				.ToList();
		}

		// Reads the issued/valid/expire times for the current product from its cached GeoJSON (local time)
		// into OutlookTimesText — the ForeCast card's middle line. Cleared when None is selected or no
		// times are available.
		private void UpdateOutlookTimes()
		{
			// When the layer is toggled off, treat the selection as "none" so the times line, the legend
			// and the card all reflect what's actually on the map.
			var product = _isOutlookVisible ? _selectedOption?.Product : null;
			// A HELD cycle describes ITS issuance's file (null while loading or when it failed), never the live one.
			var held = _selectedCycleOption.Cycle;
			var shown = held is null ? product : product is null ? null : _heldProduct;
			var times = shown is null ? null : _spcOutlookService.GetTimesForProduct(shown);

			// Legend = the selected product's FULL scale (least→most severe), gated by the same visibility as
			// the times so it appears only when an outlook is actually shown.
			// Split at the kind: solid rows stay plain, CIG rows carry "In Outlook" read from the same cache file
			// (re-read on every refresh, since this runs from OnOutlooksRefreshed too).
			// ⚠️ The legend keys what is DRAWN, so the header box hides it too; the card and times don't.
			var drawn = _isShown ? shown : null;
			var scale = drawn is null
				? System.Array.Empty<SpcRiskLevel>()
				: _spcOutlookService.GetLegendForProduct(drawn);
			var present = drawn is null
				? new HashSet<string>()
				: _spcOutlookService.GetHatchGroupsInOutlook(drawn);
			_legendEntries = scale.Where(l => !l.IsConditionalIntensity).ToList();
			_hatchLegendRows = scale.Where(l => l.IsConditionalIntensity)
				.Select(l => new OutlookHatchLegendRow(l, present.Contains(l.Code)))
				.ToList();
			OnPropertyChanged(nameof(LegendEntries));
			OnPropertyChanged(nameof(HatchLegendRows));
			OnPropertyChanged(nameof(HasLegend));
			OnPropertyChanged(nameof(HasHatchLegend));

			if (times is null)
			{
				OutlookTimesText = string.Empty;
			}
			else
			{
				var parts = new List<string>(2);
				if (times.Issued is { } issued)
				{
					parts.Add($"Issued {issued.ToLocalTime():ddd h:mm tt}");
				}
				if (times.Valid is { } valid && times.Expire is { } expire)
				{
					parts.Add($"Valid {valid.ToLocalTime():ddd h:mm tt} → {expire.ToLocalTime():ddd h:mm tt}");
				}
				if (held is { } c)
				{
					parts.Insert(0, $"{SpcIssuanceCycles.Label(c)} issuance"); // same wording as PastCast's context
				}
				OutlookTimesText = string.Join("  ·  ", parts);
			}

			UpdateOutlookCard(product);
		}

		// Sets the card's headline and kicks off the (lazy, disk-cached) forecast-discussion fetch. Cleared
		// for "None". ⚠️ Takes no times: the issued/valid strings it used to format went with the deleted
		// Outlook Details window, and the card's own times line is OutlookTimesText, set by the caller.
		private void UpdateOutlookCard(SpcOutlookProduct? product)
		{
			if (product is null)
			{
				_outlookCardTitle = string.Empty;
				_narrativeFor = null;
				OutlookNarrativeText = string.Empty;
			}
			else if (_selectedCycleOption.Cycle is not null)
			{
				// SPC publishes the prose for its LATEST issuance only; showing that under a held earlier outlook
				// would pair one issuance's map with another's reasoning.
				_outlookCardTitle = $"Day {product.Day} · {product.TypeLabel}";
				_narrativeFor = null;
				OutlookNarrativeText = "SPC's forecast discussion is published for the latest issuance only — pick Latest to read it.";
			}
			else
			{
				_outlookCardTitle = $"Day {product.Day} · {product.TypeLabel}";
				_ = RefreshOutlookNarrativeAsync(product);
			}

			// ⚠️ The title is a FIELD, not a property: CardHeadline reads it directly, so RaiseCard() below
			// is what announces it. Separate OutlookCardTitle / OutlookIssuedText / OutlookValidText /
			// HasOutlookCard properties fed the deleted Outlook Details window; the issued + valid strings
			// they formatted are now one line, built in ApplyCurrentOutlook as OutlookTimesText.
			OnPropertyChanged(nameof(HasOutlook));
			OnPropertyChanged(nameof(ShownOnMap));
			OnPropertyChanged(nameof(CanPickCycle));
			RaiseCard();
		}

		// The product the current narrative belongs to — so a same-product refresh updates the
		// text silently, while a product switch shows the "Loading…" placeholder.
		private SpcOutlookProduct? _narrativeFor;

		// Fetches the SPC forecast discussion (network → disk cache) and shows it, unless the user
		// changed selection while it loaded.
		private async Task RefreshOutlookNarrativeAsync(SpcOutlookProduct product)
		{
			if (!ReferenceEquals(_narrativeFor, product))
			{
				OutlookNarrativeText = "Loading forecast discussion…";
			}

			string? text = null;
			try
			{
				text = await _spcOutlookService.GetNarrativeAsync(product);
			}
			catch
			{
				// Best effort; fall through to the not-available message.
			}

			if (!ReferenceEquals(_selectedOption?.Product, product) || _selectedCycleOption.Cycle is not null)
			{
				return; // selection changed mid-fetch (an earlier issuance picked counts — see UpdateOutlookCard)
			}
			_narrativeFor = product;
			// ⚠️ The source BEFORE the text: MapViewModel re-reads both when OutlookNarrativeText changes.
			NarrativeSourceUrl = text is null ? null : _spcOutlookService.NarrativePageUrl(product);
			OutlookNarrativeText = text ?? "Forecast discussion isn't available for this product yet.";
		}

		/// <summary>The SPC page <see cref="OutlookNarrativeText"/> was read from (the window's SOURCE link);
		/// null while the text is a status line.</summary>
		public string? NarrativeSourceUrl { get; private set; }

		/// <summary>
		/// Called after the launch outlook refresh finishes: re-applies the current
		/// selection so a first-run (empty cache) overlay appears and the issued/valid
		/// readout picks up the freshly-written times.
		/// </summary>
		/// <remarks>⚠️ A refresh can bring a NEW issuance, so the Cycle list is rebuilt — but a HELD issuance is
		/// immutable, so it is not re-fetched or redrawn; only Latest follows the refresh.</remarks>
		public void OnOutlooksRefreshed()
		{
			var held = _selectedCycleOption.Cycle;
			RebuildCycleOptions();
			if (held is null || _selectedCycleOption.Cycle != held)
			{
				ApplyCurrentOutlook();
			}
		}

		/// <summary>Called by MapViewModel once the map page is ready: applies the startup outlook state
		/// and starts the next-update progress tick.</summary>
		public async Task OnMapsReadyAsync()
		{
			_isMapReady = true;

			// Show the selected outlook only if the visibility toggle is on (it defaults off, so the
			// app launches with no outlook); sync the fill opacity to the slider's initial value.
			var startupProduct = _selectedOption?.Product;
			if (startupProduct is not null && _isOutlookVisible && _isShown)
			{
				await ShowWithHatchingAsync(startupProduct);
			}
			await _mapService.SetOutlookOpacityAsync(_outlookOpacity);
			UpdateOutlookTimes();

			// Drive the SPC outlook next-update progress bar.
			_ = RunProgressTickAsync();
		}
	}
}
